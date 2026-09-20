using System;
using System.Text;

namespace DSHCore.Diagnostics
{
    /// <summary>
    /// 游戏内指令 <c>/dshlog</c> 的**纯托管**实现。
    ///
    /// <para><b>为什么单独一个文件、且不引用任何游戏类型？</b></para>
    /// <para>
    /// 网页查看器本身是纯托管的，所以它能在**不启动游戏**的情况下被离线测试
    /// （见仓库历史里的冒烟测试台）。指令逻辑如果直接写在 Harmony 补丁里，就绑死在
    /// IL2CPP 运行时上，只能靠「启动游戏 + 用户名是 debug + 手敲指令」来验证 ——
    /// 那是最难自动化的一条路。
    /// </para>
    /// <para>
    /// 这里只做「字符串进 → 字符串出」，把结果交给调用方去打印。
    /// 于是 <see cref="Execute"/> 可以在普通 .NET 控制台程序里被直接断言。
    /// 游戏胶水（Harmony 补丁 + <c>DebugManager.PrintLine</c>）在 <see cref="DebugCommands"/>。
    /// </para>
    /// </summary>
    internal static class LogViewerCommand
    {
        /// <summary>指令名（不含前导 <c>/</c>）。</summary>
        public const string Name = "dshlog";

        /// <summary>
        /// 可以补全的子指令。
        ///
        /// <para><b>唯一真源</b>：<see cref="Help"/>（帮助文本）与 <see cref="Suggest"/>
        /// （Tab 补全）都从这里读。以前帮助是手写的一串 Append，补全另写一份 —— 那种写法
        /// 迟早会出现「帮助里有、Tab 补不出来」的偏差。</para>
        ///
        /// <para><see cref="Subcommand.Arg"/> 为 <c>null</c> 表示不带参数的那个（默认行为）。</para>
        /// </summary>
        private static readonly Subcommand[] Subcommands =
        {
            new Subcommand(null, "开启（已开启则显示状态）"),
            new Subcommand("off", "关闭并释放端口（内存历史保留）"),
            new Subcommand("open", "用默认浏览器打开"),
            new Subcommand("url", "只打印带令牌的地址"),
            new Subcommand("status", "显示状态"),
            new Subcommand("console", "显示 / 隐藏 BepInEx 控制台窗口"),
            new Subcommand("clear", "清空内存里的历史"),
            new Subcommand("help", "本帮助"),
        };

        private readonly struct Subcommand
        {
            public readonly string Arg;
            public readonly string Description;

            public Subcommand(string arg, string description)
            {
                Arg = arg;
                Description = description;
            }
        }

        /// <summary>
        /// Tab 补全：算出应该追加到游戏自动补全列表里的项。
        ///
        /// <para><b>纯函数</b>（字符串数组进、字符串数组出），所以能脱离游戏离线断言 ——
        /// 这一点跟 <see cref="Execute"/> 一样，是刻意维持的。
        /// 游戏胶水在 <see cref="DebugCommands"/>。</para>
        ///
        /// <para>匹配规则对齐官方 <c>DebugManager.FillNameSuggestions</c> 的
        /// <c>StartsWith(..., OrdinalIgnoreCase)</c>：输入 <c>/</c>（<c>parts</c> 是空串）时
        /// 会列出全部指令，输入 <c>/ds</c> 时列出 <c>dshlog</c>，输入 <c>/zz</c> 时什么都不加。</para>
        /// </summary>
        /// <param name="args">已去掉前导 <c>/</c> 的参数（与 <see cref="Execute"/> 同格式）。</param>
        public static string[] Suggest(string[] args)
        {
            if (args == null || args.Length == 0)
                return Array.Empty<string>();

            string first = args[0] ?? string.Empty;

            // 还在敲指令名
            if (args.Length == 1)
            {
                return StartsWith(Name, first)
                    ? new[] { Name }
                    : Array.Empty<string>();
            }

            // 已经在敲子指令，但只对完全匹配到我们时给建议
            // （部分匹配交给官方自己的机制，不越俎代庖）
            if (args.Length == 2 && string.Equals(first, Name, StringComparison.OrdinalIgnoreCase))
            {
                string prefix = args[1] ?? string.Empty;
                var hits = new System.Collections.Generic.List<string>(Subcommands.Length);
                for (int i = 0; i < Subcommands.Length; i++)
                {
                    var arg = Subcommands[i].Arg;
                    if (arg != null && StartsWith(arg, prefix))
                        hits.Add(arg);
                }
                return hits.ToArray();
            }

            return Array.Empty<string>();
        }

        private static bool StartsWith(string name, string prefix) =>
            name.StartsWith(prefix ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 处理一条指令。返回要打印到游戏内控制台的文本；
        /// <c>null</c> 表示「这不是我们的指令」，调用方应把它交回官方处理。
        /// </summary>
        /// <param name="args">
        /// 已按空格切分、且**已去掉前导 <c>/</c>** 的参数（<c>args[0]</c> 是指令名）。
        /// </param>
        public static string Execute(string[] args)
        {
            if (args == null || args.Length == 0)
                return null;

            if (!string.Equals(args[0], Name, StringComparison.OrdinalIgnoreCase))
                return null;

            string sub = args.Length > 1 ? (args[1] ?? string.Empty).ToLowerInvariant() : string.Empty;

            switch (sub)
            {
                case "":
                case "on":
                case "start":
                    return Start();

                case "off":
                case "stop":
                case "close":
                    return Stop();

                case "open":
                case "browser":
                    return Open();

                case "url":
                case "addr":
                    return Url();

                case "status":
                case "stat":
                    return Status();

                case "console":
                    return Console();

                case "clear":
                    return Clear();

                case "help":
                case "?":
                    return Help();

                default:
                    return "[DSHCore] 未知参数「" + args[1] + "」。\n" + Help();
            }
        }

        private static string Start()
        {
            if (LogPipeline.IsServerRunning)
                return "[DSHCore] 日志查看器已经在运行了。\n" + Status();

            if (!LogPipeline.StartServer(out var message))
                return "[DSHCore] " + message;

            return "[DSHCore] " + message +
                   "\n（停止：/" + Name + " off；更多：/" + Name + " help）";
        }

        private static string Stop()
        {
            LogPipeline.StopServer(out var message);
            return "[DSHCore] " + message;
        }

        private static string Open()
        {
            if (!LogPipeline.IsServerRunning)
                return "[DSHCore] 日志查看器还没开。先用 /" + Name + " 开启。";

            if (LogPipeline.TryOpenInBrowser(null))
                return "[DSHCore] 已用默认浏览器打开：" + LogPipeline.Url;

            // 打不开就给地址，让用户手动粘 —— 总比只说一句「失败」有用
            return "[DSHCore] 打不开浏览器，请手动访问：\n" + LogPipeline.Url;
        }

        private static string Url()
        {
            if (!LogPipeline.IsServerRunning)
                return "[DSHCore] 日志查看器还没开。先用 /" + Name + " 开启。";

            var sb = new StringBuilder(220);
            sb.Append("[DSHCore] ").Append(LogPipeline.Url);
            // URL 里带访问令牌 —— 提醒一句，免得被随手贴出去
            sb.Append("\n（地址里含本次会话的访问令牌，别公开分享）");
            return sb.ToString();
        }

        private static string Status()
        {
            var sb = new StringBuilder(420);
            sb.Append("[DSHCore] 日志查看器\n");

            sb.Append("  状态      : ");
            sb.Append(LogPipeline.IsServerRunning ? "运行中" : "未运行（捕获中）");
            sb.Append('\n');

            sb.Append("  地址      : ");
            sb.Append(LogPipeline.IsServerRunning
                ? LogPipeline.Url + "\n（含本次会话令牌，别公开分享）"
                : "—（用 /" + Name + " 开启）");
            sb.Append('\n');

            sb.Append("  已捕获    : ").Append(LogPipeline.EmittedCount).Append(" 条");
            sb.Append("（缓冲 ").Append(LogPipeline.RingCount).Append('/').Append(LogPipeline.RingCapacity).Append("）\n");

            sb.Append("  已丢弃    : ").Append(LogPipeline.DroppedCount).Append(" 条");
            if (LogPipeline.DroppedCount > 0)
                sb.Append("（队列满，可在配置里调大 QueueCapacity）");
            sb.Append('\n');

            if (LogPipeline.IsServerRunning)
            {
                sb.Append("  已连接    : ").Append(LogPipeline.ClientCount).Append(" 个浏览器\n");
                sb.Append("  监听      : ").Append(LogPipeline.ServerAccessMode).Append('\n');
            }

            sb.Append("  控制台窗口: ").Append(ConsoleWindowState());

            return sb.ToString();
        }

        private static string Console()
        {
            if (!ConsoleWindow.IsAvailable)
                return "[DSHCore] 没有控制台窗口（BepInEx.cfg 里 [Logging.Console] Enabled = false，或游戏由终端启动）。";

            if (!ConsoleWindow.OwnsConsole)
                return "[DSHCore] 控制台窗口属于父进程（你是从终端启动的游戏），不去动它。";

            bool visible = ConsoleWindow.Toggle();
            return "[DSHCore] BepInEx 控制台窗口已" + (visible ? "显示" : "隐藏") + "。";
        }

        private static string Clear()
        {
            int before = LogPipeline.RingCount;
            LogPipeline.ClearHistory();
            return "[DSHCore] 已清空内存里的 " + before + " 条历史（磁盘日志 BepInEx/LogOutput.log 不受影响）。";
        }

        private static string Help()
        {
            // 对齐："/dshlog" + 子指令列，后面接说明。宽度按最长的子指令（console）算。
            const int ArgWidth = 7;

            var sb = new StringBuilder(520);
            sb.Append("[DSHCore] /").Append(Name).Append(" —— 本地网页日志查看器\n");
            for (int i = 0; i < Subcommands.Length; i++)
            {
                var sub = Subcommands[i];
                sb.Append("  /").Append(Name).Append(' ');
                sb.Append((sub.Arg ?? string.Empty).PadRight(ArgWidth));
                sb.Append(sub.Description).Append('\n');
            }
            sb.Append("配置：[LogViewer] 段，见 BepInEx/config/").Append(Plugin.GUID).Append(".cfg");
            return sb.ToString();
        }

        private static string ConsoleWindowState()
        {
            if (!ConsoleWindow.IsAvailable)
                return "不可用";
            if (!ConsoleWindow.OwnsConsole)
                return "属于父进程（不动它）";
            return ConsoleWindow.IsVisible ? "可见" : "已隐藏";
        }
    }
}
