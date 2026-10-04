using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MVZ2.Level;
using MVZ2.Level.Entities;
using MVZ2.Level.HeldItems;
using MVZ2.Level.Inputing;
using MVZ2.Managers;
using MVZ2.Scenes;
using MVZ2Logic.HeldItems;
using MVZ2Logic.Level;
using PVZEngine.Level;
using UnityEngine;

namespace Replay
{
    /// <summary>
    /// 补丁安装。
    ///
    /// ⚠️ 全部目标都是 **PVZEngine / MVZ2 的普通类型**（含静态扩展类 LogicLevelExt 一律不碰）。
    /// 本仓库踩过的坑：凡是在 <c>Plugin.Load()</c> 里反射带
    /// <c>[PropertyRegistryRegion]</c> 的 <c>MVZ2Logic.*</c> 静态类，都会提前跑它的
    /// <c>.cctor</c> → NRE → 被运行时永久缓存的 TypeInitializationException，
    /// 之后连原版功能一起废掉。本 mod 的挂钩点避开了这类类型，可以安全地在 Load 里挂。
    /// </summary>
    internal static class ReplayHooks
    {
        private static Harmony harmony;
        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;

            harmony = new Harmony(Plugin.GUID + ".hooks");
            int ok = 0, fail = 0;

            // ---- 漏斗：5 个调用点 ----
            // prefix = 回放期间拦掉玩家的真实输入（return false），postfix = 录制。
            Patch(typeof(LevelController), "UI_OnGridPointerInteractionCallback",
                new[] { typeof(int), typeof(int), typeof(PointerInteractionArgs) },
                nameof(ReplayPatches.GridPrefix), nameof(ReplayPatches.GridPostfix), ref ok, ref fail);

            Patch(typeof(LevelController), "UI_OnEntityPointerInteractionCallback",
                new[] { typeof(EntityController), typeof(PointerInteractionArgs) },
                nameof(ReplayPatches.EntityPrefix), nameof(ReplayPatches.EntityPostfix), ref ok, ref fail);

            Patch(typeof(LevelController), "UI_OnBlueprintPointerInteractionCallback",
                new[] { typeof(int), typeof(PointerInteractionArgs), typeof(bool) },
                nameof(ReplayPatches.BlueprintPrefix), nameof(ReplayPatches.BlueprintPostfix), ref ok, ref fail);

            Patch(typeof(LevelController), "UI_OnRaycastReceiverPointerInteractionCallback",
                new[] { typeof(LawnArea), typeof(PointerInteractionArgs) },
                nameof(ReplayPatches.LawnPrefix), nameof(ReplayPatches.LawnPostfix), ref ok, ref fail);

            // 蓝图热键不经过漏斗（直接构造 target 调 DoHeldItemPointerEvent），单独记。
            Patch(typeof(LevelController), "OnBlueprintKey",
                new[] { typeof(int), typeof(bool), typeof(int) },
                nameof(ReplayPatches.BlueprintKeyPrefix), nameof(ReplayPatches.BlueprintKeyPostfix), ref ok, ref fail);

            // ---- 手持物状态（漏斗之外的支路：工具按钮 / 工具热键）----
            //
            // 不挂 HeldItemComponent.SetHeldItem/ResetHeldItem/CancelHeldItem！
            // 实测（2026-10-04 test-9）：那三个方法本身没问题，但**触碰这个类型**
            // 就会触发它的静态构造函数
            //     public static readonly NamespaceID componentID = new NamespaceID(Global.BuiltinNamespace, "heldItem");
            // 而 Global.BuiltinNamespace 要等游戏初始化之后才可用 ⇒ 在 Plugin.Load 阶段
            // 解析到一个 SEHException，并且 TypeInitializationException 会被运行时
            // **永久缓存**（连带给以后想用这个类型的任何代码下毒）。
            //
            // 好在不需要它：手持物状态变化的全部来源是
            //   ① 漏斗内部（选卡/落地拾取）→ 已由上面的指针事件代表；
            //   ② 镐子/星之碎片/触发器的按钮与热键 → 就是下面这三个入口；
            //   ③ 自动派生（ValidateHeldItem / 行为 OnUpdate 的重置）→ 回放会自动重现。
            Patch(typeof(LevelController), "ClickPickaxe",
                Type.EmptyTypes,
                nameof(ReplayPatches.PickaxeClickPrefix), nameof(ReplayPatches.PickaxeClickPostfix), ref ok, ref fail);

            Patch(typeof(LevelController), "ClickStarshard",
                Type.EmptyTypes,
                nameof(ReplayPatches.StarshardClickPrefix), nameof(ReplayPatches.StarshardClickPostfix), ref ok, ref fail);

            Patch(typeof(LevelController), "ClickTrigger",
                Type.EmptyTypes,
                nameof(ReplayPatches.TriggerClickPrefix), nameof(ReplayPatches.TriggerClickPostfix), ref ok, ref fail);

            // ---- 时间轴与生命周期 ----
            // 前缀负责「把这一 tick 该发生的输入发出去」，后缀负责录制与指纹核对。
            Patch(typeof(LevelEngine), "Update",
                Type.EmptyTypes,
                nameof(ReplayPatches.EngineUpdatePrefix), nameof(ReplayPatches.EngineUpdatePostfix), ref ok, ref fail);

            Patch(typeof(LevelController), "StartGame",
                Type.EmptyTypes,
                null, nameof(ReplayPatches.StartGamePostfix), ref ok, ref fail);

            // LoadGame 有多个重载的风险，交给 AccessTools 自己找唯一那个。
            Patch(typeof(LevelController), "LoadGame",
                null,
                null, nameof(ReplayPatches.LoadGamePostfix), ref ok, ref fail);

            Patch(typeof(LevelController), "Dispose",
                Type.EmptyTypes,
                null, nameof(ReplayPatches.DisposePostfix), ref ok, ref fail);

            Patch(typeof(MainManager), "OnApplicationQuit",
                Type.EmptyTypes,
                nameof(ReplayPatches.QuitPrefix), null, ref ok, ref fail);

            // 自测的每帧驱动点。MainSceneController.Update 在主场景里**任何页面**都会跑
            // （主菜单/地图/关卡），所以拿它当心跳最省事；真正的自测开关在配置里。
            Patch(typeof(MainSceneController), "Update",
                Type.EmptyTypes,
                null, nameof(ReplayPatches.SceneUpdatePostfix), ref ok, ref fail);

            // ---- Global.Input 的指针位置（回放时报告回放到的那一刻）----
            // 这三个方法是纯静态工具，没有静态字段，在 Load 阶段反射它们不会触发
            // 那种依赖游戏初始化的 .cctor（仓库里踩过的是 MVZ2Logic 里带
            // [PropertyRegistryRegion] 的那类静态类，不是这个）。
            PatchIn(typeof(MVZ2Logic.Inputs.InputHelper), "GetPointerPosition",
                new[] { typeof(int), typeof(int) },
                typeof(ReplayInputPatches), nameof(ReplayInputPatches.GetPointerPositionByButtonPrefix), null, ref ok, ref fail);

            PatchIn(typeof(MVZ2Logic.Inputs.InputHelper), "GetPointerPosition",
                new[] { typeof(int) },
                typeof(ReplayInputPatches), nameof(ReplayInputPatches.GetPointerPositionByPointerIdPrefix), null, ref ok, ref fail);

            PatchIn(typeof(MVZ2Logic.Inputs.InputHelper), "TryGetPointerPosition",
                new[] { typeof(int), typeof(int), typeof(Vector2).MakeByRefType() },
                typeof(ReplayInputPatches), nameof(ReplayInputPatches.TryGetPointerPositionPrefix), null, ref ok, ref fail);

            Plugin.Log?.LogInfo("[Replay] 补丁安装完成：成功 " + ok + " 个，失败 " + fail + " 个");
            if (fail > 0)
                Plugin.Log?.LogWarning("[Replay] 有 " + fail + " 个补丁没挂上，录制内容会不完整");
        }

        /// <summary>挂一个补丁（prefix/postfix 都在 <see cref="ReplayPatches"/> 里）；失败只记日志。</summary>
        private static bool Patch(Type type, string method, Type[] args, string prefix, string postfix, ref int ok, ref int fail)
        {
            return PatchIn(type, method, args, typeof(ReplayPatches), prefix, postfix, ref ok, ref fail);
        }

        /// <summary>挂一个补丁，可指定 prefix/postfix 所在的类型。</summary>
        private static bool PatchIn(Type type, string method, Type[] args, Type holder, string prefix, string postfix, ref int ok, ref int fail)
        {
            try
            {
                MethodInfo target = args == null
                    ? AccessTools.Method(type, method)
                    : AccessTools.Method(type, method, args);
                if (target == null)
                {
                    fail++;
                    Plugin.Log?.LogError("[Replay] 找不到补丁目标：" + type.FullName + "." + method);
                    return false;
                }

                HarmonyMethod pre = null;
                HarmonyMethod post = null;
                if (prefix != null)
                {
                    var m = AccessTools.Method(holder, prefix);
                    if (m == null) { fail++; Plugin.Log?.LogError("[Replay] 找不到 prefix " + prefix); return false; }
                    pre = new HarmonyMethod(m);
                }
                if (postfix != null)
                {
                    var m = AccessTools.Method(holder, postfix);
                    if (m == null) { fail++; Plugin.Log?.LogError("[Replay] 找不到 postfix " + postfix); return false; }
                    post = new HarmonyMethod(m);
                }

                harmony.Patch(target, pre, post);
                ok++;
                return true;
            }
            catch (Exception e)
            {
                fail++;
                Plugin.Log?.LogError("[Replay] 挂补丁失败 " + type.FullName + "." + method + " —— " + Describe(e));
                return false;
            }
        }

        /// <summary>把异常链摊平；Harmony 的失败原因通常在内层。</summary>
        private static string Describe(Exception e)
        {
            var sb = new StringBuilder();
            var cur = e;
            int depth = 0;
            while (cur != null && depth < 6)
            {
                if (depth > 0)
                    sb.Append("  <-  ");
                sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
                cur = cur.InnerException;
                depth++;
            }
            return sb.ToString();
        }
    }
}
