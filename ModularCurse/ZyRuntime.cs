using System;
using System.Collections.Generic;
using MVZ2.GameContent.Artifacts;
using MVZ2.GameContent.Buffs;
using MVZ2.GameContent.Buffs.Contraptions;
using MVZ2.GameContent.Contraptions;
using MVZ2.GameContent.Damages;
using MVZ2.GameContent.Effects;
using MVZ2.GameContent.Enemies;
using MVZ2.GameContent.Pickups;
using MVZ2.GameContent.Recharges;
using MVZ2.GameContent.Seeds;
using MVZ2.Level;
using MVZ2.Vanilla.Audios;
using MVZ2.Vanilla.Contraptions;
using MVZ2.Vanilla.Entities;
using MVZ2.Vanilla.Level;
using MVZ2.Vanilla.Pickups;
using MVZ2Logic.Blueprints;
using MVZ2Logic.Entities;
using MVZ2Logic.Level;
using MVZ2Logic.Spawns;
using PVZEngine;
using PVZEngine.Buffs;
using PVZEngine.Damages;
using PVZEngine.Definitions;
using PVZEngine.Entities;
using PVZEngine.Level;
using PVZEngine.Models;
using PVZEngine.SeedPacks;
using PVZEngine.Spawns;
using PVZEngine.Tools.Timing;
using UnityEngine;

namespace ModularCurse
{
    /// <summary>
    /// ZY 池（咒鵺兼程 / 3q4a 的诅咒）的运行期逻辑。
    ///
    /// 与 <see cref="CurseRuntime"/> 同一套分工：能用修饰符表达的写在 buff 里
    /// （ZY-C15 的光照 ×50%），其余需要回调/跨帧状态的都在这里，由
    /// <see cref="CurseHooks"/> 的 Harmony 补丁驱动。
    ///
    /// 每一条都以"关卡是否挂着对应诅咒"为唯一开关（<see cref="CurseBuffBase.Active"/>）。
    ///
    /// 编号 2026-09 重排过：zy_c1..zy_c16 的**语义**如下（名字颜色见 curses.xml）：
    ///   zy_c1  红 趋光性     zy_c2  红 夜长梦多      zy_c3  红 地狱残响     zy_c4  红 排异反应
    ///   zy_c5  绿 斋禁       zy_c6  绿 磨灭          zy_c7  绿 室上性心动过速 zy_c8  绿 我们不去幻想乡
    ///   zy_c9  蓝 它回来了   zy_c10 蓝 病毒式传播    zy_c11 蓝 衰变         zy_c12 蓝 久经沙场
    ///   zy_c13 白 纯粹       zy_c14 白 恶意          zy_c15 白 晚安         zy_c16 白 抗拒
    /// </summary>
    internal static class ZyRuntime
    {
        #region 常量
        // zy_c1 趋光性
        public const int ZY_C1_SLEEP_TICKS = 8 * CurseRuntime.TICKS_PER_SECOND;
        // zy_c2 夜长梦多
        public const float ZY_C2_PER_WAVE = 0.025f;
        public const float ZY_C2_CAP = 0.50f;
        // zy_c3 地狱残响（矿场的生产间隔 → 30s）
        public const int ZY_C3_PRODUCE_TICKS = 30 * CurseRuntime.TICKS_PER_SECOND;
        // zy_c4 排异反应
        public const int ZY_C4_BAN_TICKS = 12 * CurseRuntime.TICKS_PER_SECOND;
        // zy_c5 斋禁
        public const int ZY_C5_STARSHARD_LOSS = 1;
        // zy_c6 磨灭
        public const int ZY_C6_ERASE_COUNT = 1;
        // zy_c7 室上性心动过速
        public const float ZY_C7_MIN_WAVE_SECONDS = 16f;
        // zy_c8 我们不去幻想乡（+200% = ×3）
        public const float ZY_C8_MULTIPLIER = 3f;
        // zy_c10 病毒式传播
        public const int ZY_C10_COPIES = 1;
        // zy_c12 久经沙场（>80 且未死亡 → 之后单次 ≤20）
        public const float ZY_C12_THRESHOLD = 80f;
        public const float ZY_C12_LIMIT = 20f;
        // zy_c16 抗拒
        public const float ZY_C16_CLICK_DAMAGE = 10f;
        #endregion

        private static readonly NamespaceID IdZyC1 = CurseIds.Get("zy_c1");
        private static readonly NamespaceID IdZyC2 = CurseIds.Get("zy_c2");
        private static readonly NamespaceID IdZyC3 = CurseIds.Get("zy_c3");
        private static readonly NamespaceID IdZyC4 = CurseIds.Get("zy_c4");
        private static readonly NamespaceID IdZyC5 = CurseIds.Get("zy_c5");
        private static readonly NamespaceID IdZyC6 = CurseIds.Get("zy_c6");
        private static readonly NamespaceID IdZyC7 = CurseIds.Get("zy_c7");
        private static readonly NamespaceID IdZyC8 = CurseIds.Get("zy_c8");
        private static readonly NamespaceID IdZyC9 = CurseIds.Get("zy_c9");
        private static readonly NamespaceID IdZyC10 = CurseIds.Get("zy_c10");
        private static readonly NamespaceID IdZyC11 = CurseIds.Get("zy_c11");
        private static readonly NamespaceID IdZyC12 = CurseIds.Get("zy_c12");
        private static readonly NamespaceID IdZyC13 = CurseIds.Get("zy_c13");
        private static readonly NamespaceID IdZyC14 = CurseIds.Get("zy_c14");
        private static readonly NamespaceID IdZyC15 = CurseIds.Get("zy_c15");
        private static readonly NamespaceID IdZyC16 = CurseIds.Get("zy_c16");

        /// <summary>ZY-C13 允许携带的四个制品（需求指定的例外）。</summary>
        private static NamespaceID[] artifactExemptions;

        private static NamespaceID[] ArtifactExemptions
        {
            get
            {
                var list = artifactExemptions;
                if (list == null)
                {
                    list = new NamespaceID[]
                    {
                        VanillaArtifactID.ufoToy,        // 飞碟玩具
                        VanillaArtifactID.smartPhone,    // 智能手机
                        VanillaArtifactID.pagodaBranch,  // 槐树树枝
                        VanillaArtifactID.greedyVacuum,  // 贪婪吸尘器
                    };
                    artifactExemptions = list;
                }
                return list;
            }
        }

        private static bool On(LevelEngine level, NamespaceID id)
        {
            return CurseBuffBase.Active(level, id);
        }

        private static bool SameID(NamespaceID a, NamespaceID b)
        {
            return a != null && b != null && a.SpaceName == b.SpaceName && a.Path == b.Path;
        }

        /// <summary>第一大波是否已经开始（`wavesPerFlag` 的整数倍波即大波）。</summary>
        public static bool HasFirstBigWaveStarted(LevelEngine level)
        {
            try
            {
                var perFlag = level.GetWavesPerFlag();
                return perFlag > 0 && level.CurrentWave >= perFlag;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>当前是第几大波（1 起算）。</summary>
        private static int CurrentBigWaveIndex(LevelEngine level)
        {
            return level.CurrentFlag + 1;
        }

        /// <summary>
        /// 无尽模式下的"本轮第几大波"（1 起算）—— 每轮会重新从第 1 大波开始，
        /// 所以要用 `CurrentFlag % totalFlags`。
        /// </summary>
        private static int CurrentLocalBigWaveIndex(LevelEngine level)
        {
            var total = level.GetTotalFlags();
            if (total <= 0)
                return CurrentBigWaveIndex(level);
            if (!level.IsEndless())
                return CurrentBigWaveIndex(level);
            return (level.CurrentFlag % total) + 1;
        }

        /// <summary>是否已被光照到（自身是光源也算）。</summary>
        private static bool IsLit(Entity entity)
        {
            try
            {
                return entity.IsLightSource() || entity.IsIlluminated();
            }
            catch (Exception)
            {
                return true; // 读不出来时按"有光"处理，宁可少睡也不要无故静止
            }
        }

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

        #region 每帧
        /// <summary>由 <see cref="CurseRuntime.Tick"/> 调用（在它自己的每帧逻辑之后）。</summary>
        public static void Tick(LevelEngine level,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity> entities)
        {
            if (level == null)
                return;
            try
            {
                TickBigWave(level);
                TickSleep(level, entities);
                TickDimLights(level, entities);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ZyRuntime.Tick] " + e);
            }
        }

        /// <summary>关卡切换时的清空由 <see cref="CurseState.EnsureLevel"/> 统一负责。</summary>

        #region zy_c5 斋禁 / zy_c6 磨灭（大波开始）
        /// <summary>大波开始时触发一次（用 `CurrentFlag*1000+波数` 做去重标记）。</summary>
        private static void TickBigWave(LevelEngine level)
        {
            var perFlag = level.GetWavesPerFlag();
            var wave = level.CurrentWave;
            if (perFlag <= 0 || wave <= 0 || wave % perFlag != 0)
                return;
            var mark = (long)level.CurrentFlag * 1000L + wave;
            if (CurseState.ZyBigWaveMark == mark)
                return;
            CurseState.ZyBigWaveMark = mark;

            if (On(level, IdZyC5))
                OnBigWaveZhairn(level);
            if (On(level, IdZyC6))
                OnBigWaveErase(level);
        }

        /// <summary>zy_c5 斋禁：每大波开始时失去所有机械能，并失去 1 个星之碎片。</summary>
        private static void OnBigWaveZhairn(LevelEngine level)
        {
            try
            {
                level.SetEnergy(0);
                var lostShard = 0;
                if (level.GetStarshardCount() >= ZY_C5_STARSHARD_LOSS)
                {
                    level.AddStarshardCount(-ZY_C5_STARSHARD_LOSS);
                    lostShard = ZY_C5_STARSHARD_LOSS;
                }
                // 「壶抽不到牌」的那个音效（fatigue）。
                level.PlaySound(VanillaSoundID.fatigue);
                Plugin.Log?.LogInfo("[ZYC5] 第 " + level.CurrentWave + " 波：失去所有机械能，失去 "
                    + lostShard + " 个星之碎片");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ZYC5] 失败: " + e.GetType().Name);
            }
        }

        /// <summary>zy_c6 磨灭：每大波开始时从右往左永久禁用 1 张器械蓝图。</summary>
        private static void OnBigWaveErase(LevelEngine level)
        {
            try
            {
                var total = level.GetSeedSlotCount();
                if (total <= 0)
                    return;
                var count = ZY_C6_ERASE_COUNT;
                var reason = VanillaBlueprintErrors.decrepify;
                var erased = 0;
                for (var i = total - 1; i >= 0 && count > 0; i--)
                {
                    var pack = level.GetSeedPackAt(i);
                    if (pack == null || pack.IsDisabled())
                        continue;
                    pack.SetProperty(EngineSeedProps.DISABLE_ID, reason);
                    count--;
                    erased++;
                }
                Plugin.Log?.LogInfo("[ZYC6] 第 " + level.CurrentWave + " 波：从右往左禁用 " + erased + " 个蓝图");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ZYC6] 失败: " + e.GetType().Name);
            }
        }
        #endregion

        #region zy_c1 趋光性
        /// <summary>
        /// zy_c1：排除日光影响后，非夜行性器械在光照范围外累计 8s 后睡着。
        /// 器械自身的光照也算；光照恢复则立刻醒来。
        /// 「无法被美梦丝入眠的器械不受影响」—— 用原版 <see cref="DreamSilk.CanSleep"/> 判定。
        ///
        /// 「睡着」用的就是原版睡眠 buff（<see cref="NocturnalBuff"/>，带 Zzz 提示、
        /// AI_FROZEN + 不爆炸）。只摘我们自己挂上去的那些，绝不碰原版的昼夜睡眠。
        /// </summary>
        private static void TickSleep(LevelEngine level,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity> entities)
        {
            if (!On(level, IdZyC1))
            {
                WakeAll(level);
                return;
            }
            if (entities == null)
                return;
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (entity == null || entity.IsDead)
                    continue;
                if (entity.Type != EntityTypes.PLANT)
                    continue;
                if (!entity.IsFriendlyEntity())
                    continue;
                // 夜行性器械本来就会因原版规则睡觉，不归这条诅咒管。
                if (entity.IsNocturnal())
                    continue;

                if (IsLit(entity))
                {
                    CurseState.ZySleepTicks.Remove(entity.Pointer);
                    Wake(entity);
                    continue;
                }
                if (CurseState.ZySleeping.Contains(entity.Pointer))
                    continue;
                // 美梦丝也叫不醒/弄不睡的器械，本诅咒同样无效。
                if (!DreamSilk.CanSleep(entity))
                {
                    CurseState.ZySleepTicks.Remove(entity.Pointer);
                    continue;
                }

                int ticks;
                if (!CurseState.ZySleepTicks.TryGetValue(entity.Pointer, out ticks))
                    ticks = 0;
                ticks++;
                if (ticks >= ZY_C1_SLEEP_TICKS)
                {
                    CurseState.ZySleepTicks.Remove(entity.Pointer);
                    Sleep(entity);
                }
                else
                {
                    CurseState.ZySleepTicks[entity.Pointer] = ticks;
                }
            }
        }

        private static void Sleep(Entity entity)
        {
            try
            {
                var level = entity.Level;
                var definition = level.Content.GetBuffDefinition(VanillaBuffID.Contraption.nocturnal);
                if (definition == null)
                    return;
                if (HasBuff(entity, VanillaBuffID.Contraption.nocturnal))
                {
                    // 已经有睡眠 buff 了（例如原版对夜行性器械的昼夜睡眠，或别处挂的）。
                    // 明确**不**记进 ZySleeping —— 那份不是我们挂的，将来也不该由我们摘掉。
                    return;
                }
                CurseBuffBase.AddBuff(entity, definition);
                CurseState.ZySleeping.Add(entity.Pointer);
                Plugin.Log?.LogInfo("[ZYC1] 器械入眠（离开光照 8s）: " + entity.GetDefinitionID());
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ZYC1] 入眠失败: " + e.GetType().Name);
            }
        }

        /// <summary>只在"我们自己让它睡的"情况下唤醒。</summary>
        private static void Wake(Entity entity)
        {
            if (!CurseState.ZySleeping.Remove(entity.Pointer))
                return;
            try
            {
                CurseBuffBase.RemoveBuffsOf(entity, VanillaBuffID.Contraption.nocturnal);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ZYC1] 唤醒失败: " + e.GetType().Name);
            }
        }

        /// <summary>诅咒被关掉（或离开本关）时把睡着的都叫醒。</summary>
        private static void WakeAll(LevelEngine level)
        {
            if (CurseState.ZySleeping.Count == 0)
                return;
            foreach (var ptr in new List<IntPtr>(CurseState.ZySleeping))
            {
                var entity = CurseBuffBase.FindByPointer(level, ptr);
                if (entity != null)
                    Wake(entity);
                else
                    CurseState.ZySleeping.Remove(ptr);
            }
        }
        #endregion

        #region zy_c15 晚安（非荧石发光器械光照 ×50%）
        /// <summary>zy_c15：给"非荧石"的发光器械挂上光照 ×50% 的子 buff。</summary>
        private static void TickDimLights(LevelEngine level,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Entity> entities)
        {
            if (!On(level, IdZyC15))
                return;
            if (entities == null)
                return;
            var definition = CurseBuffs.Get("zy_c15_dim");
            if (definition == null)
                return;
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (entity == null || entity.IsDead)
                    continue;
                if (entity.Type != EntityTypes.PLANT)
                    continue;
                if (!entity.IsLightSource())
                    continue;
                // 荧石不受影响。
                if (SameID(entity.GetDefinitionID(), VanillaContraptionID.glowstone))
                    continue;
                if (HasBuff(entity, definition.GetID()))
                    continue;
                CurseBuffBase.AddBuff(entity, definition);
            }
        }
        #endregion
        #endregion

        #region Harmony 入口
        /// <summary>zy_c2 夜长梦多：每小波 +2.5%，上限 50%。</summary>
        public static int ScaleMaxRecharge(SeedPack pack, int value)
        {
            if (pack == null)
                return value;
            try
            {
                var level = pack.Level;
                if (level == null)
                    return value;

                if (On(level, IdZyC2))
                {
                    var percent = ZY_C2_PER_WAVE * level.CurrentWave;
                    if (percent > ZY_C2_CAP)
                        percent = ZY_C2_CAP;
                    if (percent > 0f)
                        return Mathf.RoundToInt(value * (1f + percent));
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ZYC2] 冷却计算失败: " + e.GetType().Name);
            }
            return value;
        }

        /// <summary>zy_c4 排异反应：拾取星之碎片 → 星之碎片被禁用 12s（可叠加、无上限）。</summary>
        public static void OnStarshardCollected(Entity pickup)
        {
            if (pickup == null)
                return;
            try
            {
                var level = pickup.Level;
                if (level == null || !On(level, IdZyC4))
                    return;
                CurseState.ZyStarshardBan += ZY_C4_BAN_TICKS;
                CurseRuntime.RefreshToolDisableState(level);
                Plugin.Log?.LogInfo("[ZYC4] 拾取星之碎片 → 星之碎片禁用累计 "
                    + (CurseState.ZyStarshardBan / CurseRuntime.TICKS_PER_SECOND) + "s");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ZYC4] 失败: " + e.GetType().Name);
            }
        }

        /// <summary>zy_c11 衰变：友方爆炸的伤害阵营改为中立（打所有人）。</summary>
        public static void DecayFaction(LevelEngine level, ref int faction)
        {
            if (level == null)
                return;
            try
            {
                if (!On(level, IdZyC11))
                    return;
                if (faction != level.GetLeftFaction())
                    return;
                faction = VanillaFactions.NEUTRAL;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// zy_c9 它回来了：被消除亡语 / 坠空 / 落水而死的敌人重生为携带「成真」的恶魂。
        /// 需求：被地雷击杀的敌人除外 —— 地雷的爆炸用
        /// 「IGNORE_ARMOR + REMOVE_ON_DEATH + NO_DEATH_EFFECTS」这一组标记，据此识别。
        /// </summary>
        public static void TryRebirthAsGhast(Entity enemy, DeathInfo info)
        {
            if (enemy == null || info == null)
                return;
            if (!CurseBuffBase.IsEnemyType(enemy.Type))
                return;
            try
            {
                var level = enemy.Level;
                if (level == null || !On(level, IdZyC9))
                    return;
                // 恶魂自己不再变一次（否则会无限套娃）。
                if (enemy.IsEntityOf(VanillaEnemyID.ghast))
                    return;
                if (IsMineKill(info))
                    return;

                var noDeathEffects = info.HasEffect(VanillaDamageEffects.NO_DEATH_EFFECTS)
                    || enemy.HasNoDeathEffects();
                var fell = info.HasEffect(VanillaDamageEffects.FALL_OFF);
                var drowned = info.HasEffect(VanillaDamageEffects.DROWN);
                if (!noDeathEffects && !fell && !drowned)
                    return;

                var ghast = enemy.SpawnWithParams(VanillaEnemyID.ghast, enemy.Position);
                if (ghast == null)
                    return;
                var definition = level.Content.GetBuffDefinition(VanillaBuffID.Enemy.nightmareComeTrue);
                if (definition != null)
                    CurseBuffBase.AddBuff(ghast, definition);
                Plugin.Log?.LogInfo("[ZYC9] " + enemy.GetDefinitionID() + " 重生为恶魂");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ZYC9] 重生失败: " + e.GetType().Name);
            }
        }

        /// <summary>这次死亡是否来自地雷（mine_tnt）的爆炸。</summary>
        private static bool IsMineKill(DeathInfo info)
        {
            return info.HasEffect(VanillaDamageEffects.IGNORE_ARMOR)
                && info.HasEffect(VanillaDamageEffects.REMOVE_ON_DEATH)
                && info.HasEffect(VanillaDamageEffects.NO_DEATH_EFFECTS);
        }

        /// <summary>
        /// zy_c10 病毒式传播：第 x 大波开始后，出怪点数**低于** x 的敌人生成时，
        /// 额外在其他行生成 1 个不占用出怪点数的复制。
        /// 例：第一大波后（x=1）出怪点数 0 的敌人会额外多 1 个；第二大波后（x=2）点数 0/1 的都会多 1 个。
        /// </summary>
        public static void OnEnemySpawned(LevelEngine level, SpawnDefinition spawnDef, int lane)
        {
            if (level == null || spawnDef == null)
                return;
            if (CurseState.ZyViralBusy)
                return;
            try
            {
                if (!On(level, IdZyC10))
                    return;
                if (!HasFirstBigWaveStarted(level))
                    return;
                var x = CurrentBigWaveIndex(level);
                if (spawnDef.GetSpawnLevel() >= x)
                    return;
                var laneCount = level.GetMaxLaneCount();
                if (laneCount <= 1)
                    return;

                CurseState.ZyViralBusy = true;
                var spawnX = level.GetEnemySpawnX();
                var made = 0;
                for (var offset = 1; offset < laneCount && made < ZY_C10_COPIES; offset++)
                {
                    var other = (lane + offset) % laneCount;
                    if (other == lane)
                        continue;
                    level.SpawnEnemy(spawnDef, other, spawnX);
                    made++;
                }
                if (made > 0)
                    Plugin.Log?.LogInfo("[ZYC10] " + spawnDef.GetID() + " 额外生成 " + made + " 个复制");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ZYC10] 复制失败: " + e.GetType().Name);
            }
            finally
            {
                CurseState.ZyViralBusy = false;
            }
        }

        /// <summary>zy_c12 久经沙场：单次伤害超过 80（不含 80）且没死 → 永久记录。</summary>
        public static void MarkVeteran(Entity entity, DamageInput input)
        {
            if (entity == null || input == null)
                return;
            if (!CurseBuffBase.IsEnemyType(entity.Type))
                return;
            try
            {
                var level = entity.Level;
                if (level == null || !On(level, IdZyC12))
                    return;
                if (!(input.Amount > ZY_C12_THRESHOLD))
                    return;
                if (entity.IsDead)
                    return;
                if (CurseState.ZyVeteran.Add(entity.Pointer))
                    Plugin.Log?.LogInfo("[ZYC12] " + entity.GetDefinitionID() + " 获得久经沙场（单次伤害上限 20）");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>zy_c12 久经沙场：把久经沙场敌人的单次伤害压到 20。</summary>
        public static void ClampVeteranDamage(Entity entity, DamageInput input)
        {
            if (entity == null || input == null)
                return;
            if (!CurseState.ZyVeteran.Contains(entity.Pointer))
                return;
            if (input.Amount > ZY_C12_LIMIT)
                input.SetAmount(ZY_C12_LIMIT);
        }

        /// <summary>zy_c7 室上性心动过速：第一大波开始前波长最短 16s。</summary>
        public static float MinWaveSeconds(LevelEngine level, float value)
        {
            if (level == null)
                return value;
            try
            {
                if (!On(level, IdZyC7))
                    return value;
                if (HasFirstBigWaveStarted(level))
                    return value;
                return Mathf.Max(value, ZY_C7_MIN_WAVE_SECONDS);
            }
            catch (Exception)
            {
                return value;
            }
        }

        /// <summary>
        /// zy_c8 我们不去幻想乡：倒数第二大波开始后，出怪量 +200%。
        /// 无尽模式按"本轮第几大波"判断（每轮重置）。
        ///
        /// 关卡格数（totalFlags）的对应关系：
        ///   3 格关 → 第 2 大波开始加出怪（2f 加、3f 也加）；
        ///   2 格关 → 第 1 大波开始就加；
        ///   1 格关 → 不生效。
        /// </summary>
        public static float ScaleSpawnPoints(LevelEngine level, float value)
        {
            if (level == null)
                return value;
            try
            {
                if (!On(level, IdZyC8))
                    return value;
                var total = level.GetTotalFlags();
                if (total <= 1)
                    return value;
                if (CurrentLocalBigWaveIndex(level) < total - 1)
                    return value;
                return value * ZY_C8_MULTIPLIER;
            }
            catch (Exception)
            {
                return value;
            }
        }

        /// <summary>zy_c13 纯粹：这个制品是否允许携带（true = 允许）。</summary>
        public static bool IsArtifactAllowed(LevelBlueprintChooseController ctl, NamespaceID id)
        {
            if (ctl == null || !NamespaceID.IsValid(id))
                return true;
            try
            {
                if (!On(ctl.Level, IdZyC13))
                    return true;
                var list = ArtifactExemptions;
                for (var i = 0; i < list.Length; i++)
                {
                    if (SameID(id, list[i]))
                        return true;
                }
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>zy_c13：把这一关实际带进来的制品里的非例外项清掉（含继承来的）。</summary>
        public static void PruneArtifacts(LevelEngine level)
        {
            if (level == null)
                return;
            try
            {
                if (!On(level, IdZyC13))
                    return;
                var artifacts = level.GetArtifacts();
                if (artifacts == null || artifacts.Length == 0)
                    return;
                var ids = new NamespaceID[artifacts.Length];
                var changed = false;
                for (var i = 0; i < artifacts.Length; i++)
                {
                    var artifact = artifacts[i];
                    var id = artifact == null || artifact.Definition == null
                        ? null
                        : artifact.Definition.GetID();
                    if (!NamespaceID.IsValid(id))
                        continue;
                    var list = ArtifactExemptions;
                    var allowed = false;
                    for (var j = 0; j < list.Length; j++)
                    {
                        if (SameID(id, list[j]))
                        {
                            allowed = true;
                            break;
                        }
                    }
                    ids[i] = allowed ? id : null;
                    if (!allowed)
                        changed = true;
                }
                if (!changed)
                    return;
                level.ReplaceArtifacts(ids);
                Plugin.Log?.LogInfo("[ZYC13] 已移除不允许携带的制品");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ZYC13] 清理制品失败: " + e.GetType().Name);
            }
        }

        /// <summary>zy_c14 恶意（攻击间隔取最大值 → 攻速取最小值）。</summary>
        public static float MinAttackSpeed(Entity entity, float value)
        {
            return ClampSpeed(entity, value, false);
        }

        /// <summary>zy_c14 恶意（生产间隔取最大值 → 生产速度取最小值）。</summary>
        public static float MinProduceSpeed(Entity entity, float value)
        {
            return ClampSpeed(entity, value, true);
        }

        /// <summary>重入保护：两个补丁会互相调用对方的取值函数。</summary>
        private static bool inSpeedClamp;

        /// <summary>
        /// zy_c14 恶意：第一大波开始前，器械的「攻击间隔」与「生产间隔」取最大值。
        ///
        /// 间隔与速度成反比，因此这里改成两个速度取 min —— 也就是让快的那一项
        /// 减速到慢的那一项（原版默认值都是 1，所以只对"被强化过的/生产特化"的器械有感觉）。
        /// </summary>
        private static float ClampSpeed(Entity entity, float value, bool produce)
        {
            if (entity == null || inSpeedClamp)
                return value;
            try
            {
                var level = entity.Level;
                if (level == null || !On(level, IdZyC14))
                    return value;
                if (HasFirstBigWaveStarted(level))
                    return value;
                if (entity.Type != EntityTypes.PLANT)
                    return value;
                if (!entity.IsFriendlyEntity())
                    return value;
                float other;
                inSpeedClamp = true;
                try
                {
                    other = produce ? entity.GetAttackSpeed() : entity.GetProduceSpeed();
                }
                finally
                {
                    inSpeedClamp = false;
                }
                return Mathf.Min(value, other);
            }
            catch (Exception)
            {
                inSpeedClamp = false;
                return value;
            }
        }

        /// <summary>
        /// zy_c16 抗拒：用鼠标点击器械时，其失去 10 点耐久。
        /// （不再阻止点击 —— 该干什么还干什么，只是掉血。）
        /// </summary>
        public static void OnContraptionClicked(Entity contraption)
        {
            if (contraption == null || contraption.IsDead)
                return;
            if (contraption.Type != EntityTypes.PLANT)
                return;
            try
            {
                var level = contraption.Level;
                if (level == null || !On(level, IdZyC16))
                    return;
                var effects = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<NamespaceID>(1);
                effects[0] = VanillaDamageEffects.MUTE;
                contraption.TakeDamage(ZY_C16_CLICK_DAMAGE, new DamageEffectList(effects), null, null);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ZYC16] 点击扣耐久失败: " + e.GetType().Name);
            }
        }

        /// <summary>
        /// 矿场的「打开 / 关闭」动画。
        /// interop 生成的代理上看不到 <c>Entity : IHasModel</c>，所以要先按指针包一层 <c>IHasModel</c>
        /// （与 <c>new IBuffTarget(ptr)</c> 同一个套路）。
        /// </summary>
        private static void SetMinerOpen(Entity entity, bool open)
        {
            try
            {
                PVZEngine.Models.HasModelExt.SetAnimationBool(
                    new PVZEngine.Models.IHasModel(entity.Pointer), "Open", open);
            }
            catch (Exception)
            {
                // 动画只是表现，失败就算了。
            }
        }

        /// <summary>
        /// zy_c3 地狱残响：把矿场（<see cref="Miner"/>）的生产间隔改成 30s。
        ///
        /// 原版 <c>Miner.Update</c> 里写死了 <c>PRODUCE_TIME = 300</c>（10s），
        /// 没有属性可以改，所以这里整段接管它的 Update（返回 true = 已完全替代）。
        /// </summary>
        public static bool ReplaceMinerUpdate(Entity entity)
        {
            if (entity == null)
                return false;
            try
            {
                var level = entity.Level;
                if (level == null || !On(level, IdZyC3))
                    return false;
                if (!level.IsNoEnergy() && (level.IsDay() || Miner.WorksAllDay(entity)))
                {
                    var timer = Miner.GetProductTimer(entity);
                    if (timer != null)
                    {
                        timer.Run(1f);
                        if (timer.Expired)
                        {
                            entity.Produce(VanillaPickupID.redstone);
                            entity.PlaySound(VanillaSoundID.throwSound);
                            timer.ResetTime(ZY_C3_PRODUCE_TICKS);
                        }
                    }
                    SetMinerOpen(entity, true);
                }
                else
                {
                    SetMinerOpen(entity, false);
                }
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ZYC3] 矿场接管失败: " + e.GetType().Name);
                return false;
            }
        }
        #endregion
    }
}
