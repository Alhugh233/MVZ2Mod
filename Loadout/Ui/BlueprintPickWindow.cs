using System;
using System.Collections.Generic;
using DSHCore.Ui;
using MVZ2.Blueprints;
using MVZ2.Level.Blueprints;
using MVZ2.UI;
using MVZ2Logic.Models;
using PVZEngine;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// **选蓝图浮层** —— 直接克隆原版选卡页里那个「点命令方块后弹出来的复制面板」
    /// （<c>CommandBlockPanelStandalone</c>）。
    ///
    /// <para>它本来就是「一整块深色背景 + 5×8 的蓝图网格 + 底部翻页（带页数）」，
    /// 而 5×8 = 40 正是 <c>BlueprintDisplayerStandalone.maxCountPerPage</c>，
    /// 所以「五行八列 + 翻页 + 页数」这一整套不用自己实现。</para>
    ///
    /// <para>在它上面再加一条**已选框**（与蓝图页的内容框同一种样式），
    /// 点里面的卡片就是把这张卡放回去。全部走原版节点，样式与选卡界面一致。</para>
    ///
    /// <para><b>点击一律自己命中</b>（<see cref="UiHitTest"/>）：原版卡片上的
    /// <c>LevelPointerInteractionHandler</c> 在托盘里不一定收得到事件，而且它自己也会处理一次
    /// —— 两边都处理就会「点一下翻两遍」。所以卡片只用来显示，交互归我们。</para>
    ///
    /// <para><b>允许同一种蓝图带多张</b>：网格里点一下就是「再加一张」，
    /// 要减少就在已选框里把那一张点掉（那里是按张数来的）。</para>
    /// </summary>
    internal sealed class BlueprintPickWindow
    {
        private GameObject _root;
        private CommandBlockChoosePanel _panel;
        private BlueprintDisplayer _displayer;
        private BlueprintDisplayerStandalone _standalone;
        private BlueprintStrip _chosen;
        private UiOptionRow _okRow;
        private UiOptionRow _cancelRow;
        private Action<List<NamespaceID>> _onOk;

        private readonly List<NamespaceID> _all = new List<NamespaceID>();
        private readonly List<NamespaceID> _picked = new List<NamespaceID>();
        private bool _built;

        /// <summary>每几帧给当前页补一次悬停提示（见 <see cref="ApplyPageTooltips"/>）。</summary>
        private int _tooltipFrames;
        /// <summary>上一次铺提示时的页码 / 卡数（自测用）。</summary>
        private int _tooltipPageIndex = -1;
        private int _tooltipRows;
        private int _tooltipPages;

        private const string OkLabel = "确认";
        private const string CancelLabel = "取消";

        /// <summary>
        /// 标题行（镜像框坐着的那一条）的高度。
        /// 镜像框本身高 <see cref="BoxH"/> = 76，居在这一条里 → 上下各 6 的边距。
        /// </summary>
        private const float TitleH = 88f;

        /// <summary>镜像框的高度：卡片 72 + 上下各 2（卡片与边框贴合）。</summary>
        private const float BoxH = 76f;

        /// <summary>
        /// 可见框体（原版 <c>BlueprintListStandalone</c>）的宽度 —— **原版的 400**。
        /// 里头的镜像框与选卡网格块都是 386（= 原版网格块自己的宽度），两边各留 7 的边距。
        /// </summary>
        private const float PanelW = 400f;

        /// <summary>框内两样东西（镜像框 / 选卡网格块）的宽度 —— 与选卡网格对齐。</summary>
        private const float ContentW = 386f;

        /// <summary>框体总高：标题行（88）+ 网格块（362）+ 下排（58）。</summary>
        private const float PanelH = 508f;

        /// <summary>被藏掉的原版文字（只用于日志，说明「标题」到底是哪一条）。</summary>
        private readonly List<string> _hiddenTexts = new List<string>();

        private static readonly List<BlueprintPickWindow> All = new List<BlueprintPickWindow>();

        public bool IsValid { get { return _built && UiFinder.IsAlive(_root); } }

        public bool IsOpen { get { return IsValid && _root.activeSelf; } }

        public int PickedCount { get { return _picked.Count; } }

        public static void TickAll()
        {
            for (var i = 0; i < All.Count; i++)
            {
                try { All[i].Tick(); }
                catch (Exception) { }
            }
        }

        public static void HideAll()
        {
            for (var i = 0; i < All.Count; i++)
            {
                try { All[i].Close(); }
                catch (Exception) { }
            }
        }

        public static void ForgetAll()
        {
            for (var i = 0; i < All.Count; i++)
                All[i]._built = false;
            All.Clear();
        }

        public static BlueprintPickWindow Create(string name)
        {
            var w = new BlueprintPickWindow();
            w.Build(name);
            if (w._built)
                All.Add(w);
            return w;
        }

        // ------------------------------------------------------------------
        // 搭建
        // ------------------------------------------------------------------

        private void Build(string name)
        {
            var root = LoadoutPanel.DialogRoot;
            if (root == null)
                return;
            var src = BlueprintUi.Source();
            if (src == null || src.commandBlockChoosePanel == null)
            {
                Plugin.LogOnce("pick-src", "[Pick] 拿不到原版「命令方块选卡面板」（不在关卡内？）");
                return;
            }

            _root = UiClone.Instantiate(src.commandBlockChoosePanel.gameObject, root, name, true);
            if (!UiFinder.IsAlive(_root))
                return;
            try
            {
                // 不参与对话框自己的排版，整块盖上去。
                var le = _root.GetComponent<LayoutElement>();
                if (le == null)
                    le = _root.AddComponent<LayoutElement>();
                if (le != null)
                    le.ignoreLayout = true;
                var rt = _root.transform.TryCast<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = Vector2.zero;
                    rt.sizeDelta = Vector2.zero;
                }

                _panel = _root.GetComponent<CommandBlockChoosePanel>();
                if (_panel != null)
                    _displayer = _panel.displayer;
                if (_displayer != null)
                    _standalone = _displayer.TryCast<BlueprintDisplayerStandalone>();

                BuildGridView();
                HideStrayTexts();
                BuildChosenStrip();
                BuildButtons();

                _root.SetActive(false);
                _built = true;
                Plugin.Log?.LogInfo("[Pick] 已克隆原版选卡面板：" + name
                    + "  网格=" + (_standalone == null ? "?" : _standalone.maxCountPerPage.ToString())
                    + "/页  已选框=" + (_chosen == null ? "无" : "有")
                    + "  确认=" + (_okRow == null ? "无" : "有")
                    + "  取消=" + (_cancelRow == null ? "无" : "有"));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Pick] 搭建失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 网格区几何 —— 数值直接取自原版 prefab（实测）：
        /// <code>
        /// BlueprintListStandalone 400x470 @居中    ← 可见的整块框体
        ///   TitleRegion  400x50  锚[0,1~1,1] 位(0,0)   ← 标题行
        ///   Blueprints   386x362 锚(0.5,1)    位(0,-50) ← 8列x48 × 5行x72，零余量
        ///   Bottom       400x58  锚[0,0~1,0]           ← 下排（翻页 | 取消）
        /// </code>
        /// 尺寸账：<c>50 + 362 + 58 = 470</c>。
        ///
        /// <para>要把镜像框塞进标题行（那里本来只有一行标题，标题被我们藏了之后就是一片空白），
        /// 标题行从 50 长到 88。而 <c>88 + 362 + 58 = 508</c>，
        /// 所以框体相应从 470 长到 508 —— 网格尺寸**一点不动**（压缩网格会让卡片溢出）。</para>
        /// </summary>
        private void BuildGridView()
        {
            if (_displayer == null)
                return;
            var rt = _displayer.transform.TryCast<RectTransform>();
            if (rt == null)
                return;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(PanelW, PanelH);
            rt.anchoredPosition = Vector2.zero;

            // 标题行：加高到 76（它只含着被我们藏掉的标题，尺寸只影响下面几个锚点的参照）。
            var title = FindChild(_displayer.transform, "TitleRegion");
            if (title != null)
            {
                var trt = title.TryCast<RectTransform>();
                if (trt != null)
                {
                    trt.anchorMin = new Vector2(0f, 1f);
                    trt.anchorMax = new Vector2(1f, 1f);
                    trt.pivot = new Vector2(0.5f, 1f);
                    trt.anchoredPosition = Vector2.zero;
                    trt.sizeDelta = new Vector2(0f, TitleH);
                }
            }

            // 网格：尺寸不动（386x362），只整体下移 26 —— 下移到标题行下面，
            // 它的底边正好落在下排（58）上，零间隙。
            var grid = FindChild(_displayer.transform, "Blueprints");
            if (grid != null)
            {
                var grt = grid.TryCast<RectTransform>();
                if (grt != null)
                {
                    grt.anchorMin = new Vector2(0.5f, 1f);
                    grt.anchorMax = new Vector2(0.5f, 1f);
                    grt.pivot = new Vector2(0.5f, 1f);
                    grt.anchoredPosition = new Vector2(0f, -TitleH);
                }
            }
        }

        /// <summary>按名字在直属子节点里找一个（子节点很少，不需要递归）。</summary>
        private static Transform FindChild(Transform parent, string name)
        {
            if (parent == null)
                return null;
            try
            {
                for (var i = 0; i < parent.childCount; i++)
                {
                    var c = parent.GetChild(i);
                    if (c != null && c.name == name)
                        return c;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// 镜像框（已选蓝图）—— 就放在**标题行**那个位置上（标题被藏了，那里是空的）。
        /// 尺寸 386x76：宽度与下面的选卡网格对齐（两边都是 386），卡片高 72、上下各留 2。
        /// 纵向上居在 88 高的标题行里 → 框与框体上下各 6 的边距。
        /// </summary>
        private void BuildChosenStrip()
        {
            // 父节点用显示区（不是面板根）：显示区就是那块可见框体。
            var parent = _displayer != null ? _displayer.transform : _root.transform;
            _chosen = BlueprintStrip.Create(parent, "DSH_PickChosen");
            if (_chosen == null)
                return;
            _chosen.SetSize(ContentW, BoxH);
            var rt = _chosen.Root.transform.TryCast<RectTransform>();
            if (rt == null)
                return;
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -(TitleH - BoxH) * 0.5f);
        }

        /// <summary>
        /// 藏掉克隆面板里**原版自带的文字**（原版那个「复制蓝图」面板自己带的标题/说明，
        /// 我们只要网格，不要它的文案）。
        ///
        /// <para>不动的两处：卡片子树里的文字（卡面的一部分 —— 费用 / 快捷键）、
        /// 以及翻页那一片（页码 + 两个翻页按钮）。</para>
        /// </summary>
        private void HideStrayTexts()
        {
            _hiddenTexts.Clear();
            HideTexts(_root.transform, 0);
            if (_hiddenTexts.Count <= 0)
                return;
            var sb = new System.Text.StringBuilder(80);
            for (var i = 0; i < _hiddenTexts.Count && i < 6; i++)
            {
                if (i > 0)
                    sb.Append(" / ");
                sb.Append('\'').Append(_hiddenTexts[i]).Append('\'');
            }
            Plugin.Log?.LogInfo("[Pick] 已隐藏克隆面板自带的文字节点 " + _hiddenTexts.Count + " 个：" + sb);
        }

        private void HideTexts(Transform node, int depth)
        {
            if (node == null || depth > 12)
                return;
            for (var i = 0; i < node.childCount; i++)
            {
                var child = node.GetChild(i);
                if (child == null)
                    continue;
                var go = child.gameObject;
                // 卡片（含模板）里的文字是我要的（费用 / 快捷键）→ 整棵跳过。
                if (go.GetComponent<Blueprint>() != null)
                    continue;
                // 翻页那一片（页码 + 翻页按钮）也不动。
                if (_standalone != null && _standalone.pageRoot != null
                    && go == _standalone.pageRoot)
                    continue;
                var tmp = go.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    if (tmp.gameObject.activeSelf)
                    {
                        // 标签被藏了，承载它的按钮也得跟着走 —— 不然面板上会留一个空白按钮框。
                        // 但别把翻页那一片一起带走（有些原版按钮是它们的父节点）。
                        var button = FindButtonAncestor(child);
                        if (button != null && ContainsPageRoot(button))
                            button = null;
                        (button != null ? button : go).SetActive(false);
                        _hiddenTexts.Add(tmp.text);
                    }
                    continue;
                }
                HideTexts(child, depth + 1);
            }
        }

        /// <summary>往上找最近的按钮节点（不越过面板根）。</summary>
        private GameObject FindButtonAncestor(Transform node)
        {
            try
            {
                var t = node;
                while (t != null && t != _root.transform)
                {
                    if (t.GetComponent<Button>() != null)
                        return t.gameObject;
                    t = t.parent;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>这个按钮节点是不是（或含着）翻页那一片。</summary>
        private bool ContainsPageRoot(GameObject go)
        {
            if (go == null || _standalone == null || _standalone.pageRoot == null)
                return false;
            try { return _standalone.pageRoot.transform.IsChildOf(go.transform); }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 确认 / 取消。
        ///
        /// <para>用 DSHCore 的原版按钮行素材（<see cref="UiOptionRow"/> 的按钮模式）：
        /// 它的 <c>SetKind</c> 会**确定地**把那个控件打开、把另外三个关掉，
        /// 尺寸契约也由它自己提供 —— 这正是这个库推荐的做法。</para>
        ///
        /// <para>为什么不用克隆面板自带的按钮：它的文案归游戏的语言 / 刷新逻辑管，
        /// 第二次打开时会把我们写上去的字改回去（实测：变成「取消 / 返回」）。</para>
        ///
        /// <para>为什么不用原版的 <c>ButtonRow</c>（页签那种）：它里头的 <c>ElementList</c>
        /// 造出来的条目要等它自己的一次 <c>Update</c> 才会被打开，实测在**第一个**浮层里
        /// （建完就被 <c>SetActive(false)</c> 的那个）一直不会打开 → 两个按钮整块看不见。</para>
        /// </summary>
        private void BuildButtons()
        {
            // 挂到原版的下排容器 Bottom（400x58）里，跟翻页按钮**同一行**：
            // 原版那里本来就有个 200x40 的 CancelButton（已被 HideStrayTexts 藏掉），
            // 我们就把那个 200 宽的槽均分给确认 / 取消（各 95，中间 10 缝）。
            // 横向占用：翻页按钮从左边算 [20,60]，页码 [310,390] —— 互不重叠。
            var parent = FindChild(_displayer != null ? _displayer.transform : _root.transform, "Bottom");
            if (parent == null)
                parent = _displayer != null ? _displayer.transform : _root.transform;

            _okRow = UiOptionRow.Create(parent, "DSH_PickOk", UiOptionKind.Button);
            if (_okRow != null)
            {
                PlaceBottomButton(_okRow.Root, true);
                _okRow.SetButton(OkLabel, Confirm);
            }
            _cancelRow = UiOptionRow.Create(parent, "DSH_PickCancel", UiOptionKind.Button);
            if (_cancelRow != null)
            {
                PlaceBottomButton(_cancelRow.Root, false);
                _cancelRow.SetButton(CancelLabel, Close);
            }
            ApplyButtonLabels();
            if (_okRow == null || _cancelRow == null)
                Plugin.LogOnce("pick-buttons", "[Pick] 按钮行素材不可用，确认/取消没建起来。");
        }

        /// <summary>行里现在看得见的东西（确认没把四合一里的另外三个控件一起露出来）。</summary>
        private static string RowChildren(GameObject rowRoot)
        {
            if (!UiFinder.IsAlive(rowRoot))
                return "（无）";
            try
            {
                var sb = new System.Text.StringBuilder(60);
                var t = rowRoot.transform;
                for (var i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (c == null || !c.gameObject.activeSelf)
                        continue;
                    if (sb.Length > 0)
                        sb.Append(',');
                    sb.Append(c.name);
                }
                return sb.Length == 0 ? "（全关）" : sb.ToString();
            }
            catch (Exception e)
            {
                return "（" + e.GetType().Name + "）";
            }
        }

        // 说明：曾经在这里加过「按组件把按钮行里的节点全打开」的兜底（因为第一个浮层里按钮
        // 看起来没显出来）。那是**错的**：
        //  · 按钮的可见性归原版 OptionWidgets / UiOptionRow 管（SetKind 会把另外三个控件关掉）；
        //    强行全开会把下拉框 / 滑杆 / 复选框一起放出来，行里同时出现
        //    LabeledDropdown + LabeledToggle + TextSlider + TextButton，第一个文本还是下拉框的「语言」。
        //  · 当时「看不见」的真正原因是：自测里那个浮层被建在**面板没打开**的时候，
        //    整个浮层的 activeInHierarchy 都是 0（activeSelf 却是 1）。

        /// <summary>
        /// 摆到下排（<paramref name="left"/> = 左那个）。坐标是 `<c>Bottom</c>` 局部坐标：
        /// <c>Bottom</c> 的轴心在底边中点，所以局部 x ∈ [-200,200]。
        /// 左边的：左对齐 + x=100 → 从框体左边算 [100,195]；右边：右对齐 + x=-100 → [205,300]。
        /// 纵向用 0.5 锚 + y=0 → 正好跟翻页按钮同一横带（下排内 [9,49]）。
        /// </summary>
        private static void PlaceBottomButton(GameObject go, bool left)
        {
            if (!UiFinder.IsAlive(go))
                return;
            var rt = go.transform.TryCast<RectTransform>();
            if (rt == null)
                return;
            var ax = left ? 0f : 1f;
            rt.anchorMin = new Vector2(ax, 0.5f);
            rt.anchorMax = new Vector2(ax, 0.5f);
            rt.pivot = new Vector2(ax, 0.5f);
            rt.sizeDelta = new Vector2(95f, 40f);
            rt.anchoredPosition = new Vector2(left ? 100f : -100f, 0f);
        }

        /// <summary>
        /// 铺按钮文案。**每次打开都重铺一遍** —— 这两个按钮是我们自己的，
        /// 但重铺一次代价几乎为零，而且能顶掉任何外来的改动。
        /// </summary>
        private void ApplyButtonLabels()
        {
            try
            {
                if (_okRow != null)
                    _okRow.SetLabel(OkLabel);
                if (_cancelRow != null)
                    _cancelRow.SetLabel(CancelLabel);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("pick-labels", "[Pick] 设置按钮文案失败：" + e.GetType().Name);
            }
            // 刚造出来的行还没算过布局，逼一次（浮层不在面板内容区那棵子树里）。
            try
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(_root.transform.TryCast<RectTransform>());
            }
            catch (Exception) { }
        }

        /// <summary>自测用：几帧后再看一眼按钮（原版列表是下一帧才把条目激活的）。</summary>
        public string DebugState()
        {
            if (!IsValid)
                return "（浮层无效）";
            return Describe() + "  自=" + (_root.activeSelf ? 1 : 0)
                + " 链=" + (_root.activeInHierarchy ? 1 : 0) + "\n  " + DebugButtons()
                + "\n  按钮文字：" + ButtonLabels();
        }
        // ------------------------------------------------------------------
        // 开关
        // ------------------------------------------------------------------

        /// <summary>
        /// 打开。 <paramref name="picked"/> 是初始已选（会被复制，取消时不影响调用方）。
        ///
        /// <para>★ <b>不去重</b>：同一种蓝图可以带多张。</para>
        /// </summary>
        public void Open(string title, List<NamespaceID> all, List<NamespaceID> picked, Action<List<NamespaceID>> onOk)
        {
            if (!IsValid)
                return;
            _onOk = onOk;
            _all.Clear();
            if (all != null)
            {
                for (var i = 0; i < all.Count; i++)
                {
                    if (NamespaceID.IsValid(all[i]))
                        _all.Add(all[i]);
                }
            }
            _picked.Clear();
            if (picked != null)
            {
                for (var i = 0; i < picked.Count; i++)
                {
                    if (NamespaceID.IsValid(picked[i]))
                        _picked.Add(picked[i]);
                }
            }

            try
            {
                _root.SetActive(true);
                _root.transform.SetAsLastSibling();
            }
            catch (Exception) { }
            // 原版按钮的文案归它自己的刷新逻辑管 → 每次打开都重铺一遍。
            ApplyButtonLabels();
            Refresh(true);
            Plugin.Log?.LogInfo("[Pick] 已打开：" + title + "  候选=" + _all.Count
                + "  已选=" + _picked.Count + "  页=" + PageText());
        }

        public void Close()
        {
            try
            {
                if (UiFinder.IsAlive(_root))
                    _root.SetActive(false);
            }
            catch (Exception) { }
        }

        private void Confirm()
        {
            try
            {
                var cb = _onOk;
                var result = new List<NamespaceID>(_picked);
                Close();
                if (cb != null)
                    cb(result);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Pick] 确认失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // 刷新
        // ------------------------------------------------------------------

        private void Refresh(bool resetPage)
        {
            try
            {
                RefreshGrid(resetPage);
                RefreshChosen();
            }
            catch (Exception e)
            {
                Plugin.LogOnce("pick-refresh", "[Pick] 刷新失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private ChoosingBlueprintViewData[] BuildViews()
        {
            var views = new ChoosingBlueprintViewData[_all.Count];
            for (var i = 0; i < _all.Count; i++)
            {
                var id = _all[i];
                views[i] = new ChoosingBlueprintViewData
                {
                    blueprint = BlueprintUi.ViewOf(id, false),
                    // ★ 网格里的卡**始终亮着**：已选过的也不置灰。
                    //   原因：置灰（disabled）与洗白（recharge=1，card_curtain 帷幕）都是
                    //   「不能选 / 已经用完」的语义，而这里每一张都是可再选一张的。
                    //   已选过的也不另做标记（保持与原版选卡界面视觉一致）。
                    disabled = false,
                    selected = false,
                    recharge = 0f,
                };
            }
            return views;
        }

        /// <summary>
        /// 重铺网格。
        ///
        /// <para>★ <b>页数在任何一页选了卡之后不能跳回第一页</b>：<paramref name="resetPage"/> 只在
        /// <see cref="Open"/> 时为 true。另外 <c>UpdateItems</c> 自己会把 <c>currentPage</c> 夹一遍，
        /// 所以只能在它跑完之后再夹一次（用它的 <c>maxPages</c> 才是最新的）。</para>
        /// </summary>
        private void RefreshGrid(bool resetPage)
        {
            if (_displayer == null)
                return;
            var views = BuildViews();
            var target = -1;
            if (_standalone != null)
            {
                var max = Mathf.Max(0, _standalone.maxPages - 1);
                target = resetPage ? 0 : Mathf.Clamp(_standalone.currentPage, 0, max);
                if (_standalone.currentPage != target)
                    _standalone.currentPage = target;
            }
            _displayer.UpdateItems(views);
            // 刷新后给这一页的卡铺上悬停提示（蓝图名 + 说明，没说明就用 id）。
            ApplyCardTooltips(views);
            if (_standalone != null && target >= 0)
            {
                var want = Mathf.Clamp(target, 0, Mathf.Max(0, _standalone.maxPages - 1));
                if (_standalone.currentPage != want)
                {
                    _standalone.currentPage = want;
                    _displayer.UpdateItems(views);
                    ApplyCardTooltips(views);
                }
            }
        }

        /// <summary>
        /// 给网格当前页的每一张卡挂悬停提示。
        ///
        /// <para>克隆出来的卡脱离了官方选卡控制器（它是按**下标**给官方那批卡注册提示来源的），
        /// 所以自己不会有任何提示 —— 得我们一张张铺。</para>
        /// </summary>
        private void ApplyCardTooltips(ChoosingBlueprintViewData[] views)
        {
            ApplyPageTooltips();
        }

        /// <summary>
        /// 给网格里**所有页**的卡铺提示。
        ///
        /// <para>★ <b>必须是所有页，不能只铺当前页</b>：官方
        /// <c>BlueprintDisplayerStandalone.UpdateItems</c> 会**一次性把每一页都建好填满**，
        /// 翻页只是 <c>SetCurrentPage</c> 切一下 <c>activeSelf</c> —— 不经过我们的刷新。
        /// 只铺当前页的话，翻页后新页上的卡一个提示都没有，
        /// 要等到再选一次卡（触发我们的刷新）才补上 —— 实测症状正是
        /// 「翻页后 tooltip 要选过蓝图才显示」。</para>
        ///
        /// <para><see cref="BlueprintTooltip"/> 自己带缓存，重复调用什么也不做。</para>
        /// </summary>
        private void ApplyPageTooltips()
        {
            if (_standalone == null || _standalone.pageList == null)
                return;
            var per = Mathf.Max(1, _standalone.maxCountPerPage);
            var pages = _standalone.pageList.count;
            var current = Mathf.Clamp(_standalone.currentPage, 0, Mathf.Max(0, pages - 1));
            var applied = 0;
            for (var p = 0; p < pages; p++)
            {
                var pageRt = _standalone.pageList.getElement(p);
                if (pageRt == null)
                    continue;
                var page = pageRt.gameObject.GetComponent<BlueprintDisplayerStandalonePage>();
                if (page == null || page.blueprintList == null)
                    continue;
                var count = page.blueprintList.count;
                for (var i = 0; i < count; i++)
                {
                    var index = p * per + i;
                    if (index < 0 || index >= _all.Count)
                        break;
                    var rt = page.blueprintList.getElement(i);
                    var bp = rt == null ? null : rt.GetComponent<Blueprint>();
                    BlueprintTooltip.Apply(bp, _all[index], false);
                    applied++;
                }
            }
            _tooltipPages = pages;
            _tooltipPageIndex = current;
            _tooltipRows = applied;
        }

        /// <summary>
        /// 自测用：数一遍**每一页**的卡上到底有没有提示。
        /// 逐页报（当前页标 *），这样才能证明「翻页之前提示就已经铺好了」。
        /// </summary>
        public string DebugTooltipCoverage()
        {
            if (!IsOpen)
                return "（选卡浮层没开）";
            var sb = new System.Text.StringBuilder(200);
            try
            {
                var per = Mathf.Max(1, _standalone == null ? 40 : _standalone.maxCountPerPage);
                var pages = _standalone == null || _standalone.pageList == null ? 0 : _standalone.pageList.count;
                var current = _standalone == null ? -1 : _standalone.currentPage;
                sb.Append("候选=").Append(_all.Count).Append("  页数=").Append(pages)
                  .Append("  当前页=").Append(current + 1).Append("  上次铺了 ").Append(_tooltipRows).Append(" 格");
                for (var p = 0; p < pages; p++)
                {
                    var pageRt = _standalone.pageList.getElement(p);
                    var page = pageRt == null ? null : pageRt.gameObject.GetComponent<BlueprintDisplayerStandalonePage>();
                    if (page == null || page.blueprintList == null)
                    {
                        sb.Append("\n    第").Append(p + 1).Append("页：拿不到");
                        continue;
                    }
                    var total = page.blueprintList.count;
                    var withTip = 0;
                    var sample = "（无）";
                    for (var i = 0; i < total; i++)
                    {
                        var rt = page.blueprintList.getElement(i);
                        var bp = rt == null ? null : rt.GetComponent<Blueprint>();
                        if (!UiFinder.IsAlive(bp))
                            continue;
                        try
                        {
                            if (bp.gameObject.GetComponent<TooltipHandler>() != null)
                            {
                                withTip++;
                                if (sample == "（无）")
                                {
                                    var idx = p * per + i;
                                    var id = idx >= 0 && idx < _all.Count ? _all[idx] : null;
                                    sample = BlueprintTooltip.TitleOf(id, false) + " / " + BlueprintTooltip.TextOf(id);
                                }
                            }
                        }
                        catch (Exception) { }
                    }
                    sb.Append("\n    第").Append(p + 1).Append("页").Append(p == current ? "*" : " ")
                      .Append("（").Append(p == current ? "当前" : "未显示").Append("）：")
                      .Append(total).Append(" 格，有提示 ").Append(withTip)
                      .Append("  样例=").Append(sample);
                }
            }
            catch (Exception e)
            {
                sb.Append("（统计失败 ").Append(e.GetType().Name).Append("）");
            }
            return sb.ToString();
        }

        private void RefreshChosen()
        {
            if (_chosen == null)
                return;
            // 一张都没选就是空框（不再画「深色空卡」）；同一种蓝图带多张就占多格。
            if (_chosen.Count != _picked.Count)
                _chosen.SetCount(_picked.Count);
            for (var i = 0; i < _picked.Count; i++)
                _chosen.SetCard(i, _picked[i], false, i);
            // ★ 滚到末端：框体宽 386 只装得下 8 张，卡满了以后新加的那张就在框外 ——
            //   不滚的话看起来就是「点了没用 / 数量没变」。
            _chosen.ScrollToEnd();
        }

        // ------------------------------------------------------------------
        // 交互
        // ------------------------------------------------------------------

        public void Tick()
        {
            if (!IsOpen)
                return;
            // 保险：每 5 帧再给网格里所有页的卡补一次悬停提示。
            // 刷新时已经铺过一遍了（见 ApplyPageTooltips），这里只是兜住
            // 「建页/换页比我们那一次调用更晚」这种时序 —— 带缓存，重复调用什么也不做。
            try
            {
                if (++_tooltipFrames % 5 == 0)
                    ApplyPageTooltips();
            }
            catch (Exception) { }
            try
            {
                if (!Input.GetMouseButtonDown(0))
                    return;
                var pointer = (Vector2)Input.mousePosition;

                // 先看已选框：点它就是那一张放回去（按张数，不是按种类）。
                var hit = _chosen == null ? -1 : _chosen.Hit(pointer);
                if (hit >= 0)
                {
                    if (hit < _picked.Count)
                        _picked.RemoveAt(hit);
                    Refresh(false);
                    return;
                }

                // 再看网格：点一下就是**再加一张**（允许同一种蓝图带多张，所以不做"再点取消"）。
                Blueprint card;
                var id = HitGrid(pointer, out card);
                Plugin.Log?.LogInfo("[Pick] 点击 屏(" + Mathf.RoundToInt(pointer.x) + ","
                    + Mathf.RoundToInt(pointer.y) + ")  已选框命中=-1  网格命中="
                    + (NamespaceID.IsValid(id) ? "有" : "无")
                    + (NamespaceID.IsValid(id) ? "" : "（没点到卡；网格本页 " + GridCells() + " 格）"));
                if (NamespaceID.IsValid(id))
                    AddFromGrid(id, card);
            }
            catch (Exception) { }
        }

        /// <summary>当前页有几格（诊断用）。</summary>
        private int GridCells()
        {
            try
            {
                if (_standalone == null || _standalone.pageList == null)
                    return -1;
                var i = Mathf.Clamp(_standalone.currentPage, 0, Mathf.Max(0, _standalone.pageList.count - 1));
                var rt = _standalone.pageList.getElement(i);
                var page = rt == null ? null : rt.GetComponent<BlueprintDisplayerStandalonePage>();
                return page == null || page.blueprintList == null ? -2 : page.blueprintList.count;
            }
            catch (Exception) { return -3; }
        }

        /// <summary>
        /// 从网格加一张，并把镜像框滚到末端。
        ///
        /// <para>（以前这里会播一段「飞入」动画，现已去掉：用户那边看不到，不值得为它承担
        /// <c>MovingBlueprint</c> 会把网格里的卡搬走、以及飞行节点被截边这些副作用。）</para>
        /// </summary>
        private void AddFromGrid(NamespaceID id, Blueprint card)
        {
            _picked.Add(id);
            Refresh(false);
            Plugin.Log?.LogInfo("[Pick] 加一张「" + BlueprintOps.BlueprintName(id, false) + "」→ 已选 "
                + _picked.Count);
        }

        /// <summary>网格里被点到的蓝图 id（无效 = 没点到）；<paramref name="card"/> 是那张卡的组件。</summary>
        private NamespaceID HitGrid(Vector2 screenPoint, out Blueprint card)
        {
            card = null;
            if (_standalone == null || _standalone.pageList == null)
                return null;
            var per = Mathf.Max(1, _standalone.maxCountPerPage);
            var pageIndex = Mathf.Clamp(_standalone.currentPage, 0, Mathf.Max(0, _standalone.pageList.count - 1));
            var pageRt = _standalone.pageList.getElement(pageIndex);
            if (pageRt == null)
                return null;
            var page = pageRt.GetComponent<BlueprintDisplayerStandalonePage>();
            if (page == null || page.blueprintList == null)
                return null;

            var count = page.blueprintList.count;
            for (var i = 0; i < count; i++)
            {
                var rt = page.blueprintList.getElement(i);
                if (rt == null)
                    continue;
                if (!UiHitTest.Contains(rt.gameObject, screenPoint))
                    continue;
                var index = pageIndex * per + i;
                if (index < 0 || index >= _all.Count)
                    return null;
                card = rt.GetComponent<Blueprint>();
                return _all[index];
            }
            return null;
        }


        private string PageText()
        {
            if (_standalone == null)
                return "?";
            try
            {
                return (_standalone.currentPage + 1) + "/" + Mathf.Max(1, _standalone.maxPages);
            }
            catch (Exception)
            {
                return "?";
            }
        }

        /// <summary>两个按钮上真实显示的文案（「第二次打开会不会变回去」只能这样验）。</summary>
        public string ButtonLabels()
        {
            return LabelOf("确认", _okRow) + " | " + LabelOf("取消", _cancelRow)
                + "  行内=" + RowChildren(_okRow == null ? null : _okRow.Root)
                + "/" + RowChildren(_cancelRow == null ? null : _cancelRow.Root);
        }

        private static string LabelOf(string fallback, UiOptionRow row)
        {
            if (row == null || !row.IsValid)
                return "（无）";
            try
            {
                var ts = row.Root.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (var i = 0; ts != null && i < ts.Length; i++)
                {
                    if (ts[i] == null || !ts[i].gameObject.activeInHierarchy)
                        continue;
                    return "'" + ts[i].text + "'";
                }
                return "（" + fallback + " 无文字）";
            }
            catch (Exception e)
            {
                return "（" + e.GetType().Name + "）";
            }
        }

        /// <summary>自测用：已选框第 <paramref name="index"/> 张卡的整棵节点树。</summary>
        public string DebugChosenTree(int index)
        {
            if (!IsOpen || _chosen == null)
                return "（已选框不可用）";
            return _chosen.DebugTree(index);
        }

        /// <summary>
        /// 自测用：把**同一个蓝图**在「已选框」与「网格」里的图标实况打出来对比。
        ///
        /// <para>要回答的问题：两边拿到的是不是同一张贴图（同名的占位图很骗人）。</para>
        /// </summary>
        public string DebugCompareIcon()
        {
            var sb = new System.Text.StringBuilder(900);
            try
            {
                if (_chosen == null)
                    return "（没有已选框）";
                var id = _chosen.IdAt(0);
                sb.Append("镜像#0  蓝图=").Append(id == null ? "（无）" : id.ToString()).Append('\n')
                  .Append("  ").Append(BlueprintStrip.IconInfo(_chosen.BlueprintAt(0)).Replace("\n", "\n  ")).Append('\n');
                if (!NamespaceID.IsValid(id))
                    return sb.ToString();

                // 在网格的全部页里找同一个蓝图
                var bp = FindGridBlueprint(id);
                if (bp == null)
                {
                    sb.Append("网格：没在当前候选页里找到同一个蓝图（无法对比）");
                    return sb.ToString();
                }
                sb.Append("网格(同蓝图)  节点=").Append(bp.gameObject.name).Append('\n')
                  .Append("  ").Append(BlueprintStrip.IconInfo(bp).Replace("\n", "\n  "))
                  .Append("\n[镜像链]\n  ")
                  .Append(BlueprintStrip.ChainDump(_chosen.BlueprintAt(0)).Replace("\n", "\n  "))
                  .Append("\n[网格链]\n  ")
                  .Append(BlueprintStrip.ChainDump(bp).Replace("\n", "\n  "));
            }
            catch (Exception e)
            {
                sb.Append("对比失败：").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        private Blueprint FindGridBlueprint(NamespaceID id)
        {
            if (_standalone == null || _standalone.pageList == null)
                return null;
            var per = Mathf.Max(1, _standalone.maxCountPerPage);
            for (var i = 0; i < _all.Count; i++)
            {
                if (!BlueprintUi.Same(_all[i], id))
                    continue;
                var pageIndex = i / per;
                if (pageIndex >= _standalone.pageList.count)
                    return null;
                var pageRt = _standalone.pageList.getElement(pageIndex);
                var page = pageRt == null ? null : pageRt.GetComponent<BlueprintDisplayerStandalonePage>();
                if (page == null || page.blueprintList == null)
                    return null;
                var cell = i - pageIndex * per;
                if (cell >= page.blueprintList.count)
                    return null;
                var rt = page.blueprintList.getElement(cell);
                return rt == null ? null : rt.GetComponent<Blueprint>();
            }
            return null;
        }

        /// <summary>自测用：已选框第 <paramref name="index"/> 张卡的图标实况。</summary>
        public string DebugChosenIcon(int index)
        {
            if (!IsOpen || _chosen == null)
                return "（已选框不可用）";
            return _chosen.DebugIcon(index);
        }

        /// <summary>自测用：网格里第 <paramref name="cell"/> 张卡的整棵节点树（与镜像卡对比）。</summary></summary>
        public string DebugGridTree(int cell)
        {
            try
            {
                if (_standalone == null || _standalone.pageList == null)
                    return "（没有网格）";
                var pageIndex = Mathf.Clamp(_standalone.currentPage, 0, Mathf.Max(0, _standalone.pageList.count - 1));
                var pageRt = _standalone.pageList.getElement(pageIndex);
                var page = pageRt == null ? null : pageRt.GetComponent<BlueprintDisplayerStandalonePage>();
                if (page == null || page.blueprintList == null || cell >= page.blueprintList.count)
                    return "（没有这张卡）";
                var rt = page.blueprintList.getElement(cell);
                return "网格卡#" + cell + "：\n" + BlueprintStrip.BlueprintTree(rt == null ? null : rt.GetComponent<Blueprint>());
            }
            catch (Exception e)
            {
                return "树 dump 失败：" + e.GetType().Name;
            }
        }

        /// <summary>自测用：网格里第 <paramref name="cell"/> 张卡的图标实况。</summary>
        public string DebugGridIcon(int cell)
        {
            try
            {
                var bp = GridCard(cell);
                if (bp == null)
                    return "（没有这张卡）";
                return BlueprintStrip.IconInfo(bp);
            }
            catch (Exception e)
            {
                return "图标 dump 失败：" + e.GetType().Name;
            }
        }

        private Blueprint GridCard(int cell)
        {
            if (_standalone == null || _standalone.pageList == null)
                return null;
            var pageIndex = Mathf.Clamp(_standalone.currentPage, 0, Mathf.Max(0, _standalone.pageList.count - 1));
            var pageRt = _standalone.pageList.getElement(pageIndex);
            var page = pageRt == null ? null : pageRt.GetComponent<BlueprintDisplayerStandalonePage>();
            if (page == null || page.blueprintList == null || cell < 0 || cell >= page.blueprintList.count)
                return null;
            var rt = page.blueprintList.getElement(cell);
            return rt == null ? null : rt.GetComponent<Blueprint>();
        }

        /// <summary>自测用：给已选框前几张卡套上不同的灰化组合（量像素看哪组才是亮的）。</summary>
        public string DebugVariants()
        {
            if (!IsOpen || _chosen == null)
                return "（已选框不可用）";
            return _chosen.DebugVariants();
        }

        /// <summary>自测用：点当前页的第 <paramref name="cell"/> 格（与 <see cref="Tick"/> 走同一套逻辑）。</summary>
        public string DebugClickGrid(int cell)
        {
            if (!IsOpen)
                return "点网格：浮层没开";
            try
            {
                if (_standalone == null || _standalone.pageList == null)
                    return "点网格：没有网格";
                var per = Mathf.Max(1, _standalone.maxCountPerPage);
                var pageIndex = Mathf.Clamp(_standalone.currentPage, 0, Mathf.Max(0, _standalone.pageList.count - 1));
                var pageRt = _standalone.pageList.getElement(pageIndex);
                var page = pageRt == null ? null : pageRt.GetComponent<BlueprintDisplayerStandalonePage>();
                if (page == null || page.blueprintList == null)
                    return "点网格：拿不到当前页";
                if (cell < 0 || cell >= page.blueprintList.count)
                    return "点网格：第 " + (cell + 1) + " 格越界（本页 " + page.blueprintList.count + " 格）";
                var index = pageIndex * per + cell;
                var id = index >= 0 && index < _all.Count ? _all[index] : null;
                if (!NamespaceID.IsValid(id))
                    return "点网格：第 " + (index + 1) + " 项没蓝图";
                _picked.Add(id);
                Refresh(false);
                return "点网格第 " + (cell + 1) + " 格（第 " + (pageIndex + 1) + " 页）→ 已选=" + _picked.Count
                    + " 页=" + PageText();
            }
            catch (Exception e)
            {
                return "点网格失败：" + e.GetType().Name;
            }
        }

        /// <summary>自测用：翻到第 <paramref name="page"/> 页（0 基）。</summary>
        public string DebugGotoPage(int page)
        {
            if (!IsOpen)
                return "翻页：浮层没开";
            try
            {
                if (_standalone == null)
                    return "翻页：没有网格";
                _standalone.currentPage = Mathf.Clamp(page, 0, Mathf.Max(0, _standalone.maxPages - 1));
                Refresh(false);
                return "翻到第 " + (page + 1) + " 页 → " + PageText();
            }
            catch (Exception e)
            {
                return "翻页失败：" + e.GetType().Name;
            }
        }

        /// <summary>自测用：点已选框里的第 <paramref name="index"/> 张（就是把它放回去）。</summary>
        public string DebugClickChosen(int index)
        {
            if (!IsOpen)
                return "点已选框：浮层没开";
            if (index < 0 || index >= _picked.Count)
                return "点已选框：第 " + (index + 1) + " 张不存在（共 " + _picked.Count + "）";
            _picked.RemoveAt(index);
            Refresh(false);
            return "点已选框第 " + (index + 1) + " 张 → 已选=" + _picked.Count + " 页=" + PageText();
        }

        /// <summary>自测用：关掉再开一次（就是「第二次打开」那个场景）。</summary>
        public string DebugReopen()
        {
            var all = new List<NamespaceID>(_all);
            var picked = new List<NamespaceID>(_picked);
            var cb = _onOk;
            Close();
            Open("（自测重开）", all, picked, cb);
            return "重新打开：页=" + PageText() + "  已选=" + _picked.Count + "  按钮=" + ButtonLabels();
        }

        public string Describe()
        {
            return "候选=" + _all.Count + "  已选=" + _picked.Count + "  页=" + PageText();
        }

        /// <summary>
        /// 自测用：网格当前页的卡**还在不在**（刷新之后不应该少、也不应该被搬走）。
        /// 判定不能只看「组件还在不在」（搬走 GameObject 时组件会跟着走），
        /// 所以这里做**实例 id + 父节点 id 的快照对比**：第一次调用记快照，之后每次都比对。
        /// </summary>
        public string DebugGridAlive()
        {
            if (!IsOpen)
                return "网格完好：浮层没开";
            try
            {
                if (_standalone == null || _standalone.pageList == null)
                    return "网格完好：没有网格";
                var pageIndex = Mathf.Clamp(_standalone.currentPage, 0, Mathf.Max(0, _standalone.pageList.count - 1));
                var pageRt = _standalone.pageList.getElement(pageIndex);
                var page = pageRt == null ? null : pageRt.GetComponent<BlueprintDisplayerStandalonePage>();
                if (page == null || page.blueprintList == null)
                    return "网格完好：拿不到当前页";

                var cells = page.blueprintList.count;
                var alive = 0;
                var sb = new System.Text.StringBuilder(cells * 16);
                for (var i = 0; i < cells; i++)
                {
                    var rt = page.blueprintList.getElement(i);
                    if (rt == null)
                        continue;
                    var bp = rt.GetComponent<Blueprint>();
                    if (bp == null || !UiFinder.IsAlive(bp.gameObject))
                    {
                        sb.Append('x');
                        continue;
                    }
                    alive++;
                    var parent = bp.transform.parent;
                    sb.Append(bp.gameObject.GetInstanceID()).Append('@')
                      .Append(parent == null ? 0 : parent.GetInstanceID()).Append(';');
                }

                var sig = sb.ToString();
                if (_gridSig == null)
                {
                    _gridSig = sig;
                    return "网格完好：本页 " + cells + " 格，卡还在 " + alive + "（已记快照）";
                }
                var same = string.Equals(_gridSig, sig, StringComparison.Ordinal);
                _gridSig = sig;
                return "网格完好：本页 " + cells + " 格，卡还在 " + alive
                    + (same ? "，实例/父节点都没变（判据 OK）" : "，实例或父节点变了（判据 NG：有卡被搬走）");
            }
            catch (Exception e)
            {
                return "网格完好：dump 失败 " + e.GetType().Name;
            }
        }

        /// <summary>上一轮 <see cref="DebugGridAlive"/> 的快照。</summary>
        private string _gridSig;

        /// <summary>
        /// 自测用：**网格的卡到底挂在哪棵树上**。
        ///
        /// <para>怀疑点：克隆出来的 <c>ElementList</c> 不会重映射 <c>_listRoot</c> 与它已持有的条目
        /// （本仓库在镜像条上已经吃过这个）—— 如果网格的 40 个条目其实是**原版面板**的卡，
        /// 那它们不在我们的克隆里、多半也不在激活链上，于是「点网格没反应」。</para>
        /// </summary>
        public string DebugGridWhere()
        {
            var sb = new System.Text.StringBuilder(600);
            if (!IsOpen)
                return "网格位置：浮层没开";
            try
            {
                sb.Append("网格位置：我的克隆根=").Append(UiFinder.PathOf(_root.transform))
                  .Append("（链上=").Append(_root.activeInHierarchy ? 1 : 0).Append("）\n");
                if (_standalone == null || _standalone.pageList == null)
                    return sb.Append("  （没有网格）\n").ToString();

                var pageIndex = Mathf.Clamp(_standalone.currentPage, 0, Mathf.Max(0, _standalone.pageList.count - 1));
                var pageRt = _standalone.pageList.getElement(pageIndex);
                sb.Append("  页节点=").Append(pageRt == null ? "空" : UiFinder.PathOf(pageRt))
                  .Append("  在克隆内=").Append(Inside(pageRt)).Append('\n');
                var page = pageRt == null ? null : pageRt.GetComponent<BlueprintDisplayerStandalonePage>();
                if (page == null || page.blueprintList == null)
                    return sb.Append("  （拿不到当前页）\n").ToString();

                sb.Append("  pageList.count=").Append(_standalone.pageList.count)
                  .Append("  本页格数=").Append(page.blueprintList.count)
                  .Append("  克隆内卡片数=").Append(CountInside()).Append('\n');

                for (var i = 0; i < 2; i++)
                {
                    var rt = page.blueprintList.getElement(i);
                    var bp = rt == null ? null : rt.GetComponent<Blueprint>();
                    sb.Append("  格").Append(i)
                      .Append(" 节点=").Append(rt == null ? "空" : UiFinder.PathOf(rt))
                      .Append("  链上=").Append(rt != null && rt.gameObject.activeInHierarchy ? 1 : 0)
                      .Append("  在克隆内=").Append(Inside(rt))
                      .Append("  蓝图=").Append(bp == null ? "无" : "有")
                      .Append('\n');
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                return "网格位置：dump 失败 " + e.GetType().Name + "\n";
            }
        }

        private bool Inside(Transform t)
        {
            try { return t != null && t.IsChildOf(_root.transform); }
            catch (Exception) { return false; }
        }

        private int CountInside()
        {
            try
            {
                var list = _root.GetComponentsInChildren<Blueprint>(true);
                return list == null ? 0 : list.Length;
            }
            catch (Exception) { return -1; }
        }

        /// <summary>
        /// 自测用：**把真实点击完整走一遍**（坐标由卡片的屏幕矩形算出来）。
        /// 与 <c>Tick</c> 里那段唯一的差别就是没有 <c>Input.GetMouseButtonDown</c>，
        /// 所以它能区分「命中测试挂了」和「点击没被采到」。
        /// </summary>
        public string DebugClickAt(int cell)
        {
            if (!IsOpen)
                return "模拟点击：浮层没开";
            try
            {
                if (_standalone == null || _standalone.pageList == null)
                    return "模拟点击：没有网格";
                var pageIndex = Mathf.Clamp(_standalone.currentPage, 0, Mathf.Max(0, _standalone.pageList.count - 1));
                var pageRt = _standalone.pageList.getElement(pageIndex);
                var page = pageRt == null ? null : pageRt.GetComponent<BlueprintDisplayerStandalonePage>();
                if (page == null || page.blueprintList == null)
                    return "模拟点击：拿不到当前页";
                if (cell < 0 || cell >= page.blueprintList.count)
                    return "模拟点击：第 " + (cell + 1) + " 格越界（本页 " + page.blueprintList.count + "）";

                var rt = page.blueprintList.getElement(cell);
                if (rt == null)
                    return "模拟点击：第 " + (cell + 1) + " 格是空的";
                var p = RectTransformUtility.WorldToScreenPoint(LoadoutPanel.CanvasCamera(),
                    rt.TransformPoint(rt.rect.center));

                var contains = UiHitTest.Contains(rt.gameObject, p);
                var hitChosen = _chosen == null ? -1 : _chosen.Hit(p);
                Blueprint card;
                var id = HitGrid(p, out card);
                var before = _picked.Count;
                if (contains && hitChosen < 0 && NamespaceID.IsValid(id))
                    AddFromGrid(id, card);

                return "模拟点击 格" + cell + " 屏(" + Mathf.RoundToInt(p.x) + "," + Mathf.RoundToInt(p.y) + ")"
                    + "  在卡内=" + contains + "  已选框命中=" + hitChosen
                    + "  网格命中=" + (NamespaceID.IsValid(id) ? "有" : "无")
                    + "  已选 " + before + "→" + _picked.Count;
            }
            catch (Exception e)
            {
                return "模拟点击失败：" + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>自测用：网格当前页的卡都挂上提示了吗（抽前几张看），以及最近一次的文案。</summary>
        public string DebugTooltips()
        {
            var sb = new System.Text.StringBuilder(400);
            sb.Append(BlueprintTooltip.Report()).Append('\n');
            try
            {
                if (_standalone == null || _standalone.pageList == null)
                    return sb.Append("  （没有网格）").ToString();
                var pageIndex = Mathf.Clamp(_standalone.currentPage, 0, Mathf.Max(0, _standalone.pageList.count - 1));
                var pageRt = _standalone.pageList.getElement(pageIndex);
                var page = pageRt == null ? null : pageRt.GetComponent<BlueprintDisplayerStandalonePage>();
                if (page == null || page.blueprintList == null)
                    return sb.Append("  （拿不到当前页）").ToString();
                for (var i = 0; i < 2 && i < page.blueprintList.count; i++)
                {
                    var rt = page.blueprintList.getElement(i);
                    var bp = rt == null ? null : rt.GetComponent<Blueprint>();
                    sb.Append("  网格卡#").Append(i).Append("：")
                      .Append(BlueprintTooltip.Describe(bp)).Append('\n');
                }
                if (_chosen != null)
                    sb.Append("  已选框#0：").Append(_chosen.DescribeTooltip(0)).Append('\n');
                return sb.ToString();
            }
            catch (Exception e)
            {
                return sb.Append("  提示 dump 失败：").Append(e.GetType().Name).ToString();
            }
        }

        /// <summary>自测用：把关键矩形的屏幕坐标打出来（「放不下 / 重叠」只能这样看）。</summary>
        public string DebugRects()
        {
            var sb = new System.Text.StringBuilder(400);
            sb.Append("选卡浮层：").Append(Describe()).Append('\n');
            Rect(sb, "面板", _root);
            if (_displayer != null)
                Rect(sb, "网格", _displayer.gameObject);
            // 头部 400 宽那一列被切成了三段：标题行（镜像框） / 网格块 / 下排。
            var disp = _displayer != null ? _displayer.transform : null;
            var title = FindChild(disp, "TitleRegion");
            if (title != null)
                Rect(sb, "标题行", title.gameObject);
            var block = FindChild(disp, "Blueprints");
            if (block != null)
                Rect(sb, "网格块", block.gameObject);
            var bottom = FindChild(disp, "Bottom");
            if (bottom != null)
                Rect(sb, "下排", bottom.gameObject);
            if (_standalone != null && _standalone.pageRoot != null)
                Rect(sb, "翻页区", _standalone.pageRoot);
            if (_chosen != null)
                Rect(sb, "已选框", _chosen.Root);
            if (_okRow != null && _okRow.IsValid)
                Rect(sb, "确认", _okRow.Root);
            if (_cancelRow != null && _cancelRow.IsValid)
                Rect(sb, "取消", _cancelRow.Root);
            sb.Append("  按钮文字：").Append(ButtonLabels()).Append('\n');
            sb.Append("  ").Append(DebugButtons()).Append('\n');
            GridStates(sb);
            Texts(sb);
            if (_chosen != null)
                sb.Append(_chosen.DebugRects("已选框"));
            return sb.ToString();
        }

        /// <summary>网格里前几张卡的分层状态（「网格的卡为什么是灰的」只能这样看）。</summary>
        private void GridStates(System.Text.StringBuilder sb)
        {
            try
            {
                sb.Append("  网格卡片：");
                if (_standalone == null || _standalone.pageList == null)
                {
                    sb.Append("（无网格）\n");
                    return;
                }
                var pageIndex = Mathf.Clamp(_standalone.currentPage, 0, Mathf.Max(0, _standalone.pageList.count - 1));
                var pageRt = _standalone.pageList.getElement(pageIndex);
                var page = pageRt == null ? null : pageRt.GetComponent<BlueprintDisplayerStandalonePage>();
                if (page == null || page.blueprintList == null)
                {
                    sb.Append("（拿不到当前页）\n");
                    return;
                }
                var n = Mathf.Min(4, page.blueprintList.count);
                for (var i = 0; i < n; i++)
                {
                    var rt = page.blueprintList.getElement(i);
                    var bp = rt == null ? null : rt.GetComponent<Blueprint>();
                    sb.Append('[').Append(i).Append(' ').Append(BlueprintStrip.CardState(bp)).Append("] ");
                }
                sb.Append("（本页 ").Append(page.blueprintList.count).Append(" 张）\n");
            }
            catch (Exception e)
            {
                sb.Append("网格卡片 dump 失败：").Append(e.GetType().Name).Append('\n');
            }
        }

        /// <summary>把面板里还看得见的按钮列出来（「确认 / 取消 到底摆成什么样」只能这样看）。</summary>
        public string DebugButtons()
        {
            var sb = new System.Text.StringBuilder(200);
            sb.Append("可见按钮：");
            try
            {
                var bs = _root.GetComponentsInChildren<Button>(true);
                if (bs == null)
                {
                    sb.Append("（无）");
                    return sb.ToString();
                }
                var shown = 0;
                for (var i = 0; i < bs.Length && shown < 8; i++)
                {
                    if (bs[i] == null || !bs[i].gameObject.activeInHierarchy)
                        continue;
                    var rt = bs[i].transform.TryCast<RectTransform>();
                    var r = PanelDump.ScreenRect(rt);
                    if (shown > 0)
                        sb.Append(" | ");
                    sb.Append(bs[i].gameObject.name)
                      .Append(" 屏x[").Append(Mathf.RoundToInt(r.xMin)).Append(',').Append(Mathf.RoundToInt(r.xMax))
                      .Append("] y[").Append(Mathf.RoundToInt(r.yMin)).Append(',').Append(Mathf.RoundToInt(r.yMax)).Append(']');
                    shown++;
                }
                if (shown == 0)
                    sb.Append("（无）");
            }
            catch (Exception e)
            {
                sb.Append("dump 失败：").Append(e.GetType().Name);
            }
            return sb.ToString();
        }

        /// <summary>把面板里还看得见的文字列出来（「标题文字」到底是哪一条，看这个）。</summary>
        private void Texts(System.Text.StringBuilder sb)
        {
            try
            {
                var all = _root.GetComponentsInChildren<TextMeshProUGUI>(true);
                sb.Append("  文字节点：");
                if (all == null || all.Length == 0)
                {
                    sb.Append("（无）\n");
                    return;
                }
                var shown = 0;
                for (var i = 0; i < all.Length && shown < 14; i++)
                {
                    if (all[i] == null)
                        continue;
                    if (shown > 0)
                        sb.Append(" | ");
                    if (!all[i].gameObject.activeInHierarchy)
                        sb.Append("[关]");
                    sb.Append('\'').Append(all[i].text).Append('\'');
                    shown++;
                }
                sb.Append("（共 ").Append(all.Length).Append(" 个）\n");
            }
            catch (Exception e)
            {
                sb.Append("  文字节点 dump 失败：").Append(e.GetType().Name).Append('\n');
            }
        }

        private static void Rect(System.Text.StringBuilder sb, string label, GameObject go)
        {
            if (!UiFinder.IsAlive(go))
            {
                sb.Append("  ").Append(label).Append(" = （无）\n");
                return;
            }
            var rt = go.transform.TryCast<RectTransform>();
            if (rt == null)
            {
                sb.Append("  ").Append(label).Append(" = 非 RectTransform\n");
                return;
            }
            var r = PanelDump.ScreenRect(rt);
            sb.Append("  ").Append(label).Append(" = 设计 ")
              .Append(Mathf.RoundToInt(rt.rect.width)).Append('x').Append(Mathf.RoundToInt(rt.rect.height))
              .Append(" 屏 x[").Append(Mathf.RoundToInt(r.xMin)).Append(',').Append(Mathf.RoundToInt(r.xMax))
              .Append("] y[").Append(Mathf.RoundToInt(r.yMin)).Append(',').Append(Mathf.RoundToInt(r.yMax))
              .Append("] 屏高=").Append(Screen.height).Append('\n');
        }
    }
}
