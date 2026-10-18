using System;
using MVZ2Logic.HeldItems;
using MVZ2Logic.Level;
using PVZEngine;
using PVZEngine.Level;

namespace Replay
{
    /// <summary>
    /// 蓝图手持物上的「立即触发 / 立即激发」两个标记。
    ///
    /// ── 为什么必须记 ─────────────────────────────────────────────────
    /// <para>游戏在**拿起蓝图**那一刻算出这两个标记并**存进手持物**
    /// （<c>SelectBlueprintHeldItemBehaviour.PickupBlueprint</c> →
    /// <c>builder.SetInstantTrigger(pickData.triggerValue)</c>），
    /// 之后放下去时 <c>BlueprintGlobalCallbacks</c> 会读它们决定行为
    /// （<c>heldData.IsInstantTrigger()</c> / <c>IsInstantEvoke()</c>）。</para>
    ///
    /// <para>问题在于其中一个标记依赖**玩家选项**：</para>
    /// <code>
    /// bool swapped = Global.Saves.IsTriggerUnlocked() &amp;&amp; Global.Options.IsTriggerSwapped();
    /// bool triggerValue = canInstantTrigger &amp;&amp; (holdingTrigger != swapped);
    /// </code>
    /// <para>「交换触发」是难度界面里那个开关（<c>swap_trigger</c>），**关卡内随手就能改**。
    /// 而它是玩家选项、**不在关卡关键帧里** —— 于是「录的时候开、放的时候关」
    /// （或者反过来）会让 <c>triggerValue</c> 整个反过来，回放里的器械行为就变了。</para>
    ///
    /// <para>所以录制时把这两个标记**跟着「拿起蓝图」那个事件一起记下来**，
    /// 回放时在同一个位置**按回去** —— 这样回放与选项完全无关。</para>
    ///
    /// ── 为什么不直接用 LogicHeldTypes / VanillaHeldTypes ─────────────
    /// <para>那两个静态类的字段初始化依赖 <c>Global.BuiltinNamespace</c> /
    /// <c>VanillaMod.spaceName</c>，正是本仓库反复踩过的「碰一下就提前跑 <c>.cctor</c>」
    /// 的类型。这里只需要比对 <c>NamespaceID.Path</c>，用字符串白名单最省事也最安全。</para>
    /// </summary>
    internal static class ReplayHeldFlags
    {
        /// <summary>「手持的是一张蓝图」时可能的类型名（<c>NamespaceID.Path</c>）。</summary>
        private static readonly string[] BlueprintPaths =
        {
            "blueprint",          // LogicHeldItemNames.blueprint（经典卡槽）
            "conveyor",           // LogicHeldItemNames.conveyor（传送带卡槽）
            "blueprint_pickup",   // VanillaHeldItemNames.blueprintPickup（地上掉落的卡）
        };

        /// <summary>手持物是不是「一张蓝图」（只有它才有那两个标记）。</summary>
        public static bool IsBlueprintKind(NamespaceID type)
        {
            if (type == null)
                return false;
            string path;
            try
            {
                if (type.Pointer == IntPtr.Zero)
                    return false;
                path = type.Path;
            }
            catch (Exception)
            {
                return false;
            }
            if (string.IsNullOrEmpty(path))
                return false;
            for (int i = 0; i < BlueprintPaths.Length; i++)
            {
                if (string.Equals(path, BlueprintPaths[i], StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 自测用：拿一个**现造的手持物数据**验证「属性真的写得进去、读得回来」。
        ///
        /// <para>为什么必须单独验：<c>instant_trigger</c> 这类属性是<b>注册过的键</b>才有意义的。
        /// 如果键没注册，<c>SetProperty</c> 只会默默失败（日志里顶多一句
        /// 「Trying to set a property with an invalid key!」），
        /// 于是「回放时把标记按回去」这条修复就成了**静默无效** ——
        /// 而它从游戏表现上完全看不出来（要等某个器械行为不一样了才会发现）。</para>
        ///
        /// <para>造一个空的手持物数据不需要任何游戏状态，所以这个检查在任何时候都能跑。</para>
        /// </summary>
        public static bool SelfCheck(out string detail)
        {
            var sb = new System.Text.StringBuilder(160);
            try
            {
                var id = new NamespaceID(NamespaceForProbe, "probe");
                var data = new HeldItemData(id);
                if (data == null || data.Pointer == IntPtr.Zero)
                {
                    detail = "造不出 HeldItemData";
                    return false;
                }
                var container = new IHeldItemPropertyContainer(data.Pointer);

                bool before = LogicHeldItemProps.IsInstantTrigger(container);
                LogicHeldItemProps.SetInstantTrigger(container, true);
                bool afterTrue = LogicHeldItemProps.IsInstantTrigger(container);
                LogicHeldItemProps.SetInstantTrigger(container, false);
                bool afterFalse = LogicHeldItemProps.IsInstantTrigger(container);

                LogicHeldItemProps.SetInstantEvoke(container, true);
                bool evoke = LogicHeldItemProps.IsInstantEvoke(container);

                bool ok = before == false && afterTrue && !afterFalse && evoke;
                sb.Append("初始=").Append(before ? 1 : 0)
                  .Append(" 写 true 后读回=").Append(afterTrue ? 1 : 0)
                  .Append(" 写 false 后读回=").Append(afterFalse ? 1 : 0)
                  .Append(" evoke=").Append(evoke ? 1 : 0);
                detail = sb.ToString();
                return ok;
            }
            catch (Exception e)
            {
                detail = "出错：" + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 读当前手持物上的两个标记。手持的不是蓝图（或读不到）时返回 false。
        ///
        /// <para>接口派生关系在 interop 里是断开的（<c>IHeldItemData</c> 不声明
        /// <c>IHeldItemPropertyContainer</c> 的成员），所以必须用指针**手工包一层壳**
        /// 再调那个接口上的扩展方法。同一份 native 对象本来就实现了那个接口，包壳是安全的。</para>
        /// </summary>
        public static bool TryRead(LevelEngine level, out bool instantTrigger, out bool instantEvoke)
        {
            instantTrigger = false;
            instantEvoke = false;
            if (level == null)
                return false;
            try
            {
                var type = level.GetHeldItemType();
                LastType = DescribeType(type);
                if (!IsBlueprintKind(type))
                {
                    LastSkipReason = "手持的不是蓝图（" + LastType + "）";
                    return false;
                }
                var data = level.GetHeldItemData();
                if (data == null || data.Pointer == IntPtr.Zero)
                {
                    LastSkipReason = "拿不到手持物数据";
                    return false;
                }
                var container = new IHeldItemPropertyContainer(data.Pointer);
                instantTrigger = LogicHeldItemProps.IsInstantTrigger(container);
                instantEvoke = LogicHeldItemProps.IsInstantEvoke(container);
                LastSkipReason = null;
                return true;
            }
            catch (Exception e)
            {
                LastSkipReason = "读取出错 " + e.GetType().Name;
                Plugin.Log?.LogWarning("[Replay] 读手持物即时触发标记失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 上一次 <see cref="TryRead"/> 为什么没拿到（写进报告用）。
        ///
        /// <para>为什么值得报：录制时最常见的情况是**选卡根本没成功**（能量不够 / 卡无效），
        /// 那时手上压根不是蓝图 —— 不分清楚的话，「事件里没有 itg 字段」
        /// 会被误读成「这个功能没写」。</para>
        /// </summary>
        public static string LastSkipReason { get; private set; }

        /// <summary>上一次 <see cref="TryRead"/> 看到的手持物类型（字符串形式）。</summary>
        public static string LastType { get; private set; }

        private const string NamespaceForProbe = "replay_selftest";

        private static string DescribeType(NamespaceID id)
        {
            if (id == null)
                return "null";
            try
            {
                if (id.Pointer == IntPtr.Zero)
                    return "空指针";
                return id.SpaceName + ":" + id.Path;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        /// <summary>
        /// 把两个标记按回去（只改值有变化的那一个）。
        ///
        /// <para>返回改了几个（0 = 本来就一致，不会白写）。</para>
        /// </summary>
        public static int Apply(LevelEngine level, bool instantTrigger, bool instantEvoke)
        {
            if (level == null)
                return 0;
            int changed = 0;
            try
            {
                var data = level.GetHeldItemData();
                if (data == null || data.Pointer == IntPtr.Zero)
                    return 0;
                var container = new IHeldItemPropertyContainer(data.Pointer);
                if (LogicHeldItemProps.IsInstantTrigger(container) != instantTrigger)
                {
                    LogicHeldItemProps.SetInstantTrigger(container, instantTrigger);
                    changed++;
                }
                if (LogicHeldItemProps.IsInstantEvoke(container) != instantEvoke)
                {
                    LogicHeldItemProps.SetInstantEvoke(container, instantEvoke);
                    changed++;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 写手持物即时触发标记失败：" + e.GetType().Name + ": " + e.Message);
            }
            return changed;
        }
    }
}
