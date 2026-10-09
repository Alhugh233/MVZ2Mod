using System;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DSHCore.Ui
{
    /// <summary>
    /// **克隆小工具**：横向/纵向行容器、文本、文本按钮、尺寸契约、克隆体修补、布局 dump。
    ///
    /// <para><b>为什么要有这一层：</b>本库的铁律是「控件只能克隆原版节点，绝不算坐标」，
    /// 但克隆完还有几件事必须每次做对，否则症状永远是「节点都在、就是看不见」：</para>
    /// <list type="number">
    /// <item>原版控件大多**没有 <c>LayoutElement</c>**，而我们的容器是
    /// <c>childControlWidth/Height = 1</c> 的布局组 —— 不问它 preferred 尺寸就得到
    /// 0 宽 0 高（本仓库在「列表每行 0×40」上踩过）。所以每个单元格都要显式给尺寸，
    /// 而尺寸一律**从素材自己量**（<see cref="RowHeight"/> / <see cref="ButtonWidth"/>），
    /// 量不到才按画布折算。</item>
    /// <item>克隆体的点击回调是**空**的（原版的 <c>AddListener</c> 是运行时挂的，
    /// <c>Instantiate</c> 不会复制），必须自己用 <see cref="UiClone.OnClick"/> 挂 ——
    /// 它顺带处理了「托管委托被 GC 回收 → 点一下崩」这个 IL2CPP 专属坑。</item>
    /// <item>行容器要**铺满**父容器（<c>flexibleWidth = 1</c>）：外层布局组不 forceExpand，
    /// 不给就不铺满。</item>
    /// <item>文本翻译器、空精灵 Image、<c>Mask</c> 这三样必须在克隆后修掉，
    /// 否则分别得到「改的文案自己变回去」「多一块白」「随机空指针」（见各方法注释）。</item>
    /// </list>
    ///
    /// <para>本类型由 <c>Replay</c> 的 <c>ReplayUiKit</c> 收敛而来（那套实现已实测可用）。</para>
    /// </summary>
    public static class UiKit
    {
        /// <summary>列表行/按钮的推荐字号。比原版正文（24pt）小 —— 一行里通常要横向放好几格。</summary>
        public const float LabelFontSize = 14f;

        /// <summary>面板标题的推荐字号。</summary>
        public const float TitleFontSize = 22f;

        // ------------------------------------------------------------------
        // 尺寸（全部来自素材，量不到才折算）
        // ------------------------------------------------------------------

        private static float _rowHeight;
        private static bool _rowHeightResolved;

        /// <summary>一行控件的高度。优先量原版设置项行，其次量按钮，最后按画布折算。</summary>
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
                    UiLog.WarnOnce("uikit-rowheight",
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

        /// <summary>素材自己的 RectTransform 宽度。</summary>
        public static float TemplateWidth(UiSkinPart part)
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

        /// <summary>主画布宽度（单位）。拿不到退回屏幕宽度。**不要**自己写死像素。</summary>
        public static float CanvasWidth()
        {
            return CanvasSize().x;
        }

        /// <summary>主画布高度（单位）。拿不到退回屏幕高度。</summary>
        public static float CanvasHeight()
        {
            return CanvasSize().y;
        }

        private static Vector2 CanvasSize()
        {
            try
            {
                var canvas = UiGame.MainCanvas;
                if (canvas != null)
                {
                    var rt = canvas.GetComponent<RectTransform>();
                    if (rt != null && rt.rect.width > 1f && rt.rect.height > 1f)
                        return new Vector2(rt.rect.width, rt.rect.height);
                }
            }
            catch (Exception) { }
            return new Vector2(Mathf.Max(320f, Screen.width), Mathf.Max(240f, Screen.height));
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
        /// 拿不到就自造一个 <c>HorizontalLayoutGroup</c>（布局组是可以现场 <c>AddComponent</c> 的）。</para>
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
                UiLog.WarnOnce("uikit-hcontainer", "自造横向容器失败：" + e.GetType().Name);
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
                UiLog.WarnOnce("uikit-htpl", "探测原版横向容器时异常：" + e.GetType().Name);
                return null;
            }
            return _horizontalTemplate;
        }

        /// <summary>
        /// 造一个**纵向容器**（把若干行竖着排）。
        /// 没有现成的原版纵向容器素材，所以直接 <c>AddComponent&lt;VerticalLayoutGroup&gt;</c>
        /// —— 布局组是内置 UI 组件，现场加是安全的（本仓库已验证）。
        /// </summary>
        public static GameObject VContainer(Transform parent, string name, float spacing = -1f, int padding = 0)
        {
            var go = UiHost.CreateNode(name, parent);
            if (!UiFinder.IsAlive(go))
                return null;
            try
            {
                var v = go.AddComponent<VerticalLayoutGroup>();
                if (v != null)
                {
                    v.spacing = spacing >= 0f ? spacing : Mathf.Max(2f, RowHeight * 0.12f);
                    v.childAlignment = TextAnchor.UpperCenter;
                    v.childControlWidth = true;
                    v.childControlHeight = true;
                    v.childForceExpandWidth = false;
                    v.childForceExpandHeight = false;
                    if (padding > 0)
                    {
                        var pad = new RectOffset();
                        pad.left = padding;
                        pad.right = padding;
                        pad.top = padding;
                        pad.bottom = padding;
                        v.padding = pad;
                    }
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("uikit-vcontainer", "自造纵向容器失败：" + e.GetType().Name);
            }
            return go;
        }

        /// <summary>
        /// 删掉容器的全部子节点（克隆来的容器里可能带着原版的控件）。
        ///
        /// <para><paramref name="immediately"/> = true 用 <c>DestroyImmediate</c>：
        /// **紧接着要重算布局时必须是 true**（帧末销毁的节点还在，会被算进行数里）。</para>
        /// </summary>
        public static void ClearChildren(GameObject go, bool immediately = false)
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
                    Kill(child.gameObject, immediately);
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
                UiLog.WarnOnce("uikit-fit", "给节点设尺寸失败：" + e.GetType().Name);
                return null;
            }
        }

        /// <summary>单独设 <c>flexibleHeight</c>（<see cref="Fit"/> 不管这一维）。</summary>
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

        /// <summary>
        /// 克隆一个正文文本节点。默认超宽时用省略号截断 ——
        /// 行里横着摆好几段文字时，不截断长名字会压到下一格上。
        /// </summary>
        /// <param name="preferredHeight">
        /// 高度契约：**&lt; 0（默认）= 用 <see cref="RowHeight"/>**（原版控件行的高度，行为不变）；
        /// **0 = 高度交给内容**（<see cref="AutoHeight"/>，文字换行时行高会自己长）；
        /// **&gt; 0 = 指定高度**（例如「比正文更矮的栏目标题」）。
        /// </param>
        /// <remarks>
        /// 与 <see cref="Paragraph"/> / <see cref="TextButton"/> / <c>UiOptionRow</c> 一样，
        /// 写文字前会过一遍 <see cref="UiLocale.Get"/> —— 子 mod 只要用
        /// <see cref="UiLocale.Table"/> 登记译文，界面就会跟着游戏语言变（没登记的文本原样通过）。
        /// 需要绕开取词（比如要显示「名称原文」）就直接用 <see cref="UiClone.SetLabel"/>。
        /// </remarks>
        public static TextMeshProUGUI Label(Transform parent, string name, string text,
            float fontSize = LabelFontSize, float preferredWidth = 0f, float flexibleWidth = 1f,
            TextAlignmentOptions align = TextAlignmentOptions.Left, float preferredHeight = -1f)
        {
            var go = UiSkin.Clone(UiSkinPart.Text, parent, name);
            if (!UiFinder.IsAlive(go))
                return null;
            UiClone.SetLabel(go, UiLocale.Get(text) ?? string.Empty, fontSize, preferredHeight == 0f);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            try
            {
                if (tmp != null)
                {
                    if (preferredHeight == 0f)
                        tmp.overflowMode = TextOverflowModes.Overflow;   // 由内容撑高的行不该截断
                    else
                        tmp.overflowMode = TextOverflowModes.Ellipsis;
                    tmp.alignment = align;
                }
            }
            catch (Exception) { }

            if (preferredHeight == 0f)
            {
                Fit(go, preferredWidth, -1f, flexibleWidth);
                AutoHeight(go);
            }
            else
            {
                Fit(go, preferredWidth, preferredHeight < 0f ? RowHeight : preferredHeight, flexibleWidth);
            }
            return tmp;
        }

        /// <summary>
        /// 让节点的高度**由内容决定**：把 <c>LayoutElement</c> 上的高度契约清回 -1。
        ///
        /// <para><b>为什么不能直接填 0</b>：<c>LayoutElement</c> 的 <c>layoutPriority</c>（1）高于
        /// <c>TextMeshProUGUI</c>（0），任何 ≥ 0 的返回值都会把 TMP 自己算出来的
        /// **换行后高度**盖掉（结果就是高度恒为 0 或固定死）。清成 -1 才会回落到 TMP 的 preferred。</para>
        /// </summary>
        public static LayoutElement AutoHeight(GameObject go)
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
                le.minHeight = -1f;
                le.preferredHeight = -1f;
                le.flexibleHeight = -1f;
                return le;
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("uikit-autoheight", "清高度契约失败：" + e.GetType().Name);
                return null;
            }
        }

        /// <summary>
        /// 把一段文本加粗（TMP 的 <c>Bold</c> 样式）。
        ///
        /// <para>SDF 字体走 shader 的加粗权重，**不依赖字体里有没有单独的粗体字面**；
        /// 加粗不明显（或反被压细）说明该字体材质没配 <c>_WeightBold</c>，
        /// 那就退而求其次用字号/颜色区分层级。</para>
        /// </summary>
        public static bool SetBold(TextMeshProUGUI text, bool bold = true)
        {
            if (text == null)
                return false;
            try
            {
                text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
                return true;
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("uikit-bold", "设置加粗失败：" + e.GetType().Name);
                return false;
            }
        }

        /// <summary>这一段文本现在是不是粗体（验收用）。</summary>
        public static bool IsBold(TextMeshProUGUI text)
        {
            if (text == null)
                return false;
            try { return (text.fontStyle & FontStyles.Bold) != 0; }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 克隆一个正文文本行但**允许自动换行**（说明性长文本用这个，不要用 <see cref="Label"/>）。
        /// 行高交给 TMP 自己算（<see cref="AutoHeight"/>，换行时会变高）。
        /// </summary>
        public static TextMeshProUGUI Paragraph(Transform parent, string name, string text,
            float fontSize = LabelFontSize, float flexibleWidth = 1f)
        {
            var go = UiSkin.Clone(UiSkinPart.Text, parent, name);
            if (!UiFinder.IsAlive(go))
                return null;
            UiClone.SetLabel(go, UiLocale.Get(text) ?? string.Empty, fontSize, true);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            try
            {
                if (tmp != null)
                {
                    tmp.alignment = TextAlignmentOptions.Left;
                    tmp.overflowMode = TextOverflowModes.Overflow;
                }
            }
            catch (Exception) { }
            Fit(go, 0f, -1f, flexibleWidth);
            AutoHeight(go);
            return tmp;
        }

        /// <summary>克隆一个原版文本按钮并挂上点击回调。</summary>
        /// <remarks><paramref name="name"/> 是节点名（**不过取词**），<paramref name="label"/> 是按钮文案（过取词）。</remarks>
        public static GameObject TextButton(Transform parent, string name, string label,
            Action onClick, float width = -1f, float fontSize = LabelFontSize)
        {
            var go = UiSkin.Clone(UiSkinPart.Button, parent, name);
            if (!UiFinder.IsAlive(go))
                return null;
            UiClone.SetLabel(go, UiLocale.Get(label) ?? string.Empty, fontSize, false);
            Fit(go, width > 0f ? width : ButtonWidth, RowHeight, 0f);
            if (onClick != null)
                UiClone.OnClick(go, onClick);
            return go;
        }

        /// <summary>
        /// 克隆一条**原版分割线**（原版设置界面里分类标题下面那条）。
        ///
        /// <para>素材 = <c>CategoryTemplate/Title/Line</c>：**2 单位高、横向铺满、无 sprite 的纯色 Image**。
        /// 也就是说它正是「原版自己在用的那一条」，不是自己拼的容器或文字画出来的线。</para>
        ///
        /// <para>⚠ 它是**无 sprite 的 Image** —— 如果用 <see cref="ClearEmptySpriteAlpha"/> 整备过
        /// 那棵子树，线会被清成透明。这里是从**活着的**原版设置界面取素材，
        /// 克隆体自带 <c>a = 1</c>，不受影响。</para>
        ///
        /// <para>作为一行加进纵向布局组：宽度铺满、高度 = 素材自己的 2 单位。拿不到素材时返回 null。</para>
        /// </summary>
        public static GameObject Separator(Transform parent, string name = null)
        {
            var tpl = LineTemplate();
            if (!UiFinder.IsAlive(tpl))
            {
                UiLog.WarnOnce("uikit-sep-tpl",
                    "拿不到原版分割线素材（CategoryTemplate/Line；要先进过一次原版设置界面）—— 不画分割线。");
                return null;
            }

            var go = UiClone.Instantiate(tpl, parent, string.IsNullOrEmpty(name) ? "DSH_Separator" : name);
            if (!UiFinder.IsAlive(go))
                return null;

            // 档案/设置界面在别的 Canvas 下，世界缩放会跟着克隆过来 → 归 1。
            try { go.transform.localScale = UnityEngine.Vector3.one; }
            catch (Exception) { }

            var h = OwnHeight(go);
            Fit(go, 0f, h > 1f ? h : 2f, 1f);
            // 这里**刻意不记日志**：右栏每次换选中项都会重建列表 → 逐次打日志会把日志刷满。
            // 验收看 `ModSettingsPage.DumpLayout()`（它会打分割线的矩形与「子树里没有 TMP」）。
            return go;
        }

        /// <summary>节点上那张 Image 的透明度（没有 Image 时返回 -1）。诊断「克隆出来看不见」用。</summary>
        public static float ImageAlpha(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return -1f;
            try
            {
                var img = go.GetComponent<UnityEngine.UI.Image>();
                return img == null ? -1f : img.color.a;
            }
            catch (Exception)
            {
                return -1f;
            }
        }

        private static GameObject _lineTemplate;
        private static bool _lineResolved;

        /// <summary>
        /// 原版分割线素材。
        ///
        /// <para>它在 prefab 里的位置是 <c>CategoryTemplate/**Title**/Line</c>（**不是** CategoryTemplate 的直子节点）
        /// —— 原版把这条线摆在分类标题下面。两个路径都试，拿不到再退到「整棵子树里找叫 Line 的节点」。</para>
        /// </summary>
        private static GameObject LineTemplate()
        {
            if (_lineResolved)
                return _lineTemplate;
            _lineResolved = true;

            // ① 设置界面的「更多选项」页：分类列表的模板就是 CategoryTemplate
            try
            {
                var page = UiFinder.FindOne<MVZ2.UI.OptionsDialogMoreOptionsPage>();
                var list = page == null ? null : page.categories;
                var tpl = list == null ? null : list._template;
                if (UiFinder.IsAlive(tpl))
                    _lineTemplate = FindLine(tpl);
            }
            catch (Exception) { }

            // ② 退回按路径找（版本改名时还能用）
            if (!UiFinder.IsAlive(_lineTemplate))
            {
                try
                {
                    var options = UiFinder.FindOne<MVZ2.UI.OptionsDialog>();
                    var dialog = options == null ? null : options.dialogTransform;
                    if (dialog != null)
                    {
                        var t = UiFinder.FindByPath(dialog,
                            "MoreElements/Scroll View/Viewport/Content/CategoryTemplate");
                        if (t != null && UiFinder.IsAlive(t.gameObject))
                            _lineTemplate = FindLine(t.gameObject);
                    }
                }
                catch (Exception) { }
            }

            if (UiFinder.IsAlive(_lineTemplate))
                Plugin.Log?.LogInfo("[UiKit] 分割线素材：" + UiFinder.PathOf(_lineTemplate.transform));
            return _lineTemplate;
        }

        private static GameObject FindLine(GameObject templateRoot)
        {
            var line = UiKit.Path(templateRoot, "Title/Line");
            if (UiFinder.IsAlive(line))
                return line;
            line = UiKit.Path(templateRoot, "Line");
            if (UiFinder.IsAlive(line))
                return line;
            // 再退一步：整棵子树里找名字叫 Line 的节点
            try
            {
                var all = templateRoot.GetComponentsInChildren<UnityEngine.UI.Image>(true);
                if (all != null)
                {
                    for (var i = 0; i < all.Length; i++)
                    {
                        var img = all[i];
                        if (img == null || img.gameObject.name != "Line")
                            continue;
                        return img.gameObject;
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>节点自己的 RectTransform 高度（0 = 没自己的尺寸）。</summary>
        private static float OwnHeight(GameObject go)
        {
            try
            {
                var rt = go.transform.TryCast<RectTransform>();
                if (rt == null)
                    return 0f;
                if (rt.sizeDelta.y > 0.5f)
                    return rt.sizeDelta.y;
                return rt.rect.height;
            }
            catch (Exception)
            {
                return 0f;
            }
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
                    LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 克隆体修补（三件必须做的事）
        // ------------------------------------------------------------------

        /// <summary>
        /// 把子树里**所有 <c>sprite == null</c> 的 Image 的透明度清 0**。
        ///
        /// <para>为什么必须做：uGUI 在 <c>sprite == null</c> 时画的是**纯白矩形**（不是透明）。
        /// 原版节点大量使用空 Image 当「吃射线的挡板」（标题底图、滚动视口的遮罩图形都是），
        /// 它们在原版里多半 <c>alpha = 0</c> 或 <c>enabled = 0</c>，但克隆出来就会显形 ——
        /// 表现为「面板上多了一块白」。<c>RectMask2D</c> 换掉 <c>Mask</c> 之后，
        /// 原本当遮罩图形用的那个 Image 也会开始画自己，同样要清掉。</para>
        ///
        /// <para><b>保留 <c>raycastTarget</c></b>：视口的那个 Image 承担着「接住拖动/点击」的职责，
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
                UiLog.WarnOnce("uikit-alpha", "清空图透明度失败：" + e.GetType().Name);
            }
            return n;
        }

        /// <summary>
        /// 把子树里的 <c>Mask</c> 换成 <c>RectMask2D</c>。
        ///
        /// <para><b>这条是硬要求，不是优化：</b><c>Mask</c> 走模板缓冲，会让 TMP 的子网格
        /// （<c>TMP_SubMeshUI</c>）去申请/释放**共享**的 Stencil 材质实例；克隆体和模板于是共用同一份，
        /// 之后无论哪一边被销毁都会把那份材质一起销毁，另一边再被 <c>Disable</c> 时就抛
        /// <c>NullReferenceException</c>（本仓库实测一次开页刷 11 行）。</para>
        ///
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
                UiLog.WarnOnce("uikit-mask", "替换 Mask 失败：" + e.GetType().Name);
            }
            return n;
        }

        /// <summary>
        /// 摘掉子树里所有的**文本翻译器**（<c>MVZ2.Localization.TextMeshProUGUITranslator</c>）。
        ///
        /// <para><b>这是「改了文案又自己变回去」的唯一原因，必须先做再改文字。</b>
        /// 那个组件的实现是：</para>
        /// <code>
        /// private void OnEnable() { lang.OnLanguageChanged += cb; Translate(lang.GetCurrentLanguage()); }
        /// // Translate → Component.text = lang._p(Context, Key)
        /// //   Key 取不到时退回「组件当前 text」 —— 也就是原版那句
        /// </code>
        /// <para>于是克隆体的命运是：克隆时 <c>OnEnable</c> 跑一遍、我们改写文字、
        /// **节点再次被启用时它又把翻译写回去**。摘掉它是**正确**的做法而不是绕路：
        /// 这些文案归我们所有，本来就不该跟着游戏的语言表走。</para>
        ///
        /// <para>必须先 <c>enabled = false</c> 再销毁 —— interop 组件上 <c>DestroyImmediate</c>
        /// 不保证立刻生效（<c>Mask</c> 那次实测就没生效）。</para>
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
                UiLog.WarnOnce("uikit-translator", "摘文本翻译器失败：" + e.GetType().Name + ": " + e.Message);
            }
            return n;
        }

        /// <summary>
        /// 把一棵**克隆出来的子树**整备到「可以往上放东西」的状态：换遮罩 + 清空图 + 摘翻译器。
        /// 克隆原版页面壳之后第一件事就调它。
        /// </summary>
        public static void NormalizeSubtree(GameObject root)
        {
            ReplaceMask(root);
            ClearEmptySpriteAlpha(root);
            StripTextTranslators(root);
        }

        // ------------------------------------------------------------------
        // 布局组
        // ------------------------------------------------------------------

        /// <summary>
        /// 把一个容器改成**纵向**布局容器，并让子项铺满宽度（多出来的项自然往下排）。
        ///
        /// <para><b>为什么要动原版的布局组：</b>原版有些列表容器是
        /// <c>HorizontalLayoutGroup</c>（打算横向排多个分类按钮），所以「插一个兄弟」
        /// 只会**并排**、不会另起一行。本库的原则是「排版交给布局组」，
        /// 那就把布局组本身改成纵向 —— 而不是去算坐标。</para>
        ///
        /// <para><b>容器本来没有布局组时也要能用</b>：原版有些面板（例如插件页的
        /// <c>Center/List</c>）是用锚点摆的，压根没有 <c>LayoutGroup</c>。那种情况下按
        /// <paramref name="spacing"/> / <paramref name="padding"/> 新建一个 ——
        /// 实测过：漏了这一条，后面的 <c>Fit</c> 全部形同虚设，子项会停在 0×0 上。</para>
        /// </summary>
        /// <summary>
        /// 读一个纵向布局组当前的内边距（四边取最大值；拿不到时返回 8）。
        /// interop 的 <c>RectOffset</c> 只有无参构造，也**没有**四参重载，所以只能手工赋字段。
        /// </summary>
        public static int GetPadding(UnityEngine.UI.VerticalLayoutGroup v)
        {
            try
            {
                if (v == null || v.padding == null)
                    return 8;
                var p = v.padding;
                return Mathf.Max(Mathf.Max(p.left, p.right), Mathf.Max(p.top, p.bottom));
            }
            catch (Exception)
            {
                return 8;
            }
        }

        /// <summary>把纵向布局组的内边距设成四边同值。</summary>
        public static bool SetPadding(UnityEngine.UI.VerticalLayoutGroup v, int padding)
        {
            if (v == null || padding < 0)
                return false;
            try
            {
                var p = new RectOffset();
                p.left = padding;
                p.right = padding;
                p.top = padding;
                p.bottom = padding;
                v.padding = p;
                return true;
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("uikit-setpad", "设置内边距失败：" + e.GetType().Name);
                return false;
            }
        }

        /// <summary>读一个纵向布局组的行间距（拿不到时返回 0）。</summary>
        public static float GetSpacing(UnityEngine.UI.VerticalLayoutGroup v)
        {
            try
            {
                return v == null ? 0f : v.spacing;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        /// <summary>
        /// 容器现在的内边距（四边取最大值）。拿不到布局组时返回 0。
        /// 自测用来记录「面板里到底留了多少余量」。
        /// </summary>
        public static int GetPadding(GameObject container)
        {
            if (!UiFinder.IsAlive(container))
                return 0;
            try
            {
                var v = container.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
                if (v != null)
                {
                    var p = v.padding;
                    return p == null ? 0
                        : Mathf.Max(Mathf.Max(p.left, p.right), Mathf.Max(p.top, p.bottom));
                }
                var h = container.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                if (h == null || h.padding == null)
                    return 0;
                var hp = h.padding;
                return Mathf.Max(Mathf.Max(hp.left, hp.right), Mathf.Max(hp.top, hp.bottom));
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public static bool MakeVertical(GameObject container, float spacing = 10f, int padding = 10)
        {
            if (container == null)
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
                        UiLog.WarnOnce("uikit-mkvert-del", "删掉横向布局组失败：" + e.GetType().Name);
                        return false;
                    }
                    v = null;
                }

                if (v == null)
                {
                    v = container.AddComponent<VerticalLayoutGroup>();
                    if (v == null)
                    {
                        UiLog.WarnOnce("uikit-mkvert-add",
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
                    // preferred 排（原模板是 300），在宽内容区里会挤成左侧一小条。
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
                UiLog.WarnOnce("uikit-mkvert", "改纵向布局失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 节点操作
        // ------------------------------------------------------------------

        /// <summary>找**直接子节点**（不递归）。</summary>
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

        /// <summary>按 <c>"A/B/C"</c> 路径逐级找直接子节点。任一级缺失返回 null。</summary>
        public static GameObject Path(GameObject root, string path)
        {
            if (!UiFinder.IsAlive(root) || string.IsNullOrEmpty(path))
                return null;
            var cur = root;
            var parts = path.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                var name = parts[i].Trim();
                if (name.Length == 0)
                    continue;
                cur = Child(cur, name);
                if (!UiFinder.IsAlive(cur))
                    return null;
            }
            return cur;
        }

        /// <summary>
        /// 销毁一个节点。
        ///
        /// <para><paramref name="immediately"/> = true 用 <c>DestroyImmediate</c>：
        /// 要**在紧接着的布局重算里立刻看不见它**时必须用（<c>Destroy</c> 是帧末生效的，
        /// 同一帧它还在，会被 <see cref="MakeVertical"/> 算进行数里）。</para>
        /// </summary>
        public static void Kill(GameObject go, bool immediately = false)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                if (immediately)
                    UnityEngine.Object.DestroyImmediate(go);
                else
                    UnityEngine.Object.Destroy(go);
            }
            catch (Exception)
            {
                try { UnityEngine.Object.Destroy(go); } catch (Exception) { }
            }
        }

        /// <summary>
        /// 给一个节点设宽度比例（相对主画布宽）。**这是本库唯一允许的"尺寸数字"**，
        /// 而且必须是比例而不是像素 —— 原版对话框宽度写死 400 单位，一行里多放几格就放不下。
        /// </summary>
        public static void SetWidthRatio(GameObject go, float ratio)
        {
            if (!UiFinder.IsAlive(go) || ratio <= 0f)
                return;
            try
            {
                var rt = go.transform.TryCast<RectTransform>();
                if (rt != null)
                    rt.sizeDelta = new Vector2(CanvasWidth() * ratio, rt.sizeDelta.y);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// **两栏版式**：让「左栏 + 间距 + 右栏」的**总宽**等于画布宽 × <paramref name="totalRatio"/>，
        /// 并把这个整体重新水平居中。
        ///
        /// <para><b>谁承担增量</b>：左栏宽度**不变**，右栏吸收全部增量 —— 两栏版式里
        /// 左栏是「条目名列表」（宽度够用就行），右栏才是内容区（详情 / 设置行，
        /// 越窄越会截断）。间距（gap）也从当前布局里量出来，保持原样。</para>
        ///
        /// <para><b>适用前提</b>：两栏各自是「锚在父节点水平中心」的定宽节点
        /// （<c>anchorMin.x == anchorMax.x</c>，档案页正是这样）。
        /// 拉伸型锚点下 <c>sizeDelta</c> 不是宽度，这里会**拒绝执行**并记一条日志 ——
        /// 硬改会把右栏推出屏幕，而不是让它们一起居中。</para>
        /// </summary>
        /// <returns>是否真的改了。</returns>
        public static bool SetTwoColumnWidth(GameObject left, GameObject right, float totalRatio)
        {
            if (!UiFinder.IsAlive(left) || !UiFinder.IsAlive(right) || totalRatio <= 0f)
                return false;

            var lrt = left.transform.TryCast<RectTransform>();
            var rrt = right.transform.TryCast<RectTransform>();
            if (lrt == null || rrt == null)
                return false;

            if (!Nearly(lrt.anchorMin.x, lrt.anchorMax.x) || !Nearly(rrt.anchorMin.x, rrt.anchorMax.x))
            {
                UiLog.WarnOnce("uikit-2col-anchor",
                    "两栏不是「水平居中定宽」版式，SetTwoColumnWidth 不适用（已跳过）。");
                return false;
            }

            var lw = WidthOf(lrt);
            var rw = WidthOf(rrt);
            if (lw <= 1f || rw <= 1f)
            {
                UiLog.WarnOnce("uikit-2col-size", "两栏宽度还量不到（布局没结算），宽度未改。");
                return false;
            }

            // 间距 = 右栏左边缘 − 左栏右边缘（两栏都锚在中心，anc 位置就是相对父中心）
            var gap = (rrt.anchoredPosition.x - rw * 0.5f) - (lrt.anchoredPosition.x + lw * 0.5f);
            var total = CanvasWidth() * totalRatio;
            var newRight = total - lw - gap;
            if (newRight <= 1f)
            {
                UiLog.WarnOnce("uikit-2col-fit",
                    "目标总宽 " + Mathf.RoundToInt(total) + " 放不下左右两栏，宽度未改。");
                return false;
            }

            try
            {
                // 整体居中：左栏贴 -total/2，右栏贴 +total/2
                lrt.sizeDelta = new Vector2(lw, lrt.sizeDelta.y);
                lrt.anchoredPosition = new Vector2(-total * 0.5f + lw * 0.5f, lrt.anchoredPosition.y);

                rrt.sizeDelta = new Vector2(newRight, rrt.sizeDelta.y);
                rrt.anchoredPosition = new Vector2(total * 0.5f - newRight * 0.5f, rrt.anchoredPosition.y);

                UiLog.Info("两栏版式宽度已调整：左 " + Mathf.RoundToInt(lw)
                    + " + 间距 " + Mathf.RoundToInt(gap)
                    + " + 右 " + Mathf.RoundToInt(newRight)
                    + " = " + Mathf.RoundToInt(total) + " 单位（画布宽 "
                    + Mathf.RoundToInt(CanvasWidth()) + "）");
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("设置两栏宽度失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private static bool Nearly(float a, float b)
        {
            return Mathf.Abs(a - b) < 0.001f;
        }

        private static float WidthOf(RectTransform rt)
        {
            try
            {
                if (rt.sizeDelta.x > 1f)
                    return rt.sizeDelta.x;
                return rt.rect.width;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        // ------------------------------------------------------------------
        // 描述（验收用）
        // ------------------------------------------------------------------

        /// <summary>
        /// 把一个节点的**真实屏幕矩形**描述成一行文本。
        ///
        /// <para>「节点存在 + 尺寸正确」不等于「看得见」—— 必须用数字：
        /// 矩形要落在屏幕内、行与行 y 不重叠、控件行里只有一个子控件是 on。
        /// 屏幕坐标一律走「局部角点 → <c>GetWorldCorners</c> → <c>WorldToScreenPoint</c>」，
        /// 不要用 <c>rect.size * scaleFactor</c> 自己算（画布缩放会把它算错）。</para>
        /// </summary>
        public static string Describe(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return "（无）";
            try
            {
                var active = go.activeInHierarchy;
                var rt = go.transform.TryCast<RectTransform>();
                if (rt == null)
                    return UiFinder.PathOf(go.transform) + " 非 RectTransform";
                var canvas = go.GetComponentInParent<Canvas>();
                var cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null : canvas.worldCamera;
                // ⚠️ 必须用 Il2CppStructArray：托管 Vector3[] 会被隐式转换成一份**拷贝**，
                // 被调方写的是那份拷贝 —— 读回来永远是全 0。
                var corners = new Il2CppStructArray<Vector3>(4);
                rt.GetWorldCorners(corners);
                var min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                var max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                var w = Mathf.Abs(max.x - min.x);
                var h = Mathf.Abs(max.y - min.y);
                // 屏幕坐标 y 向上；报告里一律给区间，方便人肉判断「落在屏幕内 / 行与行不重叠」。
                return (active ? "on  " : "off ")
                    + "x=" + Mathf.RoundToInt(Mathf.Min(min.x, max.x))
                    + ".." + Mathf.RoundToInt(Mathf.Max(min.x, max.x))
                    + " y=" + Mathf.RoundToInt(Mathf.Min(min.y, max.y))
                    + ".." + Mathf.RoundToInt(Mathf.Max(min.y, max.y))
                    + " (" + Mathf.RoundToInt(w) + "x" + Mathf.RoundToInt(h) + ")"
                    + "  " + UiFinder.PathOf(go.transform);
            }
            catch (Exception e)
            {
                return "描述失败 " + e.GetType().Name + " " + UiFinder.PathOf(go.transform);
            }
        }

        /// <summary>把一个节点子树里所有 TMP 的文本列出来（排障：改了文案但界面没变时先看它）。</summary>
        public static string DescribeTexts(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return "（无）";
            var sb = new StringBuilder(64);
            try
            {
                var texts = go.GetComponentsInChildren<TextMeshProUGUI>(true);
                if (texts == null || texts.Length == 0)
                    return "（子树里没有 TMP）";
                for (var i = 0; i < texts.Length; i++)
                {
                    var t = texts[i];
                    if (t == null)
                        continue;
                    sb.Append('"').Append(Trim(t.text, 16)).Append('"')
                      .Append("@fs=").Append(t.fontSize).Append(' ');
                }
            }
            catch (Exception e)
            {
                return "列文本失败 " + e.GetType().Name;
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>把一段文字截短（dump 用）。</summary>
        public static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            s = s.Replace('\n', ' ');
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
