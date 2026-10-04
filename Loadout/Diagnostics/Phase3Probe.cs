using System;
using System.Globalization;
using MVZ2Logic.Level;
using PVZEngine;

namespace Loadout
{
    /// <summary>
    /// 第三批需求的探针（「补满 / 禁用冷却 / 出怪幂数 / 传送带卡槽镜像」）。
    ///
    /// <para>它们都要求「改完读回来的值 = 玩家填的值」，所以自测里一律**来回走一遍**：
    /// 设进去 → 读出来 → 判据。</para>
    /// </summary>
    internal static class Phase3Probe
    {
        /// <summary>
        /// ① 无限能量 / 无限星之碎片：补满 + 「消耗那一帧自动回满」。
        /// </summary>
        public static string Refill()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return "（不在关卡内）";
            var sb = new System.Text.StringBuilder(300);
            try
            {
                var eBuff = LevelFacade.HasBuff(level, LevelFacade.BuffEnergy);
                var sBuff = LevelFacade.HasBuff(level, LevelFacade.BuffStarshard);
                CheatRefill.RefillNow(level);
                sb.Append("buff 能量=").Append(eBuff ? "开" : "关")
                  .Append(" 星之碎片=").Append(sBuff ? "开" : "关").Append('\n');
                sb.Append("  补齐后 能量=").Append(N(LevelFacade.GetEnergy(level)))
                  .Append('/').Append(LevelFacade.GetMaxEnergy(level))
                  .Append("  星之碎片=").Append(LevelFacade.GetStarshardCount(level))
                  .Append('/').Append(LevelFacade.GetStarshardSlots(level)).Append('\n');

                // 「消耗 → 自动回满」：直接调原版那两个方法，看补丁有没有接住。
                var maxE = LevelFacade.GetMaxEnergy(level);
                level.AddEnergy(-maxE);
                var afterSpend = LevelFacade.GetEnergy(level);
                sb.Append("  扣 ").Append(maxE).Append(" 能量后 = ").Append(N(afterSpend))
                  .Append("  判据 ").Append(eBuff ? "自动回满=" : "（buff 关着，不动）=")
                  .Append(eBuff ? (afterSpend >= maxE - 0.01f ? "OK" : "NG") : "跳过").Append('\n');

                var slots = LevelFacade.GetStarshardSlots(level);
                LevelFacade.SetStarshardCount(level, 0);
                level.AddStarshardCount(-1);
                var afterStar = LevelFacade.GetStarshardCount(level);
                sb.Append("  清空后扣 1 星之碎片 = ").Append(afterStar)
                  .Append("  判据 ").Append(sBuff ? "自动回满=" : "（buff 关着，不动）=")
                  .Append(sBuff ? (afterStar >= slots ? "OK" : "NG") : "跳过");
            }
            catch (Exception e)
            {
                sb.Append("失败：").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        /// <summary>
        /// ② 出怪点数 / 倍数 / 幂数：填进去的数值 = 生效的数值（难度修饰器被反算掉）。
        /// </summary>
        public static string SpawnPoints()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return "（不在关卡内）";
            var sb = new System.Text.StringBuilder(400);
            try
            {
                var diff = LevelFacade.GetDifficulty(level);
                sb.Append("难度=").Append(diff == null ? "（无）" : diff.SpaceName + ":" + diff.Path).Append('\n');
                Round(sb, "出怪点数", LevelFacade.GetSpawnPointAddition(level),
                    v => LevelFacade.SetSpawnPointAddition(level, v), 123f);
                Round(sb, "出怪倍数", LevelFacade.GetSpawnPointMultiplier(level),
                    v => LevelFacade.SetSpawnPointMultiplier(level, v), 2.5f);
                Round(sb, "出怪幂数", LevelFacade.GetSpawnPointPower(level),
                    v => LevelFacade.SetSpawnPointPower(level, v), 1.7f);
            }
            catch (Exception e)
            {
                sb.Append("失败：").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        private static void Round(System.Text.StringBuilder sb, string label, float before,
            Action<float> set, float want)
        {
            set(want);
            var after = Read(label);
            // 还原
            set(before);
            var back = Read(label);
            sb.Append("  ").Append(label).Append("：设 ").Append(N(want))
              .Append(" → 读回 ").Append(N(after))
              .Append("  判据 ").Append(Math.Abs(after - want) < 0.001f ? "OK" : "NG")
              .Append("；还原成 ").Append(N(before))
              .Append(" → ").Append(N(back))
              .Append(' ').Append(Math.Abs(back - before) < 0.001f ? "OK" : "NG")
              .Append('\n');
        }

        private static float Read(string label)
        {
            var level = LevelFacade.Current;
            if (label == "出怪点数")
                return LevelFacade.GetSpawnPointAddition(level);
            if (label == "出怪倍数")
                return LevelFacade.GetSpawnPointMultiplier(level);
            return LevelFacade.GetSpawnPointPower(level);
        }

        /// <summary>
        /// ③ 禁用冷却：开 → 每张卡的冷却定义变成「无」且备份了原值；关 → 原样换回。
        /// </summary>
        public static string NoRechargeRound()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return "（不在关卡内）";
            var sb = new System.Text.StringBuilder(400);
            try
            {
                var count = BlueprintOps.GetSlotCount(level);
                sb.Append("卡槽=").Append(count).Append('\n');
                var before = new string[count];
                for (var i = 0; i < count; i++)
                {
                    var pack = BlueprintOps.GetPackAt(level, i);
                    before[i] = pack == null ? null : BlueprintOps.GetRechargeIdText(pack);
                }

                LevelFacade.SetBuff(level, LevelFacade.BuffNoRecharge, false);
                NoRecharge.SetEnabled(level, true);
                var allNone = true;
                for (var i = 0; i < count; i++)
                {
                    var pack = BlueprintOps.GetPackAt(level, i);
                    if (pack == null)
                        continue;
                    var now = BlueprintOps.GetRechargeIdText(pack);
                    var none = now != null && now.IndexOf("none", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!none)
                        allNone = false;
                    sb.Append("  ").Append(BlueprintKeys.Describe(BlueprintKeys.Slot(i)))
                      .Append("：").Append(before[i] ?? "（无）").Append(" → ").Append(now ?? "（无）")
                      .Append(none ? " ✓" : " ✗").Append('\n');
                }
                sb.Append("  判据 全部换成「无」=").Append(allNone ? "OK" : "NG").Append('\n');
                sb.Append("  备份：" ).Append(NoRecharge.Report()).Append('\n');

                LevelFacade.SetBuff(level, LevelFacade.BuffNoRecharge, false);
                NoRecharge.SetEnabled(level, false);
                var allBack = true;
                for (var i = 0; i < count; i++)
                {
                    var pack = BlueprintOps.GetPackAt(level, i);
                    if (pack == null)
                        continue;
                    var now = BlueprintOps.GetRechargeIdText(pack);
                    sb.Append("  ").Append(BlueprintKeys.Describe(BlueprintKeys.Slot(i)))
                      .Append("：换回 ").Append(now ?? "（无）")
                      .Append(now == before[i] ? " ✓" : " ✗（原本 " + (before[i] ?? "（无）") + "）")
                      .Append('\n');
                    if (now != before[i])
                        allBack = false;
                }
                sb.Append("  判据 全部换回原值=").Append(allBack ? "OK" : "NG");
            }
            catch (Exception e)
            {
                sb.Append("失败：").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        /// <summary>
        /// ④ 传送带页第一行 = 卡槽（与自选卡页同一份数据），卡池是它的镜像 + 权重/数量限制。
        /// </summary>
        public static string ConveyorMirror()
        {
            return ConveyorPage.DebugMirror();
        }

        private static string N(float v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
