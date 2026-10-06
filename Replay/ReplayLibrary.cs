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
    /// <summary>
    /// 一条回放的**只读元信息**（回放列表页需要的全部字段）。
    ///
    /// <para>刻意不装整个 <see cref="ReplayFile"/>：一个回放动辄一万多条事件、
    /// 关键帧是十几万字符的 BSON，列表里几十条全解析一遍要几十兆内存和明显的卡顿。
    /// 这里只读**第一行 header**（<c>.mrep</c> 也只需解开 gzip 的头一段）。</para>
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
        public DateTime Created = DateTime.MinValue;
        public int StartTick;
        public int LastTick;
        public int Tps = 30;
        public long SizeBytes;
        public string Error;

        public bool IsBroken { get { return !string.IsNullOrEmpty(Error); } }

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

        public string CreatedText
        {
            get
            {
                if (Created == DateTime.MinValue)
                    return "时间未知";
                // 当年的存档不写年份：行里横向要摆 5 个单元格，时间列宽是硬约束。
                return Created.Year == DateTime.Now.Year
                    ? Created.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>关卡显示名（查不到就退回原始 ID）。</summary>
        public string StageText
        {
            get { return PrettyStage(Stage); }
        }

        /// <summary>
        /// 把 <c>mvz2:mountain_10</c> 这种 ID 换成游戏里显示的关卡名。
        /// 查不到（meta 没加载 / 是别的 mod 的关卡）就原样返回 ID，**不要**抛出去。
        /// </summary>
        public static string PrettyStage(string stage)
        {
            if (string.IsNullOrEmpty(stage))
                return "（未知关卡）";
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
        /// <summary>列表排序方式。</summary>
        public enum SortBy
        {
            /// <summary>新→旧（默认）。</summary>
            Time,
            /// <summary>按关卡名。</summary>
            Stage,
            /// <summary>时长长→短。</summary>
            Duration,
        }

        public static readonly string[] SortNames = { "按时间", "按关卡", "按时长" };

        // ------------------------------------------------------------------
        // 列举
        // ------------------------------------------------------------------

        /// <summary>
        /// 扫出所有回放。同一个名字同时存在 <c>.mrep</c> 与 <c>.mrep.json</c> 时只算一条，
        /// 优先读 <c>.mrep.json</c>（纯文本，不用解 gzip）。
        /// </summary>
        public static List<ReplayInfo> LoadAll()
        {
            var byName = new Dictionary<string, ReplayInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in ReplayConfig.OutputSearchDirs())
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    continue;
                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch (Exception) { continue; }

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

                    ReplayInfo info;
                    if (byName.TryGetValue(name, out info))
                    {
                        // 已经收过一条；纯文本优先。
                        if (!plain)
                            continue;
                    }
                    else
                    {
                        info = new ReplayInfo { Name = name };
                        byName[name] = info;
                    }
                    info.Path = full;
                    LoadHeader(info);
                }
            }

            var list = new List<ReplayInfo>(byName.Values);
            Sort(list, SortBy.Time);
            return list;
        }

        /// <summary>就地排序。<paramref name="by"/> 相同时有稳定的次级键（名字），保证每次显示顺序一致。</summary>
        public static void Sort(List<ReplayInfo> list, SortBy by)
        {
            if (list == null)
                return;
            Comparison<ReplayInfo> cmp;
            switch (by)
            {
                case SortBy.Stage:
                    cmp = (a, b) =>
                    {
                        var c = string.Compare(a.StageText, b.StageText, StringComparison.CurrentCulture);
                        if (c != 0)
                            return c;
                        return b.Created.CompareTo(a.Created);
                    };
                    break;
                case SortBy.Duration:
                    cmp = (a, b) =>
                    {
                        var c = b.DurationSeconds.CompareTo(a.DurationSeconds);
                        if (c != 0)
                            return c;
                        return b.Created.CompareTo(a.Created);
                    };
                    break;
                default:
                    cmp = (a, b) => b.Created.CompareTo(a.Created);
                    break;
            }
            list.Sort(cmp);
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
                if (Get(head, "magic") != "mrep")
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

        /// <summary>
        /// 按上限裁掉最旧的回放。每次写出新回放后调用一次。
        /// 上限 &lt;= 0 时什么都不做。返回删掉了几条。
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
            if (all.Count <= max)
                return 0;

            // 从最旧的开始删（LoadAll 已按时间倒序，所以从尾部取）。
            int removed = 0;
            for (int i = all.Count - 1; i >= max; i--)
            {
                string err;
                if (Delete(all[i], out err))
                    removed++;
                else
                    Plugin.Log?.LogWarning("[Replay] 清理旧回放失败：" + all[i].Name + " —— " + err);
            }
            if (removed > 0)
                Plugin.Log?.LogInfo("[Replay] 回放数超过上限 " + max + "，已清理 " + removed + " 条最旧的");
            return removed;
        }
    }
}
