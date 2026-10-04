using System;
using System.Collections.Generic;
using MVZ2.Managers;
using PVZEngine;

namespace Loadout
{
    /// <summary>需要「名称 ↔ id」互查的内容类别。</summary>
    internal enum NameKind
    {
        Area,
        Stage,
        Difficulty,
        Music,

        /// <summary>解锁条件（关卡 / 商店 / 成就解锁条件）。原版没有名称，只能按 id 匹配。</summary>
        Unlock,
    }

    /// <summary>
    /// 一类内容的**名称索引**：每个条目同时带 id、当前语言名、英文名。
    ///
    /// <para>为什么需要它：面板里的「&lt;&gt;」格子已经改成**输入框 + 补齐**
    /// （原版的下拉框组件在本环境画不出来），而用户敲进去的可能是</para>
    /// <list type="bullet">
    /// <item><c>mvz2:day</c>（完整 id）/ <c>day</c>（裸路径）</item>
    /// <item><c>序章</c>（中文名）/ <c>Prologue</c>（英文名）</item>
    /// </list>
    /// <para>三种都要能认，所以索引里三份都存，匹配时三份都试。</para>
    ///
    /// <para><b>英文名怎么来的：</b>游戏的语言包按 gettext 组织 —— PO 文件里的 msgid
    /// 就是**源语言（zh-Hans）的文本**。所以
    /// <c>LanguageManager.GetLocalizedStringParticular(context, 中文名, "en-US")</c>
    /// 就是英文名。游戏跑在中文（源语言，默认）时这条恒成立；跑在英文时英文名与中文名会相同 ——
    /// 此时「按 id 匹配」与「按当前语言名匹配」仍然可用。</para>
    ///
    /// <para><b>背景（Area）没有名字</b>：原版 <c>AreaMeta</c> / <c>MapMeta</c> 里根本没有 name 字段，
    /// 也没有任何本地化访问器，所以背景只有手写章节表（见 <see cref="NameResolver.Area"/>），
    /// 英文名一律拿不到。这不是漏做，是原版数据里就没有。</para>
    /// </summary>
    internal sealed class NameIndex
    {
        internal sealed class Entry
        {
            public NamespaceID Id;
            public string IdText;    // mvz2:day
            public string Path;      // day（或 level.mausoleum_2）
            public string Name;      // 当前语言名
            public string En;        // 英文名（拿不到时为 null）
            public string Display;   // 输入框/候选里显示的文本

            /// <summary>用于匹配的全部候选串。</summary>
            public string[] Keys;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly Dictionary<string, Entry> _byId = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly string _kindName;

        private NameIndex(string kindName)
        {
            _kindName = kindName;
        }

        public string KindName { get { return _kindName; } }

        public int Count { get { return _entries.Count; } }

        public List<Entry> Entries { get { return _entries; } }

        /// <summary>
        /// 输入框里该显示的文本：**只用名字**（没名字时用 id）。
        ///
        /// <para>为什么不用 <see cref="Entry.Display"/>：那一份是「名字  (id)」，长度是名字的两倍多，
        /// 而选值格是按 4 格一行排的（每格约 163 设计单位）—— 实测「序章  (mvz2:prologue)」需要 199，
        /// 放进去就会被裁。id 仍在候选列表里完整显示，提交时也仍然能按 id / 中文名 / 英文名匹配。</para>
        /// </summary>
        public static string BoxText(Entry entry)
        {
            if (entry == null)
                return string.Empty;
            return string.IsNullOrEmpty(entry.Name) ? (entry.IdText ?? string.Empty) : entry.Name;
        }

        // ------------------------------------------------------------------
        // 构建
        // ------------------------------------------------------------------

        public static NameIndex Build(NameKind kind)
        {
            var index = new NameIndex(kind.ToString());
            List<NamespaceID> ids;
            string context;
            Func<NamespaceID, string> nameOf;

            switch (kind)
            {
                case NameKind.Stage:
                    ids = LevelFacade.ListStages();
                    context = "levelname";
                    nameOf = NameResolver.Stage;
                    break;
                case NameKind.Difficulty:
                    ids = LevelFacade.ListDifficulties();
                    context = "difficulty";
                    nameOf = NameResolver.Difficulty;
                    break;
                case NameKind.Music:
                    ids = LevelFacade.ListMusics();
                    context = "music.name";
                    nameOf = NameResolver.Music;
                    break;
                case NameKind.Unlock:
                    // 解锁条件没有名字（也没有本地化上下文），只能按 id 匹配。
                    // 候选来源是**定义侧**（ResourceManager.unlockConditionList），
                    // 不是用户存档侧 —— 后者在标题界面可能还没加载完。
                    ids = ProgressOps.GetAllUnlockConditions();
                    context = null;
                    nameOf = null;
                    break;
                default:
                    ids = LevelFacade.ListAreas();
                    context = null;                 // 背景没有本地化上下文
                    nameOf = NameResolver.Area;
                    break;
            }

            if (ids == null)
                return index;

            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                if (id == null)
                    continue;
                var idText = id.ToString();
                if (index._byId.ContainsKey(idText))
                    continue;

                string name = null, path = null;
                try { name = nameOf == null ? null : nameOf(id); }
                catch (Exception) { }
                try { path = id.Path; }
                catch (Exception) { }

                if (string.IsNullOrEmpty(name))
                    name = idText;

                var en = context == null ? null : Localize(context, name, LangEn);
                var display = name == idText ? idText : name + "  (" + idText + ")";

                var entry = new Entry
                {
                    Id = id,
                    IdText = idText,
                    Path = path,
                    Name = name,
                    En = string.IsNullOrEmpty(en) ? null : en,
                    Display = display,
                };
                entry.Keys = BuildKeys(entry);
                index._entries.Add(entry);
                index._byId[idText] = entry;
            }

            index._entries.Sort(CompareEntry);
            return index;
        }

        private static string[] BuildKeys(Entry e)
        {
            var keys = new List<string>(6);
            AddKey(keys, e.IdText);
            AddKey(keys, e.Path);
            AddKey(keys, e.Name);
            AddKey(keys, e.En);
            AddKey(keys, e.Display);
            return keys.ToArray();
        }

        private static void AddKey(List<string> keys, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            if (keys.Contains(value))
                return;
            keys.Add(value);
        }

        private static int CompareEntry(Entry a, Entry b)
        {
            var c = string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            if (c != 0)
                return c;
            return string.Compare(a.IdText, b.IdText, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // 查询
        // ------------------------------------------------------------------

        /// <summary>精确查（id / 裸路径 / 当前语言名 / 英文名 / 显示文本）。</summary>
        public Entry Find(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            var q = text.Trim();
            if (q.Length == 0)
                return null;

            Entry hit;
            if (_byId.TryGetValue(q, out hit))
                return hit;

            for (var i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (Eq(e.IdText, q) || Eq(e.Path, q) || Eq(e.Name, q) || Eq(e.En, q) || Eq(e.Display, q))
                    return e;
            }

            // 允许 "mvz2:day" 写成 "day"（喂给 NamespaceID 解析再回查）
            var parsed = ParseId(q);
            if (parsed != null)
            {
                if (_byId.TryGetValue(parsed.ToString(), out hit))
                    return hit;
            }
            return null;
        }

        /// <summary>
        /// 模糊匹配：**前缀优先、包含兜底**，一律大小写不敏感。
        /// 空查询返回全部（调用方自己分页）。
        /// </summary>
        public List<Entry> Match(string text, int limit)
        {
            var result = new List<Entry>();
            var q = (text ?? string.Empty).Trim();
            if (q.Length == 0)
            {
                var n = limit <= 0 ? _entries.Count : Math.Min(limit, _entries.Count);
                for (var i = 0; i < n; i++)
                    result.Add(_entries[i]);
                return result;
            }

            var scored = new List<KeyValuePair<int, Entry>>();
            for (var i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                var rank = Rank(e, q);
                if (rank >= 0)
                    scored.Add(new KeyValuePair<int, Entry>(rank, e));
            }
            scored.Sort(CompareScored);
            var total = limit <= 0 ? scored.Count : Math.Min(limit, scored.Count);
            for (var i = 0; i < total; i++)
                result.Add(scored[i].Value);
            return result;
        }

        private static int CompareScored(KeyValuePair<int, Entry> a, KeyValuePair<int, Entry> b)
        {
            if (a.Key != b.Key)
                return a.Key - b.Key;
            return CompareEntry(a.Value, b.Value);
        }

        private static int Rank(Entry e, string q)
        {
            var best = -1;
            var keys = e.Keys;
            for (var i = 0; i < keys.Length; i++)
            {
                var r = RankOne(keys[i], q);
                if (r >= 0 && (best < 0 || r < best))
                    best = r;
                if (best == 0)
                    return 0;
            }
            return best;
        }

        private static int RankOne(string hay, string q)
        {
            if (string.IsNullOrEmpty(hay))
                return -1;
            if (hay.Equals(q, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (hay.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                return 1;
            if (hay.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return 2;
            return -1;
        }

        public string DisplayOf(NamespaceID id)
        {
            Entry e;
            if (id == null || !_byId.TryGetValue(id.ToString(), out e))
                return id == null ? "（无）" : id.ToString();
            return e.Display;
        }

        // ------------------------------------------------------------------
        // 工具
        // ------------------------------------------------------------------

        public const string LangZh = "zh-Hans";
        public const string LangEn = "en-US";

        private static bool Eq(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && a.Equals(b, StringComparison.OrdinalIgnoreCase);
        }

        private static NamespaceID ParseId(string text)
        {
            try
            {
                var main = LevelFacade.Main;
                if (main == null)
                    return null;
                NamespaceID parsed;
                if (NamespaceID.TryParse(text, main.BuiltinNamespace, out parsed) && parsed != null)
                    return parsed;
            }
            catch (Exception) { }
            return null;
        }

        private static readonly Il2CppSystem.Object[] NoArgs = new Il2CppSystem.Object[0];

        /// <summary>按**指定语言**取名（gettext 的 msgid = 源语言文本，见类型注释）。</summary>
        private static string Localize(string context, string key, string language)
        {
            if (string.IsNullOrEmpty(context) || string.IsNullOrEmpty(key))
                return null;
            try
            {
                var main = LevelFacade.Main;
                if (main == null || main.LanguageManager == null)
                    return null;
                var text = main.LanguageManager.GetLocalizedStringParticular(context, key, language, NoArgs);
                if (string.IsNullOrEmpty(text) || text == key)
                    return null;
                return text;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>各类别索引的缓存（按语言缓存，切语言后自动重建）。</summary>
    internal static class NameIndexes
    {
        private static readonly Dictionary<NameKind, NameIndex> Cache = new Dictionary<NameKind, NameIndex>();
        private static string _language;

        public static NameIndex Get(NameKind kind)
        {
            CheckLanguage();
            NameIndex index;
            if (Cache.TryGetValue(kind, out index) && index.Count > 0)
                return index;
            var wasEmpty = index != null;
            index = NameIndex.Build(kind);
            if (index.Count == 0)
            {
                // **空结果不入缓存**：解锁条件来自资源管理器，而资源在标题界面可能还没加载完 ——
                // 一旦把 0 项的结果缓存住，进关后再也不会重建（实测就是这样一直是 0 项）。
                Cache.Remove(kind);
                if (!wasEmpty)
                    Plugin.Log?.LogInfo("[Name] 索引 " + kind + " 暂时为空（资源可能还没加载完），稍后重试");
                return index;
            }
            Cache[kind] = index;
            Plugin.Log?.LogInfo("[Name] 建立索引 " + kind + "：" + index.Count + " 项");
            return index;
        }

        public static void Invalidate()
        {
            Cache.Clear();
            NameResolver.ClearCache();
            _language = null;
        }

        private static void CheckLanguage()
        {
            try
            {
                var main = LevelFacade.Main;
                if (main == null || main.LanguageManager == null)
                    return;
                var lang = main.LanguageManager.GetCurrentLanguage();
                if (lang == _language)
                    return;
                _language = lang;
                Cache.Clear();
            }
            catch (Exception) { }
        }

        public static string Report()
        {
            var sb = new System.Text.StringBuilder(240);
            foreach (NameKind kind in Enum.GetValues(typeof(NameKind)))
            {
                NameIndex index;
                sb.Append("  ").Append(kind.ToString().PadRight(11));
                if (Cache.TryGetValue(kind, out index))
                {
                    sb.Append(index.Count).Append(" 项  例：");
                    if (index.Count > 0)
                        sb.Append(index.Entries[0].Display);
                }
                else
                {
                    sb.Append("（未建立）");
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
