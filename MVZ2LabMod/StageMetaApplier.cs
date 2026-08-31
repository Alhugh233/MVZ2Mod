using System;
using MVZ2.Managers;
using MVZ2Logic.Level;
using PVZEngine;
using PVZEngine.Level;

namespace MVZ2LabMod
{
    /// <summary>
    /// 把关卡 meta（stages.xml）的属性补到我们的关卡定义上。
    ///
    /// 背景：原版 ModLoader.LoadStageProperties 会为"游戏自己加载的 Mod"执行
    ///   stageDef.SetProperty(ENEMY_POOL, meta.Spawns) 等一系列属性赋值。
    /// 外部 Mod（BepInEx 注入）不走该路径——ModInfo.Assemblies 为空、定义在
    /// OnInit 手动注册，导致关卡定义没有任何 meta 属性：
    ///   - ENEMY_POOL 为空 → 进关预览 CreatePreviewEnemies 空引用崩溃
    ///   - WAVE_MAX_TIME 等为 0 → 波次系统行为异常
    ///   - 出生点数加成（pointsAddition=5000）丢失 → 出怪预算异常
    ///   - START_ENERGY 为 0 → 开局无能量
    ///
    /// 本类在 ModLogicFix 中（meta 已由 MetaValidation 注入游戏 ModResource 之后）
    /// 读取游戏已解析的 StageMeta，逐项复刻 LoadStageProperties 的赋值。
    ///
    /// 实现注意：
    ///   1. 非泛型扩展 setter（SetLevelName/SetStageType 等）直接调用（原生已编译）；
    ///   2. float/int/bool 属性走代理泛型 SetProperty&lt;T&gt;（二进制中存在具体实例化）；
    ///   3. ENEMY_POOL（NamespaceID[]，仅有 fully-shared 实例化）也先试泛型，
    ///      另由 RealPrologueStage.OnStart 用非泛型 level.SetEnemyPool 兜底。
    /// ⚠ SetPropertyObject 不可用：IPropertyKey 接口分派在 PropertyMeta&lt;NamespaceID[]&gt;
    ///   上抛 MethodAccessException（metadata v31 泛型实例化接口表问题，实测）。
    /// </summary>
    internal static class StageMetaApplier
    {
        public static void Apply()
        {
            try
            {
                var defs = LabMod.StageDefs;
                if (defs.Count == 0)
                {
                    Plugin.Log?.LogWarning("[StageMetaApplier] no stage definitions registered");
                    return;
                }
                var main = MainManager.Instance;
                if (main == null)
                {
                    Plugin.Log?.LogWarning("[StageMetaApplier] MainManager.Instance is null");
                    return;
                }
                var res = main.ResourceManager;
                if (res == null)
                {
                    Plugin.Log?.LogWarning("[StageMetaApplier] ResourceManager is null");
                    return;
                }
                var metas = res.GetModStageMetas(LabMod.spaceName);
                if (metas == null || metas.Length == 0)
                {
                    Plugin.Log?.LogWarning("[StageMetaApplier] no stage metas for " + LabMod.spaceName);
                    return;
                }

                foreach (var def in defs)
                {
                    if (def == null)
                        continue;
                    MVZ2.Metas.StageMeta meta = null;
                    for (var i = 0; i < metas.Length; i++)
                    {
                        if (metas[i] != null && metas[i].ID == def.Name)
                        {
                            meta = metas[i];
                            break;
                        }
                    }
                    if (meta == null)
                    {
                        Plugin.Log?.LogWarning("[StageMetaApplier] no meta for stage " + def.Name);
                        continue;
                    }
                    ApplyMeta(def, meta);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[StageMetaApplier] error: " + e);
            }
        }

        /// <summary>按关卡 ID 取 meta（OnStart 兜底设置敌怪池用）。</summary>
        public static MVZ2.Metas.StageMeta GetStageMeta(string stageID)
        {
            try
            {
                var res = MainManager.Instance?.ResourceManager;
                var metas = res?.GetModStageMetas(LabMod.spaceName);
                if (metas == null)
                    return null;
                for (var i = 0; i < metas.Length; i++)
                {
                    if (metas[i] != null && metas[i].ID == stageID)
                        return metas[i];
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[StageMetaApplier] GetStageMeta error: " + e);
            }
            return null;
        }

        /// <summary>
        /// 修复小游戏列表顺序：把本 Mod 的 arcade 条目 Index 平移到原版 minigame 之后。
        ///
        /// 背景：ArcadeMeta.Index 是"文件内同类条目顺序"（ArcadeMetaList.FromXmlNode 按
        /// 位置分配），我们的 arcade.xml 三个 minigame 拿到 0/1/2，与原版 0/1/2 撞车。
        /// ArcadeController.GetIDListByArcadeOrder 按 Index 建数组、
        /// FirstOrDefault 一个槽只留一个 → 我们的条目把原版首个小游戏顶掉了。
        ///
        /// 修复：直接改写已解析 meta 的 Index backing field（与原版条目错开、顺序连续），
        /// 使我们的条目出现在小游戏列表末尾。偏移量 = 原版 minigame 最大 Index + 1。
        /// ⚠ 必须在所有 Mod 资源加载完成后调用（ModLogicFix 时机）——
        ///    MetaValidation 注入时原版 arcade meta 尚未加载（实测
        ///    GetArcadeMetaList("mvz2") 为空）。
        /// </summary>
        public static void ShiftArcadeIndexes()
        {
            try
            {
                var res = MainManager.Instance?.ResourceManager;
                if (res == null)
                    return;
                var vanilla = res.GetArcadeMetaList("mvz2");
                var maxVanilla = -1;
                if (vanilla != null && vanilla.metas != null)
                {
                    for (var i = 0; i < vanilla.metas.Length; i++)
                    {
                        var m = vanilla.metas[i];
                        if (m != null && m.Type == "minigame" && m.Index > maxVanilla)
                            maxVanilla = m.Index;
                    }
                }
                var offset = maxVanilla + 1;
                if (offset <= 0)
                {
                    Plugin.Log?.LogWarning("[StageMetaApplier] vanilla arcade list unavailable, indexes not shifted");
                    return;
                }
                var ours = res.GetArcadeMetaList(LabMod.spaceName);
                if (ours == null || ours.metas == null)
                    return;
                for (var i = 0; i < ours.metas.Length; i++)
                {
                    var m = ours.metas[i];
                    if (m != null && m.Type == "minigame")
                        m._Index_k__BackingField = m.Index + offset;
                }
                Plugin.Log?.LogInfo("[StageMetaApplier] arcade minigame indexes shifted by +" + offset);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[StageMetaApplier] ShiftArcadeIndexes failed: " + e);
            }
        }

        private static void ApplyMeta(PVZEngine.Level.StageDefinition stageDef, MVZ2.Metas.StageMeta meta)
        {
            // 非泛型扩展 setter（原生已编译，直接调用）。
            LogicStageProps.SetLevelName(stageDef, meta.Name);
            LogicStageProps.SetDayNumber(stageDef, meta.DayNumber);
            LogicStageProps.SetStageType(stageDef, meta.Type);
            LogicStageProps.SetNeedBlueprints(stageDef, meta.NeedBlueprints);
            LogicStageProps.SetModelPreset(stageDef, meta.ModelPreset);
            EngineLevelProps.SetStartEnergy(stageDef, meta.StartEnergy);
            LogicLevelProps.SetSpawnPointPower(stageDef, meta.SpawnPointsPower);
            LogicLevelProps.SetSpawnPointMultiplier(stageDef, meta.SpawnPointsMultiplier);
            LogicLevelProps.SetSpawnPointAddition(stageDef, meta.SpawnPointsAddition);

            // 泛型 SetProperty<T>（float/int/bool 的具体实例化存在于二进制中）。
            Try(() => stageDef.SetProperty(LogicStageProps.WAVE_MAX_TIME, meta.MaxWaveTime), "WAVE_MAX_TIME");
            Try(() => stageDef.SetProperty(LogicStageProps.WAVE_ADVANCE_TIME, meta.AdvanceWaveTime), "WAVE_ADVANCE_TIME");
            Try(() => stageDef.SetProperty(LogicStageProps.WAVE_ADVANCE_HEALTH_PERCENT, meta.AdvanceHealthPercent), "WAVE_ADVANCE_HEALTH_PERCENT");
            Try(() => stageDef.SetProperty(EngineStageProps.FIRST_WAVE_TIME, meta.FirstWaveTime), "FIRST_WAVE_TIME");
            Try(() => stageDef.SetProperty(EngineStageProps.CONTINUED_FIRST_WAVE_TIME, meta.EndlessFirstWaveTime), "CONTINUED_FIRST_WAVE_TIME");
            Try(() => stageDef.SetProperty(EngineStageProps.TOTAL_FLAGS, meta.TotalFlags), "TOTAL_FLAGS");
            Try(() => stageDef.SetProperty(LogicStageProps.DROPS_TROPHY, meta.DropsTrophy), "DROPS_TROPHY");
            Try(() => stageDef.SetProperty(LogicStageProps.NO_START_TALK_MUSIC, meta.NoStartTalkMusic), "NO_START_TALK_MUSIC");

            // ENEMY_POOL（NamespaceID[]）：先试泛型；失败由 OnStart 的非泛型兜底补救。
            if (meta.Spawns != null)
                Try(() => stageDef.SetProperty(LogicStageProps.ENEMY_POOL, meta.Spawns), "ENEMY_POOL");

            if (NamespaceID.IsValid(meta.ClearPickupModel))
                Try(() => stageDef.SetProperty(LogicStageProps.CLEAR_PICKUP_MODEL, meta.ClearPickupModel), "CLEAR_PICKUP_MODEL");
            if (NamespaceID.IsValid(meta.MusicID))
                Try(() => stageDef.SetProperty(LogicStageProps.MUSIC_ID, meta.MusicID), "MUSIC_ID");

            Plugin.Log?.LogInfo("[StageMetaApplier] " + stageDef.GetID()
                + " type=" + meta.Type
                + " flags=" + meta.TotalFlags
                + " spawns=" + (meta.Spawns == null ? "null" : meta.Spawns.Length.ToString())
                + " waveMax=" + meta.MaxWaveTime
                + " spawnAdd=" + meta.SpawnPointsAddition);
        }

        private static void Try(System.Action action, string name)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[StageMetaApplier] SetProperty " + name + " failed: " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
