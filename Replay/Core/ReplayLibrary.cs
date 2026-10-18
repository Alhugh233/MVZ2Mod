using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using MVZ2.Managers;
using PVZEngine;

namespace Replay
{
    /// <summary>回放列表里那个名字的着色档位（黄/白/红）。</summary>
    internal enum ReplayRisk
    {
        /// <summary>没什么要说的 —— 白。</summary>
        Ok = 0,
        /// <summary>**可能**播不好 —— 黄。</summary>
        Suspect = 1,
        /// <summary>上次播放确实出过问题 —— 红。</summary>
        Bad = 2,
    }

    /// <summary>
    /// 一条回放的**只读元信息**（回放列表页需要的全部字段）。
    ///
    /// <para>刻意不装整个 <see cref="ReplayFile"/>：一个回放动辄一万多条事件、
    /// 关键帧是十几万字符的 BSON，列表里几十条全解析一遍要几十兆内存和明显的卡顿。
    /// 这里只读**第一行 header**（<c>.mrep</c> 也只需解开 gzip 的头一段），
    /// 而且大多数时候连 header 都不用读 —— 见 <see cref="ReplayIndex"/>。</para>
    /// </summary>
    internal sealed class ReplayInfo
    {
        /// <summary>裸名字（不含扩展名），也是删除时按名字定位的键。</summary>
        public string Name;
        /// <summary>实际读取的那个文件（优先 <c>.mrep.json</c>，没有就是 <c>.mrep</c>）。</summary>
        public string Path;
        public string Stage;
        public string Area;
        public string Difficulty;
        /// <summary>**录制开始**时刻（header 里的 <c>created</c>）。仅供详情查看。</summary>
        public DateTime Created = DateTime.MinValue;
        /// <summary>
        /// **添加进回放库**的时刻（文件创建时间）。
        ///
        /// <para>列表里显示的「时间」就是它 —— 玩家关心的是「这条什么时候到我这里的」，
        /// 而不是录制者按下开始的那一刻（导入进来的回放两者能差好几天）。</para>
        /// </summary>
        public DateTime AddedAt = DateTime.MinValue;
        public int StartTick;
        public int LastTick;
        public int Tps = 30;
        public long SizeBytes;
        public string Error;

        // ---- 健康度（存在目录索引里，见 ReplayIndex）----

        /// <summary>播放器上次留下的结果。</summary>
        public ReplayHealth Health = ReplayHealth.Ok;

        /// <summary>上次播放结束时累计的不一致条数。</summary>
        public int LastMismatch;

        /// <summary>上次播放中第一次出现不一致的 tick（0 = 没记录）。</summary>
        public int LastBadTick;

        // ---- 协议（v10 起从 header 读出来，供回放库列表显示）----

        /// <summary>文件格式版本（header 的 <c>ver</c>）。</summary>
        public int Version;

        /// <summary>规范版本（header 的 <c>spec</c>）。</summary>
        public int SpecVersion;

        /// <summary>是不是**调试模式**文件（header 的 <c>debug</c>）。</summary>
        public bool Debug;

        /// <summary>录制时的游戏版本。</summary>
        public string GameVersion;

        /// <summary>录制时的环境哈希。空 = 文件没有环境记录（旧文件）。</summary>
        public string EnvHash;

        /// <summary>文件里记录了多少个环境条目。</summary>
        public int EnvCount;

        /// <summary>
        /// 「录制时的环境」与「当前环境」是否一致。
        ///
        /// <para>`null` = 无法判断（文件没有环境记录，或者游戏还没初始化完）。
        /// 它在列表里**只是提示**：环境不同仍然可以播（协议 §4.4），
        /// 但玩家应当知道「这一局多半不会完全复现」。</para>
        ///
        /// <para><b>结果会缓存</b>：列表每行都会读它，而它要拿当前环境哈希
        /// （即使那个哈希本身也缓存了，50 行的字符串比较也不少）。
        /// ⚠️ **“无法判断”不缓存** —— 游戏初始化完成后就有答案了。</para>
        /// </summary>
        public bool? EnvMatches
        {
            get
            {
                if (hasEnvMatches)
                    return cachedEnvMatches;
                if (string.IsNullOrEmpty(EnvHash))
                    return null;
                var now = envHashNow ?? ReplayLibrary.CurrentEnvHash();
                if (string.IsNullOrEmpty(now))
                    return null;   // 不缓存：游戏还没初始化完
                cachedEnvMatches = string.Equals(EnvHash, now, StringComparison.Ordinal);
                hasEnvMatches = true;
                return cachedEnvMatches;
            }
        }

        private bool hasEnvMatches;
        private bool cachedEnvMatches;

        /// <summary>
        /// 上次播放结束时记下的「在哪个环境哈希下完整验证通过」（空 = 没验证过）。
        /// 见 <see cref="ReplayIndexEntry.VerifiedEnv"/>。
        /// </summary>
        public string VerifiedEnv;

        /// <summary>
        /// 「**在当前这个环境里**已经完整播完一次、且一条指纹都没错」。
        ///
        /// <para>它比 <see cref="EnvMatches"/> 更强：后者只是比字符串，
        /// 而这条是实测结果。所以 <see cref="Risk"/> 里它排在静态推断**前面**。</para>
        /// </summary>
        public bool VerifiedNow
        {
            get
            {
                if (string.IsNullOrEmpty(VerifiedEnv))
                    return false;
                // "-" = 验证过，但当时拿不到环境哈希（游戏还没初始化完）。
                // 那种情况下更该信实测，而不是继续黄着。
                if (VerifiedEnv == VerifiedEnvUnknown)
                    return true;
                var now = envHashNow ?? ReplayLibrary.CurrentEnvHash();
                if (string.IsNullOrEmpty(now))
                    return true;   // 现在也拿不到 → 信实测
                return string.Equals(VerifiedEnv, now, StringComparison.Ordinal);
            }
        }

        /// <summary><see cref="VerifiedEnv"/> 的「不知道环境」哨兵值。</summary>
        internal const string VerifiedEnvUnknown = "-";

        /// <summary>
        /// 这次列表刷新时采集到的「当前环境哈希 / 游戏版本」。
        ///
        /// <para>由 <see cref="ReplayLibrary.LoadAll"/> **一次采集、注入给每一条** ——
        /// 否则 50 条会各自去问一遍 <c>ModManager</c> 与 <c>Application.version</c>，
        /// 而那是整个列表里最贵的两项。</para>
        /// </summary>
        private string envHashNow;
        private string gameNow;

        internal void SetContext(string envHash, string gameVersion)
        {
            envHashNow = envHash;
            gameNow = gameVersion;
        }

        /// <summary>一行「兼容性」提示（列表里显示；空串 = 没什么要说的）。</summary>
        public string CompatText
        {
            get
            {
                if (IsBroken)
                    return Error;
                var em = EnvMatches;
                // 实测已经在这个环境里完整验证过了 —— 就别再挂「环境不同」那句，
                // （那句会让人以为这条回放有问题，而实际上它刚被完整核对过。）
                if (em == false && !VerifiedNow)
                    return DSHCore.Ui.UiLocale.Get("录制环境与当前不同");
                if (Debug)
                    return DSHCore.Ui.UiLocale.Get("调试回放");
                return string.Empty;
            }
        }

        public bool IsBroken { get { return !string.IsNullOrEmpty(Error); } }

        // ---- 风险档位（列表名字的颜色、播放前的弹窗）----

        /// <summary>
        /// 这条回放的风险档位。
        ///
        /// <para><b>红</b> = 播放器上次明确发现过不一致；
        /// <b>黄</b> = 「有可能播不好」：读不出来 / 录制环境与当前不同 / 文件没写环境记录 /
        /// 录制时的游戏版本与当前不同 / 不是正式版本收录的（缺 <c>spec</c>）/ 是调试文件。
        /// 其余为白。</para>
        /// </summary>
        public ReplayRisk Risk
        {
            get
            {
                if (Health == ReplayHealth.Bad)
                    return ReplayRisk.Bad;
                if (Health == ReplayHealth.Suspect)
                    return ReplayRisk.Suspect;
                // ⭐ 实测优先：在**当前这个环境**里已经完整播完、指纹全对，
                // 那么下面那些静态推断（环境不同 / 没环境记录 / 版本不同）都该让位。
                // 否则一条已经验证过的回放会永远黄着。
                if (VerifiedNow)
                    return ReplayRisk.Ok;
                if (IsBroken)
                    return ReplayRisk.Suspect;
                if (EnvMatches == false)
                    return ReplayRisk.Suspect;
                if (EnvCount <= 0)
                    return ReplayRisk.Suspect;
                if (SpecVersion <= 0)
                    return ReplayRisk.Suspect;
                if (GameVersionMismatch)
                    return ReplayRisk.Suspect;
                return ReplayRisk.Ok;
            }
        }

        /// <summary>录制时的游戏版本与当前不同（拿不到当前版本时算「不同」）？</summary>
        public bool GameVersionMismatch
        {
            get
            {
                if (string.IsNullOrEmpty(GameVersion))
                    return true;
                var now = gameNow ?? ReplayLibrary.CurrentGameVersion();
                return string.IsNullOrEmpty(now)
                    || !string.Equals(GameVersion, now, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// 黄标时逐条列出「可能出问题的地方」（给播放前的弹窗用）。
        /// 红标不靠它（那时要说的是「上次出问题的时间」）。
        /// </summary>
        public string SuspectReasons
        {
            get
            {
                var sb = new StringBuilder(160);
                if (IsBroken)
                    sb.Append('\n').Append(DSHCore.Ui.UiLocale.Get("• 文件读不出来：")).Append(Error);
                if (EnvMatches == false)
                    sb.Append('\n').Append(DSHCore.Ui.UiLocale.Get("• 录制时的内容与现在不同（关卡数据、mod 或插件）。"));
                if (EnvCount <= 0)
                    sb.Append('\n').Append(DSHCore.Ui.UiLocale.Get("• 文件里没有环境记录（旧版本录的），无法判断能不能复现。"));
                if (SpecVersion <= 0)
                    sb.Append('\n').Append(DSHCore.Ui.UiLocale.Get("• 不是本 mod 正式录出的文件（没有规范版本号）。"));
                if (GameVersionMismatch && !string.IsNullOrEmpty(GameVersion))
                    sb.Append('\n').Append(DSHCore.Ui.UiLocale.Get("• 录制于 "))
                      .Append(GameVersion)
                      .Append(DSHCore.Ui.UiLocale.Get("，与当前游戏版本不同。"));
                if (Debug)
                    sb.Append('\n').Append(DSHCore.Ui.UiLocale.Get("• 这是调试模式录的文件。"));
                return sb.ToString();
            }
        }

        /// <summary>时长（秒）= (结束 tick - 起始 tick) / 30。</summary>
        public float DurationSeconds
        {
            get
            {
                var tps = Tps > 0 ? Tps : 30;
                var ticks = LastTick - StartTick;
                return ticks > 0 ? ticks / (float)tps : 0f;
            }
        }

        /// <summary>时长文案，形如 <c>12:34</c> 或 <c>1:02:03</c>。</summary>
        public string DurationText
        {
            get
            {
                var total = (int)Math.Round(DurationSeconds);
                if (total < 0)
                    total = 0;
                var h = total / 3600;
                var m = (total % 3600) / 60;
                var s = total % 60;
                return h > 0
                    ? h.ToString(CultureInfo.InvariantCulture) + ":"
                      + m.ToString("00", CultureInfo.InvariantCulture) + ":"
                      + s.ToString("00", CultureInfo.InvariantCulture)
                    : m.ToString(CultureInfo.InvariantCulture) + ":"
                      + s.ToString("00", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// 列表里显示的「时间」——**添加进库的时间**。
        ///
        /// <para>当年的存档不写年份：行里横向要摆好几个单元格，时间列宽是硬约束。</para>
        /// </summary>
        public string CreatedText
        {
            get
            {
                var t = AddedAt != DateTime.MinValue ? AddedAt : Created;
                if (t == DateTime.MinValue)
                    return DSHCore.Ui.UiLocale.Get("时间未知");
                return t.Year == DateTime.Now.Year
                    ? t.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>录制时刻的完整文案（只在详情里显示）。</summary>
        public string RecordedText
        {
            get
            {
                if (Created == DateTime.MinValue)
                    return DSHCore.Ui.UiLocale.Get("时间未知");
                return Created.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>添加时刻的完整文案（只在详情里显示）。</summary>
        public string AddedText
        {
            get
            {
                if (AddedAt == DateTime.MinValue)
                    return DSHCore.Ui.UiLocale.Get("时间未知");
                return AddedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// 列表里显示在左侧的**名字**。
        ///
        /// <para><b>玩家改过名的用他给的名字，没改过的用关卡名。</b>
        /// 理由：自动名（<c>&lt;关卡&gt;_yyyyMMdd_HHmmss</c>）对玩家毫无意义，
        /// 看关卡名才知道这条是什么；而玩家一旦自己起了名，那个名字就是他要的信息 ——
        /// 以前不管改没改都显示关卡名，导致重命名之后玩家**在列表里找不到自己那条**
        /// （以为条目丢了，其实只是那一列显示的还是关卡名）。</para>
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (IsBroken)
                    return Name + DSHCore.Ui.UiLocale.Get("（读不出来）");
                return ReplayLibrary.IsAutoName(Name) ? StageText : Name;
            }
        }

        /// <summary>
        /// 关卡显示名（查不到就退回原始 ID）。
        ///
        /// <para><b>结果会缓存</b>：每算一次都要 <c>NamespaceID.Parse</c> +
        /// <c>LevelManager.GetStageName</c>（内部还会取一次词），而列表每行都读它、
        /// 按关卡排序时每次比较还再读一次 —— 实测列表卡顿的主因就在这里。</para>
        /// </summary>
        public string StageText
        {
            get
            {
                if (stageTextResolved)
                    return stageText;
                stageText = PrettyStage(Stage);
                stageTextResolved = true;
                return stageText;
            }
        }

        private string stageText;
        private bool stageTextResolved;

        /// <summary>
        /// 把 <c>mvz2:mountain_10</c> 这种 ID 换成游戏里显示的关卡名。
        /// 查不到（meta 没加载 / 是别的 mod 的关卡）就原样返回 ID，**不要**抛出去。
        /// </summary>
        public static string PrettyStage(string stage)
        {
            if (string.IsNullOrEmpty(stage))
                return DSHCore.Ui.UiLocale.Get("（未知关卡）");
            try
            {
                var main = MainManager.Instance;
                if (main != null)
                {
                    var id = NamespaceID.Parse(stage, "mvz2");
                    if (NamespaceID.IsValid(id))
                    {
                        var name = main.LevelManager.GetStageName(id);
                        if (!string.IsNullOrEmpty(name))
                            return name;
                    }
                }
            }
            catch (Exception)
            {
            }
            return stage;
        }
    }

    /// <summary>
    /// 回放文件的**目录级操作**：列举 / 排序 / 删除 / 按数量上限清理。
    ///
    /// <para>全部操作只碰 <see cref="ReplayConfig.OutputSearchDirs"/> 给出的目录，
    /// 不做任何递归扫描。</para>
    /// </summary>
    internal static class ReplayLibrary
    {
        /// <summary>
        /// 当前环境的哈希（整个列表共用一次采集）。
        ///
        /// <para>采集要问游戏的 <c>ModManager</c>，比读一行 header 贵得多；
        /// 而一屏可能有几十条回放，所以缓存。</para>
        ///
        /// <para>⚠️ **只在「内容 mod 列表真的拿到了」时才缓存**：
        /// 运行时来源（引擎版本 / 位数 / tps）**永远**会产出条目，
        /// 所以不能拿「条目数 &gt; 0」当成功判据 —— 那会在游戏初始化完成之前
        /// 缓存一个**缺了所有内容 mod** 的哈希，于是整个列表都显示「环境不同」。</para>
        /// </summary>
        internal static string CurrentEnvHash()
        {
            if (currentEnvHash != null)
                return currentEnvHash;
            try
            {
                var env = ReplayEnvironment.Capture();
                bool sawModList = false;
                for (int i = 0; i < env.Entries.Count; i++)
                {
                    var k = env.Entries[i].Kind;
                    if (k == ReplayProtocol.ModBuiltin || k == ReplayProtocol.ModContent)
                    {
                        sawModList = true;
                        break;
                    }
                }
                if (!sawModList)
                    return null;
                currentEnvHash = env.Hash;
            }
            catch (Exception)
            {
                return null;
            }
            return currentEnvHash;
        }

        private static string currentEnvHash;

        /// <summary>
        /// 当前游戏版本（<c>Application.version</c>）—— 与 <see cref="CurrentEnvHash"/> 一样缓存。
        ///
        /// <para>它是 interop 属性，每次读都要跨一次边界；列表每行都要用它判断
        /// 「录制版本与当前是否相同」，所以只取一次。</para>
        /// </summary>
        internal static string CurrentGameVersion()
        {
            if (gameVersion != null)
                return gameVersion;
            try { gameVersion = UnityEngine.Application.version ?? string.Empty; }
            catch (Exception) { return null; }
            return gameVersion;
        }

        private static string gameVersion;

        /// <summary>排序键。</summary>
        public enum SortField
        {
            /// <summary>添加进库的时间。</summary>
            Added,
            /// <summary>时长。</summary>
            Duration,
            /// <summary>关卡名。</summary>
            Stage,
        }

        /// <summary>排序规格：键 + 方向。</summary>
        public struct SortSpec
        {
            public SortField Field;
            /// <summary>true = 降序（大/晚在前）。</summary>
            public bool Desc;

            public SortSpec(SortField field, bool desc)
            {
                Field = field;
                Desc = desc;
            }

            public override string ToString()
            {
                var i = SortIndex(this);
                return i >= 0 && i < SortLabels.Length ? SortLabels[i] : "?";
            }
        }

        /// <summary>
        /// 排序按钮的循环顺序（点击依次切换，转完一圈回到第一个）。
        ///
        /// <para>顺序是需求定死的：时间↑ → 时间↓ → 时长↑ → 时长↓ → 关卡↑ → 关卡↓。</para>
        /// </summary>
        public static readonly SortSpec[] SortCycle =
        {
            new SortSpec(SortField.Added, false),
            new SortSpec(SortField.Added, true),
            new SortSpec(SortField.Duration, false),
            new SortSpec(SortField.Duration, true),
            new SortSpec(SortField.Stage, false),
            new SortSpec(SortField.Stage, true),
        };

        /// <summary>与 <see cref="SortCycle"/> 一一对应的按钮文案。</summary>
        public static readonly string[] SortLabels = { "时间↑", "时间↓", "时长↑", "时长↓", "关卡↑", "关卡↓" };

        public static int SortIndex(SortSpec spec)
        {
            for (int i = 0; i < SortCycle.Length; i++)
            {
                if (SortCycle[i].Field == spec.Field && SortCycle[i].Desc == spec.Desc)
                    return i;
            }
            return 0;
        }

        // ------------------------------------------------------------------
        // 列举
        // ------------------------------------------------------------------

        /// <summary>
        /// 扫出所有回放。同一个名字同时存在 <c>.mrep</c> 与 <c>.mrep.json</c> 时只算一条，
        /// 优先读 <c>.mrep.json</c>（纯文本，不用解 gzip）。
        ///
        /// <para><b>大多数文件不会真的去读 header</b>：目录里的 <c>_replay_index.json</c>
        /// 缓存了每个文件的 header 快照 + 大小 + 修改时间。两者都没变就直接复用
        /// （见 <see cref="ReplayIndex"/>）。只在有变化时才回写索引。</para>
        ///
        /// <para>日志里会报 <c>复用/重读</c> 的条数 —— 这是「索引到底有没有起作用」
        /// 唯一能直接看到的证据（列表卡顿时先看它）。</para>
        /// </summary>
        public static List<ReplayInfo> LoadAll()
        {
            // ⚠️ 一次采集、注入每一条：下面 50 条各自去问一遍 ModManager / Application.version
            // 就是整个列表里最贵的开销，而且结果在这一次刷新里完全一样。
            var envNow = CurrentEnvHash();
            var gameNow = CurrentGameVersion();

            var byName = new Dictionary<string, ReplayInfo>(StringComparer.OrdinalIgnoreCase);
            int reused = 0, reread = 0;
            foreach (var dir in ReplayConfig.OutputSearchDirs())
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    continue;

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch (Exception) { continue; }

                // 先把「名字 → 路径」收好，纯文本副本优先。
                var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < files.Length; i++)
                {
                    var full = files[i];
                    string name;
                    bool plain = false;
                    if (full.EndsWith(".mrep.json", StringComparison.OrdinalIgnoreCase))
                    {
                        name = System.IO.Path.GetFileName(full);
                        name = name.Substring(0, name.Length - ".mrep.json".Length);
                        plain = true;
                    }
                    else if (full.EndsWith(".mrep", StringComparison.OrdinalIgnoreCase))
                    {
                        name = System.IO.Path.GetFileName(full);
                        name = name.Substring(0, name.Length - ".mrep".Length);
                    }
                    else
                    {
                        continue;
                    }
                    if (plain || !found.ContainsKey(name))
                        found[name] = full;
                }

                var index = ReplayIndex.Load(dir);
                bool dirty = false;

                foreach (var kv in found)
                {
                    var name = kv.Key;
                    var full = kv.Value;

                    // 多目录（现在只有一个）时，先出现的那个目录优先。
                    if (byName.ContainsKey(name))
                        continue;

                    long size = 0, written = 0, created = 0;
                    try
                    {
                        var fi = new FileInfo(full);
                        // 一次 stat 把三个值都拿到（FileInfo 自己会缓存首次调用的结果）。
                        size = fi.Length;
                        written = fi.LastWriteTimeUtc.Ticks;
                        created = fi.CreationTimeUtc.Ticks;
                    }
                    catch (Exception) { }

                    var info = new ReplayInfo { Name = name, Path = full };
                    info.SetContext(envNow, gameNow);
                    info.AddedAt = FromUtcTicks(created);

                    ReplayIndexEntry ie;
                    index.TryGetValue(name, out ie);

                    bool reuse = ie != null && ie.Usable && ie.Matches(size, written)
                        && string.Equals(ie.FileName, System.IO.Path.GetFileName(full), StringComparison.OrdinalIgnoreCase);

                    if (reuse)
                    {
                        reused++;
                        info.SizeBytes = size;
                        ApplyEntry(info, ie);
                    }
                    else
                    {
                        reread++;
                        LoadHeader(info);
                        if (info.AddedAt == DateTime.MinValue && ie != null && ie.AddedTicks != 0)
                            info.AddedAt = FromUtcTicks(ie.AddedTicks);

                        if (ie == null)
                            ie = new ReplayIndexEntry { Name = name };
                        FillEntry(ie, info, System.IO.Path.GetFileName(full), size, written, created);
                        index[name] = ie;
                        dirty = true;
                    }

                    if (ie != null)
                    {
                        info.Health = ie.Health;
                        info.LastMismatch = ie.LastMismatch;
                        info.LastBadTick = ie.LastBadTick;
                        info.VerifiedEnv = ie.VerifiedEnv;
                    }

                    byName[name] = info;
                }

                // 把已经不存在的文件从索引里剔掉（否则健康度会一直挂着）。
                if (index.Count != found.Count)
                    dirty = true;
                else
                {
                    foreach (var k in index.Keys)
                    {
                        if (!found.ContainsKey(k))
                        {
                            dirty = true;
                            break;
                        }
                    }
                }
                if (dirty)
                {
                    foreach (var k in new List<string>(index.Keys))
                    {
                        if (!found.ContainsKey(k))
                            index.Remove(k);
                    }
                    ReplayIndex.Save(dir, index);
                }
            }

            var list = new List<ReplayInfo>(byName.Values);
            if (reused + reread > 0)
            {
                Plugin.Log?.LogInfo("[Replay] 列举回放：共 " + list.Count + " 条（索引复用 "
                    + reused + " 条，重读 header " + reread + " 条）");
            }
            Sort(list, new SortSpec(SortField.Added, true));
            return list;
        }

        private static DateTime FromUtcTicks(long ticks)
        {
            if (ticks <= 0)
                return DateTime.MinValue;
            try
            {
                return new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        private static void ApplyEntry(ReplayInfo info, ReplayIndexEntry e)
        {
            info.Stage = e.Stage;
            info.Area = e.Area;
            info.Difficulty = e.Difficulty;
            info.GameVersion = e.GameVersion;
            info.EnvHash = e.EnvHash;
            info.EnvCount = e.EnvCount;
            info.StartTick = e.StartTick;
            info.LastTick = e.LastTick;
            info.Tps = e.Tps > 0 ? e.Tps : 30;
            info.Version = e.Version;
            info.SpecVersion = e.SpecVersion;
            info.Debug = e.Debug;
            info.Error = e.Error;
            info.Created = FromUtcTicks(e.RecordedTicks);
        }

        private static void FillEntry(ReplayIndexEntry e, ReplayInfo info, string fileName,
            long size, long written, long created)
        {
            e.Name = info.Name;
            e.FileName = fileName;
            e.Size = size;
            e.WrittenTicks = written;
            if (created != 0)
                e.AddedTicks = created;
            e.RecordedTicks = info.Created == DateTime.MinValue ? 0 : info.Created.Ticks;
            e.Stage = info.Stage;
            e.Area = info.Area;
            e.Difficulty = info.Difficulty;
            e.GameVersion = info.GameVersion;
            e.EnvHash = info.EnvHash;
            e.EnvCount = info.EnvCount;
            e.StartTick = info.StartTick;
            e.LastTick = info.LastTick;
            e.Tps = info.Tps;
            e.Version = info.Version;
            e.SpecVersion = info.SpecVersion;
            e.Debug = info.Debug;
            e.Error = info.Error;
        }

        /// <summary>
        /// 就地排序。同键时用**添加时间降序 + 名字**当稳定的次级键，
        /// 保证每次显示的相对顺序一致（否则同长度的行会来回跳）。
        /// </summary>
        public static void Sort(List<ReplayInfo> list, SortSpec spec)
        {
            if (list == null)
                return;

            Comparison<ReplayInfo> primary;
            switch (spec.Field)
            {
                case SortField.Stage:
                    primary = (a, b) => string.Compare(a.StageText, b.StageText, StringComparison.CurrentCulture);
                    break;
                case SortField.Duration:
                    primary = (a, b) => a.DurationSeconds.CompareTo(b.DurationSeconds);
                    break;
                default:
                    primary = (a, b) => a.AddedAt.CompareTo(b.AddedAt);
                    break;
            }

            var sign = spec.Desc ? -1 : 1;
            list.Sort((a, b) =>
            {
                var c = sign * primary(a, b);
                if (c != 0)
                    return c;
                c = b.AddedAt.CompareTo(a.AddedAt);
                if (c != 0)
                    return c;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
        }

        // ------------------------------------------------------------------
        // 自动命名 / 重命名
        // ------------------------------------------------------------------

        /// <summary>
        /// 这个名字是不是**录制器自动生成**的（= 玩家没改过名）。
        ///
        /// <para>命名格式见 <c>ReplayRecorder.BuildAutoName</c>：
        /// <c>&lt;关卡冒号换下划线&gt;_yyyyMMdd_HHmmss</c>。所以判据是
        /// 「结尾必须是 <c>_8位数字_6位数字</c>，且前面还有至少一个字符」。</para>
        ///
        /// <para>「未命名回放」与「重命名不计入上限」都以它为准 ——
        /// 玩家改过名的回放我们不能再当成临时产物随便删掉。</para>
        /// </summary>
        public static bool IsAutoName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length < 17)
                return false;
            int n = name.Length;
            if (name[n - 16] != '_' || name[n - 7] != '_')
                return false;
            for (int i = n - 15; i < n; i++)
            {
                if (i == n - 7)
                    continue;
                if (!char.IsDigit(name[i]))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 把一条回放改名（<c>.mrep</c> 与同名的 <c>.mrep.json</c> 一起改）。
        ///
        /// <para>新名字会先过一遍净化（只留字母数字与 <c>_ - . 空格</c>，空格转下划线），
        /// 与录制器写盘时用的是**同一套**规则 —— 否则「自动命名」的判据会被玩家
        /// 随手打出的怪字符搞乱。撞名时返回失败让玩家自己换一个，
        /// **不**自动加 <c>(2)</c>（那会让玩家以为自己改成功了）。</para>
        /// </summary>
        public static bool Rename(ReplayInfo info, string newName, out string error)
        {
            error = null;
            if (info == null || string.IsNullOrEmpty(info.Name))
            {
                error = "没有指定要改名的回放";
                return false;
            }

            var clean = SanitizeName(newName);
            if (string.IsNullOrEmpty(clean))
            {
                error = "新名字里没有任何可用字符（只能用字母、数字、下划线、短横线、点、空格）";
                return false;
            }
            if (string.Equals(clean, info.Name, StringComparison.OrdinalIgnoreCase))
            {
                error = "新名字和原来一样";
                return false;
            }

            var dir = System.IO.Path.GetDirectoryName(info.Path ?? "");
            if (string.IsNullOrEmpty(dir))
            {
                var outDir = ReplayConfig.ResolveOutputDir();
                if (!string.IsNullOrEmpty(outDir))
                    dir = outDir;
            }
            if (string.IsNullOrEmpty(dir))
            {
                error = "解析不出回放目录";
                return false;
            }

            var fromMrep = System.IO.Path.Combine(dir, info.Name + ".mrep");
            var toMrep = System.IO.Path.Combine(dir, clean + ".mrep");
            var fromJson = fromMrep + ".json";
            var toJson = toMrep + ".json";

            if (File.Exists(toMrep) || File.Exists(toJson))
            {
                error = "已经有一条叫「" + clean + "」的回放了";
                return false;
            }
            if (!File.Exists(fromMrep) && !File.Exists(fromJson))
            {
                error = "原文件已经不在了（可能被删过）";
                return false;
            }

            try
            {
                // 先搬纯文本副本：它只是「方便读」的附带产物，出问题也不算致命。
                if (File.Exists(fromJson))
                    File.Move(fromJson, toJson);
                if (File.Exists(fromMrep))
                    File.Move(fromMrep, toMrep);
            }
            catch (Exception e)
            {
                error = "改名失败：" + e.GetType().Name + "：" + e.Message;
                return false;
            }

            // 索引里的健康度要跟着搬（名字是主键）。
            var idxDir = ReplayConfig.ResolveOutputDir();
            if (!string.IsNullOrEmpty(idxDir))
            {
                var map = ReplayIndex.Load(idxDir);
                ReplayIndexEntry oldEntry;
                if (map.TryGetValue(info.Name, out oldEntry))
                {
                    map.Remove(info.Name);
                    oldEntry.Name = clean;
                    map[clean] = oldEntry;
                    ReplayIndex.Save(idxDir, map);
                }
            }

            Plugin.Log?.LogInfo("[Replay] 已重命名回放 " + info.Name + " → " + clean);
            info.Name = clean;
            // ⚠️ `Path` 必须一起改：随后的导出（`ReplayExporter.DialogExport(info.Path)`）
            // 拿的就是它 —— 不改的话导出会去找已经不存在的旧文件。
            info.Path = File.Exists(toJson) ? toJson : toMrep;
            return true;
        }

        /// <summary>与 <c>ReplayRecorder.SanitizeName</c> 同一套规则（那边是 private）。</summary>
        public static string SanitizeName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ' ')
                    sb.Append(c == ' ' ? '_' : c);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        // ------------------------------------------------------------------
        // 读 header
        // ------------------------------------------------------------------

        /// <summary>只读第一行（header）。失败时把原因写进 <see cref="ReplayInfo.Error"/>，不抛。</summary>
        private static void LoadHeader(ReplayInfo info)
        {
            try
            {
                var fi = new FileInfo(info.Path);
                if (fi.Exists)
                {
                    info.SizeBytes = fi.Length;
                    // 文件系统时间只是兜底：header 里的 created 才是录制时刻。
                    info.Created = fi.LastWriteTime;
                    info.AddedAt = fi.CreationTime;
                }

                var line = ReadFirstLine(info.Path);
                if (string.IsNullOrEmpty(line))
                {
                    info.Error = "文件是空的";
                    return;
                }
                if (!FlatJson.TryParse(line, out var head, out var err))
                {
                    info.Error = "header 解析失败：" + err;
                    return;
                }
                if (Get(head, "magic") != ReplayProtocol.Magic)
                {
                    info.Error = "不是回放文件";
                    return;
                }

                info.Stage = Get(head, "stage");
                info.Area = Get(head, "area");
                info.Difficulty = Get(head, "difficulty");
                info.StartTick = GetInt(head, "startTick", 0);
                info.LastTick = GetInt(head, "lastTick", 0);
                info.Tps = GetInt(head, "tps", 30);
                info.Created = ParseCreated(Get(head, "created"), info.Created);

                // 协议字段：列表要能在**不载入整份文件**的前提下就说清
                // 「这份回放能不能播、是不是在别的环境里录的」。
                info.Version = GetInt(head, "ver", 0);
                info.SpecVersion = GetInt(head, "spec", 0);
                info.Debug = string.Equals(Get(head, "debug"), "true", StringComparison.OrdinalIgnoreCase);
                info.GameVersion = Get(head, "game");
                info.EnvHash = Get(head, "envHash");
                info.EnvCount = GetInt(head, "envCount", 0);

                if (!ReplayProtocol.CanRead(info.Version))
                {
                    // 版本读不了就跟「文件损坏」一样是**播不了**，但原因必须说清 ——
                    // 否则用户会以为文件坏了而删掉它。
                    info.Error = ReplayProtocol.VersionPolicy(info.Version);
                }
            }
            catch (Exception e)
            {
                info.Error = e.GetType().Name + ": " + e.Message;
            }
        }

        private static DateTime ParseCreated(string text, DateTime fallback)
        {
            if (string.IsNullOrEmpty(text))
                return fallback;
            // 写盘时用的是 "s"（ISO 8601，无时区）→ 按本地时间读。
            DateTime t;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out t))
                return t;
            return fallback;
        }

        /// <summary>
        /// 读出文件的第一行。兼管 <c>.mrep</c>（gzip）与 <c>.mrep.json</c>（纯文本）。
        /// 用 <c>FileShare.ReadWrite</c> 打开，免得撞上游戏正在写的那一份。
        /// </summary>
        public static string ReadFirstLine(string path)
        {
            var gz = path.EndsWith(".mrep", StringComparison.OrdinalIgnoreCase);
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (!gz)
                {
                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                        return reader.ReadLine();
                }

                using (var zip = new GZipStream(fs, CompressionMode.Decompress))
                {
                    var buf = new List<byte>(512);
                    // header 一定很短（几百字节），但别假设长度：读满 64KB 还没换行就放弃。
                    for (int n = 0; n < 65536; n++)
                    {
                        var b = zip.ReadByte();
                        if (b < 0)
                            break;
                        if (b == '\n')
                            break;
                        if (b == '\r')
                            continue;
                        buf.Add((byte)b);
                    }
                    return buf.Count == 0 ? "" : Encoding.UTF8.GetString(buf.ToArray());
                }
            }
        }

        private static string Get(Dictionary<string, string> map, string key)
        {
            string v;
            return map != null && map.TryGetValue(key, out v) ? v : null;
        }

        private static int GetInt(Dictionary<string, string> map, string key, int def)
        {
            var v = Get(map, key);
            int r;
            return !string.IsNullOrEmpty(v)
                && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        // ------------------------------------------------------------------
        // 删除 / 清理
        // ------------------------------------------------------------------

        /// <summary>删掉一条回放的 <c>.mrep</c> 与 <c>.mrep.json</c>（两个都尝试）。</summary>
        public static bool Delete(ReplayInfo info, out string error)
        {
            error = null;
            if (info == null || string.IsNullOrEmpty(info.Name))
            {
                error = "没有指定要删的回放";
                return false;
            }

            var dir = System.IO.Path.GetDirectoryName(info.Path ?? "");
            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(dir))
                dirs.Add(dir);
            foreach (var d in ReplayConfig.OutputSearchDirs())
            {
                if (!string.IsNullOrEmpty(d) && !dirs.Contains(d))
                    dirs.Add(d);
            }

            bool any = false;
            var failures = new List<string>();
            for (int i = 0; i < dirs.Count; i++)
            {
                DeleteIfExists(System.IO.Path.Combine(dirs[i], info.Name + ".mrep"), ref any, failures);
                DeleteIfExists(System.IO.Path.Combine(dirs[i], info.Name + ".mrep.json"), ref any, failures);
            }

            if (failures.Count > 0)
            {
                error = string.Join("；", failures.ToArray());
                return false;
            }
            if (!any)
            {
                error = "文件已经不在了（可能被删过）";
                return false;
            }
            Plugin.Log?.LogInfo("[Replay] 已删除回放 " + info.Name);
            return true;
        }

        private static void DeleteIfExists(string path, ref bool any, List<string> failures)
        {
            try
            {
                if (!File.Exists(path))
                    return;
                File.Delete(path);
                any = true;
            }
            catch (Exception e)
            {
                failures.Add(System.IO.Path.GetFileName(path) + "：" + e.GetType().Name);
            }
        }

        /// <summary>
        /// 把一条回放的 <c>.mrep</c>（以及同名的 <c>.mrep.json</c>，如果有）写到
        /// <paramref name="destPath"/>。
        ///
        /// <para><b>为什么带一个明确的目标路径：</b>导出走的是游戏原生的「另存为」对话框
        /// （<c>FileHelper.SaveExternalFile</c>，与「切换存档 → 导出」同一条路），
        /// 目标由玩家自己挑，不再由配置项指定目录。</para>
        ///
        /// <para><b>为什么不搬走只拷贝：</b>原文件是回放库的正式成员，还要继续被列表页管理
        /// （播放 / 删除 / 按数量上限清理）。导出是「拿一份出去」，不是「移走」。</para>
        ///
        /// <para><paramref name="path"/> 允许是 <c>.mrep</c> 或 <c>.mrep.json</c>
        /// —— 载入器优先用 json，所以 <see cref="ReplayFile.Path"/> 多半是后者。</para>
        /// </summary>
        public static bool Export(string path, string destPath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "没有可导出的文件。";
                return false;
            }
            if (string.IsNullOrEmpty(destPath))
            {
                error = "没有指定保存位置。";
                return false;
            }

            var mrep = ToMrepPath(path);
            if (mrep == null)
            {
                error = "不是回放文件：" + System.IO.Path.GetFileName(path);
                return false;
            }
            if (!File.Exists(mrep))
            {
                error = "回放文件已经不在了：" + System.IO.Path.GetFileName(mrep);
                return false;
            }

            // 玩家在对话框里可能只敲了个名字（没有扩展名），补上。
            var dest = destPath;
            if (!dest.EndsWith(".mrep", StringComparison.OrdinalIgnoreCase))
                dest += ".mrep";

            var failures = new List<string>();
            try
            {
                var dir = System.IO.Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                error = "建不了目标目录：" + e.GetType().Name;
                return false;
            }

            if (!CopyOne(mrep, dest, failures))
                { error = string.Join("；", failures.ToArray()); return false; }

            // 纯文本副本跟着一起走（有才拷），名字与源文件同构：xxx.mrep / xxx.mrep.json
            var json = mrep + ".json";
            if (File.Exists(json))
                CopyOne(json, dest + ".json", failures);

            Plugin.Log?.LogInfo("[Replay] 已导出 " + System.IO.Path.GetFileName(mrep) + " → " + dest
                + (failures.Count > 0 ? "，部分失败：" + string.Join("；", failures.ToArray()) : ""));
            if (failures.Count > 0)
            {
                error = "部分文件没能写过去：" + string.Join("；", failures.ToArray());
                return false;
            }
            return true;
        }

        /// <summary>
        /// 把一个外部的 <c>.mrep</c>（以及同名的 <c>.mrep.json</c>）导入回放库。
        ///
        /// <para>与导出对称，走游戏原生的「打开文件」对话框
        /// （<c>FileHelper.OpenExternalFile</c>，与「切换存档 → 导入」同一条路）。</para>
        ///
        /// <para><b>不覆盖同名回放</b>：目标名已存在时自动退到 <c>名字 (2).mrep</c>，
        /// 免得玩家一个手滑把本地那条顶掉。</para>
        /// </summary>
        public static bool Import(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path))
            {
                error = "没有选择文件。";
                return false;
            }

            var mrep = ToMrepPath(path);
            if (mrep == null)
            {
                error = "只能导入 .mrep 文件：" + System.IO.Path.GetFileName(path);
                return false;
            }
            if (!File.Exists(mrep))
            {
                error = "文件已经不在了：" + System.IO.Path.GetFileName(mrep);
                return false;
            }

            // 先验一遍 header：选错文件时要当场说清楚，而不是往列表里塞一条「读不出来」。
            var head = new ReplayInfo { Name = System.IO.Path.GetFileName(mrep), Path = mrep };
            LoadHeader(head);
            if (head.IsBroken)
            {
                error = "这个文件不是一条能用的回放（" + head.Error + "）。";
                return false;
            }

            string dir;
            try
            {
                dir = ReplayConfig.ResolveOutputDir();
                if (string.IsNullOrEmpty(dir))
                {
                    error = "解析不出回放目录。";
                    return false;
                }
                Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                error = "建不了回放目录：" + e.GetType().Name;
                return false;
            }

            var dest = UniquePath(dir, System.IO.Path.GetFileName(mrep));
            try
            {
                File.Copy(mrep, dest, false);
            }
            catch (Exception e)
            {
                error = "复制失败：" + e.GetType().Name + "：" + e.Message;
                return false;
            }

            var json = mrep + ".json";
            if (File.Exists(json))
            {
                try { File.Copy(json, dest + ".json", false); }
                catch (Exception) { /* 纯文本副本可有可无，失败不算导入失败 */ }
            }

            // ⚠️ 「添加时间」看的是**文件创建时间**，而 File.Copy 会把源的创建时间
            // 一起带过来（导入一条很久以前录的回放，会显示成那时的日期）。
            // 所以显式改成本刻 —— 列表里那一栏的语义就是「什么时候到我这里的」。
            try { File.SetCreationTime(dest, DateTime.Now); }
            catch (Exception) { }

            Plugin.Log?.LogInfo("[Replay] 已导入 " + mrep + " → " + dest);
            return true;
        }

        /// <summary>导入进来的那条回放叫什么（导入成功后用来拼提示文案）。</summary>
        public static string ImportedName(string path)
        {
            return System.IO.Path.GetFileName(path ?? string.Empty);
        }

        /// <summary>
        /// 把「可能是 <c>.mrep.json</c> 的路径」归一成 <c>.mrep</c> 路径；不是回放文件时返回 null。
        /// </summary>
        private static string ToMrepPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            var p = path;
            if (p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                p = p.Substring(0, p.Length - 5);
            return p.EndsWith(".mrep", StringComparison.OrdinalIgnoreCase) ? p : null;
        }

        /// <summary>在 <paramref name="dir"/> 下找一个不撞名的路径：<c>a.mrep</c> → <c>a (2).mrep</c>。</summary>
        private static string UniquePath(string dir, string fileName)
        {
            var first = System.IO.Path.Combine(dir, fileName);
            if (!File.Exists(first) && !File.Exists(first + ".json"))
                return first;

            var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
            for (int i = 2; i < 1000; i++)
            {
                var candidate = System.IO.Path.Combine(dir, stem + " (" + i + ").mrep");
                if (!File.Exists(candidate) && !File.Exists(candidate + ".json"))
                    return candidate;
            }
            return first;
        }

        private static bool CopyOne(string from, string to, List<string> failures)
        {
            try
            {
                File.Copy(from, to, true);
                return true;
            }
            catch (Exception e)
            {
                failures.Add(System.IO.Path.GetFileName(to) + "：" + e.GetType().Name);
                return false;
            }
        }

        /// <summary>清理掉了哪一类（给「已清理 N 条⚠⚠」的提示文案用）。</summary>
        internal enum CleanupKind
        {
            /// <summary>一条也没删（三种都没命中，应该问玩家要不要全删）。</summary>
            None = 0,
            /// <summary>失效回放（读不出来的）。</summary>
            Broken = 1,
            /// <summary>未命名回放（录制器自动命名、玩家没改过名的）。</summary>
            AutoNamed = 2,
            /// <summary>全部回放（玩家确认过的）。</summary>
            All = 3,
        }

        /// <summary>
        /// 按「失效 → 未命名 → 交给玩家确认」的优先级清理。
        ///
        /// <para>只删第一类命中的：三条都删的话，玩家点一下「清理」就会把
        /// 明显不该删的也一起删掉。返回真正删掉了几条，
        /// <paramref name="kind"/> 说明删的是哪一类（<see cref="CleanupKind.None"/> = 该问玩家了）。</para>
        ///
        /// <para><b>为什么不把「未命名」限定在大类里：</b>玩家改过名的回放
        /// （见 <see cref="IsAutoName"/>）是他自己起的名字，我们不该当成临时产物。</para>
        /// </summary>
        public static int Cleanup(List<ReplayInfo> all, out CleanupKind kind, out string error)
        {
            error = null;
            var targets = SelectCleanupTargets(all, out kind);
            if (targets.Count == 0)
                return 0;
            return DeleteMany(targets, all, out error);
        }

        /// <summary>
        /// 只**算**这次清理会删哪些、删几条，一个文件都不动（自测/预览用）。
        ///
        /// <para>自测里调它而不是 <see cref="Cleanup"/> —— 自测跑在玩家的真实回放目录上，
        /// 而「清理」会真的删文件（那正是它该做的事）。</para>
        /// </summary>
        public static int CleanupPreview(List<ReplayInfo> all, out CleanupKind kind)
        {
            return SelectCleanupTargets(all, out kind).Count;
        }

        private static List<ReplayInfo> SelectCleanupTargets(List<ReplayInfo> all, out CleanupKind kind)
        {
            var targets = new List<ReplayInfo>();
            kind = CleanupKind.None;
            if (all == null || all.Count == 0)
                return targets;

            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && all[i].IsBroken)
                    targets.Add(all[i]);
            }
            kind = CleanupKind.Broken;

            if (targets.Count == 0)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] != null && IsAutoName(all[i].Name))
                        targets.Add(all[i]);
                }
                kind = CleanupKind.AutoNamed;
            }

            if (targets.Count == 0)
                kind = CleanupKind.None;
            return targets;
        }

        /// <summary>把列表里的回放全部删掉（调用方已经问过玩家了）。</summary>
        public static int DeleteAll(List<ReplayInfo> all, out string error)
        {
            error = null;
            if (all == null || all.Count == 0)
                return 0;
            var n = DeleteMany(new List<ReplayInfo>(all), all, out error);
            return n;
        }

        /// <summary>删一批，并从 <paramref name="all"/> 里摘掉**删成功**的那些。返回删成功条数。</summary>
        private static int DeleteMany(List<ReplayInfo> targets, List<ReplayInfo> all, out string error)
        {
            error = null;
            int removed = 0;
            var failures = new List<string>();
            for (int i = 0; i < targets.Count; i++)
            {
                string err;
                if (Delete(targets[i], out err))
                {
                    removed++;
                    if (all != null)
                    {
                        for (int j = all.Count - 1; j >= 0; j--)
                        {
                            if (ReferenceEquals(all[j], targets[i]))
                            {
                                all.RemoveAt(j);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    failures.Add(targets[i].Name + "：" + (err ?? "?"));
                }
            }
            if (failures.Count > 0)
                error = string.Join("；", failures.ToArray());
            return removed;
        }

        /// <summary>
        /// 按上限裁掉**自动命名**的回放。每次写出新回放后调用一次。
        /// 上限 &lt;= 0 时什么都不做。返回删掉了几条。
        ///
        /// <para><b>重命名过的不计入上限、也永不被自动清理</b>（需求）：
        /// 玩家自己起过名字的东西就不该被程序默默删掉。</para>
        /// </summary>
        public static int Prune()
        {
            var max = ReplayConfig.GetMaxReplays();
            if (max <= 0)
                return 0;

            List<ReplayInfo> all;
            try { all = LoadAll(); }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 清理旧回放时列举失败：" + e.GetType().Name);
                return 0;
            }

            // 只拿自动命名的那些参与计数。
            var auto = new List<ReplayInfo>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && IsAutoName(all[i].Name))
                    auto.Add(all[i]);
            }
            if (auto.Count <= max)
                return 0;

            // ⚠️ 顺序必须在这里自己定，**不能**用 LoadAll() 的返回顺序：
            // 列表页支持玩家改排序，而 LoadAll() 返回的是排过序的结果。
            // 玩家一按「按名字」/「按大小」，下面那句 "从 max 开始往后删"
            // 删掉的就成了**最新**的那几条 —— 静默删错东西。
            auto.Sort((a, b) => b.AddedAt.CompareTo(a.AddedAt)); // 新的在前

            var doomed = new List<ReplayInfo>();
            for (int i = max; i < auto.Count; i++)
                doomed.Add(auto[i]);

            string error;
            var removed = DeleteMany(doomed, null, out error);
            if (!string.IsNullOrEmpty(error))
                Plugin.Log?.LogWarning("[Replay] 清理旧回放部分失败：" + error);
            if (removed > 0)
                Plugin.Log?.LogInfo("[Replay] 自动命名的回放数超过上限 " + max + "，已清理 " + removed + " 条最旧的");
            return removed;
        }
    }
}
