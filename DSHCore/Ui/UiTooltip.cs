using System;
using Il2CppInterop.Runtime;
using MVZ2.UI;
using UnityEngine;

namespace DSHCore.Ui
{
    /// <summary>
    /// 借用**原版那一套**悬停提示（不是自绘浮层）。
    ///
    /// <para>原版链路（已对本仓库 <c>runtime\BepInEx\interop</c> 核实）：</para>
    /// <code>
    /// 节点上：TooltipAnchor   —— 提供 Position / Pivot / IsDisabled
    ///         TooltipHandler  —— 实现指针进出的接口，字段 anchor / text / context
    ///   ↓ 谁来订阅？
    /// 「宿主」订阅每个 handler 的 OnPointerEnter / OnPointerExit，
    /// 再调 MainSceneController.ShowTooltip(source)。
    ///   ↓
    /// ShowTooltip 读 source → MainSceneUI.SetTooltipPosition + SetTooltipContent + ShowTooltip
    ///   ↓
    /// MainSceneController.Update() 每帧 UpdateTooltip() → 提示跟着锚点走
    /// </code>
    ///
    /// <para><b>所以挂组件是不够的</b> —— 还必须做宿主那一步（订阅 + 调 <c>ShowTooltip</c>）。
    /// 只加组件不订阅的话，鼠标悬停会**毫无反应**，而且不报错。</para>
    ///
    /// <para><see cref="TooltipAnchor"/> 与 <see cref="TooltipHandler"/> 恰好是原版 UI 组件里
    /// **唯二可以放心 <c>AddComponent</c> 的**（前者零序列化字段、后者字段全 public），
    /// 所以这里不需要克隆。</para>
    ///
    /// <para><b>⚠️ 锚点不是随便挂的 —— 挂错会把提示正对着控件盖上去。</b>
    /// 提示的位置与朝向完全由**锚点的 <c>transform.position</c> 与 <c>TooltipAnchor.pivot</c>**
    /// 决定（<c>Tooltip.SetPosition</c> 里就是
    /// <c>rectTransform.pivot = viewData.pivot; rectTransform.position = viewData.position;</c>）。
    /// 而 <c>TooltipAnchor.pivot</c> 的默认值是 <c>(0.5, 0.5)</c> —— 提示以锚点为中心展开。
    /// 所以「直接给控件 <c>AddComponent</c> 一个 <c>TooltipAnchor</c>」等于把提示**居中盖在控件上**。</para>
    ///
    /// <para>原版的做法（实测自 <c>Assets/Prefabs/UI/Options/OptionTooltipAnchor.prefab</c>）是
    /// 一个**专门的锚点子节点**：</para>
    /// <list type="bullet">
    /// <item>RectTransform：<c>anchorMin = anchorMax = (0.5, 1)</c>（父节点的顶边中点）、
    /// <c>pivot = (0.5, 0)</c>、<c>anchoredPosition = (0, 0)</c>；</item>
    /// <item><c>TooltipAnchor.pivot = (0.5, 0)</c> → 提示的**底边中点**贴在锚点上，
    /// 于是整条提示朝**上**展开，不会压住控件；</item>
    /// <item><c>LayoutElement.ignoreLayout = true</c> → 不占父节点 <c>LayoutGroup</c> 的位置。</item>
    /// </list>
    /// <para><see cref="Attach"/> 就是照这个做的。另外：克隆来的原版行（<c>OptionWidgets</c>）
    /// **自带**一个这样接好的锚点，用 <see cref="AttachExisting"/> 直接复用即可。</para>
    /// </summary>
    public static class UiTooltip
    {
        /// <summary>原版锚点的 pivot（实测值）：<c>(0.5, 0)</c> = 提示朝上展开。</summary>
        public static readonly Vector2 AnchorPivot = new Vector2(0.5f, 0f);

        /// <summary>
        /// 给某个节点挂原版悬停提示（整行/整块当热区时挂到容器节点上）。
        /// 若节点上已经有接好锚点的 <c>TooltipHandler</c>，会直接复用那个锚点。
        /// </summary>
        public static bool Attach(GameObject target, string title, string text)
        {
            if (!UiFinder.IsAlive(target) || string.IsNullOrEmpty(text))
                return false;
            try
            {
                var handler = target.GetComponent<TooltipHandler>();
                if (handler == null)
                {
                    handler = target.AddComponent<TooltipHandler>();
                    if (handler == null)
                    {
                        UiLog.WarnOnce("tip-nocomp", "挂原版提示组件失败（AddComponent 返回了 null）。");
                        return false;
                    }
                }

                // 已有锚点（克隆来的原版行会自带）就别动它 —— 原版接好的那个比我们造的更准。
                if (!UiFinder.IsAlive(handler.anchor))
                    handler.anchor = CreateAnchorNode(target);

                if (!UiFinder.IsAlive(handler.anchor))
                    return false;

                ApplyText(handler, title, text);
                Bind(handler, title);
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("挂提示失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 复用节点上**原版已经接好锚点**的 <c>TooltipHandler</c>（典型：<see cref="UiOptionRow"/>）。
        /// 没有的话退化成 <see cref="Attach"/>。
        /// </summary>
        public static bool AttachExisting(GameObject node, string title, string text)
        {
            var handler = UiFinder.IsAlive(node) ? node.GetComponent<TooltipHandler>() : null;
            if (handler == null || !UiFinder.IsAlive(handler.anchor))
                return Attach(node, title, text);

            try
            {
                ApplyText(handler, title, text);
                Bind(handler, title);
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("复用原版提示失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>只改文案，不重挂事件（反复刷新同一行时用）。</summary>
        public static bool SetText(GameObject node, string title, string text)
        {
            var handler = UiFinder.IsAlive(node) ? node.GetComponent<TooltipHandler>() : null;
            if (handler == null)
                return false;
            try
            {
                ApplyText(handler, title, text);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 诊断：把某个节点上的提示状态打出来（位置/朝向对不对只能看运行时的值）。
        ///
        /// <para>判据是**锚点落在屏幕的哪一点**，以及它是不是真的挂在这个节点下面。
        /// <c>Tooltip.SetPosition</c> 只做 <c>pivot = viewData.pivot; position = viewData.position;</c>，
        /// 而 <c>position</c> 取自 <c>TooltipAnchor.Position</c>（= 锚点节点的 <b>transform.position</b>，
        /// 不是 RectTransform 的 anchoredPosition）。所以"提示压住控件"这类毛病，
        /// 看锚点的屏幕点就够了：它应该在**目标顶边中点**。</para>
        /// </summary>
        public static string Describe(GameObject node)
        {
            if (!UiFinder.IsAlive(node))
                return "（无节点）";
            try
            {
                var handler = node.GetComponent<TooltipHandler>();
                if (handler == null)
                    return "无 TooltipHandler";
                var anchor = handler.anchor;
                if (!UiFinder.IsAlive(anchor))
                    return "有 Handler 但 anchor 为空";

                var p = anchor.Position;
                var pv = anchor.Pivot;

                var parent = anchor.transform.parent;
                var parentName = parent == null ? "null" : parent.name;
                var nested = anchor.transform.IsChildOf(node.transform) ? 1 : 0;
                var at = "?";
                try
                {
                    var canvas = anchor.GetComponentInParent<Canvas>();
                    var cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay
                        ? null
                        : canvas.worldCamera;
                    var sp = RectTransformUtility.WorldToScreenPoint(cam, p);
                    at = "x" + Round(sp.x) + " y" + Round(sp.y);
                }
                catch (Exception) { }

                return "anchor@" + at + " pivot=(" + Decimal(pv.x) + "," + Decimal(pv.y) + ")"
                     + " parent=" + parentName + " nested=" + nested
                     + " disabled=" + (anchor.IsDisabled ? 1 : 0)
                     + " text=「" + (handler.text ?? "") + "」";
            }
            catch (Exception e)
            {
                return "读提示状态失败：" + e.GetType().Name;
            }
        }

        /// <summary>坐标取整（像素级的距离观察不需要小数）。</summary>
        private static string Round(float v)
        {
            return ((int)Mathf.Round(v)).ToString();
        }

        /// <summary>
        /// pivot 是 0~1 的分数，**不能取整**：<c>(int)0.5 == 0</c>，会把 <c>(0.5, 0)</c>
        /// 误报成 <c>(0, 0)</c>（本仓库差点因此把“锚点朝向正确”误判成“朝向错了”）。
        /// </summary>
        private static string Decimal(float v)
        {
            return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>按原版 <c>OptionTooltipAnchor</c> 的契约造一个锚点子节点。</summary>
        private static TooltipAnchor CreateAnchorNode(GameObject target)
        {
            try
            {
                var go = UiHost.CreateNode("TooltipAnchor", target.transform);
                if (!UiFinder.IsAlive(go))
                    return null;

                var rt = go.GetComponent<RectTransform>();
                if (rt != null)
                {
                    // 父节点的顶边中点
                    rt.anchorMin = new Vector2(0.5f, 1f);
                    rt.anchorMax = new Vector2(0.5f, 1f);
                    rt.pivot = new Vector2(0.5f, 0f);
                    rt.anchoredPosition = Vector2.zero;
                    rt.sizeDelta = Vector2.zero;
                }

                // 不参与父节点的 LayoutGroup 排版（原版就是这么设的）。
                var le = go.GetComponent<UnityEngine.UI.LayoutElement>();
                if (le == null)
                    le = go.AddComponent<UnityEngine.UI.LayoutElement>();
                if (le != null)
                    le.ignoreLayout = true;

                var anchor = go.AddComponent<TooltipAnchor>();
                if (anchor != null)
                    anchor.pivot = AnchorPivot;
                return anchor;
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("tip-anchor", "创建提示锚点失败：" + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        private static void ApplyText(TooltipHandler handler, string title, string text)
        {
            // 原版把文案存在 handler.text 上（词条键）。直接写中文原文也能显示 ——
            // 找不到对应词条时本地化会原样返回键本身。
            handler.text = text;
            handler.context = title ?? string.Empty;
        }

        private static void Bind(TooltipHandler handler, string title)
        {
            var enter = new System.Action<TooltipHandler>(h => Show(h, title));
            var exit = new System.Action<TooltipHandler>(h => Hide());
            var enterIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<TooltipHandler>>(enter);
            var exitIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<TooltipHandler>>(exit);

            // IL2CPP 侧只存裸函数指针，托管委托必须自己持有（否则 GC 一收就崩）。
            // 属主取 handler 所在节点：那一行被销毁时登记项会一起被回收。
            var owner = handler.gameObject;
            UiClone.KeepAlive(owner, enter, exit, enterIl2Cpp, exitIl2Cpp);
            handler.OnPointerEnter = enterIl2Cpp;
            handler.OnPointerExit = exitIl2Cpp;
        }

        /// <summary>收起提示（关面板 / 切页 / 内容被销毁时调，免得留下一条不走的提示）。</summary>
        public static void Hide()
        {
            try
            {
                var scene = UiGame.Scene;
                if (scene != null)
                    scene.HideTooltip();
            }
            catch (Exception)
            {
            }
        }

        private static void Show(TooltipHandler handler, string title)
        {
            try
            {
                var scene = UiGame.Scene;
                if (scene == null || handler == null)
                    return;

                var content = new TooltipContent
                {
                    name = title ?? string.Empty,
                    description = handler.text ?? string.Empty,
                };

                // 代理类不声明自己实现的接口 → 按指针包一层壳（与本仓库其它地方一致）。
                var target = new ITooltipTarget(handler.Pointer);
                var source = new SimpleTooltipSource(scene.uiCamera, target, content);
                scene.ShowTooltip(new ITooltipSource(source.Pointer));
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("tip-show", "调原版 ShowTooltip 失败：" + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
