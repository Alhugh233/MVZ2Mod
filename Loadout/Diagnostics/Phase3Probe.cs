using System;
using System.Globalization;
using MVZ2Logic.Grids;
using MVZ2Logic.Level;
using PVZEngine;
using PVZEngine.Level;
using PVZEngine.Placements;
using PVZEngine.SeedPacks;

namespace Loadout
{
    /// <summary>
    /// 第三批需求的探针（「补满 / 禁用冷却 / 出怪幂数 / 无条件放置 / 传送带卡槽镜像」）。
    ///
    /// <para>它们大多要求「改完读回来的值 = 玩家填的值」，所以自测里一律**来回走一遍**：
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
        /// ③ 禁用冷却：现在直接用**原版**的关卡属性
        /// （<c>EngineLevelProps.NO_RECHARGE</c> —— 原作者把 <c>/cheat recharge</c> 改成了它）。
        ///
        /// <para>判据：把每张卡的充能进度清零之后，属性打开时**所有**卡都该立刻算作已充满，
        /// 关掉时不该是全部充满。这样验的是"真的没有冷却"，而不是"某个字段被写了"。</para>
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
                var before = PVZEngine.Level.EngineLevelProps.NoRecharge(level);
                sb.Append("原版关卡属性 no_recharge=").Append(before ? "开" : "关")
                  .Append("  卡槽=").Append(count).Append('\n');

                // 先把每张卡的充能进度清零 —— 这样「有没有冷却」才有区分度。
                for (var i = 0; i < count; i++)
                {
                    var pack = BlueprintOps.GetPackAt(level, i);
                    if (pack == null)
                        continue;
                    try { pack.ResetRecharge(); }
                    catch (Exception) { }
                }

                PVZEngine.Level.EngineLevelProps.SetNoRecharge(level, false);
                int offTotal;
                var off = ChargedCount(level, out offTotal);
                PVZEngine.Level.EngineLevelProps.SetNoRecharge(level, true);
                int onTotal;
                var on = ChargedCount(level, out onTotal);

                sb.Append("  关冷却：已充满 ").Append(off).Append('/').Append(offTotal)
                  .Append("；开冷却：已充满 ").Append(on).Append('/').Append(onTotal).Append('\n');
                sb.Append("  判据 开冷却时全部立刻充满=")
                  .Append(onTotal > 0 && on == onTotal ? "OK" : "NG")
                  .Append("  判据 关冷却时并非全部充满=")
                  .Append(off < offTotal ? "OK" : "NG（这一关的卡本来就没有冷却）");

                PVZEngine.Level.EngineLevelProps.SetNoRecharge(level, before);
                sb.Append("\n  已还原成 ").Append(before ? "开" : "关");
            }
            catch (Exception e)
            {
                sb.Append("失败：").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        /// <summary>卡槽里有多少张卡算「已充满」。</summary>
        private static int ChargedCount(LevelEngine level, out int total)
        {
            total = 0;
            var charged = 0;
            var count = BlueprintOps.GetSlotCount(level);
            for (var i = 0; i < count; i++)
            {
                var pack = BlueprintOps.GetPackAt(level, i);
                if (pack == null)
                    continue;
                total++;
                try { if (pack.IsCharged()) charged++; }
                catch (Exception) { }
            }
            return charged;
        }

        /// <summary>
        /// ④ 传送带页第一行 = 卡槽（与自选卡页同一份数据），卡池是它的镜像 + 权重/数量限制。
        /// </summary>
        public static string ConveyorMirror()
        {
            return ConveyorPage.DebugMirror();
        }

        /// <summary>
        /// ⑤ 无条件放置：走**真正的两道门**（<c>PlacementDefinition.GetPlaceError</c> 放行 +
        /// <c>PlaceEntity</c> 自行生成），验「条件被放行**而且**实体真的出来了」。
        ///
        /// <para>为什么必须走真门：早期版本只放行了第一道，症状正是
        /// 「格子变绿了、费用扣了，实体却没生成」。</para>
        /// </summary>
        public static string UnconditionalPlaceRound()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return "（不在关卡内）";
            var sb = new System.Text.StringBuilder(600);
            var grid = level.GetGrid(0, 0);
            if (grid == null)
                return "（拿不到第 1 行第 1 列的格子）";

            // 优先找一张**升级类（紫卡）**：它在空格子上本来必然放不下 —— 不用先占格。
            var slot = -1;
            var upgrade = false;
            var count = BlueprintOps.GetSlotCount(level);
            for (var i = 0; i < count; i++)
            {
                var p = BlueprintOps.GetPackAt(level, i);
                if (p == null)
                    continue;
                if (slot < 0)
                    slot = i;
                if (BlueprintOps.IsUpgrade(p))
                {
                    slot = i;
                    upgrade = true;
                    break;
                }
            }
            if (slot < 0)
                return "（卡槽是空的）";

            var pack = BlueprintOps.GetPackAt(level, slot);
            var id = pack.GetDefinitionID();
            var seedDef = level.Content == null ? null : level.Content.GetSeedDefinition(id);
            var entityDef = BlueprintOps.GetSeedEntity(pack);
            if (seedDef == null || entityDef == null)
                return "（这张卡拿不到种子定义 / 实体定义）";
            sb.Append("用卡=").Append(BlueprintOps.BlueprintName(id, false))
              .Append(upgrade ? "（升级类=紫卡）" : "（普通卡）")
              .Append("  目标实体=").Append(Text(entityDef.GetID()))
              .Append("  格=第1行第1列\n");

            PVZEngine.Entities.Entity occupied = null;
            if (!upgrade)
            {
                // 普通卡在空格子上本来就放得下 —— 先占掉这一格，制造出「放不下」。
                occupied = PlacementScope.DebugSpawnScoped(level, pack, grid);
            }

            var before = BlueprintFlags.GetUnconditionalPlace(level);
            try
            {
                BlueprintFlags.SetUnconditionalPlace(level, false);
                var offErr = grid.GetEntityPlaceStatus(entityDef);
                var off = grid.PlaceEntityBlueprint(seedDef, new PlaceParams());
                var offInvalid = off == null || off.IsInvalid();
                sb.Append("  关：判定=").Append(Describe(offErr))
                  .Append("  放置=").Append(offInvalid ? "无效（什么都没生成）" : "生成了")
                  .Append('\n');

                BlueprintFlags.SetUnconditionalPlace(level, true);
                var onErr = grid.GetEntityPlaceStatus(entityDef);
                var on = grid.PlaceEntityBlueprint(seedDef, new PlaceParams());
                var spawned = on == null ? null : on.entity;
                sb.Append("  开：判定=").Append(Describe(onErr))
                  .Append("  放置=").Append(spawned == null ? "没生成 ✗" : BlueprintSpawnPatch.DescribeEntity(spawned))
                  .Append('\n');
                sb.Append("  判据 关时放不下且不生成=")
                  .Append(offErr != null && offInvalid ? "OK" : "NG")
                  .Append("  判据 开时放行且真的生成实体=")
                  .Append(onErr == null && spawned != null ? "OK" : "NG");
                if (spawned != null)
                {
                    try { spawned.Remove(); }
                    catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                sb.Append("失败：").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            finally
            {
                BlueprintFlags.SetUnconditionalPlace(level, before);
                if (occupied != null)
                {
                    try { occupied.Remove(); }
                    catch (Exception) { }
                }
            }
            return sb.ToString();
        }

        private static string Describe(NamespaceID error)
        {
            return error == null ? "可放" : "不可放（" + error.Path + "）";
        }

        /// <summary>NamespaceID 的 <c>ToString</c> 在 interop 里只打类型名，自己拼。</summary>
        private static string Text(NamespaceID id)
        {
            return id == null ? "（无）" : id.SpaceName + ":" + id.Path;
        }

        /// <summary>
        /// ⑥ 模仿者：改完之后**原版那几处显示**有没有跟着重画
        /// （选卡阶段是独立字段、关卡 HUD 只在自己事件里重画，详见 VanillaChooseBridge）。
        /// </summary>
        public static string VanillaSyncRound()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return "（不在关卡内）";
            return VanillaChooseBridge.SyncRound(level);
        }

        private static string N(float v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
