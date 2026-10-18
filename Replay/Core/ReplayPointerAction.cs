using System;
using System.Reflection;
using HarmonyLib;
using MVZ2.Managers;
using MVZ2Logic.Inputs;
using UnityEngine;

namespace Replay
{
    /// <summary>
    /// 把「输入层」的指针动作回放出去。
    ///
    /// <para><b>为什么必须单独做这一层：</b>游戏的鼠标输入有**两条**下游通路，它们彼此不重叠：</para>
    /// <list type="number">
    /// <item><b>路由事件</b>：Unity EventSystem → <c>GridController</c> / <c>EntityController</c> /
    /// <c>LawnRaycastReceiver</c> / <c>RuntimeBlueprintController</c> → <c>LevelController</c> 的
    /// 5 个 <c>UI_On*PointerInteractionCallback</c> → <c>level.DoHeldItemPointerEvent</c>。
    /// 这条我们已经在录制/回放（<c>ReplayPatches</c> 的 5 个前缀后缀）。</item>
    /// <item><b>全局回调</b>：<c>InputManager.UpdatePointer()</c> 每帧轮询真实鼠标的
    /// 3 个键 → <c>FlushPointerCaches()</c> → <c>Main.Game.RunCallbackFiltered(
    /// LogicCallbacks.POST_POINTER_ACTION, …)</c>。挂在这条上的东西**完全不经过**上面那 5 个回调。</item>
    /// </list>
    ///
    /// <para><b>漏掉第 ② 条的实际后果（实测）</b>：
    /// <c>RightMouseCancelHeldItemBehaviour</c> 就是挂在 ② 上的 —— 所以「右键取消手持物」
    /// 在回放里从来不会发生。后果是一条很隐蔽的连锁反应：</para>
    /// <code>
    /// 录制：t=556 选卡 9 → t=591 右键取消 → t=662 再选卡 9 → t=680 放下去 ✅
    /// 回放：t=556 选卡 9 → t=591 右键无效 → t=662 变成「取消」 → t=680 空手放不下 ❌
    /// </code>
    /// <para>（选卡时点已手持的卡，<c>PickupBlueprint</c> 会先 <c>CancelHeldItem</c> —— 所以
    /// 少一次取消，后面那一次点击的性质就整个反了。）</para>
    ///
    /// <para>同样的机制也顺带覆盖 <c>CrushingWalls</c> / <c>BreakoutBoard</c> 这类
    /// 「按下鼠标就有效果」的关卡内容。</para>
    ///
    /// <para><b>怎么发：</b>用反射调游戏自己的
    /// <c>Main.Game.RunCallbackFiltered(LogicCallbacks.POST_POINTER_ACTION, param, param.phase)</c>，
    /// 与 <c>InputManager</c> 完全同一条路径 —— 不自己重写取消逻辑，
    /// 这样游戏改了行为我们也自动跟着变。</para>
    /// </summary>
    internal static class ReplayPointerAction
    {
        private static bool ready;
        private static bool broken;

        private static object callbackType;      // LogicCallbacks.POST_POINTER_ACTION
        private static MethodInfo runFiltered;   // GlobalGame.RunCallbackFiltered<,>
        private static Type paramType;           // LogicCallbacks.PostPointerActionParams
        private static FieldInfo fType, fButton, fPhase, fPos, fDelta;

        public static bool Enabled
        {
            get
            {
                try { return ReplayConfig.PointerAction == null || ReplayConfig.PointerAction.Value; }
                catch (Exception) { return true; }
            }
        }

        /// <summary>已成功发出的指针动作数（写进播放小结）。</summary>
        public static int Fired { get; private set; }

        /// <summary>由**轨迹**（逐 tick）发出的次数，与 <see cref="Fired"/> 分开计数便于排障。</summary>
        public static int FiredFromTrack { get; private set; }

        public static void Reset()
        {
            Fired = 0;
            FiredFromTrack = 0;
            // ready / broken 保留：反射解析结果与本次播放无关，成功就不必再解析。
        }

        /// <summary>
        /// 把一条录制事件映射成输入层的指针动作并发出去。
        ///
        /// <para>映射：<c>Down→Press</c>、<c>Hold/Streak</c> 之类按下中的 →<c>Hold</c>、
        /// <c>Up→Release</c>、<c>Stay/Enter/Exit</c>（指针在但不按下）→<c>None</c>。
        /// 其余交互（Click / Drag / Drop / Key …）是路由层的概念，跳过。</para>
        ///
        /// <para>⚠️ 这条路径**只在「本 tick 没有逐 tick 轨迹覆盖」时才走**
        /// （见 <see cref="ReplayPointerTrajectory.TickCovered"/>，判定点在
        /// <c>ReplayPlayer.Dispatch</c>）。有轨迹时由 <see cref="FirePhases"/> 每 tick 发一次，
        /// 否则同一次按下会被发两遍（碾压墙一次点击会扣两次进度）。</para>
        /// </summary>
        public static void Fire(MainManager main, int pointerId, int interaction, float screenX, float screenY)
        {
            if (broken || main == null)
                return;
            PointerPhase phase;
            switch (interaction)
            {
                case (int)PointerInteraction.Down: phase = PointerPhase.Press; break;
                case (int)PointerInteraction.Hold: phase = PointerPhase.Hold; break;
                case (int)PointerInteraction.Up: phase = PointerPhase.Release; break;
                // 「指针在这里、但没按下」—— 原件里输入层每帧都会为非按下的键发 None
                // （并带上当时的屏幕位置），所以我们把 Stay/Enter/Exit 也按 None 发出去。
                // 这主要是为了 BreakoutBoard（打砖块）：它**不筛相位**，鼠标模式下
                // 每帧用 screenPos 把挡板移到指针处 —— 只发 Down/Hold/Up 会让挡板卡顿。
                // 按 Press 筛选的（右键取消、碎石墙）不受影响。
                case (int)PointerInteraction.Stay:
                case (int)PointerInteraction.Enter:
                case (int)PointerInteraction.Exit:
                case (int)PointerInteraction.Hover:
                    phase = PointerPhase.None;
                    break;
                default: return;
            }

            int type = InputHelper.GetPointerType(pointerId);
            int button = InputHelper.GetPointerButton(pointerId);
            if (Emit(main, type, button, phase, screenX, screenY))
                Fired++;
        }

        /// <summary>
        /// 由**逐 tick 指针轨迹**派发一次：对 3 个键各发一条，相位从按键掩码的**边沿**推出来。
        ///
        /// <para>原版里 <c>InputManager.UpdatePointer()</c> 每帧都为 3 个键各发一条
        /// （没按下就是 <c>None</c>），这里把它压缩成「每 tick 一次」：边沿（0→1）变
        /// <c>Press</c>、持续按住是 <c>Hold</c>、边沿（1→0）变 <c>Release</c>、一直没按是 <c>None</c>。
        /// 于是「点一次就扣一次进度」的碾压墙只扣一次，而「每帧读 screenPos」的打砖块也能跟到位。</para>
        ///
        /// <para>⚠️ 只对 <c>MOUSE</c> 发：键盘/触摸的指针动作本来就不走这条。</para>
        /// </summary>
        public static void FirePhases(MainManager main, float screenX, float screenY, int prevMask, int curMask)
        {
            if (broken || main == null)
                return;
            for (int button = 0; button < 3; button++)
            {
                int bit = 1 << button;
                bool was = (prevMask & bit) != 0;
                bool now = (curMask & bit) != 0;
                PointerPhase phase;
                if (now)
                    phase = was ? PointerPhase.Hold : PointerPhase.Press;
                else
                    phase = was ? PointerPhase.Release : PointerPhase.None;
                if (Emit(main, MVZ2Logic.Inputs.PointerTypes.MOUSE, button, phase, screenX, screenY))
                    FiredFromTrack++;
            }
        }

        /// <summary>真正把一条 <c>POST_POINTER_ACTION</c> 发给游戏。成功返回 true。</summary>
        private static bool Emit(MainManager main, int type, int button, PointerPhase phase,
            float screenX, float screenY)
        {
            if (broken || main == null)
                return false;
            try
            {
                EnsureInit(main);
                if (broken || runFiltered == null)
                    return false;

                var param = Activator.CreateInstance(paramType);
                fType.SetValue(param, type);
                fButton.SetValue(param, button);
                fPos.SetValue(param, new Vector2(screenX, screenY));
                fDelta.SetValue(param, Vector2.zero);
                fPhase.SetValue(param, phase);

                runFiltered.Invoke(main.Game, new object[] { callbackType, param, phase });
                return true;
            }
            catch (Exception e)
            {
                broken = true;
                Plugin.Log?.LogError("[Replay] 回放指针动作失败（已停用该功能）："
                    + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private static void EnsureInit(MainManager main)
        {
            if (ready || broken)
                return;
            ready = true;
            try
            {
                var lc = AccessTools.TypeByName("MVZ2Logic.Callbacks.LogicCallbacks");
                if (lc == null)
                    throw new InvalidOperationException("找不到 MVZ2Logic.Callbacks.LogicCallbacks");

                // 源码里是 `public readonly static … POST_POINTER_ACTION = new();`，
                // interop 把它生成成了静态**属性**（同时保留 NativeFieldInfoPtr）。
                var prop = AccessTools.Property(lc, "POST_POINTER_ACTION");
                if (prop != null)
                    callbackType = prop.GetValue(null, null);
                if (callbackType == null)
                {
                    var field = AccessTools.Field(lc, "POST_POINTER_ACTION");
                    if (field != null)
                        callbackType = field.GetValue(null);
                }
                if (callbackType == null)
                    throw new InvalidOperationException("拿不到 POST_POINTER_ACTION");

                paramType = AccessTools.Inner(lc, "PostPointerActionParams");
                if (paramType == null)
                    throw new InvalidOperationException("找不到嵌套类型 PostPointerActionParams");
                fType = paramType.GetField("type");
                fButton = paramType.GetField("button");
                fPos = paramType.GetField("screenPos");
                fDelta = paramType.GetField("delta");
                fPhase = paramType.GetField("phase");
                if (fType == null || fButton == null || fPos == null || fDelta == null || fPhase == null)
                    throw new InvalidOperationException("PostPointerActionParams 字段名对不上");

                // RunCallbackFiltered<TArgs, TFilter>（泛型 → 必须补上类型实参）
                var open = AccessTools.Method(main.Game.GetType(), "RunCallbackFiltered");
                if (open == null)
                    throw new InvalidOperationException("找不到 RunCallbackFiltered");
                if (open.IsGenericMethodDefinition)
                {
                    var phaseType = AccessTools.TypeByName("MVZ2Logic.Inputs.PointerPhase");
                    if (phaseType == null)
                        throw new InvalidOperationException("找不到 PointerPhase");
                    runFiltered = open.MakeGenericMethod(paramType, phaseType);
                }
                else
                {
                    // 万一 interop 把它生成成了非泛型（参数已经具体化），直接用。
                    runFiltered = open;
                }

                Plugin.Log?.LogInfo("[Replay] 指针动作回放已就绪（POST_POINTER_ACTION）");
            }
            catch (Exception e)
            {
                broken = true;
                Plugin.Log?.LogError("[Replay] 指针动作回放初始化失败（右键取消等功能在回放里不会生效）："
                    + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
