using System;
using System.Collections.Generic;
using System.Text;
using DSHCore.Ui;
using Il2CppInterop.Runtime;
using MVZ2.Archives;
using PVZEngine;
using PVZEngine.Tools.Unity;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 「带搜索框的可多选条目列表」—— 整套外观与交互都是**克隆对话档案（Archive）索引页左侧那一块**
    /// 得来的：搜索框 + 可滚动列表，行是原版的 <c>ArchiveTagItem</c>（<c>Toggle</c> + 文字）。
    ///
    /// <para><b>为什么要克隆它而不是自己搭：</b>进度页要的是「一个搜索框 + 一个能多选的列表」，
    /// 而原版档案页里已经有现成的这一套（<c>IndexArchivePage</c>：<c>searchInputField</c> +
    /// <c>tagsList</c>），并且它用的不是 <c>TMP_Dropdown</c>（那个组件在本环境不可用）。</para>
    ///
    /// <para><b>过滤由我们自己实现</b>：原版把过滤逻辑写在 <c>ArchiveController</c> 里
    /// （<c>name.Contains(searchPattern)</c>），控件本身只管显示。这里对**行文本**做
    /// 大小写不敏感的包含匹配 —— 行文本形如 <c>[x] mvz2:xxx</c>，所以搜索框里敲
    /// <c>[x]</c> / <c>[ ]</c> 就正好能筛出已解锁 / 未解锁的条目。</para>
    ///
    /// <para><b>「勾选」的语义 = 选中</b>（不是解锁状态）：解锁状态由行首的 <c>[x]</c> / <c>[ ]</c>
    /// 表示，勾选框只用来给「解锁 / 锁定」两个按钮挑对象。</para>
    /// </summary>
    internal sealed class UiArchiveList
    {
        /// <summary>一行条目。</summary>
        public sealed class Row
        {
            public NamespaceID Id;
            public string IdText;
            public bool Checked;      // 勾选 = 选中（供解锁/锁定用）
        }

        private readonly GameObject _root;
        private readonly ElementList _items;
        private readonly Transform _listRoot;
        private readonly Func<NamespaceID, bool> _isUnlocked;
        private readonly List<Row> _rows = new List<Row>();
        private readonly List<int> _shown = new List<int>();
        private string _filter = string.Empty;
        private Action _onChanged;

        /// <summary>所有活着的列表（面板每帧转发 <see cref="Tick"/>，点击检测要每帧跑）。</summary>
        private static readonly List<UiArchiveList> All = new List<UiArchiveList>();

        public static void TickAll()
        {
            for (var i = 0; i < All.Count; i++)
            {
                try { All[i].Tick(); }
                catch (Exception) { }
            }
        }

        /// <summary>面板重建时丢掉全部引用。</summary>
        public static void Forget()
        {
            All.Clear();
            _neutralized.Clear();
        }

        // 委托保活（IL2CPP 侧只存裸函数指针）
        private System.Action<int, GameObject> _updateManaged;
        private Il2CppSystem.Action<int, GameObject> _updateIl2Cpp;
        private System.Action<GameObject> _createManaged, _destroyManaged;
        private Il2CppSystem.Action<GameObject> _createIl2Cpp, _destroyIl2Cpp;

        private UiArchiveList(GameObject root, ElementList items, Func<NamespaceID, bool> isUnlocked)
        {
            _root = root;
            _items = items;
            _listRoot = items == null ? null : items.ListRoot;
            _isUnlocked = isUnlocked ?? ProgressOps.IsUnlocked;
        }

        public GameObject Root { get { return UiFinder.IsAlive(_root) ? _root : null; } }

        public bool IsValid { get { return UiFinder.IsAlive(_root) && _items != null && UiFinder.IsAlive(_items); } }

        public int RowCount { get { return _rows.Count; } }

        public int ShownCount { get { return _shown.Count; } }

        public string Filter { get { return _filter; } }

        public List<Row> Rows { get { return _rows; } }

        // ------------------------------------------------------------------
        // 装配
        // ------------------------------------------------------------------

        /// <summary>
        /// 克隆档案页左侧那个列表，挂到 <paramref name="parent"/> 下。
        /// 拿不到模板时返回 null（页面应当降级，而不是抛）。
        /// </summary>
        public static UiArchiveList Create(Transform parent, string name)
        {
            var template = ArchiveProbe.TagsScrollView();
            if (!UiFinder.IsAlive(template))
            {
                Plugin.LogOnce("archivelist-tpl",
                    "[ArchiveList] 拿不到档案页的列表模板，进度页的列表不可用。");
                return null;
            }

            // activate:false —— 先把「不该活着的东西」处理掉，再让它 Awake。
            var root = UiClone.Instantiate(template, parent, name, false);
            if (!UiFinder.IsAlive(root))
            {
                Plugin.LogOnce("archivelist-clone", "[ArchiveList] 列表克隆失败。");
                return null;
            }

            var items = Sanitize(root);
            if (items == null)
            {
                UnityEngine.Object.Destroy(root);
                Plugin.LogOnce("archivelist-items", "[ArchiveList] 列表里找不到 ElementList，已放弃克隆。");
                return null;
            }

            UiClone.Activate(root);
            var list = new UiArchiveList(root, items, null);
            list.Configure();
            All.Add(list);
            return list;
        }

        /// <summary>
        /// 克隆体的结构修补（**必须在激活之前做**）：
        /// <list type="number">
        /// <item>视口遮罩 <c>Mask</c> → <c>RectMask2D</c>：<c>Mask</c> 走模板缓冲，会让 TMP 的
        /// 子网格与模板共用同一份 Stencil 材质，之后任一边被销毁都会连累另一边抛空指针
        /// （与 DSHCore 处理它的可滚动容器时同一个坑）；</item>
        /// <item>本地缩放归 1：档案页在**另一个 Canvas**（<c>MainScene/Archive/Canvas</c>）下，
        /// 克隆过来的子树如果带着那边的世界缩放，行高就跟我们自己的布局组对不上。</item>
        /// </list>
        /// </summary>
        private static ElementList Sanitize(GameObject root)
        {
            try
            {
                root.transform.localScale = Vector3.one;
            }
            catch (Exception) { }

            try
            {
                var sr = root.GetComponent<ScrollRect>();
                if (sr == null)
                    sr = root.GetComponentInChildren<ScrollRect>(true);
                if (sr != null)
                {
                    try
                    {
                        sr.horizontal = false;
                        sr.vertical = true;
                    }
                    catch (Exception) { }

                    try
                    {
                        var viewport = sr.viewport;
                        if (viewport != null)
                        {
                            var mask = viewport.GetComponent<Mask>();
                            if (mask != null)
                                UnityEngine.Object.DestroyImmediate(mask);
                            if (viewport.GetComponent<RectMask2D>() == null)
                                viewport.gameObject.AddComponent<RectMask2D>();
                            // ⚠️ 视口上那张图原本是 Mask 的遮罩素材（showMaskGraphic=false 时靠改材质画成透明）。
                            // 换成 RectMask2D 之后它变回**普通 Image**，会画成一块纯白矩形（实测就是这样）。
                            Neutralize(viewport.gameObject, "视口遮罩素材");
                        }
                    }
                    catch (Exception e)
                    {
                        Plugin.LogOnce("archivelist-mask", "[ArchiveList] 替换视口遮罩失败：" + e.GetType().Name);
                    }
                }
            }
            catch (Exception) { }

            // 除了视口那张遮罩素材，其它“无 sprite 的图”不再一律藏掉 ——
            // 行的 Checkmark / Hover 也是无 sprite 的图，它们是勾选与悬停的**正经视觉**，
            // 藏了就没法看出选了哪一条（实测误伤过）。

            try
            {
                return UiFinder.FindChildComponent<ElementList>(root.transform);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 把节点上那张图“藏起来”（alpha 清 0）但保留 <c>raycastTarget</c> ——
        /// 直接禁用或删掉会顺手拆掉点击/拖拽的承接（列表要靠它接滚动）。
        /// </summary>
        private static void Neutralize(GameObject go, string why)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                var img = go.GetComponent<Image>();
                if (img == null)
                    return;
                var c = img.color;
                if (c.a <= 0.001f)
                    return;
                img.color = new Color(c.r, c.g, c.b, 0f);
                _neutralized.Add(go.name + "（" + why + "）");
            }
            catch (Exception) { }
        }

        private static readonly List<string> _neutralized = new List<string>();

        /// <summary>尺寸契约：宽跟着容器等分，高按画布折算（与 <c>UiList.SuggestHeight</c> 同源）。</summary>
        private void Configure()
        {
            try
            {
                var le = _root.GetComponent<LayoutElement>();
                if (le == null)
                    le = _root.AddComponent<LayoutElement>();
                if (le != null)
                {
                    le.minWidth = -1f;
                    le.preferredWidth = 0f;
                    le.flexibleWidth = 1f;
                    le.minHeight = -1f;
                    le.preferredHeight = UiList.SuggestHeight(0.4f);
                    le.flexibleHeight = 0f;
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("archivelist-size", "[ArchiveList] 设置列表尺寸失败：" + e.GetType().Name);
            }
        }

        public UiArchiveList OnSelectionChanged(Action handler)
        {
            _onChanged = handler;
            return this;
        }

        // ------------------------------------------------------------------
        // 数据
        // ------------------------------------------------------------------

        /// <summary>整表重建（条目集合变了的时候用）。</summary>
        public void SetSource(List<NamespaceID> ids)
        {
            _rows.Clear();
            if (ids != null)
            {
                for (var i = 0; i < ids.Count; i++)
                {
                    var id = ids[i];
                    if (id == null)
                        continue;
                    _rows.Add(new Row { Id = id, IdText = Text(id), Checked = false });
                }
                // 始终排序（进度列表的既有约定）。
                _rows.Sort((a, b) => string.Compare(a.IdText, b.IdText, StringComparison.Ordinal));
            }
            Refresh();
        }

        /// <summary>设置搜索词并重排（行文本的包含匹配，大小写不敏感）。</summary>
        public void SetFilter(string text)
        {
            var next = text ?? string.Empty;
            if (next == _filter)
                return;
            _filter = next;
            Refresh();
        }

        /// <summary>解锁状态可能变了 → 重算全部行文本并刷新可见行。</summary>
        public void RefreshLabels()
        {
            Refresh();
        }

        public List<Row> CheckedRows()
        {
            var result = new List<Row>();
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Checked)
                    result.Add(_rows[i]);
            }
            return result;
        }

        public int CheckedCount { get { return CheckedRows().Count; } }

        public void ClearChecked()
        {
            for (var i = 0; i < _rows.Count; i++)
                _rows[i].Checked = false;
            Refresh();
        }

        /// <summary>「[x] / [ ]」+ id —— 行文本本身就是解锁状态的表示（也是搜索的匹配对象）。</summary>
        public string Label(Row row)
        {
            if (row == null)
                return string.Empty;
            var unlocked = false;
            try { unlocked = _isUnlocked(row.Id); }
            catch (Exception) { }
            return (unlocked ? "[x] " : "[ ] ") + row.IdText;
        }

        // ------------------------------------------------------------------
        // 刷新
        // ------------------------------------------------------------------

        private void Refresh()
        {
            if (!IsValid)
                return;
            try
            {
                RebuildShown();
                _items.updateList(_shown.Count, UpdateAction(), CreateAction(), DestroyAction(), true);
                LoadoutPanel.RebuildLayout();
            }
            catch (Exception e)
            {
                Plugin.LogOnce("archivelist-refresh", "[ArchiveList] 刷新列表失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private void RebuildShown()
        {
            _shown.Clear();
            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (_filter.Length > 0)
                {
                    var label = Label(row);
                    if (label.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }
                _shown.Add(i);
            }
        }

        private Il2CppSystem.Action<int, GameObject> UpdateAction()
        {
            if (_updateManaged == null)
            {
                _updateManaged = new System.Action<int, GameObject>(OnRowUpdate);
                _updateIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int, GameObject>>(_updateManaged);
                UiClone.KeepAlive(_root, _updateManaged, _updateIl2Cpp);
            }
            return _updateIl2Cpp;
        }

        private Il2CppSystem.Action<GameObject> CreateAction()
        {
            if (_createManaged == null)
            {
                _createManaged = new System.Action<GameObject>(OnRowCreate);
                _createIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<GameObject>>(_createManaged);
                UiClone.KeepAlive(_root, _createManaged, _createIl2Cpp);
            }
            return _createIl2Cpp;
        }

        private Il2CppSystem.Action<GameObject> DestroyAction()
        {
            if (_destroyManaged == null)
            {
                // 行被回收/销毁时不需要额外处理：勾选状态与文字都在数据行上，
                // 下次 updateList 会把它们重新写进去。
                _destroyManaged = new System.Action<GameObject>(OnRowDestroyed);
                _destroyIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<GameObject>>(_destroyManaged);
                UiClone.KeepAlive(_root, _destroyManaged, _destroyIl2Cpp);
            }
            return _destroyIl2Cpp;
        }

        private static void OnRowDestroyed(GameObject go)
        {
            // 无操作（保留回调形状，便于以后加特效/回收逻辑）。
        }

        /// <summary>
        /// 每帧的点击检测（由面板转发）。
        ///
        /// <para><b>为什么不用行自己的 <c>Toggle</c> 事件：</b>事件只告诉我们“哪个组件”被点，
        /// 要把它换回“哪一条数据”得靠 <c>ElementList.indexOf</c> —— 一旦行被池化复用/下标有一点偏差，
        /// 就会出现「点第一列没反应/锁错一项」这类毛病（实测报过）。
        /// 矩形命中是所见即所得：指针在哪个行上，就改哪一行。</para>
        /// </summary>
        public void Tick()
        {
            if (!IsValid || _items == null)
                return;
            try
            {
                if (!Input.GetMouseButtonDown(0))
                    return;
                var pointer = (Vector2)Input.mousePosition;
                if (!Contains(_root, pointer))
                    return;
                for (var i = 0; i < _shown.Count; i++)
                {
                    var go = _items.getElement(i);
                    if (!Contains(go, pointer))
                        continue;
                    var row = _rows[_shown[i]];
                    row.Checked = !row.Checked;
                    ApplyVisual(go, row);
                    _onChanged?.Invoke();
                    return;
                }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("archivelist-click", "[ArchiveList] 处理点击失败：" + e.GetType().Name);
            }
        }

        private static bool Contains(GameObject node, Vector2 screenPoint)
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
                return RectTransformUtility.RectangleContainsScreenPoint(rt, screenPoint, LoadoutPanel.CanvasCamera());
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>把一行的文字与勾选状态写进行内控件（用原版自己的入口）。</summary>
        private void ApplyVisual(GameObject go, Row row)
        {
            try
            {
                if (!UiFinder.IsAlive(go) || row == null)
                    return;
                var item = go.GetComponent<ArchiveTagItem>();
                if (item == null)
                    return;
                item.UpdateTag(new ArchiveTagViewData { name = Label(row), value = row.Checked });
            }
            catch (Exception) { }
        }

        private void OnRowUpdate(int index, GameObject go)
        {
            try
            {
                if (!UiFinder.IsAlive(go) || index < 0 || index >= _shown.Count)
                    return;
                var item = go.GetComponent<ArchiveTagItem>();
                if (item == null)
                    return;
                var row = _rows[_shown[index]];
                // 用原版自己的更新入口：设文字 + 设勾选（SetIsOnWithoutNotify，不会回调）。
                item.UpdateTag(new ArchiveTagViewData { name = Label(row), value = row.Checked });
            }
            catch (Exception) { }
        }

        private void OnRowCreate(GameObject go)
        {
            try
            {
                if (!UiFinder.IsAlive(go))
                    return;
                // 行是 Instantiate 出来的：档案页在另一个 Canvas 下，世界缩放会被带过来 →
                // 归 1，否则行高与我们自己的布局组对不上。
                go.transform.localScale = Vector3.one;
                MuteRowToggle(go);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 让行**不再自己接收鼠标点击**。
        ///
        /// <para><b>为什么必须做：</b>行里的 <c>Toggle</c> 是原版控件，鼠标点它会自己翻一次
        /// （<c>Selectable.OnPointerClick</c>）。而点击处理权在我们手里（见 <see cref="Tick"/>），
        /// 于是同一次点击会翻**两遍** —— 我们刚写进去的勾选立刻被它翻回来，
        /// 表现就是「点了条目该行不变绿」，而数据里其实已经勾上了（下一个 bug：按钮于是对不上）。</para>
        ///
        /// <para>做法：给行加一个 <c>CanvasGroup</c> 并把 <c>blocksRaycasts</c> 关掉。
        /// 它只挡射线，不改颜色/透明度（不像 <c>interactable=false</c> 会把勾选框变灰），
        /// 也不影响滚动（拖动由上层 <c>ScrollRect</c> 接）。</para>
        /// </summary>
        private static void MuteRowToggle(GameObject row)
        {
            try
            {
                var go = row;
                if (row.GetComponent<Toggle>() == null)
                {
                    var toggle = row.GetComponentInChildren<Toggle>(true);
                    if (toggle == null)
                        return;
                    go = toggle.gameObject;
                }
                var group = go.GetComponent<CanvasGroup>();
                if (group == null)
                    group = go.AddComponent<CanvasGroup>();
                if (group != null)
                {
                    group.alpha = 1f;
                    group.interactable = true;
                    group.blocksRaycasts = false;
                }
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 诊断
        // ------------------------------------------------------------------

        public string Describe()
        {
            var sb = new StringBuilder(200);
            sb.Append("列表=").Append(UiFinder.IsAlive(_root) ? UiFinder.PathOf(_root.transform) : "无")
              .Append("  条目=").Append(_rows.Count)
              .Append("  显示=").Append(_shown.Count)
              .Append("  勾选=").Append(CheckedCount)
              .Append("  搜索「").Append(_filter).Append("」");            if (_neutralized.Count > 0)
            {
                sb.Append("  已藏的图：");
                for (var i = 0; i < _neutralized.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    sb.Append(_neutralized[i]);
                }
            }            var checkedRows = CheckedRows();
            if (checkedRows.Count > 0 && checkedRows.Count <= 6)
            {
                sb.Append("（");
                for (var i = 0; i < checkedRows.Count; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(checkedRows[i].IdText);
                }
                sb.Append("）");
            }
            var dump = RowDump();
            if (dump.Length > 0)
                sb.Append('\n').Append(dump);
            return sb.ToString();
        }

        private static string Text(NamespaceID id)
        {
            if (id == null)
                return string.Empty;
            try
            {
                // NamespaceID.ToString() 在 interop 里只会打印类型名，自己拼。
                return id.SpaceName + ":" + id.Path;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        /// <summary>
        /// 诊断：把第一行的子树画出来（名字 / 组件 / 屏幕矩形 / 是否接鼠标）。
        ///
        /// 「行不变绿」这类毛病需要知道绿色到底是哪个节点画的、点击到底被谁吃掉了 ——
        /// 这两件事从数字上看得比猜快。
        /// </summary>
        public string RowDump()
        {
            var sb = new StringBuilder(200);
            try
            {
                if (!IsValid || _shown.Count == 0)
                    return sb.ToString();
                var row = _items.getElement(0);
                if (!UiFinder.IsAlive(row))
                    return sb.ToString();
                sb.Append("首行：").Append(row.name).Append(' ').Append(RowNode(row));
                var t = row.transform;
                for (var i = 0; i < t.childCount; i++)
                {
                    var child = t.GetChild(i);
                    if (child == null)
                        continue;
                    sb.Append('\n').Append("     ").Append(RowNode(child.gameObject));
                }
            }
            catch (Exception e)
            {
                sb.Append("（行 dump 失败：").Append(e.GetType().Name).Append("）");
            }
            return sb.ToString();
        }

        private static string RowNode(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return "（无）";
            var sb = new StringBuilder(80);
            sb.Append('[');
            var toggle = go.GetComponent<Toggle>();
            if (toggle != null)
                sb.Append("Toggle ");
            if (go.GetComponent<CanvasGroup>() != null)
                sb.Append("CanvasGroup ");
            var graphic = go.GetComponent<Graphic>();
            if (graphic != null)
                sb.Append(graphic.GetType().Name).Append(graphic.raycastTarget ? "(接鼠标) " : "(不接) ");
            sb.Append(']');
            try
            {
                var rt = go.transform.TryCast<RectTransform>();
                if (rt != null)
                {
                    var r = PanelDump.ScreenRect(rt);
                    sb.Append(" 屏x[").Append(Mathf.RoundToInt(r.xMin)).Append(',').Append(Mathf.RoundToInt(r.xMax))
                      .Append("] y[").Append(Mathf.RoundToInt(r.yMin)).Append(',').Append(Mathf.RoundToInt(r.yMax)).Append(']');
                }
            }
            catch (Exception) { }
            if (toggle != null && go.GetComponent<CanvasGroup>() == null)
                sb.Append(" ⚠️Toggle 未被挡");
            return sb.ToString();
        }
    }
}
