using System;
using System.Collections.Generic;
using DSHCore.Ui;
using Il2CppInterop.Runtime;
using MVZ2.Blueprints;
using MVZ2.Level.Artifacts;
using MVZ2.Level.Blueprints;
using MVZ2.UI;
using MVZ2Logic.Level;
using MVZ2Logic.Models;
using PVZEngine;
using PVZEngine.SeedPacks;
using PVZEngine.Tools.Unity;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 网格化的一行容器（**横向**排列的一行 `<see cref="UiContainer"/>`）。
    /// 三个网格组件共用这一层，免得各写一份行/尺寸处理。
    /// </summary>
    internal static class UiGridLayout
    {
        /// <summary>造一行（横向容器），并把它的高度钉死。</summary>
        public static GameObject NewRow(Transform parent, string name, float height)
        {
            var row = UiContainer.Instantiate(parent, name, false);
            if (!UiFinder.IsAlive(row))
                return null;
            UiContainer.FlushHorizontal(row);
            UiContainer.FitCell(row, 0f, height);
            return row;
        }

        /// <summary>把 <paramref name="node"/> 作为一格放进行里（宽高固定，间隙由行的布局组给）。</summary>
        public static void AddCell(GameObject row, GameObject node, float width, float height)
        {
            if (row == null || !UiFinder.IsAlive(node))
                return;
            try
            {
                node.transform.SetParent(row.transform, false);
                UiContainer.FitCell(node, width, height);
            }
            catch (Exception) { }
        }
    }

    /// <summary>网格里的一格（记着它绑的数据 + 命中的矩形节点）。</summary>
    internal sealed class UiGridCell
    {
        public GameObject Node;
        public NamespaceID Id;
        public int Index;       // 槽位（制品）或数据下标
    }

    /// <summary>
    /// 每帧做「矩形命中」的共用逻辑。
    ///
    /// <para>为什么一律自己命中、不用原版控件的事件：原版控件（<c>Button</c> / <c>Toggle</c> /
    /// <c>LevelPointerInteractionHandler</c>）自己会处理点击，和我们的处理叠在一起就会
    /// **一次点击生效两遍**（本仓库在进度页与补全框都吃过这个）。所以克隆体一律
    /// 关掉 <c>blocksRaycasts</c>，点击只走这里。</para>
    /// </summary>
    internal static class UiHitTest
    {
        /// <summary>挡掉节点自己的鼠标接收（只挡射线，不改颜色）。</summary>
        public static void Mute(GameObject node)
        {
            if (!UiFinder.IsAlive(node))
                return;
            try
            {
                var group = node.GetComponent<CanvasGroup>();
                if (group == null)
                    group = node.AddComponent<CanvasGroup>();
                if (group == null)
                    return;
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = false;
            }
            catch (Exception) { }
        }

        public static bool Contains(GameObject node, Vector2 screenPoint)
        {
            if (!UiFinder.IsAlive(node))
                return false;
            var rt = node.transform.TryCast<RectTransform>();
            if (rt == null)
                return false;
            try
            {
                if (!rt.gameObject.activeInHierarchy)
                    return false;
                return RectTransformUtility.RectangleContainsScreenPoint(rt, screenPoint, LoadoutPanel.CanvasCamera());
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 制品网格：**带滚轮的列表，一行至多 4 个**（克隆关卡 HUD 的 <c>ArtifactItemUI</c>，
    /// 所以图标 / 计数 / 灰化都是原版那一套）。
    ///
    /// <para>选中高亮是自绘的半透明色块（原版制品条目不提供选中态，而诅咒条目的选中美术
    /// 是私有字段，拿不到）。</para>
    /// </summary>
    internal sealed class UiArtifactGrid
    {
        public const int Columns = 4;

        private readonly GameObject _root;
        private readonly GameObject _template;
        private readonly UiList _list;
        private readonly List<UiGridCell> _cells = new List<UiGridCell>();
        private readonly List<GameObject> _rows = new List<GameObject>();
        private readonly List<GameObject> _frames = new List<GameObject>();
        private float _itemW = 64f, _itemH = 64f;

        private int _selectedSlot = -1;
        private Action _onChanged;
        private bool _built;

        private UiArtifactGrid(GameObject root, GameObject template, UiList list)
        {
            _root = root;
            _template = template;
            _list = list;
        }

        private static readonly List<UiArtifactGrid> Live = new List<UiArtifactGrid>();

        public static void TickAll()
        {
            for (var i = 0; i < Live.Count; i++)
            {
                try { Live[i].Tick(); }
                catch (Exception) { }
            }
        }

        public static void ForgetAll()
        {
            Live.Clear();
        }

        public GameObject Root { get { return UiFinder.IsAlive(_root) ? _root : null; } }

        public int SelectedSlot { get { return _selectedSlot; } }

        public static UiArtifactGrid Create(Transform parent, string name, float viewportHeight)
        {
            var template = FindTemplate();
            if (!UiFinder.IsAlive(template))
            {
                Plugin.LogOnce("artgrid-tpl", "[ArtGrid] 拿不到关卡 HUD 的制品条目模板，制品列表不可用。");
                return null;
            }
            var list = UiList.Create(parent, name);
            if (list == null)
                return null;
            list.SetViewportHeight(viewportHeight);
            var grid = new UiArtifactGrid(list.Root, template, list);
            Live.Add(grid);
            return grid;
        }

        public static GameObject FindTemplate()
        {
            try
            {
                var controller = LevelFacade.Controller;
                if (controller == null)
                    return null;
                var preset = controller.GetUIPreset();
                if (preset == null)
                    return null;

                // ⚠️ 不要用 LevelUIPreset.GetArtifactAt / LevelUIBlueprintChoose.GetArtifactSlotAt：
                // 它们内部是 il2cpp 泛型方法 getElement<T>，interop 上要过 MethodInfoStoreGeneric，
                // 实测拿不到东西。直接扫子节点上的组件，稳。
                var items = preset.GetComponentsInChildren<ArtifactItemUI>(true);
                if (items != null && items.Length > 0)
                    return items[0].gameObject;

                var slots = preset.GetComponentsInChildren<ArtifactSlot>(true);
                if (slots != null && slots.Length > 0)
                    return slots[0].gameObject;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("artgrid-find", "[ArtGrid] 找制品模板失败：" + e.GetType().Name + ": " + e.Message);
            }
            return null;
        }

        public UiArtifactGrid OnChanged(Action handler)
        {
            _onChanged = handler;
            return this;
        }

        /// <summary>按槽位重建（一行 4 个）。</summary>
        public void SetSlots(int count)
        {
            Clear();
            var level = LevelFacade.Current;
            if (level == null)
                return;
            EnsureSize();
            for (var slot = 0; slot < count; slot += Columns)
            {
                var row = UiGridLayout.NewRow(_list.Content, "Row" + (slot / Columns), _itemH);
                if (row == null)
                    continue;
                _rows.Add(row);
                for (var c = 0; c < Columns && slot + c < count; c++)
                {
                    var index = slot + c;
                    var item = UiClone.Instantiate(_template, row.transform, "Art" + index, true);
                    if (!UiFinder.IsAlive(item))
                        continue;
                    UiHitTest.Mute(item);
                    UiGridLayout.AddCell(row, item, _itemW, _itemH);

                    // 选中高亮：item 后面垫一层半透明色块（不吃射线，不影响点击）。
                    var frame = UiHost.CreateNode("Sel" + index, null);
                    if (UiFinder.IsAlive(frame))
                    {
                        frame.transform.SetParent(row.transform, false);
                        var img = frame.AddComponent<Image>();
                        if (img != null)
                        {
                            img.color = new Color(1f, 0.85f, 0.2f, 0.28f);
                            img.raycastTarget = false;
                        }
                        UiContainer.FitCell(frame, _itemW, _itemH);
                        frame.transform.SetSiblingIndex(item.transform.GetSiblingIndex());
                        frame.SetActive(false);
                        _frames.Add(frame);
                    }
                    _cells.Add(new UiGridCell { Node = item, Index = index });
                }
            }
            _built = true;
            Refresh();
            LoadoutPanel.RebuildLayout();
        }

        private void EnsureSize()
        {
            if (_itemW > 1f)
                return;
            var rt = _template == null ? null : _template.transform.TryCast<RectTransform>();
            _itemW = rt != null && rt.rect.width > 1f ? rt.rect.width : 64f;
            _itemH = rt != null && rt.rect.height > 1f ? rt.rect.height : 64f;
        }

        public void Refresh()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return;
            for (var i = 0; i < _cells.Count; i++)
            {
                var cell = _cells[i];
                var artifact = ArtifactOps.GetAt(level, cell.Index);
                if (!UiFinder.IsAlive(cell.Node))
                    continue;
                var ui = cell.Node.GetComponent<ArtifactItemUI>();
                if (ui != null)
                {
                    var id = ArtifactOps.IdOf(artifact);
                    try
                    {
                        ui.SetIcon(artifact == null ? null : ArtifactOps.IconOf(id));
                        var number = ArtifactOps.GetNumber(artifact);
                        var text = ArtifactOps.GetDisplayText(artifact);
                        ui.SetNumber(string.IsNullOrEmpty(text) ? (number < 0 ? string.Empty : number.ToString()) : text);
                        ui.SetGrayscale(ArtifactOps.IsInactive(artifact));
                        ui.SetGlowing(ArtifactOps.IsGlowing(artifact));
                    }
                    catch (Exception) { }
                }
                if (i < _frames.Count && UiFinder.IsAlive(_frames[i]))
                {
                    try { _frames[i].SetActive(cell.Index == _selectedSlot); }
                    catch (Exception) { }
                }
            }
        }

        public void Tick()
        {
            if (!_built || _list == null || !_list.IsValid)
                return;
            try
            {
                if (!UiFinder.IsAlive(_root) || !_root.activeInHierarchy)
                    return;
                if (Input.GetMouseButtonDown(0))
                {
                    var pointer = (Vector2)Input.mousePosition;
                    for (var i = 0; i < _cells.Count; i++)
                    {
                        var cell = _cells[i];
                        if (!UiHitTest.Contains(cell.Node, pointer))
                            continue;
                        _selectedSlot = _selectedSlot == cell.Index ? -1 : cell.Index;
                        Refresh();
                        _onChanged?.Invoke();
                        return;
                    }
                }
            }
            catch (Exception) { }
        }

        private void Clear()
        {
            for (var i = 0; i < _frames.Count; i++)
            {
                if (UiFinder.IsAlive(_frames[i]))
                    UnityEngine.Object.Destroy(_frames[i]);
            }
            for (var i = 0; i < _rows.Count; i++)
            {
                if (UiFinder.IsAlive(_rows[i]))
                    UnityEngine.Object.Destroy(_rows[i]);
            }
            _frames.Clear();
            _rows.Clear();
            _cells.Clear();
            _selectedSlot = -1;
        }
    }
}
