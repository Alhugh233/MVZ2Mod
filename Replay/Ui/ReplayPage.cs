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
    /// （排序按钮 + 回放列表），每一行是 关卡名 · 时间 · 时长 · 播放 · 删除。
    ///
    /// <para><b>版式与生命周期全部交给 DSHCore 的 <see cref="AddonPage"/></b> ——
    /// 入口按钮的安装/纠正、「插件页索引藏起来 → 叠面板」、页面壳克隆与修补、
    /// 离开插件页自动收起、心跳驱动，都是那套设施的事。这里只写「面板里放什么」。</para>
    ///
    /// <para><b>视觉：</b>页面壳是插件页自己那套索引的克隆体（标题 / 面板 / 返回 三行都在里面），
    /// 行里的文本与按钮是官方 <c>Text</c> / <c>TextButton</c> 的克隆体（本仓库铁律：
    /// 原版 UI 的字体 / Sprite / 音效挂在私有 <c>[SerializeField]</c> 上，
    /// <c>AddComponent</c> 造出来必然是空的）。</para>
    ///
    /// <para><b>线程：</b>全部成员只允许在 Unity 主线程调用。</para>
    /// </summary>
    internal static class ReplayPage
    {
        public const string PanelName = "DSH_ReplayPanel";
        public const string ConfirmName = "DSH_ReplayConfirm";
        public const string EntryName = "DSH_ReplayEntry";

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

            private static readonly string[] SortLabels = { "按时间", "按关卡", "按时长" };

            private RectTransform _panelBody;   // Center/List（带边框美术的那个面板）
            private GameObject _bar;            // 面板里的第一行：数量 + 排序
            private TextMeshProUGUI _countText;
            private UiList _list;

            private List<ReplayInfo> _items = new List<ReplayInfo>();
            private ReplayLibrary.SortBy _sort = ReplayLibrary.SortBy.Time;

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
                DestroyConfirm();
                _panelBody = null;
                _bar = null;
                _countText = null;
                _list = null;
                _items = new List<ReplayInfo>();
            }

            /// <summary>
            /// 面板里的第一行：**数量文字 + 三个排序按钮并排**。
            /// 数量文字吃掉剩余宽度（<c>flexibleWidth = 1</c>），按钮用各自的首选宽度。
            /// </summary>
            private void BuildBar()
            {
                _bar = UiKit.HContainer(_panelBody, "DSH_ReplayBar");
                if (!UiFinder.IsAlive(_bar))
                {
                    UiLog.WarnOnce("rp-bar", "数量/排序行建不出来，列表仍然可用（只是不能排序）。");
                    return;
                }
                UiKit.Fit(_bar, -1f, UiKit.RowHeight, 1f);
                // 只吃宽度、不吃高度：flexibleHeight 留给列表，否则它会和列表一起分剩余空间。
                UiKit.SetFlexibleHeight(_bar, -1f);

                // ⚠️ 「导入 + 三个排序」一共 4 个按钮，按原来的 0.8×按钮宽排下来会把数量文字
                //    挤成 0 宽（实测：那一行只剩四个按钮，「共 N 个」整个不见了）。
                //    所以按钮收窄到 0.6×，并给数量文字一个首选宽度保底。
                _countText = UiKit.Label(_bar.transform, "Count", string.Empty,
                    UiKit.LabelFontSize, UiKit.ButtonWidth * 0.7f, 1f);

                var buttonWidth = UiKit.ButtonWidth * 0.6f;

                // 导入放在排序按钮**左边**（需求）：数量 / 导入 / 按时间 / 按关卡 / 按时长。
                UiKit.TextButton(_bar.transform, "Import", "导入", ImportReplay, buttonWidth);

                for (var i = 0; i < SortLabels.Length; i++)
                {
                    var index = i;
                    UiKit.TextButton(_bar.transform, "Sort" + i, SortLabels[i],
                        () => SortBy(index), buttonWidth);
                }
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

            /// <summary>重新扫描目录并把列表整个重建（排序变化 / 删除之后调）。</summary>
            private void Refresh()
            {
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

                // 行里横向要摆 5 个单元格（名字 / 时间 / 时长 / 播放 / 删除），
                // 按钮太宽会把文字列压到只剩省略号 —— 实测「2026-10-04 21:52」就被截成了「2026-10-0…」。
                var small = Mathf.Max(44f, UiKit.ButtonWidth * 0.58f);
                for (var i = 0; i < _items.Count; i++)
                {
                    var info = _items[i];

                    var row = UiKit.HContainer(null, "Row" + i);
                    if (!UiFinder.IsAlive(row))
                        break;
                    UiKit.Fit(row, -1f, UiKit.RowHeight, 1f);

                    UiKit.Label(row.transform, "Stage",
                        info.IsBroken ? info.Name + UiLocale.Get("（读不出来）") : info.StageText,
                        UiKit.LabelFontSize, 0f, 1.8f);
                    UiKit.Label(row.transform, "Time", info.CreatedText,
                        UiKit.LabelFontSize, 0f, 1.3f);
                    UiKit.Label(row.transform, "Len", info.DurationText,
                        UiKit.LabelFontSize, 0f, 0.6f);

                    var play = info;
                    UiKit.TextButton(row.transform, "Play", "播放", () => Play(play), small);
                    UiKit.TextButton(row.transform, "Delete", "删除", () => AskDelete(play), small);

                    if (_list.Add(row) == null)
                    {
                        try { UnityEngine.Object.Destroy(row); } catch (Exception) { }
                        break;
                    }
                }

                // 动态加完行必须逼布局组算一次，否则新行停在 0×0（本仓库踩过）。
                var anchor = _list.Root;
                if (anchor != null)
                    UiKit.Rebuild(anchor);
            }

            private void SortBy(int index)
            {
                var by = index == 1 ? ReplayLibrary.SortBy.Stage
                    : index == 2 ? ReplayLibrary.SortBy.Duration
                    : ReplayLibrary.SortBy.Time;

                ReplayLibrary.Sort(_items, by);
                _sort = by;
                BuildRows();
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
                ShowNotice("导入", ok
                    ? UiLocale.Get("已导入到回放目录：") + "\n" + ReplayLibrary.ImportedName(path)
                    : UiLocale.Get("导入失败：") + "\n" + (err ?? UiLocale.Get("未知原因。")));
            }

            private void AskDelete(ReplayInfo info)
            {
                if (info == null)
                    return;
                Confirm("删除回放",
                    info.StageText + "\n" + info.CreatedText + UiLocale.Get("　时长 ") + info.DurationText
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
                sb.Append("列表     : ").Append(UiKit.Describe(_list == null ? null : _list.Root)).Append('\n');
                sb.Append("确认面板 : ").Append(UiKit.Describe(_confirm)).Append('\n');
                sb.Append("条目数   : ").Append(ItemCount)
                  .Append("（排序 ").Append(SortLabels[(int)_sort]).Append("）\n");
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
                            sb.Append("    [").Append(c).Append("] ").Append(UiKit.Describe(cell.gameObject))
                              .Append("  \"").Append(label == null ? "" : UiKit.Trim(label.text, 24)).Append('"')
                              .Append('\n');
                        }
                    }
                }
            }
        }
    }
}
