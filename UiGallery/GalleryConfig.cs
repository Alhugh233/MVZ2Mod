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
        /// true = 往原版设置界面注入两个示例设置项（<b>本 mod 唯一会动到游戏内容的东西</b>）。
        ///
        /// <para>为什么单独一个开关：本 mod 其余部分都是「只读展示」——不注册 ModInfo、
        /// 不注册 Definition、零注入（见 README「这个 mod 自己遵守的规则」）。
        /// 而“mod 能不能给原版设置界面加一项”这件事**必须有个 mod 真的挂个 Mod 上去**才能验证，
        /// 所以就放在这个 dev 工具里，并且默认关闭。</para>
        ///
        /// <para><see cref="SelfTest"/> 为 true 时它会被强制打开（否则自测就验不到这条路径）。</para>
        /// </summary>
        public static bool SampleOptions { get; private set; }

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
                    "开发者示例：往原版设置界面注入两个由本 mod 提供的设置项（演示 DSHCore.Ui.UiOptions）。"
                    + "开启后本 mod 会注册一个 ModInfo（会出现在 mod 列表里）——这是它唯一会动游戏内容的地方，"
                    + "所以默认关闭。自测模式会自动打开它。");
                SampleOptions = sample.Value || SelfTest;
            }
            catch
            {
                SelfTest = false;
                SampleOptions = false;
            }
        }
    }
}
