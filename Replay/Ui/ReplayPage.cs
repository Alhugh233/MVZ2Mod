using System;
using System.Collections.Generic;
using System.Text;
using DSHCore.Ui;
using Il2CppInterop.Runtime;
using MVZ2.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// DSHCore 的 UiLog 是 internal，跨程序集用不了 —— 别名到本 mod 自己的那份。
using UiLog = Replay.ReplayLog;

namespace Replay
{
    /// <summary>
    /// 「回放」页面：入口挂在**插件页**（主菜单 → 更多选项 → 插件），页面本体是一个面板
    /// （数量 + 导入/刷新/清理/排序 一行 + 回放列表），每一行是 关卡名 · 时间 · 时长 · 播放 · 更多。
    ///
    /// <para><b>设计要点</b></para>
    /// <list type="bullet">
    /// <item>「时间」显示的是**添加进库的时间**（文件创建时间），不是录制开始时刻 ——
    /// 玩家关心的是这条什么时候到他这里的。</item>
    /// <item>排序只有**一个按钮**，点一下按固定的六档循环：
    /// <c>时间↑ → 时间↓ → 时长↑ → 时长↓ → 关卡↑ → 关卡↓</c>。</item>
    /// <item>行尾是【播放】+【更多】。破坏性/低频操作都收进【更多】的详情子层
    /// （重命名 / 导出 / 删除 / 返回），删除**必须二次确认**。</item>
    /// <item>关卡名那一格按**风险档位**着色：<b>白</b> = 没问题；<b>黄</b> = 可能播不好
    /// （环境不同 / 没有环境记录 / 缺版本号 / 游戏版本不同 / 读不出来）；
    /// <b>红</b> = 上次播放真的出过不一致。黄/红在播放前都会先弹窗说明，
    /// 而且两者措辞不同（黄标说「可能出问题的环境」，红标说「上次出问题的时间」）。</item>
    /// </list>
    ///
    /// <para><b>版式与生命周期全部交给 DSHCore 的 <see cref="AddonPage"/></b> ——
    /// 入口按钮的安装/纠正、「插件页索引藏起来 → 叠面板」、页面壳克隆与修补、
    /// 离开插件页自动收起、心跳驱动，都是那套设施的事。这里只写「面板里放什么」。</para>
    ///
    /// <para><b>视觉：</b>页面壳是插件页自己那套索引的克隆体（标题 / 面板 / 返回 三行都在里面），
    /// 行里的文本与按钮是官方 <c>Text</c> / <c>TextButton</c> / <c>InputField</c> 的克隆体
    /// （本仓库铁律：原版 UI 的字体 / Sprite / 音效挂在私有 <c>[SerializeField]</c> 上，
    /// <c>AddComponent</c> 造出来必然是空的）。</para>
    ///
    /// <para><b>线程：</b>全部成员只允许在 Unity 主线程调用。</para>
    /// </summary>
    internal static class ReplayPage
    {
        public const string PanelName = "DSH_ReplayPanel";
        public const string ConfirmName = "DSH_ReplayConfirm";
        public const string EntryName = "DSH_ReplayEntry";
        public const string DetailName = "DSH_ReplayDetail";

        // 单例页面对象。构造时就会把自己登记进 DSHCore 的插件页设施。
        private static readonly Page page = new Page();

        /// <summary>
        /// 确保页面对象已经构造并登记（在 <c>Plugin.Load</c> 里调一次）。
        /// 登记发生在构造函数里，所以这一句的作用是「确定它被构造出来」，返回值不重要。
        /// </summary>
        public static void EnsureRegistered()
        {
            Plugin.Log?.LogInfo("[Replay] 回放页已登记（插件页子页面，Id=" + page.Id + "）");
        }

        // ------------------------------------------------------------------
        // 转发（调用方只认这些）
        // ------------------------------------------------------------------

        /// <summary>请求「回到插件页之后自动打开回放页」（幂等）。</summary>
        public static void RequestOpen()
        {
            page.RequestOpen();
        }

        /// <summary>立刻试着打开（不等心跳节流）。给「结束播放」同帧抢页面用。</summary>
        public static void PumpNow()
        {
            page.PumpNow();
        }

        /// <summary>设置「关掉回放页之后去哪」（null = 就留在插件页，亮出它自己的索引）。</summary>
        public static void SetReturnTo(Action action)
        {
            page.SetReturnTo(action);
        }

        public static bool IsOpen { get { return page.IsOpen; } }

        public static string LastError { get { return page.LastError; } }

        public static bool Open()
        {
            return page.Open();
        }

        public static void Close()
        {
            page.Close();
        }

        // ------------------------------------------------------------------
        // 验收用（见 Ui/ReplayGuiTest.cs）—— 只读，不改变页面状态
        // ------------------------------------------------------------------

        public static bool EntryInstalled { get { return page.EntryInstalled; } }

        public static GameObject EntryNode { get { return page.EntryNode; } }

        public static GameObject PanelNode { get { return page.Root; } }

        /// <summary>面板本体（<c>Center/List</c>）—— 自测要 dump 它的子树。</summary>
        public static GameObject PanelBodyNode { get { return page.BodyNode; } }

        public static GameObject ConfirmNode { get { return page.ConfirmNode; } }

        public static int ItemCount { get { return page.ItemCount; } }

        public static string TestDeleteConfirm()
        {
            return page.TestDeleteConfirm();
        }

        public static void DumpLayout(StringBuilder sb)
        {
            page.DumpLayout(sb);
        }

        // ---- 新增功能的验收探针（见 "新增功能的验收探针"）----

        public static string TestSortCycle()
        {
            return page.TestSortCycle();
        }

        public static string TestOpenDetail()
        {
            return page.TestOpenDetail();
        }

        public static string TestRenameFirst(string newName)
        {
            return page.TestRenameFirst(newName);
        }

        public static string TestCloseDetail()
        {
            return page.TestCloseDetail();
        }

        public static string TestCleanup()
        {
            return page.TestCleanup();
        }

        public static string DescribeSort()
        {
            return page.DescribeSort();
        }

        // ==================================================================
        // 页面本身
        // ==================================================================

        /// <summary>回放页的 <see cref="AddonPage"/> 实现：只负责「面板里放什么」。</summary>
        private sealed class Page : AddonPage
        {
            /// <summary>构造时把自己登记进 DSHCore 的插件页设施（幂等）。</summary>
            public Page()
            {
                Register();
            }

            public override string Id { get { return "Replay"; } }

            public override string EntryLabel { get { return "回放"; } }

            public override string Title { get { return "回放"; } }

            /// <summary>插件页自己那套索引 = 单栏版式（标题 / 面板 / 返回）。</summary>
            protected override AddonShellSource ShellSource
            {
                get { return AddonShellSource.AddonsIndex; }
            }

            /// <summary>
            /// 面板宽度占画布宽度的比例。
            ///
            /// <para>原版那个面板是写死的 <b>500</b> 单位宽（画布 802）—— 里面一行要放
            /// 「名字 / 时间 / 时长 / 播放 / 删除」五个单元格时很紧。所以只改宽度，
            /// 高度仍然由原版 <c>Center</c> 的 <c>flexibleHeight</c> 决定
            /// （= 撑满标题与返回之间的空间）。</para>
            /// </summary>
            protected override float WidthRatio { get { return 0.94f; } }

            /// <summary>回放播放中不装入口、不开页面。</summary>
            protected override bool Suspended()
            {
                return ReplayPlayer.IsActive;
            }

            protected override void OnBuildFailed(string error)
            {
                ShowNotice("回放", error ?? "面板素材还没就绪。");
            }

            // --------------------------------------------------------------
            // 状态
            // --------------------------------------------------------------

            /// <summary>
            /// 名字文本的颜色：**黑（正常） / 黄（可能播不好） / 红（上次出过问题）**。
            ///
            /// <para>正常用黑色（而不是白色）是需求：本面板本身就是浅色底，
            /// 白字在黑底上看得清、在这块面板上反而发虚。
            /// 三档**只改颜色，不改字号/字重/对齐** —— 统一文字样式，只靠颜色区分风险。</para>
            /// </summary>
            private static readonly Color32 ColorOk = new Color32(0, 0, 0, 255);
            private static readonly Color32 ColorSuspect = new Color32(214, 148, 0, 255);
            private static readonly Color32 ColorBad = new Color32(200, 32, 24, 255);

            private RectTransform _panelBody;   // Center/List（带边框美术的那个面板）
            private GameObject _bar;            // 面板里的第一行：数量 + 操作
            private TextMeshProUGUI _countText;
            private UiList _list;
            private GameObject _sortButton;
            private TextMeshProUGUI _sortLabel;

            private List<ReplayInfo> _items = new List<ReplayInfo>();

            /// <summary>
            /// 「数据项 → 那一行的 GameObject」。
            ///
            /// <para><b>存在的唯一理由：排序不要重建 UI。</b>一行要克隆 6 个节点
            /// （1 容器 + 3 文本 + 2 按钮），58 行 = 348 次 <c>Instantiate</c>；
            /// 以前点一下排序就把这 348 次全做一遍 —— 而实际上行的**内容**没变，
            /// 只是顺序变了。现在只调 <c>SetSiblingIndex</c>。</para>
            /// </summary>
            private readonly Dictionary<ReplayInfo, GameObject> _rowByInfo =
                new Dictionary<ReplayInfo, GameObject>();
            /// <summary>默认「添加时间↓」（最新的在最上面），与旧版的默认一致。</summary>
            private ReplayLibrary.SortSpec _sort =
                new ReplayLibrary.SortSpec(ReplayLibrary.SortField.Added, true);

            // 详情弹窗（【更多】）—— 就是官方 CustomDialog 的克隆体
            private ReplayInfo _detail;
            private GameObject _detailRoot;
            private CustomDialog _detailDialog;
            private RectTransform _detailLayout;   // CustomDialog/Root（带 VerticalLayoutGroup）
            private RectTransform _detailButtons;  // CustomDialog/Root/Buttons（官方按钮行容器）
            private TMP_InputField _nameInput;
            private Action<int> _detailManaged;
            private Il2CppSystem.Action<int> _detailIl2Cpp;
            /// <summary>上一次操作（重命名 / 导出）的反馈，画在弹窗正文的最后一行。</summary>
            private string _detailNote;

            // 二次确认 / 提示
            private GameObject _confirm;
            private Action<int> _confirmManaged;
            private Il2CppSystem.Action<int> _confirmIl2Cpp;

            /// <summary>面板本体（<c>Center/List</c>）。</summary>
            public GameObject BodyNode
            {
                get { return _panelBody == null ? null : _panelBody.gameObject; }
            }

            public GameObject ConfirmNode
            {
                get { return UiFinder.IsAlive(_confirm) ? _confirm : null; }
            }

            public int ItemCount
            {
                get { return _items == null ? 0 : _items.Count; }
            }

            public string DescribeSort()
            {
                return _sort.ToString() + (DetailOpen ? "（详情开着）" : "");
            }

            private bool DetailOpen
            {
                get { return UiFinder.IsAlive(_detailRoot); }
            }

            // --------------------------------------------------------------
            // 内容
            // --------------------------------------------------------------

            protected override bool BuildContent(AddonShell shell)
            {
                // ---- 面板本体：Center/List ----
                var listNode = shell.Node("Center/List");
                if (!UiFinder.IsAlive(listNode))
                    return Fail("页面壳里找不到面板（Center/List）。");

                _panelBody = listNode.transform.TryCast<RectTransform>();
                if (_panelBody == null)
                    return Fail("面板（Center/List）不是 RectTransform。");

                // 原版那个 Scroll View 不要：它用的是 Mask（会让 TMP 子网格共用 Stencil 材质 →
                // 随机空指针），改用本库自己的 UiList —— 它的 ScrollView 素材已经被规范化成 RectMask2D。
                //
                // ⚠️ 这个面板节点**本来没有布局组**（原版用锚点摆的），所以必须新建一个 ——
                // 只写「把横向改成纵向」时它直接返回 false，于是后面的
                // preferredHeight/flexibleHeight 全部无人理会：数量行停在 0×0、
                // 列表铺满整个面板盖住了边框（实测就是这么翻车的）。
                shell.PreparePanel(listNode, clearChildren: false, spacing: 8f, padding: 10);

                BuildBar();
                BuildList();

                if (_list == null || !_list.IsValid)
                    return Fail("可滚动列表建不出来（拿不到 ScrollView 素材）。");
                return true;
            }

            protected override void OnOpened()
            {
                Plugin.Log?.LogInfo("[Replay] 回放页已打开：面板宽="
                    + (_panelBody == null ? "?" : Mathf.RoundToInt(_panelBody.sizeDelta.x).ToString()));
                Refresh();
            }

            protected override void OnClosed()
            {
                DestroyDetail();
                DestroyConfirm();
                _panelBody = null;
                _bar = null;
                _countText = null;
                _sortButton = null;
                _sortLabel = null;
                _list = null;
                _items = new List<ReplayInfo>();
            }

            /// <summary>
            /// 面板里的第一行：**数量文字 + 导入 / 刷新 / 清理回放 / 排序**。
            /// 数量文字吃掉剩余宽度（<c>flexibleWidth = 1</c>），按钮用各自的首选宽度。
            /// </summary>
            private void BuildBar()
            {
                _bar = UiKit.HContainer(_panelBody, "DSH_ReplayBar");
                if (!UiFinder.IsAlive(_bar))
                {
                    UiLog.WarnOnce("rp-bar", "数量/操作行建不出来，列表仍然可用（只是不能导入/排序）。");
                    return;
                }
                UiKit.Fit(_bar, -1f, UiKit.RowHeight, 1f);
                // 只吃宽度、不吃高度：flexibleHeight 留给列表，否则它会和列表一起分剩余空间。
                UiKit.SetFlexibleHeight(_bar, -1f);

                // ⚠️ 四个按钮按原来的 0.8×按钮宽排下来会把数量文字挤成 0 宽（实测：
                //    那一行只剩四个按钮，「共 N 个」整个不见了），所以按钮收窄，
                //    并给数量文字一个首选宽度保底。
                _countText = UiKit.Label(_bar.transform, "Count", string.Empty,
                    UiKit.LabelFontSize, UiKit.ButtonWidth * 0.5f, 1f);

                var small = UiKit.ButtonWidth * 0.5f;

                UiKit.TextButton(_bar.transform, "Import", "导入", ImportReplay, small);
                UiKit.TextButton(_bar.transform, "Refresh", "刷新", () => Refresh(), small);

                var clean = UiKit.TextButton(_bar.transform, "Cleanup", "清理回放", AskCleanup, small);
                UiTooltip.Attach(clean, "清理回放",
                    "先清理失效回放；若没有则清理未命名的回放（改过名的一条都不动）；"
                    + "两种都没有时再问你要不要全部删除。");

                // 排序按钮的字会随排序方式变（"排序：时长↓"），给它更宽的位置。
                _sortButton = UiKit.TextButton(_bar.transform, "Sort", SortButtonText(),
                    CycleSort, UiKit.ButtonWidth * 0.85f);
                _sortLabel = UiClone.FindText(_sortButton);
                UiTooltip.Attach(_sortButton, "排序",
                    "点击切换排序方式：时间↑ / 时间↓ / 时长↑ / 时长↓ / 关卡↑ / 关卡↓。");
            }

            /// <summary>
            /// 排序按钮的文案。**分段取词**再拼：「排序：」与「时间↓」各自都是词条，
            /// 直接拼整句（"排序：时间↓"）当键是查不到的（六个组合没必要各登记一条）。
            /// </summary>
            private string SortButtonText()
            {
                return UiLocale.Get("排序：") + UiLocale.Get(_sort.ToString());
            }

            /// <summary>排序方式变了以后重写按钮文案 + 逼布局刷新。</summary>
            private void RefreshSortButton()
            {
                if (!UiFinder.IsAlive(_sortButton))
                    return;
                if (_sortLabel != null)
                {
                    try { _sortLabel.text = SortButtonText() ?? string.Empty; }
                    catch (Exception) { }
                }
                UiKit.Fit(_sortButton, UiKit.ButtonWidth * 0.85f, UiKit.RowHeight, 0f);
                if (UiFinder.IsAlive(_bar))
                    UiKit.Rebuild(_bar);
            }

            private void BuildList()
            {
                _list = UiList.Create(_panelBody, "DSH_ReplayList");
                if (_list == null || !_list.IsValid)
                {
                    _list = null;
                    return;
                }
                // 面板自己有确定高度（它挂在带 flexibleHeight 的 Center 下），所以列表吃掉剩余高度即可。
                _list.SetFlexible();
            }

            /// <summary>把面板第一行的说明文字改成当前数量（需求：这里**只写回放数量**）。</summary>
            private void SetCountText(int shown, int max)
            {
                if (_countText == null)
                    return;
                try
                {
                    // 用格式串而不是拼接：整句才是可翻译的键（"共 {0} 个" → "{0} replays"）。
                    _countText.text = UiLocale.Format("共 {0} 个", shown);
                }
                catch (Exception) { }
            }

            // --------------------------------------------------------------
            // 列表内容
            // --------------------------------------------------------------

            /// <summary>
            /// 重新扫描目录并把列表整个重建（打开页 / 排序变化 / 删除 / 清理 / 重命名之后调）。
            ///
            /// <para><paramref name="force"/> = true 时即使详情弹窗开着也重扫。
            /// 重命名必须用它：新名字写进列表的同时还要把详情重新绑到**新的**
            /// <see cref="ReplayInfo"/> 上（旧对象已随旧文件一起作废）。</para>
            /// </summary>
            private void Refresh(bool force = false)
            {
                if (DetailOpen && !force)
                    return; // 详情弹窗开着时不动列表（关掉时再统一刷）
                if (!UiFinder.IsAlive(RootInternal) || _list == null || !_list.IsValid)
                    return;

                try { _items = ReplayLibrary.LoadAll(); }
                catch (Exception e)
                {
                    _items = new List<ReplayInfo>();
                    UiLog.WarnOnce("rp-list", "列举回放失败：" + e.GetType().Name + ": " + e.Message);
                }
                ReplayLibrary.Sort(_items, _sort);

                // 需求：面板第一行的说明文字**只写数量**。
                SetCountText(_items.Count, ReplayConfig.GetMaxReplays());

                BuildRows();
            }

            private void ClearRows()
            {
                _rowByInfo.Clear();
                if (_list == null || !_list.IsValid)
                    return;
                try { _list.Clear(); }
                catch (Exception) { }
            }

            private void BuildRows()
            {
                if (_list == null || !_list.IsValid)
                    return;
                ClearRows();

                // 行里横向要摆 5 个单元格（名字 / 时间 / 时长 / 播放 / 更多），
                // 按钮太宽会把文字列压到只剩省略号（实测「2026-10-04 21:52」被截成「2026-10-0…」）。
                var small = Mathf.Max(44f, UiKit.ButtonWidth * 0.58f);
                for (var i = 0; i < _items.Count; i++)
                {
                    var info = _items[i];

                    var row = UiKit.HContainer(null, "Row" + i);
                    if (!UiFinder.IsAlive(row))
                        break;
                    UiKit.Fit(row, -1f, UiKit.RowHeight, 1f);

                    // 左侧：改过名就是玩家给的名字，没改过才是关卡名（见 DisplayName）。
                    var name = UiKit.Label(row.transform, "Name", info.DisplayName,
                        UiKit.LabelFontSize, 0f, 2.2f);
                    ApplyRiskColor(name, info);

                    UiKit.Label(row.transform, "Time", info.CreatedText,
                        UiKit.LabelFontSize, 0f, 1.4f);
                    UiKit.Label(row.transform, "Len", info.DurationText,
                        UiKit.LabelFontSize, 0f, 0.7f);

                    var play = info;
                    UiKit.TextButton(row.transform, "Play", "播放", () => Play(play), small);
                    var more = UiKit.TextButton(row.transform, "More", "更多", () => OpenDetail(play), small);
                    UiTooltip.Attach(more, "更多", "查看详细信息，以及重命名 / 导出 / 删除。");

                    if (_list.Add(row) == null)
                    {
                        try { UnityEngine.Object.Destroy(row); } catch (Exception) { }
                        break;
                    }
                    _rowByInfo[info] = row;
                }

                // 动态加完行必须逼布局组算一次，否则新行停在 0×0（本仓库踩过）。
                var anchor = _list.Root;
                if (anchor != null)
                    UiKit.Rebuild(anchor);
            }

            /// <summary>
            /// 按当前 <see cref="_items"/> 的顺序**重排已有行**（不重建）。
            ///
            /// <para>行与数据项是绑死的（文本与按钮回调都是建行时定下的），
            /// 所以排序只是把行摆到对应的下标上。找不到对应行的（理论上不会）跳过。</para>
            /// </summary>
            private void ApplySortOrder()
            {
                if (_list == null || !_list.IsValid)
                    return;
                for (var i = 0; i < _items.Count; i++)
                {
                    GameObject row;
                    if (!_rowByInfo.TryGetValue(_items[i], out row) || !UiFinder.IsAlive(row))
                        continue;
                    if (row.transform.GetSiblingIndex() == i)
                        continue;
                    row.transform.SetSiblingIndex(i);
                }
                var anchor = _list.Root;
                if (anchor != null)
                    UiKit.Rebuild(anchor);
            }

            /// <summary>按风险档位给名字上色（黑 / 黄 / 红），并显式统一文字样式。</summary>
            private static void ApplyRiskColor(TextMeshProUGUI label, ReplayInfo info)
            {
                if (label == null || info == null)
                    return;
                try
                {
                    // 统一文字样式：三档的差别**只在颜色**。克降体可能带自己的字号/字重，
                    // 所以这里逐项写死，免得不同行的字看起来不一样大。
                    label.fontSize = UiKit.LabelFontSize;
                    label.fontStyle = FontStyles.Normal;
                    label.alignment = TextAlignmentOptions.Left;
                    label.overflowMode = TextOverflowModes.Ellipsis;

                    switch (info.Risk)
                    {
                        case ReplayRisk.Bad: label.color = ColorBad; break;
                        case ReplayRisk.Suspect: label.color = ColorSuspect; break;
                        default: label.color = ColorOk; break;
                    }
                }
                catch (Exception) { }
            }

            /// <summary>
            /// 点一下排序按钮：按 <see cref="ReplayLibrary.SortCycle"/> 的固定六档往下走，
            /// 转完一圈回到「时间↑」。
            ///
            /// <para><b>只重排、不重建行</b>（见 <see cref="ApplySortOrder"/>）——
            /// 这是这个按钮不卡的关键。</para>
            /// </summary>
            private void CycleSort()
            {
                var idx = ReplayLibrary.SortIndex(_sort);
                idx = (idx + 1) % ReplayLibrary.SortCycle.Length;
                _sort = ReplayLibrary.SortCycle[idx];

                ReplayLibrary.Sort(_items, _sort);
                RefreshSortButton();
                ApplySortOrder();
                Plugin.Log?.LogInfo("[Replay] 排序切换为 " + _sort);
            }

            // --------------------------------------------------------------
            // 详情弹窗（【更多】）
            // --------------------------------------------------------------

            /// <summary>
            /// 打开【更多】：**用官方 <c>CustomDialog</c> 的壳弹一个独立对话框**。
            ///
            /// <para>早期版本是把列表收起来、在同一个面板里换内容 —— 那是"省事"的做法，
            /// 但版式要自己拼（容器/间距/字号全靠猜），结果就是很丑。现在改成：
            /// 克隆官方那个「标题 + 正文 + 一行按钮」的对话框（<see cref="UiSkinPart.Dialog"/>），
            /// 用 <c>SetDialog</c> 铺标题/正文/四个按钮，**再往按钮之前插一行输入框**。</para>
            ///
            /// <para>版式与字号全部来自官方 prefab，只有正文被显式调小
            /// （原版 24pt，这里要放好几行信息）。参考实现：<c>UiGallery/GalleryPanel.cs</c>。</para>
            ///
            /// <para>⚠️ <c>CustomDialog</c> 的框体高度是 <c>ContentSizeFitter</c> 撑的、
            /// **没有滚动能力**（见 <c>DSHCore/UI_RESEARCH.md</c> §5-25）：所以正文必须是
            /// **一个**节点 + 换行，且行数要克制（按钮只占一行）。</para>
            /// </summary>
            private void OpenDetail(ReplayInfo info)
            {
                if (info == null)
                    return;
                DestroyDetail();
                _detail = info;
                _detailNote = null;

                var parent = UiHost.Resolve(UiLayer.DialogLayer);
                if (parent == null)
                    return;

                _detailRoot = UiSkin.Clone(UiSkinPart.Dialog, parent, DetailName);
                if (!UiFinder.IsAlive(_detailRoot))
                    return;

                _detailDialog = _detailRoot.GetComponent<CustomDialog>();
                if (_detailDialog == null)
                {
                    DestroyDetail();
                    return;
                }

                try
                {
                    // 官方按钮行容器 = CustomDialog.buttonRowList 的列表根；
                    // 它的父节点就是对话框的布局根（带 VerticalLayoutGroup）。
                    var buttons = _detailDialog.buttonRowList;
                    if (buttons == null || buttons._listRoot == null)
                    {
                        DestroyDetail();
                        return;
                    }
                    _detailButtons = buttons._listRoot;
                    _detailLayout = _detailButtons.parent == null
                        ? null
                        : _detailButtons.parent.TryCast<RectTransform>();
                }
                catch (Exception e)
                {
                    UiLog.WarnOnce("rp-detail-nodes", "读详情弹窗的布局节点失败：" + e.GetType().Name);
                    DestroyDetail();
                    return;
                }
                if (_detailLayout == null)
                {
                    DestroyDetail();
                    return;
                }

                // 标题 / 正文 / 四个按钮都过一遍取词（原生控件不会替我们取）。
                // ⚠️ 回调要钉住，否则 GC 一收就是「点按钮什么都不发生」。
                _detailManaged = new Action<int>(OnDetailSelected);
                _detailIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>(_detailManaged);
                _detailDialog.SetDialog(UiLocale.Get("回放详情"), BuildDetailText(info),
                    UiLocale.GetAll(DetailOptions), _detailIl2Cpp);

                // 正文用本页的字号（原版 24pt 在这块小框里会挤成一片）。
                try
                {
                    if (_detailDialog.desc != null)
                        UiClone.SetFontSize(_detailDialog.desc.gameObject, DetailBodyFontSize);
                }
                catch (Exception) { }

                // 输入框行：插在官方按钮区**之前**（与原版「标题 / 正文 / 按钮」的顺序一致）。
                try
                {
                    var row = UiKit.HContainer(_detailLayout, "DSH_DetailNameRow");
                    if (UiFinder.IsAlive(row))
                    {
                        UiKit.Fit(row, -1f, UiKit.RowHeight, 1f);
                        UiKit.SetFlexibleHeight(row, -1f);
                        UiKit.Label(row.transform, "RLabel", "新名字：",
                            UiKit.LabelFontSize, UiKit.ButtonWidth * 0.5f, 0f);
                        _nameInput = CreateNameInput(row.transform, info.Name);
                        row.transform.SetSiblingIndex(_detailButtons.GetSiblingIndex());
                    }
                }
                catch (Exception e)
                {
                    UiLog.WarnOnce("rp-detail-input", "详情弹窗插入输入框失败：" + e.GetType().Name);
                }

                Plugin.Log?.LogInfo("[Replay] 打开回放详情（弹窗）：" + info.Name);
            }

            /// <summary>
            /// 克隆一个输入框（素材是「输入名字」对话框里那个 <c>TMP_InputField</c>）。
            ///
            /// <para>现有可参考的实现：<c>Loadout/Ui/UiGrid.cs</c> 的 <c>UiField</c>、
            /// <c>Loadout/Ui/UiSearchBox.cs</c>。</para>
            /// </summary>
            private TMP_InputField CreateNameInput(Transform parent, string value)
            {
                var go = UiSkin.Clone(UiSkinPart.InputField, parent, "DNameInput");
                if (!UiFinder.IsAlive(go))
                {
                    UiLog.WarnOnce("rp-nameinput", "拿不到输入框素材（InputField）—— 重命名不可用。");
                    return null;
                }
                var input = go.GetComponent<TMP_InputField>();
                if (input == null)
                {
                    UiLog.WarnOnce("rp-nameinput2", "输入框克隆体上没有 TMP_InputField。");
                    return null;
                }
                try
                {
                    // 克隆来的是「输入玩家名字」那个框：放开长度限制、改成单行。
                    input.characterLimit = 64;
                    input.lineType = TMP_InputField.LineType.SingleLine;
                    input.contentType = TMP_InputField.ContentType.Standard;
                    input.restoreOriginalTextOnEscape = true;
                    input.text = value ?? string.Empty;
                }
                catch (Exception e)
                {
                    UiLog.WarnOnce("rp-nameinput-cfg", "配置输入框失败：" + e.GetType().Name);
                }
                // 占位文字是「输入你的名字」之类，放在这里会误导 —— 清掉。
                try
                {
                    var ph = input.placeholder;
                    if (ph != null)
                    {
                        var t = ph.TryCast<TextMeshProUGUI>();
                        if (t != null)
                            t.text = string.Empty;
                    }
                }
                catch (Exception) { }
                // 输入框吃掉本行剩余宽度。
                UiKit.Fit(go, 0f, UiKit.RowHeight, 1f);
                return input;
            }

            /// <summary>详情弹窗的按钮（下标就是 <see cref="OnDetailSelected"/> 收到的值）。</summary>
            private static readonly string[] DetailOptions = { "重命名", "导出 .mrep", "删除", "返回" };

            /// <summary>详情弹窗正文的字号。原版 24pt 放不下这么多行。</summary>
            private const float DetailBodyFontSize = 14f;

            private void OnDetailSelected(int index)
            {
                // 真玩家点击时 MainSceneUI 会先收起框体；我们这里是自己的弹窗，不用那一步。
                switch (index)
                {
                    case 0: RenameFromDetail(); break;
                    case 1: ExportFromDetail(); break;
                    case 2: AskDelete(_detail); break;
                    default: EndDetail(); break;
                }
            }

            /// <summary>把弹窗正文重写一遍（重命名 / 导出之后就地刷新，而不是再叠一个提示框）。</summary>
            private void RefreshDetailText()
            {
                try
                {
                    if (_detailDialog != null && _detailDialog.desc != null && _detail != null)
                        _detailDialog.desc.text = BuildDetailText(_detail);
                }
                catch (Exception) { }
            }

            private string BuildDetailText(ReplayInfo info)
            {
                var sb = new StringBuilder(360);
                sb.Append(info.DisplayName).Append('\n');
                sb.Append(UiLocale.Get("关卡：")).Append(info.StageText).Append('\n');
                sb.Append(UiLocale.Get("添加时间：")).Append(info.AddedText).Append('\n');
                sb.Append(UiLocale.Get("录制时间：")).Append(info.RecordedText).Append('\n');
                sb.Append(UiLocale.Get("时长：")).Append(info.DurationText)
                  .Append("　").Append(UiLocale.Get("大小：")).Append(SizeText(info)).Append('\n');
                sb.Append(UiLocale.Get("格式："))
                  .Append(info.SpecVersion > 0
                      ? UiLocale.Format("规范 {0}／格式 {1}", info.SpecVersion, info.Version)
                      : UiLocale.Format("格式 {0}", info.Version))
                  .Append(info.Debug ? UiLocale.Get("（调试文件）") : "")
                  .Append('\n');
                sb.Append(UiLocale.Get("录制环境：")).Append(EnvText(info));
                if (!string.IsNullOrEmpty(info.Error))
                    sb.Append('\n').Append(UiLocale.Get("问题：")).Append(info.Error);
                if (info.Health == ReplayHealth.Bad && info.LastMismatch > 0)
                    sb.Append('\n').Append(UiLocale.Get("上次播放："))
                      .Append(UiLocale.Format("第 {0} tick 出现不一致，共 {1} 处",
                          info.LastBadTick, info.LastMismatch));
                if (!string.IsNullOrEmpty(_detailNote))
                    sb.Append('\n').Append(_detailNote);
                return sb.ToString();
            }

            private static string SizeText(ReplayInfo info)
            {
                var kb = info.SizeBytes / 1024.0;
                return kb >= 1024
                    ? (kb / 1024.0).ToString("0.0") + " MB"
                    : kb.ToString("0.0") + " KB";
            }

            private static string EnvText(ReplayInfo info)
            {
                var em = info.EnvMatches;
                var state = em == true ? UiLocale.Get("与当前一致")
                    : em == false ? UiLocale.Get("与当前不同")
                    : UiLocale.Get("文件里没有环境记录");
                var game = string.IsNullOrEmpty(info.GameVersion) ? "?" : info.GameVersion;
                return game + "／" + state;
            }

            /// <summary>关掉详情弹窗，回到列表。</summary>
            private void EndDetail()
            {
                DestroyDetail();
                Refresh();
            }

            private void DestroyDetail()
            {
                _detail = null;
                _nameInput = null;
                _detailNote = null;
                _detailDialog = null;
                _detailLayout = null;
                _detailButtons = null;
                _detailManaged = null;
                _detailIl2Cpp = null;
                if (UiFinder.IsAlive(_detailRoot))
                {
                    try { UnityEngine.Object.Destroy(_detailRoot); }
                    catch (Exception) { }
                }
                _detailRoot = null;
            }

            private void RenameFromDetail()
            {
                var info = _detail;
                if (info == null)
                    return;
                var wanted = _nameInput != null ? _nameInput.text : null;
                var oldName = info.Name;

                string err;
                if (!ReplayLibrary.Rename(info, wanted, out err))
                {
                    _detailNote = UiLocale.Get("重命名失败：") + (err ?? UiLocale.Get("未知原因。"));
                    RefreshDetailText();
                    return;
                }

                ShowToast(UiLocale.Get("已重命名为：") + info.Name);

                // ⚠️ 必须**整个重扫**（force）：`LoadAll` 会造一批**新的 ReplayInfo**，
                // 而详情还抱着刚被丢弃的那一个。不重绑的话：
                //   ① 列表里那一行还写着旧名字；
                //   ② 接着点「导出」会去拿旧 `Path`（已经不存在的文件）。
                Refresh(true);
                _detail = null;
                for (var i = 0; i < _items.Count; i++)
                {
                    if (string.Equals(_items[i].Name, info.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        _detail = _items[i];
                        break;
                    }
                }
                if (_detail == null)
                {
                    // 理论上不会发生；真发生了就把弹窗收掉，别留一个指向空气的界面。
                    UiLog.WarnOnce("rp-rename-rebind", "重命名后在新列表里找不到「" + info.Name + "」，关闭详情");
                    EndDetail();
                    return;
                }
                if (_nameInput != null)
                {
                    try { _nameInput.text = _detail.Name; }
                    catch (Exception) { }
                }
                _detailNote = UiLocale.Get("已重命名为：") + _detail.Name
                    + UiLocale.Get("（不计入数量上限，也不会被自动清理）");
                RefreshDetailText();
                Plugin.Log?.LogInfo("[Replay] 从界面重命名 " + oldName + " → " + _detail.Name);
            }

            private void ExportFromDetail()
            {
                var info = _detail;
                if (info == null)
                    return;
                string dest, err;
                bool cancelled;
                var ok = ReplayExporter.DialogExport(info.Path, out dest, out err, out cancelled);
                if (cancelled)
                    return;
                if (ok)
                {
                    // 导出是成功的、不需要解释的事 —— 用原生弹出提示（不挡界面）。
                    ShowToast(UiLocale.Get("已导出到：") + dest);
                    return;
                }
                _detailNote = UiLocale.Get("导出失败：") + (err ?? UiLocale.Get("未知原因。"));
                RefreshDetailText();
            }

            /// <summary>
            /// 用**游戏自己的弹出提示**（Toast）报一句结果。
            ///
            /// <para>这些提示（重命名成功 / 导出成功 / 清理结果）都是「告知」而非「询问」，
            /// 以前走 <c>Confirm</c> 弹一个单按钮对话框 —— 要玩家点一下「好」才能继续，很碍事。
            /// 改成 Toast：不抢焦点、不挡界面，几秒后自己消。</para>
            ///
            /// <para>取词还是要自己过一遍（Toast 拿到什么就画什么）。取不到场景时退化成日志。</para>
            /// </summary>
            private static void ShowToast(string text)
            {
                if (string.IsNullOrEmpty(text))
                    return;
                try
                {
                    var scene = UiGame.Scene;
                    if (scene != null)
                    {
                        scene.ShowPopup(UiLocale.Get(text) ?? text);
                        return;
                    }
                }
                catch (Exception e)
                {
                    UiLog.WarnOnce("rp-toast", "原生提示失败（改为记日志）：" + e.GetType().Name);
                }
                Plugin.Log?.LogInfo("[Replay] " + text);
            }

            // --------------------------------------------------------------
            // 动作
            // --------------------------------------------------------------

            private void Play(ReplayInfo info)
            {
                if (info == null)
                    return;
                if (info.IsBroken)
                {
                    ShowNotice("无法播放", UiLocale.Get("这个回放读不出来：") + "\n" + info.Error);
                    return;
                }

                // 黄/红标在播放前必须**先说清楚为什么**，而且两种颜色的措辞不同：
                //   黄 = 「这条可能在当前环境下播不好」（环境不同 / 没环境记录 / 缺版本号 / 游戏版本不同）
                //   红 = 「上次播放到什么时候出的问题」（第几 tick、共几处不一致）
                var risk = info.Risk;
                if (risk == ReplayRisk.Bad)
                {
                    var when = info.LastMismatch > 0
                        ? UiLocale.Format("上次播放到第 {0} tick 时出现了 {1} 处不一致。",
                            info.LastBadTick, info.LastMismatch)
                        : UiLocale.Get("上次播放时出现了状态不一致。");
                    Confirm("回放可能无法正确播放",
                        when + "\n\n" + UiLocale.Get("回放只是按录制时的输入重演，无法修正录制时就已经发生的偏差。"),
                        "仍然播放", "返回",
                        index => { if (index == 0) DoPlay(info); });
                    return;
                }
                if (risk == ReplayRisk.Suspect)
                {
                    Confirm("可能不兼容的回放",
                        UiLocale.Get("这条回放可能在当前环境下无法正确重演：") + "\n"
                        + info.SuspectReasons,
                        "仍然播放", "返回",
                        index => { if (index == 0) DoPlay(info); });
                    return;
                }

                DoPlay(info);
            }

            private void DoPlay(ReplayInfo info)
            {
                // 先关页面：回放要切到关卡场景，留着面板会在关卡里挡住画面。
                Close();
                var msg = ReplayPlayer.Start(info.Name);
                Plugin.Log?.LogInfo("[Replay] 从界面播放 " + info.Name + " → " + msg);
            }

            /// <summary>
            /// 「导入」：走游戏原生的「打开文件」对话框（与「切换存档 → 导入」同一个对话框），
            /// 选中的 <c>.mrep</c> 会被复制进回放目录。
            ///
            /// <para>用原生对话框而不是自己造一个路径输入框：那个对话框是系统级的，
            /// 能直接看到 U 盘 / 下载目录，也不受「游戏画面里的输入框拿不到中文路径」这类问题影响。</para>
            ///
            /// <para>⚠️ 直接调 SFB 而不是 <c>FileHelper.OpenExternalFile</c>：后者是
            /// <c>async Task</c> + 回调，在 interop 里 await 不了（见 <c>ReplayEndPanel.DoExport</c> 的说明），
            /// 而 <c>OpenFilePanel</c> 本身就是同步返回路径数组的。</para>
            /// </summary>
            private void ImportReplay()
            {
                string path = null;
                try
                {
                    var picked = SFB.StandaloneFileBrowser.OpenFilePanel("", "", "mrep", false);
                    if (picked != null && picked.Length > 0)
                        path = picked[0];
                }
                catch (Exception e)
                {
                    ShowNotice("导入", UiLocale.Get("打开文件对话框失败：")
                        + "\n" + e.GetType().Name + "：" + e.Message);
                    return;
                }

                // 玩家在对话框里点了「取消」：什么都不做，也别弹个提示打扰他。
                if (string.IsNullOrEmpty(path))
                    return;

                string err;
                var ok = ReplayLibrary.Import(path, out err);
                if (ok)
                    Refresh();
                if (ok)
                {
                    ShowToast(UiLocale.Get("已导入到回放目录：") + ReplayLibrary.ImportedName(path));
                }
                else
                {
                    ShowNotice("导入失败", err ?? UiLocale.Get("未知原因。"));
                }
            }

            /// <summary>
            /// 「清理回放」：失效 → 未命名 → 两种都没有时问玩家要不要全删。
            /// 清理完**用小字提示清掉了哪一类、几条**（需求）。
            /// </summary>
            private void AskCleanup()
            {
                if (!UiFinder.IsAlive(RootInternal))
                    return;
                if (_items == null || _items.Count == 0)
                {
                    ShowToast(UiLocale.Get("回放目录里没有回放。"));
                    return;
                }

                // ⚠️ 必须先**算**再问，绝不能先删。
                //
                // 这里原来直接调 ReplayLibrary.Cleanup()：它按「失效 → 未命名」的顺序命中第一类。
                // 目录里只要没有失效回放，它就会命中「未命名」那一类 —— 也就是把玩家目录里
                // **所有自动命名的回放一次性删光**，而且当时连一次确认都没有。
                // （真实事故：57 条回放被一次点击清空，只剩那条改过名的。）
                ReplayLibrary.CleanupKind kind;
                int preview;
                try { preview = ReplayLibrary.CleanupPreview(_items, out kind); }
                catch (Exception e)
                {
                    ShowNotice("清理回放失败", e.GetType().Name + "：" + e.Message);
                    return;
                }

                if (kind != ReplayLibrary.CleanupKind.None && preview > 0)
                {
                    Confirm("清理回放", CleanupAskText(kind, preview), "清理", "取消",
                        index =>
                        {
                            if (index != 0)
                                return;
                            ReplayLibrary.CleanupKind done;
                            string err;
                            int removed;
                            try { removed = ReplayLibrary.Cleanup(_items, out done, out err); }
                            catch (Exception e)
                            {
                                ShowNotice("清理回放失败", e.GetType().Name + "：" + e.Message);
                                return;
                            }
                            Refresh();
                            // 需求：清理后**小字提示**清掉了哪一类、几条 —— 用原生 Toast。
                            ShowToast(CleanupResultText(done, removed));
                        });
                    return;
                }

                // 自动清理两种都没命中 —— 问玩家要不要全删。
                var total = _items.Count;
                Confirm("清理回放",
                    UiLocale.Get("没有失效或未命名的回放可清理。") + "\n"
                    + UiLocale.Format("要删除全部 {0} 条回放吗？", total) + "\n\n"
                    + UiLocale.Get("删掉之后无法恢复，确定吗？"),
                    "全部删除", "取消",
                    index =>
                    {
                        if (index != 0)
                            return;
                        string derr;
                        int n;
                        try { n = ReplayLibrary.DeleteAll(_items, out derr); }
                        catch (Exception e)
                        {
                            ShowNotice("清理回放失败", e.GetType().Name + "：" + e.Message);
                            return;
                        }
                        Refresh();
                        ShowToast(CleanupResultText(ReplayLibrary.CleanupKind.All, n));
                    });
            }

            /// <summary>
            /// 清理前的确认文字。**必须**同时说清「哪一类」和「几条」——
            /// 「未命名」这一类动辄几十条，玩家看到按钮叫「清理」会以为只删几个坏文件。
            /// </summary>
            private static string CleanupAskText(ReplayLibrary.CleanupKind kind, int n)
            {
                string what;
                switch (kind)
                {
                    case ReplayLibrary.CleanupKind.Broken:
                        what = UiLocale.Format("已失效、读不出头的回放 {0} 条", n);
                        break;
                    case ReplayLibrary.CleanupKind.AutoNamed:
                        what = UiLocale.Format("未命名的回放 {0} 条（改过名的一条都不会动）", n);
                        break;
                    default:
                        what = UiLocale.Format("回放 {0} 条", n);
                        break;
                }
                return UiLocale.Format("将清理 {0}。", what) + "\n\n"
                    + UiLocale.Get("删掉之后无法恢复，确定吗？");
            }

            /// <summary>清理结果的小字提示（必须说清清的是**哪一类**）。</summary>
            private static string CleanupResultText(ReplayLibrary.CleanupKind kind, int n)
            {
                switch (kind)
                {
                    case ReplayLibrary.CleanupKind.Broken:
                        return UiLocale.Format("已清理 {0} 条失效回放。", n);
                    case ReplayLibrary.CleanupKind.AutoNamed:
                        return UiLocale.Format("已清理 {0} 条未命名回放（改过名的一条都没动）。", n);
                    case ReplayLibrary.CleanupKind.All:
                        return UiLocale.Format("已删除全部 {0} 条回放。", n);
                    default:
                        return UiLocale.Get("没有清理任何回放。");
                }
            }

            /// <summary>删除：二次确认后真的删。</summary>
            private void AskDelete(ReplayInfo info)
            {
                if (info == null)
                    return;
                Confirm("删除回放",
                    info.DisplayName + "\n" + info.CreatedText + UiLocale.Get("　时长 ") + info.DurationText
                    + "\n\n" + UiLocale.Get("删掉之后无法恢复，确定吗？"),
                    "删除", "取消",
                    index => { if (index == 0) DoDelete(info); });
            }

            private void DoDelete(ReplayInfo info)
            {
                string err;
                var ok = ReplayLibrary.Delete(info, out err);
                if (!ok)
                {
                    ShowNotice("删除失败", err ?? UiLocale.Get("未知原因。"));
                    return;
                }
                ShowToast(UiLocale.Get("已删除：") + info.Name);

                // 从【更多】里删的：先退回列表（EndDetail 会自己 Refresh）。
                if (DetailOpen)
                {
                    EndDetail();
                    return;
                }
                for (var i = 0; i < _items.Count; i++)
                {
                    if (ReferenceEquals(_items[i], info))
                    {
                        _items.RemoveAt(i);
                        break;
                    }
                }
                Refresh();
            }

            // --------------------------------------------------------------
            // 二次确认 / 提示面板
            // --------------------------------------------------------------

            /// <summary>
            /// 弹一个二级确认面板（<paramref name="cancelLabel"/> 传 null 就只有「好」一个按钮 = 纯提示）。
            /// 面板是官方对话框的克隆体，自带全屏点击拦截，所以是模态的。
            /// </summary>
            private void Confirm(string title, string desc, string okLabel, string cancelLabel,
                Action<int> onSelect)
            {
                DestroyConfirm();

                var parent = UiHost.Resolve(UiLayer.DialogLayer);
                if (parent == null)
                    return;

                _confirm = UiSkin.Clone(UiSkinPart.Dialog, parent, ConfirmName);
                if (!UiFinder.IsAlive(_confirm))
                    return;

                var dlg = _confirm.GetComponent<CustomDialog>();
                if (dlg == null)
                {
                    DestroyConfirm();
                    return;
                }

                var options = string.IsNullOrEmpty(cancelLabel)
                    ? new[] { okLabel }
                    : new[] { okLabel, cancelLabel };

                _confirmManaged = new Action<int>(index =>
                {
                    DestroyConfirm();
                    if (onSelect != null)
                        onSelect(index);
                });
                _confirmIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>(_confirmManaged);

                try
                {
                    // 标题 / 正文 / 按钮文案都过一遍取词：走游戏自己的 CustomDialog，
                    // 那个不会自动翻（官方各处传进去的都是已经取过词的文本）。
                    // 正文常常是拼出来的（前缀 + 动态内容），拼的部分在调用点已经取过词，
                    // 这里整句查不到就原样返回 —— 两段都对。
                    dlg.SetDialog(UiLocale.Get(title), UiLocale.Get(desc),
                        UiLocale.GetAll(options), _confirmIl2Cpp);
                }
                catch (Exception e)
                {
                    UiLog.WarnOnce("rp-confirm", "弹确认面板失败：" + e.GetType().Name);
                    DestroyConfirm();
                }
            }

            private void ShowNotice(string title, string text)
            {
                Confirm(title, text, "好", null, null);
            }

            private void DestroyConfirm()
            {
                if (UiFinder.IsAlive(_confirm))
                {
                    try { UnityEngine.Object.Destroy(_confirm); }
                    catch (Exception) { }
                }
                _confirm = null;
                _confirmManaged = null;
                _confirmIl2Cpp = null;
            }

            // --------------------------------------------------------------
            // 验收
            // --------------------------------------------------------------

            /// <summary>调一次「删除」的二次确认（验收用；用第一条，没有条目就什么都不做）。</summary>
            public string TestDeleteConfirm()
            {
                if (_items == null || _items.Count == 0)
                    return "（没有回放，跳过二次确认测试）";
                var info = _items[0];
                AskDelete(info);
                return UiFinder.IsAlive(_confirm) ? "已弹出确认面板：" + info.Name : "确认面板没弹出来";
            }

            /// <summary>把排序按六档走一圈（验收用），返回每一步的文案。</summary>
            public string TestSortCycle()
            {
                var sb = new StringBuilder(200);
                sb.Append("起始=").Append(_sort);
                var back = false;
                for (int i = 0; i < ReplayLibrary.SortCycle.Length; i++)
                {
                    CycleSort();
                    sb.Append(" → ").Append(_sort);
                    if (i == ReplayLibrary.SortCycle.Length - 1)
                    {
                        // 转完一圈应当回到「时间↑」
                        back = _sort.Field == ReplayLibrary.SortField.Added && !_sort.Desc;
                    }
                }
                sb.Append("；按钮文案=「").Append(_sortLabel != null ? _sortLabel.text : "（无）").Append("」");
                sb.Append("；一圈回到起点=").Append(back);
                return sb.ToString();
            }

            /// <summary>打开第一条的【更多】详情弹窗（验收用）。</summary>
            public string TestOpenDetail()
            {
                if (_items == null || _items.Count == 0)
                    return "（没有回放，跳过详情测试）";
                OpenDetail(_items[0]);
                if (!DetailOpen)
                    return "详情弹窗没建出来";
                var title = "";
                var body = "";
                var btns = "";
                try
                {
                    title = _detailDialog.title != null ? _detailDialog.title.text : "";
                    body = _detailDialog.desc != null ? _detailDialog.desc.text : "";
                    var row = _detailButtons != null
                        ? _detailButtons.GetComponent<MVZ2.UI.ButtonRow>() : null;
                    btns = row != null ? "行上有 ButtonRow" : "（拿不到 ButtonRow）";
                }
                catch (Exception) { }
                var bodySize = "?";
                try
                {
                    if (_detailDialog.desc != null)
                        bodySize = ((int)_detailDialog.desc.fontSize).ToString();
                }
                catch (Exception) { }
                return "已打开弹窗：「" + _items[0].Name + "」；标题=「" + title
                    + "」；正文字号=" + bodySize + "；正文行数=" + (body ?? "").Split('\n').Length
                    + "；有输入框=" + (_nameInput != null)
                    + "；输入框内容=「" + (_nameInput != null ? _nameInput.text : "") + "」"
                    + "；" + btns;
            }

            /// <summary>
            /// 在详情里改名（验收用，绕开输入框直接给名字）。
            ///
            /// <para>⚠️ 跑在玩家的真实回放目录上，所以：① **只动自动命名的**
            /// （不是自动命名的一律跳过）；② 改完**立即改回原名**，净效果为零。</para>
            /// </summary>
            public string TestRenameFirst(string newName)
            {
                if (!DetailOpen)
                    return "详情子层没开着";

                var info = _detail;
                if (info == null)
                    return "详情没指向任何回放";

                var orig = info.Name;
                if (!ReplayLibrary.IsAutoName(orig))
                    return "「" + orig + "」不是自动命名的（是玩家取的名字），跳过重命名";

                if (_nameInput != null)
                {
                    try { _nameInput.text = newName ?? string.Empty; }
                    catch (Exception) { }
                }
                RenameFromDetail();

                var renamed = false;
                ReplayInfo back = null;
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].Name == newName)
                    {
                        renamed = true;
                        back = _items[i];
                        break;
                    }
                }

                // 改回原名（净效果为零，不动玩家数据）。
                var restored = false;
                if (back != null)
                {
                    string err;
                    restored = ReplayLibrary.Rename(back, orig, out err);
                }
                Refresh();

                return "「" + orig + "」→「" + newName + "」改名成功=" + renamed
                    + "；已改回原名=" + restored
                    + "（自动命名的判定：改过名后 " + ReplayLibrary.IsAutoName(newName)
                    + " = false ⇒ 不计入数量上限、不会被自动清理）";
            }

            /// <summary>从详情弹窗返回列表（验收用）。</summary>
            public string TestCloseDetail()
            {
                if (!DetailOpen)
                    return "详情弹窗没开着";
                EndDetail();
                return "已关闭详情弹窗；条目数=" + ItemCount
                    + "；列表仍在=" + UiFinder.IsAlive(_bar);
            }

            /// <summary>
            /// 跑一次「清理回放」的**判定**（验收用）。
            ///
            /// <para>⚠️ 用 <see cref="ReplayLibrary.CleanupPreview"/> 而**不真删**：
            /// 自测跑在玩家的真实回放目录上，而「清理」真的会删文件。
            /// 分类逻辑与真删完全共用（<c>SelectCleanupTargets</c>）。</para>
            /// </summary>
            public string TestCleanup()
            {
                if (_items == null || _items.Count == 0)
                    return "（没有回放，跳过清理测试）";
                var before = _items.Count;
                ReplayLibrary.CleanupKind kind;
                int target;
                try { target = ReplayLibrary.CleanupPreview(_items, out kind); }
                catch (Exception e) { return "清理判定出错：" + e.GetType().Name; }
                var text = CleanupResultText(kind, target);
                return "共 " + before + " 条 → 本次会删 " + target + " 条，类别=" + kind
                    + "（未真的删；真删走按钮）。提示文案=「" + text + "」";
            }

            /// <summary>
            /// 把页面的**真实屏幕矩形**写进报告。
            ///
            /// <para>「节点存在 + 尺寸正确」不等于「看得见」—— 必须用数字：
            /// 矩形要落在屏幕内、行与行 y 不重叠、控件行里只有一个子控件是 on。</para>
            /// </summary>
            public void DumpLayout(StringBuilder sb)
            {
                if (sb == null)
                    return;

                sb.Append(DescribeEntry());
                if (Shell != null)
                    sb.Append(Shell.Describe());

                sb.Append("面板     : ").Append(UiKit.Describe(BodyNode)).Append('\n');
                sb.Append("数量行   : ").Append(UiKit.Describe(_bar)).Append('\n');
                sb.Append("  数量文字 : ")
                  .Append(_countText == null ? "（无）" : "\"" + UiKit.Trim(_countText.text, 24) + "\"")
                  .Append('\n');
                sb.Append("  排序按钮 : ")
                  .Append(_sortLabel == null ? "（无）" : "\"" + UiKit.Trim(_sortLabel.text, 24) + "\"")
                  .Append("  ").Append(UiTooltip.Describe(_sortButton))
                  .Append('\n');
                sb.Append("列表     : ").Append(UiKit.Describe(_list == null ? null : _list.Root)).Append('\n');
                sb.Append("详情弹窗 : ").Append(UiKit.Describe(_detailRoot)).Append('\n');
                sb.Append("确认面板 : ").Append(UiKit.Describe(_confirm)).Append('\n');
                sb.Append("条目数   : ").Append(ItemCount)
                  .Append("（排序 ").Append(_sort.ToString()).Append("）\n");
                sb.Append("屏幕     : ").Append(Screen.width).Append('x').Append(Screen.height).Append('\n');

                if (_list != null && _list.IsValid && _list.Content != null)
                {
                    var content = _list.Content;
                    sb.Append("列表内容 : ").Append(UiKit.Describe(content.gameObject)).Append('\n');
                    var n = Math.Min(content.childCount, 8);
                    for (var i = 0; i < n; i++)
                    {
                        var row = content.GetChild(i);
                        if (row == null)
                            continue;
                        sb.Append("  行").Append(i).Append(" : ").Append(UiKit.Describe(row.gameObject)).Append('\n');
                        for (var c = 0; c < row.childCount; c++)
                        {
                            var cell = row.GetChild(c);
                            if (cell == null)
                                continue;
                            var label = UiClone.FindText(cell.gameObject);
                            // 名字那一格的颜色就是风险档位，必须进报告（字面看不出黄/白/红）。
                            var color = "";
                            if (label != null)
                            {
                                try
                                {
                                    var cc = label.color;
                                    color = "rgb(" + Mathf.RoundToInt(cc.r * 255)
                                        + "," + Mathf.RoundToInt(cc.g * 255)
                                        + "," + Mathf.RoundToInt(cc.b * 255) + ")";
                                }
                                catch (Exception) { }
                            }
                            sb.Append("    [").Append(c).Append("] ").Append(UiKit.Describe(cell.gameObject))
                              .Append("  \"").Append(label == null ? "" : UiKit.Trim(label.text, 24)).Append('"')
                              .Append(' ').Append(color)
                              .Append('\n');
                        }
                    }
                }
            }
        }
    }
}
