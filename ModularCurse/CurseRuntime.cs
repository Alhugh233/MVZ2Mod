using System;
using System.Collections.Generic;
using MVZ2.GameContent.Buffs;
using MVZ2.GameContent.Buffs.Contraptions;
using MVZ2.GameContent.Buffs.Entities;
using MVZ2.GameContent.Contraptions;
using MVZ2.GameContent.Damages;
using MVZ2.GameContent.Effects;
using MVZ2.GameContent.Seeds;
using MVZ2.Vanilla.Audios;
using MVZ2.Vanilla.Contraptions;
using MVZ2.Vanilla.Entities;
using MVZ2.Vanilla.Enemies;
using MVZ2.Vanilla.Level;
using MVZ2.Vanilla.Projectiles;
using MVZ2Logic.Armors;
using MVZ2Logic.Blueprints;
using MVZ2Logic.Entities;
using MVZ2Logic.Grids;
using MVZ2Logic.Level;
using PVZEngine;
using PVZEngine.Armors;
using PVZEngine.Buffs;
using PVZEngine.Damages;
using PVZEngine.Entities;
using PVZEngine.Grids;
using PVZEngine.Level;
using PVZEngine.SeedPacks;
using UnityEngine;

namespace ModularCurse
{
    /// <summary>
    /// 诅咒的运行期逻辑。
    ///
    /// 为什么逻辑不写在 buff 的触发器里：
    ///   原版 <c>AddTrigger*</c> 接收的是 IL2CPP 侧委托（Il2CppSystem.Action&lt;...&gt;），
    ///   而所有关卡回调的参数结构体都含引用字段（非 blittable），
    ///   Il2CppInterop 的 DelegateSupport.ConvertDelegate 直接拒绝：
    ///     "Delegate has parameter of type ... (non-blittable struct) which is not supported"
    ///   因此托管 mod 无法注册原生触发器。
    ///
    /// 分工：
    ///   - 诅咒 buff（QcBuffs / ScBuffs）负责"原生"部分：CURSE_COUNT 计数、
    ///     可用修饰符表达的效果（QC-C8 首波时间、SC-C7 重量、SC-C12 出怪间隔）；
    ///   - 本类负责需要回调/事件的效果与跨帧状态，由 <see cref="CurseHooks"/> 的
    ///     Harmony 补丁驱动，全部以"关卡是否挂着对应诅咒 buff"为开关。
    /// </summary>
    internal static class CurseRuntime
    {
        #region 常量（与文档 / curses.xml 保持一致）
        // QC-C1
        public const int SHORT_CIRCUIT_TICKS = 90;
        // QC-C2
        public const int TOOL_COOLDOWN_TICKS = 90;
        // QC-C3
        public const int WITHER_TICKS = 810;
        /// <summary>QC-C3：等待大招结束的最长时间（防止某些器械的 evoked 状态永不结束）。</summary>
        public const int QC_C3_MAX_WAIT_TICKS = 900;
        // QC-C4
        public const int QC_C4_CHANCE = 25;
        // QC-C6
        public const int QC_C6_CHANCE = 10;
        // QC-C8
        public const float FIRST_WAVE_ADVANCE_SECONDS = 5f;
        // QC-C9
        public const float PROJECTILE_SPEED_MULTIPLIER = 1f / 3f;
        // QC-C11
        public const int COST_PER_FLAG = 50;
        // QC-C12
        public const int LOAN_SIGN_COLUMN = 2;
        public const int LOAN_SIGN_DELAY = 3;
        // SC-C1（第 10 个同种器械后，每多 3 个使花费 +20%）
        public const int SC_C1_FREE_COUNT = 10;
        public const int SC_C1_STEP = 3;
        public const float SC_C1_COST_PER_STEP = 0.20f;
        // SC-C2
        public const int SC_C2_CHARM_TICKS = 60;
        // SC-C3（第 10 波后，敌人死亡 → 眩晕所有非免控器械 0.1s，可叠加）
        public const int SC_C3_MIN_WAVE = 10;
        public const int SC_C3_STUN_TICKS = 3;
        // SC-C4（每 4s 恢复 5%，最多 100 点，BOSS 不生效）
        public const int SC_C4_HEAL_INTERVAL = 4 * TICKS_PER_SECOND;
        public const float SC_C4_HEAL_PERCENT = 0.05f;
        public const float SC_C4_HEAL_CAP = 100f;
        // SC-C5（防具耐久 ×1.3，直接给血量）
        public const float SC_C5_ARMOR_MULTIPLIER = 1.3f;
        // SC-C7（击退量减半；接触地面的敌人重量至少为 HEAVY）
        public const float SC_C7_KNOCKBACK_MULTIPLIER = 0.5f;
        // SC-C8（第 10 波后首次死亡 → 复活至本体 15%）
        public const int SC_C8_MIN_WAVE = 10;
        public const float SC_C8_REVIVE_PERCENT = 0.15f;
        // SC-C9（debuff 累积超过 36 → 12s 状态抗性）
        public const float SC_C9_ACCUM_THRESHOLD = 36f;
        public const int SC_C9_IMMUNE_TICKS = 12 * TICKS_PER_SECOND;
        // SC-C10（出场 10s 不死，血量保底 1）
        public const int SC_C10_GRACE_TICKS = 10 * TICKS_PER_SECOND;
        // SC-C11
        public const float SC_C11_DAMAGE_MULTIPLIER = 0.4f;
        // SC-C12
        public const float SC_C12_INTERVAL_MULTIPLIER = 0.90f;
        // SC-C13（3s = 90 tick，计数与时间缩放无关）
        public const int SC_C13_SURVIVOR_TICKS = 90;
        public const int SC_C13_MIN_WAVE = 10;
        // SC-C14（每 10 波封禁 15s）
        public const int SC_C14_WAVE_INTERVAL = 10;
        public const int SC_C14_BAN_TICKS = 15 * TICKS_PER_SECOND;
        // SC-C15（每秒受伤上限 = max(当前血量/耐久 × 40%, 600)）
        public const float SC_C15_RATIO = 0.4f;
        public const float SC_C15_MIN_LIMIT = 600f;
        // SC-C16（固定减伤 5：本体与防具都算，且先于其它减伤；最终至少留 1 点）
        public const float SC_C16_FLAT_REDUCTION = 5f;

        public const int TICKS_PER_SECOND = 30;
        #endregion

        private static readonly NamespaceID IdQcC1 = CurseIds.Get("qc_c1");
        private static readonly NamespaceID IdQcC2 = CurseIds.Get("qc_c2");
        private static readonly NamespaceID IdQcC3 = CurseIds.Get("qc_c3");
        private static readonly NamespaceID IdQcC4 = CurseIds.Get("qc_c4");
        private static readonly NamespaceID IdQcC5 = CurseIds.Get("qc_c5");
        private static readonly NamespaceID IdQcC6 = CurseIds.Get("qc_c6");
        private static readonly NamespaceID IdQcC7 = CurseIds.Get("qc_c7");
        private static readonly NamespaceID IdQcC9 = CurseIds.Get("qc_c9");
        private static readonly NamespaceID IdQcC10 = CurseIds.Get("qc_c10");
        private static readonly NamespaceID IdQcC11 = CurseIds.Get("qc_c11");
        private static readonly NamespaceID IdQcC12 = CurseIds.Get("qc_c12");

        private static readonly NamespaceID IdScC1 = CurseIds.Get("sc_c1");
        private static readonly NamespaceID IdScC2 = CurseIds.Get("sc_c2");
        private static readonly NamespaceID IdScC3 = CurseIds.Get("sc_c3");
        private static readonly NamespaceID IdScC4 = CurseIds.Get("sc_c4");
        private static readonly NamespaceID IdScC5 = CurseIds.Get("sc_c5");
        private static readonly NamespaceID IdScC6 = CurseIds.Get("sc_c6");
        private static readonly NamespaceID IdScC7 = CurseIds.Get("sc_c7");
        private static readonly NamespaceID IdScC8 = CurseIds.Get("sc_c8");
        private static readonly NamespaceID IdScC9 = CurseIds.Get("sc_c9");
        private static readonly NamespaceID IdScC10 = CurseIds.Get("sc_c10");
        private static readonly NamespaceID IdScC11 = CurseIds.Get("sc_c11");
        private static readonly NamespaceID IdScC12 = CurseIds.Get("sc_c12");
        private static readonly NamespaceID IdScC13 = CurseIds.Get("sc_c13");
        private static readonly NamespaceID IdScC14 = CurseIds.Get("sc_c14");
        private static readonly NamespaceID IdScC15 = CurseIds.Get("sc_c15");
        private static readonly NamespaceID IdScC16 = CurseIds.Get("sc_c16");

        private static readonly NamespaceID IdZyC1 = CurseIds.Get("zy_c1");
        private static readonly NamespaceID IdZyC9 = CurseIds.Get("zy_c9");
        private static readonly NamespaceID IdZyC15 = CurseIds.Get("zy_c15");

        /// <summary>QM-C1 漏电豁免名单（金苹果 / 蜡烛）。懒初始化：不能在类型初始化器里碰游戏类型。</summary>
        private static NamespaceID[] shortCircuitExemptions;

        /// <summary>SC-C10 自检日志上限：因是衍生物而被跳过出场无敌的实体最多打几条。</summary>
        private const int SPAWN_INVINCIBLE_SKIP_LOG_LIMIT = 8;
        private static int spawnInvincibleSkipLogged;

        private static readonly System.Random rng = new System.Random();

        private static bool Roll(int percent)
        {
            return rng.Next(100) < percent;
        }

        private static bool On(LevelEngine level, NamespaceID id)
        {
            return CurseBuffBase.Active(level, id);
        }

        /// <summary>
        /// 每关一次的自检：关卡诅咒计数是否已包含本 mod 的诅咒。
        /// 期望值 = 生效的原版诅咒数 + 本 mod 生效的诅咒数（修饰符生效时才会相等）。
        /// </summary>
        private static void LogCurseCountDiag(LevelEngine level)
        {
            try
            {
                var ours = 0;
                for (var i = 0; i < CurseIds.Qc.Length; i++)
                {
                    if (On(level, CurseIds.Get(CurseIds.Qc[i])))
                        ours++;
                }
                for (var i = 0; i < CurseIds.Sc.Length; i++)
                {
                    if (On(level, CurseIds.Get(CurseIds.Sc[i])))
                        ours++;
                }
                Plugin.Log?.LogInfo("[Diag] 关卡诅咒计数 = " + level.GetCurseCount()
                    + "（含本 mod 生效 " + ours + " 条）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Diag] 诅咒计数自检失败: " + e.GetType().Name);
            }
        }

        #region 每帧
        /// <summary>关卡每帧末尾调用（挂在 LevelEngine.Update 之后）。</summary>
        public static void Tick(LevelEngine level)
        {
            if (level == null)
                return;
            try
            {
                if (CurseState.EnsureLevel(level))
                    spawnInvincibleSkipLogged = 0;

                if (CurseState.CountDiagLevel != level.Pointer)
                {
                    CurseState.CountDiagLevel = level.Pointer;
                    LogCurseCountDiag(level);
                }

                var needList = On(level, IdScC1) || On(level, IdScC2) || On(level, IdScC4)
                    || On(level, IdScC5) || On(level, IdScC7) || On(level, IdScC9) || On(level, IdScC10)
                    || On(level, IdScC11) || On(level, IdQcC6)
                    || ZyNeedsEntityList(level)
                    || CurseState.PendingWither.Count > 0;
                var entities = needList ? CurseBuffBase.FindAll(level) : null;

                if (On(level, IdScC1))
                    RefreshContraptionCounts(entities);
                if (On(level, IdScC11))
                    RefreshLaneFront(level, entities);

                TickDamageWindow(level);
                TickWaveGuard(level);
                TickToolCooldown(level);
                TickPendingWither(level, entities);
                TickHeal(level, entities);
                TickArmorScale(level, entities);
                TickStatusAccum(level, entities);
                TickStun();
                TickGrace(level);
                TickSurvivors(level);
                TickCharms(level);
                TickSpawnRolls(level, entities);
                TickLoanSign(level);
                ZyRuntime.Tick(level, entities);
                RefreshToolDisableState(level);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CurseRuntime.Tick] " + e);
            }
        }

        /// <summary>
        /// ZY 池里需要「关卡内全部实体」列表的诅咒。
        /// 与 SC 那几条一样：漏了这里就会出现"只勾这一条时静默失效"。
        /// </summary>
        private static bool ZyNeedsEntityList(LevelEngine level)
        {
            return On(level, IdZyC1) || On(level, IdZyC15);
        }

        /// <summary>SC-C1：每种器械在场的数量（费用结算读取）。</summary>
        private static void RefreshContraptionCounts(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity> entities)
        {
            CurseState.ContraptionCounts.Clear();
            if (entities == null)
                return;
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (entity == null || entity.Type != EntityTypes.PLANT)
                    continue;
                var id = entity.GetDefinitionID();
                if (id == null)
                    continue;
                CurseState.ContraptionCounts.TryGetValue(id, out var count);
                CurseState.ContraptionCounts[id] = count + 1;
            }
        }

        /// <summary>SC-C11：每行最靠前敌人的列号。</summary>
        private static void RefreshLaneFront(LevelEngine level,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity> entities)
        {
            CurseState.LaneFrontColumn.Clear();
            if (entities == null)
                return;
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (entity == null || !CurseBuffBase.IsEnemyType(entity.Type))
                    continue;
                // "最靠前" = 碰撞箱最左端所在的列（越靠左越深入我方）。
                var column = level.GetColumn(entity.GetBounds().min.x);
                var lane = entity.GetLane();
                if (!CurseState.LaneFrontColumn.TryGetValue(lane, out var front) || column < front)
                    CurseState.LaneFrontColumn[lane] = column;
            }
        }

        /// <summary>QC-C2：工具冷却倒计时（禁用状态统一在 <see cref="RefreshToolDisableState"/> 校准）。</summary>
        private static void TickToolCooldown(LevelEngine level)
        {
            // ZY-C5（排异反应）的禁用与 QC-C2 无关，必须无条件递减。
            if (CurseState.ZyStarshardBan > 0)
                CurseState.ZyStarshardBan--;
            if (!On(level, IdQcC2))
                return;
            if (CurseState.PickaxeCooldown > 0)
                CurseState.PickaxeCooldown--;
            if (CurseState.StarshardCooldown > 0)
                CurseState.StarshardCooldown--;
        }

        /// <summary>SC-C4（血肉核心）：敌怪本体每 4s 回复 5% 生命（每次最多 100 点，BOSS 不生效）。</summary>
        private static void TickHeal(LevelEngine level,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity> entities)
        {
            if (!On(level, IdScC4))
                return;
            if (--CurseState.HealTimer > 0)
                return;
            CurseState.HealTimer = SC_C4_HEAL_INTERVAL;
            if (entities == null)
                return;
            for (var i = 0; i < entities.Length; i++)
            {
                var enemy = entities[i];
                if (enemy == null || enemy.IsDead)
                    continue;
                // 只有 ENEMY 回血：BOSS 是独立类型，天然被排除。
                if (enemy.Type != EntityTypes.ENEMY)
                    continue;
                // SC-C13 不屈无敌期间不回血。
                if (CurseState.Survivors.ContainsKey(enemy.Pointer))
                    continue;
                var max = enemy.GetMaxHealth();
                if (!(max > 0) || enemy.Health >= max)
                    continue;
                var amount = Mathf.Min(max * SC_C4_HEAL_PERCENT, SC_C4_HEAL_CAP);
                if (amount <= 0)
                    continue;
                // 只回本体，不动防具。
                VanillaEntityExt.Heal(enemy, amount, null);
            }
        }

        /// <summary>
        /// SC-C5（龙头骨）：敌怪的防具耐久 ×1.3。
        ///
        /// 实现方式是**直接把上限与当前耐久都乘 1.3**（而不是给防具挂"受伤 ×0.77"的修饰符）——
        /// 需求要求"改为直接给血量，避免免伤计算"，这样它与 C15 的每秒上限、C16 的护甲
        /// 都不再叠加，也不会被别的免伤算法重复应用。
        /// 每件防具只乘一次（按防具指针去重，新生成的防具会被后续帧补上）。
        /// </summary>
        private static void TickArmorScale(LevelEngine level,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity> entities)
        {
            if (!On(level, IdScC5))
                return;
            if (entities == null)
                return;
            for (var i = 0; i < entities.Length; i++)
            {
                var enemy = entities[i];
                if (enemy == null || enemy.IsDead)
                    continue;
                if (!CurseBuffBase.IsEnemyType(enemy.Type))
                    continue;
                try
                {
                    var slots = new Il2CppSystem.Collections.Generic.List<NamespaceID>();
                    enemy.GetActiveArmorSlots(slots);
                    for (var j = 0; j < slots.Count; j++)
                    {
                        var slot = slots[j];
                        if (slot == null)
                            continue;
                        var armor = enemy.GetArmorAtSlot(slot);
                        if (!Armor.Exists(armor))
                            continue;
                        if (!CurseState.ScArmorScaled.Add(armor.Pointer))
                            continue;
                        var max = armor.GetMaxHealth();
                        if (max > 0)
                            armor.SetProperty(EngineArmorProps.MAX_HEALTH, max * SC_C5_ARMOR_MULTIPLIER);
                        armor.Health = armor.Health * SC_C5_ARMOR_MULTIPLIER;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning("[SCC5] 防具强化失败: " + e.GetType().Name);
                }
            }
        }

        /// <summary>
        /// SC-C9（古代历石）：敌怪受到的 debuff 累积时长超过 36s 后，12s 内无视所有 debuff。
        ///
        /// 累积方式 = 「持有 debuff 种类数 × 经过秒数」：每帧把当前持有的（表中）状态种类数
        /// 按 1/30 秒累加。例：同时持寒冰 + 发光 → 每秒 +2 → 18s 就满 36；
        /// 只持寒冰 → 每秒 +1 → 36s 才满。
        /// 进入状态抗性时清除已有的表中 debuff，并在抗性期间不再重新累积。
        /// </summary>
        private static void TickStatusAccum(LevelEngine level,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity> entities)
        {
            var active = On(level, IdScC9);
            if (!active && CurseState.ScStatusAccum.Count == 0 && CurseState.ScStatusImmune.Count == 0)
                return;
            if (entities == null)
                return;

            if (!active)
            {
                CurseState.ScStatusAccum.Clear();
                CurseState.ScStatusImmune.Clear();
                return;
            }

            for (var i = 0; i < entities.Length; i++)
            {
                var enemy = entities[i];
                if (enemy == null || enemy.IsDead)
                    continue;
                if (!CurseBuffBase.IsEnemyType(enemy.Type))
                    continue;

                // 抗性倒计时。
                int remain;
                if (CurseState.ScStatusImmune.TryGetValue(enemy.Pointer, out remain))
                {
                    remain--;
                    if (remain <= 0)
                        CurseState.ScStatusImmune.Remove(enemy.Pointer);
                    else
                        CurseState.ScStatusImmune[enemy.Pointer] = remain;
                    continue;
                }

                var kinds = CountStatusKinds(enemy);
                if (kinds <= 0)
                {
                    CurseState.ScStatusAccum.Remove(enemy.Pointer);
                    continue;
                }
                float accum;
                CurseState.ScStatusAccum.TryGetValue(enemy.Pointer, out accum);
                accum += kinds / (float)TICKS_PER_SECOND;
                if (accum <= SC_C9_ACCUM_THRESHOLD)
                {
                    CurseState.ScStatusAccum[enemy.Pointer] = accum;
                    continue;
                }
                // 进入状态抗性：清掉已有的表中 debuff + 12s 内不再获取。
                CurseState.ScStatusAccum.Remove(enemy.Pointer);
                CurseState.ScStatusImmune[enemy.Pointer] = SC_C9_IMMUNE_TICKS;
                ClearStatusDebuffs(enemy);
                Plugin.Log?.LogInfo("[SCC9] " + enemy.GetDefinitionID() + " 进入 12s 状态抗性");
            }
        }

        /// <summary>SC-C9：该敌怪当前持有的（表中）状态效果种类数。</summary>
        private static int CountStatusKinds(Entity enemy)
        {
            var list = StatusTable;
            var n = 0;
            for (var i = 0; i < list.Length; i++)
            {
                if (HasBuff(enemy, list[i]))
                    n++;
            }
            return n;
        }

        /// <summary>SC-C9：清掉表中的全部状态效果。</summary>
        private static void ClearStatusDebuffs(Entity enemy)
        {
            var list = StatusTable;
            for (var i = 0; i < list.Length; i++)
                CurseBuffBase.RemoveBuffsOf(enemy, list[i]);
        }

        private static NamespaceID[] statusTable;
        /// <summary>SC-C9 的状态效果表（沿用之前诅咒用的那一套）。</summary>
        private static NamespaceID[] StatusTable
        {
            get
            {
                var list = statusTable;
                if (list == null)
                {
                    list = new NamespaceID[]
                    {
                        VanillaBuffID.Enemy.slow,           // 寒冰（减速）
                        VanillaBuffID.Entity.transfenserGlowing, // 发光
                        VanillaBuffID.Enemy.enemyWeakness,  // 虚弱
                        VanillaBuffID.Entity.withered,      // 凋零
                        VanillaBuffID.Entity.petrified,     // 石化
                        VanillaBuffID.Entity.burning,       // 燃烧
                        BuiltinBuff("frozen"),              // 冰冻（该 buff 在早期内测版的 interop 里没有静态字段，用原始 ID）
                        VanillaBuffID.Enemy.gravelOnFace,   // 沙砾
                    };
                    statusTable = list;
                }
                return list;
            }
        }

        /// <summary>构造原版命名空间下的 ID（用于 interop 里没有静态字段的 buff）。</summary>
        private static NamespaceID BuiltinBuff(string path)
        {
            return new NamespaceID("mvz2", path);
        }

        /// <summary>实体身上是否挂着某个 buff。</summary>
        private static bool HasBuff(Entity entity, NamespaceID id)
        {
            if (entity == null || !NamespaceID.IsValid(id))
                return false;
            try
            {
                return new IBuffTarget(entity.Pointer).Buffs.HasBuff(id);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// SC-C9：这个状态效果是否被 12s 状态抗性挡下（true = 拦截本次施加）。
        /// 由 <see cref="CurseHooks"/> 的 PreApplyStatusEffect 前缀调用。
        /// </summary>
        public static bool IsStatusImmune(Entity entity, BuffDefinition definition)
        {
            if (entity == null || definition == null)
                return false;
            try
            {
                var level = entity.Level;
                if (level == null || !On(level, IdScC9))
                    return false;
                if (!CurseBuffBase.IsEnemyType(entity.Type))
                    return false;
                if (!CurseState.ScStatusImmune.ContainsKey(entity.Pointer))
                    return false;
                var id = definition.GetID();
                var list = StatusTable;
                for (var i = 0; i < list.Length; i++)
                {
                    if (id != null && id.SpaceName == list[i].SpaceName && id.Path == list[i].Path)
                        return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>SC-C10：出场 10s 不死窗口倒计时（血量保底 1）。</summary>
        private static void TickGrace(LevelEngine level)
        {
            if (CurseState.ScGrace.Count == 0)
                return;
            if (!On(level, IdScC10))
            {
                CurseState.ScGrace.Clear();
                return;
            }
            List<IntPtr> expired = null;
            foreach (var pair in CurseState.ScGrace)
            {
                var remaining = pair.Value - 1;
                CurseState.ScGrace[pair.Key] = remaining;
                if (remaining <= 0)
                    (expired ??= new List<IntPtr>()).Add(pair.Key);
            }
            if (expired == null)
                return;
            foreach (var ptr in expired)
                CurseState.ScGrace.Remove(ptr);
        }

        /// <summary>SC-C13：不屈计时结束 → 摘掉无敌 buff 并执行死亡。</summary>
        private static void TickSurvivors(LevelEngine level)
        {
            if (CurseState.Survivors.Count == 0)
                return;
            List<IntPtr> expired = null;
            foreach (var pair in CurseState.Survivors)
            {
                var remaining = pair.Value - 1;
                CurseState.Survivors[pair.Key] = remaining;
                if (remaining <= 0)
                    (expired ??= new List<IntPtr>()).Add(pair.Key);
            }
            if (expired == null)
                return;
            foreach (var ptr in expired)
            {
                CurseState.Survivors.Remove(ptr);
                var entity = CurseBuffBase.FindByPointer(level, ptr);
                if (entity == null || entity.IsDead)
                    continue;
                Plugin.Log?.LogInfo("[SCC13] 不屈结束，敌怪正常死亡");
                ClearSurvivorBuff(entity);
                entity.Die();
            }
        }

        /// <summary>SC-C2：叛变计时结束 → 还原阵营。</summary>
        private static void TickCharms(LevelEngine level)
        {
            if (CurseState.Charmed.Count == 0)
                return;
            List<IntPtr> expired = null;
            foreach (var pair in CurseState.Charmed)
            {
                var remaining = pair.Value - 1;
                CurseState.Charmed[pair.Key] = remaining;
                if (remaining <= 0)
                    (expired ??= new List<IntPtr>()).Add(pair.Key);
            }
            if (expired == null)
                return;
            foreach (var ptr in expired)
            {
                CurseState.Charmed.Remove(ptr);
                var entity = CurseBuffBase.FindByPointer(level, ptr);
                if (entity == null || entity.IsDead)
                    continue;
                try
                {
                    entity.RemoveCharm(null);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning("[SCC2] remove charm failed: " + e.GetType().Name);
                }
            }
        }

        /// <summary>QC-C6 隐形 / SC-C2 叛变 / SC-C10 出场 10s 不死：首次见到实体时处理一次。</summary>
        private static void TickSpawnRolls(LevelEngine level,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity> entities)
        {
            var needInvisible = On(level, IdQcC6);
            var needCharm = On(level, IdScC2);
            var needGrace = On(level, IdScC10);
            if (!needInvisible && !needCharm && !needGrace)
                return;
            if (entities == null)
                return;
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (entity == null)
                    continue;

                if (needGrace && CurseBuffBase.IsEnemyType(entity.Type)
                    && CurseState.SpawnInvincibleApplied.Add(entity.Pointer))
                {
                    // SC-C10 只给「自然生成」的敌怪：衍生物（泡泡枪的泡泡、炮筒的炮弹僵尸、
                    // 死灵法师的骨墙、恐怖之母的寄生虫……）全部是被别的实体 Spawn 出来的，不算。
                    // 注意：无论是否生效都要记进 SpawnInvincibleApplied，避免每帧重复判断。
                    if (IsNaturallySpawned(entity))
                    {
                        CurseState.ScGrace[entity.Pointer] = SC_C10_GRACE_TICKS;
                    }
                    else if (spawnInvincibleSkipLogged < SPAWN_INVINCIBLE_SKIP_LOG_LIMIT)
                    {
                        spawnInvincibleSkipLogged++;
                        Plugin.Log?.LogInfo("[SCC10] 衍生物不享受出场保护：" + entity.GetDefinitionID()
                            + (spawnInvincibleSkipLogged >= SPAWN_INVINCIBLE_SKIP_LOG_LIMIT ? "（后续不再记录）" : ""));
                    }
                }
                if (needInvisible && CurseBuffBase.IsEnemyType(entity.Type)
                    && CurseState.InvisibilityRolled.Add(entity.Pointer))
                {
                    if (Roll(QC_C6_CHANCE))
                        ApplyInvisibility(entity);
                }
                if (needCharm && entity.Type == EntityTypes.PLANT && CurseState.CharmProcessed.Add(entity.Pointer))
                {
                    TryCharm(entity);
                }
            }
        }

        /// <summary>
        /// SC-C10（灾厄链甲）：该敌怪是否还在「出场 10s 不死」的保护窗口里。
        /// 由死亡前缀调用（返回 true = 取消本次死亡），触发时把血量顶回 1 点
        /// —— 满足「至少保留 1 点血量」。
        /// </summary>
        public static bool PreventDeathByGrace(Entity enemy, DeathInfo info)
        {
            if (enemy == null || CurseState.ScGrace.Count == 0)
                return false;
            if (!CurseState.ScGrace.ContainsKey(enemy.Pointer))
                return false;
            // 溺水 / 坠空这类"不可复活"的死因不拦（否则会留下永远出不去的实体）。
            if (info != null && info.HasEffect(VanillaDamageEffects.NO_REVIVAL))
                return false;
            if (enemy.Health < 1f)
                enemy.Health = 1f;
            Plugin.Log?.LogInfo("[SCC10] 出场保护：本次死亡被取消（血量顶回 1）");
            return true;
        }

        /// <summary>
        /// 小推车碾压（<c>INSTA_KILL</c>）无视诅咒给予的无敌 —— 需求「无敌不能免疫小推车造成的伤害」。
        /// 由 <c>VanillaEntityExt.TakeDamage</c> 前缀调用：在游戏检查 <c>IsInvincible()</c> 之前
        /// 把本 mod 挂上的铁幕无敌摘掉（只摘 SC-C13 不屈那一份）。
        /// </summary>
        public static void AllowInstaKillThroughCurseInvincibility(DamageInput input)
        {
            if (input == null || input.Entity == null)
                return;
            if (!input.HasEffect(VanillaDamageEffects.INSTA_KILL))
                return;
            var entity = input.Entity;
            if (!CurseState.Survivors.ContainsKey(entity.Pointer))
                return;
            CurseState.Survivors.Remove(entity.Pointer);
            ClearSurvivorBuff(entity);
            Plugin.Log?.LogInfo("[SCC13] 小推车伤害无视诅咒无敌");
        }

        /// <summary>
        /// SC-C10：只有「自然生成」的敌怪才有出场保护，衍生物一律豁免。
        ///
        /// 判定依据是实体身上没有生成来源（<c>Entity.SpawnerReference == null</c>）：
        ///   - 原版波次出怪走 <c>level.Spawn(entityDef, pos, null)</c>（见 LogicLevelExt.SpawnEnemy），
        ///     来源必然为空 → 算自然生成；
        ///   - 所有衍生物都是被别的实体 Spawn 出来的（泡泡枪 <c>entity.Spawn</c>、
        ///     炮筒 <c>SpawnWithParams</c>、死灵法师骨墙、恐怖之母寄生虫……），
        ///     来源非空 → 不算。
        ///
        /// 不看类型名 / 定义 ID，因此以后新增的衍生物自动适用，不需要维护名单。
        /// </summary>
        private static bool IsNaturallySpawned(Entity entity)
        {
            try
            {
                return entity.SpawnerReference == null;
            }
            catch (Exception e)
            {
                // 读不出来时按「衍生物」处理：宁可少给保护，也不能让衍生物白拿保护
                Plugin.Log?.LogWarning("[SCC10] 读取生成来源失败，按衍生物处理: " + e.GetType().Name);
                return false;
            }
        }

        /// <summary>QC-C12：关卡开始后延迟几帧生成借贷告示牌。</summary>
        private static void TickLoanSign(LevelEngine level)
        {
            if (!On(level, IdQcC12))
                return;
            if (CurseState.LoanSignPlaced)
                return;
            // 读档进入的关卡里，存档本身就是「上一局生成过告示牌」的状态：
            // CurseState 是托管静态字段、跟着进程走，读档后 LoanSignPlaced 又是 false，
            // 于是每读一次档就再补一块 —— 所以先问关卡自己有没有（见 HasLoanSign）。
            if (HasLoanSign(level))
            {
                CurseState.LoanSignPlaced = true;
                Plugin.Log?.LogInfo("[QCC12] 关卡里已存在借贷告示牌（读档），本次不再生成");
                return;
            }
            if (CurseState.LoanSignDelay < LOAN_SIGN_DELAY)
            {
                CurseState.LoanSignDelay++;
                return;
            }
            CurseState.LoanSignPlaced = true;
            PlaceLoanSign(level);
        }

        /// <summary>
        /// QC-C12：关卡里是否已经有借贷告示牌。
        ///
        /// 用「关卡里的实体」而不是托管侧的记忆来判断 —— 实体是跟着存档走的，
        /// 因此读档后能正确认出上一局留下的那一块（而 <see cref="CurseState"/> 不会）。
        /// </summary>
        private static bool HasLoanSign(LevelEngine level)
        {
            try
            {
                var all = CurseBuffBase.FindAll(level);
                if (all == null)
                    return false;
                var wanted = VanillaContraptionID.loanSign;
                for (var i = 0; i < all.Length; i++)
                {
                    var entity = all[i];
                    if (entity == null)
                        continue;
                    var id = entity.GetDefinitionID();
                    if (id != null && id.SpaceName == wanted.SpaceName && id.Path == wanted.Path)
                        return true;
                }
            }
            catch (Exception e)
            {
                // 查不出来时按「没有」处理：宁可多生成一块，也不要整条诅咒失效。
                Plugin.Log?.LogWarning("[QCC12] 检查已有告示牌失败: " + e.GetType().Name);
            }
            return false;
        }
        #endregion

        #region QC-C1 漏电
        /// <summary>器械受伤后补一次 3s 漏电（不会触发 TNT → 高压 TNT）。</summary>
        public static void ShortCircuit(Entity contraption)
        {
            if (contraption == null || !On(contraption.Level, IdQcC1))
                return;
            if (IsShortCircuitExempt(contraption))
                return;
            try
            {
                VanillaEntityExt.ShortCircuit(contraption, SHORT_CIRCUIT_TICKS, null);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[QCC1] " + e.GetType().Name);
            }
        }

        /// <summary>
        /// 金苹果 / 蜡烛的啃食转换效果会先检查 <c>target.IsAIFrozen()</c>，
        /// 而漏电自带的 frankenstein_shocked 恰好会设置 AI_FROZEN ——
        /// 不豁免的话这两个器械被啃时自己的核心功能会被禁掉。
        /// </summary>
        private static bool IsShortCircuitExempt(Entity contraption)
        {
            var list = shortCircuitExemptions;
            if (list == null)
            {
                list = new NamespaceID[] { VanillaContraptionID.goldenApple, VanillaContraptionID.cursedCandle };
                shortCircuitExemptions = list;
            }
            var id = contraption.GetDefinitionID();
            if (id == null)
                return false;
            for (var i = 0; i < list.Length; i++)
            {
                if (id.SpaceName == list[i].SpaceName && id.Path == list[i].Path)
                    return true;
            }
            return false;
        }
        #endregion

        #region QC-C2 工具冷却
        /// <summary>使用镐子/星之碎片后给该工具单独上 3s 冷却。</summary>
        public static void StartToolCooldown(Entity entity, bool pickaxe)
        {
            if (entity == null)
                return;
            var level = entity.Level;
            if (!On(level, IdQcC2))
                return;
            if (pickaxe)
                CurseState.PickaxeCooldown = TOOL_COOLDOWN_TICKS;
            else
                CurseState.StarshardCooldown = TOOL_COOLDOWN_TICKS;
            RefreshToolDisableState(level);
        }

        /// <summary>
        /// 把两件工具的禁用状态校准到当前状态（幂等）。
        /// 直接写关卡禁用属性可以复用原版"衰老"的禁用理由与图标，
        /// 且不影响由 buff modifier 提供的禁用。
        ///
        /// 禁用来源：QC-C2（使用后的 3s 冷却）、SC-C14（每 10 波的 20s 封禁）。
        /// 只在"我们曾经写过"或"现在需要写"的情况下动它，
        /// 避免误删关卡 meta 等其它来源设置的禁用。
        /// </summary>
        public static void RefreshToolDisableState(LevelEngine level)
        {
            if (level == null)
                return;
            var banned = IsWaveGuardActive(level);
            var pickaxe = banned || CurseState.PickaxeCooldown > 0;
            // ZY-C5（排异反应）累积的星之碎片禁用也走同一个禁用位。
            var starshard = banned || CurseState.StarshardCooldown > 0 || CurseState.ZyStarshardBan > 0;
            if (!pickaxe && !starshard && !CurseState.ToolsDisabledByUs)
                return;
            CurseState.ToolsDisabledByUs = pickaxe || starshard;
            SetToolDisabled(level, true, pickaxe);
            SetToolDisabled(level, false, starshard);
        }

        private static void SetToolDisabled(LevelEngine level, bool pickaxe, bool disabled)
        {
            var idKey = pickaxe ? LogicLevelProps.PICKAXE_DISABLE_ID : LogicLevelProps.STARSHARD_DISABLE_ID;
            var iconKey = pickaxe ? LogicLevelProps.PICKAXE_DISABLE_ICON : LogicLevelProps.STARSHARD_DISABLE_ICON;

            if (disabled)
            {
                level.SetProperty(idKey, VanillaBlueprintErrors.decrepify);
                level.SetProperty(iconKey, true);
                return;
            }
            // 关闭：只清理我们自己写过的值，避免误删其它来源（如关卡 meta）设置的禁用。
            if (level.TryGetProperty<NamespaceID>(idKey, out var current, true) && IsDecrepify(current))
            {
                level.SetProperty<NamespaceID>(idKey, null);
            }
        }

        private static bool IsDecrepify(NamespaceID id)
        {
            if (id == null)
                return false;
            var reason = VanillaBlueprintErrors.decrepify;
            return id.SpaceName == reason.SpaceName && id.Path == reason.Path;
        }
        #endregion

        #region QC-C3 凋零
        /// <summary>
        /// 器械被星之碎片激发后获得 27s 凋零。
        ///
        /// 不能立刻施加：凋零（衰老）会让器械失效，而**大招往往跨多帧**执行
        /// （如双向发射器的连续射击），当场打断就等于"大招被诅咒吃了"。
        /// 因此这里只入队，由 <see cref="TickPendingWither"/> 等到
        /// <c>entity.IsEvoked()</c> 变成 false（大招结束）再真正上凋零。
        /// </summary>
        public static void ApplyWither(Entity contraption)
        {
            if (contraption == null || !On(contraption.Level, IdQcC3))
                return;
            CurseState.PendingWither[contraption.Pointer] = QC_C3_MAX_WAIT_TICKS;
            Plugin.Log?.LogInfo("[QCC3] 已入队凋零（等大招结束）");
        }

        /// <summary>QC-C3：大招结束后施加凋零。</summary>
        private static void TickPendingWither(LevelEngine level,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity> entities)
        {
            if (CurseState.PendingWither.Count == 0)
                return;
            if (!On(level, IdQcC3))
            {
                CurseState.PendingWither.Clear();
                return;
            }
            var keys = new List<IntPtr>(CurseState.PendingWither.Keys);
            for (var i = 0; i < keys.Count; i++)
            {
                var ptr = keys[i];
                var entity = FindIn(entities, ptr);
                if (entity == null || entity.IsDead)
                {
                    CurseState.PendingWither.Remove(ptr);
                    continue;
                }
                var remaining = CurseState.PendingWither[ptr] - 1;
                var evoking = false;
                try
                {
                    evoking = entity.IsEvoked();
                }
                catch (Exception)
                {
                }
                if (evoking && remaining > 0)
                {
                    CurseState.PendingWither[ptr] = remaining;
                    continue;
                }
                CurseState.PendingWither.Remove(ptr);
                WitherNow(entity);
            }
        }

        /// <summary>真正施加凋零（直接写 timeout，避开 SC-C9 的时长缩放）。</summary>
        private static void WitherNow(Entity contraption)
        {
            try
            {
                var definition = contraption.Level.Content.GetBuffDefinition(VanillaBuffID.Entity.withered);
                if (definition == null)
                    return;
                var buff = CurseBuffBase.AddBuff(contraption, definition);
                if (buff == null)
                    return;
                buff.SetProperty(WitheredBuff.PROP_TIMEOUT, WITHER_TICKS);
                Plugin.Log?.LogInfo("[QCC3] 凋零已施加");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[QCC3] " + e.GetType().Name);
            }
        }

        /// <summary>在当前帧的实体快照里按指针找实体。</summary>
        private static Entity FindIn(
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity> entities, IntPtr ptr)
        {
            if (entities == null)
                return null;
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (entity != null && entity.Pointer == ptr)
                    return entity;
            }
            return null;
        }
        #endregion

        #region QC-C4 尸体复活
        /// <summary>
        /// 尸体即将消失（<c>VanillaEnemyExt.FaintRemove</c>）时调用。
        /// 返回 true = 取消消失，让尸体原地复活。
        ///
        /// 为什么挂在这里，而不是死亡瞬间（<c>Entity.Die</c>）：
        ///   与「控制灵符」「尸解仙竹杖」一致 —— 只有"死亡并倒下"（留有尸体）
        ///   的敌人能走到 FaintRemove（无尸体的敌人在 EnemyCommonBehaviour.PostDeath
        ///   里就已 Remove）。所以"从尸体里爬起来"这件事在物理上只能发生在这一刻。
        ///
        /// 注意：SC-C8（寄生藤蔓）**不**走这条路 —— 它要能突破"地雷/蜡烛/核弹/石砖加农炮"
        /// 这类销毁尸体的死因，因此改在 <c>Entity.Die</c> 前缀里直接拦下死亡（见 TrySurviveDeath）。
        /// </summary>
        public static bool BeforeFaintRemove(Entity enemy)
        {
            if (enemy == null || !CurseBuffBase.IsEnemyType(enemy.Type))
                return false;
            try
            {
                var level = enemy.Level;
                if (level == null)
                    return false;

                // ---- QC-C4：25% 从尸体中复活（BOSS 除外） ----
                if (On(level, IdQcC4) && CurseState.QcRevived.Add(enemy.Pointer)
                    && TryQcRevive(enemy))
                {
                    return true;
                }
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[QCC4] FaintRemove 判定失败: " + e.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// QC-C4：从尸体中复活（满血）。
        ///
        /// 与 SC-C4 的区别：BOSS 不复活、不恢复防具（只是把本体拉起来），
        /// 并且不受波数限制 —— 完全按 QC-C4 原本的数值（25%）来。
        /// </summary>
        private static bool TryQcRevive(Entity enemy)
        {
            if (enemy.Type == EntityTypes.BOSS)
                return false;
            var lethal = enemy.GetLethalDeathInfo();
            if (lethal != null)
            {
                // 不可复活的死因（如某些特效伤害）。
                if (lethal.HasEffect(VanillaDamageEffects.NO_REVIVAL))
                    return false;
                // 无尸体的死因（理应走不到这里，保险）。
                if (enemy.WillRemoveOnDeath(lethal))
                    return false;
                // 复活视为亡语：不触发亡语的死因（地雷、蜡烛等）不会复活。
                if (!enemy.ShouldTriggerDeathEffects(lethal))
                    return false;
            }
            if (!Roll(QC_C4_CHANCE))
                return false;
            enemy.Revive();
            enemy.Health = enemy.GetMaxHealth();
            enemy.PlaySound(VanillaSoundID.revived);
            Plugin.Log?.LogInfo("[QCC4] 敌怪复活（尸体）");
            return true;
        }

        /// <summary>SC-C8：把尸体完全复活（满血 + 防具满耐久）。已被移除（旧 SC-C4 的实现）。</summary>
        #endregion

        #region QC-C5 死亡留火
        /// <summary>实体死亡前调用（此时地格仍有效）。</summary>
        public static void LeaveFire(Entity entity)
        {
            if (entity == null)
                return;
            var type = entity.Type;
            if (type != EntityTypes.PLANT && !CurseBuffBase.IsEnemyType(type))
                return;
            var level = entity.Level;
            if (!On(level, IdQcC5))
                return;
            try
            {
                // 预先指定阵营，让火焰从诞生起就是敌方
                // （器械死亡产生的火焰原本是友方，会去烧敌人）。
                var spawnParam = entity.GetSpawnParams();
                spawnParam.SetProperty(EngineEntityProps.FACTION, level.GetRightFaction());

                // 石砖加农炮占两格，全部留火；其余实体只留一格。
                var defID = entity.GetDefinitionID();
                var multiGrid = defID != null
                    && defID.SpaceName == VanillaContraptionID.brickCannon.SpaceName
                    && defID.Path == VanillaContraptionID.brickCannon.Path;

                if (multiGrid)
                {
                    var grids = new Il2CppSystem.Collections.Generic.List<LawnGrid>();
                    entity.GetTakenGridsNonAlloc(grids);
                    for (var i = 0; i < grids.Count; i++)
                        SpawnGridFire(grids[i], entity, spawnParam);
                    return;
                }

                var grid = entity.GetGrid();
                if (grid == null)
                {
                    var grids = new Il2CppSystem.Collections.Generic.List<LawnGrid>();
                    entity.GetTakenGridsNonAlloc(grids);
                    if (grids.Count > 0)
                        grid = grids[0];
                }
                SpawnGridFire(grid, entity, spawnParam);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[QCC5] " + e.GetType().Name);
            }
        }

        private static void SpawnGridFire(LawnGrid grid, Entity source, SpawnParams spawnParam)
        {
            if (grid == null)
                return;
            try
            {
                var enemyFaction = source.Level.GetRightFaction();
                var fire = GridFire.Spawn(grid, source, spawnParam);
                if (fire != null)
                    fire.SetFaction(enemyFaction);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[QCC5] GridFire.Spawn failed: " + e.GetType().Name);
            }
        }
        #endregion

        #region QC-C6 隐形
        private static void ApplyInvisibility(Entity enemy)
        {
            try
            {
                var definition = enemy.Level.Content.GetBuffDefinition(VanillaBuffID.Enemy.darkMatterInvisible);
                if (definition == null)
                    return;
                CurseBuffBase.AddBuff(enemy, definition);
                Plugin.Log?.LogInfo("[QCC6] 敌怪隐形");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[QCC6] " + e.GetType().Name);
            }
        }
        #endregion

        #region QC-C7 漫长充能
        /// <summary>把"很长"（≥50s）的充能时长统一改为 75s。</summary>
        public static int ReplaceLongRecharge(SeedPack seedPack, int ticks)
        {
            if (seedPack == null)
                return ticks;
            var level = seedPack.Level;
            if (!On(level, IdQcC7))
                return ticks;
            if (ticks >= QcC7Buff.LONG_RECHARGE_THRESHOLD)
                return QcC7Buff.LONG_RECHARGE_REPLACED;
            return ticks;
        }
        #endregion

        #region QC-C9 子弹速度
        public static void ModifyProjectileSpeed(Entity shooter, ProjectileShootArgs args)
        {
            if (shooter == null || args == null)
                return;
            if (shooter.Type != EntityTypes.PLANT)
                return;
            if (!On(shooter.Level, IdQcC9))
                return;
            try
            {
                args.velocity *= PROJECTILE_SPEED_MULTIPLIER;
                if (args.spawnParam != null)
                    args.spawnParam.EntityVelocity *= PROJECTILE_SPEED_MULTIPLIER;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[QCC9] " + e.GetType().Name);
            }
        }
        #endregion

        #region QC-C10 穿透阻挡
        private static Entity pierceTarget;

        public static void SetPierceTarget(Entity target)
        {
            pierceTarget = target;
        }

        /// <summary>重质量敌怪可以让穿透弹幕停下。</summary>
        public static bool BlockPierce(Entity projectile, bool originalResult)
        {
            if (!originalResult)
                return false;
            if (projectile == null || pierceTarget == null)
                return false;
            if (!On(projectile.Level, IdQcC10))
                return false;
            try
            {
                if (!CurseBuffBase.IsEnemyType(pierceTarget.Type))
                    return false;
                if (pierceTarget.GetMass() < VanillaMass.HEAVY)
                    return false;
                Plugin.Log?.LogInfo("[QCC10] 穿透被重质量敌怪阻挡");
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        #endregion

        #region QC-C11 大波涨价 / SC-C1 人口税
        /// <summary>
        /// QC-C11 的涨价档位：当前波属于第几个大波。
        /// 大波在 wavesPerFlag 的整数倍波（w10 / w20 …）触发，因此这里用
        /// <c>CurrentWave / wavesPerFlag</c> 而非 <c>CurrentFlag</c> ——
        /// 后者在该大波**打完之后**才自增，会让涨价滞后到 w11。
        /// </summary>
        public static int GetFlagsForCost(LevelEngine level)
        {
            var perFlag = level.GetWavesPerFlag();
            if (perFlag <= 0)
                return 0;
            return level.CurrentWave / perFlag;
        }

        /// <summary>调整蓝图花费（QC-C11 每大波 +50；SC-C1 超出 10 个后每个 +10%）。</summary>
        public static int AdjustCost(SeedPack seedPack, int cost)
        {
            if (seedPack == null)
                return cost;
            var value = (float)cost;
            try
            {
                var level = seedPack.Level;
                if (level == null)
                    return cost;

                if (On(level, IdQcC11))
                    value += COST_PER_FLAG * GetFlagsForCost(level);

                if (On(level, IdScC1))
                {
                    var entityID = LogicSeedProps.GetSeedEntityID(seedPack);
                    if (entityID != null && CurseState.ContraptionCounts.TryGetValue(entityID, out var count))
                    {
                        // 第 10 个之后的每 3 个：13 → +20%，16 → +40%……
                        var extra = (count - SC_C1_FREE_COUNT) / SC_C1_STEP;
                        if (extra > 0)
                            value *= 1f + SC_C1_COST_PER_STEP * extra;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Cost] " + e.GetType().Name);
            }
            return Mathf.Max(0, Mathf.FloorToInt(value));
        }
        #endregion

        #region QC-C12 借贷告示牌
        private static void PlaceLoanSign(LevelEngine level)
        {
            try
            {
                var laneCount = level.GetMaxLaneCount();
                if (laneCount <= 0)
                    return;

                var loanID = VanillaContraptionID.loanSign;
                var loanDef = level.Content.GetEntityDefinition(loanID);
                if (loanDef == null)
                    return;

                // 随机顺序尝试各条路：优先放到能直接放告示牌的地格。
                var lanes = new List<int>(laneCount);
                for (var i = 0; i < laneCount; i++)
                    lanes.Add(i);
                for (var i = lanes.Count - 1; i > 0; i--)
                {
                    var j = rng.Next(i + 1);
                    var tmp = lanes[i];
                    lanes[i] = lanes[j];
                    lanes[j] = tmp;
                }

                var waterLane = -1;
                for (var i = 0; i < lanes.Count; i++)
                {
                    var lane = lanes[i];
                    var grid = level.GetGrid(LOAN_SIGN_COLUMN, lane);
                    if (grid == null)
                        continue;
                    if (CanPlace(grid, loanDef))
                    {
                        PlaceBlueprint(level, grid, loanID, "借贷告示牌");
                        return;
                    }
                    if (waterLane < 0)
                        waterLane = lane;
                }

                // 所有路都放不了（全是水路）→ 选一条先生睡莲，再放告示牌。
                if (waterLane < 0)
                {
                    Plugin.Log?.LogWarning("[QCC12] 找不到第三列地格");
                    return;
                }
                var waterGrid = level.GetGrid(LOAN_SIGN_COLUMN, waterLane);
                PlaceBlueprint(level, waterGrid, VanillaContraptionID.lilyPad, "睡莲");
                PlaceBlueprint(level, waterGrid, loanID, "借贷告示牌");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[QCC12] spawn failed: " + e);
            }
        }

        /// <summary>该地格能否直接放下这个实体（与游戏自身的放置校验一致）。</summary>
        private static bool CanPlace(LawnGrid grid, EntityDefinition definition)
        {
            try
            {
                return grid.GetEntitySpawnStatus(definition) == null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void PlaceBlueprint(LevelEngine level, LawnGrid grid, NamespaceID entityID, string label)
        {
            try
            {
                var blueprintID = LogicBlueprintID.FromEntity(entityID);
                var seedDef = level.Content.GetSeedDefinition(blueprintID);
                if (seedDef == null)
                {
                    Plugin.Log?.LogWarning("[QCC12] 找不到蓝图 " + blueprintID);
                    return;
                }
                // 只有诅咒自己生成的这一块告示牌产出 0 星碎。
                CurseState.SuppressLoanSignLend = true;
                try
                {
                    grid.UseEntityBlueprintDefinition(seedDef, null);
                }
                finally
                {
                    CurseState.SuppressLoanSignLend = false;
                }
                Plugin.Log?.LogInfo("[QCC12] 已生成 " + label + "（列 " + grid.Column + " 行 " + grid.Lane + "）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[QCC12] 生成 " + label + " 失败: " + e.GetType().Name);
            }
            finally
            {
                CurseState.SuppressLoanSignLend = false;
            }
        }

        /// <summary>诅咒自己生成的那一块告示牌把借贷次数清为 0。</summary>
        public static void OnLoanSignInit(Entity entity)
        {
            if (entity == null || !CurseState.SuppressLoanSignLend)
                return;
            if (!On(entity.Level, IdQcC12))
                return;
            LoanSign.SetLendCount(entity, 0);
        }
        #endregion

        #region SC-C2 器械叛变
        /// <summary>
        /// SC-C2：器械放下后叛变 2s。
        ///
        /// 免疫条件（满足任意一条即不叛变）：
        ///   1. 带「可激活」标签（<c>triggerActive</c>，即能被点击激活）；
        ///   2. 带「忠诚」标签（<c>loyal</c>）；
        ///   3. 特殊豁免名单：御柱 / 自动兽夹 / 冲击活塞 / 闪电之珠。
        /// </summary>
        private static void TryCharm(Entity contraption)
        {
            if (!contraption.ExistsAndAlive())
                return;
            if (!CanDefect(contraption))
                return;
            try
            {
                contraption.CharmPermanent(contraption.Level.GetRightFaction(), null);
                CurseState.Charmed[contraption.Pointer] = SC_C2_CHARM_TICKS;
                Plugin.Log?.LogInfo("[SCC2] 器械叛变 2s: " + contraption.GetDefinitionID());
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SCC2] charm failed: " + e.GetType().Name);
            }
        }

        /// <summary>SC-C2：该器械是否会被本诅咒策反。</summary>
        private static bool CanDefect(Entity contraption)
        {
            try
            {
                if (contraption.IsTriggerActive())
                    return false;
                if (contraption.IsLoyal())
                    return false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SCC2] 读取标签失败: " + e.GetType().Name);
                return false;
            }
            return !IsCharmExempt(contraption);
        }

        /// <summary>
        /// 特殊豁免名单（用户指定：仅剩御柱 / 冲击活塞）。
        /// 自动兽夹、闪电之珠从名单里移除 —— 它们本来就靠「可激活」标签免疫，
        /// 但不再特别豁免（标签失效时就应该会叛变）。
        /// </summary>
        private static NamespaceID[] charmExemptions;
        private static NamespaceID[] CharmExemptions
        {
            get
            {
                var list = charmExemptions;
                if (list == null)
                {
                    list = new NamespaceID[]
                    {
                        VanillaContraptionID.onbashira,
                        VanillaContraptionID.punchton,
                    };
                    charmExemptions = list;
                }
                return list;
            }
        }

        private static bool IsCharmExempt(Entity contraption)
        {
            var id = contraption.GetDefinitionID();
            if (id == null)
                return false;
            var list = CharmExemptions;
            for (var i = 0; i < list.Length; i++)
            {
                if (id.SpaceName == list[i].SpaceName && id.Path == list[i].Path)
                    return true;
            }
            return false;
        }
        #endregion
        #region SC-C6 碎片污染
        /// <summary>
        /// 判定本次星之碎片是否无效。计数按"使用尝试"累加，
        /// 无效位置为 4、7、10…（即 n &gt; 1 且 n % 3 == 1）。
        /// </summary>
        public static bool IsStarshardUseVoid(LevelEngine level)
        {
            if (!On(level, IdScC6))
                return false;
            CurseState.StarshardUses++;
            var index = CurseState.StarshardUses;
            return index > 1 && index % 3 == 1;
        }
        #endregion

        #region SC-C9 状态抗性入口（见 TickStatusAccum）
        #endregion

        #region 受伤结算
        /// <summary>
        /// 受伤前判定。结算顺序（按需求固定）：
        ///   1. <b>SC-C13</b> 不屈无敌期间：直接免疫（不进后面的计算）。
        ///   2. <b>SC-C16</b> 固定减伤 5（本体与防具都减）—— "先减少伤害，然后再计算别的减伤"；
        ///      这一条还负责「最终结算至少保留 1 点伤害」。
        ///   3. <b>SC-C11</b> 后排 ×40%。
        ///   4. <b>SC-C15</b> 每秒受伤上限（本体 / 防具分别计算），同时把「防具吃不下的溢出伤害」
        ///      转给本体。
        ///   5. <b>SC-C13</b> 阈值判定 —— 需求：与 C15 同时开启时只计算最终受伤结果，
        ///      因此它排在 C15 之后。
        /// </summary>
        public static void PreDamage(DamageInput input)
        {
            if (input == null)
                return;
            var entity = input.Entity;
            if (entity == null)
                return;
            if (!CurseBuffBase.IsEnemyType(entity.Type))
                return;
            var level = entity.Level;
            try
            {
                // C13：已经进入不屈无敌 —— 完全免疫伤害（但不免疫状态效果）。
                if (On(level, IdScC13) && CurseState.Survivors.ContainsKey(entity.Pointer))
                {
                    input.SetAmount(0f);
                    return;
                }

                // 本次伤害是否打在防具上（C15 据此分开计数）。
                var hitsArmor = WillHitArmor(entity, input);
                var raw = input.Amount;

                // C16：固定减伤（本体与防具都减），最先结算。
                if (On(level, IdScC16))
                {
                    input.Add(-SC_C16_FLAT_REDUCTION);
                    if (input.Amount < 0f)
                        input.SetAmount(0f);
                    // 「最终结算时至少保留 1 点伤害」：原本能造成伤害的，不能被减到 0
                    // （例如僵尸互啃）。已经为 0 的（免疫/完全减伤）不在此列。
                    if (raw > 0f && input.Amount < 1f)
                        input.SetAmount(1f);
                }

                // C11：不在本行最前格的敌人受伤 ×40%。
                if (On(level, IdScC11) && !IsAtLaneFront(entity))
                    input.Multiply(SC_C11_DAMAGE_MULTIPLIER);

                // C15：每秒受伤上限（本体 / 防具分别计算）+ 防具溢出伤害结算到本体。
                if (On(level, IdScC15))
                {
                    LimitDamagePerSecond(entity, input, hitsArmor);
                    if (hitsArmor)
                        AllowArmorOverflowToBody(input);
                }

                // C13：阈值判定（此时已经是最终受伤结果）。
                if (On(level, IdScC13) && ShouldTriggerSurvivor(entity, input.Amount))
                {
                    ActivateSurvivor(entity);
                    input.SetAmount(0f);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CurseRuntime.PreDamage] " + e);
            }
        }

        /// <summary>
        /// 这次伤害是否会打在防具上（与 VanillaEntityExt.TakeDamage 的判定保持一致）。
        /// 命中防具时输入量会被直接当作防具伤害的基础值，因此在这里乘系数就等于防具免伤。
        /// </summary>
        private static bool WillHitArmor(Entity entity, DamageInput input)
        {
            if (NamespaceID.IsValid(input.ShieldTarget))
                return false;
            if (input.HasEffect(VanillaDamageEffects.IGNORE_ARMOR))
                return false;
            var armor = entity.GetMainArmor();
            if (!Armor.Exists(armor))
                return false;
            return !armor.IsIgnored();
        }

        /// <summary>
        /// SC-C13（永恒之心）：只有当这次伤害**本身超出（本体血量上限 + 当前防具耐久）**时才触发。
        ///
        /// 需求补充：
        ///   · 只在第 10 波之后、且只对「非无来源」（自然出场）的敌人生效；
        ///   · 不能触发两次（同一只敌怪只给一次不屈）；
        ///   · 与 C15 同时开启时只计算最终受伤结果 —— 因此调用点排在 C15 之后。
        ///
        /// 刻意**只看上限，不看当前血量**：否则残血敌人被一点小伤害打死也会触发
        /// 不屈（例如 200 血上限的敌人剩 15 血、挨 20 点伤害就无敌 3s，这显然不合理）。
        /// </summary>
        private static bool ShouldTriggerSurvivor(Entity entity, float amount)
        {
            if (entity.IsDead)
                return false;
            if (!(amount > 0f))
                return false;
            if (!IsNaturallySpawned(entity))
                return false;
            var level = entity.Level;
            if (level == null || level.CurrentWave <= SC_C13_MIN_WAVE)
                return false;
            if (CurseState.ScSurvivorTriggered.Contains(entity.Pointer))
                return false;
            var threshold = entity.GetMaxHealth() + GetMainArmorHealth(entity);
            if (!(threshold > 0f))
                return false;
            return amount >= threshold;
        }

        /// <summary>
        /// SC-C15（强固补丁）：把「防具吃不完的溢出伤害」结算到本体。
        ///
        /// 原版 <c>ArmoredTakeDamage</c> 只在伤害带 <c>DAMAGE_BODY_AFTER_ARMOR_BROKEN</c>
        /// 时才把溢出转给本体，而 <c>DamageInput.Effects</c> 是只读自动属性 ——
        /// 只能用反射换掉整个 DamageEffectList（新列表 = 原效果 + 该标记）。
        /// 反射拿不到时静默放弃（只影响这一个细节）。
        /// </summary>
        private static void AllowArmorOverflowToBody(DamageInput input)
        {
            try
            {
                if (input.HasEffect(VanillaDamageEffects.DAMAGE_BODY_AFTER_ARMOR_BROKEN))
                    return;
                if (damageInputEffectsSetter == null)
                    damageInputEffectsSetter = HarmonyLib.AccessTools.PropertySetter(typeof(DamageInput), "Effects");
                if (damageInputEffectsSetter == null)
                {
                    Plugin.Log?.LogWarning("[SCC15] 找不到 DamageInput.Effects 的 setter，溢出转移关闭");
                    return;
                }
                var old = input.Effects;
                var oldArray = old == null ? null : old.GetEffects();
                var count = oldArray == null ? 0 : oldArray.Length;
                var array = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<NamespaceID>(count + 1);
                for (var i = 0; i < count; i++)
                    array[i] = oldArray[i];
                array[count] = VanillaDamageEffects.DAMAGE_BODY_AFTER_ARMOR_BROKEN;
                damageInputEffectsSetter.Invoke(input, new object[] { new DamageEffectList(array) });
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SCC15] 溢出转移失败: " + e.GetType().Name);
            }
        }

        private static System.Reflection.MethodInfo damageInputEffectsSetter;

        /// <summary>主防具的当前耐久（SC-C15 用它算上限）；没有防具时返回 0。</summary>
        private static float GetMainArmorHealth(Entity entity)
        {
            try
            {
                var armor = entity.GetMainArmor();
                if (Armor.Exists(armor))
                    return armor.Health;
            }
            catch (Exception)
            {
            }
            return 0f;
        }

        /// <summary>
        /// SC-C15（强固补丁）：把本次伤害压到"本秒剩余额度"以内。
        /// 上限 = max(当前血量/耐久 × 40%, 600)；本体与防具各算各的。
        /// 因为是在游戏把伤害拆给防具/本体**之前**削减输入量，
        /// 被砍掉的部分不会自己溢到本体（溢出转移见 <see cref="AllowArmorOverflowToBody"/>）。
        /// </summary>
        private static void LimitDamagePerSecond(Entity entity, DamageInput input, bool hitsArmor)
        {
            var amount = input.Amount;
            if (!(amount > 0f))
                return;
            var current = hitsArmor ? GetMainArmorHealth(entity) : entity.Health;
            var limit = Mathf.Max(current * SC_C15_RATIO, SC_C15_MIN_LIMIT);
            var table = hitsArmor ? CurseState.ArmorDamagePerSecond : CurseState.BodyDamagePerSecond;
            float dealt;
            table.TryGetValue(entity.Pointer, out dealt);
            var remain = limit - dealt;
            if (remain <= 0f)
            {
                input.SetAmount(0f);
                return;
            }
            if (amount > remain)
            {
                amount = remain;
                input.SetAmount(amount);
            }
            table[entity.Pointer] = dealt + amount;
        }

        /// <summary>
        /// SC-C13：触发不屈。用原版铁幕 buff —— 它同时提供"完全免疫伤害"与
        /// "黑↔红呼吸闪烁"的提示动画；但它是 buff，因此不免疫状态效果。
        /// 计时用的是本 mod 自己的 tick 计数（与减速/冰冻等时间缩放无关），
        /// 因此无敌时长总是 3s，不会被状态延长或缩短，也不受小推车影响（见
        /// <see cref="AllowInstaKillThroughCurseInvincibility"/>）。
        /// </summary>
        private static void ActivateSurvivor(Entity entity)
        {
            CurseState.Survivors[entity.Pointer] = SC_C13_SURVIVOR_TICKS;
            // 同一条敌怪只给一次不屈。
            CurseState.ScSurvivorTriggered.Add(entity.Pointer);
            try
            {
                var definition = entity.Level.Content.GetBuffDefinition(VanillaBuffID.Contraption.ironCurtain);
                if (definition != null)
                {
                    var buff = CurseBuffBase.AddBuff(entity, definition);
                    if (buff != null)
                    {
                        // 给一点余量，保证结束时由我们统一摘掉它再执行死亡。
                        buff.SetProperty(IronCurtainBuff.PROP_TIMEOUT, SC_C13_SURVIVOR_TICKS + 30);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SCC13] iron curtain failed: " + e.GetType().Name);
            }
            try
            {
                entity.SpawnWithParams(VanillaEffectID.petrifiedShards, entity.GetCenter());
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SCC13] effect failed: " + e.GetType().Name);
            }
            Plugin.Log?.LogInfo("[SCC13] 敌怪不屈：无敌 3s 后死亡");
        }

        /// <summary>移除不屈期间挂上的铁幕 buff。</summary>
        private static void ClearSurvivorBuff(Entity entity)
        {
            try
            {
                CurseBuffBase.RemoveBuffsOf(entity, VanillaBuffID.Contraption.ironCurtain);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SCC13] clear buff failed: " + e.GetType().Name);
            }
        }

        /// <summary>SC-C11：是否位于本行最靠前的那一格。</summary>
        private static bool IsAtLaneFront(Entity entity)
        {
            try
            {
                var lane = entity.GetLane();
                if (!CurseState.LaneFrontColumn.TryGetValue(lane, out var front))
                    return true;
                var column = entity.Level.GetColumn(entity.GetBounds().min.x);
                return column <= front;
            }
            catch (Exception)
            {
                return true;
            }
        }
        #endregion

        #region 铁幕无敌（SC-C3 / SC-C10 / SC-C13 共用）
        /// <summary>
        /// 给实体挂一份铁幕无敌（持续 <paramref name="ticks"/> 帧）。
        ///
        /// 原版 <c>IronCurtainBuff.PostAdd</c> 每次都会把 Timeout 写成 MAX_TIMEOUT(150)，
        /// 所以时长必须我们自己覆盖；而 <c>AddBuff</c> 每调一次就新建一份 buff
        /// （各自计时），正好实现 SC-C3 要求的「可叠加」。
        /// </summary>
        public static void AddInvincibility(Entity entity, int ticks)
        {
            if (entity == null || entity.IsDead)
                return;
            try
            {
                var definition = entity.Level.Content.GetBuffDefinition(VanillaBuffID.Contraption.ironCurtain);
                if (definition == null)
                    return;
                var buff = CurseBuffBase.AddBuff(entity, definition);
                if (buff != null)
                    buff.SetProperty(IronCurtainBuff.PROP_TIMEOUT, ticks);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[IronCurtain] 挂无敌失败: " + e.GetType().Name);
            }
        }
        #endregion

        #region SC-C3 恶咒之星（敌人死亡 → 眩晕所有非免控器械 0.1s）
        /// <summary>
        /// SC-C3：第 10 波后，每个敌人死亡时眩晕**所有非免控器械** 0.1s（可叠加）。
        /// 「视为亡语」—— 由调用方传入是否触发亡语，不触发亡语的死因不会引发它。
        /// 免控判定与原版荧石大招一致：<c>entity.CanDeactive()</c>。
        /// </summary>
        public static void OnEnemyDied(Entity enemy, bool triggersDeathEffects)
        {
            if (enemy == null || !triggersDeathEffects)
                return;
            try
            {
                var level = enemy.Level;
                if (level == null || !On(level, IdScC3))
                    return;
                if (level.CurrentWave <= SC_C3_MIN_WAVE)
                    return;
                var all = CurseBuffBase.FindAll(level);
                if (all == null)
                    return;
                var stunned = 0;
                for (var i = 0; i < all.Length; i++)
                {
                    var contraption = all[i];
                    if (contraption == null || contraption.IsDead)
                        continue;
                    if (contraption.Type != EntityTypes.PLANT)
                        continue;
                    if (!contraption.IsFriendlyEntity() || !contraption.CanDeactive())
                        continue;
                    StunStackable(contraption, SC_C3_STUN_TICKS);
                    stunned++;
                }
                if (stunned > 0)
                    Plugin.Log?.LogInfo("[SCC3] 敌人死亡 → 眩晕 " + stunned + " 个器械 0.1s（可叠加）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SCC3] 眩晕失败: " + e.GetType().Name);
            }
        }

        /// <summary>
        /// 可叠加的眩晕：原版 <c>StunBuff.MaxStunTime</c> 只在"新的比旧的长"时才写时间
        /// （早期内测版的 interop 里连这个方法都找不到），所以改为自己记账：
        /// 每个器械维护一个「剩余眩晕 tick」，每次叠加就 <c>+0.1s</c> 后把**累计值**交给
        /// <c>entity.Stun</c>（它只会把时长换成更大的那个 = 我们的累计值），
        /// 自己再每帧递减同一份计数，两边同步。
        /// </summary>
        private static void StunStackable(Entity contraption, int ticks)
        {
            int remaining;
            CurseState.ScStun.TryGetValue(contraption.Pointer, out remaining);
            if (remaining < 0)
                remaining = 0;
            remaining += ticks;
            CurseState.ScStun[contraption.Pointer] = remaining;
            contraption.Stun(remaining);
        }

        /// <summary>SC-C3：每帧递减自己记的眩晕剩余时长。</summary>
        private static void TickStun()
        {
            if (CurseState.ScStun.Count == 0)
                return;
            List<IntPtr> expired = null;
            foreach (var pair in CurseState.ScStun)
            {
                var remaining = pair.Value - 1;
                CurseState.ScStun[pair.Key] = remaining;
                if (remaining <= 0)
                    (expired ??= new List<IntPtr>()).Add(pair.Key);
            }
            if (expired == null)
                return;
            foreach (var ptr in expired)
                CurseState.ScStun.Remove(ptr);
        }
        #endregion

        #region SC-C7 钢铁吸盘（击退量减半 + 落地敌人至少重质量）
        /// <summary>
        /// SC-C7：敌人受到的击退量减半；接触地面的敌人重量至少为 HEAVY。
        ///
        /// 实现位置是原版的击退系数函数
        /// （<c>VanillaEntityProps.GetKnockbackMultiplier(entity, massMultiplier)</c>）：
        /// 以 <c>mass = max(GetMass(), HEAVY)</c> 重算原公式（地面敌人）后再乘 0.5。
        ///
        /// 为什么不沿用旧版的「把木风扇的质量判定顶到 999」（也不再给敌人
        /// <c>MASS +1</c> 的修饰符）：需求明确「不再禁止木风扇的吹飞效果与击退，
        /// 也不是简单的加重量」。木风扇的吹飞/击退走的是
        /// <c>BlownByWoodenFanBuff.GetBlowMass</c>（与击退系数无关），
        /// 因此我们只改撃退系数，风扇的吹飞与击退完全保留。
        /// </summary>
        public static float ModifyKnockback(Entity entity, float massMultiplier, float value)
        {
            if (entity == null)
                return value;
            try
            {
                if (!CurseBuffBase.IsEnemyType(entity.Type))
                    return value;
                if (!On(entity.Level, IdScC7))
                    return value;
                var mass = entity.GetMass();
                if (IsOnGround(entity) && mass < VanillaMass.HEAVY)
                    mass = VanillaMass.HEAVY;
                var recompiled = Mathf.Max(0f, 2f - Mathf.Pow(2f, mass * massMultiplier));
                return recompiled * SC_C7_KNOCKBACK_MULTIPLIER;
            }
            catch (Exception)
            {
                return value;
            }
        }

        /// <summary>敌人是否接触地面（与木风扇的吹飞判定用同一套条件）。</summary>
        private static bool IsOnGround(Entity entity)
        {
            try
            {
                if (entity.IsOnGround)
                    return true;
                if (entity.IsInCloud())
                    return false;
                return entity.GetGravity() >= 0.001f && entity.GetRelativeY() <= 3f;
            }
            catch (Exception)
            {
                return false;
            }
        }
        #endregion

        #region SC-C8 寄生藤蔓（首次死亡 → 复活至 20%）
        /// <summary>
        /// SC-C8：第 10 波后，**自然出场**的敌怪首次死亡时改为不死，
        /// 本体血回到上限的 20%（不恢复防具、不触发亡语）。
        ///
        /// 做法是在 <c>Entity.Die(DeathInfo)</c> 前缀里取消这次死亡
        /// （走的正是游戏自己 <c>PRE_ENTITY_DEATH</c> 回调那条路），并把血量补到 20%。
        /// 因此「地雷 / 蜡烛 / 核弹 / 石砖加农炮」那种会销毁尸体的死因也拦得住
        /// —— 需求明确要求这些「去除亡语的行为」不影响本次复活；
        /// 但 <c>NO_REVIVAL</c>（溺水、坠楼这类"不可复活"）仍然会阻止它。
        /// </summary>
        public static bool TrySurviveDeath(Entity enemy, DeathInfo info)
        {
            if (enemy == null || !CurseBuffBase.IsEnemyType(enemy.Type))
                return false;
            try
            {
                var level = enemy.Level;
                if (level == null || !On(level, IdScC8))
                    return false;
                if (level.CurrentWave <= SC_C8_MIN_WAVE)
                    return false;
                if (!IsNaturallySpawned(enemy))
                    return false;
                if (info != null && info.HasEffect(VanillaDamageEffects.NO_REVIVAL))
                    return false;
                // 同一只敌怪只复活一次。
                if (!CurseState.ScRevived.Add(enemy.Pointer))
                    return false;
                var max = enemy.GetMaxHealth();
                if (!(max > 0f))
                    return false;
                enemy.Health = max * SC_C8_REVIVE_PERCENT;
                enemy.PlaySound(VanillaSoundID.revived);
                Plugin.Log?.LogInfo("[SCC8] 寄生藤蔓：首次死亡改为复活至本体 20%");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SCC8] 判定失败: " + e.GetType().Name);
                return false;
            }
        }
        #endregion

        #region SC-C14 命运之瓶（每 10 波封禁 20s）
        /// <summary>SC-C14：每 10 波开始时封禁 器械放置 / 镐子 / 星之碎片 20s（驱动器例外）。</summary>
        private static void TickWaveGuard(LevelEngine level)
        {
            if (CurseState.WaveGuardTicks > 0)
                CurseState.WaveGuardTicks--;
            if (!On(level, IdScC14))
                return;
            var wave = level.CurrentWave;
            if (wave <= 0 || wave % SC_C14_WAVE_INTERVAL != 0)
                return;
            if (CurseState.WaveGuardWave == wave)
                return;
            CurseState.WaveGuardWave = wave;
            CurseState.WaveGuardTicks = SC_C14_BAN_TICKS;
            Plugin.Log?.LogInfo("[SCC14] 第 " + wave + " 波：禁用放置 / 镐子 / 星之碎片 "
                + (SC_C14_BAN_TICKS / TICKS_PER_SECOND) + "s");
        }

        /// <summary>SC-C14：封禁期是否正在生效（镐子 / 星之碎片 / 器械放置共用）。</summary>
        public static bool IsWaveGuardActive(LevelEngine level)
        {
            return level != null && CurseState.WaveGuardTicks > 0;
        }

        /// <summary>SC-C14：这个器械是否禁止放置（驱动器除外 —— 需求：「不影响驱动」）。</summary>
        public static bool IsPlacementBanned(NamespaceID entityID)
        {
            if (entityID == null)
                return false;
            var driver = VanillaContraptionID.drivenser;
            return !(entityID.SpaceName == driver.SpaceName && entityID.Path == driver.Path);
        }
        #endregion

        #region SC-C15 强固补丁（每秒受伤上限）
        /// <summary>SC-C15：每秒清一次「本秒已受伤量」计数（本体与防具各一份）。</summary>
        private static void TickDamageWindow(LevelEngine level)
        {
            if (--CurseState.DamageWindowTimer > 0)
                return;
            CurseState.DamageWindowTimer = TICKS_PER_SECOND;
            if (!On(level, IdScC15))
                return;
            CurseState.BodyDamagePerSecond.Clear();
            CurseState.ArmorDamagePerSecond.Clear();
        }
        #endregion
    }
}
