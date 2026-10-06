using BepInEx.Configuration;

namespace UiGallery
{
    /// <summary>
    /// 本 mod 的配置。
    ///
    /// <para>只有一项：<see cref="SelfTest"/>。它的存在是因为**自动化验证没法在游戏里打字** ——
    /// 打开自测后，游戏进主菜单时会自动把画廊建起来、把素材解析报告与布局摘要打进日志，
    /// 于是"UI 基础设施到底能不能在这台机器上工作"就变成了一个可 grep 的结论。
    /// 这也是 <c>GraphicConsole</c> 已经验证过的做法（见其 <c>Diagnostics/</c>）。</para>
    /// </summary>
    internal static class GalleryConfig
    {
        /// <summary>true = 进主菜单后自动打开画廊并 dump 报告（排障 / CI 式冒烟用）。</summary>
        public static bool SelfTest { get; private set; }

        /// <summary>
        /// true = 声明三条**模组设置页**样例（<b>推荐路径：零注入</b>）。
        ///
        /// <para>它们只会出现在「插件页 → 模组设置（DSHCore）」里，不会碰原版设置界面 ——
        /// 用 <see cref="DSHCore.Ui.UiOptions.DeclareToggle"/> 等三句话就能挂一条，
        /// 不需要 ModInfo、不需要 Definition、不需要 XML。</para>
        ///
        /// <para><see cref="SelfTest"/> 为 true 时它会被强制打开（否则自测就验不到这条路径）。</para>
        /// </summary>
        public static bool SampleOptions { get; private set; }

        /// <summary>
        /// true = 把两条设置项**放进原版设置界面**（另一条路：需要完整注入链）。
        ///
        /// <para>它会注册一个真的 <c>ModInfo</c>（于是本 mod 会出现在 mod 列表里）、
        /// 注入两个定义类，并依赖 <c>assets/Content/metas/options.xml</c>。
        /// 默认关闭，而且**不**被自测模式自动打开 —— 自测不该顺手把 mod 列表改掉。</para>
        /// </summary>
        public static bool SampleVanillaOptions { get; private set; }

        // ---- 模组设置页样例的**值**：存在本 mod 自己的配置文件里 ----
        //
        // 这一段本身就是示范：值怎么存完全由声明方决定。用游戏自己的全局选项也行
        // （Global.Options.SetOptionBool(自己的 NamespaceID, v)），用 BepInEx 配置也行。

        /// <summary>样例复选框的值。</summary>
        public static ConfigEntry<bool> SampleToggle;

        /// <summary>样例滑条的值。</summary>
        public static ConfigEntry<int> SampleSlider;

        /// <summary>样例多值项（选项下标）的值。</summary>
        public static ConfigEntry<int> SampleChoice;

        public static void Init(ConfigFile config)
        {
            if (config == null)
                return;

            try
            {
                var section = config.Bind("General", "SelfTest", false,
                    "开发者自测：进主菜单后自动打开 UI 组件画廊，把素材解析报告与"
                    + "面板布局摘要打进 BepInEx 日志（以 [UiGallery] 开头）。"
                    + "正常玩的时候保持 false。");
                SelfTest = section.Value;

                var sample = config.Bind("General", "SampleOptions", false,
                    "开发者示例：声明三条设置项，它们会出现在「插件页 → 模组设置（DSHCore）」里"
                    + "（演示 DSHCore.Ui.UiOptions 的默认路径：零注入）。自测模式会自动打开它。");
                SampleOptions = sample.Value || SelfTest;

                var vanilla = config.Bind("General", "SampleVanillaOptions", false,
                    "开发者示例（另一条路）：把两条设置项放进**原版设置界面**。"
                    + "需要完整注入链，会让本 mod 注册一个 ModInfo（出现在 mod 列表里），所以默认关闭。");
                SampleVanillaOptions = vanilla.Value;

                SampleToggle = config.Bind("SampleValues", "Toggle", true,
                    "模组设置页样例复选框的当前值（由设置界面写入）。");
                SampleSlider = config.Bind("SampleValues", "Slider", 3,
                    "模组设置页样例滑条的当前值（由设置界面写入）。");
                SampleChoice = config.Bind("SampleValues", "Choice", 1,
                    "模组设置页样例多值项的当前选项下标（由设置界面写入）。");
            }
            catch
            {
                SelfTest = false;
                SampleOptions = false;
                SampleVanillaOptions = false;
            }
        }
    }
}
