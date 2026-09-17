using System;
using HarmonyLib;
using MVZ2.Debugs;
using MVZ2Logic;
using MVZ2Logic.Commands;

namespace ModularCurse
{
    /// <summary>
    /// 控制台指令 <c>/curse</c>。
    ///
    ///   /curse all    强制显示所有诅咒池（不按池过滤，列出全部诅咒）
    ///   /curse off    恢复按诅咒池过滤
    ///   /curse        查看当前状态
    ///
    /// 为什么直接挂 Harmony，而不是走官方的 <c>CommandDefinition</c>：
    ///   官方的命令是"数据驱动"的，链路是
    ///     <c>Content/metas/commands.xml</c> → <c>ModResource.CommandMetaList</c>
    ///     → <c>ModLoader.LoadCommandProperties(mod)</c> 里
    ///       <c>mod.GetCommandDefinition(new NamespaceID(mod.Namespace, meta.ID))</c>
    ///     → 把 <c>VARIANTS</c> 等属性写到定义上（<c>ValidateCommand</c> 要求 variants 非空）。
    ///   两个问题：
    ///     1. 定义必须注册在 **mod 自己的命名空间** 下才能被这行查找命中，而控制台
    ///        解析裸命令名用的默认命名空间是 <c>mvz2</c>（<c>NamespaceID.TryParse(command,
    ///        Main.BuiltinNamespace)</c>）→ 于是官方路线下只能写成
    ///        <c>/modular_curse:curse</c>，拿不到 <c>/curse</c>；
    ///     2. 要自己跨程序集写 <c>ICommandVariantMeta[]</c>（接口数组）到属性上，
    ///        在 IL2CPP 下是纯属自找麻烦。
    ///   我们只需要一条排障用指令，所以直接在 <c>DebugManager.ExecuteCommand</c> 上加前缀
    ///   拦截 —— 命令名解析复用官方的 <see cref="CommandUtility.SplitCommand"/>，
    ///   行为与官方指令一致（同样只在控制台里、同样受 debug 权限限制）。
    /// </summary>
    internal static class CurseCommand
    {
        private const string CommandName = "curse";

        private static bool applied;
        private static Harmony harmony;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            harmony = new Harmony(Plugin.GUID + ".cursecommand");
            try
            {
                var target = AccessTools.Method(typeof(DebugManager), "ExecuteCommand",
                    new[] { typeof(string), typeof(int) });
                if (target == null)
                {
                    Plugin.Log?.LogError("[CurseCommand] 找不到 DebugManager.ExecuteCommand");
                    return;
                }
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(CurseCommand), nameof(ExecuteCommandPrefix)));
                Plugin.Log?.LogInfo("[CurseCommand] patched /" + CommandName);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CurseCommand] patch 失败: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>返回 false = 我们的指令，不再交给原版解析。</summary>
        private static bool ExecuteCommandPrefix(string input)
        {
            try
            {
                if (!IsCurseCommand(input))
                    return true;
                Plugin.Log?.LogInfo("[CurseCommand] 收到：" + input.Trim());
                PrintLine("> " + input.Trim());
                Handle(input);
                return false;
            }
            catch (Exception e)
            {
                PrintLine("[curse] 执行出错: " + e.GetType().Name + ": " + e.Message);
                Plugin.Log?.LogError("[CurseCommand] " + e);
                return false;
            }
        }

        private static bool IsCurseCommand(string input)
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
            var sub = parts != null && parts.Length > 1 ? parts[1] : string.Empty;
            switch (sub == null ? string.Empty : sub.ToLowerInvariant())
            {
                case "all":
                case "on":
                    {
                        var live = CursePoolFeature.ApplyForceAll(true);
                        PrintLine(live
                            ? "[curse] 已强制显示所有诅咒池（本次选卡立即生效）。"
                            : "[curse] 已记下：显示所有诅咒池，进入选卡界面后生效。");
                        PrintLine("[curse] 当前：" + CursePoolFeature.Describe());
                        break;
                    }
                case "off":
                case "none":
                    {
                        var live = CursePoolFeature.ApplyForceAll(false);
                        PrintLine(live
                            ? "[curse] 已恢复按诅咒池过滤。"
                            : "[curse] 已记下：按诅咒池过滤，进入选卡界面后生效。");
                        PrintLine("[curse] 当前：" + CursePoolFeature.Describe());
                        break;
                    }
                case "status":
                    PrintLine("[curse] 当前：" + CursePoolFeature.Describe());
                    break;
                default:
                    PrintLine("[curse] 当前：" + CursePoolFeature.Describe());
                    PrintUsage();
                    break;
            }
        }

        private static void PrintUsage()
        {
            PrintLine("[curse] 用法：");
            PrintLine("  /curse all    强制显示所有诅咒池（调试用）");
            PrintLine("  /curse off    恢复按诅咒池过滤");
            PrintLine("  /curse        查看当前状态");
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
