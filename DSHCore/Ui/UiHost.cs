using System;
using System.Collections.Generic;
using UnityEngine;

namespace DSHCore.Ui
{
    /// <summary>UI 应该挂在哪一层。选错层的表现是"看不见"或"被原版盖住"。</summary>
    public enum UiLayer
    {
        /// <summary>
        /// 与官方对话框同级（<c>GlobalUICanvas/Dialogs</c> 下）。
        /// **推荐**：能盖住其它页面，且与官方对话框同画布、同缩放。
        /// </summary>
        DialogLayer,

        /// <summary>主 UI 画布根（<c>GlobalUICanvas</c>）。最稳，但可能被 <c>Dialogs</c> 里的东西盖住。</summary>
        MainCanvas,
    }

    /// <summary>
    /// 解决「挂到哪里才可见 / 才不被覆盖 / 才被原版布局接管」，并负责**己方节点标记**。
    ///
    /// <para><b>己方标记为什么必须有：</b>本库找素材是按组件类型在场景里搜的。如果把自己克隆出来的
    /// 控件也算进候选，下一次查找就会**拿自己的克隆体当模板** —— 典型症状是"第二次打开面板时
    /// 文案变回原版"（因为那次克隆的来源正是上一次的、已经被改过的面板）。在节点名上加前缀，
    /// 查找时整条祖先链一起跳过，是最省事且能在场景 dump 里一眼看出来的做法。</para>
    /// </summary>
    public static class UiHost
    {
        /// <summary>己方节点名前缀。自己的节点会被改名成 <c>DSH_xxx</c>。</summary>
        public const string OwnPrefix = "DSH_";

        private static readonly HashSet<string> OwnNames = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 把某个**具体名字**也当成己方节点（供已有 mod 收敛旧代码用，例如 GraphicConsole 的
        /// <c>GraphicConsoleTemplates</c>）。注册后 <see cref="IsOwn"/> 会一并跳过它。
        /// </summary>
        public static void RegisterOwnName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return;
            OwnNames.Add(name);
        }

        /// <summary>这个名字是不是己方节点。</summary>
        public static bool IsOwnName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            if (name.StartsWith(OwnPrefix, StringComparison.Ordinal))
                return true;
            return OwnNames.Contains(name);
        }

        /// <summary>这个节点是不是己方的（自身或**任一祖先**命中即算）。</summary>
        public static bool IsOwn(Transform t)
        {
            var cur = t;
            var guard = 0;
            while (cur != null && guard++ < 64)
            {
                try
                {
                    if (IsOwnName(cur.name))
                        return true;
                }
                catch (Exception)
                {
                    return false;
                }
                cur = cur.parent;
            }
            return false;
        }

        /// <summary>把一个节点（含整棵子树）标记为己方：改名加前缀。</summary>
        public static void MarkOwn(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                if (!go.name.StartsWith(OwnPrefix, StringComparison.Ordinal))
                    go.name = OwnPrefix + go.name;
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------
        // 宿主解析
        // ------------------------------------------------------------------

        /// <summary>取一个可以安全挂载的父节点。找不到返回 null（此时**不要**挂，直接放弃）。</summary>
        public static Transform Resolve(UiLayer layer)
        {
            switch (layer)
            {
                case UiLayer.DialogLayer:
                    var dialogs = DialogLayer();
                    if (dialogs != null)
                        return dialogs;
                    break;
            }

            var canvas = UiGame.MainCanvas;
            return canvas == null ? null : canvas.transform;
        }

        /// <summary>
        /// <c>GlobalUICanvas/Dialogs</c> —— 官方放对话框的那一层。
        /// 先按"官方对话框的父节点"取（最准），再退回按名字找。
        /// </summary>
        public static Transform DialogLayer()
        {
            var dialog = UiGame.Dialog;
            if (dialog != null)
            {
                try
                {
                    var parent = dialog.transform.parent;
                    if (parent != null)
                        return parent;
                }
                catch (Exception)
                {
                }
            }

            var canvas = UiGame.MainCanvas;
            if (canvas == null)
                return null;
            return UiFinder.FindChild(canvas.transform, "Dialogs");
        }

        // ------------------------------------------------------------------
        // 新建 / 挂载
        // ------------------------------------------------------------------

        /// <summary>
        /// 新建一个**撑满父节点**的容器。
        ///
        /// <para>为什么必须用它而不是裸 <c>new GameObject(name, typeof(RectTransform))</c>：
        /// 新建的 RectTransform 是 <c>100×100</c> 且锚在中心，列表/面板挂进去会只有
        /// 一小块，行宽、裁剪、滚动全都乱 —— 这是实测踩过的坑（见 UI_RESEARCH.md §6-⑩）。</para>
        /// </summary>
        public static GameObject CreateStretched(string name, Transform parent)
        {
            var go = CreateNode(name, parent);
            if (go == null)
                return null;
            try
            {
                var rt = go.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = new Vector2(0f, 0f);
                    rt.anchorMax = new Vector2(1f, 1f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.offsetMin = new Vector2(0f, 0f);
                    rt.offsetMax = new Vector2(0f, 0f);
                    rt.localScale = Vector3.one;
                }
            }
            catch (Exception e)
            {
                UiLog.Warn("设置撑满锚点失败：" + e.GetType().Name);
            }
            return go;
        }

        /// <summary>新建一个带 RectTransform 的节点并挂到父节点下（保持 local 变换）。返回的节点已被标记为己方。</summary>
        public static GameObject CreateNode(string name, Transform parent)
        {
            try
            {
                var go = new GameObject(name);
                go.AddComponent<RectTransform>();
                if (parent != null)
                    go.transform.SetParent(parent, false);
                MarkOwn(go);
                return go;
            }
            catch (Exception e)
            {
                UiLog.Warn("新建 UI 节点失败：" + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 把 <paramref name="node"/> 插到 <paramref name="original"/> 的父节点里、**紧跟其后**。
        ///
        /// <para>这是「往原版已有面板里加东西」的推荐姿势（ModularCurse 的诅咒入口按钮就是这么做的）：
        /// 只要原版的父节点上有 <c>LayoutGroup</c>，插入的节点会被自动排版 ——
        /// **一个坐标都不用算**。自己定尺寸/缩放是 GraphicConsole 那条路的起点，
        /// 也是它观感失控的根本原因。</para>
        /// </summary>
        public static bool InsertAfter(GameObject original, GameObject node)
        {
            if (!UiFinder.IsAlive(original) || !UiFinder.IsAlive(node))
                return false;
            try
            {
                var parent = original.transform.parent;
                if (parent == null)
                    return false;

                node.transform.SetParent(parent, false);
                node.transform.SetSiblingIndex(original.transform.GetSiblingIndex() + 1);
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("插入兄弟节点失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>把节点挂到某一层下（保持 local 变换）。</summary>
        public static bool Attach(GameObject node, UiLayer layer)
        {
            var parent = Resolve(layer);
            if (parent == null || !UiFinder.IsAlive(node))
                return false;
            try
            {
                node.transform.SetParent(parent, false);
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("挂载节点失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 给一个节点（或其子节点）挂上原版同一套悬停提示。
        /// 实现已抽到 <see cref="UiTooltip"/> —— 只加组件是不够的，还必须做宿主那一步
        /// （订阅 <c>OnPointerEnter</c> 并调 <c>ShowTooltip</c>），否则鼠标悬停毫无反应。
        /// </summary>
        public static bool AttachTooltip(GameObject target, string title, string text)
        {
            return UiTooltip.Attach(target, title, text);
        }
    }
}
