using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2.GameContent.Recharges;
using MVZ2.Managers;
using MVZ2.Metas;
using MVZ2Logic;
using MVZ2Logic.Blueprints;
using MVZ2Logic.Entities;
using MVZ2Logic.Games;
using MVZ2Logic.Level;
using MVZ2Logic.Saves;
using PVZEngine;
using PVZEngine.Entities;
using PVZEngine.Level;
using PVZEngine.SeedPacks;
using UnityEngine;

namespace Loadout
{
    /// <summary>
    /// 页面2（蓝图 / 传送带）用的游戏侧封装。
    ///
    /// <para><b>只写 pack 级属性</b>：蓝图的效果回退链是「pack → seed 定义」，
    /// 而 seed 定义是**全局单例**（写它会污染所有关卡且不存盘）。pack 的属性会随
    /// .lvl 存盘，所以面板改的东西是「这一张卡、这一关」，与存档修改器同源。</para>
    /// </summary>
    internal static class BlueprintOps
    {
        private static MainManager Main { get { return MainManager.Instance; } }

        #region 冷却预设

        /// <summary>一个官方冷却预设。</summary>
        internal sealed class RechargePreset
        {
            public NamespaceID Id;
            public string Name;     // 无 / 短 / 长 / 很长
            public int Ticks;       // 预设的**战斗**上限（tick，30tick = 1s）
        }

        /// <summary>
        /// 四个预设。秒数就直接是表里这几个（TPS = 30）：无 0s、短 7.5s、长 30s、很长 50s。
        ///
        /// <para>用的都是**战斗上限**（`MAX_RECHARGE`）；「初始上限」面板不管（需求指定）。</para>
        /// </summary>
        internal static readonly RechargePreset[] Presets =
        {
            new RechargePreset { Id = VanillaRechargeID.none,        Name = "无",   Ticks = 0 },
            new RechargePreset { Id = VanillaRechargeID.shortTime,   Name = "短",   Ticks = 225 },
            new RechargePreset { Id = VanillaRechargeID.longTime,    Name = "长",   Ticks = 900 },
            new RechargePreset { Id = VanillaRechargeID.veryLongTime, Name = "很长", Ticks = 1500 },
        };

        /// <summary>自定义冷却时用的载体预设 —— 「无」的上限是 0，缩放不动，必须借「短」。</summary>
        internal static RechargePreset ShortPreset
        {
            get { return Presets[1]; }
        }

        /// <summary>脚本里的 tick 率（<c>Ticks.defaultTPS</c>）。</summary>
        public const float TicksPerSecond = 30f;

        /// <summary>这张卡当前的冷却定义 id（<c>nsp:path</c> 文本）；读不出来返回 null。</summary>
        public static string GetRechargeIdText(SeedPack pack)
        {
            if (pack == null)
                return null;
            try
            {
                var id = pack.GetRechargeID();
                return NamespaceID.IsValid(id) ? id.SpaceName + ":" + id.Path : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>这张卡会放出哪个实体（拿不到返回 null）。</summary>
        public static EntityDefinition GetSeedEntity(SeedPack pack)
        {
            if (pack == null)
                return null;
            try
            {
                var id = pack.GetSeedEntityID();
                if (!NamespaceID.IsValid(id))
                    return null;
                var level = pack.Level;
                return level == null ? null : level.Content.GetEntityDefinition(id);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 这张卡放出来的是不是「夜用」器械。
        ///
        /// <para>原版是器械定义上的 <c>nocturnal</c> 属性：白天放出来会挂一个
        /// <c>NocturnalBuff</c> 让它睡觉（<c>ContraptionCommonBehaviour.Init</c>）。
        /// 所以「白天不睡觉」这个开关只对夜用器械有意义 —— 别的器械置灰正合适。</para>
        /// </summary>
        public static bool IsNocturnal(SeedPack pack)
        {
            var def = GetSeedEntity(pack);
            if (def == null)
                return false;
            try { return def.IsNocturnal(); }
            catch (Exception) { return false; }
        }

        /// <summary>这张卡是不是「升级类」（紫卡）。</summary>
        public static bool IsUpgrade(SeedPack pack)
        {
            if (pack == null)
                return false;
            try { return pack.IsUpgradeBlueprint(); }
            catch (Exception) { return false; }
        }

        public static RechargePreset PresetOf(NamespaceID id)
        {
            if (id == null)
                return null;
            for (var i = 0; i < Presets.Length; i++)
            {
                if (SameId(Presets[i].Id, id))
                    return Presets[i];
            }
            return null;
        }

        /// <summary>整关的冷却速度（`StageDefinition` 构造器默认 1；「禁用冷却」buff 会把它拉到 1e8）。</summary>
        public static float LevelSpeed(LevelEngine level)
        {
            try { return level.GetRechargeSpeed(); }
            catch (Exception) { return 1f; }
        }

        /// <summary>某张卡**生效**的冷却速度（= 基础值 × 所有乘算修正）。</summary>
        public static float CardSpeed(SeedPack pack)
        {
            try { return pack == null ? 1f : pack.GetRechargeSpeed(); }
            catch (Exception) { return 1f; }
        }

        /// <summary>某张卡的冷却速度**修正系数**（生效 / 基础）。拿不到基础值时按 1 处理。</summary>
        public static float CardSpeedMultiplier(SeedPack pack)
        {
            try
            {
                if (pack == null)
                    return 1f;
                var baseValue = pack.GetProperty<float>(EngineSeedProps.RECHARGE_SPEED, ignoreBuffs: true);
                if (baseValue <= 0.0001f)
                    return 1f;
                return pack.GetRechargeSpeed() / baseValue;
            }
            catch (Exception) { return 1f; }
        }

        /// <summary>把 tick 折算成秒（用**当前**速度）。</summary>
        public static float TicksToSeconds(LevelEngine level, SeedPack pack, float ticks)
        {
            var speed = TicksPerSecond * LevelSpeed(level) * CardSpeed(pack);
            if (speed <= 0.0001f)
                return 0f;
            return ticks / speed;
        }

        /// <summary>当前**战斗上限**折算成的秒数（面板上「冷却(秒)」显示的就是它）。</summary>
        public static float GetCooldownSeconds(LevelEngine level, SeedPack pack)
        {
            if (pack == null)
                return 0f;
            try { return TicksToSeconds(level, pack, pack.GetUsedMaxRecharge()); }
            catch (Exception) { return 0f; }
        }

        /// <summary>某个预设按当前速度折算的秒数（预设列表里显示的就是它）。</summary>
        public static float PresetSeconds(LevelEngine level, SeedPack pack, RechargePreset preset)
        {
            return preset == null ? 0f : TicksToSeconds(level, pack, preset.Ticks);
        }

        /// <summary>
        /// 当前冷却对应的预设；对不上任何一个就是「未定义」（返回 null）。
        ///
        /// <para>判定走**折算后**的秒数（与需求一致）：预设跟随修正系数变动，
        /// 例如易难度 ×1.25 时「短」显示 6.0s，此时 6.0s 的卡就判为「短」。</para>
        /// </summary>
        public static RechargePreset MatchPreset(LevelEngine level, SeedPack pack)
        {
            if (pack == null)
                return null;
            var seconds = GetCooldownSeconds(level, pack);
            for (var i = 0; i < Presets.Length; i++)
            {
                var target = PresetSeconds(level, pack, Presets[i]);
                if (Mathf.Abs(target - seconds) <= 0.06f)
                    return Presets[i];
            }
            return null;
        }

        /// <summary>直接应用一个预设（不缩放速度，并把基础速度复位成 1）。</summary>
        public static bool ApplyRechargePreset(LevelEngine level, SeedPack pack, RechargePreset preset)
        {
            if (pack == null || preset == null)
                return false;
            try
            {
                pack.SetProperty(EngineSeedProps.RECHARGE_ID, preset.Id);
                pack.SetProperty(EngineSeedProps.RECHARGE_SPEED, 1f);
                FinishCooldownEdit(pack);
                Plugin.Log?.LogInfo("[Blueprint] 冷却预设 → " + preset.Name
                    + "（折算 " + Fmt(PresetSeconds(level, pack, preset)) + "s）");
                return true;
            }
            catch (Exception e)
            {
                LogOnce("preset-" + preset.Name, "应用冷却预设失败：" + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 自定义冷却：让这张卡的**实际**冷却等于 <paramref name="seconds"/>。
        ///
        /// <para>做法：挂到「短」预设上（它的上限 225 非 0，能被冷却速度缩放），
        /// 再把基础冷却速度写成「让生效速度正好落在目标」的那个值：
        /// <c>base = 225 / (30 × 关卡速度 × 秒数 × 修正系数)</c>。</para>
        ///
        /// <para>输入秒数是**折算后**的秒数（= 面板显示的那个），所以玩家填多少就是多少，
        /// 不受难度 / 控制棒等修正影响。</para>
        /// </summary>
        public static bool SetCooldownSeconds(LevelEngine level, SeedPack pack, float seconds)
        {
            if (pack == null)
                return false;
            if (seconds <= 0.05f)
                return ApplyRechargePreset(level, pack, Presets[0]);   // 0 秒 = 无冷却

            // 恰好等于某个预设 → 直接切预设（需求指定）
            for (var i = 1; i < Presets.Length; i++)
            {
                if (Mathf.Abs(PresetSeconds(level, pack, Presets[i]) - seconds) <= 0.06f)
                    return ApplyRechargePreset(level, pack, Presets[i]);
            }

            try
            {
                var levelSpeed = LevelSpeed(level);
                if (levelSpeed <= 0.0001f)
                    return false;
                var multiplier = CardSpeedMultiplier(pack);
                var baseSpeed = ShortPreset.Ticks / (TicksPerSecond * levelSpeed * seconds * multiplier);
                if (baseSpeed <= 0.0001f)
                    baseSpeed = 0.0001f;

                pack.SetProperty(EngineSeedProps.RECHARGE_ID, ShortPreset.Id);
                pack.SetProperty(EngineSeedProps.RECHARGE_SPEED, baseSpeed);
                FinishCooldownEdit(pack);
                Plugin.Log?.LogInfo("[Blueprint] 自定义冷却 " + Fmt(seconds) + "s → 基础速度 "
                    + baseSpeed.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                    + "（折算后 " + Fmt(GetCooldownSeconds(level, pack)) + "s）");
                return true;
            }
            catch (Exception e)
            {
                LogOnce("cooldown", "设置冷却失败：" + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 改完冷却后的收尾：不再用「初始上限」、并把当前充能顶满。
        ///
        /// <para><c>SetStartRecharge(false)</c> 是必须的 —— 否则卡片还在用**初始上限**
        /// （长/很长的初始冷却是 20s/35s），玩家刚设的数字要等打一次才生效。</para>
        /// </summary>
        private static void FinishCooldownEdit(SeedPack pack)
        {
            try
            {
                pack.SetStartRecharge(false);
                pack.FullRecharge();
            }
            catch (Exception) { }
        }
        #endregion

        #region 费用 / 模仿者 / 逆阵营

        public static int GetCost(SeedPack pack)
        {
            try { return pack == null ? 0 : pack.GetCost(); }
            catch (Exception) { return 0; }
        }

        public static bool SetCost(SeedPack pack, int cost)
        {
            if (pack == null)
                return false;
            if (cost < 0)
                cost = 0;
            try
            {
                pack.SetProperty(EngineSeedProps.COST, (float)cost);
                return true;
            }
            catch (Exception e)
            {
                LogOnce("cost", "改费用失败：" + e.Message);
                return false;
            }
        }

        public static bool GetCommandBlock(SeedPack pack)
        {
            try { return pack != null && pack.IsCommandBlock(); }
            catch (Exception) { return false; }
        }

        public static bool SetCommandBlock(SeedPack pack, bool value)
        {
            if (pack == null)
                return false;
            try
            {
                pack.SetCommandBlock(value);
                return true;
            }
            catch (Exception e)
            {
                LogOnce("cmdblock", "改模仿者失败：" + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 把某张卡的冷却**恢复成原版默认**，并把**当前冷却重设成原版初始冷却**。
        ///
        /// <para>两步：</para>
        /// <list type="number">
        /// <item>把 <c>RECHARGE_ID</c> / <c>RECHARGE_SPEED</c> 写回**种子定义**上的原值
        /// —— 面板改冷却时就是往 pack 上写这两个属性，写回原值即"撤销修改"。之后
        /// <c>GetUsedMaxRecharge()</c>（面板那一格读的就是它）又变回原版的上限。</item>
        /// <item>再走官方那句「重置所有卡牌重装填进度」：
        /// <c>SetStartRecharge(true)</c> + <c>ResetRecharge()</c>（<c>LevelEngine.ResetAllRechargeProgress</c>
        /// 里就是这两句）—— 于是这张卡回到"刚开局、还在等第一次初始冷却"的状态。</item>
        /// </list>
        /// </summary>
        public static bool ResetCooldown(LevelEngine level, SeedPack pack)
        {
            if (level == null || pack == null)
                return false;
            try
            {
                var def = level.Content == null ? null : level.Content.GetSeedDefinition(pack.GetDefinitionID());
                if (def != null)
                {
                    pack.SetProperty(EngineSeedProps.RECHARGE_ID, def.GetRechargeID());
                    pack.SetProperty(EngineSeedProps.RECHARGE_SPEED,
                        def.GetProperty<float>(EngineSeedProps.RECHARGE_SPEED));
                }
                pack.SetStartRecharge(true);
                pack.ResetRecharge();
                Plugin.Log?.LogInfo("[Blueprint] 重置冷却 → " + BlueprintName(pack.GetDefinitionID(), false)
                    + "（折算 " + Fmt(GetCooldownSeconds(level, pack)) + "s，当前进度 "
                    + Fmt(pack.GetRecharge()) + "）");
                return true;
            }
            catch (Exception e)
            {
                LogOnce("resetcd", "重置冷却失败：" + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 清空卡槽：**卡槽数不变、每格置空**（需求指定「保留格子占位」）。
        ///
        /// <para>用官方的 <c>ClearSeedPacks()</c> —— 它就是把每一格的种子包移除
        /// （<c>RemoveSeedPackAt</c> 把槽位置 null，数组长度不动），所以格子还在、卡没了。</para>
        /// </summary>
        public static bool ClearSlots(LevelEngine level)
        {
            if (level == null)
                return false;
            try
            {
                // ⚠ 先记住原来的槽数：ClearSeedPacks 之后槽位是 null，但**槽数不变**，
                // 所以按槽号清标记的顺序无所谓；关键是**每一格都要清**
                // （需求：清空卡槽要把被改过的属性数据一并清掉，否则下次往这一格塞别的卡
                //   会继承上一张卡的标记）。
                var slots = level.GetSeedSlotCount();
                level.ClearSeedPacks();
                for (var i = 0; i < slots; i++)
                    BlueprintFlags.ClearAll(level, BlueprintKeys.Slot(i));
                BlueprintMarks.Refresh();
                Plugin.Log?.LogInfo("[Blueprint] 已清空卡槽（" + slots + " 格，标记一并清除）");
                return true;
            }
            catch (Exception e)
            {
                LogOnce("clearslots", "清空卡槽失败：" + e.Message);
                return false;
            }
        }

        // ---- 血量（按卡，生成时生效）------------------------------------

        /// <summary>这张卡被指定的血量；没设过返回 &lt;0。</summary>
        public static float GetHealthOverride(LevelEngine level, string key)
        {
            return BlueprintFlags.GetHealth(level, key);
        }

        /// <summary>指定血量（&lt;0 = 恢复原版）。</summary>
        public static bool SetHealthOverride(LevelEngine level, string key, float value)
        {
            var ok = BlueprintFlags.SetHealth(level, key, value);
            BlueprintMarks.Refresh();
            return ok;
        }

        // ---- 白天不睡觉（按卡，生成时生效）--------------------------------

        public static bool GetNoDaySleep(LevelEngine level, string key)
        {
            return BlueprintFlags.GetNoDaySleep(level, key);
        }

        public static bool SetNoDaySleep(LevelEngine level, string key, bool value)
        {
            var ok = BlueprintFlags.SetNoDaySleep(level, key, value);
            BlueprintMarks.Refresh();
            return ok;
        }

        // ---- 无条件放置（全局）------------------------------------------------

        public static bool GetUnconditionalPlace(LevelEngine level)
        {
            return BlueprintFlags.GetUnconditionalPlace(level);
        }

        public static bool SetUnconditionalPlace(LevelEngine level, bool value)
        {
            var ok = BlueprintFlags.SetUnconditionalPlace(level, value);
            Plugin.Log?.LogInfo("[Blueprint] 无条件放置 = " + (value ? "开" : "关")
                + (ok ? "" : "（写入失败）"));
            return ok;
        }

        /// <summary>
        /// 这张蓝图**定义的**血量上限（= 它放出来的器械的 <c>MAX_HEALTH</c>）。
        ///
        /// <para>返回 &lt;0 表示问不到（不在关卡内 / 蓝图没有种子实体 / 属性读不出来）。
        /// 注意读的是**定义**上的值：关卡里的实体各有自己的属性，面板这里只是把
        /// 「原版是多少」显示出来，作为修改前的参照。</para>
        /// </summary>
        public static float GetBlueprintHealth(LevelEngine level, NamespaceID blueprintID)
        {
            if (level == null || !NamespaceID.IsValid(blueprintID))
                return -1f;
            try
            {
                var content = level.Content;
                if (content == null)
                    return -1f;
                var seedDef = content.GetSeedDefinition(blueprintID);
                if (seedDef == null)
                    return -1f;
                var entityID = seedDef.GetSeedEntityID();
                if (!NamespaceID.IsValid(entityID))
                    return -1f;
                var entityDef = content.GetEntityDefinition(entityID);
                if (entityDef == null)
                    return -1f;
                return entityDef.GetProperty<float>(EngineEntityProps.MAX_HEALTH);
            }
            catch (Exception e)
            {
                LogOnce("hp-read", "读蓝图血量失败：" + e.GetType().Name + ": " + e.Message);
                return -1f;
            }
        }

        /// <summary>
        /// 「逆阵营放置」标记 —— 按**卡**存在关卡存档旁边的标记文件里
        /// （见 <see cref="BlueprintFlags"/>）。真正生效在 <see cref="ReverseFactionPatch"/>：
        /// 放置时把生成的实体翻到右阵营。
        /// </summary>
        public static bool GetReverseFaction(LevelEngine level, string key)
        {
            return BlueprintFlags.GetReverse(level, key);
        }

        public static bool SetReverseFaction(LevelEngine level, string key, bool value)
        {
            try
            {
                var ok = BlueprintFlags.SetReverse(level, key, value);
                BlueprintMarks.Refresh();
                return ok;
            }
            catch (Exception e)
            {
                LogOnce("reversefaction", "改逆阵营失败：" + e.Message);
                return false;
            }
        }

        /// <summary>「模仿者」标记（按卡）。</summary>
        public static bool GetCommandFlag(LevelEngine level, string key)
        {
            return BlueprintFlags.GetCommandBlock(level, key);
        }

        public static bool SetCommandFlag(LevelEngine level, string key, bool value)
        {
            try
            {
                return BlueprintFlags.SetCommandBlock(level, key, value);
            }
            catch (Exception e)
            {
                LogOnce("cmdflag", "改模仿者标记失败：" + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 把「模仿者」标记同步到卡槽里的 SeedPack 上（每帧调一次，代价很小）。
        ///
        /// <para>为什么不做成「改的时候写一次」：传送带的卡是关卡中途从卡池里抽出来的，
        /// 抽出来的时候才会出现新的 SeedPack，所以得持续盯着。
        /// 键用 <see cref="BlueprintKeys.OfPack"/>（槽里的卡 → 槽位键；抽出来的 → 蓝图 id 键）。</para>
        /// </summary>
        public static void TickFlags(LevelEngine level)
        {
            if (level == null)
                return;
            try
            {
                var count = GetSlotCount(level);
                for (var i = 0; i < count; i++)
                {
                    var pack = GetPackAt(level, i);
                    if (pack == null)
                        continue;
                    var want = GetCommandFlag(level, BlueprintKeys.Slot(i));
                    if (pack.IsCommandBlock() != want)
                        pack.SetCommandBlock(want);
                }
                // 传送带抽出来的卡不在卡槽里，键是蓝图 id（卡池条目）。
                var drawn = GetConveyorPackCount(level);
                for (var i = 0; i < drawn; i++)
                {
                    var pack = GetConveyorPackAt(level, i);
                    if (pack == null)
                        continue;
                    var want = GetCommandFlag(level, BlueprintKeys.OfId(pack.GetDefinitionID()));
                    if (pack.IsCommandBlock() != want)
                        pack.SetCommandBlock(want);
                }
            }
            catch (Exception) { }
        }

        /// <summary>传送带抽出来的卡的数量 / 取第 <paramref name="index"/> 张（读不到就 0/null）。</summary>
        public static int GetConveyorPackCount(LevelEngine level)
        {
            try { return level == null ? 0 : level.GetConveyorSlotCount(); }
            catch (Exception) { return 0; }
        }

        public static SeedPack GetConveyorPackAt(LevelEngine level, int index)
        {
            try { return level == null ? null : level.GetConveyorSeedPackAt(index); }
            catch (Exception) { return null; }
        }
        #endregion

        #region 卡槽 / 卡组

        public static int GetSlotCount(LevelEngine level)
        {
            try { return level == null ? 0 : level.GetSeedSlotCount(); }
            catch (Exception) { return 0; }
        }

        public static bool SetSlotCount(LevelEngine level, int count)
        {
            if (level == null)
                return false;
            if (count < 0)
                count = 0;
            try
            {
                level.SetSeedSlotCount(count);
                return true;
            }
            catch (Exception e)
            {
                LogOnce("slots", "改卡槽数失败：" + e.Message);
                return false;
            }
        }

        public static SeedPack GetPackAt(LevelEngine level, int slot)
        {
            try { return level == null ? null : level.GetSeedPackAt(slot); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 换掉某一格。
        ///
        /// <para>没有走官方的 <c>GetLastBlueprint</c>（它的形参在 interop 里变成了
        /// <c>Il2CppSystem…IEnumerable&lt;ClassicSeedPack&gt;</c>，我们手上没有能塞进去的实例），
        /// 而是照官方 <c>SetupBattleBlueprints</c> 的做法直接新建 + 决定初始充能状态。</para>
        /// </summary>
        public static bool ReplaceBlueprint(LevelEngine level, int slot, NamespaceID blueprintID)
        {
            if (level == null || !NamespaceID.IsValid(blueprintID))
                return false;
            try
            {
                var old = level.GetSeedPackAt(slot);
                var commandBlock = old != null && GetCommandFlag(level, BlueprintKeys.Slot(slot)) != false;
                var pack = level.CreateSeedPack(blueprintID);
                if (pack == null)
                    return false;
                // 官方同款：开局（第 0 波）从满充能起，打到一半换卡就直接是可用状态。
                if (level.CurrentFlag <= 0)
                    pack.SetStartRecharge(true);
                else
                    pack.FullRecharge();
                if (commandBlock)
                    pack.SetCommandBlock(true);
                level.ReplaceSeedPackAt(slot, pack);
                // 这一格换成了别的蓝图 → 老的那一套标记跟着走（否则新卡会继承旧卡的设置）。
                var had = old == null ? null : old.GetDefinitionID();
                if (old != null && !SameId(had, blueprintID))
                    BlueprintFlags.ClearAll(level, BlueprintKeys.Slot(slot));
                BlueprintMarks.Refresh();
                return true;
            }
            catch (Exception e)
            {
                LogOnce("replace", "换卡失败：" + e.Message);
                return false;
            }
        }

        public static bool RemoveBlueprint(LevelEngine level, int slot)
        {
            try { return level != null && level.RemoveSeedPackAt(slot); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 把卡槽内容整体设成 <paramref name="picked"/>（内容没变的格子**不动**，
        /// 省得把那一格的充能进度重置掉）。两个页面的「调整选卡 / 调整卡池」共用它 ——
        /// 它们本来就是同一份数据。
        /// </summary>
        public static bool ApplySlots(LevelEngine level, List<NamespaceID> picked)
        {
            if (level == null)
                return false;
            try
            {
                var want = picked == null ? 0 : picked.Count;
                var total = Mathf.Max(1, want);
                SetSlotCount(level, total);
                for (var i = 0; i < total; i++)
                {
                    var pack = GetPackAt(level, i);
                    var have = pack == null ? null : pack.GetDefinitionID();
                    var target = i < want ? picked[i] : null;
                    if (!NamespaceID.IsValid(target))
                    {
                        if (pack != null)
                            RemoveBlueprint(level, i);
                        continue;
                    }
                    if (!SameId(have, target))
                        ReplaceBlueprint(level, i, target);
                }
                BlueprintMarks.Refresh();
                return true;
            }
            catch (Exception e)
            {
                LogOnce("applyslots", "写卡槽失败：" + e.Message);
                return false;
            }
        }

        /// <summary>诊断用：把卡槽填上若干张卡（自测里用来让蓝图页有东西可画）。</summary>
        public static string DebugFillSlots(LevelEngine level, int count)
        {
            if (level == null)
                return "不在关卡内";
            try
            {
                var ids = AllBlueprintIDs();
                if (ids.Count == 0)
                    return "没有可用的蓝图定义";
                SetSlotCount(level, count);
                var names = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    var id = ids[i % ids.Count];
                    ReplaceBlueprint(level, i, id);
                    names.Add(BlueprintName(id, false));
                }
                return "已填 " + count + " 格：" + string.Join(", ", names.ToArray());
            }
            catch (Exception e)
            {
                return "填卡失败：" + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>全部蓝图 id（官方定义组里的 seed 定义，实测 360 个）。</summary>
        public static List<NamespaceID> AllBlueprintIDs()
        {
            var result = new List<NamespaceID>();
            try
            {
                var game = Global.Game;
                if (game == null)
                    return result;
                var content = new IGameContent(game.Pointer);
                var seeds = content.GetDefinitions<SeedDefinition>("seed");
                if (seeds == null)
                    return result;
                for (var i = 0; i < seeds.Length; i++)
                {
                    var seed = seeds[i];
                    if (seed == null || !NamespaceID.IsValid(seed.id))
                        continue;
                    if (!Contains(result, seed.id))
                        result.Add(seed.id);
                }
            }
            catch (Exception e)
            {
                LogOnce("allbp", "枚举蓝图失败：" + e.Message);
            }
            return result;
        }

        public static string BlueprintName(NamespaceID id, bool commandBlock)
        {
            try
            {
                var main = Main;
                if (main == null || main.ResourceManager == null)
                    return Text(id);
                var name = main.ResourceManager.GetBlueprintName(id, commandBlock);
                return string.IsNullOrEmpty(name) ? Text(id) : name;
            }
            catch (Exception) { return Text(id); }
        }
        #endregion

        #region 传送带
        public static bool IsConveyorMode(LevelEngine level)
        {
            try { return level != null && level.IsConveyorMode(); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 切换传送带 / 自选卡模式（只切 HUD 那一行 + 本组件自己的开关）。
        ///
        /// <para><b>出卡不归它管</b>：真正抽卡的是关卡定义上的 <c>ConveyorStageBehaviour</c>。
        /// 非传送带关卡（定义里没有这个行为）切过去不会自动出卡 —— 补上的那一步在
        /// <see cref="ConveyorRig"/>：它给这类关卡自己建一个行为并每帧驱动。</para>
        /// </summary>
        public static bool SetConveyorMode(LevelEngine level, bool value)
        {
            if (level == null)
                return false;
            try
            {
                level.SetConveyorMode(value);
                return true;
            }
            catch (Exception e)
            {
                LogOnce("conveyormode", "切换传送带模式失败：" + e.Message);
                return false;
            }
        }

        public static int GetConveyorSlotCount(LevelEngine level)
        {
            try { return level == null ? 0 : level.GetConveyorSlotCount(); }
            catch (Exception) { return 0; }
        }

        public static bool SetConveyorSlotCount(LevelEngine level, int count)
        {
            if (level == null || count < 0)
                return false;
            try
            {
                level.SetConveyorSlotCount(count);
                return true;
            }
            catch (Exception) { return false; }
        }

        public static float GetConveySpeed(LevelEngine level)
        {
            try { return level == null ? 1f : level.GetConveySpeed(); }
            catch (Exception) { return 1f; }
        }

        public static bool SetConveySpeed(LevelEngine level, float speed)
        {
            if (level == null || speed < 0f)
                return false;
            try
            {
                level.SetProperty(LogicStageProps.CONVEY_SPEED, speed);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>当前关卡生效的卡池（原版只把它写在 Stage 定义上，关卡级可能是空的）。</summary>
        public static IConveyorPoolEntry[] GetPool(LevelEngine level)
        {
            try
            {
                if (level == null)
                    return null;
                var pool = level.GetConveyorPool();
                return pool;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 把卡池写到**关卡级**属性上（会随 .lvl 存盘）。
        /// ⚠️ 原版从不这么做（原版只把卡池写在关卡定义上），属于「接管」性质 →
        /// 归兼容性等级管（<see cref="GcFeature.ConveyorTakeover"/>，Full 档）。
        /// </summary>
        public static bool SetPool(LevelEngine level, List<PoolItem> items)
        {
            if (level == null)
                return false;
            if (!CompatConfig.IsEnabled(GcFeature.ConveyorTakeover))
            {
                LogOnce("pool-compat", "写卡池被兼容性等级挡住（需要 Full）："
                    + CompatConfig.NameOf(GcFeature.ConveyorTakeover));
                return false;
            }
            try
            {
                level.SetProperty(LogicStageProps.CONVEYOR_POOL, ItemsToPool(items));
                return true;
            }
            catch (Exception e)
            {
                LogOnce("pool", "写卡池失败：" + e.Message);
                return false;
            }
        }

        /// <summary>把卡池读成可编辑的条目（权重 = Count，数量限制 = MinCount）。</summary>
        public static List<PoolItem> PoolToItems(IConveyorPoolEntry[] pool)
        {
            var result = new List<PoolItem>();
            if (pool == null)
                return result;
            for (var i = 0; i < pool.Length; i++)
            {
                var e = pool[i];
                if (e == null || !NamespaceID.IsValid(e.ID))
                    continue;
                result.Add(new PoolItem { Id = e.ID, Weight = Mathf.Max(0, e.Count), Min = Mathf.Max(0, e.MinCount) });
            }
            return result;
        }

        public static Il2CppReferenceArray<IConveyorPoolEntry> ItemsToPool(List<PoolItem> items)
        {
            var count = items == null ? 0 : items.Count;
            var result = new Il2CppReferenceArray<IConveyorPoolEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var it = items[i];
                result[i] = MakeEntry(it.Id, Mathf.Max(0, it.Weight), Mathf.Max(0, it.Min));
            }
            return result;
        }

        /// <summary>
        /// 造一个原版的卡池条目。
        ///
        /// <para>⚠️ 关键：interop 把 il2cpp **接口生成成了类**（`Il2CppObjectBase` 的子类 + 指针构造器），
        /// 所以代理类型的 .NET 接口列表是空的 —— 「具体类型 → 接口」编译期就过不去
        /// （CS0029），运行期也抛 `Unable to cast object of type ... to type ...IConveyorPoolEntry`。
        /// 正确做法是在 il2cpp 里 `new` 出真对象，再用指针包一层接口视图
        /// （与 `new ITooltipTarget(obj.Pointer)` 同一套路）。</para>
        /// </summary>
        private static IConveyorPoolEntry MakeEntry(NamespaceID id, int count, int min)
        {
            var entry = new ConveyorPoolEntry(id, count, min);
            return new IConveyorPoolEntry(entry.Pointer);
        }

        internal sealed class PoolItem
        {
            public NamespaceID Id;
            public int Weight;   // = ConveyorPoolEntry.Count（既是初始数量也是权重）
            public int Min;      // = ConveyorPoolEntry.MinCount（抽空后补货下限）
        }

        /// <summary>当前总权重（需求里那个「当前总权重」）。</summary>
        public static int TotalWeight(List<PoolItem> items)
        {
            var sum = 0;
            for (var i = 0; items != null && i < items.Count; i++)
                sum += Mathf.Max(0, items[i].Weight);
            return sum;
        }
        #endregion

        #region 选卡预设
        /// <summary>是否有可复用的上次选卡（官方「重选」按钮的判据）。</summary>
        public static bool HasLastSelection()
        {
            try
            {
                var saves = Global.Saves;
                return saves != null && saves.GetLastSelection() != null;
            }
            catch (Exception) { return false; }
        }
        #endregion

        #region 工具
        public static bool SameId(NamespaceID a, NamespaceID b)
        {
            if (a == null || b == null)
                return a == null && b == null;
            return a.SpaceName == b.SpaceName && a.Path == b.Path;
        }

        private static bool Contains(List<NamespaceID> list, NamespaceID id)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (SameId(list[i], id))
                    return true;
            }
            return false;
        }

        private static string Text(NamespaceID id)
        {
            if (id == null)
                return "（无）";
            try { return id.SpaceName + ":" + id.Path; }
            catch (Exception) { return "?"; }
        }

        public static string Fmt(float v)
        {
            return v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static readonly HashSet<string> logged = new HashSet<string>();
        private static void LogOnce(string key, string message)
        {
            if (logged.Add(key))
                Plugin.Log?.LogWarning("[Blueprint] " + message);
        }
        #endregion
    }
}
