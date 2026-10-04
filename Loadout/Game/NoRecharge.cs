using System;
using MVZ2Logic.Level;
using PVZEngine.Level;
using PVZEngine.SeedPacks;

namespace Loadout
{
    /// <summary>
    /// 「禁用冷却」的两件事：把卡槽里每张卡的冷却换成**无**，关闭时**换回原值**。
    ///
    /// <para>原版那个 <c>debugNoRecharge</c> buff 的做法是把**整关的冷却速度**拉到 1e8 ——
    /// 效果上等价于「所有卡都无冷却」，但它有两个副作用：① 蓝图的「冷却(秒)」一格会显示
    /// 一个荒谬的数字（面板遇到 ≥1000 会置灰，见 <see cref="BlueprintPage"/>）；
    /// ② 它盖住了玩家自己改过的冷却（改完看不到效果）。</para>
    ///
    /// <para>所以这里按需求改成**逐卡替换**：开启时先把每张卡的
    /// <c>RECHARGE_ID</c> / <c>RECHARGE_SPEED</c> **备份**下来（备份的是**卡上的当前值** ——
    /// 玩家改过的也算，所以关掉之后能原样换回来），再把冷却定义换成「无」。</para>
    ///
    /// <para><b>备份写进存档</b>（<c>.loadoutflags</c> 的 <c>rc:</c> 行）——
    /// 因为「无冷却」也是写进卡属性、会随 <c>.lvl</c> 存下来的；不备份原值，
    /// 下次进关卡就再也换不回去了。这条落盘受 <see cref="GcFeature.SaveCustomData"/> 管
    /// （Safe 档下只在内存里备份，本次会话仍然能换回去）。</para>
    /// </summary>
    internal static class NoRecharge
    {
        /// <summary>上一次看到的 buff 状态（边沿检测；null = 还没看过）。</summary>
        private static bool? lastState;
        private static long lastLevelId;

        /// <summary>
        /// 每帧自愈（由面板心跳调用，面板关着也跑）：检测「禁用冷却」buff 的**边沿**。
        ///
        /// <para>为什么必须自愈：buff 是**关卡级**的、会跟着 <c>.lvl</c> 存下来，
        /// 所以「上次开着就退出关卡 → 再进来」时不会有任何按钮点击事件，
        /// 只能靠这里发现「buff 在、但卡上还是原冷却」并补一次。</para>
        /// </summary>
        public static void Tick()
        {
            var level = LevelFacade.Current;
            if (level == null)
            {
                lastState = null;
                lastLevelId = 0;
                return;
            }
            try
            {
                var id = level.Pointer.ToInt64();
                if (id != lastLevelId)
                {
                    lastLevelId = id;
                    lastState = null;      // 换关 → 重新判定
                }
                var want = BlueprintFlags.GetNoRecharge(level);
                if (lastState.HasValue && lastState.Value == want)
                    return;
                lastState = want;
                if (want)
                    Apply(level);
                else
                {
                    // 只有在「确实有备份」时才还原（否则每次进关卡都会白写一遍属性）。
                    if (BlueprintFlags.RechargeKeys(level).Count > 0)
                        Restore(level);
                }
            }
            catch (Exception)
            {
                // 心跳上不能抛。
            }
        }

        /// <summary>面板上的开关（写我们自己的标记，随存档走）。</summary>
        public static void SetEnabled(LevelEngine level, bool value)
        {
            if (level == null)
                return;
            BlueprintFlags.SetNoRecharge(level, value);
            if (value)
                ForceApply(level);
            else
                ForceRestore(level);
        }

        /// <summary>强行应用（按钮点开时立即生效，不等心跳）。</summary>
        public static void ForceApply(LevelEngine level)
        {
            lastState = true;
            Apply(level);
        }

        /// <summary>强行还原。</summary>
        public static void ForceRestore(LevelEngine level)
        {
            lastState = false;
            Restore(level);
        }

        /// <summary>
        /// 把卡槽里每张卡的冷却换成「无」，换之前先把当前值备份下来。
        /// </summary>
        public static void Apply(LevelEngine level)
        {
            if (level == null || !CompatConfig.IsEnabled(GcFeature.NoRecharge))
                return;
            var count = BlueprintOps.GetSlotCount(level);
            var done = 0;
            for (var i = 0; i < count; i++)
            {
                var pack = BlueprintOps.GetPackAt(level, i);
                if (pack == null)
                    continue;
                var key = BlueprintKeys.Slot(i);
                try
                {
                    // 已经有备份就不覆盖 —— 否则「开→关→开」会把「无」自己备份进去。
                    if (!BlueprintFlags.GetRechargeBackup(level, key).Has)
                    {
                        var id = BlueprintOps.GetRechargeIdText(pack);
                        if (!string.IsNullOrEmpty(id))
                            BlueprintFlags.SetRechargeBackup(level, key, id, BlueprintOps.GetBaseRechargeSpeed(pack));
                    }
                    BlueprintOps.ApplyRechargePreset(level, pack, BlueprintOps.Presets[0]);
                    done++;
                }
                catch (Exception) { }
            }
            Plugin.Log?.LogInfo("[NoRecharge] 已把 " + done + " 张卡的冷却换成「无」（原值已备份"
                + (BlueprintFlags.CanPersist ? "到存档" : "在内存（当前等级不落盘）") + "）");
        }

        /// <summary>把备份的原值换回去，并清掉备份。</summary>
        public static void Restore(LevelEngine level)
        {
            if (level == null)
                return;
            var count = BlueprintOps.GetSlotCount(level);
            var done = 0;
            for (var i = 0; i < count; i++)
            {
                var key = BlueprintKeys.Slot(i);
                var backup = BlueprintFlags.GetRechargeBackup(level, key);
                if (!backup.Has)
                    continue;
                var pack = BlueprintOps.GetPackAt(level, i);
                if (pack != null)
                {
                    try
                    {
                        BlueprintOps.SetRecharge(pack, backup.RechargeId, backup.Speed);
                        done++;
                    }
                    catch (Exception) { }
                }
                BlueprintFlags.ClearRechargeBackup(level, key);
            }
            Plugin.Log?.LogInfo("[NoRecharge] 已把 " + done + " 张卡的冷却换回原值");
        }

        /// <summary>自测用：现在的状态与备份情况。</summary>
        public static string Report()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return "（不在关卡内）";
            var sb = new System.Text.StringBuilder(200);
            var on = BlueprintFlags.GetNoRecharge(level);
            var keys = BlueprintFlags.RechargeKeys(level);
            sb.Append("禁用冷却 开关=").Append(on ? "开" : "关")
              .Append("  备份=").Append(keys.Count).Append(" 条")
              .Append("  可落盘=").Append(BlueprintFlags.CanPersist ? "是" : "否（等级不足）");
            for (var i = 0; i < keys.Count; i++)
            {
                var b = BlueprintFlags.GetRechargeBackup(level, keys[i]);
                sb.Append("\n    ").Append(BlueprintKeys.Describe(keys[i]))
                  .Append(" → ").Append(b.RechargeId)
                  .Append(" 速度=").Append(b.Speed.ToString("0.####"));
            }
            return sb.ToString();
        }
    }
}
