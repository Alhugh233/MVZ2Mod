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

        public override string ToString()
        {
            return "t=" + Tick + " en=" + Enemies + " w=" + Wave + " fl=" + Flags + " cl=" + Cleared
                + " ec=" + EntityCount + " eh=" + (EntityHash ?? "-") + " r=" + Rng
                + " ed=[" + (EntityDefs ?? "-") + "]";
        }
    }

    /// <summary>一个已载入内存的回放文件。</summary>
    internal sealed class ReplayFile
    {
        public string Path;

        public int Version;
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

        /// <summary>能不能播：必须有起始关键帧（否则无法重建关卡状态）。</summary>
        public bool CanPlay => HasKeyframe && !string.IsNullOrEmpty(KeyframeBson)
            && !string.IsNullOrEmpty(Stage) && !string.IsNullOrEmpty(Area);

        public string Describe()
        {
            return "ver=" + Version + " 关卡=" + Stage + " 面积=" + Area + " 难度=" + Difficulty
                + " 种子=" + Seed + " tick=" + StartTick + ".." + LastTick
                + " 关键帧=" + (HasKeyframe ? (KeyframeTick + "(" + KeyframeBson.Length + "字符)") : "无")
                + " 事件=" + Events.Count + " 指纹=" + Fingerprints.Count;
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
            if (GetString(head, "magic") != "mrep")
            {
                error = "不是 .mrep 文件（magic=" + GetString(head, "magic") + "）";
                return null;
            }

            file.Version = GetInt(head, "ver", 0);
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
                if (kind == "keyframe")
                {
                    file.KeyframeTick = GetInt(obj, "tick", 0);
                    file.KeyframeBson = GetString(obj, "state");
                    file.HasKeyframe = file.HasKeyframe && !string.IsNullOrEmpty(file.KeyframeBson);
                }
                else if (kind == "fp")
                {
                    file.Fingerprints.Add(new ReplayFingerprint
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
