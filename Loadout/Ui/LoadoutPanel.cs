using System;
using System.Collections.Generic;
using System.Text;
using DSHCore.Ui;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 一个面板页。
    ///
    /// <para>页面**只负责往内容区加行**（<see cref="LoadoutPanel.AddRow"/>），
    /// 一行是怎么摆的完全交给原版布局组 —— 页面里不应该出现坐标或尺寸。</para>
    /// </summary>
    internal interface IPanelPage
    {
        /// <summary>页签文字（保持短，页签是原版按钮）。</summary>
        string Name { get; }

        /// <summary>往内容区装配本页（可重复调用，实现里必须先假定内容区是空的）。</summary>
        void Build();

        /// <summary>每帧（页面自己做节流）。默认什么都不做。</summary>
        void Tick();

        /// <summary>
        /// 每帧调一次，**不节流**。
        ///
        /// <para>只放「必须在玩家按下的那一帧就处理掉」的东西 —— 最典型的是点选：
        /// <c>Input.GetMouseButtonDown(0)</c> 只在**一帧**为真，而 <see cref="Tick"/> 是
        /// 30 帧一次的慢速轮询，把点击放在那里就等于「要连点好几十下才中一次」
        /// （实测症状：蓝图页镜像框里的卡得点好几次才能选中）。</para>
        ///
        /// <para>默认什么都不做：只有自己管点击的页需要实现它。</para>
        /// </summary>
        void Frame() { }

        /// <summary>诊断文字（布局 dump 会打出来）。</summary>
        string Report();
    }

    /// <summary>
    /// 面板壳：**全部视觉都是原版 prefab 的克隆体**。
    ///
    /// <para>外壳就是官方的「标题 + 正文 + 若干按钮」对话框（<c>CustomDialog</c>），
    /// 我们只做三件事：</para>
    /// <list type="number">
    /// <item>把 <c>Root</c> 的宽度设成**画布宽度的 <see cref="WidthRatio"/>**
    /// （原版对话框宽度固定 400 单位，4 列网格放不下；框体美术是拉伸锚点，会自动跟着变宽）；</item>
    /// <item>在标题下面插一行**页签**（克隆原版 <c>ButtonRow</c>，当前页按钮置灰）；</item>
    /// <item>把页面的行插在页签之后、官方按钮区之前。</item>
    /// </list>
    ///
    /// <para><b>为什么不算坐标：</b>插入的每一行都落在官方对话框自己的
    /// <c>VerticalLayoutGroup</c> 里，位置/高度由原版算。整份代码里唯一的"尺寸"就是
    /// 对话框宽度那一个比例，它和 <see cref="UiList.SuggestHeight"/> 同源（按画布折算，
    /// 不写死像素）。</para>
    /// </summary>
    internal static class LoadoutPanel
    {
        public const string RootName = "DSH_LoadoutPanel";

        /// <summary>对话框正文/页签的标签字号。</summary>
        public const float LabelFontSize = 14f;

        /// <summary>面板宽度占画布宽度的比例。</summary>
        public const float WidthRatio = 0.94f;

        private static GameObject _root;
        private static CustomDialog _dialog;
        private static RectTransform _content;   // Root（带 VerticalLayoutGroup）
        private static RectTransform _buttons;   // Root/Buttons（官方按钮区）
        private static GameObject _titleArea;
        private static GameObject _tabsNode;
        private static ButtonRow _tabs;

        private static readonly List<GameObject> Rows = new List<GameObject>();
        private static readonly List<UiGridRow> EqualizeRows = new List<UiGridRow>();
        private static bool _equalizePending;
        private static IPanelPage[] _pages = new IPanelPage[0];
        private static bool _pagesInLevel;
        /// <summary>页集合上上次同步时的「传送带模式」（自选卡与传送带是两套页）。</summary>
        private static bool _pagesConveyor;
        private static int _page = -1;
        private static string _lastError;
        private static int _tick;

        // 委托必须静态持有：IL2CPP 侧只存裸函数指针，托管委托被回收就会崩。
        private static System.Action<int> _optionManaged;
        private static Il2CppSystem.Action<int> _optionIl2Cpp;
        private static System.Action<ButtonRow, int> _tabManaged;
        private static Il2CppSystem.Action<ButtonRow, int> _tabIl2Cpp;

        // 拖动（屏幕空间两点差分，见 TickDrag）
        private static bool _dragging;
        private static Vector2 _dragPointer;
        private static Vector2 _dragLocalStart;
        private static Vector2 _dragAnchoredStart;

        // ------------------------------------------------------------------
        // 对外状态
        // ------------------------------------------------------------------

        public static bool IsOpen
        {
            get
            {
                if (!UiFinder.IsAlive(_root))
                    return false;
                try { return _root.activeSelf; }
                catch (Exception) { return false; }
            }
        }

        public static string LastError { get { return _lastError; } }

        public static RectTransform Content { get { return _content; } }

        /// <summary>
        /// 对话框根节点（<c>DSH_LoadoutPanel</c>）—— 它**不是**布局组，所以适合放浮层：
        /// 子节点不会参与面板的纵向排版，不会把面板顶高（补全候选框就挂在这里）。
        /// </summary>
        public static RectTransform DialogRoot
        {
            get { return _root == null ? null : _root.transform.TryCast<RectTransform>(); }
        }

        /// <summary>登记一个需要「等分单元格」的行，布局结算后统一处理。</summary>
        public static void RegisterEqualizeRow(UiGridRow row)
        {
            if (row == null || EqualizeRows.Contains(row))
                return;
            EqualizeRows.Add(row);
            _equalizePending = true;
        }

        private static readonly List<BlueprintStrip> Strips = new List<BlueprintStrip>();

        /// <summary>
        /// 登记一条蓝图条：它要靠**所在行的实际宽度**才能把卡片摆正
        /// （<see cref="BlueprintStrip.FitWidth"/>）。刚建出来时宽度还是 0，
        /// 而且页面自己的 <see cref="IPanelPage.Tick"/> 是节流的 —— 不在这里补一次，
        /// 「建好 → 立刻读布局」这类动作（自测、切页）会读到一坨挤在左上角的卡片。
        /// </summary>
        public static void RegisterStrip(BlueprintStrip strip)
        {
            if (strip == null || Strips.Contains(strip))
                return;
            Strips.Add(strip);
            _equalizePending = true;
        }

        /// <summary>
        /// 让布局彻底结算：先重建一次（拿到各容器的真实宽度），再按真实宽度等分各行的单元格。
        ///
        /// <para>顺序不能反：等分要用父容器的实际宽度，而刚建出来的节点宽度还是 0。</para>
        /// </summary>
        public static void SettleLayout()
        {
            RebuildLayout();
            for (var i = Strips.Count - 1; i >= 0; i--)
            {
                var s = Strips[i];
                if (s == null || !s.IsValid)
                {
                    Strips.RemoveAt(i);
                    continue;
                }
                try { s.FitWidth(); }
                catch (Exception) { }
            }
            if (!_equalizePending)
                return;
            _equalizePending = false;
            for (var i = 0; i < EqualizeRows.Count; i++)
            {
                try { EqualizeRows[i].EqualizeNow(); }
                catch (Exception) { }
            }
            RebuildLayout();
        }

        public static int PageIndex { get { return _page; } }

        public static int PageCount { get { return _pages.Length; } }

        public static string PageName
        {
            get
            {
                if (_page < 0 || _page >= _pages.Length)
                    return "（未选页）";
                return _pages[_page].Name;
            }
        }

        /// <summary>当前页的自有状态（诊断用）。</summary>
        public static string PageReport()
        {
            try
            {
                if (_page < 0 || _page >= _pages.Length)
                    return string.Empty;
                return _pages[_page].Report() ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        // ------------------------------------------------------------------
        // 开关
        // ------------------------------------------------------------------

        public static bool Open()
        {
            if (!Build())
                return false;
            try
            {
                _root.SetActive(true);
                // 关卡内 / 关卡外 是两套页面：进面板时按当前场景选一套。
                SyncPageSet();
                PageTabState();
                ShowPage(_page < 0 ? 0 : _page);
                UiHost.MarkOwn(_root);
                return true;
            }
            catch (Exception e)
            {
                _lastError = "激活面板失败：" + e.GetType().Name + ": " + e.Message;
                Plugin.Log?.LogWarning("[Panel] " + _lastError);
                return false;
            }
        }

        public static void Close()
        {
            UiTooltip.Hide();
            UiCompleter.HideAll();
            UiPickWindow.HideAll();
            BlueprintPickWindow.HideAll();
            if (!UiFinder.IsAlive(_root))
                return;
            try { _root.SetActive(false); }
            catch (Exception) { }
            _dragging = false;
        }

        public static void Toggle()
        {
            if (IsOpen)
                Close();
            else
                Open();
        }

        /// <summary>丢掉现有面板并强制重新解析素材（切场景 / 排障用）。</summary>
        public static void Rebuild()
        {
            DestroyPanel();
            UiSkin.Prepare(true);
        }

        public static bool ShowPage(int index)
        {
            if (index < 0 || index >= _pages.Length)
                return false;
            if (!Build())
                return false;

            try
            {
                ClearRows();
                _page = index;
                var page = _pages[index];

                // 对话框契约天然带「标题 + 正文」，但本面板**不要**这两行
                // （内容本身已经把自己的含义说清楚了，多两行只会把框体撑高）。
                // 做法：文案留空 + 把两个节点关掉 —— 布局组会直接忽略未激活的子节点，
                // 所以既不多占高度，也不用去改对话框自己的逻辑。
                _dialog.SetDialog(string.Empty, string.Empty, new[] { "关闭" }, CloseAction());
                HideNode(TitleRow());
                HideNode(DescNode());

                page.Build();
                PageTabState();
                // ★ 关键：本页的行是刚才才挂进内容容器的，布局组这一帧还没算过 ——
                // 不主动逼它算一次，「行 -> 子节点」的尺寸会一直停在 0（实测蓝图镜像行整行
                // 都是 0x0，卡片全挤在左上角）。清行时的那次重算发生在 Build 之前，救不了。
                // SettleLayout 里顺手还会把各条蓝图条按真实宽度摆一次。
                SettleLayout();
                _invalidate = false;
                return true;
            }
            catch (Exception e)
            {
                _lastError = "装配第 " + (index + 1) + " 页失败：" + e.GetType().Name + ": " + e.Message;
                Plugin.Log?.LogWarning("[Panel] " + _lastError);
                return false;
            }
        }

        private static bool _invalidate;

        public static void Invalidate()
        {
            _invalidate = true;
        }

        // ------------------------------------------------------------------
        // 每帧
        // ------------------------------------------------------------------

        public static void Tick()
        {
            try
            {
                HandleHotkey();
                HandleLevelTransition();

                // 这两件与"面板开不开"无关：
                //  · HUD 压制要在面板关着的时候继续生效；
                //  · 选卡界面的「全部蓝图」按钮出现在关卡内，那时面板通常是关着的。
                LevelHud.Tick();
                BlueprintToggleButton.Tick();
                // 传送带行为也要与面板开没开无关：面板关着的时候它得继续抽卡。
                ConveyorRig.Tick();
                // 「禁用冷却」要自愈：buff 是关卡级的、会跟着 .lvl 存下来，
                // 所以「上次开着就退出关卡、再进来」时不会有任何按钮点击，只能靠这里发现。
                NoRecharge.Tick();
                // 星之碎片那条补丁必须**进关卡之后**才挂（提前反射 LogicLevelProps 会把游戏打坏，
                // 见 CheatRefill 的类注释）；这里每帧试一次，挂上就再也不做。
                CheatRefill.Tick();

                if (!IsOpen)
                    return;

                // 页集合可能在面板开着的时候变了（最典型：「点击传送带模式」）。
                // 以前只在 Open() 里同步，于是点完按钮页面不会切 —— 这里每帧保险一下，
                // 集合没变时它第一行就返回，开销可忽略。
                SyncPageSet();

                TickDrag();
                UiCompleter.TickAll();
                UiArchiveList.TickAll();
                UiArtifactGrid.TickAll();
                UiPickWindow.TickAll();
                BlueprintPickWindow.TickAll();

                // 等分/尺寸这类需要“真实宽度”的事，等布局结算完再补一次。
                if (_equalizePending)
                    SettleLayout();

                if (_invalidate)
                {
                    ShowPage(_page < 0 ? 0 : _page);
                    return;
                }

                // 点击这类「只在一帧为真」的东西走这条路（见 IPanelPage.Frame）。
                if (_page >= 0 && _page < _pages.Length)
                    _pages[_page].Frame();

                // 页面同步（慢速轮询）：游戏状态被别的途径改了（进关卡、读档、关卡脚本）
                // 之后标签会自己跟回来。
                _tick++;
                if (_tick % 30 == 0 && _page >= 0 && _page < _pages.Length)
                    _pages[_page].Tick();
            }
            catch (Exception e)
            {
                LogOnce("tick", "面板心跳异常：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool _wasInLevel;

        /// <summary>
        /// 进出关卡时**自动关面板**：关卡内/关卡外是两套页面，把关卡内的页面带到关卡外
        /// （或反过来）没有意义，而且页面里的对象引用会失效。
        /// </summary>
        private static void HandleLevelTransition()
        {
            try
            {
                var inLevel = LevelFacade.InLevel;
                if (LevelFacade.SwitchingLevel)
                {
                    // 换关就丢掉传送带行为（新关卡的传送带要重新判断有没有），
                    // 以及放置作用域的标签（实体 ID 在新关卡里没有意义）。
                    ConveyorRig.Reset();
                    PlacementScope.Reset();
                    if (IsOpen)
                    {
                        Plugin.Log?.LogInfo("[Panel] 正在切换关卡，自动关闭面板");
                        Close();
                    }
                    _wasInLevel = inLevel;
                    return;
                }
                if (inLevel != _wasInLevel)
                {
                    _wasInLevel = inLevel;
                    if (IsOpen)
                    {
                        Plugin.Log?.LogInfo("[Panel] 场景变化（" + (inLevel ? "进入关卡" : "离开关卡")
                            + "），自动关闭面板");
                        Close();
                    }
                }
            }
            catch (Exception e)
            {
                LogOnce("transit", "关卡切换检测失败：" + e.GetType().Name);
            }
        }

        private static void HandleHotkey()
        {
            var key = CompatConfig.ResolveHotkey();
            if (key == KeyCode.None)
                return;
            if (!Input.GetKeyDown(key))
                return;
            // 输入框正在编辑时不抢键（否则打字会直接开/关面板）。
            if (UiField.AnyFocused)
                return;
            Toggle();
        }

        // ------------------------------------------------------------------
        // 构建
        // ------------------------------------------------------------------

        private static bool Build()
        {
            if (UiFinder.IsAlive(_root) && UiFinder.IsAlive(_dialog) && _content != null && _buttons != null)
                return true;

            DestroyPanel();
            _lastError = null;

            if (!UiSkin.Prepare())
            {
                _lastError = "UI 素材还没就绪（游戏 UI 未起来，或素材解析失败）。";
                return false;
            }

            var parent = UiHost.Resolve(UiLayer.DialogLayer);
            if (parent == null)
            {
                _lastError = "找不到可用的 UI 宿主。";
                return false;
            }

            _root = UiSkin.Clone(UiSkinPart.Dialog, parent, RootName);
            if (!UiFinder.IsAlive(_root))
            {
                _lastError = "对话框壳克隆失败。";
                return false;
            }

            _dialog = _root.GetComponent<CustomDialog>();
            if (_dialog == null)
            {
                _lastError = "克隆体上没有 CustomDialog 组件。";
                DestroyPanel();
                return false;
            }

            try
            {
                var buttons = _dialog.buttonRowList;
                if (buttons == null || buttons._listRoot == null)
                {
                    _lastError = "拿不到对话框的按钮容器（buttonRowList / _listRoot）。";
                    DestroyPanel();
                    return false;
                }
                _buttons = buttons._listRoot;
                _content = _buttons.parent == null ? null : _buttons.parent.TryCast<RectTransform>();
            }
            catch (Exception e)
            {
                _lastError = "读对话框布局节点失败：" + e.GetType().Name + ": " + e.Message;
                DestroyPanel();
                return false;
            }

            if (_content == null)
            {
                _lastError = "对话框布局根为空。";
                DestroyPanel();
                return false;
            }

            // 面板宽度：原版对话框宽度固定 400 单位（画布约 800 单位宽），4 列网格放不下。
            // 这里把它设成画布宽度的一个比例 —— 高度仍然由 ContentSizeFitter 撑，
            // 框体美术（拉伸锚点）与标题/正文/按钮都会自动跟随。
            ApplyWidth();

            // 页签行（克隆原版按钮行）插在标题之后。
            BuildTabs();

            // 对话框壳自带一个全屏的点击拦截（Blocker）—— 它会让面板外面的游戏 UI 也点不动。
            // 需求是“面板之外的输入不受影响”，所以把它关掉（关节点而不是删，避免别处还引用着它）。
            DisableBlocker();

            // 标题行（拖动把手）：按名字找，找不到也不致命（只影响能不能拖）。
            try
            {
                var t = UiFinder.FindByPath(_content, "TitleArea");
                _titleArea = t == null ? null : t.gameObject;
            }
            catch (Exception) { }

            try { _root.SetActive(false); }
            catch (Exception) { }

            Plugin.Log?.LogInfo("[Panel] 面板已构建：" + UiFinder.PathOf(_root.transform)
                + "  宽度=" + Mathf.RoundToInt(_content.sizeDelta.x)
                + "  页签=" + (UiFinder.IsAlive(_tabsNode) ? "OK" : "无"));
            return true;
        }

        /// <summary>
        /// 关掉对话框壳自带的全屏点击拦截。
        ///
        /// <para>原版对话框用它来吃掉“点外面”的点击（模态对话框的常规做法），但我们这个面板
        /// 明确**不是模态的**（需求：面板开着也要能点游戏），所以它必须关。
        /// 关节点而不是销毁：对话框自己的代码可能还引用着它。</para>
        /// </summary>
        private static void DisableBlocker()
        {
            try
            {
                if (!UiFinder.IsAlive(_root))
                    return;
                var nodes = _root.GetComponentsInChildren<Transform>(true);
                var found = 0;
                for (var i = 0; i < nodes.Length; i++)
                {
                    var t = nodes[i];
                    if (t == null)
                        continue;
                    // 只认名字：原版叫 Blocker；顺带把名字里带 blocker 的都收拾了。
                    var name = t.gameObject.name;
                    if (name == null || name.IndexOf("blocker", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    // 只处理“几乎盖满屏幕”的那种节点，避免误伤别的东西。
                    var rt = t.TryCast<RectTransform>();
                    if (rt == null || rt.rect.height < Screen.height * 0.5f)
                        continue;
                    if (t.gameObject.activeSelf)
                        t.gameObject.SetActive(false);
                    found++;
                }
                Plugin.Log?.LogInfo("[Panel] 全屏点击拦截（Blocker）：已处理 " + found + " 个");
            }
            catch (Exception e)
            {
                LogOnce("blocker", "关闭全屏点击拦截失败：" + e.GetType().Name);
            }
        }

        private static void ApplyWidth()
        {
            try
            {
                var width = CanvasWidth() * WidthRatio;
                _content.sizeDelta = new Vector2(width, _content.sizeDelta.y);
            }
            catch (Exception e)
            {
                LogOnce("width", "设置面板宽度失败：" + e.GetType().Name);
            }
        }

        /// <summary>主画布的宽度（单位）。拿不到就退回屏幕宽度 —— 与 UiList.SuggestHeight 同思路：
        /// 尺寸按画布折算，不写死像素。</summary>
        public static float CanvasWidth()
        {
            try
            {
                var canvas = UiGame.MainCanvas;
                if (canvas != null)
                {
                    var rt = canvas.GetComponent<RectTransform>();
                    if (rt != null && rt.rect.width > 1f)
                        return rt.rect.width;
                }
            }
            catch (Exception) { }
            return Mathf.Max(320f, Screen.width);
        }

        /// <summary>主画布的高度（单位）。拿不到就退回屏幕高度（不写死像素）。</summary>
        public static float CanvasHeight()
        {
            try
            {
                var canvas = UiGame.MainCanvas;
                if (canvas != null)
                {
                    var rt = canvas.GetComponent<RectTransform>();
                    if (rt != null && rt.rect.height > 1f)
                        return rt.rect.height;
                }
            }
            catch (Exception) { }
            return Mathf.Max(240f, Screen.height);
        }

        private static void BuildTabs()
        {
            _tabsNode = UiSkin.Clone(UiSkinPart.ButtonRow, _content, "DSH_Tabs");
            if (!UiFinder.IsAlive(_tabsNode))
            {
                LogOnce("tabs", "页签行克隆失败（ButtonRow 素材不可用），页面只能靠切换内容查看。");
                return;
            }
            try
            {
                // 插在标题之后（原版顺序是 标题 / 正文 / 按钮，这里变成 标题 / 页签 / 正文 / 内容 / 按钮）
                var titleArea = UiFinder.FindByPath(_content, "TitleArea");
                var index = titleArea == null ? 0 : titleArea.GetSiblingIndex() + 1;
                _tabsNode.transform.SetSiblingIndex(index);

                _tabs = _tabsNode.GetComponent<ButtonRow>();
                if (_tabs == null)
                {
                    LogOnce("tabs2", "页签行上没有 ButtonRow 组件。");
                    return;
                }
                _tabs.add_OnButtonClick(TabAction());
            }
            catch (Exception e)
            {
                LogOnce("tabs3", "装配页签行失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void DestroyPanel()
        {
            ClearRows();
            UiTooltip.Hide();
            UiCompleter.Forget();
            UiField.Forget();
            EqualizeRows.Clear();
            _equalizePending = false;
            _dialog = null;
            _content = null;
            _buttons = null;
            _titleArea = null;
            _tabsNode = null;
            _tabs = null;
            _page = -1;
            _pages = new IPanelPage[0];
            _pagesInLevel = false;
            _pagesConveyor = false;

            if (UiFinder.IsAlive(_root))
            {
                try { UnityEngine.Object.Destroy(_root); }
                catch (Exception) { }
            }
            _root = null;
        }

        // ------------------------------------------------------------------
        // 内容行
        // ------------------------------------------------------------------

        /// <summary>
        /// 把一行塞进对话框自己的 <c>VerticalLayoutGroup</c>，位置在官方按钮区**之前**。
        ///
        /// <para>注意这里不建任何容器节点：新建的容器没有 LayoutGroup，子节点会全叠在
        /// 同一个位置。直接用官方那个排好版的根节点，代价只是维护一份"本次插了哪些行"的清单。</para>
        /// </summary>
        public static void AddRow(GameObject row)
        {
            if (!UiFinder.IsAlive(row) || _content == null)
                return;
            try
            {
                row.transform.SetParent(_content, false);
                if (_buttons != null)
                    row.transform.SetSiblingIndex(_buttons.GetSiblingIndex());
                Rows.Add(row);
            }
            catch (Exception e)
            {
                LogOnce("addrow", "插入内容行失败：" + e.GetType().Name);
            }
        }

        /// <summary>
        /// 把节点插到 <paramref name="anchor"/> 的**正后面**（补齐弹窗用）。
        ///
        /// <para>父节点取**锚点的父节点**（不是内容根）：这样插在某个网格行后面就会落在对话框的内容区里，
        /// 插在某一列的字段后面就会落在那列里 —— 两种情况都不需要算坐标。</para>
        /// </summary>
        public static bool InsertAfter(GameObject anchor, GameObject node)
        {
            if (!UiFinder.IsAlive(anchor) || !UiFinder.IsAlive(node))
                return false;
            try
            {
                var parent = anchor.transform.parent;
                if (parent == null)
                    return false;
                node.transform.SetParent(parent, false);
                node.transform.SetSiblingIndex(anchor.transform.GetSiblingIndex() + 1);
                if (!Rows.Contains(node))
                    Rows.Add(node);
                RebuildLayout();
                return true;
            }
            catch (Exception e)
            {
                LogOnce("insert", "插入节点失败：" + e.GetType().Name);
                return false;
            }
        }

        public static void RemoveRow(GameObject row)
        {
            if (row == null)
                return;
            Rows.Remove(row);
            if (!UiFinder.IsAlive(row))
                return;
            try { UnityEngine.Object.Destroy(row); }
            catch (Exception) { }
        }

        public static void ClearRows()
        {
            UiTooltip.Hide();
            // 弹窗节点也在 Rows 里，会被一起销毁 → 先把引用丢掉。
            UiCompleter.Forget();
            UiArchiveList.Forget();
            UiField.Forget();
            for (var i = 0; i < Rows.Count; i++)
            {
                if (!UiFinder.IsAlive(Rows[i]))
                    continue;
                try
                {
                    // 用 DestroyImmediate：Destroy 要到帧末才真的移除，而
                    // "清空 → 立刻重建 → 立刻读布局" 是自测里的常规动作，
                    // 延迟销毁会让旧行还挂在布局组里（实测 self-test 里数到了 15 个子节点）。
                    UnityEngine.Object.DestroyImmediate(Rows[i]);
                }
                catch (Exception) { }
            }
            Rows.Clear();
            UiClone.PruneKeepAlive();
            RebuildLayout();
        }

        public static void RebuildLayout()
        {
            try
            {
                if (_content != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_content);
            }
            catch (Exception) { }
        }
        /// <summary>给内容行挂**原版**悬停提示（<c>UiTooltip</c> 会把它锚在目标上方）。</summary>
        public static void AddTooltip(GameObject node, string title, string text)
        {
            if (!UiFinder.IsAlive(node) || string.IsNullOrEmpty(text))
                return;
            try
            {
                UiTooltip.Attach(node, title ?? string.Empty, text);
            }
            catch (Exception e)
            {
                LogOnce("tip", "挂悬停提示失败：" + e.GetType().Name);
            }
        }

        /// <summary>
        /// 只改已有提示的文案（挂过 <see cref="AddTooltip"/> 的节点用）。
        /// 场景：同一项在不同状态下要换说明（例如关卡冷却速度异常时把「冷却(秒)」置灰并换文案）。
        /// </summary>
        public static void SetTooltip(GameObject node, string title, string text)
        {
            if (!UiFinder.IsAlive(node) || string.IsNullOrEmpty(text))
                return;
            try
            {
                if (!UiTooltip.SetText(node, title ?? string.Empty, text))
                    UiTooltip.Attach(node, title ?? string.Empty, text);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 页签 / 页集合
        // ------------------------------------------------------------------
        /// <summary>
        /// 按当前场景（关卡内 / 关卡外 + 是不是传送带）挑一套页面。
        /// 集合没变时什么都不做（所以每帧调也没关系）；变了会**按页名保持当前页**
        /// 并置上 <c>_invalidate</c>，下一帧 Tick 就把这一页重建出来。
        /// </summary>
        public static void SyncPageSet()
        {
            var inLevel = LevelFacade.InLevel;
            var conveyor = inLevel && BlueprintOps.IsConveyorMode(LevelFacade.Current);
            if (_pages.Length > 0 && _pagesInLevel == inLevel && _pagesConveyor == conveyor)
                return;

            // 换集合时**按页名保持当前页**：两套页面的页名是一样的（基础 / 蓝图 / 制品），
            // 所以「点传送带模式 → 立刻停在传送带那套的蓝图页」不需要额外逻辑。
            var wantName = _page >= 0 && _page < _pages.Length ? _pages[_page].Name : null;

            _pagesInLevel = inLevel;
            _pagesConveyor = conveyor;
            _pages = PanelPages.For(inLevel, conveyor);

            var target = 0;
            if (!string.IsNullOrEmpty(wantName))
            {
                for (var i = 0; i < _pages.Length; i++)
                {
                    if (_pages[i].Name == wantName)
                    {
                        target = i;
                        break;
                    }
                }
            }
            _page = target;
            // 已经开着的话 → 下一帧 Tick 里的 _invalidate 分支会重建这一页。
            _invalidate = true;

            if (!UiFinder.IsAlive(_tabsNode) || _tabs == null)
                return;
            try
            {
                var labels = new string[_pages.Length];
                for (var i = 0; i < _pages.Length; i++)
                    labels[i] = _pages[i].Name;
                _tabs.UpdateButtons(ToIl2Cpp(labels));
                Plugin.Log?.LogInfo("[Panel] 页集合 = " + (inLevel ? "关卡内" : "关卡外")
                    + "：" + string.Join(" / ", labels));
            }
            catch (Exception e)
            {
                LogOnce("tabs4", "刷新页签失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>当前页的页签按钮置灰（= 原版的"不可用"观感，用作选中态）。</summary>
        private static void PageTabState()
        {
            if (_tabs == null || _page < 0)
                return;
            try
            {
                var list = _tabs.buttonList;
                if (list == null)
                    return;
                for (var i = 0; i < list.Count; i++)
                {
                    // 注意用**非泛型** getElement：il2cpp 泛型方法在 interop 里要走
                    // MethodInfoStoreGeneric，能避就避。
                    var item = list.getElement(i);
                    var button = item == null ? null : item.GetComponent<TextButton>();
                    if (button == null || button.Button == null)
                        continue;
                    button.Button.interactable = i != _page;
                }
            }
            catch (Exception e)
            {
                LogOnce("tabstate", "刷新页签选中态失败：" + e.GetType().Name);
            }
        }

        public static void NextPage()
        {
            if (_pages.Length == 0)
                return;
            ShowPage((_page + 1) % _pages.Length);
        }

        public static void PrevPage()
        {
            if (_pages.Length == 0)
                return;
            ShowPage((_page - 1 + _pages.Length) % _pages.Length);
        }

        /// <summary>按页名或序号切页（诊断指令用）。</summary>
        public static bool ShowPageByName(string nameOrIndex)
        {
            if (_pages.Length == 0)
                return false;
            if (string.IsNullOrWhiteSpace(nameOrIndex))
                return false;
            int index;
            if (int.TryParse(nameOrIndex, out index))
                return ShowPage(index - 1 >= 0 ? index - 1 : 0);
            for (var i = 0; i < _pages.Length; i++)
            {
                if (string.Equals(_pages[i].Name, nameOrIndex, StringComparison.OrdinalIgnoreCase))
                    return ShowPage(i);
            }
            return false;
        }

        // ------------------------------------------------------------------
        // 拖动
        // ------------------------------------------------------------------

        /// <summary>
        /// 拖动面板：整段换算都在**屏幕空间**做。
        ///
        /// <para>为什么不能直接在 <c>anchoredPosition</c> 上加屏幕像素：它是**父节点空间**的单位，
        /// 而祖先链上还叠着画布的 <c>scaleFactor</c>（1.33 / 1.72）。做法是按下时记录鼠标屏幕点与
        /// 面板枢轴的父节点局部点，之后用「两次 <c>ScreenPointToLocalPointInRectangle</c> 的差」
        /// 把屏幕位移换算成局部位移 —— 一个缩放系数都不用手算。</para>
        /// </summary>
        private static void TickDrag()
        {
            if (_content == null)
                return;
            try
            {
                var pointer = (Vector2)Input.mousePosition;
                if (Input.GetMouseButtonDown(0))
                {
                    if (CanStartDrag(pointer))
                    {
                        _dragging = true;
                        _dragPointer = pointer;
                        Vector2 local;
                        if (ScreenToLayoutLocal(pointer, out local))
                        {
                            _dragLocalStart = local;
                            _dragAnchoredStart = _content.anchoredPosition;
                        }
                        else
                        {
                            _dragging = false;
                        }
                    }
                }
                else if (Input.GetMouseButtonUp(0))
                {
                    _dragging = false;
                }
                else if (_dragging)
                {
                    Vector2 local;
                    if (ScreenToLayoutLocal(pointer, out local))
                    {
                        var target = _dragAnchoredStart + (local - _dragLocalStart);
                        _content.anchoredPosition = ClampAnchored(target);
                    }
                }
            }
            catch (Exception e)
            {
                _dragging = false;
                LogOnce("drag", "拖动失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 能不能开始拖动：**面板内、且不在任何可交互控件上**。
        ///
        /// <para>需求要的是「所有可交互控件之外的位置均可拖动」—— 所以不能再像早期那样只认
        /// 标题行/页签行，而是反过来判定：指针在面板身上，且不落在任何一个 <c>Selectable</c>
        /// （按钮 / 开关 / 输入框）或 <c>ScrollRect</c>（列表的滚动区域）上。
        /// 输入框必须排除，否则想在输入框里拖选文字就会把面板拖跑。</para>
        /// </summary>
        private static bool CanStartDrag(Vector2 screenPoint)
        {
            if (!UiFinder.IsAlive(_root))
                return false;
            // 必须在面板身上（否则点游戏世界也会拖面板）。
            if (!Contains(_content == null ? null : _content.gameObject, screenPoint))
                return false;
            return !PointerOverInteractive(_root.transform, screenPoint);
        }

        /// <summary>指针是否落在己方子树里的可交互控件上。</summary>
        private static bool PointerOverInteractive(Transform root, Vector2 screenPoint)
        {
            if (root == null)
                return false;
            try
            {
                var selectables = root.GetComponentsInChildren<Selectable>(false);
                if (selectables != null)
                {
                    for (var i = 0; i < selectables.Length; i++)
                    {
                        if (Contains(selectables[i], screenPoint))
                            return true;
                    }
                }
                var scrolls = root.GetComponentsInChildren<ScrollRect>(false);
                if (scrolls != null)
                {
                    for (var i = 0; i < scrolls.Length; i++)
                    {
                        if (Contains(scrolls[i], screenPoint))
                            return true;
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        private static bool Contains(Component node, Vector2 screenPoint)
        {
            if (node == null || !UiFinder.IsAlive(node))
                return false;
            return Contains(node.gameObject, screenPoint);
        }

        private static bool Contains(GameObject node, Vector2 screenPoint)
        {
            if (!UiFinder.IsAlive(node))
                return false;
            var rt = node.transform.TryCast<RectTransform>();
            if (rt == null)
                return false;
            try
            {
                // 未激活的节点（比如被我们关掉的标题行）不应该还能当把手/控件。
                if (!rt.gameObject.activeInHierarchy)
                    return false;
                return RectTransformUtility.RectangleContainsScreenPoint(rt, screenPoint, CanvasCamera());
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 实测命中诊断：给定点上，UI 事件系统认为最上层的几个对象是谁。
        ///
        /// <para>存在的理由：「面板阻挡了面板以外的输入」这种毛病只能靠它定住 ——
        /// 光看自己面板的层级是看不出别人的东西盖在上面的。</para>
        /// </summary>
        public static string RaycastAt(Vector2 screenPoint, int max)
        {
            var sb = new StringBuilder(400);
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es == null)
                {
                    sb.Append("（没有 EventSystem）\n");
                    return sb.ToString();
                }
                var data = new UnityEngine.EventSystems.PointerEventData(es);
                data.position = screenPoint;
                var results = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                es.RaycastAll(data, results);
                var n = results.Count;
                sb.Append("点(").Append(Mathf.RoundToInt(screenPoint.x)).Append(',')
                  .Append(Mathf.RoundToInt(screenPoint.y)).Append(") 命中 ").Append(n).Append(" 个：");
                var shown = 0;
                for (var i = 0; i < n && shown < max; i++)
                {
                    var go = results[i].gameObject;
                    if (go == null)
                        continue;
                    if (shown > 0)
                        sb.Append(" / ");
                    sb.Append(UiFinder.PathOf(go.transform));
                    shown++;
                }
                if (shown == 0)
                    sb.Append("（无 —— 这一点上的输入是通的）");
                sb.Append('\n');
            }
            catch (Exception e)
            {
                sb.Append("命中诊断失败：").Append(e.GetType().Name).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>当前鼠标位置下的命中诊断。</summary>
        public static string RaycastUnderPointer(int max)
        {
            return RaycastAt((Vector2)Input.mousePosition, max);
        }

        private static Vector2 ClampAnchored(Vector2 anchored)
        {
            // 位置限制在**屏幕空间**判断：先把候选位置的屏幕位移换算成局部位移，再折算允许范围。
            var size = _content.rect.size;
            var parent = _content.parent == null ? null : _content.parent.TryCast<RectTransform>();
            if (parent == null || size.x <= 0f || size.y <= 0f)
                return anchored;

            var scale = LocalScaleToScreen();
            if (scale <= 0f)
                return anchored;

            // 面板中心允许落在屏幕内的范围（屏幕像素）→ 局部单位
            var margin = 10f;                                  // 横向至少留 10px 可见
            var halfW = size.x * 0.5f;
            var halfH = size.y * 0.5f;

            var minX = margin - halfW * scale;
            var maxX = Screen.width - margin + halfW * scale;
            var minY = halfH * scale;
            var maxY = Screen.height - halfH * scale;

            var parentRect = parent;
            var screenOfZero = RectTransformUtility.WorldToScreenPoint(CanvasCamera(),
                parentRect.TransformPoint(parentRect.rect.center));

            // 当前 anchored 对应的中心屏幕点
            var centerScreen = screenOfZero + anchored * scale;

            float cx = centerScreen.x, cy = centerScreen.y;
            if (minX > maxX) cx = Screen.width * 0.5f;
            else cx = Mathf.Clamp(cx, minX, maxX);
            if (minY > maxY) cy = Screen.height * 0.5f;
            else cy = Mathf.Clamp(cy, minY, maxY);

            return new Vector2((cx - screenOfZero.x) / scale, (cy - screenOfZero.y) / scale);
        }

        /// <summary>面板所在节点上「1 局部单位 = 多少屏幕像素」。</summary>
        private static float LocalScaleToScreen()
        {
            var parent = _content.parent == null ? null : _content.parent.TryCast<RectTransform>();
            if (parent == null)
                return 0f;
            var cam = CanvasCamera();
            var p0 = RectTransformUtility.WorldToScreenPoint(cam, parent.TransformPoint(Vector3.zero));
            var p1 = RectTransformUtility.WorldToScreenPoint(cam, parent.TransformPoint(new Vector3(100f, 0f, 0f)));
            return Mathf.Abs(p1.x - p0.x) / 100f;
        }

        private static bool ScreenToLayoutLocal(Vector2 screenPoint, out Vector2 local)
        {
            local = Vector2.zero;
            var parent = _content.parent == null ? null : _content.parent.TryCast<RectTransform>();
            if (parent == null)
                return false;
            Vector2 value;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, screenPoint, CanvasCamera(), out value))
                return false;
            local = value;
            return true;
        }

        /// <summary>某个节点上「1 局部单位 = 多少屏幕像素」（拖动与浮层摆放共用）。</summary>
        public static float LocalScaleToScreenOf(RectTransform node)
        {
            if (node == null)
                return 0f;
            try
            {
                var cam = CanvasCamera();
                var p0 = RectTransformUtility.WorldToScreenPoint(cam, node.TransformPoint(Vector3.zero));
                var p1 = RectTransformUtility.WorldToScreenPoint(cam, node.TransformPoint(new Vector3(100f, 0f, 0f)));
                return Mathf.Abs(p1.x - p0.x) / 100f;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        public static Camera CanvasCamera()
        {
            try
            {
                var scene = UiGame.Scene;
                return scene == null ? null : scene.uiCamera;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // 诊断
        // ------------------------------------------------------------------

        public static string Report()
        {
            var sb = new StringBuilder(700);
            sb.Append("面板：").Append(UiFinder.IsAlive(_root)
                ? UiFinder.PathOf(_root.transform) + "（" + (IsOpen ? "已显示" : "已隐藏") + "）"
                : "未构建").Append('\n');
            sb.Append("对话框：").Append(UiFinder.IsAlive(_dialog) ? "OK" : "无").Append('\n');
            sb.Append("内容宿主：").Append(_content == null ? "无" : UiFinder.PathOf(_content)).Append('\n');
            sb.Append("按钮区：").Append(_buttons == null ? "无" : UiFinder.PathOf(_buttons)).Append('\n');
            sb.Append("页签：").Append(UiFinder.IsAlive(_tabsNode) ? UiFinder.PathOf(_tabsNode.transform) : "无").Append('\n');
            sb.Append("页集合：").Append(_pagesInLevel ? "关卡内" : "关卡外")
              .Append("  共 ").Append(_pages.Length).Append(" 页  当前=").Append(PageName)
              .Append("  内容行=").Append(Rows.Count).Append('\n');
            sb.Append("画布宽=").Append(Mathf.RoundToInt(CanvasWidth()))
              .Append("  面板宽=").Append(_content == null ? 0 : Mathf.RoundToInt(_content.sizeDelta.x))
              .Append("  保活委托=").Append(UiClone.KeepAliveCount).Append('\n');
            if (!string.IsNullOrEmpty(_lastError))
                sb.Append("最近错误：").Append(_lastError).Append('\n');
            sb.Append('\n').Append(UiSkin.Report);
            return sb.ToString();
        }

        /// <summary>标题所在的那一行（<c>TitleArea</c>）—— 关的是它，不是标题文字本身。</summary>
        private static GameObject TitleRow()
        {
            try
            {
                if (_titleArea != null && UiFinder.IsAlive(_titleArea))
                    return _titleArea;
                var t = _content == null ? null : UiFinder.FindByPath(_content, "TitleArea");
                return t == null ? null : t.gameObject;
            }
            catch (Exception) { return null; }
        }

        private static void HideNode(GameObject node)
        {
            if (!UiFinder.IsAlive(node))
                return;
            try
            {
                if (node.activeSelf)
                    node.SetActive(false);
            }
            catch (Exception) { }
        }

        private static GameObject DescNode()
        {
            try
            {
                var t = _dialog == null ? null : _dialog.desc;
                return UiFinder.IsAlive(t) ? t.gameObject : null;
            }
            catch (Exception) { return null; }
        }

        private static void LogOnce(string key, string message)
        {
            Plugin.LogOnce(key, "[Panel] " + message);
        }

        /// <summary>
        /// string[] → il2cpp 字符串数组。
        ///
        /// <para>为什么不用隐式转换：interop 给 <c>Il2CppReferenceArray&lt;string&gt;</c> 定义的
        /// 转换运算符不会自动降到派生类型 <c>Il2CppStringArray</c>，直接传 <c>string[]</c> 会编译不过。
        /// DSHCore 内部也是这么造的（<c>UiList.ToIl2Cpp</c>）。</para>
        /// </summary>
        internal static Il2CppStringArray ToIl2Cpp(string[] items)
        {
            var n = items == null ? 0 : items.Length;
            var arr = new Il2CppStringArray(n);
            for (var i = 0; i < n; i++)
                arr[i] = items[i];
            return arr;
        }

        // ------------------------------------------------------------------
        // 委托
        // ------------------------------------------------------------------

        private static Il2CppSystem.Action<int> CloseAction()
        {
            if (_optionIl2Cpp != null)
                return _optionIl2Cpp;
            _optionManaged = new System.Action<int>(OnOptionSelected);
            _optionIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>(_optionManaged);
            return _optionIl2Cpp;
        }

        private static void OnOptionSelected(int index)
        {
            // 底部只有一个「关闭」按钮。
            Close();
        }

        private static Il2CppSystem.Action<ButtonRow, int> TabAction()
        {
            if (_tabIl2Cpp != null)
                return _tabIl2Cpp;
            _tabManaged = new System.Action<ButtonRow, int>(OnTabClicked);
            _tabIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ButtonRow, int>>(_tabManaged);
            return _tabIl2Cpp;
        }

        private static void OnTabClicked(ButtonRow row, int index)
        {
            try
            {
                ShowPage(index);
            }
            catch (Exception e)
            {
                LogOnce("tabclick", "切页失败：" + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
