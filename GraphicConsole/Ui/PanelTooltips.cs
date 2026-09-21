using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using MVZ2.Managers;
using MVZ2.UI;
using UnityEngine;

namespace GraphicConsole
{
    /// <summary>
    /// 借**原版的那一套**悬停提示（不是自绘浮层）。
    ///
    /// 原版链路（源码/dump 核实）：
    /// <code>
    /// 元素上：TooltipAnchor(MonoBehaviour, 提供 Position/Pivot/IsDisabled, 实现 ITooltipAnchor)
    ///         TooltipHandler(MonoBehaviour, 实现 IPointerEnterHandler/IPointerExitHandler
    ///                        + ITooltipTarget, 字段 anchor / text)
    ///   ↓ 谁来订阅事件？
    /// OptionsDialogMainPage / OptionsDialogController 这类"宿主"订阅每个 handler 的
    /// OnPointerEnter / OnPointerExit，再调 MainSceneController.ShowTooltip(source)。
    ///   ↓ MainSceneController.ShowTooltip(ITooltipSource)
    /// 读 source.GetTarget().Anchor → MainSceneUI.SetTooltipPosition(TooltipPosition)
    ///                             + SetTooltipContent(TooltipContent) + ShowTooltip()
    /// 而 MainSceneController.Update() 每帧 UpdateTooltip() → 提示跟着锚点走
    /// （所以面板被拖走时提示也会跟着走）。
    /// </code>
    ///
    /// 所以这里**只做宿主该做的那一步**：加两个原版组件 + 订阅 enter/exit + 给内容。
    /// 视觉效果、定位、跟随、淡入淡出全部由游戏自己负责 —— 与设置界面/图鉴/制品按钮的提示
    /// 完全同一套。
    ///
    /// ⚠️ 组件挂在**格子根节点**上（那一行容器），不是挂在控件上：
    ///   · 事件系统是**向上冒泡**的（ExecuteEvents.GetEventHandler 会沿父链找），
    ///     所以鼠标落在行内任意一个可点击图形（标签文本 / 复选框 / 按钮）上都会命中，
    ///     悬停区域自然就是整行；
    ///   · 不能反过来"在行上盖一张透明 Image 当热区" —— 那会吃掉下面按钮的点击。
    /// </summary>
    internal static class PanelTooltips
    {
        /// <summary>给某个控件挂上原版悬停提示。<paramref name="target"/> 一般传格子的根节点。</summary>
        public static bool Attach(GameObject target, string title, string text)
        {
            if (target == null || string.IsNullOrEmpty(text))
                return false;
            try
            {
                var anchor = target.GetComponent<TooltipAnchor>();
                if (anchor == null)
                    anchor = target.AddComponent<TooltipAnchor>();
                var handler = target.GetComponent<TooltipHandler>();
                if (handler == null)
                    handler = target.AddComponent<TooltipHandler>();
                if (anchor == null || handler == null)
                {
                    Plugin.Log?.LogWarning("[Tip] 挂原版提示组件失败（AddComponent 返回 null）");
                    return false;
                }

                // anchor 就是"提示贴哪儿"：位置取 transform.position，pivot 决定贴的角。
                handler.anchor = anchor;
                // 原版把文案存在 handler.text 上（MukioI18n 的词条键）。这里直接存中文原文：
                // 没有对应词条时 i18n 会原样返回键本身，所以不影响显示。
                SetHandlerText(handler, text);

                var enter = new System.Action<TooltipHandler>(h => Show(h, title));
                var exit = new System.Action<TooltipHandler>(h => Hide());
                var enterIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<TooltipHandler>>(enter);
                var exitIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<TooltipHandler>>(exit);
                // 委托必须自己持有（IL2CPP 侧只存裸函数指针），属主销毁后由 PruneKeepAlive 回收。
                UiFactory.KeepAlive(target, enter, exit, enterIl2Cpp, exitIl2Cpp);
                handler.OnPointerEnter = enterIl2Cpp;
                handler.OnPointerExit = exitIl2Cpp;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Tip] 挂原版提示失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>把一个格子的提示挂到它的根节点上，标题用格子标签。</summary>
        public static bool Attach(WidgetCell cell, string text)
        {
            if (cell == null || !cell.Exists)
                return false;
            string title = null;
            try
            {
                var label = cell.Label;
                if (label != null)
                    title = label.text;
            }
            catch (Exception)
            {
            }
            return Attach(cell.Root, title, text);
        }

        /// <summary>更新已挂提示的文案（例如「重载」的提示会随待应用的背景/关卡变）。</summary>
        public static bool SetText(WidgetCell cell, string text)
        {
            if (cell == null || !cell.Exists || text == null)
                return false;
            try
            {
                var handler = cell.Root.GetComponent<TooltipHandler>();
                if (handler == null)
                    return Attach(cell, text);
                SetHandlerText(handler, text);
                return true;
            }
            catch (Exception e)
            {
                WarnOnce("settext-cell", e);
                return false;
            }
        }

        /// <summary>把一个节点上的提示文字取出来（诊断用）。</summary>
        public static string GetText(WidgetCell cell)
        {
            if (cell == null || !cell.Exists)
                return null;
            try
            {
                var handler = cell.Root.GetComponent<TooltipHandler>();
                return handler == null ? null : handler.text;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>收起提示（关面板 / 切页 / 内容重建时调，免得留下一条不走的提示）。</summary>
        public static void Hide()
        {
            try
            {
                var main = MainManager.Instance;
                if (main != null && main.Scene != null)
                    main.Scene.HideTooltip();
            }
            catch (Exception)
            {
            }
        }

        private static void Show(TooltipHandler handler, string title)
        {
            try
            {
                var main = MainManager.Instance;
                if (main == null || main.Scene == null || handler == null)
                    return;
                var text = GetHandlerText(handler);
                var content = new TooltipContent
                {
                    name = title ?? string.Empty,
                    description = text ?? string.Empty,
                };
                // 代理类不声明自己实现的接口 → 按指针包一层壳（与本项目其它地方一致）。
                var target = new ITooltipTarget(handler.Pointer);
                var source = new SimpleTooltipSource(main.Scene.uiCamera, target, content);
                main.Scene.ShowTooltip(new ITooltipSource(source.Pointer));
            }
            catch (Exception e)
            {
                WarnOnce("show", e);
            }
        }

        private static string GetHandlerText(TooltipHandler handler)
        {
            try
            {
                return handler.text;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static void SetHandlerText(TooltipHandler handler, string text)
        {
            try
            {
                handler.text = text;
            }
            catch (Exception e)
            {
                WarnOnce("settext", e);
            }
        }

        private static readonly HashSet<string> warned = new HashSet<string>();

        private static void WarnOnce(string key, Exception e)
        {
            if (!warned.Add(key))
                return;
            Plugin.Log?.LogWarning("[Tip] 原版提示调用失败（" + key + "）："
                + e.GetType().Name + ": " + e.Message);
        }

        /// <summary>诊断用：把某个行上的提示组件状态打出来（位置空间对不对只能看运行时的值）。</summary>
        public static string Describe(GameObject target)
        {
            if (target == null)
                return "（无目标）";
            try
            {
                var anchor = target.GetComponent<TooltipAnchor>();
                var handler = target.GetComponent<TooltipHandler>();
                if (anchor == null || handler == null)
                    return "无提示组件";
                var pos = anchor.Position;
                var pivot = anchor.Pivot;
                return target.name + "：anchor.pos=(x" + Round(pos.x) + ", y" + Round(pos.y)
                    + ", z" + Round(pos.z) + ") pivot=(x" + Round(pivot.x) + ", y" + Round(pivot.y)
                    + ") disabled=" + (anchor.IsDisabled ? 1 : 0)
                    + " text=「" + (handler.text ?? string.Empty) + "」";
            }
            catch (Exception e)
            {
                return "读提示状态失败：" + e.GetType().Name;
            }
        }

        private static string Round(float v)
        {
            return ((int)v).ToString();
        }
    }
}
