using System;
using DSHCore.Ui;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// DSHCore 的 UiLog 是 internal，跨程序集用不了 —— 这里把它别名到本 mod 自己的那份
// （接口一致：WarnOnce(key, msg) / Warn(msg)），代码可以原样搬。
using UiLog = Replay.ReplayLog;

namespace Replay
{
    /// <summary>
    /// 回放页用到的**克隆小工具**：横向行容器 / 文本 / 文本按钮。
    ///
    /// <para><b>为什么要单独一层：</b>本仓库的铁律是「控件只能克隆原版节点，绝不算坐标」，
    /// 但克隆完还有三件事必须每次做对，否则症状是「节点都在、就是看不见」：</para>
    /// <list type="number">
    /// <item>原版控件**没有 <c>LayoutElement</c>**，而我们的容器是
    /// <c>childControlWidth/Height = 1</c> 的布局组 —— 不问它 preferred 尺寸就得到 0 宽 0 高
    /// （本仓库在「列表每行 0×40」上踩过）。所以每个单元格都要显式给尺寸，
    /// 而尺寸一律**从素材自己量**（<see cref="RowHeight"/> / <see cref="ButtonWidth"/>），
    /// 量不到才按画布折算。</item>
    /// <item>克隆体的点击回调是**空**的（原版的 <c>AddListener</c> 是运行时挂的，Instantiate 不会复制），
    /// 必须自己用 <see cref="UiClone.OnClick"/> 挂 —— 且它顺带处理了
    /// 「托管委托被 GC 回收 → 点一下崩」这个 IL2CPP 专属坑。</item>
    /// <item>行容器要**铺满**父容器（<c>flexibleWidth = 1</c>）：外层布局组不 forceExpand，
    /// 不给就不铺满。</item>
    /// </list>
    /// </summary>
    internal static class ReplayUiKit
    {
        /// <summary>列表行/按钮的字号。比原版正文（24pt）小 —— 行里要横向放 5 个单元格。</summary>
        public const float LabelFontSize = 14f;

        /// <summary>面板标题字号。</summary>
        public const float TitleFontSize = 22f;

        // ------------------------------------------------------------------
        // 尺寸（全部来自素材，量不到才折算）
        // ------------------------------------------------------------------

        private static float _rowHeight;
        private static bool _rowHeightResolved;

        /// <summary>一行控件的高度。优先量原版设置项行，其次量输入框，最后按画布折算。</summary>
        public static float RowHeight
        {
            get
            {
                if (_rowHeightResolved)
                    return _rowHeight;
                _rowHeightResolved = true;
                _rowHeight = TemplateHeight(UiSkinPart.OptionRow);
                if (_rowHeight <= 1f)
                    _rowHeight = TemplateHeight(UiSkinPart.Button);
                if (_rowHeight <= 1f)
                {
                    _rowHeight = Mathf.Max(24f, CanvasHeight() * 0.06f);
                    UiLog.WarnOnce("rp-rowheight",
                        "量不到原版控件高度，行高改用画布折算值 " + Mathf.RoundToInt(_rowHeight));
                }
                return _rowHeight;
            }
        }

        private static float _buttonWidth;
        private static bool _buttonWidthResolved;

        /// <summary>一个文本按钮的宽度。量不到就按行高的 3 倍折算（原版按钮是横长条）。</summary>
        public static float ButtonWidth
        {
            get
            {
                if (_buttonWidthResolved)
                    return _buttonWidth;
                _buttonWidthResolved = true;
                _buttonWidth = TemplateWidth(UiSkinPart.Button);
                if (_buttonWidth <= 1f)
                    _buttonWidth = RowHeight * 3f;
                return _buttonWidth;
            }
        }

        /// <summary>素材自己的 RectTransform 高度（原版控件的「出厂尺寸」就在这里）。</summary>
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
                if (rt.rect.height > 1f)
                    return rt.rect.height;
                return rt.sizeDelta.y > 1f ? rt.sizeDelta.y : 0f;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        private static float TemplateWidth(UiSkinPart part)
        {
            try
            {
                var tpl = UiSkin.Template(part);
                if (!UiFinder.IsAlive(tpl))
                    return 0f;
                var rt = tpl.transform.TryCast<RectTransform>();
                if (rt == null)
                    return 0f;
                if (rt.rect.width > 1f)
                    return rt.rect.width;
                return rt.sizeDelta.x > 1f ? rt.sizeDelta.x : 0f;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        /// <summary>主画布宽度（单位）。拿不到退回屏幕宽度。</summary>
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

        /// <summary>主画布高度（单位）。拿不到退回屏幕高度。</summary>
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

        // ------------------------------------------------------------------
        // 容器
        // ------------------------------------------------------------------

        /// <summary>
        /// 造一个**横向容器**（一行里横着摆几个东西）。
        ///
        /// <para>首选克隆原版设置面板里「一行三个滑条」的那个容器
        /// （<c>OptionsDialog/Dialog/MainElements/Sliders/Sliders1</c>）——
        /// 间距、对齐、<c>childControl*</c> 都是 prefab 里配好的，观感天然一致；
        /// 拿不到就自造一个 <c>HorizontalLayoutGroup</c>（本仓库已验证布局组可以现场 <c>AddComponent</c>）。</para>
        /// </summary>
        public static GameObject HContainer(Transform parent, string name)
        {
            var tpl = HorizontalTemplate();
            if (UiFinder.IsAlive(tpl))
            {
                var clone = UiClone.Instantiate(tpl, parent, name);
                if (UiFinder.IsAlive(clone))
                {
                    ClearChildren(clone);
                    return clone;
                }
            }

            var go = UiHost.CreateNode(name, parent);
            if (!UiFinder.IsAlive(go))
                return null;
            try
            {
                var lg = go.AddComponent<HorizontalLayoutGroup>();
                if (lg != null)
                {
                    lg.spacing = Mathf.Max(2f, RowHeight * 0.12f);
                    lg.childAlignment = TextAnchor.MiddleLeft;
                    lg.childControlWidth = true;
                    lg.childControlHeight = true;
                    lg.childForceExpandWidth = false;
                    lg.childForceExpandHeight = false;
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("rp-hcontainer", "自造横向容器失败：" + e.GetType().Name);
            }
            return go;
        }

        private static bool _horizontalResolved;
        private static GameObject _horizontalTemplate;

        private static GameObject HorizontalTemplate()
        {
            if (_horizontalResolved)
                return _horizontalTemplate;
            _horizontalResolved = true;
            try
            {
                var options = UiFinder.FindOne<MVZ2.UI.OptionsDialog>();
                if (options == null)
                    return null;
                var dialog = options.dialogTransform;
                if (dialog == null)
                    return null;
                var row = UiFinder.FindByPath(dialog, "MainElements/Sliders/Sliders1");
                if (row == null || row.GetComponent<HorizontalLayoutGroup>() == null)
                    return null;
                _horizontalTemplate = row.gameObject;
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("rp-htpl", "探测原版横向容器时异常：" + e.GetType().Name);
                return null;
            }
            return _horizontalTemplate;
        }

        /// <summary>删掉容器的全部子节点（克隆来的容器里可能带着原版的控件）。</summary>
        public static void ClearChildren(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                for (var i = go.transform.childCount - 1; i >= 0; i--)
                {
                    var child = go.transform.GetChild(i);
                    if (child == null)
                        continue;
                    try { UnityEngine.Object.Destroy(child.gameObject); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 单元格
        // ------------------------------------------------------------------

        /// <summary>
        /// 给一个节点挂上尺寸契约。<paramref name="preferredWidth"/> / <paramref name="preferredHeight"/>
        /// 传 &lt;0 表示「不管这一维」（让布局组去问控件自己）。
        /// </summary>
        public static LayoutElement Fit(GameObject go, float preferredWidth, float preferredHeight,
            float flexibleWidth = -1f)
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
                if (preferredWidth >= 0f)
                    le.preferredWidth = preferredWidth;
                if (preferredHeight >= 0f)
                    le.preferredHeight = preferredHeight;
                if (flexibleWidth >= 0f)
                    le.flexibleWidth = flexibleWidth;
                return le;
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("rp-fit", "给节点设尺寸失败：" + e.GetType().Name);
                return null;
            }
        }

        /// <summary>
        /// 克隆一个正文文本节点。超宽时用省略号截断 ——
        /// 行里横向摆了 3 段文字，不截断的话长关卡名会压到下一格上。
        /// </summary>
        public static TextMeshProUGUI Label(Transform parent, string name, string text,
            float fontSize = LabelFontSize, float preferredWidth = 0f, float flexibleWidth = 1f,
            TextAlignmentOptions align = TextAlignmentOptions.Left)
        {
            var go = UiSkin.Clone(UiSkinPart.Text, parent, name);
            if (!UiFinder.IsAlive(go))
                return null;
            UiClone.SetLabel(go, text ?? string.Empty, fontSize, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            try
            {
                if (tmp != null)
                {
                    tmp.overflowMode = TextOverflowModes.Ellipsis;
                    tmp.alignment = align;
                }
            }
            catch (Exception) { }
            Fit(go, preferredWidth, RowHeight, flexibleWidth);
            return tmp;
        }

        /// <summary>克隆一个原版文本按钮并挂上点击回调。</summary>
        public static GameObject TextButton(Transform parent, string name, string label,
            Action onClick, float width = -1f, float fontSize = LabelFontSize)
        {
            var go = UiSkin.Clone(UiSkinPart.Button, parent, name);
            if (!UiFinder.IsAlive(go))
                return null;
            UiClone.SetLabel(go, label ?? string.Empty, fontSize, false);
            Fit(go, width > 0f ? width : ButtonWidth, RowHeight, 0f);
            if (onClick != null)
                UiClone.OnClick(go, onClick);
            return go;
        }

        /// <summary>让布局组立刻重算一次。动态增删子节点后不调它，新行会停在 0×0。</summary>
        public static void Rebuild(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                var rt = go.transform.TryCast<RectTransform>();
                if (rt != null)
                    UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 克隆体修补
        // ------------------------------------------------------------------

        /// <summary>
        /// 把子树里**所有 <c>sprite == null</c> 的 Image 的透明度清 0**。
        ///
        /// <para>为什么必须做：uGUI 在 <c>sprite == null</c> 时画的是**纯白矩形**（不是透明）。
        /// 原版节点大量使用空 Image 当"吃射线的挡板"（标题底图、滚动视口的遮罩图形都是），
        /// 它们在原版里多半 <c>alpha=0</c> 或 <c>enabled=0</c>，但克隆出来就会显形 ——
        /// 表现为"面板上多了一块白"。<c>RectMask2D</c> 换掉 <c>Mask</c> 之后，
        /// 原本当遮罩图形用的那个 Image 也会开始画自己，同样要清掉。</para>
        ///
        /// <para><b>保留 <c>raycastTarget</c></b>：视口的那个 Image 承担着"接住拖动/点击"的职责，
        /// 只清颜色不影响它。</para>
        /// </summary>
        public static int ClearEmptySpriteAlpha(GameObject root)
        {
            if (!UiFinder.IsAlive(root))
                return 0;
            var n = 0;
            try
            {
                var images = root.GetComponentsInChildren<Image>(true);
                if (images == null)
                    return 0;
                for (var i = 0; i < images.Length; i++)
                {
                    var img = images[i];
                    if (img == null || img.sprite != null)
                        continue;
                    var c = img.color;
                    if (c.a <= 0.001f)
                        continue;
                    img.color = new Color(c.r, c.g, c.b, 0f);
                    n++;
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("rp-alpha", "清空图透明度失败：" + e.GetType().Name);
            }
            return n;
        }

        /// <summary>
        /// 把子树里的 <c>Mask</c> 换成 <c>RectMask2D</c>。
        ///
        /// <para><b>这条是硬要求，不是优化：</b><c>Mask</c> 走模板缓冲，会让 TMP 的子网格
        /// （<c>TMP_SubMeshUI</c>）去申请/释放**共享**的 Stencil 材质实例；克隆体和模板于是共用同一份，
        /// 之后无论哪一边被销毁都会把那份材质一起销毁，另一边再被 <c>Disable</c> 时就抛
        /// <c>NullReferenceException</c>。本仓库实测到的崩栈就是这个：</para>
        /// <code>
        /// Material.GetFloat ← TMP_SubMeshUI.UpdateMaterial ← SetMaterialDirty
        ///   ← MaskableGraphic.OnDisable ← TMP_SubMeshUI.OnDisable
        /// </code>
        /// <para>矩形的滚动视口本来只需要矩形裁剪，<c>RectMask2D</c> 不需要 Sprite、不碰模板缓冲。</para>
        /// </summary>
        public static int ReplaceMask(GameObject root)
        {
            if (!UiFinder.IsAlive(root))
                return 0;
            var n = 0;
            try
            {
                var masks = root.GetComponentsInChildren<Mask>(true);
                if (masks == null)
                    return 0;
                for (var i = 0; i < masks.Length; i++)
                {
                    var m = masks[i];
                    if (m == null)
                        continue;
                    var go = m.gameObject;
                    // 先**禁用**再销毁：`DestroyImmediate` 对 interop 组件代理实测**不生效**
                    // （dump 里能看到 Mask 与 RectMask2D 并存），而只要 Mask 还在 enabled 状态，
                    // TMP 的子网格就会继续去申请共享的 Stencil 材质 —— 崩溃依旧。
                    try { m.enabled = false; }
                    catch (Exception) { }
                    try { UnityEngine.Object.DestroyImmediate(m); }
                    catch (Exception) { }
                    if (go.GetComponent<RectMask2D>() == null)
                        go.AddComponent<RectMask2D>();
                    n++;
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("rp-mask", "替换 Mask 失败：" + e.GetType().Name);
            }
            return n;
        }

        /// <summary>
        /// 把一个容器改成**纵向**布局容器，并让子项铺满宽度（多出来的项自然往下排）。
        ///
        /// <para><b>为什么要动原版的布局组：</b>插件页的列表容器是
        /// <c>HorizontalLayoutGroup</c>（原版打算横向排多个分类按钮），
        /// 所以「克隆一个按钮插进去」只会**并排**，不会另起一行。
        /// 本仓库的原则是"排版交给布局组"，那就把布局组本身改成纵向 —— 而不是去算坐标。</para>
        ///
        /// <para><b>容器本来没有布局组时也要能用</b>：原版有些面板（例如插件页的
        /// <c>Center/List</c>）是用锚点摆的，压根没有 <c>LayoutGroup</c>。
        /// 那种情况下按 <paramref name="spacing"/> / <paramref name="padding"/> 新建一个 ——
        /// 实测过：漏了这一条，后面的 <c>Fit</c> 全部形同虚设，子项会停在 0×0 上。</para>
        ///
        /// <para>会把每个直接子节点的 <c>LayoutElement</c> 改成"宽度吃掉剩余空间"，
        /// 并（在容器**纵向不拉伸**且没有 <c>ContentSizeFitter</c> 时）把高度调到刚好放下所有行 ——
        /// 滚动视图的内容节点高度是**它自己算**的，不改就会把多出来的行裁掉。</para>
        /// </summary>
        public static bool MakeVertical(GameObject container, float spacing = 10f, int padding = 10)
        {
            if (!UiFinder.IsAlive(container))
                return false;
            try
            {
                var h = container.GetComponent<HorizontalLayoutGroup>();
                var v = container.GetComponent<VerticalLayoutGroup>();

                float useSpacing = spacing;
                bool cw = true, ch = true, ew = false, eh = false;
                int padLeft = padding, padTop = padding, padBottom = padding, padRight = padding;

                if (h != null)
                {
                    useSpacing = h.spacing;
                    cw = h.childControlWidth;
                    ch = h.childControlHeight;
                    ew = h.childForceExpandWidth;
                    eh = h.childForceExpandHeight;
                    try
                    {
                        if (h.padding != null)
                        {
                            padLeft = h.padding.left;
                            padTop = h.padding.top;
                            padBottom = h.padding.bottom;
                            padRight = h.padding.right;
                        }
                    }
                    catch (Exception) { }

                    // 先删再加：同一个节点上不允许同时存在两个 LayoutGroup。
                    try { UnityEngine.Object.DestroyImmediate(h); }
                    catch (Exception e)
                    {
                        UiLog.WarnOnce("rp-mkvert-del", "删掉横向布局组失败：" + e.GetType().Name);
                        return false;
                    }
                    v = null;
                }

                if (v == null)
                {
                    v = container.AddComponent<VerticalLayoutGroup>();
                    if (v == null)
                    {
                        UiLog.WarnOnce("rp-mkvert-add",
                            "加纵向布局组失败（AddComponent 返回 null）：" + UiFinder.PathOf(container.transform));
                        return false;
                    }
                }

                v.spacing = useSpacing;
                v.childAlignment = TextAnchor.UpperCenter;
                v.childControlWidth = cw;
                v.childControlHeight = ch;
                v.childForceExpandWidth = false;   // 交给 flexibleWidth，别强制
                v.childForceExpandHeight = eh;
                try
                {
                    // interop 的 RectOffset 只有无参构造（四参重载没有生成），所以手工赋值。
                    var pad = new RectOffset();
                    pad.left = padLeft;
                    pad.right = padRight;
                    pad.top = padTop;
                    pad.bottom = padBottom;
                    v.padding = pad;
                }
                catch (Exception) { }

                var t = container.transform;
                var n = t.childCount;
                float inner = 0f;
                var rows = 0;
                for (var i = 0; i < n; i++)
                {
                    var child = t.GetChild(i);
                    if (child == null)
                        continue;
                    var go = child.gameObject;
                    var le = go.GetComponent<LayoutElement>();
                    if (le == null)
                        le = go.AddComponent<LayoutElement>();
                    // 铺满宽度：父布局组 forceExpandWidth = false，不给 flexibleWidth 就会按
                    // preferred 排（原模板是 300），在 480 宽的内容区里会挤成左侧一小条。
                    le.preferredWidth = -1f;
                    le.flexibleWidth = 1f;
                    if (le.ignoreLayout)
                        continue;
                    var hgt = le.preferredHeight > 0f ? le.preferredHeight : 40f;
                    try
                    {
                        var crt = child.TryCast<RectTransform>();
                        if (crt != null && crt.rect.height > 1f)
                            hgt = crt.rect.height;
                    }
                    catch (Exception) { }
                    inner += hgt;
                    rows++;
                }

                // 内容高度：**只有纵向不拉伸的容器才需要自己写**。
                //  - 滚动视图的内容节点（锚 0/1-1/1 或 0/1-0/1）：高度是它自己算的，
                //    不改就会把多出来的行裁掉 → 必须写。
                //  - 锚 y 拉伸的容器（锚 0.5/0-0.5/1，例如页面里的面板）：高度由父布局组给，
                //    写进去反而会把它压扁 → 绝对不能碰。
                var panelRt = container.transform.TryCast<RectTransform>();
                var stretched = panelRt != null
                    && Mathf.Abs(panelRt.anchorMax.y - panelRt.anchorMin.y) > 0.001f;
                if (!stretched && container.GetComponent<ContentSizeFitter>() == null && rows > 0)
                {
                    if (panelRt != null)
                    {
                        var total = padTop + padBottom + inner + useSpacing * (rows - 1);
                        panelRt.sizeDelta = new Vector2(panelRt.sizeDelta.x, total);
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("rp-mkvert", "改纵向布局失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 摘掉子树里所有的**文本翻译器**（<c>MVZ2.Localization.TextMeshProUGUITranslator</c>）。
        ///
        /// <para><b>这是「改了文案又自己变回去」的唯一原因，必须先做再改文字。</b>
        /// 那个组件的实现是：</para>
        /// <code>
        /// private void OnEnable() { lang.OnLanguageChanged += cb; Translate(lang.GetCurrentLanguage()); }
        /// // TextMeshProUGUITranslator.Translate → Component.text = lang._p(Context, Key)
        /// //   Key 取不到时退回「组件当前 text」 —— 也就是原版那句「插件」/「资源包」
        /// </code>
        /// <para>于是克隆体的命运是：克隆时 <c>OnEnable</c> 跑一遍（把原版的键缓存下来）、
        /// 我们改写文字、**节点再次被启用时它又把翻译写回去** ——
        /// 实测：<c>Build()</c> 里读出来明明是「回放」，面板一 <c>SetActive(true)</c> 就变回「插件」。
        /// 游戏里切语言也会触发同一条路。</para>
        ///
        /// <para>摘掉它是**正确**的做法而不是绕路：这几处文案归本 mod 所有，
        /// 本来就不该跟着游戏的语言表走。必须先 <c>enabled = false</c> 再销毁 ——
        /// interop 组件上 <c>DestroyImmediate</c> 不保证立刻生效（Mask 那次实测就没生效）。</para>
        /// </summary>
        public static int StripTextTranslators(GameObject root)
        {
            if (!UiFinder.IsAlive(root))
                return 0;
            var n = 0;
            try
            {
                var list = root.GetComponentsInChildren<MVZ2.Localization.TextMeshProUGUITranslator>(true);
                if (list == null)
                    return 0;
                for (var i = 0; i < list.Length; i++)
                {
                    var t = list[i];
                    if (t == null)
                        continue;
                    try { t.enabled = false; } catch (Exception) { }
                    try { UnityEngine.Object.DestroyImmediate(t); } catch (Exception) { }
                    n++;
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("rp-translator", "摘文本翻译器失败：" + e.GetType().Name + ": " + e.Message);
            }
            return n;
        }

        /// <summary>单独设 <c>flexibleHeight</c>（<c>Fit</c> 不管这一维）。</summary>
        public static void SetFlexibleHeight(GameObject go, float value)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                var le = go.GetComponent<LayoutElement>();
                if (le == null)
                    le = go.AddComponent<LayoutElement>();
                if (le != null)
                    le.flexibleHeight = value;
            }
            catch (Exception) { }
        }

        /// <summary>从子树里按名字找一个直接子节点。</summary>
        public static GameObject Child(GameObject root, string name)
        {
            if (!UiFinder.IsAlive(root) || string.IsNullOrEmpty(name))
                return null;
            try
            {
                var t = root.transform.Find(name);
                return t == null ? null : t.gameObject;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>销毁一个节点（立即，不等帧末 —— 布局组要马上重算）。</summary>
        public static void Kill(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try { UnityEngine.Object.Destroy(go); }
            catch (Exception) { }
        }
    }
}
