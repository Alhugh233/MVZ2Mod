using System;
using System.Text;
using DSHCore.Ui;

namespace DSHCore.Diagnostics
{
    /// <summary>
    /// 游戏内指令 <c>/dshtrans</c>：管**模组译文的外部文件**。
    ///
    /// <para>为什么需要一条指令：译文表是给**玩家**改的，而玩家不可能去读源码里的字面串。
    /// 这条指令提供「导出键表 → 编辑 → 重新加载」这个闭环：</para>
    /// <list type="number">
    /// <item><c>/dshtrans</c> —— 看状态（外部目录、当前语言读到了多少条、见过多少源串）；</item>
    /// <item><c>/dshtrans keys</c> —— 列出全部已知源串与它们当前的取值；</item>
    /// <item><c>/dshtrans write en-US</c> —— 把键表写成翻译模板文件（已存在则不覆盖）；</item>
    /// <item>改完文件后 <c>/dshtrans reload</c> —— 重新读盘，不用重启游戏。</item>
    /// </list>
    ///
    /// <para>游戏胶水（Harmony 补丁 + <c>DebugManager.PrintLine</c>）在 <see cref="DebugCommands"/>；
    /// 本类保持**纯函数**（字符串数组进、字符串出），便于离线断言。</para>
    /// </summary>
    internal static class TransCommand
    {
        /// <summary>指令名（不含前导 <c>/</c>）。</summary>
        public const string Name = "dshtrans";

        private static readonly Subcommand[] Subcommands =
        {
            new Subcommand(null, "显示状态（外部目录 / 当前语言 / 已见源串数）"),
            new Subcommand("keys", "列出全部已知源串与当前取值"),
            new Subcommand("write", "写出翻译模板：write <语言码> [overwrite]"),
            new Subcommand("reload", "重新读取外部译文文件（改完文件不用重启）"),
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

        /// <summary>Tab 补全（形状与 <c>LogViewerCommand.Suggest</c> 一致）。</summary>
        public static string[] Suggest(string[] args)
        {
            if (args == null || args.Length == 0)
                return Array.Empty<string>();

            var first = args[0] ?? string.Empty;
            if (args.Length == 1)
                return StartsWith(Name, first) ? new[] { Name } : Array.Empty<string>();

            if (args.Length == 2 && string.Equals(first, Name, StringComparison.OrdinalIgnoreCase))
            {
                var prefix = args[1] ?? string.Empty;
                var hits = new System.Collections.Generic.List<string>(Subcommands.Length);
                for (var i = 0; i < Subcommands.Length; i++)
                {
                    var arg = Subcommands[i].Arg;
                    if (arg != null && StartsWith(arg, prefix))
                        hits.Add(arg);
                }
                return hits.ToArray();
            }

            return Array.Empty<string>();
        }

        /// <summary>
        /// 执行。返回 <c>null</c> 表示「不是我的指令」（交给下一个指令 / 原版逻辑）。
        /// </summary>
        public static string Execute(string[] args)
        {
            if (args == null || args.Length == 0)
                return null;
            if (!string.Equals(args[0], Name, StringComparison.OrdinalIgnoreCase))
                return null;

            var sub = args.Length > 1 ? (args[1] ?? string.Empty).ToLowerInvariant() : null;

            switch (sub)
            {
                case null:
                    return Status();
                case "keys":
                    return UiLocale.DumpKeys();
                case "reload":
                    return Reload();
                case "write":
                    return Write(args);
                case "help":
                case "?":
                    return Help();
                default:
                    return "未知子指令：" + args[1] + "\n" + Help();
            }
        }

        private static string Status()
        {
            var sb = new StringBuilder(600);
            sb.Append("模组译文（DSHCore.Ui.UiLocale）\n");
            sb.Append(UiLocale.Report()).Append('\n');
            sb.Append("\n用 /").Append(Name).Append(" help 看用法。");
            return sb.ToString();
        }

        private static string Reload()
        {
            var n = UiLocale.ReloadExternal();
            return "已清掉 " + n + " 种语言的外部译文缓存。\n" + UiLocale.Report();
        }

        private static string Write(string[] args)
        {
            if (args.Length < 3 || string.IsNullOrEmpty(args[2]))
                return "用法：/" + Name + " write <语言码> [overwrite]\n例如：/" + Name + " write en-US";

            var lang = args[2];
            var overwrite = args.Length > 3
                && string.Equals(args[3], "overwrite", StringComparison.OrdinalIgnoreCase);

            var path = UiLocale.WriteTemplate(lang, overwrite);
            if (path == null)
            {
                return "没写文件：`" + System.IO.Path.Combine(UiLocale.ExternalDirectory, lang + ".txt")
                    + "` 已经存在。\n"
                    + "（模板是**追加**在 mod 自带译文之上的，所以不需要全量覆盖；"
                    + "要强制重写就在最后加 overwrite。）";
            }
            return "已写出翻译模板：" + path + "\n"
                + "把译文写在每条 `=` 的右边（**留空 = 还没译**，会退回 mod 自带的译文），"
                + "然后 /" + Name + " reload 生效。";
        }

        private static string Help()
        {
            var sb = new StringBuilder(400);
            sb.Append("用法：/").Append(Name).Append(" [子指令]\n");
            for (var i = 0; i < Subcommands.Length; i++)
            {
                var c = Subcommands[i];
                sb.Append("  /").Append(Name);
                if (c.Arg != null)
                    sb.Append(' ').Append(c.Arg);
                sb.Append("  —— ").Append(c.Description).Append('\n');
            }
            sb.Append("\n译文文件放在：").Append(UiLocale.ExternalDirectory).Append('\n');
            sb.Append("  <语言码>.txt     主文件（直接编辑这一份）\n");
            sb.Append("  <语言码>.d/*.txt 附加文件（可选，多个来源各放一份）\n");
            sb.Append("\n格式：每行 `源串 = 译文`，`#` 开头是注释，`\\n` 表示换行。\n");
            sb.Append("译文**留空** = 还没译（会退回 mod 自带的译文，不会变空白）。\n");
            sb.Append("mod 的语言跟着**游戏设置**走：要新增一种语言，先在「插件 → 资源包」\n");
            sb.Append("里启用一份支持该语言的语言包，再放对应的 <语言码>.txt。");
            return sb.ToString();
        }

        private static bool StartsWith(string s, string prefix)
        {
            return s != null && (string.IsNullOrEmpty(prefix)
                || s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }
}
