using System;
using System.Collections.Generic;
using DSHCore.Ui;
using Il2CppInterop.Runtime;
using MVZ2.DebugConsole;
using PVZEngine;
using PVZEngine.Tools.Unity;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 「带补齐的输入框」：**输入框 + 原版控制台那个候选弹窗**。
    ///
    /// <para>存在的理由：原版的 <c>TMP_Dropdown</c> 展开列表在本游戏里一个像素都画不出来
    /// （DSHCore 实测过并声明不提供替代实现），所以「从一组里选一个」的格子必须换做法。
    /// 面板要的是"能输、能补、能认 id 和名字"，而这正是原版控制台干的事。</para>
    ///
    /// <para><b>弹窗素材是原版的控制台补齐面板</b>
    /// （<c>DebugConsole.prefab/Bottom/AutoComplete</c>：框体 + <c>ElementList</c> 候选项
    /// （图标 + 文字 + 选中态）+ 上/下翻页箭头）。它是 <c>ElementList</c>，不是 TMP 下拉框，
    /// 所以没有"画不出来"的问题。</para>
    ///
    /// <para><b>弹窗怎么摆：</b>把它插进对话框自己的纵向布局里、**紧跟在输入框所在那一行的后面**。
    /// 也就是说候选列表是面板内容区的一部分（开着时把它下面的行往下推），而不是浮在上面 ——
    /// 这样不需要任何坐标计算，也不会踩到"浮层被画布裁掉/盖住"那一类坑。</para>
    ///
    /// <para><b>行为（与用户确认过的方案）：</b>聚焦即列出全部候选；id / 当前语言名 / 英文名
    /// 三路匹配，前缀优先、包含兜底、大小写不敏感；↑↓ 选择、Tab/Enter 补全、Esc 还原、
    /// 鼠标点候选也能补全；提交时解析不出来就**回退原值**。</para>
    /// </summary>
    internal sealed class UiCompleter
    {
        /// <summary>一页最多显示多少候选（**固定 6 行**，需求指定）。</summary>
        private const int DefaultWindow = 6;

        /// <summary>浮层到输入框的间距（屏幕像素）。</summary>
        private const float GapPixels = 2f;

        private const int MaxCandidates = 400;

        private static readonly List<UiCompleter> All = new List<UiCompleter>();

        private readonly UiField _field;
        private readonly NameKind _kind;
        private readonly GameObject _anchorRow;
        private NameIndex _index;

        private GameObject _popup;
        private GameObject _box;
        private ElementList _items;
        private Button _up;
        private Button _down;
        private bool _built;
        private bool _open;
        private float _rowHeight;
        private TextMeshProUGUI _rowText;
        private float _rowIconWidth;
        /// <summary>本帧的鼠标按下是否落在浮层里（诊断用）。</summary>
        private bool _clickInPopup;

        /// <summary>上一次打印的浮层尺寸/位置签名（只在变化时打日志）。</summary>
        private string _lastLogSig;

        private readonly List<NameIndex.Entry> _candidates = new List<NameIndex.Entry>();
        private readonly List<NameIndex.Entry> _shown = new List<NameIndex.Entry>();
        private int _selected;
        private int _viewStart;
        private int _window = DefaultWindow;
        private string _snapshot = string.Empty;

        // 委托保活（IL2CPP 侧只存裸函数指针）
        private System.Action<int, GameObject> _onItemManaged;
        private System.Action<GameObject> _onItemCreateManaged;
        private System.Action<GameObject> _onItemDestroyManaged;
        private System.Action _upManaged, _downManaged;

        private UiCompleter(UiField field, NameKind kind, GameObject anchorRow)
        {
            _field = field;
            _kind = kind;
            _anchorRow = anchorRow;
        }

        /// <summary>
        /// 索引**惰性解析**，而不是建页时固定一个实例。
        ///
        /// <para>理由：解锁条件来自资源侧，在标题界面建页时可能还没加载完（拿到 0 项的索引）；
        /// 若在这里把那个空索引存住，之后哪怕缓存里已经建好 155 项，这个格子也永远只有 0 个候选。</para>
        /// </summary>
        private NameIndex Index
        {
            get
            {
                if (_index == null || _index.Count == 0)
                    _index = NameIndexes.Get(_kind);
                return _index;
            }
        }

        public bool IsOpen { get { return _open; } }

        public string KindName { get { return _kind.ToString(); } }

        // ------------------------------------------------------------------
        // 装配 / 清理
        // ------------------------------------------------------------------

        /// <summary>
        /// 给一个字段挂上补齐。<paramref name="anchorRow"/> 是**该字段所在的一整行**
        /// （弹窗会插在它后面，所以不能传字段自己）。
        /// </summary>
        public static UiCompleter Attach(UiField field, NameKind kind, GameObject anchorRow)
        {
            if (field == null || !field.IsValid)
                return null;

            var completer = new UiCompleter(field, kind, anchorRow);
            field.OnChanged(text => completer.OnTextChanged(text));
            field.OnSubmit(text => completer.OnCommitted(text));
            All.Add(completer);
            return completer;
        }

        public static void HideAll()
        {
            for (var i = 0; i < All.Count; i++)
            {
                try { All[i].Hide(); }
                catch (Exception) { }
            }
        }

        /// <summary>面板重建时放弃全部引用（弹窗节点会随内容行一起被销毁）。</summary>
        public static void Forget()
        {
            for (var i = 0; i < All.Count; i++)
            {
                All[i]._popup = null;
                All[i]._box = null;
                All[i]._items = null;
                All[i]._up = null;
                All[i]._down = null;
                All[i]._built = false;
                All[i]._open = false;
                All[i]._clickInPopup = false;
            }
            All.Clear();
        }

        public static void TickAll()
        {
            for (var i = 0; i < All.Count; i++)
            {
                try { All[i].Tick(); }
                catch (Exception) { }
            }
        }

        /// <summary>诊断用：第一个补齐字段（页面重建后可能为 null）。</summary>
        public static UiCompleter DebugFirst
        {
            get { return All.Count == 0 ? null : All[0]; }
        }

        /// <summary>诊断用：该字段的候选弹窗节点（取外框，那才是参与布局的那个）。</summary>
        public static GameObject DebugPopupOf(UiCompleter c)
        {
            if (c == null)
                return null;
            if (UiFinder.IsAlive(c._box))
                return c._box;
            return UiFinder.IsAlive(c._popup) ? c._popup : null;
        }

        /// <summary>
        /// 诊断/自测用：把第一个可用的补齐字段聚焦并把候选列表展开。
        ///
        /// <para>存在的理由：「弹窗画出来了没有」只能靠截图证明，而截图需要一个"弹窗开着"的时刻 ——
        /// 这不能靠人手去点，得程序化地把它开起来。</para>
        /// </summary>
        public static string DebugOpenFirst()
        {
            for (var i = 0; i < All.Count; i++)
            {
                var c = All[i];
                try
                {
                    if (c._field == null || !c._field.IsValid)
                        continue;
                    c._field.Focus();
                    c.Refresh(true, true);
                    return c.KindName + " 已聚焦并展开候选（显示 " + c._shown.Count
                        + "/" + c._candidates.Count + " 项，每页 " + c._window + "）\n" + DescribeAll();
                }
                catch (Exception e)
                {
                    Plugin.LogOnce("complete-debug", "[Complete] 展开候选失败：" + e.GetType().Name);
                }
            }
            return "（没有可用的补齐字段）";
        }

        /// <summary>
        /// 行为模拟自测：给补齐字段塞 <paramref name="total"/> 个假候选，走一遍翻页并逐步记录
        /// 「起点 / 可见项 / 选中项」，最后给一条判据。
        ///
        /// <para>为什么需要它：翻页是纯逻辑，手点测不出「点两次下一页又跳回第一页」这种
        /// 一圈一页的怪行为（旧实现就是 <c>起点 += 页 × 每页数</c> + “越界就绕到另一端”）。
        /// 把每一步的数字打出来，「逐页 / 不循环 / 不跳 / 末页不足也照旧」就一目了然。</para>
        /// </summary>
        public static string DebugPageSimulation(int total = 17)
        {
            UiCompleter c = null;
            for (var i = 0; i < All.Count && c == null; i++)
            {
                if (All[i]._field != null && All[i]._field.IsValid)
                    c = All[i];
            }
            if (c == null)
                return "（没有可用的补齐字段，翻页模拟跳过）";

            var savedCandidates = new List<NameIndex.Entry>(c._candidates);
            var savedSelected = c._selected;
            var savedStart = c._viewStart;
            var sb = new System.Text.StringBuilder(400);
            var ok = true;
            try
            {
                c.EnsurePopup();
                c._candidates.Clear();
                for (var i = 0; i < total; i++)
                {
                    var name = "模拟项 " + (i + 1).ToString("00");
                    c._candidates.Add(new NameIndex.Entry
                    {
                        IdText = "sim" + i,
                        Path = "sim" + i,
                        Name = name,
                        Display = name,
                    });
                }
                c._selected = 0;
                c._viewStart = 0;
                c.FillItems();
                c.Show();

                var window = Mathf.Max(1, c._window);
                var pages = (total + window - 1) / window;
                sb.Append("翻页模拟：候选=").Append(total).Append("  每页=").Append(window)
                  .Append("  页数=").Append(pages).Append('\n');
                sb.Append("  ").Append(Step(c, "初始")).Append('\n');

                var prev = c._viewStart;
                for (var i = 0; i < 5; i++)
                {
                    c.Page(1);
                    if (c._viewStart < prev)
                    {
                        ok = false;
                        sb.Append("  ⚠️ 下翻时起点回退（绕回第一页）\n");
                    }
                    if (c._viewStart % window != 0)
                    {
                        ok = false;
                        sb.Append("  ⚠️ 下翻后起点不是整页\n");
                    }
                    prev = c._viewStart;
                    sb.Append("  ").Append(Step(c, "下翻" + (i + 1))).Append('\n');
                }
                if (prev == 0)
                {
                    ok = false;
                    sb.Append("  ⚠️ 下翻完全没有反应\n");
                }
                if (prev > total - 1)
                {
                    ok = false;
                    sb.Append("  ⚠️ 下翻越过了最后一项\n");
                }

                for (var i = 0; i < 6; i++)
                {
                    c.Page(-1);
                    if (c._viewStart > prev)
                    {
                        ok = false;
                        sb.Append("  ⚠️ 上翻时起点前进\n");
                    }
                    prev = c._viewStart;
                    sb.Append("  ").Append(Step(c, "上翻" + (i + 1))).Append('\n');
                }
                if (c._viewStart != 0)
                {
                    ok = false;
                    sb.Append("  ⚠️ 连点上翻回不到第一页\n");
                }
                sb.Append("判据 翻页=").Append(ok ? "OK" : "NG");
            }
            catch (Exception e)
            {
                sb.Append("（翻页模拟失败：").Append(e.GetType().Name).Append("）");
            }
            finally
            {
                c._candidates.Clear();
                c._candidates.AddRange(savedCandidates);
                c._selected = savedSelected;
                c._viewStart = savedStart;
                try { c.FillItems(); } catch (Exception) { }
                try { c.Refresh(true); } catch (Exception) { }
            }
            return sb.ToString();
        }

        private static string Step(UiCompleter c, string label)
        {
            var first = c._shown.Count == 0 ? "（空）" : c._shown[0].Display;
            var last = c._shown.Count == 0 ? "（空）" : c._shown[c._shown.Count - 1].Display;
            return label.PadRight(6)
                + " 起点=" + c._viewStart
                + " 可见=" + c._shown.Count
                + " 首项=" + first
                + " 末项=" + last
                + " 选中=" + c._selected;
        }

        public static string DescribeAll()
        {
            if (All.Count == 0)
                return "（无补齐字段）";            var sb = new System.Text.StringBuilder(240);
            for (var i = 0; i < All.Count; i++)
            {
                var c = All[i];
                sb.Append("  ").Append(c.KindName)
                  .Append("  弹窗=").Append(c._built ? (UiFinder.IsAlive(c._popup) ? "OK" : "丢失") : "未建")
                  .Append("  开=").Append(c._open ? 1 : 0)
                  .Append("  浮层内点击=").Append(c._clickInPopup ? 1 : 0)
                  .Append("  候选=").Append(c._candidates.Count)
                  .Append("  显示=").Append(c._shown.Count)
                  .Append("  选中=").Append(c._selected)
                  .Append("  窗口=").Append(c._window)
                  .Append("  ").Append(PopupRect(c))
                  .Append("  文本「").Append(c._field == null ? "?" : c._field.Text).Append("」")
                  .Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>弹窗的尺寸/位置 —— 「弹窗建好了但高度是 0」这种毛病只能这样看出来。</summary>
        private static string PopupRect(UiCompleter c)
        {
            try
            {
                var node = DebugPopupOf(c);
                if (!UiFinder.IsAlive(node))
                    return string.Empty;
                var rt = node.transform.TryCast<RectTransform>();
                if (rt == null)
                    return string.Empty;
                var r = PanelDump.ScreenRect(rt);
                return "弹窗尺寸=" + Mathf.RoundToInt(rt.rect.width) + "x" + Mathf.RoundToInt(rt.rect.height)
                    + " 屏y[" + Mathf.RoundToInt(r.yMin) + "~" + Mathf.RoundToInt(r.yMax) + "]";
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        // ------------------------------------------------------------------
        // 每帧：键盘
        // ------------------------------------------------------------------

        private void Tick()
        {
            if (_field == null || !_field.IsValid)
                return;

            var focused = _field.IsFocused;

            // 鼠标按下：
            //  · 落在浮层里 → 浮层自己处理（点候选补全 / 点箭头翻页），浮层保持不动；
            //  · 落在浮层外、又不在本格上 → **立刻关掉浮层**（点面板别处、点面板外都算）。
            //
            // ⚠️ 不能改成「监听输入框失焦」：TMP 在点**任何**按钮时都会让输入框失焦，
            // 而「点翻页箭头」也会失焦 —— 照那个判据要么把浮层误关，要么一旦被跳过
            // 就再也关不掉（实测两条都报过）。
            _clickInPopup = false;
            if (Input.GetMouseButtonDown(0))
            {
                var pointer = (Vector2)Input.mousePosition;
                if (Contains(PopupNode(), pointer))
                {
                    _clickInPopup = true;
                    HandlePopupClick(pointer);
                }
                else if (ShouldCloseOnPointerDown(pointer))
                {
                    Hide();
                }
            }

            // 进焦点：记快照（Esc 还原用），并立刻列出全部候选。
            if (focused && !_wasFocused)
            {
                _snapshot = _field.Text;
                Refresh(true, true);
            }
            _wasFocused = focused;

            // 浮层开着时，即使输入框已经失焦（点了翻页箭头）继续响应键盘。
            if (!focused && !_open)
                return;

            // 浮层开着就每帧校一次位置/尺寸（换页会改行数、面板拖动会改输入框位置）。
            if (_open)
                LayoutBox();

            if (Input.GetKeyDown(KeyCode.Tab)
                || Input.GetKeyDown(KeyCode.Return)
                || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                Choose();
                return;
            }
            if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                Move(-1);
                return;
            }
            if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                Move(1);
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Revert();
                Hide();
            }
        }

        /// <summary>当前要参与命中的浮层节点（外框优先）。</summary>
        private GameObject PopupNode()
        {
            return UiFinder.IsAlive(_box) ? _box : _popup;
        }

        /// <summary>
        /// 这一次鼠标按下是否应该关掉浮层。
        ///
        /// <para>判据只有一条：**点在浮层和本格之外**。刻意与“输入框失焦”脱钩 ——
        /// 点翻页箭头/候选项都会让输入框失焦，用失焦当判据就会出现「点箭头后浮层再也关不掉」
        /// （以及反向的「一失焦就误关」），两条都实测报过。</para>
        /// </summary>
        private bool ShouldCloseOnPointerDown(Vector2 pointer)
        {
            if (!_open)
                return false;
            if (Contains(PopupNode(), pointer))
                return false;
            if (Contains(_field == null ? null : _field.Root, pointer))
                return false;
            return true;
        }

        /// <summary>指针是否落在该节点（含未激活时返回 false）。</summary>
        private static bool Contains(GameObject node, Vector2 screenPoint)
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

        /// <summary>
        /// 浮层里的鼠标点击：**只管候选项**（点候选 → 补全）。
        ///
        /// <para>翻页箭头不在这里处理：箭头是原版 <c>Button</c>，用它的 <c>onClick</c>
        /// （鼠标抬起时触发）就与游戏自己的控件语义一致（不可用时不响应）。
        /// 两处都做会让一次点击翻**两页** —— 「翻页效果混乱」的一大半就是这个。</para>
        ///
        /// <para>不用行 <c>Toggle</c> 的事件而用实测命中：行的下标映射一旦有一点偏差
        /// 就会“点第一项没反应”，而矩形命中是所见即所得。</para>
        /// </summary>
        private void HandlePopupClick(Vector2 pointer)
        {
            try
            {
                if (_items == null)
                    return;
                for (var i = 0; i < _shown.Count; i++)
                {
                    var go = _items.getElement(i);
                    if (!Contains(go, pointer))
                        continue;
                    _selected = _viewStart + i;
                    Choose();
                    return;
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("complete-click", "[Complete] 处理浮层点击失败：" + e.GetType().Name);
            }
        }

        private bool _wasFocused;

        // ------------------------------------------------------------------
        // 文本变化 / 提交
        // ------------------------------------------------------------------

        private void OnTextChanged(string text)
        {
            try
            {
                if (_field == null || !_field.IsValid)
                    return;
                if (!_field.IsFocused)
                    return;
                Refresh(true);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("complete-changed", "[Complete] 过滤候选失败：" + e.GetType().Name);
            }
        }

        /// <summary>
        /// 提交：把文本解析成 id 并交给外部处理；解析不出来就**回退原值**。
        /// 外部处理（页面）通过 <see cref="SetHandler"/> 注册。
        ///
        /// <para>页面收到 <paramref name="handler"/> 的参数为 <c>null</c> 时表示「当前没有选中任何值」
        /// （框被清空，或文本解析不出来）；页面自己决定要不要清掉对应的待改值。</para>
        /// </summary>
        private void OnCommitted(string text)
        {
            try
            {
                // 清空输入框 = 取消当前选择，不算“无法识别”。
                if (string.IsNullOrWhiteSpace(text))
                {
                    _picked = null;
                    if (_handler != null)
                        _handler(null);
                    return;
                }

                var entry = _picked != null && _field != null && NameIndex.BoxText(_picked) == _field.Text
                    ? _picked
                    : Index.Find(text);

                if (entry != null)
                {
                    // 值没变 → **什么都不做**。
                    // TMP 在输入框失焦时也会走提交（点一下输入框再点别处就够触发），
                    // 而"把同一个值再应用一遍"对有副作用的项是灾难 ——
                    // 音乐就是这样被从头重播的（用户报过）。
                    if (SameEntry(_picked, entry))
                    {
                        _snapshot = NameIndex.BoxText(entry);
                        return;
                    }
                    _picked = entry;
                    if (_handler != null)
                        _handler(entry);
                    _snapshot = entry.Display;
                }
                else
                {
                    // 解析不出来 → 回退到快照（并让页面知道"这次没改成"）。
                    Plugin.Log?.LogInfo("[Complete] 输入「" + text + "」无法识别，已回退到「" + _snapshot + "」");
                    _field.Text = _snapshot;
                    _handler?.Invoke(null);
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("complete-commit", "[Complete] 提交失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private Action<NameIndex.Entry> _handler;
        private NameIndex.Entry _picked;

        /// <summary>提交回调：<c>null</c> 表示"文本解析不出来"（此时字段已被回退成原值）。</summary>
        public UiCompleter SetHandler(Action<NameIndex.Entry> handler)
        {
            _handler = handler;
            return this;
        }

        /// <summary>程序化设值（不触发提交，但会更新快照）。</summary>
        public void SetValue(NamespaceID id)
        {
            if (_field == null)
                return;
            var entry = id == null ? null : FindById(id);
            _picked = entry;
            _field.Text = entry != null
                ? NameIndex.BoxText(entry)
                : (id == null ? string.Empty : id.ToString());
            _snapshot = _field.Text;
        }

        /// <summary>页面正在编辑这个格子吗（慢速同步要跳过它）。</summary>
        public bool IsEditing
        {
            get { return _field != null && _field.IsValid && _field.IsFocused; }
        }

        /// <summary>当前框里的文本。</summary>
        public string Text
        {
            get { return _field == null ? string.Empty : _field.Text; }
        }

        /// <summary>
        /// 按游戏里的实际值**静默同步**（慢速重同步用）。
        ///
        /// <para>三个前提：框不在编辑中；文本真的变了才写（毎 30 帧写一次会打断光标/选区）；
        /// 解析不出条目就回落到 id 文本（仍可按 id 匹配）。</para>
        /// </summary>
        public void SyncTo(NamespaceID id)
        {
            if (_field == null || !_field.IsValid || _field.IsFocused)
                return;
            // ⚠️ id 为 null 时**什么都不做**：那是“游戏这边现在读不到这个值”
            // （实测关卡的 Difficulty 会在某些时刻为 null），不是“值被清空了”。
            // 照直写会把已经显示出来的正确值抹掉（难度格就是这样变空的）。
            if (id == null)
                return;
            var entry = FindById(id);
            var text = entry != null ? NameIndex.BoxText(entry) : id.ToString();
            if (_field.Text == text)
                return;
            _picked = entry;
            _field.Text = text;
            _snapshot = text;
        }

       private NameIndex.Entry FindById(NamespaceID id)
        {
            if (id == null)
                return null;
            var entries = Index.Entries;
            for (var i = 0; i < entries.Count; i++)
            {
                if (id.Equals(entries[i].Id))
                    return entries[i];
            }
            return null;
        }

        /// <summary>两个条目是不是同一个 id（用于「值没变就不重复应用」）。</summary>
        private static bool SameEntry(NameIndex.Entry a, NameIndex.Entry b)
        {
            if (a == null || b == null)
                return false;
            try { return a.Id != null && a.Id.Equals(b.Id); }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------
        // 候选列表
        // ------------------------------------------------------------------

        private void Refresh(bool show, bool listAll = false)
        {
            if (!EnsurePopup())
                return;

            _candidates.Clear();
            if (listAll)
            {
                // 刚进焦点：框里装的是上一次选中的值，不能拿它当筛选词 ——
                // 否则“聚焦即列出全部候选”会变成“只列出一项”。
                var entries = Index.Entries;
                for (var i = 0; i < entries.Count && _candidates.Count < MaxCandidates; i++)
                    _candidates.Add(entries[i]);
                var current = Index.Find(_field.Text);
                _selected = 0;
                if (current != null)
                {
                    var at = _candidates.IndexOf(current);
                    if (at >= 0)
                        _selected = at;
                }
            }
            else
            {
                _candidates.AddRange(Index.Match(_field.Text, MaxCandidates));
                // 选中项尽量保持可见；没有候选时选中归零。
                if (_selected >= _candidates.Count)
                    _selected = _candidates.Count - 1;
                if (_selected < 0)
                    _selected = 0;
            }
            ClampWindow();
            FillItems();

            if (show && _candidates.Count > 0)
            {
                LayoutBox();
                Show();
            }
            else
            {
                Hide();
            }
        }

        /// <summary>
        /// 浮层的尺寸与位置。
        ///
        /// <para><b>尺寸</b>：高 = 6 行（需求固定 6 个条目） + 上下箭头 + 原版弹窗自己的内边距/间距；
        /// 宽 = 最长候选文字（实测）+ 图标宽 + 内边距，并夹在「不能超出面板宽度」的上限内。</para>
        ///
        /// <para><b>位置</b>：挂在**对话框根节点**下（它没有布局组，所以浮层不会参与面板的纵向排版、
        /// 也不会把面板顶高），枢轴取左下角的（0,1）—— 即“以左上角为基准向下展开”，
        /// 左上角对齐输入框的左下角。因为和输入框挂在同一个父节点下，拖面板时两者一起走。</para>
        /// </summary>
        private void LayoutBox()
        {
            try
            {
                if (!UiFinder.IsAlive(_box))
                    return;
                var box = _box.transform.TryCast<RectTransform>();
                if (box == null)
                    return;

                var root = LoadoutPanel.DialogRoot;
                var input = _field == null ? null : _field.Input;
                if (root == null || input == null)
                    return;
                var inputRt = input.transform.TryCast<RectTransform>();
                if (inputRt == null)
                    return;

                var line = _rowHeight > 1f ? _rowHeight : UiKit.LineHeight;
                var rows = Mathf.Max(1, _window);
                var overhead = GroupOverhead(_popup) + UiContainer.PaddingOf(_box);
                var arrows = PreferredHeight(_up == null ? null : _up.gameObject)
                    + PreferredHeight(_down == null ? null : _down.gameObject);
                var rowGap = SpacingOf(_items);
                var height = overhead + arrows + rows * line + rowGap * Mathf.Max(0, rows - 1);

                var width = MeasureWidth(root);

                // 锚点取父节点左下角的一个**点**，枢轴取左上角（“以左上角为基准向下展开”），
                // 位置用「屏幕点 → 父节点局部点 − 父节点矩形左下角」算 —— 不用手算任何缩放系数。
                //
                // ⚠️ 不要用 `box.position = 世界点` 那种写法：实测在这个环境里 SettleLayout 之后
                // 浮层会被摆到屏幕外一万多像素处（世界坐标赋值与 RectTransform 的锚点换算没对上）。
                box.anchorMin = new Vector2(0f, 0f);
                box.anchorMax = new Vector2(0f, 0f);
                box.pivot = new Vector2(0f, 1f);
                box.sizeDelta = new Vector2(width, height);

                var cam = LoadoutPanel.CanvasCamera();
                var worldCorner = inputRt.TransformPoint(new Vector3(inputRt.rect.xMin, inputRt.rect.yMin, 0f));
                var screen = RectTransformUtility.WorldToScreenPoint(cam, worldCorner);
                Vector2 local;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(root, screen, cam, out local))
                    return;
                var anchored = local - root.rect.min;
                var scale = LoadoutPanel.LocalScaleToScreenOf(root);
                if (scale <= 0f)
                    scale = 1f;
                anchored.y -= GapPixels / scale;

                // 超出屏幕就往上提（浮层往下展开，所以下限看屏底）。
                box.anchoredPosition = anchored;
                var screenRect = PanelDump.ScreenRect(box);
                if (screenRect.yMin < 2f)
                {
                    anchored.y += (2f - screenRect.yMin) / scale;
                    box.anchoredPosition = anchored;
                    screenRect = PanelDump.ScreenRect(box);
                }
                // 日志只打“变化”：浮层开着时这个函数每帧都跑，不加这个判定会把日志洗没。
                var sig = Mathf.RoundToInt(width) + "x" + Mathf.RoundToInt(height) + "@"
                    + Mathf.RoundToInt(screenRect.xMin) + "," + Mathf.RoundToInt(screenRect.yMin);
                if (sig != _lastLogSig)
                {
                    _lastLogSig = sig;
                    Plugin.Log?.LogInfo("[Complete] " + KindName + " 浮层 " + Mathf.RoundToInt(width)
                        + "x" + Mathf.RoundToInt(height) + " 行=" + rows
                        + " 输入框屏[" + Mathf.RoundToInt(PanelDump.ScreenRect(inputRt).xMin) + ","
                        + Mathf.RoundToInt(PanelDump.ScreenRect(inputRt).yMin) + " → "
                        + Mathf.RoundToInt(PanelDump.ScreenRect(inputRt).xMax) + ","
                        + Mathf.RoundToInt(PanelDump.ScreenRect(inputRt).yMax) + "]"
                        + " 浮层屏[" + Mathf.RoundToInt(screenRect.xMin) + ","
                        + Mathf.RoundToInt(screenRect.yMin) + " → "
                        + Mathf.RoundToInt(screenRect.xMax) + ","
                        + Mathf.RoundToInt(screenRect.yMax) + "]");
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("complete-layout", "[Complete] 摆放候选浮层失败：" + e.GetType().Name);
            }
        }

        /// <summary>浮层宽度：最长候选文字需要多宽（本页的候选里量）。</summary>
        private float MeasureWidth(RectTransform root)
        {
            var max = 60f;
            try
            {
                if (_rowText != null)
                {
                    for (var i = 0; i < _shown.Count; i++)
                    {
                        var size = _rowText.GetPreferredValues(_shown[i].Display);
                        if (size.x > max)
                            max = size.x;
                    }
                }
            }
            catch (Exception) { }
            var width = max + _rowIconWidth + UiContainer.PaddingOf(_popup);
            // 上限：不能超出面板本身。
            var limit = root.rect.width * 0.92f;
            if (width > limit)
                width = limit;
            return Mathf.Max(120f, width);
        }

        private static float SpacingOf(ElementList list)
        {
            try
            {
                if (list == null)
                    return 0f;
                var lg = list.GetComponent<LayoutGroup>();
                if (lg == null)
                    return 0f;
                var hv = lg.TryCast<HorizontalOrVerticalLayoutGroup>();
                return hv == null ? 0f : hv.spacing;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        private void ClampWindow()
        {
            if (_window < 2)
                _window = 2;
            var total = _candidates.Count;
            if (total <= _window)
            {
                _viewStart = 0;
                return;
            }
            if (_selected < _viewStart)
                _viewStart = _selected;
            if (_selected >= _viewStart + _window)
                _viewStart = _selected - _window + 1;
            if (_viewStart < 0)
                _viewStart = 0;
            if (_viewStart > total - _window)
                _viewStart = total - _window;
        }

        private void FillItems()
        {
            if (_items == null)
                return;
            _shown.Clear();
            for (var i = _viewStart; i < _candidates.Count && _shown.Count < _window; i++)
                _shown.Add(_candidates[i]);

            try
            {
                _items.updateList(_shown.Count, ItemUpdate(), ItemCreate(), ItemDestroy(), true);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("complete-fill", "[Complete] 填充候选失败：" + e.GetType().Name + ": " + e.Message);
                return;
            }

            SetArrow(_up, _viewStart > 0);
            SetArrow(_down, _viewStart + _window < _candidates.Count);
        }

        private Il2CppSystem.Action<int, GameObject> ItemUpdate()
        {
            if (_onItemManaged == null)
            {
                _onItemManaged = new System.Action<int, GameObject>(OnItemUpdate);
                _onItemIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int, GameObject>>(_onItemManaged);
                UiClone.KeepAlive(_popup, _onItemManaged, _onItemIl2Cpp);
            }
            return _onItemIl2Cpp;
        }

        private Il2CppSystem.Action<int, GameObject> _onItemIl2Cpp;
        private Il2CppSystem.Action<GameObject> _onItemCreateIl2Cpp;
        private Il2CppSystem.Action<GameObject> _onItemDestroyIl2Cpp;

        private Il2CppSystem.Action<GameObject> ItemCreate()
        {
            if (_onItemCreateManaged == null)
            {
                _onItemCreateManaged = new System.Action<GameObject>(OnItemCreate);
                _onItemCreateIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<GameObject>>(_onItemCreateManaged);
                UiClone.KeepAlive(_popup, _onItemCreateManaged, _onItemCreateIl2Cpp);
            }
            return _onItemCreateIl2Cpp;
        }

        private Il2CppSystem.Action<GameObject> ItemDestroy()
        {
            if (_onItemDestroyManaged == null)
            {
                _onItemDestroyManaged = new System.Action<GameObject>(OnItemDestroy);
                _onItemDestroyIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<GameObject>>(_onItemDestroyManaged);
                UiClone.KeepAlive(_popup, _onItemDestroyManaged, _onItemDestroyIl2Cpp);
            }
            return _onItemDestroyIl2Cpp;
        }

        private void OnItemUpdate(int index, GameObject item)
        {
            try
            {
                if (!UiFinder.IsAlive(item) || index < 0 || index >= _shown.Count)
                    return;
                var component = item.GetComponent<DebugConsoleAutoCompleteItem>();
                if (component == null)
                    return;
                component.SetText(_shown[index].Display);
                component.SetIsOn(index == _selected - _viewStart);
            }
            catch (Exception) { }
        }

        private void OnItemCreate(GameObject item)
        {
            try
            {
                if (!UiFinder.IsAlive(item))
                    return;
                item.transform.localScale = Vector3.one;
                // 候选行不再自己接鼠标点击：它里面的 Toggle 会把点击自己翻一次，
                // 而点击处理权在我们手里（HandlePopupClick）—— 两处都做就是一次点击选两遍。
                MuteToggle(item);
            }
            catch (Exception) { }
        }

        /// <summary>挡掉节点上 Toggle 的鼠标接收（只挡射线，不改颜色）。见 <see cref="UiArchiveList"/> 同类做法。</summary>
        private static void MuteToggle(GameObject item)
        {
            try
            {
                var go = item.GetComponent<Toggle>() != null ? item : null;
                if (go == null)
                {
                    var toggle = item.GetComponentInChildren<Toggle>(true);
                    if (toggle == null)
                        return;
                    go = toggle.gameObject;
                }
                var group = go.GetComponent<CanvasGroup>();
                if (group == null)
                    group = go.AddComponent<CanvasGroup>();
                if (group != null)
                {
                    group.alpha = 1f;
                    group.interactable = true;
                    group.blocksRaycasts = false;
                }
            }
            catch (Exception) { }
        }

        private void OnItemDestroy(GameObject item)
        {
            // 无操作（保留回调形状）。
        }

        /// <summary>
        /// 自测：关不关浮层的判据（点浮层内 / 本格内不该关，点别处该关）。
        ///
        /// <para>“点翻页箭头后浮层再也关不掉”那个 bug 就是这条判据的历史包袱，
        /// 所以这里用矩形中心点把它钉住（浮层、本格、屏幕角落各一个）。</para>
        /// </summary>
        public static string DebugCloseSimulation()
        {
            UiCompleter c = null;
            for (var i = 0; i < All.Count && c == null; i++)
            {
                if (All[i]._field != null && All[i]._field.IsValid)
                    c = All[i];
            }
            if (c == null)
                return "（没有可用的补齐字段，关闭模拟跳过）";

            var sb = new System.Text.StringBuilder(220);
            var wasOpen = c._open;
            try
            {
                c.EnsurePopup();
                c.Show();
                var popup = Centre(c.PopupNode());
                var fieldNode = c._field == null ? null : c._field.Root;
                var field = Centre(fieldNode);
                if (popup == null || field == null)
                {
                    sb.Append("关闭判据模拟：量不到浮层或本格的矩形（浮层=")
                      .Append(popup == null ? "无" : "OK").Append(" 本格=")
                      .Append(field == null ? "无" : "OK").Append('）');
                    return sb.ToString();
                }
                var a = c.ShouldCloseOnPointerDown(popup.Value);
                var b = c.ShouldCloseOnPointerDown(field.Value);
                var d = c.ShouldCloseOnPointerDown(new Vector2(2f, 2f));
                sb.Append("关闭判据模拟：点浮层内=" ).Append(a ? 1 : 0).Append("（应 0）")
                  .Append("  点本格内=").Append(b ? 1 : 0).Append("（应 0）")
                  .Append("  点屏角=").Append(d ? 1 : 0).Append("（应 1）")
                  .Append("  判据=").Append(!a && !b && d ? "OK" : "NG");
            }
            catch (Exception e)
            {
                sb.Append("（关闭模拟失败：").Append(e.GetType().Name).Append('）');
            }
            finally
            {
                try { c._open = wasOpen; if (!wasOpen) c.Hide(); }
                catch (Exception) { }
            }
            return sb.ToString();
        }

        private static Vector2? Centre(GameObject node)
        {
            if (!UiFinder.IsAlive(node))
                return null;
            var rt = node.transform.TryCast<RectTransform>();
            if (rt == null)
                return null;
            try
            {
                var r = PanelDump.ScreenRect(rt);
                return new Vector2((r.xMin + r.xMax) * 0.5f, (r.yMin + r.yMax) * 0.5f);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void SetArrow(Button button, bool interactable)
        {
            if (button == null)
                return;
            try { button.interactable = interactable; }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 键盘动作
        // ------------------------------------------------------------------

        private void Move(int delta)
        {
            if (_candidates.Count == 0)
                return;
            _selected = (_selected + delta + _candidates.Count) % _candidates.Count;
            ClampWindow();
            FillItems();
            SyncItemSelection();
        }

        /// <summary>只更新选中态（不重建列表），键盘上下移动时用。</summary>
        private void SyncItemSelection()
        {
            if (_items == null)
                return;
            try
            {
                for (var i = 0; i < _shown.Count; i++)
                {
                    var go = _items.getElement(i);
                    if (!UiFinder.IsAlive(go))
                        continue;
                    var item = go.GetComponent<DebugConsoleAutoCompleteItem>();
                    if (item != null)
                        item.SetIsOn(i == _selected - _viewStart);
                }
            }
            catch (Exception) { }
        }

        private void Choose()
        {
            if (_candidates.Count == 0 || _selected < 0 || _selected >= _candidates.Count)
            {
                // 没有候选时把当前文本原样提交一次（可能是直接敲了 id）。
                _field.Text = _field.Text;
                InvokeHandlerWithParsed();
                Hide();
                return;
            }
            var entry = _candidates[_selected];
            _picked = entry;
            _field.Text = NameIndex.BoxText(entry);
            _snapshot = _field.Text;
            if (_handler != null)
                _handler(entry);
            Hide();
            Plugin.Log?.LogInfo("[Complete] 已补全 " + KindName + " → " + entry.Display);
        }

        private void InvokeHandlerWithParsed()
        {
            if (_handler == null)
                return;
            var entry = Index.Find(_field.Text);
            if (entry != null)
            {
                _picked = entry;
                _handler(entry);
            }
            else
            {
                _field.Text = _snapshot;
                _handler(null);
            }
        }

        private void Revert()
        {
            _field.Text = _snapshot;
        }

        private void Show()
        {
            if (!_built)
                return;
            var node = UiFinder.IsAlive(_box) ? _box : _popup;
            if (!UiFinder.IsAlive(node))
                return;
            try
            {
                if (!_open)
                {
                    // 显隐做在**外框**上：外框不活跃时父布局组会直接忽略它（不留高度），
                    // 而只关内层的话，外框那段预留高度会变成一片空白。
                    node.SetActive(true);
                    if (UiFinder.IsAlive(_popup) && !_popup.activeSelf)
                        _popup.SetActive(true);
                    _open = true;
                }
            }
            catch (Exception) { }
        }

        private void Hide()
        {
            var node = UiFinder.IsAlive(_box) ? _box : _popup;
            if (!UiFinder.IsAlive(node))
            {
                _open = false;
                return;
            }
            try
            {
                if (_open)
                {
                    node.SetActive(false);
                    _open = false;
                }
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 弹窗构建
        // ------------------------------------------------------------------

        /// <summary>
        /// 修补候选列表与候选行的尺寸契约。
        ///
        /// <para><b>这就是「补全框里字符串被挤成一条竖线」的根因与修法。</b>
        /// 控制台那个面板的候选是给**短单词**用的：模板行只有 82x20，里面的文字节点宽度写死 72。
        /// 我们的候选是「名字  (命名空间:id)」（需要 200~580 单位），塞进那套契约之后：
        /// 行与文字的矩形被算成 0，而 TMP 的自动换行于是把每个字符都断到单独一行 —— 视觉上就是一条竖线。</para>
        ///
        /// <para>这里全部按**原版素材自己的尺寸**补：行高 = 模板行自己的矩形高、行宽跟列表走；
        /// 文字节点占满行内剩余宽度，并且**关掉自动换行**（再长也不会断成多行，超出用省略号）。</para>
        /// </summary>
        private void PatchItems()
        {
            try
            {
                if (_items == null)
                    return;

                // ① 列表本体：占满弹窗（它自己的矩形是个既定小尺寸，不补就会把行挤在角落里）。
                UiContainer.FillParent(_items.gameObject, null);

                // ② 行模板：等宽（宽弹性）+ 固定高（取素材自己的行高）。
                var tpl = _items.getTemplate();
                if (!UiFinder.IsAlive(tpl))
                    return;
                var rt = tpl.transform.TryCast<RectTransform>();
                _rowHeight = rt == null ? 20f : Mathf.Max(12f, Mathf.Abs(rt.rect.height));
                UiContainer.FitCell(tpl, 0f, _rowHeight);

                // ③ 行内文字：占满剩余宽度、不换行；并记下文字节点与图标宽度（浮层宽度要用）。
                var texts = tpl.GetComponentsInChildren<TextMeshProUGUI>(true);
                if (texts != null)
                {
                    for (var i = 0; i < texts.Length; i++)
                    {
                        var t = texts[i];
                        if (t == null)
                            continue;
                        try
                        {
                            t.enableWordWrapping = false;
                            t.overflowMode = TextOverflowModes.Ellipsis;
                            // 需求：候选文字**左对齐**（原版控制台这个是居中）。
                            t.alignment = TextAlignmentOptions.Left;
                        }
                        catch (Exception) { }
                        if (_rowText == null)
                            _rowText = t;
                        var le = t.gameObject.GetComponent<LayoutElement>();
                        if (le == null)
                            le = t.gameObject.AddComponent<LayoutElement>();
                        if (le != null)
                        {
                            le.minWidth = -1f;
                            le.preferredWidth = 0f;
                            le.flexibleWidth = 1f;
                        }
                    }
                }
                // 图标宽（浮层宽度 = 图标 + 文字 + 内边距）。
                _rowIconWidth = 0f;
                var icon = UiFinder.FindChild(tpl.transform, "Icon");
                if (icon != null)
                {
                    var iconRt = icon.TryCast<RectTransform>();
                    if (iconRt != null && iconRt.rect.width > 1f)
                        _rowIconWidth = iconRt.rect.width;
                }
                Plugin.Log?.LogInfo("[Complete] 候选项契约已补齐：行高=" + Mathf.RoundToInt(_rowHeight)
                    + "  图标宽=" + Mathf.RoundToInt(_rowIconWidth)
                    + "  文字节点=" + (texts == null ? 0 : texts.Length)
                    + "  对齐=" + (_rowText == null ? "?" : _rowText.alignment.ToString())
                    + "  换行=" + (_rowText == null ? "?" : (_rowText.enableWordWrapping ? "开" : "关")));
            }
            catch (Exception e)
            {
                Plugin.LogOnce("complete-patch", "[Complete] 修补候选项契约失败：" + e.GetType().Name);
            }
        }

        /// <summary>某个节点自己的 preferred 高度（拿不到就返回 0）。</summary>
        private static float PreferredHeight(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return 0f;
            try
            {
                var le = go.GetComponent<LayoutElement>();
                return le == null || le.preferredHeight < 0f ? 0f : le.preferredHeight;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        /// <summary>
        /// 一个布局组自己吃掉的高度：四边内边距 + 子节点之间的间距。
        /// 全部读**素材自己的值**（间距按“子节点数 - 1”算，多算一格只是保守一点）。
        /// </summary>
        private static float GroupOverhead(GameObject node)
        {
            if (!UiFinder.IsAlive(node))
                return 0f;
            try
            {
                var lg = node.GetComponent<LayoutGroup>();
                if (lg == null)
                    return 0f;
                var total = 0f;
                try
                {
                    var pad = lg.padding;
                    total += pad.top + pad.bottom + pad.left + pad.right;
                }
                catch (Exception) { }
                try
                {
                    var hv = lg.TryCast<HorizontalOrVerticalLayoutGroup>();
                    if (hv != null)
                        total += hv.spacing * Mathf.Max(0, node.transform.childCount - 1);
                }
                catch (Exception) { }
                return total;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        private bool EnsurePopup()
        {
            if (_built)
                return UiFinder.IsAlive(_popup) && _items != null;
            _built = true;

            var template = FindPopupTemplate();
            if (!UiFinder.IsAlive(template))
            {
                Plugin.LogOnce("complete-tpl",
                    "[Complete] 拿不到原版控制台的补齐面板（DebugConsoleUI.autoCompletePanel），"
                    + "输入框仍然可用，但没有候选列表。");
                return false;
            }

            _popup = UiClone.Instantiate(template, null, "DSH_Complete");
            if (!UiFinder.IsAlive(_popup))
            {
                Plugin.LogOnce("complete-clone", "[Complete] 补齐面板克隆失败。");
                return false;
            }

            // 原版这个面板自带 ContentSizeFitter（按自己的内容撑尺寸）——
            // 在我们这个「高度由内容撑」的对话框里，它会把面板撑到屏幕外（实测 40x1872）。
            UiContainer.DisableSizeFitters(_popup);

            // 外框 = 浮层容器：挂在**对话框根节点**下（它没有布局组），并标成「不参与布局」，
            // 尺寸完全由我们按候选内容算（见 LayoutBox）。
            // 早期版本把它插进面板的纵向布局里，结果是候选一开就把面板顶得很高 —— 需求不要那个效果。
            _box = UiContainer.Instantiate(null, "DSH_CompleteBox", true);
            if (!UiFinder.IsAlive(_box))
            {
                Plugin.LogOnce("complete-box", "[Complete] 候选外框创建失败。");
                return false;
            }
            try
            {
                var root = LoadoutPanel.DialogRoot;
                if (root != null)
                    _box.transform.SetParent(root, false);
                var le = _box.GetComponent<LayoutElement>();
                if (le == null)
                    le = _box.AddComponent<LayoutElement>();
                if (le != null)
                {
                    le.ignoreLayout = true;
                    le.minWidth = -1f;
                    le.preferredWidth = -1f;
                    le.flexibleWidth = -1f;
                    le.minHeight = -1f;
                    le.preferredHeight = -1f;
                    le.flexibleHeight = -1f;
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("complete-parent", "[Complete] 摆放浮层容器失败：" + e.GetType().Name);
            }
            UiContainer.FillParent(_popup, _box.transform);
            _items = UiFinder.FindChildComponent<ElementList>(_popup.transform);
            var buttons = _popup.GetComponentsInChildren<Button>(true);
            _up = FindArrow(buttons, "ArrowUp", 0);
            _down = FindArrow(buttons, "ArrowDown", 1);

            // 需求指定固定 6 个条目。
            _window = DefaultWindow;
            PatchItems();

            if (_items == null)
            {
                Plugin.LogOnce("complete-items", "[Complete] 补齐面板里找不到 ElementList（候选项列表）。");
            }

            if (_up != null)
            {
                _upManaged = new System.Action(() => Page(-1));
                UiClone.OnClick(_up.gameObject, _upManaged);
            }
            if (_down != null)
            {
                _downManaged = new System.Action(() => Page(1));
                UiClone.OnClick(_down.gameObject, _downManaged);
            }

            try { _box.SetActive(false); }
            catch (Exception) { }
            try { _popup.SetActive(false); }
            catch (Exception) { }
            _open = false;

            Plugin.Log?.LogInfo("[Complete] 已装配补齐浮层：" + KindName
                + "  候选项列表=" + (_items == null ? "无" : "OK")
                + "  箭头=" + (_up == null ? "无" : "OK") + "/" + (_down == null ? "无" : "OK")
                + "  每页=" + _window);
            return _items != null;
        }

        /// <summary>
        /// 翻页：**逐页走、不循环**。
        ///
        /// <para>早期实现是 <c>_viewStart += delta * _window</c> + “越界就跳到另一端”，
        /// 于是 10 项时点下一页会直接从第 1 页跳到第 2 页再“绕回”第 1 页 —— 看起来就是乱跳。
        /// 现在按页码算：页 = 下取整(起点 / 每页数)，夹在 [0, 页数-1] 内。
        /// 末页不足一整页也照旧（框体高度恒为 6 行，不会跳）。</para>
        ///
        /// <para>换页**不是改选**：只在选中项被甩出可视窗口时才把它拖进来。</para>
        /// </summary>
        private void Page(int delta)
        {
            var total = _candidates.Count;
            if (total == 0 || delta == 0)
                return;
            var window = Mathf.Max(1, _window);
            var pages = Mathf.Max(1, (total + window - 1) / window);
            var page = _viewStart / window + delta;
            if (page < 0)
                page = 0;
            if (page > pages - 1)
                page = pages - 1;
            _viewStart = page * window;
            if (_selected < _viewStart)
                _selected = _viewStart;
            var last = Mathf.Min(total - 1, _viewStart + window - 1);
            if (_selected > last)
                _selected = last;
            FillItems();
            LayoutBox();
        }

        private static Button FindArrow(Button[] buttons, string keyword, int fallbackIndex)
        {
            if (buttons == null || buttons.Length == 0)
                return null;
            for (var i = 0; i < buttons.Length; i++)
            {
                var b = buttons[i];
                if (b == null)
                    continue;
                try
                {
                    if (!string.IsNullOrEmpty(b.name)
                        && b.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        return b;
                }
                catch (Exception) { }
            }
            return fallbackIndex >= 0 && fallbackIndex < buttons.Length ? buttons[fallbackIndex] : null;
        }

        /// <summary>每页显示多少项 —— 与原版控制台保持一致（读它的 <c>maxSuggestionCount</c>）。</summary>
        private static int ResolveWindow()
        {
            try
            {
                var main = LevelFacade.Main;
                if (main == null)
                    return DefaultWindow;
                var scene = main.Scene;
                if (scene == null)
                    return DefaultWindow;
                var console = scene.debugConsole;
                if (console == null)
                    return DefaultWindow;
                var n = console.maxSuggestionCount;
                if (n >= 2 && n <= 20)
                    return n;
            }
            catch (Exception) { }
            return DefaultWindow;
        }

        private static GameObject FindPopupTemplate()
        {
            try
            {
                var main = LevelFacade.Main;
                if (main == null)
                    return null;
                var scene = main.Scene;
                if (scene == null)
                    return null;
                var console = scene.debugConsole;
                if (console == null)
                    return null;
                var ui = console.ui;
                if (ui == null)
                    return null;
                var panel = ui.autoCompletePanel;
                return UiFinder.IsAlive(panel) ? panel : null;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("complete-tpl2", "[Complete] 取控制台补齐面板时异常：" + e.GetType().Name);
                return null;
            }
        }
    }
}
