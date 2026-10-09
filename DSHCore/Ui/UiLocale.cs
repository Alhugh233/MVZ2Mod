using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2.Localization;

namespace DSHCore.Ui
{
    /// <summary>
    /// **模组文本的本地化接口**：把 mod 自己的文案接进游戏的语言系统。
    ///
    /// <para><b>游戏那一套是怎么回事</b>（实测 + 反编译 <c>MVZ2/Localization/</c>）：</para>
    /// <list type="bullet">
    /// <item><b>源语言是 <c>zh-Hans</c></b>（<c>LanguageManager.SOURCE_LANGUAGE</c>）——
    /// 游戏自己的文案就写中文，其它语言靠 catalog 翻译过去。默认语言也是 <c>zh-Hans</c>。</item>
    /// <item>查找入口是 <c>LanguageManager.TryGetLocalizedString(text, language, out str)</c>：
    /// 依次问每个 <c>ICatalogProvider</c>，全都没命中就返回原文。
    /// <c>Global.Localization.GetText(...)</c> / <c>_(...)</c> / TMP 上的
    /// <c>TextMeshProUGUITranslator</c> 最终都走到这个方法。</item>
    /// <item>**官方的 mod 语言包**是「语言包资源包」：<c>assets/&lt;nsp&gt;/&lt;lang&gt;/*.mo</c>
    /// （gettext），由 <c>ResourcePackManager</c> 读成 <c>ResourcePack</c>
    /// （它自己实现 <c>ICatalogProvider</c>）再 <c>LanguageManager.AddCatalogProvider</c>。
    /// 也就是说：**mod 要出多语言，正规做法是打一份语言包 zip**，
    /// 在「插件页 → 语言包」里启用。</item>
    /// </list>
    ///
    /// <para><b>为什么本库不去实现 <c>ICatalogProvider</c>：</b>托管类要实现 il2cpp 接口得走
    /// <c>ClassInjector</c> 的接口代理，而在这套环境里接口分派是有坑的
    /// （<c>MetaInjection</c> 就是为了绕开它才改成直读磁盘 XML 的）。
    /// 本库改用**在查找入口上挂一个前缀**（<see cref="Apply"/>）：
    /// 效果与「多注册一个 catalog provider」等价 —— 游戏里任何一次文本查找都会先问
    /// 我们的小表，命中就返回。</para>
    ///
    /// <para><b>用法</b>（<c>Load()</c> 里声明，纯托管）：</para>
    /// <code>
    /// UiLocale.Add("Better Furnace", "更好的熔炉");            // 省略语言 = 源语言(zh-Hans)
    /// UiLocale.Add("Better Furnace", "Better Furnace", UiLocale.English);
    /// string label = UiLocale.Get("更好的熔炉");                // 当前语言下的文案
    /// </code>
    /// <para><b>约定</b>：源串（key）写成**语言中立**的那一份（一般就是英文名），
    /// 中文用 <c>Add(key, 中文)</c> 补上；查不到时返回源串本身，所以不补也不会崩。</para>
    ///
    /// <para>⚠ <b>带参数的串（<c>{0}</c>）不走本表</b>：官方 provider 命中后会自己
    /// <c>string.Format</c>，而我们拿到的是 il2cpp 的 <c>object[]</c>，转换代价与出错面都大 ——
    /// 所以带参数的查找直接放行给原逻辑（见 <c>TryGetLocalizedStringPrefix</c>）。</para>
    /// </summary>
    public static class UiLocale
    {
        /// <summary>中文（= 游戏源语言）。</summary>
        public const string Chinese = "zh-Hans";

        /// <summary>英文。</summary>
        public const string English = "en-US";

        /// <summary>源语言（mod 不写语言的 <c>Add</c> 默认落在这里）。</summary>
        public const string SourceLanguage = Chinese;

        private const string HarmonyId = "qoznos.mvz2.core.uilocale";
        private const string ProbeKey = "DSHCore.LocaleProbe";

        /// <summary>自检用的上下文探针（验证「带上下文的查找」是不是也挂上了）。</summary>
        private const string ProbeContext = "dsh.localeprobe";

        // ------------------------------------------------------------------
        // 表
        // ------------------------------------------------------------------

        /// <summary>语言 → (源串 → 译文)。</summary>
        private static readonly Dictionary<string, Dictionary<string, string>> Tables
            = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        /// <summary>
        /// 本次会话里**被问过的键**（<see cref="Get"/> / <see cref="In"/> / <see cref="Add"/>）。
        ///
        /// <para>键是**内部形式**（带上下文的条目在 <c>上下文\u0004源串</c> 里用 EOT 分隔）。
        /// 要展示给人看请过 <see cref="DescribeKey"/>，要拆回去用 <see cref="SplitKey"/>。</para>
        ///
        /// <para>用途是导出翻译模板（<c>/dshtrans write &lt;lang&gt;</c>）：玩家只需要把游戏跑一遍
        /// （进主菜单 → 打开一次「模组设置」页，让所有文案被问过一遍），就能得到一份
        /// 覆盖全部界面的键表 —— 不用去读源码里的字面串。</para>
        /// </summary>
        private static readonly List<string> SeenKeys = new List<string>();
        private static readonly HashSet<string> SeenKeySet = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 拿一张**归属某个 mod** 的译文表（同名重复调用返回同一张）。
        ///
        /// <para>这是 mod 侧写多语言的推荐入口 —— 一个 mod 一个 <c>Localization.cs</c>，
        /// 一块写完自己的全部文案（见 <see cref="UiLocaleTable"/> 的示例）。</para>
        /// </summary>
        public static UiLocaleTable Table(string owner)
        {
            var key = owner ?? string.Empty;
            UiLocaleTable table;
            if (TableRegistry.TryGetValue(key, out table))
                return table;
            table = new UiLocaleTable(key);
            TableRegistry[key] = table;
            return table;
        }

        /// <summary>已登记的全部译文表（诊断用）。</summary>
        public static UiLocaleTable[] RegisteredTables
        {
            get { return new List<UiLocaleTable>(TableRegistry.Values).ToArray(); }
        }

        private static readonly Dictionary<string, UiLocaleTable> TableRegistry
            = new Dictionary<string, UiLocaleTable>(StringComparer.Ordinal);

        /// <summary>登记一条译文。<paramref name="language"/> 省略 = 源语言（中文）。
        ///
        /// <para><paramref name="context"/> = **文本上下文**（对应游戏自己的 <c>_p(context, text)</c>）。
        /// 游戏里「名字类」文本都带上下文：关卡名用 <c>LogicStrings.CONTEXT_LEVEL_NAME</c>、
        /// 诅咒名/说明用 <c>CONTEXT_CURSE_NAME</c> / <c>CONTEXT_CURSE_TOOLTIP</c>……
        /// 同一个中文串在不同上下文里可能是两种意思，所以**上下文是键的一部分**。
        /// 不带上下文的登记与查询只与不带上下文的登记互相对应（另见 <see cref="TryTable"/> 的匹配顺序）。</para>
        /// </summary>
        public static void Add(string source, string translated, string language = null, string context = null)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translated))
                return;
            var lang = string.IsNullOrEmpty(language) ? SourceLanguage : language;

            Dictionary<string, string> table;
            if (!Tables.TryGetValue(lang, out table))
            {
                table = new Dictionary<string, string>(StringComparer.Ordinal);
                Tables[lang] = table;
            }
            table[Key(context, source)] = translated;
            Remember(context, source);
        }

        /// <summary>记下「界面问过这个源串」（幂等）。</summary>
        private static void Remember(string source)
        {
            Remember(null, source);
        }

        /// <summary>记下「界面问过这个（上下文, 源串）」（幂等）。</summary>
        private static void Remember(string context, string source)
        {
            if (string.IsNullOrEmpty(source) || source == ProbeKey)
                return;
            var key = Key(context, source);
            if (SeenKeySet.Add(key))
                SeenKeys.Add(key);
        }

        /// <summary>批量登记（<c>Add(a, b); Add(a2, b2); …</c> 的简写：<c>AddAll("a","b","a2","b2")</c>）。</summary>
        public static void AddAll(string language, params string[] pairs)
        {
            if (pairs == null)
                return;
            for (var i = 0; i + 1 < pairs.Length; i += 2)
                Add(pairs[i], pairs[i + 1], language);
        }

        /// <summary>已登记的语言码。</summary>
        public static string[] Languages
        {
            get
            {
                var list = new List<string>(Tables.Keys);
                return list.ToArray();
            }
        }

        /// <summary>某个语言下登记了多少条。</summary>
        public static int CountOf(string language)
        {
            Dictionary<string, string> table;
            return (language != null && Tables.TryGetValue(language, out table)) ? table.Count : 0;
        }

        /// <summary>某个语言下**带上下文**的条目有多少条（游戏内容名）。</summary>
        public static int ContextCountOf(string language)
        {
            Dictionary<string, string> table;
            if (language == null || !Tables.TryGetValue(language, out table))
                return 0;
            var n = 0;
            foreach (var key in table.Keys)
            {
                if (key.IndexOf(ContextSeparator) >= 0)
                    n++;
            }
            return n;
        }

        private static bool TryTable(string language, string source, out string translated)
        {
            return TryTable(language, source, null, false, out translated);
        }

        /// <summary>
        /// 查表（外部文件 → 代码表）。
        ///
        /// <para><b>带上下文时先查带上下文的键，再（仅在 <paramref name="plainFallback"/> 时）退回
        /// 不带上下文的键</b> —— 后者是给“我自己既把它当内容名、又把它当界面文字”的调用方用的
        /// 便利（<see cref="Get(string,string)"/>）。</para>
        ///
        /// <para>⚠ 给**游戏查找链的补丁**永远用 <c>plainFallback: false</c>：
        /// 游戏会对官方的每一条名字文本做带上下文的查找，允许回退的话，
        /// 一个 mod 的界面译文（例如 Loadout 的「音乐」「背景」这种泛词）
        /// 就会掺进官方的 **内容名** 查找里。上下文存在的意义就是这个，
        /// 不能让两份表互相串味。</para>
        /// </summary>
        private static bool TryTable(string language, string source, string context, bool plainFallback,
            out string translated)
        {
            translated = null;
            if (string.IsNullOrEmpty(language) || string.IsNullOrEmpty(source))
                return false;

            var strict = !string.IsNullOrEmpty(context) && !plainFallback;

            // ① 外部文件优先：用户改得动的那一份（见 UiLocale 的「外部文件」注释）。
            EnsureExternal(language);
            ExternalTable ext;
            if (_external.TryGetValue(language, out ext))
            {
                if (!string.IsNullOrEmpty(context) && ext.Map.TryGetValue(Key(context, source), out translated))
                    return true;
                if (!strict && ext.Map.TryGetValue(source, out translated))
                    return true;
            }

            // ② 代码里 Add 的那份
            Dictionary<string, string> table;
            if (!Tables.TryGetValue(language, out table))
                return false;
            if (!string.IsNullOrEmpty(context) && table.TryGetValue(Key(context, source), out translated))
                return true;
            if (strict)
                return false;
            return table.TryGetValue(source, out translated);
        }

        // ------------------------------------------------------------------
        // 上下文（键的组成部分）
        // ------------------------------------------------------------------

        /// <summary>
        /// 上下文与源串的分隔符 —— 与游戏自己的 catalog 键一致（gettext 的 EOT）。
        /// 用同一个字符意味着两份表在概念上可以互相导出/导入。
        /// </summary>
        private const char ContextSeparator = '\u0004';

        /// <summary>拼内部键。</summary>
        private static string Key(string context, string source)
        {
            return string.IsNullOrEmpty(context) ? source : context + ContextSeparator + source;
        }

        /// <summary>把内部键拆回「上下文 / 源串」（没有上下文时 context 为 null）。</summary>
        public static void SplitKey(string key, out string context, out string source)
        {
            context = null;
            source = key;
            if (string.IsNullOrEmpty(key))
                return;
            var i = key.IndexOf(ContextSeparator);
            if (i < 0)
                return;
            context = key.Substring(0, i);
            source = key.Substring(i + 1);
        }

        /// <summary>内部键的人读形式：<c>[上下文] 源串</c>（没有上下文就只有源串）。</summary>
        public static string DescribeKey(string key)
        {
            string context, source;
            SplitKey(key, out context, out source);
            return string.IsNullOrEmpty(context) ? source : "[" + context + "] " + source;
        }

        // ------------------------------------------------------------------
        // 外部文件（玩家可编辑的译文表）
        // ------------------------------------------------------------------

        /// <summary>
        /// 外部译文表的目录：<c>&lt;BepInEx 配置目录&gt;/DSHCore/lang</c>（首次使用时自动创建）。
        ///
        /// <para><b>两种放法</b>（都按「后加载覆盖先加载」的顺序合并）：</para>
        /// <list type="number">
        /// <item><c>lang/&lt;语言码&gt;.txt</c> —— 主文件（玩家直接编辑这一份）；</item>
        /// <item><c>lang/&lt;语言码&gt;.d/*.txt</c> —— 附加文件（按文件名排序；
        /// 多个 mod / 多个人各放一份，互不覆盖）。</item>
        /// </list>
        ///
        /// <para><b>内容格式</b>（UTF-8，<c>#</c> 开头是注释）：</para>
        /// <code>
        /// # 源串 = 译文
        /// MVZ2 Loadout = Graphical Console
        /// UI 组件画廊 = UI Gallery
        /// </code>
        /// <para>转义：<c>\n</c> 换行、<c>\t</c> 制表符、<c>\\</c> 反斜杠。
        /// 键**必须与 mod 里的源串逐字符一致**（照抄 <c>/dshtrans keys</c> 的输出最保险）。</para>
        ///
        /// <para><b>它不会凭空创造语言</b>：只有当游戏里**已经有**这个语言（= 玩家导入并启用了
        /// 一份支持它的语言包）时，对应文件才会被读到 —— mod 的显示语言始终跟着游戏设置走。</para>
        /// </summary>
        public static string ExternalDirectory
        {
            get
            {
                try
                {
                    return System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "DSHCore", "lang");
                }
                catch (Exception)
                {
                    return "BepInEx/config/DSHCore/lang";
                }
            }
        }

        private sealed class ExternalTable
        {
            /// <summary>源串 → 译文。</summary>
            public readonly Dictionary<string, string> Map
                = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>读到的文件（「名字（N 条）」，用于报告）。</summary>
            public readonly List<string> Files = new List<string>();
        }

        /// <summary>语言 → 已加载的外部表（**加载过就不再读盘**，见 <see cref="ReloadExternal"/>）。</summary>
        private static readonly Dictionary<string, ExternalTable> _external
            = new Dictionary<string, ExternalTable>(StringComparer.Ordinal);

        private static bool _externalLogged;

        /// <summary>保证某个语言的外部表已经读过（每个语言只读一次）。</summary>
        private static void EnsureExternal(string language)
        {
            if (string.IsNullOrEmpty(language) || _external.ContainsKey(language))
                return;

            var table = new ExternalTable();
            try
            {
                var dir = ExternalDirectory;
                var candidates = new List<string>();

                // ① 主文件：zh-Hans.txt（语言码里的 '-' 也接受写成 '_'，与语言包目录名一致）
                candidates.Add(System.IO.Path.Combine(dir, language + ".txt"));
                var underscore = language.Replace('-', '_');
                if (underscore != language)
                    candidates.Add(System.IO.Path.Combine(dir, underscore + ".txt"));

                // ② 附加文件目录：zh-Hans.d/*.txt（排序后加载，靠后的覆盖靠前的）
                var extraDir = System.IO.Path.Combine(dir, language + ".d");
                try
                {
                    if (System.IO.Directory.Exists(extraDir))
                    {
                        var files = System.IO.Directory.GetFiles(extraDir, "*.txt");
                        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                        candidates.AddRange(files);
                    }
                }
                catch (Exception e)
                {
                    UiLog.WarnOnce("uilocale-extdir", "[UiLocale] 读附加目录失败：" + e.GetType().Name);
                }

                for (var i = 0; i < candidates.Count; i++)
                {
                    var path = candidates[i];
                    if (!System.IO.File.Exists(path))
                        continue;
                    var n = ParseExternalFile(path, table.Map);
                    if (n > 0)
                        table.Files.Add(System.IO.Path.GetFileName(path) + "（" + n + " 条）");
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("uilocale-ext", "[UiLocale] 读外部译文失败：" + e.GetType().Name + ": " + e.Message);
            }

            _external[language] = table;

            if (table.Files.Count > 0 && !_externalLogged)
            {
                _externalLogged = true;
                Plugin.Log?.LogInfo("[UiLocale] 已加载外部译文（" + language + "）："
                    + string.Join("、", table.Files.ToArray())
                    + "  目录 " + ExternalDirectory);
            }
        }

        /// <summary>
        /// 清掉外部表的缓存 → 下次取词重新读盘（改完文件不用重启游戏）。
        /// 返回清掉了几种语言。
        /// </summary>
        public static int ReloadExternal()
        {
            var n = _external.Count;
            _external.Clear();
            _externalLogged = false;
            return n;
        }

        /// <summary>某个语言读到了多少条外部译文、来自哪些文件。</summary>
        public static string DescribeExternal(string language)
        {
            EnsureExternal(language);
            ExternalTable table;
            if (!_external.TryGetValue(language, out table))
                return "（未加载）";
            if (table.Files.Count == 0)
                return "无（" + System.IO.Path.Combine(ExternalDirectory, language + ".txt") + " 不存在）";
            return table.Map.Count + " 条 ← " + string.Join("、", table.Files.ToArray());
        }

        /// <summary>
        /// 解析一个译文文件，把条目并进 <paramref name="into"/>。返回**有效**条数。
        ///
        /// <para>格式：每行 <c>源串 = 译文</c>；<c>#</c> 或 <c>;</c> 开头是注释；空行忽略；
        /// <c>=</c> 取**第一个**（译文里可以再出现 <c>=</c>）。</para>
        ///
        /// <para>**带上下文**的行写成 <c>[上下文] 源串 = 译文</c>，例如
        /// <c>[curse.name] 怪物的大脑 = The Creature's Brain</c>。
        /// 上下文就是游戏自己的那套（<c>curse.name</c> / <c>curse.tooltip</c> / <c>level.name</c> …），
        /// 可以用 <c>/dshtrans keys</c> 或在 <c>LogicStrings</c> 里查到。
        /// 不带前缀的行 = 不带上下文（普通界面文字）。</para>
        ///
        /// <para>**译文留空（<c>键 =</c>）等于「还没译」**：这一条会被跳过，
        /// 取词继续往下走（代码表 → 游戏 catalog → 源串）。这样
        /// <see cref="WriteTemplate"/> 导出的模板可以直接当工作稿 ——
        /// 没填的行不会把界面变成空白。</para>
        /// </summary>
        private static int ParseExternalFile(string path, Dictionary<string, string> into)
        {
            var n = 0;
            try
            {
                var lines = System.IO.File.ReadAllLines(path, Encoding.UTF8);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line == null)
                        continue;
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                        continue;

                    var eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    var key = line.Substring(0, eq).Trim();
                    var value = Unescape(line.Substring(eq + 1).Trim());
                    if (key.Length == 0 || value.Length == 0)
                        continue;

                    // [上下文] 源串 —— 方括号必须在第一个 '=' 之前、且非空
                    string context = null;
                    if (key[0] == '[')
                    {
                        var close = key.IndexOf(']');
                        if (close > 1)
                        {
                            context = key.Substring(1, close - 1).Trim();
                            key = key.Substring(close + 1).Trim();
                            if (key.Length == 0)
                                continue;
                        }
                    }

                    into[Key(context, key)] = value;
                    n++;
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("uilocale-extfile-" + path,
                    "[UiLocale] 解析外部译文失败（" + System.IO.Path.GetFileName(path) + "）："
                    + e.GetType().Name);
            }
            return n;
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0)
                return s;
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    var c = s[++i];
                    if (c == 'n')
                        sb.Append('\n');
                    else if (c == 't')
                        sb.Append('\t');
                    else if (c == '\\')
                        sb.Append('\\');
                    else
                        sb.Append(c);
                    continue;
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // 键表（导出翻译模板）
        // ------------------------------------------------------------------

        /// <summary>本次会话里被问过的源串（排序后的快照）。</summary>
        public static string[] KnownKeys
        {
            get
            {
                var list = new List<string>(SeenKeys);
                list.Sort(StringComparer.Ordinal);
                return list.ToArray();
            }
        }

        /// <summary>
        /// 把键表写成一份**翻译模板**到外部目录（<c>lang/&lt;语言码&gt;.txt</c>）。
        /// 已存在时**不覆盖**（返回 null 并在报告里说明），避免抹掉玩家已经译好的内容。
        /// </summary>
        /// <param name="language">目标语言码（也是文件名）。</param>
        /// <param name="overwrite">true = 覆盖已存在的文件。</param>
        public static string WriteTemplate(string language, bool overwrite = false)
        {
            if (string.IsNullOrEmpty(language))
                return null;
            try
            {
                var dir = ExternalDirectory;
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, language + ".txt");
                if (System.IO.File.Exists(path) && !overwrite)
                    return null;

                var keys = KnownKeys;
                var sb = new StringBuilder(4096);
                sb.Append("# DSHCore 模组译文表\n");
                sb.Append("# 语言：").Append(language).Append('\n');
                sb.Append("# 格式：源串 = 译文      （'=' 左边是键，右边是译文；# 开头是注释；\\n 表示换行）\n");
                sb.Append("# 没译的行**留空**即可 —— 右边空着 = 这一条不生效，会退回 mod 自带的译文。\n");
                sb.Append("# 带 [上下文] 前缀的行是「游戏内容名」（关卡名 / 诅咒名 …），上下文不要改。\n");
                sb.Append("# 用法：把译文写在 '=' 右边。改完用游戏内指令 /dshtrans reload 重新读取，或重启游戏。\n");
                sb.Append("# 注意：这里**不会**凭空让游戏多出一种语言 —— \n");
                sb.Append("#       要使用某个语言，得先在「插件 → 资源包」里启用一份支持该语言的语言包。\n");
                sb.Append("# 提示：本文件是**追加**在 mod 自带译文之上的（同一条以本文件为准）。\n");
                sb.Append("#\n");
                sb.Append("# 括号里的中文是当前的显示文本，供参考。\n\n");

                for (var i = 0; i < keys.Length; i++)
                {
                    var key = keys[i];
                    string context, source;
                    SplitKey(key, out context, out source);
                    var current = In(source, language, context);
                    sb.Append("# ").Append(current == source ? "（无译文）" : current).Append('\n');
                    sb.Append(DescribeKey(key)).Append(" = \n\n");
                }

                System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                Plugin.Log?.LogInfo("[UiLocale] 已写出翻译模板：" + path + "（" + keys.Length + " 条）");
                return path;
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("uilocale-write", "[UiLocale] 写模板失败：" + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // 取词
        // ------------------------------------------------------------------

        /// <summary>游戏当前语言码（拿不到时按源语言 = 中文算）。</summary>
        public static string CurrentLanguage
        {
            get
            {
                try
                {
                    var main = UiGame.Main;
                    var lm = main == null ? null : main.LanguageManager;
                    if (lm != null)
                    {
                        var lang = lm.GetCurrentLanguage();
                        if (!string.IsNullOrEmpty(lang))
                            return lang;
                    }
                }
                catch (Exception) { }
                return SourceLanguage;
            }
        }

        /// <summary>
        /// 当前语言下的文案：①外部文件 → ②代码里 <see cref="Add"/> 的表
        /// → ③游戏本地化（mod 语言包 <c>.mo</c> 走这条）→ ④原样返回源串。
        /// </summary>
        public static string Get(string source)
        {
            return Get(source, null);
        }

        /// <summary>带上下文的取词（游戏内容名用；见 <see cref="Add"/> 的 <c>context</c>）。</summary>
        public static string Get(string source, string context)
        {
            if (string.IsNullOrEmpty(source))
                return source;
            EnsureProbed();
            Remember(context, source);

            var lang = CurrentLanguage;
            string t;
            if (TryTable(lang, source, context, true, out t))
                return t;

            if (TryGame(source, lang, context, out t))
                return t;

            return source;
        }

        /// <summary>指定语言取词（诊断 / 自测用）。</summary>
        public static string In(string source, string language)
        {
            return In(source, language, null);
        }

        /// <summary>指定语言 + 上下文取词（诊断 / 自测用）。</summary>
        public static string In(string source, string language, string context)
        {
            if (string.IsNullOrEmpty(source))
                return source;
            Remember(context, source);
            string t;
            if (TryTable(language, source, context, true, out t))
                return t;
            if (TryGame(source, language, context, out t))
                return t;
            return source;
        }

        /// <summary>换掉字符串里的占位符（其余格式串走的仍是游戏自己的逻辑）。</summary>
        public static string Format(string source, params object[] args)
        {
            var text = Get(source);
            if (args == null || args.Length == 0)
                return text;
            try { return string.Format(text, args); }
            catch (Exception) { return text; }
        }

        /// <summary>
        /// 逐个取词（对话框那一组按钮文案一趟搞定）。
        ///
        /// <para>给走**游戏原生**控件的调用点用：那些控件拿到什么就画什么，不会替我们取词，
        /// 所以必须自己先过一遍（<c>UiKit</c> 那几个克隆辅助函数已经内置了取词，
        /// 不需要再包一层）。</para>
        /// </summary>
        public static string[] GetAll(params string[] texts)
        {
            if (texts == null)
                return null;
            var result = new string[texts.Length];
            for (var i = 0; i < texts.Length; i++)
                result[i] = Get(texts[i]);
            return result;
        }

        /// <summary>
        /// 问游戏自己的本地化链（catalog provider 都在这里）。
        ///
        /// <para>用 <c>GetLocalizedString</c>（返回串）而不是 <c>TryGetLocalizedString</c>
        /// （多一个 <c>out</c> 参数）：前者是同一个入口、调用形状已经在 <c>Loadout/Ui/NameIndex</c>
        /// 里验证过；「查不到」的判据就是它把**源串原样**还回来。</para>
        ///
        /// <para>带 <paramref name="context"/> 时走 <c>GetLocalizedStringParticular</c> ——
        /// 游戏里「名字类」文本（关卡名 / 诅咒名 / 实体名…）都是这条链，
        /// 所以诊断报告里能看到语言包到底有没有盖住某条内容。</para>
        /// </summary>
        private static bool TryGame(string source, string language, out string translated)
        {
            return TryGame(source, language, null, out translated);
        }

        private static bool TryGame(string source, string language, string context, out string translated)
        {
            translated = null;
            try
            {
                var main = UiGame.Main;
                var lm = main == null ? null : main.LanguageManager;
                if (lm == null)
                    return false;

                var result = string.IsNullOrEmpty(context)
                    ? lm.GetLocalizedString(source, language, NoArgs)
                    : lm.GetLocalizedStringParticular(context, source, language, NoArgs);
                if (string.IsNullOrEmpty(result) || result == source)
                    return false;
                translated = result;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static readonly Il2CppSystem.Object[] NoArgs = new Il2CppSystem.Object[0];

        // ------------------------------------------------------------------
        // 接入游戏查找链
        // ------------------------------------------------------------------

        private static bool _applied;
        private static bool _probed;

        /// <summary>挂链自检的结论（给日志与 <see cref="Report"/> 用）。</summary>
        private static string _probeResult = "未跑（游戏还没初始化）";

        /// <summary>
        /// 把自己挂进游戏的文本查找链（幂等；本库的 <c>Plugin.Load()</c> 已经调过，
        /// 单独用本类的 mod 不用管）。挂不上只是「只有 <see cref="Get"/> 生效」，
        /// 不会报错，日志里会写清楚。
        ///
        /// <para>挂**两种**查找（各两个重载，共 4 处）：</para>
        /// <list type="number">
        /// <item><c>TryGetLocalizedString(text, language, out, args)</c> —— 普通界面文字（gettext 的无上下文条目）；</item>
        /// <item><c>TryGetLocalizedStringParticular(context, text, language, out, args)</c> ——
        /// <b>游戏内容名</b>。关卡名 / 诅咒名 / 诅咒说明 / 实体名 / 难度名…全部走这条
        /// （<c>LogicGameExt.GetCurseName</c> 等 → <c>GetTextParticular</c>）。
        /// 不挂这条的话，mod 的 XML 里的中文名字在英文下就永远是中文 ——
        /// 这是「mod 多语言」真正欠的那一半。</item>
        /// </list>
        ///
        /// <para>不带参数（<c>args</c> 为空）的查找才接管：带参数的串由官方 provider 自己
        /// <c>string.Format</c>（见 <see cref="TryGetLocalizedStringPrefix"/> 的不变量）。</para>
        /// </summary>
        public static void Apply()
        {
            if (_applied)
                return;
            _applied = true;

            var plain = new HarmonyMethod(AccessTools.Method(typeof(UiLocale),
                nameof(TryGetLocalizedStringPrefix)));
            var particular = new HarmonyMethod(AccessTools.Method(typeof(UiLocale),
                nameof(TryGetLocalizedStringParticularPrefix)));

            var patched = 0;
            patched += PatchOne(Find("TryGetLocalizedString", false, false), plain, "普通-原生重载");
            patched += PatchOne(Find("TryGetLocalizedString", false, true), plain, "普通-托管重载");
            patched += PatchOne(Find("TryGetLocalizedStringParticular", true, false), particular, "上下文-原生重载");
            patched += PatchOne(Find("TryGetLocalizedStringParticular", true, true), particular, "上下文-托管重载");

            if (patched == 0)
                Plugin.Log?.LogWarning("[UiLocale] 没能挂上 LanguageManager 的文本查找，"
                    + "mod 译文只在本库自己的界面里生效（UiLocale.Get）。");
            else
                Plugin.Log?.LogInfo("[UiLocale] 已挂进游戏文本查找链（补丁 " + patched + "/4 处）");
        }

        /// <summary>
        /// 找 <c>LanguageManager</c> 上的一个重载。
        /// </summary>
        /// <param name="particular">
        /// true = 找 <c>TryGetLocalizedStringParticular(context, text, language, out, args)</c>（多一个 context）。
        /// </param>
        /// <param name="managedArgs">
        /// 最后一个参数用托管数组 <c>Il2CppSystem.Object[]</c>（true）还是原生
        /// <c>Il2CppReferenceArray&lt;Il2CppSystem.Object&gt;</c>（false）——
        /// 同一个方法在互操作层有**两份**，两个都得挂，否则总有一半调用绕过了我们。
        /// </param>
        private static System.Reflection.MethodInfo Find(string name, bool particular, bool managedArgs)
        {
            var types = new List<Type>();
            if (particular)
                types.Add(typeof(string));               // context
            types.Add(typeof(string));                   // text
            types.Add(typeof(string));                   // language
            types.Add(typeof(string).MakeByRefType());   // translated
            types.Add(managedArgs
                ? typeof(Il2CppSystem.Object[])
                : typeof(Il2CppReferenceArray<Il2CppSystem.Object>));
            return AccessTools.Method(typeof(LanguageManager), name, types.ToArray());
        }

        private static int PatchOne(System.Reflection.MethodInfo target, HarmonyMethod prefix, string what)
        {
            if (target == null)
                return 0;
            try
            {
                new Harmony(HarmonyId).Patch(target, prefix: prefix);
                return 1;
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("uilocale-patch-" + what,
                    "[UiLocale] 补丁 " + what + " 失败：" + e.GetType().Name + ": " + e.Message);
                return 0;
            }
        }

        /// <summary>
        /// 查找前缀：命中我们的小表就不走原逻辑。
        ///
        /// <para>⚠ 不变量：**只处理不带参数的查找**。</para>
        /// </summary>
        private static bool TryGetLocalizedStringPrefix(string text, string language, ref string translated,
            Il2CppReferenceArray<Il2CppSystem.Object> args, ref bool __result)
        {
            try
            {
                if (args != null && args.Length > 0)
                    return true;    // 带参数的串交给官方逻辑（格式化在 provider 里做）
                string hit;
                if (!TryTable(language, text, out hit))
                    return true;
                translated = hit;
                __result = true;
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// 上下文查找前缀 —— 游戏内容名（关卡名 / 诅咒名与说明 / 实体名 / 难度名…）走这条。
        ///
        /// <para>与普通前缀同一套不变量（只接不带参数的查找），另外**严格匹配上下文**：
        /// 不允许退回不带上下文的条目，理由是游戏会对官方的每一条名字做这种查找，
        /// 而 mod 的界面文字里有一堆泛词（「音乐」「背景」…），放它们进来会弄脏官方内容。</para>
        /// </summary>
        private static bool TryGetLocalizedStringParticularPrefix(string context, string text, string language,
            ref string translated, Il2CppReferenceArray<Il2CppSystem.Object> args, ref bool __result)
        {
            try
            {
                if (args != null && args.Length > 0)
                    return true;
                if (string.IsNullOrEmpty(context) || string.IsNullOrEmpty(text))
                    return true;
                string hit;
                if (!TryTable(language, text, context, false, out hit))
                    return true;
                translated = hit;
                __result = true;
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// 自检：拿一个探针串走**游戏自己的入口**（<c>LanguageManager.GetLocalizedString</c>），
        /// 看前缀有没有真的挂上。只在第一次取词时跑一次（那时游戏已经初始化完）。
        /// </summary>
        private static void EnsureProbed()
        {
            if (_probed)
                return;
            try
            {
                var main = UiGame.Main;
                var lm = main == null ? null : main.LanguageManager;
                if (lm == null)
                    return;             // 游戏还没初始化完，下次再说
                _probed = true;

                // ① 普通查找（界面文字）
                Add(ProbeKey, ProbeKey + "=ok");
                var viaGame = lm.GetLocalizedString(ProbeKey, CurrentLanguage, NoArgs);
                var ok = viaGame == ProbeKey + "=ok";

                // ② 带上下文的查找（游戏内容名）—— 没挂上的话，mod 的关卡/诅咒名字
                //    在英文下永远是中文
                Add(ProbeKey + "Ctx", ProbeKey + "=ok", null, ProbeContext);
                var viaGameCtx = lm.GetLocalizedStringParticular(ProbeContext, ProbeKey + "Ctx", CurrentLanguage, NoArgs);
                var okCtx = viaGameCtx == ProbeKey + "=ok";

                _probeResult = ok && okCtx
                    ? "OK（普通 + 上下文两条链都读到了本库的译文）"
                    : (ok
                        ? "**半通**（普通链通、上下文链未生效 —— mod 内容名不会翻译）"
                        : "**未生效**（译文只在本库界面里可见）");
                if (!ok || !okCtx)
                    Plugin.Log?.LogWarning("[UiLocale] 挂链自检：" + _probeResult);
            }
            catch (Exception e)
            {
                _probed = true;
                _probeResult = "失败（" + e.GetType().Name + "）";
                UiLog.WarnOnce("uilocale-probe", "[UiLocale] 自检失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>报告（自测 / 排障用）。</summary>
        public static string Report()
        {
            var sb = new StringBuilder(300);
            sb.Append("本地化表：").Append(Languages.Length).Append(" 种语言，当前语言=")
              .Append(CurrentLanguage).Append('\n');
            for (var i = 0; i < Languages.Length; i++)
            {
                var lang = Languages[i];
                sb.Append("  ").Append(lang).Append(" : 代码表 ").Append(CountOf(lang)).Append(" 条")
                  .Append("  外部 ").Append(DescribeExternal(lang)).Append('\n');
            }
            var current = CurrentLanguage;
            if (CountOf(current) == 0 && DescribeExternal(current).StartsWith("无"))
                sb.Append("  （当前语言没有任何译文 —— mod 名会原样显示源串）\n");
            sb.Append("  带上下文的条目（游戏内容名）：");
            for (var i = 0; i < Languages.Length; i++)
            {
                if (i > 0)
                    sb.Append("，");
                sb.Append(Languages[i]).Append(" ").Append(ContextCountOf(Languages[i])).Append(" 条");
            }
            sb.Append('\n');
            sb.Append("  外部目录：").Append(ExternalDirectory).Append('\n');
            sb.Append("  本次会话见过的源串：").Append(SeenKeys.Count).Append(" 条")
              .Append("（导出模板：/dshtrans write <语言码>）\n");
            sb.Append("  示例 源串「MVZ2 Loadout」→ 当前语言「")
              .Append(In("MVZ2 Loadout", current)).Append("」，英文「")
              .Append(In("MVZ2 Loadout", English)).Append("」\n");
            sb.Append("  游戏查找链：").Append(_applied ? "已挂补丁" : "**未挂**")
              .Append("，自检=").Append(_probed ? "已跑" : "未跑（游戏还没初始化）")
              .Append(" → ").Append(_probeResult);
            return sb.ToString();
        }

        /// <summary>
        /// 键表 + 每个键在当前语言下的取值（<c>/dshtrans keys</c> 用）。
        /// </summary>
        public static string DumpKeys()
        {
            var keys = KnownKeys;
            var lang = CurrentLanguage;
            var sb = new StringBuilder(2048);
            sb.Append("源串（").Append(keys.Length).Append(" 条）→ 「").Append(lang).Append("」下的取值：\n");
            for (var i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                string context, source;
                SplitKey(key, out context, out source);
                sb.Append("  ").Append(DescribeKey(key)).Append('\n')
                  .Append("    = ").Append(In(source, lang, context)).Append('\n');
            }
            return sb.ToString();
        }
    }
}
