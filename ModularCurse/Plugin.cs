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

namespace ModularCurse
{
    /// <summary>
    /// 模块化诅咒（Modular Curse）—— 依赖 DSHCore 前置库。
    ///
    /// 把自定义诅咒接入**原版诅咒系统**（0.7.0 起加入）：
    ///   1. ModBootstrap 登记 modular_curse（DSHCore.MetaInjection 据此把
    ///      StreamingAssets/Mods/modular_curse/Content/metas/*.xml 注入 ModResource）。
    ///   2. OnRegisterModInfos 注册 ModInfo：Logic = ModularCurseMod.CreateInstance()。
    ///   3. 官方 LoadAll 对每个 mod 调 PreInit/Load/OnInit；其中
    ///      - ModLoader.LoadStages 时机由 CurseDefinitionPatch 创建 24 个诅咒 buff 定义；
    ///      - ModLoader.LoadCurseMetas 用本 mod 的 curses.xml 创建 DefaultCurseDefinition。
    ///   4. 玩家在关卡开始前勾选诅咒 → Level.AddBuff(buffId) → 诅咒 buff 生效。
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency(DSHCore.Plugin.GUID)]
    public class Plugin : BasePlugin
    {
        public const string GUID = "qoznos.mvz2.modularcurse";
        public const string NAME = "MVZ2 Modular Curse";
        public const string VERSION = "0.3.0";

        internal static new ManualLogSource Log;

        /// <summary>
        /// 本 mod 全部定义的稳定跟踪（绕过官方 Mod.GetDefinitions 的原生 LINQ：
        /// metadata v31 注入环境下 DefinitionGroup.GetDefinitions 的
        /// SelectMany/OfType 非确定性抛 MethodAccessException 或返回空 →
        /// game.AddMod 拷不到 defs → 诅咒列表为空）。
        ///
        /// <para>存储与补丁已收敛到 <see cref="InjectedDefinitions"/>（DSHCore 的公共设施，
        /// 之前 LaboratoryMod 与本 mod 各抄了一份）。返回的是设施内部那个列表对象，
        /// 所以 <c>lock (AllDefinitions)</c> 依然锁在同一个对象上。</para>
        /// </summary>
        internal static System.Collections.Generic.List<Definition> AllDefinitions
        {
            get { return InjectedDefinitions.ListFor(ModularCurseMod.spaceName); }
        }

        internal static void TrackDefinition(Definition def)
        {
            InjectedDefinitions.Track(def);
        }

        public override void Load()
        {
            Log = base.Log;
            Log.LogInfo("[Plugin] Load begin");

            // 登记命名空间 + 注册注入类（必须在任何实例化之前）。
            // ClassInjector 缺陷修复已由 DSHCore 在它自己的 Load() 里应用。
            ModBootstrap.Register(
                spaceName: ModularCurseMod.spaceName,
                logicType: typeof(ModularCurseMod),
                xmlDefaultNamespace: "mvz2");
            CurseBuffs.RegisterAll();

            // 定义设施：装补丁 + 声明命名空间。
            // 「跟踪 / 拦 Mod.GetDefinitions / 跳 Mod.PostReloadMods」这三件事
            // 已从本 mod 的 CurseDefinitionPatch 收敛到 DSHCore 的公共设施。
            InjectedDefinitions.Apply();
            InjectedDefinitions.Register(ModularCurseMod.spaceName);

            // 定义/生命周期补丁。
            CurseDefinitionPatch.Apply();

            // 原版没有扩展点的那几条诅咒所需的 Harmony 补丁。
            CurseHooks.Apply();

            // 诅咒弹窗内的「诅咒池」条目行 + 按池过滤面板。
            CursePoolFeature.Apply();

            // 控制台指令 /curse（all / off / 状态）。
            CurseCommand.Apply();

            // 全部用户可见文案（诅咒名 / 诅咒说明 / 诅咒池 / 本 mod 自述）——
            // 必须早于任何界面或关卡开始取词。登记完这些，
            // 游戏里的诅咒面板与悬停提示会自己跟着语言变（DSHCore 挂了查找链）。
            CurseLocale.Register();

            // 订阅 0.7 官方的外部 Mod 注册点（System.Func → Il2CppSystem.Func
            // 的委托转换由 DSHCore.ModInfos 完成）。
            ModInfos.OnRegister(OnRegisterModInfos);

            // 「模组设置」页里显示的**中文名**：源串是 NAME，译文挂在
            // DSHCore.Ui.UiLocale（= 游戏的语言系统，切到英文就显示英文原名）。
            UiLocale.Add(NAME, "模块化诅咒");
            UiLocale.Add("模块化诅咒（癫狂之触 + SC 池）",
                "Modular curses (Touch Of Insanity + SC)", UiLocale.English);
            UiOptions.DeclareMod(ModularCurseMod.spaceName, NAME,
                "模块化诅咒（癫狂之触 + SC 池）", "QoZnoS");

            Log.LogInfo(NAME + " v" + VERSION + " loaded, waiting for ModManager.RegisterModInfos...");
        }

        /// <summary>向游戏注册本 Mod 的 ModInfo。Logic 直接是注入实例。</summary>
        private static Il2CppSystem.Threading.Tasks.Task OnRegisterModInfos(
            IGlobalGame game, Il2CppSystem.Collections.Generic.List<ModInfo> results)
        {
            try
            {
                Log.LogInfo("OnRegisterModInfos called, adding ModInfo " + ModularCurseMod.spaceName);
                var logic = ModularCurseMod.CreateInstance();
                VTableRebind.Ensure(typeof(ModularCurseMod));

                // ModInfo 构造样板（空 locator / 存档版本 / 空 Assemblies）见
                // DSHCore.ModInfos。定义由 CurseDefinitionPatch 手工创建，
                // 不走程序集扫描。
                results.Add(ModInfos.Create(ModularCurseMod.spaceName, logic, "MVZ2 Modular Curse"));
                Log.LogInfo("OnRegisterModInfos: ModInfo added (" + ModularCurseMod.spaceName + ")");
            }
            catch (Exception e)
            {
                Log.LogError("OnRegisterModInfos error: " + e);
            }
            return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }
    }
}
