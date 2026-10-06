using System;
using System.Text;
using DSHCore;
using DSHCore.Ui;
using HarmonyLib;
using MVZ2;
using MVZ2.Managers;
using MVZ2.Modding;
using MVZ2Logic;
using MVZ2Logic.Commands;
using MVZ2Logic.Modding;
using MVZ2Logic.Options;
using PVZEngine;

namespace UiGallery
{
    /// <summary>
    /// **示例：往原版设置界面里加两个设置项**（需要完整注入链的那条路）。
    ///
    /// <para>默认路径（<see cref="SampleOptions"/>）是「进 DSHCore 自己的模组设置页，零注入」；
    /// 本类是**另一条路**的活样本：声明时显式写 <c>scope: UiOptionScope.VanillaMenu</c>，
    /// 于是它会走一整套注入链 —— 三处调用解决的事：声明（<c>Plugin.Load</c>）→
    /// 登记（官方 <c>LoadStages</c> 时机）→ 写属性（<c>PostInit</c>）。
    /// 底层要做的却是一整套：手工创建注入定义、绕过 <c>Mod.GetDefinitions</c> 的原生 LINQ、
    /// 钉 GC 根、自己推导 7 个属性键，还要把属性写在正确的时机 —— 这些都在 DSHCore 里，
    /// 见 <see cref="InjectedDefinitions"/> 与 <see cref="UiOptions"/> 的类注释。</para>
    ///
    /// <para>只在 <see cref="GalleryConfig.SampleVanillaOptions"/> 打开时生效
    /// （默认关闭，而且刻意**不**被自测模式自动打开 —— 它会让 UiGallery 注册 ModInfo、
    /// 从而出现在 mod 列表里）。</para>
    ///
    /// <para><b>XML 仍然要一份</b>（<c>assets/Content/metas/options.xml</c>）：文案与分类在那里。
    /// 分类必须是**已存在**的分类 ID（<c>general</c> / <c>graphics</c> / …）；
    /// 写不存在的分类不会报错，但整组会从界面上消失。</para>
    /// </summary>
    internal static class SampleVanillaOptions
    {
        /// <summary>复选框的 id（与 <c>assets/Content/metas/options.xml</c> 一致）。</summary>
        public const string ToggleId = "uig_sample_toggle";

        /// <summary>滑条的 id（与 <c>assets/Content/metas/options.xml</c> 一致）。</summary>
        public const string SliderId = "uig_sample_slider";

        private static readonly NamespaceID ToggleKey = new NamespaceID(UiGalleryMod.spaceName, ToggleId);
        private static readonly NamespaceID SliderKey = new NamespaceID(UiGalleryMod.spaceName, SliderId);

        /// <summary>
        /// 打开整个示例功能：注册命名空间与注入类 → 声明两条设置项 → 挂登记补丁 → 订阅 ModInfo 注册点。
        /// 只在 <see cref="GalleryConfig.SampleVanillaOptions"/> 为真时调用。
        /// </summary>
        internal static void Enable()
        {
            try
            {
                // 登记命名空间 + 注册全部注入类（必须在任何实例化之前）。
                ModBootstrap.Register(
                    spaceName: UiGalleryMod.spaceName,
                    logicType: typeof(UiGalleryMod),
                    xmlDefaultNamespace: "mvz2");

                InjectedDefinitions.Apply();
                InjectedDefinitions.Register(UiGalleryMod.spaceName);

                Declare();
                ApplyPatch();

                ModInfos.OnRegister(OnRegisterModInfos);
                Plugin.Log?.LogInfo("[SampleVanillaOptions] 已启用（会往原版设置界面注入 2 条示例设置项，"
                    + "并因此在本 mod 列表里注册一个 ModInfo）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[SampleVanillaOptions] Enable error: " + e);
            }
        }

        /// <summary>
        /// 向游戏注册本 mod 的 ModInfo。
        ///
        /// <para>注意：这一步让 UiGallery **出现在 mod 列表里**（平时它不注册 ModInfo）。
        /// 这是示例功能的必要代价 —— 没有 Mod 实例就没有 <c>mod.AddDefinition</c>。</para>
        /// </summary>
        private static Il2CppSystem.Threading.Tasks.Task OnRegisterModInfos(
            MVZ2Logic.Games.IGlobalGame game,
            Il2CppSystem.Collections.Generic.List<ModInfo> results)
        {
            try
            {
                var logic = UiGalleryMod.CreateInstance();
                VTableRebind.Ensure(typeof(UiGalleryMod));
                results.Add(ModInfos.Create(UiGalleryMod.spaceName, logic, "MVZ2 UI Gallery (示例设置项)"));
                Plugin.Log?.LogInfo("[SampleVanillaOptions] ModInfo 已注册（" + UiGalleryMod.spaceName + "）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[SampleVanillaOptions] OnRegisterModInfos error: " + e);
            }
            return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>在 <c>Plugin.Load()</c> 调用（纯托管，不碰游戏）。</summary>
        internal static void Declare()
        {
            try
            {
                UiOptions.DeclareToggle(
                    UiGalleryMod.spaceName, ToggleId,
                    category: "general",
                    label: "UI 画廊示例开关",
                    tooltip: "由 UiGallery 经 DSHCore.Ui.UiOptions 注入的设置项：这一行没有任何 UI 代码。",
                    order: 100,
                    getter: GetToggle,
                    setter: SetToggle,
                    scope: UiOptionScope.VanillaMenu);

                UiOptions.DeclareSlider(
                    UiGalleryMod.spaceName, SliderId,
                    category: "graphics",
                    label: "UI 画廊示例滑条",
                    min: 0f, max: 10f, wholeNumbers: true,
                    tooltip: "同一个封装出来的滑条：标签显示成「标签：值」，拖动时逐帧刷新。",
                    order: 101,
                    getter: GetSlider,
                    setter: SetSlider,
                    format: v => ((int)v) + " 格",
                    scope: UiOptionScope.VanillaMenu);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[SampleVanillaOptions] Declare error: " + e);
            }
        }

        // ---- 值的存取：用游戏自己的全局选项（键落在本 mod 的命名空间里，不会和别人撞） ----

        private static bool GetToggle()
        {
            return Global.Options.GetOptionBool(ToggleKey);
        }

        private static void SetToggle(bool value)
        {
            Global.Options.SetOptionBool(ToggleKey, value);
            Global.Options.SaveOptions();
        }

        private static float GetSlider()
        {
            return Global.Options.GetOptionInt(SliderKey);
        }

        private static void SetSlider(float value)
        {
            Global.Options.SetOptionInt(SliderKey, (int)value);
            Global.Options.SaveOptions();
        }

        // ------------------------------------------------------------------
        // 登记（官方 LoadStages 时机）
        // ------------------------------------------------------------------

        /// <summary>
        /// 在官方 <c>ModLoader.LoadStages</c> 时机登记本 mod 声明的设置项。
        ///
        /// <para>为什么必须是这个时机：<c>Mod.AddDefinition</c> 要落在 <c>LoadDefinitions</c> 之内、
        /// <c>game.AddMod</c> 之前，否则定义进不了全局内容表。</para>
        /// </summary>
        internal static void ApplyPatch()
        {
            try
            {
                var target = AccessTools.Method(typeof(ModLoader), "LoadStages");
                if (target == null)
                {
                    Plugin.Log?.LogError("[SampleVanillaOptions] 找不到 ModLoader.LoadStages");
                    return;
                }
                new Harmony(Plugin.GUID + ".samplevanillaoptions").Patch(target,
                    prefix: new HarmonyMethod(typeof(SampleVanillaOptions), nameof(LoadStagesPrefix)));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[SampleVanillaOptions] 挂钩失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void LoadStagesPrefix(Mod mod)
        {
            try
            {
                // 判定归属必须用 NamespaceOf（先比 native 指针）：注入实例的 mod.Namespace
                // 字符串存在被 GC 回收的窗口，直接比字符串会让登记静默失败。
                if (InjectedDefinitions.NamespaceOf(mod) != UiGalleryMod.spaceName)
                    return;
                // 复核一次命名空间字段：登记之后游戏自己也要读它。
                ModNamespacePin.Ensure(mod, UiGalleryMod.spaceName);
                var n = UiOptions.Attach(mod);
                Plugin.Log?.LogInfo("[SampleVanillaOptions] LoadStages：已登记 " + n + " 条设置项");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[SampleVanillaOptions] Attach error: " + e);
            }
        }

        // ------------------------------------------------------------------
        // 验收（复刻官方渲染判据，见 DSHCore/UI_RESEARCH.md §8）
        // ------------------------------------------------------------------

        /// <summary>
        /// 把"这两条设置项到底会不会出现在设置界面里"变成一个可读结论。
        ///
        /// <para>判据就是**官方渲染代码自己的判据**
        /// （<c>OptionsDialogController.UpdateMoreOptionsPageWidgets</c>）：
        /// 定义在官方表里 → <c>GetCategoryID()</c> 有效 → 该分类的 meta 存在
        /// （否则整组静默消失）→ 类型模式匹配 → <c>GetValue()</c> 可读。
        /// 全部满足就一定会被画出来，不需要人肉去点设置界面。</para>
        /// </summary>
        internal static string Report()
        {
            var sb = new StringBuilder(800);
            try
            {
                if (!GalleryConfig.SampleVanillaOptions)
                {
                    sb.Append("未启用（General.SampleVanillaOptions = false）");
                    return sb.ToString();
                }

                var main = MainManager.Instance;
                if (main == null)
                {
                    sb.Append("MainManager 未就绪");
                    return sb.ToString();
                }

                sb.Append(UiOptions.Report());
                sb.Append(CheckOne("Toggle", ToggleId));
                sb.Append(CheckOne("Slider", SliderId));
            }
            catch (Exception e)
            {
                sb.Append("报告失败：").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        private static string CheckOne(string kind, string id)
        {
            var full = UiGalleryMod.spaceName + ":" + id;
            var sb = new StringBuilder(220);
            sb.Append("  ").Append(kind).Append(' ').Append(full);

            try
            {
                var main = MainManager.Instance;
                var game = main.Game;
                var res = main.ResourceManager;
                var nid = new NamespaceID(UiGalleryMod.spaceName, id);

                var def = game.GetDefinition<OptionWidgetDefinition>(
                    MVZ2Logic.Definitions.LogicDefinitionTypes.OPTION_WIDGET, nid);
                if (def == null)
                {
                    sb.Append("：**定义不在官方表里**（登记失败）\n");
                    return sb.ToString();
                }

                var cat = def.GetCategoryID();
                var catMeta = cat == null ? null : res.GetOptionCategoryMeta(cat);
                var widgetMeta = res.GetOptionWidgetMeta(nid);

                // 类型模式匹配：官方 GetWidgetViewData 是 switch(definition) { case OptionToggleDefinition: ... }，
                // 匹配不上就会被渲染成"未知控件"，所以这一条也要查。
                var typed = def is OptionToggleDefinition || def is OptionSliderDefinition;

                sb.Append("  category=").Append(cat == null
                    ? "**null（会被官方过滤掉）**" : cat.SpaceName + ":" + cat.Path);
                sb.Append(" 分类meta=").Append(catMeta == null ? "**缺失 → 整组消失**" : "有");
                sb.Append(" 元数据=").Append(widgetMeta == null ? "无" : "有");
                sb.Append(" label='").Append(widgetMeta == null ? "-" : widgetMeta.Label).Append('\'');
                sb.Append(" 类型匹配=").Append(typed ? "是" : "**否**");
                sb.Append(" → ").Append(cat != null && catMeta != null && typed
                    ? "**会出现在设置界面**" : "**不会出现**");
                sb.Append('\n');
            }
            catch (Exception e)
            {
                sb.Append("：检查抛异常 ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n');
            }
            return sb.ToString();
        }
    }
}
