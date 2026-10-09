using System;
using System.Text;
using DSHCore.Ui;

namespace Loadout
{
    /// <summary>
    /// 把**兼容性等级**做成一行设置项，出现在 **「插件 → 模组设置（DSHCore）」** 里
    /// （本 mod 名下）。
    ///
    /// <para><b>为什么不再是原版设置界面里的那一行：</b>那需要注入一个真的 <c>ModInfo</c> +
    /// 注入定义类 + 一份 <c>options.xml</c> + 严格的三时机，而这一项本来就是本 mod 自己的东西。
    /// 现在 <see cref="UiOptions"/> 默认把它注册到本库自己的模组设置页 ——
    /// **零注入、不需要 Mod 实例、不需要 XML**：本 mod 从此是真·零注入
    /// （不注册 il2cpp 类型、不注册 Definition、不注册 ModInfo）。</para>
    ///
    /// <para><b>值存哪</b>：就是原来那个 BepInEx 配置项 <c>Compat.CompatLevel</c> ——
    /// 界面与配置文件是**同一份数据**，谁改都立刻生效，也不用迁移老用户的值。
    /// 所以这里只提供 <c>getter</c>/<c>setter</c> 两个回调。</para>
    ///
    /// <para><b>为什么用「选择」而不是滑条</b>：三个值是有名字的枚举
    /// （Safe / Standard / Full），滑条表达不了名字。</para>
    ///
    /// <para><b>⚠ 渲染形态</b>：在模组设置页里多值项是**按钮行**（「标签：当前值」，点一下切到下一个值），
    /// 不是下拉框 —— 原版 <c>TMP_Dropdown</c> 的展开列表在本环境一个像素都画不出来，
    /// DSHCore 不提供它的替代实现（见 <c>DSHCore/UI_GUIDE.md</c> §9）。</para>
    /// </summary>
    internal static class CompatOptions
    {
        /// <summary>本 mod 的命名空间（设置项 id 就是 <c>loadout:compat_level</c>）。</summary>
        public const string SpaceName = "loadout";

        /// <summary>设置项 id。</summary>
        public const string ChoiceId = "compat_level";

        /// <summary>排序权重（同一 mod 内的顺序）。</summary>
        private const int Order = 900;

        /// <summary>由插件 <c>Load()</c> 调用：声明这一行（纯托管，不碰游戏）。</summary>
        public static void Enable()
        {
            try
            {
                Declare();
                Plugin.Log?.LogInfo("[CompatOptions] 已启用（兼容性等级会出现在「插件 → 模组设置（DSHCore）」里）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CompatOptions] Enable error: " + e);
            }
        }

        /// <summary>纯托管：把这一行声明出来。</summary>
        private static void Declare()
        {
            // 本 mod 不再注册 ModInfo（零注入），所以自己要给“模组设置”页一个介绍 ——
            // 否则列表里只会显示裸命名空间 loadout。
            // 名字/说明/作者都是**可翻译的源串**（第三个之后的参数里给的是译文）。
            UiOptions.DeclareMod(SpaceName, "MVZ2 Loadout",
                "图形化控制台面板（调试工具，替代原版控制台输入窗）", "QoZnoS", "图形化控制台");
            UiOptions.DeclareChoice(
                SpaceName, ChoiceId,
                category: "gameplay",      // 只有「同时进原版设置界面」时才用得到
                label: "兼容性等级",
                items: LevelNames(),
                tooltip: "本 mod 会接管多少原版行为。等级不够的功能在面板里是**置灰**的，调上去立刻生效，不用重启。\n"
                    + "Safe     = 只用原版已有的属性与接口，不挂任何会改行为的补丁。\n"
                    + "Standard = 默认。额外接管「面板打开时游戏速度=0」「选卡显示所有蓝图」「蜘蛛图标作为入口」"
                    + "，以及蓝图页的「血量 / 白天不睡觉」。\n"
                    + "Full     = 全部功能，含需要替换官方行为的危险项（传送带卡池接管 / 逆阵营放置 / 无条件放置）。\n"
                    + "点一下这一行就切到下一个等级。",
                order: Order,
                getter: () => CompatConfig.LevelIndex,
                setter: CompatConfig.SetLevelIndex);
        }

        /// <summary>三个等级的名字（托管数组 —— 不需要构造 il2cpp 的 List）。</summary>
        private static string[] LevelNames()
        {
            var levels = CompatConfig.Levels;
            var names = new string[levels.Length];
            for (var i = 0; i < levels.Length; i++)
                names[i] = CompatConfig.LevelName(levels[i]);
            return names;
        }

        // ------------------------------------------------------------------
        // 验收
        // ------------------------------------------------------------------

        /// <summary>
        /// 「这一行会不会出现在模组设置页里」的可读结论（自测用）。
        ///
        /// <para>判据是模组设置页自己的取数逻辑：<see cref="UiOptions.ForPage"/>
        /// 能拿到这条声明、且它带 getter/setter（否则页面会渲染成只读行）。</para>
        /// </summary>
        public static string Report()
        {
            var sb = new StringBuilder(300);
            try
            {
                sb.Append("兼容性等级（").Append(SpaceName).Append(':').Append(ChoiceId).Append("）：\n");

                var mine = UiOptions.ForPage(SpaceName);
                sb.Append("  模组设置页里的行数 = ").Append(mine.Length).Append('\n');

                UiOptions.Declaration found = null;
                for (var i = 0; i < mine.Length; i++)
                {
                    if (mine[i].Name == ChoiceId)
                        found = mine[i];
                }
                if (found == null)
                {
                    sb.Append("  ❌ **这一条不在模组设置页的取数结果里**（声明没生效？）");
                    return sb.ToString();
                }

                sb.Append("  行类型 = ").Append(found.PageKind)
                  .Append("  可编辑 = ").Append(found.CanEdit ? "是" : "**否（会渲染成只读行）**").Append('\n');
                var items = found.Items;
                sb.Append("  选项 = ").Append(items.Length).Append('[');
                for (var i = 0; i < items.Length; i++)
                {
                    if (i > 0)
                        sb.Append(" / ");
                    sb.Append(items[i]);
                }
                sb.Append("]\n");
                sb.Append("  当前值 = ").Append(found.CurrentItemText)
                  .Append("（下标 ").Append(found.CurrentIndex).Append("）\n");
                sb.Append("  判据 会出现在模组设置页 = ")
                  .Append(found.CanEdit && items.Length > 0 ? "OK" : "NG");
            }
            catch (Exception e)
            {
                sb.Append("（报告失败 ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append("）");
            }
            return sb.ToString();
        }
    }
}
