using System;
using System.Text;
using DSHCore.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 运行时布局 dump —— **视觉问题的判据**。
    ///
    /// <para>「节点存在 + 尺寸正确」不等于「看得见」，而"看不见"的成因（被盖住、sprite 是空的、
    /// 字色和底色一样、内容把面板顶出屏幕）光看代码看不出来。所以这里把
    /// **屏幕矩形 + 字号/自动缩放/换行 + 每个单元格的可用宽度与标签需要的宽度** 打成文本，
    /// 交给 <c>tools/run-loadout-selftest.ps1</c> 落盘再 grep。</para>
    ///
    /// <para>屏幕坐标一律走「局部角点 → <c>TransformPoint</c> → <c>WorldToScreenPoint</c>」，
    /// 不乘 <c>scaleFactor</c>（那会漏掉祖先链上的 localScale）。</para>
    /// </summary>
    internal static class PanelDump
    {
        private const int MaxDepth = 8;

        /// <summary>简短状态（面板 / 素材来源 / 名称索引 / 子系统）。</summary>
        public static string Report()
        {
            var sb = new StringBuilder(1200);
            sb.Append("\n===== Loadout 状态 =====\n");
            sb.Append(LoadoutPanel.Report());
            sb.Append('\n').Append(PanelPages.Report()).Append('\n');
            sb.Append("行容器素材  横向：").Append(UiContainer.Source).Append('\n');
            sb.Append("            纵向：").Append(UiContainer.VerticalSource).Append('\n');
            sb.Append("名称索引：\n").Append(NameIndexes.Report());
            sb.Append("补齐字段：\n").Append(UiCompleter.DescribeAll());
            sb.Append("蓝图补全：").Append(AllBlueprintsPatch.ShowAll ? "全部" : "原版")
              .Append("  上次清单=").Append(AllBlueprintsPatch.LastOrderedCount)
              .Append(" 项（官方 ").Append(AllBlueprintsPatch.LastOfficialCount).Append(" 项）\n");
            sb.Append(BlueprintToggleButton.Describe()).Append('\n');
            sb.Append(DebugIconEntry.Describe()).Append('\n');
            sb.Append("兼容性：CompatLevel=").Append(CompatConfig.Level)
              .Append("  面板暂停=").Append(PanelPause.Active ? 1 : 0)
              .Append("  后台暂停=").Append(PanelPause.Enabled ? 1 : 0).Append('\n');
            sb.Append("HUD：").Append(LevelHud.Describe()).Append('\n');
            sb.Append('\n').Append(UiSkin.ReportCompact);
            return sb.ToString();
        }

        /// <summary>完整 dump：逐页装配 + 把面板/内容行/单元格的屏幕矩形打出来。</summary>
        public static string DumpAll()
        {
            var sb = new StringBuilder(4000);
            sb.Append("\n===== Loadout 布局 dump =====\n");
            sb.Append("屏幕=").Append(Screen.width).Append('x').Append(Screen.height).Append('\n');

            var wasOpen = LoadoutPanel.IsOpen;
            if (!LoadoutPanel.Open())
            {
                sb.Append("面板打不开：").Append(LoadoutPanel.LastError).Append('\n');
                return sb.ToString();
            }

            var count = LoadoutPanel.PageCount;
            sb.Append("页数=").Append(count).Append('\n');
            for (var i = 0; i < count; i++)
            {
                if (!LoadoutPanel.ShowPage(i))
                {
                    sb.Append("第 ").Append(i + 1).Append(" 页装配失败：")
                      .Append(LoadoutPanel.LastError).Append('\n');
                    continue;
                }
                sb.Append("\n--- 第 ").Append(i + 1).Append(" 页：").Append(LoadoutPanel.PageName).Append(" ---\n");
                // 刚重建完的子节点还没参与过排版（LayoutGroup 要到帧末才跑），
                // 不强制重算就会读到全 0 的矩形 —— 那样 dump 会把人带到错误结论上。
                // （SettleLayout 顺带把「等分列」按父容器的真实宽度重算一遍。）
                LoadoutPanel.SettleLayout();
                DumpPanel(sb);
                DumpRows(sb);
                DumpCompleters(sb);
                DumpList(sb);
                var pageReport = LoadoutPanel.PageReport();
                if (!string.IsNullOrEmpty(pageReport))
                {
                    sb.Append("  页面状态：\n");
                    foreach (var line in pageReport.Split('\n'))
                        sb.Append("    ").Append(line).Append('\n');
                }
            }

            if (!wasOpen)
                LoadoutPanel.Close();
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // 面板
        // ------------------------------------------------------------------

        private static void DumpPanel(StringBuilder sb)
        {
            var content = LoadoutPanel.Content;
            if (content == null)
            {
                sb.Append("（内容宿主为空）\n");
                return;
            }
            AppendNode(sb, "内容根", content.gameObject, 0);

            var root = content.parent;
            if (root != null)
            {
                var rt = root.TryCast<RectTransform>();
                if (rt != null)
                    AppendNode(sb, "对话框根", rt.gameObject, -1);
            }
            DumpDialogChildren(sb);

            // 判定：面板必须完整落在屏幕内。
            var rect = ScreenRect(content);
            sb.Append("  判据 面板完整在屏内：")
              .Append(rect.yMin >= -0.5f && rect.yMax <= Screen.height + 0.5f ? "OK" : "**超出**")
              .Append("（y ").Append(Mathf.RoundToInt(rect.yMin)).Append('~')
              .Append(Mathf.RoundToInt(rect.yMax)).Append(" / 屏高 ").Append(Screen.height).Append("）\n");

            DumpRaycasts(sb);
        }

        /// <summary>
        /// 命中诊断：几个采样点上，UI 事件系统认为最上层的是谁。
        ///
        /// <para>“面板阻挡了面板以外的输入”这类毛病只能靠它定住 —— 看自己面板的层级是看不出
        /// 别人的东西盖在上面的。</para>
        /// </summary>
        private static void DumpRaycasts(StringBuilder sb)
        {
            try
            {
                sb.Append("  命中诊断：\n");
                sb.Append("    ").Append(LoadoutPanel.RaycastAt(new Vector2(8f, 8f), 3));
                sb.Append("    ").Append(LoadoutPanel.RaycastAt(
                    new Vector2(Screen.width * 0.5f, Screen.height - 8f), 3));
                sb.Append("    ").Append(LoadoutPanel.RaycastUnderPointer(3));
            }
            catch (Exception) { }
        }

        /// <summary>对话框根的直接子节点（看有没有 Blocker 之类的全屏节点）。</summary>
        private static void DumpDialogChildren(StringBuilder sb)
        {
            try
            {
                var root = LoadoutPanel.DialogRoot;
                if (root == null)
                    return;
                sb.Append("  对话框根子节点（").Append(root.childCount).Append(" 个）：");
                for (var i = 0; i < root.childCount; i++)
                {
                    var child = root.GetChild(i);
                    if (child == null)
                        continue;
                    if (i > 0)
                        sb.Append(" / ");
                    sb.Append(child.gameObject.name)
                      .Append(child.gameObject.activeSelf ? "" : "(off)");
                }
                sb.Append('\n');
            }
            catch (Exception) { }
        }

        private static void DumpRows(StringBuilder sb)
        {
            var content = LoadoutPanel.Content;
            if (content == null)
                return;
            sb.Append("  内容行（").Append(content.childCount).Append(" 个子节点）：\n");
            try
            {
                for (var i = 0; i < content.childCount; i++)
                {
                    var child = content.GetChild(i);
                    if (child == null)
                        continue;
                    AppendNode(sb, "行" + (i + 1), child.gameObject, 0);
                    DumpCells(sb, child, 1);
                    // 前两行的第一列再整棵 dump 一次：
                    // 「文字左边缘和同列控件左边缘对不对齐」只能靠逐层矩形去对。
                    if (i < 5 && child.childCount > 0)
                    {
                        sb.Append("      · 首列完整子树：\n");
                        DumpNodeDeep(sb, child.GetChild(0).gameObject, 4, 6);
                    }
                }
            }
            catch (Exception e)
            {
                sb.Append("  遍历内容行失败：").Append(e.GetType().Name).Append('\n');
            }
        }

        /// <summary>
        /// 「放得下 / 挤」的判定。自动缩放的文本会自己缩字号去适配，判据因此是
        /// 「缩到 fontSizeMin 还放不放得下」，而不是「当前字号放不放得下」——
        /// 否则原版那些自动缩放的文字会被全判成「挤」，把真正的问题淹掉。
        /// </summary>
        private static string Verdict(TextMeshProUGUI node, float have, float need)
        {
            if (have >= need - 1f)
                return "放得下";
            var min = node == null ? 0f : node.fontSizeMin;
            var size = node == null ? 0f : node.fontSize;
            if (node != null && node.enableAutoSizing && min > 0f && size > 0f)
            {
                var needAtMin = need * (min / size);
                return have >= needAtMin - 1f ? "放得下（自动缩放）" : "**挤（缩到最小仍溢出）**";
            }
            return "**挤（会溢出）**";
        }

        /// <summary>逐层 dump 一棵子树（每行带矩形 + 布局契约 + TMP 信息）。</summary>
        private static void DumpNodeDeep(StringBuilder sb, GameObject go, int depth, int maxDepth)
        {
            if (!UiFinder.IsAlive(go) || depth > maxDepth)
                return;
            try
            {
                AppendNode(sb, new string(' ', depth * 2), go, depth);
                var rt = go.transform.TryCast<RectTransform>();
                if (rt == null)
                    return;
                for (var i = 0; i < rt.childCount; i++)
                    DumpNodeDeep(sb, rt.GetChild(i).gameObject, depth + 1, maxDepth);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 逐单元格检查「标签需要的宽度 vs 拿到手的宽度」——这是 4 列网格最容易出问题的地方
        /// （原版控件是按整行宽度设计的）。
        /// </summary>
        private static void DumpCells(StringBuilder sb, Transform row, int depth)
        {
            if (depth > 3 || row == null)
                return;
            try
            {
                var n = row.childCount;
                if (n == 0)
                    return;
                for (var i = 0; i < n; i++)
                {
                    var cell = row.GetChild(i);
                    if (cell == null)
                        continue;
                    var rt = cell.TryCast<RectTransform>();
                    if (rt == null)
                        continue;
                    var labels = cell.GetComponentsInChildren<TextMeshProUGUI>(true);
                    if (labels == null || labels.Length == 0)
                        continue;
                    var worst = 0f;
                    var text = string.Empty;
                    TextMeshProUGUI worstNode = null;
                    for (var k = 0; k < labels.Length; k++)
                    {
                        var t = labels[k];
                        if (t == null)
                            continue;
                        try
                        {
                            if (!t.gameObject.activeInHierarchy || string.IsNullOrEmpty(t.text))
                                continue;
                            // 输入框的占位文本（"输入文字……"）不是标签，跳过它。
                            if (t.name == "Placeholder")
                                continue;
                            var need = t.GetPreferredValues(t.text).x;
                            if (need > worst)
                            {
                                worst = need;
                                text = t.text;
                                worstNode = t;
                            }
                        }
                        catch (Exception) { }
                    }
                    if (worst <= 0f)
                        continue;
                    var have = rt.rect.width;
                    var cellRect = ScreenRect(rt);
                    sb.Append("      · ").Append(Pad(cell.name, 20))
                      .Append(" 格子宽=").Append(Mathf.RoundToInt(have))
                      .Append(" 屏x[").Append(Mathf.RoundToInt(cellRect.xMin)).Append(',')
                      .Append(Mathf.RoundToInt(cellRect.xMax)).Append(']')
                      .Append("  最长标签需要=").Append(Mathf.RoundToInt(worst))
                      .Append(" 「").Append(Short(text)).Append("」")
                      .Append("  字号=").Append(worstNode == null ? "?" : worstNode.fontSize.ToString("0.#"))
                      .Append("  文字屏x[").Append(Mathf.RoundToInt(ScreenRect(worstNode.transform.TryCast<RectTransform>()).xMin))
                      .Append(",")
                      .Append(Mathf.RoundToInt(ScreenRect(worstNode.transform.TryCast<RectTransform>()).xMax))
                      .Append("] → ")
                      .Append(Verdict(worstNode, have, worst))
                      .Append('\n');
                    if (rt.childCount > 0 && depth < 3)
                        DumpCells(sb, cell, depth + 1);
                }
            }
            catch (Exception) { }
        }

        private static void DumpCompleters(StringBuilder sb)
        {
            sb.Append("  补齐字段状态：\n").Append(UiCompleter.DescribeAll());
            DumpPopup(sb);
        }

        /// <summary>
        /// 只 dump 候选弹窗 —— 弹窗是**展开的时候才建**的，而布局 dump 发生在展开之前，
        /// 所以要有一个能在展开之后单独调用的入口。
        /// </summary>
        public static string PopupReport()
        {
            var sb = new StringBuilder(1200);
            DumpPopup(sb);
            return sb.ToString();
        }

        /// <summary>
        /// 候选弹窗的子树 —— 「弹窗开着但看不见」的原因几乎总在尺寸上：
        /// 素材自带的 <c>ContentSizeFitter</c> 会**盖掉**父布局组给的尺寸（我们给 preferred 也白给），
        /// 所以这里把它和每层子节点的实际尺寸都打出来。
        /// </summary>
        private static void DumpPopup(StringBuilder sb)
        {
            try
            {
                var popup = UiCompleter.DebugPopupOf(UiCompleter.DebugFirst);
                if (!UiFinder.IsAlive(popup))
                    return;
                sb.Append("  候选弹窗子树：\n");
                DumpPopupNode(sb, popup, 0);
                DumpPopupItems(sb, popup);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 候选行本身 —— 弹窗的矩形对了但条目文字还是一团的话，毛病一定在这一层
        /// （条目没宽度契约、或里面的文字节点被压成 0 宽）。
        /// </summary>
        private static void DumpPopupItems(StringBuilder sb, GameObject popup)
        {
            try
            {
                var list = UiFinder.FindChildComponent<PVZEngine.Tools.Unity.ElementList>(popup.transform);
                if (list == null)
                {
                    sb.Append("     （弹窗里找不到 ElementList）\n");
                    return;
                }
                sb.Append("    候选项：共 ").Append(list.Count).Append(" 项，前 3 项如下\n");
                for (var i = 0; i < list.Count && i < 3; i++)
                {
                    var item = list.getElement(i);
                    if (!UiFinder.IsAlive(item))
                        continue;
                    sb.Append("      #").Append(i).Append('\n');
                    DumpPopupNode(sb, item, 2);
                }
                var tpl = list.getTemplate();
                if (UiFinder.IsAlive(tpl))
                {
                    sb.Append("      模板（隐藏）：").Append(tpl.name)
                      .Append(tpl.activeSelf ? " on" : " off").Append('\n');
                    DumpPopupNode(sb, tpl, 2);
                }
            }
            catch (Exception e)
            {
                sb.Append("    候选项 dump 失败：").Append(e.GetType().Name).Append('\n');
            }
        }

        private static void DumpPopupNode(StringBuilder sb, GameObject go, int depth)
        {
            if (!UiFinder.IsAlive(go) || depth > 4)
                return;
            try
            {
                var rt = go.transform.TryCast<RectTransform>();
                if (rt == null)
                    return;
                var sb2 = new StringBuilder();
                for (var i = 0; i < depth; i++)
                    sb2.Append("  ");
                var fitter = go.GetComponent<ContentSizeFitter>();
                sb.Append("    ").Append(sb2).Append(Pad(go.name, 24))
                  .Append(go.activeSelf ? "on " : "off")
                  .Append(Mathf.RoundToInt(rt.rect.width)).Append('x')
                  .Append(Mathf.RoundToInt(rt.rect.height))
                  .Append("  子=").Append(rt.childCount)
                  .Append("  ").Append(Layout(go))
                  .Append("  适配器=")
                  .Append(fitter == null ? "无"
                      : (fitter.horizontalFit == ContentSizeFitter.FitMode.Unconstrained ? "横不限" : "横" + fitter.horizontalFit)
                        + "/" + (fitter.verticalFit == ContentSizeFitter.FitMode.Unconstrained ? "纵不限" : "纵" + fitter.verticalFit))
                  .Append('\n');
                for (var i = 0; i < rt.childCount; i++)
                    DumpPopupNode(sb, rt.GetChild(i).gameObject, depth + 1);
            }
            catch (Exception) { }
        }

        private static void DumpList(StringBuilder sb)
        {
            var content = LoadoutPanel.Content;
            if (content == null)
                return;
            try
            {
                var scroll = UiFinder.FindChildComponent<ScrollRect>(content);
                if (scroll == null)
                    return;
                var rt = scroll.transform.TryCast<RectTransform>();
                var view = ScreenRect(rt);
                sb.Append("  可滚动容器：")
                  .Append(UiFinder.PathOf(rt))
                  .Append("  视口 ")
                  .Append(Mathf.RoundToInt(view.width)).Append('x').Append(Mathf.RoundToInt(view.height))
                  .Append("  屏 [").Append(Mathf.RoundToInt(view.xMin)).Append(',')
                  .Append(Mathf.RoundToInt(view.yMin)).Append(" → ")
                  .Append(Mathf.RoundToInt(view.xMax)).Append(',')
                  .Append(Mathf.RoundToInt(view.yMax)).Append("]\n");
                var inner = scroll.content;
                if (inner != null)
                    sb.Append("  内容高度=").Append(Mathf.RoundToInt(inner.rect.height))
                      .Append("  行数=").Append(inner.childCount)
                      .Append(inner.rect.height > view.height ? "（可滚动）" : "（无需滚动）").Append('\n');
            }
            catch (Exception e)
            {
                sb.Append("  列表 dump 失败：").Append(e.GetType().Name).Append('\n');
            }
        }

        // ------------------------------------------------------------------
        // 节点
        // ------------------------------------------------------------------

        private static void AppendNode(StringBuilder sb, string label, GameObject go, int depth)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                var rt = go.transform.TryCast<RectTransform>();
                if (rt == null)
                    return;
                var rect = ScreenRect(rt);
                sb.Append("  ").Append(Pad(label, 10))
                  .Append(Pad(go.name, 22))
                  .Append(go.activeSelf ? "on " : "off")
                  .Append(" 设计=").Append(Mathf.RoundToInt(rt.rect.width)).Append('x')
                  .Append(Mathf.RoundToInt(rt.rect.height))
                  .Append("  屏 [").Append(Mathf.RoundToInt(rect.xMin)).Append(',')
                  .Append(Mathf.RoundToInt(rect.yMin)).Append(" → ")
                  .Append(Mathf.RoundToInt(rect.xMax)).Append(',')
                  .Append(Mathf.RoundToInt(rect.yMax)).Append(']')
                  .Append("  ").Append(Graphics(go))
                  .Append(" ").Append(Layout(go))
                  .Append('\n');
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 节点的排版契约 —— 0 宽 / 0 高时用它判定到底是谁没给尺寸：
        /// <c>LayoutElement</c> 的 preferred/flexible，以及节点自己是不是布局组。
        /// </summary>
        private static string Layout(GameObject go)
        {
            try
            {
                var sb = new StringBuilder();
                var lg = go.GetComponent<LayoutGroup>();
                sb.Append(lg == null ? "--" : "LG");
                if (lg != null)
                {
                    // 布局组自己的内边距 / 间距 / 子节点控制：对齐与错位的答案全在这儿。
                    try
                    {
                        var pad = lg.padding;
                        sb.Append(" 内距(").Append(pad.left).Append(',').Append(pad.right)
                          .Append(',').Append(pad.top).Append(',').Append(pad.bottom).Append(')');
                    }
                    catch (Exception) { }
                    try
                    {
                        var hv = lg.TryCast<HorizontalOrVerticalLayoutGroup>();
                        if (hv != null)
                        {
                            sb.Append(" 控制(cw=").Append(hv.childControlWidth ? 1 : 0)
                              .Append(" ch=").Append(hv.childControlHeight ? 1 : 0)
                              .Append(" fw=").Append(hv.childForceExpandWidth ? 1 : 0)
                              .Append(" fh=").Append(hv.childForceExpandHeight ? 1 : 0)
                              .Append(" 间距=").Append(hv.spacing).Append(')');
                        }
                    }
                    catch (Exception) { }
                }
                var le = go.GetComponent<LayoutElement>();
                if (le == null)
                {
                    sb.Append(" LE=无");
                }
                else
                {
                    sb.Append(" LE 宽:").Append(le.preferredWidth.ToString("0.#")).Append('/')
                      .Append(le.flexibleWidth.ToString("0.#"))
                      .Append(" 高:").Append(le.preferredHeight.ToString("0.#")).Append('/')
                      .Append(le.flexibleHeight.ToString("0.#"));
                    // 这一条很关键：带 ignoreLayout 的节点搬进布局组后不会被给尺寸（父子全 0）。
                    if (le.ignoreLayout)
                        sb.Append(" ⚠不参与布局");
                }

                // 文字节点：字号 + 这句话真正需要多宽 —— 「被挤成竖线」只看矩形是看不出来的。
                try
                {
                    var tmp = go.GetComponent<TextMeshProUGUI>();
                    if (tmp != null)
                    {
                        var need = string.IsNullOrEmpty(tmp.text) ? 0f : tmp.GetPreferredValues(tmp.text).x;
                        sb.Append(" TMP=").Append(tmp.fontSize.ToString("0.#"))
                          .Append(tmp.enableAutoSizing
                              ? "(auto " + tmp.fontSizeMin.ToString("0.#") + ".." + tmp.fontSizeMax.ToString("0.#") + ")"
                              : "")
                          .Append(" 需=").Append(Mathf.RoundToInt(need));
                    }
                }
                catch (Exception) { }
                return sb.ToString();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string Graphics(GameObject go)
        {
            try
            {
                var img = go.GetComponent<Image>();
                if (img == null)
                    return string.Empty;
                return "Image en=" + (img.enabled ? 1 : 0)
                     + " a=" + img.color.a.ToString("0.##")
                     + " sprite=" + (img.sprite == null ? "null" : "有")
                     + " raycast=" + (img.raycastTarget ? 1 : 0);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>节点的屏幕矩形（局部角点 → 世界 → 屏幕）。</summary>
        public static Rect ScreenRect(RectTransform rt)
        {
            if (rt == null)
                return new Rect();
            try
            {
                var cam = LoadoutPanel.CanvasCamera();
                var r = rt.rect;
                var a = RectTransformUtility.WorldToScreenPoint(cam,
                    rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
                var b = RectTransformUtility.WorldToScreenPoint(cam,
                    rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
                return new Rect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
                    Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
            }
            catch (Exception)
            {
                return new Rect();
            }
        }

        private static string Pad(string text, int width)
        {
            text = text ?? string.Empty;
            if (text.Length >= width)
                return text.Substring(0, width);
            return text.PadRight(width);
        }

        private static string Short(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            text = text.Replace('\n', ' ');
            return text.Length <= 18 ? text : text.Substring(0, 18) + "…";
        }
    }
}
