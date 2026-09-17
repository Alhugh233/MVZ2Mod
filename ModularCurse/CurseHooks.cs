using System;
using BepInEx.Logging;
using HarmonyLib;
using MVZ2.GameContent.Contraptions;
using MVZ2.GameContent.HeldItems;
using MVZ2.GameContent.Projectiles;
using MVZ2.Vanilla.Contraptions;
using MVZ2.Vanilla.Entities;
using MVZ2.Vanilla.Enemies;
using MVZ2.Vanilla.Projectiles;
using MVZ2Logic.Level;
using PVZEngine.Collisions;
using PVZEngine.Damages;
using PVZEngine.Entities;
using PVZEngine.Level;
using PVZEngine.SeedPacks;

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

            // ---------- QC-C1 漏电 ----------
            TryPatch("VanillaEntityExt.TakeDamage",
                AccessTools.Method(typeof(VanillaEntityExt), "TakeDamage", new[] { typeof(DamageInput) }),
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

            // ---------- QC-C5 死亡留火焰 ----------
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

            // ---------- SC-C9 debuff 时长 ----------
            PatchDebuff("InflictSlow");
            PatchDebuff("InflictWeakness");
            PatchDebuff("InflictPetrified");
            PatchDebuff("InflictWither");

            // ---------- QC-C12 借贷告示牌产出 ----------
            TryPatch("LoanSign.Init",
                AccessTools.Method(typeof(LoanSign), "Init", new[] { typeof(Entity) }),
                prefix: new HarmonyMethod(typeof(CurseHooks), nameof(LoanSignInitPrefix)));

            // ---------- 统一计时 ----------
            TryPatch("LevelEngine.Update",
                AccessTools.Method(typeof(LevelEngine), "Update", Type.EmptyTypes),
                postfix: new HarmonyMethod(typeof(CurseHooks), nameof(LevelUpdatePostfix)));

            Log?.LogInfo("[CurseHooks] all hooks processed");
        }

        private static void PatchDebuff(string methodName)
        {
            var target = AccessTools.Method(typeof(VanillaEntityExt), methodName);
            if (target == null)
            {
                Log?.LogError("[CurseHooks] patch target not found: VanillaEntityExt." + methodName);
                return;
            }
            try
            {
                var prefix = new HarmonyMethod(AccessTools.Method(typeof(DebuffTimePatch), "Prefix"));
                harmony.Patch(target, prefix: prefix);
                Log?.LogInfo("[CurseHooks] patched VanillaEntityExt." + methodName);
            }
            catch (Exception e)
            {
                Log?.LogError("[CurseHooks] patch failed: " + methodName + " -> "
                    + e.GetType().Name + ": " + e.Message);
            }
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

        #region QC-C1 漏电
        [HarmonyPostfix]
        private static void TakeDamagePostfix(DamageInput input, ref DamageOutput __result)
        {
            try
            {
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

        #region QC-C5 留火焰 / QC-C4 + SC-C4 尸体复活
        /// <summary>QC-C5：死亡时留下火焰（"复活归复活、火焰归火焰"，两者互不影响）。</summary>
        [HarmonyPrefix]
        private static void DiePrefix(Entity __instance)
        {
            try
            {
                var entity = __instance;
                if (entity == null)
                    return;
                CurseRuntime.LeaveFire(entity);
            }
            catch (Exception e)
            {
                Log?.LogError("[Die] prefix error: " + e);
            }
        }

        /// <summary>QC-C4 / SC-C4：尸体即将消失时判定复活（返回 false = 取消消失）。</summary>
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

        #region SC-C5 / C8 / C10 / C11 受伤前
        [HarmonyPrefix]
        private static void PreTakeDamagePrefix(DamageInput damageInfo, DamageOutput output)
        {
            CurseRuntime.PreDamage(damageInfo);
        }
        #endregion

        #region QC-C7 漫长充能
        [HarmonyPostfix]
        private static void GetUsedMaxRechargePostfix(SeedPack __instance, ref int __result)
        {
            __result = CurseRuntime.ReplaceLongRecharge(__instance, __result);
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

        #region 计时
        [HarmonyPostfix]
        private static void LevelUpdatePostfix(LevelEngine __instance)
        {
            CurseRuntime.Tick(__instance);
        }
        #endregion
    }

    /// <summary>
    /// SC-C9：把 debuff 时长缩为 1/3。
    /// 用一个共用前缀覆盖多个 <c>InflictXxx(Entity entity, int time, ILevelSourceReference?)</c>，
    /// 靠参数名按位置接管。
    /// </summary>
    internal static class DebuffTimePatch
    {
        [HarmonyPrefix]
        private static void Prefix(Entity entity, ref int time)
        {
            try
            {
                time = CurseRuntime.ScaleDebuffTime(entity, time);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[SCC9] prefix error: " + e);
            }
        }
    }
}
