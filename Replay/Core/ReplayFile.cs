using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MVZ2Logic.Serialization;

namespace Replay
{
    /// <summary>
    /// 一行「扁平 JSON 对象」的极简解析器。
    ///
    /// <para>为什么不用 Newtonsoft / MongoDB.Bson：本 mod 写出的行格式是**自己控制的**，
    /// 里面只有「字符串 / 数字 / true / false」这几种值，没有任何嵌套。手写 60 行比引一个
    /// interop 代理程序集可靠得多（那种 DLL 在 IL2CPP 下是代理类型，泛型与装箱处处是坑），
    /// 也不会有版本冲突。</para>
    ///
    /// <para>值统一以字符串形式返回；调用方按自己的 schema 取值。
    /// 字符串会被反转义，所以 <c>Json.Str</c> 转义的 keyframe BSON 能原样取回。</para>
    /// </summary>
    internal static class FlatJson
    {
        /// <summary>解析一行，成功返回 true。</summary>
        public static bool TryParse(string line, out Dictionary<string, string> map, out string error)
        {
            map = new Dictionary<string, string>(StringComparer.Ordinal);
            error = null;
            if (line == null)
            {
                error = "空行";
                return false;
            }

            int i = 0;
            int n = line.Length;
            SkipWs(line, ref i, n);
            if (i >= n || line[i] != '{')
            {
                error = "不是 JSON 对象";
                return false;
            }
            i++;
            SkipWs(line, ref i, n);
            if (i < n && line[i] == '}')
                return true;

            while (true)
            {
                SkipWs(line, ref i, n);
                if (i >= n || line[i] != '"')
                {
                    error = "第 " + i + " 字符不是键的引号";
                    return false;
                }
                i++;
                string key = ReadString(line, ref i, n, out error);
                if (key == null)
                    return false;

                SkipWs(line, ref i, n);
                if (i >= n || line[i] != ':')
                {
                    error = "键 " + key + " 后没有冒号";
                    return false;
                }
                i++;
                SkipWs(line, ref i, n);

                string value;
                if (i < n && line[i] == '"')
                {
                    i++;
                    value = ReadString(line, ref i, n, out error);
                    if (value == null)
                        return false;
                }
                else
                {
                    // 裸 token：数字 / true / false / null
                    int start = i;
                    while (i < n && line[i] != ',' && line[i] != '}')
                        i++;
                    value = line.Substring(start, i - start).Trim();
                }
                map[key] = value;

                SkipWs(line, ref i, n);
                if (i >= n)
                {
                    error = "对象没有闭合";
                    return false;
                }
                if (line[i] == ',')
                {
                    i++;
                    continue;
                }
                if (line[i] == '}')
                    return true;
                error = "第 " + i + " 字符既不是逗号也不是右花括号";
                return false;
            }
        }

        private static void SkipWs(string s, ref int i, int n)
        {
            while (i < n)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    i++;
                else
                    break;
            }
        }

        /// <summary>读一个字符串体（调用方已经吃掉开引号），返回未转义的内容。</summary>
        private static string ReadString(string s, ref int i, int n, out string error)
        {
            error = null;
            var sb = new System.Text.StringBuilder(64);
            while (i < n)
            {
                char c = s[i++];
                if (c == '"')
                    return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (i >= n)
                {
                    error = "转义符后字符串就结束了";
                    return null;
                }
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        {
                            if (i + 4 > n)
                            {
                                error = "\\u 转义不完整";
                                return null;
                            }
                            if (!ushort.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                            {
                                error = "\\u 转义不是十六进制";
                                return null;
                            }
                            sb.Append((char)code);
                            i += 4;
                            break;
                        }
                    default:
                        error = "未知转义 \\" + e;
                        return null;
                }
            }
            error = "字符串没有闭合";
            return null;
        }
    }

    /// <summary>回放里的一条动作事件。字段含义见 DESIGN.md 第 5 节。</summary>
    internal sealed class ReplayEvent
    {
        public int Tick;
        public string Kind;      // grid / entity / blueprint / bpkey / lawn / tool(set,reset 为 v1 遗留)

        // 指针参数（grid / entity / blueprint / lawn / bpkey）
        public int PointerId = -1;
        public int Button;
        public int Interaction;

        // 目标
        public int Lane;
        public int Column;
        public long EntityId;
        public int Index;
        public bool Conveyor;
        public int Key;
        public int Area;
        public string Type;      // set
        public string Tool;      // tool

        // 实体事件的目标身份（v2 后期加入）：定义 ID + 记时的坐标。
        // 实体 ID 会因任何一处生成时机不同而整体错位，靠这两个字段才能把目标找回来。
        public string EntityDef;
        public bool HasEntityPos;
        public float Ex, Ey, Ez;

        /// <summary>
        /// 实体所占的**格子分层**（v6 加入），形如 <c>main</c> / <c>protector,carrier</c>；
        /// 不在任何分层上（敌人、拾取物…）是空串。
        ///
        /// <para><b>为什么必须有它：</b>同一格最多能同时站 4 个可被镐子挖掉的器械 ——
        /// <c>承载位 / 保护位 / 主要位 / 工具位</c>。它们**坐标完全相同**（同一个格子），
        /// 所以「按坐标最近」根本分不出是哪一个；而游戏的目标判定恰恰是按分层来的
        /// （<c>VanillaGridLayers.protectedLayers</c> + <c>pointerPositionY &gt;= 0.5f</c> 在
        /// 保护位与被保护位之间二选一）。锚点选错 → 镐子挖到另一株。</para>
        /// </summary>
        public string EntityLayer;

        // 指针位置（v2 起记录）：屏幕坐标 + 射线命中的世界坐标
        public bool HasPos;
        public float Sx, Sy;
        public float Wx, Wy, Wz;

        /// <summary>
        /// 这个事件发生之后，**手持蓝图上的两个标记**（v8 起记录）。
        ///
        /// <para>为什么必须在**每个拿起蓝图的事件上**记：<c>instantTrigger</c> 是由
        /// 「是否手持触发器」与**玩家选项**「交换触发」（<c>swap_trigger</c>）的异或算出来的，
        /// 而那个选项不在关卡关键帧里、关卡内随手就能改。不记下来的话，
        /// 「录的时候开、放的时候关」会把器械行为整个反过来（详见 <see cref="ReplayHeldFlags"/>）。</para>
        ///
        /// <para><see cref="HasHeldFlags"/> 为 false 时（旧文件、或事件发生时手上不是蓝图）
        /// 回放不做任何干预。</para>
        /// </summary>
        public bool HasHeldFlags;
        public bool InstantTrigger;
        public bool InstantEvoke;

        public override string ToString()
        {
            return Kind + "@" + Tick;
        }
    }

    /// <summary>一条状态指纹（录制时的状态采样，回放时用来核对一致性）。</summary>
    internal sealed class ReplayFingerprint
    {
        public int Tick;
        public int Enemies;
        public int Wave;
        public int Flags;
        public bool Cleared;
        public string Rng;

        /// <summary>存活实体数量。</summary>
        public int EntityCount = -1;
        /// <summary>实体集合的哈希（所有实体的 (id, 定义ID) 按 id 排序后哈希）。</summary>
        public string EntityHash;

        /// <summary>
        /// 各实体定义的数量直方图，形如 <c>mvz2:redstone=3,mvz2:zombie=2</c>。
        ///
        /// <para>比 <see cref="EntityHash"/> 好在**可读**：集合哈希对不上时，它能直接看出
        /// 「多了哪一类 / 少了哪一类」，而不用先猜。</para>
        /// </summary>
        public string EntityDefs;

        /// <summary>
        /// 7 条 RNG 流各自的状态哈希，顺序见 <see cref="RngStreamNames"/>。
        /// 只比合并后的 <see cref="Rng"/> 只能知道「有 RNG 跑偏了」，这一项能说是**哪一条**，
        /// 从而把范围缩小到用它的那个子系统。
        /// </summary>
        public string[] RngStreams;

        /// <summary>与 <see cref="RngStreams"/> 一一对应的流名字（顺序 = 录制器写入顺序）。</summary>
        public static readonly string[] RngStreamNames =
            { "level", "entity", "effect", "round", "spawn", "conveyor", "misc" };

        /// <summary>
        /// 指纹行里的**附加字段**（协议 v10 起允许任意键）。
        ///
        /// <para>它同时服务两件事：① 调试模式的内置字段（<c>rx</c>/<c>ei</c>/<c>lk</c>）；
        /// ② 第三方采样器用 <c>x_</c> 前缀写的自定义字段。</para>
        ///
        /// <para>播放时的比对是**通用**的：凡是录制侧指纹里出现、且不属于
        /// <see cref="BuiltinKeys"/> 的键，都要求回放侧一致 ——
        /// 于是「新加一种指纹」不需要在播放侧补一行比较代码。</para>
        /// </summary>
        public readonly List<KeyValuePair<string, string>> Extra =
            new List<KeyValuePair<string, string>>();

        /// <summary>写一条附加字段（同名覆盖）。</summary>
        public void SetExtra(string key, string value)
        {
            if (string.IsNullOrEmpty(key))
                return;
            for (int i = 0; i < Extra.Count; i++)
            {
                if (Extra[i].Key == key)
                {
                    Extra[i] = new KeyValuePair<string, string>(key, value);
                    return;
                }
            }
            Extra.Add(new KeyValuePair<string, string>(key, value));
        }

        /// <summary>读一条附加字段；没有返回 null。</summary>
        public string ExtraOf(string key)
        {
            for (int i = 0; i < Extra.Count; i++)
            {
                if (Extra[i].Key == key)
                    return Extra[i].Value;
            }
            return null;
        }

        /// <summary>内置字段的键名（这些由强类型成员承载，读文件时不进 <see cref="Extra"/>）。</summary>
        public static readonly string[] BuiltinKeys =
        {
            "t", "k", "en", "w", "fl", "cl", "ec", "eh", "ed", "r",
            "r0", "r1", "r2", "r3", "r4", "r5", "r6",
        };

        /// <summary>是不是内置键（内置键由强类型字段承载，不进 <see cref="Extra"/>）。</summary>
        public static bool IsBuiltinKey(string key)
        {
            for (int i = 0; i < BuiltinKeys.Length; i++)
            {
                if (BuiltinKeys[i] == key)
                    return true;
            }
            return false;
        }

        public override string ToString()
        {
            return "t=" + Tick + " en=" + Enemies + " w=" + Wave + " fl=" + Flags + " cl=" + Cleared
                + " ec=" + EntityCount + " eh=" + (EntityHash ?? "-") + " r=" + Rng
                + " ed=[" + (EntityDefs ?? "-") + "]";
        }
    }

    /// <summary>
    /// 回放里的一次**选项对话框**（v9 起记录）。
    ///
    /// <para>梦魇的「选择命运」和上锁的箱子的「赎金」都会 <c>PauseGame</c> 然后弹一个
    /// 多选对话框等真人点。回放里必须自己答完，否则逻辑 tick 永远不前进。</para>
    ///
    /// <para><b>为什么除了下标还要记文本：</b>上锁的箱子的选项是**按当前金币/碎片动态生成**的
    /// （钱够才有「付钱」那一项）。金币来自全局存档、不在关卡关键帧里，所以
    /// 「录的时候有钱、放的时候没钱」会让选项个数都不一样 —— 只靠下标会选错甚至越界。
    /// 记下所选那一项的文本，回放时优先按文本定位。</para>
    /// </summary>
    internal sealed class ReplayDialogRecord
    {
        public int Tick;
        public string Title;
        public string Desc;
        public string[] Options = Array.Empty<string>();
        /// <summary>玩家选的是第几项（-1 = 录制结束时也没选，例如中途退出了）。</summary>
        public int Chosen = -1;
        /// <summary>所选那一项的**文本**（跨选项列表差异的定位依据）。</summary>
        public string ChosenName;

        public override string ToString()
        {
            return "dlg@" + Tick + " 选项=" + Options.Length + " 选=" + Chosen;
        }
    }

    /// <summary>一个已载入内存的回放文件。</summary>
    internal sealed class ReplayFile
    {
        public string Path;

        /// <summary>格式版本（header 的 <c>ver</c>）。读者**只据此**决定能不能播。</summary>
        public int Version;

        /// <summary>规范版本（header 的 <c>spec</c>）。只作人读信息。</summary>
        public int SpecVersion;

        /// <summary>写文件的 mod 版本（header 的 <c>mod</c>）。</summary>
        public string ModVersion;

        public string Game;
        public string Stage;
        public string Area;
        public string Difficulty;
        public int Seed;
        public int Tps = 30;
        public int StartTick;
        public int LastTick;
        public bool HasKeyframe;
        public int EventCountInHeader;
        public int FingerprintCountInHeader;

        // ---- 协议 v10 新增的 header 字段 ----

        /// <summary>引擎版本（header 的 <c>unity</c>）。</summary>
        public string Unity;

        /// <summary>平台位数（header 的 <c>plat</c>）。</summary>
        public string Platform;

        /// <summary>录制时的环境哈希（header 的 <c>envHash</c>）。</summary>
        public string EnvHash;

        /// <summary>录制时的环境条目数（header 的 <c>envCount</c>）。</summary>
        public int EnvCount;

        /// <summary>录制时用的指纹间隔（header 的 <c>fpt</c>）。0 / 缺失 = 未知。</summary>
        public int FingerprintInterval;

        /// <summary>是不是**调试模式**文件（header 的 <c>debug</c>）。</summary>
        public bool Debug;

        /// <summary>本文件用到的可选特性（header 的 <c>caps</c>，逗号分隔）。</summary>
        public string Caps = string.Empty;

        /// <summary>录制时启用的采样器（header 的 <c>samplers</c>，逗号分隔）。</summary>
        public string Samplers = string.Empty;

        /// <summary>header 里**有没有** <c>samplers</c> 这个键（旧文件没有，用于协议自检）。</summary>
        public bool HasSamplersField;

        /// <summary>动作事件里有没有带手持标记（<c>itg</c>/<c>evk</c>）的 —— 由实际内容推出，不信 caps。</summary>
        public bool HasHeldFlagsInEvents;

        /// <summary>录制结束原因（header 的 <c>end</c>）。</summary>
        public string EndReason;

        /// <summary>录制开始时间（header 的 <c>created</c>）。</summary>
        public string Created;

        /// <summary>
        /// 录制那一刻的**窗口尺寸**（v7 起记录）。
        ///
        /// <para>事件里的 <c>sx/sy</c> 是屏幕像素，而「像素 → 世界坐标」的换算里含屏幕高，
        /// 所以换个窗口尺寸再播，同一个像素就指向别的格子了。记下这个尺寸，
        /// 回放时才能把坐标换算回「同一个世界点」（见 <see cref="ReplayViewport"/>）。</para>
        ///
        /// <para>旧文件（没有这两个字段）读出来是 0 → <see cref="ReplayViewport.Active"/> 为假
        /// → 一切照旧（恒等换算）。</para>
        /// </summary>
        public float ScreenW;
        public float ScreenH;

        public int KeyframeTick;
        public string KeyframeBson;

        public readonly List<ReplayEvent> Events = new List<ReplayEvent>();
        public readonly List<ReplayFingerprint> Fingerprints = new List<ReplayFingerprint>();
        public readonly List<string> Warnings = new List<string>();

        /// <summary>
        /// 逐 tick 的指针轨迹（v9 起记录，见 <see cref="ReplayPointerTrajectory"/>）。
        /// 只有「这一关里存在会读实时指针的内容」时才有内容。
        /// </summary>
        public readonly List<ReplayPointerTrajectory.Sample> PointerSamples =
            new List<ReplayPointerTrajectory.Sample>();

        /// <summary>回放里出现过的选项对话框（v9 起记录，见 <see cref="ReplayDialogs"/>）。</summary>
        public readonly List<ReplayDialogRecord> Dialogs = new List<ReplayDialogRecord>();

        /// <summary>环境条目（v10 起记录，见 <see cref="ReplayEnvironment"/>）。</summary>
        internal readonly List<ReplayEnvEntry> EnvEntries = new List<ReplayEnvEntry>();

        /// <summary>环境摘要行里的字段（schema 解析用；<see cref="ReplayEnvironment"/> 才是终态）。</summary>
        internal string EnvSummaryGame;
        internal string EnvSummaryUnity;
        internal string EnvSummaryPlatform;
        internal int EnvSummaryTps;

        /// <summary>
        /// 录制开始那一刻的**全局金币**（v9 起记录，header 的 <c>mn</c>）。
        ///
        /// <para>金币不在关卡关键帧里（它是全局存档的钱），但它会参与关卡逻辑的**分支**：
        /// 上锁的箱子按「钱够不够」决定要不要生成「付钱」那一项。回放时用这个值给金币影子播种，
        /// 才能让这些分支与录制时一致（见 <see cref="ReplaySaveGuard.SeedMoney"/>）。</para>
        /// </summary>
        public bool HasStartMoney;
        public int StartMoney;

        /// <summary>
        /// 录制时的环境（游戏版本 / 引擎 / 平台 / 全部已装 mod）。协议 v10 起有；
        /// 旧文件是空壳（只有 header 里的 <c>game</c>），此时 <see cref="HasEnv"/> 为假。
        /// </summary>
        public ReplayEnvironment Environment = new ReplayEnvironment();

        /// <summary>文件里有没有 <c>env</c> 记录（旧文件没有）。</summary>
        public bool HasEnv;

        /// <summary>播放策略（**由文件决定**，见 <see cref="ReplayPlaybackPolicy"/>）。</summary>
        public ReplayPlaybackPolicy Policy = new ReplayPlaybackPolicy();

        /// <summary>能不能播：必须有起始关键帧（否则无法重建关卡状态）。</summary>
        public bool CanPlay => HasKeyframe && !string.IsNullOrEmpty(KeyframeBson)
            && !string.IsNullOrEmpty(Stage) && !string.IsNullOrEmpty(Area);

        public string Describe()
        {
            return "ver=" + Version + "（" + ReplayProtocol.SpecVersion + " 版规范）"
                + " 关卡=" + Stage + " 面积=" + Area + " 难度=" + Difficulty
                + " 种子=" + Seed + " tick=" + StartTick + ".." + LastTick
                + " 关键帧=" + (HasKeyframe ? (KeyframeTick + "(" + KeyframeBson.Length + "字符)") : "无")
                + " 事件=" + Events.Count + " 指纹=" + Fingerprints.Count
                + " 指针采样=" + PointerSamples.Count + " 对话框=" + Dialogs.Count
                + " 环境=" + (HasEnv ? Environment.Hash : "无")
                + (Debug ? " [调试文件]" : string.Empty);
        }

        #region 载入
        /// <summary>
        /// 按名字载入。名字可以是完整路径、带扩展名的文件名，或裸名字。
        /// 优先读 <c>.mrep.json</c>（纯文本，不会被 gzip 影响），找不到再读 <c>.mrep</c>。
        /// </summary>
        public static ReplayFile Load(string nameOrPath, out string error)
        {
            var path = Resolve(nameOrPath, out error);
            if (path == null)
                return null;
            return LoadFromPath(path, out error);
        }

        /// <summary>解析一个已存在的文件。</summary>
        public static ReplayFile LoadFromPath(string path, out string error)
        {
            error = null;
            string[] lines;
            try
            {
                if (path.EndsWith(".mrep", StringComparison.OrdinalIgnoreCase))
                {
                    // .mrep 是 gzip 压缩的逐行文本，直接用游戏自己的解压读法。
                    var text = SerializeHelper.ReadCompressed(path);
                    lines = SplitLines(text);
                }
                else
                {
                    lines = ReadAllLinesShared(path);
                }
            }
            catch (Exception e)
            {
                error = "读取失败：" + e.GetType().Name + ": " + e.Message;
                return null;
            }

            if (lines == null || lines.Length == 0)
            {
                error = "文件是空的";
                return null;
            }

            var file = new ReplayFile { Path = path };

            // ---- 第 1 行：header ----
            if (!FlatJson.TryParse(lines[0], out var head, out var herr))
            {
                error = "header 解析失败（" + herr + "）";
                return null;
            }
            if (GetString(head, "magic") != ReplayProtocol.Magic)
            {
                error = "不是 " + ReplayProtocol.Magic + " 文件（magic=" + GetString(head, "magic") + "）";
                return null;
            }

            // 格式版本先判：比本 mod 支持的更新就直接拒绝（缺字段与「字段含义变了」是两回事，
            // 前者能降级读取，后者会静默播错）。
            file.Version = GetInt(head, "ver", 0);
            if (!ReplayProtocol.CanRead(file.Version))
            {
                error = "无法读取这份回放：" + ReplayProtocol.VersionPolicy(file.Version);
                return null;
            }

            file.SpecVersion = GetInt(head, "spec", 0);
            file.ModVersion = GetString(head, "mod");
            file.Game = GetString(head, "game");
            file.Stage = GetString(head, "stage");
            file.Area = GetString(head, "area");
            file.Difficulty = GetString(head, "difficulty");
            file.Seed = GetInt(head, "seed", 0);
            file.Tps = GetInt(head, "tps", 30);
            file.StartTick = GetInt(head, "startTick", 0);
            file.LastTick = GetInt(head, "lastTick", 0);
            file.HasKeyframe = GetBool(head, "hasKeyframe", false);
            file.EventCountInHeader = GetInt(head, "eventCount", 0);
            file.FingerprintCountInHeader = GetInt(head, "fingerprintCount", 0);
            file.ScreenW = GetFloat(head, "sw", 0f);
            file.ScreenH = GetFloat(head, "sh", 0f);
            if (head.ContainsKey("mn"))
            {
                file.HasStartMoney = true;
                file.StartMoney = GetInt(head, "mn", 0);
            }

            // ---- 协议 v10 的 header 字段（旧文件没有 → 一律按「旧行为」取值）----
            file.Unity = GetString(head, "unity");
            file.Platform = GetString(head, "plat");
            file.EnvHash = GetString(head, "envHash");
            file.EnvCount = GetInt(head, "envCount", 0);
            file.FingerprintInterval = GetInt(head, "fpt", 0);
            file.Debug = GetBool(head, "debug", false);
            file.Caps = GetString(head, "caps") ?? string.Empty;
            file.HasSamplersField = head.ContainsKey("samplers");
            file.Samplers = file.HasSamplersField ? GetString(head, "samplers") : null;
            file.EndReason = GetString(head, "end");
            file.Created = GetString(head, "created");

            // 旧文件没有任何 v10 字段 → 至少把「有指针轨迹 / 有对话框」按计数补上，
            // 这样播放策略在旧文件上也能拿到正确的能力集（caps 只是声明，计数才是事实）。
            if (string.IsNullOrEmpty(file.Caps))
            {
                var caps = new System.Collections.Generic.List<string>();
                if (GetInt(head, "ptrCount", 0) > 0) caps.Add(ReplayProtocol.CapPointer);
                if (GetInt(head, "dlgCount", 0) > 0) caps.Add(ReplayProtocol.CapDialog);
                if (file.ScreenW > 0f && file.ScreenH > 0f) caps.Add(ReplayProtocol.CapViewport);
                file.Caps = ReplayProtocol.JoinCaps(caps.ToArray());
            }
            if (file.FingerprintInterval <= 0)
            {
                // 旧文件没记间隔：从 header 的指纹条数与 tick 跨度粗估一个，
                // 只用于「文件自述自检」的提示，不参与任何语义。
                int span = file.LastTick - file.StartTick;
                if (file.FingerprintCountInHeader > 1 && span > 0)
                    file.FingerprintInterval = span / (file.FingerprintCountInHeader - 1);
            }
            if (file.Samplers == null)
            {
                // 旧文件：按「当时的默认行为」推一份采样器清单。
                var s = new System.Collections.Generic.List<string>
                {
                    "state.basic", "state.entities", "state.rng",
                };
                file.Samplers = string.Join(",", s.ToArray());
            }

            // ---- 之后每行一个对象 ----
            int badLines = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line) || line.Trim().Length == 0)
                    continue;
                if (!FlatJson.TryParse(line, out var obj, out var perr))
                {
                    badLines++;
                    if (file.Warnings.Count < 5)
                        file.Warnings.Add("第 " + (i + 1) + " 行解析失败：" + perr);
                    continue;
                }

                var kind = GetString(obj, "k");
                if (kind == ReplayProtocol.KindEnv)
                {
                    // v10：环境记录。**一类两形**：有 id = 一个条目，没有 = 摘要。
                    if (obj.ContainsKey("id"))
                    {
                        file.EnvEntries.Add(new ReplayEnvEntry
                        {
                            Id = GetString(obj, "id"),
                            Name = GetString(obj, "name"),
                            Version = GetString(obj, "ver"),
                            Kind = GetString(obj, "kind"),
                            DataVersion = obj.ContainsKey("dv") ? GetInt(obj, "dv", -1) : -1,
                            Note = GetString(obj, "note"),
                            Hashable = GetBool(obj, "h", true),
                        });
                    }
                    else
                    {
                        file.EnvSummaryGame = GetString(obj, "game");
                        file.EnvSummaryUnity = GetString(obj, "unity");
                        file.EnvSummaryPlatform = GetString(obj, "plat");
                        file.EnvSummaryTps = GetInt(obj, "tps", 0);
                    }
                }
                else if (kind == ReplayProtocol.KindKeyframe)
                {
                    file.KeyframeTick = GetInt(obj, "tick", 0);
                    file.KeyframeBson = GetString(obj, "state");
                    file.HasKeyframe = file.HasKeyframe && !string.IsNullOrEmpty(file.KeyframeBson);
                }
                else if (kind == ReplayProtocol.KindFingerprint)
                {
                    var fp = new ReplayFingerprint
                    {
                        Tick = GetInt(obj, "t", 0),
                        Enemies = GetInt(obj, "en", 0),
                        Wave = GetInt(obj, "w", 0),
                        Flags = GetInt(obj, "fl", 0),
                        Cleared = GetBool(obj, "cl", false),
                        EntityCount = obj.ContainsKey("ec") ? GetInt(obj, "ec", -1) : -1,
                        EntityHash = GetString(obj, "eh"),
                        EntityDefs = GetString(obj, "ed"),
                        RngStreams = ReadRngStreams(obj),
                        Rng = GetString(obj, "r"),
                    };
                    // v10：附加字段（调试字段 / 第三方采样器字段 / 更新版本加的新字段）。
                    // **不认识的键一律收进 Extra**，这样即使本 mod 不认识它，
                    // 播放时也能做「逐字比对」（见 CompareFingerprints）。
                    foreach (var kv in obj)
                    {
                        if (ReplayFingerprint.IsBuiltinKey(kv.Key))
                            continue;
                        fp.Extra.Add(new KeyValuePair<string, string>(kv.Key, kv.Value));
                    }
                    file.Fingerprints.Add(fp);
                }
                else if (kind == ReplayProtocol.KindPointer)
                {
                    // v9：逐 tick 指针轨迹（不是「动作事件」，所以不进 Events）。
                    file.PointerSamples.Add(new ReplayPointerTrajectory.Sample
                    {
                        Tick = GetInt(obj, "t", 0),
                        X = GetFloat(obj, "sx", 0f),
                        Y = GetFloat(obj, "sy", 0f),
                        Mask = GetInt(obj, "mb", 0),
                    });
                }
                else if (kind == ReplayProtocol.KindDialog)
                {
                    // v9：选项对话框（由 ReplayDialogs 按帧驱动应答，不进 tick 注入队列）。
                    file.Dialogs.Add(new ReplayDialogRecord
                    {
                        Tick = GetInt(obj, "t", 0),
                        Title = GetString(obj, "title"),
                        Desc = GetString(obj, "desc"),
                        Options = ReplayDialogs.Split(GetString(obj, "op")),
                        Chosen = GetInt(obj, "sel", -1),
                        ChosenName = GetString(obj, "selName"),
                    });
                }
                else
                {
                    var ev = new ReplayEvent
                    {
                        Tick = GetInt(obj, "t", 0),
                        Kind = kind,
                        PointerId = GetInt(obj, "pid", 0),
                        Button = GetInt(obj, "btn", 0),
                        Interaction = GetInt(obj, "ia", -1),
                    };
                    switch (kind)
                    {
                        case "grid":
                            ev.Lane = GetInt(obj, "lane", 0);
                            ev.Column = GetInt(obj, "col", 0);
                            break;
                        case "entity":
                            ev.EntityId = GetLong(obj, "id", 0);
                            ev.EntityDef = GetString(obj, "def");
                            ev.EntityLayer = GetString(obj, "lyr");
                            if (obj.ContainsKey("ex"))
                            {
                                ev.HasEntityPos = true;
                                ev.Ex = GetFloat(obj, "ex", 0f);
                                ev.Ey = GetFloat(obj, "ey", 0f);
                                ev.Ez = GetFloat(obj, "ez", 0f);
                            }
                            break;
                        case "blueprint":
                            ev.Index = GetInt(obj, "idx", 0);
                            ev.Conveyor = GetBool(obj, "cv", false);
                            break;
                        case "bpkey":
                            ev.Index = GetInt(obj, "idx", 0);
                            ev.Conveyor = GetBool(obj, "cv", false);
                            ev.Key = GetInt(obj, "key", 0);
                            break;
                        case "lawn":
                            ev.Area = GetInt(obj, "area", 0);
                            break;
                        case "set":
                            ev.Type = GetString(obj, "type");
                            break;
                        case "reset":
                            break;
                        case "tool":
                            ev.Tool = GetString(obj, "name");
                            break;
                        default:
                            if (file.Warnings.Count < 5)
                                file.Warnings.Add("第 " + (i + 1) + " 行是未知事件类型：" + kind);
                            continue;
                    }

                    // v2 起记录了指针位置；v1 没有，保持 HasPos=false。
                    if (obj.ContainsKey("sx"))
                    {
                        ev.HasPos = true;
                        ev.Sx = GetFloat(obj, "sx", 0f);
                        ev.Sy = GetFloat(obj, "sy", 0f);
                        ev.Wx = GetFloat(obj, "wx", 0f);
                        ev.Wy = GetFloat(obj, "wy", 0f);
                        ev.Wz = GetFloat(obj, "wz", 0f);
                    }

                    // v8 起：拿起蓝图那一刻的「立即触发 / 立即激发」标记。
                    // 两个字段**同时存在**才算有效 —— 只写一个会让「另一个是 false」与
                    // 「旧文件没有这两个字段」分不开。
                    if (obj.ContainsKey("itg") && obj.ContainsKey("evk"))
                    {
                        ev.HasHeldFlags = true;
                        ev.InstantTrigger = GetBool(obj, "itg", false);
                        ev.InstantEvoke = GetBool(obj, "evk", false);
                    }

                    file.Events.Add(ev);
                }
            }

            if (badLines > 0)
                file.Warnings.Add("有 " + badLines + " 行无法解析，已跳过");

            // 稳定按 tick 排序（文件本来就是顺序写的，这里只是防御手工编辑过的文件）。
            StableSortByTick(file.Events);

            // 「有没有手持标记」按**实际内容**推，不信 caps：
            // caps 是录制时的声明，可能因为这一局里根本没有蓝图事件而空有声明。
            for (int i = 0; i < file.Events.Count; i++)
            {
                if (file.Events[i].HasHeldFlags)
                {
                    file.HasHeldFlagsInEvents = true;
                    break;
                }
            }

            // ---- 环境：把条目 + 摘要拼成终态对象 ----
            file.HasEnv = file.EnvEntries.Count > 0 || !string.IsNullOrEmpty(file.EnvSummaryGame);
            file.Environment = ReplayEnvironment.FromRecords(
                file.EnvEntries,
                !string.IsNullOrEmpty(file.EnvSummaryGame) ? file.EnvSummaryGame : file.Game,
                !string.IsNullOrEmpty(file.EnvSummaryUnity) ? file.EnvSummaryUnity : file.Unity,
                !string.IsNullOrEmpty(file.EnvSummaryPlatform) ? file.EnvSummaryPlatform : file.Platform,
                file.EnvSummaryTps > 0 ? file.EnvSummaryTps : file.Tps);

            // header 里声明的环境哈希优先（那是录制时算的，比重新算更能说明「当时是什么环境」）。
            if (!string.IsNullOrEmpty(file.EnvHash)
                && !string.Equals(file.EnvHash, file.Environment.Hash, StringComparison.Ordinal))
            {
                file.Warnings.Add("header 的环境哈希 " + file.EnvHash + " 与 env 行重算的 "
                    + file.Environment.Hash + " 不一致（文件可能被手工编辑过）");
            }
            if (file.Version >= ReplayProtocol.FormatVersion && !file.HasEnv)
                file.Warnings.Add("这是 v" + file.Version + " 文件，但没有 env 记录");

            // ---- 播放策略：**完全由文件决定** ----
            file.Policy = ReplayPlaybackPolicy.FromFile(file);
            for (int i = 0; i < file.Policy.SelfCheckWarnings.Count; i++)
                file.Warnings.Add("协议自检：" + file.Policy.SelfCheckWarnings[i]);

            if (!file.CanPlay)
                error = "这个回放没有可用的起始关键帧，无法播放。";
            return file;
        }

        /// <summary>把名字解析成真实文件路径。</summary>
        public static string Resolve(string nameOrPath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(nameOrPath))
            {
                error = "没有指定回放名";
                return null;
            }

            if (nameOrPath.IndexOf('/') >= 0 || nameOrPath.IndexOf('\\') >= 0)
            {
                if (File.Exists(nameOrPath))
                    return nameOrPath;
                error = "找不到文件：" + nameOrPath;
                return null;
            }

            var dirs = ReplayConfig.OutputSearchDirs();
            var candidates = new List<string>();
            var dir = dirs.Count > 0 ? dirs[0] : "";
            for (int d = 0; d < dirs.Count; d++)
            {
                var baseDir = dirs[d];
                if (nameOrPath.EndsWith(".mrep", StringComparison.OrdinalIgnoreCase)
                    || nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(System.IO.Path.Combine(baseDir, nameOrPath));
                }
                else
                {
                    candidates.Add(System.IO.Path.Combine(baseDir, nameOrPath + ".mrep.json"));
                    candidates.Add(System.IO.Path.Combine(baseDir, nameOrPath + ".mrep"));
                }
            }
            foreach (var c in candidates)
            {
                if (File.Exists(c))
                    return c;
            }
            error = "找不到回放：" + nameOrPath + "（目录 " + string.Join(" / ", dirs.ToArray()) + "）";
            return null;
        }

        /// <summary>列出回放目录里的回放（按名字排序）。
        /// 会同时扫「当前位置」和「旧位置」，所以改过输出目录后老文件不会“消失”。</summary>
        public static List<string> ListNames()
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirs = ReplayConfig.OutputSearchDirs();
            for (int d = 0; d < dirs.Count; d++)
            {
                var dir = dirs[d];
                if (!Directory.Exists(dir))
                    continue;
                foreach (var f in Directory.GetFiles(dir))
                {
                    string name = System.IO.Path.GetFileName(f);
                    if (name.EndsWith(".mrep", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring(0, name.Length - 5);
                    else if (name.EndsWith(".mrep.json", StringComparison.OrdinalIgnoreCase))
                        name = name.Substring(0, name.Length - 10);
                    else
                        continue;
                    if (seen.Add(name))
                        result.Add(name);
                }
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>按 tick 稳定排序（同 tick 保持原有先后顺序 —— 顺序对回放很重要）。</summary>
        private static void StableSortByTick(List<ReplayEvent> events)
        {
            int n = events.Count;
            if (n < 2)
                return;
            var order = new int[n];
            for (int i = 0; i < n; i++)
                order[i] = i;
            Array.Sort(order, (x, y) =>
            {
                int c = events[x].Tick.CompareTo(events[y].Tick);
                return c != 0 ? c : x.CompareTo(y);
            });
            var copy = events.ToArray();
            for (int i = 0; i < n; i++)
                events[i] = copy[order[i]];
        }

        private static string[] SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return new string[0];
            return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static string[] ReadAllLinesShared(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var list = new List<string>();
            string line;
            while ((line = reader.ReadLine()) != null)
                list.Add(line);
            return list.ToArray();
        }
        #endregion

        #region 取值辅助
        public static string GetString(Dictionary<string, string> o, string key)
        {
            return o.TryGetValue(key, out var v) ? v : null;
        }

        /// <summary>
        /// 读指纹里那 7 条 RNG 流的哈希（键 <c>r0</c>..<c>r6</c>）。
        /// 7 个键**都**存在才算有效，否则返回 null（旧文件没有这一项）。
        /// 键名按 <see cref="ReplayFingerprint.RngStreamNames"/> 的顺序。
        /// </summary>
        public static string[] ReadRngStreams(Dictionary<string, string> o)
        {
            var names = ReplayFingerprint.RngStreamNames;
            var arr = new string[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                var v = GetString(o, "r" + i);
                if (string.IsNullOrEmpty(v))
                    return null;
                arr[i] = v;
            }
            return arr;
        }

        public static int GetInt(Dictionary<string, string> o, string key, int def)
        {
            if (!o.TryGetValue(key, out var v) || string.IsNullOrEmpty(v))
                return def;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : def;
        }

        public static long GetLong(Dictionary<string, string> o, string key, long def)
        {
            if (!o.TryGetValue(key, out var v) || string.IsNullOrEmpty(v))
                return def;
            return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : def;
        }

        public static float GetFloat(Dictionary<string, string> o, string key, float def)
        {
            if (!o.TryGetValue(key, out var v) || string.IsNullOrEmpty(v))
                return def;
            return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : def;
        }

        public static bool GetBool(Dictionary<string, string> o, string key, bool def)
        {
            if (!o.TryGetValue(key, out var v) || string.IsNullOrEmpty(v))
                return def;
            return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }
        #endregion
    }
}
