using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GraphicConsole
{
    /// <summary>面板里一个「格子」的类型。对应需求文档：[]复选框 &lt;&gt;下拉 __输入框 {按钮}。</summary>
    internal enum CellKind
    {
        Toggle,
        Dropdown,
        Button,
        Number,
    }

    /// <summary>
    /// 一个格子 = 「左侧自适应宽度的标签」+「右侧固定宽度的控件」。
    /// 所有引用都可能为 null（模板缺失时降级），用之前一律判空。
    /// </summary>
    internal sealed class WidgetCell
    {
        public string Id;
        public CellKind Kind;
        public GameObject Root;
        public GameObject ControlRoot;
        public UnityEngine.UI.Toggle Toggle;
        public TMP_Dropdown Dropdown;
        public UnityEngine.UI.Button Button;
        public TMP_InputField Input;
        public TMPro.TextMeshProUGUI Label;
        /// <summary>复选框行是否自带标签（此时 <see cref="Label"/> 指向它内部的文本）。</summary>
        public bool LabelInsideControl;

        // ---- 下拉格自己的状态（弹窗已改成自绘，见 <see cref="PickerOverlay"/>）----
        /// <summary>选项文本。</summary>
        public List<string> Options;
        /// <summary>当前选中的下标（-1 = 无选项）。</summary>
        public int Selected = -1;
        /// <summary>显示当前值的那行文本（原 TMP_Dropdown 的 captionText）。</summary>
        public TextMeshProUGUI Caption;
        /// <summary>选择浮层的标题（就用格子标签）。</summary>
        public string Title;
        /// <summary>下拉格的底板图（自己画悬停高亮用）。</summary>
        public Image ControlImage;
        /// <summary>鼠标当前是否停在这个下拉格上。</summary>
        public bool Hovered;

        public bool Exists { get { return UiFinder.IsAlive(Root); } }

        public void SetLabel(string text)
        {
            UiFactory.SetLabelText(Label, text);
            if (!LabelInsideControl)
                UiFactory.MeasureLabel(Label, text);
        }
    }

    /// <summary>
    /// 用**克隆模板架上的节点**的方式造 UI，并统一管理克隆出来的东西。
    ///
    /// 为什么不自己 <c>AddComponent&lt;Text&gt;</c> 造文本：TMP 文本需要字体资源，游戏用的是
    /// 一个从 prefab 序列化进来的字体资产，托管侧没有可靠的按名获取入口；克隆现成节点就把它
    /// 连字体、材质、换行设置一起继承下来了。按钮的 sprite / 点击音效 / Deselector 同理。
    ///
    /// 只有**纯布局用**的容器（RectTransform / HorizontalLayoutGroup / VerticalLayoutGroup /
    /// GridLayoutGroup / ContentSizeFitter / LayoutElement / RectMask2D）是现造的 —— 这些组件
    /// 不带资源引用，现造没有副作用。
    /// （原版那堆 Image 都挂着 ImageResolver，会在 OnEnable 把 sprite 设回去，不能用现造的 Image 冒充。）
    /// </summary>
    internal static class UiFactory
    {
        #region 委托保活
        /// <summary>
        /// 托管委托必须自己持有：IL2CPP 侧只存裸函数指针，托管委托被 GC 回收后回调就指向
        /// 已释放的内存（表现为点击无反应或随机崩）。
        ///
        /// 这里按**属主对象**登记，属主销毁后连同委托一起丢掉 —— 否则每次重建内容列表
        /// （可能几百行、每行一个委托）就会无限增长。
        /// </summary>
        private sealed class KeepAliveEntry
        {
            public GameObject Owner;
            public object[] Items;
        }

        private static readonly List<KeepAliveEntry> keepAlive = new List<KeepAliveEntry>();

        public static void KeepAlive(GameObject owner, params object[] items)
        {
            if (items == null || items.Length == 0)
                return;
            keepAlive.Add(new KeepAliveEntry { Owner = owner, Items = items });
        }

        /// <summary>丢掉落空属主的登记。列表重建时与心跳里定期调用。</summary>
        public static void PruneKeepAlive()
        {
            for (var i = keepAlive.Count - 1; i >= 0; i--)
            {
                if (!UiFinder.IsAlive(keepAlive[i].Owner))
                    keepAlive.RemoveAt(i);
            }
        }

        public static int KeepAliveCount { get { return keepAlive.Count; } }

        /// <summary>面板整体销毁时清空（避免跨场景累积）。</summary>
        public static void ClearKeepAlive()
        {
            keepAlive.Clear();
        }
        #endregion

        #region 克隆与容器
        public static GameObject Clone(GameObject template, Transform parent, string name)
        {
            if (template == null || parent == null)
                return null;
            try
            {
                var obj = UnityEngine.Object.Instantiate((UnityEngine.Object)template, parent, false);
                var go = obj == null ? null : obj.TryCast<GameObject>();
                if (go == null)
                    return null;
                go.name = name;
                go.layer = parent.gameObject.layer;
                return go;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Ui] 克隆「" + template.name + "」失败：" + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>清空子节点。先 SetActive(false) 再 Destroy，避免销毁前那一帧闪现。</summary>
        public static void ClearChildren(GameObject go)
        {
            if (go == null)
                return;
            try
            {
                var t = go.transform;
                for (var i = t.childCount - 1; i >= 0; i--)
                {
                    var child = t.GetChild(i);
                    if (child == null)
                        continue;
                    child.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Ui] 清空子节点失败：" + e.GetType().Name);
            }
        }

        /// <summary>新建一个纯布局容器（只有 RectTransform，不带任何资源引用）。</summary>
        public static GameObject NewRect(Transform parent, string name)
        {
            try
            {
                var go = new GameObject(name);
                go.layer = parent.gameObject.layer;
                go.AddComponent<RectTransform>();
                go.transform.SetParent(parent, false);
                return go;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Ui] 新建容器失败：" + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        public static RectTransform RectOf(GameObject go)
        {
            if (go == null)
                return null;
            try
            {
                return go.transform.TryCast<RectTransform>();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static RectTransform RectOf(Component c)
        {
            if (c == null)
                return null;
            return RectOf(c.gameObject);
        }

        /// <summary>铺满父节点。</summary>
        public static void Stretch(Transform t)
        {
            var rt = t == null ? null : t.TryCast<RectTransform>();
            if (rt == null)
                return;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>屏幕居中固定尺寸。</summary>
        public static void CenterFixed(Transform t, float width, float height)
        {
            var rt = t == null ? null : t.TryCast<RectTransform>();
            if (rt == null)
                return;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(width, height);
        }

        /// <summary>锚在父节点左上角、固定尺寸。</summary>
        public static void TopLeft(RectTransform rt, Vector2 size, Vector2 offset)
        {
            if (rt == null)
                return;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = size;
            rt.anchoredPosition = offset;
        }

        /// <summary>顶部横向铺满、固定高度。</summary>
        public static void TopStretch(RectTransform rt, float height)
        {
            if (rt == null)
                return;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, height);
            rt.anchoredPosition = Vector2.zero;
        }

        /// <summary>给 LayoutGroup 用的尺寸声明。</summary>
        public static LayoutElement Layout(GameObject go, float preferredHeight, float flexibleHeight = 0f,
            float preferredWidth = -1f, float flexibleWidth = -1f)
        {
            if (go == null)
                return null;
            try
            {
                var le = go.GetComponent<LayoutElement>();
                if (le == null)
                    le = go.AddComponent<LayoutElement>();
                if (preferredHeight >= 0f)
                    le.preferredHeight = preferredHeight;
                if (flexibleHeight >= 0f)
                    le.flexibleHeight = flexibleHeight;
                if (preferredWidth >= 0f)
                    le.preferredWidth = preferredWidth;
                if (flexibleWidth >= 0f)
                    le.flexibleWidth = flexibleWidth;
                return le;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Ui] 设置 LayoutElement 失败：" + e.GetType().Name);
                return null;
            }
        }

        /// <summary>让 LayoutGroup 跳过这个子节点（位置我们自己给）。</summary>
        public static void IgnoreLayout(GameObject go)
        {
            var le = Layout(go, -1f, -1f);
            if (le != null)
                le.ignoreLayout = true;
        }

        public static void SetActiveSafe(Transform t, bool active)
        {
            if (t == null)
                return;
            try
            {
                t.gameObject.SetActive(active);
            }
            catch (Exception)
            {
            }
        }

        public static void SetActiveSafe(GameObject go, bool active)
        {
            if (go == null)
                return;
            try
            {
                go.SetActive(active);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>任意组件重载（LabeledToggle 这类没有到 Transform 的隐式转换）。</summary>
        public static void SetActiveSafe(Component c, bool active)
        {
            if (c == null)
                return;
            try
            {
                c.gameObject.SetActive(active);
            }
            catch (Exception)
            {
            }
        }
        #endregion

        #region 布局组
        /// <summary>横向行：子节点宽度按各自 preferredWidth、不强制撑满（这是"文本右侧紧贴控件"的关键）。</summary>
        public static HorizontalLayoutGroup ConfigureRow(GameObject go, float spacing)
        {
            if (go == null)
                return null;
            try
            {
                var group = go.AddComponent<HorizontalLayoutGroup>();
                group.spacing = spacing;
                group.childAlignment = TextAnchor.MiddleLeft;
                group.childControlWidth = true;
                group.childControlHeight = true;
                group.childForceExpandWidth = false;
                group.childForceExpandHeight = false;
                SetPadding(group, 0, 0);
                return group;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Ui] 配置行布局失败：" + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        public static VerticalLayoutGroup ConfigureVertical(GameObject go, float spacing, int padY)
        {
            if (go == null)
                return null;
            try
            {
                var group = go.AddComponent<VerticalLayoutGroup>();
                group.spacing = spacing;
                group.childAlignment = TextAnchor.UpperLeft;
                group.childControlWidth = true;
                group.childControlHeight = true;
                group.childForceExpandWidth = true;
                group.childForceExpandHeight = false;
                SetPadding(group, 0, padY);
                return group;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Ui] 配置纵向布局失败：" + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>把网格容器配成「固定 N 列、左上对齐」。</summary>
        public static GridLayoutGroup ConfigureGrid(GameObject go, int columns, Vector2 cellSize,
            Vector2 spacing, int padX, int padY)
        {
            if (go == null)
                return null;
            try
            {
                var grid = go.AddComponent<GridLayoutGroup>();
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                grid.constraintCount = columns;
                grid.cellSize = cellSize;
                grid.spacing = spacing;
                grid.childAlignment = TextAnchor.UpperLeft;
                SetPadding(grid, padX, padY);
                return grid;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Ui] 配置网格失败：" + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 设置 LayoutGroup 的内边距。
        /// ⚠️ interop 里的 <c>RectOffset</c> **没有** (l, r, t, b) 构造函数 ——
        /// 只能改它返回的实例（引用类型，改的就是 native 对象）再赋回去。
        /// </summary>
        private static void SetPadding(HorizontalOrVerticalLayoutGroup group, int padX, int padY)
        {
            try
            {
                var pad = group.padding;
                if (pad == null)
                    return;
                pad.left = padX;
                pad.right = padX;
                pad.top = padY;
                pad.bottom = padY;
                group.padding = pad;
            }
            catch (Exception)
            {
            }
        }

        private static void SetPadding(GridLayoutGroup grid, int padX, int padY)
        {
            try
            {
                var pad = grid.padding;
                if (pad == null)
                    return;
                pad.left = padX;
                pad.right = padX;
                pad.top = padY;
                pad.bottom = padY;
                grid.padding = pad;
            }
            catch (Exception)
            {
            }
        }
        #endregion

        #region 文本
        /// <summary>
        /// 统一文本样式。
        ///
        /// ⚠️ 关键是 <c>enableAutoSizing = false</c>。原版这些 TMP 文本（对话框标题、
        /// 选项标签…）**都开着自动缩放**，不改的话我们在代码里设的字号会被 TMP 按
        /// "容器大小"重算 —— 实测同一批标签里有人变 36px、有人变 8px（列表条目 8px 根本看不清），
        /// 而需求要的是"文本大小固定"。所以只要是我们接手的文本，一律先关自动缩放。
        /// </summary>
        /// <param name="fontSize">≤ 0 表示保留模板自带的字号。</param>
        /// <param name="wrap">null = 不改换行设置。</param>
        /// <param name="alignment">null = 不改对齐。</param>
        public static void StyleLabel(TextMeshProUGUI label, float fontSize = -1f, bool? wrap = null,
            TextAlignmentOptions? alignment = null)
        {
            if (label == null)
                return;
            try
            {
                label.enableAutoSizing = false;
                if (fontSize > 0f)
                    label.fontSize = fontSize;
                if (wrap.HasValue)
                {
                    label.enableWordWrapping = wrap.Value;
                    label.overflowMode = wrap.Value ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
                }
                if (alignment.HasValue)
                    label.alignment = alignment.Value;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>造一个独立文本（克隆模板架上的 TMP 文本，连字体一起继承）。</summary>
        public static TextMeshProUGUI MakeLabel(Transform parent, string text, float fontSize,
            bool wrap = false, bool autoWidth = true, float maxWidth = -1f)
        {
            var go = Clone(UiSources.TextTemplate, parent, "Label");
            if (go == null)
                return null;
            var label = go.GetComponent<TextMeshProUGUI>();
            if (label == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }
            try
            {
                label.text = text ?? string.Empty;
            }
            catch (Exception)
            {
            }
            // 先把字号/换行/对齐固定下来（关掉 TMP 自动缩放），再量宽度。
            StyleLabel(label, fontSize, wrap, TextAlignmentOptions.Left);
            // 对齐：文本节点自己按内容定宽时，纵向居中看起来最自然。
            var rt = RectOf(go);
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0f, 0.5f);
                rt.anchorMax = new Vector2(0f, 0.5f);
                rt.pivot = new Vector2(0f, 0.5f);
            }
            if (autoWidth)
                MeasureLabel(label, text, maxWidth >= 0f ? maxWidth : GcLayout.LabelMaxWidth);
            go.SetActive(true);
            return label;
        }

        public static void SetLabelText(TextMeshProUGUI label, string text)
        {
            if (label == null)
                return;
            try
            {
                label.text = text ?? string.Empty;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 按文字量定宽 —— 这就是需求里「文本长度根据文字数量决定、文本大小固定、
        /// 文本右侧紧贴相关组件」的实现。
        ///
        /// 用 TMP 自己的 <c>GetPreferredValues</c> 量宽度（已经算进字体、字号、字距），
        /// 写进 LayoutElement.preferredWidth；父级行布局是"不强制撑满"的，
        /// 所以宽度就正好贴在文字右边，下一个控件紧接着开始。
        /// </summary>
        public static void MeasureLabel(TextMeshProUGUI label, string text, float maxWidth = -1f)
        {
            if (label == null)
                return;
            try
            {
                var measured = 0f;
                try
                {
                    var value = label.GetPreferredValues(text ?? string.Empty);
                    measured = value.x;
                }
                catch (Exception)
                {
                }
                if (measured <= 0f)
                {
                    // GetPreferredValues 拿不到时按字符数估：全角按 1 个字宽、半角按 0.55。
                    var size = label.fontSize;
                    var units = 0f;
                    var s = text ?? string.Empty;
                    for (var i = 0; i < s.Length; i++)
                        units += s[i] > 0x2000 ? 1f : 0.55f;
                    measured = units * size * 1.02f;
                }
                var limit = maxWidth > 0f ? maxWidth : GcLayout.LabelMaxWidth;
                measured = Mathf.Min(measured, limit) + 2f;
                Layout(label.gameObject, -1f, -1f, measured, 0f);
            }
            catch (Exception)
            {
            }
        }
        #endregion

        #region 控件
        /// <summary>把 managed 的字符串列表转成 interop 列表（下拉框接口要这个类型）。</summary>
        public static Il2CppSystem.Collections.Generic.List<string> MakeStringList(IList<string> items)
        {
            var list = new Il2CppSystem.Collections.Generic.List<string>();
            if (items == null)
                return list;
            for (var i = 0; i < items.Count; i++)
                list.Add(items[i] ?? string.Empty);
            return list;
        }

        /// <summary>给按钮挂点击回调（owner 用于保活登记，销毁后自动回收）。</summary>
        public static bool BindClick(UnityEngine.UI.Button button, Action handler)
        {
            if (button == null || handler == null)
                return false;
            try
            {
                var managed = new System.Action(handler);
                var il2cpp = DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(managed);
                KeepAlive(button.gameObject, managed, il2cpp);
                button.onClick.AddListener(il2cpp);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Ui] 挂按钮回调失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>给输入框挂「编辑结束」回调。</summary>
        public static bool BindEndEdit(TMP_InputField input, Action<string> handler)
        {
            if (input == null || handler == null)
                return false;
            try
            {
                var managed = new System.Action<string>(handler);
                var il2cpp = DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<string>>(managed);
                KeepAlive(input.gameObject, managed, il2cpp);
                input.onEndEdit.AddListener(il2cpp);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Ui] 挂输入框回调失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 造一个格子：<c>[标签(自适应宽)] [控件(固定或自适应宽)]</c>。
        /// 复选框例外 —— 它的标签就在模板内部，不另造标签。
        /// </summary>
        /// <param name="labelMaxWidth">标签最大宽度（超出截断）；&lt;0 用默认值。</param>
        /// <param name="flexibleControl">控件是否吸收行内剩余宽度（窄格子里的下拉框/输入框用 true）。</param>
        public static WidgetCell CreateCell(Transform parent, string id, CellKind kind, string label, float width,
            float labelMaxWidth = -1f, bool flexibleControl = false)
        {
            var cell = new WidgetCell();
            cell.Id = id;
            cell.Kind = kind;

            var row = NewRect(parent, "Cell_" + id);
            if (row == null)
                return null;
            cell.Root = row;
            ConfigureRow(row, GcLayout.LabelGap);
            Layout(row, -1f, -1f, width, 0f);

            var labelLimit = labelMaxWidth > 0f ? labelMaxWidth : GcLayout.LabelMaxWidth;

            try
            {
                switch (kind)
                {
                    case CellKind.Toggle:
                        {
                            var go = Clone(UiSources.ToggleTemplate, row.transform, "Toggle");
                            if (go == null)
                                break;
                            cell.ControlRoot = go;
                            cell.Toggle = go.GetComponent<UnityEngine.UI.Toggle>();
                            cell.Label = FindLabel(go.transform);
                            cell.LabelInsideControl = true;
                            // 复选框自带的文字是原版为了它自己的容器设计的字号，
                            // 只关自动缩放（保留模板字号）就已对齐；其余由行的宽度决定。
                            StyleLabel(cell.Label);
                            // ⚠️ 必须把文字改成我们的标签：复选框的文本在模板内部，
                            // 不写就会一直显示原版设置界面的「菜单」。
                            SetLabelText(cell.Label, label);
                            SetActiveSafe(go, true);
                            Layout(go, -1f, -1f, width, 0f);
                            row.SetActive(true);
                            return cell;
                        }

                    case CellKind.Dropdown:
                    case CellKind.Number:
                        {
                            cell.Label = MakeLabel(row.transform, label, GcLayout.LabelFontSize, false, true, labelLimit);
                            var template = kind == CellKind.Dropdown ? UiSources.DropdownTemplate : UiSources.InputTemplate;
                            var go = Clone(template, row.transform, kind == CellKind.Dropdown ? "Dropdown" : "Input");
                            if (go == null)
                                break;
                            cell.ControlRoot = go;
                            if (flexibleControl)
                                Layout(go, -1f, -1f, 0f, 1f);
                            else
                                Layout(go, -1f, -1f,
                                    kind == CellKind.Dropdown ? GcLayout.DropdownWidth : GcLayout.InputWidth, 0f);
                            SetActiveSafe(go, true);
                            if (kind == CellKind.Dropdown)
                            {
                                cell.Dropdown = go.GetComponent<TMP_Dropdown>();
                                PrepareDropdown(cell.Dropdown);
                                TakeOverDropdown(cell, go, label);
                            }
                            else
                            {
                                cell.Input = go.GetComponent<TMP_InputField>();
                                PrepareInput(cell.Input);
                            }
                            row.SetActive(true);
                            return cell;
                        }

                    case CellKind.Button:
                        {
                            var go = Clone(UiSources.ButtonTemplate, row.transform, "Button");
                            if (go == null)
                                break;
                            cell.ControlRoot = go;
                            cell.Button = go.GetComponent<UnityEngine.UI.Button>();
                            cell.Label = FindLabel(go.transform);
                            Layout(go, -1f, -1f, width, 0f);
                            SetLabelText(cell.Label, label);
                            StyleLabel(cell.Label);
                            SetActiveSafe(go, true);
                            row.SetActive(true);
                            return cell;
                        }
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Ui] 造格子「" + id + "」失败：" + e);
            }
            Plugin.Log?.LogError("[Ui] 造格子「" + id + "」失败（模板缺失）");
            return cell;
        }

        /// <summary>
        /// 下拉框的兜底修正。
        ///
        /// 关键点：TMP_Dropdown 展开时按**自己的 RectTransform** 算弹出位置与尺寸。
        /// 如果它继承到的尺寸不对（例如 0 高、或宽到屏幕外），弹窗就会出现在看不见的地方
        /// —— 用户看到的正是「下拉框点不开」。所以这里显式保证：
        ///   · 高度 ≥ 行高；
        ///   · 允许交互（克隆体的 Selectable 状态可能被模板带成不可交互）；
        ///   · 展开前先把 <c>template</c> 的 RectTransform 撑到合理尺寸；
        ///   · **弹窗画布的相机/渲染方式对齐面板所在画布**（见 <see cref="AlignPopupCanvas"/>）。
        /// </summary>
        private static void PrepareDropdown(TMP_Dropdown dd)
        {
            if (dd == null)
                return;
            try
            {
                dd.interactable = true;
                var template = dd.template;
                if (template != null)
                {
                    // 模板节点平时是 inactive 的，TMP 会在 Show() 里临时激活它。
                    // 这里只保证它有合理的尺寸，不动它的 active 状态。
                    if (template.sizeDelta.y < 60f)
                        template.sizeDelta = new Vector2(template.sizeDelta.x, 150f);
                    AlignPopupCanvas(dd, template);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Ui] 修正下拉框失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>弹窗画布的 sortingOrder —— 必须高于面板所在画布，否则会被面板盖住。</summary>
        private const int PopupSortingBaseOrder = 30000;

        /// <summary>
        /// ⚠️⚠️ 让下拉弹窗的画布与面板所在画布**渲染方式一致** —— 这是
        /// bug「下拉选择框点开后不显示 UI，乱点能注意到功能有效」的**真正病因**。
        ///
        /// TMP 展开下拉框时会在 template 上 <c>AddComponent&lt;Canvas&gt;()</c>：
        /// 新建出来的 Canvas 默认 <c>ScreenSpaceOverlay / worldCamera = null</c>，
        /// TMP 只改 <c>overrideSorting = true</c> 与 <c>sortingOrder = 30000</c>。
        /// 而本游戏的主 UI 画布 <c>GlobalUICanvas</c> 是 **Screen Space - Camera**
        /// （mode=ScreenSpaceCamera，cam=UICamera，planeDistance=100）。
        /// 开了 <c>overrideSorting</c> 的嵌套画布会被当成**独立画布**、按它自己的
        /// renderMode / worldCamera 处理 → 弹窗画布没有相机 → 整棵弹窗一个像素都画不出来；
        /// 但它的 <c>GraphicRaycaster</c> 照常生效，所以点是点得到的。
        ///
        /// 现场证据（自测 dump + 截图差分）：
        ///   · 弹窗节点 active、Image.enabled=1、色 alpha=1、<c>CanvasGroup.alpha=1</c>、
        ///     Canvas overrideSorting=1 order=30000 layer=Dialog、屏幕矩形完全正确；
        ///   · 「展开前 / 展开后」两张截图的像素差分≈0（几百个差异像素全在别处）；
        ///   · 同一个视角下**没展开**的 Dropdown 底板显示正常（所以不是 sprite/alpha 的问题）；
        ///   · 根画布 <c>mode=ScreenSpaceCamera cam=UICamera</c>，弹窗画布 <c>cam=null</c>。
        /// 对齐 renderMode / worldCamera / planeDistance / sortingLayerID 之后就恢复正常了。
        /// </summary>
        private static void AlignPopupCanvas(TMP_Dropdown dd, RectTransform template)
        {
            var popupCanvas = template.GetComponent<Canvas>();
            if (popupCanvas == null)
                return;
            Canvas root = null;
            try
            {
                var any = dd.GetComponentInParent<Canvas>();
                root = any == null ? null : any.rootCanvas;
            }
            catch (Exception)
            {
            }
            if (root == null)
            {
                Plugin.Log?.LogWarning("[Ui] 找不到根画布，无法对齐下拉弹窗画布（"+dd.name+"）");
                return;
            }
            popupCanvas.overrideSorting = true;
            popupCanvas.sortingLayerID = root.sortingLayerID;
            popupCanvas.sortingOrder = Mathf.Max(PopupSortingBaseOrder, root.sortingOrder + 1);
            popupCanvas.renderMode = root.renderMode;
            popupCanvas.worldCamera = root.worldCamera;
            popupCanvas.planeDistance = root.planeDistance;
        }

        private static void PrepareInput(TMP_InputField input)
        {
            if (input == null)
                return;
            try
            {
                input.contentType = TMP_InputField.ContentType.Standard;
                input.characterLimit = 16;
                input.lineType = TMP_InputField.LineType.SingleLine;
                input.interactable = true;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>在子树里找第一个 TMP 文本（用作按钮/复选框的标签）。</summary>
        private static TextMeshProUGUI FindLabel(Transform root)
        {
            if (root == null)
                return null;
            try
            {
                var direct = root.GetComponent<TextMeshProUGUI>();
                if (direct != null)
                    return direct;
                var node = UiFinder.FindChild(root, "Text");
                if (node != null)
                {
                    var t = node.GetComponent<TextMeshProUGUI>();
                    if (t != null)
                        return t;
                }
                return UiFinder.FindChildComponent<TextMeshProUGUI>(root);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void SetDropdownOptions(WidgetCell cell, IList<string> options)
        {
            if (cell == null)
                return;
            try
            {
                if (cell.Options == null)
                    cell.Options = new List<string>();
                cell.Options.Clear();
                if (options != null)
                {
                    for (var i = 0; i < options.Count; i++)
                        cell.Options.Add(options[i] ?? string.Empty);
                }
                if (cell.Selected < 0 || cell.Selected >= cell.Options.Count)
                    cell.Selected = cell.Options.Count > 0 ? 0 : -1;
                RefreshDropdownCaption(cell);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Ui] 设置下拉选项失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        public static void SetDropdownValue(WidgetCell cell, int index)
        {
            if (cell == null || index < 0)
                return;
            try
            {
                var count = cell.Options == null ? 0 : cell.Options.Count;
                if (count > 0 && index >= count)
                    index = count - 1;
                cell.Selected = index;
                RefreshDropdownCaption(cell);
            }
            catch (Exception)
            {
            }
        }

        public static int GetDropdownValue(WidgetCell cell)
        {
            return cell == null ? -1 : cell.Selected;
        }

        /// <summary>把当前选中项的文本写到下拉框自己的那行显示文本上。</summary>
        public static void RefreshDropdownCaption(WidgetCell cell)
        {
            if (cell == null)
                return;
            try
            {
                var text = string.Empty;
                if (cell.Options != null && cell.Selected >= 0 && cell.Selected < cell.Options.Count)
                    text = cell.Options[cell.Selected];
                SetLabelText(cell.Caption, text);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 接管一个下拉格：只留 TMP_Dropdown 的**外观**（底板 + 显示当前值的文本），
        /// 点击改成开我们自己的选择浮层。
        ///
        /// 为什么：TMP 自带的弹窗在本环境里完全画不出来（详见 <see cref="PickerOverlay"/>）。
        /// 做法：
        ///   · 先把 captionText 拿下来（销毁组件后就取不到了）；
        ///   · 直接 <c>Destroy</c> 掉 TMP_Dropdown —— 它一旦被点到就会 <c>Show()</c>
        ///     生成那个在本环境里画不出来的弹窗，而且它的 OnEnable/RefreshShownValue
        ///     会把显示文本改回模板自带的「Option A」；
        ///   · 点击交给 <see cref="TickDropdowns"/> 的**鼠标位置轮询**（没挂 Button —— 见那里的注释）。
        /// </summary>
        private static void TakeOverDropdown(WidgetCell cell, GameObject control, string title)
        {
            try
            {
                var dd = cell.Dropdown;
                if (dd != null)
                {
                    var caption = dd.captionText;
                    if (caption != null)
                        cell.Caption = caption.TryCast<TextMeshProUGUI>();
                }
                if (cell.Caption == null)
                    cell.Caption = FindLabel(control.transform);
                StyleLabel(cell.Caption);

                if (dd != null)
                {
                    UnityEngine.Object.Destroy(dd);
                    cell.Dropdown = null;
                }

                var image = control.GetComponent<Image>();
                // ⚠️ 这里**不能**用 AddComponent<Button>()：自测实测它对克隆出来的下拉节点
                // **返回 null**（不抛异常，日志「下拉格 area 需要新建 Button：结果=**null**」），
                // 于是“点击”根本没有入口 —— 这就是“下拉框点开没反应”的直接原因。
                // 引擎侧 UI 类型的 AddComponent 在本环境不可靠（而 MVZ2 自己的组件，例如
                // TooltipHandler，AddComponent 是好的）。
                // 所以交互全部走 TickDropdowns 的鼠标位置轮询 + 自己画的悬停高亮。
                cell.ControlImage = image;
                cell.Button = null;
                cell.Title = title;
                if (cell.Options == null)
                    cell.Options = new List<string>();
                if (!dropdownCells.Contains(cell))
                    dropdownCells.Add(cell);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Ui] 接管下拉框失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        #region 下拉格的点击兜底
        /// <summary>当前页面里所有下拉格。</summary>
        private static readonly List<WidgetCell> dropdownCells = new List<WidgetCell>();
        /// <summary>判定鼠标是否落在下拉格上要用的相机（由 <see cref="PanelShell"/> 在构建时给）。</summary>
        public static Camera PickerCamera;

        /// <summary>切页 / 重建内容时清空登记。</summary>
        public static void ClearDropdowns()
        {
            dropdownCells.Clear();
        }

        /// <summary>
        /// 每帧由面板心跳调用：鼠标按下时看看是否落在某个下拉格上，是则打开选择浮层。
        ///
        /// 为什么除了 Button 之外还要这一条：
        ///   · Button 路线依赖 Unity 的 UI 事件系统（GraphicRaycaster → EventSystem → 事件冒泡），
        ///     任何一环出问题都是“点了没反应”，而且极难从日志看出来；
        ///   · 这里直接用 <c>Input.mousePosition</c> + <c>RectangleContainsScreenPoint</c> 判定，
        ///     与事件系统、raycast、targetGraphic 全部无关，只是一个坐标包含判断。
        /// 两条路并存：Button 负责 hover 视觉与语义，轮询负责“一定点得开”。
        /// </summary>
        public static void TickDropdowns()
        {
            var picker = PickerOverlay.Shared;
            if (picker == null || dropdownCells.Count == 0)
                return;
            try
            {
                var pointer = (Vector2)Input.mousePosition;
                var canClick = !picker.IsOpen && Input.GetMouseButtonDown(0);
                for (var i = 0; i < dropdownCells.Count; i++)
                {
                    var cell = dropdownCells[i];
                    if (cell == null || !UiFinder.IsAlive(cell.ControlRoot))
                        continue;
                    var rt = RectOf(cell.ControlRoot);
                    if (rt == null)
                        continue;
                    var over = RectTransformUtility.RectangleContainsScreenPoint(rt, pointer, PickerCamera);
                    // 悬停高亮（自己画 —— 原版按钮的 hover 效果拿不到，因为 Button 挂不上）。
                    SetHover(cell, over);
                    if (canClick && over)
                    {
                        OpenPicker(cell, cell.Title);
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                if (warnedTicks.Add("tickdropdowns"))
                    Plugin.Log?.LogWarning("[Ui] 下拉格轮询失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static readonly Color DropdownHover = new Color(0.82f, 0.9f, 1f, 1f);

        private static void SetHover(WidgetCell cell, bool over)
        {
            var image = cell.ControlImage;
            if (image == null)
                return;
            try
            {
                if (cell.Hovered == over)
                    return;
                cell.Hovered = over;
                image.color = over ? DropdownHover : Color.white;
            }
            catch (Exception)
            {
            }
        }

        private static readonly HashSet<string> warnedTicks = new HashSet<string>();

        /// <summary>
        /// 诊断用：把当前页面上每个下拉格的状态打出来。
        /// 特意包含 <c>targetGraphic</c> —— 「AddComponent 出来的 Button 没有 targetGraphic
        /// 所以点不动」是很难从现象反推的一类问题（点击被静默吞掉），必须看到值才算验过。
        /// </summary>
        public static string DescribeDropdowns()
        {
            if (dropdownCells.Count == 0)
                return "（本页没有下拉格）";
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < dropdownCells.Count; i++)
            {
                var cell = dropdownCells[i];
                sb.Append(i).Append("：");
                if (cell == null || !cell.Exists)
                {
                    sb.Append("已失效\n");
                    continue;
                }
                sb.Append(cell.Id).Append(" 标题=").Append(cell.Title)
                    .Append(" 选项=").Append(cell.Options == null ? -1 : cell.Options.Count)
                    .Append(" 选中=").Append(cell.Selected)
                    .Append(" 显示文本=「").Append(CaptionText(cell)).Append("」");
                try
                {
                    var button = cell.Button;
                    if (button == null)
                        sb.Append(" Button=无");
                    else
                    {
                        var graphic = button.targetGraphic;
                        sb.Append(" Button=有 targetGraphic=")
                            .Append(graphic == null
                                ? "**null（点不动！）**"
                                : (graphic.name + "/" + (graphic is Image img && img.sprite != null ? img.sprite.name : "无图")))
                            .Append(" interactable=").Append(button.interactable ? 1 : 0);
                    }
                    var rt = RectOf(cell.ControlRoot);
                    if (rt != null)
                    {
                        var min = rt.TransformPoint(new Vector3(rt.rect.xMin, rt.rect.yMin, 0f));
                        var max = rt.TransformPoint(new Vector3(rt.rect.xMax, rt.rect.yMax, 0f));
                        var cam = PickerCamera;
                        var a = RectTransformUtility.WorldToScreenPoint(cam, min);
                        var b = RectTransformUtility.WorldToScreenPoint(cam, max);
                        sb.Append(" 屏=[").Append((int)a.x).Append(',').Append((int)a.y)
                            .Append(" → ").Append((int)b.x).Append(',').Append((int)b.y).Append(']');
                    }
                }
                catch (Exception e)
                {
                    sb.Append(" <读状态失败 ").Append(e.GetType().Name).Append('>');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string CaptionText(WidgetCell cell)
        {
            try
            {
                return cell.Caption == null ? "（无）" : (cell.Caption.text ?? string.Empty);
            }
            catch (Exception)
            {
                return "?";
            }
        }
        #endregion

        /// <summary>点下拉格 → 打开自绘的选择浮层。</summary>
        private static void OpenPicker(WidgetCell cell, string title)
        {
            var picker = PickerOverlay.Shared;
            if (picker == null || cell == null || cell.Options == null || cell.Options.Count == 0)
                return;
            picker.Open(title, cell.Options, cell.Selected, index =>
            {
                cell.Selected = index;
                RefreshDropdownCaption(cell);
            });
        }

        public static void SetToggleValue(WidgetCell cell, bool value)
        {
            if (cell == null || cell.Toggle == null)
                return;
            try
            {
                cell.Toggle.SetIsOnWithoutNotify(value);
            }
            catch (Exception)
            {
            }
        }

        public static bool GetToggleValue(WidgetCell cell)
        {
            if (cell == null || cell.Toggle == null)
                return false;
            try
            {
                return cell.Toggle.isOn;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void SetInputText(WidgetCell cell, string text)
        {
            if (cell == null || cell.Input == null)
                return;
            try
            {
                cell.Input.SetTextWithoutNotify(text ?? string.Empty);
            }
            catch (Exception)
            {
                try
                {
                    cell.Input.text = text ?? string.Empty;
                }
                catch (Exception)
                {
                }
            }
        }

        public static string GetInputText(WidgetCell cell)
        {
            if (cell == null || cell.Input == null)
                return string.Empty;
            try
            {
                return cell.Input.text ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
        #endregion
    }
}
