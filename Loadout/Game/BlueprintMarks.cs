using System;
using System.Collections.Generic;
using PVZEngine.Level;

namespace Loadout
{
    /// <summary>一张卡上的标记（生成期用）。</summary>
    internal struct BlueprintMark
    {
        /// <summary>这张卡有没有任何标记；false 时别的字段都无意义。</summary>
        public bool Has;
        /// <summary>指定血量；&lt;= 0 表示没设。</summary>
        public float Health;
        public bool NoDaySleep;
        public bool ReverseFaction;
    }

    /// <summary>
    /// 生成期用的**标记速查表**：卡键 → 该应用哪些改动。
    ///
    /// <para>为什么要一张表而不是每次都问 <see cref="BlueprintFlags"/>：
    /// 生成漏斗在**原生调用栈**上，那里绝对不能做磁盘 IO
    /// （<c>BlueprintFlags.Sync</c> 换关卡时会读文件）。所以标记一变就把表重算一次，
    /// 生成时只做一次字典查。</para>
    ///
    /// <para>与旧的「蓝图 id → 实体 id」命中表的区别：**没有实体 id 这一层**。
    /// 键就是「哪一张卡」，由 <see cref="PlacementScope"/> 在放置那一刻算出来，
    /// 所以自然生成的同种实体不可能被误伤。</para>
    /// </summary>
    internal static class BlueprintMarks
    {
        private static readonly Dictionary<string, BlueprintMark> map =
            new Dictionary<string, BlueprintMark>(StringComparer.Ordinal);

        private static bool dirty = true;

        /// <summary>标记改了之后叫一次（下次生成前重算）。</summary>
        public static void Refresh()
        {
            dirty = true;
        }

        /// <summary>这张卡的标记；没有标记返回 <c>Has == false</c>。</summary>
        public static BlueprintMark Get(LevelEngine level, string key)
        {
            if (string.IsNullOrEmpty(key))
                return default(BlueprintMark);
            if (dirty)
                Rebuild(level);
            BlueprintMark mark;
            return map.TryGetValue(key, out mark) ? mark : default(BlueprintMark);
        }

        /// <summary>速查表里一共有几条（自测/日志用）。</summary>
        public static int Count
        {
            get { return map.Count; }
        }

        private static void Rebuild(LevelEngine level)
        {
            dirty = false;
            map.Clear();
            if (level == null)
                return;
            try
            {
                var hp = BlueprintFlags.HealthMap(level);
                foreach (var pair in hp)
                {
                    if (pair.Value <= 0f)
                        continue;
                    BlueprintMark mark;
                    map.TryGetValue(pair.Key, out mark);
                    mark.Has = true;
                    mark.Health = pair.Value;
                    map[pair.Key] = mark;
                }

                var sleep = BlueprintFlags.NoDaySleepTargets(level);
                for (var i = 0; i < sleep.Count; i++)
                    Flag(sleep[i], true, false);

                var reverse = BlueprintFlags.ReverseTargets(level);
                for (var i = 0; i < reverse.Count; i++)
                    Flag(reverse[i], false, true);
            }
            catch (Exception) { }
        }

        private static void Flag(string key, bool noDaySleep, bool reverse)
        {
            if (string.IsNullOrEmpty(key))
                return;
            BlueprintMark mark;
            map.TryGetValue(key, out mark);
            mark.Has = true;
            if (noDaySleep)
                mark.NoDaySleep = true;
            if (reverse)
                mark.ReverseFaction = true;
            map[key] = mark;
        }
    }
}
