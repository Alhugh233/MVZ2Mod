using System;
using System.Collections.Generic;
using DSHCore.Ui;
using MVZ2.Blueprints;
using MVZ2.Level.Blueprints;
using MVZ2.UI;
using MVZ2Logic.Level;
using MVZ2Logic.Models;
using PVZEngine;
using PVZEngine.Tools.Unity;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 蓝图 UI 的「取材处」——**全部克隆原版选卡面板**的活节点，不自己搭控件。
    ///
    /// <para>为什么这么做：原版选卡面板里的卡片就是「关卡里掉落的那种蓝图」，
    /// 背景 / 图标 / 费用 / 充能圈 / 灰化 / 空框全是同一套 <c>BlueprintViewData</c> 与同一个
    /// <c>Blueprint</c> prefab 节点。自己拼控件永远差一点，直接克隆就对得上。</para>
    ///
    /// <para>节点路径（实测，0.7.0）：
    /// <code>
    /// Front/BlueprintChoose/Root/BlueprintChoosingPanel                     ← 选卡面板（400x528）
    /// Front/BlueprintChoose/CommandBlockPanelStandalone                    ← 命令方块的「复制面板」（800x600，5x8 网格 + 翻页）
    /// Back/Side/Blueprints/Classic/Chosen/ChosenBlueprints                 ← 已选蓝图横排条（BlueprintList）
    /// </code></para>
    /// </summary>
    internal static class BlueprintUi
    {
        /// <summary>原版选卡面板（不在关卡内 / 关卡 UI 没起来时为 null）。</summary>
        public static LevelUIBlueprintChoose Source()
        {
            try
            {
                var controller = LevelFacade.Controller;
                if (controller == null)
                    return null;
                var preset = controller.GetUIPreset();
                if (preset == null)
                    return null;
                return preset.BlueprintChoose;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool Endless()
        {
            try
            {
                var level = LevelFacade.Current;
                return level != null && level.IsEndless();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>单张卡的显示数据 —— 与蓝图掉落物走的是同一个入口。</summary>
        public static BlueprintViewData ViewOf(NamespaceID id, bool commandBlock)
        {
            try
            {
                var main = LevelFacade.Main;
                if (main == null || main.ResourceManager == null)
                    return BlueprintViewData.Empty;
                return main.ResourceManager.GetBlueprintViewData(id, Endless(), commandBlock);
            }
            catch (Exception)
            {
                return BlueprintViewData.Empty;
            }
        }

        /// <summary>选卡面板用的显示数据（原版 <c>AlmanacManager.GetChoosingBlueprintViewData</c> 的等价物）。</summary>
        public static ChoosingBlueprintViewData ChooseViewOf(NamespaceID id)
        {
            return new ChoosingBlueprintViewData
            {
                blueprint = ViewOf(id, false),
                disabled = false,
                selected = false,
                recharge = 0f,
            };
        }

        /// <summary>
        /// 候选蓝图清单：优先用 <see cref="AllBlueprintsPatch"/> 记下的**官方排序 + 追加**，
        /// 拿不到才退回「全部 seed 定义」。
        /// </summary>
        public static List<NamespaceID> Candidates()
        {
            var list = new List<NamespaceID>();
            try
            {
                var ordered = AllBlueprintsPatch.Ordered();
                for (var i = 0; i < ordered.Count; i++)
                {
                    if (NamespaceID.IsValid(ordered[i]))
                        list.Add(ordered[i]);
                }
            }
            catch (Exception) { }
            if (list.Count == 0)
                list = BlueprintOps.AllBlueprintIDs();
            return list;
        }

        public static bool Same(NamespaceID a, NamespaceID b)
        {
            return BlueprintOps.SameId(a, b);
        }

        public static bool Contains(List<NamespaceID> list, NamespaceID id)
        {
            if (list == null)
                return false;
            for (var i = 0; i < list.Count; i++)
            {
                if (Same(list[i], id))
                    return true;
            }
            return false;
        }

        public static string NameOf(NamespaceID id, bool commandBlock)
        {
            return BlueprintOps.BlueprintName(id, commandBlock);
        }
    }

    /// <summary>
    /// **蓝图内容框** —— 一个灰色底的矩形，里面**左对齐**横排一组原版蓝图卡，滚轮可横向滚动。
    ///
    /// <para>三件事的来源：</para>
    /// <list type="bullet">
    /// <item><b>框体与滚动</b> ＝ <c>UiSkinPart.ScrollView</c> 的克隆体（原版设置界面那个可滚动容器，
    /// 视口上的 <c>Mask</c> 已被 DSHCore 换成 <c>RectMask2D</c>）。原版它只能竖滚，这里把
    /// <c>horizontal</c> 打开、<c>vertical</c> 关掉 —— Unity 的 <c>ScrollRect</c> 在「只横」时
    /// 会把滚轮的 y 分量映射成横向，所以**滚轮即横滚**，样式也还是原版那一套。</item>
    /// <item><b>卡片</b> ＝ 克隆原版选卡面板里那条「已选蓝图」（<c>ChosenBlueprints</c>）当
    /// **卡片工厂**，卡片本身是原版 <c>Blueprint</c> 节点，数据与蓝图掉落物同一个入口。</item>
    /// <item><b>排版</b> ＝ 自己算（见 <see cref="Place"/>）：卡片的轴心是**左上角**，
    /// 而且工厂上那个列表组件每帧都会按它自己的规则搬卡片，所以它被 <c>enabled = false</c> 掉。</item>
    /// </list>
    ///
    /// <para>用法：<see cref="SetCount"/> 铺 N 张，再 <see cref="SetCard"/> 填内容；
    /// 内容无效（空卡槽）的位置**不画卡片**，框体自己就是那个空框。</para>
    /// </summary>
    internal sealed class BlueprintStrip
    {
        private GameObject _root;         // 框体（可滚动容器克隆体；素材缺失时退回卡片工厂本身）
        private GameObject _factory;      // 卡片工厂：原版「已选蓝图」条的克隆，0 尺寸、看不见
        private ClassicBlueprintSet _set;
        private ScrollRect _scroll;       // 框体的滚动组件（已改成「只横滚」）
        private RectTransform _viewport;  // 裁剪视口（命中测试也以它为界）
        private float _cardWidth = 48f;
        private float _cardHeight = 72f;
        private float _padX = 2f;         // 卡片与框体左右边缘的间距（贴合边框，越小越紧）
        private bool _boxed;              // 是否拿到了真正的框体素材
        private bool _stretched;
        private float _lastWidth;
        private float _lastHeight;
        private int _layoutRetries;
        private static int _placeLogs;
        private GameObject _cards;        // 卡片父节点 = 框体的 Content（横滚动的就是它）

        /// <summary>每张卡对应的蓝图 id（空框 = 无效 id）。</summary>
        public readonly List<NamespaceID> Ids = new List<NamespaceID>();
        /// <summary>每张卡对应的卡槽下标（-1 = 不是卡槽里的卡）。</summary>
        public readonly List<int> Slots = new List<int>();

        public GameObject Root { get { return UiFinder.IsAlive(_root) ? _root : null; } }

        public bool IsValid { get { return UiFinder.IsAlive(_root) && _set != null; } }

        public int Count { get { return Ids.Count; } }

        /// <summary>
        /// 克隆一条。拿不到原版节点就返回 null（调用方要能接受）。
        /// </summary>
        public static BlueprintStrip Create(Transform parent, string name)
        {
            var src = BlueprintUi.Source();
            if (src == null || src.chosenBlueprints == null)
            {
                Plugin.LogOnce("strip-src", "[Strip] 拿不到原版「已选蓝图」节点（不在关卡内？），镜像行不可用。");
                return null;
            }
            var strip = new BlueprintStrip();

            // ---- 1) 卡片工厂：原版那条「已选蓝图」只用来 CreateBlueprint() ----
            strip._factory = UiClone.Instantiate(src.chosenBlueprints.gameObject, parent,
                name + "_Factory", true);
            if (!UiFinder.IsAlive(strip._factory))
                return null;
            strip._set = strip._factory.GetComponent<ClassicBlueprintSet>();
            if (strip._set == null)
            {
                Plugin.LogOnce("strip-comp", "[Strip] 克隆体上没有 ClassicBlueprintSet，内容框不可用。");
                return null;
            }

            // 原版那条是给 480 宽准备的；量一下实际格子尺寸。
            try
            {
                strip._cardWidth = Mathf.Max(1f, strip._set.cellSize.x);
                strip._cardHeight = Mathf.Max(1f, strip._set.cellSize.y);
            }
            catch (Exception) { }

            // ⚠️ 关掉工厂上那个列表组件：它每帧都会按自己那套「offset + 自动居中」把卡片
            // 搬回去（实测我摆好的位置会被它整体推到左边）。位置改由 Place() 自己算。
            try
            {
                strip._set.enabled = false;
            }
            catch (Exception)
            {
                Plugin.LogOnce("strip-disable", "[Strip] 关不掉克隆体上的列表组件（位置可能被它覆盖）。");
            }
            MakeInvisible(strip._factory);
            strip._root = strip._factory;
            // ---- 2) 框体：原版可滚动容器（灰色底 + 矩形裁剪视口 + ScrollRect）----
            var box = UiSkin.Clone(UiSkinPart.ScrollView, parent, name);
            if (UiFinder.IsAlive(box))
            {
                strip._root = box;
                strip._boxed = true;
                try { strip._factory.transform.SetParent(box.transform, false); }
                catch (Exception) { }
                strip.SetupBox();
            }
            else
            {
                Plugin.LogOnce("strip-box", "[Strip] 拿不到可滚动容器素材，内容框退回「只有卡片」的形态。");
                strip.SetupBare();
            }

            strip.Clear();
            strip.FillRow();

            // 登记：宽高要等布局结算后才有值，靠面板的 SettleLayout 补一次
            // （不然「建好立刻读布局」会读到卡片全挤在左上角）。
            LoadoutPanel.RegisterStrip(strip);

            Plugin.Log?.LogInfo("[Strip] 内容框就绪：" + name + "  框体=" + (strip._boxed ? "有" : "无")
                + "  格子=" + strip._cardWidth + "x" + strip._cardHeight
                + "  视口=" + (strip._viewport == null ? "-" : "有")
                + "  滚动=" + (strip._scroll == null || !strip._scroll.horizontal ? "-" : "横向"));
            return strip;
        }

        // ------------------------------------------------------------------
        // 框体搭建
        // ------------------------------------------------------------------

        /// <summary>把可滚动容器克隆体配成「横向内容框」：视口裁切 + Content 当卡片父节点。</summary>
        private void SetupBox()
        {
            try
            {
                _scroll = _root.GetComponent<ScrollRect>();
                if (_scroll == null)
                    _scroll = _root.GetComponentInChildren<ScrollRect>(true);
                if (_scroll == null)
                {
                    Plugin.LogOnce("strip-scroll", "[Strip] 可滚动容器克隆体上没有 ScrollRect。");
                    SetupBare();
                    return;
                }

                try { _viewport = _scroll.viewport; } catch (Exception) { }

                // Content：优先用 ScrollRect 自己的引用，再按原版结构（Viewport 的第一个子节点）兜底。
                var content = (RectTransform)null;
                try
                {
                    var c = _scroll.content;
                    if (c != null)
                        content = c;
                }
                catch (Exception) { }
                if (content == null)
                {
                    if (_viewport == null)
                    {
                        var masks = _root.GetComponentsInChildren<RectMask2D>(true);
                        if (masks != null && masks.Length > 0)
                            _viewport = masks[0].transform.TryCast<RectTransform>();
                    }
                    if (_viewport != null && _viewport.childCount > 0)
                        content = _viewport.GetChild(0).TryCast<RectTransform>();
                }
                if (content == null)
                {
                    Plugin.LogOnce("strip-content", "[Strip] 可滚动容器里找不到 Content，退回「只有卡片」的形态。");
                    SetupBare();
                    return;
                }

                // 视口：铺满框体（原版右边要给竖向滚动条让位，这里不要滚动条了）。
                // 内边距只留 2 —— 需求要的是「蓝图与边框贴合」。
                if (_viewport != null)
                {
                    _viewport.anchorMin = Vector2.zero;
                    _viewport.anchorMax = Vector2.one;
                    _viewport.offsetMin = new Vector2(2f, 2f);
                    _viewport.offsetMax = new Vector2(-2f, -2f);
                }

                // Content 是原版列表的内容节点：摘掉它的布局组 / 尺寸适配器 / 行生成器，清空残留行。
                StripComponents(content);
                ClearChildren(content);
                _cards = content.gameObject;
                ConfigureScroll(content);
                AddFill();
            }
            catch (Exception e)
            {
                Plugin.LogOnce("strip-setup", "[Strip] 配置内容框失败：" + e.GetType().Name + ": " + e.Message);
                SetupBare();
            }
        }

        /// <summary>拿不到框体素材时的形态：卡片挂在自己建的容器上（没有灰底、不能滚）。</summary>
        private void SetupBare()
        {
            _boxed = false;
            _scroll = null;
            _viewport = null;
            var parent = UiFinder.IsAlive(_root) ? _root.transform : null;
            var go = UiHost.CreateNode("Cards", parent);
            if (!UiFinder.IsAlive(go))
                return;
            var crt = go.transform.TryCast<RectTransform>();
            if (crt != null)
            {
                crt.anchorMin = Vector2.zero;
                crt.anchorMax = Vector2.one;
                crt.pivot = new Vector2(0f, 0.5f);   // local x = 0 就是左边缘
                crt.offsetMin = Vector2.zero;
                crt.offsetMax = Vector2.zero;
            }
            var le = go.GetComponent<LayoutElement>();
            if (le == null)
                le = go.AddComponent<LayoutElement>();
            if (le != null)
                le.ignoreLayout = true;
            _cards = go;
        }

        /// <summary>
        /// 把 <c>ScrollRect</c> 改成「只横向」并接上我们自己的 Content。
        ///
        /// <para>滚轮：Unity 的 <c>ScrollRect.OnScroll</c> 在 <c>horizontal &amp;&amp; !vertical</c> 时
        /// 会把滚轮的 y 分量当作横向位移，所以「只横」的滚动框天然就是滚轮横滚，不用自己写输入。</para>
        /// </summary>
        private void ConfigureScroll(RectTransform content)
        {
            content.anchorMin = new Vector2(0f, 0f);
            content.anchorMax = new Vector2(0f, 1f);
            content.pivot = new Vector2(0f, 0.5f);     // local x = 0 就是左边缘
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(content.sizeDelta.x, 0f);

            if (_scroll == null)
                return;
            _scroll.horizontal = true;
            _scroll.vertical = false;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.inertia = false;
            // 原版默认灵敏度是 1（一格滚轮才 1 像素），横向条上根本抡不动。
            //
            // ★ 负号 = **反转滚轮方向**（需求要的）：Unity 的 ScrollRect.OnScroll 在
            //   「只横向」时算的是 `velocity.x = scrollDelta.y * scrollSensitivity`，
            //   所以把灵敏度取负，滚轮的位移方向就整体翻过来。
            _scroll.scrollSensitivity = -40f;

            // 滚轮要落到 ScrollRect 上，前提是鼠标下至少有一层能接收射线：
            // 卡片的射线是自己关掉的（命中归 UiHitTest），所以框体自己得当那一层。
            // 透明 Image 不会显形（alpha = 0），但整块矩形区域都能接到滚轮。
            try
            {
                var img = _root.GetComponent<Image>();
                if (img == null)
                {
                    img = _root.AddComponent<Image>();
                    if (img != null)
                        img.color = new Color(0f, 0f, 0f, 0f);
                }
                if (img != null)
                    img.raycastTarget = true;
            }
            catch (Exception) { }
            try
            {
                var vs = _scroll.verticalScrollbar;
                if (vs != null)
                    vs.gameObject.SetActive(false);
                var hs = _scroll.horizontalScrollbar;
                if (hs != null)
                    hs.gameObject.SetActive(false);
            }
            catch (Exception) { }
            _scroll.verticalScrollbar = null;
            _scroll.horizontalScrollbar = null;
            _scroll.content = content;
        }

        /// <summary>
        /// 垫一层纯色底。
        ///
        /// <para>原版那个背景用的是 <c>form_hollow</c>（一个「空框」贴图，中间未必是实的），
        /// 而需求要的是「一个灰色底的矩形」。垫层加在**最下面**（<c>SetSiblingIndex(0)</c>）：
        /// 背景贴图是实的话它被盖住、什么也不影响；背景是空框的话它就是那块灰底。</para>
        /// </summary>
        private void AddFill()
        {
            try
            {
                var go = UiHost.CreateNode("Fill", _root.transform);
                if (!UiFinder.IsAlive(go))
                    return;
                go.transform.SetSiblingIndex(0);
                var rt = go.transform.TryCast<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.offsetMin = new Vector2(1f, 1f);
                    rt.offsetMax = new Vector2(-1f, -1f);
                }
                var img = go.AddComponent<Image>();
                if (img != null)
                {
                    img.sprite = null;                                  // 纯色矩形
                    img.color = new Color(0f, 0f, 0f, 0.35f);           // 深灰（半透明）
                    img.raycastTarget = false;                          // 射线交给框体自己
                }
                var le = go.AddComponent<LayoutElement>();
                if (le != null)
                    le.ignoreLayout = true;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("strip-fill", "[Strip] 加底色失败：" + e.GetType().Name);
            }
        }

        /// <summary>把节点变成「看不见、也不参与布局」的纯容器（卡片工厂用）。</summary>
        private static void MakeInvisible(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                var rt = go.transform.TryCast<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = new Vector2(0f, 1f);
                    rt.anchorMax = new Vector2(0f, 1f);
                    rt.pivot = new Vector2(0f, 1f);
                    rt.sizeDelta = Vector2.zero;
                    rt.anchoredPosition = Vector2.zero;
                }
                var le = go.GetComponent<LayoutElement>();
                if (le == null)
                    le = go.AddComponent<LayoutElement>();
                if (le != null)
                {
                    le.ignoreLayout = true;
                    le.minWidth = 0f;
                    le.minHeight = 0f;
                    le.preferredWidth = 0f;
                    le.preferredHeight = 0f;
                    le.flexibleWidth = 0f;
                    le.flexibleHeight = 0f;
                }
            }
            catch (Exception) { }
        }

        /// <summary>摘掉会让手工排版失效的组件（布局组 / 尺寸适配器 / 原版行生成器）。</summary>
        private static void StripComponents(RectTransform rt)
        {
            if (rt == null)
                return;
            Kill(rt.GetComponent<HorizontalLayoutGroup>());
            Kill(rt.GetComponent<VerticalLayoutGroup>());
            Kill(rt.GetComponent<GridLayoutGroup>());
            Kill(rt.GetComponent<ContentSizeFitter>());
            Kill(rt.GetComponent<LayoutElement>());
            Kill(rt.GetComponent<PVZEngine.Tools.Unity.ElementList>());
            Kill(rt.GetComponent<PVZEngine.Tools.Unity.ElementListUI>());
        }

        private static void ClearChildren(RectTransform rt)
        {
            if (rt == null)
                return;
            try
            {
                for (var i = rt.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.DestroyImmediate(rt.GetChild(i).gameObject);
            }
            catch (Exception) { }
        }

        /// <summary>立刻销毁组件（<c>Destroy</c> 要到帧末才生效，那时 <c>Awake</c> 已经跑过了）。</summary>
        private static void Kill(Component c)
        {
            if (c == null)
                return;
            try { UnityEngine.Object.DestroyImmediate(c); }
            catch (Exception) { }
        }

        /// <summary>
        /// 让这条**拉伸填满所在行**，并且**不被布局组排版**。
        ///
        /// <para>注意：<c>UiGridRow.AddCell</c> 会把 <c>ignoreLayout</c> 重新设回 false
        /// （它以为这一格该由布局组按 <c>preferredHeight</c> 定尺寸），所以搬进行里之后
        /// 要再调一次这个。</para>
        /// </summary>
        public void FillRow()
        {
            if (!IsValid)
                return;
            try
            {
                var rootRt = _root.transform.TryCast<RectTransform>();
                if (rootRt != null)
                {
                    rootRt.anchorMin = Vector2.zero;
                    rootRt.anchorMax = Vector2.one;
                    rootRt.pivot = new Vector2(0.5f, 0.5f);
                    rootRt.offsetMin = Vector2.zero;
                    rootRt.offsetMax = Vector2.zero;
                }
                var rle = _root.GetComponent<LayoutElement>();
                if (rle == null)
                    rle = _root.AddComponent<LayoutElement>();
                if (rle != null)
                    rle.ignoreLayout = true;
                _stretched = true;
                _lastWidth = 0f;
                _lastHeight = 0f;
                _layoutRetries = 0;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("strip-fillrow", "[Strip] 撑满所在行失败：" + e.GetType().Name);
            }
        }

        /// <summary>
        /// 把框体**定死**成 <paramref name="width"/> × <paramref name="height"/>（居中锚点）。
        ///
        /// <para>只给「不在布局组里、要自己摆位」的场合用（比如选卡浮层上方那条已选框）。
        /// 放进 <c>UiGridRow</c> 的那种**不要调**：让行布局组按 <c>flexibleWidth</c> 把它撑满整行。</para>
        /// </summary>
        public void SetSize(float width, float height)
        {
            if (!IsValid)
                return;
            try
            {
                var rt = _root.transform.TryCast<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = new Vector2(0.5f, 0.5f);
                    rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.sizeDelta = new Vector2(width, height);
                }
                _stretched = true;
                _lastWidth = width;
                _lastHeight = height;
                _layoutRetries = 0;
                Place();
            }
            catch (Exception e)
            {
                Plugin.LogOnce("strip-size", "[Strip] 设置尺寸失败：" + e.GetType().Name);
            }
        }

        public bool Stretched { get { return _stretched; } }

        /// <summary>
        /// 容器在布局组里，宽高要等一帧才有值 → 每帧调一次。
        /// <para>每帧都会重摆一次卡片：成本是几次 <c>localPosition</c> 赋值（位置没变就不写），
        /// 但能顶掉任何别的组件对卡片位置的改动。</para>
        /// </summary>
        public void FitWidth()
        {
            if (!IsValid)
                return;
            try
            {
                var rt = _root.transform.TryCast<RectTransform>();
                if (rt == null)
                    return;
                var width = rt.rect.width;
                if (width <= 1f)
                {
                    // 行还没被布局组算过（整条是 0x0）→ 逼它算一次；只重试几次，避免每帧硬算。
                    if (_layoutRetries < 3)
                    {
                        _layoutRetries++;
                        LoadoutPanel.RebuildLayout();
                    }
                    return;
                }
                _layoutRetries = 0;
                _lastWidth = width;
                _lastHeight = rt.rect.height;
                Place();
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 摆卡片 + 给 Content 定宽。
        ///
        /// <para><b>★ 卡片的轴心是 (0,1)（左上角）</b> —— 把卡片放在局部 <c>(x, y)</c> 时它占
        /// <c>[x, x+格宽] × [y-格高, y]</c>，向右下生长。而 Content 的 local 原点在
        /// **框体左边缘的水平线上（竖直中线）**，所以：<br/>
        /// · 横向左对齐：第 i 张的左边缘 = <c>内边距 + i * 格宽</c>；<br/>
        /// · 纵向居中：上边缘 = <c>格高 / 2</c>（与框体高度无关）。</para>
        ///
        /// <para>Content 的宽度 = 内边距×2 + 张数×格宽 —— 比视口窄时原版的 <c>Clamped</c> 会把它
        /// 拴在左边（正好是「左对齐」），比视口宽时才能横向滚。</para>
        ///
        /// <para>不用原版的 <c>offset</c> / <c>GetBlueprintLocalPosition</c>：它还要再叠一层自己的
        /// 「自动居中」，实测会把整组推到最左边。</para>
        /// </summary>
        private void Place()
        {
            if (!IsValid)
                return;
            var want = UiFinder.IsAlive(_cards) ? _cards.transform : null;
            if (want == null)
                return;

            if (_boxed)
            {
                var crt = want.TryCast<RectTransform>();
                if (crt != null)
                {
                    var w = _padX * 2f + Ids.Count * _cardWidth;
                    if (Mathf.Abs(crt.sizeDelta.x - w) > 0.5f)
                        crt.sizeDelta = new Vector2(w, crt.sizeDelta.y);
                }
            }

            var y = _cardHeight * 0.5f;
            for (var i = 0; i < Ids.Count; i++)
            {
                var bp = _set.GetBlueprintAt(i);
                if (bp == null)
                    continue;
                try
                {
                    if (bp.transform.parent != want)
                        bp.transform.SetParent(want, false);
                    var target = new Vector3(_padX + i * _cardWidth, y, 0f);
                    if ((bp.transform.localPosition - target).sqrMagnitude > 0.01f)
                        bp.transform.localPosition = target;
                }
                catch (Exception) { }
            }

            if (_placeLogs < 2)
            {
                _placeLogs++;
                Plugin.Log?.LogInfo("[Strip] 摆卡 n=" + Ids.Count + "  起x=" + _padX
                    + "  上边缘=" + y + "  框体=" + (_boxed ? "有" : "无"));
            }
        }

        /// <summary>
        /// 把卡片挪到**我们自己**的容器下（见 <see cref="Create"/> 里的说明）。
        /// </summary>
        private void Adopt(Blueprint bp)
        {
            if (bp == null)
                return;
            try
            {
                var parent = UiFinder.IsAlive(_cards) ? _cards.transform : _root.transform;
                if (parent != null)
                    bp.transform.SetParent(parent, false);
            }
            catch (Exception) { }
        }

        /// <summary>清空成 0 张。</summary>
        public void Clear()
        {
            Ids.Clear();
            Slots.Clear();
            if (_set == null)
                return;
            try
            {
                for (var i = _set.GetBlueprintCount() - 1; i >= 0; i--)
                    _set.DestroyBlueprintAt(i);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("strip-clear", "[Strip] 清空失败：" + e.GetType().Name);
            }
            // 兜底：万一有卡片被漏在外面（原版列表的 itemList 与我们不同步时），直接扫掉。
            try
            {
                if (UiFinder.IsAlive(_cards))
                {
                    var leftovers = _cards.GetComponentsInChildren<Blueprint>(true);
                    for (var i = 0; leftovers != null && i < leftovers.Length; i++)
                    {
                        if (leftovers[i] != null)
                            UnityEngine.Object.Destroy(leftovers[i].gameObject);
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 铺 <paramref name="count"/> 张卡（内容由 <see cref="SetCard"/> 填）。
        /// 传 0 就是什么都不画 —— **框体自己就是那个空框**（需求：空卡槽不画深色卡片）。
        /// </summary>
        public void SetCount(int count)
        {
            Clear();
            if (!IsValid || count <= 0)
            {
                Place();
                return;
            }
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var bp = _set.CreateBlueprint();
                    if (bp == null)
                        continue;
                    _set.InsertBlueprint(i, bp);
                    Adopt(bp);
                    bp.UpdateView(BlueprintViewData.Empty);
                    bp.SetDisabled(false);
                    bp.SetSelected(false);
                    // ⚠ 不能写 SetRecharge(1)：那会让卡的 `Recharge`（sprite=card_curtain，alpha 0.5）
                    // 整张盖满，卡看上去就是「洗白/发灰」的。0 = 帷幕收起，与原版未选中的卡一致。
                    bp.SetRecharge(0f);
                    // 先全部藏起来，等内容填上再显示（空卡槽不画东西）。
                    bp.gameObject.SetActive(false);
                    Ids.Add(null);
                    Slots.Add(-1);
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("strip-count", "[Strip] 铺卡失败：" + e.GetType().Name + ": " + e.Message);
            }
            Place();
        }

        /// <summary>填第 <paramref name="index"/> 格。传无效 id 就是**不画**（空出来）。</summary>
        public void SetCard(int index, NamespaceID id, bool commandBlock, int slot)
        {
            if (!IsValid || index < 0 || index >= Ids.Count)
                return;
            try
            {
                var bp = _set.GetBlueprintAt(index);
                if (bp == null)
                    return;
                Ids[index] = id;
                Slots[index] = slot;
                if (!NamespaceID.IsValid(id))
                {
                    bp.gameObject.SetActive(false);
                    return;
                }
                bp.gameObject.SetActive(true);
                bp.UpdateView(BlueprintUi.ViewOf(id, commandBlock));
                bp.SetDisabled(false);
                bp.SetSelected(false);
                // 同上：0 = 不盖帷幕（卡面亮着）。
                bp.SetRecharge(0f);
                // 悬停提示：蓝图名 + 说明（没定义说明就用 id）。见 BlueprintTooltip。
                BlueprintTooltip.Apply(bp, id, commandBlock);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 把某格标成「已选」：用原版那层**灰化覆盖**（<c>SetDisabled</c>）——
        /// 与原版选卡面板里「已选的卡」是同一种表现。未选中的格子一律恢复成正常。
        /// </summary>
        public void SetMark(int index, bool selected)
        {
            if (!IsValid || index < 0 || index >= Ids.Count)
                return;
            if (!NamespaceID.IsValid(Ids[index]))
                return;
            try
            {
                var bp = _set.GetBlueprintAt(index);
                if (bp != null)
                {
                    bp.SetSelected(false);
                    bp.SetDisabled(selected);
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 把某格**左右镜像**（<c>false</c> = 还原）。
        ///
        /// <para>用途：这张卡开了「逆阵营放置」时，镜像框里的**器械图标左右翻转** ——
        /// 一眼能看出哪张被动过手脚，而且不脏色（早期版本是染红，看着像"被禁用"）。</para>
        ///
        /// <para>只翻 <c>iconImage</c> / <c>iconImageCommandBlock</c> 这两个图标节点
        /// （<c>Blueprint</c> 自己的公开字段，<c>ImageResolver</c> 上就有 <c>rectTransform</c>），
        /// 不碰卡框与费用文字 —— 整个卡翻的话费用数字会变成镜像，读不了。</para>
        /// </summary>
        public void SetMirror(int index, bool mirror)
        {
            if (!IsValid || index < 0 || index >= Ids.Count)
                return;
            var bp = _set.GetBlueprintAt(index);
            if (bp == null)
                return;
            try
            {
                var scale = mirror ? new Vector3(-1f, 1f, 1f) : Vector3.one;
                ApplyMirror(bp.iconImage, scale);
                ApplyMirror(bp.iconImageCommandBlock, scale);
            }
            catch (Exception) { }
        }

        private static void ApplyMirror(MVZ2Logic.UI.ImageResolver resolver, Vector3 scale)
        {
            if (resolver == null)
                return;
            try
            {
                var rt = resolver.rectTransform;
                if (rt != null)
                    rt.localScale = scale;
            }
            catch (Exception) { }
        }

        /// <summary>命中测试：返回被点到的格子下标（-1 = 没点到）。</summary>
        public int Hit(Vector2 screenPoint)
        {
            if (!IsValid)
                return -1;
            // 滚出框外的部分不算命中（卡片矩形会超出被裁切的视口）。
            if (_viewport != null && !UiHitTest.Contains(_viewport.gameObject, screenPoint))
                return -1;
            try
            {
                for (var i = 0; i < Ids.Count; i++)
                {
                    var bp = _set.GetBlueprintAt(i);
                    if (bp == null || !NamespaceID.IsValid(Ids[i]))
                        continue;
                    if (UiHitTest.Contains(bp.gameObject, screenPoint))
                        return i;
                }
            }
            catch (Exception) { }
            return -1;
        }

        public string Describe()
        {
            return "格数=" + Ids.Count + " 有效=" + ValidCount() + " 框体=" + (_boxed ? "有" : "无");
        }

        /// <summary>自测用：第 <paramref name="index"/> 格的卡状态（含各覆盖层的显隐）。</summary>
        public string CardStateAt(int index)
        {
            if (!IsValid)
                return "（框体无效）";
            if (index < 0 || index >= Ids.Count)
                return "（越界 " + index + "/" + Ids.Count + "）";
            try { return CardState(_set.GetBlueprintAt(index)); }
            catch (Exception e) { return "（" + e.GetType().Name + "）"; }
        }

        /// <summary>
        /// 自测用：第 <paramref name="index"/> 格那两个图标节点的**缩放与颜色**。
        ///
        /// <para>逆阵营的观感是「左右翻转」（<c>localScale.x = -1</c>）而**不是染红** ——
        /// 这两件事都得能直接读出来才叫验过。</para>
        /// </summary>
        public string MirrorStateAt(int index)
        {
            if (!IsValid || index < 0 || index >= Ids.Count)
                return "（越界 " + index + "）";
            var bp = _set.GetBlueprintAt(index);
            if (bp == null)
                return "（无卡）";
            var sx = ScaleX(bp.iconImage);
            var color = ColorOf(bp.iconImage);
            var white = Mathf.Abs(color.r - 1f) < 0.01f && Mathf.Abs(color.g - 1f) < 0.01f && Mathf.Abs(color.b - 1f) < 0.01f;
            return "Icon.sx=" + Fmt(sx)
                + "  CommandBlockIcon.sx=" + Fmt(ScaleX(bp.iconImageCommandBlock))
                + "  Icon.色=" + Fmt(color.r) + "/" + Fmt(color.g) + "/" + Fmt(color.b)
                + "\n    判据 图标左右翻转=" + (sx < 0f ? "OK（sx=-1）" : "NG（没翻转）")
                + "  判据 没有染红=" + (white ? "OK（仍是白）" : "NG（被染色了）");
        }

        private static float ScaleX(MVZ2Logic.UI.ImageResolver resolver)
        {
            try
            {
                var rt = resolver == null ? null : resolver.rectTransform;
                return rt == null ? 1f : rt.localScale.x;
            }
            catch (Exception) { return 1f; }
        }

        private static Color ColorOf(MVZ2Logic.UI.ImageResolver resolver)
        {
            try
            {
                var rt = resolver == null ? null : resolver.rectTransform;
                var img = rt == null ? null : rt.GetComponent<Image>();
                return img == null ? Color.white : img.color;
            }
            catch (Exception) { return Color.white; }
        }

        /// <summary>自测用：第 <paramref name="index"/> 格那张卡上的悬停提示状态。</summary>
        public string DescribeTooltip(int index)
        {
            if (!IsValid)
                return "（框体无效）";
            if (index < 0 || index >= Ids.Count)
                return "（越界 " + index + "/" + Ids.Count + "）";
            try { return BlueprintTooltip.Describe(_set.GetBlueprintAt(index)); }
            catch (Exception e) { return "（" + e.GetType().Name + "）"; }
        }

        /// <summary>
        /// 滚到最右端。
        ///
        /// <para>为什么需要它：框体宽 386 只能装下 8 张卡（386 / 48 ≈ 8.04）。**卡槽一满，
        /// 新加的那张就在框外了** —— 用户会以为「点了没反应」或者「数量没变」。</para>
        /// </summary>
        public void ScrollToEnd()
        {
            if (!IsValid || _scroll == null)
                return;
            try
            {
                _scroll.horizontalNormalizedPosition = 1f;
                // 内容比视口窄时归一化位置没有意义（会把它推到最左），这里显式兜一下。
                var content = UiFinder.IsAlive(_cards) ? _cards.transform.TryCast<RectTransform>() : null;
                var viewport = _viewport == null ? null : _viewport.TryCast<RectTransform>();
                if (content == null || viewport == null)
                    return;
                if (content.rect.width <= viewport.rect.width + 1f)
                    _scroll.horizontalNormalizedPosition = 0f;
            }
            catch (Exception) { }
        }

        /// <summary>自测用：把这条的每个格子的屏幕矩形打出来（「排到哪儿去了」只能这样看）。</summary>
        public string DebugRects(string label)
        {
            var sb = new System.Text.StringBuilder(600);
            if (!IsValid)
            {
                sb.Append("  ").Append(label).Append(" = （无效）\n");
                return sb.ToString();
            }
            try
            {
                var rt = _root.transform.TryCast<RectTransform>();
                var r = PanelDump.ScreenRect(rt);
                sb.Append("  ").Append(label).Append(" 设计 ")
                  .Append(Mathf.RoundToInt(rt.rect.width)).Append('x').Append(Mathf.RoundToInt(rt.rect.height))
                  .Append(" 屏 x[").Append(Mathf.RoundToInt(r.xMin)).Append(',').Append(Mathf.RoundToInt(r.xMax))
                  .Append("] y[").Append(Mathf.RoundToInt(r.yMin)).Append(',').Append(Mathf.RoundToInt(r.yMax))
                  .Append("] 格子=").Append(Fmt(_cardWidth)).Append("x").Append(Fmt(_cardHeight))
                  .Append(" 框体=").Append(_boxed ? "有" : "无")
                  .Append(" 滚动=").Append(_scroll == null ? "-" : (_scroll.horizontal ? "横" : "竖"))
                  .Append(" 记录尺寸=").Append(Fmt(_lastWidth)).Append("x").Append(Fmt(_lastHeight))
                  .Append(" 列表组件=").Append(_set != null && _set.enabled ? "on" : "off")
                  .Append(" 列表条目=").Append(Count2())
                  .Append(" 子节点=").Append(UiFinder.IsAlive(_cards)
                      ? _cards.transform.childCount.ToString() : "-")
                  .Append(" 卡片父节点=").Append(UiFinder.IsAlive(_cards) ? UiFinder.PathOf(_cards.transform) : "（无）")
                  .Append('\n');
                if (_viewport != null)
                {
                    var vr = PanelDump.ScreenRect(_viewport);
                    sb.Append("    视口 屏 x[").Append(Mathf.RoundToInt(vr.xMin)).Append(',').Append(Mathf.RoundToInt(vr.xMax))
                      .Append("] y[").Append(Mathf.RoundToInt(vr.yMin)).Append(',').Append(Mathf.RoundToInt(vr.yMax))
                      .Append("] 组件=").Append(ComponentNames(_viewport.gameObject)).Append('\n');
                }
                if (UiFinder.IsAlive(_cards))
                {
                    var cr = PanelDump.ScreenRect(_cards.transform.TryCast<RectTransform>());
                    sb.Append("    内容 设计 ").Append(Fmt(_cards.transform.TryCast<RectTransform>().rect.width))
                      .Append(" 屏 x[").Append(Mathf.RoundToInt(cr.xMin)).Append(',').Append(Mathf.RoundToInt(cr.xMax))
                      .Append("] y[").Append(Mathf.RoundToInt(cr.yMin)).Append(',').Append(Mathf.RoundToInt(cr.yMax))
                      .Append("] 组件=").Append(ComponentNames(_cards)).Append('\n');
                }
                sb.Append("    根 锚").Append(V(rt.anchorMin)).Append("~").Append(V(rt.anchorMax))
                  .Append(" 轴").Append(V(rt.pivot))
                  .Append(" 位").Append(V(rt.anchoredPosition))
                  .Append(" 尺寸").Append(V(rt.sizeDelta)).Append('\n');
                var crt = UiFinder.IsAlive(_cards) ? _cards.transform.TryCast<RectTransform>() : null;
                if (crt != null)
                {
                    var cr = PanelDump.ScreenRect(crt);
                    sb.Append("    框体图像=").Append(ImageInfo()).Append('\n');
                    sb.Append("    卡父 锚").Append(V(crt.anchorMin)).Append("~").Append(V(crt.anchorMax))
                      .Append(" 轴").Append(V(crt.pivot))
                      .Append(" 位").Append(V(crt.anchoredPosition))
                      .Append(" 尺寸").Append(V(crt.sizeDelta))
                      .Append(" 屏 y[").Append(Mathf.RoundToInt(cr.yMin)).Append(',').Append(Mathf.RoundToInt(cr.yMax))
                      .Append("] 局部y=").Append(Fmt(crt.localPosition.y)).Append('\n');
                    sb.Append("    卡父组件=").Append(ComponentNames(_cards)).Append('\n');
                    sb.Append("    卡片组件=").Append(UiFinder.IsAlive(_cards)
                        ? ComponentNames(_cards.transform.GetChild(0).gameObject) : "（无）").Append('\n');
                }
                for (var i = 0; i < Ids.Count; i++)
                {
                    var bp = _set.GetBlueprintAt(i);
                    if (bp == null)
                        continue;
                    var br = PanelDump.ScreenRect(bp.transform.TryCast<RectTransform>());
                    sb.Append("    #").Append(i)
                      .Append(' ').Append(NamespaceID.IsValid(Ids[i]) ? BlueprintOps.BlueprintName(Ids[i], false) : "（空）")
                      .Append(" 屏 x[").Append(Mathf.RoundToInt(br.xMin)).Append(',').Append(Mathf.RoundToInt(br.xMax))
                      .Append("] y[").Append(Mathf.RoundToInt(br.yMin)).Append(',').Append(Mathf.RoundToInt(br.yMax))
                      .Append("] 局部x=").Append(Fmt(bp.transform.localPosition.x))
                      .Append(" 局部y=").Append(Fmt(bp.transform.localPosition.y))
                      .Append(" 节点=").Append(bp.name).Append('#').Append(bp.GetInstanceID())
                      .Append(" 父=").Append(Tail(bp.transform.parent))
                      .Append('\n');
                    sb.Append("       ").Append(CardState(bp)).Append('\n');
                }
            }
            catch (Exception e)
            {
                sb.Append("  ").Append(label).Append(" dump 失败：").Append(e.GetType().Name).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 一张卡**内部各层**的开关（「这张卡到底灰没灰」只能这样看）。
        ///
        /// <para>卡片模板的两层：<c>Empty</c>（空框）与 <c>Root</c>（真卡面）；
        /// 卡面里还叠着 <c>Disabled</c>（灰化）/ <c>Selected</c>（选中）/ <c>Recharge</c>（充能遮罩）
        /// / <c>Twinkle</c>（闪）几张覆盖图。灰不灰看的是 <c>Root/Disabled</c> 这一层的开关。</para>
        /// </summary>
        /// <summary>
        /// 一张卡**内部各层**的实况 —— 按**同层顺序**列出（顺序就是绘制顺序，后画的盖住先画的）。
        ///
        /// <para>卡片模板的两层：<c>Empty</c>（空框）与 <c>Root</c>（真卡面）；卡面里还叠着
        /// <c>Disabled</c>/<c>Selected</c>/<c>Recharge</c>/<c>Twinkle</c> 几张覆盖图。
        /// 「这张卡为什么看起来是灰的」只能靠「谁在谁上面 + 各自的贴图/透明度/填充量」来看。</para>
        /// </summary>
        public static string CardState(Blueprint bp)
        {
            if (bp == null)
                return "（无卡）";
            var sb = new System.Text.StringBuilder(200);
            Leaf(sb, bp.transform, "Empty");
            Leaf(sb, bp.transform, "Root");
            var root = Find(bp.transform, "Root");
            if (root == null)
                return sb.ToString();
            sb.Append(" | Root层（按顺序）：");
            for (var i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c == null)
                    continue;
                sb.Append(i).Append(':').Append(Img(c.name, c)).Append(' ');
            }
            var cost = Find(root, "Cost");
            if (cost != null)
                sb.Append(" Cost='").Append(Text(cost)).Append('\'');
            return sb.ToString();
        }

        /// <summary>一张覆盖图的实况（开关 / 透明度 / 填充量 / 贴图）—— 「到底哪一层把卡盖住了」看这里。</summary>
        private static string Img(string label, Transform t)
        {
            if (t == null)
                return label + "=（无）";
            try
            {
                var img = t.GetComponent<Image>();
                if (img == null)
                    return label + "=（无图）";
                var c = img.color;
                // sx/lsx：镜像看的是 <c>localScale.x</c>（逆阵营 = -1），和颜色无关
                //（早先是把图标染红，现在改成左右翻转）。
                var s = t.localScale;
                return label + "(on=" + (t.gameObject.activeSelf ? "1" : "0")
                    + (t.gameObject.activeInHierarchy ? "" : "/链关")
                    + ",en=" + (img.enabled ? "1" : "0")
                    + ",a=" + Fmt(c.a)
                    + ",rgb=" + Fmt(c.r) + "/" + Fmt(c.g) + "/" + Fmt(c.b)
                    + ",fill=" + Fmt(img.fillAmount)
                    + ",sx=" + Fmt(s.x)
                    + ",spr=" + (img.sprite == null ? "无" : img.sprite.name) + ")";
            }
            catch (Exception)
            {
                return label + "=（?）";
            }
        }

        private static Transform Find(Transform parent, string name)
        {
            if (parent == null || string.IsNullOrEmpty(name))
                return null;
            try { return parent.Find(name); }
            catch (Exception) { return null; }
        }

        private static void Leaf(System.Text.StringBuilder sb, Transform parent, string name)
        {
            var t = Find(parent, name);
            if (t == null)
                return;
            if (sb.Length > 0)
                sb.Append(' ');
            // 两个都要看：activeSelf=1 但父链被关掉时**一个像素都画不出来**。
            sb.Append(name).Append('=')
              .Append(t.gameObject.activeSelf ? "on" : "off")
              .Append(t.gameObject.activeInHierarchy ? "" : "（链关）");
        }

        private static string Text(Transform t)
        {
            try
            {
                var tmp = t.GetComponent<TextMeshProUGUI>();
                return tmp == null ? "?" : tmp.text;
            }
            catch (Exception) { return "?"; }
        }

        /// <summary>
        /// 自测用：给前几张卡分别套上不同的「灰化 / 充能」组合，然后截图量像素 ——
        /// 「到底哪一组才是原版那种亮卡」只能这样试出来。
        /// <list type="number">
        /// <item>#0 SetRecharge(0)</item>
        /// <item>#1 SetDisabled(true)</item>
        /// <item>#2 SetRecharge(0) + SetDisabled(true)</item>
        /// <item>#3 SetRecharge(0) + SetSelected(true)</item>
        /// <item>#4 SetRecharge(0.5)</item>
        /// <item>#5 对照（不动）</item>
        /// </list>
        /// </summary>
        public string DebugVariants()
        {
            if (!IsValid)
                return "（内容框无效）";
            var sb = new System.Text.StringBuilder(200);
            for (var i = 0; i < Ids.Count && i < 6; i++)
            {
                var bp = _set.GetBlueprintAt(i);
                if (bp == null)
                    continue;
                try
                {
                    switch (i)
                    {
                        case 0: bp.SetRecharge(0f); break;
                        case 1: bp.SetDisabled(true); break;
                        case 2: bp.SetRecharge(0f); bp.SetDisabled(true); break;
                        case 3: bp.SetRecharge(0f); bp.SetSelected(true); break;
                        case 4: bp.SetRecharge(0.5f); break;
                        default: break;
                    }
                    var root = Find(bp.transform, "Root");
                    sb.Append('#').Append(i).Append(' ')
                      .Append(Img("Rech", Find(root, "Recharge"))).Append(' ')
                      .Append(Img("Dis", Find(root, "Disabled"))).Append('\n');
                }
                catch (Exception e)
                {
                    sb.Append('#').Append(i).Append(" 失败 ").Append(e.GetType().Name).Append('\n');
                }
            }
            return "变体测试：\n" + sb;
        }

        /// <summary>第 <paramref name="index"/> 格对应的蓝图 id（越界/无效返回 null）。</summary>
        public NamespaceID IdAt(int index)
        {
            return index < 0 || index >= Ids.Count ? null : Ids[index];
        }

        /// <summary>第 <paramref name="index"/> 格的卡片组件。</summary>
        public Blueprint BlueprintAt(int index)
        {
            if (!IsValid || index < 0 || index >= Ids.Count)
                return null;
            return _set.GetBlueprintAt(index);
        }

        /// <summary>自测用：第 <paramref name="index"/> 张卡的图标实况。</summary>
        public string DebugIcon(int index)
        {
            if (!IsValid || index < 0 || index >= Ids.Count)
                return "（没有这张卡）";
            return IconInfo(_set.GetBlueprintAt(index));
        }

        /// <summary>自测用：第 <paramref name="index"/> 张卡的**整棵节点树**（找出「网格卡多出来的东西」）。</summary>
        public string DebugTree(int index)
        {
            if (!IsValid || index < 0 || index >= Ids.Count)
                return "（没有这张卡）";
            var bp = _set.GetBlueprintAt(index);
            if (bp == null)
                return "（没有这张卡）";
            return BlueprintTree(bp);
        }

        /// <summary>一张卡的整棵节点树（名字 + 开关 + 有没有 Graphic）。</summary>
        public static string BlueprintTree(Blueprint bp)
        {
            if (bp == null)
                return "（无卡）";
            var sb = new System.Text.StringBuilder(400);
            try
            {
                var all = bp.GetComponentsInChildren<Transform>(true);
                sb.Append("节点数=").Append(all == null ? 0 : all.Length).Append('\n');
                for (var i = 0; all != null && i < all.Length; i++)
                {
                    var t = all[i];
                    if (t == null)
                        continue;
                    sb.Append("  ").Append(i).Append(':').Append(t.name)
                      .Append(t.gameObject.activeSelf ? "(on" : "(off");
                    if (!t.gameObject.activeInHierarchy)
                        sb.Append("/链关");
                    sb.Append(')');
                    try
                    {
                        var img = t.GetComponent<Image>();
                        if (img != null)
                            sb.Append(" Img[a=").Append(Fmt(img.color.a))
                              .Append(",rgb=").Append(Fmt(img.color.r)).Append('/').Append(Fmt(img.color.g))
                              .Append('/').Append(Fmt(img.color.b))
                              .Append(",spr=").Append(img.sprite == null ? "无" : img.sprite.name)
                              .Append(",mat=").Append(img.material == null ? "无" : img.material.name).Append(']');
                    }
                    catch (Exception) { }
                    sb.Append('\n');
                }
            }
            catch (Exception e)
            {
                sb.Append("树 dump 失败：").Append(e.GetType().Name).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 一张卡的图标层实况：**贴图对象本身**（实例 id / 贴图尺寸 / 矩形 / 材质）。
        ///
        /// <para>为什么要看实例 id：「贴图名字对得上」不代表是同一张贴图 ——
        /// 资源系统完全可能给了一个同名的占位图。</para>
        /// </summary>
        public static string IconInfo(Blueprint bp)
        {
            if (bp == null)
                return "（无卡）";
            var sb = new System.Text.StringBuilder(250);
            try
            {
                var root = Find(bp.transform, "Root");
                var icons = Find(root, "Icons");
                var icon = Find(icons, "Icon");
                sb.Append("Icons 尺寸=").Append(Rect(icons)).Append(" 链=").Append(Chain(icons));
                sb.Append("\n    Icon 尺寸=").Append(Rect(icon)).Append(" 链=").Append(Chain(icon));
                var img = icon == null ? null : icon.GetComponent<Image>();
                if (img == null)
                {
                    sb.Append("\n    （Icon 上没有 Image）");
                    return sb.ToString();
                }
                var sp = img.sprite;
                sb.Append("\n    Image en=").Append(img.enabled ? 1 : 0)
                  .Append(" a=").Append(Fmt(img.color.a))
                  .Append(" type=").Append(img.type)
                  .Append(" maskable=").Append(img.maskable ? 1 : 0)
                  .Append(" mat=").Append(img.material == null ? "无" : img.material.name)
                  .Append(" 屏=").Append(ScreenRect(icon));
                sb.Append("\n    sprite=").Append(sp == null ? "无" : sp.name)
                  .Append(" id=").Append(sp == null ? 0 : sp.GetInstanceID())
                  .Append(" rect=").Append(sp == null ? "-" : sp.rect.width + "x" + sp.rect.height)
                  .Append(" px/unit=").Append(sp == null ? "-" : Fmt(sp.pixelsPerUnit));
                var tex = sp == null ? null : sp.texture;
                sb.Append("\n    texture=").Append(tex == null ? "无" : tex.name)
                  .Append(" id=").Append(tex == null ? 0 : tex.GetInstanceID())
                  .Append(" 尺寸=").Append(tex == null ? "-" : tex.width + "x" + tex.height)
                  .Append(" 均值=").Append(TexMean(tex));
            }
            catch (Exception e)
            {
                sb.Append("\n    失败：").Append(e.GetType().Name);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 贴图的平均颜色（取样，不是逐像素）。
        ///
        /// <para>用途：区「真的贴图」与「同名占位图」—— 占位图看上去也是这张贴图的名字，
        /// 但它整张是均匀灰的。不能读的贴图（未开 Read/Write）会报错，所以包一层 try。</para>
        /// </summary>
        private static string TexMean(Texture2D tex)
        {
            if (tex == null)
                return "-";
            try
            {
                var w = tex.width;
                var h = tex.height;
                var px = tex.GetPixels32();
                if (px == null || px.Length == 0)
                    return "（空）";
                var step = Mathf.Max(1, px.Length / 2000);
                long r = 0, g = 0, b = 0, a = 0, n = 0;
                for (var i = 0; i < px.Length; i += step)
                {
                    var c = px[i];
                    r += c.r; g += c.g; b += c.b; a += c.a; n++;
                }
                if (n == 0)
                    return "（空）";
                return (r / n) + "/" + (g / n) + "/" + (b / n) + " a=" + (a / n)
                    + " " + w + "x" + h;
            }
            catch (Exception e)
            {
                return "（读不了：" + e.GetType().Name + "）";
            }
        }

        private static string Rect(Transform t)
        {
            if (t == null)
                return "（无）";
            var rt = t.TryCast<RectTransform>();
            if (rt == null)
                return "（非 RT）";
            return Fmt(rt.rect.width) + "x" + Fmt(rt.rect.height)
                + "@" + Fmt(rt.anchoredPosition.x) + "," + Fmt(rt.anchoredPosition.y)
                + " 缩放" + Fmt(t.localScale.x);
        }

        /// <summary>可见性链（从自己到 Canvas）。</summary>
        private static string Chain(Transform t)
        {
            if (t == null)
                return "（无）";
            var sb = new System.Text.StringBuilder(60);
            var cur = t;
            var n = 0;
            while (cur != null && n < 14)
            {
                if (!cur.gameObject.activeSelf)
                    sb.Append('/').Append(cur.name).Append("=关");
                cur = cur.parent;
                n++;
            }
            return sb.Length == 0 ? "全开" : sb.ToString();
        }

        private static string ScreenRect(Transform t)
        {
            var rt = t == null ? null : t.TryCast<RectTransform>();
            if (rt == null)
                return "（无）";
            var r = PanelDump.ScreenRect(rt);
            return "x[" + Mathf.RoundToInt(r.xMin) + "," + Mathf.RoundToInt(r.xMax)
                + "] y[" + Mathf.RoundToInt(r.yMin) + "," + Mathf.RoundToInt(r.yMax) + "]";
        }

        /// <summary>
        /// 一张卡**从自己往上到 Canvas 根**的整条链（每层的 Graphic / CanvasGroup / 尺寸）。
        ///
        /// <para>用途：卡片自身各层都查不出差异时，差异只可能在**祖先链**上
        /// （遮盖、透明度、材质、绘制顺序都由祖先决定）。</para>
        /// </summary>
        public static string ChainDump(Blueprint bp)
        {
            if (bp == null)
                return "（无卡）";
            var sb = new System.Text.StringBuilder(900);
            var t = bp.transform;
            var depth = 0;
            while (t != null && depth < 16)
            {
                sb.Append(new string(' ', depth)).Append(depth).Append(':').Append(t.name)
                  .Append(t.gameObject.activeSelf ? "(on" : "(off");
                if (!t.gameObject.activeInHierarchy)
                    sb.Append("/链关");
                sb.Append(')');
                try
                {
                    var cg = t.GetComponent<CanvasGroup>();
                    if (cg != null)
                        sb.Append(" CG[a=").Append(Fmt(cg.alpha)).Append(" ray=").Append(cg.blocksRaycasts ? 1 : 0).Append(']');
                    var gr = t.GetComponent<Graphic>();
                    if (gr != null)
                    {
                        var c = gr.color;
                        sb.Append(" <").Append(gr.GetType().Name)
                          .Append(" en=").Append(gr.enabled ? 1 : 0)
                          .Append(" a=").Append(Fmt(c.a))
                          .Append(" rgb=").Append(Fmt(c.r)).Append('/').Append(Fmt(c.g)).Append('/').Append(Fmt(c.b))
                          .Append(" mat=").Append(gr.material == null ? "无" : gr.material.name);
                        var img = gr.TryCast<Image>();
                        if (img != null)
                            sb.Append(" spr=").Append(img.sprite == null ? "无"
                                : img.sprite.name + '#' + img.sprite.GetInstanceID());
                        sb.Append('>');
                    }
                    var rt = t.TryCast<RectTransform>();
                    if (rt != null)
                        sb.Append(" 屏").Append(ScreenRect(t)).Append(" 缩放").Append(Fmt(t.localScale.x));
                    sb.Append(" 兄弟=").Append(t.GetSiblingIndex()).Append('/')
                      .Append(t.parent == null ? 0 : t.parent.childCount);
                }
                catch (Exception e)
                {
                    sb.Append(" （读不了：").Append(e.GetType().Name).Append('）');
                }
                sb.Append('\n');
                t = t.parent;
                depth++;
            }
            return sb.ToString();
        }

        private static string Fmt(float v)
        {
            return v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string V(Vector2 v)
        {
            return "(" + Fmt(v.x) + "," + Fmt(v.y) + ")";
        }

        /// <summary>只取路径末尾一小段，够看出「在谁下面」即可。</summary>
        private static string Tail(Transform t)
        {
            if (t == null)
                return "（无）";
            try
            {
                var p = UiFinder.PathOf(t);
                if (string.IsNullOrEmpty(p))
                    return t.name;
                return p.Length <= 24 ? p : "…" + p.Substring(p.Length - 24);
            }
            catch (Exception)
            {
                return "（?）";
            }
        }

        /// <summary>节点上挂了哪些组件（只有名字，用来找「谁在排版」）。</summary>
        private static string ComponentNames(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return "（无效）";
            try
            {
                var list = go.GetComponents<Component>();
                if (list == null)
                    return "（null）";
                var sb = new System.Text.StringBuilder(80);
                for (var i = 0; i < list.Length; i++)
                {
                    var c = list[i];
                    if (c == null)
                        continue;
                    if (sb.Length > 0)
                        sb.Append(',');
                    sb.Append(c.GetType().Name);
                }
                return sb.Length == 0 ? "（无）" : sb.ToString();
            }
            catch (Exception)
            {
                return "（?）";
            }
        }

        private int Count2()
        {
            try { return _set == null ? -1 : _set.GetBlueprintCount(); }
            catch (Exception) { return -2; }
        }

        /// <summary>框体上那几层图的 sprite / 颜色（「灰底到底画出来没有」只能这样看）。</summary>
        private string ImageInfo()
        {
            if (!UiFinder.IsAlive(_root))
                return "（无）";
            try
            {
                var imgs = _root.GetComponentsInChildren<Image>(true);
                if (imgs == null || imgs.Length == 0)
                    return "（无图像）";
                var sb = new System.Text.StringBuilder(90);
                for (var i = 0; i < imgs.Length && i < 6; i++)
                {
                    if (imgs[i] == null)
                        continue;
                    if (sb.Length > 0)
                        sb.Append(" / ");
                    var sp = imgs[i].sprite;
                    var c = imgs[i].color;
                    sb.Append(imgs[i].gameObject.name).Append(':')
                      .Append(sp == null ? "无sprite" : sp.name)
                      .Append(" a=").Append(Fmt(c.a))
                      .Append(" rgb=").Append(Fmt(c.r)).Append('/').Append(Fmt(c.g)).Append('/').Append(Fmt(c.b))
                      .Append(imgs[i].raycastTarget ? " 射线=on" : " 射线=off");
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                return "（" + e.GetType().Name + "）";
            }
        }

        private int ValidCount()
        {
            var n = 0;
            for (var i = 0; i < Ids.Count; i++)
            {
                if (NamespaceID.IsValid(Ids[i]))
                    n++;
            }
            return n;
        }
    }
}
