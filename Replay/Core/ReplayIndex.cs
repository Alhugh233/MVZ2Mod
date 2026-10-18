using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Replay
{
    /// <summary>
    /// 一条回放的**健康度**（列表里名字的颜色、播放前的弹窗都由它决定）。
    ///
    /// <para>它是一个**持久化**的值：记录的是「上一次播放实际发生了什么」，
    /// 所以只能由播放器来写。<see cref="Suspect"/> 这一档也可以由玩家直接标记
    /// （「我知道这条有问题，别老提醒我」），但当前只有播放器会写。</para>
    /// </summary>
    internal enum ReplayHealth
    {
        /// <summary>没发现任何问题（或播放到一半以上都没出问题）。</summary>
        Ok = 0,
        /// <summary>已知有问题，但不确定这次会不会复现。</summary>
        Suspect = 1,
        /// <summary>上次播放明确出了问题（状态不一致）。</summary>
        Bad = 2,
    }

    /// <summary>
    /// 回放目录索引文件（<c>&lt;回放目录&gt;/_replay_index.json</c>）里的一条记录。
    ///
    /// <para>两个用途：</para>
    /// <list type="number">
    /// <item><b>省掉重复的 header 读取。</b>回放列表每次刷新都要看几十个文件的
    /// <c>stage</c>/<c>created</c>/<c>ver</c>/<c>envHash</c>，而 <c>.mrep</c> 是 gzip，
    /// 每条都要解一次压缩头。索引把「上次读到的结果」连同
    /// <c>Size</c> + <c>LastWriteTimeUtc</c> 一起存下来；两者都没变就直接复用。</item>
    /// <item><b>存健康度。</b>「这条回放上次播得怎么样」没有任何别的自然归属 ——
    /// 回放文件本身是玩家可以随便拷贝/导出的只读产物，而索引是「本机这一份库」的状态。</item>
    /// </list>
    ///
    /// <para>索引**永远可以丢**：内容缺失/损坏/版本不符时静默重建，只是慢一点。</para>
    /// </summary>
    internal sealed class ReplayIndexEntry
    {
        /// <summary>裸名字（不含扩展名），也是条目的主键。</summary>
        public string Name;

        /// <summary>上次实际读取的那个文件（优先 <c>.mrep.json</c>，没有就是 <c>.mrep</c>）。</summary>
        public string FileName;

        /// <summary>文件长度（字节）。</summary>
        public long Size;

        /// <summary><c>LastWriteTimeUtc.Ticks</c>。</summary>
        public long WrittenTicks;

        /// <summary><c>CreationTimeUtc.Ticks</c> —— 列表显示的「添加时间」。</summary>
        public long AddedTicks;

        /// <summary>录制开始时刻（header 的 <c>created</c>）的 <c>Ticks</c>，0 = 不知道。</summary>
        public long RecordedTicks;

        // ---- header 快照 ----
        public string Stage;
        public string Area;
        public string Difficulty;
        public string GameVersion;
        public string EnvHash;
        public int EnvCount;
        public int StartTick;
        public int LastTick;
        public int Tps = 30;
        public int Version;
        public int SpecVersion;
        public bool Debug;

        /// <summary>header 读取失败的原因（空 = 读得出来）。</summary>
        public string Error;

        // ---- 健康度 ----
        public ReplayHealth Health;
        /// <summary>上次播放结束时累计的不一致条数（0 = 没记过）。</summary>
        public int LastMismatch;
        /// <summary>上次播放中**第一次**出现不一致的 tick（0 = 没记录）。</summary>
        public int LastBadTick;
        /// <summary>健康度最后变化的时刻（<c>UtcNow.Ticks</c>），给界面排序/提示用。</summary>
        public long HealthTicks;

        /// <summary>
        /// **在这条环境哈希下**被完整验证过（一次没有指纹不一致地跑到一半以上）。
        ///
        /// <para>为什么要它：黄标的判据里有一半是「录制环境与当前不同 / 文件没环境记录 /
        /// 游戏版本不同」，这些是**静态推断**。但玩家真的完整播完一次、指纹全对上之后，
        /// 静态推断就该让位给实测 —— 否则那条回放永远黄着，玩家会以为它有问题。</para>
        ///
        /// <para>存的是**当时的当前环境哈希**（不是文件里的），空 = 没验证过。
        /// 环境一变（换 mod / 升级游戏）哈希就变了，黄标自动回来。</para>
        /// </summary>
        public string VerifiedEnv;

        /// <summary>这一条是否可供复用（header 快照有效）。</summary>
        public bool Usable
        {
            get { return !string.IsNullOrEmpty(FileName) && (!string.IsNullOrEmpty(Error) || SpecVersion > 0 || Version > 0); }
        }

        /// <summary>
        /// 「文件有没有变过」——变了就不能复用 header 快照。
        /// </summary>
        public bool Matches(long size, long writtenTicks)
        {
            return Size == size && WrittenTicks == writtenTicks;
        }
    }

    /// <summary>
    /// 回放目录索引的读写。
    ///
    /// <para><b>格式</b>与 <c>.mrep</c> 一样是**逐行扁平 JSON**（第 1 行元信息，其余每行一条），
    /// 这样可以直接复用 <see cref="FlatJson"/>，不必为嵌套结构再写一个解析器。</para>
    ///
    /// <para><b>写盘是原子的</b>（先写 <c>.tmp</c> 再 <c>File.Replace</c>/<c>Move</c>），
    /// 因为游戏随时可能被强退，而一个半截的索引会让整个列表退回「全量重读」——虽然不致命，
    /// 但没必要。</para>
    /// </summary>
    internal static class ReplayIndex
    {
        internal const int FormatVersion = 1;

        internal const string FileName = "_replay_index.json";

        /// <summary>索引文件路径（回放目录下）。</summary>
        internal static string PathIn(string dir)
        {
            return string.IsNullOrEmpty(dir) ? null : System.IO.Path.Combine(dir, FileName);
        }

        /// <summary>读一份索引。任何问题都返回「空表」，绝不抛。</summary>
        internal static Dictionary<string, ReplayIndexEntry> Load(string dir)
        {
            var map = new Dictionary<string, ReplayIndexEntry>(StringComparer.OrdinalIgnoreCase);
            var path = PathIn(dir);
            if (path == null || !File.Exists(path))
                return map;
            try
            {
                int version = 0;
                bool first = true;
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    Dictionary<string, string> m;
                    string err;
                    if (!FlatJson.TryParse(line, out m, out err))
                        return new Dictionary<string, ReplayIndexEntry>(StringComparer.OrdinalIgnoreCase);

                    if (first)
                    {
                        first = false;
                        version = GetInt(m, "v", 0);
                        if (version != FormatVersion)
                            return new Dictionary<string, ReplayIndexEntry>(StringComparer.OrdinalIgnoreCase);
                        continue;
                    }

                    var e = ReadEntry(m);
                    if (!string.IsNullOrEmpty(e.Name))
                        map[e.Name] = e;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 读回放索引失败（当成没有索引）："
                    + e.GetType().Name + ": " + e.Message);
                return new Dictionary<string, ReplayIndexEntry>(StringComparer.OrdinalIgnoreCase);
            }
            return map;
        }

        /// <summary>写一份索引。失败只记日志（索引是纯优化，绝不能因此让列表打不开）。</summary>
        internal static void Save(string dir, Dictionary<string, ReplayIndexEntry> map)
        {
            var path = PathIn(dir);
            if (path == null || map == null)
                return;
            var tmp = path + ".tmp";
            try
            {
                var sb = new StringBuilder(256 + map.Count * 256);
                sb.Append("{\"v\":").Append(Json.Num(FormatVersion))
                  .Append(",\"env\":").Append(Json.Str(ReplayLibrary.CurrentEnvHash()))
                  .Append('}').Append('\n');
                foreach (var e in map.Values)
                    WriteEntry(sb, e);

                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, null); }
                    catch (Exception) { File.Copy(tmp, path, true); File.Delete(tmp); }
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 写回放索引失败（不影响使用）："
                    + e.GetType().Name + ": " + e.Message);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { }
            }
        }

        /// <summary>
        /// 单独更新一条的健康度（播放结束时调）。
        ///
        /// <para>**独立的读-改-写**：播放结束发生在关卡里，而回放页早就关了、
        /// 那份内存里的索引也已经不在了。这个操作一次只跑一次，开销可以忽略。</para>
        /// </summary>
        internal static void UpdateHealth(string dir, string name, ReplayHealth health, int lastMismatch, int lastBadTick)
        {
            UpdateHealth(dir, name, health, lastMismatch, lastBadTick, null);
        }

        /// <summary>
        /// 同上，并且记下「是在哪个环境哈希下验证的」。
        ///
        /// <para><paramref name="verifiedEnv"/> 只有「跑完了且一条指纹都没错」时才传；
        /// 传 null/空 就是**清掉**之前的验证记录（相等于「这次又没把握」）。</para>
        /// </summary>
        internal static void UpdateHealth(string dir, string name, ReplayHealth health,
            int lastMismatch, int lastBadTick, string verifiedEnv)
        {
            if (string.IsNullOrEmpty(dir))
                return;
            name = NormalizeName(name);
            if (string.IsNullOrEmpty(name))
                return;
            try
            {
                var map = Load(dir);
                // 历史遗留：早期版本用「带扩展名的文件名」当键写过健康度
                //（`xxx.mrep`），于是列表里那条（裸名）永远读不到 → 黄标降不下来。
                // 这里顺手把这类脏键清掉，免得它一直躺在索引里。
                RemoveExtKeys(map, name);
                ReplayIndexEntry e;
                if (!map.TryGetValue(name, out e) || e == null)
                {
                    e = new ReplayIndexEntry { Name = name };
                    map[name] = e;
                }
                e.Health = health;
                e.LastMismatch = lastMismatch;
                e.LastBadTick = lastBadTick;
                e.HealthTicks = DateTime.UtcNow.Ticks;
                e.VerifiedEnv = string.IsNullOrEmpty(verifiedEnv) ? null : verifiedEnv;
                Save(dir, map);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 更新回放健康度失败："
                    + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 索引的主键是**裸名字**（不含 <c>.mrep</c>）。任何外部传进来的名字都要过这一道 ——
        /// 否则会写到一条谁都读不到的记录上（实测就是「黄标怎么都不降级」的真凶）。
        /// </summary>
        internal static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            // 先砍掉 .json 再砍 .mrep：两种文件名都见过（.mrep / .mrep.json）。
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 5);
            if (name.EndsWith(".mrep", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 5);
            return name.Trim();
        }

        /// <summary>把「带扩展名的同名脏键」从索引里清掉。</summary>
        private static void RemoveExtKeys(Dictionary<string, ReplayIndexEntry> map, string bare)
        {
            if (map == null || string.IsNullOrEmpty(bare))
                return;
            var junk = new List<string>(2);
            foreach (var kv in map)
            {
                if (string.Equals(kv.Key, bare, StringComparison.Ordinal))
                    continue;
                if (string.Equals(NormalizeName(kv.Key), bare, StringComparison.Ordinal))
                    junk.Add(kv.Key);
            }
            for (int i = 0; i < junk.Count; i++)
                map.Remove(junk[i]);
        }

        /// <summary>读一条的健康度（找不到返回 <see cref="ReplayHealth.Ok"/> + 0）。</summary>
        internal static void ReadHealth(Dictionary<string, ReplayIndexEntry> map, string name,
            out ReplayHealth health, out int lastMismatch)
        {
            health = ReplayHealth.Ok;
            lastMismatch = 0;
            ReplayIndexEntry e;
            if (map != null && !string.IsNullOrEmpty(name) && map.TryGetValue(name, out e) && e != null)
            {
                health = e.Health;
                lastMismatch = e.LastMismatch;
            }
        }

        // ------------------------------------------------------------------
        // 单行编解码
        // ------------------------------------------------------------------

        private static void WriteEntry(StringBuilder sb, ReplayIndexEntry e)
        {
            sb.Append("{\"n\":").Append(Json.Str(e.Name));
            sb.Append(",\"f\":").Append(Json.Str(e.FileName));
            sb.Append(",\"sz\":").Append(Json.Num(e.Size));
            sb.Append(",\"wt\":").Append(Json.Num(e.WrittenTicks));
            sb.Append(",\"ct\":").Append(Json.Num(e.AddedTicks));
            if (!string.IsNullOrEmpty(e.Error))
            {
                // 读不出来的条目只存错误，不存半套 header 字段。
                sb.Append(",\"err\":").Append(Json.Str(e.Error));
            }
            else
            {
                sb.Append(",\"rt\":").Append(Json.Num(e.RecordedTicks));
                // ⚠️ 全部用 Str(Safe)：Json.Str(null) 写的是裸 null，
                // 而 FlatJson 读裸 token 会得到**字符串 "null"** —— 那就成了
                // 一个看得见、却怎么也查不到出处的脏值（列表上直接显示 "null"）。
                sb.Append(",\"stage\":").Append(Json.Str(Safe(e.Stage)));
                sb.Append(",\"area\":").Append(Json.Str(Safe(e.Area)));
                sb.Append(",\"diff\":").Append(Json.Str(Safe(e.Difficulty)));
                sb.Append(",\"game\":").Append(Json.Str(Safe(e.GameVersion)));
                sb.Append(",\"eh\":").Append(Json.Str(Safe(e.EnvHash)));
                sb.Append(",\"ec\":").Append(Json.Num(e.EnvCount));
                sb.Append(",\"st\":").Append(Json.Num(e.StartTick));
                sb.Append(",\"lt\":").Append(Json.Num(e.LastTick));
                sb.Append(",\"tps\":").Append(Json.Num(e.Tps));
                sb.Append(",\"ver\":").Append(Json.Num(e.Version));
                sb.Append(",\"spec\":").Append(Json.Num(e.SpecVersion));
                sb.Append(",\"dbg\":").Append(Json.Bool(e.Debug));
            }
            if (e.Health != ReplayHealth.Ok)
            {
                sb.Append(",\"hp\":").Append(Json.Num((int)e.Health));
                sb.Append(",\"lm\":").Append(Json.Num(e.LastMismatch));
                sb.Append(",\"lb\":").Append(Json.Num(e.LastBadTick));
                sb.Append(",\"ht\":").Append(Json.Num(e.HealthTicks));
            }
            if (!string.IsNullOrEmpty(e.VerifiedEnv))
                sb.Append(",\"ve\":").Append(Json.Str(e.VerifiedEnv));
            sb.Append('}').Append('\n');
        }

        private static ReplayIndexEntry ReadEntry(Dictionary<string, string> m)
        {
            var e = new ReplayIndexEntry();
            e.Name = Get(m, "n");
            e.FileName = Get(m, "f");
            e.Size = GetLong(m, "sz", 0);
            e.WrittenTicks = GetLong(m, "wt", 0);
            e.AddedTicks = GetLong(m, "ct", 0);
            e.Error = Get(m, "err");
            if (string.IsNullOrEmpty(e.Error))
            {
                e.RecordedTicks = GetLong(m, "rt", 0);
                e.Stage = Get(m, "stage");
                e.Area = Get(m, "area");
                e.Difficulty = Get(m, "diff");
                e.GameVersion = Get(m, "game");
                e.EnvHash = Get(m, "eh");
                e.EnvCount = GetInt(m, "ec", 0);
                e.StartTick = GetInt(m, "st", 0);
                e.LastTick = GetInt(m, "lt", 0);
                e.Tps = GetInt(m, "tps", 30);
                e.Version = GetInt(m, "ver", 0);
                e.SpecVersion = GetInt(m, "spec", 0);
                e.Debug = string.Equals(Get(m, "dbg"), "true", StringComparison.OrdinalIgnoreCase);
            }
            int hp = GetInt(m, "hp", 0);
            e.Health = hp >= 1 && hp <= 2 ? (ReplayHealth)hp : ReplayHealth.Ok;
            e.LastMismatch = GetInt(m, "lm", 0);
            e.LastBadTick = GetInt(m, "lb", 0);
            e.HealthTicks = GetLong(m, "ht", 0);
            e.VerifiedEnv = Get(m, "ve");
            return e;
        }

        private static string Get(Dictionary<string, string> m, string key)
        {
            string v;
            if (m == null || !m.TryGetValue(key, out v))
                return null;
            // 裸 null 读回来是字符串 "null"，当成空值。
            return v == "null" ? null : v;
        }

        private static string Safe(string s)
        {
            return s ?? string.Empty;
        }

        private static int GetInt(Dictionary<string, string> m, string key, int def)
        {
            int r;
            var v = Get(m, key);
            return !string.IsNullOrEmpty(v)
                && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def;
        }

        private static long GetLong(Dictionary<string, string> m, string key, long def)
        {
            long r;
            var v = Get(m, key);
            return !string.IsNullOrEmpty(v)
                && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def;
        }
    }
}
