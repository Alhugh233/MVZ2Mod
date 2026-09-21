using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GraphicConsole
{
    /// <summary>
    /// 自绘的「选择浮层」—— 面板里所有下拉格都改用它，不再用 TMP_Dropdown 自带的弹窗。
    ///
    /// 为什么不用 TMP 的：实测在本环境（主 UI 画布 <c>GlobalUICanvas</c> 是
    /// <b>Screen Space - Camera</b> + UICamera）里，TMP 生成的 <c>Dropdown List</c> 节点
    /// <b>一个像素都画不出来</b>，但它的 GraphicRaycaster 照常工作：
    ///   · 节点 active、<c>Image.enabled=1</c>、颜色 alpha=1、<c>CanvasGroup.alpha=1</c>；
    ///   · <c>Canvas overrideSorting=1 order=30000 layer=Dialog</c>，屏幕矩形完全正确；
    ///   · 「展开前 / 展开后」两张截图的**像素差分≈0**（差异像素全在别处）；
    ///   · 已经试过的修法全部无效：把弹窗画布的 renderMode/worldCamera/planeDistance 对齐根画布、
    ///     关掉 overrideSorting、甚至把整个弹窗节点挪到面板 Content 的最末尾 —— 画面零变化。
    /// 用户看到的就是需求文档里那句「下拉选择框点开后不显示 UI，乱点能注意到功能有效」。
    ///
    /// 所以改成自绘：浮层就是面板内部的一层普通 UI（克隆原版按钮 / 文本），走的是本 mod
    /// 已经验证过的渲染路径；选项多了用**分页**，不依赖 ScrollRect + Mask（那套同样没验证过）。
    /// </summary>
    internal sealed class PickerOverlay
    {
        /// <summary>当前面板的选择浮层（同一时刻只会存在一个面板）。</summary>
        public static PickerOverlay Shared;

        private const int Columns = 3;
        private const int Rows = 11;
        /// <summary>每页条目数。</summary>
        private const int PageSize = Columns * Rows;

        private const float Pad = 10f;
        private const float TitleHeight = 40f;
        private const float CellHeight = 42f;
        private const float CellGap = 6f;
        private const float BottomHeight = 40f;

        private static readonly Color Backdrop = new Color(0.05f, 0.05f, 0.08f, 0.94f);
        private static readonly Color RowNormal = new Color(1f, 1f, 1f, 1f);
        private static readonly Color RowSelected = new Color(0.45f, 0.8f, 1f, 1f);

        private readonly RectTransform dialogRect;
        private GameObject root;
        private TextMeshProUGUI titleLabel;
        private TextMeshProUGUI pageLabel;
        private Button prevButton;
        private Button nextButton;
        private readonly List<GameObject> rows = new List<GameObject>();
        private readonly List<TextMeshProUGUI> rowLabels = new List<TextMeshProUGUI>();
        private readonly List<Image> rowImages = new List<Image>();

        private IList<string> options;
        private int selected = -1;
        private int page;
        private Action<int> onPick;

        public PickerOverlay(RectTransform dialogRect)
        {
            this.dialogRect = dialogRect;
            Shared = this;
        }

        public bool IsOpen
        {
            get { return UiFinder.IsAlive(root) && root.activeSelf; }
        }

        #region 构建
        private bool Build()
        {
            if (UiFinder.IsAlive(root))
                return true;
            if (dialogRect == null)
                return false;

            try
            {
                // 浮层挂在 Dialog 下并铺满它。
                // ⚠️ Dialog 上有 VerticalLayoutGroup（Header/Content/Footer），
                //    所以必须 IgnoreLayout，否则布局组会把浮层排到别处。
                root = UiFactory.NewRect(dialogRect, "GcPicker");
                if (root == null)
                    return false;
                UiFactory.Stretch(root.transform);
                UiFactory.IgnoreLayout(root);
                root.transform.SetAsLastSibling();

                // 底板：克隆原版按钮的 Image（现造 Image 会缺 sprite 且没有 ImageResolver）。
                // 用它同时吃掉点击（空白处点击不该穿透到下面的控件）。
                var bg = UiFactory.Clone(UiSources.ButtonTemplate, root.transform, "Bg");
                if (bg != null)
                {
                    UiFactory.Stretch(bg.transform);
                    var bgImage = bg.GetComponent<Image>();
                    if (bgImage != null)
                        bgImage.color = Backdrop;
                    var bgButton = bg.GetComponent<Button>();
                    if (bgButton != null)
                    {
                        bgButton.transition = Selectable.Transition.None;
                        UiFactory.BindClick(bgButton, Noop);
                    }
                }

                titleLabel = UiFactory.MakeLabel(root.transform, string.Empty, GcLayout.FontSize);
                Place(titleLabel, Pad, Pad, GcLayout.Width - Pad * 2f - 200f, TitleHeight);

                // 取消按钮（右上角）
                var cancel = UiFactory.Clone(UiSources.ButtonTemplate, root.transform, "Cancel");
                if (cancel != null)
                {
                    Place(cancel, GcLayout.Width - Pad - 180f, Pad, 180f, TitleHeight);
                    var label = LabelOf(cancel);
                    UiFactory.StyleLabel(label, GcLayout.FontSize);
                    UiFactory.SetLabelText(label, "取消");
                    var button = cancel.GetComponent<Button>();
                    if (button != null)
                        UiFactory.BindClick(button, Close);
                }

                // 选项按钮：一次造满一页，之后翻页只改文字/高亮/显隐。
                for (var i = 0; i < PageSize; i++)
                {
                    var go = UiFactory.Clone(UiSources.ButtonTemplate, root.transform, "Item" + i);
                    var label = go == null ? null : LabelOf(go);
                    var image = go == null ? null : go.GetComponent<Image>();
                    var button = go == null ? null : go.GetComponent<Button>();
                    if (label != null)
                        UiFactory.StyleLabel(label, GcLayout.SmallFontSize);
                    var slot = i;
                    if (button != null)
                        UiFactory.BindClick(button, () => OnRowClicked(slot));
                    rows.Add(go);
                    rowLabels.Add(label);
                    rowImages.Add(image);
                    if (go != null)
                        go.SetActive(false);
                }

                // 底部：上一页 / 页码 / 下一页
                prevButton = MakeBottomButton("上页", 0f, 200f, () => TurnPage(-1));
                nextButton = MakeBottomButton("下页", GcLayout.Width - Pad - 200f, 200f, () => TurnPage(1));
                pageLabel = UiFactory.MakeLabel(root.transform, string.Empty, GcLayout.FontSize);
                Place(pageLabel, Pad + 210f, GcLayout.Height - Pad - BottomHeight, GcLayout.Width - Pad * 2f - 420f, BottomHeight);
                if (pageLabel != null)
                    pageLabel.alignment = TextAlignmentOptions.Center;

                root.SetActive(false);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Picker] 构建失败：" + e.GetType().Name + ": " + e.Message);
                Destroy();
                return false;
            }
        }

        private Button MakeBottomButton(string text, float x, float width, Action handler)
        {
            var go = UiFactory.Clone(UiSources.ButtonTemplate, root.transform, "Btn" + text);
            if (go == null)
                return null;
            Place(go, x, GcLayout.Height - Pad - BottomHeight, width, BottomHeight);
            var label = LabelOf(go);
            UiFactory.StyleLabel(label, GcLayout.FontSize);
            UiFactory.SetLabelText(label, text);
            var button = go.GetComponent<Button>();
            if (button != null)
                UiFactory.BindClick(button, handler);
            return button;
        }

        /// <summary>按「Dialog 左上角 + 向下 y」摆放（比记账 anchor/pivot 直观）。</summary>
        private static void Place(GameObject go, float x, float y, float width, float height)
        {
            var rt = UiFactory.RectOf(go);
            if (rt == null)
                return;
            UiFactory.TopLeft(rt, new Vector2(width, height), new Vector2(x, -y));
        }

        private static void Place(TextMeshProUGUI label, float x, float y, float width, float height)
        {
            Place(label == null ? null : label.gameObject, x, y, width, height);
        }

        private static TextMeshProUGUI LabelOf(GameObject go)
        {
            if (go == null)
                return null;
            try
            {
                return UiFinder.FindChildComponent<TextMeshProUGUI>(go.transform);
            }
            catch (Exception)
            {
                return null;
            }
        }
        #endregion

        #region 使用
        public void Open(string title, IList<string> newOptions, int newSelected, Action<int> pick)
        {
            if (!Build())
                return;
            options = newOptions;
            onPick = pick;
            selected = newSelected;
            var count = Count;
            if (selected < 0 || selected >= count)
                selected = count > 0 ? 0 : -1;
            page = selected < 0 ? 0 : selected / PageSize;

            if (titleLabel != null)
                UiFactory.SetLabelText(titleLabel, title ?? string.Empty);
            RefreshPage();
            root.SetActive(true);
            Plugin.Log?.LogInfo("[Picker] 打开选择浮层：" + title + "（" + count + " 项，第 " + (page + 1) + " 页）");
        }

        public void Close()
        {
            if (UiFinder.IsAlive(root))
                root.SetActive(false);
            onPick = null;
        }

        public void Destroy()
        {
            if (Shared == this)
                Shared = null;
            UiFactory.SetActiveSafe(root, false);
            if (root != null)
                UnityEngine.Object.Destroy(root);
            root = null;
            rows.Clear();
            rowLabels.Clear();
            rowImages.Clear();
        }

        private int Count
        {
            get { return options == null ? 0 : options.Count; }
        }

        private int PageCount
        {
            get
            {
                var n = Count;
                return n <= 0 ? 1 : (n + PageSize - 1) / PageSize;
            }
        }

        private void TurnPage(int delta)
        {
            page += delta;
            RefreshPage();
        }

        private void OnRowClicked(int slot)
        {
            var index = page * PageSize + slot;
            if (index < 0 || index >= Count)
                return;
            selected = index;
            var handler = onPick;
            Close();
            if (handler != null)
            {
                try
                {
                    handler(index);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogError("[Picker] 应用选择失败：" + e.Message);
                }
            }
        }
        #endregion

        #region 绘制
        private void RefreshPage()
        {
            if (!UiFinder.IsAlive(root))
                return;
            var count = Count;
            var pages = PageCount;
            if (page < 0)
                page = 0;
            if (page >= pages)
                page = pages - 1;

            // 网格：竖向铺满标题与底栏之间的空间。
            var top = Pad + TitleHeight + CellGap;
            var bottom = GcLayout.Height - Pad - BottomHeight - CellGap;
            var height = bottom - top;
            var pitch = height / Rows;
            var cellHeight = Mathf.Min(CellHeight, pitch - CellGap);
            var cellWidth = (GcLayout.Width - Pad * 2f - CellGap * (Columns - 1)) / Columns;

            for (var i = 0; i < PageSize; i++)
            {
                var go = i < rows.Count ? rows[i] : null;
                if (!UiFinder.IsAlive(go))
                    continue;
                var index = page * PageSize + i;
                if (index >= count)
                {
                    go.SetActive(false);
                    continue;
                }
                var col = i % Columns;
                var row = i / Columns;
                Place(go, Pad + col * (cellWidth + CellGap), top + row * pitch, cellWidth, cellHeight);
                var label = rowLabels[i];
                UiFactory.SetLabelText(label, options[index]);
                if (label != null)
                    label.alignment = TextAlignmentOptions.Center;
                var image = rowImages[i];
                if (image != null)
                    image.color = index == selected ? RowSelected : RowNormal;
                go.SetActive(true);
            }

            if (pageLabel != null)
                UiFactory.SetLabelText(pageLabel, "第 " + (page + 1) + " / " + pages + " 页");
            SetInteractable(prevButton, page > 0);
            SetInteractable(nextButton, page < pages - 1);
        }

        private static void SetInteractable(Button button, bool value)
        {
            if (button == null)
                return;
            try
            {
                button.interactable = value;
            }
            catch (Exception)
            {
            }
        }
        #endregion

        private static void Noop()
        {
        }
    }
}
