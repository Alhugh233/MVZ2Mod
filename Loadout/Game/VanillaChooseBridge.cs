using System;
using MVZ2.Blueprints;
using MVZ2.Level.Blueprints;
using PVZEngine;
using PVZEngine.Level;
using UnityEngine;

namespace Loadout
{
    /// <summary>
    /// 与**原版蓝图显示**互通的桥 —— 面板改完一张卡之后，让原版那几处显示跟着重画。
    ///
    /// <para>要照看的有<b>两处</b>，它们的刷新机制完全不同：</para>
    ///
    /// <list type="number">
    /// <item><b>选卡阶段</b>（<c>ChosenBlueprintController</c>）：它不看 <c>SeedPack</c>，
    /// 而是自己存了一个 <c>CommandBlock</c> 字段 —— 那是 <c>CreateChosenBlueprint</c> 时从
    /// <c>BlueprintChooseItem.isCommandBlock</c> 拷进来的。所以只改 pack 属性它不会变，
    /// 必须**显式改字段**再 <c>UpdateView()</c>。</item>
    ///
    /// <item><b>关卡 HUD 那一排</b>（<c>ClassicBlueprintController</c> / <c>ConveyorBlueprintController</c>）：
    /// 它的 <c>GetBlueprintViewData()</c> 走的是 <c>ResourceManager.GetBlueprintViewData(SeedPack)</c>，
    /// 数据是活的 —— 但**重画只在 <c>SeedPack.OnDefinitionChanged</c> 时发生**，
    /// 而我们改的是 pack 上的**属性**（模仿者 / 费用 / 冷却），不触发那个事件。
    /// 于是症状就是「镜像框里白了、下面那一排还是原样」—— 必须我们自己调 <c>UpdateView()</c>。
    /// （<c>Blueprint.UpdateView</c> 只改图标 / 费用 / 空态，**不碰充能圈**，
    /// 所以多刷几次没有副作用。）</item>
    /// </list>
    ///
    /// <para>⚠ 自己的克隆体（镜像框里的卡）也在场景里，必须用 <see cref="DSHCore.Ui.UiHost.IsOwn"/>
    /// 排除掉 —— 它们挂着没 <c>Init</c> 过的控制器，调 <c>UpdateView()</c> 会空引用。</para>
    /// </summary>
    internal static class VanillaChooseBridge
    {
        // ------------------------------------------------------------------
        // 选卡阶段
        // ------------------------------------------------------------------

        /// <summary>
        /// 把 slot 上那张已选蓝图（id 为 <paramref name="id"/>）的模仿者状态改成
        /// <paramref name="commandBlock"/> 并重画。<returns>改到了几张。</returns>
        /// </summary>
        public static int SyncCommandBlock(int slot, NamespaceID id, bool commandBlock)
        {
            var list = FindChosen();
            if (list == null)
                return 0;
            var count = 0;
            for (var i = 0; i < list.Length; i++)
            {
                var c = list[i];
                if (c == null || DSHCore.Ui.UiHost.IsOwn(c.transform))
                    continue;
                if (!Matches(c, slot, id))
                    continue;
                try
                {
                    if (c.CommandBlock != commandBlock)
                        c.CommandBlock = commandBlock;
                    c.UpdateView();
                    count++;
                }
                catch (Exception) { }
            }
            return count;
        }

        // ------------------------------------------------------------------
        // 关卡 HUD
        // ------------------------------------------------------------------

        /// <summary>
        /// 把关卡 HUD 那一排蓝图按当前数据重画一次。
        ///
        /// <para><paramref name="slot"/> &gt;= 0 时只刷那一格（省得每改一张就把整排重画一遍）；
        /// 传 -1 刷全部。</para>
        /// </summary>
        public static int RefreshLevelBlueprints(LevelEngine level, int slot)
        {
            var done = 0;
            try
            {
                var preset = LevelFacade.Controller == null ? null : LevelFacade.Controller.GetUIPreset();
                var ui = preset == null ? null : preset.Blueprints;
                if (ui == null)
                    return 0;

                if (ui.IsConveyorMode())
                {
                    var count = BlueprintOps.GetConveyorPackCount(level);
                    for (var i = 0; i < count; i++)
                    {
                        var bp = ui.GetConveyorBlueprintAt(i);
                        if (bp == null)
                            continue;
                        try
                        {
                            var c = bp.GetComponent<ConveyorBlueprintController>();
                            if (c == null || !MatchIndex(c.Index, slot))
                                continue;
                            c.UpdateView();
                            done++;
                        }
                        catch (Exception) { }
                    }
                }
                else
                {
                    var count = BlueprintOps.GetSlotCount(level);
                    for (var i = 0; i < count; i++)
                    {
                        var bp = ui.GetClassicBlueprintAt(i);
                        if (bp == null)
                            continue;
                        try
                        {
                            var c = bp.GetComponent<ClassicBlueprintController>();
                            if (c == null || !MatchIndex(c.Index, slot))
                                continue;
                            c.UpdateView();
                            done++;
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("vbridge-hud", "[Blueprints] 重画关卡 HUD 蓝图失败："
                    + e.GetType().Name + ": " + e.Message);
            }
            return done;
        }

        /// <summary>
        /// 面板改完一张卡之后的统一收尾：选卡阶段的字段对齐 + 关卡 HUD 重画。
        /// <paramref name="slot"/> &lt; 0 = 全部。
        /// </summary>
        public static string RefreshAll(LevelEngine level, int slot)
        {
            if (level == null)
                return string.Empty;
            var chosen = slot >= 0
                ? SyncCommandBlock(slot, SlotId(level, slot),
                    BlueprintOps.GetCommandFlag(level, BlueprintKeys.Slot(slot)))
                : SyncAllChosen(level);
            var hud = RefreshLevelBlueprints(level, slot);
            return "原版显示已同步（选卡 " + chosen + " 张 / 关卡 " + hud + " 张）";
        }

        /// <summary>按关卡里的标记把所有已选蓝图的模仿者状态重新对一遍。</summary>
        private static int SyncAllChosen(LevelEngine level)
        {
            var list = FindChosen();
            if (list == null)
                return 0;
            var count = 0;
            for (var i = 0; i < list.Length; i++)
            {
                var c = list[i];
                if (c == null || DSHCore.Ui.UiHost.IsOwn(c.transform))
                    continue;
                try
                {
                    var want = BlueprintOps.GetCommandFlag(level, BlueprintKeys.Slot(c.Index));
                    if (c.CommandBlock != want)
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
            var list = FindChosen();
            if (list == null)
                return 0;
            var n = 0;
            for (var i = 0; i < list.Length; i++)
            {
                if (list[i] != null && !DSHCore.Ui.UiHost.IsOwn(list[i].transform))
                    n++;
            }
            return n;
        }

        /// <summary>
        /// 自测用：把关卡 HUD 那一排蓝图逐张报出来（下标 / 卡上的模仿者状态）。
        /// 判据是「遍历得到卡」+「卡上的状态与我们的标记一致」—— 遍历不到就说明
        /// 这一条路走错了（那 HUD 永远刷不了）。
        /// </summary>
        public static string Report(LevelEngine level)
        {
            var sb = new System.Text.StringBuilder(400);
            try
            {
                var preset = LevelFacade.Controller == null ? null : LevelFacade.Controller.GetUIPreset();
                var ui = preset == null ? null : preset.Blueprints;
                if (ui == null)
                    return "  HUD 蓝图：（拿不到关卡 UI 预设）";
                var conveyor = ui.IsConveyorMode();
                sb.Append("  HUD 蓝图：模式=").Append(conveyor ? "传送带" : "自选卡");
                var count = conveyor ? BlueprintOps.GetConveyorPackCount(level) : BlueprintOps.GetSlotCount(level);
                var found = 0;
                for (var i = 0; i < count; i++)
                {
                    var bp = conveyor ? ui.GetConveyorBlueprintAt(i) : ui.GetClassicBlueprintAt(i);
                    if (bp == null)
                    {
                        sb.Append("\n    #").Append(i).Append(" （空槽）");
                        continue;
                    }
                    found++;
                    var index = -1;
                    var cb = false;
                    try
                    {
                        if (conveyor)
                        {
                            var c = bp.GetComponent<ConveyorBlueprintController>();
                            if (c != null) { index = c.Index; cb = c.IsCommandBlock(); }
                        }
                        else
                        {
                            var c = bp.GetComponent<ClassicBlueprintController>();
                            if (c != null) { index = c.Index; cb = c.IsCommandBlock(); }
                        }
                    }
                    catch (Exception) { }
                    sb.Append("\n    #").Append(i).Append(" 下标=").Append(index)
                      .Append(" 模仿者=").Append(cb ? "开" : "关");
                }
                sb.Append("\n    判据 能遍历到 ").Append(found).Append(" 张卡=")
                  .Append(found > 0 ? "OK" : "NG（HUD 重画这条路走不通）");
            }
            catch (Exception e)
            {
                sb.Append("  HUD 蓝图：读取失败 ").Append(e.GetType().Name);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 自测用：证明「镜像框里改了东西 → 原版那一排蓝图**必须重画**」这件事。
        ///
        /// <para>做法是标准的 A/B：先把 HUD 上第二张卡改成「模仿者」但**故意不刷新**
        /// （此时它应该还是旧样子 —— 这就是用户看到的 bug 现场），
        /// 再走一次 <see cref="RefreshAll"/>，看卡面是不是真的变了。</para>
        /// </summary>
        public static string SyncRound(LevelEngine level)
        {
            if (level == null)
                return "（不在关卡内）";
            var sb = new System.Text.StringBuilder(700);
            try
            {
                var controller = LevelFacade.Controller;
                var preset = controller == null ? null : controller.GetUIPreset();
                var ui = preset == null ? null : preset.Blueprints;
                if (ui == null)
                    return "  HUD 蓝图：（拿不到关卡 UI 预设）";

                var conveyor = ui.IsConveyorMode();
                var count = conveyor ? BlueprintOps.GetConveyorPackCount(level) : BlueprintOps.GetSlotCount(level);
                sb.Append("  HUD 蓝图：模式=").Append(conveyor ? "传送带" : "自选卡");

                // 挑一张 HUD 上真有、而且控制器 / SeedPack 都在的卡。
                Blueprint bp = null;
                RuntimeBlueprintController c = null;
                var found = 0;
                for (var i = 0; i < count; i++)
                {
                    var node = conveyor ? ui.GetConveyorBlueprintAt(i) : ui.GetClassicBlueprintAt(i);
                    if (node == null)
                    {
                        sb.Append("\n    #").Append(i).Append(" （空槽）");
                        continue;
                    }
                    found++;
                    if (bp != null)
                        continue;
                    RuntimeBlueprintController ctl = conveyor
                        ? (RuntimeBlueprintController)node.GetComponent<ConveyorBlueprintController>()
                        : (RuntimeBlueprintController)node.GetComponent<ClassicBlueprintController>();
                    if (ctl == null || ctl.SeedPack == null)
                        continue;
                    bp = node;
                    c = ctl;
                }
                sb.Append("\n    判据 能遍历到 ").Append(found).Append(" 张卡=")
                  .Append(found > 0 ? "OK" : "NG（HUD 重画这条路走不通）");
                if (c == null)
                {
                    sb.Append("\n    （没有带 SeedPack 的卡，跳过重画验证）");
                    return sb.ToString();
                }

                var slot = c.Index;
                var key = BlueprintKeys.Slot(slot);
                var flag0 = BlueprintOps.GetCommandFlag(level, key);
                var cb0 = c.IsCommandBlock();
                var fp0 = Fingerprint(bp);

                // ① 只改状态、**不刷新** → 卡面应该一点没变（这就是用户看到的 bug 现场）。
                BlueprintOps.SetCommandBlock(c.SeedPack, !cb0);
                BlueprintOps.SetCommandFlag(level, key, !cb0);
                var fpStale = Fingerprint(bp);

                // ② 走一次我们的重画桥 → 卡面应该跟着变。
                var sync = RefreshAll(level, slot);
                var fp1 = Fingerprint(bp);

                sb.Append("\n    槽").Append(slot).Append(" 模仿者 ").Append(cb0 ? "开" : "关")
                  .Append(" → ").Append(!cb0 ? "开" : "关").Append("：").Append(sync)
                  .Append("\n      不刷新时卡面=").Append(fpStale == fp0 ? "没变（就是 bug 现场）" : "居然变了")
                  .Append("\n      重画后卡面=").Append(fp1 != fpStale ? "变了" : "还是没变")
                  .Append("\n    判据 不重画就不变=").Append(fpStale == fp0 ? "OK" : "NG")
                  .Append("  判据 重画后卡面变了=").Append(fp1 != fpStale ? "OK" : "NG（重画没生效）");

                // 还原。
                BlueprintOps.SetCommandBlock(c.SeedPack, cb0);
                BlueprintOps.SetCommandFlag(level, key, flag0);
                RefreshAll(level, slot);
                sb.Append("\n    已还原（卡面回到原样=")
                  .Append(Fingerprint(bp) == fp0 ? "是" : "否（原本就在动，比如充能倒计时）").Append("）");
            }
            catch (Exception e)
            {
                sb.Append("\n    失败：").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        /// <summary>自测用：一张卡面的「指纹」（各层的贴图/颜色/填充/缩放），用来判断有没有真的重画。</summary>
        private static string Fingerprint(Blueprint bp)
        {
            try { return BlueprintStrip.CardState(bp); }
            catch (Exception e) { return "（" + e.GetType().Name + "）"; }
        }

        // ------------------------------------------------------------------
        // 内部
        // ------------------------------------------------------------------

        private static bool MatchIndex(int index, int slot)
        {
            // slot < 0 = 全刷；下标还没设过（-1）时保守地当它匹配，重画一次总比不刷好。
            return slot < 0 || index < 0 || index == slot;
        }

        private static NamespaceID SlotId(LevelEngine level, int slot)
        {
            try
            {
                var pack = BlueprintOps.GetPackAt(level, slot);
                return pack == null ? null : pack.GetDefinitionID();
            }
            catch (Exception) { return null; }
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

        private static ChosenBlueprintController[] FindChosen()
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

