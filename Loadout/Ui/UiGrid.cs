using System;
using System.Collections.Generic;
using DSHCore.Ui;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 「一行里横着摆几个东西」的容器素材。
    ///
    /// <para>原版里一行多个控件的做法（见 <c>OptionsDialog</c>：一行三个滑条、
    /// 一行两个复选框）就是**一个横向 <c>LayoutGroup</c> 容器 + 每个子项
    /// <c>LayoutElement.flexibleWidth = 1</c>**。本类负责把那个容器弄到手：</para>
    ///
    /// <list type="number">
    /// <item><b>首选</b>：克隆原版设置面板里那个「一行三个滑条」的容器
    /// （<c>OptionsDialog/Dialog/MainElements/Sliders/Sliders1</c>）—— 布局参数
    /// （间距、对齐、childControl*）都是 prefab 里配好的，我们只清空它的子节点；</item>
    /// <item><b>退路</b>：自造节点 + <c>AddComponent&lt;HorizontalLayoutGroup&gt;</c>
    /// （本仓库已验证 <c>LayoutElement</c> 可以这么加，布局组现场探测）。</item>
    /// </list>
    ///
    /// <para>抓到哪一条会打进日志（<see cref="Source"/>），排障时先看它。</para>
    /// </summary>
    internal static class UiContainer
    {
        private static bool _resolved;
        private static GameObject _template;
        private static GameObject _vTemplate;
        private static string _source = "（未解析）";
        private static string _vSource = "（未解析）";

        public static string Source { get { return _source; } }

        public static string VerticalSource { get { return _vSource; } }

        /// <summary>造一个容器（<paramref name="vertical"/> 决定横/纵）。返回的节点没有父节点，由调用方挂。</summary>
        public static GameObject Instantiate(Transform parent, string name, bool vertical = false)
        {
            Ensure(vertical);
            var tpl = vertical ? _vTemplate : _template;
            if (UiFinder.IsAlive(tpl))
            {
                var clone = UiClone.Instantiate(tpl, parent, name);
                if (UiFinder.IsAlive(clone))
                {
                    ClearChildren(clone);
                    return clone;
                }
            }
            return Fallback(parent, name, vertical);
        }

        private static void Ensure(bool vertical)
        {
            if (vertical)
            {
                if (_resolved2)
                    return;
                _resolved2 = true;
                var row = OptionDialogChild("MainElements/Sliders");
                if (row != null && row.GetComponent<VerticalLayoutGroup>() != null)
                {
                    _vTemplate = row.gameObject;
                    _vSource = UiFinder.PathOf(row) + "（原版纵向容器）";
                }
                else
                {
                    _vSource = "（拿不到原版纵向容器，用自造容器）";
                }
                return;
            }

            if (_resolved)
                return;
            _resolved = true;
            var hrow = OptionDialogChild("MainElements/Sliders/Sliders1");
            if (hrow != null && hrow.GetComponent<HorizontalLayoutGroup>() != null)
            {
                _template = hrow.gameObject;
                _source = UiFinder.PathOf(hrow) + "（原版横向行容器）";
            }
            else
            {
                _source = "（拿不到原版横向容器，用自造容器）";
            }
        }

        private static bool _resolved2;

        /// <summary>在原版设置面板里按相对路径找节点（找不到返回 null）。</summary>
        private static Transform OptionDialogChild(string path)
        {
            try
            {
                var options = UiFinder.FindOne<MVZ2.UI.OptionsDialog>();
                if (options == null)
                {
                    return null;
                }
                var dialog = options.dialogTransform;
                if (dialog == null)
                    return null;
                return UiFinder.FindByPath(dialog, path);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("uicontainer-probe", "[Ui] 探测原版容器时异常：" + e.GetType().Name);
                return null;
            }
        }

        private static GameObject Fallback(Transform parent, string name, bool vertical)
        {
            var go = UiHost.CreateNode(name, parent);
            if (!UiFinder.IsAlive(go))
                return null;
            if (vertical)
            {
                var v = go.AddComponent<VerticalLayoutGroup>();
                if (v == null)
                {
                    Plugin.LogOnce("uicontainer-addv",
                        "[Ui] 自造纵向容器失败：AddComponent<VerticalLayoutGroup> 返回 null");
                    return go;
                }
                v.spacing = 10f;
                v.childAlignment = TextAnchor.UpperLeft;
                v.childControlWidth = true;
                v.childControlHeight = true;
                v.childForceExpandWidth = false;
                v.childForceExpandHeight = false;
                return go;
            }

            var lg = go.AddComponent<HorizontalLayoutGroup>();
            if (lg == null)
            {
                Plugin.LogOnce("uicontainer-add",
                    "[Ui] 自造横向容器失败：AddComponent<HorizontalLayoutGroup> 返回 null");
                return go;
            }
            lg.spacing = 10f;
            lg.childAlignment = TextAnchor.MiddleCenter;
            lg.childControlWidth = true;
            lg.childControlHeight = true;
            lg.childForceExpandWidth = false;
            lg.childForceExpandHeight = false;
            return go;
        }

        private static string DescribeGroup(Transform row)
        {
            try
            {
                var groups = row.GetComponents<LayoutGroup>();
                if (groups == null || groups.Length == 0)
                    return "无 LayoutGroup";
                var sb = new System.Text.StringBuilder();
                for (var i = 0; i < groups.Length; i++)
                {
                    if (i > 0)
                        sb.Append('+');
                    sb.Append(groups[i].GetIl2CppType().Name);
                }
                return sb.ToString();
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static GameObject Fallback(Transform parent, string name)
        {
            var go = UiHost.CreateNode(name, parent);
            if (!UiFinder.IsAlive(go))
                return null;
            var lg = go.AddComponent<HorizontalLayoutGroup>();
            if (lg == null)
            {
                Plugin.LogOnce("uicontainer-add", "[Ui] 自造行容器失败：AddComponent<HorizontalLayoutGroup> 返回 null");
                return go;
            }
            lg.spacing = 10f;
            lg.childAlignment = TextAnchor.MiddleCenter;
            lg.childControlWidth = true;
            lg.childControlHeight = true;
            lg.childForceExpandWidth = false;
            lg.childForceExpandHeight = false;
            return go;
        }

        internal static void ClearChildren(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                var t = go.transform;
                for (var i = t.childCount - 1; i >= 0; i--)
                {
                    var child = t.GetChild(i);
                    if (child == null)
                        continue;
                    // 用 DestroyImmediate：模板的子孙可能带着会在激活时 Awake 的组件，
                    // 交给帧末的 Destroy 就来不及了。
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("uicontainer-clear", "[Ui] 清空容器子节点失败：" + e.GetType().Name);
            }
        }

        /// <summary>
        /// 把节点变成「等分单元格」：宽度 preferred 0 + flexible 1（父布局组按 flex 等分）。
        ///
        /// <para><b>高度必须自己给</b>：原版那些控件（<c>OptionWidgets</c> / <c>LabeledToggle</c>）
        /// **没有 <c>LayoutElement</c>**，而 <c>LayoutUtility.GetPreferredHeight</c> 在组件里找不到
        /// <c>ILayoutElement</c> 时返回 <b>0</b> —— 于是在「高度由内容撑」的对话框里，整行会被算成 0 高、
        /// 五行叠在一起。原版自己不受影响，因为它的面板是**固定高度**（外层布局组有多余空间可分）。</para>
        ///
        /// <para>高度的来源是**原版素材自己的 RectTransform 高度**（<see cref="UiKit.TemplateHeight"/>），
        /// 不是写死的数字。</para>
        /// </summary>
        internal static LayoutElement FitCell(GameObject go, float preferredWidth = 0f, float preferredHeight = -1f)
        {
            var le = Fit(go, preferredWidth);
            if (le == null)
                return null;
            try
            {
                le.preferredHeight = preferredHeight;
                le.flexibleHeight = 0f;
            }
            catch (Exception) { }
            return le;
        }

        /// <summary>
        /// 去掉节点自己那个布局组的**左右内边距**。
        ///
        /// <para>为什么要这个：单元格容器是从原版设置行的容器克隆来的，它自带一层左右内边距
        /// （实测 13）；而开关行那个格子（<c>OptionWidgets</c>）的内边距是 0。
        /// 于是同一列里「开关的格子」与「标签 + 输入框的格子」会差 13 像素 ——
        /// 这正是“第三/四/五行的文字与按钮对不齐”的原因。清零之后：
        /// 标签左边缘 = 格子左边缘 = 同列开关的左边缘，输入框右边缘 = 格子右边缘。</para>
        ///
        /// <para>只动左右，不动上下（上下要留给行高）。</para>
        /// </summary>
        internal static void FlushHorizontal(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                var lg = go.GetComponent<LayoutGroup>();
                if (lg == null)
                    return;
                var pad = lg.padding;
                pad.left = 0;
                pad.right = 0;
                lg.padding = pad;
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 把一个节点做成「填满父容器」（宽高都由父布局组决定）。
        ///
        /// <para>用于把素材塞进我们自己定好尺寸的框里：flexible 两个轴都给 1，
        /// 而父容器只有一个孩子时，孩子就会拿到容器的全部剩余空间。</para>
        /// </summary>
        internal static GameObject FillParent(GameObject go, Transform parent)
        {
            if (!UiFinder.IsAlive(go))
                return null;
            try
            {
                if (parent != null)
                    go.transform.SetParent(parent, false);
                var le = go.GetComponent<LayoutElement>();
                if (le == null)
                    le = go.AddComponent<LayoutElement>();
                if (le != null)
                {
                    // ⚠️ 必须显式关掉“不参与布局”：原版很多浮层面板（比如控制台的补齐面板）
                    // 是手动定位的，根节点上带着 ignoreLayout = true。
                    // 那种节点搬进布局组里不会被给尺寸 → 自身与子节点全被算成 0，
                    // 文字于是被自动换行断成一条竖线。
                    le.ignoreLayout = false;
                    le.minWidth = -1f;
                    le.preferredWidth = 0f;
                    le.flexibleWidth = 1f;
                    le.minHeight = -1f;
                    le.preferredHeight = 0f;
                    le.flexibleHeight = 1f;
                }
            }
            catch (Exception) { }
            return go;
        }

        /// <summary>一个节点自己布局组的内边距合计（拿不到返回 0）。</summary>
        internal static float PaddingOf(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return 0f;
            try
            {
                var lg = go.GetComponent<LayoutGroup>();
                if (lg == null)
                    return 0f;
                var pad = lg.padding;
                return pad.left + pad.right + pad.top + pad.bottom;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        /// <summary>改一个节点的「高度契约」（preferredHeight），用于拿到实测值后再修正。</summary>
        internal static void SetPreferredHeight(GameObject go, float height)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                var le = go.GetComponent<LayoutElement>();
                if (le == null)
                    le = go.AddComponent<LayoutElement>();
                if (le != null)
                {
                    le.preferredHeight = height;
                    le.flexibleHeight = 0f;
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 关掉节点（含子节点）上的 <c>ContentSizeFitter</c>。
        ///
        /// <para>原版有些面板是「按内容自适应」的（带适配器），它会在每次排版时直接改写自己的尺寸，
        /// **父布局组给的尺寸完全不算** —— 把它塞进我们的对话框里就会得到奇怪的结果（实测
        /// 控制台的补齐面板被适配器按内容撑成 40x1872，直接跑到屏幕外）。
        /// 我们的对话框高度是内容撑的，尺寸必须由外层布局组说了算，所以要把适配器取消。</para>
        /// </summary>
        internal static void DisableSizeFitters(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                var fitters = go.GetComponentsInChildren<ContentSizeFitter>(true);
                if (fitters == null)
                    return;
                for (var i = 0; i < fitters.Length; i++)
                {
                    var f = fitters[i];
                    if (f == null)
                        continue;
                    f.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                    f.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 把节点变成「行容器」：**宽填满父容器**，高度交给子节点。
        ///
        /// <para>宽度必须是「preferred 0 + flexible 1」而不是全部忽略：忽略宽度时该节点会
        /// 直接拿自己布局组算出的内容宽度（= 所有子节点之和），于是嵌在一列里的行会宽到溢出面板
        /// （实测解锁/锁定两个按钮那行宽 732，而它所在的列只有 346 → 锁定按钮跑到框外）。</para>
        /// </summary>
        internal static LayoutElement FitRow(GameObject go)
        {
            var le = Fit(go);
            if (le == null)
                return null;
            try
            {
                le.preferredHeight = -1f;
                le.minHeight = -1f;
                le.flexibleHeight = -1f;
            }
            catch (Exception) { }
            return le;
        }

        /// <summary>把节点变成「等分单元格」：preferred 0 + flexible 1（父布局组按 flex 等分）。</summary>
        internal static LayoutElement Fit(GameObject go, float preferredWidth = 0f)
        {
            if (!UiFinder.IsAlive(go))
                return null;
            try
            {
                var le = go.GetComponent<LayoutElement>();
                if (le == null)
                    le = go.AddComponent<LayoutElement>();
                if (le == null)
                    return null;
                // 原版有些节点带 ignoreLayout（手动定位的浮层）→ 搬进布局组后不会被给尺寸。
                le.ignoreLayout = false;
                le.minWidth = -1f;
                le.preferredWidth = preferredWidth;
                le.flexibleWidth = 1f;
                return le;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("uicontainer-fit", "[Ui] 设置单元格尺寸契约失败：" + e.GetType().Name);
                return null;
            }
        }
    }

    /// <summary>
    /// 一格「标签 + 输入框」。
    ///
    /// <para>为什么不用原版的下拉框：<c>TMP_Dropdown</c> 的展开列表在本环境一个像素都画不出来
    /// （DSHCore 已实测并声明不提供替代实现）。所以「&lt;&gt;」这种"从一组里选一个"的格子改成
    /// **输入框 + 原版控制台那种补齐弹窗**（见 <see cref="UiCompleter"/>）。</para>
    /// </summary>
    internal sealed class UiField
    {
        private static readonly List<UiField> All = new List<UiField>();

        /// <summary>不是本类型但同样要参与「有输入框在编辑」判断的输入框（如进度页的搜索框）。</summary>
        private static readonly List<TMP_InputField> External = new List<TMP_InputField>();

        private readonly GameObject _root;
        private readonly TextMeshProUGUI _label;
        private readonly TMP_InputField _input;
        private readonly List<Action<string>> _submits = new List<Action<string>>();

        private UiField(GameObject root, TextMeshProUGUI label, TMP_InputField input)
        {
            _root = root;
            _label = label;
            _input = input;
            All.Add(this);
        }

        /// <summary>有没有任何输入框正在编辑（面板热键要避开它，否则打字会开关面板）。</summary>
        public static bool AnyFocused
        {
            get
            {
                for (var i = 0; i < All.Count; i++)
                {
                    var f = All[i];
                    if (f == null || !UiFinder.IsAlive(f._input))
                        continue;
                    try
                    {
                        if (f._input.isFocused)
                            return true;
                    }
                    catch (Exception) { }
                }
                for (var i = 0; i < External.Count; i++)
                {
                    var input = External[i];
                    if (!UiFinder.IsAlive(input))
                        continue;
                    try
                    {
                        if (input.isFocused)
                            return true;
                    }
                    catch (Exception) { }
                }
                return false;
            }
        }

        /// <summary>登记一个外部输入框（面板销毁时会一起清掉）。</summary>
        public static void RegisterExternal(TMP_InputField input)
        {
            if (input == null)
                return;
            External.Add(input);
        }

        /// <summary>面板重建时丢掉全部引用。</summary>
        public static void Forget()
        {
            All.Clear();
            External.Clear();
        }

        public GameObject Root { get { return UiFinder.IsAlive(_root) ? _root : null; } }

        public TextMeshProUGUI Label { get { return UiFinder.IsAlive(_label) ? _label : null; } }

        public TMP_InputField Input { get { return UiFinder.IsAlive(_input) ? _input : null; } }

        public bool IsValid { get { return UiFinder.IsAlive(_root) && UiFinder.IsAlive(_input); } }

        /// <summary>创建一个字段格（<paramref name="numeric"/> 为 true 时只允许数字）。</summary>
        public static UiField Create(string name, string labelText, bool numeric = false, bool integer = false)
        {
            var root = UiContainer.Instantiate(null, name);
            if (!UiFinder.IsAlive(root))
                return null;
            UiContainer.FitCell(root, 0f, UiKit.RowHeight);
            // 标签必须从格子左边缘开始（与同列的开关左边缘对齐）。
            UiContainer.FlushHorizontal(root);

            var label = UiKit.Label(root.transform, "Label", labelText);
            var input = UiKit.Input(root.transform, "Input", numeric, integer);
            if (label == null || input == null)
            {
                // 缺素材也不要抛：把半成品交回去，页面上看得见但不好用，
                // 日志里会有素材解析报告说明原因。
                Plugin.LogOnce("uifield-miss", "[Ui] 字段格素材缺失（标签或输入框拿不到）");
            }
            return new UiField(root, label, input);
        }

        public string Text
        {
            get
            {
                var input = Input;
                return input == null ? string.Empty : (input.text ?? string.Empty);
            }
            set
            {
                var input = Input;
                if (input == null)
                    return;
                try { input.SetTextWithoutNotify(value ?? string.Empty); }
                catch (Exception)
                {
                    try { input.text = value ?? string.Empty; }
                    catch (Exception) { }
                }
            }
        }

        public void SetLabel(string text)
        {
            var label = Label;
            if (label == null)
                return;
            try { label.text = text ?? string.Empty; }
            catch (Exception) { }
        }

        /// <summary>值被改动时回调（每次输入都调，用于补齐实时过滤）。</summary>
        public void OnChanged(Action<string> handler)
        {
            var input = Input;
            if (input == null || handler == null)
                return;
            try
            {
                var managed = new System.Action<string>((s) => handler(s ?? string.Empty));
                var il2cpp = DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<string>>(managed);
                UiClone.KeepAlive(_root, managed, il2cpp);
                input.onValueChanged.AddListener(il2cpp);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("uifield-changed", "[Ui] 挂输入回调失败：" + e.GetType().Name);
            }
        }

        /// <summary>提交回调（回车 / 失焦 / 外部调用 <see cref="Commit"/>）。</summary>
        public void OnSubmit(Action<string> handler)
        {
            if (handler == null)
                return;
            _submits.Add(handler);

            var input = Input;
            if (input == null)
                return;
            try
            {
                var managed = new System.Action<string>((s) => handler(s ?? string.Empty));
                var il2cpp = DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<string>>(managed);
                UiClone.KeepAlive(_root, managed, il2cpp);
                input.onSubmit.AddListener(il2cpp);
                input.onEndEdit.AddListener(il2cpp);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("uifield-submit", "[Ui] 挂提交回调失败：" + e.GetType().Name);
            }
        }

        /// <summary>程序化地触发一次提交（与回车/失焦同一条路）。</summary>
        public void Commit()
        {
            var text = Text;
            for (var i = 0; i < _submits.Count; i++)
            {
                try { _submits[i](text); }
                catch (Exception e) { Plugin.LogOnce("uifield-commit", "[Ui] 提交回调异常：" + e.GetType().Name); }
            }
        }

        public bool IsFocused
        {
            get
            {
                var input = Input;
                if (input == null)
                    return false;
                try { return input.isFocused; }
                catch (Exception) { return false; }
            }
        }

        public void Focus()
        {
            var input = Input;
            if (input == null)
                return;
            try { input.ActivateInputField(); }
            catch (Exception) { }
        }

        public int Caret
        {
            get
            {
                var input = Input;
                if (input == null)
                    return 0;
                try { return input.stringPosition; }
                catch (Exception) { return 0; }
            }
        }
    }

    /// <summary>网格的一行：横向容器 + 单元格。</summary>
    internal sealed class UiGridRow
    {
        private readonly GameObject _root;
        private readonly List<GameObject> _cells = new List<GameObject>();
        private readonly bool _equalize;

        private UiGridRow(GameObject root, bool equalize)
        {
            _root = root;
            _equalize = equalize;
        }

        public GameObject Root { get { return UiFinder.IsAlive(_root) ? _root : null; } }

        public int CellCount { get { return _cells.Count; } }

        /// <summary>
        /// 建一行。
        ///
        /// <para><paramref name="equalize"/> = true 时会把单元格做成**等宽列**（5×4 网格与两列布局用）；
        /// false 则靠布局组自己分（每个单元格 preferred 0 + flexible 1 → 自动等分），
        /// 适合「标签 + 搜索框」这种本来就不该等宽的场合。</para>
        /// </summary>
        public static UiGridRow Create(string name, bool equalize = true)
        {
            var root = UiContainer.Instantiate(null, name);
            if (!UiFinder.IsAlive(root))
                return null;
            return new UiGridRow(root, equalize);
        }

        /// <summary>加一个「原版设置项」单元格（复选框 / 滑条 / 按钮）。</summary>
        public UiOptionRow AddOptionRow(string name, UiOptionKind kind)
        {
            var row = UiOptionRow.Create(_root.transform, name, kind);
            if (row == null)
                return null;
            UiContainer.FitCell(row.Root, 0f, UiKit.RowHeight);
            _cells.Add(row.Root);
            RearrangeIfFinished();
            return row;
        }

        /// <summary>加一个「标签 + 输入框」单元格（<paramref name="numeric"/> 为 true 时只收数字）。</summary>
        public UiField AddField(string name, string labelText, bool numeric = false, bool integer = false)
        {
            var field = UiField.Create(name, labelText, numeric, integer);
            if (field == null)
                return null;
            field.Root.transform.SetParent(_root.transform, false);
            UiContainer.FitCell(field.Root, 0f, UiKit.RowHeight);
            _cells.Add(field.Root);
            RearrangeIfFinished();
            return field;
        }

        /// <summary>加一个裸容器（纵向时用来做“一列”）。</summary>
        public GameObject AddContainer(string name, bool vertical)
        {
            var go = UiContainer.Instantiate(_root.transform, name, vertical);
            if (!UiFinder.IsAlive(go))
                return null;
            if (vertical)
                UiContainer.FitCell(go);
            else
                UiContainer.FitRow(go);
            _cells.Add(go);
            RearrangeIfFinished();
            return go;
        }

        /// <summary>
        /// 把一个**现成的节点**当成一格放进本行（宽度弹性、高度由参数定）。
        /// 用途：把克隆出来的原版控件（比如蓝图条）塞进一行里。
        /// </summary>
        public GameObject AddCell(GameObject node, float height)
        {
            if (!UiFinder.IsAlive(node))
                return null;
            try
            {
                node.transform.SetParent(_root.transform, false);
                UiContainer.FitCell(node, 0f, height);
            }
            catch (Exception) { }
            _cells.Add(node);
            RearrangeIfFinished();
            return node;
        }

        /// <summary>
        /// 收尾：本行宽度填满父容器，并登记「等分列」（真的等分要等布局结算后、
        /// 按父容器的**实际宽度**算 —— 见 <see cref="EqualizeNow"/>）。
        /// </summary>
        public GameObject Finish()
        {
            UiContainer.FitRow(_root);
            _finished = true;
            if (_equalize)
                LoadoutPanel.RegisterEqualizeRow(this);
            return Root;
        }

        /// <summary>
        /// 收尾，并把**行高定死**。
        ///
        /// <para>行里放的是「克隆体 + <c>ignoreLayout</c>」时（蓝图条就是这样：它靠拉伸锚点
        /// 自己填满整行，所以布局组必须别管它）布局组拿不到任何子节点的高度 →
        /// 行高会塌成 0。这时用这个收尾，行高由行自己声明。</para>
        /// </summary>
        public GameObject FinishFixed(float height)
        {
            var root = Finish();
            try
            {
                var le = root != null ? root.GetComponent<LayoutElement>() : null;
                if (le == null && root != null)
                    le = root.AddComponent<LayoutElement>();
                if (le != null)
                {
                    le.minHeight = height;
                    le.preferredHeight = height;
                    le.flexibleHeight = 0f;
                }
            }
            catch (Exception) { }
            return root;
        }

        private bool _finished;

        /// <summary>已经 Finish 过的行又被追加了格子（第五行的「重载」就是这样）→ 重新登记。</summary>
        private void RearrangeIfFinished()
        {
            if (_finished && _equalize)
                LoadoutPanel.RegisterEqualizeRow(this);
        }

        /// <summary>
        /// 把本行的单元格做成**等宽**的固定列（需求里的「5*4 网格对齐」要的就是这个）。
        ///
        /// <para><b>为什么不能只靠 <c>flexibleWidth = 1</c>：</b>Unity 的布局组分配剩余空间时，
        /// 是“先按各自的最小宽度占位，再把剩下的按 flexible 分” —— 而格子的最小宽度 = 里面标签需要的最小宽度，
        /// 于是「旗数」那一格与「星之碎片上限」那一格实测差了 53 个单位（145 vs 198），
        /// 四列就此错位，并且与第一、二行的原版开关格子（等分）也对不上。</para>
        ///
        /// <para><b>可用宽度要按“本行的父容器”算，不能用面板宽度</b>：本行可能嵌在某一列里
        /// （进度页那行两个按钮就是），用面板宽度算出来的 346 会让它们溢出那一列。
        /// 父容器的矩形要走完一次布局才有值，所以这个方法由 <see cref="LoadoutPanel"/> 在布局结算后调。</para>
        /// </summary>
        public void EqualizeNow()
        {
            var n = _cells.Count;
            if (n == 0 || !UiFinder.IsAlive(_root))
                return;
            try
            {
                var parent = _root.transform.parent == null
                    ? null
                    : _root.transform.parent.TryCast<RectTransform>();
                var available = parent == null ? 0f : parent.rect.width;
                if (available <= 1f)
                    return;

                // 父容器的内边距不属于本行可用的宽度。
                try
                {
                    var hostLg = parent.GetComponent<LayoutGroup>();
                    if (hostLg != null)
                    {
                        var hostPad = hostLg.padding;
                        available -= hostPad.left + hostPad.right;
                    }
                }
                catch (Exception) { }

                var left = 0f;
                var right = 0f;
                var spacing = 0f;
                var lg = _root.GetComponent<LayoutGroup>();
                if (lg != null)
                {
                    try
                    {
                        var pad = lg.padding;
                        left = pad.left;
                        right = pad.right;
                    }
                    catch (Exception) { }
                    try
                    {
                        var hv = lg.TryCast<HorizontalOrVerticalLayoutGroup>();
                        if (hv != null)
                            spacing = hv.spacing;
                    }
                    catch (Exception) { }
                }

                var share = (available - left - right - spacing * (n - 1)) / n;
                if (share <= 1f)
                    return;

                for (var i = 0; i < n; i++)
                {
                    var le = _cells[i] == null ? null : _cells[i].GetComponent<LayoutElement>();
                    if (le == null)
                        continue;
                    le.minWidth = share;
                    le.preferredWidth = share;
                    le.flexibleWidth = 0f;
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("grid-equalize", "[Ui] 等分单元格失败：" + e.GetType().Name);
            }
        }
    }

    /// <summary>克隆原版素材的小工具（标签 / 输入框 / 文本）。</summary>
    internal static class UiKit
    {
        private static float _rowHeight;
        private static bool _rowHeightResolved;

        /// <summary>
        /// 「一行控件」该多高 —— **从原版素材自己的 RectTransform 高度量出来**，不是写死的数字。
        ///
        /// <para>为什么必须显式给高度：原版控件（<c>OptionWidgets</c>/<c>LabeledToggle</c>）没有
        /// <c>LayoutElement</c>，<c>LayoutUtility.GetPreferredHeight</c> 找不到 <c>ILayoutElement</c>
        /// 时返回 0；原版自己不受影响（它的面板是固定高度，外层布局组有富余空间可分），
        /// 而我们的对话框高度是内容撑的 —— 不给高度就会算出 0、几行叠在一起。</para>
        /// </summary>
        public static float RowHeight
        {
            get
            {
                if (_rowHeightResolved)
                    return _rowHeight;
                _rowHeightResolved = true;
                _rowHeight = TemplateHeight(UiSkinPart.OptionRow);
                if (_rowHeight <= 1f)
                    _rowHeight = TemplateHeight(UiSkinPart.InputField);
                if (_rowHeight <= 1f)
                {
                    // 素材量不到（模板还没被排版过）→ 按画布高度折算，别写死像素。
                    _rowHeight = Mathf.Max(24f, LoadoutPanel.CanvasWidth() * 0.05f);
                    Plugin.LogOnce("uikit-rowheight",
                        "[Ui] 量不到原版行高，改用画布折算值 " + Mathf.RoundToInt(_rowHeight));
                }
                return _rowHeight;
            }
        }

        /// <summary>
        /// 「一行文字占多高」—— 用**原版正文素材**量：给 TMP 一个单字，读它算出来的 preferred 高度。
        ///
        /// <para>不能直接读素材 RectTransform 的高度：那个节点是对话框正文，高度是多行文本块的高度
        /// （实测 72 = 3 行），拿它当行高会把候选列表每页压到 3 行。也不能写死像素：字号/字体改了就不准。</para>
        /// </summary>
        public static float LineHeight
        {
            get
            {
                if (_lineHeightResolved)
                    return _lineHeight;
                _lineHeightResolved = true;
                _lineHeight = 0f;
                try
                {
                    var tpl = UiSkin.Template(UiSkinPart.Text);
                    if (UiFinder.IsAlive(tpl))
                    {
                        var tmp = tpl.GetComponent<TextMeshProUGUI>();
                        if (tmp != null)
                        {
                            var size = tmp.GetPreferredValues("字");
                            if (size.y > 1f)
                                _lineHeight = size.y;
                        }
                    }
                }
                catch (Exception) { }
                if (_lineHeight <= 1f)
                    _lineHeight = LoadoutPanel.LabelFontSize * 1.25f;   // 量不到就按字号折算
                return _lineHeight;
            }
        }

        private static float _lineHeight;
        private static bool _lineHeightResolved;

        /// <summary>素材自己的 RectTransform 高度（原版控件的“出厂尺寸”就在这里）。</summary>
        public static float TemplateHeight(UiSkinPart part)
        {
            try
            {
                var tpl = UiSkin.Template(part);
                if (!UiFinder.IsAlive(tpl))
                    return 0f;
                var rt = tpl.transform.TryCast<RectTransform>();
                if (rt == null)
                    return 0f;
                var h = rt.rect.height;
                if (h > 1f)
                    return h;
                return rt.sizeDelta.y > 1f ? rt.sizeDelta.y : 0f;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        /// <summary>
        /// 「一行控件」里的文字字号 —— 取**原版开关行自己的标签**字号，
        /// 而不是面板自定义的那个（14）。需求要求“同列文字字号一致”，
        /// 而第一、二行是原版控件，字号不归我们管，只能把我们自己的对齐过去。
        ///
        /// <para><b>优先量“已经建出来的开关”</b>：素材在设置对话框里，它那边的字号（14）
        /// 与我们面板里实际渲染出来的（16）不一定一样 —— 而要对齐的是**眼前这些开关**。</para>
        ///
        /// <para>退路是素材自己的 <c>fontSizeMax</c>（自动缩放的 TMP 里 <c>fontSize</c> 只是
        /// “上次算出来的值”，不是设计值）。注意不能直接取素材里第一个 TMP：那一行是「四合一」，
        /// 第一个 TMP 属于**下拉框**那一支（实测 12pt），要找 <c>LabeledToggle</c> 里的那个。</para>
        /// </summary>
        public static float ControlFontSize
        {
            get
            {
                // ① 已经建出来的原版开关（最准）。
                var live = LiveControlFontSize();
                if (live > 0f)
                    return live;

                // ② 素材（只在量不到活体时才用，且缓存）。
                if (_controlFontSize <= 0f)
                    _controlFontSize = TemplateControlFontSize();
                return _controlFontSize;
            }
        }

        /// <summary>面板里已经渲染出来的原版开关标签字号（找不到返回 0）。</summary>
        private static float LiveControlFontSize()
        {
            try
            {
                var content = LoadoutPanel.Content;
                if (content == null)
                    return 0f;
                var toggles = content.GetComponentsInChildren<Toggle>(true);
                if (toggles == null)
                    return 0f;
                for (var i = 0; i < toggles.Length; i++)
                {
                    var t = toggles[i];
                    if (t == null || !t.gameObject.activeInHierarchy)
                        continue;
                    var tmp = t.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (tmp != null && tmp.fontSize > 1f)
                        return tmp.fontSize;
                }
            }
            catch (Exception) { }
            return 0f;
        }

        private static float TemplateControlFontSize()
        {
            var size = LoadoutPanel.LabelFontSize;
            try
            {
                var tpl = UiSkin.Template(UiSkinPart.OptionRow);
                if (UiFinder.IsAlive(tpl))
                {
                    var toggle = UiFinder.FindChild(tpl.transform, "LabeledToggle");
                    var node = toggle == null ? tpl.transform : toggle;
                    var tmp = node.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (tmp != null && tmp.fontSize > 1f)
                        size = tmp.enableAutoSizing && tmp.fontSizeMax > 1f ? tmp.fontSizeMax : tmp.fontSize;
                }
            }
            catch (Exception) { }
            return size;
        }

        private static float _controlFontSize;

        /// <summary>
        /// 标签：克隆对话框正文素材，改字号、关自动换行、**去掉它的 flexibleWidth=1**
        /// （否则它会和同格里的输入框平分剩余宽度，而不是"只占文字那么宽"）。
        ///
        /// <para>TMP 自己实现了 <c>ILayoutElement</c>，所以不加 <c>LayoutElement</c> 时
        /// 它的 preferred 宽度就是文字宽度 —— 这也是这里不去显式量文字的原因。</para>
        /// </summary>
        public static TextMeshProUGUI Label(Transform parent, string name, string text)
        {
            var go = UiSkin.Clone(UiSkinPart.Text, parent, name);
            if (!UiFinder.IsAlive(go))
            {
                Plugin.LogOnce("uikit-label", "[Ui] 拿不到标签素材（Text）");
                return null;
            }
            UiClone.SetFontSize(go, ControlFontSize, false);
            try
            {
                var le = go.GetComponent<LayoutElement>();
                if (le == null)
                    le = go.AddComponent<LayoutElement>();
                if (le != null)
                {
                    // 标签宽度按**实测文本宽度**钉死：不能 flexible（会和同格的输入框抢宽度），
                    // 也不能只给 preferred —— 同格里输入框的 preferred 更大时，
                    // 布局组会把标签压到小于文字宽度（实测「背景」被压成 18/需要 28，
                    // 文字就会压到输入框上），所以 minWidth 也要一起给。
                    var need = 0f;
                    var probe = go.GetComponent<TextMeshProUGUI>();
                    if (probe != null && !string.IsNullOrEmpty(text))
                    {
                        try { need = Mathf.Ceil(probe.GetPreferredValues(text).x) + 1f; }
                        catch (Exception) { need = 0f; }
                    }
                    le.minWidth = need > 0f ? need : -1f;
                    le.preferredWidth = need > 0f ? need : -1f;
                    le.flexibleWidth = 0f;
                }
            }
            catch (Exception) { }

            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
            {
                try { tmp.text = text ?? string.Empty; }
                catch (Exception) { }
            }
            return tmp;
        }

        /// <summary>输入框：克隆「输入名字」对话框的 TMP 输入框。</summary>
        public static TMP_InputField Input(Transform parent, string name, bool numeric, bool integer)
        {
            var go = UiSkin.Clone(UiSkinPart.InputField, parent, name);
            if (!UiFinder.IsAlive(go))
            {
                Plugin.LogOnce("uikit-input", "[Ui] 拿不到输入框素材（InputField）");
                return null;
            }
            var input = go.GetComponent<TMP_InputField>();
            if (input == null)
            {
                Plugin.LogOnce("uikit-input2", "[Ui] 输入框克隆体上没有 TMP_InputField");
                return null;
            }
            try
            {
                // 名字对话框的输入框是给"玩家名"用的，这里要放开限制、改成单行。
                input.characterLimit = 0;
                input.lineType = TMP_InputField.LineType.SingleLine;
                input.contentType = numeric
                    ? (integer ? TMP_InputField.ContentType.IntegerNumber : TMP_InputField.ContentType.DecimalNumber)
                    : TMP_InputField.ContentType.Standard;
                input.restoreOriginalTextOnEscape = true;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("uikit-input3", "[Ui] 配置输入框失败：" + e.GetType().Name);
            }
            // 输入框吃掉本格剩余宽度。
            UiContainer.FitCell(go, 0f, RowHeight);
            try
            {
                var le = go.GetComponent<LayoutElement>();
                if (le != null)
                {
                    le.preferredWidth = 0f;
                    le.minWidth = -1f;
                    le.flexibleWidth = 1f;
                }
            }
            catch (Exception) { }
            return input;
        }

        /// <summary>一段普通文本（正文/说明），可以指定字号。</summary>
        public static TextMeshProUGUI Text(Transform parent, string name, string text, float fontSize)
        {
            var go = UiSkin.Clone(UiSkinPart.Text, parent, name);
            if (!UiFinder.IsAlive(go))
                return null;
            UiClone.SetFontSize(go, fontSize <= 0f ? LoadoutPanel.LabelFontSize : fontSize);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
            {
                try { tmp.text = text ?? string.Empty; }
                catch (Exception) { }
            }
            return tmp;
        }

        /// <summary>把节点设成"占满一行宽度"（内容行用）。</summary>
        public static void FullWidth(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                var le = go.GetComponent<LayoutElement>();
                if (le == null)
                    le = go.AddComponent<LayoutElement>();
                if (le == null)
                    return;
                le.minWidth = -1f;
                le.preferredWidth = 0f;
                le.flexibleWidth = 1f;
            }
            catch (Exception) { }
        }
    }
}
