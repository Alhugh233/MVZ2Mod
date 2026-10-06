using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace DSHCore.Ui
{
    /// <summary>页面壳（版式）从哪个原版页面克隆。</summary>
    public enum AddonShellSource
    {
        /// <summary>
        /// 插件页自己的索引（<c>Addons/Canvas/Index</c>）：
        /// <c>Top{Title} / Center{List{Background, ScrollView}} / Bottom{ReturnButton}</c>
        /// —— **单栏**版式，与「资源包」「回放」同款。
        /// </summary>
        AddonsIndex,

        /// <summary>
        /// 对话档案页的索引（<c>Archive/Canvas/Index</c>）：
        /// <c>Top{Title} / Center{Filter{…Tags}, List{…}} / Bottom{ReturnButton}</c>
        /// —— **左右两栏**版式（左栏里还有一个搜索框 + 一组标签列表）。
        /// </summary>
        ArchiveIndex,
    }

    /// <summary>
    /// 页面壳：基类克隆出来的版式节点集合。子类只往里塞内容，不碰坐标。
    /// </summary>
    public sealed class AddonShell
    {
        /// <summary>整壳（<c>DSH_xxxPanel</c>，挂在本 mod 的对话框层下）。</summary>
        public GameObject Root;

        /// <summary>标题行（<c>Top</c>）。</summary>
        public GameObject Top;

        /// <summary>标题节点（<c>Top/Title</c>，里面是 Image + TMP）。</summary>
        public GameObject Title;

        /// <summary>内容区（<c>Center</c>）。</summary>
        public GameObject Center;

        /// <summary>底部行（<c>Bottom</c>）。</summary>
        public GameObject Bottom;

        /// <summary>返回按钮（<c>Bottom/ReturnButton</c>）。</summary>
        public GameObject ReturnButton;

        /// <summary>按 <c>"A/B"</c> 路径取壳里的节点。</summary>
        public GameObject Node(string path)
        {
            return UiKit.Path(Root, path);
        }

        /// <summary>
        /// 把壳里的某个面板整备好，可以往里塞行：
        /// ①（可选）清掉它自带的内容；② 处理掉它自带的滚动视图；
        /// ③ 让框体美术不参与排版；④ 保证有一个**我们能控制的**纵向布局组。
        ///
        /// <para><paramref name="clearChildren"/> = true 用于「这一栏要完全换成我们自己的东西」
        /// （例如左右两栏都要重排）；false 只清滚动视图，保留原版其余的装饰/占位节点。</para>
        /// </summary>
        public GameObject PreparePanel(GameObject node, bool clearChildren = false,
            float spacing = 8f, int padding = -1)
        {
            if (!UiFinder.IsAlive(node))
                return null;

            if (clearChildren)
                UiKit.ClearChildren(node, true);
            else
                UiKit.Kill(UiKit.Child(node, "Scroll View"), true);

            // 框体美术是**绝对定位**的装饰节点，不能参与排版（否则它会占掉第一行）。
            var bg = UiKit.Child(node, "Background");
            if (UiFinder.IsAlive(bg))
            {
                var le = bg.GetComponent<UnityEngine.UI.LayoutElement>();
                if (le == null)
                    le = bg.AddComponent<UnityEngine.UI.LayoutElement>();
                if (le != null)
                    le.ignoreLayout = true;
            }

            EnsureVertical(node, spacing, padding);
            return node;
        }

        /// <summary>
        /// 保证 <paramref name="node"/> 上有一个「我们能控制」的 <c>VerticalLayoutGroup</c>。
        ///
        /// <para>已经有的话**沿用它的间距/内边距/对齐**（那是 prefab 调好的），
        /// 但把 <c>childControl*</c> / <c>childForceExpand*</c> 设成我们需要的值 ——
        /// 我们的行是靠 <c>LayoutElement</c> 报尺寸的，不控制就是 0×0。</para>
        ///
        /// <para><paramref name="padding"/> = <c>-1</c> 表示**原样保留**容器自己的内边距
        /// （原版按像素调过，比我们猜的值可信）；要覆盖就传非负值。</para>
        /// </summary>
        public static bool EnsureVertical(GameObject node, float spacing = 8f, int padding = -1)
        {
            if (!UiFinder.IsAlive(node))
                return false;
            try
            {
                var v = node.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
                if (v == null)
                {
                    // 没有布局组才需要自己建 —— 这时内边距由我们定（默认 8）。
                    if (padding >= 0)
                        return UiKit.MakeVertical(node, spacing, padding);
                    var current = UiKit.GetPadding(v);
                    return UiKit.MakeVertical(node, spacing, current);
                }

                v.childControlWidth = true;
                v.childControlHeight = true;
                v.childForceExpandWidth = false;
                v.childForceExpandHeight = false;
                if (padding >= 0)
                    UiKit.SetPadding(v, padding);
                return true;
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("addonshell-vert", "整备纵向布局失败：" + e.GetType().Name);
                return false;
            }
        }

        public string Describe()
        {
            var sb = new StringBuilder(300);
            sb.Append("页面壳   : ").Append(UiKit.Describe(Root)).Append('\n');
            sb.Append("  标题   : ").Append(UiKit.Describe(Title)).Append('\n');
            if (UiFinder.IsAlive(Title))
                sb.Append("    文本 : ").Append(UiKit.DescribeTexts(Title)).Append('\n');
            sb.Append("  内容区 : ").Append(UiKit.Describe(Center)).Append('\n');
            sb.Append("  底部   : ").Append(UiKit.Describe(Bottom)).Append('\n');
            sb.Append("    返回 : ").Append(UiKit.Describe(ReturnButton)).Append('\n');
            return sb.ToString();
        }
    }

    /// <summary>
    /// **插件页子页面**：把「在插件（Addons）页里加一个新页面」这件事全部封起来。
    ///
    /// <para><b>一个子页面需要做的事（以前每个 mod 都要抄一遍）</b>：</para>
    /// <list type="number">
    /// <item>在插件页的索引列表里插一个入口按钮（克隆它自己的按钮 → 摘掉文本翻译器 → 改文案 →
    /// 插到兄弟位 → 把列表容器改成纵向），并**定期纠正文案** ——
    /// 那个列表自己的刷新回调会把文案写回去；</item>
    /// <item>点击入口后克隆一份原版页面壳，把插件页自己的索引藏起来，叠上我们的面板；</item>
    /// <item>页面壳里自带的 <c>Mask</c> / 空精灵 Image / 文本翻译器必须在**塞内容之前**修掉
    /// （否则分别是随机空指针、一块白、文案被写回）；</item>
    /// <item>标题 / 返回按钮接线；</item>
    /// <item>玩家从插件页退出去（返回上一级 / 切到别的页）时自动收面板，
    /// 以及「从关卡直接落到本页」这种特殊入口的返回目标。</item>
    /// </list>
    ///
    /// <para><b>子类只需要写</b>：<see cref="Id"/> / <see cref="EntryLabel"/> / <see cref="Title"/>
    /// 和 <see cref="BuildContent"/>（往 <see cref="AddonShell"/> 里塞东西）。</para>
    ///
    /// <para><b>心跳由本库自己驱动</b>（见 <see cref="AddonPages.Apply"/>），
    /// 子 mod 不需要再自己去挂 <c>MainSceneController.Update</c>。</para>
    ///
    /// <para><b>线程</b>：全部成员只允许在 Unity 主线程调用。</para>
    /// </summary>
    public abstract class AddonPage
    {
        // ------------------------------------------------------------------
        // 子类要实现的
        // ------------------------------------------------------------------

        /// <summary>唯一标识（用来拼节点名与日志前缀，建议 ASCII）。</summary>
        public abstract string Id { get; }

        /// <summary>插件页上那个入口按钮的文案。</summary>
        public abstract string EntryLabel { get; }

        /// <summary>页面标题（页面壳 <c>Top/Title</c> 的文案）。</summary>
        public abstract string Title { get; }

        /// <summary>页面壳从哪个原版页面克隆（默认插件页自己的索引 = 单栏版式）。</summary>
        protected virtual AddonShellSource ShellSource
        {
            get { return AddonShellSource.AddonsIndex; }
        }

        /// <summary>
        /// 「主面板」在壳里的路径（相对 <see cref="AddonShell.Root"/>）；空 = 不改宽度。
        /// 只在需要把默认宽度的面板撑宽时用（见 <see cref="WidthRatio"/>）。
        /// </summary>
        protected virtual string WidthTargetPath
        {
            get { return "Center/List"; }
        }

        /// <summary>主面板宽度 / 主画布宽度。<c>0</c> = 不改（用原版宽度）。</summary>
        protected virtual float WidthRatio
        {
            get { return 0f; }
        }

        /// <summary>
        /// **两栏版式**（<see cref="AddonShellSource.ArchiveIndex"/>）下，两栏**合计**宽度占画布宽的比例。
        /// <c>0</c> = 保持 prefab 里的原宽度。
        ///
        /// <para>实现见 <see cref="UiKit.SetTwoColumnWidth"/>：左栏宽度不变、**右栏吸收全部增量**，
        /// 两栏 + 中间的间距整体重新水平居中。想让总宽与某个单栏页面一致时，
        /// 两边填同一个比例即可（例如「回放」页是 <c>0.94</c>）。</para>
        /// </summary>
        protected virtual float TwoColumnWidthRatio
        {
            get { return 0f; }
        }

        /// <summary>两栏版式的左栏路径（相对页面壳）。</summary>
        protected virtual string LeftColumnPath
        {
            get { return "Center/Filter"; }
        }

        /// <summary>两栏版式的右栏路径（相对页面壳）。</summary>
        protected virtual string RightColumnPath
        {
            get { return "Center/List"; }
        }

        /// <summary>
        /// **页面标题行（<c>Top</c>）的高度**（单位）；<c>0</c> = 用 prefab 原值（80）。
        ///
        /// <para>标题只占一行文字，原版给它的高度有余量；调小它 = 把高度让给下方的内容区
        /// （壳是纵向布局组，<c>Center</c> 是弹性高度的那一个，会自动吃掉多出来的空间）。</para>
        ///
        /// <para>⚠ 别调得比标题节点自己矮太多（原版标题节点是 60 单位高）：
        /// 纵向布局组把子节点摆到比它自己高的时候，标题竖着会溢出到内容区上。</para>
        /// </summary>
        protected virtual float TopHeight
        {
            get { return 0f; }
        }

        /// <summary>面板里行的间距 / 内边距。</summary>
        protected virtual float PanelSpacing
        {
            get { return 8f; }
        }

        /// <summary>面板内边距。</summary>
        protected virtual int PanelPadding
        {
            get { return 10; }
        }

        /// <summary>
        /// 往页面里塞内容。此时壳已克隆好、已修补、已隐藏；
        /// <see cref="AddonShell.Root"/> 处于 <c>SetActive(false)</c> 状态，可以放心布置。
        /// 返回 false 表示建不出来（<see cref="LastError"/> 里写原因）。
        /// </summary>
        protected abstract bool BuildContent(AddonShell shell);

        /// <summary>面板建好并显示之后的钩子（重建列表、刷新数据都放这里）。</summary>
        protected virtual void OnOpened()
        {
        }

        /// <summary>面板收起 / 拆掉之后的钩子（释放自己持有的节点引用）。</summary>
        protected virtual void OnClosed()
        {
        }

        /// <summary>
        /// 是否暂时不装入口、不开页面（例如「回放」在播放中不该装入口）。
        /// 返回 true 时 <see cref="Tick"/> 什么都不做。
        /// </summary>
        protected virtual bool Suspended()
        {
            return false;
        }

        /// <summary>面板建不出来时的提示（默认只记日志；有对话框能力的子类可以弹提示）。</summary>
        protected virtual void OnBuildFailed(string error)
        {
        }

        /// <summary>在 <see cref="BuildContent"/> 里报告「建不出来」并记下原因（写法：<c>return Fail("…");</c>）。</summary>
        protected bool Fail(string error)
        {
            _lastError = error;
            return false;
        }

        /// <summary>
        /// 「离开插件页就收面板」的例外：返回目标非空（本次页面是**从关卡直接落过来**的）
        /// 时不自动收，见 <see cref="SetReturnTo"/>。
        /// </summary>
        public bool HasReturnTarget
        {
            get { return _returnTo != null; }
        }

        // ------------------------------------------------------------------
        // 状态
        // ------------------------------------------------------------------

        private bool _registered;
        private bool _open;
        private bool _pendingOpen;
        private bool _entryFailed;
        private int _tick;
        private string _lastError;

        /// <summary>面板建好了没有（跟「现在显示着」是两回事）。</summary>
        private bool _built;

        private Action _returnTo;

        private GameObject _entry;
        private GameObject _indexUi;

        /// <summary>插件页上那个入口按钮（没装上时为 null）。</summary>
        public GameObject EntryNode
        {
            get { return UiFinder.IsAlive(_entry) ? _entry : null; }
        }

        /// <summary>入口装上了没有（验收用）。</summary>
        public bool EntryInstalled
        {
            get { return UiFinder.IsAlive(_entry); }
        }

        /// <summary>页面现在显示着没有。</summary>
        public bool IsOpen
        {
            get { return _open; }
        }

        /// <summary>页面壳（没建或已拆时为 null）。</summary>
        public AddonShell Shell { get; private set; }

        /// <summary>页面壳节点（没建或已拆时为 null）。</summary>
        public GameObject Root
        {
            get { return UiFinder.IsAlive(RootInternal) ? RootInternal : null; }
        }

        /// <summary>最近一次失败的原因（建不出来时看它）。</summary>
        public string LastError
        {
            get { return _lastError; }
        }

        /// <summary>页面壳节点（不做存活性判断，给子类内部用）。</summary>
        protected GameObject RootInternal { get; set; }

        // ------------------------------------------------------------------
        // 登记
        // ------------------------------------------------------------------

        /// <summary>登记到本库的插件页设施（幂等）。**子类构造函数里调一次即可。**</summary>
        protected void Register()
        {
            if (_registered)
                return;
            _registered = true;
            AddonPages.Register(this);
        }

        // ------------------------------------------------------------------
        // 请求打开
        // ------------------------------------------------------------------

        /// <summary>
        /// 请求「回到插件页之后自动打开本页」（幂等）。
        ///
        /// <para>用于「先在别的场景做事、做完再切回插件页」的流程：页面切换与场景卸载需要时间，
        /// 直接建面板会建在正在卸载的场景上、或者建完又被页面切换盖掉。</para>
        /// </summary>
        public void RequestOpen()
        {
            _pendingOpen = true;
        }

        /// <summary>
        /// 立刻试着把「挂着单的页面」打开（不等心跳节流）。
        /// 场景还没卸完时也开工 —— 面板挂在主场景的对话框层下，关卡是另加载的场景，卸它动不到我们。
        /// </summary>
        public void PumpNow()
        {
            TryOpenPending(true);
        }

        /// <summary>
        /// 设置「收掉本页之后去哪」（null = 就留在插件页，亮出它自己的索引）。
        /// 非 null 表示本页是**从其它场景直接落过来**的，玩家没经过插件页的索引。
        /// </summary>
        public void SetReturnTo(Action action)
        {
            _returnTo = action;
        }

        // ------------------------------------------------------------------
        // 打开 / 关闭
        // ------------------------------------------------------------------

        /// <summary>打开本页（已打开则什么都不做）。返回是否成功。</summary>
        public bool Open()
        {
            if (_open)
                return true;
            if (!EnsureBuilt())
            {
                OnBuildFailed(_lastError ?? "面板素材还没就绪。");
                SetIndexVisible(true);
                return false;
            }

            // 背景留着（它画在插件页上），只把插件页自己的条目藏起来。
            SetIndexVisible(false);
            UiClone.Activate(RootInternal);
            _open = true;
            try
            {
                OnOpened();
            }
            catch (Exception e)
            {
                UiLog.Warn("[" + Id + "] OnOpened 抛异常：" + e.GetType().Name + ": " + e.Message);
            }
            return true;
        }

        /// <summary>收起本页（幂等）。</summary>
        public void Close()
        {
            _open = false;
            _pendingOpen = false;

            var back = _returnTo;
            _returnTo = null;

            Teardown();

            if (back != null)
            {
                // 本次页面是**从别的场景直接落过来**的：玩家从没经过插件页的索引，
                // 关掉面板就该执行调用方给的动作（例如回主菜单），而不是把索引亮出来。
                try
                {
                    back();
                    return;
                }
                catch (Exception e)
                {
                    UiLog.WarnOnce("addonpg-back-" + Id,
                        "[" + Id + "] 从本页返回失败：" + e.GetType().Name + ": " + e.Message);
                }
            }
            SetIndexVisible(true);
        }

        private void SetIndexVisible(bool visible)
        {
            if (!UiFinder.IsAlive(_indexUi))
                return;
            try { _indexUi.SetActive(visible); }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 心跳
        // ------------------------------------------------------------------

        /// <summary>
        /// 每帧检查（由 <see cref="AddonPages"/> 驱动，**不要自己调**）。
        ///
        /// <para>节流 20 帧：这是个「页面出现时把按钮装上去」的幂等检查，不是每帧都要做的事。</para>
        /// </summary>
        internal void Tick()
        {
            _tick++;
            if ((_tick % 20) != 0)
                return;

            try
            {
                if (Suspended())
                    return;

                if (_pendingOpen)
                {
                    TryOpenPending(false);
                    return;
                }

                if (_open)
                {
                    // 面板开着的时候也要纠正入口文案：游戏会在**页面刚显示出来**的那一两秒里
                    // 把按钮文案写回原样（实测：装好是新的，随后变回「资源包」，
                    // 而字号还是我们设的那个 —— 是 text 的 setter 被再次调用了）。
                    EnsureEntryLabel();

                    // 玩家从插件页退出去（返回上一级 / 切到别的页）时把面板一起收掉，
                    // 否则它会留在屏幕上挡住之后的界面。
                    //
                    // ⚠️ 「从其它场景直接落到本页」时不要收：那一瞬间页面还在切，
                    //    面板是我们同帧抢出来的，收掉就白开了。
                    if (_returnTo == null && !AddonPages.IsAddonsPage())
                        Close();
                    return;
                }

                EnsureEntry();
                EnsureEntryLabel();
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("addonpg-tick-" + Id,
                    "[" + Id + "] 插件页子页面心跳出错：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private void TryOpenPending(bool force)
        {
            if (Suspended())
                return;
            if (!force && LevelStillLoaded())
                return;
            if (!AddonPages.IsAddonsPage())
                return;

            _pendingOpen = false;
            Plugin.Log?.LogInfo("[AddonPage:" + Id + "] 页面已就绪，打开子页面"
                + (force ? "（同帧）" : ""));
            Open();
        }

        /// <summary>关卡场景还在（回放/关卡里的调用方要靠它判断「现在换页安全吗」）。</summary>
        private static bool LevelStillLoaded()
        {
            try
            {
                var main = UiGame.Main;
                if (main == null)
                    return false;
                var lm = main.LevelManager;
                if (lm == null)
                    return false;
                return lm.GetLevelController() != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 入口按钮
        // ------------------------------------------------------------------

        /// <summary>在插件页的索引里插一个入口按钮（幂等；素材不对就只记一次警告）。</summary>
        private void EnsureEntry()
        {
            if (UiFinder.IsAlive(_entry))
                return;
            if (_entryFailed)
                return;

            MVZ2.Addons.AddonsUI ui = null;
            try { ui = UiFinder.FindOne<MVZ2.Addons.AddonsUI>(); }
            catch (Exception e)
            {
                _entryFailed = true;
                UiLog.WarnOnce("addonpg-addons",
                    "找不到插件页组件（MVZ2.Addons.AddonsUI）：" + e.GetType().Name);
                return;
            }
            if (ui == null)
                return;   // 场景还没起来，下次再看

            GameObject root = null;
            try { root = ui.gameObject; } catch (Exception) { }
            GameObject index = null;
            try { index = ui.indexUI; } catch (Exception) { }
            if (!UiFinder.IsAlive(index))
            {
                // 索引节点拿不到（字段改名 / 是别的版本）→ 退回整页搜索。
                UiLog.WarnOnce("addonpg-indexui", "插件页的 indexUI 取不到，改用整页搜索按钮。");
                index = root;
            }
            if (!UiFinder.IsAlive(index))
            {
                _entryFailed = true;
                return;
            }

            var template = AddonPages.FindEntryTemplate(index);
            if (template == null && index != root)
                template = AddonPages.FindEntryTemplate(root);
            if (template == null)
            {
                _entryFailed = true;
                UiLog.WarnOnce("addonpg-entry-tpl",
                    "插件页里找不到「带文字的按钮」可作模板，" + Id + " 的入口没装上。");
                return;
            }

            var clone = UiClone.Instantiate(template, null, "DSH_" + Id + "Entry");
            if (!UiFinder.IsAlive(clone))
            {
                _entryFailed = true;
                UiLog.WarnOnce("addonpg-entry-clone", "克隆插件页按钮失败，" + Id + " 的入口没装上。");
                return;
            }

            // 只改文字、**不动字号/自动缩放** —— 入口要和旁边的原版按钮长得一模一样。
            // 先把克隆体上的文本翻译器摘掉：不摘的话节点一启用它就把文字写回成原版那句。
            UiKit.StripTextTranslators(clone);
            UiClone.SetLabel(clone, EntryLabel);
            UiClone.OnClick(clone, OpenFromEntry);

            // 插到「最后一个己方入口」之后（没有就插在模板正后面）。
            // 让多个子页面按登记顺序排下来，而不是每次都在模板后面挤成倒序。
            var anchor = AddonPages.FindLastOwnEntry(template) ?? template;
            if (!UiHost.InsertAfter(anchor, clone))
            {
                try { UnityEngine.Object.Destroy(clone); } catch (Exception) { }
                _entryFailed = true;
                UiLog.WarnOnce("addonpg-entry-insert", "把入口插进插件页失败。");
                return;
            }

            // 插进树里之后再写一次文案：克隆时对象还没有父节点，
            // 个别 TMP 在这种状态下的赋值会随后被它自己的布局/激活回调覆盖回去。
            UiClone.SetLabel(clone, EntryLabel);

            // 列表容器原本是**横向**布局（原版打算横排多个分类按钮），所以「插一个兄弟」
            // 只会并排、不会另起一行。改成纵向，条目就各占一行。
            var content = anchor.transform.parent == null
                ? null : anchor.transform.parent.gameObject;
            var vertical = UiKit.MakeVertical(content);
            if (!vertical)
                UiLog.WarnOnce("addonpg-entry-vertical",
                    "插件页的列表容器没能改成纵向（拿不到横向布局组？），入口可能与其它按钮并排。");

            _entry = clone;
            _indexUi = index;
            Plugin.Log?.LogInfo("[AddonPage:" + Id + "] 已在「插件」页加入口："
                + UiFinder.PathOf(clone.transform) + "（纵向=" + vertical + "，文案=\"" + EntryLabel + "\"）");
        }

        /// <summary>玩家点了插件页上的入口：这是一次**正常进入**，返回目标是插件页自己。</summary>
        private void OpenFromEntry()
        {
            _returnTo = null;
            Open();
        }

        /// <summary>
        /// 把入口按钮的文案重新确认一遍。
        ///
        /// <para><b>为什么要反复确认：</b>装上去之后，插件页里那个列表**自己的刷新回调会把文案写回去**。
        /// 实测：装好那一瞬间读出来是新文案，两秒后变回原版那句，而**字号还是我们设的** ——
        /// 说明是 <c>text</c> 的 setter 被再次调用了，不是节点被重建。</para>
        /// </summary>
        private void EnsureEntryLabel()
        {
            if (!UiFinder.IsAlive(_entry))
                return;
            var t = UiClone.FindText(_entry);
            if (t == null)
                return;
            try
            {
                if (t.text != EntryLabel)
                    t.text = EntryLabel;
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 构建 / 拆除
        // ------------------------------------------------------------------

        private bool EnsureBuilt()
        {
            if (_built && UiFinder.IsAlive(RootInternal))
                return true;

            Teardown();
            _lastError = null;

            if (!UiSkin.Prepare())
            {
                _lastError = "UI 素材还没就绪（要先进过一次主菜单，游戏才会把设置面板那套素材实例化出来）。";
                return false;
            }

            var parent = UiHost.Resolve(UiLayer.DialogLayer);
            if (parent == null)
            {
                _lastError = "找不到可用的 UI 宿主。";
                return false;
            }

            var source = AddonPages.FindShellSource(ShellSource);
            if (!UiFinder.IsAlive(source))
            {
                _lastError = ShellSource == AddonShellSource.ArchiveIndex
                    ? "拿不到对话档案页的页面壳（ArchiveUI.indexUI）—— 要先进过一次对话档案页。"
                    : "拿不到插件页的页面壳（AddonsUI.indexUI）—— 要先进过一次插件页。";
                return false;
            }

            RootInternal = UiClone.Instantiate(source, parent, "DSH_" + Id + "Panel");
            if (!UiFinder.IsAlive(RootInternal))
            {
                _lastError = "页面壳克隆失败。";
                return false;
            }

            // 壳里自带的三样东西必须先修掉（见 UiKit 各自的注释）：
            //   Mask → 随机空指针；空精灵 Image → 一块白；文本翻译器 → 改的文案被写回去。
            var masks = UiKit.ReplaceMask(RootInternal);
            var cleared = UiKit.ClearEmptySpriteAlpha(RootInternal);
            var stripped = UiKit.StripTextTranslators(RootInternal);

            var shell = new AddonShell
            {
                Root = RootInternal,
                Top = UiKit.Child(RootInternal, "Top"),
                Center = UiKit.Child(RootInternal, "Center"),
                Bottom = UiKit.Child(RootInternal, "Bottom"),
            };
            shell.Title = UiKit.Child(shell.Top, "Title");
            shell.ReturnButton = UiKit.Child(shell.Bottom, "ReturnButton");
            Shell = shell;

            // 标题
            if (UiFinder.IsAlive(shell.Title))
                UiClone.SetLabel(shell.Title, Title);
            else
                UiLog.WarnOnce("addonpg-title-" + Id, "页面壳里找不到标题（Top/Title）。");

            // 返回按钮（就在面板下面，原版位置）
            if (UiFinder.IsAlive(shell.ReturnButton))
                UiClone.OnClick(shell.ReturnButton, Close);

            if (!UiFinder.IsAlive(shell.Center))
            {
                _lastError = "页面壳里找不到内容区（Center）。";
                Teardown();
                return false;
            }

            // 标题行高度（把余量让给内容区）
            if (TopHeight > 0f && UiFinder.IsAlive(shell.Top))
            {
                UiKit.Fit(shell.Top, -1f, TopHeight, -1f);
                UiKit.Rebuild(RootInternal);
            }

            // 主面板宽度（比例，不是像素）
            if (WidthRatio > 0f && !string.IsNullOrEmpty(WidthTargetPath))
                UiKit.SetWidthRatio(shell.Node(WidthTargetPath), WidthRatio);

            // 两栏版式：按「合计宽度」调整（左栏不动，右栏吸收增量），见 UiKit.SetTwoColumnWidth。
            if (TwoColumnWidthRatio > 0f)
            {
                if (!UiKit.SetTwoColumnWidth(shell.Node(LeftColumnPath), shell.Node(RightColumnPath),
                        TwoColumnWidthRatio))
                {
                    UiLog.WarnOnce("addonpg-2col-" + Id,
                        "[" + Id + "] 两栏宽度没调成（素材版式变了？），保持原宽度。");
                }
            }

            bool ok;
            try
            {
                ok = BuildContent(shell);
            }
            catch (Exception e)
            {
                _lastError = "布置页面内容时抛异常：" + e.GetType().Name + "：" + e.Message;
                UiLog.Warn("[" + Id + "] BuildContent 抛异常：" + e);
                ok = false;
            }
            if (!ok)
            {
                if (string.IsNullOrEmpty(_lastError))
                    _lastError = "页面内容建不出来。";
                Teardown();
                return false;
            }

            _built = true;
            UiClone.Deactivate(RootInternal);
            Plugin.Log?.LogInfo("[AddonPage:" + Id + "] 页面已构建：" + UiFinder.PathOf(RootInternal.transform)
                + "（换遮罩=" + masks + " 清空图=" + cleared + " 摘翻译器=" + stripped + "）");
            return true;
        }

        private void Teardown()
        {
            try
            {
                OnClosed();
            }
            catch (Exception e)
            {
                UiLog.Warn("[" + Id + "] OnClosed 抛异常：" + e.GetType().Name + ": " + e.Message);
            }

            UiKit.Kill(RootInternal);
            RootInternal = null;
            Shell = null;
            _built = false;
            UiClone.PruneKeepAlive();
        }

        // ------------------------------------------------------------------
        // 验收
        // ------------------------------------------------------------------

        /// <summary>入口的屏幕矩形（验收用）。</summary>
        public string DescribeEntry()
        {
            var sb = new StringBuilder(120);
            sb.Append("入口按钮 : ").Append(UiKit.Describe(_entry)).Append('\n');
            if (UiFinder.IsAlive(_entry))
                sb.Append("  入口文本 : ").Append(UiKit.DescribeTexts(_entry)).Append('\n');
            return sb.ToString();
        }
    }

    /// <summary>
    /// 插件页子页面的**登记表 + 心跳驱动 + 共用查找**。
    ///
    /// <para>心跳由本库自己挂在官方本来就每帧跑的 <c>MainSceneController.Update</c> 上
    /// （<see cref="Apply"/>，在 <c>DSHCore.Plugin.Load</c> 里调一次），
    /// 所以子 mod 不需要再自己挂心跳。</para>
    /// </summary>
    public static class AddonPages
    {
        private const string HarmonyId = "qoznos.mvz2.core.addonpages";

        private static readonly List<AddonPage> Pages = new List<AddonPage>();
        private static bool _applied;

        /// <summary>登记一个页面（幂等；同 Id 重复登记会忽略后一个）。</summary>
        public static void Register(AddonPage page)
        {
            if (page == null || string.IsNullOrEmpty(page.Id))
            {
                UiLog.Warn("[AddonPages] 忽略无效登记（Id 为空）。");
                return;
            }
            for (var i = 0; i < Pages.Count; i++)
            {
                if (Pages[i].Id == page.Id)
                {
                    UiLog.Warn("[AddonPages] 重复登记 " + page.Id + "，忽略后一个。");
                    return;
                }
            }
            Pages.Add(page);
        }

        /// <summary>按 Id 取页面（没登记返回 null）。</summary>
        public static AddonPage Get(string id)
        {
            for (var i = 0; i < Pages.Count; i++)
            {
                if (Pages[i].Id == id)
                    return Pages[i];
            }
            return null;
        }

        /// <summary>已登记的全部页面。</summary>
        public static AddonPage[] All()
        {
            return Pages.ToArray();
        }

        /// <summary>当前停在「插件」页（主菜单 → 更多选项 → 插件）。</summary>
        public static bool IsAddonsPage()
        {
            try
            {
                var scene = UiGame.Scene;
                if (scene == null)
                    return false;
                return (int)scene.currentPage == (int)MVZ2Logic.Scenes.MainScenePageType.Addons;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>页面壳素材（按来源类型取。查不到返回 null）。</summary>
        public static GameObject FindShellSource(AddonShellSource source)
        {
            try
            {
                switch (source)
                {
                    case AddonShellSource.ArchiveIndex:
                        var archive = UiFinder.FindOne<MVZ2.Archives.ArchiveUI>();
                        if (archive == null)
                            return null;
                        var index = archive.indexUI;
                        if (UiFinder.IsAlive(index) && UiFinder.IsAlive(index.gameObject))
                            return index.gameObject;
                        return archive.gameObject;

                    default:
                        var ui = UiFinder.FindOne<MVZ2.Addons.AddonsUI>();
                        if (ui == null)
                            return null;
                        var idx = ui.indexUI;
                        return UiFinder.IsAlive(idx) ? idx : ui.gameObject;
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("addonpg-shell-" + source, "取页面壳素材失败：" + e.GetType().Name);
                return null;
            }
        }

        /// <summary>
        /// 找一个「带文字的原版按钮」当入口模板。取**第一个** ——
        /// 插件页索引的层级顺序就是它自己的显示顺序，第一个就是列表里的第一项。
        /// </summary>
        public static GameObject FindEntryTemplate(GameObject root)
        {
            if (!UiFinder.IsAlive(root))
                return null;
            try
            {
                var buttons = root.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                if (buttons == null)
                    return null;
                for (var i = 0; i < buttons.Length; i++)
                {
                    var b = buttons[i];
                    if (b == null)
                        continue;
                    var go = b.gameObject;
                    if (!UiFinder.IsAlive(go))
                        continue;
                    if (UiHost.IsOwn(go.transform))       // 别拿自己的克隆体当模板
                        continue;
                    if (UiClone.FindText(go) == null)     // 没有文字的按钮不是入口
                        continue;
                    return go;
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("addonpg-entry-search", "在插件页里找按钮时异常：" + e.GetType().Name);
            }
            return null;
        }

        /// <summary>
        /// 取最后一个**己方**入口节点（模板的兄弟里 <c>DSH_</c> 前缀的那些）。
        /// 用来把新入口插在已有入口之后，保证多个子页面按登记顺序排下来。
        /// </summary>
        public static GameObject FindLastOwnEntry(GameObject template)
        {
            if (!UiFinder.IsAlive(template))
                return null;
            try
            {
                var parent = template.transform.parent;
                if (parent == null)
                    return null;
                GameObject last = null;
                var n = parent.childCount;
                for (var i = 0; i < n; i++)
                {
                    var child = parent.GetChild(i);
                    if (child == null)
                        continue;
                    if (!UiHost.IsOwn(child))
                        continue;
                    last = child.gameObject;
                }
                return last;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 挂上心跳（幂等）。在 <c>DSHCore.Plugin.Load</c> 里调用一次 ——
        /// 心跳点用官方本来就每帧跑的 <c>MainSceneController.Update</c>，
        /// 不额外注入 MonoBehaviour（注入类型是崩溃风险来源）。
        ///
        /// <para>顺带补掉一个官方坑：<c>DisplayAddons(null)</c> 会让插件页的「返回」变成**黑屏**
        /// （见 <see cref="DisplayAddonsPrefix"/>）。</para>
        /// </summary>
        public static void Apply()
        {
            if (_applied)
                return;
            _applied = true;
            try
            {
                var target = AccessTools.Method(typeof(MVZ2.Scenes.MainSceneController), "Update");
                if (target == null)
                {
                    Plugin.Log?.LogWarning("[AddonPages] 找不到 MainSceneController.Update，插件页子页面不可用。");
                }
                else
                {
                    new Harmony(HarmonyId).Patch(target,
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(AddonPages), nameof(UpdatePostfix))));
                    Plugin.Log?.LogInfo("[AddonPages] 心跳已挂上（MainSceneController.Update 后缀）");
                }
            }
            catch (Exception e)
            {
                UiLog.Warn("[AddonPages] 挂心跳失败：" + e.GetType().Name + ": " + e.Message);
            }

            try
            {
                var target = AccessTools.Method(typeof(MVZ2.Scenes.MainSceneController), "DisplayAddons");
                if (target != null)
                {
                    new Harmony(HarmonyId).Patch(target,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(AddonPages),
                            nameof(DisplayAddonsPrefix))));
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("addonpages-dapatch", "[AddonPages] DisplayAddons 补丁失败："
                    + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 给 <c>DisplayAddons(null)</c> 补一个默认返回目标。
        ///
        /// <para><b>为什么必须补：</b>官方 <c>MainScenePage.Return()</c> 的实现是
        /// <c>OnReturnClick?.Invoke(); Hide();</c> —— 回调为 <c>null</c> 时
        /// 「返回」只把页面**藏起来**、没有任何东西顶上来，玩家看到的就是一整屏黑。</para>
        ///
        /// <para>正常入口（主菜单里的「插件」按钮）自己会传回调，只有**程序化**调用
        /// （自测、其他 mod 的跳转）才会漏掉；这里统一兜成「回主菜单」——
        /// 与官方那个回调（<c>DisplayMainmenuToBasement</c>）一致。</para>
        /// </summary>
        private static bool DisplayAddonsPrefix(MVZ2.Scenes.MainSceneController __instance,
            ref Il2CppSystem.Action onReturn)
        {
            if (onReturn != null || __instance == null)
                return true;
            try
            {
                var instance = __instance;
                var managed = new Action(() =>
                {
                    try { instance.DisplayMainmenuToBasement(); }
                    catch (Exception) { }
                });
                var il2cpp = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<Il2CppSystem.Action>(managed);
                UiClone.KeepAlive(instance.gameObject, managed, il2cpp);
                onReturn = il2cpp;
                Plugin.Log?.LogInfo("[AddonPages] DisplayAddons(null) → 已补上默认返回目标（回主菜单）；"
                    + "否则插件页的「返回」会黑屏。");
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("addonpages-danull",
                    "[AddonPages] 补默认返回目标失败：" + e.GetType().Name + ": " + e.Message);
            }
            return true;
        }

        private static void UpdatePostfix()
        {
            try
            {
                for (var i = 0; i < Pages.Count; i++)
                    Pages[i].Tick();
            }
            catch (Exception)
            {
                // 心跳里抛异常会打断 MainSceneController.Update，必须吞掉。
            }
        }

        /// <summary>状态报告（排障 / 验收用）。</summary>
        public static string Report()
        {
            var sb = new StringBuilder(400);
            sb.Append("插件页子页面：").Append(Pages.Count).Append(" 个\n");
            for (var i = 0; i < Pages.Count; i++)
            {
                var p = Pages[i];
                sb.Append("  ").Append(p.Id)
                  .Append("  入口=").Append(p.EntryInstalled ? "已装" : "**未装**")
                  .Append("  页面=").Append(p.IsOpen ? "打开" : (p.Root != null ? "已建/收起" : "未建"))
                  .Append("  标签=\"").Append(p.EntryLabel).Append('"')
                  .Append('\n');
                if (!string.IsNullOrEmpty(p.LastError))
                    sb.Append("      最近失败：").Append(p.LastError).Append('\n');
            }
            return sb.ToString();
        }
    }
}
