using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GraphicConsole
{
    /// <summary>
    /// 内容列表 —— 一列**可点选的纯文本条目**（行为对齐存档修改器里的 <c>ttk.Treeview</c>：
    /// 单选、点击即选中、上下滚动、选中态高亮）。
    ///
    /// 为什么自己搭而不是克隆现成的列表：原版的列表都是"模板 + 数据项"的复合结构
    /// （<c>ElementList</c> + 专用条目 prefab，例如资源包列表、诅咒列表），体积与复杂度都远超
    /// 需求，而且条目外观是给它自己的数据用的。本组件只需要纯文本条目，所以用
    /// <c>RectMask2D</c>（不需要图形资源）+ 现造的布局组 + 克隆的按钮节点搭出来，
    /// 这样条目外观（背景 / 文字 / 点击音效 / 悬停）仍然是原版的。
    ///
    /// 结构：
    /// <code>
    /// ListRoot   RectTransform + LayoutElement(高) + ScrollRect
    ///   Viewport RectTransform + RectMask2D      ← 用 RectMask2D 而不是 Mask：
    ///                                                Mask 需要一颗 Image 才能裁剪，
    ///                                                而原版 Image 都挂 ImageResolver 会改 sprite
    ///     Content RectTransform + VerticalLayoutGroup + ContentSizeFitter(v=PreferredSize)
    ///       Row × N（克隆按钮，LayoutElement 高 = 行高）
    /// </code>
    /// </summary>
    internal sealed class GcListView
    {
        private GameObject root;
        private RectTransform viewport;
        private RectTransform content;
        private ScrollRect scroll;
        private readonly List<GameObject> rows = new List<GameObject>();
        private readonly List<TMPro.TextMeshProUGUI> rowLabels = new List<TMPro.TextMeshProUGUI>();
        private List<string> items = new List<string>();

        public int SelectedIndex { get; private set; } = -1;
        public bool Exists { get { return UiFinder.IsAlive(root); } }
        public int Count { get { return items.Count; } }
        public GameObject Root { get { return root; } }

        /// <summary>选中项变化时回调（参数是索引，-1 = 没选中）。</summary>
        public Action<int> OnSelectionChanged;

        public bool Build(Transform parent, string name, float height)
        {
            if (Exists)
                return true;
            if (!UiFinder.IsAlive(UiSources.ButtonTemplate))
                return false;

            root = UiFactory.NewRect(parent, name);
            if (root == null)
                return false;
            // ⚠️ 必须铺满父节点：NewRect 造出来的 RectTransform 是默认的 100x100 锚在中心，
            // 不铺满的话整个列表只有 100x100（行宽、裁剪区、滚动全都因此错乱）——
            // 这正是「内容列表行为诡异」的直接原因。
            UiFactory.Stretch(root.transform);
            UiFactory.Layout(root, height, 0f);
            var rootRt = UiFactory.RectOf(root);

            var viewportGo = UiFactory.NewRect(root.transform, "Viewport");
            if (viewportGo == null)
                return false;
            viewport = UiFactory.RectOf(viewportGo);
            UiFactory.Stretch(viewport);
            // RectMask2D：不需要图形资源就能裁剪，正好避开 ImageResolver 的坑。
            viewportGo.AddComponent<RectMask2D>();

            var contentGo = UiFactory.NewRect(viewportGo.transform, "Content");
            if (contentGo == null)
                return false;
            content = UiFactory.RectOf(contentGo);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.sizeDelta = new Vector2(0f, 0f);
            var group = UiFactory.ConfigureVertical(contentGo, 0f, 0);
            if (group != null)
            {
                group.childForceExpandHeight = false;
                group.childControlHeight = true;
            }
            var fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            try
            {
                scroll = root.AddComponent<ScrollRect>();
                scroll.content = content;
                scroll.viewport = viewport;
                scroll.horizontal = false;
                scroll.vertical = true;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.scrollSensitivity = 30f;
                scroll.inertia = false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[List] 创建 ScrollRect 失败：" + e);
                return false;
            }
            return true;
        }

        public void SetItems(IList<string> newItems)
        {
            items = new List<string>();
            if (newItems != null)
            {
                for (var i = 0; i < newItems.Count; i++)
                    items.Add(newItems[i] ?? string.Empty);
            }

            // 整表重建：条目数可能从几十变到几百，重建比增量维护可靠；
            // 重建时把旧条目的委托登记一起回收（见 UiFactory.PruneKeepAlive）。
            ClearRows();
            SelectedIndex = -1;
            if (content != null)
            {
                for (var i = 0; i < items.Count; i++)
                    CreateRow(i);
            }
            UiFactory.PruneKeepAlive();
        }

        private void CreateRow(int index)
        {
            var row = UiFactory.Clone(UiSources.ButtonTemplate, content, "Row_" + index);
            if (row == null)
                return;
            UiFactory.Layout(row, GcLayout.ListRowHeight, 0f, -1f, 1f);

            var label = UiFinder.FindChildComponent<TMPro.TextMeshProUGUI>(row.transform);
            UiFactory.SetLabelText(label, items[index]);
            if (label != null)
            {
                try
                {
                    // 固定字号（关自动缩放）—— 否则原版按钮文本的自动缩放
                    // 会把列表条目算成 8px，根本看不清。
                    UiFactory.StyleLabel(label, GcLayout.SmallFontSize, false, TMPro.TextAlignmentOptions.Left);
                }
                catch (Exception)
                {
                }
            }

            var button = row.GetComponent<UnityEngine.UI.Button>();
            var captured = index;
            UiFactory.BindClick(button, () => Select(captured));

            rows.Add(row);
            rowLabels.Add(label);
            row.SetActive(true);
            ApplyRowColor(index);
        }

        private void ClearRows()
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row == null)
                    continue;
                try
                {
                    row.SetActive(false);
                    UnityEngine.Object.Destroy(row);
                }
                catch (Exception)
                {
                }
            }
            rows.Clear();
            rowLabels.Clear();
        }

        /// <summary>选中某项（不触发回调）。</summary>
        public void Select(int index, bool notify = true)
        {
            if (index < -1 || index >= items.Count)
                return;
            var before = SelectedIndex;
            SelectedIndex = index;
            for (var i = 0; i < rows.Count; i++)
                ApplyRowColor(i);
            if (index >= 0)
                ScrollTo(index);
            if (notify && before != index && OnSelectionChanged != null)
            {
                try
                {
                    OnSelectionChanged(index);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning("[List] 选中回调异常：" + e.Message);
                }
            }
        }

        public void ClearSelection()
        {
            Select(-1);
        }

        /// <summary>是否有选中项（供"无选中时禁用按钮"用）。</summary>
        public bool HasSelection { get { return SelectedIndex >= 0 && SelectedIndex < items.Count; } }

        /// <summary>当前选中项的原始文本（没选中返回 null）。</summary>
        public string SelectedText
        {
            get
            {
                if (!HasSelection)
                    return null;
                return items[SelectedIndex];
            }
        }

        private void ApplyRowColor(int index)
        {
            if (index < 0 || index >= rows.Count)
                return;
            var row = rows[index];
            if (row == null)
                return;
            try
            {
                var image = row.GetComponent<UnityEngine.UI.Image>();
                if (image == null)
                    return;
                image.color = index == SelectedIndex ? GcLayout.ListRowSelected : GcLayout.ListRowNormal;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>把某一行滚进视野（手动算，不依赖 ScrollRect 的内部实现）。</summary>
        private void ScrollTo(int index)
        {
            if (scroll == null || viewport == null || content == null)
                return;
            try
            {
                var viewH = viewport.rect.height;
                var contentH = content.rect.height;
                if (viewH <= 0f || contentH <= viewH)
                {
                    scroll.verticalNormalizedPosition = 1f;
                    return;
                }
                // ScrollRect 的 verticalNormalizedPosition：1 = 顶部，0 = 底部。
                var rowTop = index * GcLayout.ListRowHeight;
                var rowBottom = rowTop + GcLayout.ListRowHeight;
                var currentTop = (1f - scroll.verticalNormalizedPosition) * (contentH - viewH);
                var currentBottom = currentTop + viewH;
                if (rowTop < currentTop)
                {
                    scroll.verticalNormalizedPosition = 1f - (rowTop / (contentH - viewH));
                }
                else if (rowBottom > currentBottom)
                {
                    var target = rowBottom - viewH;
                    scroll.verticalNormalizedPosition = 1f - (target / (contentH - viewH));
                }
            }
            catch (Exception)
            {
            }
        }

        public void Destroy()
        {
            ClearRows();
            try
            {
                if (root != null)
                    UnityEngine.Object.Destroy(root);
            }
            catch (Exception)
            {
            }
            root = null;
            content = null;
            viewport = null;
            scroll = null;
        }
    }
}
