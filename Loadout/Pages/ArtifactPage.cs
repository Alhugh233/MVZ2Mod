using System;
using System.Collections.Generic;
using DSHCore.Ui;
using MVZ2.Level.Artifacts;
using MVZ2.Managers;
using MVZ2Logic.Artifacts;
using PVZEngine;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 关卡内 · 页「制品」。
    ///
    /// <para>左侧是**带滚轮的制品列表，一行 4 个**（克隆关卡 HUD 的 <c>ArtifactItemUI</c>，
    /// 所以图标 / 计数 / 灰化都是原版那一套）；右侧从上到下是
    /// 「制品框 → 添加 → 计数/删除 → 失效/清空」。</para>
    ///
    /// <para>⚠️ 需求里的「不限制数量」是真的不限制：槽位是数组，HUD 那边排不下不归本面板管。</para>
    /// </summary>
    internal sealed class ArtifactPage : IPanelPage
    {
        public string Name { get { return "制品"; } }

        private UiArtifactGrid _grid;
        private ArtifactSlot _slotBox;
        private ArtifactItemUI _slotIcon;
        private GameObject _slotBoxGo;
        private TextMeshProUGUI _slotName;
        private UiField _count, _number;
        private UiOptionRow _inactiveRow;
        private NamespaceID _pendingId;
        private bool _built;
        private bool _refreshing;

        private static UiPickWindow _picker;

        public void Build()
        {
            _grid = null;
            _slotBox = null;
            _slotIcon = null;
            _slotBoxGo = null;
            _slotName = null;
            _count = _number = null;
            _inactiveRow = null;
            _pendingId = null;
            _built = false;

            var level = LevelFacade.Current;
            if (level == null)
            {
                AddNote("本页只在关卡内可用（当前不在关卡内）。");
                return;
            }

            var columns = UiGridRow.Create("Row_Artifacts", equalize: false);
            if (columns == null)
                return;

            // ---- 左：可滚动的制品列表 ----
            var left = columns.AddContainer("Col_List", true);
            if (UiFinder.IsAlive(left))
            {
                _grid = UiArtifactGrid.Create(left.transform, "ArtifactList", UiList.SuggestHeight(0.42f));
                if (_grid != null)
                {
                    _grid.OnChanged(Refresh);
                    _grid.SetSlots(ArtifactOps.GetSlotCount(level));
                }
            }

            // ---- 右：操作列 ----
            var right = columns.AddContainer("Col_ArtifactOps", true);
            if (UiFinder.IsAlive(right))
            {
                var boxRow = UiGridRow.Create("Row_ArtifactBox", equalize: false);
                if (boxRow != null)
                {
                    BuildSlotBox(boxRow);
                    LoadoutPanel.AddRow(FixRow(boxRow, right));
                }
                var addRow = UiGridRow.Create("Row_ArtifactAdd", equalize: false);
                if (addRow != null)
                {
                    _count = addRow.AddField("F_AddCount", "添加数量", numeric: true, integer: true);
                    if (_count != null)
                    {
                        _count.Text = "1";
                        LoadoutPanel.AddTooltip(_count.Root, "添加数量", "一次添加几份（各自独立，光环叠加）。");
                    }
                    var add = addRow.AddOptionRow("B_Add", UiOptionKind.Button);
                    if (add != null)
                    {
                        add.SetButton("添加", OnAdd);
                        LoadoutPanel.AddTooltip(add.Root, "添加", "把左边选中的制品加进卡槽。");
                    }
                    LoadoutPanel.AddRow(FixRow(addRow, right));
                }
                var numberRow = UiGridRow.Create("Row_ArtifactNumber", equalize: false);
                if (numberRow != null)
                {
                    _number = numberRow.AddField("F_Number", "计数", numeric: true, integer: true);
                    if (_number != null)
                    {
                        _number.OnSubmit(OnNumberSubmitted);
                        LoadoutPanel.AddTooltip(_number.Root, "计数",
                            "制品上的数字：多数制品是秒倒计时，少数是次数（-1 = 不显示）。");
                    }
                    var del = numberRow.AddOptionRow("B_Delete", UiOptionKind.Button);
                    if (del != null)
                    {
                        del.SetButton("删除", OnDelete);
                        LoadoutPanel.AddTooltip(del.Root, "删除", "删掉列表里选中的那个制品。");
                    }
                    LoadoutPanel.AddRow(FixRow(numberRow, right));
                }
                var stateRow = UiGridRow.Create("Row_ArtifactState", equalize: false);
                if (stateRow != null)
                {
                    _inactiveRow = stateRow.AddOptionRow("T_Inactive", UiOptionKind.Toggle);
                    if (_inactiveRow != null)
                    {
                        _inactiveRow.SetToggle("失效", false, OnInactiveChanged);
                        LoadoutPanel.AddTooltip(_inactiveRow.Root, "失效", "置灰：图标变暗、效果由制品自己决定是否停用。");
                    }
                    var clear = stateRow.AddOptionRow("B_Clear", UiOptionKind.Button);
                    if (clear != null)
                    {
                        clear.SetButton("清空", OnClear);
                        LoadoutPanel.AddTooltip(clear.Root, "清空", "删掉全部制品。");
                    }
                    LoadoutPanel.AddRow(FixRow(stateRow, right));
                }
            }

            LoadoutPanel.AddRow(columns.Finish());

            _built = true;
            Refresh();
            Plugin.Log?.LogInfo("[ArtifactPage] 已装配：" + Describe());
        }

        /// <summary>把「行」挂到右列容器下（行是自己造的，得手动进容器）。</summary>
        private static GameObject FixRow(UiGridRow row, GameObject column)
        {
            var node = row.Finish();
            if (UiFinder.IsAlive(node) && UiFinder.IsAlive(column))
                node.transform.SetParent(column.transform, false);
            return node;
        }

        /// <summary>制品框：克隆原版选卡界面的制品槽（可点 → 开选择窗口）。</summary>
        private void BuildSlotBox(UiGridRow row)
        {
            var template = FindSlotTemplate();
            var holder = row.AddContainer("SlotBox", false);
            if (UiFinder.IsAlive(holder) && UiFinder.IsAlive(template))
            {
                var box = UiClone.Instantiate(template, holder.transform, "Box", true);
                if (UiFinder.IsAlive(box))
                {
                    UiContainer.FitCell(box, 56f, 56f);
                    _slotBoxGo = box;
                    _slotBox = box.GetComponent<ArtifactSlot>();
                    _slotIcon = box.GetComponent<ArtifactItemUI>();
                    // 注：ArtifactSlot.OnClick 在 interop 里是 Il2CppSystem.Action<T>，
                    // C# 编译器不允许 += 一个 lambda。所以不看它的事件，自己在 Tick 里做命中。
                }
            }
            _slotName = UiKit.Label(row.Root.transform, "SlotName", "未选择");
            if (_slotName != null)
            {
                var le = _slotName.gameObject.GetComponent<LayoutElement>();
                if (le == null)
                    le = _slotName.gameObject.AddComponent<LayoutElement>();
                if (le != null)
                    le.preferredHeight = UiKit.RowHeight;
            }
            LoadoutPanel.AddTooltip(row.Root, "制品", "点一下从全部制品里挑一个，再按「添加」。");
        }

        private static GameObject FindSlotTemplate()
        {
            try
            {
                var controller = LevelFacade.Controller;
                if (controller == null)
                    return null;
                var preset = controller.GetUIPreset();
                if (preset == null)
                    return null;

                // 优先：选卡面板里那个“空制品框”（构图上就是我们要的那个框）。
                var chooseUI = preset.BlueprintChoose;
                if (chooseUI != null)
                {
                    var slots = chooseUI.GetComponentsInChildren<ArtifactSlot>(true);
                    if (slots != null && slots.Length > 0)
                        return slots[0].gameObject;
                }
                // 退一步：关卡 HUD 上的制品条目（关卡里一个制品都没有时才拿不到）。
                return UiArtifactGrid.FindTemplate();
            }
            catch (Exception) { return null; }
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
        // 交互
        // ------------------------------------------------------------------

        private void OnAdd()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return;
            var id = _pendingId;
            if (!NamespaceID.IsValid(id))
            {
                var selected = ArtifactOps.IdOf(ArtifactOps.GetAt(level, _grid == null ? -1 : _grid.SelectedSlot));
                id = selected;
            }
            if (!NamespaceID.IsValid(id))
            {
                Plugin.Log?.LogWarning("[ArtifactPage] 还没挑制品（先点一下右侧的制品框）");
                return;
            }
            int count;
            if (!int.TryParse((_count == null ? "1" : _count.Text ?? "1").Trim(), out count) || count <= 0)
                count = 1;
            ArtifactOps.Add(level, id, count);
            LoadoutPanel.Invalidate();
        }

        private void OnDelete()
        {
            var level = LevelFacade.Current;
            if (level == null || _grid == null || _grid.SelectedSlot < 0)
            {
                Plugin.Log?.LogWarning("[ArtifactPage] 没选中任何制品");
                return;
            }
            ArtifactOps.RemoveAt(level, _grid.SelectedSlot);
            LoadoutPanel.Invalidate();
        }

        private void OnClear()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return;
            ArtifactOps.Clear(level);
            LoadoutPanel.Invalidate();
        }

        private void OnInactiveChanged(bool value)
        {
            if (_refreshing)
                return;
            var artifact = Selected();
            ArtifactOps.SetInactive(artifact, value);
            Refresh();
        }

        private void OnNumberSubmitted(string text)
        {
            var artifact = Selected();
            int value;
            if (artifact == null || !int.TryParse((text ?? string.Empty).Trim(), out value))
            {
                Refresh();
                return;
            }
            ArtifactOps.SetNumber(artifact, value);
            Refresh();
        }

        private Artifact Selected()
        {
            var level = LevelFacade.Current;
            if (level == null || _grid == null || _grid.SelectedSlot < 0)
                return null;
            return ArtifactOps.GetAt(level, _grid.SelectedSlot);
        }

        private void OpenPicker()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return;
            if (_picker == null)
                _picker = UiPickWindow.Create("DSH_PickArtifact");
            _picker.OpenArtifacts("选择制品", ArtifactOps.GetSlotCount(level), () =>
            {
                var slot = _picker.ArtifactSlot;
                var artifact = ArtifactOps.GetAt(level, slot);
                _pendingId = ArtifactOps.IdOf(artifact);
                _picker.Close();
                Refresh();
            });
        }

        // ------------------------------------------------------------------
        // 刷新 / 诊断
        // ------------------------------------------------------------------

        public void Tick()
        {
            if (!_built)
                return;
            if (LevelFacade.Current == null)
            {
                LoadoutPanel.Invalidate();
                return;
            }
            // 槽位数被别的途径改了（游戏读档等）→ Refresh 里会比一下并重建。
            Refresh();
            TickSlotBoxClick();
        }

        /// <summary>制品框的点击：自己命中（原版事件在 interop 里挂不上）。</summary>
        private void TickSlotBoxClick()
        {
            try
            {
                if (_slotBoxGo == null || !UiFinder.IsAlive(_slotBoxGo))
                    return;
                if (!Input.GetMouseButtonDown(0))
                    return;
                if (UiHitTest.Contains(_slotBoxGo, (Vector2)Input.mousePosition))
                    OpenPicker();
            }
            catch (Exception) { }
        }

        private string Describe()
        {
            var level = LevelFacade.Current;
            return "槽位=" + (level == null ? 0 : ArtifactOps.GetSlotCount(level))
                + " 待添加=" + (NamespaceID.IsValid(_pendingId) ? ArtifactOps.NameOf(_pendingId) : "（无）");
        }

        private int _lastSlots = -1;

        private void Refresh()
        {
            var level = LevelFacade.Current;
            var artifact = Selected();
            _refreshing = true;
            try
            {
                if (_grid != null && level != null)
                {
                    var count = ArtifactOps.GetSlotCount(level);
                    if (count != _lastSlots)
                    {
                        _lastSlots = count;
                        _grid.SetSlots(count);
                    }
                    else
                    {
                        _grid.Refresh();
                    }
                }
                if (_slotBoxGo != null && UiFinder.IsAlive(_slotBoxGo))
                {
                    var sprite = ArtifactOps.IconOf(_pendingId);
                    try
                    {
                        if (_slotBox != null)
                            _slotBox.UpdateView(new ArtifactViewData { sprite = sprite });
                        else if (_slotIcon != null)
                        {
                            _slotIcon.SetIcon(sprite);
                            _slotIcon.SetNumber(string.Empty);
                        }
                    }
                    catch (Exception) { }
                }
                if (_slotName != null)
                    _slotName.text = NamespaceID.IsValid(_pendingId) ? ArtifactOps.NameOf(_pendingId) : "未选择";
                if (_number != null)
                    _number.Text = artifact == null ? "-" : ArtifactOps.GetNumber(artifact).ToString();
                if (_inactiveRow != null && _inactiveRow.Widgets != null && _inactiveRow.Widgets.Toggle != null)
                    _inactiveRow.Widgets.Toggle.Toggle.SetIsOnWithoutNotify(ArtifactOps.IsInactive(artifact));
            }
            catch (Exception) { }
            finally
            {
                _refreshing = false;
            }
        }

        public string Report()
        {
            var sb = new System.Text.StringBuilder(200);
            var level = LevelFacade.Current;
            var count = level == null ? 0 : ArtifactOps.GetSlotCount(level);
            sb.Append("槽位=").Append(count)
              .Append("  选中=").Append(_grid == null ? -1 : _grid.SelectedSlot)
              .Append("  待添加=").Append(NamespaceID.IsValid(_pendingId) ? ArtifactOps.NameOf(_pendingId) : "（无）")
              .Append('\n');
            for (var i = 0; i < count; i++)
            {
                var artifact = ArtifactOps.GetAt(level, i);
                sb.Append("  #").Append(i).Append(' ');
                if (artifact == null)
                {
                    sb.Append("（空）").Append('\n');
                    continue;
                }
                var auraCount = 0;
                try { auraCount = artifact.GetAuraEffectCount(); }
                catch (Exception) { }
                sb.Append(ArtifactOps.NameOf(ArtifactOps.IdOf(artifact)))
                  .Append(" 计数=").Append(ArtifactOps.GetNumber(artifact))
                  .Append(" 光环=").Append(auraCount)
                  .Append(ArtifactOps.IsInactive(artifact) ? " 失效" : string.Empty)
                  .Append('\n');
            }
            return sb.ToString();
        }
    }
}
