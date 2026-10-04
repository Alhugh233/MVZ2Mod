using System;
using MVZ2.Level.Blueprints;
using PVZEngine;
using UnityEngine;

namespace Loadout
{
    /// <summary>
    /// 与**原版选卡界面**互通的桥。
    ///
    /// <para><b>为什么需要它</b>：本 mod 的「模仿者」标记是写在 <c>SeedPack</c> 属性上的
    /// （<c>BlueprintOps.SetCommandBlock</c>），战斗里那张卡（<c>RuntimeBlueprintController</c>）
    /// 的 <c>IsCommandBlock()</c> 正是读这个属性，所以会照常变白。
    /// 但**选卡阶段**用的不是 SeedPack，而是每个已选卡各自的
    /// <c>ChosenBlueprintController.CommandBlock</c> 字段（在 <c>CreateChosenBlueprint</c> 时
    /// 从 <c>BlueprintChooseItem.isCommandBlock</c> 拷进去的）—— 面板改 pack 它不会知道，
    /// 于是出现「镜像框里白了、原版选卡处还是原样」。</para>
    ///
    /// <para>这里就在改标记之后把原版那几个控制器一起改掉并重画。
    /// 匹配方式：先按**卡槽下标**（<c>Controller.Index</c>），下标对不上再按蓝图 id
    /// —— 面板允许「卡槽顺序」和选卡顺序不一致，两个条件都试才稳。</para>
    /// </summary>
    internal static class VanillaChooseBridge
    {
        /// <summary>
        /// 把 slot 上那张已选蓝图（id 为 <paramref name="id"/>）的模仿者状态改成
        /// <paramref name="commandBlock"/> 并重画。<returns>改到了几张。</returns>
        /// </summary>
        public static int SyncCommandBlock(int slot, NamespaceID id, bool commandBlock)
        {
            var list = Find();
            if (list == null)
                return 0;
            var count = 0;
            for (var i = 0; i < list.Length; i++)
            {
                var c = list[i];
                if (c == null)
                    continue;
                if (!Matches(c, slot, id))
                    continue;
                try
                {
                    if (c.CommandBlock == commandBlock)
                        continue;
                    c.CommandBlock = commandBlock;
                    c.UpdateView();
                    count++;
                }
                catch (Exception) { }
            }
            return count;
        }

        /// <summary>把所有已选蓝图按「卡槽 → 标记」重新对一遍（清空卡槽 / 调整选卡之后用）。</summary>
        public static int SyncAll(Func<int, NamespaceID, bool> wanted)
        {
            if (wanted == null)
                return 0;
            var list = Find();
            if (list == null)
                return 0;
            var count = 0;
            for (var i = 0; i < list.Length; i++)
            {
                var c = list[i];
                if (c == null)
                    continue;
                try
                {
                    var want = wanted(c.Index, c.GetDefinitionID());
                    if (c.CommandBlock == want)
                        continue;
                    c.CommandBlock = want;
                    c.UpdateView();
                    count++;
                }
                catch (Exception) { }
            }
            return count;
        }

        /// <summary>当前原版选卡界面上有几张已选蓝图（0 = 不在选卡阶段）。</summary>
        public static int Count()
        {
            var list = Find();
            if (list == null)
                return 0;
            var n = 0;
            for (var i = 0; i < list.Length; i++)
            {
                if (list[i] != null)
                    n++;
            }
            return n;
        }

        private static bool Matches(ChosenBlueprintController c, int slot, NamespaceID id)
        {
            try
            {
                if (slot >= 0 && c.Index == slot)
                    return true;
                return NamespaceID.IsValid(id) && c.GetDefinitionID() == id;
            }
            catch (Exception) { return false; }
        }

        private static ChosenBlueprintController[] Find()
        {
            try
            {
                // 选卡界面出来的时候这些卡都是激活的，FindObjectsOfType 够用（它跳过未激活的）。
                return UnityEngine.Object.FindObjectsOfType<ChosenBlueprintController>();
            }
            catch (Exception) { return null; }
        }
    }
}
