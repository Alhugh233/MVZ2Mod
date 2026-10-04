using System;
using System.Collections.Generic;
using System.Text;
using DSHCore.Ui;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace UiGallery
{
    /// <summary>
    /// 画廊面板本体。**它的全部视觉都是原版 prefab 的克隆体**：框体、标题、正文、按钮行
    /// 全部来自官方 <c>CustomDialog</c>，导航按钮由 <c>CustomDialog.SetDialog</c> 自己铺。
    ///
    /// <para>本文件是「用 DSHCore.Ui 做一个面板需要写多少代码」的答案。刻意不出现任何
    /// 尺寸/坐标常量 —— 内容行直接塞进官方对话框自己的 <c>VerticalLayoutGroup</c>。</para>
    /// </summary>
    internal static class GalleryPanel
    {
        public const string RootName = "DSH_UiGallery";

        /// <summary>
        /// 本画廊自己的**标题**字号。
        ///
        /// <para>素材（<c>UiSkinPart.Title</c>）出厂是 36pt + 18~36 自动缩放，那是给
        /// "一行短标题"用的；画廊每页的页号与说明都挤在标题下面，所以这里显式降下来。
        /// 注意：本画廊降的是**自己面板上的文本**，没有改 <c>UiSkin</c> 的素材
        /// —— 别的 mod 拿到的依旧是原版字号。</para>
        /// </summary>
        public const float TitleFontSize = 22f;

        /// <summary>
        /// 本画廊自己的**正文**字号（页说明、示例文字、报告）。
        ///
        /// <para>对话框正文素材出厂是 24pt，原版拿它放一两行提示；画廊要放整页说明性文字，
        /// 24pt 两行就把面板顶出屏幕，所以降下来。同样只改**本面板**。</para>
        /// </summary>
        public const float BodyFontSize = 14f;

        private static GameObject _root;
        private static MVZ2.UI.CustomDialog _dialog;
        private static RectTransform _rootLayout;    // CustomDialog/Root（带 VerticalLayoutGroup）
        private static RectTransform _buttonsNode;   // CustomDialog/Root/Buttons（官方按钮行容器）

        private static readonly List<GameObject> Rows = new List<GameObject>();
        private static int _page = -1;
        private static string _lastError;

        // 导航回调必须静态持有：IL2CPP 侧只存裸函数指针，托管委托被回收就会崩。
        private static System.Action<int> _navManaged;
        private static Il2CppSystem.Action<int> _navIl2Cpp;

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

        public static string CurrentPageName
        {
            get
            {
                if (_page < 0 || _page >= GalleryPages.All.Length)
                    return "（未选页）";
                return GalleryPages.All[_page].Name;
            }
        }

        // ------------------------------------------------------------------
        // 对外操作
        // ------------------------------------------------------------------

        public static bool Open()
        {
            if (!Build())
                return false;

            try
            {
                _root.SetActive(true);
                if (_page < 0)
                    ShowPage(0);
                UiHost.MarkOwn(_root);
                return true;
            }
            catch (Exception e)
            {
                _lastError = "激活面板失败：" + e.GetType().Name + ": " + e.Message;
                Plugin.Log?.LogWarning("[Gallery] " + _lastError);
                return false;
            }
        }

        public static void Close()
        {
            UiTooltip.Hide();
            if (!UiFinder.IsAlive(_root))
                return;
            try { _root.SetActive(false); }
            catch (Exception) { }
        }

        public static void Toggle()
        {
            if (IsOpen)
                Close();
            else
                Open();
        }

        /// <summary>丢掉现有面板并强制重新解析素材（切场景后素材失效时用）。</summary>
        public static void Rebuild()
        {
            DestroyPanel();
            UiSkin.Prepare(true);
        }

        public static bool ShowPage(int index)
        {
            if (index < 0 || index >= GalleryPages.All.Length)
                return false;
            if (!Build())
                return false;

            try
            {
                ClearRows();
                _page = index;

                var page = GalleryPages.All[index];

                // 标题 / 正文 / 导航按钮全部交给官方对话框自己 —— 这就是原版的
                // 「标题 + 正文 + 若干按钮」契约，连排版都不用管。
                //
                // ⚠️ 标题保持**短**：标题节点是 36pt + 自动缩放，而 TitleArea 固定只有 40px 高，
                //    所以它只能占一行（长了就缩小，缩到 18pt 还不合就往上溢出框体）。
                //    页面信息因此放到正文里。
                Plugin.Log?.LogInfo("[Gallery] 按钮行模板体检：\n" + RowTemplateDiagnostic());

                _dialog.SetDialog(
                    "UI 组件画廊",
                    "第 " + (index + 1) + "/" + GalleryPages.All.Length + " 页 · " + page.Name
                        + "\n" + page.Description,
                    GalleryPages.NavLabels(),
                    NavAction());

                // 标题/正文字号：**本画廊自己的选择**（原版素材是 36 / 24，那是给短提示用的）。
                // 必须在 SetDialog 之后 —— SetLabel 那类方法会写文本，这里只改字号，不动文案。
                UiClone.SetFontSize(_dialog.title == null ? null : _dialog.title.gameObject, TitleFontSize);
                UiClone.SetFontSize(_dialog.desc == null ? null : _dialog.desc.gameObject, BodyFontSize);

                page.Build(AddRow);
                return true;
            }
            catch (Exception e)
            {
                _lastError = "装配第 " + (index + 1) + " 页失败：" + e.GetType().Name + ": " + e.Message;
                Plugin.Log?.LogWarning("[Gallery] " + _lastError);
                return false;
            }
        }

        public static string Report()
        {
            var sb = new StringBuilder(900);

            sb.Append("面板：").Append(UiFinder.IsAlive(_root)
                ? UiFinder.PathOf(_root.transform) + "（" + (IsOpen ? "已显示" : "已隐藏") + "）"
                : "未构建").Append('\n');

            sb.Append("对话框组件：").Append(UiFinder.IsAlive(_dialog) ? "OK" : "无").Append('\n');
            sb.Append("内容宿主：").Append(_rootLayout == null ? "无" : UiFinder.PathOf(_rootLayout)).Append('\n');
            sb.Append("按钮容器：").Append(_buttonsNode == null ? "无" : UiFinder.PathOf(_buttonsNode)).Append('\n');
            sb.Append("当前页：").Append(CurrentPageName).Append("（第 ").Append(_page + 1)
              .Append(" / ").Append(GalleryPages.All.Length).Append(" 页）  内容行数：").Append(Rows.Count).Append('\n');
            sb.Append("保活委托：").Append(UiClone.KeepAliveCount).Append('\n');

            if (!string.IsNullOrEmpty(_lastError))
                sb.Append("最近错误：").Append(_lastError).Append('\n');

            sb.Append('\n').Append(UiSkin.Report);
            return sb.ToString();
        }

        /// <summary>
        /// 当前页里**真正活着**的那个下拉框（自测探针用）。找不到返回 null。
        ///
        /// <para>为什么必须挑活着的：每个设置项行都是官方 <c>OptionWidgets</c> 的克隆，
        /// 里面**本来就含一个 <c>TMP_Dropdown</c>**（只是被 <c>SetKind</c> 关掉了）。
        /// 直接取“第一个 TMP_Dropdown”会拿到那个关着的空壳 —— 于是探针会去展开一个
        /// 不存在的下拉框（<c>Show()</c> 在 <c>!IsActive()</c> 时直接返回），
        /// 得出“弹窗画不出来”的假结论。</para>
        ///
        /// <para>也不去“找场景里第一个 TMP_Dropdown” —— 那会摸到主菜单里原版的设置界面。</para>
        /// </summary>
        public static TMPro.TMP_Dropdown CurrentDropdown()
        {
            TMPro.TMP_Dropdown fallback = null;
            for (var i = 0; i < Rows.Count; i++)
            {
                var dd = UiDropdown.Of(Rows[i]);
                if (dd == null)
                    continue;
                if (dd.gameObject.activeInHierarchy)
                    return dd;
                if (fallback == null)
                    fallback = dd;
            }
            return fallback;
        }

        /// <summary>
        /// 「按钮行模板」体检：把 <c>_dialog.buttonRowList</c> 与它的模板、以及模板里那个
        /// <c>ButtonRow.buttonList</c> 的模板都打出来。
        ///
        /// <para>存在的理由：<c>CustomDialog.SetDialog</c> 内部会
        /// <c>Instantiate</c> 模板行，而 <c>ElementList.Awake</c> 第一件事就是访问
        /// <c>_template.transform</c>。模板一旦为空/已销毁，这里就会抛
        /// <c>NullReferenceException</c>（表现为 <c>Player.log</c> 里
        /// <c>ElementList.Awake ← Instantiate</c> 的一行）。所以把这两层模板的
        /// 「在哪 / 还活着吗 / 是不是本克隆体的子节点」先记下来，出问题时一眼能定位。</para>
        /// </summary>
        internal static string RowTemplateDiagnostic()
        {
            var sb = new StringBuilder(400);
            try
            {
                if (!UiFinder.IsAlive(_dialog))
                {
                    sb.Append("  对话框：无");
                    return sb.ToString();
                }

                var rows = _dialog.buttonRowList;
                sb.Append("  buttonRowList：").Append(UiFinder.IsAlive(rows)
                    ? UiFinder.PathOf(rows.transform) : "无").Append('\n');

                if (!UiFinder.IsAlive(rows))
                    return sb.ToString();

                var rowTpl = rows.getTemplate();
                sb.Append("    行模板：").Append(UiFinder.IsAlive(rowTpl)
                    ? UiFinder.PathOf(rowTpl.transform) : "不可用（null/已销毁）");
                if (UiFinder.IsAlive(rowTpl) && UiFinder.IsAlive(_root))
                    sb.Append("  属于本面板=").Append(rowTpl.transform.IsChildOf(_root.transform) ? "是" : "否");
                sb.Append('\n');

                if (!UiFinder.IsAlive(rowTpl))
                    return sb.ToString();

                var row = rowTpl.GetComponent<MVZ2.UI.ButtonRow>();
                sb.Append("    行模板上的 ButtonRow：").Append(row != null ? "有" : "无").Append('\n');
                if (row == null)
                    return sb.ToString();

                var list = row.buttonList;
                sb.Append("      buttonList：").Append(UiFinder.IsAlive(list)
                    ? UiFinder.PathOf(list.transform) : "无").Append('\n');
                if (!UiFinder.IsAlive(list))
                    return sb.ToString();

                var btnTpl = list.getTemplate();
                sb.Append("        按钮模板：").Append(UiFinder.IsAlive(btnTpl)
                    ? UiFinder.PathOf(btnTpl.transform) : "不可用（null/已销毁）");
                if (UiFinder.IsAlive(btnTpl) && UiFinder.IsAlive(rowTpl))
                    sb.Append("  属于模板行=").Append(btnTpl.transform.IsChildOf(rowTpl.transform) ? "是" : "否");
                sb.Append('\n');
            }
            catch (Exception e)
            {
                sb.Append("  体检失败：").Append(e.GetType().Name).Append(": ").Append(e.Message);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 运行时布局 dump：把面板与每个内容行的**屏幕矩形**打出来。
        ///
        /// <para>为什么需要它：日志里"节点都建出来了"和"屏幕上看得见"是两件事，
        /// 而截图在 IL2CPP 下可能落空白帧 —— 用矩形可以把这件事变成一个确定的数字：
        /// 面板是不是在屏幕内、行与行是不是拉开了（y 不重叠）、行宽是不是铺满了父节点。</para>
        ///
        /// <para>屏幕坐标一律走「局部角点 → 世界 → 屏幕」，不要用
        /// <c>rect.size * canvas.scaleFactor</c>：祖先链上的 <c>localScale</c> 会算漏
        /// （本仓库踩过，见 UI_RESEARCH.md §6）。</para>
        /// </summary>
        public static string LayoutDump()
        {
            var sb = new StringBuilder(1400);
            try
            {
                sb.Append("屏幕 ").Append(Screen.width).Append('x').Append(Screen.height).Append('\n');
                AppendRect(sb, "面板", _root, null);
                // 框体美术是 Root/Background（在内容宿主那一层里，不是对话框根的直接子节点）。
                AppendRect(sb, "框体", _rootLayout == null ? null : ChildByName(_rootLayout.gameObject, "Background"), null);
                AppendRect(sb, "内容宿主", _rootLayout == null ? null : _rootLayout.gameObject, null);
                AppendRect(sb, "按钮区", _buttonsNode == null ? null : _buttonsNode.gameObject, null);

                // 标题单独打：标题溢出框体时的唯一线索就是它的字号与自动缩放开关。
                AppendRect(sb, "标题", _dialog == null ? null : FindTitleNode(), DescribeTextOnly());
                // 正文也打：它是我们显式改过字号的节点（原版素材 24 → 本面板 14）。
                AppendRect(sb, "正文", FindDescNode(), DescribeText(FindDescNode()));

                for (var i = 0; i < Rows.Count; i++)
                    AppendRect(sb, "  行" + (i + 1), Rows[i], DescribeRow(Rows[i]));
            }
            catch (Exception e)
            {
                sb.Append("布局 dump 失败：").Append(e.GetType().Name).Append('\n');
            }
            return sb.ToString();
        }

        private static GameObject FindTitleNode()
        {
            try
            {
                var t = _dialog.title;
                return UiFinder.IsAlive(t) ? t.gameObject : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static GameObject FindDescNode()
        {
            try
            {
                var t = _dialog.desc;
                return UiFinder.IsAlive(t) ? t.gameObject : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>找直接子节点（用来把「框体美术」的矩形也打出来，好判断标题有没有跑出框）。</summary>
        private static GameObject ChildByName(GameObject parent, string name)
        {
            if (!UiFinder.IsAlive(parent))
                return null;
            try
            {
                var t = parent.transform.Find(name);
                return t == null ? null : t.gameObject;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>行的附加信息：提示锚点落在屏幕哪一点 + 行里哪个控件开着、它的标签字号多少。</summary>
        private static string DescribeRow(GameObject row)
        {
            if (!UiFinder.IsAlive(row))
                return null;
            try
            {
                var sb = new StringBuilder(240);
                var widgets = row.GetComponent<MVZ2.UI.OptionWidgets>();
                GameObject active = null;
                if (widgets != null)
                {
                    AppendSub(sb, "tgl", widgets.Toggle == null ? null : widgets.Toggle.gameObject);
                    AppendSub(sb, "sld", widgets.Slider == null ? null : widgets.Slider.gameObject);
                    AppendSub(sb, "dd", widgets.Dropdown == null ? null : widgets.Dropdown.gameObject);
                    AppendSub(sb, "btn", widgets.TextButton == null ? null : widgets.TextButton.gameObject);
                    active = ActiveChild(widgets);
                }

                var tip = UiTooltip.Describe(row);
                if (!string.IsNullOrEmpty(tip) && tip != "无 TooltipHandler" && tip != "（无节点）")
                    sb.Append("  tip[").Append(tip).Append(']');

                // 标签字号要从**开着的那个控件**里读。整行 GetComponentInChildren 找到的
                // 往往是别的（关着的）节点，会得出误导性的字号。
                var label = DescribeText(active);
                if (!string.IsNullOrEmpty(label))
                    sb.Append("  label[").Append(label).Append(']');
                else
                {
                    var any = DescribeText(row);
                    if (!string.IsNullOrEmpty(any))
                        sb.Append("  text[").Append(any).Append(']');
                }

                return sb.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static GameObject ActiveChild(MVZ2.UI.OptionWidgets w)
        {
            if (w.Toggle != null && w.Toggle.gameObject.activeSelf)
                return w.Toggle.gameObject;
            if (w.Slider != null && w.Slider.gameObject.activeSelf)
                return w.Slider.gameObject;
            if (w.Dropdown != null && w.Dropdown.gameObject.activeSelf)
                return w.Dropdown.gameObject;
            if (w.TextButton != null && w.TextButton.gameObject.activeSelf)
                return w.TextButton.gameObject;
            return null;
        }

        private static void AppendSub(StringBuilder sb, string name, GameObject go)
        {
            sb.Append(' ').Append(name).Append('=');
            if (!UiFinder.IsAlive(go))
            {
                sb.Append("null");
                return;
            }
            try
            {
                var rt = go.GetComponent<RectTransform>();
                var r = rt == null ? default(Rect) : rt.rect;
                sb.Append(go.activeSelf ? "on" : "off");
                sb.Append('(').Append(Mathf.RoundToInt(r.width)).Append('x').Append(Mathf.RoundToInt(r.height)).Append(')');
            }
            catch (Exception)
            {
                sb.Append('?');
            }
        }

        /// <summary>节点子树里第一个 TMP 的字号、自动缩放开关与**文本内容**。</summary>
        private static string DescribeText(GameObject node)
        {
            if (!UiFinder.IsAlive(node))
                return null;
            try
            {
                var t = node.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                if (t == null)
                    return null;
                var text = t.text ?? string.Empty;
                if (text.Length > 24)
                    text = text.Substring(0, 24) + "…";
                return "fs=" + Mathf.RoundToInt(t.fontSize)
                     + " auto=" + (t.enableAutoSizing ? 1 : 0)
                     + " wrap=" + (t.enableWordWrapping ? 1 : 0)
                     + " 「" + text + "」";
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string DescribeTextOnly()
        {
            var go = FindTitleNode();
            return go == null ? null : DescribeText(go);
        }

        private static void AppendRect(StringBuilder sb, string label, GameObject go, string extra)
        {
            if (!UiFinder.IsAlive(go))
            {
                sb.Append("  ").Append(label).Append("：（无）\n");
                return;
            }

            try
            {
                var rt = go.GetComponent<RectTransform>();
                if (rt == null)
                {
                    sb.Append("  ").Append(label).Append("：（无 RectTransform）\n");
                    return;
                }

                var canvas = go.GetComponentInParent<Canvas>();
                var cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null
                    : canvas.worldCamera;

                var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
                rt.GetWorldCorners(corners);
                var min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                var max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);

                var w = Mathf.RoundToInt(Mathf.Abs(max.x - min.x));
                var h = Mathf.RoundToInt(Mathf.Abs(max.y - min.y));

                sb.Append("  ").Append(label).Append("：");
                sb.Append("x").Append(Mathf.RoundToInt(Mathf.Min(min.x, max.x)));
                sb.Append(" y").Append(Mathf.RoundToInt(Mathf.Min(min.y, max.y)));
                sb.Append("  ").Append(w).Append('x').Append(h);
                sb.Append("  可见=").Append(rt.gameObject.activeInHierarchy ? "1" : "0");
                if (!string.IsNullOrEmpty(extra))
                    sb.Append(' ').Append(extra);
                sb.Append('\n');
            }
            catch (Exception e)
            {
                sb.Append("  ").Append(label).Append("：读矩形失败（").Append(e.GetType().Name).Append("）\n");
            }
        }

        // ------------------------------------------------------------------
        // 构建
        // ------------------------------------------------------------------

        private static bool Build()
        {
            if (UiFinder.IsAlive(_root) && UiFinder.IsAlive(_dialog) && _rootLayout != null && _buttonsNode != null)
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

            _dialog = _root.GetComponent<MVZ2.UI.CustomDialog>();
            if (_dialog == null)
            {
                _lastError = "克隆体上没有 CustomDialog 组件。";
                DestroyPanel();
                return false;
            }

            try
            {
                // 官方按钮行容器 = CustomDialog.buttonRowList 的列表根；
                // 它的父节点就是对话框的布局根（带 VerticalLayoutGroup）。
                var buttons = _dialog.buttonRowList;
                if (buttons == null || buttons._listRoot == null)
                {
                    _lastError = "拿不到对话框的按钮容器（buttonRowList / _listRoot）。";
                    DestroyPanel();
                    return false;
                }
                _buttonsNode = buttons._listRoot;
                // parent 是 Transform，布局根要用 RectTransform（同一个对象，只是代理类型不同）。
                _rootLayout = _buttonsNode.parent == null
                    ? null
                    : _buttonsNode.parent.TryCast<RectTransform>();
            }
            catch (Exception e)
            {
                _lastError = "读对话框布局节点失败：" + e.GetType().Name + ": " + e.Message;
                DestroyPanel();
                return false;
            }

            if (_rootLayout == null)
            {
                _lastError = "对话框布局根为空。";
                DestroyPanel();
                return false;
            }

            // 先建好不显示：Open() 才 SetActive(true)。
            try { _root.SetActive(false); }
            catch (Exception) { }

            Plugin.Log?.LogInfo("[Gallery] 面板已构建：" + UiFinder.PathOf(_root.transform));
            return true;
        }

        private static void DestroyPanel()
        {
            ClearRows();
            UiTooltip.Hide();
            _dialog = null;
            _rootLayout = null;
            _buttonsNode = null;
            _page = -1;

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
        /// 把一行塞进对话框自己的 VerticalLayoutGroup，位置在官方按钮区**之前**
        /// （与原版「标题 / 正文 / 按钮」的顺序一致）。
        ///
        /// <para>注意这里没有建任何容器节点：<b>新建的容器没有 LayoutGroup，子节点会全叠在
        /// 同一个 100×100 的位置上</b>（详见 UI_RESEARCH.md §6-⑩）。直接用官方那个排好版的
        /// 根节点，代价只是维护一份"本次插了哪些行"的清单。</para>
        /// </summary>
        private static void AddRow(GameObject row)
        {
            if (!UiFinder.IsAlive(row) || _rootLayout == null)
                return;
            try
            {
                row.transform.SetParent(_rootLayout, false);
                if (_buttonsNode != null)
                    row.transform.SetSiblingIndex(_buttonsNode.GetSiblingIndex());
                Rows.Add(row);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Gallery] 插入内容行失败：" + e.GetType().Name);
            }
        }

        private static void ClearRows()
        {
            UiTooltip.Hide();
            for (var i = 0; i < Rows.Count; i++)
            {
                if (!UiFinder.IsAlive(Rows[i]))
                    continue;
                try { UnityEngine.Object.Destroy(Rows[i]); }
                catch (Exception) { }
            }
            Rows.Clear();
            UiClone.PruneKeepAlive();
        }

        // ------------------------------------------------------------------
        // 导航
        // ------------------------------------------------------------------

        private static Il2CppSystem.Action<int> NavAction()
        {
            if (_navIl2Cpp != null)
                return _navIl2Cpp;
            _navManaged = new System.Action<int>(OnNavSelected);
            _navIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>(_navManaged);
            // 属主是面板根，面板销毁后由 PruneKeepAlive 回收。
            UiClone.KeepAlive(_root, _navManaged, _navIl2Cpp);
            return _navIl2Cpp;
        }

        private static void OnNavSelected(int index)
        {
            // 导航只有三个按钮：上一页 / 下一页 / 关闭。
            // 不做「每页一个按钮」：那会把对话框的按钮区撑成好几行，
            // 整个框体跟着长高，很容易超出屏幕（实测 9 个按钮 = 305px）。
            switch (index)
            {
                case 0:
                    ShowPage(Wrap(_page - 1));
                    return;
                case 1:
                    ShowPage(Wrap(_page + 1));
                    return;
                default:
                    Close();
                    return;
            }
        }

        private static int Wrap(int index)
        {
            var n = GalleryPages.All.Length;
            return ((index % n) + n) % n;
        }
    }
}
