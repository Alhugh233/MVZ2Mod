using System;
using HarmonyLib;
using MVZ2.Level;

namespace GraphicConsole
{
    /// <summary>
    /// 面板暂停：postfix <c>LevelController.GetGameSpeed()</c>，面板打开时返回 0。
    ///
    /// 详细理由见 <see cref="PanelPause"/>。一句话：**改速度而不是改"运行中"状态**，
    /// 这样关卡逻辑冻结、但蓝图 / 镐子 / 星之碎片与草坪点击全部照常可用
    /// —— 需求里"面板显示时也能正常操作游戏"就是这个意思。
    ///
    /// 兼容性：这是**只改返回值、不改参数、不跳过原方法**的 postfix，
    /// 对其它 mod 无感；但它确实改变了官方行为，所以归入 Standard 档，可被关掉。
    /// 关掉后 <c>GetGameSpeed</c> 原样返回，一切回归官方行为（面板只是显示，不再冻结关卡）。
    /// </summary>
    internal static class GameSpeedPatch
    {
        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            try
            {
                var target = AccessTools.Method(typeof(LevelController), "GetGameSpeed");
                if (target == null)
                {
                    Plugin.Log?.LogError("[Pause] 找不到 LevelController.GetGameSpeed，面板暂停不生效");
                    return;
                }
                var harmony = new Harmony(Plugin.GUID + ".gamespeed");
                harmony.Patch(target, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(GameSpeedPatch), nameof(GetGameSpeedPostfix))));
                Plugin.Log?.LogInfo("[Pause] 已挂 LevelController.GetGameSpeed postfix（面板打开时速度=0）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Pause] 挂 GetGameSpeed postfix 失败：" + e);
            }
        }

        [HarmonyPostfix]
        private static void GetGameSpeedPostfix(ref float __result)
        {
            try
            {
                if (__result <= 0f)
                    return;
                if (PanelPause.Active)
                    __result = 0f;
            }
            catch (Exception)
            {
                // 绝不能让异常冒回原生调用栈（这个方法每帧都被调用）。
            }
        }
    }
}
