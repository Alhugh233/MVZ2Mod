using System;
using DSHCore.Ui;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MVZ2.Scenes;
using UnityEngine;

namespace Loadout
{
    /// <summary>
    /// 把左上角那个「调试控制台图标」变成**面板入口**。
    ///
    /// <para>需求是「用一个可视化的命令面板**替代**原版的输入窗口」，所以入口用原版的大蜘蛛图标
    /// 最自然。做法是**克隆一份原图标**当我们的入口，把原图标藏起来（它点开的是原版控制台）——
    /// 而不是去劫持原图标的点击，那样会连带把原版控制台一起打开。</para>
    ///
    /// <para>为什么挂 <c>MainSceneUI.SetDebugIconActive</c>：官方<b>每帧</b>都在用它设置图标的显隐
    /// （受「是否 debug 存档 / 是否在可用页面」这些条件控制），所以挂它的 postfix 就等于免费拿到
    /// 官方的显示条件 —— 我们只需要把"显示"转发给我们自己的副本。</para>
    /// </summary>
    internal static class DebugIconEntry
    {
        private static bool applied;
        private static GameObject entry;
        private static System.Action<DebugConsoleIcon> onClickManaged;
        private static Il2CppSystem.Action<DebugConsoleIcon> onClickIl2Cpp;
        private static int lastClickFrame = -9999;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            try
            {
                var target = AccessTools.Method(typeof(MainSceneUI), "SetDebugIconActive");
                if (target == null)
                {
                    Plugin.Log?.LogError("[Icon] 找不到 MainSceneUI.SetDebugIconActive，面板图标入口不可用");
                    return;
                }
                var harmony = new Harmony(Plugin.GUID + ".icon");
                harmony.Patch(target, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(DebugIconEntry), nameof(Postfix))));
                Plugin.Log?.LogInfo("[Icon] 已挂 MainSceneUI.SetDebugIconActive postfix（面板入口 = 图标副本）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Icon] 挂图标 postfix 失败：" + e);
            }
        }

        [HarmonyPostfix]
        private static void Postfix(MainSceneUI __instance, bool active)
        {
            try
            {
                if (__instance == null)
                    return;
                var original = __instance.debugConsoleIcon;
                if (!UiFinder.IsAlive(original))
                    return;

                if (!UiFinder.IsAlive(entry))
                {
                    entry = UiClone.Instantiate(original.gameObject, original.transform.parent, "DSH_LoadoutEntry");
                    if (!UiFinder.IsAlive(entry))
                    {
                        Plugin.LogOnce("icon-clone", "[Icon] 图标副本创建失败，入口仍用原图标（点开的是原版控制台）");
                        return;
                    }
                    var icon = entry.GetComponent<DebugConsoleIcon>();
                    if (icon != null)
                    {
                        onClickManaged = new System.Action<DebugConsoleIcon>(OnEntryClick);
                        onClickIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<DebugConsoleIcon>>(onClickManaged);
                        icon.add_OnClick(onClickIl2Cpp);
                    }
                    else
                    {
                        Plugin.LogOnce("icon-comp", "[Icon] 图标副本上没有 DebugConsoleIcon 组件，点击不会开面板");
                    }
                    // 不挂悬停提示：图标本身就是入口，提示会挡住旁边的原版 UI，
                    // 而且它每次悬停都会弹一次（需求里明确不要）。
                    Plugin.Log?.LogInfo("[Icon] 面板图标入口已创建：" + UiFinder.PathOf(entry.transform));
                }

                // 原图标藏起来（避免两个一样的图标并排，而且它会开原版控制台）。
                try
                {
                    if (original.gameObject.activeSelf)
                        original.gameObject.SetActive(false);
                }
                catch (Exception) { }

                try
                {
                    if (entry.activeSelf != active)
                        entry.SetActive(active);
                }
                catch (Exception) { }
            }
            catch (Exception e)
            {
                Plugin.LogOnce("icon-err", "[Icon] 图标入口异常：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void OnEntryClick(DebugConsoleIcon icon)
        {
            try
            {
                var frame = Time.frameCount;
                var delta = frame - lastClickFrame;
                lastClickFrame = frame;

                // 防抖：官方图标自己也会在这一帧里被重设显隐（`SetDebugIconActive` 每帧都在跑），
                // 实测一次交互可能落到多个回调上。20 帧内只让第一次生效 —— 人手上按不出这个间隔，
                // 所以不会误伤正常开关。
                if (delta < 20)
                {
                    Plugin.LogOnce("icon-debounce",
                        "[Icon] 瞬间收到多次点击回调，已按防抖忽略（只生效一次）");
                    return;
                }

                LoadoutPanel.Toggle();
                Plugin.Log?.LogInfo("[Icon] 面板 " + (LoadoutPanel.IsOpen ? "已打开" : "已关闭")
                    + "（剩余错误：" + (LoadoutPanel.LastError ?? "无") + "）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Icon] 开关面板失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        public static string Describe()
        {
            return "图标入口=" + (UiFinder.IsAlive(entry) ? UiFinder.PathOf(entry.transform) : "未创建");
        }
    }
}
