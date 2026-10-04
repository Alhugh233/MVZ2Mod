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
    /// <para><b>位置与背景：</b>点击插件页上的「回放」后，我们只做两件事 ——
    /// 把插件页自己的索引内容（<c>AddonsUI.indexUI</c>）藏起来，然后把面板叠上去。
    /// 这样页面背景仍然是插件页原本那张（与资源包 / 对话档案同款），
    /// 不需要自己去拼任何背景美术，也不会在切页时留下孤儿节点。</para>
    ///
    /// <para><b>视觉：</b>面板整壳是官方 <c>CustomDialog</c> 的克隆体，行里的文本与按钮
    /// 也都是官方 <c>Text</c> / <c>TextButton</c> 的克隆体（本仓库铁律：
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

        /// <summary>插件页上那个入口按钮的文案。</summary>
        private const string EntryLabel = "回放";

        /// <summary>面板宽度占画布宽度的比例。</summary>
        ///
        /// <para>原版那个面板是写死的 <b>500</b> 单位宽（画布 802）—— 里面一行要放
        /// 「名字 / 时间 / 时长 / 播放 / 删除」五个单元格时很紧。所以只改宽度，
        /// 高度仍然由原版 <c>Center</c> 的 <c>flexibleHeight</c> 决定（= 撑满标题与返回之间的空间）。</para>
        private const float WidthRatio = 0.94f;

        private static readonly string[] SortLabels = { "按时间", "按关卡", "按时长" };

        // ------------------------------------------------------------------
        // 状态
        // ------------------------------------------------------------------

        // 入口（装在插件页里，常驻）
        private static GameObject _entry;
        private static GameObject _indexUi;
        private static bool _entryFailed;    // 素材不对，别每 20 帧重试一次刷日志

        // 页面：外壳是**插件页那个 Index 的克隆体**（标题 / 面板 / 返回 三行都在里面）
        private static GameObject _panel;
        private static RectTransform _panelBody;   // Center/List（带边框美术的那个面板）
        private static GameObject _bar;            // 面板里的第一行：数量 + 排序
        private static TextMeshProUGUI _countText;
        private static UiList _list;

        private static List<ReplayInfo> _items = new List<ReplayInfo>();
        private static ReplayLibrary.SortBy _sort = ReplayLibrary.SortBy.Time;
        private static bool _open;
        private static int _tick;
        private static string _lastError;

        // 二次确认 / 提示
        private static GameObject _confirm;
        private static Action<int> _confirmManaged;
        private static Il2CppSystem.Action<int> _confirmIl2Cpp;

        public static bool IsOpen { get { return _open; } }

        public static string LastError { get { return _lastError; } }

        // ------------------------------------------------------------------
        // 每帧
        // ------------------------------------------------------------------

        /// <summary>主场景每帧调一次（节流在里面）。</summary>
        public static void Tick()
        {
            if (!ReplayConfig.GetGuiEnabled())
                return;

            // 20 帧一次足够：这是个"页面出现时把按钮装上去"的幂等检查，
            // 不是每帧都要做的事。
            _tick++;
            if ((_tick % 20) != 0)
                return;

            try
            {
                if (_open)
                {
                    // 面板开着的时候也要纠正入口文案：游戏会在**页面刚显示出来**的那一两秒里
                    // 把按钮文案写回原样（实测：装好是「回放」，随后变回「资源包」，
                    // 而字号还是我们设的那个 —— 是 text 的 setter 被再次调用了）。
                    // 只在关着的时候跑的话，要等关面板才纠正过来；两边都跑才即时生效。
                    EnsureEntryLabel();

                    // 玩家从插件页退出去（返回上一级 / 切到别的页）时把面板一起收掉，
                    // 否则它会留在屏幕上挡住之后的界面。
                    if (!IsAddonsPage())
                        Close();
                    return;
                }
                if (ReplayPlayer.IsActive)
                    return;
                EnsureEntry();
                EnsureEntryLabel();
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("rp-tick", "回放页每帧检查出错：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>当前是否停在「插件」页。</summary>
        private static bool IsAddonsPage()
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

        // ------------------------------------------------------------------
        // 入口按钮（插件页）
        // ------------------------------------------------------------------

        /// <summary>
        /// 在插件页的索引里插一个「回放」按钮。
        ///
        /// <para>做法是**克隆它自己已经有的按钮**再插到旁边的兄弟位置 ——
        /// 和 ModularCurse 的诅咒入口同一条路：父节点上本来就有 <c>LayoutGroup</c>，
        /// 插进去就会被原版排版，一个坐标都不用算。</para>
        /// </summary>
        private static void EnsureEntry()
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
                UiLog.WarnOnce("rp-addons", "找不到插件页组件（MVZ2.Addons.AddonsUI）：" + e.GetType().Name);
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
                UiLog.WarnOnce("rp-indexui", "插件页的 indexUI 取不到，改用整页搜索按钮。");
                index = root;
            }
            if (!UiFinder.IsAlive(index))
            {
                _entryFailed = true;
                return;
            }

            var template = FindEntryTemplate(index);
            if (template == null && index != root)
                template = FindEntryTemplate(root);
            if (template == null)
            {
                _entryFailed = true;
                UiLog.WarnOnce("rp-entry-tpl",
                    "插件页里找不到「带文字的按钮」可作模板，回放入口没装上（/replay 指令仍然可用）。");
                return;
            }

            var clone = UiClone.Instantiate(template, null, EntryName);
            if (!UiFinder.IsAlive(clone))
            {
                _entryFailed = true;
                UiLog.WarnOnce("rp-entry-clone", "克隆插件页按钮失败，回放入口没装上。");
                return;
            }

            // 只改文字、**不动字号/自动缩放** —— 入口要和旁边的原版按钮长得一模一样（推荐用法）。
            // 先把克隆体上的文本翻译器摘掉：不摘的话节点一启用它就把文字写回成「资源包」。
            ReplayUiKit.StripTextTranslators(clone);
            UiClone.SetLabel(clone, EntryLabel);
            UiClone.OnClick(clone, Open);

            // 插到模板按钮**正后面**。父节点本来就有 LayoutGroup（原版自己排的），
            // 所以插进去就会被自动排版，一个坐标都不用算。
            if (!UiHost.InsertAfter(template, clone))
            {
                try { UnityEngine.Object.Destroy(clone); } catch (Exception) { }
                _entryFailed = true;
                UiLog.WarnOnce("rp-entry-insert", "把「回放」入口插进插件页失败。");
                return;
            }

            // 插进树里之后再写一次文案：克隆时对象还没有父节点，
            // 个别 TMP 在这种状态下的赋值会随后被它自己的布局/激活回调覆盖回去。
            UiClone.SetLabel(clone, EntryLabel);

            // 列表容器原本是**横向**布局（原版打算横排多个分类按钮），所以"插一个兄弟"
            // 只会并排、不会另起一行。改成纵向，两个按钮就各占一行。
            var content = template.transform.parent == null
                ? null : template.transform.parent.gameObject;
            var vertical = ReplayUiKit.MakeVertical(content);
            if (!vertical)
                UiLog.WarnOnce("rp-entry-vertical",
                    "插件页的列表容器没能改成纵向（拿不到横向布局组？），入口可能与「资源包」并排。");

            _entry = clone;
            _indexUi = index;
            Plugin.Log?.LogInfo("[Replay] 已在「插件」页加入口：" + UiFinder.PathOf(clone.transform)
                + "（模板 " + UiFinder.PathOf(template.transform) + "，纵向=" + vertical + "）"
                + "\n  入口上的文本：" + DescribeTexts(clone));
        }

        /// <summary>
        /// 找一个「带文字的原版按钮」当模板。取**第一个** ——
        /// 插件页索引的层级顺序就是它自己的显示顺序，第一个就是面板里的「资源包」。
        /// 插在它后面 = 条目列表里的第二项（列表已改成纵向，所以是**下一行**）。
        /// </summary>
        private static GameObject FindEntryTemplate(GameObject root)
        {
            if (!UiFinder.IsAlive(root))
                return null;
            try
            {
                var buttons = root.GetComponentsInChildren<Button>(true);
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
                UiLog.WarnOnce("rp-entry-search", "在插件页里找按钮时异常：" + e.GetType().Name);
            }
            return null;
        }

        /// <summary>
        /// 把入口按钮的文案重新确认一遍。
        ///
        /// <para><b>为什么要反复确认：</b>装上去之后，插件页里那个列表**自己的刷新回调会把文案写回去**。
        /// 实测（2026-10-04 test-9）：装好那一瞬间读出来是 <c>"回放"</c>，
        /// 两秒后变成 <c>"资源包"</c>，而**字号还是我们设的那个** ——
        /// 说明是 <c>text</c> 的 setter 被再次调用了，不是节点被重建。
        /// 所以每次心跳检查时幂等地纠正一下（一次 <c>GetComponent</c> + 一次字符串比较，可以忽略）。</para>
        /// </summary>
        private static void EnsureEntryLabel()
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
        // 开关页面
        // ------------------------------------------------------------------

        public static void Open()
        {
            if (_open)
                return;
            if (!Build())
            {
                ShowNotice("回放", _lastError ?? "面板素材还没就绪。");
                SetIndexVisible(true);
                return;
            }

            // 背景留着（它画在插件页上），只把插件页自己的条目藏起来。
            SetIndexVisible(false);
            try { _panel.SetActive(true); } catch (Exception) { }
            _open = true;
            Refresh();
        }

        public static void Close()
        {
            _open = false;
            DestroyConfirm();
            Teardown();
            SetIndexVisible(true);
        }

        private static void SetIndexVisible(bool visible)
        {
            if (!UiFinder.IsAlive(_indexUi))
                return;
            try { _indexUi.SetActive(visible); }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 面板
        // ------------------------------------------------------------------

        private static bool Build()
        {
            if (UiFinder.IsAlive(_panel) && _panelBody != null && _list != null && _list.IsValid)
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

            // ---- 外壳：克隆**插件页自己的 Index** ----
            //
            // 为什么不用对话框（CustomDialog）：需求要的版式是「标题在面板上方、返回在面板下方」
            // （与自定义 / 资源包 / 对话档案同款），而对话框是「标题正文在框体内、按钮在框体底边」。
            // 插件页的 Index 就是现成的那个版式：
            //   Index<V> / Top{Title} / Center{List{Background, ScrollView}} / Bottom{ReturnButton}
            // 克隆它，标题位置 / 面板高度 / 返回位置全部由**原版布局组**决定，不写一个坐标。
            var index = FindAddonsIndex();
            if (!UiFinder.IsAlive(index))
            {
                _lastError = "拿不到插件页的页面壳（AddonsUI.indexUI）—— 要先进过一次插件页。";
                return false;
            }

            _panel = UiClone.Instantiate(index, parent, PanelName);
            if (!UiFinder.IsAlive(_panel))
            {
                _lastError = "页面壳克隆失败。";
                return false;
            }

            // 清掉克隆体里空 sprite 的 Image（uGUI 会把它们画成纯白矩形），
            // 把 Mask 换成 RectMask2D（Mask 的模板缓冲会让 TMP 子网格共用 Stencil 材质实例，
            // 之后任一边被销毁都会把材质一起销毁 → 另一边 OnDisable 时抛空指针），
            // 并摘掉**文本翻译器** —— 否则节点一启用，我们改的字就会被它按翻译键写回去。
            ReplayUiKit.ClearEmptySpriteAlpha(_panel);
            ReplayUiKit.ReplaceMask(_panel);
            var stripped = ReplayUiKit.StripTextTranslators(_panel);

            // ---- 标题 ----
            var top = ReplayUiKit.Child(_panel, "Top");
            var titleNode = UiFinder.IsAlive(top) ? ReplayUiKit.Child(top, "Title") : null;
            if (UiFinder.IsAlive(titleNode))
            {
                var ok = UiClone.SetLabel(titleNode, "回放");
                Plugin.Log?.LogInfo("[Replay] 标题节点=" + UiFinder.PathOf(titleNode.transform)
                    + " 改成功=" + ok + " 现在文本=" + DescribeTexts(titleNode));
            }
            else
            {
                UiLog.WarnOnce("rp-title", "页面壳里找不到标题（Top/Title），标题会沿用原版的文字。");
            }

            // ---- 返回按钮（就在面板下面，原版位置） ----
            var bottom = ReplayUiKit.Child(_panel, "Bottom");
            var returnButton = UiFinder.IsAlive(bottom) ? ReplayUiKit.Child(bottom, "ReturnButton") : null;
            if (UiFinder.IsAlive(returnButton))
                UiClone.OnClick(returnButton, Close);

            // ---- 面板本体：Center/List ----
            var center = ReplayUiKit.Child(_panel, "Center");
            var listNode = UiFinder.IsAlive(center) ? ReplayUiKit.Child(center, "List") : null;
            if (!UiFinder.IsAlive(listNode))
            {
                _lastError = "页面壳里找不到面板（Center/List）。";
                Teardown();
                return false;
            }
            _panelBody = listNode.transform.TryCast<RectTransform>();

            // 只改**宽度**：原版那个面板写死 500 单位（画布 802），一行里放
            // 「名字 / 时间 / 时长 / 播放 / 删除」五个单元格时太紧。
            // 高度不动 —— 它由原版 Center 的 flexibleHeight 撑着（= 标题与返回之间的全部空间），
            // 也就是参照截图里那个"高面板"。
            try
            {
                if (_panelBody != null)
                    _panelBody.sizeDelta = new Vector2(
                        ReplayUiKit.CanvasWidth() * WidthRatio, _panelBody.sizeDelta.y);
            }
            catch (Exception) { }

            // 原版那个 Scroll View 不要：它用的是 Mask（见上），改用本库自己的 UiList ——
            // 它的 ScrollView 素材已经被规范化成 RectMask2D。
            ReplayUiKit.Kill(ReplayUiKit.Child(listNode, "Scroll View"));

            // 面板内部改纵向：第一行 = 数量 + 排序，第二行 = 列表。
            //
            // ⚠️ 这个面板节点**本来没有布局组**（原版用锚点摆的），所以必须让 MakeVertical
            // 给它**新建**一个 —— 之前只写"把横向改成纵向"时它直接返回 false，
            // 于是后面的 preferredHeight/flexibleHeight 全部无人理会：数量行停在 0×0、
            // 列表铺满整个面板盖住了边框（实测就是这么翻车的）。
            var bg = ReplayUiKit.Child(listNode, "Background");
            if (UiFinder.IsAlive(bg))
            {
                var ble = bg.GetComponent<LayoutElement>();
                if (ble == null)
                    ble = bg.AddComponent<LayoutElement>();
                if (ble != null)
                    ble.ignoreLayout = true;   // 它是框体美术，不参与排版
            }
            if (!ReplayUiKit.MakeVertical(listNode, 8f, 10))
            {
                _lastError = "面板节点建不出纵向布局。";
                Teardown();
                return false;
            }

            BuildBar();
            BuildList();

            if (_list == null || !_list.IsValid)
            {
                if (string.IsNullOrEmpty(_lastError))
                    _lastError = "可滚动列表建不出来（拿不到 ScrollView 素材）。";
                Teardown();
                return false;
            }

            try { _panel.SetActive(false); } catch (Exception) { }
            Plugin.Log?.LogInfo("[Replay] 回放页已构建：" + UiFinder.PathOf(_panel.transform)
                + " 面板宽=" + Mathf.RoundToInt(_panelBody.sizeDelta.x)
                + " 摘掉翻译器=" + stripped);
            return true;
        }

        /// <summary>插件页的页面壳（<c>AddonsUI.indexUI</c>）。</summary>
        private static GameObject FindAddonsIndex()
        {
            try
            {
                var ui = UiFinder.FindOne<MVZ2.Addons.AddonsUI>();
                if (ui == null)
                    return null;
                var idx = ui.indexUI;
                return UiFinder.IsAlive(idx) ? idx : ui.gameObject;
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("rp-idx", "取插件页页面壳失败：" + e.GetType().Name);
                return null;
            }
        }

        /// <summary>
        /// 面板里的第一行：**数量文字 + 三个排序按钮并排**。
        /// 数量文字吃掉剩余宽度（<c>flexibleWidth = 1</c>），按钮用各自的首选宽度。
        /// </summary>
        private static void BuildBar()
        {
            _bar = ReplayUiKit.HContainer(_panelBody, "DSH_ReplayBar");
            if (!UiFinder.IsAlive(_bar))
            {
                UiLog.WarnOnce("rp-bar", "数量/排序行建不出来，列表仍然可用（只是不能排序）。");
                return;
            }
            ReplayUiKit.Fit(_bar, -1f, ReplayUiKit.RowHeight, 1f);
            // 只吃宽度、不吃高度：flexibleHeight 留给列表，否则它会和列表一起分剩余空间。
            ReplayUiKit.SetFlexibleHeight(_bar, -1f);

            _countText = ReplayUiKit.Label(_bar.transform, "Count", string.Empty,
                ReplayUiKit.LabelFontSize, 0f, 1f);

            for (var i = 0; i < SortLabels.Length; i++)
            {
                var index = i;
                ReplayUiKit.TextButton(_bar.transform, "Sort" + i, SortLabels[i],
                    () => SortBy(index), ReplayUiKit.ButtonWidth * 0.8f);
            }
        }

        private static void BuildList()
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
        private static void SetCountText(int shown, int max)
        {
            if (_countText == null)
                return;
            try
            {
                _countText.text = "共 " + shown + " 个";
            }
            catch (Exception) { }
        }

        private static void Teardown()
        {
            ClearRows();
            ReplayUiKit.Kill(_panel);
            _panel = null;
            _panelBody = null;
            _bar = null;
            _countText = null;
            _list = null;
            UiClone.PruneKeepAlive();
        }

        // ------------------------------------------------------------------
        // 列表内容
        // ------------------------------------------------------------------

        /// <summary>重新扫描目录并把列表整个重建（排序变化 / 删除之后调）。</summary>
        private static void Refresh()
        {
            if (!UiFinder.IsAlive(_panel))
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

        private static void ClearRows()
        {
            if (_list == null || !_list.IsValid)
                return;
            try { _list.Clear(); }
            catch (Exception) { }
        }

        private static void BuildRows()
        {
            if (_list == null || !_list.IsValid)
                return;
            ClearRows();

            // 行里横向要摆 5 个单元格（名字 / 时间 / 时长 / 播放 / 删除），
            // 按钮太宽会把文字列压到只剩省略号 —— 实测「2026-10-04 21:52」就被截成了「2026-10-0…」。
            var small = Mathf.Max(44f, ReplayUiKit.ButtonWidth * 0.58f);
            for (var i = 0; i < _items.Count; i++)
            {
                var info = _items[i];

                var row = ReplayUiKit.HContainer(null, "Row" + i);
                if (!UiFinder.IsAlive(row))
                    break;
                ReplayUiKit.Fit(row, -1f, ReplayUiKit.RowHeight, 1f);

                ReplayUiKit.Label(row.transform, "Stage",
                    info.IsBroken ? info.Name + "（读不出来）" : info.StageText,
                    ReplayUiKit.LabelFontSize, 0f, 1.8f);
                ReplayUiKit.Label(row.transform, "Time", info.CreatedText,
                    ReplayUiKit.LabelFontSize, 0f, 1.3f);
                ReplayUiKit.Label(row.transform, "Len", info.DurationText,
                    ReplayUiKit.LabelFontSize, 0f, 0.6f);

                var play = info;
                ReplayUiKit.TextButton(row.transform, "Play", "播放", () => Play(play), small);
                ReplayUiKit.TextButton(row.transform, "Delete", "删除", () => AskDelete(play), small);

                if (_list.Add(row) == null)
                {
                    try { UnityEngine.Object.Destroy(row); } catch (Exception) { }
                    break;
                }
            }

            // 动态加完行必须逼布局组算一次，否则新行停在 0×0（本仓库踩过）。
            var anchor = _list.Root;
            if (anchor != null)
                ReplayUiKit.Rebuild(anchor);
        }

        private static void SortBy(int index)
        {
            var by = index == 1 ? ReplayLibrary.SortBy.Stage
                : index == 2 ? ReplayLibrary.SortBy.Duration
                : ReplayLibrary.SortBy.Time;

            // 再点一次同一种排序 → 反序（除了时间：时间只有"新→旧"一种有意义的默认）
            ReplayLibrary.Sort(_items, by);
            _sort = by;
            BuildRows();
        }

        // ------------------------------------------------------------------
        // 动作
        // ------------------------------------------------------------------

        private static void Play(ReplayInfo info)
        {
            if (info == null)
                return;
            if (info.IsBroken)
            {
                ShowNotice("无法播放", "这个回放读不出来：\n" + info.Error);
                return;
            }

            // 先关页面：回放要切到关卡场景，留着面板会在关卡里挡住画面。
            Close();
            var msg = ReplayPlayer.Start(info.Name);
            Plugin.Log?.LogInfo("[Replay] 从界面播放 " + info.Name + " → " + msg);
        }

        private static void AskDelete(ReplayInfo info)
        {
            if (info == null)
                return;
            Confirm("删除回放",
                info.StageText + "\n" + info.CreatedText + "　时长 " + info.DurationText
                + "\n\n删掉之后无法恢复，确定吗？",
                "删除", "取消",
                index => { if (index == 0) DoDelete(info); });
        }

        private static void DoDelete(ReplayInfo info)
        {
            string err;
            var ok = ReplayLibrary.Delete(info, out err);
            if (!ok)
            {
                ShowNotice("删除失败", err ?? "未知原因。");
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

        // ------------------------------------------------------------------
        // 二次确认 / 提示面板
        // ------------------------------------------------------------------

        /// <summary>
        /// 弹一个二级确认面板（<paramref name="cancelLabel"/> 传 null 就只有"好"一个按钮 = 纯提示）。
        /// 面板是官方对话框的克隆体，自带全屏点击拦截，所以是模态的。
        /// </summary>
        private static void Confirm(string title, string desc, string okLabel, string cancelLabel,
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
                dlg.SetDialog(title, desc, options, _confirmIl2Cpp);
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("rp-confirm", "弹确认面板失败：" + e.GetType().Name);
                DestroyConfirm();
            }
        }

        private static void ShowNotice(string title, string text)
        {
            Confirm(title, text, "好", null, null);
        }

        private static void DestroyConfirm()
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

        // ------------------------------------------------------------------
        // 委托
        // ------------------------------------------------------------------

        // ------------------------------------------------------------------
        // 验收用（见 Ui/ReplayGuiTest.cs）—— 只读，不改变页面状态
        // ------------------------------------------------------------------

        /// <summary>「插件」页上的回放入口装上了没有。</summary>
        public static bool EntryInstalled { get { return UiFinder.IsAlive(_entry); } }

        public static GameObject EntryNode { get { return UiFinder.IsAlive(_entry) ? _entry : null; } }

        public static GameObject PanelNode { get { return UiFinder.IsAlive(_panel) ? _panel : null; } }

        /// <summary>面板本体（<c>Center/List</c>）—— 自测要 dump 它的子树。</summary>
        public static GameObject PanelBodyNode { get { return _panelBody == null ? null : _panelBody.gameObject; } }

        public static GameObject ConfirmNode { get { return UiFinder.IsAlive(_confirm) ? _confirm : null; } }

        public static int ItemCount { get { return _items == null ? 0 : _items.Count; } }

        /// <summary>调一次「删除」的二次确认（验收用；用第一条，没有条目就什么都不做）。</summary>
        public static string TestDeleteConfirm()
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
        /// 矩形要落在屏幕内、行与行 y 不重叠、控件行里只有一个子控件是 on。
        /// 屏幕坐标一律走「局部角点 → GetWorldCorners → WorldToScreenPoint」，
        /// 不要用 <c>rect.size * scaleFactor</c> 自己算（画布缩放会把它算错）。</para>
        /// </summary>
        public static void DumpLayout(StringBuilder sb)
        {
            if (sb == null)
                return;
            sb.Append("入口按钮 : ").Append(Describe(_entry)).Append('\n');
            if (UiFinder.IsAlive(_entry))
                sb.Append("  入口文本 : ").Append(DescribeTexts(_entry)).Append('\n');
            sb.Append("页面壳   : ").Append(Describe(_panel)).Append('\n');
            sb.Append("标题区   : ").Append(Describe(
                UiFinder.IsAlive(_panel) ? ReplayUiKit.Child(_panel, "Top") : null)).Append('\n');
            if (UiFinder.IsAlive(_panel))
            {
                var title = ReplayUiKit.Child(_panel, "Top");
                var titleNode = UiFinder.IsAlive(title) ? ReplayUiKit.Child(title, "Title") : null;
                sb.Append("  标题文本 : ").Append(DescribeTexts(titleNode)).Append('\n');
            }
            sb.Append("面板     : ").Append(Describe(_panelBody == null ? null : _panelBody.gameObject)).Append('\n');
            sb.Append("数量行   : ").Append(Describe(_bar)).Append('\n');
            sb.Append("  数量文字 : ").Append(_countText == null ? "（无）" : "\"" + Trim(_countText.text, 24) + "\"").Append('\n');
            sb.Append("列表     : ").Append(Describe(_list == null ? null : _list.Root)).Append('\n');
            sb.Append("确认面板 : ").Append(Describe(_confirm)).Append('\n');
            sb.Append("条目数   : ").Append(ItemCount)
              .Append("（排序 ").Append(SortLabels[(int)_sort]).Append("）\n");
            sb.Append("屏幕     : ").Append(Screen.width).Append('x').Append(Screen.height).Append('\n');

            if (UiFinder.IsAlive(_panel))
            {
                var bottom = ReplayUiKit.Child(_panel, "Bottom");
                sb.Append("返回区   : ").Append(Describe(bottom)).Append('\n');
                if (UiFinder.IsAlive(bottom))
                    sb.Append("  返回按钮 : ").Append(Describe(ReplayUiKit.Child(bottom, "ReturnButton"))).Append('\n');
            }

            if (_list != null && _list.IsValid && _list.Content != null)
            {
                var content = _list.Content;
                sb.Append("列表内容 : ").Append(Describe(content.gameObject)).Append('\n');
                var n = Math.Min(content.childCount, 8);
                for (var i = 0; i < n; i++)
                {
                    var row = content.GetChild(i);
                    if (row == null)
                        continue;
                    sb.Append("  行").Append(i).Append(" : ").Append(Describe(row.gameObject)).Append('\n');
                    for (var c = 0; c < row.childCount; c++)
                    {
                        var cell = row.GetChild(c);
                        if (cell == null)
                            continue;
                        var label = UiClone.FindText(cell.gameObject);
                        sb.Append("    [").Append(c).Append("] ").Append(Describe(cell.gameObject));
                        if (label != null)
                            sb.Append("  \"").Append(Trim(label.text, 24)).Append('"');
                        sb.Append('\n');
                    }
                }
            }
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            s = s.Replace('\n', ' ');
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>把一个节点子树里所有 TMP 的文本列出来（排障：改了文案但界面没变时先看它）。</summary>
        private static string DescribeTexts(GameObject go)
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
                    sb.Append('"').Append(Trim(t.text, 16)).Append('"').Append('@').Append(t.fontSize).Append(' ');
                }
            }
            catch (Exception e)
            {
                return "列文本失败 " + e.GetType().Name;
            }
            return sb.ToString().TrimEnd();
        }

        private static string Describe(GameObject go)
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
                // 被调方写的是那份拷贝 —— 读回来永远是全 0（本次实测：所有矩形都报成 0x0）。
                var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
                rt.GetWorldCorners(corners);
                var min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                var max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                var w = Mathf.Abs(max.x - min.x);
                var h = Mathf.Abs(max.y - min.y);
                return (active ? "on  " : "off ")
                    + "x=" + Mathf.RoundToInt(Mathf.Min(min.x, max.x)) + ".." + Mathf.RoundToInt(Mathf.Max(min.x, max.x))
                    + " y=" + Mathf.RoundToInt(Mathf.Min(min.y, max.y)) + ".." + Mathf.RoundToInt(Mathf.Max(min.y, max.y))
                    + " (" + Mathf.RoundToInt(w) + "x" + Mathf.RoundToInt(h) + ")";
            }
            catch (Exception e)
            {
                return "测量失败 " + e.GetType().Name;
            }
        }
    }
}
