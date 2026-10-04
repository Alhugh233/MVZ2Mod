using System;
using System.IO;
using System.Text;
using DSHCore.Ui;
using MVZ2.Blueprints;
using MVZ2.Level.Artifacts;
using MVZ2.Level.Blueprints;
using MVZ2.UI;
using PVZEngine.Tools.Unity;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 排障用：把**原版选卡面板**（<see cref="LevelUIBlueprintChoose"/>）的运行时层级整棵打出来，
    /// 直接写到 <c>BepInEx\_choose_probe.txt</c>。
    ///
    /// <para>为什么要它：面板 prefab 在 Addressables bundle 里，mod 侧看不到 prefab 文件；
    /// 「要克隆哪个节点、多大、挂着什么组件」只能从**活着的实例**上量。</para>
    ///
    /// <para>为什么写文件而不写日志：几百行日志混在 LogOutput 里很难扒，而且
    /// <c>Plugin.Log</c> 的每行调用会把上下文冲乱。</para>
    ///
    /// <para>顺带确认了一件大事：<b>Il2CppInterop 把 private 字段也生成了可访问属性</b>
    /// （<c>chosenBlueprints</c> / <c>pageList</c> / <c>maxCountPerPage</c> 在源码里都是 private
    /// <c>[SerializeField]</c>）—— 原版面板内部其实够得着。</para>
    /// </summary>
    internal static class ChooseProbe
    {
        public static string Dump()
        {
            var sb = new StringBuilder(20000);
            try
            {
                var controller = LevelFacade.Controller;
                if (controller == null)
                    return Fail("（不在关卡内）");
                var preset = controller.GetUIPreset();
                if (preset == null)
                    return Fail("（拿不到 UIPreset）");
                var choose = preset.BlueprintChoose;
                if (choose == null)
                    return Fail("（拿不到 BlueprintChoose）");

                sb.Append("===== 原版选卡面板层级 =====\n");
                sb.Append("根路径：").Append(UiFinder.PathOf(choose.transform)).Append('\n');
                Walk(sb, choose.transform, 0, 7);

                sb.Append("\n--- 关键子对象 ---\n");
                Slot(sb, "chosenBlueprints", choose.chosenBlueprints);
                Slot(sb, "blueprintChoosePanel", choose.blueprintChoosePanel);
                Slot(sb, "commandBlockChoosePanel", choose.commandBlockChoosePanel);
                Node(sb, "choosingBlueprintRoot", choose.choosingBlueprintRoot);
                Node(sb, "runtimeBlueprintRoot", choose.runtimeBlueprintRoot);

                sb.Append("\n--- BlueprintDisplayer ---\n");
                var displayers = choose.GetComponentsInChildren<BlueprintDisplayer>(true);
                sb.Append("实例数=").Append(displayers == null ? 0 : displayers.Length).Append('\n');
                for (var i = 0; displayers != null && i < displayers.Length; i++)
                    Displayer(sb, displayers[i]);

                sb.Append("\n--- 已选蓝图行（BlueprintList） ---\n");
                var list = choose.chosenBlueprints;
                if (list != null)
                {
                    sb.Append("  类型=").Append(list.GetType().Name)
                      .Append("  项数=").Append(list.GetBlueprintCount())
                      .Append("  水平=").Append(list.horizontal).Append('\n');
                    var first = list.GetBlueprintAt(0);
                    sb.Append("  首项=").Append(UiFinder.IsAlive(first == null ? null : first.gameObject)
                        ? first.gameObject.name : "（无）").Append('\n');
                }

                sb.Append("\n--- 卡片模板（Page 里的 Template） ---\n");
                DumpCardTemplate(sb, choose);
            }
            catch (Exception e)
            {
                sb.Append("dump 失败：").Append(e.GetType().Name).Append(": ").Append(e.Message)
                  .Append('\n').Append(e.StackTrace).Append('\n');
            }
            Write(sb.ToString());
            return "已写 BepInEx\\_choose_probe.txt（" + sb.Length + " 字符）";
        }

        private static string Fail(string reason)
        {
            Write(reason);
            return reason;
        }

        // ------------------------------------------------------------------
        // 「命令方块选卡面板」的专项探针
        // ------------------------------------------------------------------

        /// <summary>
        /// 把**「命令方块选卡面板」**（<c>CommandBlockPanelStandalone</c>，我们克隆的那一块）整棵打出来，
        /// 外加原版那条「蓝图飞入」动画节点与几个关键类型的 API 名单。
        ///
        /// <para>要它解决的三个问题：<br/>
        /// ① 面板自带的标题行到底在哪、多大（我们要把镜像框放到那个位置）；<br/>
        /// ② 翻页那一排的几何（确认/取消要跟它同排）；<br/>
        /// ③ 原版「蓝图飞进已选条」的动画是谁在驱动（<c>MovingBlueprints</c> 是
        /// <c>BlueprintChoose</c> 的子节点，**不在我们克隆的那棵子树里**，得自己接）。</para>
        /// </summary>
        public static string DumpPicker()
        {
            var sb = new StringBuilder(40000);
            try
            {
                var src = BlueprintUi.Source();
                if (src == null)
                    return Fail2("（拿不到 BlueprintChoose）");
                var pick = src.commandBlockChoosePanel;
                if (pick == null)
                    return Fail2("（拿不到 CommandBlockChoosePanel）");

                sb.Append("===== 原版「命令方块选卡面板」全树 =====\n");
                Walk(sb, pick.transform, 0, 12);

                sb.Append("\n--- 「飞入」动画节点 ---\n");
                Moving(sb, src);

                sb.Append("\n--- 节点上的组件 ---\n");
                Comps2(sb, "面板根", pick.gameObject);
                if (pick.displayer != null)
                    Comps2(sb, "网格", pick.displayer.gameObject);

                sb.Append("\n--- 面板视图里的关键引用 ---\n");
                Ref(sb, "displayer", pick.displayer == null ? null : pick.displayer.gameObject);
                Ref(sb, "cancelButton", pick.cancelButton == null ? null : pick.cancelButton.gameObject);

                sb.Append("\n--- API 名单 ---\n");
                Api(sb, "LevelUIBlueprintChoose", typeof(LevelUIBlueprintChoose));
                Api(sb, "CommandBlockChoosePanel", typeof(CommandBlockChoosePanel));
                Api(sb, "BlueprintDisplayerStandalone", typeof(BlueprintDisplayerStandalone));
                Api(sb, "BlueprintDisplayerStandalonePage", typeof(BlueprintDisplayerStandalonePage));

                sb.Append("\n--- 动画相关类型 ---\n");
                SearchTypes(sb, "Moving");
                var bvd = FindType("BlueprintViewData");
                if (bvd != null)
                {
                    Api(sb, "BlueprintViewData", bvd);
                    MemberTypes(sb, "BlueprintViewData", bvd);
                }
                var cbvd = FindType("ChoosingBlueprintViewData");
                if (cbvd != null)
                    MemberTypes(sb, "ChoosingBlueprintViewData", cbvd);
                var view = FindType("BlueprintChoosingViewData");
                if (view != null)
                    Api(sb, "BlueprintChoosingViewData", view);
                var mb = FindType("MovingBlueprint");
                if (mb != null)
                    Api(sb, "MovingBlueprint", mb);
                var mbs = FindType("MovingBlueprintCollection");
                if (mbs != null)
                    Api(sb, "MovingBlueprintCollection", mbs);
            }
            catch (Exception e)
            {
                sb.Append("dump 失败：").Append(e.GetType().Name).Append(": ").Append(e.Message)
                  .Append('\n').Append(e.StackTrace).Append('\n');
            }
            Write2(sb.ToString());
            return "已写 BepInEx\\_pick_probe.txt（" + sb.Length + " 字符）";
        }

        private static string Fail2(string reason)
        {
            Write2(reason);
            return reason;
        }

        private static void Write2(string text)
        {
            try
            {
                var path = Path.Combine(Environment.CurrentDirectory, "BepInEx", "_pick_probe.txt");
                File.WriteAllText(path, text, new UTF8Encoding(false));
                Plugin.Log?.LogInfo("[ChooseProbe] 已写 " + path);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ChooseProbe] 写文件失败：" + e.Message);
            }
        }

        /// <summary>原版「蓝图从网格飞进已选条」那套节点。</summary>
        private static void Moving(StringBuilder sb, LevelUIBlueprintChoose src)
        {
            try
            {
                var root = src.transform;
                var moving = UiFinder.FindByPath(root, "MovingBlueprints");
                if (moving == null)
                {
                    // 可能挂在别的层级上 → 全树搜一次。
                    var all = src.GetComponentsInChildren<Transform>(true);
                    for (var i = 0; all != null && i < all.Length; i++)
                    {
                        if (all[i] != null && all[i].name.Contains("Moving"))
                        {
                            moving = all[i];
                            break;
                        }
                    }
                }
                if (moving == null || !UiFinder.IsAlive(moving.gameObject))
                {
                    sb.Append("  （没有 Moving* 节点）\n");
                    return;
                }
                Walk(sb, moving, 1, 6);
                sb.Append("  父节点=").Append(PathOf(moving.parent)).Append('\n');
                var kids = moving.GetComponentsInChildren<Transform>(true);
                for (var i = 0; kids != null && i < kids.Length; i++)
                {
                    if (kids[i] == null || kids[i] == moving)
                        continue;
                    sb.Append("  子节点 ").Append(kids[i].name).Append(" → ")
                      .Append(CompsAll(kids[i].gameObject)).Append('\n');
                }
            }
            catch (Exception e)
            {
                sb.Append("  动画节点 dump 失败：").Append(e.GetType().Name).Append('\n');
            }
        }

        private static void Comps2(StringBuilder sb, string label, GameObject go)
        {
            sb.Append("  ").Append(label).Append(" = ").Append(go.name).Append(" → ")
              .Append(CompsAll(go)).Append('\n');
        }

        /// <summary>节点上的**全部**组件（这个环境里 AddComponent 造不出原版控件，所以要把名字看全）。</summary>
        private static string CompsAll(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return "（无）";
            try
            {
                var list = go.GetComponents<Component>();
                var sb = new StringBuilder(90);
                for (var i = 0; list != null && i < list.Length; i++)
                {
                    if (list[i] == null)
                        continue;
                    if (sb.Length > 0)
                        sb.Append(',');
                    sb.Append(RealName(list[i]));
                }
                return sb.Length == 0 ? "（无）" : sb.ToString();
            }
            catch (Exception e)
            {
                return "（" + e.GetType().Name + "）";
            }
        }

        /// <summary>
        /// 组件的**真实 il2cpp 类名**。
        ///
        /// <para><c>GetComponents&lt;Component&gt;()</c> 返回的元素是按**声明类型**（<c>Component</c>）
        /// 包装的，所以 <c>GetType().Name</c> 一律是 "Component"、什么也认不出来
        /// （实测：卡片上那堆组件全打印成 "Component"）。真实名字只能回到底层去问。</para>
        /// </summary>
        private static string RealName(Component c)
        {
            try
            {
                if (c == null || c.Pointer == IntPtr.Zero)
                    return "（空）";
                var klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(c.Pointer);
                if (klass == IntPtr.Zero)
                    return c.GetType().Name;
                // ⚠️ il2cpp_class_get_name 返回的是 IntPtr（原生 char*），不是 string。
                var ptr = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(klass);
                var name = ptr == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr);
                return string.IsNullOrEmpty(name) ? c.GetType().Name : name;
            }
            catch (Exception)
            {
                return "（?）";
            }
        }

        /// <summary>在**已加载的程序集**里按名字找类型（interop 代理类型也能反射出全部成员）。</summary>
        private static Type FindType(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName))
                return null;
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                for (var i = 0; i < asms.Length; i++)
                {
                    Type[] types;
                    try { types = asms[i].GetTypes(); }
                    catch (Exception) { continue; }
                    for (var j = 0; types != null && j < types.Length; j++)
                    {
                        if (types[j] != null && types[j].Name == simpleName)
                            return types[j];
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>所有名字里带 <paramref name="fragment"/> 的类型（找动画组件用）。</summary>
        private static void SearchTypes(StringBuilder sb, string fragment)
        {
            sb.Append("  名字含「").Append(fragment).Append("」的类型：");
            try
            {
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                var first = true;
                for (var i = 0; i < asms.Length; i++)
                {
                    Type[] types;
                    try { types = asms[i].GetTypes(); }
                    catch (Exception) { continue; }
                    for (var j = 0; types != null && j < types.Length; j++)
                    {
                        var t = types[j];
                        if (t == null || t.Name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        if (!first)
                            sb.Append(", ");
                        first = false;
                        sb.Append(t.FullName);
                    }
                }
                if (first)
                    sb.Append("（没有）");
                sb.Append('\n');
            }
            catch (Exception e)
            {
                sb.Append("搜不了：").Append(e.GetType().Name).Append('\n');
            }
        }

        private static void Ref(StringBuilder sb, string label, GameObject go)
        {
            sb.Append("  ").Append(label).Append(" = ").Append(PathOf(go == null ? null : go.transform)).Append('\n');
        }

        /// <summary>一个类型的字段 / 属性（含类型名）—— 找「贴图/模型放在哪个字段里」用。</summary>
        private static void MemberTypes(StringBuilder sb, string label, Type type)
        {
            sb.Append("  【").Append(label).Append(" 成员类型】\n");
            try
            {
                const System.Reflection.BindingFlags F =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.DeclaredOnly;

                var fields = type.GetFields(F);
                sb.Append("    字段：");
                for (var i = 0; fields != null && i < fields.Length; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(fields[i].Name).Append(':').Append(fields[i].FieldType.Name);
                }
                sb.Append('\n');

                var props = type.GetProperties(F);
                sb.Append("    属性：");
                for (var i = 0; props != null && i < props.Length; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(props[i].Name).Append(':').Append(props[i].PropertyType.Name);
                }
                sb.Append('\n');
            }
            catch (Exception e)
            {
                sb.Append("    反射失败：").Append(e.GetType().Name).Append('\n');
            }
        }

        /// <summary>一个类型的字段 / 属性 / 方法名单（interop 代理类型也能反射）。</summary>
        private static void Api(StringBuilder sb, string label, Type type)
        {
            sb.Append("  【").Append(label).Append("】\n");
            try
            {
                const System.Reflection.BindingFlags F =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;

                var fields = type.GetFields(F);
                sb.Append("    字段：");
                for (var i = 0; fields != null && i < fields.Length; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(fields[i].Name);
                }
                sb.Append('\n');

                var props = type.GetProperties(F);
                sb.Append("    属性：");
                for (var i = 0; props != null && i < props.Length; i++)
                {
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(props[i].Name);
                }
                sb.Append('\n');

                var methods = type.GetMethods(F);
                sb.Append("    方法：");
                var first = true;
                for (var i = 0; methods != null && i < methods.Length; i++)
                {
                    var m = methods[i];
                    var n = m.Name;
                    if (n.StartsWith("get_") || n.StartsWith("set_")
                        || n.StartsWith("add_") || n.StartsWith("remove_"))
                        continue;
                    if (!first)
                        sb.Append(", ");
                    first = false;
                    sb.Append(n).Append('(');
                    var ps = m.GetParameters();
                    for (var j = 0; ps != null && j < ps.Length; j++)
                    {
                        if (j > 0)
                            sb.Append(',');
                        sb.Append(ps[j].ParameterType.Name);
                    }
                    sb.Append(')');
                }
                sb.Append('\n');
            }
            catch (Exception e)
            {
                sb.Append("    反射失败：").Append(e.GetType().Name).Append('\n');
            }
        }

        private static void Write(string text)
        {
            try
            {
                var path = Path.Combine(Environment.CurrentDirectory, "BepInEx", "_choose_probe.txt");
                File.WriteAllText(path, text, new UTF8Encoding(false));
                Plugin.Log?.LogInfo("[ChooseProbe] 已写 " + path);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[ChooseProbe] 写文件失败：" + e.Message);
            }
        }

        /// <summary>卡片的实际模板节点（Page/Template）——「显示效果与掉落物一致」要靠它的组件。</summary>
        private static void DumpCardTemplate(StringBuilder sb, LevelUIBlueprintChoose choose)
        {
            try
            {
                var displayers = choose.GetComponentsInChildren<BlueprintDisplayer>(true);
                for (var i = 0; displayers != null && i < displayers.Length; i++)
                {
                    var standalone = displayers[i] == null ? null : displayers[i].TryCast<BlueprintDisplayerStandalone>();
                    if (standalone == null || standalone.pageList == null)
                        continue;
                    var pageTpl = standalone.pageList.getTemplate();
                    if (pageTpl == null)
                        continue;
                    var page = pageTpl.GetComponent<BlueprintDisplayerStandalonePage>();
                    if (page == null || page.blueprintList == null)
                        continue;
                    var cardTpl = page.blueprintList.getTemplate();
                    if (cardTpl == null)
                        continue;
                    sb.Append("  卡片模板=").Append(cardTpl.name)
                      .Append("  路径=").Append(UiFinder.PathOf(cardTpl)).Append('\n');
                    Walk(sb, cardTpl, 2, 5);
                    return;
                }
            }
            catch (Exception e)
            {
                sb.Append("  卡片模板 dump 失败：").Append(e.GetType().Name).Append('\n');
            }
        }

        private static void Displayer(StringBuilder sb, BlueprintDisplayer d)
        {
            if (d == null)
                return;
            sb.Append("  ").Append(d.GetType().Name)
              .Append("  路径=").Append(UiFinder.PathOf(d.transform)).Append('\n');
            var standalone = d.TryCast<BlueprintDisplayerStandalone>();
            if (standalone == null)
                return;
            sb.Append("     maxCountPerPage=").Append(standalone.maxCountPerPage)
              .Append("  currentPage=").Append(standalone.currentPage)
              .Append("  maxPages=").Append(standalone.maxPages).Append('\n');
            sb.Append("     pageText=").Append(PathOf(standalone.pageText == null ? null : standalone.pageText.gameObject))
              .Append("  上一页=").Append(PathOf(standalone.previousPageButton == null ? null : standalone.previousPageButton.gameObject))
              .Append("  下一页=").Append(PathOf(standalone.nextPageButton == null ? null : standalone.nextPageButton.gameObject))
              .Append("  pageRoot=").Append(PathOf(standalone.pageRoot)).Append('\n');
            var pages = standalone.pageList;
            sb.Append("     pageList 项数=").Append(pages == null ? -1 : pages.count)
              .Append("  模板=").Append(pages == null || pages.getTemplate() == null ? "-" : pages.getTemplate().gameObject.name)
              .Append('\n');
        }

        private static string PathOf(GameObject go)
        {
            return UiFinder.IsAlive(go) ? UiFinder.PathOf(go.transform) : "（无）";
        }

        private static string PathOf(Transform t)
        {
            return t == null ? "（无）" : UiFinder.PathOf(t);
        }

        private static void Node(StringBuilder sb, string label, GameObject go)
        {
            sb.Append("  ").Append(label).Append(" = ");
            if (!UiFinder.IsAlive(go))
            {
                sb.Append("（无）\n");
                return;
            }
            sb.Append(go.name).Append("  路径=").Append(UiFinder.PathOf(go.transform));
            Info(sb, go, 0);
            sb.Append('\n');
        }

        private static void Slot(StringBuilder sb, string label, Component comp)
        {
            sb.Append("  ").Append(label).Append(" = ");
            if (comp == null || !UiFinder.IsAlive(comp.gameObject))
            {
                sb.Append("（无）\n");
                return;
            }
            sb.Append(comp.GetType().Name).Append("  节点=").Append(comp.gameObject.name)
              .Append("  路径=").Append(UiFinder.PathOf(comp.transform));
            Info(sb, comp.gameObject, 0);
            sb.Append('\n');
        }

        /// <summary>逐层走一遍（**用 Transform 递归** —— 上层容器可能根本没有 RectTransform）。</summary>
        private static void Walk(StringBuilder sb, Transform tr, int depth, int maxDepth)
        {
            if (tr == null || depth > maxDepth)
                return;
            try
            {
                var go = tr.gameObject;
                sb.Append(new string(' ', depth * 2)).Append(go.name)
                  .Append(go.activeSelf ? " on" : " (off)");
                Info(sb, go, depth);
                sb.Append("  |").Append(Comps(go)).Append('\n');
                for (var i = 0; i < tr.childCount; i++)
                    Walk(sb, tr.GetChild(i), depth + 1, maxDepth);
            }
            catch (Exception e)
            {
                sb.Append(new string(' ', depth * 2)).Append("走不下去：").Append(e.GetType().Name).Append('\n');
            }
        }

        /// <summary>尺寸 / 锚点 / 布局契约。非 RectTransform 的节点只报类型。</summary>
        private static void Info(StringBuilder sb, GameObject go, int depth)
        {
            var rt = go.transform.TryCast<RectTransform>();
            if (rt == null)
            {
                sb.Append("  [非 RectTransform: ").Append(go.transform.GetType().Name).Append(']');
                return;
            }
            sb.Append("  ").Append(Mathf.RoundToInt(rt.rect.width)).Append('x').Append(Mathf.RoundToInt(rt.rect.height))
              .Append(" 锚[").Append(rt.anchorMin.x.ToString("0.##")).Append(',').Append(rt.anchorMin.y.ToString("0.##"))
              .Append("~").Append(rt.anchorMax.x.ToString("0.##")).Append(',').Append(rt.anchorMax.y.ToString("0.##"))
              .Append("] 轴(").Append(rt.pivot.x.ToString("0.##")).Append(',').Append(rt.pivot.y.ToString("0.##"))
              .Append(") 位(").Append(Mathf.RoundToInt(rt.anchoredPosition.x)).Append(',')
              .Append(Mathf.RoundToInt(rt.anchoredPosition.y)).Append(')');
        }

        /// <summary>节点上认得出的组件（逐个类型 GetComponent，比 GetComponents&lt;Component&gt; 稳）。</summary>
        private static string Comps(GameObject go)
        {
            var sb = new StringBuilder(80);
            Add<Image>(go, sb, "Image");
            Add<Button>(go, sb, "Button");
            Add<Toggle>(go, sb, "Toggle");
            Add<TextMeshProUGUI>(go, sb, "TMP");
            Add<ScrollRect>(go, sb, "ScrollRect");
            Add<RectMask2D>(go, sb, "RectMask2D");
            Add<Mask>(go, sb, "Mask");
            Add<GridLayoutGroup>(go, sb, "Grid");
            Add<HorizontalLayoutGroup>(go, sb, "HLayout");
            Add<VerticalLayoutGroup>(go, sb, "VLayout");
            Add<LayoutElement>(go, sb, "LE");
            Add<ContentSizeFitter>(go, sb, "Fitter");
            Add<ElementListUI>(go, sb, "ElementListUI");
            Add<Blueprint>(go, sb, "Blueprint");
            Add<ArtifactSlot>(go, sb, "ArtifactSlot");
            Add<ArtifactItemUI>(go, sb, "ArtifactItemUI");
            Add<BlueprintDisplayer>(go, sb, "Displayer");
            Add<BlueprintDisplayerStandalonePage>(go, sb, "DisplayerPage");
            Add<BlueprintSet>(go, sb, "BlueprintSet");
            Add<BlueprintChoosePanel>(go, sb, "ChoosePanel");
            Add<CommandBlockChoosePanel>(go, sb, "CBChoosePanel");
            Add<CommandBlockSlot>(go, sb, "CommandBlockSlot");
            if (sb.Length > 0)
                sb.Length -= 1;
            return sb.ToString();
        }

        private static void Add<T>(GameObject go, StringBuilder sb, string label) where T : Component
        {
            try
            {
                var c = go.GetComponent<T>();
                if (c != null)
                    sb.Append(label).Append(',');
            }
            catch (Exception) { }
        }
    }
}
