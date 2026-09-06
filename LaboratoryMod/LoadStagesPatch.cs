using System;
using HarmonyLib;
using MVZ2.Modding;
using MVZ2Logic.Modding;

namespace LaboratoryMod
{
    /// <summary>
    /// 关卡定义创建接管（替代 IStageDefinitionProvider 注册）。
    ///
    /// 0.7 中 ModLoader.LoadAll 会对 stageProviders.OrderBy(Priority) 做原生 LINQ
    /// 接口分派 —— 注入类实现 IStageDefinitionProvider 时在 metadata v31 抛
    /// MethodAccessException（get_Priority on System.Object，与 SetPropertyObject 同源）。
    ///
    /// 因此不注册 provider，而是在官方 LoadStages(mod) 时机（LoadDefinitions 内）：
    /// 对 mvz2_lab 的 meta 直接创建注入的 StageDefinition 并 mod.AddDefinition(...)。
    /// 随后 LoadDefinitionProperties 中的官方 LoadStageProperties(mod) 会自动读取
    /// meta 并应用全部属性（ENEMY_POOL / 出怪点数 / 名字 / 音乐 / 解锁等），无需 hack。
    ///
    /// 注意：本补丁只拦截 mod.Namespace == mvz2_lab，其余 mod 走原逻辑。
    /// </summary>
    internal static class LoadStagesPatch
    {
        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            var harmony = new Harmony("qoznos.mvz2.labmod.loadstagespatch");
            var target = AccessTools.Method(typeof(ModLoader), "LoadStages");
            if (target == null)
            {
                Plugin.Log?.LogError("[LoadStagesPatch] ModLoader.LoadStages not found!");
                return;
            }
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(LoadStagesPatch), nameof(Prefix)));
            Plugin.Log?.LogInfo("LoadStagesPatch applied");
        }

        [HarmonyPrefix]
        private static bool Prefix(ModLoader __instance, Mod mod)
        {
            try
            {
                if (mod == null || mod.Namespace != LabMod.spaceName)
                    return true; // 其他 mod 走原逻辑

                Plugin.Log?.LogInfo("[LoadStagesPatch] LoadStages for " + mod.Namespace);
                // real_prologue 冒烟。
                var stage = Stages.RealPrologueStage.CreateInstance();
                if (stage != null)
                {
                    mod.AddDefinition(stage);
                    Plugin.Log?.LogInfo("[LoadStagesPatch] added stage " + stage.GetID());
                }
                // zero_k_space（Phase 3）。
                var zeroStage = Stages.ZeroKSpaceStage.CreateInstance();
                if (zeroStage != null)
                {
                    mod.AddDefinition(zeroStage);
                    Plugin.Log?.LogInfo("[LoadStagesPatch] added stage " + zeroStage.GetID());
                }
                // 记录到 LabMod，PostInit 时统一应用 meta 属性
                // （此刻 vanilla 程序集属性尚未注册，SetProperty 会 invalid key）。
                LabMod.RegisterStage(stage);
                LabMod.RegisterStage(zeroStage);
                // 跳过原方法（不再遍历 stageProviders）。
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[LoadStagesPatch] Prefix error: " + e);
                return false;
            }
        }
    }
}
