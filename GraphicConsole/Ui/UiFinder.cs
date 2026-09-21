using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GraphicConsole
{
    /// <summary>
    /// 在**活动场景**里找组件（含 <c>SetActive(false)</c> 的对象）。
    ///
    /// 为什么不能用 <c>FindObjectOfType</c> / <c>FindObjectsOfType</c>：它们默认跳过未激活对象，
    /// 而我们要复用的 UI 素材（弹窗、列表模板、控件模板）平时全都是 <c>SetActive(false)</c> 的。
    /// 所以只能自己从场景根对象递归遍历 —— 这也是本仓库里已验证过的做法
    /// （ModularCurse 的 CurseDialogProbe / CursePoolRow 都这么干）。
    ///
    /// 另外一点经验：同一份 UI 预设在同一场景里可能有**多个实例**（Standalone / Mobile），
    /// 所以收集到多个候选时，优先选「父链可见」的那一份。
    /// </summary>
    internal static class UiFinder
    {
        /// <summary>递归深度上限。UI 层级再深也不会超过这个数，防止意外的环导致栈溢出。</summary>
        public const int MaxDepth = 16;

        /// <summary>
        /// 我们自己的对象名。查找时必须跳过它们，否则：
        ///   - 会把自己的面板（也带 <c>InputNameDialog</c> 组件）当成克隆源；
        ///   - 会把自己的克隆控件当成模板。
        /// 这正是"第二次打开面板文字变成原版文案"的真正原因，所以在最底层堵掉。
        /// </summary>
        private static readonly string[] OwnNames =
        {
            PanelShell.RootName,
            UiSources.HolderName,
            GraphicConsoleIcon.IconName,
        };

        /// <summary>这个名字的节点是否属于本 mod（自身或祖先任一命中即算）。</summary>
        public static bool IsOwnNode(Transform t)
        {
            var cur = t;
            var guard = 0;
            while (cur != null && guard++ < 64)
            {
                var n = cur.name;
                for (var i = 0; i < OwnNames.Length; i++)
                {
                    if (n == OwnNames[i])
                        return true;
                }
                cur = cur.parent;
            }
            return false;
        }

        public static List<T> FindAll<T>() where T : Component
        {
            var result = new List<T>();
            try
            {
                var scene = SceneManager.GetActiveScene();
                var roots = scene.GetRootGameObjects();
                if (roots == null)
                    return result;
                for (var i = 0; i < roots.Length; i++)
                {
                    var go = roots[i];
                    if (go == null)
                        continue;
                    if (IsOwnNode(go.transform))
                        continue;
                    Collect(go, result, 0);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[UiFinder] 遍历场景失败：" + e.GetType().Name + ": " + e.Message);
            }
            return result;
        }

        /// <summary>找组件，优先返回「父链可见」的那一个（没有则返回第一个）。</summary>
        public static T FindOne<T>() where T : Component
        {
            var all = FindAll<T>();
            if (all.Count == 0)
                return null;
            for (var i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c != null && IsChainActive(c.transform))
                    return c;
            }
            return all[0];
        }

        private static void Collect<T>(GameObject go, List<T> result, int depth) where T : Component
        {
            if (go == null || depth > MaxDepth)
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

        /// <summary>按名字在整棵子树里找第一个节点（广度无所谓，只要求找得到）。</summary>
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

        /// <summary>按 "A/B/C" 路径找节点（逐级 <c>Transform.Find</c>）。</summary>
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
                try
                {
                    cur = cur.Find(parts[i]);
                }
                catch (Exception)
                {
                    return null;
                }
            }
            return cur;
        }

        /// <summary>在子树里找第一个该类型的组件（深度优先）。</summary>
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

        /// <summary>父链上是否有任何一层被禁用（即"这一支在屏幕上是不是可见的"）。</summary>
        public static bool IsChainActive(Transform t)
        {
            var cur = t;
            var guard = 0;
            while (cur != null && guard++ < 64)
            {
                if (!cur.gameObject.activeSelf)
                    return false;
                cur = cur.parent;
            }
            return true;
        }

        /// <summary>对象是否还活着（il2cpp 那边没被销毁）。</summary>
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

        /// <summary>安全取组件（失败返回 null，不抛）。</summary>
        public static T Get<T>(Transform t, string path = null) where T : Component
        {
            var node = string.IsNullOrEmpty(path) ? t : FindByPath(t, path);
            if (node == null)
                return null;
            try
            {
                return node.GetComponent<T>();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>把节点的父链名字串起来，仅用于日志。</summary>
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
