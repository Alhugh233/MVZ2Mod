using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2.Debugs;
using MVZ2Logic;

namespace DSHCore.Diagnostics
{
    /// <summary>
    /// 把 <c>/dshlog</c> 接进游戏的调试控制台。
    ///
    /// <para><b>为什么用 Harmony 拦截，而不是注册官方 CommandDefinition？</b></para>
    /// <para>
    /// 官方那条路要 metaXML 的 <c>commands.xml</c>、要把程序集登记进 <c>ModInfo.Assemblies</c>、
    /// 参数类型只认 <c>bool/int/float/command/id</c>（想收自由文本得靠 <c>type="id"</c> 不写
    /// idType 的漏洞）；而且定义必须注册在 **mod 自己的命名空间** 下才能被查到，裸命令名
    /// 解析用的默认命名空间却是 <c>mvz2</c> → 只能写成 <c>/mod_nsp:dshlog</c>。
    /// 更关键的是往 <c>GlobalGame.definitionGroup</c> 里塞注入 def 有 metadata v31 下的
    /// LINQ 枚举崩溃风险（见 AI_GUIDE §5-⑤）。
    /// </para>
    /// <para>
    /// 拦截 <c>DebugManager.ExecuteCommand</c> 就干净得多 —— 这也是本仓库里
    /// <c>ModularCurse/CurseCommand.cs</c>（<c>/curse</c>）已经验证过的做法。
    /// 两边各拦各的命令名，互不干扰：Harmony 会跑完**所有** prefix，
    /// 只有「所有 prefix 都返回 true」才交给原方法，所以哪怕两条 prefix 同时存在也安全。
    /// </para>
    ///
    /// <para><b>执行时机与线程</b>：控制台提交发生在 Unity 的 Update 流程里，
    /// 所以 <see cref="Prefix"/> 跑在**游戏主线程**。这一点很重要 —— 它意味着这里可以
    /// 安全地碰 il2cpp 类型（<see cref="Global.Debugs"/>）；换成后台线程就会崩
    /// （见 AI_GUIDE §5-⑮）。</para>
    /// </summary>
    internal static class DebugCommands
    {
        private const string HarmonyId = "qoznos.mvz2.core.debugcommands";

        public static void Apply()
        {
            try
            {
                var harmony = new Harmony(HarmonyId);

                // 用 AccessTools 而不是 Type.GetMethod：后者对 il2cpp 生成的成员
                // 经常找不到（默认只找 public 且不看 interop 的包装）。
                var target = AccessTools.Method(
                    typeof(DebugManager),
                    nameof(DebugManager.ExecuteCommand),
                    new[] { typeof(string), typeof(int) });

                if (target == null)
                {
                    Plugin.Log?.LogWarning(
                        "找不到 DebugManager.ExecuteCommand(string,int)，游戏内指令 /" +
                        LogViewerCommand.Name + " 不可用（其余功能不受影响）。");
                    return;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(DebugCommands), nameof(Prefix))));

                // Tab 自动补全：把 /dshlog 塞进建议列表。
                // 官方 FillNameSuggestions 只遍历「已注册的 CommandDefinition」，
                // 而我们刻意不走那条注册路（见类注释），所以必须自己补。
                // 注意：这是个纯追加的 void prefix，不拦截原方法 ——
                // FillSuggestions 只能有一个 prefix 负责「替换」，我们只做「追加」。
                var suggestTarget = AccessTools.Method(
                    typeof(DebugManager),
                    "FillSuggestions",
                    new[] { typeof(Il2CppStringArray), typeof(Il2CppSystem.Collections.Generic.List<string>) });

                if (suggestTarget != null)
                {
                    harmony.Patch(suggestTarget, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(DebugCommands), nameof(SuggestPrefix))));
                }
                else
                {
                    Plugin.Log?.LogWarning(
                        "找不到 DebugManager.FillSuggestions，/" + LogViewerCommand.Name +
                        " 不会出现在 Tab 补全里（指令本身仍可用）。");
                }

                Plugin.Log?.LogInfo("游戏内指令已就绪：/" + LogViewerCommand.Name + "、/" + TransCommand.Name + "（含 Tab 补全）");
            }
            catch (Exception ex)
            {
                // 指令挂不上绝不能影响游戏：查看器仍可用配置里的 Enabled = true 自动开启
                Plugin.Log?.LogWarning("游戏内指令注册失败（不影响游戏）：" + ex.Message);
            }
        }

        /// <summary>
        /// 往自动补全列表里追加我们的建议。
        ///
        /// <para><b>必须是 void</b>：返回 <c>false</c> 会跳过原方法，那样官方的补全就全没了。
        /// 这里只做「追加」—— 建议的去重交给下面那个循环，排序由调用方负责
        /// （<c>DebugConsoleController.UpdateSuggestions</c> 会自己 <c>Sort()</c>）。</para>
        ///
        /// <para>线程：补全发生在输入框内容变化的 Update 流程里，所以同样在游戏主线程，
        /// 读写 il2cpp 的 <c>List</c>/数组是安全的。</para>
        /// </summary>
        private static void SuggestPrefix(
            Il2CppStringArray parts,
            Il2CppSystem.Collections.Generic.List<string> currentSuggestions)
        {
            try
            {
                if (parts == null || currentSuggestions == null || parts.Length == 0)
                    return;

                var args = new string[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    args[i] = parts[i];

                var extra = LogViewerCommand.Suggest(args);
                for (int i = 0; i < extra.Length; i++)
                {
                    // 官方将来若真的注册了同名指令，别出现两条一样的建议
                    if (!Contains(currentSuggestions, extra[i]))
                        currentSuggestions.Add(extra[i]);
                }

                var extra2 = TransCommand.Suggest(args);
                for (int i = 0; i < extra2.Length; i++)
                {
                    if (!Contains(currentSuggestions, extra2[i]))
                        currentSuggestions.Add(extra2[i]);
                }
            }
            catch (Exception ex)
            {
                // 补全出错必须吞掉：抛出会打断整个输入框的刷新
                Plugin.Log?.LogDebug("自动补全注入失败：" + ex.Message);
            }
        }

        private static bool Contains(Il2CppSystem.Collections.Generic.List<string> list, string value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 拦截所有控制台输入。返回 <c>false</c> 表示「这条我处理了，别再交给官方」，
        /// 否则交给官方（官方的行为是找不到命令 → 打印 COMMAND_NOT_FOUND）。
        /// </summary>
        private static bool Prefix(string input)
        {
            try
            {
                var args = Split(input);
                var text = LogViewerCommand.Execute(args);
                if (text == null)
                    text = TransCommand.Execute(args);

                if (text == null)
                    return true;    // 不是我们的指令

                Plugin.Log?.LogInfo("[dsh] 收到：" + input.Trim());

                // 回显一行，和官方的输出风格对齐（官方也会先 Print "> {input}"）
                PrintLine("> " + input.Trim());
                PrintLine(text);
                return false;
            }
            catch (Exception ex)
            {
                // 绝不能把异常抛回游戏的输入处理链路 —— 那会连带后续所有指令都失效
                try { PrintLine("[DSHCore] 指令执行出错：" + ex.Message); } catch { }
                Plugin.Log?.LogError("[dsh] " + ex);
                return false;
            }
        }

        /// <summary>
        /// 按空白切分并剥掉前导 <c>/</c>。
        ///
        /// <para>刻意不复用游戏的 <c>CommandUtility.SplitCommand</c>：它要多引一层
        /// <c>MVZ2Logic.Commands</c>，而这个切分规则简单到只有几行。更重要的是 ——
        /// 保持解析规则在本文件里自洽，出问题时不必再去对照另一个程序集的实现。</para>
        /// </summary>
        private static string[] Split(string input)
        {
            var trimmed = input.Trim();
            if (trimmed.StartsWith("/", StringComparison.Ordinal))
                trimmed = trimmed.Substring(1);

            if (trimmed.Length == 0)
                return Array.Empty<string>();

            return trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>打印到游戏内控制台；通道不可用时退回 BepInEx 日志，别让消息凭空消失。</summary>
        private static void PrintLine(string text)
        {
            var debug = Global.Debugs;
            if (debug != null)
            {
                debug.Print(text + "\n");
                return;
            }
            Plugin.Log?.LogInfo(text);
        }
    }
}
