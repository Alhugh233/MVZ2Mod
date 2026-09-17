using System;
using System.Collections.Generic;
using System.Text;
using MVZ2.Level;
using MVZ2.Level.Curses;
using PVZEngine.Tools.Unity;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ModularCurse
{
    /// <summary>
    /// 一次性诊断：把**活的**诅咒选择框（<see cref="CurseChoosingDialog"/>）整棵子树的
    /// 层级、RectTransform 几何与关键组件打到日志里。
    ///
    /// 目的：确定「把诅咒池选择条目塞进诅咒弹窗」时该挂到哪个节点、留多大地方，
    /// 而不是靠猜 prefab。
    /// </summary>
    internal static class CurseDialogProbe
    {
        /// <summary>
        /// 排查 UI 结构时改成 true：进入选卡界面会把整棵诅咒弹窗的层级 + 几何 + 组件打进日志。
        /// 平时关闭（一次会话几百行日志没必要）。
        /// </summary>
        private const bool Enabled = false;

        private const int MaxDepth = 8;
        private const int MaxNodes = 400;

        private static bool done;
        private static int nodes;

        /// <summary>整个会话只跑一次（进入选卡界面时自动触发）。</summary>
        public static void DumpOnce(LevelBlueprintChooseController ctl)
        {
            if (!Enabled || done || ctl == null)
                return;
            done = true;
            try
            {
                var chooseUI = ctl.chooseUI;
                if (chooseUI == null)
                {
                    Plugin.Log?.LogWarning("[Probe] chooseUI 为空");
                    return;
                }
                var tests = new List<CurseChoosingDialog>();
                Collect(chooseUI.transform, tests, 0);
                Log("找到 " + tests.Count + " 个 CurseChoosingDialog");
                for (var i = 0; i < tests.Count; i++)
                {
                    var d = tests[i];
                    Log("---- #" + i + " path=" + Path(d.transform)
                        + " activeSelf=" + d.gameObject.activeSelf
                        + " chainActive=" + IsChainActive(d.transform));
                    nodes = 0;
                    Walk(d.transform, 0);
                }
            }
            catch (Exception e)
            {
                Log("Probe 失败: " + e);
            }
        }

        private static void Collect(Transform node, List<CurseChoosingDialog> results, int depth)
        {
            if (node == null || depth > MaxDepth)
                return;
            try
            {
                var d = node.GetComponent<CurseChoosingDialog>();
                if (d != null)
                    results.Add(d);
            }
            catch (Exception)
            {
            }
            for (var i = 0; i < node.childCount; i++)
                Collect(node.GetChild(i), results, depth + 1);
        }

        private static void Walk(Transform node, int depth)
        {
            if (node == null || depth > MaxDepth || nodes >= MaxNodes)
                return;
            nodes++;
            var pad = new string(' ', depth * 2);
            var rt = node.TryCast<RectTransform>();
            var line = new StringBuilder();
            line.Append("  ").Append(pad).Append(node.name);
            line.Append(" active=").Append(node.gameObject.activeSelf ? 1 : 0);
            if (rt != null)
            {
                line.Append(" a=(").Append(F(rt.anchorMin.x)).Append(',').Append(F(rt.anchorMin.y))
                    .Append(")-(").Append(F(rt.anchorMax.x)).Append(',').Append(F(rt.anchorMax.y)).Append(')');
                line.Append(" p=(").Append(F(rt.anchoredPosition.x)).Append(',').Append(F(rt.anchoredPosition.y)).Append(')');
                line.Append(" s=(").Append(F(rt.sizeDelta.x)).Append(',').Append(F(rt.sizeDelta.y)).Append(')');
                line.Append(" pv=(").Append(F(rt.pivot.x)).Append(',').Append(F(rt.pivot.y)).Append(')');
            }
            var comps = Comps(node.gameObject);
            if (comps.Length > 0)
                line.Append(" [").Append(comps).Append(']');
            Log(line.ToString());
            for (var i = 0; i < node.childCount; i++)
                Walk(node.GetChild(i), depth + 1);
        }

        private static string Comps(GameObject go)
        {
            var parts = new List<string>();
            try
            {
                var list = go.GetComponent<ElementList>();
                if (list != null)
                {
                    var tpl = list.getTemplate();
                    var tplDesc = "<null>";
                    if (tpl != null)
                    {
                        tplDesc = tpl.name + ":" + Rect(tpl.transform)
                            + ",active=" + (tpl.activeSelf ? 1 : 0)
                            + ",curseItem=" + (tpl.GetComponent<CurseSelectItem>() != null ? 1 : 0)
                            + ",parent=" + (tpl.transform.parent == null ? "-" : tpl.transform.parent.name);
                    }
                    parts.Add("ElementList(count=" + list.Count + ",tpl=" + tplDesc + ")");
                }
            }
            catch (Exception) { }
            try
            {
                var g = go.GetComponent<GridLayoutGroup>();
                if (g != null)
                    parts.Add("Grid(cell=" + F(g.cellSize.x) + "x" + F(g.cellSize.y)
                        + ",spacing=" + F(g.spacing.x) + "x" + F(g.spacing.y)
                        + ",constraint=" + g.constraint + ",count=" + g.constraintCount + ")");
            }
            catch (Exception) { }
            try
            {
                var v = go.GetComponent<VerticalLayoutGroup>();
                if (v != null)
                    parts.Add("VLayout(spacing=" + F(v.spacing) + ",align=" + v.childAlignment
                        + ",ctrlH=" + (v.childControlHeight ? 1 : 0) + ",forceH=" + (v.childForceExpandHeight ? 1 : 0) + ")");
            }
            catch (Exception) { }
            try
            {
                var h = go.GetComponent<HorizontalLayoutGroup>();
                if (h != null)
                    parts.Add("HLayout(spacing=" + F(h.spacing) + ",align=" + h.childAlignment + ")");
            }
            catch (Exception) { }
            try
            {
                var s = go.GetComponent<ScrollRect>();
                if (s != null)
                    parts.Add("ScrollRect(content=" + (s.content == null ? "-" : s.content.name)
                        + ",view=" + (s.viewport == null ? "-" : s.viewport.name) + ")");
            }
            catch (Exception) { }
            try
            {
                var le = go.GetComponent<LayoutElement>();
                if (le != null)
                    parts.Add("LayoutElem(min=" + F(le.minWidth) + "x" + F(le.minHeight)
                        + ",pref=" + F(le.preferredWidth) + "x" + F(le.preferredHeight)
                        + ",flex=" + F(le.flexibleWidth) + "x" + F(le.flexibleHeight) + ")");
            }
            catch (Exception) { }
            try
            {
                var f = go.GetComponent<ContentSizeFitter>();
                if (f != null)
                    parts.Add("SizeFitter(h=" + f.horizontalFit + ",v=" + f.verticalFit + ")");
            }
            catch (Exception) { }
            try
            {
                var img = go.GetComponent<Image>();
                if (img != null)
                    parts.Add("Image(sprite=" + (img.sprite == null ? "-" : img.sprite.name)
                        + ",type=" + img.type + ",color=" + F(img.color.r) + "/" + F(img.color.a) + ")");
            }
            catch (Exception) { }
            try
            {
                var btn = go.GetComponent<Button>();
                if (btn != null)
                    parts.Add("Button(interactable=" + (btn.interactable ? 1 : 0) + ")");
            }
            catch (Exception) { }
            try
            {
                var txt = go.GetComponent<TextMeshProUGUI>();
                if (txt != null)
                    parts.Add("TMP('" + txt.text + "',size=" + F(txt.fontSize) + ")");
            }
            catch (Exception) { }
            try
            {
                if (go.GetComponent<CurseSelectItem>() != null)
                    parts.Add("CurseSelectItem");
            }
            catch (Exception) { }
            try
            {
                var cg = go.GetComponent<CanvasGroup>();
                if (cg != null)
                    parts.Add("CanvasGroup(a=" + F(cg.alpha) + ",blocks=" + (cg.blocksRaycasts ? 1 : 0) + ")");
            }
            catch (Exception) { }
            // 全部组件类型名（确认没带上会重设 sprite 的 ImageResolver 之类）。
            try
            {
                var all = go.GetComponents<Component>();
                if (all != null)
                {
                    var names = new List<string>();
                    for (var i = 0; i < all.Length; i++)
                    {
                        var c = all[i];
                        if (c != null)
                            names.Add(c.GetIl2CppType().Name);
                    }
                    parts.Add("#" + string.Join(",", names.ToArray()));
                }
            }
            catch (Exception) { }
            return string.Join(" | ", parts.ToArray());
        }

        private static string Rect(Transform t)
        {
            try
            {
                var rt = t.TryCast<RectTransform>();
                if (rt == null)
                    return "-";
                return F(rt.sizeDelta.x) + "x" + F(rt.sizeDelta.y);
            }
            catch (Exception)
            {
                return "-";
            }
        }

        private static string Path(Transform t)
        {
            var s = "";
            var cur = t;
            var guard = 0;
            while (cur != null && guard++ < 12)
            {
                s = cur.name + (s.Length == 0 ? "" : "/" + s);
                cur = cur.parent;
            }
            return s;
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

        private static string F(float v)
        {
            return Mathf.RoundToInt(v).ToString();
        }

        private static void Log(string text)
        {
            Plugin.Log?.LogInfo("[Probe] " + text);
        }
    }
}
