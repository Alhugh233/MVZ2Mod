using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using DSHCore;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2.Modding;
using MVZ2Logic.Games;
using PVZEngine;
using LaboratoryMod.Stages;

namespace LaboratoryMod
{
    /// <summary>
    /// MVZ2 实验室内容 Mod（依赖 DSHCore 前置库）。
    ///
    /// 0.7 集成方式（官方链）：
    ///   1. ModBootstrap 登记 mvz2_lab 并注册注入类
    ///      （XML metas 磁盘注入、vtable 兜底生效）。
    ///   2. OnRegisterModInfos 注册 ModInfo：Logic = LabMod.CreateInstance()
    ///      （注入实例，LoadMods 不再重建 → 直接进入官方 LoadAll）。
    ///   3. LabMod.PreInit 准备关卡注册（实际定义由 LoadStagesPatch 在官方时机创建）。
    ///   4. ModLoader.LoadStages 官方用 meta → 建关卡定义并 AddDefinition；
    ///      LoadStageProperties 官方自动应用 meta 属性（ENEMY_POOL 等）。
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency(DSHCore.Plugin.GUID)]
    public class Plugin : BasePlugin
    {
        public const string GUID = "qoznos.mvz2.labmod";
        public const string NAME = "MVZ2 Laboratory Content";
        public const string VERSION = "0.2.0";

        internal static new ManualLogSource Log;

        /// <summary>
        /// 本 mod 全部定义的稳定跟踪（绕过官方 Mod.GetDefinitions 的原生 LINQ：
        /// metadata v31 注入环境下 DefinitionGroup.GetDefinitions 的 SelectMany
        /// 接口枚举非确定性抛 MethodAccessException / 返回空 → game.AddMod 拷不到
        /// defs → 列表消失 + ChangeStage missing。作者亦在移除 LINQ 印证此坑）。
        /// </summary>
        internal static readonly System.Collections.Generic.List<Definition> AllDefinitions
            = new System.Collections.Generic.List<Definition>();

        internal static void TrackDefinition(Definition def)
        {
            if (def == null)
                return;
            lock (AllDefinitions)
            {
                for (int i = 0; i < AllDefinitions.Count; i++)
                {
                    if (AllDefinitions[i] == def)
                        return;
                }
                AllDefinitions.Add(def);
            }
        }

        public override void Load()
        {
            Log = base.Log;

            // 登记命名空间 + 注册全部注入类（必须在任何实例化之前）。
            // ClassInjector 缺陷修复已由 DSHCore 在它自己的 Load() 里应用。
            ModBootstrap.Register(
                spaceName: LabMod.spaceName,
                logicType: typeof(LabMod),
                xmlDefaultNamespace: "mvz2",
                extraInjectedTypes: new[]
                {
                    typeof(Stages.RealPrologueStage),
                    typeof(Stages.ZeroKSpaceStage),
                    typeof(Stages.UnknowPalaceStage),
                    typeof(Buffs.ZeroKSpaceCostBuff)
                });

            // 0.7 中 ModLoader.LoadAll 会对 stageProviders.OrderBy(Priority) 做原生 LINQ
            // 接口分派，注入类实现 IStageDefinitionProvider 在 metadata v31 抛
            // MethodAccessException（与 SetPropertyObject 同源）。因此不用 provider，
            // 改由 LoadStagesPatch 在官方 LoadStages 时机直接创建关卡定义。
            LoadStagesPatch.Apply();
            // unknow_palace 出怪接管在 LabMod.PostInit 应用（此时 LoadAll 已完成、
            // AddMod 已跑完；Chainloader 早期 patch 会提前初始化 LogicLevelExt
            // 干扰官方加载 → game.AddMod 时 DefinitionGroup List 损坏崩溃）。

            // 订阅 0.7 官方的外部 Mod 注册点（System.Func → Il2CppSystem.Func
            // 的委托转换由 DSHCore.ModInfos 完成）。
            ModInfos.OnRegister(OnRegisterModInfos);

            Log.LogInfo(NAME + " v" + VERSION + " loaded, waiting for ModManager.RegisterModInfos...");
        }

        /// <summary>
        /// 向游戏注册本 Mod 的 ModInfo。Logic 直接是注入实例（0.7 不再重建）。
        /// </summary>
        private static Il2CppSystem.Threading.Tasks.Task OnRegisterModInfos(
            IGlobalGame game, Il2CppSystem.Collections.Generic.List<ModInfo> results)
        {
            try
            {
                Log.LogInfo("OnRegisterModInfos called, adding ModInfo " + LabMod.spaceName);
                var logic = LabMod.CreateInstance();
                // 立即修复 vtable（防止 Class::Init 重置影响后续官方 LoadAll 虚调用）。
                VTableRebind.Ensure(typeof(LabMod));

                // ModInfo 构造样板（空 locator / 存档版本 / 空 Assemblies）见
                // DSHCore.ModInfos。定义经官方 LoadStages meta 链创建，
                // 不走程序集扫描 attribute。
                results.Add(ModInfos.Create(LabMod.spaceName, logic, "MVZ2 Laboratory"));
                Log.LogInfo("OnRegisterModInfos: ModInfo added (mvz2_lab), Logic="
                    + (logic == null ? "null" : logic.GetType().FullName));
            }
            catch (Exception e)
            {
                Log.LogError("OnRegisterModInfos error: " + e);
            }
            return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }
    }
}
