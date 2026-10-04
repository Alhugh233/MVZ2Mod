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
    ///
    /// 注意：官方 LoadDefinitionProperties（LoadStageProperties 等）会遍历 mvz2_lab
    /// 的 stage meta 并对注入 def 做大量 SetProperty —— 属性 key 在 LoadAll 阶段对
    /// 注入 def 无效（invalid key 全部丢弃）且原生遍历易 native 崩，因此一并跳过，
    /// 属性改由 LabMod.PostInit 用稳定 ID + 已注册 key 手动应用（StageMetaApplier）。
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
            // 【实验】Pin 修复后，注入 def 的 propertyDict 不再被回收 ——
            // 官方 LoadDefinitionProperties 应能正常应用 meta 属性（关卡名/敌怪池/
            // 类型/音乐）。若仍崩则恢复下方跳过补丁。
            // var targetProps = AccessTools.Method(typeof(ModLoader), "LoadDefinitionProperties");
            // if (targetProps == null)
            // {
            //     Plugin.Log?.LogError("[LoadStagesPatch] ModLoader.LoadDefinitionProperties not found!");
            // }
            // else
            // {
            //     harmony.Patch(targetProps,
            //         prefix: new HarmonyMethod(typeof(LoadStagesPatch), nameof(LoadDefPropsPrefix)));
            // }
            Plugin.Log?.LogInfo("LoadStagesPatch applied");
        }

        /// <summary>跳过官方 LoadDefinitionProperties(mod) 对注入 def 的属性应用。</summary>
        [HarmonyPrefix]
        private static bool LoadDefPropsPrefix(Mod mod)
        {
            try
            {
                if (mod == null || mod.Namespace != LabMod.spaceName)
                    return true; // 其它 mod 走原逻辑
                Plugin.Log?.LogInfo("[LoadStagesPatch] LoadDefinitionProperties skipped for " + mod.Namespace
                    + " (properties applied in PostInit)");
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[LoadStagesPatch] LoadDefPropsPrefix error: " + e);
                return true;
            }
        }

        [HarmonyPrefix]
        private static bool Prefix(ModLoader __instance, Mod mod)
        {
            try
            {
                if (mod == null || mod.Namespace != LabMod.spaceName)
                    return true; // 其他 mod 走原逻辑

                Plugin.Log?.LogInfo("[LoadStagesPatch] LoadStages for " + mod.Namespace);
                // 正式注册费用倍率 buff 定义（乘数 3/5）。
                // 必须在 LoadBuffMetas（LoadDefinitions 内，LoadStages 之后）前 AddDefinition，
                // 这样 LoadBuffMetas 遍历 GetAllBuffDefinitions 时能找到 buffs.xml 词条（无警告），
                // 且读档时 Content.GetBuffDefinition 能恢复定义。
                Buffs.ZeroKSpaceCostBuff.RegisterDefinitions(mod);
                // real_prologue 冒烟。
                var stage = Stages.RealPrologueStage.CreateInstance();
                if (stage != null)
                {
                    mod.AddDefinition(stage);
                    Plugin.TrackDefinition(stage);
                    Plugin.Log?.LogInfo("[LoadStagesPatch] added stage mvz2_lab:"
                        + Stages.RealPrologueStage.StageID);
                }
                // zero_k_space（Phase 3）。
                var zeroStage = Stages.ZeroKSpaceStage.CreateInstance();
                if (zeroStage != null)
                {
                    mod.AddDefinition(zeroStage);
                    Plugin.TrackDefinition(zeroStage);
                    Plugin.Log?.LogInfo("[LoadStagesPatch] added stage mvz2_lab:"
                        + Stages.ZeroKSpaceStage.StageID);
                }
                // unknow_palace（真理伪证殿堂，Phase 4）。
                var unknowStage = Stages.UnknowPalaceStage.CreateInstance();
                if (unknowStage != null)
                {
                    mod.AddDefinition(unknowStage);
                    Plugin.TrackDefinition(unknowStage);
                    Plugin.Log?.LogInfo("[LoadStagesPatch] added stage mvz2_lab:"
                        + Stages.UnknowPalaceStage.StageID);
                    // behaviours 诊断：出怪时 Gem 全局回调对 def 做 HasBehaviour<T>
                    // 遍历 behaviours（原生 List）。若 Enumerator 越界 = List 损坏。
                    // GetBehaviourCount 只读 List._size，安全；此处加载早期可暴露问题。
                    try
                    {
                        var bc = unknowStage.GetBehaviourCount();
                        Plugin.Log?.LogInfo("[LoadStagesPatch] unknow behaviours count=" + bc);
                    }
                    catch (System.Exception bcErr)
                    {
                        Plugin.Log?.LogError("[LoadStagesPatch] unknow behaviours count error: " + bcErr);
                    }
                }
                // 记录到 LabMod（PostInit 时用稳定 ID 常量应用 meta 属性）。
                // 不用 stage.GetID()（注入 def 上 GetID 非确定返回损坏 NamespaceID），
                // 直接用创建时已知的 StageID 常量构造。
                LabMod.RegisterStage(stage, Stages.RealPrologueStage.StageID);
                LabMod.RegisterStage(zeroStage, Stages.ZeroKSpaceStage.StageID);
                LabMod.RegisterStage(unknowStage, Stages.UnknowPalaceStage.StageID);

                // 绕过官方 Mod.GetDefinitions 的原生 LINQ 的补丁由
                // DSHCore.InjectedDefinitions.Apply() 统一装（在 Plugin.Load 里）。
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
