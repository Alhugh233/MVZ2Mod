using System;
using System.Collections.Generic;
using System.IO;
using PVZEngine;

namespace ModularCurse
{
    /// <summary>
    /// 诅咒池的当前选择 + 每关记忆。
    ///
    /// 为什么自己落一个文件，而不是塞进游戏的存档结构：
    ///   游戏给外部 mod 的存档入口是 <c>Mod.CreateSaveData()</c> / <c>LoadSaveData(string)</c>，
    ///   要写入自定义字段得再造一个 <c>ModSaveData</c> 子类（IL2CPP 下注入新类型成本高、风险大），
    ///   而我们要存的东西只是「关卡 → 池」这么一张小表。
    ///   因此直接写到游戏为本 mod 分配的存档目录：
    ///     <c>userdata/userN/modular_curse/curse_pool.txt</c>
    ///   内容是每行 <c>命名空间:路径=池编号</c>，与游戏存档同生命周期、互不干扰。
    ///
    /// 键使用关卡的 <c>LevelEngine.StageID</c>，因此是「每关单独记」。
    /// </summary>
    internal static class CursePoolState
    {
        /// <summary>
        /// 旧版（还有「全部」那一档时）叫 curse_pools.txt，池编号也整体偏移 1。
        /// 换文件名而不是做版本兼容：池是单选必选的，旧记录本来就无法直接沿用。
        /// </summary>
        private const string FileName = "curse_pool.txt";

        private static CursePool current = CursePoolRules.Default;
        private static bool fileLoaded;
        private static readonly Dictionary<string, CursePool> perStage
            = new Dictionary<string, CursePool>(StringComparer.Ordinal);

        public static CursePool Current => current;

        /// <summary>
        /// 调试开关（<c>/curse all</c>）：强制显示所有诅咒池，即**不按池过滤**，
        /// 面板里列出全部诅咒（等同于此功能加入之前的行为）。
        /// 只存在于本次运行、不落盘 —— 它是排障用的临时状态，
        /// 而玩家选择的池才是要长期记住的。
        /// </summary>
        private static bool forceAll;

        public static bool ForceAll => forceAll;

        public static void SetForceAll(bool value) => forceAll = value;

        /// <summary>进入关卡时调用：装载该关上次选的池；没有记录则回到默认池。</summary>
        public static void LoadFor(NamespaceID stageID)
        {
            try
            {
                EnsureLoaded();
                var key = Key(stageID);
                var before = current;
                if (key != null && perStage.TryGetValue(key, out var saved) && CursePoolRules.IsValid(saved))
                {
                    current = saved;
                }
                else
                {
                    current = CursePoolRules.Default;
                }
                // 换池时 ReloadChoosingCurses 会被重新调用，这里重复装载不值一提；
                // 但只有真正变化时才打日志，免得一行切池刷两条一样的记录。
                if (current != before)
                {
                    Plugin.Log?.LogInfo("[CursePool] 本关开始池 = " + CursePoolRules.Name(current)
                        + "（关卡 " + (key ?? "<未知>") + "）");
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CursePool] 读取池记录失败: " + e.GetType().Name);
                current = CursePoolRules.Default;
            }
        }

        /// <summary>玩家切换池：立即生效并写入本关记录。</summary>
        public static void Select(NamespaceID stageID, CursePool pool)
        {
            current = pool;
            try
            {
                EnsureLoaded();
                var key = Key(stageID);
                if (key == null)
                    return;
                perStage[key] = pool;
                Save();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CursePool] 保存池记录失败: " + e.GetType().Name);
            }
        }

        #region 存档文件
        private static string Key(NamespaceID stageID)
        {
            if (stageID == null)
                return null;
            var nsp = stageID.SpaceName;
            var path = stageID.Path;
            if (string.IsNullOrEmpty(nsp) || string.IsNullOrEmpty(path))
                return null;
            return nsp + ":" + path;
        }

        private static string FilePath()
        {
            var main = MVZ2.Managers.MainManager.Instance;
            if (main == null)
                return null;
            var save = main.SaveManager;
            if (save == null)
                return null;
            var dir = save.GetUserModSaveDataDirectory(save.GetCurrentUserIndex(), CurseIds.Nsp);
            if (string.IsNullOrEmpty(dir))
                return null;
            return Path.Combine(dir, FileName);
        }

        private static void EnsureLoaded()
        {
            if (fileLoaded)
                return;
            fileLoaded = true;
            try
            {
                var file = FilePath();
                if (file == null || !File.Exists(file))
                    return;
                foreach (var line in File.ReadAllLines(file))
                {
                    var text = line == null ? null : line.Trim();
                    if (string.IsNullOrEmpty(text))
                        continue;
                    var eq = text.LastIndexOf('=');
                    if (eq <= 0 || eq >= text.Length - 1)
                        continue;
                    var key = text.Substring(0, eq).Trim();
                    if (!int.TryParse(text.Substring(eq + 1).Trim(), out var value))
                        continue;
                    var pool = (CursePool)value;
                    if (CursePoolRules.IsValid(pool))
                        perStage[key] = pool;
                }
                Plugin.Log?.LogInfo("[CursePool] 已读取 " + perStage.Count + " 条关卡池记录");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CursePool] 读取 " + FileName + " 失败: " + e.GetType().Name);
            }
        }

        private static void Save()
        {
            try
            {
                var file = FilePath();
                if (file == null)
                    return;
                var dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var lines = new List<string>(perStage.Count);
                foreach (var pair in perStage)
                    lines.Add(pair.Key + "=" + (int)pair.Value);
                File.WriteAllLines(file, lines.ToArray());
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CursePool] 写入 " + FileName + " 失败: " + e.GetType().Name);
            }
        }
        #endregion
    }
}
