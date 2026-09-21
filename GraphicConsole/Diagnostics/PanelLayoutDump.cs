using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GraphicConsole
{
    /// <summary>
    /// 把面板的**实际布局**打进日志（名字 / 显隐 / 锚点 / 位置 / 尺寸 / 屏幕坐标 / 图形状态 / 控件类型与文字）。
    ///
    /// 为什么需要它：面板是"克隆现成节点 + 现造布局容器"拼出来的，视觉问题
    /// （文字挤在一起、控件跑到屏幕外、下拉框弹窗位置不对、图不显示）从代码上看不出来，
    /// 必须看运行时真实的 RectTransform 与 Graphic 状态。日志比截图更好检索，也能直接对照
    /// <see cref="GcLayout"/> 的数字。
    ///
    /// 用法：
    ///   · 配置 <c>General.SelfTest</c> 非空时自动 dump（见 <see cref="SelfTest"/>）；
    ///   · 或 <c>/gc dump</c> 手动打印当前面板布局。
    ///
    /// 注：<see cref="ProbeDropdown"/> / <see cref="LogProbeState"/> 里与 TMP 自带下拉弹窗有关的部分
    /// 是**历史诊断**留下的（当初就是靠它们查出“弹窗画不出来”），现在下拉格改用
    /// <see cref="PickerOverlay"/> 自绘，面板里已不再有 TMP_Dropdown，这些入口只会记录一句
    /// “找不到 / 浮层状态”，保留仅为以后需要时参照。
    /// </summary>
    internal static class PanelLayoutDump
    {
        public static void Dump(GameObject root, string note)
        {
            if (!UiFinder.IsAlive(root))
            {
                Log("[GcDump] 面板不存在，无法 dump（" + note + "）");
                return;
            }
            var sb = new StringBuilder();
            sb.Append("[GcDump] ===== 面板布局：").Append(note).Append(" =====\n");

            var canvas = FindCanvas(root.transform);
            var cam = CameraOf(canvas);
            var scale = canvas == null ? 1f : canvas.scaleFactor;
            sb.Append("[GcDump] 画布=").Append(canvas == null ? "未找到" : canvas.name)
                .Append("  scaleFactor=").Append(Num(scale))
                .Append("  屏幕=").Append(Screen.width).Append("x").Append(Screen.height)
                .Append("  面板缩放=").Append(Num(GcLayout.Scale))
                .Append('\n');
            sb.Append("[GcDump] 层级 | 显隐 | 锚点 | 轴心 | 位置 | 尺寸(设计) | 屏幕中心/屏幕尺寸 | 图形 | 内容\n");

            Walk(root.transform, 0, cam, sb);
            sb.Append("[GcDump] END\n");
            Log(sb.ToString());
        }

        /// <summary>
        /// dump **模板架**里的东西 —— 它们是我们从原版对话框/控件复制过来的**未经改动的副本**，
        /// 所以这是查看"原版结构到底长什么样"（例如对话框的框体美术挂在哪个节点上）最直接的手段。
        /// </summary>
        public static void DumpTemplateSources()
        {
            var holder = UiSources.Holder;
            if (!UiFinder.IsAlive(holder))
            {
                Log("[GcDump] 模板架不存在（UiSources.Prepare 失败？）");
                return;
            }
            var sb = new StringBuilder();
            sb.Append("[GcDump] ===== 原版模板原样（未改动副本）=====\n");
            var cam = CameraOf(FindCanvas(holder.transform));
            Walk(holder.transform, 0, cam, sb);
            sb.Append("[GcDump] END\n");
            Log(sb.ToString());
        }

        /// <summary>
        /// 程序化展开第一个下拉框，然后把它的子树 dump 出来。
        ///
        /// 用途：bug「下拉选择框点开后不显示UI，乱点能注意到功能有效」静态看不出来，
        /// 必须看展开后 TMP 实际生成的 "Dropdown List" 节点：位置、尺寸、Canvas.overrideSorting、
        /// sortingOrder、以及每一层的 Graphic 是否 enabled / 有没有 sprite。
        /// </summary>
        /// <param name="keepOpen">
        /// true = 展开后**不**收起（留给 <see cref="SelfTest"/> 截图看真实画面）。
        /// 布局 dump 只能证明节点存在、尺寸正确，"看得见 / 看不见"只有截图能定论。
        /// </param>
        public static void ProbeDropdown(GameObject root, string note, bool keepOpen = false)
        {
            if (!UiFinder.IsAlive(root))
                return;
            var dd = FindDropdown(root.transform);
            if (dd == null)
            {
                Log("[GcDump] 面板里找不到有选项的 TMP_Dropdown，跳下拉框探测（" + note + "）");
                return;
            }
            probed = dd;
            var sb = new StringBuilder();
            sb.Append("[GcDump] ===== 下拉框探测：").Append(note).Append(" =====\n");
            try
            {
                sb.Append("[GcDump] 目标=").Append(UiFinder.PathOf(dd.transform))
                    .Append(" value=").Append(dd.value)
                    .Append(" options=").Append(dd.options == null ? -1 : dd.options.Count)
                    .Append(" interactable=").Append(dd.interactable ? 1 : 0)
                    .Append(" isActiveAndEnabled=").Append(dd.isActiveAndEnabled ? 1 : 0)
                    .Append('\n');
                var template = dd.template;
                sb.Append("[GcDump] template=").Append(template == null ? "null" : UiFinder.PathOf(template))
                    .Append(template == null
                        ? "\n"
                        : " size=" + Num(template.sizeDelta.x) + "x" + Num(template.sizeDelta.y)
                          + " active=" + (template.gameObject.activeSelf ? 1 : 0) + "\n");
            }
            catch (Exception e)
            {
                sb.Append("[GcDump] 读下拉框状态失败：").Append(e.GetType().Name).Append(':').Append(e.Message).Append('\n');
            }

            try
            {
                dd.Show();
                sb.Append("[GcDump] 已调用 Show()，下面是展开后的完整子树：\n");
                var cam = CameraOf(FindCanvas(dd.transform));
                Walk(dd.transform, 1, cam, sb);
                DumpSortingInfo(sb, dd);
                sb.Append("[GcDump] END\n");
                Log(sb.ToString());
                sb = null;
                LogProbeState(note + " / Show 后");
            }
            catch (Exception e)
            {
                if (sb != null)
                    sb.Append("[GcDump] Show() 失败：").Append(e.GetType().Name).Append(':').Append(e.Message).Append('\n');
            }
            if (!keepOpen)
                HideProbe();
            if (sb != null)
            {
                sb.Append(keepOpen ? "[GcDump] 保持展开（不 Hide），留给截图\n" : "[GcDump] 已调用 Hide()\n");
                sb.Append("[GcDump] END\n");
                Log(sb.ToString());
            }
            LogProbeState(note + (keepOpen ? " / 保持展开" : " / Hide 后"));
        }

        private static TMP_Dropdown probed;

        /// <summary>排序层名（拿不到就返回问号）。</summary>
        private static string LayerName(int id)
        {
            try
            {
                var name = SortingLayer.IDToName(id);
                return string.IsNullOrEmpty(name) ? "?" : name;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        /// <summary>
        /// 打印与**渲染排序**有关的事实：
        ///   · 场景里都有哪些排序层（名字 / id / value）—— value 才是先后顺序；
        ///   · 从上到下的画布链（含 overrideSorting / sortingOrder / layer）。
        ///
        /// 为什么关心这个：TMP 展开下拉框时是自己 <c>AddComponent&lt;Canvas&gt;()</c>
        /// （排序层默认落到 Default），然后只改 <c>sortingOrder = 30000</c>。
        /// 如果面板所在的画布用的是**自定义排序层**（层序 > Default），那么弹窗画布
        /// 即便 sortingOrder 是 30000，仍然整个排在面板后面 —— 表现为
        /// 「弹窗节点健在、可点、但完全看不见」。
        /// </summary>
        public static void DumpSortingInfo(StringBuilder sb, TMP_Dropdown dd)
        {
            sb.Append("[GcDump] --- 排序层表 ---\n");
            try
            {
                var layers = SortingLayer.layers;
                for (var i = 0; i < layers.Length; i++)
                {
                    var l = layers[i];
                    sb.Append("[GcDump]   layer[").Append(i).Append("] name=").Append(l.name)
                        .Append(" id=").Append(l.id).Append(" value=").Append(l.value).Append('\n');
                }
            }
            catch (Exception e)
            {
                sb.Append("[GcDump]   读排序层失败：").Append(e.GetType().Name).Append('\n');
            }

            sb.Append("[GcDump] --- 画布链（自身 → 根）---\n");
            if (dd != null)
            {
                var t = dd.transform;
                var guard = 0;
                while (t != null && guard++ < 24)
                {
                    var c = t.GetComponent<Canvas>();
                    if (c != null)
                    {
                        sb.Append("[GcDump]   ").Append(UiFinder.PathOf(t))
                            .Append("  override=").Append(c.overrideSorting ? 1 : 0)
                            .Append(" order=").Append(c.sortingOrder)
                            .Append(" layer=").Append(LayerName(c.sortingLayerID))
                            .Append('(').Append(c.sortingLayerID).Append(')')
                            .Append(" mode=").Append(c.renderMode)
                            .Append(" cam=").Append(c.worldCamera == null ? "null" : c.worldCamera.name)
                            .Append(" plane=").Append(Num(c.planeDistance))
                            .Append(" display=").Append(c.targetDisplay)
                            .Append(" scale=").Append(Num(c.scaleFactor))
                            .Append('\n');
                    }
                    t = t.parent;
                }
            }
        }

        /// <summary>
        /// 直接问「弹窗这一刻到底在不在、活没活、画布拉不拉得出来」。
        ///
        /// 为什么要单独一个方法：像素差分只能证明「画面上没变化」，但分不清是
        ///   (a) 节点被销毁/失活，还是 (b) 节点健在却没渲染。
        /// 这两者的修法完全不同，所以必须把运行时的存活状态打出来。
        /// </summary>
        public static void LogProbeState(string stage)
        {
            var dd = probed;
            var sb = new StringBuilder();
            sb.Append("[GcProbe] ").Append(stage).Append("：");
            if (dd == null)
            {
                // 下拉格现在用的是自绘浮层（<see cref="PickerOverlay"/>），面板里已经不存在
                // TMP_Dropdown 了 —— 所以这里改成报告浮层状态。
                var picker = PickerOverlay.Shared;
                Log(sb.Append(picker == null
                    ? "选择浮层不存在"
                    : ("选择浮层 IsOpen=" + (picker.IsOpen ? 1 : 0))).Append('\n').ToString());
                return;
            }
            try
            {
                sb.Append("下拉框 存活=").Append(UiFinder.IsAlive(dd.gameObject) ? 1 : 0)
                    .Append(" 层级激活=").Append(dd.gameObject.activeInHierarchy ? 1 : 0)
                    .Append(" 可交互=").Append(dd.interactable ? 1 : 0);

                Transform list = null;
                try
                {
                    list = ProbeList();
                }
                catch (Exception)
                {
                }
                if (list == null)
                {
                    sb.Append("  → Dropdown List **不存在**\n");
                    Log(sb.ToString());
                    return;
                }

                var go = list.gameObject;
                sb.Append("  同名弹窗数=").Append(CountLists());
                sb.Append("\n[GcProbe]   Dropdown List 自身=").Append(go.activeSelf ? 1 : 0)
                    .Append(" 层级=").Append(go.activeInHierarchy ? 1 : 0)
                    .Append(" 子节点=").Append(list.childCount);
                var img = go.GetComponent<Image>();
                sb.Append(" Image=").Append(img == null
                    ? "null"
                    : (img.enabled ? 1 : 0) + " alpha=" + Num(img.color.a)
                      + " sprite=" + (img.sprite == null ? "null" : img.sprite.name));
                var canvas = go.GetComponent<Canvas>();
                sb.Append(" Canvas=").Append(canvas == null
                    ? "null"
                    : (canvas.enabled ? 1 : 0) + " override=" + (canvas.overrideSorting ? 1 : 0)
                      + " order=" + canvas.sortingOrder + " layer=" + LayerName(canvas.sortingLayerID));
                var cg = go.GetComponent<CanvasGroup>();
                if (cg != null)
                    sb.Append(" CanvasGroup alpha=").Append(Num(cg.alpha));
                var cr = go.GetComponent<CanvasRenderer>();
                if (cr != null)
                    sb.Append(" CanvasRenderer cull=").Append(cr.cull ? 1 : 0)
                        .Append(" alpha=").Append(Num(cr.GetAlpha()));
                var rt = list.TryCast<RectTransform>();
                if (rt != null)
                {
                    var r = ScreenRect(rt, CameraOf(FindCanvas(list)));
                    sb.Append(" 屏=[").Append(Num(r.xMin)).Append(',').Append(Num(r.yMin))
                        .Append(" → ").Append(Num(r.xMax)).Append(',').Append(Num(r.yMax)).Append(']');
                }
                sb.Append('\n');

                var vp = list.Find("Viewport");
                if (vp != null)
                {
                    var m = vp.GetComponent<Mask>();
                    var vi = vp.GetComponent<Image>();
                    sb.Append("[GcProbe]   Viewport 激活=").Append(vp.gameObject.activeInHierarchy ? 1 : 0)
                        .Append(" Mask=").Append(m == null ? "null" : (m.enabled ? 1 : 0) + "/show=" + (m.showMaskGraphic ? 1 : 0))
                        .Append(" Image=").Append(vi == null ? "null" : (vi.enabled ? 1 : 0) + " alpha=" + Num(vi.color.a))
                        .Append('\n');
                    var content = vp.Find("Content");
                    if (content != null)
                    {
                        sb.Append("[GcProbe]   Content 激活=").Append(content.gameObject.activeInHierarchy ? 1 : 0)
                            .Append(" 子节点=").Append(content.childCount);
                        if (content.childCount > 0)
                        {
                            var item = content.GetChild(0);
                            sb.Append(" 首项=").Append(item.name)
                                .Append(" 激活=").Append(item.gameObject.activeInHierarchy ? 1 : 0);
                            var label = FindDeep(item, "Item Label");
                            if (label != null)
                            {
                                var tmp = label.GetComponent<TMPro.TextMeshProUGUI>();
                                sb.Append(" 文本=").Append(tmp == null ? "?" : ("「" + tmp.text + "」"));
                            }
                        }
                        sb.Append('\n');
                    }
                }
            }
            catch (Exception e)
            {
                sb.Append("\n[GcProbe]   读状态异常：").Append(e.GetType().Name).Append(':').Append(e.Message).Append('\n');
            }
            Log(sb.ToString());
        }

        /// <summary>按名字深度优先找一个子节点。</summary>
        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null)
                return null;
            if (root.name == name)
                return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var hit = FindDeep(root.GetChild(i), name);
                if (hit != null)
                    return hit;
            }
            return null;
        }

        /// <summary>
        /// 用一批假数据把**自绘的选择浮层**打开（120 项 → 4 页，当前项在第 2 页）。
        ///
        /// 为什么要它：以下拉框为入口的交互（选择浮层）没法靠人手点做回归，
        /// 而“浮层到底画没画出来”只能靠截图差分证实 —— 这里就是那一次截图的前置动作。
        /// </summary>
        public static void ProbePicker()
        {
            var picker = PickerOverlay.Shared;
            if (picker == null)
            {
                Log("[GcDump] 没有选择浮层（面板没构建？）");
                return;
            }
            var items = new List<string>();
            for (var i = 1; i <= 120; i++)
                items.Add("自测条目 " + i + "（长名字用来看截断效果）");
            picker.Open("自测：选择浮层（120 项）", items, 37,
                index => Log("[GcDump] 浮层选择结果 = " + index));
        }

        /// <summary>
        /// 弹窗（TMP 生成的 "Dropdown List"）的 Transform。
        ///
        /// ⚠️ 取**最后一个**同名子节点：TMP 的 <c>Hide()</c> 只把弹窗淡到透明而**不一定真的销毁**，
        /// 于是同一个下拉框底下可能同时躺着若干个 "Dropdown List"。
        /// <c>Transform.Find()</c> 返回的是第一个（最旧的、已经淡出为 0 的死节点），
        /// 拿它做判断就会得出完全错误的结论。
        /// </summary>
        private static Transform ProbeList()
        {
            var dd = probed;
            if (dd == null || !UiFinder.IsAlive(dd.gameObject))
                return null;
            try
            {
                Transform found = null;
                var t = dd.transform;
                for (var i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (c.name == "Dropdown List")
                        found = c;
                }
                return found;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>数一数当前有几个同名的 "Dropdown List"（活/死各算一个，用来看 Hide 有没有遗留）。</summary>
        private static int CountLists()
        {
            var dd = probed;
            if (dd == null)
                return -1;
            try
            {
                var n = 0;
                var t = dd.transform;
                for (var i = 0; i < t.childCount; i++)
                {
                    if (t.GetChild(i).name == "Dropdown List")
                        n++;
                }
                return n;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>收起上一次 <see cref="ProbeDropdown"/> 展开的弹窗。</summary>
        public static void HideProbe()
        {
            var dd = probed;
            if (dd == null)
                return;
            try
            {
                if (UiFinder.IsAlive(dd.gameObject))
                    dd.Hide();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[GcDump] Hide() 失败：" + e.Message);
            }
        }

        /// <summary>
        /// 控件级探测：下拉格（选项/选中/按钮 targetGraphic）+ 原版提示组件（TooltipHandler/
        /// TooltipAnchor 的 Position/Pivot）。
        ///
        /// 为什么单开一个：这两个都是“挂在克隆体上的组件是否真的生效”类问题，
        /// 布局树里看不出来（组件不在 Walk 的输出里），而它们恰好是本次两个难调 bug 的核心。
        /// </summary>
        public static void ProbeWidgets(GameObject root, string note)
        {
            var sb = new StringBuilder();
            sb.Append("[GcWidget] ===== 控件探测：").Append(note).Append(" =====\n");
            sb.Append("[GcWidget] --- 下拉格 ---\n");
            try
            {
                sb.Append(UiFactory.DescribeDropdowns());
            }
            catch (Exception e)
            {
                sb.Append("读下拉格失败：").Append(e.GetType().Name).Append('\n');
            }

            sb.Append("[GcWidget] --- 原版悬停提示 ---\n");
            try
            {
                if (!UiFinder.IsAlive(root))
                {
                    sb.Append("面板不存在\n");
                }
                else
                {
                    var handlers = root.GetComponentsInChildren<MVZ2.UI.TooltipHandler>(true);
                    sb.Append("TooltipHandler 共 ").Append(handlers == null ? 0 : handlers.Length).Append(" 个\n");
                    if (handlers != null)
                    {
                        var max = Math.Min(handlers.Length, 6);
                        for (var i = 0; i < max; i++)
                        {
                            var h = handlers[i];
                            if (h == null)
                                continue;
                            sb.Append("  ").Append(PanelTooltips.Describe(h.gameObject)).Append('\n');
                        }
                    }
                }
            }
            catch (Exception e)
            {
                sb.Append("读提示组件失败：").Append(e.GetType().Name).Append(':').Append(e.Message).Append('\n');
            }
            sb.Append("[GcWidget] END\n");
            Log(sb.ToString());
        }

        /// <summary>在树里找一个**有选项**的下拉框（没选项的展开也看不出什么）。</summary>
        private static TMP_Dropdown FindDropdown(Transform t)
        {
            if (t == null)
                return null;
            try
            {
                var dd = t.GetComponent<TMP_Dropdown>();
                if (dd != null && dd.options != null && dd.options.Count > 0)
                    return dd;
                for (var i = 0; i < t.childCount; i++)
                {
                    var r = FindDropdown(t.GetChild(i));
                    if (r != null)
                        return r;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static void Walk(Transform t, int depth, Camera cam, StringBuilder sb)
        {
            if (t == null || depth > 24)
                return;
            try
            {
                var rt = t.TryCast<RectTransform>();
                sb.Append("[GcDump] ");
                for (var i = 0; i < depth; i++)
                    sb.Append("  ");
                sb.Append(t.name);

                sb.Append(" | ").Append(SafeActive(t) ? "on" : "OFF");

                if (rt != null)
                {
                    sb.Append(" | ").Append(Num(rt.anchorMin.x)).Append(',').Append(Num(rt.anchorMin.y))
                        .Append('-').Append(Num(rt.anchorMax.x)).Append(',').Append(Num(rt.anchorMax.y));
                    sb.Append(" | ").Append(Num(rt.pivot.x)).Append(',').Append(Num(rt.pivot.y));
                    sb.Append(" | ").Append(Num(rt.anchoredPosition.x)).Append(',').Append(Num(rt.anchoredPosition.y));
                    sb.Append(" | ").Append(Num(rt.sizeDelta.x)).Append('x').Append(Num(rt.sizeDelta.y));
                    var size = rt.rect.size;
                    sb.Append(" (实 ").Append(Num(size.x)).Append('x').Append(Num(size.y)).Append(')');
                    // **屏幕矩形**（绝对像素）—— 判断"东西到底有没有画在屏幕里"只需这一项。
                    var rect = ScreenRect(rt, cam);
                    sb.Append(" | 屏 [").Append(Num(rect.xMin)).Append(',').Append(Num(rect.yMin))
                        .Append(" → ").Append(Num(rect.xMax)).Append(',').Append(Num(rect.yMax)).Append(']');
                }

                AppendGraphics(t, sb);
                AppendContent(t, sb);
                sb.Append('\n');

                for (var i = 0; i < t.childCount; i++)
                    Walk(t.GetChild(i), depth + 1, cam, sb);
            }
            catch (Exception e)
            {
                sb.Append("  <遍历异常 ").Append(e.GetType().Name).Append(">\n");
            }
        }

        /// <summary>
        /// 图形相关的状态 —— 这里是「图不显示」类问题的关键：
        /// <c>Image.enabled=false</c>、sprite 为 null、alpha=0、被 canvas 的 overrideSorting 压到别处，
        /// 都会让节点"存在但看不见"。
        /// </summary>
        private static void AppendGraphics(Transform t, StringBuilder sb)
        {
            try
            {
                var image = t.GetComponent<Image>();
                if (image != null)
                {
                    var sprite = image.sprite;
                    sb.Append(" | Image en=").Append(image.enabled ? 1 : 0)
                        .Append(" 色=").Append(Color(image.color))
                        .Append(" sprite=").Append(sprite == null
                            ? "null"
                            : "「" + sprite.name + "」" + Num(sprite.rect.width) + "x" + Num(sprite.rect.height))
                        .Append(" raycast=").Append(image.raycastTarget ? 1 : 0)
                        .Append(" type=").Append(image.type);
                }
                var raw = t.GetComponent<RawImage>();
                if (raw != null)
                    sb.Append(" | RawImage en=").Append(raw.enabled ? 1 : 0)
                        .Append(" 色=").Append(Color(raw.color))
                        .Append(" tex=").Append(raw.texture == null ? "null" : "有");
                var canvas = t.GetComponent<Canvas>();
                if (canvas != null)
                    sb.Append(" | Canvas overrideSorting=").Append(canvas.overrideSorting ? 1 : 0)
                        .Append(" sortingOrder=").Append(canvas.sortingOrder)
                        .Append(" enabled=").Append(canvas.enabled ? 1 : 0)
                        .Append(" layerID=").Append(canvas.sortingLayerID)
                        .Append(" layer=").Append(LayerName(canvas.sortingLayerID));
                var raycaster = t.GetComponent<GraphicRaycaster>();
                if (raycaster != null)
                    sb.Append(" | GraphicRaycaster en=").Append(raycaster.enabled ? 1 : 0);
                var group = t.GetComponent<CanvasGroup>();
                if (group != null)
                    sb.Append(" | CanvasGroup alpha=").Append(Num(group.alpha))
                        .Append(" interactable=").Append(group.interactable ? 1 : 0)
                        .Append(" blocks=").Append(group.blocksRaycasts ? 1 : 0);
                if (t.GetComponent<RectMask2D>() != null)
                    sb.Append(" | RectMask2D");
                var mask = t.GetComponent<Mask>();
                if (mask != null)
                    sb.Append(" | Mask en=").Append(mask.enabled ? 1 : 0)
                        .Append(" showGraphic=").Append(mask.showMaskGraphic ? 1 : 0);
            }
            catch (Exception)
            {
            }
        }

        private static void AppendContent(Transform t, StringBuilder sb)
        {
            try
            {
                var text = t.GetComponent<TextMeshProUGUI>();
                if (text != null)
                {
                    var s = text.text ?? string.Empty;
                    if (s.Length > 40)
                        s = s.Substring(0, 40) + "…";
                    sb.Append(" | 文本[").Append(s.Replace("\n", "\\n")).Append("] 字号=").Append(Num(text.fontSize))
                        .Append(" autoSize=").Append(text.enableAutoSizing ? 1 : 0)
                        .Append(" 色=").Append(Color(text.color));
                }
                var button = t.GetComponent<Button>();
                if (button != null)
                    sb.Append(" | Button interactable=").Append(button.interactable ? 1 : 0);
                var toggle = t.GetComponent<Toggle>();
                if (toggle != null)
                    sb.Append(" | Toggle isOn=").Append(toggle.isOn ? 1 : 0)
                        .Append(" interactable=").Append(toggle.interactable ? 1 : 0);
                var dropdown = t.GetComponent<TMP_Dropdown>();
                if (dropdown != null)
                {
                    sb.Append(" | Dropdown value=").Append(dropdown.value)
                        .Append('/').Append(dropdown.options == null ? -1 : dropdown.options.Count)
                        .Append(" interactable=").Append(dropdown.interactable ? 1 : 0);
                    var template = dropdown.template;
                    if (template != null)
                        sb.Append(" template=").Append(Num(template.sizeDelta.x)).Append('x').Append(Num(template.sizeDelta.y))
                            .Append(template.gameObject.activeSelf ? "(on)" : "(off)");
                    else
                        sb.Append(" template=null");
                }
                var input = t.GetComponent<TMP_InputField>();
                if (input != null)
                    sb.Append(" | Input[").Append(input.text ?? string.Empty).Append("] interactable=")
                        .Append(input.interactable ? 1 : 0);
                var scroll = t.GetComponent<ScrollRect>();
                if (scroll != null)
                    sb.Append(" | ScrollRect v=").Append(Num(scroll.verticalNormalizedPosition))
                        .Append(" h=").Append(scroll.horizontal ? 1 : 0).Append(" v=").Append(scroll.vertical ? 1 : 0);
                var grid = t.GetComponent<GridLayoutGroup>();
                if (grid != null)
                    sb.Append(" | Grid cols=").Append(grid.constraintCount)
                        .Append(" cell=").Append(Num(grid.cellSize.x)).Append('x').Append(Num(grid.cellSize.y));
                var h = t.GetComponent<HorizontalLayoutGroup>();
                if (h != null)
                    sb.Append(" | HGroup spacing=").Append(Num(h.spacing))
                        .Append(" expandW=").Append(h.childForceExpandWidth ? 1 : 0);
                var v = t.GetComponent<VerticalLayoutGroup>();
                if (v != null)
                    sb.Append(" | VGroup spacing=").Append(Num(v.spacing))
                        .Append(" expandH=").Append(v.childForceExpandHeight ? 1 : 0);
            }
            catch (Exception)
            {
            }
        }

        private static string Color(Color c)
        {
            return Num(c.r) + "," + Num(c.g) + "," + Num(c.b) + "," + Num(c.a);
        }

        /// <summary>RectTransform 在屏幕上的实际尺寸（px）。走局部角点 → 世界 → 屏幕，含祖先 localScale。</summary>
        private static Vector2 ScreenSize(RectTransform rt, Camera cam, Vector2 size)
        {
            try
            {
                var min = new Vector3(
                    -size.x * rt.pivot.x,
                    -size.y * rt.pivot.y,
                    0f);
                var max = new Vector3(
                    size.x * (1f - rt.pivot.x),
                    size.y * (1f - rt.pivot.y),
                    0f);
                var a = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(min));
                var b = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(max));
                return new Vector2(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
            }
            catch (Exception)
            {
                return Vector2.zero;
            }
        }

        /// <summary>RectTransform 四个角在屏幕上的包围盒（px）。含祖先 localScale。</summary>
        private static Rect ScreenRect(RectTransform rt, Camera cam)
        {
            try
            {
                var rect = rt.rect;
                var a = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(new Vector3(rect.xMin, rect.yMin, 0f)));
                var b = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(new Vector3(rect.xMax, rect.yMin, 0f)));
                var c = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(new Vector3(rect.xMin, rect.yMax, 0f)));
                var d = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(new Vector3(rect.xMax, rect.yMax, 0f)));
                var minX = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x));
                var maxX = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
                var minY = Mathf.Min(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y));
                var maxY = Mathf.Max(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y));
                return new Rect(minX, minY, maxX - minX, maxY - minY);
            }
            catch (Exception)
            {
                return new Rect();
            }
        }

        private static string Num(float v)
        {
            return v.ToString("0.##");
        }

        private static bool SafeActive(Transform t)
        {
            try
            {
                return t.gameObject.activeSelf;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Canvas FindCanvas(Transform t)
        {
            var cur = t;
            var guard = 0;
            while (cur != null && guard++ < 32)
            {
                try
                {
                    var c = cur.GetComponent<Canvas>();
                    if (c != null)
                        return c;
                }
                catch (Exception)
                {
                }
                cur = cur.parent;
            }
            return null;
        }

        private static Camera CameraOf(Canvas canvas)
        {
            if (canvas == null)
                return null;
            try
            {
                return canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Log(string text)
        {
            try
            {
                Plugin.Log?.LogInfo(text);
            }
            catch (Exception)
            {
            }
        }
    }
}
