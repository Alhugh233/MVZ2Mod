using System;
using MVZ2.Level;
using MVZ2.Level.Entities;
using MVZ2.Level.HeldItems;
using MVZ2.Level.Inputing;
using MVZ2.Managers;
using MVZ2Logic.HeldItems;
using MVZ2Logic.Level;
using PVZEngine.Entities;
using PVZEngine.Level;
using UnityEngine;

namespace Replay
{
    /// <summary>
    /// 所有 Harmony 补丁的实际方法体。
    ///
    /// ── 为什么挂这 5 个「调用点」而不是那个统一的漏斗 ──────────────────
    /// 理想的做法是只挂 <c>LogicLevelExt.DoHeldItemPointerEvent(LevelEngine, IHeldItemTarget, ...)</c>，
    /// 但 interop 的现实是：
    ///   · <c>IHeldItemTarget</c> 被生成成**类**（Il2CppObjectBase 子类）；
    ///   · 具体目标 <c>HeldItemTargetGrid</c> / <c>HeldItemTargetEntity</c> 被生成成
    ///     **Il2CppSystem.ValueType 的派生类**，且**不声明**这个接口；
    ///   · 而且它们是以「声明类型」装箱传进漏斗的（Il2CppObjectPool.Get&lt;IHeldItemTarget&gt;）。
    ///   ⇒ 在漏斗里 <c>target is HeldItemTargetGrid</c> **恒为假**，拿不到行列/实体 ID。
    /// 所以改挂 5 个调用点 —— 那里的参数都是 int / 枚举 / 强类型引用，读起来零风险，
    /// 而且拿到的正是「语义目标」（行列、实体 ID、卡槽号），本来就是回放最需要的形态。
    ///
    /// 这 5 个点就是玩家输入影响仿真的全部入口（0.7.0 test-9 实测枚举）。
    /// </summary>
    internal static class ReplayPatches
    {
        #region 回放期间的输入拦截
        /// <summary>
        /// 回放中把**玩家的**输入全部丢掉，只留关卡外操作（暂停 / 加速 / 血条 / Esc）。
        ///
        /// <para>注意两个必须同时成立的细节：</para>
        /// <list type="number">
        /// <item>注入走的就是这几个方法本身（反射调用），而 Harmony 的前缀对
        /// <c>il2cpp_runtime_invoke</c> 进来的调用**一样生效**，所以<br/>
        /// <c>ReplayPlayer.ShouldBlockInput</c> 必须在「正在注入」时为 false，
        /// 否则会把自己的事件也丢掉。这个开关在同一条线程上翻转，不需要锁。</item>
        /// <item>拦在**这 8 个入口**上而不是 <c>UpdateKeys</c>：键盘热键（数字选卡、
        /// 镐子/星之碎片/触发器）最终也落到这几个方法上，所以一处拦就全拦住了。</item>
        /// </list>
        /// </summary>
        private static bool Blocked()
        {
            return ReplayPlayer.ShouldBlockInput;
        }

        /// <summary>指针事件的坐标：屏幕位置 + 射线命中的世界位置。</summary>
        private static void ReadPos(PointerInteractionArgs args, out Vector2 screen, out Vector3 world)
        {
            screen = Vector2.zero;
            world = Vector3.zero;
            if (args == null)
                return;
            try { screen = args.position; }
            catch (Exception) { }
            try
            {
                var rr = args.pointerCurrentRaycast;
                if (rr != null)
                    world = rr.worldPosition;
            }
            catch (Exception) { }
        }
        #endregion

        #region 漏斗：5 个调用点
        /// <summary>进漏斗（计数器，不是 bool —— 防止嵌套时提前清零）。</summary>
        public static void FunnelEnter()
        {
            ReplayRecorder.EnterFunnel();
        }

        /// <summary>格子：放置 / 移动器械，以及草坪格子上的各种互动。</summary>
        public static bool GridPrefix()
        {
            return !Blocked();
        }

        public static void GridPostfix(LevelController __instance, int lane, int column, PointerInteractionArgs args)
        {
            ReplayRecorder.ExitFunnel();
            ReadPos(args, out var screen, out var world);
            ReplayRecorder.RecordGrid(__instance, lane, column, args.pointerId, (int)args.button, (int)args.interaction, screen, world);
        }

        /// <summary>实体：工具（镐子/星之碎片/触发）与手持物对实体使用。</summary>
        public static bool EntityPrefix(EntityController entityCtrl)
        {
            // ⚠️ 锚点身份必须在**事件发生之前**抓：这次事件本身可能把目标弄没
            // （镐子会 entity.Die、拾取物会被 Collect），而后缀是在事件之后跑的。
            // 另外分层（承载位/保护位/主要位/工具位）也只有在那时才是完整的。
            ReplayRecorder.CaptureEntityAnchor(entityCtrl);
            return !Blocked();
        }

        public static void EntityPostfix(LevelController __instance, EntityController entityCtrl, PointerInteractionArgs args)
        {
            ReplayRecorder.ExitFunnel();
            ReadPos(args, out var screen, out var world);
            ReplayRecorder.RecordEntity(__instance,
                args.pointerId, (int)args.button, (int)args.interaction, screen, world);
        }

        /// <summary>点选蓝图（选卡槽）。</summary>
        public static bool BlueprintPrefix()
        {
            return !Blocked();
        }

        public static void BlueprintPostfix(LevelController __instance, int index, PointerInteractionArgs args, bool conveyor)
        {
            ReplayRecorder.ExitFunnel();
            ReadPos(args, out var screen, out var world);
            ReplayRecorder.RecordBlueprint(__instance, index, conveyor, args.pointerId, (int)args.button, (int)args.interaction, screen, world);
        }

        /// <summary>蓝图热键（数字键选卡）。</summary>
        public static bool BlueprintKeyPrefix()
        {
            return !Blocked();
        }

        public static void BlueprintKeyPostfix(LevelController __instance, int i, bool conveyor, int key)
        {
            ReplayRecorder.RecordBlueprintKey(__instance, i, conveyor, key);
        }

        /// <summary>草坪区域（点空白处取消手持物等）。</summary>
        public static bool LawnPrefix()
        {
            return !Blocked();
        }

        public static void LawnPostfix(LevelController __instance, LawnArea area, PointerInteractionArgs args)
        {
            ReplayRecorder.ExitFunnel();
            ReadPos(args, out var screen, out var world);
            ReplayRecorder.RecordLawn(__instance, (int)area, args.pointerId, (int)args.button, (int)args.interaction, screen, world);
        }
        #endregion

        #region 手持物状态（漏斗之外的支路：工具按钮 / 工具热键）
        // 为什么不挂 HeldItemComponent.SetHeldItem/ResetHeldItem/CancelHeldItem：见 ReplayHooks 里的注释。
        // 简单说：那个类型的静态构造函数依赖游戏初始化，在 Plugin.Load 阶段碰不得。
        public static bool PickaxeClickPrefix()
        {
            return !Blocked();
        }

        public static void PickaxeClickPostfix(LevelController __instance)
        {
            ReplayRecorder.RecordToolClick(__instance, "pickaxe");
        }

        public static bool StarshardClickPrefix()
        {
            return !Blocked();
        }

        public static void StarshardClickPostfix(LevelController __instance)
        {
            ReplayRecorder.RecordToolClick(__instance, "starshard");
        }

        public static bool TriggerClickPrefix()
        {
            return !Blocked();
        }

        public static void TriggerClickPostfix(LevelController __instance)
        {
            ReplayRecorder.RecordToolClick(__instance, "trigger");
        }
        #endregion

        #region 时间轴与生命周期
        /// <summary>
        /// 每个逻辑 tick 的**开始**（<c>LevelEngine.Update</c> 之前）。
        /// 此刻 <c>AddLevelTime()</c> 还没跑，<c>GetLevelTime()==T</c> 正好对应
        /// 「第 T 个 tick 已结束、第 T+1 个还没跑」，与录制时事件打戳的位置一致。
        /// </summary>
        public static void EngineUpdatePrefix(LevelEngine __instance)
        {
            ReplayPlayer.InjectDue(__instance);
        }

        /// <summary>每个逻辑 tick 的末尾（<c>LevelEngine.Update</c> 之后）。</summary>
        public static void EngineUpdatePostfix(LevelEngine __instance)
        {
            ReplayRecorder.NotifyTick(__instance);
            ReplayPlayer.AfterTick(__instance);
        }

        /// <summary>新关卡开局：选卡/制品/难度/诅咒都已应用完（<c>ApplyChoose</c> 在 StartGame 内）。</summary>
        public static void StartGamePostfix(LevelController __instance)
        {
            ReplayRecorder.NotifyLevelReady(__instance, false);
        }

        /// <summary>从存档载入关卡。</summary>
        public static void LoadGamePostfix(LevelController __instance, bool __result)
        {
            if (__result)
                ReplayRecorder.NotifyLevelReady(__instance, true);
        }

        /// <summary>关卡结束（退出关卡 / 重载 / 回标题）。</summary>
        public static void DisposePostfix(LevelController __instance)
        {
            ReplayPlayer.NotifyLevelDispose(__instance);
            ReplayRecorder.NotifyLevelDispose(__instance);
        }

        /// <summary>退出游戏（否则最后一局会丢）。</summary>
        public static void QuitPrefix()
        {
            ReplayPlayer.Stop("application-quit");
            ReplayRecorder.NotifyQuit();
        }

        /// <summary>主场景每帧（自测 / 播放器 / 回放页的驱动点）。</summary>
        public static void SceneUpdatePostfix(MVZ2.Scenes.MainSceneController __instance)
        {
            ReplaySelfTest.Tick(__instance);
            ReplayGuiTest.Tick(__instance);
            ReplayPlayer.Tick(__instance);
            ReplayPage.Tick();
        }
        #endregion
    }

    /// <summary>
    /// 把 <c>Global.Input</c> 的「当前指针位置」接管成回放位置。
    ///
    /// <para>录制下来的指针事件已经带位置，但还有几个地方**不看回调参数**、
    /// 直接问实时输入（典型：<c>CombatHeldItemBehaviour</c> 与 <c>SwordHeldItemBehaviour</c>
    /// 里的 <c>Global.Input.GetPointerScreenPosition(...)</c>）。
    /// 不接管的话，玩家鼠标停在哪里就会影响回放里剑砍的方向之类的东西。</para>
    ///
    /// <para>方法很小，IL2CPP 有可能把它内联进调用方 —— 真被内联了前缀就永远不会跑，
    /// 所以播放总结里会报「改写次数」：一直是 0 就说明这个补丁没生效（不影响其它功能）。</para>
    /// </summary>
    internal static class ReplayInputPatches
    {
        public static bool GetPointerPositionByButtonPrefix(int type, int button, ref Vector2 __result)
        {
            if (ReplayPlayer.TryOverridePointerPosition(type, button, out var pos))
            {
                __result = pos;
                return false;
            }
            return true;
        }

        public static bool GetPointerPositionByPointerIdPrefix(int pointerId, ref Vector2 __result)
        {
            if (ReplayPlayer.TryOverridePointerPosition(-1, 0, out var pos))
            {
                __result = pos;
                return false;
            }
            return true;
        }

        public static bool TryGetPointerPositionPrefix(int type, int button, ref Vector2 screenPosition, ref bool __result)
        {
            if (ReplayPlayer.TryOverridePointerPosition(type, button, out var pos))
            {
                screenPosition = pos;
                __result = true;
                return false;
            }
            return true;
        }
    }
}
