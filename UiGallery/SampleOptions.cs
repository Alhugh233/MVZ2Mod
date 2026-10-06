using System;
using System.Text;
using DSHCore.Ui;

namespace UiGallery
{
    /// <summary>
    /// **示例：三句话把设置项挂进「模组设置」页**（<see cref="UiOptions"/> 的**默认路径**）。
    ///
    /// <para>它声明三条设置项（复选框 / 滑条 / 多值），它们会出现在
    /// 「插件页 → 模组设置（DSHCore）」里 <c>uig</c> 名下 —— **不占用原版设置菜单**。</para>
    ///
    /// <para><b>这条路要写的东西只有这些</b>：一句声明 + 读写值的两个回调。
    /// 不需要 <c>ModInfo</c>、不需要注入定义类、不需要 <c>options.xml</c>、
    /// 不需要 <c>Attach</c> 的三时机 —— 这个 mod 因此仍然是**零注入**的。
    /// 对照的另一条路见 <see cref="SampleVanillaOptions"/>。</para>
    ///
    /// <para><b>值存哪</b>：本 mod 自己的 BepInEx 配置项
    /// （<see cref="GalleryConfig.SampleToggle"/> 等）—— 顺便示范「值的持久化完全由声明方决定」。</para>
    /// </summary>
    internal static class SampleOptions
    {
        /// <summary>本 mod 在设置页里的命名空间。</summary>
        public const string SpaceName = "uig";

        public const string ToggleId = "uig_sample_toggle";
        public const string SliderId = "uig_sample_slider";
        public const string ChoiceId = "uig_sample_choice";

        private const int OrderToggle = 100;
        private const int OrderSlider = 101;
        private const int OrderChoice = 102;

        private static readonly string[] ChoiceItems = { "低", "中", "高" };

        /// <summary>由插件 <c>Load()</c> 调用（纯托管，不碰游戏）。</summary>
        internal static void Enable()
        {
            try
            {
                // 走默认路径的 mod 不注册 ModInfo，所以自己要给“模组设置”页一个介绍。
                // 名字/说明/作者都是源串；末位参数是中文译文。
                UiOptions.DeclareMod(SpaceName, "MVZ2 UI Gallery",
                    "UI 组件画廊（开发者工具：用原版素材拼界面的最小范例）", "QoZnoS", "UI 组件画廊");

                UiOptions.DeclareToggle(
                    SpaceName, ToggleId,
                    category: "general",
                    label: "UI 画廊示例开关",
                    tooltip: "由 UiGallery 经 DSHCore.Ui.UiOptions 声明的设置项：这一行没有任何 UI 代码。",
                    order: OrderToggle,
                    getter: GetToggle,
                    setter: SetToggle);

                UiOptions.DeclareSlider(
                    SpaceName, SliderId,
                    category: "graphics",
                    label: "UI 画廊示例滑条",
                    min: 0f, max: 10f, wholeNumbers: true,
                    tooltip: "同一个封装出来的滑条：标签显示成「标签：值」，拖动时逐帧刷新，松手才提交。",
                    order: OrderSlider,
                    getter: GetSlider,
                    setter: SetSlider,
                    format: v => ((int)v) + " 格");

                UiOptions.DeclareChoice(
                    SpaceName, ChoiceId,
                    category: "gameplay",
                    label: "UI 画廊示例选择",
                    items: ChoiceItems,
                    tooltip: "多值项在模组设置页里是「标签：当前值」的按钮行，点一下切到下一个值"
                        + "（原版下拉框的展开列表在本环境画不出来，本库不提供替代实现）。",
                    order: OrderChoice,
                    getter: GetChoice,
                    setter: SetChoice);

                Plugin.Log?.LogInfo("[SampleOptions] 已声明 3 条设置项"
                    + "（它们会出现在「插件 → 模组设置（DSHCore）」的 uig 名下；零注入）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[SampleOptions] Enable error: " + e);
            }
        }

        // ---- 值的存取（这里用本 mod 的配置项；用游戏全局选项或静态字段也一样） ----

        private static bool GetToggle()
        {
            var entry = GalleryConfig.SampleToggle;
            return entry != null && entry.Value;
        }

        private static void SetToggle(bool value)
        {
            var entry = GalleryConfig.SampleToggle;
            if (entry != null)
                entry.Value = value;
        }

        private static float GetSlider()
        {
            var entry = GalleryConfig.SampleSlider;
            return entry == null ? 0f : entry.Value;
        }

        private static void SetSlider(float value)
        {
            var entry = GalleryConfig.SampleSlider;
            if (entry != null)
                entry.Value = (int)value;
        }

        private static int GetChoice()
        {
            var entry = GalleryConfig.SampleChoice;
            return entry == null ? 0 : entry.Value;
        }

        private static void SetChoice(int value)
        {
            var entry = GalleryConfig.SampleChoice;
            if (entry != null)
                entry.Value = value;
        }

        // ------------------------------------------------------------------
        // 验收
        // ------------------------------------------------------------------

        /// <summary>
        /// 「这三条会不会出现在模组设置页里」的可读结论（自测用）。
        ///
        /// <para>判据就是模组设置页自己的取数逻辑：<see cref="UiOptions.ForPage"/>
        /// 能拿到声明、且这条声明带 getter/setter（否则页面会把它渲染成只读行）。</para>
        /// </summary>
        internal static string Report()
        {
            var sb = new StringBuilder(600);
            try
            {
                if (!GalleryConfig.SampleOptions)
                {
                    sb.Append("未启用（General.SampleOptions = false）");
                    return sb.ToString();
                }

                var mine = UiOptions.ForPage(SpaceName);
                sb.Append("模组设置页（uig）取到的设置项：").Append(mine.Length).Append(" 条\n");
                for (var i = 0; i < mine.Length; i++)
                    sb.Append(CheckOne(mine[i]));
            }
            catch (Exception e)
            {
                sb.Append("报告失败：").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        private static string CheckOne(UiOptions.Declaration d)
        {
            var sb = new StringBuilder(200);
            try
            {
                sb.Append("  ").Append(d.FullID)
                  .Append(" [").Append(d.PageKind).Append(']')
                  .Append("  文案=「").Append(d.Text).Append('」')
                  .Append("  可编辑=").Append(d.CanEdit ? "是" : "**否**");

                switch (d.PageKind)
                {
                    case UiOptionKind.Slider:
                        sb.Append("  范围=").Append(d.SliderMin).Append('~').Append(d.SliderMax)
                          .Append("  当前值=").Append(d.CurrentValueText);
                        break;
                    case UiOptionKind.Button:
                        sb.Append("  选项=").Append(d.Items.Length)
                          .Append("  当前=").Append(d.CurrentItemText);
                        break;
                    default:
                        sb.Append("  当前值=").Append(d.CurrentBool ? "开" : "关");
                        break;
                }

                sb.Append(" → ").Append(d.CanEdit ? "**会出现在模组设置页**" : "**不会出现（只读）**");
                sb.Append('\n');
            }
            catch (Exception e)
            {
                sb.Append("：检查抛异常 ").Append(e.GetType().Name).Append('\n');
            }
            return sb.ToString();
        }
    }
}
