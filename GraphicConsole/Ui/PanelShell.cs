using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GraphicConsole
{
    /// <summary>
    /// 面板外壳 —— 负责「分页」这件事本身：页签行、内容区、底部按钮，以及拖动。
    ///
    /// 结构（全部从模板架克隆 / 现造的纯布局容器，尺寸见 <see cref="GcLayout"/>）：
    /// <code>
    /// GraphicConsolePanel        ← 对话框模板的克隆
    ///   Dialog                   ← 固定尺寸 + 统一缩放（GcLayout.Scale）
    ///     Frame                  ← 克隆原对话框的 Background（框体美术），ignoreLayout + 铺满
    ///     Header                 ← 页签行（需求：顶部不要名称，只要分页按钮）
    ///     Content                ← 页面内容
    ///     Footer                 ← 暂停开关 / 关闭 / 原版控制台
    /// </code>
    ///
    /// ⚠️ 两个刻意的决定：
    /// <list type="number">
    /// <item><b>把原对话框的 Blocker（全屏遮罩）删掉。</b>
    ///   需求要"面板显示时也能正常操作游戏"，遮罩会吃掉所有点向游戏的点击。
    ///   代价是点击面板外会点到游戏 —— 这正是想要的。</item>
    /// <item><b>拖拽自己实现</b>（在 Header 上按下并拖动）。
    ///   加不了新的 MonoBehaviour（那需要 ClassInjector 注入，本 mod 的原则是零注入），
    ///   所以在每帧心跳里用 <c>RectTransformUtility.RectangleContainsScreenPoint</c> 判断
    ///   按下位置是否在页签行内，然后改 Dialog 的 anchoredPosition，
    ///   并夹住到"至少留 <see cref="GcLayout.MinVisibleOnScreen"/> 像素在屏幕内"。</item>
    /// </list>
    /// </summary>
    internal sealed class PanelShell
    {
        public const string RootName = "GraphicConsolePanel";

        private static readonly Vector2 HiddenPosition = new Vector2(99999f, 99999f);

        private GameObject root;
        private RectTransform dialogRect;
        private Transform content;
        private Transform header;
        private readonly List<GameObject> tabButtons = new List<GameObject>();
        private readonly List<TextMeshProUGUI> tabLabels = new List<TextMeshProUGUI>();
        private readonly List<IPanelPage> pages = new List<IPanelPage>();
        private int currentIndex = -1;
        private bool built;

        // ---- 悬停说明（需求：特殊按钮的说明写在悬停提示里，不占面板空间）----
        //     实现走**原版那套**（TooltipAnchor + TooltipHandler + MainSceneController.ShowTooltip），
        //     见 <see cref="PanelTooltips"/>。

        // ---- 选择浮层（下拉格的弹窗；TMP 自带弹窗在本环境画不出来，见 PickerOverlay）----
        private PickerOverlay picker;

        // ---- 拖动状态 ----
        private bool dragging;
        /// <summary>按下时鼠标的屏幕坐标。</summary>
        private Vector2 dragStartPointer;
        /// <summary>按下时面板枢轴的屏幕坐标（拖动全程都在屏幕空间里算，见 <see cref="TickDrag"/>）。</summary>
        private Vector2 dragStartCenterScreen;
        private Canvas canvas;

        public bool IsBuilt { get { return built && UiFinder.IsAlive(root); } }
        public bool IsOpen { get { return IsBuilt && SafeActiveSelf(root); } }
        public Transform Content { get { return content; } }
        public GameObject Root { get { return root; } }
        public IList<IPanelPage> Pages { get { return pages; } }
        public int CurrentIndex { get { return currentIndex; } }
        public string BuildReport { get; private set; }

        #region 构建
        public bool Build()
        {
            if (IsBuilt)
                return true;
            // 上一次构建失败时留下的残骸要清掉，否则会同时存在多个面板、
            // 而且 <see cref="IsBuilt"/> 会对半成品返回 true（这正是
            // 「第二次打开面板文字变回原版文案」的成因之一）。
            if (root != null)
                Destroy();
            built = false;
            BuildReport = string.Empty;

            if (!UiSources.Prepare())
            {
                BuildReport = "UI 模板不完整：" + UiSources.LastReport;
                return false;
            }

            try
            {
                // ⚠️ 父节点必须是**模板架的父节点**，不能是模板架本身：
                // 模板架是 SetActive(false) 的，把面板挂进去会让整支面板 inactive，
                // LayoutGroup 一次都不会跑（子节点全停在 100x100 @ 0,0），屏幕上也看不到。
                var templateParent = UiSources.HostParent;
                if (templateParent == null && UiSources.DialogTemplate != null)
                    templateParent = UiSources.DialogTemplate.transform.parent;
                if (templateParent == null)
                {
                    BuildReport = "模板没有宿主父节点，无法确定挂载位置";
                    return false;
                }

                root = UiFactory.Clone(UiSources.DialogTemplate, templateParent, RootName);
                if (root == null)
                {
                    BuildReport = "克隆对话框失败";
                    return false;
                }
                root.transform.SetAsLastSibling();
                canvas = FindCanvas(root.transform);

                // ⚠️ 需求：面板显示时也要能操作游戏 → 删掉全屏遮罩。
                var blocker = UiFinder.FindChild(root.transform, "Blocker");
                if (blocker != null)
                {
                    blocker.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(blocker.gameObject);
                }

                var dialogNode = UiFinder.FindChild(root.transform, "Dialog");
                if (dialogNode == null)
                {
                    BuildReport = "克隆体里找不到 Dialog 节点";
                    return false;
                }
                dialogRect = UiFactory.RectOf(dialogNode);

                // 框体：固定设计尺寸 + 统一缩放（GcLayout.Scale 是"整体等比缩小"的那个旋钮）
                UiFactory.CenterFixed(dialogNode, GcLayout.Width, GcLayout.Height);
                dialogNode.localScale = new Vector3(GcLayout.Scale, GcLayout.Scale, 1f);
                var fitter = dialogNode.GetComponent<ContentSizeFitter>();
                if (fitter != null)
                {
                    fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                    fitter.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
                }

                // 先把框体美术（Background）与输入框模板的相关节点摘出来，再清空子节点。
                var background = UiFinder.FindChild(dialogNode, "Background");
                UiFactory.ClearChildren(dialogNode.gameObject);

                // 纵向布局：Header / Content / Footer 三者顺序排列，尺寸由 LayoutElement 决定。
                var vgroup = dialogNode.GetComponent<VerticalLayoutGroup>();
                if (vgroup == null)
                    vgroup = dialogNode.gameObject.AddComponent<VerticalLayoutGroup>();
                vgroup.spacing = GcLayout.DialogSpacing;
                vgroup.childAlignment = TextAnchor.UpperCenter;
                vgroup.childControlWidth = true;
                vgroup.childControlHeight = true;
                vgroup.childForceExpandWidth = true;
                vgroup.childForceExpandHeight = false;
                SetPadding(vgroup, GcLayout.DialogPadX, GcLayout.DialogPadY);

                // ① 框体美术：铺满 + 让布局组跳过它
                if (background != null)
                {
                    var frameGo = UiFactory.Clone(background.gameObject, dialogNode, "Frame");
                    if (frameGo != null)
                    {
                        UiFactory.Stretch(frameGo.transform);
                        UiFactory.IgnoreLayout(frameGo);
                        frameGo.SetActive(true);
                        // ⚠️ 必须**强制把框体图的 Image 打开**。
                        // 实测：克隆出来的 Frame 的 <c>Image.enabled</c> 是 0 —— 于是 sprite 明明在
                        // （sprite=「form」，Sliced）却什么都不画，看上去就是"面板的背景没了"。
                        // 原版对话框的 ImageResolver 会读写 <c>Image.enabled</c>，
                        // 而“当前该不该显示”是它自己的状态，克隆体不该继承。
                        var frameImage = frameGo.GetComponent<UnityEngine.UI.Image>();
                        if (frameImage != null)
                        {
                            if (!frameImage.enabled)
                                Plugin.Log?.LogWarning("[Panel] 框体图克隆后是 disabled，已强制启用（sprite="
                                    + (frameImage.sprite == null ? "null" : frameImage.sprite.name) + "）");
                            frameImage.enabled = true;
                        }
                        var sourceImage = background.GetComponent<UnityEngine.UI.Image>();
                        if (sourceImage != null && !sourceImage.enabled)
                            Plugin.Log?.LogWarning("[Panel] 提示：原对话框的框体图本来就是 disabled 状态");
                    }
                }

                // ② 页签行
                var headerGo = UiFactory.NewRect(dialogNode, "Header");
                if (headerGo == null)
                {
                    BuildReport = "新建页签行失败";
                    return false;
                }
                header = headerGo.transform;
                UiFactory.Layout(headerGo, GcLayout.HeaderHeight, 0f);
                var headerGroup = headerGo.AddComponent<HorizontalLayoutGroup>();
                headerGroup.spacing = GcLayout.TabSpacing;
                headerGroup.childAlignment = TextAnchor.MiddleLeft;
                headerGroup.childControlWidth = true;
                headerGroup.childControlHeight = true;
                headerGroup.childForceExpandWidth = false;
                headerGroup.childForceExpandHeight = false;

                // ③ 内容区
                var contentGo = UiFactory.NewRect(dialogNode, "Content");
                if (contentGo == null)
                {
                    BuildReport = "新建内容区失败";
                    return false;
                }
                content = contentGo.transform;
                // 高度 = 面板高 - 上下内边距 - 页签行 - 底部行 - 两条间距
                UiFactory.Layout(contentGo, GcLayout.ContentHeight, 1f);

                // ④ 底部按钮行
                var footerGo = UiFactory.NewRect(dialogNode, "Footer");
                if (footerGo == null)
                {
                    BuildReport = "新建底部按钮行失败";
                    return false;
                }
                UiFactory.Layout(footerGo, GcLayout.FooterHeight, 0f);
                var footerGroup = footerGo.AddComponent<HorizontalLayoutGroup>();
                footerGroup.spacing = 8f;
                footerGroup.childAlignment = TextAnchor.MiddleRight;
                footerGroup.childControlWidth = true;
                footerGroup.childControlHeight = true;
                footerGroup.childForceExpandWidth = false;
                footerGroup.childForceExpandHeight = false;
                BuildFooter(footerGo.transform);

                // 选择浮层：挂在 Dialog 下，铺满整块面板（下拉格点开的就是它）。
                picker = new PickerOverlay(dialogRect);
                // 下拉格的“位置包含判定”要用的相机（ScreenSpaceCamera 画布必须传，否则判定恒假）。
                UiFactory.PickerCamera = CanvasCamera();

                // ⑤ 页签（必须在 root 激活前后都无所谓，但按钮事件依赖已实例化）
                BuildTabs();

                // ⚠️ 顺序关键：
                //   · `built` 必须先置 true 再 ShowPage —— ShowPage 开头就有
                //     `if (!IsBuilt) return;`，先调会直接返回，于是内容区永远是空的
                //     （布局 dump 里 Content 一个子节点都没有，就是这么来的）。
                //   · 这里**不**把 root 设为激活。激活由 Open() 负责；否则
                //     Build() 刚跑完 IsOpen 就是 true，ConsolePanel.Open() 会以为
                //     "已经开着"而直接返回，跳过 Open() 里的页面装配与置顶。
                built = true;
                if (pages.Count > 0)
                    ShowPage(0);

                BuildReport = "面板已构建（缩放 " + GcLayout.Scale + "，" + UiSources.LastReport + "）";
                Plugin.Log?.LogInfo("[Panel] " + BuildReport);
                return true;
            }
            catch (Exception e)
            {
                BuildReport = "构建面板失败：" + e;
                Plugin.Log?.LogError("[Panel] " + BuildReport);
                Destroy();
                return false;
            }
        }

        private void BuildFooter(Transform parent)
        {
            // 需求：底部只留「关闭」与「原版控制台」。
            // 原来的「暂停」按钮已移除 —— 暂停现在由页面1 的「后台暂停」复选框统一管理
            // （它同时写原版全局选项 pause_on_focus_lost 与面板自身的速度=0，见 <see cref="PanelPause"/>）。
            var closeGo = UiFactory.Clone(UiSources.ButtonTemplate, parent, "CloseButton");
            if (closeGo != null)
            {
                UiFactory.Layout(closeGo, GcLayout.FooterHeight, 0f, GcLayout.FooterButtonWidth, 0f);
                var closeLabel = UiFinder.FindChildComponent<TextMeshProUGUI>(closeGo.transform);
                UiFactory.SetLabelText(closeLabel, "关闭");
                UiFactory.StyleLabel(closeLabel, GcLayout.FontSize, false, TextAlignmentOptions.Center);
                UiFactory.BindClick(closeGo.GetComponent<UnityEngine.UI.Button>(), Close);
                closeGo.SetActive(true);
            }

            var consoleGo = UiFactory.Clone(UiSources.ButtonTemplate, parent, "ConsoleButton");
            if (consoleGo != null)
            {
                UiFactory.Layout(consoleGo, GcLayout.FooterHeight, 0f, GcLayout.FooterButtonWidth, 0f);
                var consoleLabel = UiFinder.FindChildComponent<TextMeshProUGUI>(consoleGo.transform);
                UiFactory.SetLabelText(consoleLabel, "原版控制台");
                UiFactory.StyleLabel(consoleLabel, GcLayout.FontSize, false, TextAlignmentOptions.Center);
                UiFactory.BindClick(consoleGo.GetComponent<UnityEngine.UI.Button>(), () =>
                {
                    Close();
                    try
                    {
                        var main = MVZ2.Managers.MainManager.Instance;
                        if (main != null && main.Scene != null)
                            main.Scene.DisplayConsole();
                    }
                    catch (Exception e)
                    {
                        Plugin.Log?.LogWarning("[Panel] 打开原版控制台失败：" + e.GetType().Name);
                    }
                });
                consoleGo.SetActive(true);
            }
        }

        #region 悬停说明
        /// <summary>让某个格子悬停时显示原版提示（标题就用格子的标签）。</summary>
        public bool AddTooltip(WidgetCell cell, string text)
        {
            return PanelTooltips.Attach(cell, text);
        }

        /// <summary>让某个节点悬停时显示原版提示。</summary>
        public bool AddTooltip(RectTransform target, string title, string text)
        {
            return PanelTooltips.Attach(target == null ? null : target.gameObject, title, text);
        }

        /// <summary>收起当前提示（每次切页 / 关面板时调）。</summary>
        public void ClearTooltips()
        {
            PanelTooltips.Hide();
        }
        #endregion

        private void BuildTabs()
        {
            if (header == null)
                return;
            for (var i = 0; i < pages.Count; i++)
            {
                var index = i;
                var go = UiFactory.Clone(UiSources.ButtonTemplate, header, "Tab_" + i);
                if (go == null)
                    continue;
                UiFactory.Layout(go, -1f, 0f, GcLayout.TabWidth, 0f);
                var label = UiFinder.FindChildComponent<TextMeshProUGUI>(go.transform);
                UiFactory.SetLabelText(label, pages[i].Title);
                // 固定字号 + 居中 + 关自动缩放（原版按钮文本开着自动缩放，会把手符算小/算大）。
                UiFactory.StyleLabel(label, GcLayout.TabFontSize, false, TextAlignmentOptions.Center);
                UiFactory.BindClick(go.GetComponent<UnityEngine.UI.Button>(), () => ShowPage(index));
                go.SetActive(true);
                tabButtons.Add(go);
                tabLabels.Add(label);
            }
        }

        private static void SetPadding(HorizontalOrVerticalLayoutGroup group, int padX, int padY)
        {
            try
            {
                var pad = group.padding;
                if (pad == null)
                    return;
                pad.left = padX;
                pad.right = padX;
                pad.top = padY;
                pad.bottom = padY;
                group.padding = pad;
            }
            catch (Exception)
            {
            }
        }
        #endregion

        #region 分页
        public void AddPage(IPanelPage page)
        {
            if (page == null)
                return;
            pages.Add(page);
            Plugin.Log?.LogInfo("[Panel] 注册页面：" + page.Title);
            if (IsBuilt && header != null)
            {
                var index = pages.Count - 1;
                var go = UiFactory.Clone(UiSources.ButtonTemplate, header, "Tab_" + index);
                if (go != null)
                {
                    UiFactory.Layout(go, -1f, 0f, GcLayout.TabWidth, 0f);
                    var label = UiFinder.FindChildComponent<TextMeshProUGUI>(go.transform);
                    UiFactory.SetLabelText(label, page.Title);
                    UiFactory.BindClick(go.GetComponent<UnityEngine.UI.Button>(), () => ShowPage(index));
                    go.SetActive(true);
                    tabButtons.Add(go);
                    tabLabels.Add(label);
                }
            }
        }

        public void ShowPage(int index)
        {
            if (index < 0 || index >= pages.Count)
                return;
            if (!IsBuilt)
                return;
            if (currentIndex == index && currentIndex >= 0)
                return;

            if (currentIndex >= 0 && currentIndex < pages.Count)
                pages[currentIndex].OnLeave();

            // 清掉上一页的内容。每次切页整页重建是有意的：克隆出来的控件引用会随销毁失效，
            // 重建比"复用 + 逐个重置"可靠得多，而切页频率很低。
            UiFactory.ClearChildren(content.gameObject);
            UiFactory.PruneKeepAlive();
            UiFactory.ClearDropdowns();
            ClearTooltips();
            if (picker != null)
                picker.Close();

            currentIndex = index;
            var page = pages[index];
            page.Build(this, content);
            page.Refresh();
            UpdateTabStates();
            Plugin.Log?.LogInfo("[Panel] 切到页面 " + index + "：" + page.Title);
        }

        private void UpdateTabStates()
        {
            for (var i = 0; i < tabButtons.Count; i++)
            {
                var go = tabButtons[i];
                if (go == null)
                    continue;
                try
                {
                    var button = go.GetComponent<UnityEngine.UI.Button>();
                    if (button != null)
                        button.interactable = (i != currentIndex);
                    // 当前页的页签用原色、其余页签压暗一点，比"禁用"更容易一眼看出在哪一页。
                    var label = i < tabLabels.Count ? tabLabels[i] : null;
                    if (label != null)
                    {
                        label.color = i == currentIndex
                            ? new Color(1f, 1f, 1f, 1f)
                            : new Color(0.75f, 0.75f, 0.75f, 1f);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        public void Refresh()
        {
            if (!IsOpen)
                return;
            if (currentIndex >= 0 && currentIndex < pages.Count)
                pages[currentIndex].Refresh();
        }
        #endregion

        #region 开关
        public void Open()
        {
            if (!Build())
                return;
            if (dialogRect != null && dialogRect.anchoredPosition == HiddenPosition)
                dialogRect.anchoredPosition = Vector2.zero;
            root.SetActive(true);
            root.transform.SetAsLastSibling();
            if (currentIndex < 0 && pages.Count > 0)
            {
                currentIndex = -1;
                ShowPage(0);
            }
            else
            {
                Refresh();
            }
            dragging = false;
            // 这个日志里不要写与暂停相关的状态（暂停已不归面板管）。
            Plugin.Log?.LogInfo("[Panel] 面板已打开");
        }

        public void Close()
        {
            if (!IsBuilt)
                return;
            if (picker != null)
                picker.Close();
            ClearTooltips();
            if (currentIndex >= 0 && currentIndex < pages.Count)
                pages[currentIndex].OnLeave();
            dragging = false;
            root.SetActive(false);
            Plugin.Log?.LogInfo("[Panel] 面板已关闭");
        }

        public void ToggleOpen()
        {
            if (IsOpen)
                Close();
            else
                Open();
        }

        public void Destroy()
        {
            if (picker != null)
            {
                picker.Destroy();
                picker = null;
            }
            try
            {
                if (root != null)
                    UnityEngine.Object.Destroy(root);
            }
            catch (Exception)
            {
            }
            root = null;
            dialogRect = null;
            content = null;
            header = null;
            canvas = null;
            tabButtons.Clear();
            tabLabels.Clear();
            currentIndex = -1;
            built = false;
            dragging = false;
        }
        #endregion

        #region 拖动
        /// <summary>
        /// 在页签行上按住拖动来移动面板。
        ///
        /// ⚠️ 两处曾经的错误，都修在这里：
        ///
        /// **① 手感"比鼠标快"。** 原来的换算是 <c>delta / (GcLayout.Scale * canvasScale())</c>。
        /// 但 <c>anchoredPosition</c> 是**父节点空间**的坐标，**不受 Dialog 自己的
        /// <c>localScale</c> 影响** —— 多除的那个 0.55 让位移被放大 1/0.55 ≈ 1.82 倍。
        /// 现在改用 <c>RectTransformUtility.ScreenPointToLocalPointInRectangle</c>
        /// 把鼠标点直接换算到**父节点局部坐标**，两次采样相减即得位移。
        /// 这样屏幕上 1px = 面板上 1px，与所有祖先缩放（含画布缩放、面板等比缩放）天然解耦。
        ///
        /// **② 可移出画面的距离不对，且能从上下方移出。** 原来按"设计尺寸 × 缩放"手算，
        /// 还漏了画布缩放。现在用**屏幕尺寸**描述约束：
        ///   · 纵向：面板必须**完整留在屏幕内**（需求：不应能从上下方移出画面）；
        ///   · 横向：允许拖到只剩 <see cref="GcLayout.MinVisibleOnScreen"/> 像素可见。
        /// </summary>
        /// <summary>
        /// 在页签行上按住拖动来移动面板。
        ///
        /// 全程在**屏幕像素**空间里算，只在最后一步换算回父节点局部坐标：
        ///   · 位移：<c>目标中心 = 按下时中心 + (鼠标 - 按下时鼠标)</c> ——
        ///     屏幕上鼠标走 1px，面板就走 1px，天然 1:1 跟手；
        ///   · 夹取：直接在屏幕矩形上做，用面板的**屏幕尺寸**（含所有祖先缩放）。
        ///
        /// 为什么不能拿 <c>anchoredPosition</c> 做缩放换算（之前两版都错在这里）：
        ///   · <c>anchoredPosition</c> 是**父节点空间**的坐标，与 Dialog 自己的
        ///     <c>localScale = 0.55</c> 无关；而它的“1 单位 = 多少屏幕像素”是画布缩放
        ///     （实测 1.33），不是自己算的那个 <c>0.731</c> —— 两者相差 1.82 倍，
        ///     于是横向限制偏紧（拖不到“只剩10px”）、纵向偏松（能从上/下拖出画面）。
        ///   · 屏幕空间的公式里一个缩放系数都不出现，也就没得错。
        /// </summary>
        public void TickDrag()
        {
            if (!IsOpen || dialogRect == null || header == null)
                return;
            try
            {
                var pointer = (Vector2)Input.mousePosition;
                if (Input.GetMouseButtonDown(0))
                {
                    if (RectTransformUtility.RectangleContainsScreenPoint(
                            header.TryCast<RectTransform>(), pointer, CanvasCamera()))
                    {
                        dragging = true;
                        dragStartPointer = pointer;
                        dragStartCenterScreen = PanelPivotScreen();
                    }
                }
                else if (Input.GetMouseButtonUp(0))
                {
                    dragging = false;
                }
                else if (dragging)
                {
                    var target = ClampCenterScreen(dragStartCenterScreen + (pointer - dragStartPointer));
                    Vector2 local;
                    if (ScreenToParentLocal(target, out local))
                        dialogRect.anchoredPosition = local;
                }
            }
            catch (Exception e)
            {
                dragging = false;
                Plugin.Log?.LogWarning("[Panel] 拖动失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>面板枢轴点当前的屏幕坐标（枢轴是 0.5,0.5，所以就是面板中心）。</summary>
        public Vector2 PanelPivotScreen()
        {
            if (dialogRect == null)
                return Vector2.zero;
            try
            {
                var rect = dialogRect.rect;
                var world = dialogRect.TransformPoint(new Vector3(rect.center.x, rect.center.y, 0f));
                return RectTransformUtility.WorldToScreenPoint(CanvasCamera(), world);
            }
            catch (Exception)
            {
                return Vector2.zero;
            }
        }

        /// <summary>把屏幕坐标换算到 Dialog 父节点的局部坐标（可直接赋给 anchoredPosition）。</summary>
        private bool ScreenToParentLocal(Vector2 screenPoint, out Vector2 local)
        {
            local = Vector2.zero;
            var parent = dialogRect == null ? null : dialogRect.parent;
            var parentRect = parent == null ? null : parent.TryCast<RectTransform>();
            if (parentRect == null)
                return false;
            Vector2 value;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    parentRect, screenPoint, CanvasCamera(), out value))
                return false;
            local = value;
            return true;
        }

        /// <summary>
        /// 把面板**中心的屏幕坐标**夹到允许范围内：
        ///   · 纵向：面板完整留在屏幕内（高度超过屏幕时居中）；
        ///   · 横向：至少保留 <see cref="GcLayout.MinVisibleOnScreen"/> 像素可见。
        /// </summary>
        private Vector2 ClampCenterScreen(Vector2 center)
        {
            var size = DialogScreenSize();
            if (size.x <= 0f || size.y <= 0f)
                return center;
            var halfW = size.x * 0.5f;
            var halfH = size.y * 0.5f;
            var screenW = Screen.width;
            var screenH = Screen.height;
            var margin = GcLayout.MinVisibleOnScreen;

            float minX = margin - halfW, maxX = screenW - margin + halfW;
            // 纵向：面板必须完整在屏幕内 → 中心余量就是 (屏高 - 面板高)/2；
            // 面板比屏幕还高时两边反过来，此时只能居中。
            float minY = halfH, maxY = screenH - halfH;
            if (minY > maxY)
            {
                minY = maxY = screenH * 0.5f;
            }
            if (minX > maxX)
            {
                minX = maxX = screenW * 0.5f;
            }
            return new Vector2(Mathf.Clamp(center.x, minX, maxX), Mathf.Clamp(center.y, minY, maxY));
        }

        /// <summary>面板当前在屏幕上的真实尺寸（px）。含祖先链上的 localScale（那条 0.55 等比缩放）。</summary>
        private Vector2 DialogScreenSize()
        {
            if (dialogRect == null)
                return Vector2.zero;
            try
            {
                var rect = dialogRect.rect;
                var min = dialogRect.TransformPoint(new Vector3(rect.xMin, rect.yMin, 0f));
                var max = dialogRect.TransformPoint(new Vector3(rect.xMax, rect.yMax, 0f));
                var cam = CanvasCamera();
                var a = RectTransformUtility.WorldToScreenPoint(cam, min);
                var b = RectTransformUtility.WorldToScreenPoint(cam, max);
                return new Vector2(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
            }
            catch (Exception)
            {
                return Vector2.zero;
            }
        }

        private Camera CanvasCamera()
        {
            if (canvas == null)
                canvas = FindCanvas(root == null ? null : root.transform);
            if (canvas == null)
                return null;
            try
            {
                return canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private float CanvasScale()
        {
            if (canvas == null)
                canvas = FindCanvas(root == null ? null : root.transform);
            if (canvas == null)
                return 1f;
            try
            {
                return canvas.scaleFactor;
            }
            catch (Exception)
            {
                return 1f;
            }
        }

        private static Canvas FindCanvas(Transform t)
        {
            var cur = t;
            var guard = 0;
            while (cur != null && guard++ < 32)
            {
                try
                {
                    var c = cur.GetComponent<Canvas>();
                    if (c != null)
                        return c;
                }
                catch (Exception)
                {
                }
                cur = cur.parent;
            }
            return null;
        }
        #endregion

        private static bool SafeActiveSelf(GameObject go)
        {
            try
            {
                return go.activeSelf;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
