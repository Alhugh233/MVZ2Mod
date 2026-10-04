using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DSHCore.Ui
{
    /// <summary>
    /// 在**活动场景**里按组件类型找东西（含 <c>SetActive(false)</c> 的对象）。
    ///
    /// <para><b>为什么不能用 <c>FindObjectOfType</c> / <c>FindObjectsOfType</c>：</b>
    /// 它们默认跳过未激活对象，而本游戏里**要复用的 UI 素材（控件模板、列表模板、对话框）
    /// 平时全都是 <c>SetActive(false)</c> 的** —— 例如 <c>ElementList.Awake</c> 会主动把
    /// 模板子节点关掉。用官方 API 找，结果永远是"找不到"，然后就会误以为"只能自绘"。</para>
    ///
    /// <para>另一条经验：同一份 UI 预设在同一场景里可能有**多个实例**（Standalone / Mobile，
    /// 主菜单 / 关卡各一份），所以拿到多个候选时优先选「父链可见」的那一个。</para>
    ///
    /// <para>本类型由 <c>GraphicConsole</c> 已验证的实现收敛而来（见 DSHCore/UI_RESEARCH.md §6）。</para>
    /// </summary>
    public static class UiFinder
    {
        /// <summary>递归深度上限。UI 层级再深也不会超过这个数，用来防意外的环导致栈溢出。</summary>
        public const int MaxDepth = 16;

        // ------------------------------------------------------------------
        // 查找
        // ------------------------------------------------------------------

        /// <summary>收集场景里所有该类型的组件。<b>不返回己方节点上的</b>（见 <see cref="UiHost.IsOwn"/>）。</summary>
        public static List<T> FindAll<T>() where T : Component
        {
            var result = new List<T>();
            try
            {
                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid())
                    return result;

                var roots = scene.GetRootGameObjects();
                if (roots == null)
                    return result;

                for (var i = 0; i < roots.Length; i++)
                {
                    var go = roots[i];
                    if (!IsAlive(go))
                        continue;
                    if (UiHost.IsOwn(go.transform))
                        continue;
                    Collect(go, result, 0);
                }
            }
            catch (Exception e)
            {
                UiLog.Warn("遍历场景失败：" + e.GetType().Name + ": " + e.Message);
            }
            return result;
        }

        /// <summary>找单个组件，优先返回「父链可见」的那一个（没有则返回第一个）。找不到返回 null。</summary>
        public static T FindOne<T>() where T : Component
        {
            var all = FindAll<T>();
            if (all.Count == 0)
                return null;
            for (var i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (IsAlive(c) && IsChainActive(c.transform))
                    return c;
            }
            return all[0];
        }

        private static void Collect<T>(GameObject go, List<T> result, int depth) where T : Component
        {
            if (depth > MaxDepth || !IsAlive(go))
                return;

            try
            {
                var c = go.GetComponent<T>();
                if (c != null)
                    result.Add(c);
            }
            catch (Exception)
            {
                // 单个节点取组件失败不应该中断整次遍历。
            }

            try
            {
                var t = go.transform;
                var n = t.childCount;
                for (var i = 0; i < n; i++)
                {
                    var child = t.GetChild(i);
                    if (child == null)
                        continue;
                    Collect(child.gameObject, result, depth + 1);
                }
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------
        // 子树内查找
        // ------------------------------------------------------------------

        /// <summary>按名字在整棵子树里找第一个节点。</summary>
        public static Transform FindChild(Transform root, string name, int depth = 0)
        {
            if (root == null || depth > MaxDepth)
                return null;
            try
            {
                if (root.name == name)
                    return root;
                var n = root.childCount;
                for (var i = 0; i < n; i++)
                {
                    var r = FindChild(root.GetChild(i), name, depth + 1);
                    if (r != null)
                        return r;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        /// <summary>按 <c>"A/B/C"</c> 路径找节点（逐级 <c>Transform.Find</c>）。</summary>
        public static Transform FindByPath(Transform root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path))
                return null;
            var cur = root;
            var parts = path.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                if (cur == null)
                    return null;
                try { cur = cur.Find(parts[i]); }
                catch (Exception) { return null; }
            }
            return cur;
        }

        /// <summary>在子树里深度优先找第一个该类型的组件。</summary>
        public static T FindChildComponent<T>(Transform root, int depth = 0) where T : Component
        {
            if (root == null || depth > MaxDepth)
                return null;
            try
            {
                var c = root.GetComponent<T>();
                if (c != null)
                    return c;
                var n = root.childCount;
                for (var i = 0; i < n; i++)
                {
                    var r = FindChildComponent<T>(root.GetChild(i), depth + 1);
                    if (r != null)
                        return r;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        // ------------------------------------------------------------------
        // 判定 / 杂项
        // ------------------------------------------------------------------

        /// <summary>父链上是否有任何一层被禁用（即"这一支在屏幕上是不是可见的"）。</summary>
        public static bool IsChainActive(Transform t)
        {
            var cur = t;
            var guard = 0;
            while (cur != null && guard++ < 64)
            {
                try
                {
                    if (!cur.gameObject.activeSelf)
                        return false;
                }
                catch (Exception)
                {
                    return false;
                }
                cur = cur.parent;
            }
            return true;
        }

        /// <summary>
        /// 对象是否还活着（il2cpp 那边没被销毁）。
        /// 光判 <c>!= null</c> 不够：被 <c>Destroy</c> 过的代理对象仍能通过 null 比较。
        /// </summary>
        public static bool IsAlive(UnityEngine.Object obj)
        {
            if (obj == null)
                return false;
            try
            {
                if (obj.Pointer == IntPtr.Zero)
                    return false;
                if (obj is GameObject go)
                    return go.transform != null;
                if (obj is Component comp)
                    return comp.transform != null;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>安全取组件（失败返回 null，不抛）。<paramref name="path"/> 为相对路径时先按路径定位。</summary>
        public static T Get<T>(Transform t, string path = null) where T : Component
        {
            var node = string.IsNullOrEmpty(path) ? t : FindByPath(t, path);
            if (node == null)
                return null;
            try { return node.GetComponent<T>(); }
            catch (Exception) { return null; }
        }

        /// <summary>把节点的父链名字串起来，仅用于日志 / 排障报告。</summary>
        public static string PathOf(Transform t)
        {
            try
            {
                var parts = new List<string>();
                var cur = t;
                var guard = 0;
                while (cur != null && guard++ < 32)
                {
                    parts.Add(cur.name);
                    cur = cur.parent;
                }
                parts.Reverse();
                return string.Join("/", parts.ToArray());
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}
