using System;
using HarmonyLib;
using MVZ2.GameContent.Contraptions;
using MVZ2Logic.Entities;
using MVZ2Logic.Grids;
using MVZ2Logic.Placements;
using PVZEngine;
using PVZEngine.Definitions;
using PVZEngine.Entities;
using PVZEngine.Grids;
using PVZEngine.Level;
using PVZEngine.Placements;

namespace Loadout
{
    /// <summary>
    /// 「无条件放置」：**忽略放置条件**（地形 / 已占用 / 需蒋叶…），但**仍然扣费用与冷却**。
    ///
    /// <para><b>为什么要动两个地方</b>（早期版本只挂了第一处，你看到的症状正是
    /// 「费用和冷却照扣、实体却没生成出来」）：</para>
    ///
    /// <list type="number">
    /// <item>官方那道「放得下吗」的门在 <c>PlacementDefinition.GetPlaceError(grid, entityDef)</c>。
    /// <c>LogicGridExt.CanPlaceBlueprint</c>（放手时的检查）与 <c>GetSeedHeldHighlight</c>
    /// （把格子染红/绿）**都是**经它来的（<c>GetEntityPlaceStatus</c>）—— 所以只挂前者，
    /// 拖动时格子仍然是红色。</item>
    ///
    /// <item>真正的生成在 <c>PlacementDefinition.PlaceEntity</c> 里：它**又遍历一遍**
    /// 各 <c>PlaceMethod.GetPlaceError</c>，全都有错就返回 <c>InvalidOutput</c>（什么都不生成）。
    /// 所以只放行第一道门的结果就是"扣了费、没出东西"。
    /// 这里在放行时**自己按最朴素的实体生成方式**把它生成出来
    /// （照抄官方 <c>EntityPlaceMethod.PlaceEntity</c> 的那几行）。</item>
    /// </list>
    ///
    /// <para><b>紫卡（升级类）</b>：升级类放置平时要求格子上已经有可升级的器械，否则不生成任何东西
    /// —— 于是那张卡的标记（血量 / 阵营…）永远没机会生效。「无条件放置」下改成
    /// **直接生成它自己的器械**（需求：紫卡的器械属性交给仅在无条件放置下生成的实体），
    /// 于是标记照常落在那个新实体上。</para>
    ///
    /// <para><b>影响范围</b>：只碰蓝图放置。敌人的生成走
    /// <c>LawnGrid.CanSpawnEntity</c> / <c>GetEntitySpawnStatus</c>（另一条链），不受影响。</para>
    ///
    /// <para>补丁始终挂上，是否生效在运行时按
    /// <see cref="GcFeature.UnconditionalPlace"/> + 关卡级开关判断。</para>
    /// </summary>
    internal static class BlueprintPlacementPatch
    {
        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            try
            {
                var harmony = new Harmony(Plugin.GUID + ".unconditionalplace");

                // ① 放行「放得下吗」的判定（同时让格子染成绿色）。
                var check = AccessTools.Method(typeof(PlacementDefinition), "GetPlaceError",
                    new[] { typeof(LawnGrid), typeof(EntityDefinition) });
                if (check != null)
                {
                    harmony.Patch(check, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(BlueprintPlacementPatch), nameof(CheckPrefix))));
                }
                else
                {
                    Plugin.Log?.LogError("[UncondPlace] 找不到 PlacementDefinition.GetPlaceError，"
                        + "无条件放置只会放行检查、不会真的生成实体");
                }

                // ② 真的把实体生成出来（否则「扣了费没出东西」）。
                var place = AccessTools.Method(typeof(PlacementDefinition), "PlaceEntity",
                    new[] { typeof(LawnGrid), typeof(EntityDefinition), typeof(PlaceParams) });
                if (place != null)
                {
                    harmony.Patch(place, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(BlueprintPlacementPatch), nameof(PlacePrefix))));
                }
                else
                {
                    Plugin.Log?.LogError("[UncondPlace] 找不到 PlacementDefinition.PlaceEntity，"
                        + "无条件放置不会真的生成实体");
                }

                Plugin.Log?.LogInfo("[UncondPlace] 已挂放置补丁（判定放行 + 自行生成实体，含紫卡）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[UncondPlace] 挂补丁失败：" + e);
            }
        }

        /// <summary>现在该不该放行？（等级 + 关卡级开关）</summary>
        private static bool Active(LawnGrid grid)
        {
            if (!CompatConfig.IsEnabled(GcFeature.UnconditionalPlace))
                return false;
            var level = grid == null ? null : grid.Level;
            return level != null && BlueprintFlags.GetUnconditionalPlace(level);
        }

        /// <summary>返回 false = 跳过原方法，<c>error</c> 留空（= 当作「放得下」）。</summary>
        private static bool CheckPrefix(LawnGrid grid, ref NamespaceID __result)
        {
            try
            {
                if (!Active(grid))
                    return true;
                __result = null;
                return false;
            }
            catch (Exception)
            {
                return true;   // 判不出来就交给原版
            }
        }

        /// <summary>
        /// 返回 false = 跳过原方法，由我们直接把实体生成出来。
        ///
        /// <para>照抄官方 <c>EntityPlaceMethod.PlaceEntity</c>：模仿者先生成一个命令方块
        /// （它过一会儿自己变成真器械），否则直接生成种子对应的实体。
        /// 位置换算 / 占用格子 / 放音效全在 <c>LawnGrid.SpawnPlacedEntity</c> 里，观感与正常放置一致。</para>
        /// </summary>
        private static bool PlacePrefix(LawnGrid grid, EntityDefinition entity, PlaceParams param,
            ref PlaceOutput __result)
        {
            try
            {
                if (!Active(grid) || entity == null)
                    return true;

                var commandBlock = param != null && param.IsCommandBlock();
                var variant = param == null ? 0 : param.GetVariant();
                Entity spawned;
                if (commandBlock)
                {
                    var cbParams = CommandBlock.GetImitateSpawnParams(entity.GetID());
                    cbParams.SetProperty(LogicEntityProps.VARIANT, variant);
                    spawned = grid.SpawnPlacedEntity(VanillaContraptionID.commandBlock, cbParams);
                }
                else
                {
                    var spawnParams = new SpawnParams();
                    spawnParams.SetProperty(LogicEntityProps.VARIANT, variant);
                    spawned = grid.SpawnPlacedEntity(entity.GetID(), spawnParams);
                }

                __result = new PlaceOutput(spawned, entity)
                {
                    isCommandBlock = commandBlock,
                    increaseTakenConveyorSeed = true,
                };
                return false;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("uncondplace", "[UncondPlace] 自行生成失败，交回官方："
                    + e.GetType().Name + ": " + e.Message);
                return true;
            }
        }
    }
}
