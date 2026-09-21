using System;
using System.Collections.Generic;
using PVZEngine;
using TMPro;
using UnityEngine;

namespace GraphicConsole
{
    /// <summary>
    /// 关卡外面板 · 页面1「进度」。
    ///
    /// 布局（按需求的"内容列表组件"约定：**列表一列、相关控件一列**）：
    /// <code>
    /// ┌─ 顶部整行 ─────────────────────────────────────────────┐
    /// │ 绿宝石:__  {解锁全部关卡} {解锁全部成就} {移除商店已购买项}  │
    /// ├─ 左列（内容列表）──────────┬─ 右列（相关控件）───────────┤
    /// │ |已解锁条目|（可点选）        │ 解锁条目:&lt;&gt;（只列未解锁的）  │
    /// │                             │ {添加}  {删除}              │
    /// │                             │ 状态文本                    │
    /// └─────────────────────────────┴────────────────────────────┘
    /// </code>
    ///
    /// 关键语义（需求的"待修改"里点名的）：
    ///   · 列表里显示的是**已启用（已解锁）**的条目；
    ///   · 右侧下拉框**只列还未解锁**的条目 —— 所以「添加」是真的"加入"，
    ///     而不是"把列表里已有的项改成开"；
    ///   · 「删除」作用于**列表里选中的那一项**（把它反解锁）。
    ///
    /// 关于按钮的可用性：添加在那个下拉框没有可选项时禁用；删除在没有选中列表项时禁用。
    /// </summary>
    internal sealed class GlobalProgressPage : PanelPageBase
    {
        public override string Title { get { return "进度"; } }

        private const float RowH = 46f;
        private const float RowGap = 4f;
        private const float TopY = 0f;
        private const float ColumnsY = RowH + RowGap + 4f;
        private const float LeftX = 0f;
        private const float RightX = GcLayout.ContentWidth - GcLayout.ControlColumnWidth;
        private const float LeftWidth = RightX - GcLayout.ColumnGap;

        private WidgetCell cMoney;
        private WidgetCell bUnlockLevels, bUnlockAchievements, bRemoveStore;
        private WidgetCell cAddEntry;
        private WidgetCell cRemoveEntry;
        private WidgetCell cPickEntry;

        private GcListView list;
        private TextMeshProUGUI statusLabel;

        /// <summary>全部可解锁条件。</summary>
        private readonly List<NamespaceID> allEntries = new List<NamespaceID>();
        /// <summary>当前列表显示的（= 已解锁的）条目，与列表行一一对应。</summary>
        private readonly List<NamespaceID> shownEntries = new List<NamespaceID>();
        /// <summary>右侧下拉框里的（= 未解锁的）条目，与下拉选项一一对应。</summary>
        private readonly List<NamespaceID> lockedEntries = new List<NamespaceID>();

        private bool listBuilt;
        /// <summary>是否已经至少加载过一次条目。首帧必须强制加载一次 ——
        /// 否则 <see cref="NeedsReload"/> 在"什么都没加载"的情况下会算出两边都是 0 而返回 false，
        /// 结果列表永远是空的、下拉框保留 TMP 自带的 Placeholder 选项。</summary>
        private bool entriesLoaded;

        protected override void OnBuild()
        {
            // ---- 顶部整行：绿宝石 + 三个批量操作 ----
            var top = NewRow(Content, "TopRow", TopY, RowH, RowGap);
            if (top != null)
            {
                Position(top, LeftX, GcLayout.ContentWidth, TopY, RowH);
                cMoney = NewCell(top, "money", CellKind.Number, "绿宝石", 250f, 90f, false);
                bUnlockLevels = NewCell(top, "unlock_levels", CellKind.Button, "解锁全部关卡", 160f);
                bUnlockAchievements = NewCell(top, "unlock_achievements", CellKind.Button, "解锁全部成就", 160f);
                bRemoveStore = NewCell(top, "remove_store", CellKind.Button, "移除商店已购买项", 190f);
                Bind(bUnlockLevels, OnUnlockLevels);
                Bind(bUnlockAchievements, OnUnlockAchievements);
                Bind(bRemoveStore, OnRemoveStore);
            }

            // ---- 左列：内容框 + 内容列表 ----
            var leftHolder = UiFactory.NewRect(Content, "ListColumn");
            var listHeight = GcLayout.ContentHeight - ColumnsY - 4f;
            if (listHeight < 120f)
                listHeight = 120f;
            if (leftHolder != null)
            {
                Position(leftHolder.transform, LeftX, LeftWidth, ColumnsY, listHeight);
                // 需求：内容列表要有一个**内容框**包着。
                // 用原版输入框/下拉框那块 form_hollow 空心边框（克隆模板 → 清子节点 →
                // 干掉 TMP_InputField），raycastTarget=false 免得吃掉列表行的点击。
                BuildListFrame(leftHolder.transform);
                var inner = UiFactory.NewRect(leftHolder.transform, "ListInner");
                if (inner != null)
                {
                    const float pad = 8f;
                    var innerRt = UiFactory.RectOf(inner);
                    if (innerRt != null)
                    {
                        innerRt.anchorMin = new Vector2(0f, 0f);
                        innerRt.anchorMax = new Vector2(1f, 1f);
                        innerRt.pivot = new Vector2(0.5f, 0.5f);
                        innerRt.offsetMin = new Vector2(pad, pad);
                        innerRt.offsetMax = new Vector2(-pad, -pad);
                    }
                    list = new GcListView();
                    if (!list.Build(inner.transform, "UnlockList", listHeight - pad * 2f))
                    {
                        Plugin.Log?.LogError("[Progress] 内容列表创建失败（按钮模板缺失？）");
                        list = null;
                    }
                    else
                    {
                        list.OnSelectionChanged = OnListSelectionChanged;
                        listBuilt = true;
                    }
                }
            }

            // ---- 右列：选择框 + 添加 / 删除 + 状态 ----
            var y = ColumnsY;
            var pickRow = NewRow(Content, "PickRow", y, RowH, RowGap);
            if (pickRow != null)
            {
                Position(pickRow, RightX, GcLayout.ControlColumnWidth, y, RowH);
                cPickEntry = NewCell(pickRow, "entry_pick", CellKind.Dropdown, "解锁条目", GcLayout.ControlColumnWidth, 96f, true);
            }
            y += RowH + RowGap;

            var buttonRow = NewRow(Content, "EntryButtons", y, RowH, RowGap);
            if (buttonRow != null)
            {
                Position(buttonRow, RightX, GcLayout.ControlColumnWidth, y, RowH);
                cAddEntry = NewCell(buttonRow, "entry_add", CellKind.Button, "添加", 150f);
                cRemoveEntry = NewCell(buttonRow, "entry_remove", CellKind.Button, "删除", 150f);
                Bind(cAddEntry, OnAddEntry);
                Bind(cRemoveEntry, OnRemoveEntry);
            }
            y += RowH + RowGap;

            statusLabel = NewHint(Content, y, 96f, string.Empty, new Color(1f, 0.85f, 0.4f, 1f));
            if (statusLabel != null)
                Position(statusLabel.transform, RightX, GcLayout.ControlColumnWidth, y, 96f);

            RegisterTooltips();
        }

        private void RegisterTooltips()
        {
            AddTooltip(bUnlockAchievements,
                "原版成就是**条件驱动**的（AchievementMeta.Unlock 是一串条件），\n"
                + "没有\"按成就解锁\"的入口，所以这里实际做的是：解锁全部已注册的解锁条件（含关卡与商店）。");
            AddTooltip(bRemoveStore,
                "遍历原版商店产物表，按 ProductMeta.Stages[].Unlocks 把已购项**反解锁**（收回购买记录）。\n"
                + "会立刻写入用户存档，不可撤销。");
            AddTooltip(cPickEntry,
                "只列出**还没有解锁**的条目。\n"
                + "选中后点「添加」即可解锁。（其他页面若条目太多，会改成弹出面板可视化选择。）");
            AddTooltip(cRemoveEntry, "把左列列表中**选中的那一项**反解锁（收回）。");
            AddTooltip(cMoney, "直接改写用户存档里的绿宝石数量。会立刻写入存档，不可撤销。");
        }

        private void AddTooltip(WidgetCell cell, string text)
        {
            if (cell == null || Shell == null || text == null)
                return;
            Shell.AddTooltip(cell, text);
        }

        /// <summary>
        /// 给内容列表铺一个原版的空心边框（form_hollow）。
        /// 就是原版输入框/下拉框那块底板 —— 克隆一份、清掉子节点与 TMP_InputField。
        /// </summary>
        private static void BuildListFrame(Transform parent)
        {
            try
            {
                var go = UiFactory.Clone(UiSources.InputTemplate, parent, "ListFrame");
                if (go == null)
                    return;
                UiFactory.ClearChildren(go);
                var input = go.GetComponent<TMP_InputField>();
                if (input != null)
                    UnityEngine.Object.Destroy(input);
                var image = go.GetComponent<UnityEngine.UI.Image>();
                if (image != null)
                    image.raycastTarget = false;
                UiFactory.Stretch(go.transform);
                go.SetActive(true);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Progress] 列表边框创建失败：" + e.Message);
            }
        }

        private static void Bind(WidgetCell cell, Action onClick)
        {
            if (cell != null && cell.Button != null)
                UiFactory.BindClick(cell.Button, onClick);
        }

        /// <summary>把容器摆到内容区左上角坐标系的指定位置（x 向右、y 向下）。</summary>
        private static void Position(Transform t, float x, float width, float y, float height)
        {
            var rt = UiFactory.RectOf(t);
            if (rt == null)
                return;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(width, height);
            rt.anchoredPosition = new Vector2(x, -y);
        }

        protected override void OnRefresh()
        {
            SyncNumber(cMoney, ProgressOps.GetMoney().ToString(), v => ProgressOps.SetMoney(ClampMoney(v)));

            if (!listBuilt)
                return;
            // 列表与下拉都依赖"当前解锁状态"，状态变了才重建（避免每帧重建几百行）。
            if (NeedsReload())
                ReloadEntries();

            UpdateButtons();
            UpdateStatus();
        }

        private bool NeedsReload()
        {
            // 首帧必须强制加载一次（见 entriesLoaded 的说明）。
            if (!entriesLoaded)
                return true;
            return shownEntries.Count != CountUnlocked() || lockedEntries.Count != allEntries.Count - CountUnlocked();
        }

        private int CountUnlocked()
        {
            var count = 0;
            for (var i = 0; i < allEntries.Count; i++)
            {
                if (ProgressOps.IsUnlocked(allEntries[i]))
                    count++;
            }
            return count;
        }

        private void ReloadEntries()
        {
            allEntries.Clear();
            try
            {
                var game = MVZ2Logic.Global.Game;
                if (game != null)
                {
                    var ids = game.GetAllUnlockConditions();
                    if (ids != null)
                    {
                        for (var i = 0; i < ids.Length; i++)
                        {
                            var id = ids[i];
                            if (NamespaceID.IsValid(id))
                                allEntries.Add(id);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Progress] 读取解锁条件列表失败：" + e.Message);
            }

            var keepIndex = list == null ? -1 : list.SelectedIndex;
            var keepId = keepIndex >= 0 && keepIndex < shownEntries.Count ? shownEntries[keepIndex] : null;
            shownEntries.Clear();
            lockedEntries.Clear();

            // 需求：列表始终排序。按 id 的字符串序排（命名空间在前，所以同类条目会聚在一起）；
            // 排序放在**分流之前**，于是列表（已解锁）与下拉框（未解锁）两边顺序一致。
            allEntries.Sort(CompareId);

            var labels = new List<string>();
            for (var i = 0; i < allEntries.Count; i++)
            {
                var id = allEntries[i];
                if (ProgressOps.IsUnlocked(id))
                {
                    shownEntries.Add(id);
                    labels.Add("✔  " + id.ToString());
                }
                else
                {
                    lockedEntries.Add(id);
                }
            }

            if (list != null)
                list.SetItems(labels);

            // 右侧下拉：只列未解锁的条目
            var pickLabels = new List<string>();
            for (var i = 0; i < lockedEntries.Count; i++)
                pickLabels.Add(lockedEntries[i].ToString());
            UiFactory.SetDropdownOptions(cPickEntry, pickLabels);

            // 尽量保持原来的选中项
            if (list != null && keepId != null)
            {
                var idx = -1;
                for (var i = 0; i < shownEntries.Count; i++)
                {
                    if (LevelFacade.SameId(shownEntries[i], keepId))
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx >= 0)
                    list.Select(idx, false);
            }

            Plugin.Log?.LogInfo("[Progress] 解锁条件 " + allEntries.Count
                + " 项：已解锁 " + shownEntries.Count + "，未解锁 " + lockedEntries.Count);
            entriesLoaded = true;
        }

        /// <summary>排序用比较器：按「命名空间:路径」的原始字符串序，保证每次打开顺序一致。</summary>
        private static int CompareId(NamespaceID a, NamespaceID b)
        {
            var sa = a == null ? string.Empty : a.ToString();
            var sb = b == null ? string.Empty : b.ToString();
            return string.CompareOrdinal(sa, sb);
        }

        private void UpdateButtons()
        {
            SetInteractable(cAddEntry, lockedEntries.Count > 0);
            SetInteractable(cRemoveEntry, list != null && list.HasSelection);
        }

        private void SetInteractable(WidgetCell cell, bool value)
        {
            if (cell == null || cell.Button == null)
                return;
            try
            {
                cell.Button.interactable = value;
            }
            catch (Exception)
            {
            }
        }

        private void UpdateStatus()
        {
            if (statusLabel == null)
                return;
            var text = "已解锁 " + shownEntries.Count + " / " + allEntries.Count + " 项解锁条件";
            var id = SelectedShown();
            if (id != null)
                text += "\n列表选中：" + id.ToString();
            else
                text += "\n列表选中：（无）";
            var pick = SelectedLocked();
            if (pick != null)
                text += "\n下拉选中：" + pick.ToString();
            UiFactory.SetLabelText(statusLabel, text);
        }

        private NamespaceID SelectedShown()
        {
            if (list == null || !list.HasSelection)
                return null;
            var index = list.SelectedIndex;
            if (index < 0 || index >= shownEntries.Count)
                return null;
            return shownEntries[index];
        }

        private NamespaceID SelectedLocked()
        {
            var index = UiFactory.GetDropdownValue(cPickEntry);
            if (index < 0 || index >= lockedEntries.Count)
                return null;
            return lockedEntries[index];
        }

        private void OnListSelectionChanged(int index)
        {
            UpdateButtons();
            UpdateStatus();
        }

        private void OnAddEntry()
        {
            var id = SelectedLocked();
            if (id == null)
            {
                Plugin.Log?.LogWarning("[Progress] 下拉框里没有可添加的条目");
                return;
            }
            ProgressOps.Unlock(id);
            Plugin.Log?.LogInfo("[Progress] 已解锁 " + id.ToString());
            ReloadEntries();
            UpdateButtons();
            UpdateStatus();
        }

        private void OnRemoveEntry()
        {
            var id = SelectedShown();
            if (id == null)
            {
                Plugin.Log?.LogWarning("[Progress] 未选中要删除的条目");
                return;
            }
            ProgressOps.Relock(id);
            Plugin.Log?.LogInfo("[Progress] 已反解锁 " + id.ToString());
            ReloadEntries();
            UpdateButtons();
            UpdateStatus();
        }

        private void OnUnlockLevels()
        {
            var count = ProgressOps.UnlockAllLevels();
            Plugin.Log?.LogInfo("[Progress] 解锁全部关卡完成，新增 " + count + " 项");
            ReloadEntries();
            UpdateButtons();
            UpdateStatus();
        }

        private void OnUnlockAchievements()
        {
            var count = ProgressOps.UnlockAllAchievements();
            Plugin.Log?.LogInfo("[Progress] 解锁全部成就完成，新增 " + count + " 项");
            ReloadEntries();
            UpdateButtons();
            UpdateStatus();
        }

        private void OnRemoveStore()
        {
            var count = ProgressOps.RemovePurchasedStoreItems();
            Plugin.Log?.LogInfo("[Progress] 移除商店已购买项完成，收回 " + count + " 项");
            ReloadEntries();
            UpdateButtons();
            UpdateStatus();
        }

        private static int ClampMoney(long value)
        {
            if (value < 0L)
                return 0;
            if (value > 999999999L)
                return 999999999;
            return (int)value;
        }
    }
}
