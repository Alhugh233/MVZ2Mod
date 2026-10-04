using System;
using System.Text;
using DSHCore.Ui;
using MVZ2.Archives;
using PVZEngine.Tools.Unity;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 「对话档案」（游戏里的 <c>ArchiveController</c> / <c>ArchiveUI</c>）的探针。
    ///
    /// <para><b>为什么要它：</b>档案的索引页（<c>IndexArchivePage</c>）里已经有一套原生的
    /// 「搜索框 + 可多选条目列表 + 条目列表」，而进度页正好需要这套东西 ——
    /// 但档案页是 <c>MainScenePage</c>（平时 <c>SetActive(false)</c>），看不到摸不到，
    /// 结构只能从游戏源码（<c>_Scripts/0.7.0 .../MVZ2/Archive/</c>）推。
    /// 真正决定能不能复用、怎么复用的是**运行时尺寸与布局契约**，所以这里把它们打出来。</para>
    ///
    /// <para>源码事实（<c>ArchiveController.UpdateIndex</c> / <c>UpdateFilteredTalks</c>）：</para>
    /// <list type="bullet">
    /// <item><c>IndexArchivePage.searchInputField</c>：<c>TMP_InputField</c>，只有 <c>onEndEdit</c> 事件；</item>
    /// <item><c>tagsList</c>：<c>ElementList</c>，模板是 <c>ArchiveTagItem</c>（<c>Toggle</c> + 文字），
    /// **多选**（每个条目独立开关）；</item>
    /// <item><c>talksList</c>：<c>ElementList</c>，模板是 <c>ArchiveTalkItem</c>（<c>Button</c> + 文字），单选；</item>
    /// <item>搜索的过滤逻辑在 <c>ArchiveController</c> 里（<c>name.Contains(searchPattern)</c>），
    /// 不在控件里 —— 所以我们自己克隆控件时，过滤由我们自己实现。</item>
    /// </list>
    /// </summary>
    internal static class ArchiveProbe
    {
        private static ArchiveUI _cached;
        private static bool _searched;

        /// <summary>档案 UI（含未激活对象；找不到返回 null）。</summary>
        public static ArchiveUI Find()
        {
            if (UiFinder.IsAlive(_cached))
                return _cached;
            if (_searched && _cached == null)
            {
                // 已经找过一轮且没找到：再试一次（场景切换后可能会变），但不再刷日志。
                var again = UiFinder.FindOne<ArchiveUI>();
                _cached = again;
                return again;
            }
            _searched = true;
            try
            {
                _cached = UiFinder.FindOne<ArchiveUI>();
                Plugin.Log?.LogInfo("[Archive] " + (_cached == null
                    ? "场景里找不到 ArchiveUI（档案页可能没随场景加载）"
                    : "找到 ArchiveUI：" + UiFinder.PathOf(_cached.transform)));
            }
            catch (Exception e)
            {
                Plugin.LogOnce("archive-find", "[Archive] 查找 ArchiveUI 失败：" + e.GetType().Name + ": " + e.Message);
                _cached = null;
            }
            return _cached;
        }

        /// <summary>档案索引页（找不到返回 null）。</summary>
        public static IndexArchivePage Index()
        {
            var ui = Find();
            if (ui == null)
                return null;
            try
            {
                var page = ui.indexUI;
                return UiFinder.IsAlive(page) ? page : null;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("archive-index", "[Archive] 取 indexUI 失败：" + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>搜索框（<c>TMP_InputField</c>），找不到返回 null。</summary>
        public static TMP_InputField SearchField()
        {
            var page = Index();
            if (page == null)
                return null;
            try
            {
                var field = page.searchInputField;
                return UiFinder.IsAlive(field) ? field : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>左侧那个**可多选**的条目列表（标签列表），找不到返回 null。</summary>
        public static ElementList TagsList()
        {
            var page = Index();
            if (page == null)
                return null;
            try
            {
                var list = page.tagsList;
                return UiFinder.IsAlive(list) ? list : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 搜索框所在的那一整块（<c>Center/Filter/Contents/Search</c>，含底图与占位符）。
        /// 克隆它就能得到一个功能完好的搜索框（<c>TMP_InputField</c> 在里面）。
        /// </summary>
        public static GameObject SearchNode()
        {
            return NodeAt("Center/Filter/Contents/Search");
        }

        /// <summary>
        /// 搜索框那个输入框节点本体（<c>Center/Filter/Contents/Search/InputField</c>）。
        ///
        /// <para>为什么克隆它而不是克隆外层的 <c>Search</c>：外层 <c>Search</c> 是个竖向布局组、
        /// 自身没有 <c>LayoutElement</c>，它里面那个「标题」的 <c>TMP</c> 与输入框的高度都得靠
        /// 档案页自己的布局链算出来；而克隆出来的子树已经脱离那条链，高度会算成 0。
        /// 直接拿输入框本休，就能用我们自己的尺寸契约（与面板里其它输入框同一套）。</para>
        /// </summary>
        public static GameObject SearchFieldNode()
        {
            return NodeAt("Center/Filter/Contents/Search/InputField");
        }

        /// <summary>
        /// 左侧列表的**可滚动容器**（<c>Center/Filter/Contents/Tags/Scroll View</c>）。
        /// 克隆它 = 拿到滚动 + 视口 + Content（Content 上挂着 <c>ElementList</c>，
        /// 模板是它自己的子节点 <c>TagItem</c> —— 克隆时 Unity 会把内部引用重定向到克隆体）。
        /// </summary>
        public static GameObject TagsScrollView()
        {
            return NodeAt("Center/Filter/Contents/Tags/Scroll View");
        }

        private static GameObject NodeAt(string path)
        {
            var page = Index();
            if (page == null)
                return null;
            try
            {
                var t = UiFinder.FindByPath(page.transform, path);
                return t == null ? null : t.gameObject;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("archive-path-" + path, "[Archive] 按路径找节点失败（" + path + "）：" + e.GetType().Name);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // 诊断
        // ------------------------------------------------------------------

        public static string Dump()
        {
            var sb = new StringBuilder(4000);
            sb.Append("===== 对话档案（ArchiveUI）=====\n");

            var ui = Find();
            if (ui == null)
            {
                sb.Append("未找到 ArchiveUI —— 无法复用它的搜索框与列表。\n");
                return sb.ToString();
            }

            sb.Append("ArchiveUI = ").Append(UiFinder.PathOf(ui.transform)).Append('\n');

            var page = Index();
            if (page == null)
            {
                sb.Append("indexUI 取不到（字段为空）。\n");
                return sb.ToString();
            }

            var field = SearchField();
            sb.Append("搜索框     = ").Append(field == null ? "（无）" : UiFinder.PathOf(field.transform))
              .Append("  ").Append(RectOf(field == null ? null : field.gameObject)).Append('\n');

            DumpList(sb, "标签列表(左/多选)", TagsList(), true);
            DumpList(sb, "对话列表(右/单选)", TalksListSafe(), false);

            sb.Append("\n--- 索引页子树 ---\n");
            DumpSubtree(sb, page.gameObject, 0, 5);

            var tags = TagsList();
            if (tags != null)
            {
                var tpl = TemplateOf(tags);
                if (tpl != null)
                {
                    sb.Append("\n--- 标签列表的条目模板 ---\n");
                    DumpSubtree(sb, tpl, 0, 3);
                }
            }

            var search = SearchField();
            if (search != null)
            {
                sb.Append("\n--- 搜索框子树 ---\n");
                DumpSubtree(sb, search.gameObject, 0, 3);
            }
            return sb.ToString();
        }

        private static ElementList TalksListSafe()
        {
            var page = Index();
            if (page == null)
                return null;
            try
            {
                var list = page.talksList;
                return UiFinder.IsAlive(list) ? list : null;
            }
            catch (Exception) { return null; }
        }

        private static GameObject TemplateOf(ElementList list)
        {
            try
            {
                var tpl = list.getTemplate();
                return UiFinder.IsAlive(tpl) ? tpl : null;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("archive-tpl", "[Archive] 取列表模板失败：" + e.GetType().Name);
                return null;
            }
        }

        private static void DumpList(StringBuilder sb, string label, ElementList list, bool isTag)
        {
            if (list == null)
            {
                sb.Append(label).Append(" = （无）\n");
                return;
            }
            try
            {
                var tpl = TemplateOf(list);
                var compCount = 0;
                if (tpl != null)
                {
                    // 用泛型逐个探：il2cpp 里 GetComponent(System.Type) 收的是 Il2CppSystem.Type，
                    // 托管 Type 传不进去。
                    compCount = isTag
                        ? (tpl.GetComponent<ArchiveTagItem>() == null ? 0 : 1)
                        : (tpl.GetComponent<ArchiveTalkItem>() == null ? 0 : 1);
                }
                sb.Append(label).Append(" = ").Append(UiFinder.PathOf(list.transform))
                  .Append("  ").Append(RectOf(list.gameObject))
                  .Append("  项数=").Append(list.Count)
                  .Append("  pooled=").Append(list.pooled ? 1 : 0)
                  .Append('\n');
                sb.Append("    列表根 = ").Append(list.ListRoot == null ? "（无）" : UiFinder.PathOf(list.ListRoot))
                  .Append('\n');
                sb.Append("    模板   = ").Append(tpl == null ? "（无）" : tpl.name)
                  .Append("  条目组件=").Append(compCount == 1 ? "OK" : "缺")
                  .Append("  ").Append(RectOf(tpl))
                  .Append('\n');
            }
            catch (Exception e)
            {
                sb.Append(label).Append(" 读取失败：").Append(e.GetType().Name).Append('\n');
            }
        }

        private static void DumpSubtree(StringBuilder sb, GameObject go, int depth, int maxDepth)
        {
            if (!UiFinder.IsAlive(go) || depth > maxDepth)
                return;
            try
            {
                var rt = go.transform.TryCast<RectTransform>();
                if (rt == null)
                    return;
                var pad = new StringBuilder();
                for (var i = 0; i < depth; i++)
                    pad.Append("  ");

                sb.Append("  ").Append(pad).Append(Pad(go.name, 22))
                  .Append(go.activeSelf ? "on " : "off")
                  .Append(RectOf(go))
                  .Append("  ").Append(Contracts(go))
                  .Append('\n');

                for (var i = 0; i < rt.childCount; i++)
                    DumpSubtree(sb, rt.GetChild(i).gameObject, depth + 1, maxDepth);
            }
            catch (Exception) { }
        }

        private static string RectOf(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return "（无）";
            try
            {
                var rt = go.transform.TryCast<RectTransform>();
                if (rt == null)
                    return "（非 RectTransform）";
                var r = PanelDump.ScreenRect(rt);
                return Mathf.RoundToInt(rt.rect.width) + "x" + Mathf.RoundToInt(rt.rect.height)
                     + " 屏[" + Mathf.RoundToInt(r.xMin) + "," + Mathf.RoundToInt(r.yMin)
                     + " → " + Mathf.RoundToInt(r.xMax) + "," + Mathf.RoundToInt(r.yMax) + "]";
            }
            catch (Exception)
            {
                return "（读取失败）";
            }
        }

        /// <summary>布局契约摘要：布局组 / LayoutElement / 适配器 / 滚动视图 / 特殊组件。</summary>
        private static string Contracts(GameObject go)
        {
            var sb = new StringBuilder(110);
            try
            {
                var lg = go.GetComponent<LayoutGroup>();
                if (lg == null)
                {
                    sb.Append("--");
                }
                else
                {
                    sb.Append(ShortTypeName(lg));
                    // padding / spacing / 子节点控制：对齐问题的答案全在这几个数里。
                    try
                    {
                        var pad = lg.padding;
                        sb.Append("(pad ").Append(pad.left).Append(',').Append(pad.right)
                          .Append(',').Append(pad.top).Append(',').Append(pad.bottom).Append(')');
                    }
                    catch (Exception) { }
                    try
                    {
                        var hv = lg.TryCast<HorizontalOrVerticalLayoutGroup>();
                        if (hv != null)
                        {
                            sb.Append("[cw=").Append(hv.childControlWidth ? 1 : 0)
                              .Append(" ch=").Append(hv.childControlHeight ? 1 : 0)
                              .Append(" fw=").Append(hv.childForceExpandWidth ? 1 : 0)
                              .Append(" fh=").Append(hv.childForceExpandHeight ? 1 : 0)
                              .Append(" sp=").Append(hv.spacing).Append(']');
                        }
                    }
                    catch (Exception) { }
                }

                var le = go.GetComponent<LayoutElement>();
                sb.Append(le == null
                    ? " LE=无"
                    : " LE " + le.preferredWidth.ToString("0.#") + "/" + le.flexibleWidth.ToString("0.#")
                        + " " + le.preferredHeight.ToString("0.#") + "/" + le.flexibleHeight.ToString("0.#"));

                var fitter = go.GetComponent<ContentSizeFitter>();
                if (fitter != null)
                    sb.Append(" CSF=").Append((int)fitter.horizontalFit).Append('/').Append((int)fitter.verticalFit);

                if (go.GetComponent<ScrollRect>() != null)
                    sb.Append(" [ScrollRect]");
                if (go.GetComponent<RectMask2D>() != null)
                    sb.Append(" [RectMask2D]");
                if (go.GetComponent<Mask>() != null)
                    sb.Append(" [Mask]");
                if (go.GetComponent<ElementList>() != null)
                    sb.Append(" [ElementList]");
                if (go.GetComponent<Toggle>() != null)
                    sb.Append(" [Toggle]");
                if (go.GetComponent<Button>() != null)
                    sb.Append(" [Button]");
                if (go.GetComponent<TMP_InputField>() != null)
                    sb.Append(" [InputField]");

                var tmp = go.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    sb.Append(" [TMP ").Append(tmp.fontSize.ToString("0.#"))
                      .Append(tmp.enableAutoSizing ? " auto" : "")
                      .Append(" 需要=").Append(Mathf.RoundToInt(Need(tmp))).Append(']');
                }
            }
            catch (Exception) { }
            return sb.ToString();
        }

        private static float Need(TextMeshProUGUI tmp)
        {
            try
            {
                return string.IsNullOrEmpty(tmp.text) ? 0f : tmp.GetPreferredValues(tmp.text).x;
            }
            catch (Exception) { return 0f; }
        }

        private static string ShortTypeName(object obj)
        {
            try
            {
                // 用托管代理类的名字（interop 代理类名 = il2cpp 里的类型名）。
                var name = obj.GetType().Name;
                return name.Replace("HorizontalOrVerticalLayoutGroup", "HVLG")
                           .Replace("LayoutGroup", "LG");
            }
            catch (Exception) { return "LG?"; }
        }

        private static string Pad(string text, int width)
        {
            var s = text ?? string.Empty;
            return s.Length >= width ? s : s + new string(' ', width - s.Length);
        }
    }
}
