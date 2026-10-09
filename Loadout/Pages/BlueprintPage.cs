using System;
using System.Collections.Generic;
using DSHCore.Ui;
using MVZ2.UI;
using PVZEngine;
using PVZEngine.Level;
using PVZEngine.SeedPacks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 关卡内 · 页「蓝图」（**自选卡模式**）。
    ///
    /// <para>第一行是**卡组镜像**：一个灰色内容框，里面从左到右把这一关卡槽里的蓝图摆出来
    /// （框体与卡片全部来自原版选卡面板，数据与蓝图掉落物同一个 <c>BlueprintViewData</c> 入口），
    /// 卡片多了可以**用滚轮横向滚**。没有卡槽时框体自己就是那个空框。
    ///
    /// <para>点卡片即**选中**（单选）：被选中的卡按原版语义**灰化**（<c>SetDisabled</c>，
    /// 与原版选卡面板里「已选的卡」同一种表现），下面的字段会**填上它的当前值**
    /// 并作用在选中的那张卡上；一个都没选时作用在**全部卡**上。</para>
    /// </summary>
    internal sealed class BlueprintPage : IPanelPage
    {
        public string Name { get { return "蓝图"; } }

        private BlueprintStrip _strip;
        private UiField _cost, _cooldown, _health;
        private UiOptionRow _presetRow, _resetRow;
        private UiOptionRow _commandBlockRow, _reverseRow, _daySleepRow, _placeRow;
        private UiOptionRow _clearRow;
        /// <summary>第四行的「传送带模式」（要按兼容等级置灰）。</summary>
        private UiOptionRow _conveyorRow;
        private bool _built;
        private bool _refreshing;

        /// <summary>被选中的卡槽下标（**单选**：-1 = 没选）。</summary>
        private int _selected = -1;

        /// <summary>自测用：两条通路的调用次数（证明点选真的走的是每帧那条）。</summary>
        private static int _frameCalls;
        private static int _tickCalls;

        private static BlueprintPickWindow _picker;
        private static BlueprintPage _last;

        /// <summary>自测用：打开「调整选卡」并回一段矩形报告。</summary>
        public static string DebugOpenPicker()
        {
            if (_last == null || !_last._built)
                return "（蓝图页未装配）";
            _last.OpenPicker();
            var picker = _picker;
            if (picker == null || !picker.IsOpen)
                return "（选卡浮层打不开）";
            return picker.DebugRects();
        }

        /// <summary>
        /// 自测用：把选卡浮层的交互跑一遍 —— 同一种蓝图选两张、翻到后面几页再选、点已选框放回。
        /// 这三件都是需求里点名要的行为，而它们没法用布局 dump 验。
        /// </summary>
        public static string DebugPickInteract()
        {
            var picker = _picker;
            if (picker == null || !picker.IsOpen)
                return "（选卡浮层未打开）";
            var sb = new System.Text.StringBuilder(500);
            sb.Append("  交互自检 开始：").Append(picker.Describe())
              .Append("  按钮=").Append(picker.ButtonLabels()).Append('\n');
            sb.Append("    ").Append(picker.DebugReopen()).Append('\n');
            sb.Append("    ").Append(picker.DebugClickGrid(0)).Append('\n');
            sb.Append("    ").Append(picker.DebugClickGrid(0)).Append('\n');
            sb.Append("    ").Append(picker.DebugGotoPage(2)).Append('\n');
            sb.Append("    ").Append(picker.DebugClickGrid(1)).Append('\n');
            sb.Append("    ").Append(picker.DebugClickChosen(0)).Append('\n');
            sb.Append("  交互自检 结束：").Append(picker.Describe())
              .Append("  按钮=").Append(picker.ButtonLabels()).Append('\n');
            sb.Append("  ").Append(picker.DebugButtons()).Append('\n');
            return sb.ToString();
        }

        /// <summary>自测用：同一个蓝图在「已选框」与「网格」里的图标对比。</summary>
        public static string DebugCompareIcon()
        {
            var picker = _picker;
            if (picker == null || !picker.IsOpen)
                return "（选卡浮层未打开）";
            return picker.DebugCompareIcon();
        }

        /// <summary>自测用：把镜像卡与网格卡的节点树对比打出来（「网格卡多出来的东西」）。</summary>
        public static string DebugTrees()
        {
            var picker = _picker;
            if (picker == null || !picker.IsOpen)
                return "（选卡浮层未打开）";
            return "  已选框#0 图标：\n    " + picker.DebugChosenIcon(0).Replace("\n", "\n    ")
                + "\n  网格卡#0 图标：\n    " + picker.DebugGridIcon(0).Replace("\n", "\n    ")
                + "\n  网格卡#1 图标：\n    " + picker.DebugGridIcon(1).Replace("\n", "\n    ");
        }

        /// <summary>自测用：给已选框前几张卡套上不同的灰化组合（量像素看哪组才是亮的）。</summary>
        public static string DebugVariants()
        {
            var picker = _picker;
            if (picker == null || !picker.IsOpen)
                return "（选卡浮层未打开）";
            return picker.DebugVariants();
        }

        /// <summary>
        /// 自测用：点镜像行第 <paramref name="slot"/> 格两次（单选取 / 取消），
        /// 并把每格的卡状态贴出来 —— 「只有被选中那格置灰」只能这样验。
        /// </summary>
        public static string DebugPickSlot(int slot)
        {
            if (_last == null || !_last._built)
                return "（蓝图页未装配）";
            var strip = _last._strip;
            if (strip == null)
                return "（没有镜像框）";
            var sb = new System.Text.StringBuilder(400);
            _last.TogglePick(slot);
            sb.Append("点镜像第 ").Append(slot + 1).Append(" 格 → ").Append(_last.Report()).Append('\n');
            var n = Mathf.Min(strip.Count, 3);
            for (var i = 0; i < n; i++)
                sb.Append("    格").Append(i + 1).Append("：").Append(strip.CardStateAt(i)).Append('\n');
            _last.TogglePick(slot);
            sb.Append("再点一次 → ").Append(_last.Report());
            return sb.ToString();
        }

        /// <summary>
        /// 自测用：给第 <paramref name="slot"/> 格标上血量与「白天不睡觉」，打一份生成期速查表
        /// （验「按卡记的标记」能不能正确落到生成时要查的那张表），随后**还原**。
        ///
        /// <para>顺带验「同一格」与「另一格」互不干扰 —— 这正是本轮的修复点。</para>
        /// </summary>
        public static string DebugFlagMap(int slot)
        {
            if (_last == null || !_last._built)
                return "（蓝图页未装配）";
            var level = LevelFacade.Current;
            if (level == null)
                return "（不在关卡内）";
            var pack = BlueprintOps.GetPackAt(level, slot);
            if (pack == null)
                return "（第 " + slot + " 格没有卡）";
            var id = pack.GetDefinitionID();
            var key = BlueprintKeys.Slot(slot);
            var sb = new System.Text.StringBuilder(300);
            sb.Append("标记第 ").Append(slot + 1).Append(" 格（")
              .Append(BlueprintOps.BlueprintName(id, false)).Append("，键 ").Append(key).Append("）：");
            BlueprintOps.SetHealthOverride(level, key, 12345f);
            BlueprintOps.SetNoDaySleep(level, key, true);
            sb.Append('\n').Append("    ").Append(BlueprintSpawnPatch.DebugMarkTable(level));
            sb.Append('\n').Append("    键解析：")
              .Append("这一格 → ").Append(BlueprintKeys.Describe(BlueprintKeys.OfPack(level, pack)))
              .Append("；另一格 → ").Append(BlueprintKeys.Describe(BlueprintKeys.Slot(slot + 1)));
            // 还原（免得把自测的标记留在关卡里）。
            BlueprintOps.SetHealthOverride(level, key, -1f);
            BlueprintOps.SetNoDaySleep(level, key, false);
            sb.Append('\n').Append("    还原后：").Append(BlueprintSpawnPatch.DebugMarkTable(level));
            return sb.ToString();
        }

        /// <summary>自测用：网格的卡到底挂在哪个节点下（不在我们的克隆里 → 点不中）。</summary>
        public static string DebugGridWhere()
        {
            var picker = _picker;
            if (picker == null || !picker.IsOpen)
                return "（选卡浮层未打开）";
            return picker.DebugGridWhere();
        }

        /// <summary>自测用：把真实点击完整走一遍（能否命中、能否起飞）。</summary>
        public static string DebugClickAt(int cell)
        {
            var picker = _picker;
            if (picker == null || !picker.IsOpen)
                return "（选卡浮层未打开）";
            return picker.DebugClickAt(cell);
        }

        /// <summary>自测用：镜像框里的卡都挂上提示了吗。</summary>
        public static string DebugMirrorTooltips()
        {
            if (_last == null || !_last._built || _last._strip == null)
                return "（蓝图页未装配）";
            var sb = new System.Text.StringBuilder(300);
            sb.Append(_last._strip.DescribeTooltip(0));
            for (var i = 1; i < 2 && i < _last._strip.Count; i++)
                sb.Append("\n    ").Append(_last._strip.DescribeTooltip(i));
            return sb.ToString();
        }

        /// <summary>
        /// 自测用：把某一格的「逆阵营放置」打开，看镜像框里那张卡**是不是左右翻转**
        /// （<c>Icon</c> 的 <c>localScale.x = -1</c>）而不是被染红。验完还原。
        /// </summary>
        public static string DebugMirror()
        {
            if (_last == null || !_last._built || _last._strip == null)
                return "（蓝图页未装配）";
            var level = LevelFacade.Current;
            if (level == null)
                return "（不在关卡内）";
            var count = BlueprintOps.GetSlotCount(level);
            var slot = -1;
            for (var i = 0; i < count; i++)
            {
                if (BlueprintOps.GetPackAt(level, i) != null)
                {
                    slot = i;
                    break;
                }
            }
            if (slot < 0)
                return "（卡槽是空的）";
            var key = BlueprintKeys.Slot(slot);
            var before = BlueprintOps.GetReverseFaction(level, key);
            var sb = new System.Text.StringBuilder(400);
            try
            {
                BlueprintOps.SetReverseFaction(level, key, true);
                _last.Refresh();
                sb.Append("逆阵营=开（").Append(key).Append("）→ 镜像框第 ")
                  .Append(slot).Append(" 格：\n      ")
                  .Append(_last._strip.MirrorStateAt(slot));
                BlueprintOps.SetReverseFaction(level, key, before);
                _last.Refresh();
                sb.Append("\n     判据 已还原=").Append(before ? "开" : "关");
            }
            catch (Exception e)
            {
                sb.Append("失败：").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        /// <summary>自测用：网格当前页的卡都挂上提示了吗。</summary>
        public static string DebugTooltips()
        {
            var picker = _picker;
            if (picker == null || !picker.IsOpen)
                return "（选卡浮层未打开）";
            return picker.DebugTooltips();
        }

        /// <summary>自测用：浮层网格**每一页**的提示覆盖（验「翻页不用先选卡也有提示」）。</summary>
        public static string DebugTooltipCoverage()
        {
            var picker = _picker;
            if (picker == null || !picker.IsOpen)
                return "（选卡浮层未打开）";
            return picker.DebugTooltipCoverage();
        }

        /// <summary>自测用：把选中项设成 <paramref name="slot"/>（-1 = 取消）并刷新（验置灰）。</summary>
        public static string DebugSelect(int slot)
        {
            if (_last == null || !_last._built)
                return "（蓝图页未装配）";
            _last._selected = slot;
            _last.Refresh();
            return "选中=" + (slot < 0 ? "无" : ("槽" + slot)) + "\n" + DebugGate();
        }

        /// <summary>自测用：网格里当前页的卡还在不在。</summary>
        public static string DebugGridAlive()
        {
            var picker = _picker;
            if (picker == null || !picker.IsOpen)
                return "（选卡浮层未打开）";
            return picker.DebugGridAlive();
        }

        /// <summary>
        /// 自测用：点选其实走的是哪条通路。
        /// <c>Input.GetMouseButtonDown(0)</c> 只在一帧为真，而 <c>Tick</c> 是 30 帧一次的轮询 ——
        /// 放在 <c>Tick</c> 里就会「要连点几十下才中一次」，所以点选必须走 <c>Frame</c>。
        /// </summary>
        public static string DebugClickPath()
        {
            return "点击通路：Frame（每帧）=" + _frameCalls + "  Tick（30 帧一次）=" + _tickCalls;
        }

        /// <summary>自测用：过几帧之后再看一眼浮层（按钮到底显出来了没有）。</summary>
        public static string DebugPickState()
        {
            var picker = _picker;
            if (picker == null || !picker.IsOpen)
                return "（选卡浮层未打开）";
            return "  等几帧后：" + picker.DebugState();
        }

        public void Build()
        {
            _last = this;
            _strip = null;
            _cost = _cooldown = _health = null;
            _presetRow = _resetRow = null;
            _commandBlockRow = _reverseRow = _daySleepRow = _placeRow = null;
            _clearRow = null;
            _conveyorRow = null;
            _selected = -1;
            _built = false;

            var level = LevelFacade.Current;
            if (level == null)
            {
                AddNote("本页只在关卡内可用（当前不在关卡内）。");
                return;
            }

            // ---- 第一行：卡组镜像（内容框 + 横向滚动，可点选）----
            var row1 = UiGridRow.Create("Row_BlueprintMirror", equalize: false);
            if (row1 != null)
            {
                _strip = BlueprintStrip.Create(row1.Root.transform, "Mirror");
                if (_strip != null)
                {
                    row1.AddCell(_strip.Root, 76f);
                    _strip.FillRow();   // AddCell 会把 ignoreLayout 设回 false，这里再抢回来
                }
                LoadoutPanel.AddRow(row1.FinishFixed(76f));
            }
            if (_strip == null)
                AddNote("拿不到原版选卡面板的卡片素材（关卡 UI 还没起来？），镜像行不可用。");
            Refresh();

            // ---- 第二行：费用 / 冷却(秒) / 冷却预设 / 重置冷却 ----
            var row2 = UiGridRow.Create("Row_BlueprintFields");
            if (row2 != null)
            {
                _cost = row2.AddField("F_Cost", "费用", numeric: true, integer: true);
                if (_cost != null)
                {
                    _cost.OnSubmit(OnCostSubmitted);
                    LoadoutPanel.AddTooltip(_cost.Root, "费用", "这张卡的能耗。\n只对选中的那一张生效。");
                }
                _cooldown = row2.AddField("F_Cooldown", "冷却(秒)", numeric: true);
                if (_cooldown != null)
                {
                    _cooldown.OnSubmit(OnCooldownSubmitted);
                    LoadoutPanel.AddTooltip(_cooldown.Root, "冷却(秒)",
                        "战斗中这张卡的冷却时间（上限）。\n0 = 无冷却。");
                }
                _presetRow = row2.AddOptionRow("B_RechargePreset", UiOptionKind.Button);
                if (_presetRow != null)
                {
                    _presetRow.SetButton("冷却预设", CyclePreset);
                    LoadoutPanel.AddTooltip(_presetRow.Root, "冷却预设",
                        "点一下切到下一个预设：无 / 短 / 长 / 很长。\n自己填过冷却时会显示「未定义」。");
                }
                _resetRow = row2.AddOptionRow("B_ResetCooldown", UiOptionKind.Button);
                if (_resetRow != null)
                {
                    _resetRow.SetButton("重置冷却", ResetCooldown);
                    LoadoutPanel.AddTooltip(_resetRow.Root, "重置冷却",
                        "把这张卡当前的冷却清空（立刻就绪），并把预设恢复成原版初始。");
                }
                LoadoutPanel.AddRow(row2.Finish());
            }

            // ---- 第三行：血量 / 模仿者 / 逆阵营放置 / 白天不睡觉 ----
            var row3 = UiGridRow.Create("Row_BlueprintFlags");
            if (row3 != null)
            {
                _health = row3.AddField("F_Health", "血量", numeric: true);
                if (_health != null)
                {
                    _health.OnSubmit(OnHealthSubmitted);
                    LoadoutPanel.AddTooltip(_health.Root, "血量",
                        "这张卡放出来的器械的血量上限。\n显示的是这张卡当前的值。");
                }
                _commandBlockRow = row3.AddOptionRow("T_CommandBlock", UiOptionKind.Toggle);
                if (_commandBlockRow != null)
                {
                    _commandBlockRow.SetToggle("模仿者", false, v => SetFlag(v, true));
                    LoadoutPanel.AddTooltip(_commandBlockRow.Root, "模仿者",
                        "用命令方块模仿这张卡：放置时先出命令方块，稍后才变成真正的器械。\n"
                        + "开启后卡片会变白，选卡界面与战斗界面都会同步。");
                }
                _reverseRow = row3.AddOptionRow("T_ReverseFaction", UiOptionKind.Toggle);
                if (_reverseRow != null)
                {
                    _reverseRow.SetToggle("逆阵营放置", false, v => SetFlag(v, false));
                    LoadoutPanel.AddTooltip(_reverseRow.Root, "逆阵营放置",
                        "这张卡放出来的器械属于右阵营（敌我互调）。\n需要兼容性等级 Full。");
                }
                _daySleepRow = row3.AddOptionRow("T_NoDaySleep", UiOptionKind.Toggle);
                if (_daySleepRow != null)
                {
                    _daySleepRow.SetToggle("白天不睡觉", false, v => SetDaySleep(v));
                    LoadoutPanel.AddTooltip(_daySleepRow.Root, "白天不睡觉",
                        "让「夜用」器械在白天也不睡觉（不进睡眠状态）。");
                }
                LoadoutPanel.AddRow(row3.Finish());
            }

            // ---- 第四行：无条件放置 / 调整选卡 / 清空卡槽 / 传送带模式 ----
            var row4 = UiGridRow.Create("Row_BlueprintActions");
            if (row4 != null)
            {
                _placeRow = row4.AddOptionRow("T_UnconditionalPlace", UiOptionKind.Toggle);
                if (_placeRow != null)
                {
                    _placeRow.SetToggle("无条件放置", false, v => SetUnconditionalPlace(v));
                    LoadoutPanel.AddTooltip(_placeRow.Root, "无条件放置",
                        "忽略放置条件（地形 / 已占用 / 需蒋叶等），随处可放。");
                }
                var pick = row4.AddOptionRow("B_Adjust", UiOptionKind.Button);
                if (pick != null)
                {
                    pick.SetButton("调整选卡", OpenPicker);
                    LoadoutPanel.AddTooltip(pick.Root, "调整选卡", "打开选卡面板，从全部蓝图里挑卡放进卡槽。");
                }
                _clearRow = row4.AddOptionRow("B_ClearSlots", UiOptionKind.Button);
                if (_clearRow != null)
                {
                    _clearRow.SetButton("清空卡槽", ClearSlots);
                    LoadoutPanel.AddTooltip(_clearRow.Root, "清空卡槽",
                        "把卡槽里的卡全部清掉，连带各格的血量/阵营等标记一起清。");
                }
                var conveyor = row4.AddOptionRow("B_Conveyor", UiOptionKind.Button);
                _conveyorRow = conveyor;
                if (conveyor != null)
                {
                    conveyor.SetButton("传送带模式", () =>
                    {
                        var lv = LevelFacade.Current;
                        if (lv != null)
                            ConveyorRig.Enable(lv);   // 切模式 + **加一个传送带行为**（否则不出卡）
                        // 点完要**立刻**把页面切到传送带那一套（页名不变 → 停在蓝图页）。
                        LoadoutPanel.SyncPageSet();
                        LoadoutPanel.Invalidate();
                    });
                    LoadoutPanel.AddTooltip(conveyor.Root, "传送带模式",
                        "把这一关切成传送带，并给它加上原版的传送带行为（会从卡池里抽卡）。\n"
                        + "卡池内容 = 当前的卡槽，权重/数量限制在传送带页里调。");
                }
                LoadoutPanel.AddRow(row4.Finish());
            }

            _built = true;
            // ★ 必须在**所有行都建好之后**再刷一次：上面的那次 Refresh 跑在第二~四行还不存在的时候，
            //   那些 SetRowEnabled / SetFieldEnabled 全落到 null 上 —— 于是"打开本页时二、三行没置灰，
            //   点一下卡再点掉才变灰"（本轮修的就是它）。
            Refresh();
            Plugin.Log?.LogInfo("[BlueprintPage] 已装配：" + Describe());
        }

        private string Describe()
        {
            var level = LevelFacade.Current;
            return "卡槽=" + (level == null ? 0 : BlueprintOps.GetSlotCount(level))
                + " 镜像=" + (_strip == null ? "无" : _strip.Describe())
                + " 选中=" + (_selected < 0 ? "无" : _selected.ToString());
        }

        private void AddNote(string text)
        {
            var go = UiHost.CreateNode("Note", null);
            if (!UiFinder.IsAlive(go))
                return;
            var label = UiKit.Label(go.transform, "Text", text);
            if (label != null)
            {
                var le = go.GetComponent<LayoutElement>();
                if (le == null)
                    le = go.AddComponent<LayoutElement>();
                if (le != null)
                {
                    le.minHeight = -1f;
                    le.preferredHeight = 40f;
                    le.flexibleWidth = 1f;
                }
            }
            LoadoutPanel.AddRow(go);
        }

        // ------------------------------------------------------------------
        // 选中集
        // ------------------------------------------------------------------

        /// <summary>
        /// **作用目标**。
        ///
        /// <para>★ 需求：本页**只有第四行的「无条件放置」对所有蓝图生效**，其余功能
        /// （费用 / 冷却 / 重置冷却 / 血量 / 模仿者 / 逆阵营 / 白天不睡觉）
        /// **只对被选中的那一张**生效 —— 所以一张都没选时这里是**空集**（写操作落到空集上
        /// 就等于什么也不做，而不是默默改全部）。</para>
        /// </summary>
        private List<int> Targets()
        {
            var result = new List<int>();
            var level = LevelFacade.Current;
            if (level == null)
                return result;
            var count = BlueprintOps.GetSlotCount(level);
            Prune(count);
            if (_selected >= 0 && _selected < count)
                result.Add(_selected);
            return result;
        }

        /// <summary>确实选中了一张可用的卡。</summary>
        private bool HasSelection()
        {
            return Targets().Count > 0;
        }

        /// <summary>丢掉越界的选中项（卡槽数被改小 / 换过关卡之后）。</summary>
        private void Prune(int count)
        {
            if (_selected < 0 || _selected >= count)
                _selected = -1;
        }

        /// <summary>
        /// 显示用的那张卡（= 选中的那张；一张都没选就是 null）。
        /// 不再「退回第一张」：字段要改的就是选中的那张，显示别人的值会误导。
        /// </summary>
        private SeedPack DisplayTarget()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return null;
            var count = BlueprintOps.GetSlotCount(level);
            Prune(count);
            return _selected >= 0 && _selected < count ? BlueprintOps.GetPackAt(level, _selected) : null;
        }

        /// <summary>
        /// 选中的这张卡的**标记键**（<c>s:下标</c>）；没选中返回 null。
        ///
        /// <para>标记按「这一张卡」记（不是按蓝图 id）—— 同一种蓝图带多张时每张各自一套设置。</para>
        /// </summary>
        private string TargetKey()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return null;
            var count = BlueprintOps.GetSlotCount(level);
            Prune(count);
            return _selected >= 0 && _selected < count ? BlueprintKeys.Slot(_selected) : null;
        }

        /// <summary>点一张卡：单选（再点同一张就取消）。</summary>
        private void TogglePick(int slot)
        {
            _selected = _selected == slot ? -1 : slot;
            Refresh();
        }

        // ------------------------------------------------------------------
        // 启用 / 停用（没选中时除了「无条件放置」都不能改）
        // ------------------------------------------------------------------

        /// <summary>改一格输入框的可编辑状态。</summary>
        private static void SetFieldEnabled(UiField field, bool on)
        {
            var input = field == null ? null : field.Input;
            if (input == null)
                return;
            try { input.interactable = on; }
            catch (Exception) { }
        }

        /// <summary>改一个设置项（复选框 / 按钮）的可点状态。</summary>
        private static void SetRowEnabled(UiOptionRow row, bool on)
        {
            var w = row == null ? null : row.Widgets;
            if (w == null)
                return;
            try
            {
                if (w.Toggle != null && w.Toggle.Toggle != null)
                    w.Toggle.Toggle.interactable = on;
                if (w.TextButton != null && w.TextButton.Button != null)
                    w.TextButton.Button.interactable = on;
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 交互
        // ------------------------------------------------------------------

        private void OnCostSubmitted(string text)
        {
            int value;
            if (!int.TryParse((text ?? string.Empty).Trim(), out value))
            {
                Refresh();
                return;
            }
            ForEachPack(pack => BlueprintOps.SetCost(pack, value));
            SyncVanilla(TargetSlot());
            Refresh();
        }

        private void OnCooldownSubmitted(string text)
        {
            var level = LevelFacade.Current;
            float value;
            if (level == null || !TryParse(text, out value))
            {
                Refresh();
                return;
            }
            if (BlueprintOps.LevelSpeed(level) >= 1000f)
            {
                Plugin.Log?.LogWarning("[BlueprintPage] 关卡冷却速度异常（禁用冷却？），忽略自定义冷却");
                Refresh();
                return;
            }
            ForEachPack(pack => BlueprintOps.SetCooldownSeconds(level, pack, value));
            SyncVanilla(TargetSlot());
            Refresh();
        }

        // ------------------------------------------------------------------
        // 本轮新增的功能
        // ------------------------------------------------------------------

        /// <summary>没选中时字段的提示（明确告诉玩家为什么不能改）。</summary>
        private const string ScopeHint =
            "先在镜像框里点选一张蓝图。\n本页除「无条件放置」以外的功能都只对选中的那张生效。";

        /// <summary>血量：按**这一张卡**记（只影响它之后放出来的实体）。</summary>
        private void OnHealthSubmitted(string text)
        {
            var level = LevelFacade.Current;
            var key = TargetKey();
            float value;
            if (level == null || key == null || !TryParse(text, out value))
            {
                Refresh();
                return;
            }
            // 0 或负数 = 恢复原版。
            BlueprintOps.SetHealthOverride(level, key, value > 0f ? value : -1f);
            Refresh();
        }

        /// <summary>重置冷却：恢复原版默认 + 把当前冷却重设成原版初始冷却。</summary>
        private void ResetCooldown()
        {
            var level = LevelFacade.Current;
            var pack = DisplayTarget();
            if (level == null || pack == null)
            {
                Refresh();
                return;
            }
            BlueprintOps.ResetCooldown(level, pack);
            SyncVanilla(TargetSlot());
            Refresh();
        }

        /// <summary>清空卡槽：卡槽数不变、每格置空。</summary>
        private void ClearSlots()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return;
            _selected = -1;
            BlueprintOps.ClearSlots(level);
            SyncVanilla(-1);
            LoadoutPanel.Invalidate();
        }

        /// <summary>白天不睡觉：按**这一张卡**记。参数就是复选框的勾选状态（勾上 = 不睡觉）。</summary>
        private void SetDaySleep(bool noDaySleep)
        {
            if (_refreshing)
                return;
            var level = LevelFacade.Current;
            var key = TargetKey();
            if (level == null || key == null)
            {
                Refresh();
                return;
            }
            BlueprintOps.SetNoDaySleep(level, key, noDaySleep);
            Refresh();
        }

        /// <summary>无条件放置：**全局**（对所有蓝图生效）。</summary>
        private void SetUnconditionalPlace(bool value)
        {
            if (_refreshing)
                return;
            var level = LevelFacade.Current;
            if (level == null)
            {
                Refresh();
                return;
            }
            BlueprintOps.SetUnconditionalPlace(level, value);
            Refresh();
        }

        private void SetFlag(bool value, bool commandBlock)
        {
            if (_refreshing)
                return;
            var level = LevelFacade.Current;
            var key = TargetKey();
            if (level == null || key == null)
            {
                Refresh();
                return;
            }
            if (commandBlock)
            {
                // 模仿者：标记按「这张卡」记（TickFlags 每帧把它同步到卡槽里的 SeedPack 上）。
                BlueprintOps.SetCommandFlag(level, key, value);
                var pack = DisplayTarget();
                if (pack != null)
                    BlueprintOps.SetCommandBlock(pack, value);
                // ★ 原版那两处显示都要重画（选卡阶段的字段 + 关卡 HUD 的卡），详见 VanillaChooseBridge。
                int slot;
                if (BlueprintKeys.TrySlot(key, out slot))
                    VanillaChooseBridge.RefreshAll(level, slot);
            }
            else
            {
                BlueprintOps.SetReverseFaction(level, key, value);
                SyncVanilla(TargetSlot());
            }
            Refresh();
        }

        /// <summary>
        /// 面板改完之后让**原版那几处显示**跟着重画（详见 <see cref="VanillaChooseBridge"/>）。
        /// <paramref name="slot"/> &lt; 0 = 全部刷。
        /// </summary>
        private static void SyncVanilla(int slot)
        {
            try
            {
                var level = LevelFacade.Current;
                if (level != null)
                    VanillaChooseBridge.RefreshAll(level, slot);
            }
            catch (Exception) { }
        }

        /// <summary>当前选中的卡槽（没选中 / 不是槽位键 → -1）。</summary>
        private int TargetSlot()
        {
            int slot;
            var key = TargetKey();
            return key != null && BlueprintKeys.TrySlot(key, out slot) ? slot : -1;
        }

        private void ForEachPack(Action<SeedPack> act)
        {
            var level = LevelFacade.Current;
            if (level == null || act == null)
                return;
            var targets = Targets();
            for (var i = 0; i < targets.Count; i++)
            {
                var pack = BlueprintOps.GetPackAt(level, targets[i]);
                if (pack == null)
                    continue;
                try { act(pack); }
                catch (Exception) { }
            }
        }

        /// <summary>预设按钮：循环到下一个预设（匹配不上时从「无」开始）。</summary>
        private void CyclePreset()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return;
            var pack = DisplayTarget();
            var current = level != null && pack != null ? BlueprintOps.MatchPreset(level, pack) : null;
            var index = 0;
            if (current != null)
            {
                for (var i = 0; i < BlueprintOps.Presets.Length; i++)
                {
                    if (BlueprintOps.Presets[i] == current)
                    {
                        index = i + 1;
                        break;
                    }
                }
            }
            ForEachPack(p => BlueprintOps.ApplyRechargePreset(level, p, BlueprintOps.Presets[index % BlueprintOps.Presets.Length]));
            SyncVanilla(TargetSlot());
            Refresh();
        }

        private void OpenPicker()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return;
            if (_picker == null || !_picker.IsValid)
                _picker = BlueprintPickWindow.Create("DSH_PickBlueprints");
            if (_picker == null)
                return;

            var current = new List<NamespaceID>();
            var count = BlueprintOps.GetSlotCount(level);
            for (var i = 0; i < count; i++)
            {
                var pack = BlueprintOps.GetPackAt(level, i);
                if (pack != null && NamespaceID.IsValid(pack.GetDefinitionID()))
                    current.Add(pack.GetDefinitionID());
            }
            _picker.Open("调整选卡", BlueprintUi.Candidates(), current, ApplyPicked);
        }

        /// <summary>把选卡结果写回卡槽（内容没变的格子不动，省得丢掉充能进度）。</summary>
        private void ApplyPicked(List<NamespaceID> picked)
        {
            var level = LevelFacade.Current;
            if (level == null)
                return;
            BlueprintOps.ApplySlots(level, picked);
            // 换过卡之后：原版那两处（选卡阶段的已选卡 + 关卡 HUD）都要重新对一遍
            // （换掉的那一格标记已被清掉，显示必须跟着回去）。
            SyncVanilla(-1);
            _selected = -1;
            LoadoutPanel.Invalidate();
        }

        // ------------------------------------------------------------------
        // 刷新 / 诊断
        // ------------------------------------------------------------------

        public void Tick()
        {
            _tickCalls++;
            if (!_built)
                return;
            var level = LevelFacade.Current;
            if (level == null)
            {
                LoadoutPanel.Invalidate();
                return;
            }
            BlueprintOps.TickFlags(level);
            // ★ 慢速轮询整体重铺一次：兼容性等级可能在设置界面被改（没动本页），
            //   也可能被关卡脚本改了卡槽 —— 不重铺的话各种置灰/取值会停在旧状态。
            Refresh();
            if (_strip != null)
                _strip.FitWidth();
        }

        /// <summary>
        /// 每帧（**不节流**）：镜像条的点选。
        ///
        /// <para>★ 必须在这里而不是 <see cref="Tick"/>：<c>GetMouseButtonDown(0)</c> 只在一帧为真，
        /// 而 <c>Tick</c> 是 30 帧一次的轮询 —— 放在那里就会「要连点几十下才中一次」。</para>
        /// </summary>
        public void Frame()
        {
            _frameCalls++;
            if (!_built || _strip == null)
                return;
            // 选卡浮层盖在面板上面：那会儿点什么都归浮层，别把点击又算到镜像条头上。
            if (_picker != null && _picker.IsOpen)
                return;
            try
            {
                if (!Input.GetMouseButtonDown(0))
                    return;
                var hit = _strip.Hit((Vector2)Input.mousePosition);
                if (hit >= 0)
                    TogglePick(hit);
            }
            catch (Exception) { }
        }

        /// <summary>把「选中项的当前值」写回字段；一张都没选就把字段置空并停用。</summary>
        private void Refresh()
        {
            var level = LevelFacade.Current;
            var pack = DisplayTarget();
            var key = TargetKey();
            // ★ 没选中时：除了「无条件放置」（全局），本页什么都不该改。
            var scoped = pack != null && key != null;
            // ★ 兼容性等级不足的功能 → 相关按钮**置灰**（不管选没选中），并在悬停提示里说明去哪儿调。
            var overrideOk = CompatConfig.IsEnabled(GcFeature.BlueprintOverrides);
            var reverseOk = CompatConfig.IsEnabled(GcFeature.ReverseFaction);
            var placeOk = CompatConfig.IsEnabled(GcFeature.UnconditionalPlace);
            var conveyorOk = CompatConfig.IsEnabled(GcFeature.ConveyorTakeover);
            _refreshing = true;
            try
            {
                if (level != null)
                    BlueprintOps.TickFlags(level);
                RefreshStrip(level);
                if (_cost != null)
                {
                    _cost.Text = pack == null ? "-" : BlueprintOps.GetCost(pack).ToString();
                    SetFieldEnabled(_cost, scoped);
                    LoadoutPanel.SetTooltip(_cost.Root, "费用", scoped
                        ? "这张卡的能耗。"
                        : ScopeHint);
                }
                if (_cooldown != null)
                {
                    var ok = level != null && BlueprintOps.LevelSpeed(level) < 1000f;
                    _cooldown.Text = pack == null ? "-"
                        : (ok ? BlueprintOps.Fmt(BlueprintOps.GetCooldownSeconds(level, pack)) : "—");
                    SetFieldEnabled(_cooldown, scoped && ok);
                    LoadoutPanel.SetTooltip(_cooldown.Root, "冷却(秒)",
                        !scoped ? ScopeHint
                        : ok ? "战斗中这张卡的冷却时间（上限）。\n0 = 无冷却。\n修正系数 " + Fmt(Modifier(level, pack))
                             + "\n输入秒数 = 应用秒数 × " + Fmt(Modifier(level, pack))
                           : "当前关卡的冷却速度异常（例如开着「禁用冷却」），这一项没有意义。");
                }
                if (_presetRow != null)
                {
                    var preset = scoped && level != null ? BlueprintOps.MatchPreset(level, pack) : null;
                    _presetRow.SetLabel(!scoped ? "冷却预设" : (preset == null ? "冷却预设：未定义" : "冷却预设：" + preset.Name));
                    SetRowEnabled(_presetRow, scoped);
                }
                if (_resetRow != null)
                {
                    SetRowEnabled(_resetRow, scoped);
                    LoadoutPanel.SetTooltip(_resetRow.Root, "重置冷却",
                        scoped ? "把这张卡**当前**的冷却清空（立刻就绪），并把预设恢复成原版初始。"
                               : ScopeHint);
                }
                if (_health != null)
                {
                    var id = pack == null ? null : pack.GetDefinitionID();
                    var hp = BlueprintOps.GetBlueprintHealth(level, id);
                    var over = BlueprintOps.GetHealthOverride(level, key);
                    // 显示优先级：这张卡设过的值 > 蓝图定义的原始值。
                    _health.Text = !scoped ? "-"
                        : (over >= 0f ? BlueprintOps.Fmt(over) : (hp < 0f ? "-" : BlueprintOps.Fmt(hp)));
                    SetFieldEnabled(_health, scoped && overrideOk);
                    LoadoutPanel.SetTooltip(_health.Root, "血量",
                        !overrideOk ? CompatConfig.DisabledHint(GcFeature.BlueprintOverrides)
                        : !scoped ? ScopeHint
                        : "这一张卡放出来的器械的血量上限（按**这一张卡**记）。\n"
                          + "显示的是这张卡当前的值；没设过就是蓝图定义的原值。\n"
                          + "改它只影响**之后**放出来的实体，自然生成的同种实体不受影响。");
                }
                if (_commandBlockRow != null && _commandBlockRow.Widgets != null && _commandBlockRow.Widgets.Toggle != null)
                    _commandBlockRow.Widgets.Toggle.Toggle.SetIsOnWithoutNotify(pack != null && BlueprintOps.GetCommandBlock(pack));
                if (_reverseRow != null && _reverseRow.Widgets != null && _reverseRow.Widgets.Toggle != null)
                    _reverseRow.Widgets.Toggle.Toggle.SetIsOnWithoutNotify(scoped && BlueprintOps.GetReverseFaction(level, key));
                if (_daySleepRow != null && _daySleepRow.Widgets != null && _daySleepRow.Widgets.Toggle != null)
                    _daySleepRow.Widgets.Toggle.Toggle.SetIsOnWithoutNotify(scoped && BlueprintOps.GetNoDaySleep(level, key));
                SetRowEnabled(_commandBlockRow, scoped);
                SetRowEnabled(_reverseRow, scoped && reverseOk);
                // 「白天不睡觉」只对**夜用器械**有意义：白天放出来才会挂 NocturnalBuff 去睡。
                // 不是夜用的器械直接置灰（否则勾了也没任何变化，看着像 bug）。
                var nocturnal = scoped && BlueprintOps.IsNocturnal(pack);
                SetRowEnabled(_daySleepRow, scoped && overrideOk && nocturnal);
                LoadoutPanel.SetTooltip(_reverseRow == null ? null : _reverseRow.Root, "逆阵营放置",
                    !reverseOk ? CompatConfig.DisabledHint(GcFeature.ReverseFaction)
                    : !scoped ? ScopeHint
                    : "这一张卡放出来的器械属于右阵营（敌我互调）。\n"
                      + "开了之后镜像框里这张卡的图标会**左右翻转**，一眼能看出哪张动过。");
                LoadoutPanel.SetTooltip(_daySleepRow == null ? null : _daySleepRow.Root, "白天不睡觉",
                    !overrideOk ? CompatConfig.DisabledHint(GcFeature.BlueprintOverrides)
                    : !scoped ? ScopeHint
                    : !nocturnal ? "这张卡放出来的是不是「夜用」器械，不会睡觉，这一项无效。"
                    : "让「夜用」器械在白天也不睡觉（不进睡眠状态）。");
                // 「无条件放置」是**全局**的，选中与否都能改；但要等级够。
                if (_placeRow != null && _placeRow.Widgets != null && _placeRow.Widgets.Toggle != null)
                    _placeRow.Widgets.Toggle.Toggle.SetIsOnWithoutNotify(
                        level != null && BlueprintOps.GetUnconditionalPlace(level));
                SetRowEnabled(_placeRow, level != null && placeOk);
                LoadoutPanel.SetTooltip(_placeRow == null ? null : _placeRow.Root, "无条件放置",
                    !placeOk ? CompatConfig.DisabledHint(GcFeature.UnconditionalPlace)
                    : "忽略放置条件（地形 / 已占用 / 需蒋叶等），随处可放。\n"
                      + "**这一项是全局的**：对所有蓝图生效（费用与冷却照扣）。");
                // 「传送带模式」要写关卡级卡池，要等级够。
                SetRowEnabled(_conveyorRow, level != null && conveyorOk);
                LoadoutPanel.SetTooltip(_conveyorRow == null ? null : _conveyorRow.Root, "传送带模式",
                    !conveyorOk ? CompatConfig.DisabledHint(GcFeature.ConveyorTakeover)
                    : "把这一关切成传送带，并给它加上原版的传送带行为（会从卡池里抽卡）。\n"
                      + "卡池内容 = 当前的卡槽，权重/数量限制在传送带页里调。");
            }
            catch (Exception) { }
            finally
            {
                _refreshing = false;
            }
        }

        /// <summary>镜像行：卡槽 i 对应第 i 格；没有卡槽时框体自己就是那个空框。</summary>
        private void RefreshStrip(LevelEngine level)
        {
            if (_strip == null)
                return;
            var count = level == null ? 0 : BlueprintOps.GetSlotCount(level);
            var cells = count;
            if (_strip.Count != cells)
            {
                _strip.SetCount(cells);
                _strip.FitWidth();
            }
            Prune(count);
            for (var i = 0; i < cells; i++)
            {
                var pack = i < count ? BlueprintOps.GetPackAt(level, i) : null;
                var id = pack == null ? null : pack.GetDefinitionID();
                _strip.SetCard(i, id, pack != null && BlueprintOps.GetCommandBlock(pack), i);
                // 逆阵营标记 → 镜像框里这张卡的**图标左右翻转**（空槽 / 没标记就还原）。
                var key = BlueprintKeys.Slot(i);
                var reverse = pack != null && level != null && BlueprintOps.GetReverseFaction(level, key);
                _strip.SetMirror(i, reverse);
                _strip.SetMark(i, IsPicked(i));
            }
        }

        private bool IsPicked(int slot)
        {
            return _selected == slot;
        }

        /// <summary>时间修正系数（应用秒数 × 它 = 输入/实际秒数）。</summary>
        private static float Modifier(LevelEngine level, SeedPack pack)
        {
            if (level == null || pack == null)
                return 1f;
            var speed = BlueprintOps.LevelSpeed(level) * BlueprintOps.CardSpeed(pack);
            if (speed <= 0.0001f)
                return 1f;
            return 1f / speed;
        }

        private static string Fmt(float v)
        {
            return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static bool TryParse(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return float.TryParse(text.Trim().Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// 自测用：二三行每个控件的**可交互状态** + 兼容等级门控。
        ///
        /// <para>本轮两个需求都靠它证明：① 打开本页时（一张都没选）二三行应当**立刻是灰的**；
        /// ② 兼容等级不够的功能（逆阵营 / 无条件放置 / 传送带模式）**不管选没选都该是灰的**。</para>
        /// </summary>
        public static string DebugGate()
        {
            if (_last == null || !_last._built)
                return "（蓝图页未装配）";
            var p = _last;
            var sb = new System.Text.StringBuilder(300);
            sb.Append("选中=").Append(p._selected < 0 ? "无" : ("槽" + p._selected))
              .Append("  等级=").Append(CompatConfig.Level)
              .Append("  作用目标=").Append(p.Targets().Count).Append('\n');
            sb.Append("    行2 费用=").Append(Field(p._cost))
              .Append(" 冷却=").Append(Field(p._cooldown))
              .Append(" 预设=").Append(Row(p._presetRow))
              .Append(" 重置=").Append(Row(p._resetRow)).Append('\n');
            sb.Append("    行3 血量=").Append(Field(p._health))
              .Append(" 模仿者=").Append(Row(p._commandBlockRow))
              .Append(" 逆阵营=").Append(Row(p._reverseRow))
              .Append(" 白天不睡觉=").Append(Row(p._daySleepRow)).Append('\n');
            sb.Append("    行4 无条件放置=").Append(Row(p._placeRow))
              .Append(" 传送带模式=").Append(Row(p._conveyorRow))
              .Append("  等级开关 血量/睡觉=").Append(On(GcFeature.BlueprintOverrides))
              .Append(" 逆阵营=").Append(On(GcFeature.ReverseFaction))
              .Append(" 无条件=").Append(On(GcFeature.UnconditionalPlace))
              .Append(" 传送带=").Append(On(GcFeature.ConveyorTakeover));
            return sb.ToString();
        }

        /// <summary>
        /// 自测用：**真的走一遍放置生成**，验「标记只对该卡放出来的实体生效」。
        ///
        /// <para>三条对比（都在真实格子上生成，生成完就删掉）：</para>
        /// <list type="number">
        /// <item>带「槽 A」的作用域（A 被标了 12345 血 / 不睡觉 / 逆阵营）→ 应当全中；</item>
        /// <item>带「槽 B」的作用域（B 没标记）→ 应当**一点变化都没有**；</item>
        /// <item>**不带作用域**（= 自然生成那条路，用的是和 A 完全相同的实体 id）→ 也应当**一点变化都没有**
        /// —— 这一条就是本轮修掉的 bug（以前自然生成的僵尸也被改了血量）。</item>
        /// </list>
        /// </summary>
        public static string DebugPlaceProbe()
        {
            if (_last == null || !_last._built)
                return "（蓝图页未装配）";
            var level = LevelFacade.Current;
            if (level == null)
                return "（不在关卡内）";
            var count = BlueprintOps.GetSlotCount(level);
            var slotA = -1;
            var slotB = -1;
            for (var i = 0; i < count; i++)
            {
                if (BlueprintOps.GetPackAt(level, i) == null)
                    continue;
                if (slotA < 0) slotA = i;
                else if (slotB < 0) slotB = i;
            }
            if (slotA < 0)
                return "（卡槽是空的）";
            if (slotB < 0)
                slotB = slotA;

            var packA = BlueprintOps.GetPackAt(level, slotA);
            var packB = BlueprintOps.GetPackAt(level, slotB);
            var idA = packA.GetDefinitionID();
            var keyA = BlueprintKeys.Slot(slotA);
            var sb = new System.Text.StringBuilder(400);
            sb.Append("基准「").Append(BlueprintOps.BlueprintName(idA, false))
              .Append("」原版血量=").Append(Fmt(BlueprintOps.GetBlueprintHealth(level, idA)))
              .Append("  标在 ").Append(keyA).Append("（槽").Append(slotA + 1).Append("）");

            BlueprintOps.SetHealthOverride(level, keyA, 12345f);
            BlueprintOps.SetNoDaySleep(level, keyA, true);
            BlueprintOps.SetReverseFaction(level, keyA, true);
            PVZEngine.Entities.Entity a = null, b = null, c = null;
            try
            {
                a = PlacementScope.DebugSpawnScoped(level, packA, level.GetGrid(0, 0));
                b = PlacementScope.DebugSpawnScoped(level, packB, level.GetGrid(0, 1));
                c = PlacementScope.DebugSpawnUnscoped(level, idA, level.GetGrid(1, 0));
                sb.Append("\n    A 带「").Append(keyA).Append("」作用域 → ")
                  .Append(BlueprintSpawnPatch.DescribeEntity(a));
                sb.Append("\n    B 带「").Append(BlueprintKeys.Slot(slotB)).Append("」作用域 → ")
                  .Append(BlueprintSpawnPatch.DescribeEntity(b));
                sb.Append("\n    C 不带作用域（自然生成那条路，同一个实体 id）→ ")
                  .Append(BlueprintSpawnPatch.DescribeEntity(c));
            }
            catch (Exception e)
            {
                sb.Append("\n    （生成探针失败 ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append("）");
            }
            finally
            {
                // 生成出来只是为了量一量 —— 量完就删掉，别留在关卡里。
                Remove(a);
                Remove(b);
                Remove(c);
                BlueprintOps.SetHealthOverride(level, keyA, -1f);
                BlueprintOps.SetNoDaySleep(level, keyA, false);
                BlueprintOps.SetReverseFaction(level, keyA, false);
            }
            return sb.ToString();
        }

        /// <summary>探针用：把生成出来的实体删掉（失败就算了，别影响报告）。</summary>
        private static void Remove(PVZEngine.Entities.Entity e)
        {
            try
            {
                if (e != null)
                    e.Remove();
            }
            catch (Exception) { }
        }

        /// <summary>自测用：强制重铺一次本页，再报告各控件的可交互状态（验「改等级后跟着变」）。</summary>
        public static string DebugRefresh()
        {
            if (_last == null || !_last._built)
                return "（蓝图页未装配）";
            _last.Refresh();
            return DebugGate();
        }

        private static string On(GcFeature feature)
        {
            return CompatConfig.IsEnabled(feature) ? "开" : "关";
        }

        private static string Field(UiField field)
        {
            var input = field == null ? null : field.Input;
            if (input == null)
                return "无";
            try { return input.interactable ? "可改" : "灰"; }
            catch (Exception) { return "?"; }
        }

        private static string Row(UiOptionRow row)
        {
            var w = row == null ? null : row.Widgets;
            if (w == null)
                return "无";
            var state = "?";
            try
            {
                if (w.Toggle != null && w.Toggle.Toggle != null)
                    state = w.Toggle.Toggle.interactable ? "可点" : "灰";
                if (w.TextButton != null && w.TextButton.Button != null)
                    state = w.TextButton.Button.interactable ? "可点" : "灰";
            }
            catch (Exception) { }
            return state;
        }

        public string Report()
        {
            var sb = new System.Text.StringBuilder(200);
            var level = LevelFacade.Current;
            sb.Append("卡槽=").Append(level == null ? 0 : BlueprintOps.GetSlotCount(level))
              .Append("  镜像格=").Append(_strip == null ? 0 : _strip.Count)
              .Append("  选中=").Append(_selected < 0 ? "无" : ("槽" + _selected))
              .Append("  作用目标=").Append(Targets().Count)
              .Append('\n');
            if (_strip != null)
                sb.Append(_strip.DebugRects("镜像行"));
            if (level != null)
            {
                for (var i = 0; i < BlueprintOps.GetSlotCount(level); i++)
                {
                    var pack = BlueprintOps.GetPackAt(level, i);
                    if (pack == null)
                        continue;
                    sb.Append("  #").Append(i)
                      .Append(IsPicked(i) ? " [x] " : " [ ] ")
                      .Append(BlueprintOps.BlueprintName(pack.GetDefinitionID(), BlueprintOps.GetCommandBlock(pack)))
                      .Append(" 费=").Append(BlueprintOps.GetCost(pack))
                      .Append(" 冷=").Append(Fmt(BlueprintOps.GetCooldownSeconds(level, pack))).Append('s')
                      .Append(" 预设=").Append(BlueprintOps.MatchPreset(level, pack) == null
                          ? "未定义" : BlueprintOps.MatchPreset(level, pack).Name)
                      .Append(BlueprintOps.GetCommandBlock(pack) ? " 模仿者" : string.Empty)
                      .Append(BlueprintOps.GetReverseFaction(level, BlueprintKeys.Slot(i)) ? " 逆阵营" : string.Empty)
                      .Append('\n');
                }
            }
            return sb.ToString();
        }
    }
}
