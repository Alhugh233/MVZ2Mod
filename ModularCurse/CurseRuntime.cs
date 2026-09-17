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
        // SC-C1
        public const int SC_C1_FREE_COUNT = 10;
        public const float SC_C1_COST_PER_EXTRA = 0.05f;
        // SC-C2
        public const int SC_C2_CHARM_TICKS = 60;
        // SC-C3（BOSS 不生效）
        public const float SC_C3_HEAL_PERCENT = 0.01f;
        public const float SC_C3_HEAL_CAP = 20f;
        // SC-C4
        public const int SC_C4_MIN_WAVE = 10;
        public const int SC_C4_CHANCE = 20;
        // SC-C5
        public const float SC_C5_DAMAGE_MULTIPLIER = 0.75f;
        // SC-C8
        public const float SC_C8_FLAT_REDUCTION = 5f;
        // SC-C9
        public const float SC_C9_TIME_MULTIPLIER = 1f / 3f;
        // SC-C10（出场无敌 8s）
        public const int SC_C10_INVINCIBLE_TICKS = 8 * TICKS_PER_SECOND;
        // SC-C11
        public const float SC_C11_DAMAGE_MULTIPLIER = 0.33f;
        // SC-C12
        public const float SC_C12_INTERVAL_MULTIPLIER = 0.93f;
        // SC-C13（2s = 60 tick，计数与时间缩放无关）
        public const int SC_C13_SURVIVOR_TICKS = 60;

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

                var needList = On(level, IdScC1) || On(level, IdScC2) || On(level, IdScC3)
                    || On(level, IdScC7) || On(level, IdScC10) || On(level, IdScC11) || On(level, IdQcC6)
                    || CurseState.PendingWither.Count > 0;
                var entities = needList ? CurseBuffBase.FindAll(level) : null;

                if (On(level, IdScC1))
                    RefreshContraptionCounts(entities);
                if (On(level, IdScC11))
                    RefreshLaneFront(level, entities);

                TickToolCooldown(level);
                TickPendingWither(level, entities);
                TickHeal(level, entities);
                TickSurvivors(level);
                TickCharms(level);
                TickSpawnRolls(level, entities);
                TickLoanSign(level);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CurseRuntime.Tick] " + e);
            }
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

        /// <summary>QC-C2：工具冷却倒计时 + 禁用状态校准。</summary>
        private static void TickToolCooldown(LevelEngine level)
        {
            if (!On(level, IdQcC2))
                return;
            if (CurseState.PickaxeCooldown > 0)
                CurseState.PickaxeCooldown--;
            if (CurseState.StarshardCooldown > 0)
                CurseState.StarshardCooldown--;
            EnsureToolDisableState(level);
        }

        /// <summary>SC-C3：敌怪本体每秒回复 1% 生命（每秒最多 20 点，BOSS 不生效）。</summary>
        private static void TickHeal(LevelEngine level,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity> entities)
        {
            if (!On(level, IdScC3))
                return;
            if (--CurseState.HealTimer > 0)
                return;
            CurseState.HealTimer = TICKS_PER_SECOND;
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
                var amount = Mathf.Min(max * SC_C3_HEAL_PERCENT, SC_C3_HEAL_CAP);
                if (amount <= 0)
                    continue;
                // 只回本体，不动防具。
                VanillaEntityExt.Heal(enemy, amount, null);
            }
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

        /// <summary>QC-C6 隐形 / SC-C2 叛变 / SC-C7 重量 / SC-C10 出场无敌：首次见到实体时处理一次。</summary>
        private static void TickSpawnRolls(LevelEngine level,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity> entities)
        {
            var needInvisible = On(level, IdQcC6);
            var needCharm = On(level, IdScC2);
            var needMass = On(level, IdScC7);
            var needInvincible = On(level, IdScC10);
            if (!needInvisible && !needCharm && !needMass && !needInvincible)
                return;
            if (entities == null)
                return;
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (entity == null)
                    continue;

                if (needMass && CurseBuffBase.IsEnemyType(entity.Type) && CurseState.MassApplied.Add(entity.Pointer))
                {
                    var def = CurseBuffs.Get("sc_c7_for_enemy");
                    if (def != null)
                        CurseBuffBase.AddBuff(entity, def);
                }
                if (needInvincible && CurseBuffBase.IsEnemyType(entity.Type)
                    && CurseState.SpawnInvincibleApplied.Add(entity.Pointer))
                {
                    // SC-C10 只给「自然生成」的敌怪：衍生物（泡泡枪的泡泡、炮筒的炮弹僵尸、
                    // 死灵法师的骨墙、恐怖之母的寄生虫……）全部是被别的实体 Spawn 出来的，不算。
                    // 注意：无论是否生效都要记进 SpawnInvincibleApplied，避免每帧重复判断。
                    if (IsNaturallySpawned(entity))
                    {
                        ApplySpawnInvincibility(entity);
                    }
                    else if (spawnInvincibleSkipLogged < SPAWN_INVINCIBLE_SKIP_LOG_LIMIT)
                    {
                        spawnInvincibleSkipLogged++;
                        Plugin.Log?.LogInfo("[SCC10] 衍生物不享受出场无敌：" + entity.GetDefinitionID()
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
        /// SC-C10：只有「自然生成」的敌怪才有出场无敌，衍生物一律豁免。
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
                // 读不出来时按「衍生物」处理：宁可少给无敌，也不能让衍生物白拿无敌
                Plugin.Log?.LogWarning("[SCC10] 读取生成来源失败，按衍生物处理: " + e.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// SC-C10：敌怪出场时无敌 8s。
        /// 用原版铁幕 buff（石护罩那个）—— 它同时提供"完全免疫伤害"
        /// 与"黑↔红呼吸闪烁"的提示动画；但它是 buff，因此不免疫状态效果。
        /// 超时由 buff 自己处理（到点自动移除），不需要我们额外计时。
        /// </summary>
        private static void ApplySpawnInvincibility(Entity enemy)
        {
            if (enemy == null || enemy.IsDead)
                return;
            try
            {
                var definition = enemy.Level.Content.GetBuffDefinition(VanillaBuffID.Contraption.ironCurtain);
                if (definition == null)
                    return;
                var buff = CurseBuffBase.AddBuff(enemy, definition);
                if (buff != null)
                    buff.SetProperty(IronCurtainBuff.PROP_TIMEOUT, SC_C10_INVINCIBLE_TICKS);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SCC10] iron curtain failed: " + e.GetType().Name);
            }
        }

        /// <summary>QC-C12：关卡开始后延迟几帧生成借贷告示牌。</summary>
        private static void TickLoanSign(LevelEngine level)
        {
            if (!On(level, IdQcC12))
                return;
            if (CurseState.LoanSignPlaced)
                return;
            if (CurseState.LoanSignDelay < LOAN_SIGN_DELAY)
            {
                CurseState.LoanSignDelay++;
                return;
            }
            CurseState.LoanSignPlaced = true;
            PlaceLoanSign(level);
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
            EnsureToolDisableState(level);
        }

        /// <summary>
        /// 把两件工具的禁用状态校准到当前计时状态（幂等）。
        /// 直接写关卡禁用属性可以复用原版"衰老"的禁用理由与图标，
        /// 且不影响由 buff modifier 提供的禁用。
        /// </summary>
        public static void EnsureToolDisableState(LevelEngine level)
        {
            if (level == null)
                return;
            SetToolDisabled(level, true, CurseState.PickaxeCooldown > 0);
            SetToolDisabled(level, false, CurseState.StarshardCooldown > 0);
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

        #region QC-C4 复活 / SC-C4 尸体复活
        /// <summary>
        /// 尸体即将消失（<c>VanillaEnemyExt.FaintRemove</c>）时调用。
        /// 返回 true = 取消消失，让尸体原地复活。
        ///
        /// 为什么两条诅咒都挂在这里，而不是死亡瞬间（<c>Entity.Die</c>）：
        ///   与「控制灵符」「尸解仙竹杖」一致 —— 只有"死亡并倒下"（留有尸体）
        ///   的敌人能走到 FaintRemove（无尸体的敌人在 EnemyCommonBehaviour.PostDeath
        ///   里就已 Remove）。所以"从尸体里爬起来"这件事在物理上只能发生在这一刻。
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

                if (!On(level, IdScC4))
                    return false;
                // 同一只敌人只判定一次（第一次死亡；复活后不再判定）。
                if (!CurseState.ScRevived.Add(enemy.Pointer))
                    return false;
                if (level.CurrentWave <= SC_C4_MIN_WAVE)
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
                    // 可以被「取消亡语」的器械跳过本次判定（与 QC-C4 同一套判据）。
                    if (!enemy.ShouldTriggerDeathEffects(lethal))
                        return false;
                }
                if (!Roll(SC_C4_CHANCE))
                    return false;
                ReviveCorpse(enemy);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SCC4] FaintRemove 判定失败: " + e.GetType().Name);
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

        /// <summary>SC-C4：把尸体完全复活（满血 + 防具满耐久）。</summary>
        private static void ReviveCorpse(Entity enemy)
        {
            try
            {
                enemy.Revive();
                enemy.Health = enemy.GetMaxHealth();
                RestoreArmor(enemy);
                enemy.PlaySound(VanillaSoundID.revived);
                Plugin.Log?.LogInfo("[SCC4] 敌怪完全复活（尸体）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SCC4] revive failed: " + e.GetType().Name);
            }
        }

        /// <summary>把该实体身上所有防具的耐久恢复到满值。</summary>
        private static void RestoreArmor(Entity entity)
        {
            try
            {
                var slots = new Il2CppSystem.Collections.Generic.List<NamespaceID>();
                entity.GetActiveArmorSlots(slots);
                for (var i = 0; i < slots.Count; i++)
                {
                    var slot = slots[i];
                    if (slot == null)
                        continue;
                    var armor = entity.GetArmorAtSlot(slot);
                    if (!Armor.Exists(armor))
                        continue;
                    armor.Health = armor.GetMaxHealth();
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SCC4] 防具恢复失败: " + e.GetType().Name);
            }
        }
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
                        var extra = count - SC_C1_FREE_COUNT;
                        if (extra > 0)
                            value *= 1f + SC_C1_COST_PER_EXTRA * extra; // 加法叠加
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

        /// <summary>特殊豁免名单（用户指定：御柱 / 自动兽夹 / 冲击活塞 / 闪电之珠）。</summary>
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
                        VanillaContraptionID.autotrap,
                        VanillaContraptionID.punchton,
                        VanillaContraptionID.lightningOrb,
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

        #region SC-C9 debuff 时长
        public static int ScaleDebuffTime(Entity entity, int time)
        {
            if (entity == null || time <= 0)
                return time;
            if (!On(entity.Level, IdScC9))
                return time;
            return Mathf.Max(1, Mathf.RoundToInt(time * SC_C9_TIME_MULTIPLIER));
        }
        #endregion

        #region 受伤结算
        /// <summary>
        /// 受伤前判定（SC-C13 不屈 / SC-C8 固定减伤 / SC-C5 防具免伤 / SC-C11 后排减伤）。
        /// 顺序按需求：C5 在 C8 之后计算。
        /// </summary>
        public static void PreDamage(DamageInput input)
        {
            if (input == null)
                return;
            var entity = input.Entity;
            if (entity == null)
                return;
            var level = entity.Level;
            var enemy = CurseBuffBase.IsEnemyType(entity.Type);
            try
            {
                if (enemy && On(level, IdScC13))
                {
                    if (CurseState.Survivors.ContainsKey(entity.Pointer))
                    {
                        // 无敌期间完全免疫伤害（但不免疫状态效果）。
                        input.SetAmount(0f);
                        return;
                    }
                    if (ShouldTriggerSurvivor(entity, input.Amount))
                    {
                        ActivateSurvivor(entity);
                        input.SetAmount(0f);
                        return;
                    }
                }

                if (!enemy)
                    return;

                // 本次伤害是否打在防具上（C8 只看本体，C5 只看防具）。
                var hitsArmor = WillHitArmor(entity, input);

                // C8：先减固定值（只作用于**本体**伤害，防具伤害不减）。
                if (On(level, IdScC8) && !hitsArmor)
                {
                    input.Add(-SC_C8_FLAT_REDUCTION);
                    if (input.Amount < 0f)
                        input.SetAmount(0f);
                }

                // C5：再对**防具**乘免伤系数（在 C8 之后）。
                if (On(level, IdScC5) && hitsArmor)
                    input.Multiply(SC_C5_DAMAGE_MULTIPLIER);

                if (On(level, IdScC11) && !IsAtLaneFront(entity))
                    input.Multiply(SC_C11_DAMAGE_MULTIPLIER);
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
        /// SC-C13：只有当这次伤害**本身超出本体血量上限**时才触发。
        ///
        /// 刻意**只看血上限，不看当前血量**：否则残血敌人被一点小伤害打死也会触发
        /// 不屈（例如 200 血上限的敌人剩 15 血、挨 20 点伤害就无敌 2s，这显然不合理）。
        /// 也不算防具 —— 判据就是「这一击能不能从满血直接把本体打没」。
        /// </summary>
        private static bool ShouldTriggerSurvivor(Entity entity, float amount)
        {
            if (entity.IsDead)
                return false;
            if (!(amount > 0f))
                return false;
            var maxHealth = entity.GetMaxHealth();
            if (!(maxHealth > 0f))
                return false;
            return amount >= maxHealth;
        }

        /// <summary>
        /// SC-C13：触发不屈。用原版铁幕 buff —— 它同时提供"完全免疫伤害"与
        /// "黑↔红呼吸闪烁"的提示动画；但它是 buff，因此不免疫状态效果。
        /// 计时用的是本 mod 自己的 tick 计数（与减速/冰冻等时间缩放无关），
        /// 因此敌怪总是 2s 后死亡。
        /// </summary>
        private static void ActivateSurvivor(Entity entity)
        {
            CurseState.Survivors[entity.Pointer] = SC_C13_SURVIVOR_TICKS;
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
            Plugin.Log?.LogInfo("[SCC13] 敌怪不屈：无敌 2s 后死亡");
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
    }
}
