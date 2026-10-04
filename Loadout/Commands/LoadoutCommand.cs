using System;
using DSHCore.Ui;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2.Debugs;
using MVZ2Logic;
using MVZ2Logic.Commands;

namespace Loadout
{
    /// <summary>
    /// 排障用指令 <c>/loadout</c>（含 Tab 补全）。
    ///
    /// <para>为什么不用官方的 <c>[AutoCommandDefinition]</c> 注册一条新命令：那要注册 Definition
    /// 并走 XML metas，而本 mod 的原则是**零注入**；在 IL2CPP 下走那条路是纯属自找麻烦。
    /// 这里在 <c>DebugManager.ExecuteCommand</c> 上加前缀拦截 —— 命令名解析复用官方的
    /// <see cref="CommandUtility.SplitCommand"/>，所以行为与官方指令一致
    /// （同样只在控制台里、同样受 debug 权限限制）。</para>
    ///
    /// <para>Tab 补全（<see cref="FillSuggestionsPrefix"/>）必须遵守两条规则：</para>
    /// <list type="number">
    /// <item>它**必须 void**（只往列表里追加）—— 返回 false 会把官方补全全吞掉，而且不报错；</item>
    /// <item>建议项要返回**裸词**（不带 <c>/</c>、不带前缀），因为官方去拼 <c>'/' + 前缀 + 空格 + 建议</c>。</item>
    /// </list>
    /// </summary>
    internal static class LoadoutCommand
    {
        private const string CommandName = "loadout";

        private static readonly string[] SubCommands = { "open", "close", "dump", "report", "reload", "page", "archive", "hit" };

        private static bool applied;
        private static Harmony harmony;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            harmony = new Harmony(Plugin.GUID + ".command");
            try
            {
                var target = AccessTools.Method(typeof(DebugManager), "ExecuteCommand",
                    new[] { typeof(string), typeof(int) });
                if (target == null)
                {
                    Plugin.Log?.LogError("[Command] 找不到 DebugManager.ExecuteCommand");
                    return;
                }
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(LoadoutCommand), nameof(ExecuteCommandPrefix)));
                ApplySuggestionPatch(harmony);
                Plugin.Log?.LogInfo("[Command] 已挂 /" + CommandName + "（诊断用，含 Tab 补全）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Command] patch 失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void ApplySuggestionPatch(Harmony h)
        {
            try
            {
                var target = AccessTools.Method(typeof(DebugManager), "FillSuggestions",
                    new[] { typeof(Il2CppStringArray),
                            typeof(Il2CppSystem.Collections.Generic.List<string>) });
                if (target == null)
                {
                    Plugin.Log?.LogWarning("[Command] 找不到 DebugManager.FillSuggestions，Tab 补全不可用");
                    return;
                }
                h.Patch(target,
                    prefix: new HarmonyMethod(typeof(LoadoutCommand), nameof(FillSuggestionsPrefix)));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Command] 挂补全 prefix 失败：" + e.GetType().Name);
            }
        }

        /// <summary>
        /// ⚠️ 必须是 void：只追加，绝不能 <c>return false</c>
        /// （那会把官方补全全吞掉，而且不报错）。
        /// </summary>
        private static void FillSuggestionsPrefix(Il2CppStringArray parts,
            Il2CppSystem.Collections.Generic.List<string> currentSuggestions)
        {
            try
            {
                if (parts == null || parts.Length == 0 || currentSuggestions == null)
                    return;
                var first = parts[0];
                if (string.IsNullOrEmpty(first))
                    return;

                // 还没打过空格：补指令名（只有当已输入部分确实是我们的前缀时才给）。
                if (parts.Length == 1)
                {
                    if (!CommandName.StartsWith(first, StringComparison.OrdinalIgnoreCase))
                        return;
                    AddUnique(currentSuggestions, CommandName);
                    return;
                }

                if (!string.Equals(first, CommandName, StringComparison.OrdinalIgnoreCase))
                    return;

                // 第二条：补子指令。
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
                // 补全里绝不能抛（会穿回原生代码）。
            }
        }

        private static void AddUnique(Il2CppSystem.Collections.Generic.List<string> list, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            var count = list.Count;
            for (var i = 0; i < count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.Ordinal))
                    return;
            }
            list.Add(value);
        }

        /// <summary>返回 false = 是我们的指令，不再交给原版解析。</summary>
        private static bool ExecuteCommandPrefix(string input)
        {
            try
            {
                if (!IsOurCommand(input))
                    return true;
                PrintLine("> " + input.Trim());
                Handle(input);
                return false;
            }
            catch (Exception e)
            {
                PrintLine("[" + CommandName + "] 执行出错：" + e.GetType().Name + ": " + e.Message);
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
                ? parts[1].Trim().ToLowerInvariant()
                : string.Empty;

            switch (sub)
            {
                case "":
                    PrintLine("[" + CommandName + "] 面板：" + (LoadoutPanel.IsOpen ? "已打开" : "已关闭")
                        + "  当前页=" + LoadoutPanel.PageName);
                    PrintLine("用法：/" + CommandName + " open | close | page <序号|名字> | dump | report | archive | hit | reload");
                    PrintLine(PanelPages.Report());
                    return;

                case "open":
                    PrintLine(LoadoutPanel.Open()
                        ? "[" + CommandName + "] 面板已打开：" + LoadoutPanel.PageName
                        : "[" + CommandName + "] 打开失败：" + LoadoutPanel.LastError);
                    return;

                case "close":
                    LoadoutPanel.Close();
                    PrintLine("[" + CommandName + "] 面板已关闭");
                    return;

                case "page":
                    var arg = parts.Length > 2 ? parts[2] : null;
                    if (string.IsNullOrEmpty(arg))
                    {
                        PrintLine("[" + CommandName + "] " + PanelPages.Report());
                        return;
                    }
                    PrintLine(LoadoutPanel.ShowPageByName(arg)
                        ? "[" + CommandName + "] 已切到第 " + (LoadoutPanel.PageIndex + 1) + " 页：" + LoadoutPanel.PageName
                        : "[" + CommandName + "] 找不到该页：" + arg);
                    return;

                case "dump":
                    foreach (var line in PanelDump.DumpAll().Split('\n'))
                        PrintLine(line);
                    return;

                case "archive":
                    foreach (var line in ArchiveProbe.Dump().Split('\n'))
                        PrintLine(line);
                    return;

                case "hit":
                    foreach (var line in LoadoutPanel.RaycastUnderPointer(8).Split('\n'))
                        PrintLine(line);
                    return;

                case "report":
                    foreach (var line in PanelDump.Report().Split('\n'))
                        PrintLine(line);
                    return;

                case "reload":
                    NameIndexes.Invalidate();
                    LoadoutPanel.Rebuild();
                    PrintLine("[" + CommandName + "] 已强制重新解析 UI 素材并清空名称索引");
                    return;

                default:
                    PrintLine("[" + CommandName + "] 未知子指令：" + sub);
                    PrintLine("用法：/" + CommandName + " open | close | page <序号|名字> | dump | report | archive | reload");
                    return;
            }
        }

        private static void PrintLine(string text)
        {
            try
            {
                Global.Debugs?.Print(text + "\n");
            }
            catch (Exception)
            {
            }
        }
    }
}
