using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MVZ2.Level;
using MVZ2.Level.Curses;
using MVZ2.Managers;
using MVZ2Logic.Audios;
using PVZEngine.Tools.Unity;
using UnityEngine;
using UnityEngine.UI;

namespace ModularCurse
{
    /// <summary>
    /// 诅咒弹窗里「诅咒池」那一行 —— **放在滚动区内部**，与原版诅咒列表用一条横线分隔。
    ///
    /// 实测到的活体结构（见 <see cref="CurseDialogProbe"/>）：
    ///
    ///   SelectCurseDialog                        356x294，平时 SetActive(false)
    ///     Dialog
    ///       Background
    ///       Content      [VerticalLayoutGroup spacing=0 align=MiddleCenter ctrlH=1 forceH=0]
    ///         Top   (50) [LayoutElement pref=-1x50 flex=1x-1]         ← 标题行
    ///         Center(194)[LayoutElement pref=296x-1 flex=-1x1]        ← 滚动区（flex=1）
    ///           Scroll View [ScrollRect]
    ///             Viewport [Mask]
    ///               Content [ElementList | Grid 4列 64x64 间距1 | SizeFitter v=PreferredSize]
    ///                 CurseSelectItem（模板）… 诅咒条目
    ///             Scrollbar Vertical
    ///         Bottom(50) [LayoutElement pref=-1x50 flex=1x-1]         ← 返回/全选/取消全选
    ///
    /// 为什么这样挂：
    ///   - 「移入滚动区」= 成为 **滚动区 Content（也就是 ElementList 的 listRoot）的子节点**，
    ///     于是它跟着一起滚动、一起被 Viewport 裁剪；
    ///   - Content 上挂着 <c>GridLayoutGroup</c>，会把所有子节点塞进 64×64 的格子里。
    ///     要放"跨整行的池条目 + 横线"就必须让布局组**跳过**它们：
    ///     给它们各挂一个 <c>LayoutElement.ignoreLayout = true</c>
    ///     （游戏自己的 DebugConsoleInputField 也是这么干的）；
    ///   - 跳过后位置由我们自己给，于是再给 Grid 设 <c>padding.top</c> = 池区高度，
    ///     把真正的诅咒格子整体压下去 —— 既不会重叠，ContentSizeFitter 也会把
    ///     Content 撑高（滚动范围自动正确）。
    ///
    /// 条目外观：直接复制诅咒列表自己的模板（<c>ElementList.getTemplate()</c>，
    /// 即 <c>CurseSelectItem</c>）→ 方框（外框 + 内框两层）、选中高亮、悬停锚点
    /// 与原版诅咒条目完全一致，尺寸也用同一个 64×64；
    /// 图标再按原版诅咒按钮的渲染方式上一层紫色 tint（见 <see cref="CursePoolIcons"/>）。
    /// 条目**左对齐**，与下方诅咒列表的第一列齐平。
    ///
    /// 扩展性：条目完全由 <see cref="CursePoolRules.Pools"/> 驱动，
    /// 列数/尺寸/行数按可用宽度自动算（排不下就换行并加大预留高度），
    /// 所以以后加诅咒池只需要往 <c>Pools</c> 里加一项 + 补图标与描述。
    /// </summary>
    internal static class CursePoolRow
    {
        private const string RowName = "CursePoolRow_ModularCurse";
        private const string DividerName = "CursePoolDivider_ModularCurse";

        /// <summary>条目首选/最小边长、间距、行距。</summary>
        /// <remarks>
        /// 首选尺寸取 64 = 原版诅咒条目的尺寸：诅咒条目是「外框 64（Background, form）
        /// + 内框 48（Slot, form_hollow）」两层，尺寸小于 64 时内外两层会叠在一起、
        /// 看起来就少了一圈边框。跟下方诅咒按钮同尺寸才能套上同一种边框。
        /// </remarks>
        private const float MaxItemSize = 64f;
        private const float MinItemSize = 40f;
        private const float ItemSpacing = 1f;
        private const float RowGap = 8f;
        /// <summary>横线高度与它上下留白。</summary>
        private const float DividerHeight = 2f;
        private const float DividertAlpha = 0.8f;
        private const float AboveDivider = 8f;
        private const float BelowDivider = 8f;
        /// <summary>拿不到 Grid 参数时的兜底内容宽度（4×64 + 3×1）。</summary>
        private const float FallbackContentWidth = 259f;
        /// <summary>预留区高度上限（超过就只记日志，不再硬撑，免得把滚区挤没）。</summary>
        private const float MaxReservedHeight = 130f;

        private static GameObject rowObject;
        private static GameObject dividerObject;
        private static readonly List<CurseSelectItem> items = new List<CurseSelectItem>();
        private static LevelBlueprintChooseController controller;

        /// <summary>记住 Grid 原始 padding.top，避免重复叠加。</summary>
        private static IntPtr paddedGrid = IntPtr.Zero;
        private static int originalPadTop;
        private static bool tintLogged;

        // 托管委托必须自己持有，否则会被 GC 回收（IL2CPP 侧只存裸指针）。
        private static System.Action<CurseSelectItem> clickManaged;
        private static Il2CppSystem.Action<CurseSelectItem> clickIl2Cpp;
        private static System.Action<CurseSelectItem> enterManaged;
        private static Il2CppSystem.Action<CurseSelectItem> enterIl2Cpp;
        private static System.Action<CurseSelectItem> exitManaged;
        private static Il2CppSystem.Action<CurseSelectItem> exitIl2Cpp;

        /// <summary>诅咒弹窗每次打开 / 列表重建时调用：必要时造出池条目行（幂等）。</summary>
        public static void Ensure(LevelBlueprintChooseController ctl)
        {
            if (ctl == null)
                return;
            controller = ctl;
            CursePoolIcons.Prepare(ctl);
            try
            {
                var dialog = FindDialog(ctl);
                if (dialog == null)
                {
                    Plugin.Log?.LogError("[CursePool] 找不到活的诅咒弹窗，无法加入池条目");
                    return;
                }
                var list = FindCurseList(dialog.transform);
                if (list == null)
                {
                    Plugin.Log?.LogError("[CursePool] 诅咒弹窗里找不到诅咒列表（ElementList）");
                    return;
                }
                // ElementList / GridLayoutGroup / ContentSizeFitter 挂在同一个对象上，
                // 它就是滚动区的 Content（也是 listRoot）。
                var gridRoot = list.transform;
                if (IsAlive(gridRoot))
                    return;

                var template = list.getTemplate();
                if (template == null || template.GetComponent<CurseSelectItem>() == null)
                {
                    Plugin.Log?.LogError("[CursePool] 诅咒列表模板不是 CurseSelectItem");
                    return;
                }
                var grid = gridRoot.GetComponent<GridLayoutGroup>();
                if (grid == null)
                {
                    Plugin.Log?.LogError("[CursePool] 滚动区 Content 上没有 GridLayoutGroup");
                    return;
                }
                var dialogContent = FindLayoutParent(gridRoot);
                var top = dialogContent == null ? null : dialogContent.Find("Top");
                if (top == null)
                {
                    Plugin.Log?.LogError("[CursePool] 找不到诅咒弹窗的行容器 / Top");
                    return;
                }

                rowObject = null;
                dividerObject = null;
                items.Clear();

                var pools = CursePoolRules.Pools;
                var width = ContentWidth(grid);
                var layout = Layout(pools.Length, width);

                // 1) 行容器：复制标题行（自带 RectTransform + LayoutElement），清空内容。
                rowObject = Clone(top.gameObject, gridRoot, RowName);
                if (rowObject == null)
                {
                    Plugin.Log?.LogError("[CursePool] 复制池条目行失败");
                    return;
                }
                ClearChildren(rowObject);
                SetIgnoreLayout(rowObject);
                Place(rowObject, 0f, layout.RowHeight);
                rowObject.SetActive(true);

                // 2) 池条目：复制诅咒列表模板，按算好的格子摆开（左对齐）。
                EnsureDelegates();
                for (var i = 0; i < pools.Length; i++)
                {
                    var itemGo = Clone(template, rowObject.transform, "Pool_" + CursePoolRules.Label(pools[i]));
                    if (itemGo == null)
                        continue;
                    var col = i % layout.Columns;
                    var row = i / layout.Columns;
                    var rt = itemGo.transform.TryCast<RectTransform>();
                    if (rt != null)
                    {
                        rt.anchorMin = new Vector2(0f, 1f);
                        rt.anchorMax = new Vector2(0f, 1f);
                        rt.pivot = new Vector2(0f, 1f);
                        rt.sizeDelta = new Vector2(layout.ItemSize, layout.ItemSize);
                        // 左对齐：光标点从滚动区左边开始，与下方诅咒列表的第一列齐平。
                        rt.anchoredPosition = new Vector2(
                            col * (layout.ItemSize + ItemSpacing),
                            -(row * (layout.ItemSize + RowGap)));
                    }
                    itemGo.SetActive(true);
                    var item = itemGo.GetComponent<CurseSelectItem>();
                    if (item == null)
                        continue;
                    item.add_OnClick(clickIl2Cpp);
                    item.add_OnPointerEnter(enterIl2Cpp);
                    item.add_OnPointerExit(exitIl2Cpp);
                    items.Add(item);
                }

                // 3) 横线（拿不到就退化成一条空白间隔）。
                var hasDivider = CreateDivider(gridRoot,
                    layout.RowHeight + AboveDivider, width);

                // 4) 把诅咒格子整体压到池区下面。
                var reserved = layout.RowHeight + (hasDivider
                    ? AboveDivider + DividerHeight + BelowDivider
                    : BelowDivider);
                if (reserved > MaxReservedHeight)
                {
                    Plugin.Log?.LogWarning("[CursePool] 池区预留高度 " + (int)reserved
                        + " 偏高（池数量 " + pools.Length + "），滚动区会变窄");
                }
                SetGridPaddingTop(grid, reserved);

                Plugin.Log?.LogInfo("[CursePool] 已在滚动区内创建 " + items.Count + " 个池条目"
                    + "（" + layout.Columns + "列×" + layout.Rows + "行，条目 "
                    + (int)layout.ItemSize + "，行高 " + (int)layout.RowHeight
                    + "，左对齐，可用宽 " + (int)width + "，横线 " + hasDivider
                    + "，padding.top " + (int)reserved
                    + "，弹窗 active=" + (dialog.gameObject.activeSelf ? 1 : 0) + "）");

                Refresh();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CursePool] 创建池条目行失败: " + e);
            }
        }

        /// <summary>同步图标与选中态（弹窗每次打开、每次换池都调）。</summary>
        public static void Refresh()
        {
            try
            {
                if (items.Count == 0)
                    return;
                var pools = CursePoolRules.Pools;
                var current = CursePoolState.Current;
                var forceAll = CursePoolState.ForceAll;
                var tint = CursePoolIcons.Tint;
                var tinted = false;
                for (var i = 0; i < items.Count && i < pools.Length; i++)
                {
                    var item = items[i];
                    if (item == null)
                        continue;
                    item.UpdateItem(new CurseSelectItemViewData
                    {
                        empty = false,
                        icon = CursePoolIcons.Get(pools[i]),
                        // /curse all 时不过滤，因此"没有哪个池是当前的"。
                        selected = !forceAll && pools[i] == current,
                        disabled = false,
                    });
                    if (TintIcon(item, tint))
                        tinted = true;
                }
                if (!tintLogged)
                {
                    tintLogged = true;
                    if (tinted)
                    {
                        Plugin.Log?.LogInfo("[CursePool] 池图标已按原版按钮的方式叠加紫色滤镜");
                    }
                    else
                    {
                        Plugin.Log?.LogWarning("[CursePool] 没能给池图标叠加滤镜（找不到 Root/Slot/Icon）");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CursePool] 刷新池条目失败: " + e.GetType().Name);
            }
        }

        #region 布局与摆放
        private struct LayoutInfo
        {
            public int Columns;
            public int Rows;
            public float ItemSize;
            public float RowHeight;
            public float TotalWidth;
        }

        /// <summary>按可用宽度与池数量算列数/行数/条目尺寸。</summary>
        private static LayoutInfo Layout(int count, float width)
        {
            var result = new LayoutInfo();
            if (count <= 0)
                count = 1;
            // 先试一行放下。
            var single = (width - (count - 1) * ItemSpacing) / count;
            if (single >= MinItemSize)
            {
                result.Columns = count;
                result.ItemSize = Mathf.Min(MaxItemSize, single);
            }
            else
            {
                // 一行放不下：按最小尺寸算列数，剩下的换行。
                var columns = Mathf.Max(1, Mathf.FloorToInt((width + ItemSpacing) / (MinItemSize + ItemSpacing)));
                result.Columns = columns;
                result.ItemSize = MinItemSize;
            }
            result.Rows = (count + result.Columns - 1) / result.Columns;
            result.TotalWidth = result.Columns * result.ItemSize + (result.Columns - 1) * ItemSpacing;
            result.RowHeight = result.Rows * result.ItemSize + (result.Rows - 1) * RowGap;
            return result;
        }

        /// <summary>滚动区内容的设计宽度（格子数 × 格子边长 + 间距）。</summary>
        private static float ContentWidth(GridLayoutGroup grid)
        {
            try
            {
                if (grid.constraint == GridLayoutGroup.Constraint.FixedColumnCount
                    && grid.constraintCount > 0)
                {
                    return grid.cellSize.x * grid.constraintCount
                        + grid.spacing.x * (grid.constraintCount - 1);
                }
            }
            catch (Exception)
            {
            }
            return FallbackContentWidth;
        }

        /// <summary>把节点摆到滚动区顶部：宽满、高固定、按需下移。</summary>
        private static void Place(GameObject go, float topOffset, float height)
        {
            var rt = go.transform.TryCast<RectTransform>();
            if (rt == null)
                return;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, height);
            rt.anchoredPosition = new Vector2(0f, -topOffset);
        }

        /// <summary>让 GridLayoutGroup 跳过这个子节点（位置由我们自己给）。</summary>
        private static void SetIgnoreLayout(GameObject go)
        {
            try
            {
                var le = go.GetComponent<LayoutElement>();
                if (le == null)
                    le = go.AddComponent<LayoutElement>();
                le.ignoreLayout = true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CursePool] ignoreLayout 设置失败: " + e.GetType().Name);
            }
        }

        /// <summary>给网格设 top padding，把真正的诅咒格子压到池区下面。</summary>
        private static void SetGridPaddingTop(GridLayoutGroup grid, float top)
        {
            try
            {
                // 注意：interop 里的 RectOffset 没有 (l, r, t, b) 构造函数，
                // 所以直接改它返回的实例（RectOffset 是引用类型，改的就是 native 对象），
                // 再赋回去以触发布局重建。
                var pad = grid.padding;
                if (pad == null)
                    return;
                if (paddedGrid != grid.Pointer)
                {
                    paddedGrid = grid.Pointer;
                    originalPadTop = pad.top;
                }
                pad.top = originalPadTop + Mathf.RoundToInt(top);
                grid.padding = pad;
                Plugin.Log?.LogInfo("[CursePool] Grid padding.top = " + pad.top
                    + "（原始 " + originalPadTop + "）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CursePool] 设置 padding 失败: " + e.GetType().Name
                    + ": " + e.Message);
            }
        }

        /// <summary>横线：新建一个只有 Image 的节点（不带任何会重设 sprite 的组件）。</summary>
        private static bool CreateDivider(Transform parent, float topOffset, float width)
        {
            try
            {
                var go = new GameObject(DividerName);
                if (go == null)
                    return false;
                dividerObject = go;
                go.AddComponent<RectTransform>();
                var image = go.AddComponent<Image>();
                if (image != null)
                {
                    image.sprite = null;          // 无 sprite 的 Image = 纯色矩形
                    image.type = Image.Type.Simple;
                    image.raycastTarget = false;
                    var tint = CursePoolIcons.Tint;
                    // 用按钮那个紫色，但压成半透明，作为分隔线（不做成纯黑/纯白的硬线）。
                    image.color = new Color(tint.r, tint.g, tint.b, DividertAlpha);
                }
                go.AddComponent<LayoutElement>();
                go.transform.SetParent(parent, false);
                SetIgnoreLayout(go);
                Place(go, topOffset, DividerHeight);
                go.SetActive(true);
                Plugin.Log?.LogInfo("[CursePool] 已创建分隔横线（宽 " + (int)width
                    + "，高 " + (int)DividerHeight + "，距顶 " + (int)topOffset + "）");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CursePool] 创建横线失败（跳过）: " + e.GetType().Name
                    + ": " + e.Message);
                dividerObject = null;
                return false;
            }
        }
        #endregion

        #region 事件
        private static void EnsureDelegates()
        {
            if (clickIl2Cpp != null)
                return;
            clickManaged = new System.Action<CurseSelectItem>(OnItemClicked);
            clickIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<CurseSelectItem>>(clickManaged);
            enterManaged = new System.Action<CurseSelectItem>(OnItemPointerEnter);
            enterIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<CurseSelectItem>>(enterManaged);
            exitManaged = new System.Action<CurseSelectItem>(OnItemPointerExit);
            exitIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<CurseSelectItem>>(exitManaged);
        }

        private static void OnItemClicked(CurseSelectItem item)
        {
            try
            {
                var index = IndexOf(item);
                if (index < 0 || controller == null)
                    return;
                // 与原版诅咒条目点击同一个音效（tap）。
                PlayClickSound();
                CursePoolFeature.ApplyPool(controller, CursePoolRules.Pools[index]);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CursePool] 池条目点击失败: " + e);
            }
        }

        private static void OnItemPointerEnter(CurseSelectItem item)
        {
            try
            {
                var index = IndexOf(item);
                if (index < 0)
                    return;
                var pool = CursePoolRules.Pools[index];
                Camera camera = null;
                try
                {
                    var c = controller == null ? null : controller.Controller;
                    if (c != null)
                        camera = c.GetCamera();
                }
                catch (Exception)
                {
                }
                CurseTooltip.Show(item.Pointer, camera,
                    CursePoolRules.Name(pool), CursePoolRules.Description(pool));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CursePool] 池条目提示失败: " + e.GetType().Name);
            }
        }

        private static void OnItemPointerExit(CurseSelectItem item)
        {
            CurseTooltip.Hide();
        }

        private static void PlayClickSound()
        {
            try
            {
                var main = MainManager.Instance;
                if (main != null && main.SoundManager != null)
                    main.SoundManager.Play2D(LogicSoundID.tap, 1f);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CursePool] 点击音效失败: " + e.GetType().Name);
            }
        }

        private static int IndexOf(CurseSelectItem item)
        {
            if (item == null)
                return -1;
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] != null && items[i].Pointer == item.Pointer)
                    return i;
            }
            return -1;
        }
        #endregion

        #region 工具
        private static bool IsAlive(Transform expectedParent)
        {
            try
            {
                if (rowObject == null || rowObject.Pointer == IntPtr.Zero)
                    return false;
                var t = rowObject.transform;
                if (t == null)
                    return false;
                var p = t.parent;
                if (expectedParent == null)
                    return true;
                return p != null && p.Pointer == expectedParent.Pointer;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static GameObject Clone(GameObject template, Transform parent, string name)
        {
            try
            {
                var obj = UnityEngine.Object.Instantiate(
                    (UnityEngine.Object)template, parent, false);
                var go = obj == null ? null : obj.TryCast<GameObject>();
                if (go == null)
                    return null;
                go.name = name;
                return go;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CursePool] 复制节点失败: " + e.GetType().Name);
                return null;
            }
        }

        /// <summary>清空复制来的子节点（先禁用再销毁，避免有一帧闪现）。</summary>
        private static void ClearChildren(GameObject go)
        {
            for (var i = go.transform.childCount - 1; i >= 0; i--)
            {
                var child = go.transform.GetChild(i);
                if (child == null)
                    continue;
                child.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        /// <summary>把条目里的图标 Image 上成与原版诅咒按钮相同的紫色 tint。返回是否真的改了。</summary>
        private static bool TintIcon(CurseSelectItem item, Color tint)
        {
            try
            {
                var node = item.transform.Find("Root/Slot/Icon");
                if (node == null)
                    return false;
                var image = node.GetComponent<Image>();
                if (image == null)
                    return false;
                image.color = tint;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>找**活的**诅咒弹窗（同一份 UI 预设可能有多个实例，优先取父链可见的）。</summary>
        private static CurseChoosingDialog FindDialog(LevelBlueprintChooseController ctl)
        {
            var chooseUI = ctl.chooseUI;
            if (chooseUI == null)
                return null;
            var found = new List<CurseChoosingDialog>();
            CollectDialogs(chooseUI.transform, found, 0);
            for (var i = 0; i < found.Count; i++)
            {
                if (IsChainActive(found[i].transform))
                    return found[i];
            }
            return found.Count > 0 ? found[0] : null;
        }

        private static void CollectDialogs(Transform node, List<CurseChoosingDialog> found, int depth)
        {
            if (node == null || depth > 10)
                return;
            try
            {
                var d = node.GetComponent<CurseChoosingDialog>();
                if (d != null)
                    found.Add(d);
            }
            catch (Exception)
            {
            }
            for (var i = 0; i < node.childCount; i++)
                CollectDialogs(node.GetChild(i), found, depth + 1);
        }

        /// <summary>找诅咒列表：模板是 CurseSelectItem 的那个 ElementList。</summary>
        private static ElementList FindCurseList(Transform node, int depth = 0)
        {
            if (node == null || depth > 10)
                return null;
            try
            {
                var list = node.GetComponent<ElementList>();
                if (list != null)
                {
                    var tpl = list.getTemplate();
                    if (tpl != null && tpl.GetComponent<CurseSelectItem>() != null)
                        return list;
                }
            }
            catch (Exception)
            {
            }
            for (var i = 0; i < node.childCount; i++)
            {
                var result = FindCurseList(node.GetChild(i), depth + 1);
                if (result != null)
                    return result;
            }
            return null;
        }

        /// <summary>从滚动区 Content 往上找那个带 VerticalLayoutGroup 的行容器。</summary>
        private static Transform FindLayoutParent(Transform node)
        {
            var cur = node;
            var guard = 0;
            while (cur != null && guard++ < 10)
            {
                try
                {
                    if (cur.GetComponent<VerticalLayoutGroup>() != null)
                        return cur;
                }
                catch (Exception)
                {
                }
                cur = cur.parent;
            }
            return null;
        }

        private static bool IsChainActive(Transform t)
        {
            var cur = t;
            while (cur != null)
            {
                if (!cur.gameObject.activeSelf)
                    return false;
                cur = cur.parent;
            }
            return true;
        }
        #endregion
    }
}
