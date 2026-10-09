using System;
using System.Collections.Generic;
using DSHCore.Ui;
using Il2CppInterop.Runtime;
using MVZ2.Blueprints;
using MVZ2.Level.Artifacts;
using MVZ2.Level.Blueprints;
using MVZ2.UI;
using MVZ2Logic.Level;
using MVZ2Logic.Models;
using PVZEngine;
using PVZEngine.SeedPacks;
using PVZEngine.Tools.Unity;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 网格化的一行容器（**横向**排列的一行 `<see cref="UiContainer"/>`）。
    /// 三个网格组件共用这一层，免得各写一份行/尺寸处理。
    /// </summary>
    internal static class UiGridLayout
    {
        /// <summary>造一行（横向容器），并把它的高度钉死。</summary>
        public static GameObject NewRow(Transform parent, string name, float height)
        {
            var row = UiContainer.Instantiate(parent, name, false);
            if (!UiFinder.IsAlive(row))
                return null;
            UiContainer.FlushHorizontal(row);
            UiContainer.FitCell(row, 0f, height);
            return row;
        }

        /// <summary>把 <paramref name="node"/> 作为一格放进行里（宽高固定，间隙由行的布局组给）。</summary>
        public static void AddCell(GameObject row, GameObject node, float width, float height)
        {
            if (row == null || !UiFinder.IsAlive(node))
                return;
            try
            {
                node.transform.SetParent(row.transform, false);
                UiContainer.FitCell(node, width, height);
            }
            catch (Exception) { }
        }
    }

    /// <summary>网格里的一格（记着它绑的数据 + 命中的矩形节点）。</summary>
    internal sealed class UiGridCell
    {
        public GameObject Node;
        public int Index;       // 槽位（制品）或数据下标
    }

    /// <summary>
    /// 每帧做「矩形命中」的共用逻辑。
    ///
    /// <para>为什么一律自己命中、不用原版控件的事件：原版控件（<c>Button</c> / <c>Toggle</c> /
    /// <c>LevelPointerInteractionHandler</c>）自己会处理点击，和我们的处理叠在一起就会
    /// **一次点击生效两遍**（本仓库在进度页与补全框都吃过这个）。所以克隆体一律
    /// 关掉 <c>blocksRaycasts</c>，点击只走这里。</para>
    /// </summary>
    internal static class UiHitTest
    {
        /// <summary>挡掉节点自己的鼠标接收（只挡射线，不改颜色）。</summary>
        public static void Mute(GameObject node)
        {
            if (!UiFinder.IsAlive(node))
                return;
            try
            {
                var group = node.GetComponent<CanvasGroup>();
                if (group == null)
                    group = node.AddComponent<CanvasGroup>();
                if (group == null)
                    return;
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = false;
            }
            catch (Exception) { }
        }

        public static bool Contains(GameObject node, Vector2 screenPoint)
        {
            if (!UiFinder.IsAlive(node))
                return false;
            var rt = node.transform.TryCast<RectTransform>();
            if (rt == null)
                return false;
            try
            {
                if (!rt.gameObject.activeInHierarchy)
                    return false;
                return RectTransformUtility.RectangleContainsScreenPoint(rt, screenPoint, CameraFor(node));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 算屏幕坐标 / 做命中测试该用哪台相机 —— **按节点自己所在的 Canvas 决定**。
        ///
        /// <para><b>不能一律用场景的 UI 相机</b>：本 mod 的节点分布在好几个画布上
        /// （面板在自己的对话框画布，选卡界面那套在关卡 UI 的画布上）。对不属于同一画布的节点，
        /// 用错相机算出来的屏幕坐标会整个跑到屏外 —— 实测「选卡切换按钮」就是这样：
        /// 算出来是 (1324,911)，而屏幕只有 995x741，矩形命中永远失败（按钮看得见、点不了）。</para>
        ///
        /// <para>规则就是 Unity 自己那一套：Overlay 画布用 <c>null</c>，其余用该画布的 <c>worldCamera</c>。</para>
        /// </summary>
        public static Camera CameraFor(GameObject node)
        {
            try
            {
                if (!UiFinder.IsAlive(node))
                    return LoadoutPanel.CanvasCamera();
                var canvas = node.GetComponentInParent<Canvas>();
                if (canvas == null)
                    return LoadoutPanel.CanvasCamera();
                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                    return null;
                return canvas.worldCamera != null ? canvas.worldCamera : LoadoutPanel.CanvasCamera();
            }
            catch (Exception)
            {
                return LoadoutPanel.CanvasCamera();
            }
        }

        /// <summary>自测用：节点所在画布的画布信息（看「是不是用错相机了」）。</summary>
        public static string CanvasOf(GameObject node)
        {
            try
            {
                if (!UiFinder.IsAlive(node))
                    return "（无节点）";
                var canvas = node.GetComponentInParent<Canvas>();
                if (canvas == null)
                    return "（不在任何画布下）";
                var cam = canvas.worldCamera;
                return "mode=" + canvas.renderMode + " 相机=" + (cam == null ? "null" : cam.name)
                    + " 场景相机=" + (LoadoutPanel.CanvasCamera() == null ? "null" : LoadoutPanel.CanvasCamera().name)
                    + " 同=" + (canvas.renderMode == RenderMode.ScreenSpaceOverlay
                        ? (LoadoutPanel.CanvasCamera() == null ? "（Overlay，应为 null）" : "否")
                        : (cam == LoadoutPanel.CanvasCamera() ? "是" : "否"));
            }
            catch (Exception e)
            {
                return "（读不出来 " + e.GetType().Name + "）";
            }
        }
    }

    /// <summary>
    /// 制品网格：**带滚轮的列表，一行至多 4 个**。
    ///
    /// <para><b>格子模板用的是 <c>ArtifactSlot</c>（选卡面板里那个空制品框），不是关卡 HUD 的
    /// <c>ArtifactItemUI</c></b>。原因见 <see cref="FindTemplate"/>：后者带 Animator，
    /// 图标尺寸由动画写死，我们既改不动也关不掉（关了图标就隐形）。}</para>
    ///
    /// <para>选中高亮是自绘的半透明色块（原版制品不提供选中态，而诅咒条目的选中美术
    /// 是私有字段，拿不到）。</para>
    /// </summary>
    internal sealed class UiArtifactGrid
    {
        public const int Columns = 4;

        private readonly GameObject _root;
        private readonly GameObject _template;
        private readonly UiList _list;
        private readonly List<UiGridCell> _cells = new List<UiGridCell>();
        private readonly List<GameObject> _rows = new List<GameObject>();
        private readonly List<GameObject> _frames = new List<GameObject>();

        private int _selectedSlot = -1;
        private Action _onChanged;
        private bool _built;

        private UiArtifactGrid(GameObject root, GameObject template, UiList list)
        {
            _root = root;
            _template = template;
            _list = list;
        }

        private static readonly List<UiArtifactGrid> Live = new List<UiArtifactGrid>();

        public static void TickAll()
        {
            for (var i = 0; i < Live.Count; i++)
            {
                try { Live[i].Tick(); }
                catch (Exception) { }
            }
        }

        public static void ForgetAll()
        {
            Live.Clear();
        }

        public GameObject Root { get { return UiFinder.IsAlive(_root) ? _root : null; } }

        public int SelectedSlot { get { return _selectedSlot; } }

        public static UiArtifactGrid Create(Transform parent, string name, float viewportHeight)
        {
            var template = FindTemplate();
            if (!UiFinder.IsAlive(template))
            {
                Plugin.LogOnce("artgrid-tpl", "[ArtGrid] 拿不到关卡 HUD 的制品条目模板，制品列表不可用。");
                return null;
            }
            var list = UiList.Create(parent, name);
            if (list == null)
                return null;
            list.SetViewportHeight(viewportHeight);
            var grid = new UiArtifactGrid(list.Root, template, list);
            Live.Add(grid);
            return grid;
        }

        public static GameObject FindTemplate()
        {
            try
            {
                var controller = LevelFacade.Controller;
                if (controller == null)
                    return null;
                var preset = controller.GetUIPreset();
                if (preset == null)
                    return null;

                // ⚠️ 不要用 LevelUIPreset.GetArtifactAt / LevelUIBlueprintChoose.GetArtifactSlotAt：
                // 它们内部是 il2cpp 泛型方法 getElement<T>，interop 上要过 MethodInfoStoreGeneric，
                // 实测拿不到东西。直接扫子节点上的组件，稳。
                //
                // ★ 用 **ArtifactSlot**（选卡面板里那个空制品框），不用 ArtifactItemUI：
                //   后者带 Animator，**图标的尺寸是动画给的**（模板里 Icon 的 sizeDelta 是 0×0），
                //   关掉动画图标就没了、不关动画图标就溢出格子 —— 两难。
                //   ArtifactSlot 没有动画，图标是固定尺寸的 Image，正是「一行 4 个」要的东西。
                var chooseUI = preset.BlueprintChoose;
                if (chooseUI != null)
                {
                    var chooseSlots = chooseUI.GetComponentsInChildren<ArtifactSlot>(true);
                    if (chooseSlots != null && chooseSlots.Length > 0)
                        return chooseSlots[0].gameObject;
                }
                var slots = preset.GetComponentsInChildren<ArtifactSlot>(true);
                if (slots != null && slots.Length > 0)
                    return slots[0].gameObject;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("artgrid-find", "[ArtGrid] 找制品模板失败：" + e.GetType().Name + ": " + e.Message);
            }
            return null;
        }

        public UiArtifactGrid OnChanged(Action handler)
        {
            _onChanged = handler;
            return this;
        }

        /// <summary>按槽位重建（一行 4 个）。</summary>
        public void SetSlots(int count)
        {
            Clear();
            var level = LevelFacade.Current;
            if (level == null)
                return;
            ClearContent();
            for (var slot = 0; slot < count; slot += Columns)
            {
                var row = UiGridLayout.NewRow(_list.Content, "Row" + (slot / Columns), Cell);
                if (row == null)
                    continue;
                // 行距要小、排布要归一：原版那个横向行容器是给设置面板的滑条用的
                // （间距 20、不控宽、可能带尺寸适配器），在这里会让格子全被算成 0 宽。
                Normalize(row);
                _rows.Add(row);
                for (var c = 0; c < Columns && slot + c < count; c++)
                {
                    var index = slot + c;
                    var item = UiClone.Instantiate(_template, row.transform, "Art" + index, true);
                    if (!UiFinder.IsAlive(item))
                        continue;
                    UiHitTest.Mute(item);
                    // 固定尺寸（不能被 flexibleWidth 摊开 —— 那样同一行里每个格子宽窄不一）。
                    FixedSize(item, Cell, Cell);

                    // 选中高亮：item 后面垫一层半透明色块（不吃射线，不影响点击）。
                    var frame = UiHost.CreateNode("Sel" + index, null);
                    if (UiFinder.IsAlive(frame))
                    {
                        frame.transform.SetParent(row.transform, false);
                        var img = frame.AddComponent<Image>();
                        if (img != null)
                        {
                            img.color = new Color(1f, 0.85f, 0.2f, 0.28f);
                            img.raycastTarget = false;
                        }
                        FixedSize(frame, Cell, Cell);
                        frame.transform.SetSiblingIndex(item.transform.GetSiblingIndex());
                        frame.SetActive(false);
                        _frames.Add(frame);
                    }
                    _cells.Add(new UiGridCell { Node = item, Index = index });
                }
            }
            _built = true;
            Refresh();
            LoadoutPanel.RebuildLayout();
        }

        /// <summary>单格边长（固定值 —— 模板是关着的、量不出尺寸，而「一行 4 个」要的就是固定格）。</summary>
        private const float Cell = 64f;

        /// <summary>把一个节点钉成固定尺寸（<c>flexibleWidth = 0</c>，否则会被布局组摊开）。</summary>
        private static void FixedSize(GameObject go, float w, float h)
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
                le.ignoreLayout = false;
                le.minWidth = -1f;
                le.preferredWidth = w;
                le.flexibleWidth = 0f;
                le.minHeight = -1f;
                le.preferredHeight = h;
                le.flexibleHeight = 0f;
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 把一行的排布**归一化**。
        ///
        /// <para>行容器是从原版设置面板那个「滑条行」克隆来的，它带着那边的排版习惯
        /// （间距 20、子节点不要控宽、可能还有 <c>ContentSizeFitter</c>）。那套在纵向列表里
        /// 会让格子被算成 0 宽 / 溢出视口 —— 实测症状就是「列表区一片空白，一个图标都看不到」。
        /// 所以这里显式重设：间距 4、控宽控高、不强制铺满、内距清零、缩放归 1、关掉尺寸适配器。</para>
        /// </summary>
        private static void Normalize(GameObject row)
        {
            if (!UiFinder.IsAlive(row))
                return;
            try { row.transform.localScale = Vector3.one; }
            catch (Exception) { }
            try { UiContainer.DisableSizeFitters(row); }
            catch (Exception) { }
            try
            {
                var lg = row.GetComponent<HorizontalLayoutGroup>();
                if (lg != null)
                {
                    lg.spacing = 4f;
                    lg.childAlignment = TextAnchor.MiddleLeft;
                    lg.childControlWidth = true;
                    lg.childControlHeight = true;
                    lg.childForceExpandWidth = false;
                    lg.childForceExpandHeight = false;
                    // ⚠ interop 的 RectOffset 没有四参构造器 —— 只能原地改字段。
                    var pad = lg.padding;
                    if (pad != null)
                    {
                        pad.left = 0;
                        pad.right = 0;
                        pad.top = 0;
                        pad.bottom = 0;
                        lg.padding = pad;
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 给格子写一个小号计数文字（左下角）。
        ///
        /// <para><c>ArtifactSlot</c> 本身**没有**计数文本（原版选卡面板里那是空白框，不显示数量），
        /// 所以自己贴一个：只在 <c>count != 1</c> 时显示，免得整列都是「1」。</para>
        /// </summary>
        private static void SetNumber(GameObject item, int count)
        {
            if (!UiFinder.IsAlive(item))
                return;
            try
            {
                var t = item.transform.Find("Num");
                if (count <= 1)
                {
                    if (t != null)
                        t.gameObject.SetActive(false);
                    return;
                }
                if (t == null)
                {
                    var tmp = UiKit.Label(item.transform, "Num", "0");
                    if (tmp == null)
                        return;
                    var go = tmp.gameObject;
                    UiHitTest.Mute(go);
                    try { tmp.fontSize = Mathf.Max(8f, UiKit.ControlFontSize * 0.75f); }
                    catch (Exception) { }
                    try { tmp.alignment = TextAlignmentOptions.BottomRight; }
                    catch (Exception) { }
                    // 计数是贴在格子上的，不能参与行/列布局（否则会把格子撑开）。
                    try
                    {
                        var le0 = go.GetComponent<LayoutElement>();
                        if (le0 != null)
                            le0.ignoreLayout = true;
                    }
                    catch (Exception) { }
                    var rt = go.transform.TryCast<RectTransform>();
                    if (rt != null)
                    {
                        rt.anchorMin = new Vector2(1f, 0f);
                        rt.anchorMax = new Vector2(1f, 0f);
                        rt.pivot = new Vector2(1f, 0f);
                        rt.sizeDelta = new Vector2(Cell, Cell * 0.5f);
                        rt.anchoredPosition = new Vector2(-2f, 0f);
                    }
                    t = go.transform;
                }
                t.gameObject.SetActive(true);
                var label = t.GetComponent<TextMeshProUGUI>();
                if (label != null)
                    label.text = count.ToString();
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 把「失效」的制品染灰。
        ///
        /// <para><c>ArtifactSlot</c> 只负责「有没有图」，不管灰化（原版选卡面板里它永远是空的），
        /// 所以自己把格子里**所有** Image 上色：正常白、失效灰。<c>UpdateView</c> 刚写的图不受影响
        /// （它只动 sprite、不动 color）。</para>
        /// </summary>
        private static void TintSlot(GameObject item, bool grayscale)
        {
            if (!UiFinder.IsAlive(item))
                return;
            try
            {
                var imgs = item.GetComponentsInChildren<Image>(true);
                for (var i = 0; imgs != null && i < imgs.Length; i++)
                {
                    if (imgs[i] == null)
                        continue;
                    imgs[i].color = grayscale ? new Color(0.45f, 0.45f, 0.45f, 1f) : Color.white;
                }
            }
            catch (Exception) { }
        }

        /// <summary>自测用：格子尺寸与行数（“一行放得下几个”靠它判定）。</summary>
        public string Describe()
        {
            return "格子=" + Mathf.RoundToInt(Cell) + "x" + Mathf.RoundToInt(Cell)
                + " 模板名=" + (UiFinder.IsAlive(_template) ? _template.name : "（无）")
                + " 行数=" + _rows.Count + " 条目=" + _cells.Count
                + " 列数=" + Columns
                + "\n    行0=" + RectOf(_rows.Count > 0 ? _rows[0] : null)
                + "  行参数=" + GroupOf(_rows.Count > 0 ? _rows[0] : null)
                + "\n    内容=" + RectOf(_list != null && _list.Content != null ? _list.Content.gameObject : null)
                + "  内容子节点=" + (_list != null && _list.Content != null ? _list.Content.childCount : -1)
                + "\n    格0=" + RectOf(_cells.Count > 0 ? _cells[0].Node : null)
                + "  格0可见=" + (IsVisible(_cells.Count > 0 ? _cells[0].Node : null) ? "是" : "否")
                // 看**最后一个**格子：空槽位没有图标，只有非空的才看得出问题。
                + "\n    末格树：\n" + NodeTree(_cells.Count > 0 ? _cells[_cells.Count - 1].Node : null, 0, 3);
        }

        /// <summary>自测用：一棵节点的名字 / 开关 / 尺寸 / 图（看「图标为什么会溢出格子」）。</summary>
        private static string NodeTree(GameObject go, int depth, int maxDepth)
        {
            if (!UiFinder.IsAlive(go) || depth > maxDepth)
                return string.Empty;
            var sb = new System.Text.StringBuilder(600);
            AppendNode(sb, go, depth, maxDepth);
            return sb.ToString();
        }

        private static void AppendNode(System.Text.StringBuilder sb, GameObject go, int depth, int maxDepth)
        {
            try
            {
                var name = go.name;
                for (var i = 0; i < depth; i++)
                    sb.Append("    ");
                sb.Append(go.activeSelf ? "" : "(隐)").Append(name).Append(' ').Append(RectOf(go));
                var img = go.GetComponent<Image>();
                if (img != null)
                    sb.Append(" 图=").Append(img.sprite == null ? "无" : img.sprite.name)
                      .Append(" 开=").Append(img.enabled ? 1 : 0)
                      .Append(" a=").Append(img.color.a.ToString("0.##"))
                      .Append(" 保留比例=").Append(img.preserveAspect ? 1 : 0);
                sb.Append('\n');
                for (var i = 0; i < go.transform.childCount; i++)
                {
                    var c = go.transform.GetChild(i);
                    if (c != null)
                        AppendNode(sb, c.gameObject, depth + 1, maxDepth);
                }
            }
            catch (Exception) { }
        }

        private static bool IsVisible(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return false;
            try { return go.activeInHierarchy; }
            catch (Exception) { return false; }
        }

        private static string RectOf(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return "（无）";
            try
            {
                var rt = go.transform.TryCast<RectTransform>();
                if (rt == null)
                    return "（没有 RectTransform）";
                var cam = LoadoutPanel.CanvasCamera();
                var br = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.min));
                var tl = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.max));
                return "设计=" + Mathf.RoundToInt(rt.rect.width) + "x" + Mathf.RoundToInt(rt.rect.height)
                    + " 屏[" + Mathf.RoundToInt(br.x) + "," + Mathf.RoundToInt(br.y)
                    + " → " + Mathf.RoundToInt(tl.x) + "," + Mathf.RoundToInt(tl.y) + "]";
            }
            catch (Exception e)
            {
                return "（读不出来 " + e.GetType().Name + "）";
            }
        }

        private static string GroupOf(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return "（无）";
            try
            {
                var lg = go.GetComponent<HorizontalLayoutGroup>();
                if (lg == null)
                    return "（没有横向布局组）";
                return "间距=" + Mathf.RoundToInt(lg.spacing)
                    + " 控制cw=" + (lg.childControlWidth ? 1 : 0)
                    + " ch=" + (lg.childControlHeight ? 1 : 0)
                    + " 铺满fw=" + (lg.childForceExpandWidth ? 1 : 0)
                    + " fh=" + (lg.childForceExpandHeight ? 1 : 0);
            }
            catch (Exception) { return "（读不出来）"; }
        }

        public void Refresh()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return;
            for (var i = 0; i < _cells.Count; i++)
            {
                var cell = _cells[i];
                if (!UiFinder.IsAlive(cell.Node))
                    continue;
                var artifact = ArtifactOps.GetAt(level, cell.Index);
                var id = ArtifactOps.IdOf(artifact);
                var sprite = artifact == null ? null : ArtifactOps.IconOf(id);
                // ★ 用 ArtifactSlot（选卡面板那个空制品框）：没有 Animator，图标就是一张固定尺寸的图，
                //   给它什么 sprite 就画什么，不会像 ArtifactItemUI 那样被动画改尺寸。
                var slot = cell.Node.GetComponent<ArtifactSlot>();
                if (slot != null)
                {
                    try { slot.UpdateView(new ArtifactViewData { sprite = sprite }); }
                    catch (Exception) { }
                }
                SetNumber(cell.Node, ArtifactOps.GetNumber(artifact));
                TintSlot(cell.Node, ArtifactOps.IsInactive(artifact));
                if (i < _frames.Count && UiFinder.IsAlive(_frames[i]))
                {
                    try { _frames[i].SetActive(cell.Index == _selectedSlot); }
                    catch (Exception) { }
                }
            }
        }

        public void Tick()
        {
            if (!_built || _list == null || !_list.IsValid)
                return;
            try
            {
                if (!UiFinder.IsAlive(_root) || !_root.activeInHierarchy)
                    return;
                if (Input.GetMouseButtonDown(0))
                {
                    var pointer = (Vector2)Input.mousePosition;
                    for (var i = 0; i < _cells.Count; i++)
                    {
                        var cell = _cells[i];
                        if (!UiHitTest.Contains(cell.Node, pointer))
                            continue;
                        _selectedSlot = _selectedSlot == cell.Index ? -1 : cell.Index;
                        Refresh();
                        _onChanged?.Invoke();
                        return;
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 清掉内容容器里**原本就有**的子节点（从原版可滚动容器克隆来的，里面留着它的示例条目），
        /// 否则那几条会占在列表顶部（实测：内容高度 178 里有 ~37 是它们）。
        /// </summary>
        private void ClearContent()
        {
            try
            {
                var content = _list == null ? null : _list.Content;
                if (content == null)
                    return;
                for (var i = content.childCount - 1; i >= 0; i--)
                {
                    var c = content.GetChild(i);
                    if (c != null)
                        UnityEngine.Object.DestroyImmediate(c.gameObject);
                }
            }
            catch (Exception) { }
        }

        private void Clear()
        {
            for (var i = 0; i < _frames.Count; i++)
            {
                if (UiFinder.IsAlive(_frames[i]))
                    UnityEngine.Object.Destroy(_frames[i]);
            }
            for (var i = 0; i < _rows.Count; i++)
            {
                if (UiFinder.IsAlive(_rows[i]))
                    UnityEngine.Object.Destroy(_rows[i]);
            }
            _frames.Clear();
            _rows.Clear();
            _cells.Clear();
            _selectedSlot = -1;
        }
    }
}
