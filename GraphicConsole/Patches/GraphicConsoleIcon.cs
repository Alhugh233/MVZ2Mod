using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace GraphicConsole
{
    /// <summary>
    /// 面板入口图标 —— 用原版 **debug 模式下左上角那个半透明蜘蛛小图标**。
    ///
    /// 做法：把原图标**复制一份**挂到它下面，由我们的副本负责打开面板；
    /// 同时把原图标藏起来（避免左上角出现两个一模一样的图标）。
    /// 因为原图标本来就是"打开控制台"的入口，而控制台已经被面板取代
    /// （面板底部的「原版控制台」按钮、以及 `/` 热键都还在），所以这样分配入口是合理的。
    ///
    /// 为什么要 patch <c>MainSceneUI.SetDebugIconActive</c> 而不是自己控制显隐：
    ///   这个方法由 <c>MainSceneController.Update</c> 每帧调用，参数就是"官方认为该不该显示"
    ///   （debug 存档 + 可用页面）。挂它的 postfix 就等于免费拿到了官方的显示条件，
    ///   我们只需要"原文案改成显示我们的图标"。
    ///
    /// 兼容性：这是**改写返回值/替换显示对象**的行为改动，所以归入 Standard 档，
    /// 可以用「兼容性等级」或单项开关关掉（关掉后原图标恢复原样、功能退回热键）。
    /// </summary>
    internal static class GraphicConsoleIcon
    {
        public const string IconName = "GraphicConsoleIcon";

        private static bool applied;
        private static GameObject originalIcon;
        private static GameObject ourIcon;
        private static bool lastDesired;
        private static bool warnedOnce;

        public static bool IsReady { get { return UiFinder.IsAlive(ourIcon); } }

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            try
            {
                var target = AccessTools.Method(typeof(MVZ2.Scenes.MainSceneUI), "SetDebugIconActive");
                if (target == null)
                {
                    Plugin.Log?.LogError("[Icon] 找不到 MainSceneUI.SetDebugIconActive，面板入口图标未接管");
                    return;
                }
                var harmony = new Harmony(Plugin.GUID + ".icon");
                harmony.Patch(target, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(GraphicConsoleIcon), nameof(SetDebugIconActivePostfix))));
                Plugin.Log?.LogInfo("[Icon] 已挂 SetDebugIconActive postfix（左上角蜘蛛图标作为面板入口）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Icon] 挂补丁失败：" + e);
            }
        }

        [HarmonyPostfix]
        private static void SetDebugIconActivePostfix(MVZ2.Scenes.MainSceneUI __instance, bool active)
        {
            try
            {
                if (!CompatConfig.IsEnabled(GcFeature.DebugIconOpensPanel))
                {
                    // 关闭时：让原图标恢复原样，并清掉我们的副本。
                    RestoreOriginal();
                    return;
                }
                Resolve(__instance);
                if (originalIcon == null)
                    return;

                // 原图标永远不显示（它的职责由我们的副本承担）。
                if (originalIcon.activeSelf)
                    originalIcon.SetActive(false);

                if (!UiFinder.IsAlive(ourIcon))
                    CreateIcon(originalIcon);
                if (UiFinder.IsAlive(ourIcon) && ourIcon.activeSelf != active)
                    ourIcon.SetActive(active);
                lastDesired = active;
            }
            catch (Exception e)
            {
                if (!warnedOnce)
                {
                    warnedOnce = true;
                    Plugin.Log?.LogWarning("[Icon] 接管入口图标失败：" + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        private static void Resolve(MVZ2.Scenes.MainSceneUI ui)
        {
            if (UiFinder.IsAlive(originalIcon))
                return;
            // 场景切换后旧对象会销毁，重新解析一次。
            try
            {
                var source = ui.debugConsoleIcon;
                originalIcon = source == null ? null : source.gameObject;
            }
            catch (Exception)
            {
                originalIcon = null;
            }
            if (originalIcon != null)
            {
                ourIcon = null;
                Plugin.Log?.LogInfo("[Icon] 已找到原版调试图标：" + UiFinder.PathOf(originalIcon.transform));
            }
        }

        private static void CreateIcon(GameObject template)
        {
            var parent = template.transform.parent;
            if (parent == null)
                return;
            ourIcon = UiFactory.Clone(template, parent, IconName);
            if (ourIcon == null)
                return;

            // 摆到原图标下面一行（左上角是纵向排列，往下偏移一个图标高度 + 间距）。
            try
            {
                var templateRt = UiFactory.RectOf(template);
                var rt = UiFactory.RectOf(ourIcon);
                if (templateRt != null && rt != null)
                {
                    rt.anchorMin = templateRt.anchorMin;
                    rt.anchorMax = templateRt.anchorMax;
                    rt.pivot = templateRt.pivot;
                    rt.sizeDelta = templateRt.sizeDelta;
                    var step = Mathf.Abs(templateRt.sizeDelta.y);
                    if (step < 1f)
                        step = templateRt.rect.height;
                    if (step < 1f)
                        step = 48f;
                    rt.anchoredPosition = templateRt.anchoredPosition + new Vector2(0f, -step - 8f);
                }
            }
            catch (Exception)
            {
            }

            // 半透明（需求：半透明的蜘蛛小图标）
            try
            {
                var image = ourIcon.GetComponent<Image>();
                if (image != null)
                {
                    var c = image.color;
                    image.color = new Color(c.r, c.g, c.b, 0.65f);
                }
            }
            catch (Exception)
            {
            }

            // 点击 → 开关面板
            try
            {
                var icon = ourIcon.GetComponent<MVZ2.Scenes.DebugConsoleIcon>();
                if (icon != null)
                {
                    var managed = new System.Action<MVZ2.Scenes.DebugConsoleIcon>(OnIconClicked);
                    var il2cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<MVZ2.Scenes.DebugConsoleIcon>>(managed);
                    UiFactory.KeepAlive(ourIcon, managed, il2cpp);
                    icon.add_OnClick(il2cpp);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Icon] 挂图标点击回调失败：" + e.GetType().Name + ": " + e.Message);
            }

            ourIcon.SetActive(lastDesired);
            Plugin.Log?.LogInfo("[Icon] 已创建面板入口图标（" + UiFinder.PathOf(ourIcon.transform) + "）");
        }

        private static void OnIconClicked(MVZ2.Scenes.DebugConsoleIcon icon)
        {
            try
            {
                Plugin.Log?.LogInfo("[Icon] 蜘蛛图标被点击 → 开关面板");
                ConsolePanel.Toggle();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Icon] 图标点击处理失败：" + e);
            }
        }

        /// <summary>功能关闭时恢复原状。</summary>
        private static void RestoreOriginal()
        {
            try
            {
                if (UiFinder.IsAlive(ourIcon))
                {
                    ourIcon.SetActive(false);
                    UnityEngine.Object.Destroy(ourIcon);
                }
            }
            catch (Exception)
            {
            }
            ourIcon = null;
            try
            {
                if (UiFinder.IsAlive(originalIcon) && originalIcon.activeSelf != lastDesired)
                    originalIcon.SetActive(lastDesired);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>场景切换时丢掉引用。</summary>
        public static void ResetOnSceneChange()
        {
            originalIcon = null;
            ourIcon = null;
            lastDesired = false;
        }

        public static string Describe()
        {
            return "入口图标: " + (IsReady ? "已创建" : "未创建")
                + "，原图标=" + (UiFinder.IsAlive(originalIcon) ? "已找到" : "未找到")
                + "，本帧期望显示=" + lastDesired;
        }
    }
}
