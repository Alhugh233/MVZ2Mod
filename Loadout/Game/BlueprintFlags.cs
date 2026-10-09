using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using PVZEngine;
using PVZEngine.Level;
using PVZEngine.SeedPacks;

namespace Loadout
{
    /// <summary>
    /// 面板写在**一张卡**上的标记。
    ///
    /// <para>三个开关（逆阵营放置 / 模仿者 / 白天不睡觉）+ 一个数值（血量），
    /// 另有一个**全局**开关（无条件放置）。</para>
    ///
    /// <para><b>为什么不用 SeedPack 上的自定义属性</b>：本 mod 不注册 <c>PropertyMeta</c>
    /// —— interop 代理程序集里 <c>PropertyRegistryRegionAttribute</c> 已经不是
    /// <c>System.Attribute</c>（CS0616），没法让 <c>PropertyMapper.InitTypePropertyMaps</c>
    /// 认出我们的区域类；而未注册的 <c>PropertyKey</c> 恒为 0，会互相碰撞、也写不进 <c>.lvl</c>。</para>
    ///
    /// <para><b>★ 键 = 「哪一张卡」，不是「哪个蓝图 id」</b>（见 <see cref="BlueprintKeys"/>）：
    /// 自选卡模式下是<b>卡槽下标</b>（<c>s:2</c>），传送带模式下是<b>卡池条目</b>（<c>i:mvz2:xxx</c>）。
    /// 这样「同一张僵尸蓝图带三张、各自不同血量」才成立 —— 按 id 记的话三张会共享同一个值。
    /// 键的解析在放置那一刻做（放的是哪一张卡），所以自然生成的怪物永远不受影响。</para>
    ///
    /// <para><b>存在哪</b>：关卡存档（<c>.lvl</c>）旁边一个同名 <c>.loadoutflags</c> 文本文件，
    /// 一行一条：<c>rf:键</c>（逆阵营）/ <c>cb:键</c>（模仿者）/ <c>sl:键</c>（白天不睡觉）
    /// / <c>hp:键=数值</c>（血量）/ <c>up:</c>（无条件放置，全局一条）。
    /// 老文件里没有 <c>s:</c>/<c>i:</c> 前缀的键按**蓝图 id**读（读入时补 <c>i:</c>），所以旧存档不会失效。</para>
    /// </summary>
    internal static class BlueprintFlags
    {
        private const string PREFIX_REVERSE = "rf:";
        private const string PREFIX_COMMAND = "cb:";
        private const string PREFIX_SLEEP = "sl:";
        private const string PREFIX_HEALTH = "hp:";
        private const string PREFIX_PLACE = "up:";

        private static readonly HashSet<string> reverse = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> command = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> noDaySleep = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> health = new Dictionary<string, float>(StringComparer.Ordinal);
        private static bool unconditionalPlace;
        private static string loadedPath;

        /// <summary>
        /// 现在允许把自定义数据写进存档吗（<see cref="GcFeature.SaveCustomData"/>，Standard 档）。
        ///
        /// <para>关掉时所有标记只在**本次会话的内存**里生效 —— 需求明确要求
        /// 「禁用冷却在存档里写自定义数据的功能仅在 Standard 下实现，Safe 模式只写到内存」。</para>
        /// </summary>
        public static bool CanPersist
        {
            get { return CompatConfig.IsEnabled(GcFeature.SaveCustomData); }
        }

        // ------------------------------------------------------------------
        // 对外
        // ------------------------------------------------------------------

        /// <summary>必要时从磁盘刷新（关卡换了就重读）。</summary>
        public static void Sync(LevelEngine level)
        {
            try
            {
                var path = PathFor(level);
                if (path == loadedPath)
                    return;
                Load(path);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("bflags-sync", "[Flags] 读标记失败：" + e.Message);
            }
        }

        public static bool GetReverse(LevelEngine level, string key)
        {
            return Contains(level, reverse, key);
        }

        public static bool SetReverse(LevelEngine level, string key, bool value)
        {
            return Set(level, reverse, PREFIX_REVERSE, key, value);
        }

        public static bool GetCommandBlock(LevelEngine level, string key)
        {
            return Contains(level, command, key);
        }

        public static bool SetCommandBlock(LevelEngine level, string key, bool value)
        {
            return Set(level, command, PREFIX_COMMAND, key, value);
        }

        // ---- 白天不睡觉（按卡）------------------------------------------------

        public static bool GetNoDaySleep(LevelEngine level, string key)
        {
            return Contains(level, noDaySleep, key);
        }

        public static bool SetNoDaySleep(LevelEngine level, string key, bool value)
        {
            return Set(level, noDaySleep, PREFIX_SLEEP, key, value);
        }

        /// <summary>全部标了「白天不睡觉」的卡键（补丁那边用）。</summary>
        public static List<string> NoDaySleepTargets(LevelEngine level)
        {
            Sync(level);
            lock (noDaySleep)
                return new List<string>(noDaySleep);
        }

        // ---- 血量（按卡，带数值）----------------------------------------------

        /// <summary>这张卡被指定生成的血量；没设过返回 &lt;0。</summary>
        public static float GetHealth(LevelEngine level, string key)
        {
            if (string.IsNullOrEmpty(key))
                return -1f;
            Sync(level);
            lock (health)
            {
                float value;
                return health.TryGetValue(key, out value) ? value : -1f;
            }
        }

        public static bool SetHealth(LevelEngine level, string key, float value)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            try
            {
                Sync(level);
                lock (health)
                {
                    if (value < 0f)
                        health.Remove(key);     // 负值 = 恢复原版
                    else
                        health[key] = value;
                }
                Save(level);
                return true;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("bflags-hp", "[Flags] 写血量失败：" + e.Message);
                return false;
            }
        }

        /// <summary>全部设过血量的卡键 &amp; 数值（补丁那边用）。</summary>
        public static Dictionary<string, float> HealthMap(LevelEngine level)
        {
            Sync(level);
            lock (health)
                return new Dictionary<string, float>(health, StringComparer.Ordinal);
        }

        /// <summary>清掉这张卡上的**全部**标记（换卡 / 还原时用）。</summary>
        public static bool ClearAll(LevelEngine level, string key)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            try
            {
                Sync(level);
                lock (reverse) reverse.Remove(key);
                lock (command) command.Remove(key);
                lock (noDaySleep) noDaySleep.Remove(key);
                lock (health) health.Remove(key);
                Save(level);
                BlueprintSpawnPatch.Refresh();
                ReverseFactionPatch.Refresh();
                return true;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("bflags-clear", "[Flags] 清标记失败：" + e.Message);
                return false;
            }
        }

        // ---- 冷却备份（「禁用冷却」用）--------------------------------------------

        // ---- 无条件放置（全局）------------------------------------------------

        public static bool GetUnconditionalPlace(LevelEngine level)
        {
            Sync(level);
            return unconditionalPlace;
        }

        public static bool SetUnconditionalPlace(LevelEngine level, bool value)
        {
            try
            {
                Sync(level);
                unconditionalPlace = value;
                Save(level);
                return true;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("bflags-up", "[Flags] 写无条件放置失败：" + e.Message);
                return false;
            }
        }

        /// <summary>全部标了逆阵营的卡键（补丁那边要用）。</summary>
        public static List<string> ReverseTargets(LevelEngine level)
        {
            Sync(level);
            lock (reverse)
                return new List<string>(reverse);
        }

        /// <summary>切换关卡时把标记文件一起搬过去。</summary>
        public static void CarryOver(string fromPath, string toPath)
        {
            try
            {
                var src = SidePath(fromPath);
                var dst = SidePath(toPath);
                if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst) || !File.Exists(src))
                    return;
                var dir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.Copy(src, dst, true);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 内部
        // ------------------------------------------------------------------

        private static bool Contains(LevelEngine level, HashSet<string> set, string key)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            Sync(level);
            lock (set)
                return set.Contains(key);
        }

        private static bool Set(LevelEngine level, HashSet<string> set, string prefix, string key, bool value)
        {
            if (string.IsNullOrEmpty(key))
                return false;
            try
            {
                Sync(level);
                lock (set)
                {
                    if (value)
                        set.Add(key);
                    else
                        set.Remove(key);
                }
                Save(level);
                return true;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("bflags-set", "[Flags] 写标记失败：" + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 规范化一个从文件里读出来的键。
        /// 老文件里没有 <c>s:</c>/<c>i:</c> 前缀的键原本就是**蓝图 id**，补上 <c>i:</c> 即可继续用。
        /// </summary>
        private static string NormalizeKey(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;
            if (text.StartsWith(BlueprintKeys.SlotTag, StringComparison.Ordinal)
                || text.StartsWith(BlueprintKeys.IdTag, StringComparison.Ordinal))
                return text;
            return BlueprintKeys.OfIdText(text);
        }

        private static string SidePath(string statePath)
        {
            return string.IsNullOrEmpty(statePath) ? null : statePath + ".loadoutflags";
        }

        private static string PathFor(LevelEngine level)
        {
            try
            {
                var main = LevelFacade.Main;
                var manager = main == null ? null : main.LevelManager;
                var stage = LevelFacade.GetStageID(level);
                if (manager == null || !NamespaceID.IsValid(stage))
                    return null;
                return SidePath(manager.GetLevelStatePath(stage));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Load(string path)
        {
            reverse.Clear();
            command.Clear();
            noDaySleep.Clear();
            health.Clear();
            unconditionalPlace = false;
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var text = line == null ? null : line.Trim();
                        if (string.IsNullOrEmpty(text))
                            continue;
                        if (text.StartsWith(PREFIX_REVERSE, StringComparison.Ordinal))
                            Add(reverse, text.Substring(PREFIX_REVERSE.Length));
                        else if (text.StartsWith(PREFIX_COMMAND, StringComparison.Ordinal))
                            Add(command, text.Substring(PREFIX_COMMAND.Length));
                        else if (text.StartsWith(PREFIX_SLEEP, StringComparison.Ordinal))
                            Add(noDaySleep, text.Substring(PREFIX_SLEEP.Length));
                        else if (text.StartsWith(PREFIX_HEALTH, StringComparison.Ordinal))
                            ParseHealth(text.Substring(PREFIX_HEALTH.Length));
                        else if (text.StartsWith(PREFIX_PLACE, StringComparison.Ordinal))
                            unconditionalPlace = true;
                        else
                            Add(reverse, text);   // 兼容没有前缀的老文件
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("bflags-read", "[Flags] 解析标记文件失败：" + e.Message);
            }
            loadedPath = path;
            ReverseFactionPatch.Refresh();
            BlueprintSpawnPatch.Refresh();
        }

        /// <summary><c>hp:卡键=数值</c> 解析（空格/小数都容）。</summary>
        private static void ParseHealth(string body)
        {
            if (string.IsNullOrEmpty(body))
                return;
            var eq = body.LastIndexOf('=');
            if (eq <= 0 || eq >= body.Length - 1)
                return;
            var key = NormalizeKey(body.Substring(0, eq));
            float value;
            if (string.IsNullOrEmpty(key))
                return;
            if (!float.TryParse(body.Substring(eq + 1).Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
                return;
            health[key] = value;
        }

        /// <summary>把一行里的键规范化后放进集合（空则忽略）。</summary>
        private static void Add(HashSet<string> set, string body)
        {
            var key = NormalizeKey(body);
            if (!string.IsNullOrEmpty(key))
                set.Add(key);
        }

        private static void Save(LevelEngine level)
        {
            // Safe 档：只写内存。
            if (!CanPersist)
            {
                Plugin.LogOnce("bflags-nosave", "[Flags] 当前兼容性等级不允许写自定义存档数据（需要 Standard），标记只在本次会话生效");
                return;
            }
            try
            {
                var path = PathFor(level);
                if (string.IsNullOrEmpty(path))
                    return;
                var lines = new List<string>();
                lock (reverse)
                {
                    foreach (var text in reverse)
                        lines.Add(PREFIX_REVERSE + text);
                }
                lock (command)
                {
                    foreach (var text in command)
                        lines.Add(PREFIX_COMMAND + text);
                }
                lock (noDaySleep)
                {
                    foreach (var text in noDaySleep)
                        lines.Add(PREFIX_SLEEP + text);
                }
                lock (health)
                {
                    foreach (var pair in health)
                        lines.Add(PREFIX_HEALTH + pair.Key + "="
                            + pair.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                }
                if (unconditionalPlace)
                    lines.Add(PREFIX_PLACE);

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (lines.Count == 0)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                else
                {
                    lines.Sort(StringComparer.Ordinal);
                    File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
                }
                loadedPath = path;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("bflags-write", "[Flags] 写标记文件失败：" + e.Message);
            }
        }
    }
}
