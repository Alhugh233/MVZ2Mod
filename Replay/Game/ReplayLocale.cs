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
                "排序：", "Sort: ",
                "时间↑", "Time↑",
                "时间↓", "Time↓",
                "时长↑", "Length↑",
                "时长↓", "Length↓",
                "关卡↑", "Level↑",
                "关卡↓", "Level↓",

                // 操作行
                "导入", "Import",
                "刷新", "Refresh",
                "清理回放", "Clean up",
                "更多", "More",
                "重命名", "Rename",
                "返回", "Back",
                "导出 .mrep", "Export .mrep",

                // 列表行
                "（读不出来）", " (unreadable)",
                "播放", "Play",

                // 清理
                "回放目录里没有回放。", "There are no replays.",
                "没有失效或未命名的回放可清理。", "Nothing to clean up: no unreadable or unnamed replays.",
                "要删除全部 {0} 条回放吗？", "Delete all {0} replays?",
                "全部删除", "Delete all",
                // 清理前的确认：必须说清**哪一类、几条**。
                // 「未命名」那一类动辄几十条，只说「清理」会让玩家以为只删几个坏文件。
                "将清理 {0}。", "About to remove {0}.",
                "已失效、读不出头的回放 {0} 条", "{0} unreadable replay(s)",
                "未命名的回放 {0} 条（改过名的一条都不会动）",
                "{0} unnamed replay(s) (renamed ones will be kept)",
                "清理", "Remove",
                "已清理 {0} 条失效回放。", "Removed {0} unreadable replay(s).",
                "已清理 {0} 条未命名回放（改过名的一条都没动）。",
                "Removed {0} unnamed replay(s) (renamed ones are untouched).",
                "已删除全部 {0} 条回放。", "Deleted all {0} replay(s).",
                "没有清理任何回放。", "Nothing was cleaned up.",

                // 详情
                "新名字：", "New name: ",
                "已重命名为：", "Renamed to: ",
                "已删除：", "Deleted: ",
                "重命名失败：", "Rename failed: ",
                "清理回放失败", "Clean up failed",
                "导入失败", "Import failed",
                "重命名失败", "Rename failed",
                "关卡：", "Level: ",
                "添加时间：", "Added: ",
                "录制时间：", "Recorded: ",
                "时长：", "Length: ",
                "大小：", "Size: ",
                "格式：", "Format: ",
                "规范 {0}／格式 {1}", "Spec {0} / format {1}",
                "格式 {0}", "Format {0}",
                "（调试文件）", " (debug file)",
                "录制环境：", "Recording environment: ",
                "问题：", "Problem: ",
                "上次播放：", "Last playback: ",
                "第 {0} tick 出现不一致，共 {1} 处", "mismatch at tick {0}, {1} in total",
                "与当前一致", "matches current",
                "与当前不同", "differs from current",
                "文件里没有环境记录", "no environment record in the file",

                // 风险弹窗（黄 / 红两种措辞不同）
                "可能不兼容的回放", "Possibly incompatible replay",
                "这条回放可能在当前环境下无法正确重演：",
                "This replay may not reproduce correctly in the current environment:",
                "回放可能无法正确播放", "This replay may not play correctly",
                "上次播放到第 {0} tick 时出现了 {1} 处不一致。",
                "Last time the playback diverged at tick {0} ({1} mismatch(es)).",
                "上次播放时出现了状态不一致。",
                "Last playback reported a state mismatch.",
                "回放只是按录制时的输入重演，无法修正录制时就已经发生的偏差。",
                "A replay only re-runs the recorded input; it cannot correct drift that already happened while recording.",
                "仍然播放", "Play anyway",
                "• 文件读不出来：", "• The file cannot be read: ",
                "• 录制时的内容与现在不同（关卡数据、mod 或插件）。",
                "• The recorded content differs from now (level data, mods or plugins).",
                "• 文件里没有环境记录（旧版本录的），无法判断能不能复现。",
                "• The file has no environment record (recorded by an older version), so reproducibility is unknown.",
                "• 不是本 mod 正式录出的文件（没有规范版本号）。",
                "• Not a file recorded by an official version of this mod (no spec version).",
                "• 录制于 ", "• Recorded on ",
                "，与当前游戏版本不同。", ", which differs from the current game version.",
                "• 这是调试模式录的文件。", "• This is a debug-mode file.",

                // 提示 / 确认框
                "无法播放", "Cannot play",
                "这个回放读不出来：", "This replay can't be read:",
                "打开文件对话框失败：", "Failed to open the file dialog:",
                "已导入到回放目录：", "Imported into the replay folder:",
                "导入失败：", "Import failed:",
                "未知原因。", "Unknown reason.",
                "删除回放", "Delete replay",
                "　时长 ", "  Length ",
                "删掉之后无法恢复，确定吗？", "This can't be undone. Continue?",
                "取消", "Cancel",
                "删除失败", "Delete failed",
                "删除", "Delete",
                "好", "OK",

                // 悬停提示
                "点击切换排序方式：时间↑ / 时间↓ / 时长↑ / 时长↓ / 关卡↑ / 关卡↓。",
                "Click to cycle: Time↑ / Time↓ / Length↑ / Length↓ / Level↑ / Level↓.",
                "先清理失效回放；若没有则清理未命名的回放（改过名的一条都不动）；两种都没有时再问你要不要全部删除。",
                "Removes unreadable replays; if none, unnamed replays (renamed ones are kept). If neither exists, asks whether to delete everything.",
                "查看详细信息，以及重命名 / 导出 / 删除。",
                "Show details, plus rename / export / delete.",
                "改名后的回放不计入数量上限，也不会被自动清理。",
                "A renamed replay does not count toward the replay limit and is never auto-cleaned.",

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
