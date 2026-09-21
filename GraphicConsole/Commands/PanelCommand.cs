using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2.Debugs;
using MVZ2Logic;
using MVZ2Logic.Commands;

namespace GraphicConsole
{
    /// <summary>
    /// 诊断指令 <c>/gc</c>。
    ///
    ///   /gc            查看状态
    ///   /gc dump       打印素材解析结果与面板构建报告（排障用）
    ///   /gc open       打开面板
    ///   /gc close      关闭面板
    ///   /gc reload     强制重新解析 UI 素材（切场景后素材找不到时用）
    ///
    /// 与仓库里另外两条自定义指令（DSHCore 的 <c>/dshlog</c>、ModularCurse 的 <c>/curse</c>）
    /// 共存的关键约定：**不是自己的指令一律返回 true**。
    /// Harmony 会跑完所有 prefix，只有全部返回 true 才交给原方法 —— 所以只要各自
    /// 认领自己的名字，三方同时存在也不会互相吞指令。
    ///
    /// Tab 补全（见 <see cref="FillSuggestionsPrefix"/>）必须遵守两条规则：
    ///   ① 补丁是 <b>void prefix</b>（只往列表里追加，不拦截、不返回 bool）；
    ///   ② 往里放的是 <b>裸词</b>（不带开头的 '/'）—— 官方的自动补全自己会拼 '/'。
    /// 还有一点：官方在 <c>parts.Length == 1</c> 时只补“指令名”、否则只补“参数名”，
    /// 而 <c>gc</c> 没有注册过 CommandDefinition（本 mod 不注册任何 Definition），
    /// 所以参数那一文只能我们补。
    /// </summary>
    internal static class PanelCommand
    {
        private const string CommandName = "gc";

        /// <summary>子指令（裸词）。</summary>
        private static readonly string[] SubCommands = { "dump", "open", "close", "reload" };

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
                    Plugin.Log?.LogError("[Command] 找不到 DebugManager.ExecuteCommand，/gc 不可用");
                    return;
                }
                var harmony = new Harmony(Plugin.GUID + ".command");
                harmony.Patch(target, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(PanelCommand), nameof(ExecuteCommandPrefix))));

                ApplySuggestionPatch(harmony);
                Plugin.Log?.LogInfo("[Command] 已挂 /gc（诊断用，含 Tab 补全）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Command] 挂 /gc 失败：" + e);
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
                    Plugin.Log?.LogWarning("[Command] 找不到 DebugManager.FillSuggestions，/gc 没有 Tab 补全");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(PanelCommand), nameof(FillSuggestionsPrefix))));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Command] 挂 Tab 补全失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 往官方的补全列表里追加我们的裸词。
        /// ⚠️ 这是 <b>void</b> prefix：官方代码会照常跑完，我们只负责“多几个候选”。
        /// 所以千万不要在这里写 <c>return false</c>（那会把官方的补全全吞掉）。
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

                // 还没打过空格：补指令名（只有当已输入部分确实是 "gc" 的前缀时才给）
                if (parts.Length == 1)
                {
                    if (CommandName.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                        AddUnique(currentSuggestions, CommandName);
                    return;
                }

                if (!string.Equals(name, CommandName, StringComparison.OrdinalIgnoreCase))
                    return;

                // 已输入 "gc " 或 "gc d"：补子指令
                var last = parts[parts.Length - 1] ?? string.Empty;
                for (var i = 0; i < SubCommands.Length; i++)
                {
                    var sub = SubCommands[i];
                    if (last.Length == 0 || sub.StartsWith(last, StringComparison.OrdinalIgnoreCase))
                        AddUnique(currentSuggestions, sub);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Command] 补全异常：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void AddUnique(Il2CppSystem.Collections.Generic.List<string> list, string value)
        {
            try
            {
                if (list.Contains(value))
                    return;
                list.Add(value);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>返回 false = 这是我们的指令，不再交给原版解析。</summary>
        private static bool ExecuteCommandPrefix(string input)
        {
            try
            {
                if (!IsOurCommand(input))
                    return true;
                Plugin.Log?.LogInfo("[Command] 收到：" + input.Trim());
                PrintLine("> " + input.Trim());
                Handle(input);
                return false;
            }
            catch (Exception e)
            {
                PrintLine("[gc] 执行出错：" + e.GetType().Name + ": " + e.Message);
                Plugin.Log?.LogError("[Command] " + e);
                return false;
            }
        }

        private static bool IsOurCommand(string input)
        {
            if (string.IsNullOrEmpty(input))
                return false;
            var text = input.Trim();
            if (!text.StartsWith("/", StringComparison.Ordinal))
                return false;
            var parts = CommandUtility.SplitCommand(text);
            if (parts == null || parts.Length == 0)
                return false;
            return string.Equals(parts[0], CommandName, StringComparison.OrdinalIgnoreCase);
        }

        private static void Handle(string input)
        {
            var parts = CommandUtility.SplitCommand(input.Trim());
            var sub = parts != null && parts.Length > 1 && parts[1] != null
                ? parts[1].ToLowerInvariant()
                : string.Empty;
            switch (sub)
            {
                case "dump":
                    PrintLine("[gc] 场景素材解析：");
                    PrintLine("     " + UiSources.LastReport);
                    PrintLine("[gc] 面板状态：");
                    PrintLine("     " + ConsolePanel.Describe().Replace("\n", "\n     "));
                    PanelLayoutDump.DumpTemplateSources();
                    if (ConsolePanel.IsOpen)
                    {
                        PanelLayoutDump.Dump(ConsolePanel.RootObject, "/gc dump");
                        PanelLayoutDump.ProbeDropdown(ConsolePanel.RootObject, "/gc dump");
                        PrintLine("[gc] 已把面板布局与下拉框展开 dump 到 BepInEx 日志。");
                    }
                    else
                    {
                        PrintLine("[gc] 面板未打开，运行时布局请先打开面板再执行 /gc dump。");
                    }
                    break;

                case "open":
                    ConsolePanel.Open();
                    PrintLine("[gc] 已请求打开面板（不可用时看日志）。");
                    break;

                case "close":
                    ConsolePanel.Close();
                    PrintLine("[gc] 已请求关闭面板。");
                    break;

                case "reload":
                    UiSources.Prepare(true);
                    PrintLine("[gc] 已重新解析 UI 素材：");
                    PrintLine("     " + UiSources.LastReport);
                    break;

                default:
                    PrintLine("[gc] 图形化控制台（" + Plugin.VERSION + "）");
                    PrintLine("     /gc          查看状态");
                    PrintLine("     /gc dump     打印素材解析与面板构建报告");
                    PrintLine("     /gc open     打开面板");
                    PrintLine("     /gc close    关闭面板");
                    PrintLine("     /gc reload   强制重新解析 UI 素材");
                    PrintLine("     面板热键：" + CompatConfig.PanelHotkeyText
                        + "（可在 BepInEx/config/" + Plugin.GUID + ".cfg 改）");
                    PrintLine("     原版控制台仍然是 '/'。");
                    break;
            }
        }

        private static void PrintLine(string text)
        {
            try
            {
                var debugs = Global.Debugs;
                if (debugs != null)
                    debugs.Print(text + "\n");
            }
            catch (Exception)
            {
            }
        }
    }
}
