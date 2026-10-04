using System;
using BepInEx.Logging;
using HarmonyLib;
using MVZ2.GameContent.Buffs.Enemies;
using MVZ2.GameContent.Contraptions;
using MVZ2.GameContent.Damages;
using MVZ2.GameContent.Effects;
using MVZ2.GameContent.HeldItems;
using MVZ2.GameContent.Pickups;
using MVZ2.GameContent.Projectiles;
using MVZ2.GameContent.Seeds;
using MVZ2.Level;
using MVZ2.Vanilla.Contraptions;
using MVZ2.Vanilla.Entities;
using MVZ2.Vanilla.Enemies;
using MVZ2.Vanilla.Level;
using MVZ2.Vanilla.Projectiles;
using MVZ2Logic.Blueprints;
using MVZ2Logic.Grids;
using MVZ2Logic.Level;
using MVZ2Logic.Spawns;
using PVZEngine;
using PVZEngine.Buffs;
using PVZEngine.Collisions;
using PVZEngine.Damages;
using PVZEngine.Definitions;
using PVZEngine.Entities;
using PVZEngine.Grids;
using PVZEngine.Level;
using PVZEngine.SeedPacks;
using PVZEngine.Spawns;
using PVZEngine.Tools.Timing;

namespace ModularCurse
{
    /// <summary>
    /// 全部 Harmony 补丁。
    ///
    /// 为什么行为逻辑走 Harmony 而不是 buff 的原生触发器：
    ///   原版 <c>AddTrigger*</c> 收 IL2CPP 侧委托，而所有关卡回调的参数都是
    ///   含引用字段的结构体（非 blittable），Il2CppInterop 的
    ///   DelegateSupport.ConvertDelegate 明确拒绝这类签名
    ///   （"Delegate has parameter of type ... (non-blittable struct) which is not supported"），
    ///   托管 mod 无法注册原生触发器。
    ///
    ///   因此的分工是：**能用修饰符表达的走 buff（QC-C8 / SC-C7 / SC-C12 +
    ///   CURSE_COUNT 计数），需要回调的走这里**；两边都以"关卡是否挂着对应
    ///   诅咒 buff"为唯一开关（CurseBuffBase.Active）。
    ///
    /// 每个补丁独立 try/catch：单个签名解析失败只丢该功能，不影响其余功能与游戏启动。
    /// </summary>
    internal static class CurseHooks
    {
        private static bool applied;
        private static Harmony harmony;
        private static ManualLogSource Log => Plugin.Log;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            harmony = new Harmony(Plugin.GUID + ".hooks");

            // ---------- QC-C1 漏电 / 小推车无视诅咒无敌 ----------
            TryPatch("VanillaEntityExt.TakeDamage",
                AccessTools.Method(typeof(VanillaEntityExt), "TakeDamage", new[] { typeof(DamageInput) }),
                prefix: new HarmonyMethod(typeof(CurseHooks), nameof(TakeDamagePrefix)),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(TakeDamagePostfix)));

            // ---------- QC-C2 工具冷却 / SC-C6 碎片污染 ----------
            TryPatch("PickaxeHeldItemBehaviour.UseOnEntity",
                AccessTools.Method(typeof(PickaxeHeldItemBehaviour), "UseOnEntity"),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(PickaxeUsePostfix)));
            TryPatch("StarshardHeldItemBehaviour.UseOnEntity",
                AccessTools.Method(typeof(StarshardHeldItemBehaviour), "UseOnEntity"),
                prefix: new HarmonyMethod(typeof(CurseHooks), nameof(StarshardUsePrefix)),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(StarshardUsePostfix)));

            // ---------- QC-C3 凋零 ----------
            TryPatch("VanillaContraptionExt.Evoke",
                AccessTools.Method(typeof(VanillaContraptionExt), "Evoke", new[] { typeof(Entity) }),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(ContraptionEvokePostfix)));

            // ---------- QC-C5 死亡留火焰 / SC-C3 死亡无敌 / SC-C8 首次死亡复活（都在 Die 上） ----------
            TryPatch("Entity.Die(DeathInfo)",
                AccessTools.Method(typeof(Entity), "Die", new[] { typeof(DeathInfo) }),
                prefix: new HarmonyMethod(typeof(CurseHooks), nameof(DiePrefix)));

            // ---------- QC-C4 / SC-C4 尸体复活（尸体即将消失的时机） ----------
            TryPatch("VanillaEnemyExt.FaintRemove",
                AccessTools.Method(typeof(VanillaEnemyExt), "FaintRemove", new[] { typeof(Entity) }),
                prefix: new HarmonyMethod(typeof(CurseHooks), nameof(FaintRemovePrefix)));

            // ---------- SC-C5 / C8 / C11 / C13 受伤前 ----------
            TryPatch("VanillaEntityExt.PreTakeDamage",
                AccessTools.Method(typeof(VanillaEntityExt), "PreTakeDamage",
                    new[] { typeof(DamageInput), typeof(DamageOutput) }),
                prefix: new HarmonyMethod(typeof(CurseHooks), nameof(PreTakeDamagePrefix)));

            // ---------- QC-C7 漫长充能 ----------
            TryPatch("SeedPack.GetUsedMaxRecharge",
                AccessTools.Method(typeof(SeedPack), "GetUsedMaxRecharge"),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(GetUsedMaxRechargePostfix)));
            TryPatch("SeedPack.GetStartMaxRecharge",
                AccessTools.Method(typeof(SeedPack), "GetStartMaxRecharge"),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(GetStartMaxRechargePostfix)));

            // ---------- QC-C11 / SC-C1 费用 ----------
            TryPatch("SeedPack.GetCost",
                AccessTools.Method(typeof(SeedPack), "GetCost"),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(GetCostPostfix)));

            // ---------- QC-C9 器械子弹速度 ----------
            TryPatch("VanillaProjectileExt.ShootProjectile",
                AccessTools.Method(typeof(VanillaProjectileExt), "ShootProjectile",
                    new[] { typeof(Entity), typeof(ProjectileShootArgs) }),
                prefix: new HarmonyMethod(typeof(CurseHooks), nameof(ShootProjectilePrefix)));

            // ---------- QC-C10 穿透阻挡 ----------
            TryPatch("ProjectileCommonBehaviour.PostCollision",
                AccessTools.Method(typeof(ProjectileCommonBehaviour), "PostCollision"),
                prefix: new HarmonyMethod(typeof(CurseHooks), nameof(PostCollisionPrefix)));
            TryPatch("VanillaProjectileExt.CanPierce",
                AccessTools.Method(typeof(VanillaProjectileExt), "CanPierce", new[] { typeof(Entity) }),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(CanPiercePostfix)));

            // ---------- QC-C12 借贷告示牌产出 ----------
            TryPatch("LoanSign.Init",
                AccessTools.Method(typeof(LoanSign), "Init", new[] { typeof(Entity) }),
                prefix: new HarmonyMethod(typeof(CurseHooks), nameof(LoanSignInitPrefix)));

            // ---------- SC-C7 击退量减半 + 落地敌人至少重质量（改击退系数，不动风扇） ----------
            TryPatch("VanillaEntityProps.GetKnockbackMultiplier",
                AccessTools.Method(typeof(VanillaEntityProps), "GetKnockbackMultiplier",
                    new[] { typeof(Entity), typeof(float) }),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(GetKnockbackMultiplierPostfix)));

            // ---------- SC-C14 每 10 波封禁器械放置 ----------
            TryPatch("LogicGridExt.CanPlaceBlueprint",
                AccessTools.Method(typeof(LogicGridExt), "CanPlaceBlueprint",
                    new[] { typeof(LawnGrid), typeof(SeedDefinition), typeof(NamespaceID).MakeByRefType() }),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(CanPlaceBlueprintPostfix)));

            // ---------- ZY-C3 衰变（友方爆炸不分敌我） ----------
            TryPatch("VanillaLevelExt.Explode",
                AccessTools.Method(typeof(VanillaLevelExt), "Explode",
                    new[]
                    {
                        typeof(LevelEngine), typeof(UnityEngine.Vector3), typeof(float), typeof(int),
                        typeof(float), typeof(DamageEffectList), typeof(ILevelSourceReference),
                        typeof(Il2CppSystem.Predicate<IEntityCollider>),
                    }),
                prefix: new HarmonyMethod(typeof(CurseHooks), nameof(ExplodePrefix)));

            // ---------- ZY-C5 排异反应（拾取星之碎片） ----------
            TryPatch("CollectBehaviour_Starshard.PostCollect",
                AccessTools.Method(typeof(CollectBehaviour_Starshard), "PostCollect",
                    new[] { typeof(Entity) }),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(PostCollectPostfix)));

            // ---------- ZY-C8 病毒式传播（出怪时额外复制） ----------
            TryPatch("LogicLevelExt.SpawnEnemy(spawnDef, lane, x)",
                AccessTools.Method(typeof(LogicLevelExt), "SpawnEnemy",
                    new[] { typeof(LevelEngine), typeof(SpawnDefinition), typeof(int), typeof(float) }),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(SpawnEnemyPostfix)));

            // ---------- ZY-C9 它回来了（重生恶魂） ----------
            // （在 Entity.Die 前缀里，与 QC-C5 / SC-C3 / SC-C8 同一个入口）

            // ---------- SC-C9 古代历石（12s 状态抗性） ----------
            TryPatch("VanillaEntityExt.PreApplyStatusEffect",
                AccessTools.Method(typeof(VanillaEntityExt), "PreApplyStatusEffect",
                    new[] { typeof(Entity), typeof(BuffDefinition), typeof(ILevelSourceReference) }),
                prefix: new HarmonyMethod(typeof(CurseHooks), nameof(PreApplyStatusEffectPrefix)));

            // ---------- ZY-C11 室上性心动过速（波长下限） ----------
            TryPatch("LogicStageProps.GetWaveMaxSeconds",
                AccessTools.Method(typeof(LogicStageProps), "GetWaveMaxSeconds",
                    new[] { typeof(LevelEngine) }),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(GetWaveMaxSecondsPostfix)));

            // ---------- ZY-C12 我们不去幻想乡（出怪量 +200%） ----------
            TryPatch("LogicLevelExt.CalculateSpawnPoints(wave, flags)",
                AccessTools.Method(typeof(LogicLevelExt), "CalculateSpawnPoints",
                    new[] { typeof(LevelEngine), typeof(int), typeof(int) }),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(CalculateSpawnPointsPostfix)));

            // ---------- ZY-C13 纯粹（不能携带制品） ----------
            TryPatch("LevelBlueprintChooseController.CanChooseArtifact",
                AccessTools.Method(typeof(LevelBlueprintChooseController), "CanChooseArtifact",
                    new[] { typeof(NamespaceID) }),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(CanChooseArtifactPostfix)));
            TryPatch("LevelBlueprintChooseController.ApplyCurses",
                AccessTools.Method(typeof(LevelBlueprintChooseController), "ApplyCurses"),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(ApplyCursesZyPostfix)));

            // ---------- ZY-C14 恶意（攻击 / 生产间隔取最大值） ----------
            TryPatch("VanillaEntityProps.GetAttackSpeed",
                AccessTools.Method(typeof(VanillaEntityProps), "GetAttackSpeed",
                    new[] { typeof(Entity), typeof(bool) }),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(GetAttackSpeedPostfix)));
            TryPatch("VanillaEntityProps.GetProduceSpeed",
                AccessTools.Method(typeof(VanillaEntityProps), "GetProduceSpeed",
                    new[] { typeof(Entity) }),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(GetProduceSpeedPostfix)));

            // ---------- ZY-C3 地狱残响（矿场生产间隔 30s） ----------
            TryPatch("Miner.Update",
                AccessTools.Method(typeof(Miner), "Update", new[] { typeof(Entity) }),
                prefix: new HarmonyMethod(typeof(CurseHooks), nameof(MinerUpdatePrefix)));

            // ---------- ZY-C16 抗拒（点击器械 → 扣 10 点耐久） ----------
            TryPatch("VanillaContraptionExt.EmptyHandClick",
                AccessTools.Method(typeof(VanillaContraptionExt), "EmptyHandClick",
                    new[] { typeof(Entity) }),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(EmptyHandClickPostfix)));

            // ---------- 统一计时 ----------
            TryPatch("LevelEngine.Update",
                AccessTools.Method(typeof(LevelEngine), "Update", Type.EmptyTypes),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(LevelUpdatePostfix)));

            Log?.LogInfo("[CurseHooks] all hooks processed");
        }

        private static void TryPatch(string label, System.Reflection.MethodInfo target,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            if (target == null)
            {
                Log?.LogError("[CurseHooks] patch target not found: " + label);
                return;
            }
            try
            {
                harmony.Patch(target, prefix: prefix, postfix: postfix);
                Log?.LogInfo("[CurseHooks] patched " + label);
            }
            catch (Exception e)
            {
                Log?.LogError("[CurseHooks] patch failed: " + label + " -> "
                    + e.GetType().Name + ": " + e.Message);
            }
        }

        #region QC-C1 漏电 / 小推车无视诅咒无敌 / ZY-C12 久经沙场
        /// <summary>小推车碾压（INSTA_KILL）无视诅咒给予的无敌（SC-C13 的铁幕）。</summary>
        [HarmonyPrefix]
        private static void TakeDamagePrefix(DamageInput input)
        {
            try
            {
                CurseRuntime.AllowInstaKillThroughCurseInvincibility(input);
            }
            catch (Exception e)
            {
                Log?.LogError("[Cart] prefix error: " + e);
            }
        }

        [HarmonyPostfix]
        private static void TakeDamagePostfix(DamageInput input, ref DamageOutput __result)
        {
            try
            {
                // ZY-C12 久经沙场先记（它只关心敌人，而下面那一段会因类型提前 return）。
                if (input != null)
                    ZyRuntime.MarkVeteran(input.Entity, input);

                var output = __result;
                if (output == null || !output.HasDamageAmount())
                    return;
                var entity = output.Entity;
                if (entity == null && input != null)
                    entity = input.Entity;
                if (entity == null || entity.Type != EntityTypes.PLANT)
                    return;
                CurseRuntime.ShortCircuit(entity);
            }
            catch (Exception e)
            {
                Log?.LogError("[QCC1] postfix error: " + e);
            }
        }
        #endregion

        #region QC-C2 工具冷却 / SC-C6 碎片污染
        [HarmonyPostfix]
        private static void PickaxeUsePostfix(Entity entity)
        {
            try
            {
                CurseRuntime.StartToolCooldown(entity, true);
            }
            catch (Exception e)
            {
                Log?.LogError("[QCC2] postfix error: " + e);
            }
        }

        [HarmonyPostfix]
        private static void StarshardUsePostfix(Entity entity)
        {
            try
            {
                CurseRuntime.StartToolCooldown(entity, false);
            }
            catch (Exception e)
            {
                Log?.LogError("[QCC2] postfix error: " + e);
            }
        }

        /// <summary>SC-C6：无效的星之碎片照样扣掉，但不产生任何效果。</summary>
        [HarmonyPrefix]
        private static bool StarshardUsePrefix(Entity entity)
        {
            try
            {
                if (entity == null)
                    return true;
                if (!CurseRuntime.IsStarshardUseVoid(entity.Level))
                    return true;
                entity.Level.AddStarshardCount(-1);
                return false;
            }
            catch (Exception e)
            {
                Log?.LogError("[SCC6] prefix error: " + e);
                return true;
            }
        }
        #endregion

        #region QC-C3 凋零
        [HarmonyPostfix]
        private static void ContraptionEvokePostfix(Entity contraption)
        {
            try
            {
                CurseRuntime.ApplyWither(contraption);
            }
            catch (Exception e)
            {
                Log?.LogError("[QCC3] postfix error: " + e);
            }
        }
        #endregion

        #region QC-C5 留火焰 / SC-C3 死亡无敌 / SC-C8 首次死亡复活
        /// <summary>
        /// 死亡瞬间的统一入口：
        ///   · QC-C5：留下火焰（"复活归复活、火焰归火焰"）；
        ///   · SC-C10：出场 10s 内的敌怪不死（血量保底 1，返回 false）；
        ///   · SC-C8：非无来源的敌怪首次死亡 → 取消死亡、血回到 15%（返回 false）；
        ///   · ZY-C9：被消除亡语 / 坠空 / 落水而死 → 原地重生为携带成真的恶魂；
        ///   · SC-C3：真正死掉的敌人 → 眩晕全场非免控器械 0.1s（视为亡语）。
        /// </summary>
        [HarmonyPrefix]
        private static bool DiePrefix(Entity __instance, DeathInfo info)
        {
            try
            {
                var entity = __instance;
                if (entity == null)
                    return true;
                CurseRuntime.LeaveFire(entity);
                if (CurseRuntime.PreventDeathByGrace(entity, info))
                    return false;
                if (CurseRuntime.TrySurviveDeath(entity, info))
                    return false;
                ZyRuntime.TryRebirthAsGhast(entity, info);
                if (CurseBuffBase.IsEnemyType(entity.Type))
                    CurseRuntime.OnEnemyDied(entity, info != null && entity.ShouldTriggerDeathEffects(info));
            }
            catch (Exception e)
            {
                Log?.LogError("[Die] prefix error: " + e);
            }
            return true;
        }

        /// <summary>QC-C4：尸体即将消失时判定复活（返回 false = 取消消失）。</summary>
        [HarmonyPrefix]
        private static bool FaintRemovePrefix(Entity __0)
        {
            try
            {
                return !CurseRuntime.BeforeFaintRemove(__0);
            }
            catch (Exception e)
            {
                Log?.LogError("[FaintRemove] prefix error: " + e);
                return true;
            }
        }
        #endregion

        #region SC-C16 / C5 / C11 / C13 / C15 + ZY-C10 受伤前
        [HarmonyPrefix]
        private static void PreTakeDamagePrefix(DamageInput damageInfo, DamageOutput output)
        {
            CurseRuntime.PreDamage(damageInfo);
            // ZY-C10：久经沙场的敌人单次受伤不超过 20。
            if (damageInfo != null)
                ZyRuntime.ClampVeteranDamage(damageInfo.Entity, damageInfo);
        }
        #endregion

        #region QC-C7 漫长充能 / ZY-C2 夜长梦多 / ZY-C15 荧石冷却
        [HarmonyPostfix]
        private static void GetUsedMaxRechargePostfix(SeedPack __instance, ref int __result)
        {
            __result = CurseRuntime.ReplaceLongRecharge(__instance, __result);
            __result = ZyRuntime.ScaleMaxRecharge(__instance, __result);
        }

        [HarmonyPostfix]
        private static void GetStartMaxRechargePostfix(SeedPack __instance, ref int __result)
        {
            __result = CurseRuntime.ReplaceLongRecharge(__instance, __result);
        }
        #endregion

        #region QC-C11 / SC-C1 费用
        [HarmonyPostfix]
        private static void GetCostPostfix(SeedPack __instance, ref int __result)
        {
            __result = CurseRuntime.AdjustCost(__instance, __result);
        }
        #endregion

        #region QC-C9 子弹速度
        [HarmonyPrefix]
        private static void ShootProjectilePrefix(Entity __0, ProjectileShootArgs __1)
        {
            CurseRuntime.ModifyProjectileSpeed(__0, __1);
        }
        #endregion

        #region QC-C10 穿透阻挡
        [HarmonyPrefix]
        private static void PostCollisionPrefix(EntityCollision collision)
        {
            try
            {
                CurseRuntime.SetPierceTarget(collision == null ? null : collision.Other);
            }
            catch (Exception)
            {
            }
        }

        [HarmonyPostfix]
        private static void CanPiercePostfix(Entity projectile, ref bool __result)
        {
            try
            {
                if (CurseRuntime.BlockPierce(projectile, __result))
                    __result = false;
            }
            catch (Exception e)
            {
                Log?.LogError("[QCC10] postfix error: " + e);
            }
        }
        #endregion

        #region QC-C12 借贷告示牌
        [HarmonyPrefix]
        private static void LoanSignInitPrefix(Entity entity)
        {
            try
            {
                CurseRuntime.OnLoanSignInit(entity);
            }
            catch (Exception e)
            {
                Log?.LogError("[QCC12] prefix error: " + e);
            }
        }
        #endregion

        #region 小补丁：SC-C7 / SC-C14
        /// <summary>SC-C7：击退量减半；接触地面的敌人重量至少为 HEAVY。</summary>
        [HarmonyPostfix]
        private static void GetKnockbackMultiplierPostfix(Entity entity, float massMultiplier, ref float __result)
        {
            try
            {
                __result = CurseRuntime.ModifyKnockback(entity, massMultiplier, __result);
            }
            catch (Exception e)
            {
                Log?.LogError("[SCC7] postfix error: " + e);
            }
        }

        /// <summary>
        /// SC-C14：封禁期内"所有器械"都不允许放下（驱动器除外）。
        /// 拦在 <c>LawnGrid.CanPlaceBlueprint</c> 上 —— 手持蓝图与蓝图拾取物
        /// 两种放法都走这里（NamespaceID 重载内部也是转发到本重载）。
        /// </summary>
        [HarmonyPostfix]
        private static void CanPlaceBlueprintPostfix(LawnGrid __0, SeedDefinition __1,
            ref NamespaceID __2, ref bool __result)
        {
            try
            {
                if (!CurseRuntime.IsWaveGuardActive(__0 == null ? null : __0.Level))
                    return;
                if (__1 == null)
                    return;
                var entityID = LogicSeedProps.GetSeedEntityID(__1);
                if (!CurseRuntime.IsPlacementBanned(entityID))
                    return;
                __result = false;
                __2 = VanillaBlueprintErrors.decrepify;
            }
            catch (Exception e)
            {
                Log?.LogError("[SCC14] postfix error: " + e);
            }
        }
        #endregion

        #region ZY 池（咒鵺兼程）的补丁
        /// <summary>ZY-C3：友方爆炸的伤害阵营改为中立（打所有人，含自家器械）。</summary>
        [HarmonyPrefix]
        private static void ExplodePrefix(LevelEngine level, ref int faction)
        {
            try
            {
                ZyRuntime.DecayFaction(level, ref faction);
            }
            catch (Exception e)
            {
                Log?.LogError("[ZYC3] prefix error: " + e);
            }
        }

        /// <summary>ZY-C5：拾取星之碎片 → 禁用星之碎片 12s（可叠加）。</summary>
        [HarmonyPostfix]
        private static void PostCollectPostfix(Entity pickup)
        {
            try
            {
                ZyRuntime.OnStarshardCollected(pickup);
            }
            catch (Exception e)
            {
                Log?.LogError("[ZYC5] postfix error: " + e);
            }
        }

        /// <summary>ZY-C8：出怪点数 ≤ 当前大波序号的敌人在其他行额外生成 2 个复制。</summary>
        [HarmonyPostfix]
        private static void SpawnEnemyPostfix(LevelEngine level, SpawnDefinition spawnDef, int lane)
        {
            try
            {
                ZyRuntime.OnEnemySpawned(level, spawnDef, lane);
            }
            catch (Exception e)
            {
                Log?.LogError("[ZYC8] postfix error: " + e);
            }
        }

        /// <summary>SC-C9：12s 状态抗性期间，表中的状态效果直接被拦下。</summary>
        [HarmonyPrefix]
        private static bool PreApplyStatusEffectPrefix(Entity entity, BuffDefinition buff, ref bool __result)
        {
            try
            {
                if (!CurseRuntime.IsStatusImmune(entity, buff))
                    return true;
                __result = false;
                return false;
            }
            catch (Exception e)
            {
                Log?.LogError("[SCC9] prefix error: " + e);
                return true;
            }
        }

        /// <summary>ZY-C7：第一大波开始前波长最短 16s。</summary>
        [HarmonyPostfix]
        private static void GetWaveMaxSecondsPostfix(LevelEngine __instance, ref float __result)
        {
            try
            {
                __result = ZyRuntime.MinWaveSeconds(__instance, __result);
            }
            catch (Exception e)
            {
                Log?.LogError("[ZYC11] postfix error: " + e);
            }
        }

        /// <summary>ZY-C8：倒数第二大波起出怪量 +200%（1 格关不生效）。</summary>
        [HarmonyPostfix]
        private static void CalculateSpawnPointsPostfix(LevelEngine level, ref float __result)
        {
            try
            {
                __result = ZyRuntime.ScaleSpawnPoints(level, __result);
            }
            catch (Exception e)
            {
                Log?.LogError("[ZYC8] postfix error: " + e);
            }
        }

        /// <summary>ZY-C14：攻击 / 生产间隔取最大值（两个速度取 min）。</summary>
        [HarmonyPostfix]
        private static void GetAttackSpeedPostfix(Entity entity, ref float __result)
        {
            try
            {
                __result = ZyRuntime.MinAttackSpeed(entity, __result);
            }
            catch (Exception e)
            {
                Log?.LogError("[ZYC14] postfix error: " + e);
            }
        }

        /// <summary>ZY-C14：生产间隔取最大值。</summary>
        [HarmonyPostfix]
        private static void GetProduceSpeedPostfix(Entity entity, ref float __result)
        {
            try
            {
                __result = ZyRuntime.MinProduceSpeed(entity, __result);
            }
            catch (Exception e)
            {
                Log?.LogError("[ZYC14] postfix error: " + e);
            }
        }

        /// <summary>ZY-C3：矿场的生产间隔改为 30s（返回 false = 原版 Update 不再执行）。</summary>
        [HarmonyPrefix]
        private static bool MinerUpdatePrefix(Entity entity)
        {
            try
            {
                return !ZyRuntime.ReplaceMinerUpdate(entity);
            }
            catch (Exception e)
            {
                Log?.LogError("[ZYC3] prefix error: " + e);
                return true;
            }
        }

        /// <summary>ZY-C16：点击器械后扣 10 点耐久（点击本身照常生效）。</summary>
        [HarmonyPostfix]
        private static void EmptyHandClickPostfix(Entity contraption)
        {
            try
            {
                ZyRuntime.OnContraptionClicked(contraption);
            }
            catch (Exception e)
            {
                Log?.LogError("[ZYC16] postfix error: " + e);
            }
        }

        /// <summary>ZY-C13：不能选择（也不能携带）任何制品。</summary>
        [HarmonyPostfix]
        private static void CanChooseArtifactPostfix(LevelBlueprintChooseController __instance, NamespaceID id,
            ref bool __result)
        {
            try
            {
                if (!__result)
                    return;
                if (!ZyRuntime.IsArtifactAllowed(__instance, id))
                    __result = false;
            }
            catch (Exception e)
            {
                Log?.LogError("[ZYC13] postfix error: " + e);
            }
        }

        /// <summary>ZY-C13：真正开打时把已经带进来的非例外制品清掉（含继承来的）。</summary>
        [HarmonyPostfix]
        private static void ApplyCursesZyPostfix(LevelBlueprintChooseController __instance)
        {
            try
            {
                if (__instance == null)
                    return;
                ZyRuntime.PruneArtifacts(__instance.Level);
            }
            catch (Exception e)
            {
                Log?.LogError("[ZYC13] postfix error: " + e);
            }
        }
        #endregion

        #region 计时
        [HarmonyPostfix]
        private static void LevelUpdatePostfix(LevelEngine __instance)
        {
            CurseRuntime.Tick(__instance);
        }
        #endregion
    }
}
