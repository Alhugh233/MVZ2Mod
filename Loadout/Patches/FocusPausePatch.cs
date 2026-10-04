using System;
using HarmonyLib;
using MVZ2.Level;

namespace Loadout
{
    /// <summary>
    /// 「后台暂停」开着时，**面板开着就不要触发原版的失焦暂停**。
    ///
    /// <para>原版 <c>LevelController.UpdateFocusLost(bool)</c>（<c>OnApplicationFocus</c> 转发）里
    /// 会按全局选项 <c>pause_on_focus_lost</c> 把游戏暂停并弹出暂停对话框。
    /// 而面板的「后台暂停」用的是**同一个开关**（见 <see cref="PanelPause"/>）——
    /// 于是「面板开着 → 游戏速度已经是 0 → 切出去看一眼别的东西 → 回来发现多了一个暂停对话框」，
    /// 还得手动关掉才能继续用面板。</para>
    ///
    /// <para>做法：在这个方法上挂一个 prefix，面板开着时直接跳过。
    /// 它本身不改任何属性，只让原版那一句「失焦就暂停」不执行 —— 面板关掉之后照常生效。</para>
    /// </summary>
    internal static class FocusPausePatch
    {
        private static bool applied;
        private static bool logged;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            try
            {
                var target = AccessTools.Method(typeof(LevelController), "UpdateFocusLost", new[] { typeof(bool) });
                if (target == null)
                {
                    Plugin.Log?.LogError("[FocusPause] 找不到 LevelController.UpdateFocusLost，失焦暂停不受面板影响");
                    return;
                }
                var harmony = new Harmony(Plugin.GUID + ".focuspause");
                harmony.Patch(target, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(FocusPausePatch), nameof(Prefix))));
                Plugin.Log?.LogInfo("[FocusPause] 已挂失焦暂停补丁（面板开着时不触发原版失焦暂停）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[FocusPause] 挂补丁失败：" + e);
            }
        }

        /// <summary>返回 false = 跳过原版实现（= 不暂停、不弹对话框）。</summary>
        private static bool Prefix(bool focus)
        {
            try
            {
                // 只拦「失焦」那一次；重新获得焦点本来什么都不做。
                if (focus)
                    return true;
                if (!LoadoutPanel.IsOpen)
                    return true;
                if (!PanelPause.Enabled)
                    return true;
                if (!logged)
                {
                    logged = true;
                    Plugin.Log?.LogInfo("[FocusPause] 面板开着，已拦下一次失焦暂停");
                }
                return false;
            }
            catch (Exception)
            {
                return true;   // 判不出来就交给原版
            }
        }
    }
}
