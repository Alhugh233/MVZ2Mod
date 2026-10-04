using System;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DSHCore.Ui
{
    /// <summary>
    /// 可以从原版**克隆**的 UI 素材。
    ///
    /// <para>名字刻意按"用途"取而不是按"类名"取 —— 调用方不需要知道原版那套
    /// <c>LabeledToggle</c> / <c>TextSlider</c> 的命名。</para>
    /// </summary>
    public enum UiSkinPart
    {
        /// <summary>
        /// 官方「标题 + 正文 + 若干按钮」对话框（<c>CustomDialog</c>）整壳：
        /// <c>Blocker</c>（全屏挡点击）+ <c>Root</c>（<c>VerticalLayoutGroup</c> + <c>ContentSizeFitter</c>）
        /// + <c>Background</c>（框体美术）+ <c>TitleArea/Title</c> + <c>Desc</c> + <c>Buttons</c>。
        ///
        /// <para>克隆体上带一个**可用的 <c>CustomDialog</c> 组件**（<c>Instantiate</c> 会把内部引用
        /// 重定向到克隆体自己），所以可以直接 <c>SetDialog(...)</c>。不需要 <c>Blocker</c> 时删掉它即可。</para>
        /// </summary>
        Dialog,

        /// <summary>官方「输入名字」对话框整壳：标题 + 错误行 + TMP 输入框 + 确认/取消两个按钮。</summary>
        InputDialog,

        /// <summary>
        /// **标题**文本节点（对话框标题用的那一个）。
        ///
        /// <para>⚠️ 它是 **36pt 且开着自动缩放**（`fontSizeMin = 18`、`fontSizeMax = 36`），
        /// 而它所在的 <c>TitleArea</c> 的 <c>LayoutElement.preferredHeight</c> **固定 40**。
        /// 也就是说标题**本来就只能占一行** —— 文字太长时靠自动缩小塞进这一行，
        /// 而不是换行。</para>
        ///
        /// <para>这带来两条硬约束（都是实测踩出来的）：</para>
        /// <list type="number">
        /// <item><b>不要拿它当通用文本用</b>：36pt 在正文尺寸里过大。要放说明文字用 <see cref="Text"/>。</item>
        /// <item><b>不要关掉它（或父节点）的 <c>enableAutoSizing</c></b>：关掉之后长标题只能换成两行，
        /// 在 40px 的格子里就会往上溢出到框体外面。</item>
        /// </list>
        /// </summary>
        Title,

        /// <summary>
        /// **正文**文本节点（对话框描述用的那一个）：原版 24pt、开自动换行。
        ///
        /// <para><b>本库刻意不改它的字号</b> —— 原版字号是为「短文本提示」调的；
        /// 你自己的界面要放说明性大段文字，应该在克隆出来后**显式**改成合适的字号：
        /// <see cref="UiClone.SetFontSize"/>（只改字号，不动文案）。
        /// 统一改素材字号是错的：那会让所有拿这个素材的人都跟着变。</para>
        /// </summary>
        Text,

        /// <summary>文本按钮（<c>TextButton</c>）。</summary>
        Button,

        /// <summary>
        /// 「一行按钮」容器（<c>ButtonRow</c>）：自身带横向 <c>LayoutGroup</c> + 一个
        /// <c>ElementList</c>，用 <c>UpdateButtons(string[])</c> 就能铺满若干个文本按钮。
        ///
        /// <para>它是**唯一一个能在不碰布局代码的前提下横向排一排原生按钮**的原版容器 ——
        /// 需要多个并列按钮时优先用它，而不是自己建容器再算尺寸。</para>
        /// </summary>
        ButtonRow,

        /// <summary>
        /// **设置项行**（<c>OptionWidgets</c>）：原版的"四合一"行容器，一个节点里同时挂着
        /// 复选框 / 滑条 / 下拉框 / 按钮四个控件，运行时**只激活其中一个**。
        ///
        /// <para><b>要复选框 / 滑条 / 下拉框 / 单按钮行，一律用这个，不要单独克隆其中的某个控件。</b>
        /// 三个理由，全都是实测出来的：</para>
        /// <list type="number">
        /// <item>这四个控件的**尺寸契约由这个容器提供**：它自己的 <c>LayoutGroup</c> 是
        /// <c>childControlWidth/Height = 1</c> 且 <c>forceExpand = 0</c>（= 子节点按各自 preferred 尺寸），
        /// 而外部容器（比如对话框的 <c>Root</c>）的 <c>childControlHeight</c> 也是 1，
        /// 于是会来问本行的 preferred 高度。单独克隆某个控件时，它的 preferred 尺寸就没保证了
        /// （<c>LabeledToggle</c> 甚至**没有自己的 <c>LayoutElement</c>**）。</item>
        /// <item>本行上**自带一个已经接好锚点的 <c>TooltipHandler</c>**（锚点是行顶边中点、
        /// <c>pivot = (0.5, 0)</c>、<c>ignoreLayout = 1</c>）—— 提示因此出现在行的**上方**而不是
        /// 压在控件上。自己给别人 AddComponent 一个 <c>TooltipAnchor</c> 是拿不到这个效果的。</item>
        /// <item>四个控件同时存在不会显形：只要用 <see cref="UiOptionRow"/> 设定了类型，
        /// 它就会把另外三个关掉。**注意不要只克隆不设定类型** —— 那份模板里四个都是激活的。</item>
        /// </list>
        ///
        /// <para>用 <see cref="UiOptionRow"/> 来配置它，不要直接 <c>UpdateWidgets</c>
        /// （后者的 <c>value</c> 字段是 <c>Il2CppSystem.Object</c>，需要自己装箱）。</para>
        ///
        /// <para>⚠️ 四个控件里的**下拉框那一支不可用**：碍于技术问题，该组件无法正常工作，
        /// 请自行寻找替代方案，如果你对修复该组件有任何建议，请联系
        /// QoZnoS[https://github.com/QoZnoS]。详见 <see cref="UiOptionRow.SetDropdown"/>。</para>
        /// </summary>
        OptionRow,

        /// <summary>输入框（<c>TMP_InputField</c>）。</summary>
        InputField,

        /// <summary>
        /// **可滚动容器**（<c>ScrollRect</c> + <c>Viewport</c> + <c>Content</c> + 竖向滚动条）。
        ///
        /// <para>来源是主菜单设置界面里那个 `Scroll View`（`OptionsDialog/Dialog/MoreElements/Scroll View`）。
        /// 它是整个原版 UI 里**唯一一个现成的可滚动容器**：</para>
        /// <list type="bullet">
        /// <item>对话框自身的高度是 <c>ContentSizeFitter</c> 撑的，**没有滚动能力** ——
        /// 内容一多就顶出屏幕（见 <c>UI_RESEARCH.md</c> §7.3-25）。这是最大的实测缺口。</item>
        /// <item>自己 <c>AddComponent&lt;ScrollRect&gt;</c> 是不行的：视口需要遮罩 + 一张可用作遮罩的
        /// Sprite，而 <c>AddComponent</c> 拿不到 sprite（整个库的第一原则）。</item>
        /// </list>
        ///
        /// <para>克隆体的 <c>Content</c> 下**运行时生成的分类/选项节点会被删掉**，只留容器本身；
        /// 把自己的行塞进 <c>Content</c> 即可（用 <see cref="UiList"/> 最省事）。</para>
        ///
        /// <para><b>视口的 <c>Mask</c> 会被换成 <c>RectMask2D</c></b>（见
        /// <c>SanitizeScrollView</c>）：<c>Mask</c> 走模板缓冲，会让 TMP 的子网格
        /// （<c>TMP_SubMeshUI</c>）去申请/释放**共享**的 Stencil 材质实例，
        /// 复制出去的克隆体和模板于是共用同一份 —— 之后无论哪一边被销毁，
        /// 都会把那份材质一起销毁，另一边再被 <c>Disable</c> 时就抛
        /// <c>NullReferenceException</c>（`Player.log`：
        /// <c>Material.GetFloat ← TMP_SubMeshUI.UpdateMaterial ← OnDisable</c>）。
        /// 矩形的滚动视口本来就只需要矩形裁剪，<c>RectMask2D</c> 不需要 Sprite、不碰模板缓冲，
        /// 外观与行为一致。</para>
        /// </summary>
        ScrollView,
    }

    /// <summary>
    /// 原版 UI 素材的**克隆源**。本库最核心的一层。
    ///
    /// <para><b>为什么必须有它：</b>原版 UI 全部是 prefab 驱动，控件依赖私有
    /// <c>[SerializeField]</c> 引用（字体、Sprite、点击音效），所以
    /// <c>AddComponent</c> 造出来的控件必崩或必无效；而 UI prefab 又不在 Addressables 条目里，
    /// mod 侧加载不到。<b>「克隆场景里已有的活节点」是唯一可行路径</b> —— 本类型把
    /// "找谁克隆、怎么找到、克隆完怎么修"固化下来。
    /// 完整论证见 <c>UI_RESEARCH.md</c>。</para>
    ///
    /// <para><b>工作方式：</b>首次使用时把各素材克隆一份到自己的「模板架」（一个
    /// <c>SetActive(false)</c> 的隐藏节点）并做规范化，之后 <see cref="Clone"/> 一律从模板架克隆。
    /// 这样做有两个好处：模板不会被游戏运行时改动（对话框当前显示的文案、多出来的按钮行都不会渗进来），
    /// 而且不会出现"拿自己的克隆体当模板"的自指问题。</para>
    ///
    /// <para><b>线程：</b>全部成员只允许在 Unity 主线程调用。</para>
    ///
    /// <para><b>典型用法：</b></para>
    /// <code>
    /// var shell = UiSkin.Clone(UiSkinPart.Dialog, UiHost.Resolve(UiLayer.DialogLayer), "MyPanel");
    /// var dlg   = shell.GetComponent&lt;MVZ2.UI.CustomDialog&gt;();
    /// dlg.SetDialog("标题", "正文", options, onSelect);   // 标题会被自动缩放塞进一行
    ///
    /// var row = UiOptionRow.Create(root, "MyToggle");     // 设置项行走 OptionRow
    /// row.SetToggle("开关", true, v => { });
    /// </code>
    /// </summary>
    public static class UiSkin
    {
        private const string HolderName = "DSH_UiTemplates";

        private static readonly int PartCount = Enum.GetValues(typeof(UiSkinPart)).Length;
        private static readonly GameObject[] Templates = new GameObject[PartCount];
        private static readonly string[] Sources = new string[PartCount];

        private static GameObject _holder;
        private static bool _prepared;

        // ------------------------------------------------------------------
        // 对外状态
        // ------------------------------------------------------------------

        /// <summary>模板架是否还活着（换场景后可能被销毁）。</summary>
        private static bool HolderAlive
        {
            get { return UiFinder.IsAlive(_holder); }
        }

        /// <summary>核心素材（对话框壳 / 正文文本 / 按钮 / 设置项行）是否都已就绪。</summary>
        public static bool IsReady
        {
            get
            {
                return HolderAlive
                    && Get(UiSkinPart.Dialog) != null
                    && Get(UiSkinPart.Text) != null
                    && Get(UiSkinPart.Button) != null;
            }
        }

        /// <summary>某个素材当前是否可用。</summary>
        public static bool CanProvide(UiSkinPart part)
        {
            return Get(part) != null;
        }

        /// <summary>
        /// **量一个素材的出厂字号**（克隆一份读出来再销毁，不动模板架）。
        ///
        /// <para>存在的理由："原版这个素材到底多大"必须能**问**而不是靠文档里写死的数字。
        /// 想知道自己该不该改字号、或者要写"原版默认 = N pt"这类说明，就用它读实时值。</para>
        /// </summary>
        public static UiClone.FontInfo MeasureFont(UiSkinPart part)
        {
            GameObject go = null;
            try
            {
                go = Clone(part, null, "DSH_Measure");
                return UiClone.DescribeFont(go);
            }
            catch (Exception e)
            {
                UiLog.Warn("量字号失败（" + part + "）：" + e.GetType().Name);
                return null;
            }
            finally
            {
                if (UiFinder.IsAlive(go))
                {
                    try { UnityEngine.Object.Destroy(go); }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>
        /// 素材解析报告：每个素材来自场景里的哪个节点，或"未找到"。
        /// 出问题时先看这个（比翻日志快）。
        /// </summary>
        public static string Report
        {
            get
            {
                var sb = new StringBuilder(700);
                sb.Append("模板架：").Append(HolderAlive ? UiFinder.PathOf(_holder.transform) : "不存在").Append('\n');
                foreach (UiSkinPart part in Enum.GetValues(typeof(UiSkinPart)))
                {
                    var idx = (int)part;
                    var ok = Templates[idx] != null;
                    sb.Append(ok ? "  [OK  ] " : "  [MISS] ");
                    sb.Append(part.ToString().PadRight(12));
                    sb.Append(Sources[idx] ?? "（未解析）");
                    sb.Append('\n');
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// 精简版报告：只有素材名与状态，**不含来源路径**。
        ///
        /// <para>给「直接显示在界面上」用的。完整报告里的路径会很长（十几个节点名），
        /// 塞进一个正文节点会把它撑到屏幕外 —— 而界面上的框体是
        /// <c>ContentSizeFitter</c> 撑的，没有滚动能力。</para>
        /// </summary>
        public static string ReportCompact
        {
            get
            {
                var sb = new StringBuilder(320);
                sb.Append("模板架：").Append(HolderAlive ? "已就绪" : "未建立").Append('\n');
                foreach (UiSkinPart part in Enum.GetValues(typeof(UiSkinPart)))
                {
                    sb.Append(Templates[(int)part] != null ? "[OK]   " : "[MISS] ");
                    sb.Append(part.ToString()).Append('\n');
                }
                return sb.ToString();
            }
        }

        // ------------------------------------------------------------------
        // 取用
        // ------------------------------------------------------------------

        /// <summary>取素材本体（**只读**：可用来读 <c>LayoutElement</c> 尺寸做参考，不要改它、不要挂到界面上）。</summary>
        public static GameObject Template(UiSkinPart part)
        {
            Ensure();
            return Get(part);
        }

        /// <summary>
        /// 取一个素材的克隆体。素材缺失时返回 null 并只记一次警告（不抛）。
        /// 克隆体已规范化：已 <c>SetActive(true)</c>、已打己方标记。
        /// <c>enableAutoSizing</c> **保持素材原样**（见 <see cref="UiClone.Normalize"/>）。
        /// </summary>
        public static GameObject Clone(UiSkinPart part, Transform parent, string name = null)
        {
            var tpl = Template(part);
            if (tpl == null)
                return null;
            var go = UiClone.Instantiate(tpl, parent, name);
            CheckElementLists(go, part);
            return go;
        }

        /// <summary>
        /// **体检：克隆体里的 <c>ElementList</c> / <c>ElementListUI</c> 是否还认得自己的模板。**
        ///
        /// <para>为什么要查这一条：<c>ElementList.Awake</c> 第一件事就是
        /// <c>_template.transform.parent == _listRoot</c> —— <c>_template</c> 一旦为空/已销毁，
        /// 任何 <c>Instantiate</c> 到它都会在 Awake 里抛 NullReferenceException
        /// （<c>Player.log</c>：<c>ElementList.Awake ← Instantiate</c>），
        /// 而且**游戏自己的对话框也会跟着报**，因为它用的是同一个 prefab 契约。</para>
        ///
        /// <para>每个素材只记一次，所以它不会刷屏；出问题时去 <c>UiSkin.Report</c> 或日志里找。</para>
        /// </summary>
        private static void CheckElementLists(GameObject clone, UiSkinPart part)
        {
            if (!UiFinder.IsAlive(clone))
                return;
            try
            {
                var lists = clone.GetComponentsInChildren<PVZEngine.Tools.Unity.ElementList>(true);
                if (lists == null)
                    return;
                for (var i = 0; i < lists.Length; i++)
                {
                    var l = lists[i];
                    if (l == null)
                        continue;
                    GameObject tpl = null;
                    try { tpl = l.getTemplate(); }
                    catch (Exception) { }
                    if (UiFinder.IsAlive(tpl))
                        continue;
                    UiLog.WarnOnce("el-tpl-" + part,
                        "克隆体里的 ElementList 模板不可用（part=" + part + " @ "
                        + UiFinder.PathOf(l.transform) + "）—— 它是 ElementList.Awake 空指针的来源。");
                }

                var uiLists = clone.GetComponentsInChildren<PVZEngine.Tools.Unity.ElementListUI>(true);
                if (uiLists == null)
                    return;
                for (var i = 0; i < uiLists.Length; i++)
                {
                    var l = uiLists[i];
                    if (l == null)
                        continue;
                    // 注意：ElementListUI 的 getTemplate() 返回 RectTransform（ElementList 才是 GameObject）。
                    RectTransform tpl = null;
                    try { tpl = l.getTemplate(); }
                    catch (Exception) { }
                    if (UiFinder.IsAlive(tpl))
                        continue;
                    UiLog.WarnOnce("elu-tpl-" + part,
                        "克隆体里的 ElementListUI 模板不可用（part=" + part + " @ "
                        + UiFinder.PathOf(l.transform) + "）—— 它会让 SetDialog/updateList 直接失败。");
                }
            }
            catch (Exception) { }
        }

        /// <summary>强制重新解析全部素材（换了场景、或要排障时用）。</summary>
        public static bool Prepare(bool force = false)
        {
            if (!force && IsReady)
                return true;

            if (!UiGame.IsReady)
            {
                UiLog.WarnOnce("game-ui-not-ready", "游戏 UI 还没起来（主菜单之前），暂时无法解析 UI 素材。");
                return false;
            }

            try
            {
                if (force || !HolderAlive)
                {
                    DestroyHolder();
                    UiLog.ResetOnce();
                    if (!CreateHolder())
                        return false;
                }

                Resolve(UiSkinPart.Dialog);
                Resolve(UiSkinPart.InputDialog);
                Resolve(UiSkinPart.Title);
                Resolve(UiSkinPart.Text);
                Resolve(UiSkinPart.Button);
                Resolve(UiSkinPart.ButtonRow);
                Resolve(UiSkinPart.OptionRow);
                Resolve(UiSkinPart.InputField);
                Resolve(UiSkinPart.ScrollView);

                _prepared = true;
                return IsReady;
            }
            catch (Exception e)
            {
                UiLog.Warn("解析 UI 素材失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private static void Ensure()
        {
            if (!_prepared || !HolderAlive)
                Prepare();
        }

        private static GameObject Get(UiSkinPart part)
        {
            var idx = (int)part;
            if (idx < 0 || idx >= PartCount)
                return null;
            var go = Templates[idx];
            return UiFinder.IsAlive(go) ? go : null;
        }

        // ------------------------------------------------------------------
        // 模板架
        // ------------------------------------------------------------------

        private static bool CreateHolder()
        {
            // 宿主取「官方对话框所在的那一层」：同一个画布、同一套缩放，
            // 克隆体挂到别处就不会是这个尺寸体系了。
            var parent = UiHost.Resolve(UiLayer.DialogLayer);
            if (parent == null)
            {
                UiLog.WarnOnce("no-host", "找不到可用的 UI 宿主，UI 素材无法准备。");
                return false;
            }

            _holder = UiHost.CreateNode(HolderName, parent);
            if (!HolderAlive)
                return false;

            // 关掉：不渲染、不参与父级布局（LayoutGroup 会跳过未激活子节点）。
            // ⚠️ 挂载自己的面板时**不要**挂到它下面 —— 挂进去整支 inactive，
            //    LayoutGroup 一次都不会跑，子节点会全停在 100×100 @ 0,0。
            try { _holder.SetActive(false); }
            catch (Exception) { }

            return true;
        }

        private static void DestroyHolder()
        {
            for (var i = 0; i < PartCount; i++)
            {
                Templates[i] = null;
                Sources[i] = null;
            }
            _prepared = false;

            if (!UiFinder.IsAlive(_holder))
                return;
            try { UnityEngine.Object.Destroy(_holder); }
            catch (Exception) { }
            _holder = null;
        }

        // ------------------------------------------------------------------
        // 逐个素材的解析
        // ------------------------------------------------------------------

        private static void Resolve(UiSkinPart part)
        {
            var idx = (int)part;
            if (Templates[idx] != null && UiFinder.IsAlive(Templates[idx]))
                return;

            GameObject source = null;
            string sourcePath = null;

            try
            {
                switch (part)
                {
                    case UiSkinPart.Dialog:
                        source = SourceDialog(out sourcePath);
                        break;
                    case UiSkinPart.InputDialog:
                        source = SourceInputDialog(out sourcePath);
                        break;
                    case UiSkinPart.Title:
                        source = SourceTitle(out sourcePath);
                        break;
                    case UiSkinPart.Text:
                        source = SourceText(out sourcePath);
                        break;
                    case UiSkinPart.Button:
                        source = SourceButton(out sourcePath);
                        break;
                    case UiSkinPart.ButtonRow:
                        source = SourceButtonRow(out sourcePath);
                        break;
                    case UiSkinPart.OptionRow:
                        source = SourceOptionRow(out sourcePath);
                        break;
                    case UiSkinPart.InputField:
                        source = SourceInputField(out sourcePath);
                        break;
                    case UiSkinPart.ScrollView:
                        source = SourceScrollView(out sourcePath);
                        break;
                }
            }
            catch (Exception e)
            {
                UiLog.Warn("解析素材 " + part + " 时异常：" + e.GetType().Name + ": " + e.Message);
                return;
            }

            if (!UiFinder.IsAlive(source))
            {
                Sources[idx] = null;
                UiLog.WarnOnce("miss:" + part,
                    "素材 " + part + " 暂时找不到（场景里还没有对应的原版节点）。" +
                    "如果是在标题界面，进主菜单或关卡后会自动就绪。");
                return;
            }

            var holderTransform = _holder == null ? null : _holder.transform;

            // 先克隆**不激活**，让 Fixup 能在任何 Awake/OnEnable 跑之前做结构性修补
            // （最典型的是可滚动容器 Content 上那个 _template 为空的 ElementList ——
            //  它的 Awake 会抛 NullReferenceException，见 SanitizeScrollView）。
            var clone = UiClone.Instantiate(source, holderTransform, HolderName + "_" + part, false);
            if (clone == null)
                return;

            Fixup(clone, part);
            UiClone.Activate(clone);

            Templates[idx] = clone;
            Sources[idx] = sourcePath ?? UiFinder.PathOf(source.transform);
        }

        // ---- 各素材的来源 ------------------------------------------------

        private static GameObject SourceDialog(out string path)
        {
            var dlg = UiGame.Dialog;
            if (dlg == null)
            {
                path = null;
                return null;
            }
            path = UiFinder.PathOf(dlg.transform);
            return dlg.gameObject;
        }

        private static GameObject SourceInputDialog(out string path)
        {
            var dlg = UiGame.InputDialog;
            if (dlg == null)
            {
                path = null;
                return null;
            }
            path = UiFinder.PathOf(dlg.transform);
            return dlg.gameObject;
        }

        private static GameObject SourceTitle(out string path)
        {
            var dlg = UiGame.Dialog;
            if (dlg != null)
            {
                try
                {
                    var t = dlg.title;
                    if (UiFinder.IsAlive(t))
                    {
                        path = UiFinder.PathOf(t.transform);
                        return t.gameObject;
                    }
                }
                catch (Exception) { }
            }
            return FallbackText(out path);
        }

        private static GameObject SourceText(out string path)
        {
            var dlg = UiGame.Dialog;
            if (dlg != null)
            {
                try
                {
                    var t = dlg.desc;
                    if (UiFinder.IsAlive(t))
                    {
                        path = UiFinder.PathOf(t.transform) + "（对话框正文，24pt / 自动换行）";
                        return t.gameObject;
                    }
                }
                catch (Exception) { }
            }
            return FallbackText(out path);
        }

        private static GameObject FallbackText(out string path)
        {
            var tmp = UiFinder.FindOne<TextMeshProUGUI>();
            if (tmp == null)
            {
                path = null;
                return null;
            }
            path = UiFinder.PathOf(tmp.transform) + "（兜底：任意 TMP 文本）";
            return tmp.gameObject;
        }

        /// <summary>
        /// 按钮行的来源：<c>CustomDialog.buttonRowList</c>（ElementListUI）的模板节点。
        ///
        /// <para>⚠️ 它是 <c>ElementListUI</c> 的模板，平时被 <c>ElementList.Awake</c> 关着，
        /// 所以必须靠 <c>getTemplate()</c> 拿，不能靠遍历找“第一个 active 的 ButtonRow”。</para>
        /// </summary>
        private static GameObject SourceButtonRow(out string path)
        {
            path = null;
            var dlg = UiGame.Dialog;
            if (dlg != null)
            {
                try
                {
                    var rows = dlg.buttonRowList;
                    if (UiFinder.IsAlive(rows))
                    {
                        var tpl = rows.getTemplate();
                        if (UiFinder.IsAlive(tpl))
                        {
                            path = UiFinder.PathOf(tpl.transform);
                            return tpl.gameObject;
                        }
                    }
                }
                catch (Exception) { }
            }

            var loose = UiFinder.FindOne<MVZ2.UI.ButtonRow>();
            if (loose == null)
                return null;
            path = UiFinder.PathOf(loose.transform) + "（兜底：场景内任意 ButtonRow）";
            return loose.gameObject;
        }

        /// <summary>
        /// 按钮的来源：官方对话框的按钮行模板。
        /// <c>CustomDialog.buttonRowList</c>（ElementListUI）→ 模板 <c>ButtonRow</c>
        /// → <c>buttonList</c>（ElementList）→ 模板 <c>TextButton</c>。
        /// 这一路全用官方的 <c>getTemplate()</c>，不依赖节点名字。
        /// </summary>
        private static GameObject SourceButton(out string path)
        {
            path = null;
            var dlg = UiGame.Dialog;
            if (dlg != null)
            {
                try
                {
                    var rows = dlg.buttonRowList;
                    if (UiFinder.IsAlive(rows))
                    {
                        var rowTpl = rows.getTemplate();
                        if (rowTpl != null)
                        {
                            var row = rowTpl.GetComponent<MVZ2.UI.ButtonRow>();
                            if (row != null)
                            {
                                var list = row.buttonList;
                                if (list != null)
                                {
                                    var tpl = list.getTemplate();
                                    if (UiFinder.IsAlive(tpl))
                                    {
                                        path = UiFinder.PathOf(tpl.transform);
                                        return tpl;
                                    }
                                }
                            }
                            // 退一步：按钮行模板底下任意一个 TextButton
                            var any = UiFinder.FindChildComponent<MVZ2.UI.TextButton>(rowTpl, 0);
                            if (any != null)
                            {
                                path = UiFinder.PathOf(any.transform) + "（兜底：按钮行内的 TextButton）";
                                return any.gameObject;
                            }
                        }
                    }
                }
                catch (Exception) { }
            }

            var loose = UiFinder.FindOne<MVZ2.UI.TextButton>();
            if (loose == null)
                return null;
            path = UiFinder.PathOf(loose.transform) + "（兜底：场景内任意 TextButton）";
            return loose.gameObject;
        }

        /// <summary>
        /// 设置项行的来源：<c>OptionWidgets</c> 本身（**四合一容器**）。
        ///
        /// <para>注意这里取得的是一整个行，调用方必须用 <see cref="UiOptionRow"/> 设定类型
        /// 把其余三个控件关掉 —— 模板里四个都是激活的。</para>
        /// </summary>
        private static GameObject SourceOptionRow(out string path)
        {
            path = null;
            var widgets = UiFinder.FindOne<MVZ2.UI.OptionWidgets>();
            if (widgets == null)
                return null;

            path = UiFinder.PathOf(widgets.transform)
                 + "（四合一设置项行；用 UiOptionRow 指定类型）";
            return widgets.gameObject;
        }

        private static GameObject SourceInputField(out string path)
        {
            var dlg = UiGame.InputDialog;
            if (dlg != null)
            {
                try
                {
                    var input = UiFinder.FindChildComponent<TMP_InputField>(dlg.transform, 0);
                    if (input != null)
                    {
                        path = UiFinder.PathOf(input.transform);
                        return input.gameObject;
                    }
                }
                catch (Exception) { }
            }

            var loose = UiFinder.FindOne<TMP_InputField>();
            if (loose == null)
            {
                path = null;
                return null;
            }
            path = UiFinder.PathOf(loose.transform) + "（兜底：场景内任意输入框）";
            return loose.gameObject;
        }

        /// <summary>
        /// 可滚动容器的来源：主菜单设置界面里那个 <c>Scroll View</c>。
        ///
        /// <para><b>怎么找：</b>先从设置项行（<c>OptionWidgets</c>）往上走，第一个带 <c>ScrollRect</c>
        /// 的祖先就是它 —— 这样不需要写死节点路径，而且拿到的必然是"跟设置项同一套"的那个
        /// （不会误拿下拉框模板里那个 <c>ScrollRect</c>：那个是 OptionWidgets 的**兄弟子树**，
        /// 不是祖先）。找不到时才退回到"场景里任意 ScrollRect"。</para>
        /// </summary>
        private static GameObject SourceScrollView(out string path)
        {
            path = null;

            // ① 从 OptionWidgets 往上找带 ScrollRect 的祖先
            try
            {
                var widgets = UiFinder.FindOne<MVZ2.UI.OptionWidgets>();
                if (widgets != null)
                {
                    var t = widgets.transform.parent;
                    while (t != null)
                    {
                        var sr = t.GetComponent<ScrollRect>();
                        if (sr != null)
                        {
                            path = UiFinder.PathOf(t) + "（设置界面的可滚动容器）";
                            return t.gameObject;
                        }
                        t = t.parent;
                    }
                }
            }
            catch (Exception) { }

            // ② 兜底：场景里第一个 ScrollRect（可能是下拉框模板里的那个，会记在路径里以便辨认）
            var loose = UiFinder.FindOne<ScrollRect>();
            if (loose == null)
                return null;

            path = UiFinder.PathOf(loose.transform) + "（兜底：场景内任意可滚动容器）";
            return loose.gameObject;
        }

        // ---- 克隆后的针对性修补 -------------------------------------------

        private static void Fixup(GameObject clone, UiSkinPart part)
        {
            switch (part)
            {
                case UiSkinPart.Dialog:
                    SanitizeDialog(clone, true);
                    break;

                case UiSkinPart.InputDialog:
                    // 输入框对话框同样有框体美术，且克隆出来时通常处于隐藏态。
                    UiClone.ForceGraphicsVisible(clone);
                    ClearInputDialogState(clone);
                    break;

                case UiSkinPart.OptionRow:
                    // 模板里四个控件都是激活的，而且行上的事件转发靠 OptionWidgets.Awake 里的订阅。
                    // 所以**这里什么都不做**：由 UiOptionRow 设定类型时再关掉其余三个。
                    // 一旦在这里裁剪子节点，UiOptionRow 就拿不到那四个引用了。
                    break;

                case UiSkinPart.ScrollView:
                    SanitizeScrollView(clone);
                    break;
            }
        }

        /// <summary>
        /// 把克隆来的对话框清成"干净起点"：
        /// 清掉继承来的选项回调与文案、删掉运行时多出来的按钮行、让框体美术显形。
        /// </summary>
        private static void SanitizeDialog(GameObject clone, bool forceGraphics)
        {
            try
            {
                var dlg = clone.GetComponent<MVZ2.UI.CustomDialog>();
                if (dlg != null)
                {
                    // Instantiate 会把实例事件字段一起复制过来 —— 里面装的是**游戏自己的**
                    // 回调闭包（MainSceneUI.ShowDialog 设进去的）。不清掉的话，点我们的按钮
                    // 会去执行游戏那段逻辑。
                    try { dlg.OnOptionSelect = null; }
                    catch (Exception) { }
                }

                var title = FindTmp(clone, "Title");
                if (title != null)
                    title.text = string.Empty;
                var desc = FindTmp(clone, "Desc");
                if (desc != null)
                    desc.text = string.Empty;

                PruneListChildren(clone);
            }
            catch (Exception e)
            {
                UiLog.Warn("清理对话框克隆体失败：" + e.GetType().Name);
            }

            if (forceGraphics)
                UiClone.ForceGraphicsVisible(clone);
        }

        private static TextMeshProUGUI FindTmp(GameObject root, string childName)
        {
            var t = UiFinder.FindChild(root.transform, childName);
            if (t == null)
                return null;
            try { return t.GetComponent<TextMeshProUGUI>(); }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 删掉对话框里"运行时多出来的"按钮行，只留模板。
        /// 游戏复用同一个 <c>CustomDialog</c> 实例，上一次弹的按钮行还留在节点里；
        /// 它们的文案与监听器都指向过去那次对话，留着会串味。
        /// </summary>
        private static void PruneListChildren(GameObject dialogRoot)
        {
            try
            {
                var dlg = dialogRoot.GetComponent<MVZ2.UI.CustomDialog>();
                if (dlg == null)
                    return;
                var list = dlg.buttonRowList;
                if (!UiFinder.IsAlive(list))
                    return;

                var tpl = list.getTemplate();
                var root = list._listRoot;
                if (root == null)
                    return;

                // ⚠️ **认不出模板就什么都不删。**
                // 删错一个节点的代价是致命的：ElementList 的 `_template` 是个序列化引用，
                // 一旦它指向的节点被销毁，之后游戏自己调 updateList / Instantiate 就会在
                // ElementList.Awake 里抛 NullReferenceException（实测在 Player.log 里见过）。
                // 而"少删几个过去的按钮"最多是残留一次性视觉问题。
                // 认不出来的情况是真实存在的：游戏自己也会动这个列表（模板被隐藏/换掉）。
                if (tpl == null)
                {
                    UiLog.WarnOnce("prune-notpl",
                        "对话框的按钮行列表认不出模板（getTemplate() 为 null），"
                        + "为安全起见保留其全部子节点。");
                    return;
                }

                var childCount = root.childCount;
                for (var i = childCount - 1; i >= 0; i--)
                {
                    var child = root.GetChild(i);
                    if (child == null)
                        continue;
                    if (child == tpl.transform)
                        continue;
                    if (child.GetComponent<MVZ2.UI.ButtonRow>() == null)
                        continue;    // 只删"过去的按钮行"，别的一律不碰
                    try { UnityEngine.Object.Destroy(child.gameObject); }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 把克隆来的可滚动容器清成"干净起点"。
        ///
        /// <para>三件事：</para>
        /// <list type="number">
        /// <item><b>删掉 Content 下运行时生成的节点</b>（设置界面的分类与选项行）——
        /// 它们带着游戏自己的回调闭包，留着就会串味（跟对话框按钮行同一个道理）。</item>
        /// <item><b>修正滚动方向与视口图形</b>：默认只留竖向滚动；视口上那张图是 <c>Mask</c> 的
        /// 遮罩素材，<c>enabled=0</c> 时遮罩会失效（内容会溢出到容器外面去）。</item>
        /// <item><b>不碰 Content 的布局组件</b>：它必须保持 prefab 里那套
        /// <c>VerticalLayoutGroup + ContentSizeFitter</c>，否则内容不会被撑开、也就滚不动。</item>
        /// </list>
        /// </summary>
        private static void SanitizeScrollView(GameObject clone)
        {
            try
            {
                var sr = clone.GetComponent<ScrollRect>();
                if (sr == null)
                    sr = clone.GetComponentInChildren<ScrollRect>(true);
                if (sr == null)
                {
                    UiLog.Warn("可滚动容器克隆体上没有 ScrollRect。");
                    return;
                }

                // ① 视口遮罩换掉：Mask → RectMask2D。**必须放在删除内容之前**。
                //
                //    原因：Mask 走模板缓冲，TMP 的子网格（TMP_SubMeshUI）为此会申请一份**共享的**
                //    Stencil 材质实例（TMP_MaterialManager）。Instantiate 只会复制这个引用，
                //    于是克隆体和模板（就是游戏自己那个设置界面）共用同一份材质；之后无论哪一边
                //    先被销毁，都会把这份材质一起销毁，另一边再被 Disable 时就抛
                //    NullReferenceException（Player.log：
                //    Material.GetFloat ← TMP_SubMeshUI.UpdateMaterial ← MaskableGraphic.OnDisable，
                //    或经 Mask.OnDisable → MaskUtilities.NotifyStencilStateChanged 触发的一串）。
                //    矩形的滚动视口本来就只需要矩形裁剪，RectMask2D 不需要 Sprite、不碰模板缓冲，
                //    所以这里直接换掉，而不是去修材质共享。
                ReplaceStencilMaskWithRectMask(sr);

                // ② 摘掉容器里**属于游戏自己的"行生成器"组件**（ElementList / ElementListUI）。
                //
                //    实测（探针日志）：`Scroll View/Viewport/Content` 上挂着一个
                //    `ElementList`，而它的 `_template` 是**空**的 —— 那是原版设置界面在运行时
                //    临时塞进去的行模板，我们克隆的时候它还没（或已经不再）存在。
                //    这个组件的 `Awake` 第一件事就是 `_template.transform`，于是只要克隆体
                //    一旦在激活的父链下变活跃就抛 NullReferenceException
                //    （Player.log：`ElementList.Awake ← Instantiate`，而且调用方是 mod 时
                //     栈里连调用者都不会出现 —— 极难定位）。
                //
                //    我们的 Content 完全由它自带的 VerticalLayoutGroup 排版，不需要这个组件；
                //    留着只会有害（它还会在 `updateList` 时去 Instantiate 一个空模板）。
                StripRowSpawners(clone);

                try
                {
                    sr.horizontal = false;
                    sr.vertical = true;
                    if (sr.movementType == ScrollRect.MovementType.Unrestricted)
                        sr.movementType = ScrollRect.MovementType.Clamped;
                }
                catch (Exception) { }

                var content = FindScrollContent(sr);
                if (content == null)
                {
                    UiLog.Warn("可滚动容器里找不到 Content 节点。");
                    return;
                }

                // 删掉运行时生成的子节点（保留 Content 自身）。
                var count = content.childCount;
                for (var i = count - 1; i >= 0; i--)
                {
                    var child = content.GetChild(i);
                    if (child == null)
                        continue;
                    try { UnityEngine.Object.Destroy(child.gameObject); }
                    catch (Exception) { }
                }

                // Content 必须有布局组，否则加进去的行全部堆在同一个位置。
                if (content.GetComponent<UnityEngine.UI.LayoutGroup>() == null)
                {
                    UiLog.WarnOnce("sv-nolayout",
                        "可滚动容器的 Content 上没有 LayoutGroup —— 加进去的行会重叠。"
                        + "请在 Content 上补一个 VerticalLayoutGroup（原版那份本该自带）。");
                }

                // ③ 把"只会画成白块"的图关掉（**视觉回归修复**，理由见方法注释）。
                NeutralizeSpriteLessBackdrops(clone);
            }
            catch (Exception e)
            {
                UiLog.Warn("清理可滚动容器失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 关掉可滚动容器克隆体里**不该被画出来**的图。
        ///
        /// <para><b>这是"列表背景变成一块白/亮色"的根因修复。</b>两条都来自原版 prefab 的用法：</para>
        /// <list type="number">
        /// <item><b>视口那张图其实不是"背景"，是 <c>Mask</c> 的遮罩素材。</b>
        /// <c>Mask.showMaskGraphic = false</c> 时 Unity 靠改材质把这张图**画成透明**，
        /// 只让它写模板缓冲。我们换成 <c>RectMask2D</c> 之后它变回普通 <c>Image</c>，
        /// 于是开始老老实实画自己 —— 于是列表上面多出一块亮色。</item>
        /// <item><b><c>sprite == null</c> 的 <c>Image</c> 在 uGUI 里会画成一个纯白矩形</b>
        /// （用默认 UI 材质，不是"什么都不画"）。ScrollRect 自带的那个装饰性
        /// <c>Background</c> 就是这种情况。</item>
        /// </list>
        ///
        /// <para><b>为什么不是简单地 <c>enabled = false</c>：</b>关掉 <c>Graphic</c> 会连
        /// 射线一起关掉（原版有节点靠这层透明图当拖动/点击承接面）。所以：
        /// <c>raycastTarget == true</c> 的只把 <b>alpha 清 0</b>（不可见但仍可命中），
        /// <c>raycastTarget == false</c> 的直接 <c>enabled = false</c>。</para>
        /// </summary>
        private static void NeutralizeSpriteLessBackdrops(GameObject clone)
        {
            try
            {
                var sr = clone.GetComponent<ScrollRect>();
                if (sr == null)
                    sr = clone.GetComponentInChildren<ScrollRect>(true);

                // ① 视口那张（原来的遮罩素材）无条件处理：它有 sprite，但本来就不该被画。
                if (sr != null)
                {
                    var viewport = sr.viewport;
                    if (UiFinder.IsAlive(viewport))
                    {
                        var vpImg = viewport.GetComponent<Image>();
                        if (vpImg != null)
                            NeutralizeGraphic(vpImg, "视口遮罩素材（原 Mask 的 graphic）");
                    }
                }

                // ② 其余 sprite 为空的装饰图。
                var images = clone.GetComponentsInChildren<Image>(true);
                if (images == null)
                    return;
                for (var i = 0; i < images.Length; i++)
                {
                    var img = images[i];
                    if (img == null)
                        continue;
                    try
                    {
                        if (img.sprite != null)
                            continue;
                        NeutralizeGraphic(img, "sprite 为空的图（uGUI 会画成白矩形）");
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("sv-white", "关掉白块图失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void NeutralizeGraphic(Image img, string reason)
        {
            try
            {
                if (!img.enabled)
                    return;
                if (img.raycastTarget)
                {
                    var c = img.color;
                    img.color = new Color(c.r, c.g, c.b, 0f);
                    UiLog.WarnOnce("sv-hide-keepray-" + img.name,
                        "把 " + UiFinder.PathOf(img.transform) + " 的透明度清 0（" + reason
                        + "）；保留 raycastTarget 以免影响原版的拖动/点击承接。");
                }
                else
                {
                    img.enabled = false;
                    UiLog.WarnOnce("sv-hide-" + img.name,
                        "关掉了 " + UiFinder.PathOf(img.transform) + "（" + reason + "）。");
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 图形 dump：把一棵可滚动容器子树里的每个 <c>Image</c> 都打出来。
        ///
        /// <para>用途是**对比** —— 我们克隆体 vs 原版设置界面里那个仍然活着的容器
        /// （<see cref="FindVanillaScrollView"/>）。"看起来变了"必须变成"哪个节点的
        /// enabled/sprite/alpha 不一样了"，否则就只能靠猜。</para>
        /// </summary>
        public static string DumpScrollViewGraphics(string label, GameObject scrollRoot)
        {
            var sb = new System.Text.StringBuilder(600);
            sb.Append(label).Append("：");
            if (!UiFinder.IsAlive(scrollRoot))
            {
                sb.Append("（不存在）");
                return sb.ToString();
            }
            sb.Append(UiFinder.PathOf(scrollRoot.transform)).Append('\n');

            try
            {
                var images = scrollRoot.GetComponentsInChildren<Image>(true);
                if (images == null)
                {
                    sb.Append("  （无 Image）");
                    return sb.ToString();
                }
                for (var i = 0; i < images.Length; i++)
                {
                    var img = images[i];
                    if (img == null)
                        continue;
                    try
                    {
                        var c = img.color;
                        sb.Append("  ").Append(img.transform.name)
                          .Append("  on=").Append(img.enabled ? 1 : 0)
                          .Append(" sprite=").Append(img.sprite == null ? "null" : img.sprite.name)
                          .Append(" a=").Append(c.a.ToString("0.##"))
                          .Append(" rgb=").Append(c.r.ToString("0.##")).Append(',')
                          .Append(c.g.ToString("0.##")).Append(',').Append(c.b.ToString("0.##"))
                          .Append(" raycast=").Append(img.raycastTarget ? 1 : 0)
                          .Append("  ").Append(UiFinder.PathOf(img.transform))
                          .Append('\n');
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception e)
            {
                sb.Append("  dump 失败：").Append(e.GetType().Name);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 场景里**原版那个**可滚动容器（主菜单设置界面里的 <c>Scroll View</c>，仍然活着、未被克隆过）。
        /// 给"对比原版观感"用（见 <see cref="DumpScrollViewGraphics"/>）。找不到返回 null。
        /// </summary>
        public static GameObject FindVanillaScrollView()
        {
            string _;
            return SourceScrollView(out _);
        }

        /// <summary>
        /// 把视口上的 <c>Mask</c> 换成 <c>RectMask2D</c>（理由见 <see cref="SanitizeScrollView"/>）。
        ///
        /// <para><c>RectMask2D</c> 是纯矩形裁剪，不需要 Sprite（这正是"不能 AddComponent 造 UI"
        /// 这条原则唯一不适用的一类组件 —— 它没有任何序列化素材引用），
        /// 而矩形的滚动视口要的就是矩形裁剪。</para>
        /// </summary>
        private static void ReplaceStencilMaskWithRectMask(ScrollRect sr)
        {
            try
            {
                var viewport = sr.viewport;
                if (!UiFinder.IsAlive(viewport))
                    return;

                var mask = viewport.GetComponent<Mask>();
                if (mask != null)
                    UnityEngine.Object.Destroy(mask);

                if (viewport.GetComponent<RectMask2D>() == null)
                    viewport.gameObject.AddComponent<RectMask2D>();
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("sv-mask", "替换视口遮罩失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 摘掉可滚动容器克隆体里所有 <c>ElementList</c> / <c>ElementListUI</c> 组件。
        ///
        /// <para><b>为什么必须摘：</b>这两个组件是原版的"行生成器"，靠一个
        /// <c>[SerializeField] _template</c> 引用去 <c>Instantiate</c> 列表行。
        /// 而设置界面里那份 <c>Content</c> 上的引用**是空的**（模板是原版运行时临时塞进去的），
        /// 于是它的 <c>Awake</c>（第一句就是 <c>_template.transform</c>）必然抛空指针 ——
        /// 只要该节点在激活的父链下变活跃就会触发。克隆体不需要它，摘掉即可根治。</para>
        ///
        /// <para>必须在**克隆体被激活之前**调用（<see cref="Resolve"/> 就是这么安排的），
        /// 而且必须用 <c>DestroyImmediate</c>：<c>Destroy</c> 是帧末生效的，
        /// 组件在同一帧随父链激活时**照样会跑一次 Awake** —— 那就白摘了。</para>
        /// </summary>
        private static void StripRowSpawners(GameObject clone)
        {
            try
            {
                var lists = clone.GetComponentsInChildren<PVZEngine.Tools.Unity.ElementList>(true);
                if (lists != null)
                {
                    for (var i = 0; i < lists.Length; i++)
                    {
                        if (lists[i] != null)
                            UnityEngine.Object.DestroyImmediate(lists[i]);
                    }
                }

                var uiLists = clone.GetComponentsInChildren<PVZEngine.Tools.Unity.ElementListUI>(true);
                if (uiLists != null)
                {
                    for (var i = 0; i < uiLists.Length; i++)
                    {
                        if (uiLists[i] != null)
                            UnityEngine.Object.DestroyImmediate(uiLists[i]);
                    }
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("sv-lists", "摘除行生成器组件失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 取 <c>ScrollRect</c> 的 Content 节点。
        ///
        /// <para><c>sr.content</c> 在克隆体上应当已经指向克隆体自己的 Content（<c>Instantiate</c> 的重定向），
        /// 但为了不被 prefab 里的引用错误坑到，这里做一次形状校验：取不到就按
        /// <c>Viewport 的第一个子节点</c>找（原版结构就是这样）。</para>
        /// </summary>
        internal static RectTransform FindScrollContent(ScrollRect sr)
        {
            if (!UiFinder.IsAlive(sr))
                return null;

            try
            {
                var content = sr.content;
                if (content != null && content.transform.IsChildOf(sr.transform))
                    return content;
            }
            catch (Exception) { }

            try
            {
                var viewport = sr.viewport;
                if (UiFinder.IsAlive(viewport) && viewport.childCount > 0)
                    return viewport.GetChild(0).TryCast<RectTransform>();
            }
            catch (Exception) { }

            return null;
        }

        /// <summary>清掉输入框对话框继承来的状态（错误提示、输入内容、事件）。</summary>
        private static void ClearInputDialogState(GameObject clone)        {
            try
            {
                var dlg = clone.GetComponent<MVZ2.UI.InputNameDialog>();
                if (dlg != null)
                {
                    try { dlg.OnConfirm = null; }
                    catch (Exception) { }
                    try { dlg.OnCancel = null; }
                    catch (Exception) { }
                    try { dlg.SetErrorMessage(string.Empty); }
                    catch (Exception) { }
                    try { dlg.ClearContent(); }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
        }

    }
}
