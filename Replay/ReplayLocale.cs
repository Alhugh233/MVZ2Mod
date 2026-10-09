using DSHCore.Ui;

namespace Replay
{
    /// <summary>
    /// 本 mod 的全部用户可见文案（<b>多语言的唯一真源</b>）。
    ///
    /// <para>这里登记的都是**界面文字**（不带上下文）—— 本 mod 画的按钮、对话框、HUD、
    /// 以及改写原版关卡菜单那两个按钮的文案。它们由 DSHCore 的界面辅助函数
    /// （<c>UiKit.Label</c> / <c>UiKit.TextButton</c> / <c>UiOptionRow</c>）在画的时候
    /// 自动取词；走游戏自己的 <c>CustomDialog</c>（我们的 Confirm/ShowNotice）与
    /// 原版按钮改写的那几处，在调用点显式取词。</para>
    ///
    /// <para><b>不登记的东西</b>（两类，都别翻）：</para>
    /// <list type="bullet">
    /// <item>只写进 BepInEx 日志 / 自测报告的句子 —— 它们不是界面，跟着语言变只会让
    /// 排查变难（<c>ReplaySelfTest</c> / <c>ReplayPlayer.Describe</c> / <c>ReplayTreeDump</c>…）。</item>
    /// <item>当键用的字符串 —— Unity 节点名（<c>DSH_ReplayPanel</c>…）、插件页 Id（<c>Replay</c>）、
    /// <c>.mrep</c> 的 JSON 字段名、<c>switch (reason)</c> 的 <c>"level-cleared"</c> 这类取值。
    /// 翻了它们会静默坏掉（节点找不到 / 解析失败），而日志里看不出原因。</item>
    /// </list>
    ///
    /// <para>玩家想改/加语言：<c>BepInEx/config/DSHCore/lang/&lt;语言码&gt;.txt</c>
    /// （<c>/dshtrans write &lt;语言码&gt;</c> 可导出模板）。</para>
    /// </summary>
    internal static class ReplayLocale
    {
        private static readonly UiLocaleTable T = UiLocale.Table("replay");

        /// <summary>在 <c>Plugin.Load()</c> 里调一次（早于任何界面取词）。</summary>
        public static void Register()
        {
            Mod();
            Page();
            EndPanel();
            Patches();
            Library();
        }

        private static void Mod()
        {
            T.En("关卡回放录制 / 播放（.mrep）",
                "Level replay recording / playback (.mrep)");
        }

        // ------------------------------------------------------------------
        // 插件页里的「回放」页（入口按钮 / 标题 / 列表 / 提示框）
        // ------------------------------------------------------------------

        private static void Page()
        {
            T.EnAll(
                // 入口按钮 + 页面标题
                "回放", "Replay",

                // 数量行 + 排序按钮
                "共 {0} 个", "{0} replays",
                "按时间", "By time",
                "按关卡", "By level",
                "按时长", "By length",

                // 列表行
                "（读不出来）", " (unreadable)",
                "播放", "Play",
                "删除", "Delete",

                // 提示 / 确认框
                "无法播放", "Cannot play",
                "这个回放读不出来：", "This replay can't be read:",
                "导入", "Import",
                "打开文件对话框失败：", "Failed to open the file dialog:",
                "已导入到回放目录：", "Imported into the replay folder:",
                "导入失败：", "Import failed:",
                "未知原因。", "Unknown reason.",
                "删除回放", "Delete replay",
                "　时长 ", "  Length ",
                "删掉之后无法恢复，确定吗？", "This can't be undone. Continue?",
                "取消", "Cancel",
                "删除失败", "Delete failed",
                "好", "OK",

                // 面板建不出来时的兜底
                "面板素材还没就绪。", "Panel assets are not ready yet.");
        }

        // ------------------------------------------------------------------
        // 关卡内的「回放结束」面板
        // ------------------------------------------------------------------

        private static void EndPanel()
        {
            T.EnAll(
                "回放结束", "Replay finished",
                "导出 .mrep", "Export .mrep",
                "重新播放", "Play again",
                "退出", "Exit",
                "没有可导出的文件。", "There is nothing to export.",
                "已取消导出。", "Export cancelled.",
                "已导出到：", "Exported to: ",
                "导出失败：", "Export failed: ",
                "结果：", "Result: ",
                "　注入 ", "  Injected ",
                "　一致性 ", "  Consistency ",
                "（不一致 {0}）❌", " ({0} mismatches)❌",

                // 结束原因（画在面板正文里）
                "播放完毕", "Playback finished",
                "关卡通关", "Level cleared",
                "关卡失败", "Level failed",
                "关卡已关闭", "Level closed",
                "已结束播放", "Playback ended",
                "到达调试 tick 上限", "Debug tick limit reached",
                "自测：播放停滞，提前收尾", "Self-test: playback stalled, wrapping up",
                "自测：等待超时", "Self-test: timed out",
                "游戏退出", "Game exited",
                "结束", "Finished");
        }

        // ------------------------------------------------------------------
        // 改写原版关卡菜单（播放中那两个按钮 + 它们的确认框）
        // ------------------------------------------------------------------

        private static void Patches()
        {
            T.EnAll(
                "重新播放", "Play again",
                "结束播放", "End playback",
                "确认要重新播放吗？\n当前进度会从头开始。",
                "Play this replay again?\nCurrent progress will restart from the beginning.",
                "确认要结束播放吗？\n结束后会弹出回放结束面板，可以导出、重播或退出。",
                "End playback?\nThe replay end panel will appear, where you can export, replay or exit.");
        }

        // ------------------------------------------------------------------
        // 列表里那几个占位文本
        // ------------------------------------------------------------------

        private static void Library()
        {
            T.EnAll(
                "时间未知", "Unknown time",
                "（未知关卡）", "(Unknown level)");
        }
    }
}
