using System;
using System.Collections.Generic;
using HarmonyLib;
using MVZ2Logic.Blueprints;
using MVZ2Logic.Level;
using PVZEngine;
using PVZEngine.Entities;
using PVZEngine.Level;

namespace Loadout
{
    /// <summary>
    /// 「逆阵营放置」：被标记的那张卡放出来的器械属于右阵营（敌我互调）。
    ///
    /// <para><b>为什么必须打补丁</b>：阵营是在生成那一刻由 <c>SpawnParams</c> 定下的
    /// （<c>EntityCache.Faction</c> 读 <c>EngineEntityProps.FACTION</c>），而放置流程
    /// （<c>LawnGrid.SpawnPlacedEntity</c>）建 <c>SpawnParams</c> 的地方完全在官方内部，
    /// 面板够不着。唯一能插手的点是**生成漏斗** <c>LevelEngine.SpawnSourced</c>。</para>
    ///
    /// <para><b>命中条件</b>：这次生成能被 <see cref="PlacementScope"/> 认出属于某一张卡
    /// （放置调用开的作用域 / 模仿者变换的标签），且那张卡标了逆阵营。</para>
    ///
    /// <para>⚠ 与血量/白天不睡觉同一处、互相独立：两个 postfix 都会跑，
    /// 共用 <see cref="PlacementScope.Resolve"/> 的那一次解析结果（标签是一次性的，不能解析两遍）。</para>
    ///
    /// <para>这是**危险功能**（真的改了场上的敌我关系），归兼容性等级管：
    /// 补丁始终挂上，是否生效在运行时按 <see cref="GcFeature.ReverseFaction"/> 判断。</para>
    /// </summary>
    internal static class ReverseFactionPatch
    {
        private static bool applied;
        private static bool loggedOnce;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            try
            {
                var target = AccessTools.Method(typeof(LevelEngine), "SpawnSourced",
                    new[] { typeof(EntityDefinition), typeof(UnityEngine.Vector3), typeof(ILevelSourceReference), typeof(int), typeof(SpawnParams) });
                if (target == null)
                {
                    Plugin.Log?.LogError("[ReverseFaction] 找不到 LevelEngine.SpawnSourced，逆阵营不可用");
                    return;
                }
                var harmony = new Harmony(Plugin.GUID + ".reversefaction");
                harmony.Patch(target, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(ReverseFactionPatch), nameof(Postfix))));
                Plugin.Log?.LogInfo("[ReverseFaction] 已挂生成漏斗补丁（逆阵营放置）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ReverseFaction] 挂补丁失败：" + e);
            }
        }

        /// <summary>标记改了之后叫一次（下次生成前重算速查表）。</summary>
        public static void Refresh()
        {
            BlueprintMarks.Refresh();
        }

        private static void Postfix(ILevelSourceReference source, Entity __result)
        {
            try
            {
                if (__result == null)
                    return;
                if (!CompatConfig.IsEnabled(GcFeature.ReverseFaction))
                    return;

                var level = __result.Level;
                if (level == null)
                    return;

                string key;
                if (!PlacementScope.Resolve(__result, source, out key))
                    return;

                var mark = BlueprintMarks.Get(level, key);
                if (!mark.Has || !mark.ReverseFaction)
                    return;

                var faction = level.GetRightFaction();
                __result.SetFaction(faction);
                if (!loggedOnce)
                {
                    loggedOnce = true;
                    Plugin.Log?.LogInfo("[ReverseFaction] " + __result.GetDefinitionID()
                        + " → 阵营 " + faction + "（来自 " + BlueprintKeys.Describe(key) + "）");
                }
            }
            catch (Exception)
            {
                // 原生调用栈上不能抛。
            }
        }
    }
}
