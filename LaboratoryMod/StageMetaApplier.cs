using System;
using MVZ2.Managers;
using MVZ2.Metas;
using MVZ2Logic.Level;
using PVZEngine;
using PVZEngine.Level;

namespace LaboratoryMod
{
    /// <summary>
    /// 把关卡 meta（stages.xml 的 StageMeta）属性应用到注入的 StageDefinition。
    ///
    /// 0.7 官方 LoadStageProperties 依赖 mod.GetStageDefinition 泛型查找 + 对 def 做大量
    /// 泛型 SetProperty —— 在 metadata v31 上对注入 def/注入 mod 不可靠（与 test-1 结论
    /// 一致）。本类复刻其属性赋值，但全部用"非虚扩展 setter + Try 包裹的泛型 SetProperty"，
    /// 规避接口分派 / 泛型实例化问题。ENEMY_POOL 若泛型失败，由关卡 OnStart 兜底。
    /// </summary>
    internal static class StageMetaApplier
    {
        /// <summary>
        /// 应用 meta 属性（稳定 path 版）。LabMod.PostInit 调用，此时 LoadAll 已完成、
        /// 属性 key 已注册。spaceName 用 LabMod.spaceName，path 用 StageID 常量 ——
        /// 绝不读 stageDef.GetID()（注入 def 上 GetID 非确定返回损坏 NamespaceID）。
        /// </summary>
        public static void ApplyMetaTo(PVZEngine.Level.StageDefinition stageDef, string stagePath)
        {
            try
            {
                if (stagePath == null)
                {
                    Plugin.Log?.LogWarning("[StageMetaApplier] null stagePath, skip " + stageDef.GetType().Name);
                    return;
                }
                var res = MainManager.Instance?.ResourceManager;
                if (res == null)
                {
                    Plugin.Log?.LogWarning("[StageMetaApplier] ResourceManager unavailable");
                    return;
                }
                var metas = res.GetModStageMetas(LabMod.spaceName);
                if (metas == null || metas.Length == 0)
                {
                    Plugin.Log?.LogWarning("[StageMetaApplier] no stage metas for " + LabMod.spaceName);
                    return;
                }
                StageMeta meta = null;
                for (var i = 0; i < metas.Length; i++)
                {
                    if (metas[i] != null && metas[i].ID == stagePath)
                    {
                        meta = metas[i];
                        break;
                    }
                }
                if (meta == null)
                {
                    Plugin.Log?.LogWarning("[StageMetaApplier] no meta for stage " + stagePath);
                    return;
                }
                ApplyMeta(stageDef, meta);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[StageMetaApplier] error: " + e);
            }
        }

        /// <summary>在 LoadStagesPatch 创建 def 后立即调用（meta 已由 CustomCore 注入）。</summary>
        public static void ApplyMetaTo(PVZEngine.Level.StageDefinition stageDef)
        {
            try
            {
                var stageID = stageDef.GetID();
                if (stageID == null)
                {
                    Plugin.Log?.LogWarning("[StageMetaApplier] stage def has null ID");
                    return;
                }
                var res = MainManager.Instance?.ResourceManager;
                if (res == null)
                {
                    Plugin.Log?.LogWarning("[StageMetaApplier] ResourceManager unavailable");
                    return;
                }
                var metas = res.GetModStageMetas(stageID.SpaceName);
                if (metas == null || metas.Length == 0)
                {
                    Plugin.Log?.LogWarning("[StageMetaApplier] no stage metas for " + stageID);
                    return;
                }
                StageMeta meta = null;
                for (var i = 0; i < metas.Length; i++)
                {
                    if (metas[i] != null && metas[i].ID == stageID.Path)
                    {
                        meta = metas[i];
                        break;
                    }
                }
                if (meta == null)
                {
                    Plugin.Log?.LogWarning("[StageMetaApplier] no meta for stage " + stageID);
                    return;
                }
                ApplyMeta(stageDef, meta);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[StageMetaApplier] error: " + e);
            }
        }

        private static void ApplyMeta(PVZEngine.Level.StageDefinition stageDef, StageMeta meta)
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

            // ENEMY_POOL（NamespaceID[]）：先试泛型；失败由关卡 OnStart 的 level.SetEnemyPool 兜底。
            if (meta.Spawns != null)
                Try(() => stageDef.SetProperty(LogicStageProps.ENEMY_POOL, meta.Spawns), "ENEMY_POOL");

            if (NamespaceID.IsValid(meta.ClearPickupModel))
                Try(() => stageDef.SetProperty(LogicStageProps.CLEAR_PICKUP_MODEL, meta.ClearPickupModel), "CLEAR_PICKUP_MODEL");
            if (NamespaceID.IsValid(meta.MusicID))
                Try(() => stageDef.SetProperty(LogicStageProps.MUSIC_ID, meta.MusicID), "MUSIC_ID");

            Plugin.Log?.LogInfo("[StageMetaApplier] " + LabMod.spaceName + ":" + meta.ID
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
                Plugin.Log?.LogWarning("[StageMetaApplier] SetProperty " + name + " failed: "
                    + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
