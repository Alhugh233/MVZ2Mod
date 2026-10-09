using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MVZ2.Level.UI;
using UnityEngine;

namespace DSHCore
{
    /// <summary>
    /// 关卡 HUD 的 Animator 守卫 —— 补上原版 <see cref="LevelUIPreset"/> 漏掉的那道判断。
    ///
    /// <para><b>原版的漏洞：</b><c>ShowHugeWaveText()</c> / <c>ShowFinalWaveText()</c> /
    /// <c>ShowReadySetBuild()</c> 直接调 <c>animator.SetTrigger(...)</c>，**没有任何守卫**
    /// （<c>MVZ2/Level/UI/LevelUIPreset.cs</c>）。而同一个类里其它写 Animator 的地方都有：
    /// <c>UpdateFrame</c> 判 <c>animator.gameObject.activeInHierarchy</c>，
    /// <c>LevelUI.SetRaycastDisabled</c> 同样判。偏偏大波/最后一波提示这三处没判。</para>
    ///
    /// <para><b>为什么这三处特别容易炸：</b>这个 Animator 的组件在 prefab 里就是
    /// <c>m_Enabled = 0</c>，<c>Awake()</c> 又执行一次 <c>animator.enabled = false</c> ——
    /// 也就是说它**完全靠游戏自己每帧 <c>animator.Update(dt)</c> 养着**
    /// （<c>LevelController.UpdateFrameUI</c> → <c>LevelUIPreset.UpdateFrame</c>）。
    /// 一旦那条每帧链路断掉（HUD 被反激活、或 <c>UpdateFrame</c> 在到达 <c>uiPreset.UpdateFrame</c>
    /// 之前抛了异常把整帧截断 —— 例如克隆 UI 把共享材质弄坏后游戏自己的 HUD 每帧抛 NRE），
    /// 这个 Animator 就没有内部实例，Unity 在 <c>Animator.SetTrigger</c> 里直接抛
    /// <c>NullReferenceException</c>（栈顶是 <c>UnityEngine.Animator.SetTrigger</c>）。</para>
    ///
    /// <para><b>玩家侧看到的现象：</b>这个回调是从 <c>WaveStageBehaviour</c> 的大波计时器发出来的，
    /// 包在 <c>StageDefinition.ExecuteBehaviours</c> 的 try/catch 里，于是打出来的是一行
    /// 「关卡行为 WaveStageBehaviour 在执行 Update 时出现错误：NullReferenceException」——
    /// 看起来像关卡/大波逻辑出问题，实际起因（HUD 的每帧驱动链断了）早就发生过了。</para>
    ///
    /// <para><b>本补丁只做守卫，不改行为：</b>接管这三个方法 —— 先按原版别处的纪律判 Animator
    /// 可用（非空、未销毁、<c>activeInHierarchy</c>），可用就自己调 <c>SetTrigger</c>（异常也吃掉），
    /// 不可用就跳过并只记一条 WarnOnce。**正常路径与原版完全一致**，只是不再把 NRE 抛进关卡行为里。
    /// 注意它**只是安全网**：真正该修的是"谁弄坏了关卡 UI / 让它的每帧驱动链断掉"
    /// （克隆 UI 必须过 <see cref="DSHCore.Ui.UiClone"/> / <see cref="DSHCore.Ui.UiKit.ReplaceMask"/> 那套 Mask 纪律）。</para>
    /// </summary>
    internal static class LevelUiAnimatorGuard
    {
        /// <summary>三个没有守卫的 HUD 提示方法（方法名 → Animator 的 trigger 参数名）。</summary>
        private static readonly string[] Methods = { "ShowHugeWaveText", "ShowFinalWaveText", "ShowReadySetBuild" };

        private static bool applied;
        private static readonly HashSet<string> warned = new HashSet<string>();

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            try
            {
                var harmony = new Harmony("qoznos.mvz2.core.leveluianimatorguard");
                var prefix = new HarmonyMethod(typeof(LevelUiAnimatorGuard), nameof(Prefix));
                var patched = 0;
                for (var i = 0; i < Methods.Length; i++)
                {
                    var target = AccessTools.Method(typeof(LevelUIPreset), Methods[i]);
                    if (target == null)
                    {
                        Plugin.Log?.LogWarning("[LevelUiAnimatorGuard] 找不到 LevelUIPreset." + Methods[i]);
                        continue;
                    }
                    harmony.Patch(target, prefix: prefix);
                    patched++;
                }
                Plugin.Log?.LogInfo("[LevelUiAnimatorGuard] 已接管 " + patched + "/" + Methods.Length
                    + " 个 HUD 提示方法（原版这三处漏了 Animator 守卫）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[LevelUiAnimatorGuard] 挂补丁失败：" + e);
            }
        }

        [HarmonyPrefix]
        private static bool Prefix(LevelUIPreset __instance, MethodBase __originalMethod)
        {
            var trigger = TriggerOf(__originalMethod);

            Animator animator;
            if (!TryResolveAnimator(__instance, out animator))
            {
                // 连 animator 字段都反射不到 —— 本补丁在这台机器上等于没生效，
                // 那就**别改变原版行为**，原样交给它自己处理。
                WarnOnce("field", trigger, "反射不到 LevelUIPreset.animator 字段，本补丁跳过（原样交给原版）");
                return true;
            }

            if (animator == null || animator.Pointer == IntPtr.Zero)
            {
                WarnOnce("null", trigger, "LevelUIPreset.animator 为空或已被销毁");
                return false;
            }

            try
            {
                var go = animator.gameObject;
                if (go == null || !go.activeInHierarchy)
                {
                    // 与 UpdateFrame / LevelUI.SetRaycastDisabled 同一道守卫：原版这两处就是这么判的。
                    WarnOnce("inactive", trigger, "HUD 预设的 GameObject 不在激活状态");
                    return false;
                }
                animator.SetTrigger(trigger);
            }
            catch (Exception e)
            {
                // Animator 内部实例不存在时 Unity 就是这样抛的（详见类注释）。
                WarnOnce("set", trigger, e.GetType().Name + ": " + e.Message);
            }

            // 无论成败都不再执行原方法：它没有守卫，会把异常抛进关卡行为里。
            return false;
        }

        /// <summary>
        /// <c>LevelUIPreset.animator</c> 是私有字段（<c>Animator animator = null!;</c>），
        /// 只能反射拿。这里一次 <c>GetValue</c> 足够便宜（每次大波/最后一波才调一次）。
        ///
        /// <para>返回 <c>false</c> 只在"字段本身找不到"时发生 —— 那种情况说明本补丁失效，
        /// 调用方应当原样放行原方法，绝不因为补丁自己出问题而改变游戏行为。</para>
        /// </summary>
        private static bool TryResolveAnimator(LevelUIPreset instance, out Animator animator)
        {
            animator = null;
            if (instance == null)
                return false;
            FieldInfo field;
            try
            {
                field = AccessTools.Field(typeof(LevelUIPreset), "animator");
            }
            catch (Exception)
            {
                field = null;
            }
            if (field == null)
                return false;
            try
            {
                animator = field.GetValue(instance) as Animator;
            }
            catch (Exception)
            {
                animator = null;
            }
            return true;
        }

        private static string TriggerOf(MethodBase method)
        {
            var name = method == null ? null : method.Name;
            switch (name)
            {
                case "ShowFinalWaveText":
                    return "FinalWave";
                case "ShowReadySetBuild":
                    return "ReadySetBuild";
                default:
                    return "HugeWave";
            }
        }

        private static void WarnOnce(string key, string trigger, string reason)
        {
            lock (warned)
            {
                if (!warned.Add(key))
                    return;
            }
            Plugin.Log?.LogWarning("[LevelUiAnimatorGuard] 跳过 HUD 提示 " + trigger + "：" + reason
                + "（原版这三处没有 Animator 守卫，会直接抛 NullReferenceException）。"
                + "本补丁只挡报错 —— 真正的原因要查「谁弄坏了关卡 UI / 让它每帧的 UpdateFrame 断掉」，"
                + "克隆 UI 请一律走 UiClone / UiKit.ReplaceMask 的 Mask 纪律。");
        }
    }
}
