using System;
using System.Text;
using MVZ2.Level.Entities;
using MVZ2Logic.Level;
using PVZEngine.Entities;
using PVZEngine.Level;

namespace Replay
{
    /// <summary>
    /// 回放排障探针：注入 <c>blueprint</c> / <c>grid</c> 事件时，把**游戏当时认为的状态**
    /// 写进日志。
    ///
    /// <para><b>为什么需要它：</b>这两类事件注入后，游戏**完全可能什么都不做** ——
    /// 手持物不是那个种子包、格子不可放置、能量不够…… 而回放器从外部看不出区别，
    /// 只能看到「事件已经发出去了」。于是「某个器械没放下来」这种问题就查不下去，
    /// 只能在「id 对不上 / 事件被跳过 / 计时器漂移」之间瞎猜。</para>
    ///
    /// <para><b>关键信号：放置成功后游戏会把手持物清空</b>（<c>ResetHeldItem</c>）。
    /// 所以每次 <c>grid</c> Down 都记录「注入前手持什么 / 注入后手持什么」：</para>
    /// <list type="bullet">
    /// <item>前空后空 → 前面的**选卡就没生效**（问题在选择，不在放置）；</item>
    /// <item>前非空后非空（值不变）→ 游戏**拒绝了这次放置**（格子 / 能量 / 地形）；</item>
    /// <item>前非空后空 → 放置成功。</item>
    /// </list>
    ///
    /// <para>由 <see cref="ReplayConfig.ProbeEnabled"/> 控制，默认关（会刷屏）。</para>
    /// </summary>
    internal static class ReplayProbe
    {
        private static int used;

        public static bool Enabled
        {
            get
            {
                try { return ReplayConfig.ProbeEnabled != null && ReplayConfig.ProbeEnabled.Value; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>播放开始时重置配额。</summary>
        public static void Reset()
        {
            used = 0;
        }

        /// <summary>把一行探针信息写进日志（带配额限制）。</summary>
        public static void Write(string msg)
        {
            if (!Enabled)
                return;
            int max = 600;
            try
            {
                if (ReplayConfig.ProbeMaxLines != null)
                    max = ReplayConfig.ProbeMaxLines.Value;
            }
            catch (Exception) { }
            if (max > 0 && used >= max)
            {
                if (used == max)
                {
                    used++;
                    Plugin.Log?.LogWarning("[ReplayProbe] 已达上限 " + max + " 行，后续探针输出被省略。");
                }
                return;
            }
            used++;
            try { Plugin.Log?.LogInfo("[ReplayProbe] " + msg); } catch (Exception) { }
        }

        /// <summary>
        /// 描述当前手持物。形如 <c>blueprint #9=mvz2:maplewheel(cost 175)</c>，
        /// 没拿东西就是 <c>none</c>。
        ///
        /// <para>用 <c>IsHoldingClassicBlueprint</c> 反查是第几个种子包，而不是去读
        /// <c>IHeldItemData</c> 内部的字段 —— 后者在 interop 里是属性，形状容易猜错，
        /// 而前者是游戏自己暴露的判定函数，永远与游戏一致。</para>
        /// </summary>
        public static string Held(LevelEngine lvl)
        {
            if (lvl == null)
                return "?";
            var sb = new StringBuilder(48);
            try
            {
                var type = Ids.Ns(LogicLevelExt.GetHeldItemType(lvl));
                sb.Append(string.IsNullOrEmpty(type) ? "none" : type);
            }
            catch (Exception e)
            {
                return "查询手持物类型失败:" + e.GetType().Name;
            }

            try
            {
                int slotCount = SafeSlotCount(lvl);
                for (int i = 0; i < slotCount; i++)
                {
                    bool isBlueprint;
                    try { isBlueprint = LogicLevelExt.IsHoldingClassicBlueprint(lvl, i); }
                    catch (Exception) { continue; }
                    if (!isBlueprint)
                        continue;
                    sb.Append(" #").Append(i);
                    try
                    {
                        var sp = lvl.GetSeedPackAt(i);
                        if (sp != null)
                        {
                            sb.Append('=').Append(Ids.Ns(sp.GetDefinitionID()));
                            sb.Append("(cost ").Append(sp.GetCost()).Append(')');
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                sb.Append(" [蓝图查询失败:").Append(e.GetType().Name).Append(']');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 一条实体事件的对照：录下的 (id/def/分层) vs 回放解析出来的 (id/def/分层)，
        /// 外加那个格子里所有实体的清单。专门用来查「同格多器械时挖错株」。
        /// </summary>
        public static string EntityLine(ReplayEvent ev, LevelEngine lvl, Entity resolved)
        {
            var sb = new StringBuilder(220);
            sb.Append("entity t=").Append(ev.Tick).Append(' ').Append(DescribeInteraction(ev.Interaction));
            sb.Append(" 录制=[#").Append(ev.EntityId).Append(' ').Append(ev.EntityDef ?? "?")
              .Append(" 分层=").Append(ev.EntityLayer == null ? "(旧文件没记)" : ("'" + ev.EntityLayer + "'")).Append(']');
            if (resolved == null)
            {
                sb.Append(" 解析=失败");
            }
            else
            {
                sb.Append(" 解析=[#").Append(SafeId(resolved)).Append(' ')
                  .Append(ReplayRecorder.DefIdOf(resolved) ?? "?")
                  .Append(" 分层='").Append(ReplayRecorder.LayerKeyOf(lvl, resolved) ?? "?").Append("']");
            }
            sb.Append(" 格子=").Append(CellOf(lvl, resolved, ev));
            return sb.ToString();
        }

        private static long SafeId(Entity e)
        {
            try { return e != null ? e.ID : -1; } catch (Exception) { return -1; }
        }

        private static string DescribeInteraction(int ia)
        {
            switch (ia)
            {
                case 0: return "Hover";
                case 1: return "Enter";
                case 2: return "Stay";
                case 3: return "Exit";
                case 4: return "Down";
                case 5: return "Hold";
                case 6: return "Up";
                case 7: return "Click";
                case 12: return "Streak";
                case 13: return "Release";
                default: return "ia=" + ia;
            }
        }

        /// <summary>记录坐标所在格子里所有实体（带各自分层）—— 同格歧义的直接证据。</summary>
        private static string CellOf(LevelEngine lvl, Entity resolved, ReplayEvent ev)
        {
            if (lvl == null)
                return "?";
            float x, z;
            if (resolved != null)
            {
                try { var p = resolved.Position; x = p.x; z = p.z; }
                catch (Exception) { x = ev.Ex; z = ev.Ez; }
            }
            else
            {
                x = ev.Ex;
                z = ev.Ez;
            }
            try
            {
                int col = lvl.GetColumn(x), lane = lvl.GetLane(z);
                var all = ReplayRecorder.EnumerateEntities(lvl);
                if (all == null)
                    return "枚举失败";
                var sb = new StringBuilder(96);
                sb.Append('(').Append(lane).Append(',').Append(col).Append(") ");
                int n = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    var e = all[i];
                    if (e == null || e.Pointer == IntPtr.Zero)
                        continue;
                    UnityEngine.Vector3 p;
                    try { p = e.Position; } catch (Exception) { continue; }
                    if (lvl.GetLane(p.z) != lane || lvl.GetColumn(p.x) != col)
                        continue;
                    if (n > 0)
                        sb.Append(" + ");
                    sb.Append(ReplayRecorder.DefIdOf(e)).Append('#').Append(SafeId(e))
                      .Append('[').Append(ReplayRecorder.LayerKeyOf(lvl, e) ?? "?").Append(']');
                    n++;
                }
                return n == 0 ? sb.Append("(空)").ToString() : sb.ToString();
            }
            catch (Exception e2)
            {
                return "查询失败:" + e2.GetType().Name;
            }
        }

        /// <summary>卡片槽完整列表，形如 <c>#0=mvz2:furnace(cost 100) #1=…</c>。</summary>
        public static string SeedPacks(LevelEngine lvl)
        {
            if (lvl == null)
                return "?";
            var sb = new StringBuilder(160);
            try
            {
                int slotCount = SafeSlotCount(lvl);
                sb.Append("槽位 ").Append(slotCount).Append('：');
                for (int i = 0; i < slotCount; i++)
                {
                    if (i > 0)
                        sb.Append(' ');
                    sb.Append('#').Append(i).Append('=');
                    try
                    {
                        var sp = lvl.GetSeedPackAt(i);
                        if (sp == null)
                        {
                            sb.Append('空');
                            continue;
                        }
                        sb.Append(Ids.Ns(sp.GetDefinitionID()));
                        sb.Append('/').Append(sp.GetCost());
                    }
                    catch (Exception) { sb.Append('?'); }
                }
            }
            catch (Exception e)
            {
                sb.Append("查询失败:").Append(e.GetType().Name);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 某个格子上的实体（按坐标反推 lane/column，不依赖格子的内部结构）。
        /// 用于判断「这个格子是不是被占了 / 有没有睡莲」。
        /// </summary>
        public static string GridAt(LevelEngine lvl, int lane, int column)
        {
            if (lvl == null)
                return "?";
            try
            {
                var all = ReplayRecorder.EnumerateEntities(lvl);
                if (all == null)
                    return "枚举失败";
                var sb = new StringBuilder(64);
                int n = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    var e = all[i];
                    if (e == null || e.Pointer == IntPtr.Zero)
                        continue;
                    UnityEngine.Vector3 p;
                    try { p = e.Position; } catch (Exception) { continue; }
                    int l, c;
                    try
                    {
                        l = lvl.GetLane(p.z);
                        c = lvl.GetColumn(p.x);
                    }
                    catch (Exception) { continue; }
                    if (l != lane || c != column)
                        continue;
                    if (n > 0)
                        sb.Append(',');
                    sb.Append(ReplayRecorder.DefIdOf(e)).Append('#').Append(e.ID);
                    n++;
                }
                return n == 0 ? "(空)" : sb.ToString();
            }
            catch (Exception e)
            {
                return "查询失败:" + e.GetType().Name;
            }
        }

        private static int SafeSlotCount(LevelEngine lvl)
        {
            int slotCount = 0;
            try { slotCount = lvl.GetSeedSlotCount(); } catch (Exception) { }
            int packCount = 0;
            try { packCount = lvl.GetSeedPackCount(); } catch (Exception) { }
            // 取两者最大：有的模式槽位比实际包多（空槽），有的反过来。
            return Math.Max(slotCount, packCount);
        }
    }
}
