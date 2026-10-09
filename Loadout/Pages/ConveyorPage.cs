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
    /// 关卡内 · 页「蓝图」（**传送带模式**）。
    ///
    /// <para><b>★ 第一行显示的、编辑的就是「卡槽」</b>——与自选卡那一页是**同一份数据**。
    /// 这样两张蓝图就能做成同一套东西（最典型的需求：「能选择三个血量不同的僵尸的蓝图」，
    /// 在自选卡页和传送带页都能用，标记也共享）。</para>
    ///
    /// <para>卡池（<c>LogicStageProps.CONVEYOR_POOL</c>）是它的**镜像 + 专有属性**：
    /// 蓝图 id 与顺序跟着卡槽走，只有「权重」与「数量限制」是传送带独有的，
    /// 按蓝图 id 单独记（换掉某张卡再换回来，它的权重还在）。</para>
    ///
    /// <para>原版把卡池写在关卡定义上、关卡级一般是空的 —— 所以这里会把镜像结果
    /// **写回关卡属性**（随 <c>.lvl</c> 存盘）。这正是「从自选卡改到传送带模式后数据要写进存档」。</para>
    ///
    /// <para>「权重」其实是「初始份数」（<c>ConveyorPoolEntry.Count</c>），
    /// 「数量限制」是「补货下限」（<c>MinCount</c>）—— 原版抽卡就是按剩余份数加权的。
    /// <c>MinCount = 0</c> 表示不额外保证，抽出去的收回来接着抽，等于**不限量**。</para>
    /// </summary>
    internal sealed class ConveyorPage : IPanelPage
    {
        public string Name { get { return "蓝图"; } }

        private static BlueprintPickWindow _picker;
        private static ConveyorPage _last;

        /// <summary>自测用：打开「调整卡池」并回一段矩形报告。</summary>
        public static string DebugOpenPicker()
        {
            if (_last == null || !_last._built)
                return "（传送带页未装配）";
            _last.OpenPicker();
            var picker = _picker;
            if (picker == null || !picker.IsOpen)
                return "（卡池选卡浮层打不开）";
            return picker.DebugRects();
        }

        private BlueprintStrip _strip;
        private UiField _weight, _limit, _speed, _health;
        /// <summary>「当前概率」——**纯文本**（需求：那一条不带输入框等其它组件）。</summary>
        private GameObject _probCell;
        private TextMeshProUGUI _prob;
        private UiOptionRow _commandRow, _reverseRow, _daySleepRow, _placeRow, _clearRow;

        private List<BlueprintOps.PoolItem> _pool;
        /// <summary>第一行显示/编辑的那份**卡槽**内容（与自选卡页同一份数据）。</summary>
        private List<NamespaceID> _slots;
        /// <summary>被选中的**卡槽**下标（**单选**：-1 = 没选）。</summary>
        private int _selected = -1;
        private bool _poolWritable = true;
        private bool _built;
        private bool _refreshing;

        public void Build()
        {
            _last = this;
            _strip = null;
            _weight = _limit = _speed = _health = null;
            _probCell = null;
            _prob = null;
            _commandRow = _reverseRow = _daySleepRow = _placeRow = _clearRow = null;
            _pool = null;
            _slots = null;
            _selected = -1;
            _poolWritable = true;
            _built = false;

            var level = LevelFacade.Current;
            if (level == null)
            {
                AddNote("本页只在关卡内可用（当前不在关卡内）。");
                return;
            }
            BlueprintFlags.Sync(level);
            LoadData(level);

            // ---- 第一行：**卡槽**镜像条（与自选卡模式同一份数据）----
            var row1 = UiGridRow.Create("Row_ConveyorPool", equalize: false);
            if (row1 != null)
            {
                _strip = BlueprintStrip.Create(row1.Root.transform, "PoolStrip");
                if (_strip != null)
                {
                    row1.AddCell(_strip.Root, 76f);
                    _strip.FillRow();   // AddCell 会把 ignoreLayout 设回 false，这里再抢回来
                }
                LoadoutPanel.AddRow(row1.FinishFixed(76f));
            }

            // ---- 第二行：权重 / 数量限制 / 传送带速度 / 当前概率 ----
            var row2 = UiGridRow.Create("Row_ConveyorFields");
            if (row2 != null)
            {
                _weight = row2.AddField("F_Weight", "权重", numeric: true, integer: true);
                if (_weight != null)
                {
                    _weight.OnSubmit(OnWeightSubmitted);
                    LoadoutPanel.AddTooltip(_weight.Root, "权重",
                        "这张卡在卡池里的初始份数：份数越多，抽到它的机会越大。\n"
                        + "按蓝图 id 记，与自选卡模式的卡槽内容无关。");
                }
                _limit = row2.AddField("F_Limit", "数量限制", numeric: true, integer: true);
                if (_limit != null)
                {
                    _limit.OnSubmit(OnLimitSubmitted);
                    LoadoutPanel.AddTooltip(_limit.Root, "数量限制",
                        "卡池抽空后这张卡至少补到几份。\n0 = 不限量（抽出去的收回来接着抽）。");
                }
                _speed = row2.AddField("F_ConveySpeed", "传送带速度", numeric: true);
                if (_speed != null)
                {
                    _speed.OnSubmit(OnSpeedSubmitted);
                    LoadoutPanel.AddTooltip(_speed.Root, "传送带速度", "传送带的行进速度倍率，1 = 原版速度。");
                }
                _probCell = AddTextCell(row2, "F_Prob", "当前概率");
                _prob = TextOfCell(_probCell);
                if (_probCell != null)
                {
                    LoadoutPanel.AddTooltip(_probCell, "当前概率",
                        "算式：权重 / 总权重 = 概率。\n只对选中的那个生效（没选中时用第一张卡来算）。");
                }
                LoadoutPanel.AddRow(row2.Finish());
            }

            // ---- 第三行：血量 / 模仿者 / 逆阵营放置 / 白天不睡觉 ----
            var row3 = UiGridRow.Create("Row_ConveyorFlags");
            if (row3 != null)
            {
                _health = row3.AddField("F_Health", "血量", numeric: true);
                if (_health != null)
                {
                    _health.OnSubmit(OnHealthSubmitted);
                    LoadoutPanel.AddTooltip(_health.Root, "血量",
                        "这个器械的血量上限（按蓝图 id 记，之后新放置的按它生成）。\n只对选中的那个生效。");
                }
                _commandRow = row3.AddOptionRow("T_CommandBlock", UiOptionKind.Toggle);
                if (_commandRow != null)
                {
                    _commandRow.SetToggle("模仿者", false, v => SetFlag(v, true));
                    LoadoutPanel.AddTooltip(_commandRow.Root, "模仿者",
                        "放置时先出命令方块，稍后才变成真正的器械。");
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

            // ---- 第四行：无条件放置 / 调整卡池 / 清空卡池 / 自选卡模式 ----
            // 顺序与自选卡页的第四行**一一对应**（调整在前、清空在后），
            // 这样切模式时四个按钮的位置不变，手感一致。
            var row4 = UiGridRow.Create("Row_ConveyorActions");
            if (row4 != null)
            {
                _placeRow = row4.AddOptionRow("T_UnconditionalPlace", UiOptionKind.Toggle);
                if (_placeRow != null)
                {
                    _placeRow.SetToggle("无条件放置", false, v => SetUnconditionalPlace(v));
                    LoadoutPanel.AddTooltip(_placeRow.Root, "无条件放置",
                        "忽略放置条件（地形 / 已占用 / 需蒋叶等），随处可放。");
                }
                var adjust = row4.AddOptionRow("B_AdjustPool", UiOptionKind.Button);
                if (adjust != null)
                {
                    adjust.SetButton("调整卡池", OpenPicker);
                    LoadoutPanel.AddTooltip(adjust.Root, "调整卡池",
                        "从全部蓝图里挑一批放进卡池。\n"
                        + "卡池就是这里的卡槽（两页同一份数据），所以这里改的也是自选卡的卡槽。");
                }
                _clearRow = row4.AddOptionRow("B_ClearSlots", UiOptionKind.Button);
                if (_clearRow != null)
                {
                    _clearRow.SetButton("清空卡池", ClearSlots);
                    LoadoutPanel.AddTooltip(_clearRow.Root, "清空卡池",
                        "把卡池清空（卡槽数不变、每格置空），连带各格的血量/阵营等标记一起清掉。");
                }
                var classic = row4.AddOptionRow("B_ClassicMode", UiOptionKind.Button);
                if (classic != null)
                {
                    classic.SetButton("自选卡模式", () =>
                    {
                        var lv = LevelFacade.Current;
                        if (lv != null)
                            ConveyorRig.Disable(lv);   // 切回来 + **删掉我们加的那个传送带行为**
                        // 同上：立刻切回普通那一套页面。
                        LoadoutPanel.SyncPageSet();
                        LoadoutPanel.Invalidate();
                    });
                    LoadoutPanel.AddTooltip(classic.Root, "自选卡模式",
                        "把这一关切回自己带卡（卡池内容会留着）。");
                }
                LoadoutPanel.AddRow(row4.Finish());
            }

            // 注：以前这里还有一条「卡池写不进」的提示行。它**去掉了** ——
            // 布局组会给每个子节点之间加间距，那条即使高度为 0 也会让本页比蓝图页高一线，
            // 而需求要求「切换模式时不改变面板高度」。写不进这件事改成写在「当前概率」
            // 那一格里（那本来就要求是纯文本）+ 悬停提示 + 日志。

            _built = true;
            Refresh();
            Plugin.Log?.LogInfo("[ConveyorPage] 已装配：" + Describe());
        }

        // ------------------------------------------------------------------
        // 卡槽 / 卡池 读写
        // ------------------------------------------------------------------

        /// <summary>读当前卡槽内容。</summary>
        private static List<NamespaceID> SlotsOf(LevelEngine level)
        {
            var list = new List<NamespaceID>();
            var count = level == null ? 0 : BlueprintOps.GetSlotCount(level);
            for (var i = 0; i < count; i++)
            {
                var pack = BlueprintOps.GetPackAt(level, i);
                var id = pack == null ? null : pack.GetDefinitionID();
                list.Add(NamespaceID.IsValid(id) ? id : null);
            }
            return list;
        }

        /// <summary>
        /// 读卡槽 + 把卡池**镜像**成卡槽的样子，并把结果写回关卡（存档）。
        ///
        /// <para>镜像规则：蓝图 id 与顺序 **= 卡槽**；「权重 / 数量限制」按蓝图 id 从旧卡池里
        /// 继承（没继承到的新条目：权重 1000、数量限制 0）。</para>
        ///
        /// <para>★ 卡槽是空的时候**不动卡池** —— 原版自带传送带的关卡卡槽本来就是空的，
        /// 那时卡池是关卡设计好的内容，拿一个空表去覆盖就把它毁了。</para>
        /// </summary>
        private void LoadData(LevelEngine level)
        {
            _slots = SlotsOf(level);
            var old = BlueprintOps.PoolToItems(BlueprintOps.GetPool(level));

            var hasSlot = false;
            for (var i = 0; i < _slots.Count; i++)
            {
                if (NamespaceID.IsValid(_slots[i]))
                {
                    hasSlot = true;
                    break;
                }
            }
            if (!hasSlot)
            {
                // 卡槽是空的：直接采用关卡自己的卡池（一行只读镜像）。
                _pool = old;
                return;
            }

            var items = new List<BlueprintOps.PoolItem>();
            for (var i = 0; i < _slots.Count; i++)
            {
                var id = _slots[i];
                if (!NamespaceID.IsValid(id))
                    continue;
                var keep = Find(old, id);
                items.Add(keep ?? new BlueprintOps.PoolItem { Id = id, Weight = 1000, Min = 0 });
            }
            _pool = items;
            // ★ 写回关卡属性（随 .lvl 存盘）——「从自选卡改到传送带模式后数据写入存档」。
            _poolWritable = BlueprintOps.SetPool(level, _pool);
        }

        private static BlueprintOps.PoolItem Find(List<BlueprintOps.PoolItem> items, NamespaceID id)
        {
            for (var i = 0; items != null && i < items.Count; i++)
            {
                if (BlueprintOps.SameId(items[i].Id, id))
                    return items[i];
            }
            return null;
        }

        /// <summary>已选卡槽对应的卡池条目下标（-1 = 没有）。</summary>
        private int PoolIndexOfSelection()
        {
            if (_pool == null || _selected < 0 || _slots == null
                || _selected >= _slots.Count || !NamespaceID.IsValid(_slots[_selected]))
                return -1;
            for (var i = 0; i < _pool.Count; i++)
            {
                if (BlueprintOps.SameId(_pool[i].Id, _slots[_selected]))
                    return i;
            }
            return -1;
        }

        private void Save()
        {
            var level = LevelFacade.Current;
            if (level == null || _pool == null)
                return;
            _poolWritable = BlueprintOps.SetPool(level, _pool);
            if (!_poolWritable)
                Refresh();
        }

        // ------------------------------------------------------------------
        // 选中集
        // ------------------------------------------------------------------

        /// <summary>
        /// **作用目标**：选中的那一格卡槽。
        ///
        /// <para>★ 本页只有「无条件放置」与「传送带速度」是**关卡级**的（对所有卡生效），
        /// 其余功能（权重 / 数量限制 / 血量 / 模仿者 / 逆阵营 / 白天不睡觉）
        /// **只对被选中的那一张生效** —— 与自选卡页一致。</para>
        /// </summary>
        private List<int> Targets()
        {
            var result = new List<int>();
            if (_slots == null || _selected < 0 || _selected >= _slots.Count)
                return result;
            if (!NamespaceID.IsValid(_slots[_selected]))
                return result;
            result.Add(_selected);
            return result;
        }

        /// <summary>确实选中了一个可用的卡槽。</summary>
        private bool HasSelection()
        {
            return Targets().Count > 0;
        }

        /// <summary>显示用的那一格（没选中 = -1）。</summary>
        private int DisplayIndex()
        {
            return HasSelection() ? _selected : -1;
        }

        /// <summary>选中的那张卡（没选中返回 null）。</summary>
        private SeedPack TargetPack()
        {
            var index = DisplayIndex();
            if (index < 0)
                return null;
            return BlueprintOps.GetPackAt(LevelFacade.Current, index);
        }

        /// <summary>
        /// 选中项的**标记键**（<c>s:槽号</c>）；没选中返回 null。
        ///
        /// <para>★ 用**槽号**而不是蓝图 id：这样标记与自选卡页天然是同一份
        /// （同一张卡在传送带模式下也带着它的血量 / 阵营 / 模仿者设置）。</para>
        /// </summary>
        private string TargetKey()
        {
            var index = DisplayIndex();
            if (index < 0)
                return null;
            return BlueprintKeys.Slot(index);
        }

        /// <summary>点一张卡：单选（再点同一张就取消）。</summary>
        private void TogglePick(int index)
        {
            _selected = _selected == index ? -1 : index;
            Refresh();
        }

        private bool IsPicked(int index)
        {
            return _selected == index;
        }

        // ------------------------------------------------------------------
        // 交互
        // ------------------------------------------------------------------

        private void OnWeightSubmitted(string text)
        {
            int value;
            var index = PoolIndexOfSelection();
            if (index < 0 || !int.TryParse((text ?? string.Empty).Trim(), out value) || value < 0)
            {
                Refresh();
                return;
            }
            _pool[index].Weight = value;
            Save();
            Refresh();
        }

        private void OnLimitSubmitted(string text)
        {
            int value;
            var index = PoolIndexOfSelection();
            if (index < 0 || !int.TryParse((text ?? string.Empty).Trim(), out value) || value < 0)
            {
                Refresh();
                return;
            }
            _pool[index].Min = value;
            Save();
            Refresh();
        }

        private void OnSpeedSubmitted(string text)
        {
            var level = LevelFacade.Current;
            float value;
            if (level == null || !TryParse(text, out value) || value < 0f)
            {
                Refresh();
                return;
            }
            BlueprintOps.SetConveySpeed(level, value);
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
                BlueprintOps.SetCommandFlag(level, key, value);
                // 与蓝图页同一处理：卡本身也要变白（战斗里那张卡是读 pack 属性的）。
                var pack = TargetPack();
                if (pack != null)
                    BlueprintOps.SetCommandBlock(pack, value);
                int slot;
                if (BlueprintKeys.TrySlot(key, out slot))
                    VanillaChooseBridge.RefreshAll(level, slot);
            }
            else
            {
                BlueprintOps.SetReverseFaction(level, key, value);
            }
            Refresh();
        }

        /// <summary>面板改完之后让**原版那几处显示**跟着重画（详见 <see cref="VanillaChooseBridge"/>）。</summary>
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

        /// <summary>当前选中的卡槽（没选中 → -1）。</summary>
        private int TargetSlot()
        {
            var index = DisplayIndex();
            return index >= 0 ? index : -1;
        }

        // ------------------------------------------------------------------
        // 本轮新增功能：**只做了布局**，语义待确认（点下去只记一行日志）
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // 本轮新增的功能
        // ------------------------------------------------------------------

        /// <summary>血量：按**选中的那个卡池条目**记（只影响它之后放出来的实体）。</summary>
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
            BlueprintOps.SetHealthOverride(level, key, value > 0f ? value : -1f);
            Refresh();
        }

        /// <summary>清空卡池（= 清空卡槽）：卡槽数不变、每格置空，标记与卡池一并清掉。</summary>
        private void ClearSlots()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return;
            _selected = -1;
            BlueprintOps.ClearSlots(level);
            // 卡池是卡槽的镜像 —— 卡槽空了，卡池也跟着空（否则下一段旅程会拿旧卡池抽卡）。
            _pool = new List<BlueprintOps.PoolItem>();
            _slots = SlotsOf(level);
            Save();
            LoadoutPanel.Invalidate();
        }

        /// <summary>白天不睡觉：按**选中的那个卡池条目**记。参数就是勾选状态（勾上 = 不睡觉）。</summary>
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

        private void OpenPicker()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return;
            if (_picker == null || !_picker.IsValid)
                _picker = BlueprintPickWindow.Create("DSH_PickPool");
            if (_picker == null)
                return;

            var current = new List<NamespaceID>();
            for (var i = 0; i < BlueprintOps.GetSlotCount(level); i++)
            {
                var pack = BlueprintOps.GetPackAt(level, i);
                var id = pack == null ? null : pack.GetDefinitionID();
                if (NamespaceID.IsValid(id))
                    current.Add(id);
            }
            _picker.Open("调整卡池", BlueprintUi.Candidates(), current, ApplyPicked);
        }

        /// <summary>
        /// 选卡结果写回**卡槽**（卡池是它的镜像，随后重建）。
        ///
        /// <para>权重 / 数量限制按蓝图 id 继承：留下来的卡设置不变，新加的按 1000 / 0。</para>
        /// </summary>
        private void ApplyPicked(List<NamespaceID> picked)
        {
            var level = LevelFacade.Current;
            if (level == null)
                return;
            var old = _pool;
            BlueprintOps.ApplySlots(level, picked);
            _slots = SlotsOf(level);
            var items = new List<BlueprintOps.PoolItem>();
            for (var i = 0; i < _slots.Count; i++)
            {
                var id = _slots[i];
                if (!NamespaceID.IsValid(id))
                    continue;
                var keep = Find(old, id);
                items.Add(keep ?? new BlueprintOps.PoolItem { Id = id, Weight = 1000, Min = 0 });
            }
            _pool = items;
            _selected = -1;
            Save();
            LoadoutPanel.Invalidate();
        }

        // ------------------------------------------------------------------
        // 刷新
        // ------------------------------------------------------------------

        public void Tick()
        {
            if (!_built)
                return;
            var level = LevelFacade.Current;
            if (level == null)
            {
                LoadoutPanel.Invalidate();
                return;
            }
            BlueprintOps.TickFlags(level);
            // ★ 卡槽可能在别处被改（自选卡页的「调整选卡 / 清空卡槽」、关卡脚本…）——
            //   变了就把卡槽读一遍、卡池镜像重建一遍（权重/数量限制按 id 继承）。
            if (SlotsChanged(level))
                LoadData(level);
            // 慢速轮询整体重铺一次（理由同蓝图页：兼容性等级可能在设置界面被改）。
            Refresh();
            if (_strip != null)
                _strip.FitWidth();
        }

        /// <summary>卡槽内容与上一次读到的相比变了吗。</summary>
        private bool SlotsChanged(LevelEngine level)
        {
            var now = SlotsOf(level);
            if (_slots == null || _slots.Count != now.Count)
                return true;
            for (var i = 0; i < now.Count; i++)
            {
                if (!BlueprintOps.SameId(_slots[i], now[i]))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 每帧（**不节流**）：卡池条的点选。同蓝图页：<c>GetMouseButtonDown</c> 只在一帧为真，
        /// 放在 30 帧一次的 <see cref="Tick"/> 里会变成「要连点几十下才中」。
        /// </summary>
        public void Frame()
        {
            if (!_built || _strip == null)
                return;
            // 选卡浮层盖在面板上面：那会儿点什么都归浮层。
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

        private void Refresh()
        {
            var level = LevelFacade.Current;
            var index = DisplayIndex();
            var poolIndex = PoolIndexOfSelection();
            var scoped = index >= 0 && poolIndex >= 0;
            // 兼容性等级不足的功能 → 相关按钮置灰（悬停提示里说明去哪儿调）。
            var overrideOk = CompatConfig.IsEnabled(GcFeature.BlueprintOverrides);
            var reverseOk = CompatConfig.IsEnabled(GcFeature.ReverseFaction);
            var placeOk = CompatConfig.IsEnabled(GcFeature.UnconditionalPlace);
            _refreshing = true;
            try
            {
                if (level != null)
                    BlueprintFlags.Sync(level);
                RefreshStrip();
                var item = scoped ? _pool[poolIndex] : null;
                var total = BlueprintOps.TotalWeight(_pool);

                if (_weight != null)
                {
                    _weight.Text = item == null ? "-" : item.Weight.ToString();
                    SetFieldEnabled(_weight, scoped);
                }
                if (_limit != null)
                {
                    _limit.Text = item == null ? "-" : item.Min.ToString();
                    SetFieldEnabled(_limit, scoped);
                }

                // 「传送带速度」是**关卡级**属性（不分张），选中与否都能改。
                if (_speed != null)
                {
                    _speed.Text = level == null ? "-" : Fmt(BlueprintOps.GetConveySpeed(level));
                    SetFieldEnabled(_speed, level != null);
                }
                if (_prob != null)
                {
                    // 需求：这一格是**纯文本**，内容 = 显示计算公式（权重 / 总权重 = 概率）。
                    if (!_poolWritable && _slots != null && _slots.Count > 0)
                        _prob.text = "⚠ 写不进";
                    else if (item == null || total <= 0)
                        _prob.text = "-";
                    else
                        _prob.text = item.Weight + "/" + total + " = "
                            + (item.Weight * 100f / total).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
                            + "%";
                }
                if (_probCell != null)
                {
                    LoadoutPanel.SetTooltip(_probCell, "当前概率",
                        !scoped ? ScopeHint
                        : _poolWritable
                            ? "算式：" + (item == null ? "-" : item.Weight + " / " + total) + " = 概率。\n只对选中的那一张生效。"
                            : "⚠ 卡池写不进关卡（兼容性等级不够，或写入失败）；权重/数量限制目前只在本页预览。");
                }
                if (_health != null)
                {
                    var key = TargetKey();
                    var pack = TargetPack();
                    var id = pack == null ? null : pack.GetDefinitionID();
                    var hp = BlueprintOps.GetBlueprintHealth(level, id);
                    var over = BlueprintOps.GetHealthOverride(level, key);
                    _health.Text = !scoped ? "-"
                        : (over >= 0f ? BlueprintOps.Fmt(over) : (hp < 0f ? "-" : BlueprintOps.Fmt(hp)));
                    SetFieldEnabled(_health, scoped && overrideOk);
                    LoadoutPanel.SetTooltip(_health.Root, "血量",
                        !overrideOk ? CompatConfig.DisabledHint(GcFeature.BlueprintOverrides)
                        : !scoped ? ScopeHint
                        : "这一张卡放出来的器械的血量上限（按**这一张卡**记）。\n"
                          + "与自选卡模式共用同一个标记。");
                }

                var key2 = TargetKey();
                if (_commandRow != null && _commandRow.Widgets != null && _commandRow.Widgets.Toggle != null)
                    _commandRow.Widgets.Toggle.Toggle.SetIsOnWithoutNotify(scoped && level != null
                        && BlueprintOps.GetCommandFlag(level, key2));
                if (_reverseRow != null && _reverseRow.Widgets != null && _reverseRow.Widgets.Toggle != null)
                    _reverseRow.Widgets.Toggle.Toggle.SetIsOnWithoutNotify(scoped && level != null
                        && BlueprintOps.GetReverseFaction(level, key2));
                if (_daySleepRow != null && _daySleepRow.Widgets != null && _daySleepRow.Widgets.Toggle != null)
                    _daySleepRow.Widgets.Toggle.Toggle.SetIsOnWithoutNotify(scoped && level != null
                        && BlueprintOps.GetNoDaySleep(level, key2));
                var nocturnal = scoped && BlueprintOps.IsNocturnal(TargetPack());
                SetRowEnabled(_commandRow, scoped);
                SetRowEnabled(_reverseRow, scoped && reverseOk);
                SetRowEnabled(_daySleepRow, scoped && overrideOk && nocturnal);
                LoadoutPanel.SetTooltip(_reverseRow == null ? null : _reverseRow.Root, "逆阵营放置",
                    !reverseOk ? CompatConfig.DisabledHint(GcFeature.ReverseFaction)
                    : !scoped ? ScopeHint
                    : "这一张卡放出来的器械属于右阵营（敌我互调）。\n"
                      + "开了之后镜像框里这张卡的图标会**左右翻转**，一眼能看出哪张动过。");
                LoadoutPanel.SetTooltip(_daySleepRow == null ? null : _daySleepRow.Root, "白天不睡觉",
                    !overrideOk ? CompatConfig.DisabledHint(GcFeature.BlueprintOverrides)
                    : !scoped ? ScopeHint
                    : !nocturnal ? "这张卡放出来的不是「夜用」器械，不会睡觉，这一项无效。"
                    : "让「夜用」器械在白天也不睡觉（不进睡眠状态）。");
                // 「无条件放置」是**全局**的，但要等级够。
                if (_placeRow != null && _placeRow.Widgets != null && _placeRow.Widgets.Toggle != null)
                    _placeRow.Widgets.Toggle.Toggle.SetIsOnWithoutNotify(
                        level != null && BlueprintOps.GetUnconditionalPlace(level));
                SetRowEnabled(_placeRow, level != null && placeOk);
                LoadoutPanel.SetTooltip(_placeRow == null ? null : _placeRow.Root, "无条件放置",
                    !placeOk ? CompatConfig.DisabledHint(GcFeature.UnconditionalPlace)
                    : "忽略放置条件（地形 / 已占用 / 需蒋叶等），随处可放。\n"
                      + "**这一项是全局的**：对所有蓝图生效（费用与冷却照扣）。");
            }
            catch (Exception) { }
            finally
            {
                _refreshing = false;
            }
        }

        private void RefreshStrip()
        {
            if (_strip == null)
                return;
            var count = _slots == null ? 0 : _slots.Count;
            var cells = count;
            if (_strip.Count != cells)
            {
                _strip.SetCount(cells);
                _strip.FitWidth();
            }
            var level = LevelFacade.Current;
            for (var i = 0; i < cells; i++)
            {
                var pack = BlueprintOps.GetPackAt(level, i);
                var id = pack == null ? null : pack.GetDefinitionID();
                _strip.SetCard(i, id, pack != null && BlueprintOps.GetCommandBlock(pack), i);
                // 逆阵营标记 → 镜像框里这张卡的**图标左右翻转**（空槽 / 没标记就还原）。
                var reverse = pack != null && level != null
                    && BlueprintOps.GetReverseFaction(level, BlueprintKeys.Slot(i));
                _strip.SetMirror(i, reverse);
                _strip.SetMark(i, IsPicked(i));
            }
        }

        /// <summary>
        /// 自测用：第一行画的到底是不是**卡槽**（与自选卡页同一份数据），卡池镜像对不对。
        /// </summary>
        public static string DebugMirror()
        {
            if (_last == null || !_last._built)
                return "（传送带页未装配）";
            var p = _last;
            var level = LevelFacade.Current;
            var sb = new System.Text.StringBuilder(500);
            if (level == null)
                return "（不在关卡内）";
            var slotCount = BlueprintOps.GetSlotCount(level);
            sb.Append("卡槽数=").Append(slotCount)
              .Append("  镜像格=").Append(p._strip == null ? 0 : p._strip.Count)
              .Append("  判据 第一行=卡槽数=")
              .Append(p._strip != null && p._strip.Count == slotCount ? "OK" : "NG")
              .Append('\n');

            // 卡池的 id 顺序是否与卡槽一致（卡池 = 卡槽 + 权重/数量限制）。
            var same = true;
            for (var i = 0; i < slotCount; i++)
            {
                var pack = BlueprintOps.GetPackAt(level, i);
                var id = pack == null ? null : pack.GetDefinitionID();
                if (!NamespaceID.IsValid(id))
                    continue;
                var poolId = p._pool != null && i < p._pool.Count ? p._pool[i].Id : null;
                if (!BlueprintOps.SameId(id, poolId))
                {
                    same = false;
                    sb.Append("  第 ").Append(i + 1).Append(" 格：卡槽=")
                      .Append(BlueprintOps.BlueprintName(id, false))
                      .Append(" 卡池=").Append(poolId == null ? "（无）" : BlueprintOps.BlueprintName(poolId, false))
                      .Append('\n');
                }
            }
            sb.Append("  判据 卡池镜像卡槽=").Append(same ? "OK" : "NG").Append('\n');

            // 关卡属性里读回来的卡池 = 已经写进存档的那份。
            var back = BlueprintOps.PoolToItems(BlueprintOps.GetPool(level));
            sb.Append("  关卡属性里的卡池=").Append(back.Count).Append(" 项")
              .Append(p._poolWritable ? "（可写入）" : "（写不进）");
            for (var i = 0; i < back.Count; i++)
            {
                sb.Append("\n    #").Append(i).Append(' ')
                  .Append(BlueprintOps.BlueprintName(back[i].Id, false))
                  .Append(" 权重=").Append(back[i].Weight)
                  .Append(" 数量限制=").Append(back[i].Min);
            }
            return sb.ToString();
        }

        /// <summary>自测用：选中卡池里的第一项（验作用域与各字段的显示）。</summary>
        public static string DebugSelectFirst()        {
            if (_last == null || !_last._built)
                return "（传送带页未装配）";
            if (_last._pool == null || _last._pool.Count == 0)
                return "（卡池是空的）";
            _last._selected = 0;
            _last.Refresh();
            return "选中第 1 项 → " + _last.Report() + "\n    " + _last._strip.DescribeTooltip(0);
        }

        /// <summary>自测用：点卡池条第 <paramref name="slot"/> 格（走真寻的点击路径）。</summary>
        public static string DebugPickSlot(int slot)
        {
            if (_last == null || !_last._built || _last._strip == null)
                return "（传送带页未装配）";
            _last.TogglePick(slot);
            return "点卡池第 " + (slot + 1) + " 格 → " + _last.Report();
        }

        public string Report()
        {
            var sb = new System.Text.StringBuilder(220);
            var total = BlueprintOps.TotalWeight(_pool);
            var back = BlueprintOps.PoolToItems(BlueprintOps.GetPool(LevelFacade.Current));
            sb.Append("卡池=").Append(_pool == null ? 0 : _pool.Count)
              .Append("  镜像格=").Append(_strip == null ? 0 : _strip.Count)
              .Append("  总权重=").Append(total)
              .Append("  选中=").Append(_selected < 0 ? "无" : ("第" + (_selected + 1) + "个"))
              .Append("  作用目标=").Append(Targets().Count)
              .Append("  概率字段=").Append(_prob == null ? "无" : _prob.text)
              .Append("  血量字段=").Append(_health == null ? "无" : _health.Text)
              .Append("  概率格=")
              .Append(_probCell == null ? "无"
                  : ("纯文本/" + (_probCell.GetComponentsInChildren<TMP_InputField>(true).Length == 0 ? "无输入框" : "有输入框！")))
              .Append("  关卡属性读回=").Append(back.Count).Append(" 项/总权重=").Append(BlueprintOps.TotalWeight(back))
              .Append(_poolWritable ? "（可写入）" : "（写不进）")
              .Append('\n');
            if (_strip != null)
                sb.Append(_strip.DebugRects("卡池条"));
            for (var i = 0; _pool != null && i < _pool.Count; i++)
            {
                var it = _pool[i];
                sb.Append("  #").Append(i)
                  .Append(IsPicked(i) ? " [x] " : " [ ] ")
                  .Append(BlueprintOps.BlueprintName(it.Id, false))
                  .Append(" 权重=").Append(it.Weight)
                  .Append(" 下限=").Append(it.Min)
                  .Append(" 概率=").Append(total <= 0 ? "-" : (it.Weight * 100f / total).ToString("0.#"))
                  .Append('%').Append('\n');
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // 杂项
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // 纯文本格（「当前概率」用）
        // ------------------------------------------------------------------

        /// <summary>没选中时字段的提示。</summary>
        private const string ScopeHint =
            "先在卡池框里点选一个蓝图。\n本页除「无条件放置」以外的功能都只对选中的那个生效。";

        /// <summary>
        /// 造一个**纯文本**的单元格：容器 + 一个铺满它的标签，**没有输入框**。
        /// 宽度弹性、高度与其它格一致，所以不会影响面板高度。
        /// </summary>
        private static GameObject AddTextCell(UiGridRow row, string name, string labelText)
        {
            if (row == null)
                return null;
            var cell = UiContainer.Instantiate(null, name);
            if (!UiFinder.IsAlive(cell))
                return null;
            UiContainer.FlushHorizontal(cell);
            var label = UiKit.Label(cell.transform, "Text", labelText + "：-");
            if (label != null)
            {
                UiContainer.FillParent(label.gameObject, cell.transform);
                // 铺满之后不再需要「宽度按文字钉死」的设定。
                var le = label.GetComponent<LayoutElement>();
                if (le != null)
                {
                    le.minWidth = -1f;
                    le.preferredWidth = -1f;
                    le.flexibleWidth = 1f;
                }
            }
            row.AddCell(cell, UiKit.RowHeight);
            return cell;
        }

        /// <summary>取纯文本格里的标签（构造时返回的是格子本身）。</summary>
        private static TextMeshProUGUI TextOfCell(GameObject cell)
        {
            if (!UiFinder.IsAlive(cell))
                return null;
            try { return cell.GetComponentInChildren<TextMeshProUGUI>(true); }
            catch (Exception) { return null; }
        }

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

        private static bool TryParse(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return float.TryParse(text.Trim().Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static string Fmt(float v)
        {
            return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private string Describe()
        {
            return "卡池=" + (_pool == null ? 0 : _pool.Count)
                + " 总权重=" + BlueprintOps.TotalWeight(_pool)
                + " 镜像=" + (_strip == null ? "无" : _strip.Describe());
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
    }
}
