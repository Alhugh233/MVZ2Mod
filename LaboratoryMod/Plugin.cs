using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using DSHCore;
using DSHCore.Ui;
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
        public const string VERSION = "0.2.1";

        /// <summary>
        /// 在「模组设置（DSHCore）」页里显示的**源串**（= 语言中立的那一份）。
        /// 中文名不是写死的：<c>UiLocale.Add(DisplayName, "实验室")</c> 是一句译文，
        /// 游戏切到英文时列表里显示的就是这个英文原名。
        /// </summary>
        public const string DisplayName = "MVZ2 Laboratory";

        internal static new ManualLogSource Log;

        /// <summary>
        /// 本 mod 全部定义的稳定跟踪（绕过官方 Mod.GetDefinitions 的原生 LINQ：
        /// metadata v31 注入环境下 DefinitionGroup.GetDefinitions 的 SelectMany
        /// 接口枚举非确定性抛 MethodAccessException / 返回空 → game.AddMod 拷不到
        /// defs → 列表消失 + ChangeStage missing。作者亦在移除 LINQ 印证此坑）。
        ///
        /// <para>存储与补丁已收敛到 <see cref="InjectedDefinitions"/>（DSHCore 的公共设施）。
        /// 这里保留同名属性只是为了不改动已有调用点 —— 它返回的就是设施内部那个列表，
        /// 所以 <c>lock (AllDefinitions)</c> 依然锁在同一个对象上。</para>
        /// </summary>
        internal static System.Collections.Generic.List<Definition> AllDefinitions
        {
            get { return InjectedDefinitions.ListFor(LabMod.spaceName); }
        }

        internal static void TrackDefinition(Definition def)
        {
            InjectedDefinitions.Track(def);
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

            // 定义设施：装补丁 + 声明命名空间。
            // 本 mod 的定义全部由手工创建（注入类的官方自动注册路径被 interop 堵死，
            // 见 InjectedDefinitions 的类注释），所以跟踪 / 拦截 / 钉 GC 根这三件事
            // 都交给 DSHCore 的公共设施，本 mod 不再自带一份。
            InjectedDefinitions.Apply();
            InjectedDefinitions.Register(LabMod.spaceName);

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

            // 「模组设置」页里显示的**中文名**：源串是下面的英文原名，
            // 译文挂在 DSHCore.Ui.UiLocale（= 游戏的语言系统，切到英文就显示英文名）。
            // DeclareMod 另外把「一句说明 + 作者」也带上（本 mod 有 ModInfo，不带也行）。
            UiLocale.Add(DisplayName, "实验室");
            UiOptions.DeclareMod(LabMod.spaceName, DisplayName,
                "实验室小游戏（real_prologue / zero_k_space / unknow_palace）", "QoZnoS");

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
                results.Add(ModInfos.Create(LabMod.spaceName, logic, DisplayName));
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
