using System;
using HarmonyLib;
using MVZ2Logic.Level;
using PVZEngine.Level;

namespace LaboratoryMod.Stages
{
    /// <summary>
    /// 真理伪证殿堂（Unknow Palace）所需的引擎侧 Hook：
    ///   LogicLevelExt.SpawnWaveEnemies（每波出怪）前缀对本关完全接管出怪。
    ///
    /// 原 0.6.4 的选卡记录（POST_BLUEPRINT_SELECTION）在本 0.7 移植中不再需要：
    /// 选卡完成后 SetupBattleBlueprints 会按玩家选择建 seedpack（槽位顺序 = 选择顺序），
    /// 而第一波出怪（RunWave→SpawnWaveEnemies）必然发生在选卡之后，因此 stage 在
    /// 出怪前缀内从当前 seedpacks 快照即可得到与 0.6.4 recorded 等价的记录。
    ///
    /// SpawnWaveEnemies 是 MVZ2.Logic 的静态扩展方法，被原生代码调用；Harmony 在
    /// IL2CPP 上 patch 静态方法可行（LoadStagesPatch 先例）。非 unknow_palace
    /// 关卡均走原逻辑（前缀返回 true）。
    /// </summary>
    internal static class UnknowPalacePatch
    {
        private static bool applied;
        private static readonly object gate = new object();

        public static void Apply()
        {
            if (applied)
                return;
            lock (gate)
            {
                if (applied)
                    return;
                applied = true;
                var harmony = new Harmony("qoznos.mvz2.labmod.unknowpalace");

                var spawn = AccessTools.Method(typeof(LogicLevelExt), "SpawnWaveEnemies",
                    new[] { typeof(LevelEngine), typeof(int) });
                if (spawn == null)
                {
                    Plugin.Log?.LogError("[UnknowPalacePatch] SpawnWaveEnemies not found!");
                }
                else
                {
                    harmony.Patch(spawn, prefix: new HarmonyMethod(typeof(UnknowPalacePatch),
                        nameof(SpawnPrefix)));
                    Plugin.Log?.LogInfo("[UnknowPalacePatch] SpawnWaveEnemies patched");
                }
            }
        }

        /// <summary>从 level.StageDefinition 取得 unknow_palace 注入 def（若非本关返回 null）。</summary>
        private static UnknowPalaceStage GetStage(LevelEngine level)
        {
            try
            {
                if (level == null)
                    return null;
                var def = level.StageDefinition;
                if (def == null)
                    return null;
                // 类型判断（托管侧 is）在 v31 注入类上可用；不行则退化 ID 匹配。
                var stage = def as UnknowPalaceStage;
                if (stage != null)
                    return stage;
                var id = def.GetID();
                if (id != null && id.SpaceName == LabMod.spaceName && id.Path == UnknowPalaceStage.StageID)
                {
                    // level.StageDefinition 返回的可能是同一 native 对象的新托管
                    // wrapper，as 到注入子类失败；但 ID 已确认是本关，返回 CreateInstance
                    // 缓存的原始实例（状态为 static，任何 wrapper 共享）。
                    if (UnknowPalaceStage.Current != null)
                        return UnknowPalaceStage.Current;
                    Plugin.Log?.LogWarning("[UnknowPalacePatch] matched unknow_palace by ID but no cached instance");
                }
                return null;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[UnknowPalacePatch] GetStage error: " + e);
                return null;
            }
        }

        [HarmonyPrefix]
        private static bool SpawnPrefix(LevelEngine level, int wave)
        {
            try
            {
                var stage = GetStage(level);
                if (stage == null)
                    return true; // 其他关卡走原逻辑
                Plugin.Log?.LogInfo("[UnknowPalacePatch] SpawnWaveEnemies intercepted wave=" + wave);
                stage.SpawnCustomWave(level, wave);
                return false; // 完全接管本波出怪
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[UnknowPalacePatch] SpawnPrefix error: " + e);
                return true;
            }
        }
    }
}
