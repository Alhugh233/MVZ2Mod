using System;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.UI;

namespace DSHCore.Ui
{
    /// <summary>
    /// **纯代码可滚动列表**。
    ///
    /// <para><b>为什么不能直接用官方的 <c>ElementList</c> / <c>ElementListUI</c>：</b>
    /// 它们依赖私有 <c>[SerializeField]</c> 的模板引用，<c>AddComponent</c> 出来是空的；
    /// 而官方那套列表的"模板"又藏在 prefab 里，mod 侧拿不到。所以列表只能自己拼 ——
    /// 但**不需要自己算坐标**：<see cref="UiSkinPart.ScrollView"/> 克隆来的容器里
    /// <c>Content</c> 自带 <c>VerticalLayoutGroup</c>，把行塞进去就行。</para>
    ///
    /// <para><b>唯一的"尺寸"问题，以及为什么它不可避免：</b>滚动视图必须有**有界高度**，
    /// 否则它就没有可滚动余量。而我们的对话框壳高度是 <c>ContentSizeFitter</c> 撑的
    /// （= 内容多高它就多高），把滚动视图放进去会得到 0 高。所以必须有一个地方决定视口高度：</para>
    /// <list type="bullet">
    /// <item>父容器**本身有确定高度**（例如占满屏幕的面板）→ 用 <see cref="SetFlexible"/>，
    /// 让 <c>LayoutElement.flexibleHeight = 1</c> 去吃掉剩余空间（这是布局组协议，不是硬编码尺寸）。</item>
    /// <item>父容器是 <c>ContentSizeFitter</c>（本库的对话框壳就是这样）→ 用
    /// <see cref="SetViewportHeight"/>，高度建议由 <see cref="SuggestHeight"/> 按**画布高度**
    /// 折算，而不是写一个魔术数字。</item>
    /// </list>
    ///
    /// <para><b>用法：</b></para>
    /// <code>
    /// var list = UiList.Create(root, "MyList");
    /// list.SetViewportHeight(UiList.SuggestHeight());
    /// list.AddButton("第一项", () => { });
    /// list.AddButton("第二项", () => { });
    /// </code>
    /// </summary>
    public sealed class UiList
    {
        private readonly GameObject _root;
        private readonly ScrollRect _scroll;
        private readonly RectTransform _content;

        private UiList(GameObject root, ScrollRect scroll, RectTransform content)
        {
            _root = root;
            _scroll = scroll;
            _content = content;
        }

        /// <summary>列表根节点（克隆来的可滚动容器）。挂 <c>LayoutElement</c> 的就是它。</summary>
        public GameObject Root
        {
            get { return UiFinder.IsAlive(_root) ? _root : null; }
        }

        /// <summary>内部的 <c>ScrollRect</c>（要调灵敏度 / 惯性时用）。</summary>
        public ScrollRect Scroll
        {
            get { return UiFinder.IsAlive(_scroll) ? _scroll : null; }
        }

        /// <summary>内容容器（带 <c>VerticalLayoutGroup</c>）。把行挂到这里。</summary>
        public RectTransform Content
        {
            get { return _content != null ? _content : null; }
        }

        public bool IsValid
        {
            get { return UiFinder.IsAlive(_root) && UiFinder.IsAlive(_scroll) && _content != null; }
        }

        /// <summary>当前行数。</summary>
        public int Count
        {
            get
            {
                try { return _content == null ? 0 : _content.childCount; }
                catch (Exception) { return 0; }
            }
        }

        /// <summary>
        /// 造一个列表。<paramref name="parent"/> 为空时不挂父节点（调用方自己
        /// <c>SetParent</c>，跟 <see cref="UiSkin.Clone"/> 的约定一致）。
        /// </summary>
        public static UiList Create(Transform parent, string name = null)
        {
            var go = UiSkin.Clone(UiSkinPart.ScrollView, parent, name);
            if (!UiFinder.IsAlive(go))
            {
                UiLog.WarnOnce("list-miss",
                    "拿不到可滚动容器素材 —— 它来自主菜单的设置界面，进主菜单后才会就绪。");
                return null;
            }

            var sr = go.GetComponent<ScrollRect>();
            if (sr == null)
                sr = go.GetComponentInChildren<ScrollRect>(true);

            var content = UiSkin.FindScrollContent(sr);
            if (sr == null || content == null)
            {
                UiLog.WarnOnce("list-miss2", "可滚动容器克隆体里没有 ScrollRect 或 Content。");
                return null;
            }

            var list = new UiList(go, sr, content);

            // 默认横向铺满父容器。**这一步不能省**：外层布局组（本库的对话框 Root）是
            // `forceExpandWidth = 0`，不给 flexibleWidth 就会算出 0 宽 —— 实测过：
            // 视口 0x297、每一行 0x40，看着“行数对、位置对”，实际一个像素都看不到。
            list.SetStretch();
            return list;
        }

        // ------------------------------------------------------------------
        // 尺寸
        // ------------------------------------------------------------------

        /// <summary>
        /// 横向铺满父容器（默认开）。父布局组是 <c>forceExpandWidth = 0</c> 时不铺满就是 0 宽。
        /// </summary>
        public UiList SetStretch(bool stretch = true)
        {
            return ApplyLayoutElement(le =>
            {
                le.flexibleWidth = stretch ? 1f : -1f;
            });
        }

        /// <summary>让列表吃掉父容器布局组里的剩余高度（父容器必须自己是有界的）。</summary>
        public UiList SetFlexible(bool flexible = true)
        {
            return ApplyLayoutElement(le =>
            {
                le.flexibleHeight = flexible ? 1f : -1f;
            });
        }

        /// <summary>
        /// 给视口一个确定高度（<c>ContentSizeFitter</c> 类父容器下必须这么做）。
        /// </summary>
        public UiList SetViewportHeight(float pixels)
        {
            if (pixels <= 0f)
                return this;
            return ApplyLayoutElement(le =>
            {
                le.preferredHeight = pixels;
                le.minHeight = -1f;
                le.flexibleHeight = -1f;
            });
        }

        /// <summary>
        /// 按主画布高度折算一个建议视口高度。
        ///
        /// <para>存在的意义是**避免魔术数字**：写 <c>420f</c> 在任何分辨率下都可能不合适，
        /// 而"画布高度的 60%"是跟着游戏自己缩放的。想要别的比例就传
        /// <paramref name="fraction"/>（0.3~0.8 比较合理）。</para>
        /// </summary>
        public static float SuggestHeight(float fraction = 0.6f)
        {
            try
            {
                var canvas = UiGame.MainCanvas;
                if (canvas != null)
                {
                    var rt = canvas.GetComponent<RectTransform>();
                    if (rt != null && rt.rect.height > 1f)
                        return rt.rect.height * Mathf.Clamp(fraction, 0.1f, 0.95f);
                }
            }
            catch (Exception) { }

            // 拿不到画布就退回屏幕高度（Overlay 下与画布高度同量级）
            return Mathf.Max(120f, Screen.height * Mathf.Clamp(fraction, 0.1f, 0.95f));
        }

        /// <summary>
        /// 把列表的**行排布**调成「紧凑 + 铺满内容框」。
        ///
        /// <para>三件事，都是原版留给「条目又多又长」的列表的余量，我们的列表用不上：</para>
        /// <list type="number">
        /// <item><b>行间距</b>（<c>Content</c> 纵向布局组的 <c>spacing</c>）—— 原版素材是 10 单位，
        /// 行与行之间空出一大截；</item>
        /// <item><b>内距与铺满</b>：清掉 <c>Content</c> 的内距，并让行强制铺满宽度；</item>
        /// <item><b>内容容器自己的水平内缩</b> —— 原版把 <c>Content</c> 摆得比视口窄一圈
        /// （实测窄 12 屏幕像素），行因此看起来**没填满框体**。</item>
        /// </list>
        /// </summary>
        /// <param name="spacing">行间距（单位）；&lt; 0 = 不改。</param>
        public UiList SetRowFit(bool stretch = true, float spacing = -1f)
        {
            if (!IsValid)
                return this;
            try
            {
                var v = _content.GetComponent<VerticalLayoutGroup>();
                if (v != null)
                {
                    if (spacing >= 0f)
                        v.spacing = spacing;
                    v.childControlWidth = true;
                    v.childForceExpandWidth = stretch;
                    v.childControlHeight = true;
                    v.childForceExpandHeight = false;
                    if (v.padding != null)
                        v.padding = new RectOffset();
                }

                // 横向拉伸锚点下 sizeDelta.x 就是「比视口窄多少」→ 清 0 让它铺满。
                if (stretch && !Mathf.Approximately(_content.anchorMin.x, _content.anchorMax.x))
                {
                    var sd = _content.sizeDelta;
                    _content.sizeDelta = new Vector2(0f, sd.y);
                }
                Rebuild();
            }
            catch (Exception e)
            {
                UiLog.Warn("调整行排布失败：" + e.GetType().Name + ": " + e.Message);
            }
            return this;
        }

        private UiList ApplyLayoutElement(Action<LayoutElement> apply)
        {
            if (!IsValid)
                return this;
            try
            {
                var le = _root.GetComponent<LayoutElement>();
                if (le == null)
                    le = _root.AddComponent<LayoutElement>();
                if (le != null)
                    apply(le);
            }
            catch (Exception e)
            {
                UiLog.Warn("设置列表尺寸失败：" + e.GetType().Name + ": " + e.Message);
            }
            return this;
        }

        // ------------------------------------------------------------------
        // 内容
        // ------------------------------------------------------------------

        /// <summary>
        /// 把一个现成的节点加为一行。**由调用方负责造节点**（一般用
        /// <see cref="UiSkin.Clone"/> + <see cref="UiOptionRow"/>），本方法只负责挂载与重排。
        /// </summary>
        public GameObject Add(GameObject item)
        {
            if (!IsValid || !UiFinder.IsAlive(item))
                return null;
            try
            {
                item.transform.SetParent(_content, false);
                item.SetActive(true);

                // 行也要横向铺满：Content 的布局组同样不 forceExpand，
                // 没给宽度信息的行会被算成 0 宽。已经有 LayoutElement 的行**不动**
                // （那是调用方自己的契约，比如 UiOptionRow.SetStretch）。
                if (item.GetComponent<LayoutElement>() == null)
                {
                    var le = item.AddComponent<LayoutElement>();
                    if (le != null)
                        le.flexibleWidth = 1f;
                }

                Rebuild();
            }
            catch (Exception e)
            {
                UiLog.Warn("往列表加行失败：" + e.GetType().Name + ": " + e.Message);
                return null;
            }
            return item;
        }

        /// <summary>用工厂函数造一行再加进去（工厂拿到的就是 Content 的 Transform）。</summary>
        public GameObject Add(Func<Transform, GameObject> factory)
        {
            if (factory == null)
                return null;
            return Add(factory(_content));
        }

        /// <summary>
        /// 加一个**原生文本按钮行**。
        ///
        /// <para>用的是官方 <c>ButtonRow</c>（一个横向布局组 + 一个 <c>ElementList</c>），
        /// 点击回调按本仓库已验证的方式转成 IL2CPP 委托并保活。</para>
        /// </summary>
        public GameObject AddButton(string label, Action onClick = null)
        {
            var go = UiSkin.Clone(UiSkinPart.ButtonRow, null, "Item_Button");
            if (!UiFinder.IsAlive(go))
                return null;

            var row = go.GetComponent<MVZ2.UI.ButtonRow>();
            if (row == null)
            {
                try { UnityEngine.Object.Destroy(go); } catch (Exception) { }
                return null;
            }

            try
            {
                row.UpdateButtons(ToIl2Cpp(new[] { label ?? string.Empty }));
            }
            catch (Exception e)
            {
                UiLog.Warn("设置列表按钮文案失败：" + e.GetType().Name);
            }

            if (onClick != null)
            {
                try
                {
                    var managed = new Action<MVZ2.UI.ButtonRow, int>((r, i) => onClick());
                    var il2cpp = DelegateSupport
                        .ConvertDelegate<Il2CppSystem.Action<MVZ2.UI.ButtonRow, int>>(managed);
                    UiClone.KeepAlive(go, managed, il2cpp);
                    row.add_OnButtonClick(il2cpp);
                }
                catch (Exception e)
                {
                    UiLog.Warn("挂列表按钮回调失败：" + e.GetType().Name + ": " + e.Message);
                }
            }

            return Add(go);
        }

        /// <summary>加一行**原生设置项行**（复选框 / 滑条 / 选择 / 单按钮），返回配置器。</summary>
        public UiOptionRow AddOptionRow(string name = null, UiOptionKind kind = UiOptionKind.Toggle)
        {
            var row = UiOptionRow.Create(null, name, kind);
            if (row == null)
                return null;
            if (Add(row.Root) == null)
                return null;
            row.SetStretch();
            return row;
        }

        /// <summary>
        /// 改一个**原生文本按钮行**（<see cref="AddButton"/> 造出来的那种）的文案。
        ///
        /// <para><b>为什么不能用 <c>UiClone.SetLabel</c>：</b>行里的按钮是
        /// <c>ButtonRow</c> 自己在运行时用 <c>UpdateButtons</c> 生成/复用出来的，
        /// 直接找子节点上的 TMP 改字**不生效**（拿到的往往是模板那一份）——
        /// 症状是「日志里改了，画面上还是老文案」。必须再调一次 <c>UpdateButtons</c>。</para>
        ///
        /// <para>文案会过一遍 <see cref="UiLocale.Get"/>（与 <see cref="UiKit.Label"/> 一致）。</para>
        /// </summary>
        public static bool SetButtonLabel(GameObject row, string label)
        {
            if (!UiFinder.IsAlive(row))
                return false;
            try
            {
                var br = row.GetComponent<MVZ2.UI.ButtonRow>();
                if (br == null)
                    return false;
                br.UpdateButtons(ToIl2Cpp(new[] { UiLocale.Get(label) ?? string.Empty }));
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("改按钮行文案失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>清空所有行。</summary>
        public void Clear()
        {
            if (!IsValid)
                return;
            try
            {
                for (var i = _content.childCount - 1; i >= 0; i--)
                {
                    var child = _content.GetChild(i);
                    if (child == null)
                        continue;
                    try { UnityEngine.Object.Destroy(child.gameObject); }
                    catch (Exception) { }
                }
                Rebuild();
            }
            catch (Exception e)
            {
                UiLog.Warn("清空列表失败：" + e.GetType().Name);
            }
        }

        /// <summary>
        /// 强制重算布局。
        ///
        /// <para>动态增删子节点后 <c>LayoutGroup</c> 不会立刻重排，同帧读 <c>rect</c> 会拿到旧值 ——
        /// 所以 <see cref="Add"/> / <see cref="Clear"/> 内部都会调它。</para>
        /// </summary>
        public void Rebuild()
        {
            try
            {
                if (_content != null)
                    LayoutRebuilder.ForceRebuildLayoutImmediate(_content);

                var sr = Scroll;
                if (sr != null)
                {
                    var crt = sr.GetComponent<RectTransform>();
                    if (crt != null)
                        LayoutRebuilder.ForceRebuildLayoutImmediate(crt);
                }
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 滚动
        // ------------------------------------------------------------------

        /// <summary>滚到顶部（<c>verticalNormalizedPosition</c>：1 = 顶、0 = 底）。</summary>
        public UiList ScrollToTop()
        {
            return SetVerticalPosition(1f);
        }

        /// <summary>滚到底部。</summary>
        public UiList ScrollToBottom()
        {
            return SetVerticalPosition(0f);
        }

        /// <summary>设置滚动位置（0 = 底，1 = 顶）。</summary>
        public UiList SetVerticalPosition(float normalized)
        {
            var sr = Scroll;
            if (sr == null)
                return this;
            try
            {
                sr.verticalNormalizedPosition = Mathf.Clamp01(normalized);
            }
            catch (Exception) { }
            return this;
        }

        /// <summary>滚动位置（0 = 底，1 = 顶）；无效时返回 -1。</summary>
        public float VerticalPosition
        {
            get
            {
                var sr = Scroll;
                if (sr == null)
                    return -1f;
                try { return sr.verticalNormalizedPosition; }
                catch (Exception) { return -1f; }
            }
        }

        // ------------------------------------------------------------------
        // 诊断
        // ------------------------------------------------------------------

        /// <summary>
        /// 紧凑结构行：**滚动容器 → 视口 → 内容 → 首行** 的屏幕矩形，加一行「内容比视口窄多少」。
        ///
        /// <para>排查「行没有填满框体」时就靠这几行数字 —— <see cref="SetRowFit"/> 改的正是
        /// 内容容器自己的水平内缩（原版素材把它摆得比视口窄一圈）。</para>
        /// </summary>
        public string DescribeStructure()
        {
            if (!IsValid)
                return "  结构     : （无效列表）";
            var sb = new StringBuilder(240);
            try
            {
                var vp = Viewport();
                sb.Append("  滚动容器 : ").Append(UiKit.Describe(_root)).Append('\n');
                sb.Append("    视口   : ").Append(UiKit.Describe(vp == null ? null : vp.gameObject)).Append('\n');
                sb.Append("    内容   : ").Append(UiKit.Describe(_content == null ? null : _content.gameObject)).Append('\n');
                var w = _content == null ? 0f : _content.rect.width;
                var vw = vp == null ? 0f : vp.rect.width;
                sb.Append("    宽对比 : 内容=").Append(Round(w)).Append("  视口=").Append(Round(vw))
                  .Append("  差=").Append(Round(vw - w)).Append(" 单位");
                if (Count > 0)
                {
                    var first = _content.GetChild(0);
                    if (first != null)
                        sb.Append('\n').Append("    首行   : ").Append(UiKit.Describe(first.gameObject));
                }
            }
            catch (Exception e)
            {
                sb.Append("（结构描述失败 ").Append(e.GetType().Name).Append("）");
            }
            return sb.ToString();
        }

        /// <summary>视口（<c>ScrollRect.viewport</c>）。</summary>
        private RectTransform Viewport()
        {
            var sr = Scroll;
            if (sr == null)
                return null;
            try
            {
                var vp = sr.viewport;
                return vp == null ? null : vp;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>把列表的实际状态打出来（行数/高度/滚动位置/Content 的布局组件）。</summary>
        public string Describe()
        {
            if (!IsValid)
                return "（无效列表）";

            var sb = new StringBuilder(300);
            try
            {
                sb.Append("根=").Append(UiFinder.PathOf(_root.transform)).Append('\n');
                sb.Append("  行数=").Append(Count);
                sb.Append("  滚动位置=").Append(VerticalPosition.ToString("0.##",
                    System.Globalization.CultureInfo.InvariantCulture));

                var rootRt = _root.GetComponent<RectTransform>();
                if (rootRt != null)
                    sb.Append("  视口=").Append(Round(rootRt.rect.width)).Append('x').Append(Round(rootRt.rect.height));
                sb.Append('\n');

                sb.Append("  Content=");
                if (_content == null)
                {
                    sb.Append("（无）\n");
                }
                else
                {
                    sb.Append(Round(_content.rect.width)).Append('x').Append(Round(_content.rect.height));
                    sb.Append("  layout=").Append(DescribeComponents(_content.gameObject)).Append('\n');
                }

                var le = _root.GetComponent<LayoutElement>();
                sb.Append("  LayoutElement=");
                sb.Append(le == null
                    ? "（无 —— 在 ContentSizeFitter 类父容器下视口高度会是 0）"
                    : "preferredH=" + Number(le.preferredHeight) + " flexibleH=" + Number(le.flexibleHeight));
                sb.Append('\n');

                var sr = Scroll;
                sb.Append("  ScrollRect：horizontal=").Append(sr == null ? "?" : (sr.horizontal ? 1 : 0).ToString())
                  .Append(" vertical=").Append(sr == null ? "?" : (sr.vertical ? 1 : 0).ToString());
                sb.Append(" viewport=").Append(sr == null || !UiFinder.IsAlive(sr.viewport)
                    ? "（无）" : UiFinder.PathOf(sr.viewport)).Append('\n');

                // 第一行的矩形：行是否真的排开了，看这个
                if (Count > 0)
                {
                    var first = _content.GetChild(0);
                    if (first != null)
                    {
                        var rt = first.TryCast<RectTransform>();
                        if (rt != null)
                            sb.Append("  行1=").Append(Round(rt.rect.width)).Append('x').Append(Round(rt.rect.height))
                              .Append("  y=").Append(Round(rt.anchoredPosition.y)).Append('\n');
                    }
                }

                // 图形清单 + 与原版的对比：排查"背景颜色不对"这类观感问题只能靠它
                // （原版那个容器仍然活在主菜单设置界面里，两边字段一一对照即可）。
                sb.Append('\n');
                sb.Append(UiSkin.DumpScrollViewGraphics("  图形（本列表）", _root));
                sb.Append(UiSkin.DumpScrollViewGraphics("  图形（原版原型）", UiSkin.FindVanillaScrollView()));
            }
            catch (Exception e)
            {
                sb.Append("读列表状态失败：").Append(e.GetType().Name).Append('\n');
            }
            return sb.ToString();
        }

        private static string DescribeComponents(GameObject go)
        {
            try
            {
                var all = go.GetComponents<Component>();
                if (all == null || all.Length == 0)
                    return "（无）";
                var sb = new StringBuilder(80);
                for (var i = 0; i < all.Length; i++)
                {
                    var c = all[i];
                    if (c == null)
                        continue;
                    if (sb.Length > 0)
                        sb.Append(',');
                    sb.Append(c.GetType().Name);
                }
                return sb.ToString();
            }
            catch (Exception)
            {
                return "?";
            }
        }

        private static string Round(float v)
        {
            return ((int)Mathf.Round(v)).ToString();
        }

        private static string Number(float v)
        {
            return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static Il2CppStringArray ToIl2Cpp(string[] items)
        {
            var n = items == null ? 0 : items.Length;
            var arr = new Il2CppStringArray(n);
            for (var i = 0; i < n; i++)
                arr[i] = items[i];
            return arr;
        }
    }
}
