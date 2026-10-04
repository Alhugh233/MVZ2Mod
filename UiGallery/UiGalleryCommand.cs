using System;
using DSHCore.Ui;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2.Debugs;
using MVZ2Logic;

namespace UiGallery
{
    /// <summary>
    /// 诊断指令 <c>/dshui</c>。
    ///
    /// <pre>
    /// /dshui                打开画廊（已打开则显示状态）
    /// /dshui close          关闭
    /// /dshui page &lt;名称&gt;   跳到某一页（名称可用 /dshui page 看列表）
    /// /dshui report         打印素材解析报告 + 模板架位置
    /// /dshui rebuild        强制重新解析 UI 素材（切场景后素材找不到时用）
    /// /dshui dd             展开下拉框并打印它的运行时状态（画布/尺寸/ Graphic）
    /// /dshui help           帮助
    /// </pre>
    ///
    /// <para>与本仓库另外几条自定义指令（DSHCore 的 <c>/dshlog</c>、GraphicConsole 的 <c>/gc</c>、
    /// ModularCurse 的 <c>/curse</c>）共存的关键约定：<b>不是自己的指令一律返回 true</b>。
    /// Harmony 会跑完所有 prefix，只有全部返回 true 才交给原方法。</para>
    /// </summary>
    internal static class UiGalleryCommand
    {
        private const string CommandName = "dshui";

        private static readonly string[] SubCommands = { "close", "page", "report", "rebuild", "dd", "help" };

        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;

            try
            {
                var target = AccessTools.Method(typeof(DebugManager), "ExecuteCommand",
                    new[] { typeof(string), typeof(int) });
                if (target == null)
                {
                    Plugin.Log?.LogWarning("[Command] 找不到 DebugManager.ExecuteCommand，/dshui 不可用。");
                    return;
                }

                var harmony = new Harmony(Plugin.GUID + ".command");
                harmony.Patch(target, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(UiGalleryCommand), nameof(ExecuteCommandPrefix))));

                ApplySuggestionPatch(harmony);

                Plugin.Log?.LogInfo("[Command] 游戏内指令已就绪：/" + CommandName + "（含 Tab 补全）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Command] 挂 /dshui 失败（不影响游戏）：" + e.Message);
            }
        }

        private static void ApplySuggestionPatch(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(DebugManager), "FillSuggestions",
                    new[] { typeof(Il2CppStringArray), typeof(Il2CppSystem.Collections.Generic.List<string>) });
                if (target == null)
                {
                    Plugin.Log?.LogWarning("[Command] 找不到 DebugManager.FillSuggestions，/dshui 没有 Tab 补全。");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(UiGalleryCommand), nameof(FillSuggestionsPrefix))));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Command] 挂 Tab 补全失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 往官方补全列表里追加裸词。
        /// ⚠️ 必须是 <b>void</b> prefix —— 写 <c>return false</c> 会把官方补全整个吞掉，
        /// 而且不报错（见 DSHCore/AI_GUIDE.md §5-⑰）。
        /// </summary>
        private static void FillSuggestionsPrefix(Il2CppStringArray parts,
            Il2CppSystem.Collections.Generic.List<string> currentSuggestions)
        {
            try
            {
                if (parts == null || parts.Length < 1 || currentSuggestions == null)
                    return;

                var name = parts[0];
                if (string.IsNullOrEmpty(name))
                    return;

                if (parts.Length == 1)
                {
                    if (CommandName.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                        AddUnique(currentSuggestions, CommandName);
                    return;
                }

                if (!string.Equals(name, CommandName, StringComparison.OrdinalIgnoreCase))
                    return;

                var last = parts[parts.Length - 1] ?? string.Empty;
                for (var i = 0; i < SubCommands.Length; i++)
                {
                    var sub = SubCommands[i];
                    if (last.Length == 0 || sub.StartsWith(last, StringComparison.OrdinalIgnoreCase))
                        AddUnique(currentSuggestions, sub);
                }
            }
            catch (Exception)
            {
                // 补全出错必须吞掉：抛出会打断整个输入框的刷新。
            }
        }

        private static void AddUnique(Il2CppSystem.Collections.Generic.List<string> list, string value)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.Ordinal))
                    return;
            }
            list.Add(value);
        }

        // ------------------------------------------------------------------
        // 执行
        // ------------------------------------------------------------------

        private static bool ExecuteCommandPrefix(string input)
        {
            try
            {
                var parts = Split(input);
                if (parts == null || parts.Length == 0)
                    return true;

                if (!string.Equals(parts[0], CommandName, StringComparison.OrdinalIgnoreCase))
                    return true;   // 不是自己的指令 → 必须让给别人

                Print(Execute(parts));
                return false;
            }
            catch (Exception e)
            {
                // prefix 里抛异常会中断整个控制台输入路径，必须自己兜住。
                try { Print("[UiGallery] 指令执行出错：" + e.Message); }
                catch (Exception) { }
                return false;
            }
        }

        internal static string Execute(string[] args)
        {
            if (args == null || args.Length == 0)
                return "[UiGallery] 未知参数。\n" + Help();

            if (args.Length <= 1)
            {
                if (GalleryPanel.IsOpen)
                    return "[UiGallery] 画廊已打开（" + GalleryPanel.CurrentPageName + "）。"
                        + "\n" + GalleryPanel.Report();
                return GalleryPanel.Open()
                    ? "[UiGallery] 已打开 UI 组件画廊。"
                    : "[UiGallery] 打开失败：\n" + GalleryPanel.Report();
            }

            var sub = (args[1] ?? string.Empty).Trim();
            switch (sub.ToLowerInvariant())
            {
                case "":
                case "open":
                case "on":
                    return GalleryPanel.Open()
                        ? "[UiGallery] 已打开 UI 组件画廊。"
                        : "[UiGallery] 打开失败：\n" + GalleryPanel.Report();

                case "close":
                case "off":
                case "hide":
                    GalleryPanel.Close();
                    return "[UiGallery] 已关闭。";

                case "toggle":
                    GalleryPanel.Toggle();
                    return "[UiGallery] 画廊当前" + (GalleryPanel.IsOpen ? "已打开" : "已关闭") + "。";

                case "report":
                    return "[UiGallery] 素材解析报告：\n" + GalleryPanel.Report();

                case "rebuild":
                case "reload":
                    GalleryPanel.Rebuild();
                    return "[UiGallery] 已强制重新解析素材。\n" + GalleryPanel.Report();

                case "page":
                    if (args.Length <= 2 || string.IsNullOrEmpty(args[2]))
                        return "[UiGallery] 可用页面：\n" + GalleryPages.List();

                    var index = GalleryPages.IndexOf(args[2]);
                    if (index < 0)
                        return "[UiGallery] 没有这一页：「" + args[2] + "」。\n" + GalleryPages.List();

                    if (!GalleryPanel.IsOpen)
                        GalleryPanel.Open();
                    GalleryPanel.ShowPage(index);
                    return "[UiGallery] 已切到：" + GalleryPages.All[index].Name;

                case "dd":
                case "dropdown":
                    return ProbeDropdown();

                default:
                    return "[UiGallery] 未知参数「" + sub + "」。\n" + Help();
            }
        }

        private static string Help()
        {
            return "[UiGallery] /" + CommandName + " —— 原版 UI 组件画廊（开发者用）\n"
                 + "  /" + CommandName + "            打开画廊\n"
                 + "  /" + CommandName + " close      关闭\n"
                 + "  /" + CommandName + " page <名>  跳到某一页（不带参数列出全部页）\n"
                 + "  /" + CommandName + " report     打印素材解析报告\n"
                 + "  /" + CommandName + " dd         展开下拉框并打印运行时状态\n"
                 + "  /" + CommandName + " rebuild    强制重新解析 UI 素材\n"
                 + "  /" + CommandName + " help       本帮助";
        }

        /// <summary>
        /// 下拉框探针：跳到那一页、展开列表、把运行时状态打出来。
        ///
        /// <para>列表是 <c>Show()</c> 时才被创建、<c>Hide()</c> 时被销毁的，所以这条指令做的是
        /// “展开并留下”，而不是在同一栈里展开后再问一遍（那时问得到东西，但用户看不到列表本身）。</para>
        /// </summary>
        private static string ProbeDropdown()
        {
            var ddIndex = GalleryPages.IndexOf("下拉框");
            if (ddIndex < 0)
                return "[UiGallery] 找不到「下拉框」那一页。";

            if (!GalleryPanel.IsOpen)
                GalleryPanel.Open();
            GalleryPanel.ShowPage(ddIndex);

            var dd = GalleryPanel.CurrentDropdown();
            if (dd == null)
                return "[UiGallery] 这一页上没有下拉框。\n" + GalleryPanel.Report();

            UiDropdown.Show(dd);
            return "[UiGallery] 下拉框运行时状态（已尝试展开；再跑一次可以看展开后的状态）：\n"
                + UiDropdown.Describe(dd)
                + UiDropdown.DumpScrollViews();
        }

        // ------------------------------------------------------------------
        // 工具
        // ------------------------------------------------------------------

        /// <summary>
        /// 与 <c>CommandUtility.SplitCommand</c> 同语义的极简版：
        /// 去掉首尾空白与前导 <c>/</c>，按空格 / Tab 切分并丢弃空段。
        /// 刻意不复用游戏那个（要多引一层依赖），语义已对齐（DSHCore 的 <c>/dshlog</c> 同样处理）。
        /// </summary>
        internal static string[] Split(string input)
        {
            if (string.IsNullOrEmpty(input))
                return null;

            var text = input.Trim();
            if (text.StartsWith("/", StringComparison.Ordinal))
                text = text.Substring(1);
            if (text.Length == 0)
                return null;

            return text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>把结果打到游戏内控制台（与 <c>/dshlog</c> 走同一条路）。</summary>
        internal static void Print(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            try
            {
                var debugs = Global.Debugs;
                if (debugs != null)
                {
                    debugs.Print(text + "\n");
                    return;
                }
            }
            catch (Exception)
            {
            }
            Plugin.Log?.LogInfo(text);
        }
    }
}
