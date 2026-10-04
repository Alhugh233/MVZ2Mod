using System;
using System.Collections.Generic;
using HarmonyLib;
using MVZ2.GameContent.Buffs;
using MVZ2Logic.Blueprints;
using PVZEngine;
using PVZEngine.Buffs;
using PVZEngine.Definitions;
using PVZEngine.Entities;
using PVZEngine.Level;

namespace Loadout
{
    /// <summary>
    /// 蓝图的两条**按卡**的生成期改动（都只作用于被标记的那一张卡放出来的实体）：
    /// <list type="number">
    /// <item><b>血量</b>：生成后把 <c>MAX_HEALTH</c> 顶成面板设的数值，
    /// 并把**当前血量**一起顶到新上限（需求：「之后新放置的器械按这个血量生成，当前血量也顶到新上限」）；
    /// 引擎自己在 <c>MAX_HEALTH</c> 变化时是按比例缩放当前血量的
    /// （<c>Entity_Properties</c> 里 <c>Health = min(after, Health * after / before)</c>），
    /// 这里再显式写一遍是为了「刚出生就是满血」这件事确定无疑。</item>
    /// <item><b>白天不睡觉</b>：把刚中的 <c>NocturnalBuff</c> 摘掉。
    /// 「夜用」器械白天打瞌睡就是它（<c>ContraptionCommonBehaviour.Init</c> 里
    /// <c>IsNocturnal() &amp;&amp; Level.IsDay()</c> 时挂的那个 buff，带 <c>AI_FROZEN</c>）。</item>
    /// </list>
    ///
    /// <para><b>补丁点</b>：生成漏斗 <c>LevelEngine.SpawnSourced</c> 的 postfix。
    /// 但「这次生成是哪张卡放出来的」不在这里判断 —— 那是
    /// <see cref="PlacementScope"/> 的活（在放置调用上开作用域）。这里只问一句
    /// 「这个刚生成的实体对应哪张卡」，问到就按 <see cref="BlueprintMarks"/> 应用。</para>
    ///
    /// <para>⚠ <b>旧版本的 bug</b>：以前是「蓝图 id → 种子实体 id」建命中表，
    /// 于是**自然生成**的同种实体（出怪的僵尸）也被改了血量 —— 两条路在生成漏斗里
    /// 都是 <c>source == null</c>，分不出来。现在只认放置作用域，自然生成永远不命中。</para>
    /// </summary>
    internal static class BlueprintSpawnPatch
    {
        private static bool applied;

        private static bool healthLogged;
        private static bool wakeLogged;

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
                    Plugin.Log?.LogError("[BlueprintSpawn] 找不到 LevelEngine.SpawnSourced，血量/白天不睡觉不可用");
                    return;
                }
                var harmony = new Harmony(Plugin.GUID + ".blueprintspawn");
                harmony.Patch(target, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(BlueprintSpawnPatch), nameof(Postfix))));
                Plugin.Log?.LogInfo("[BlueprintSpawn] 已挂生成漏斗补丁（血量 / 白天不睡觉）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[BlueprintSpawn] 挂补丁失败：" + e);
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
                if (!CompatConfig.IsEnabled(GcFeature.BlueprintOverrides))
                    return;

                var level = __result.Level;
                if (level == null)
                    return;

                string key;
                if (!PlacementScope.Resolve(__result, source, out key))
                    return;

                var mark = BlueprintMarks.Get(level, key);
                if (!mark.Has)
                    return;

                if (mark.Health > 0f)
                    ApplyHealth(__result, mark.Health, key);
                if (mark.NoDaySleep)
                    WakeUp(__result);
            }
            catch (Exception)
            {
                // 原生调用栈上不能抛。
            }
        }

        private static void ApplyHealth(Entity entity, float hp, string key)
        {
            if (hp <= 0f)
                return;
            try
            {
                entity.SetProperty(EngineEntityProps.MAX_HEALTH, hp);
                entity.Health = hp;
                if (!healthLogged)
                {
                    healthLogged = true;
                    Plugin.Log?.LogInfo("[BlueprintSpawn] 已把 " + entity.GetDefinitionID()
                        + " 的血量顶到 " + hp.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                        + "（来自 " + BlueprintKeys.Describe(key) + "，自然生成的同种实体不受影响）");
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 摘掉「夜用」睡眠 buff。
        ///
        /// <para>⚠ 必须用 <c>new IBuffTarget(entity.Pointer)</c> 包一层：代理程序集里
        /// <c>Entity</c> **看不到** <c>IBuffTarget</c> 的成员（<c>Buffs</c> 与那些扩展方法）——
        /// 这是本仓库在诅咒那边已经踩过并记下的一条。按 **id** 删就不用碰泛型方法。</para>
        /// </summary>
        private static void WakeUp(Entity entity)
        {
            try
            {
                var target = new IBuffTarget(entity.Pointer);
                var id = VanillaBuffID.Contraption.nocturnal;
                if (!target.Buffs.HasBuff(id))
                    return;
                var removed = target.RemoveBuffs(id);
                if (removed > 0 && !wakeLogged)
                {
                    wakeLogged = true;
                    Plugin.Log?.LogInfo("[BlueprintSpawn] 已唤醒 " + entity.GetDefinitionID()
                        + "（摘掉 nocturnal，共 " + removed + " 个）");
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 自测用：把某个实体身上「会被标记影响的」几项打出来
        /// （血量上限 / 当前血量 / 阵营 / 睡眠 buff）—— 放置探针靠它对比三条路。
        /// </summary>
        public static string DescribeEntity(Entity e)
        {
            if (e == null)
                return "（没生成出来）";
            try
            {
                var hp = e.GetProperty<float>(EngineEntityProps.MAX_HEALTH);
                var faction = e.GetProperty<int>(EngineEntityProps.FACTION);
                var asleep = 0;
                try
                {
                    var target = new IBuffTarget(e.Pointer);
                    asleep = target.Buffs.HasBuff(VanillaBuffID.Contraption.nocturnal) ? 1 : 0;
                }
                catch (Exception) { asleep = -1; }
                return "血量上限=" + hp.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + " 当前=" + e.Health.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                    + " 阵营=" + faction + " 睡眠buff=" + asleep;
            }
            catch (Exception ex)
            {
                return "（读不出来 " + ex.GetType().Name + "）";
            }
        }

        /// <summary>
        /// 自测用：强制重算速查表并把它打出来。
        ///
        /// <para>验的是**最危险的那一步** ——「面板里按卡记的标记」能不能正确变成
        /// 生成时要查的那张表。真到生成那一刻才做的话，出问题就只能在实战里发现。</para>
        /// </summary>
        public static string DebugMarkTable(LevelEngine level)
        {
            try
            {
                BlueprintMarks.Refresh();
                BlueprintMarks.Get(level, "__probe__");   // 触发重算
                var sb = new System.Text.StringBuilder(300);
                sb.Append("速查表 ").Append(BlueprintMarks.Count).Append(" 条");
                foreach (var pair in BlueprintFlags.HealthMap(level))
                    sb.Append(" [").Append(BlueprintKeys.Describe(pair.Key)).Append(" 血量=")
                      .Append(pair.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append(']');
                var sleep = BlueprintFlags.NoDaySleepTargets(level);
                if (sleep.Count > 0)
                {
                    sb.Append(" 不睡觉:");
                    for (var i = 0; i < sleep.Count; i++)
                        sb.Append(' ').Append(BlueprintKeys.Describe(sleep[i]));
                }
                sb.Append("  可用=").Append(!CompatConfig.IsEnabled(GcFeature.BlueprintOverrides) ? "否（等级不足）" : "是");
                return sb.ToString();
            }
            catch (Exception e)
            {
                return "速查表：dump 失败 " + e.GetType().Name;
            }
        }
    }
}
