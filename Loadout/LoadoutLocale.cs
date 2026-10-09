using DSHCore.Ui;

namespace Loadout
{
    /// <summary>
    /// 本 mod 的全部用户可见文案（<b>多语言的唯一真源</b>）。
    ///
    /// <para><b>这些文案是怎么生效的</b>：本 mod 画界面时只用 4 个入口 ——
    /// <c>UiKit.Label</c> / <c>UiOptionRow</c>（DSHCore，已内置取词）、
    /// <c>UiField</c>（本 mod，已内置）、<c>LoadoutPanel.AddTooltip/SetTooltip</c>（本 mod，已内置）。
    /// 所以这里登记完就生效，不用去几百个调用点逐个包一层
    /// —— 只有 <c>IPanelPage.Name</c> 那 5 个页签是在 <c>SyncPageSet</c> 里单独取的词。</para>
    ///
    /// <para><b>⚠ 页签的特殊之处</b>：<c>IPanelPage.Name</c> 同时是**查找键**
    /// （<c>/loadout page 基础</c> 与「切页集合时保持当前页」都按它匹配），
    /// 所以 <c>Name</c> 本身**保持中文不动**，只有画出来的那份过取词。</para>
    ///
    /// <para><b>不登记的东西</b>：只进日志/自测报告的句子（<c>SelfTest</c> / <c>PanelDump</c> /
    /// <c>Describe</c>…）；以及当键用的字符串 —— 注意 <c>"出怪点数"</c> 这类标签同时被用作
    /// 分支判据与 Unity 节点名（<c>"N_" + label</c>，见 <c>LevelBasicPage:316</c>），
    /// 所以登记它们只能改变**画出来的文字**，判据与节点名拿到的仍是原始常量。</para>
    ///
    /// <para>玩家想改/加语言：<c>BepInEx/config/DSHCore/lang/&lt;语言码&gt;.txt</c>。</para>
    /// </summary>
    internal static class LoadoutLocale
    {
        private static readonly UiLocaleTable T = UiLocale.Table("loadout");

        /// <summary>在 <c>Plugin.Load()</c> 里调一次（早于任何界面取词）。</summary>
        public static void Register()
        {
            Mod();
            Tabs();
            Buttons();
            Labels();
            Notes();
        }

        private static void Mod()
        {
            T.En("图形化控制台面板（调试工具，替代原版控制台输入窗）",
                "Graphical console panel (a debug tool that replaces the vanilla console input window)");
        }

        // ------------------------------------------------------------------
        // 页签（显示用；Name 本身保持中文，见类注释）
        // ------------------------------------------------------------------

        private static void Tabs()
        {
            T.EnAll(
                "基础", "Basic",
                "蓝图", "Blueprints",
                "制品", "Artifacts",
                "进度", "Progress");
        }

        private static void Buttons()
        {
            T.EnAll(
                "关闭", "Close",
                "确定", "OK",
                "确认", "Confirm",
                "取消", "Cancel",
                "选择", "Select",
                "重载", "Reload",
                "重载 *", "Reload *",
                "调整选卡", "Edit Loadout",
                "清空卡槽", "Clear Slots",
                "重置冷却", "Reset Cooldown",
                "冷却预设", "Cooldown Preset",
                "冷却预设：未定义", "Cooldown Preset: undefined");
        }

        // ------------------------------------------------------------------
        // 字段标签 / 开关标签
        // ------------------------------------------------------------------

        private static void Labels()
        {
            T.EnAll(
                // 基础页
                "上帝模式", "God Mode",
                "无限能量", "Infinite Energy",
                "无限星之碎片", "Infinite Starshards",
                "禁用冷却", "Disable Cooldowns",
                "后台暂停", "Pause in Background",
                "忽略大波事件", "Ignore Huge Waves",
                "自动收集", "Auto Collect",
                "隐藏HUD", "Hide HUD",
                "背景", "Background",
                "关卡", "Level",
                "难度", "Difficulty",
                "音乐", "Music",
                "旗数", "Flags",
                "波数", "Waves",
                "能量上限", "Max Energy",
                "星之碎片上限", "Max Starshards",
                "出怪点数", "Spawn Points",
                "出怪倍数", "Spawn Multiplier",
                "出怪幂数", "Spawn Exponent",

                // 蓝图页
                "费用", "Cost",
                "冷却(秒)", "Cooldown (s)",
                "血量", "Health",
                "模仿者", "Mimic",
                "逆阵营放置", "Reverse Faction",
                "白天不睡觉", "No Day Sleep",
                "无条件放置", "Unconditional Place",
                "传送带模式", "Conveyor Mode",

                // 传送带页
                "权重", "Weight",
                "数量限制", "Count Limit",
                "传送带速度", "Conveyor Speed",
                "当前概率", "Current Chance");
        }

        // ------------------------------------------------------------------
        // 逐字说明（多行的那几句）
        // ------------------------------------------------------------------

        private static void Notes()
        {
            T.EnAll(
                "本页只在关卡内可用（当前不在关卡内）。",
                "This page is only available inside a level (you are not in one right now).",
                "拿不到原版选卡面板的卡片素材（关卡 UI 还没起来？），镜像行不可用。",
                "Cannot get the vanilla seed-packet assets (is the level UI not up yet?), so the mirror row is unavailable.",
                "当前等级不写自定义存档：冷却备份只保留在本次游戏内存里。",
                "The current compatibility level does not write custom save data: cooldown backups only live in this session's memory.");
        }
    }
}
