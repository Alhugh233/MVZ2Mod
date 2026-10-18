using System;
using System.Collections.Generic;
using System.Text;
using MVZ2.Managers;
using UnityEngine;

namespace Replay
{
    /// <summary>环境里的**一项**：游戏本体、一个内容 mod、一个 BepInEx 插件，或一条运行环境信息。</summary>
    public sealed class ReplayEnvEntry
    {
        /// <summary>
        /// 稳定标识。本体 = <c>mvz2</c>；内容 mod = 命名空间；插件 = 插件 GUID；运行环境 = <c>env:xxx</c>。
        /// </summary>
        public string Id;

        /// <summary>人读的名字（显示名 / 插件名）。</summary>
        public string Name;

        /// <summary>版本串。内容 mod 用它的关卡数据版本（见 <see cref="DataVersion"/>），插件用插件版本。</summary>
        public string Version;

        /// <summary>种类，见 <see cref="ReplayProtocol.ModBuiltin"/> 等。</summary>
        public string Kind;

        /// <summary>关卡存档数据版本（内容 mod 与本体有；其余 = -1）。</summary>
        public int DataVersion = -1;

        /// <summary>备注（人读，只写进文件供排障）。</summary>
        public string Note;

        /// <summary>参与环境哈希吗。纯展示项（例如「写文件的 mod 版本」）设为 false。</summary>
        public bool Hashable = true;

        public override string ToString()
        {
            var sb = new StringBuilder(48);
            sb.Append(Kind).Append('/').Append(Id).Append('@').Append(Version ?? "?");
            if (DataVersion >= 0)
                sb.Append("(dv ").Append(DataVersion).Append(')');
            if (!string.IsNullOrEmpty(Name) && Name != Id)
                sb.Append(' ').Append(Name);
            return sb.ToString();
        }
    }

    /// <summary>
    /// 一份「录制环境」的提供者。第三方 mod 可以注册自己的来源
    /// （例如把自己的内部版本号 / 内置数据版本写进回放，方便日后排障）。
    ///
    /// <para>⚠️ 在**录制开始时**被调一次，实现要快、不要抛异常。</para>
    /// </summary>
    public interface IReplayEnvSource
    {
        /// <summary>来源名（日志 / 排障用；同名的重复注册会被忽略）。</summary>
        string Name { get; }

        /// <summary>把本来源知道的条目追加到 <paramref name="into"/>。</summary>
        void Collect(List<ReplayEnvEntry> into);
    }

    /// <summary>
    /// 环境来源的注册表。内置三个（见 <see cref="EnsureBuiltinsLocked"/>）：内容 mod、插件、运行环境。
    /// </summary>
    public static class ReplayEnvSources
    {
        private static readonly List<IReplayEnvSource> sources = new List<IReplayEnvSource>();
        private static readonly object gate = new object();
        private static bool builtinsDone;

        /// <summary>注册一个环境来源。同名来源重复注册会被忽略。</summary>
        public static void Register(IReplayEnvSource source)
        {
            if (source == null || string.IsNullOrEmpty(source.Name))
                return;
            lock (gate)
            {
                EnsureBuiltinsLocked();
                for (int i = 0; i < sources.Count; i++)
                {
                    if (sources[i].Name == source.Name)
                        return;
                }
                sources.Add(source);
                // 按名字排序：同一套环境在不同机器上必须产出同样的顺序，环境哈希才稳定。
                sources.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            }
        }

        internal static IReplayEnvSource[] Snapshot()
        {
            lock (gate)
            {
                EnsureBuiltinsLocked();
                return sources.ToArray();
            }
        }

        private static void EnsureBuiltinsLocked()
        {
            if (builtinsDone)
                return;
            builtinsDone = true;
            sources.Add(new ContentModEnvSource());
            sources.Add(new PluginEnvSource());
            sources.Add(new RuntimeEnvSource());
            sources.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        #region 内置来源
        /// <summary>游戏本体 + 引擎 + 平台 + 时间轴单位。</summary>
        private sealed class RuntimeEnvSource : IReplayEnvSource
        {
            public string Name => "runtime";

            public void Collect(List<ReplayEnvEntry> into)
            {
                int bits = IntPtr.Size * 8;
                // ⚠️ 游戏版本用 `env:game` 这个**独立 id**，不要用 `mvz2`：
                // 内容 mod 来源里也有一条 id = `mvz2` 的条目，但它的 `ver` 是**关卡数据版本**
                // （协议 §4.2 的规定）。两者混用会让「游戏版本」显示成 "5" 这种数据版本号。
                into.Add(new ReplayEnvEntry
                {
                    Id = "env:game",
                    Name = "游戏版本",
                    Version = Safe(() => Application.version, "unknown"),
                    Kind = ReplayProtocol.ModRuntime,
                });
                into.Add(new ReplayEnvEntry
                {
                    Id = "env:unity",
                    Name = "Unity",
                    Version = Safe(() => Application.unityVersion, "unknown"),
                    Kind = ReplayProtocol.ModRuntime,
                });
                into.Add(new ReplayEnvEntry
                {
                    Id = "env:arch",
                    Name = "进程位数",
                    Version = bits == 32 ? "x86" : (bits == 64 ? "x64" : bits + "bit"),
                    Kind = ReplayProtocol.ModRuntime,
                });
                into.Add(new ReplayEnvEntry
                {
                    Id = "env:tps",
                    Name = "逻辑 tick/秒",
                    Version = "30",
                    Kind = ReplayProtocol.ModRuntime,
                });
                into.Add(new ReplayEnvEntry
                {
                    Id = "env:replay",
                    Name = "回放 mod",
                    Version = Plugin.VERSION,
                    Kind = ReplayProtocol.ModRuntime,
                    Note = "写文件的 mod 版本",
                    // 不参与哈希：写文件的 mod 自己换版本，不该让所有老回放都算「环境不同」。
                    Hashable = false,
                });
            }
        }

        /// <summary>
        /// 游戏内容 mod：走官方 <c>ModManager.GetAllModInfos()</c>。
        ///
        /// <para><b>为什么必须是这一份而不是别的：</b>关卡关键帧（<c>SerializableLevelController</c>）
        /// 的首行就带着「所有内容 mod 的 (命名空间, 关卡数据版本)」，
        /// 游戏读档时按命名空间逐个比对（<c>LevelDataIdentifierList.Compare</c>）。
        /// 回放要载入的正是同一份数据 —— 所以**这个列表就是「关键帧能不能载入」的判据**。</para>
        /// </summary>
        private sealed class ContentModEnvSource : IReplayEnvSource
        {
            public string Name => "content-mods";

            public void Collect(List<ReplayEnvEntry> into)
            {
                try
                {
                    var main = MainManager.Instance;
                    if (main == null || main.ModManager == null)
                        return;
                    var infos = main.ModManager.GetAllModInfos();
                    if (infos == null)
                        return;
                    for (int i = 0; i < infos.Length; i++)
                    {
                        var info = infos[i];
                        if (info == null)
                            continue;
                        var nsp = info.Namespace;
                        if (string.IsNullOrEmpty(nsp))
                            continue;
                        into.Add(new ReplayEnvEntry
                        {
                            Id = nsp,
                            Name = string.IsNullOrEmpty(info.DisplayName) ? nsp : info.DisplayName,
                            Version = info.LevelDataVersion.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
                            Kind = nsp == "mvz2" ? ReplayProtocol.ModBuiltin : ReplayProtocol.ModContent,
                            DataVersion = info.LevelDataVersion,
                        });
                    }
                }
                catch (Exception)
                {
                    // 拿不到就当作没有内容 mod（老版本游戏 / 调用时机太早）。
                }
            }
        }

        /// <summary>
        /// BepInEx 插件。
        ///
        /// <para>插件表只有 GUID / 名称 / 版本，但这些**同样影响仿真**：
        /// 任何改过逻辑的插件换版本，同一份回放就可能不复现。所以要记。
        /// 拿不到插件表（BepInEx API 变了）时直接放弃，不让它影响录制。</para>
        /// </summary>
        private sealed class PluginEnvSource : IReplayEnvSource
        {
            public string Name => "bepinex-plugins";

            public void Collect(List<ReplayEnvEntry> into)
            {
                try
                {
                    var chainloader = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance;
                    if (chainloader == null)
                        return;
                    var plugins = chainloader.Plugins;
                    if (plugins == null)
                        return;
                    var list = new List<ReplayEnvEntry>();
                    foreach (var kv in plugins)
                    {
                        var info = kv.Value;
                        if (info == null || info.Metadata == null)
                            continue;
                        var meta = info.Metadata;
                        if (string.IsNullOrEmpty(meta.GUID))
                            continue;
                        list.Add(new ReplayEnvEntry
                        {
                            Id = meta.GUID,
                            Name = meta.Name,
                            Version = PluginVersionOf(meta),
                            Kind = ReplayProtocol.ModPlugin,
                        });
                    }
                    // Dictionary 的枚举顺序不保证稳定 → 自己排一遍，哈希才是确定的。
                    list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                    into.AddRange(list);
                }
                catch (Exception)
                {
                }
            }

            /// <summary>
            /// 插件版本。
            ///
            /// <para><b>为什么走反射：</b><c>BepInPlugin.Version</c> 的类型是
            /// <c>SemanticVersioning.Version</c>（一个第三方包），直接写
            /// <c>meta.Version.ToString()</c> 会要求本工程引用 <c>SemanticVersioning.dll</c>。
            /// 为了读一个版本串去多引一个程序集不划算，而且那个包一旦换版本这里就编译不过 ——
            /// 用反射既不需要引用，也天然容忍它改成别的类型。</para>
            /// </summary>
            private static string PluginVersionOf(BepInEx.BepInPlugin meta)
            {
                try
                {
                    if (versionProp == null)
                        versionProp = meta.GetType().GetProperty("Version");
                    if (versionProp == null)
                        return "?";
                    var v = versionProp.GetValue(meta, null);
                    return v != null ? v.ToString() : "?";
                }
                catch (Exception)
                {
                    return "?";
                }
            }

            private static System.Reflection.PropertyInfo versionProp;
        }
        #endregion

        private static string Safe(Func<string> f, string fallback)
        {
            try
            {
                var v = f();
                return string.IsNullOrEmpty(v) ? fallback : v;
            }
            catch (Exception)
            {
                return fallback;
            }
        }
    }

    /// <summary>
    /// 一份**环境快照**：游戏 / 引擎 / 平台 + 全部已装 mod 的清单与哈希。
    ///
    /// <para>写进回放文件的 <c>env</c> 行（一行摘要 + 每个条目一行）；播放时重新采一份并比对，
    /// <b>只警告、不阻止播放</b>（既定策略：环境不同多半仍能播，硬拦反而挡住用户）。</para>
    /// </summary>
    public sealed class ReplayEnvironment
    {
        public readonly List<ReplayEnvEntry> Entries = new List<ReplayEnvEntry>();

        /// <summary>环境哈希：所有 <see cref="ReplayEnvEntry.Hashable"/> 条目的 <c>kind|id|ver</c> 排序后 FNV-1a。</summary>
        public string Hash { get; private set; } = "0000000000000000";

        /// <summary>参与哈希的条目数。</summary>
        public int HashCount { get; private set; }

        /// <summary>游戏版本（<c>Application.version</c>，就是标题界面显示的那个）。</summary>
        public string GameVersion = "unknown";

        /// <summary>引擎版本（<c>Application.unityVersion</c>）。</summary>
        public string UnityVersion = "unknown";

        /// <summary>平台（<c>x86</c> / <c>x64</c>）。</summary>
        public string Platform = "unknown";

        /// <summary>逻辑 tick/秒。协议固定 30；独立成字段是因为它决定时间轴换算。</summary>
        public int Tps = 30;

        /// <summary>采集当前环境。</summary>
        public static ReplayEnvironment Capture()
        {
            var env = new ReplayEnvironment();
            var srcs = ReplayEnvSources.Snapshot();
            for (int i = 0; i < srcs.Length; i++)
            {
                try
                {
                    srcs[i].Collect(env.Entries);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning("[Replay] 环境来源 " + srcs[i].Name + " 采集失败："
                        + e.GetType().Name);
                }
            }
            env.Adopt();
            return env;
        }

        /// <summary>从已解析的记录里重建（播放时用）。</summary>
        internal static ReplayEnvironment FromRecords(List<ReplayEnvEntry> entries,
            string gameVersion, string unityVersion, string platform, int tps)
        {
            var env = new ReplayEnvironment();
            if (entries != null)
                env.Entries.AddRange(entries);
            env.GameVersion = Coalesce(gameVersion, "unknown");
            env.UnityVersion = Coalesce(unityVersion, "unknown");
            env.Platform = Coalesce(platform, "unknown");
            env.Tps = tps > 0 ? tps : 30;
            env.Adopt();
            return env;
        }

        private static string Coalesce(string v, string fallback)
        {
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        private void Adopt()
        {
            // 从条目里补全展示用字段（来源没产出条目时保留默认值）。
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                switch (e.Id)
                {
                    case "env:game":
                        if (!string.IsNullOrEmpty(e.Version))
                            GameVersion = e.Version;
                        break;
                    case "env:unity":
                        UnityVersion = e.Version;
                        break;
                    case "env:arch":
                        Platform = e.Version;
                        break;
                    case "env:tps":
                        if (int.TryParse(e.Version, out var tps) && tps > 0)
                            Tps = tps;
                        break;
                }
            }
            BuildHash();
        }

        private void BuildHash()
        {
            var lines = new List<string>();
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                if (!e.Hashable)
                    continue;
                lines.Add((e.Kind ?? "?") + "|" + (e.Id ?? "?") + "|" + (e.Version ?? "?"));
            }
            lines.Sort(StringComparer.Ordinal);
            HashCount = lines.Count;
            Hash = Fnv1aSortedLines(lines);
        }

        /// <summary>对「已排序的行」做 FNV-1a（环境哈希与指纹都用同一套，便于跨工具比对）。</summary>
        internal static string Fnv1aSortedLines(List<string> lines)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < lines.Count; i++)
            {
                var s = lines[i];
                for (int c = 0; c < s.Length; c++)
                {
                    h ^= s[c];
                    h *= 1099511628211UL;
                }
                h ^= (ulong)'\n';
                h *= 1099511628211UL;
            }
            return Json.Hex(h);
        }

        /// <summary>只列出**内容 mod / 本体**（参与关卡存档兼容判定的那些）。</summary>
        public List<ReplayEnvEntry> ContentMods()
        {
            var list = new List<ReplayEnvEntry>();
            for (int i = 0; i < Entries.Count; i++)
            {
                var k = Entries[i].Kind;
                if (k == ReplayProtocol.ModContent || k == ReplayProtocol.ModBuiltin)
                    list.Add(Entries[i]);
            }
            return list;
        }

        /// <summary>一行摘要（写日志 / 播放小结用）。</summary>
        public string Describe()
        {
            int content = 0, plugin = 0;
            for (int i = 0; i < Entries.Count; i++)
            {
                var k = Entries[i].Kind;
                if (k == ReplayProtocol.ModContent) content++;
                else if (k == ReplayProtocol.ModPlugin) plugin++;
            }
            return "game=" + GameVersion + " unity=" + UnityVersion + " " + Platform
                + " 内容mod=" + content + " 插件=" + plugin + " 环境哈希=" + Hash;
        }

        /// <summary>
        /// 和另一份环境比对，返回**人读**的差异清单（空列表 = 一致）。
        /// 顺序固定（先本体、再内容 mod、再插件），同一对环境的报告总是同一份。
        /// </summary>
        public List<string> Diff(ReplayEnvironment other)
        {
            var result = new List<string>();
            if (other == null)
                return result;

            if (!string.Equals(Hash, other.Hash, StringComparison.Ordinal))
                result.Add("环境哈希不同：" + Hash + "（录制）→ " + other.Hash + "（当前）");
            if (!string.Equals(GameVersion, other.GameVersion, StringComparison.Ordinal))
                result.Add("游戏版本不同：" + GameVersion + "（录制）→ " + other.GameVersion + "（当前）");
            if (!string.Equals(UnityVersion, other.UnityVersion, StringComparison.Ordinal))
                result.Add("引擎版本不同：" + UnityVersion + "（录制）→ " + other.UnityVersion + "（当前）");
            if (!string.Equals(Platform, other.Platform, StringComparison.Ordinal))
                result.Add("平台位数不同：" + Platform + "（录制）→ " + other.Platform + "（当前）");

            DiffKind(other, ReplayProtocol.ModBuiltin, "游戏本体", result);
            DiffKind(other, ReplayProtocol.ModContent, "内容 mod", result);
            DiffKind(other, ReplayProtocol.ModPlugin, "插件", result);
            return result;
        }

        private void DiffKind(ReplayEnvironment other, string kind, string label, List<string> result)
        {
            var mine = Index(kind);
            var theirs = other.Index(kind);
            var keys = new List<string>(mine.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                var key = keys[i];
                if (!theirs.TryGetValue(key, out var b))
                {
                    result.Add(label + "缺失：" + key + "@" + mine[key].Version + "（录制时装了，现在没有）");
                }
                else if (!string.Equals(mine[key].Version, b.Version, StringComparison.Ordinal))
                {
                    result.Add(label + "版本不同：" + key + " " + mine[key].Version
                        + "（录制）→ " + b.Version + "（当前）");
                }
            }
            var otherKeys = new List<string>(theirs.Keys);
            otherKeys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < otherKeys.Count; i++)
            {
                if (!mine.ContainsKey(otherKeys[i]))
                    result.Add(label + "多出：" + otherKeys[i] + "@" + theirs[otherKeys[i]].Version
                        + "（录制时没有，现在装了）");
            }
        }

        private Dictionary<string, ReplayEnvEntry> Index(string kind)
        {
            var map = new Dictionary<string, ReplayEnvEntry>(StringComparer.Ordinal);
            for (int i = 0; i < Entries.Count; i++)
            {
                var e = Entries[i];
                if (e.Kind != kind || string.IsNullOrEmpty(e.Id))
                    continue;
                map[e.Id] = e;
            }
            return map;
        }

        /// <summary>
        /// 内容 mod 的「关卡数据版本」差异。
        ///
        /// <para>这一类会**直接导致关键帧载不进去**（游戏原版的比对就是按 (命名空间, 数据版本) 做的），
        /// 所以在播放小结里单独点名，而不是混在普通环境差异里。</para>
        /// </summary>
        public List<string> ContentModVersionMismatches(ReplayEnvironment other)
        {
            var result = new List<string>();
            if (other == null)
                return result;
            var mine = Index(ReplayProtocol.ModContent);
            var theirs = other.Index(ReplayProtocol.ModContent);
            var keys = new List<string>(mine.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int i = 0; i < keys.Count; i++)
            {
                if (!theirs.TryGetValue(keys[i], out var b))
                    continue;
                var a = mine[keys[i]];
                if (!string.Equals(a.Version, b.Version, StringComparison.Ordinal))
                {
                    result.Add(a.Id + " 关卡数据版本 " + a.Version + "（录制）→ " + b.Version + "（当前）");
                }
            }
            return result;
        }
    }
}
