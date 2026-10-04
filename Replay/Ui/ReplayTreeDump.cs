using System;
using System.Text;
using DSHCore.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Replay
{
    /// <summary>
    /// 把一个 UI 子树的**真实结构**打成文本（名字 / 激活状态 / 尺寸 / 布局组 / 控件类型 / 文本）。
    ///
    /// <para><b>为什么需要它：</b>本仓库所有的 UI 都是「克隆原版节点再插进原版布局组」。
    /// 插进去之后到底排到哪儿去了，**只能看原版那套布局组怎么配的** ——
    /// 是横排还是竖排、间距多少、子项是不是 forceExpand、Content 上到底挂的是
    /// <c>VerticalLayoutGroup</c> 还是 <c>GridLayoutGroup</c> 还是根本没有布局组。
    /// 光看屏幕截图分不清「排在旁边」和「排在下面但没换行」。</para>
    ///
    /// <para>所以每次要往某个原版页面里插东西之前，先把它 dump 一遍。</para>
    /// </summary>
    internal static class ReplayTreeDump
    {
        /// <summary>dump 的最大深度与每个节点的最大子节点数（防止打爆日志）。</summary>
        private const int MaxDepth = 8;
        private const int MaxChildren = 24;

        /// <summary>从 <paramref name="root"/> 开始 dump 整棵子树。返回的行带前导换行。</summary>
        public static string Dump(string title, GameObject root)
        {
            var sb = new StringBuilder(4096);
            sb.Append('\n').Append("===== ").Append(title).Append(" =====").Append('\n');
            if (!UiFinder.IsAlive(root))
            {
                sb.Append("（节点不存在）").Append('\n');
                return sb.ToString();
            }
            sb.Append("路径：").Append(UiFinder.PathOf(root.transform)).Append('\n');
            Node(sb, root.transform, 0);
            return sb.ToString();
        }

        /// <summary>按组件类型在场景里找一个，然后 dump 它的整棵子树。</summary>
        public static string DumpByType<T>(string title) where T : Component
        {
            T c = null;
            try { c = UiFinder.FindOne<T>(); }
            catch (Exception e)
            {
                return "\n===== " + title + " =====\n（查找抛异常：" + e.GetType().Name + "）\n";
            }
            if (c == null)
                return "\n===== " + title + " =====\n（场景里找不到该组件）\n";
            return Dump(title, c.gameObject);
        }

        private static void Node(StringBuilder sb, Transform t, int depth)
        {
            if (t == null || depth > MaxDepth)
                return;
            try
            {
                var go = t.gameObject;
                for (var i = 0; i < depth; i++)
                    sb.Append("  ");
                sb.Append(go.name);

                // 激活状态要分 self / inHierarchy：两者常不一致，而"看不见"多半就是后者为 false。
                var self = go.activeSelf;
                var inHier = go.activeInHierarchy;
                if (!self || !inHier)
                    sb.Append(self ? " [父链关着]" : " [自身关着]");

                var rt = t.TryCast<RectTransform>();
                if (rt != null)
                {
                    sb.Append("  ").Append(Mathf.RoundToInt(rt.rect.width)).Append('x').Append(Mathf.RoundToInt(rt.rect.height));
                    sb.Append(" @").Append(Mathf.RoundToInt(rt.anchoredPosition.x)).Append(',').Append(Mathf.RoundToInt(rt.anchoredPosition.y));
                    sb.Append(" 锚").Append(F(rt.anchorMin)).Append('-').Append(F(rt.anchorMax));
                }
                else
                {
                    sb.Append("  （无 RectTransform）");
                }

                sb.Append(DescribeLayout(go));
                sb.Append(DescribeComponents(go));
                sb.Append(DescribeText(go));
                sb.Append('\n');

                var n = t.childCount;
                var shown = n > MaxChildren ? MaxChildren : n;
                for (var i = 0; i < shown; i++)
                    Node(sb, t.GetChild(i), depth + 1);
                if (n > shown)
                {
                    for (var i = 0; i <= depth; i++)
                        sb.Append("  ");
                    sb.Append("…（还有 ").Append(n - shown).Append(" 个子节点）").Append('\n');
                }
            }
            catch (Exception e)
            {
                sb.Append("  <dump 失败 ").Append(e.GetType().Name).Append(">\n");
            }
        }

        private static string F(Vector2 v)
        {
            return v.x.ToString("0.##") + "/" + v.y.ToString("0.##");
        }

        /// <summary>布局组 —— 判「横排还是竖排」「子项会不会被拉满」全靠它。</summary>
        private static string DescribeLayout(GameObject go)
        {
            try
            {
                var h = go.GetComponent<HorizontalLayoutGroup>();
                if (h != null)
                    return "  <H " + Group(h.spacing, h.padding, h.childAlignment,
                        h.childControlWidth, h.childControlHeight, h.childForceExpandWidth, h.childForceExpandHeight) + ">";
                var v = go.GetComponent<VerticalLayoutGroup>();
                if (v != null)
                    return "  <V " + Group(v.spacing, v.padding, v.childAlignment,
                        v.childControlWidth, v.childControlHeight, v.childForceExpandWidth, v.childForceExpandHeight) + ">";
                var g = go.GetComponent<GridLayoutGroup>();
                if (g != null)
                    return "  <Grid cell=" + (int)g.cellSize.x + "x" + (int)g.cellSize.y
                        + " spacing=" + (int)g.spacing.x + "," + (int)g.spacing.y + ">";
                var le = go.GetComponent<LayoutElement>();
                if (le != null)
                    return "  <LE pref=" + le.preferredWidth.ToString("0.#") + "x" + le.preferredHeight.ToString("0.#")
                        + " flex=" + le.flexibleWidth.ToString("0.#") + "x" + le.flexibleHeight.ToString("0.#") + ">";            }
            catch (Exception) { }
            return "";
        }

        private static string Group(float spacing, RectOffset padding, TextAnchor align,
            bool cw, bool ch, bool ew, bool eh)
        {
            var pad = 0;
            try { pad = padding == null ? 0 : padding.left; } catch (Exception) { }
            return "spacing=" + spacing.ToString("0.#") + " pad=" + pad + " align=" + align
                + " control=" + (cw ? "1" : "0") + (ch ? "1" : "0")
                + " expand=" + (ew ? "1" : "0") + (eh ? "1" : "0") + ">";
        }

        /// <summary>只列「会影响外观/交互」的组件，别把 Transform 之类全打出来。</summary>
        private static string DescribeComponents(GameObject go)
        {
            var sb = new StringBuilder(48);
            try
            {
                if (go.GetComponent<Button>() != null)
                    sb.Append(" Button");
                if (go.GetComponent<Toggle>() != null)
                    sb.Append(" Toggle");
                if (go.GetComponent<ScrollRect>() != null)
                    sb.Append(" ScrollRect");
                // 内容高度是谁算的：有 ContentSizeFitter 就说明“加了子节点它会自己长高”，
                // 没有就要自己给 sizeDelta.y（否则多出来的子节点会被裁掉）。
                var csf = go.GetComponent<ContentSizeFitter>();
                if (csf != null)
                    sb.Append(" CSF(").Append(csf.horizontalFit).Append('+').Append(csf.verticalFit).Append(')');
                var img = go.GetComponent<Image>();
                if (img != null)
                    sb.Append(" Image(").Append(img.sprite == null ? "空sprite" : "有sprite")
                      .Append(',').Append(img.type)
                      .Append(",en=").Append(img.enabled ? "1" : "0").Append(')');
                if (go.GetComponent<RectMask2D>() != null)
                    sb.Append(" RectMask2D");
                var mask = go.GetComponent<Mask>();
                if (mask != null)
                    sb.Append(" Mask(en=").Append(mask.enabled ? "1" : "0").Append(')');
                if (go.GetComponent<Canvas>() != null)
                    sb.Append(" Canvas");
                // 原版的行生成器：它们会自己造子节点，克隆时常常要摘掉。
                var el = go.GetComponent("ElementList");
                if (el != null)
                    sb.Append(" ElementList");
                var elu = go.GetComponent("ElementListUI");
                if (elu != null)
                    sb.Append(" ElementListUI");
            }
            catch (Exception) { }
            return sb.Length == 0 ? "" : "  [" + sb.ToString().Trim() + "]";
        }

        private static string DescribeText(GameObject go)
        {
            try
            {
                var t = go.GetComponent<TextMeshProUGUI>();
                if (t == null)
                    return "";
                var s = t.text ?? "";
                if (s.Length > 18)
                    s = s.Substring(0, 18) + "…";
                // 自动缩放开着时 `fontSize` 只是"上次算出来的值"，**真正的约束是 min/max** ——
                // 两个看起来一样大的节点渲染出来不一样高，差别往往就在这里。
                return "  \"" + s.Replace('\n', ' ') + "\"@" + t.fontSize.ToString("0.#")
                    + (t.enableAutoSizing
                        ? "(自动 " + t.fontSizeMin.ToString("0.#") + "~" + t.fontSizeMax.ToString("0.#") + ")"
                        : "")
                    + " 对齐=" + t.alignment
                    + (t.enableWordWrapping ? "" : " 不换行");
            }
            catch (Exception) { return ""; }
        }
    }
}
