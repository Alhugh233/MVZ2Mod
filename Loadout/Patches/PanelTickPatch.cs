using System;
using HarmonyLib;
using MVZ2.Scenes;

namespace Loadout
{
    /// <summary>
    /// 面板的「每帧心跳」。
    ///
    /// 为什么不用自己的 MonoBehaviour：给游戏注入托管 MonoBehaviour 需要
    /// <c>ClassInjector.RegisterTypeInIl2Cpp</c>，那是 DSHCore 那套注入机制的一部分，
    /// 会重新引入 metadata v31 下「注入类字段不被 GC 追踪 / vtable 被重置」的风险。
    /// 本 mod 的目标是**零注入**，所以改为挂在官方本来就每帧都会跑的方法上。
    ///
    /// 挂点：<c>MVZ2.Scenes.MainSceneController.Update()</c>
    ///   - 它是主场景常驻对象的 Update，关卡内外都会跑；
    ///   - 官方的控制台热键（'/'）与截图热键（F12）就是在同一个方法里轮询的，
    ///     所以在这里轮询我们的热键，行为与原版热键完全一致（不会被输入框吃掉等）。
    ///
    /// 兼容性：这是**纯追加**的 postfix —— 不改参数、不改返回值、不跳过原方法，
    /// 对其它 mod 完全无感。因此不归入「危险功能」，始终生效。
    /// </summary>
    internal static class PanelTickPatch
    {
        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            try
            {
                var target = AccessTools.Method(typeof(MainSceneController), "Update");
                if (target == null)
                {
                    Plugin.Log?.LogError("[Panel] 找不到 MainSceneController.Update，面板将无法响应热键");
                    return;
                }
                var harmony = new Harmony(Plugin.GUID + ".tick");
                harmony.Patch(target, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(PanelTickPatch), nameof(Postfix))));
                Plugin.Log?.LogInfo("[Panel] 已挂 MainSceneController.Update postfix（面板心跳）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Panel] 挂心跳 postfix 失败：" + e);
            }
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                // 关卡切换是 Task：只能在主线程用帧轮询推进（见 LevelFacade.BeginLevelSwitch 的说明）。
                // 落在心跳里而不是面板里 —— 面板关着的时候切换照样要能完成。
                LevelFacade.TickPending();
                LoadoutPanel.Tick();
                SelfTest.Tick();
            }
            catch (Exception)
            {
                // 绝不能让异常冒回原生 Update。
            }
        }
    }
}
