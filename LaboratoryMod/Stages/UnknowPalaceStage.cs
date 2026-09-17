using System;
using DSHCore;
using Il2CppInterop.Runtime.Injection;
using Il2CppSystem.Collections.Generic;
using MVZ2.GameContent.Enemies;
using MVZ2.GameContent.Spawns;
using MVZ2.GameContent.Stages;
using MVZ2Logic.Blueprints;
using MVZ2Logic.Level;
using MVZ2Logic.Spawns;
using PVZEngine;
using PVZEngine.Callbacks;
using PVZEngine.Entities;
using PVZEngine.Grids;
using PVZEngine.Level;
using PVZEngine.SeedPacks;
using PVZEngine.Spawns;
using PVZEngine.Tools.Random;
using UnityEngine;

namespace LaboratoryMod.Stages
{
    /// <summary>
    /// 真理伪证殿堂（Unknow Palace），0.7 注入移植版。
    ///
    /// 0.6.4 UnknowPalaceStageBehaviour 核心机制：
    ///   1. POST_BLUEPRINT_SELECTION：记录玩家选择的卡（recorded / commandBlocks）；
    ///   2. PostWave(wave&gt;0)：把全部卡槽替换为记录序列中的卡
    ///      （wave &lt;= slotCount 用 recorded[wave-1]，否则随机已记录卡；空位清槽）；
    ///   3. 接管出怪（PRE 点数置 0 + POST 自定义）：
    ///      wave 1..9 每波一种（去 mega）、wave 10 固定 mega_mutant_zombie、
    ///      wave 11+ 每波种类 +1；末波每 lane 生成敌人池全部种类；
    ///      UFO 走网格生成。
    ///
    /// 0.7 注入实现（规避 metadata v31 泛型 trigger）：
    ///   - 选卡记录：LevelEngine 关卡开始时 seedpacks 已按玩家选择建好
    ///     （槽位顺序 == 选择顺序），在第一波出怪/OnSetup 时从 seedpacks
    ///     快照到注入 def 实例字段（等价 0.6.4 的 recorded，无需 trigger）；
    ///   - 出怪接管：Harmony hook LogicLevelExt.SpawnWaveEnemies（prefix 返回 false
    ///     跳过原方法 = PRE 置0 + 移除原版出怪；原方法内 PRE/POST trigger 与
    ///     spawnDef 日程均不会执行，比 0.6.4 更彻底、无需 RemoveVanillaUFOBuffs）；
    ///   - PostWave 换卡：override OnPostWave（出怪后原生调用）。
    ///
    /// 说明：为规避 Il2Cpp 泛型容器（HashSet/IEnumerable）在托管侧的遍历限制，
    /// UFO 落点简化为"指定 lane 最右列网格"（与官方 SpawnUFOAtLane 的兜底一致）；
    /// variant 池仍用官方 FillUFOVariantRandomPool（List&lt;int&gt; 可托管访问）。
    /// </summary>
    public class UnknowPalaceStage : StageDefinition
    {
        public const string StageID = "unknow_palace";

        public const int FIRST_PHASE_WAVE_COUNT = 9;
        public const int MEGA_MUTANT_WAVE = 10;
        public const int SECOND_PHASE_START_WAVE = 11;
        public const int MAX_SPAWN_COUNT = 50;

        private const string ENEMY_MEGA_MUTANT_PATH = "mega_mutant_zombie";
        private const string ENEMY_UFO_PATH = "ufo";

        public UnknowPalaceStage(IntPtr ptr) : base(ptr)
        {
        }

        // ---- 选卡记录（进程内状态）----
        // 重要：Il2CppInterop 注入类中，level.StageDefinition 每次访问可能返回
        // 不同的托管 wrapper，实例字段会各自为 null/默认（原生侧只有基类字段）。
        // 因此本关状态必须用 static（关卡 def 全局单例，无并发问题），
        // 并在 OnStart（每关开始）重置，避免跨关卡残留。
        private static NamespaceID[] recorded;
        private static bool[] recordedCommandBlocks;
        private static int planSeed;
        private static bool planSeedReady;

        /// <summary>CreateInstance 创建的原始托管实例（GetStage cast 失败的兜底）。</summary>
        internal static UnknowPalaceStage Current;

        internal static UnknowPalaceStage CreateInstance()
        {
            var stage = new UnknowPalaceStage(ClassInjector.DerivedConstructorPointer<UnknowPalaceStage>());
            ClassInjector.DerivedConstructorBody(stage);

            stage.id = Il2CppPin.Pin(new NamespaceID(LabMod.spaceName, StageID));
            stage.propertyDict = Il2CppPin.Pin(new PropertyDictionary());
            stage.triggers = Il2CppPin.Pin(new List<ITrigger>());
            stage.behaviours = Il2CppPin.Pin(new List<StageBehaviour>());

            stage.SetProperty(EngineStageProps.WAVES_PER_FLAG, 10);
            stage.SetProperty(EngineLevelProps.RECHARGE_SPEED, 1f);

            // 行为组合（二分：先去掉 Gem/Starshard——出怪时 Gem 全局回调对 def 做
            // HasBehaviour<GemStageBehaviour> 泛型遍历在注入 def 上崩（IL2CPP 泛型+
            // 注入类交互），去掉后 HasBehaviour 遍历找不到即返回，规避崩溃）。
            stage.AddBehaviour(Il2CppPin.Pin(new WaveStageBehaviour(stage)));
            stage.AddBehaviour(Il2CppPin.Pin(new FinalWaveClearBehaviour(stage)));

            Current = stage;
            return stage;
        }

        public override bool TryGetPropertyObject(IPropertyKey name, out Il2CppSystem.Object value)
        {
            try
            {
                return propertyDict.TryGetPropertyObject(name, out value);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[UnknowPalaceStage] TryGetPropertyObject error: " + e);
                value = null;
                return false;
            }
        }

        /// <summary>
        /// 读档兜底：LevelEngine 重建后从当前 seedpacks 反向恢复记录。
        /// OnStart（level.Start）时 seedpacks 已由 ApplyChoose 建好，可直接补录；
        /// 出怪时（第一波）再调一次兜底读档场景。
        /// </summary>
        private void RestoreFromSeedPacks(LevelEngine level)
        {
            if (recorded != null)
                return;
            try
            {
                int slotCount = level.GetSeedSlotCount();
                if (slotCount <= 0)
                    return;
                var rec = new NamespaceID[slotCount];
                var cmd = new bool[slotCount];
                bool any = false;
                for (int i = 0; i < slotCount; i++)
                {
                    var seed = level.GetSeedPackAt(i);
                    if (seed != null)
                    {
                        var defID = seed.GetDefinitionID();
                        if (defID != null)
                        {
                            rec[i] = defID;
                            cmd[i] = LogicSeedProps.IsCommandBlock(seed);
                            any = true;
                        }
                    }
                }
                if (!any)
                    return;
                recorded = rec;
                recordedCommandBlocks = cmd;
                Plugin.Log?.LogInfo("[UnknowPalaceStage] restored " + slotCount + " blueprints from seedpacks");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[UnknowPalaceStage] RestoreFromSeedPacks error: " + e);
            }
        }

        public override void OnStart(LevelEngine level)
        {
            try
            {
                // 每关开始重置 static 状态（避免跨关卡/重进残留）。
                recorded = null;
                recordedCommandBlocks = null;
                planSeedReady = false;
                // level.Start 前 ApplyChoose 已建立 seedpacks，直接补录选卡。
                RestoreFromSeedPacks(level);
                EnsurePlanSeed(level, "start");

                // ===== level 级敌怪池兜底（RealPrologueStage 同款）=====
                // 进关预览 CreatePreviewEnemies 读 level.GetEnemyPool()；def 级
                // 泛型 SetProperty(ENEMY_POOL) 在 metadata v31 上对注入 def 不可靠
                // （列表关卡名丢失/读 null 同源），这里直接从 meta 兜底写到 LevelEngine。
                try
                {
                    if (level.GetEnemyPool() == null)
                    {
                        var stageID = GetID();
                        if (stageID != null)
                        {
                            var res = MVZ2.Managers.MainManager.Instance?.ResourceManager;
                            var metas = res?.GetModStageMetas(stageID.SpaceName);
                            if (metas != null)
                            {
                                for (var i = 0; i < metas.Length; i++)
                                {
                                    var meta = metas[i];
                                    if (meta != null && meta.ID == stageID.Path && meta.Spawns != null)
                                    {
                                        LogicStageProps.SetEnemyPool(level, meta.Spawns);
                                        Plugin.Log?.LogInfo("[UnknowPalaceStage] OnStart: enemy pool fallback set ("
                                            + meta.Spawns.Length + " spawns)");
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    Plugin.Log?.LogInfo("[UnknowPalaceStage] OnStart: pool-set="
                        + (level.GetEnemyPool() != null));
                    PVZEngine.Level.StageDefinition self = this;
                    Plugin.Log?.LogInfo("[UnknowPalaceStage] OnStart def: levelName='"
                        + LogicStageProps.GetLevelName(self) + "'");
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogError("[UnknowPalaceStage] OnStart enemy pool fallback error: " + e);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[UnknowPalaceStage] OnStart error: " + e);
            }
        }

        // ============ PostWave 换卡（0.6.4 UnknowPalaceStageBehaviour.PostWave） ============
        public override void OnPostWave(LevelEngine level, int wave)
        {
            try
            {
                if (wave <= 0)
                    return;
                var slotCount = level.GetSeedSlotCount();
                if (slotCount <= 0)
                    return;
                if (recorded == null || recorded.Length < slotCount)
                {
                    Plugin.Log?.LogWarning("[UnknowPalaceStage] PostWave " + wave + ": recorded missing, skip replace");
                    return;
                }
                NamespaceID target;
                bool targetCommandBlock;
                if (wave <= slotCount)
                {
                    // 前 k 波：按序使用记录中的第 wave 张卡（含空位语义）。
                    target = recorded[wave - 1];
                    targetCommandBlock = recordedCommandBlocks[wave - 1];
                }
                else
                {
                    // 后续波：随机抽取一张已记录的卡。
                    var validIndices = new System.Collections.Generic.List<int>();
                    for (int i = 0; i < recorded.Length; i++)
                    {
                        if (recorded[i] != null)
                            validIndices.Add(i);
                    }
                    if (validIndices.Count <= 0)
                    {
                        Plugin.Log?.LogWarning("[UnknowPalaceStage] PostWave " + wave + ": no valid recorded card");
                        return;
                    }
                    var rng = level.GetSpawnRNG();
                    var index = validIndices[rng.Next(validIndices.Count)];
                    target = recorded[index];
                    targetCommandBlock = recordedCommandBlocks[index];
                }

                ReplaceAllBlueprints(level, target, targetCommandBlock);
                Plugin.Log?.LogInfo("[UnknowPalaceStage] PostWave " + wave + " -> card "
                    + (target == null ? "null" : target.ToString()));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[UnknowPalaceStage] OnPostWave error: " + e);
            }
        }

        private void ReplaceAllBlueprints(LevelEngine level, NamespaceID target, bool commandBlock)
        {
            for (int i = 0; i < level.GetSeedSlotCount(); i++)
            {
                if (target == null)
                {
                    level.RemoveSeedPackAt(i);
                    continue;
                }
                var seed = level.CreateSeedPack(target);
                if (seed == null)
                    continue;
                LogicSeedProps.SetCommandBlock(seed, commandBlock);
                EngineSeedProps.SetStartRecharge(seed, true);
                EngineSeedProps.FullRecharge(seed);
                level.ReplaceSeedPackAt(i, seed);
            }
        }

        // ============ 自定义出怪（SpawnWaveEnemies 前缀接管） ============
        internal void SpawnCustomWave(LevelEngine level, int wave)
        {
            try
            {
                EnsurePlanSeed(level, "spawn");
                // 补录选卡：第一波出怪必然发生在选卡（SetupBattleBlueprints）之后，
                // 此时按 seedpacks 快照得到的槽位内容 == 0.6.4 的 recorded。
                if (recorded == null)
                    RestoreFromSeedPacks(level);
                if (wave <= 0)
                    return;

                if (EngineStageProps.IsFinalWave(level, wave))
                {
                    Plugin.Log?.LogInfo("[UnknowPalaceStage] final wave " + wave + " script");
                    SpawnFinalWaveScript(level);
                    return;
                }

                var types = GetWaveTypes(level, wave);
                if (types == null || types.Length <= 0)
                    return;

                var totalPoints = LogicLevelExt.CalculateSpawnPoints(level);
                var rng = level.GetSpawnRNG();
                int spawned = 0;
                while (totalPoints > 0 && spawned < MAX_SPAWN_COUNT)
                {
                    var type = types[rng.Next(types.Length)];
                    var spawnDef = level.Content.GetSpawnDefinition(type);
                    if (spawnDef == null)
                        break;
                    var entityID = LogicSpawnProps.GetSpawnEntity(spawnDef);
                    if (entityID != null && entityID.Path == ENEMY_UFO_PATH)
                    {
                        SpawnUFOImmediate(level);
                    }
                    else
                    {
                        LogicLevelExt.SpawnEnemyAtRandomLane(level, spawnDef);
                    }
                    totalPoints -= LogicSpawnProps.GetSpawnLevel(spawnDef);
                    spawned++;
                }
                Plugin.Log?.LogInfo("[UnknowPalaceStage] wave " + wave + " custom spawn: " + spawned);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[UnknowPalaceStage] SpawnCustomWave error: " + e);
            }
        }

        private void EnsurePlanSeed(LevelEngine level, string where)
        {
            if (planSeedReady)
                return;
            var seed = level.GetSpawnRNG().Next();
            if (seed == 0)
                seed = 1;
            planSeed = seed;
            planSeedReady = true;
            Plugin.Log?.LogInfo("[UnknowPalaceStage] planSeed(" + where + ")=" + planSeed);
        }

        private NamespaceID[] GetWaveTypes(LevelEngine level, int wave)
        {
            var pool = LogicStageProps.GetEnemyPool(level);
            if (pool == null || pool.Length <= 0)
                return null;
            var rng = new RandomGenerator(planSeed);

            if (wave >= 1 && wave <= FIRST_PHASE_WAVE_COUNT)
            {
                var nonMega = new System.Collections.Generic.List<NamespaceID>();
                for (int i = 0; i < pool.Length; i++)
                {
                    if (pool[i] != null && pool[i].Path != ENEMY_MEGA_MUTANT_PATH)
                        nonMega.Add(pool[i]);
                }
                if (nonMega.Count <= 0)
                    return null;
                var arr = new NamespaceID[nonMega.Count];
                for (int i = 0; i < nonMega.Count; i++)
                    arr[i] = nonMega[i];
                Shuffle(arr, rng);
                if (wave - 1 >= arr.Length)
                    return null;
                return new[] { arr[wave - 1] };
            }
            if (wave == MEGA_MUTANT_WAVE)
            {
                for (int i = 0; i < pool.Length; i++)
                {
                    if (pool[i] != null && pool[i].Path == ENEMY_MEGA_MUTANT_PATH)
                        return new[] { pool[i] };
                }
                return null;
            }
            if (wave >= SECOND_PHASE_START_WAVE)
            {
                var count = wave - (SECOND_PHASE_START_WAVE - 1);
                var arr = new NamespaceID[pool.Length];
                for (int i = 0; i < pool.Length; i++)
                    arr[i] = pool[i];
                Shuffle(arr, rng);
                var take = Math.Min(count, arr.Length);
                var result = new NamespaceID[take];
                for (int i = 0; i < take; i++)
                    result[i] = arr[i];
                return result;
            }
            return null;
        }

        private static void Shuffle(NamespaceID[] arr, RandomGenerator rng)
        {
            for (int i = arr.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                var tmp = arr[i];
                arr[i] = arr[j];
                arr[j] = tmp;
            }
        }

        // ---- 末波脚本：每 lane 生成敌人池全部种类 ----
        private void SpawnFinalWaveScript(LevelEngine level)
        {
            var pool = LogicStageProps.GetEnemyPool(level);
            if (pool == null || pool.Length <= 0)
                return;
            var rng = level.GetSpawnRNG();
            var lanes = level.GetMaxLaneCount();
            for (int lane = 0; lane < lanes; lane++)
            {
                for (int i = 0; i < pool.Length; i++)
                {
                    var spawnID = pool[i];
                    if (spawnID == null)
                        continue;
                    var spawnDef = level.Content.GetSpawnDefinition(spawnID);
                    if (spawnDef == null)
                        continue;
                    var entityID = LogicSpawnProps.GetSpawnEntity(spawnDef);
                    if (entityID != null && entityID.Path == ENEMY_UFO_PATH)
                    {
                        SpawnUFOAtLane(level, lane, rng);
                    }
                    else
                    {
                        LogicLevelExt.SpawnEnemy(level, spawnDef, lane);
                    }
                }
            }
        }

        // ---- UFO 网格生成 ----
        private void SpawnUFOImmediate(LevelEngine level)
        {
            try
            {
                var rng = level.GetSpawnRNG();
                var variant = PickUFOVariant(level, rng);
                if (variant < 0)
                    return;
                // 随机 lane，取该 lane 最右列网格。
                var lanes = level.GetMaxLaneCount();
                if (lanes <= 0)
                    return;
                var lane = rng.Next(lanes);
                SpawnUFOAtLaneGrid(level, lane, variant);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[UnknowPalaceStage] SpawnUFOImmediate error: " + e);
            }
        }

        private void SpawnUFOAtLane(LevelEngine level, int lane, RandomGenerator rng)
        {
            try
            {
                var variant = PickUFOVariant(level, rng);
                if (variant < 0)
                    return;
                SpawnUFOAtLaneGrid(level, lane, variant);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[UnknowPalaceStage] SpawnUFOAtLane error: " + e);
            }
        }

        private int PickUFOVariant(LevelEngine level, RandomGenerator rng)
        {
            var variantPool = new List<int>();
            UndeadFlyingObject.FillUFOVariantRandomPool(level,
                LogicLevelProps.GetRightFaction(level), variantPool);
            if (variantPool.Count <= 0)
                return -1;
            return variantPool[rng.Next(variantPool.Count)];
        }

        /// <summary>
        /// 指定 lane 的最右列网格生成 UFO（0.6.4 GetSpawnGridAtLane 的简化版）。
        /// </summary>
        private void SpawnUFOAtLaneGrid(LevelEngine level, int lane, int variant)
        {
            LawnGrid grid = null;
            for (int column = level.GetMaxColumnCount() - 1; column >= 0; column--)
            {
                var g = level.GetGrid(column, lane);
                if (g != null)
                {
                    grid = g;
                    break;
                }
            }
            if (grid == null)
                return;
            var enemy = UndeadFlyingObject.SpawnAtGrid(grid, variant);
            if (enemy != null)
            {
                LogicLevelExt.TriggerEnemySpawned(level,
                    VanillaSpawnID.GetFromEntity(VanillaEnemyID.ufo), enemy);
            }
        }

        private static int CountFilled(NamespaceID[] arr)
        {
            int count = 0;
            if (arr == null)
                return 0;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] != null)
                    count++;
            }
            return count;
        }
    }
}
