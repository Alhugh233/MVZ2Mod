using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MVZ2.Level;
using MVZ2.Level.Dialogs;
using MVZ2.Level.Entities;
using MVZ2.Level.Inputing;
using MVZ2.Managers;
using MVZ2.Scenes;
using MVZ2Logic;
using MVZ2Logic.Level;
using MVZ2Logic.Serialization;
using PVZEngine;
using PVZEngine.Entities;
using PVZEngine.Level;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Replay
{
    /// <summary>
    /// 回放播放器。
    ///
    /// ── 播放模型 ──────────────────────────────────────────────────────
    /// 1. <b>重建起点</b>：把文件里的起始关键帧（整关 <c>SerializableLevelController</c>）
    ///    反序列化后交给 <c>LevelController.LoadGame</c>。它会把关卡时间一并恢复成
    ///    关键帧那一刻的值，所以 tick 0 与录制时是同一个状态。
    /// 2. <b>推进仿真</b>：不动 <c>Time.timeScale</c>、不动 tick 长度 —— 玩家自己按暂停/加速，
    ///    每个 tick 的内容不受影响。
    /// 3. <b>按 tick 注入</b>：在 <c>LevelEngine.Update</c> 的**前缀**里看
    ///    <c>GetLevelTime()</c> 是否等于下一条事件的 tick，是就派发。
    ///    录制时事件也是按这个时钟打戳的（<c>AddLevelTime()</c> 在 <c>Update</c> 末尾，
    ///    所以「tick 值 == T」正是「第 T+1 次 Update 之前」），两边语义天然对齐。
    /// 4. <b>屏蔽玩家输入</b>：回放期间 5 个指针入口 + 热键 + 3 个工具按钮全部前缀拦截
    ///    （暂停 / 加速 / 血条 / Esc 这些关卡外操作不拦）。
    /// 5. <b>核对一致性</b>：每遇到一条指纹就按录制时的同一套算法重算一遍并比对。
    ///
    /// ⚠️ 注入是**通过反射调用游戏自己的回调**（而不是自己拼 <c>DoHeldItemPointerEvent</c>）：
    /// interop 把 <c>HeldItemTargetGrid</c> 这类目标生成成了不声明 <c>IHeldItemTarget</c>
    /// 的值类型代理，从 mod 侧没法合法地装箱成那个接口。详见 ReplayPatches 顶部注释。
    /// </summary>
    internal static class ReplayPlayer
    {
        private enum Phase
        {
            Idle = 0,
            GotoScene = 1,
            WaitScene = 2,
            Playing = 3,
            Ended = 4,
            Failed = 5,
        }

        #region 状态
        private static Phase phase = Phase.Idle;
        private static ReplayFile file;
        private static LevelController bound;
        private static int cursor;                 // 下一条待注入事件
        private static int currentTick;
        private static bool injecting;
        private static int waitFrames;             // 场景加载完后再等几帧才 LoadGame
        /// <summary>
        /// 发起场景切换前那个关卡控制器的地址。
        /// <para>为了不把关键帧 <c>LoadGame</c> 到旧控制器上（见 `GotoScene` 里的注释），
        /// 必须等控制器换成**一个新实例**，而不是「场上有控制器」。</para>
        /// </summary>
        private static IntPtr oldControllerPointer;
        private static float phaseStart;
        private static string failReason;

        /// <summary>最后一次失败的说明（<see cref="ReplaySceneEntry"/> 要用它回报原因）。</summary>
        internal static string FailureReason => failReason;

        // 统计
        private static int injectedCount;
        private static int skippedNoEntity;
        private static int skippedNoController;

        /// <summary>
        /// 「目标找不到」的分类计数，键形如 <c>Down mvz2:redstone</c>。
        ///
        /// <para>为何要分类：只看总数分不清两种完全不同的东西 ——
        /// ① **无伤大雅的悬停漏掉**（Enter/Exit/Stay 打到一个已经消失的特效实体，丢掉不影响仿真）；
        /// ② **真的丢了一次点击**（<c>Down</c> 打在 <c>mvz2:redstone</c> 上 = 少收一份资源）。
        /// 把它们混在一个数字里，「资源没收到」这种问题就看不出来。</para>
        /// </summary>
        private static readonly Dictionary<string, int> missByTarget =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>把一次目标解析失败归类。（见 <see cref="missByTarget"/> 的说明。）</summary>
        private static void NoteMiss(ReplayEvent ev)
        {
            string key;
            try
            {
                var ia = (MVZ2Logic.Inputs.PointerInteraction)ev.Interaction;
                key = ia + " " + (string.IsNullOrEmpty(ev.EntityDef) ? "?" : ev.EntityDef);
            }
            catch (Exception)
            {
                key = "ia=" + ev.Interaction + " " + (ev.EntityDef ?? "?");
            }
            missByTarget.TryGetValue(key, out var n);
            missByTarget[key] = n + 1;
        }
        private static int invokeErrors;
        private static int fpCompared;
        private static int fpMatched;
        private static int fpMismatch;
        private static int fpIndexForCheck;

        /// <summary>
        /// 「文件里有、但我们这次没能算出来」的指纹字段数。
        ///
        /// <para>协议允许文件带本 mod 不认识的指纹字段（别的版本 / 第三方采样器写的）。
        /// 那些字段**不可能**被核对，所以既不能算「一致」也不能算「不一致」——
        /// 单独计数并在小结里说明，否则会被误读成「全都对上了」。</para>
        /// </summary>
        private static int fpUnverifiedFields;
        private static readonly List<string> fpUnverifiedNames = new List<string>();

        /// <summary>本次播放核对的策略（**由文件推导**，见 <see cref="ReplayPlaybackPolicy"/>）。</summary>
        private static ReplayRecordOptions verifyOptions;

        /// <summary>播放开始时的环境差异（录制环境 vs 当前环境）。空 = 一致。</summary>
        private static readonly List<string> envDiff = new List<string>();
        private static readonly List<string> envDataVersionDiff = new List<string>();

        private static int tickLimit;
        private static int recoveredCount;
        /// <summary>连同定义都找不到、最后用「记录坐标就近」兜底的次数。</summary>
        private static int positionFallbackCount;
        /// <summary>按「定义 + 分层」精确找回的次数（同格多器械时的关键指标）。</summary>
        private static int layerRecoveredCount;
        private static int defMismatchCount;
        private static int skippedWrongTarget;
        /// <summary>
        /// 「按录制时的值把蓝图手持物的即时触发/激发标记改回来」的次数。
        ///
        /// <para>> 0 就说明「交换触发」选项在录/放两边确实不同、而且我们静默地改正了它。
        /// 如果它一直是 0 而回放行为仍然不同，那就是这个选项还有别的消费者（或者补丁读不到手持物）。</para>
        /// </summary>
        private static int heldFlagFixes;

        /// <summary>
        /// 「同定义 + 最近坐标」匹配时允许的最大距离（草坪单位）。
        ///
        /// <para>尺度参照：一个格子 = <c>AreaMeta.GridWidth</c> 默认 <b>80</b> 草坪单位。
        /// 半格以内、且定义相同的实体，几乎肯定是同一个东西；再松就容易认错。</para>
        ///
        /// <para>（之前这里是 2.0 —— 只有格子的 2.5%，等于形同虚设，报「兜底也找不到」时
        /// 其实根本没给它任何机会。）</para>
        /// </summary>
        private const float EntityMatchRadius = 36.0f;
        private static readonly Dictionary<string, int> injectedByKind = new Dictionary<string, int>();
        private static readonly List<string> fpReports = new List<string>();
        private static readonly List<string> errors = new List<string>();

        /// <summary>最后一次播放的小结（自然结束或中止时都会刷新）。</summary>
        private static string lastSummary;
        /// <summary>中止/结束的原因。</summary>
        private static string stopReason;
        /// <summary>开始播放到停下的真实耗时（秒）。</summary>
        private static float elapsedSeconds;

        // 指针位置（给 Global.Input 用）
        // ⚠️ 存的是**录制时坐标系**的值（与文件里的 sx/sy 同制），不是当前窗口的像素。
        // 因为「像素 → 世界坐标」的换算里含屏幕高：把换算结果缓存下来，
        // 一旦玩家在播放中途改了窗口尺寸就会失效。所以这里保持原值、
        // 在**每次读取时**才换算（见 PointerScreen 与 ReplayViewport）。
        private static Vector2 pointerScreen;
        private static int pointerQueries;

        /// <summary>
        /// 本 tick 有没有逐 tick 轨迹数据覆盖（<see cref="ReplayPointerTrajectory.TickCovered"/> 的缓存）。
        ///
        /// <para>接管指针**一律以它为准**，不要用「整条文件里有没有轨迹」：
        /// 按需录制意味着一个文件的轨迹可能只覆盖后半段（例如 2-11 的碾压墙要等 Boss 出场才生成），
        /// 前半段没有数据时绝不能凭空造输入。</para>
        /// </summary>
        private static bool trackCovered;

        /// <summary>
        /// 按键状态（位掩码）—— **没有轨迹时**从录制的事件推出来的兜底值。
        ///
        /// <para><b>为什么不能省掉它（这里踩过大坑）：</b>游戏的
        /// <c>LevelPointerInteractionHandler.UpdateHoldAndStreak()</c>
        /// （由 <c>GridView</c> / <c>EntityController</c> 每帧调用）会自己发
        /// <c>Stay</c> / <c>Hold</c> / <c>Streak</c>，其中 <c>Streak</c> 的判据就是
        /// <c>InputHelper.IsPointerHolding(pointerId)</c>。
        /// 录制侧**不会**把这些每帧刷屏的交互记进文件，所以回放时必须由**事件推出来的掩码**
        /// 把「鼠标一直按着」这件事维持住 —— 否则拖动（<c>Down → … → Up</c>）中间的
        /// 那几个 tick 会变成「没按」，拖动直接断掉（实测会让一次放置失败）。</para>
        ///
        /// <para>有轨迹时一律以轨迹为准，见 <see cref="CurrentPointerMask"/>。</para>
        /// </summary>
        private static int eventMask;

        /// <summary>本 tick 结束时的按键掩码 / 上一 tick 结束时的按键掩码（无轨迹时用）。</summary>
        private static int maskNow;
        private static int maskPrev;
        /// <summary>本 tick **开始**时的掩码 —— 当作「上一 tick」（见 <see cref="AfterTick"/>）。</summary>
        private static int cycleStartMask;

        /// <summary>
        /// 当前指针位置（**已换算到当前窗口**）。
        ///
        /// <para>两种来源，优先级从高到低：</para>
        /// <list type="number">
        /// <item>正在派发某个带坐标的事件 → 用**那个事件**的位置（一定要用事件原值：
        /// 魔人经卷/剑的落点就是在按下那一刻由这里定下来的）；</item>
        /// <item>其余时刻 → 用逐 tick 轨迹的位置（打砖块挡板、重装兵器朝向每帧读的就是这个）。</item>
        /// </list>
        ///
        /// <para>⚠️ 这是**事件原值**，会被 <c>Global.Input</c> 的那几个查询读到，
        /// 而其中 <c>MinecartRideable</c> / <c>Snipenser</c> / <c>BreakoutBoard</c>
        /// 会拿它去改仿真 —— **绝不能**把它换成假鼠标的平滑值。
        /// 假鼠标只允许在 <c>LevelController.UpdateHeldItemPosition</c> 那一层临时替换
        /// （见 <see cref="FakePointer"/> 的类注释）。</para>
        /// </summary>
        public static Vector2 PointerScreen => ReplayViewport.ToCurrent(pointerScreen);

        /// <summary>当前按键状态（给 <c>InputHelper.IsPointerHolding</c> 之类的接管用）。</summary>
        public static int CurrentPointerMask => trackCovered
            ? ReplayPointerTrajectory.CurrentMask
            : maskNow;

        /// <summary>
        /// 回放时接管「某个键现在按着吗 / 刚按下 / 刚抬起」。
        ///
        /// <para><b>为什么必须接管：</b><c>InputHelper.IsPointerOfPhase</c> 读的是
        /// <c>Input.GetMouseButton*</c> —— 也就是**旁观者**的鼠标。不接管的话：
        /// 重装兵器在回放里永远不会开火（它要 <c>IsPointerHolding(MOUSE,0)</c>），
        /// 而旁观者按住左键又会让它乱开火。</para>
        ///
        /// <para><b>本 tick 没有轨迹数据时退回「事件推出来的掩码」</b>（<see cref="eventMask"/>），
        /// **不是**退回实时输入 —— 后者会让旁观者的鼠标决定回放表现，
        /// 而且拖动/长按这类「中间没有事件」的段落会整个断掉。</para>
        ///
        /// <para>只对 <c>MOUSE</c> 回答 —— 触摸/键盘的指针本来不在回放的范围里（录的是电脑端）。</para>
        /// </summary>
        /// <param name="phaseValue">与 <c>MVZ2Logic.Inputs.PointerPhase</c> 同值（None=0/Press=1/Hold=2/Release=3）。</param>
        public static bool TryGetPointerPhase(int type, int button, int phaseValue, out bool result)
        {
            result = false;
            if (phase != Phase.Playing)
                return false;
            if (type != MVZ2Logic.Inputs.PointerTypes.MOUSE)
                return false;
            if (button < 0 || button > 2)
                return false;

            int nowMask, prevMask;
            if (trackCovered)
            {
                nowMask = ReplayPointerTrajectory.CurrentMask;
                prevMask = ReplayPointerTrajectory.PreviousMask;
            }
            else
            {
                nowMask = maskNow;
                prevMask = maskPrev;
            }
            int bit = 1 << button;
            result = ReplayPointerTrajectory.EdgeOf((prevMask & bit) != 0, (nowMask & bit) != 0, phaseValue);
            return true;
        }

        /// <summary>
        /// 自测：验证「掩码 → 相位」这套边沿逻辑，以及**回放期间必须接管按键状态**这件事。
        /// 这几条错了的表现非常隐蔽：重装兵器不会开火、右键取消会连续触发。
        /// </summary>
        internal static bool SelfCheckPointerPhases(out string detail)
        {
            var sb = new StringBuilder(160);
            bool ok = true;

            // 0 → 1：刚按下
            ok &= Expect(sb, ReplayPointerTrajectory.EdgeOf(false, true, 1), true, "0→1 算 Press");
            ok &= Expect(sb, ReplayPointerTrajectory.EdgeOf(false, true, 2), true, "0→1 也算 Hold（按住期间为真）");
            ok &= Expect(sb, ReplayPointerTrajectory.EdgeOf(false, true, 3), false, "0→1 不算 Release");

            // 1 → 1：一直按着
            ok &= Expect(sb, ReplayPointerTrajectory.EdgeOf(true, true, 1), false, "1→1 不再是 Press（只触发一次）");
            ok &= Expect(sb, ReplayPointerTrajectory.EdgeOf(true, true, 2), true, "1→1 仍是 Hold");
            ok &= Expect(sb, ReplayPointerTrajectory.EdgeOf(true, true, 3), false, "1→1 不是 Release");

            // 1 → 0：松开
            ok &= Expect(sb, ReplayPointerTrajectory.EdgeOf(true, false, 1), false, "1→0 不是 Press");
            ok &= Expect(sb, ReplayPointerTrajectory.EdgeOf(true, false, 2), false, "1→0 不再是 Hold");
            ok &= Expect(sb, ReplayPointerTrajectory.EdgeOf(true, false, 3), true, "1→0 算 Release");

            // 0 → 0：没按
            ok &= Expect(sb, ReplayPointerTrajectory.EdgeOf(false, false, 2), false, "0→0 不按");
            ok &= Expect(sb, ReplayPointerTrajectory.EdgeOf(false, false, 0), false, "None 恒假");

            var masks = "（左=" + ReplayPointerTrajectory.BitLeft
                + " 右=" + ReplayPointerTrajectory.BitRight
                + " 中=" + ReplayPointerTrajectory.BitMiddle + "）";
            detail = sb.ToString().TrimEnd('；', ' ') + " " + masks;
            return ok;
        }

        private static bool Expect(StringBuilder sb, bool actual, bool want, string what)
        {
            bool ok = actual == want;
            sb.Append(ok ? "OK " : "✗ ").Append(what).Append("；");
            return ok;
        }

        /// <summary>
        /// 未换算的指针位置（**录制时坐标系**）。
        ///
        /// <para>只给「临时换上假鼠标 / 换回来」那条通道用：
        /// 那边两边都得是同一个坐标系，否则会在重复换算中越偏越多
        /// （写进去的是当前窗口值、读出来又换算一次）。</para>
        /// </summary>
        public static Vector2 RawPointerScreen
        {
            get { return pointerScreen; }
            set { pointerScreen = value; }
        }

        private const int MaxReports = 60;

        /// <summary>
        /// 所有不一致的 tick。
        ///
        /// <para>为什么单独存：明细列表有长度上限（<see cref="MaxReports"/>），
        /// 一旦前面的 tick 就开始发就不一致，**后面最关键的“首次出现某类差异”的位置会被截掉**
        /// （实测就被这个坑过一次：报告里只到 tick 600，看不到 690 那条 maplewheel）。
        /// 所以这里额外把所有 tick 汇总成一行写进小结。</para>
        /// </summary>
        private static readonly List<int> fpMismatchTicks = new List<int>();
        private const float GotoSceneTimeoutSec = 120f;
        private const float WaitSceneTimeoutSec = 120f;
        #endregion

        #region 反射目标
        private static bool methodsReady;
        private static MethodInfo mGrid;
        private static MethodInfo mEntity;
        private static MethodInfo mBlueprint;
        private static MethodInfo mLawn;
        private static MethodInfo mBlueprintKey;
        private static MethodInfo mClickPickaxe;
        private static MethodInfo mClickStarshard;
        private static MethodInfo mClickTrigger;
        private static MethodInfo mDialogResume;
        private static MethodInfo mFromBson;
        #endregion

        #region 对外状态
        public static bool IsPlaying => phase == Phase.Playing;
        public static bool IsActive => phase != Phase.Idle && phase != Phase.Failed && phase != Phase.Ended;

        /// <summary>是否要拦掉玩家的实际输入（回放中，且不是我们自己在注入）。</summary>
        ///
        /// <para>结束面板开着的时候也要拦：那时 <c>phase</c> 已经是 Ended，
        /// 否则玩家还能在「已经播完的那一局」里乱点、把器械放下去。</para>
        public static bool ShouldBlockInput
        {
            get
            {
                if (phase == Phase.Playing && !injecting)
                    return true;
                try { return ReplayEndPanel.IsOpen; }
                catch (Exception) { return false; }
            }
        }

        /// <summary>回放期间录制要停手，否则会把注入的动作又录一遍。</summary>
        public static bool SuppressRecording => phase != Phase.Idle && phase != Phase.Ended && phase != Phase.Failed;

        public static string CurrentName => file != null ? System.IO.Path.GetFileName(file.Path) : null;

        /// <summary>当前回放的文件路径（优先 <c>.mrep.json</c>，见 <see cref="ReplayFile.Load"/>）。</summary>
        public static string CurrentPath => file != null ? file.Path : null;

        /// <summary>当前回放文件本体（结束面板要拿它拼文案）。</summary>
        public static ReplayFile CurrentFile => file;

        /// <summary>
        /// 本次播放结束之后，是否要把「退出关卡」的落点从地图改成**回放页**。
        /// 由 <see cref="EndToReplayPage"/> 置位，被 <c>ReplayPatches.GotoMapOrMainmenuPrefix</c>
        /// 读一次就清掉（一次性指令）。
        /// </summary>
        private static bool wantsReplayPage;

        public static bool WantsReplayPage { get { return wantsReplayPage; } }

        public static void ConsumeReplayPage() { wantsReplayPage = false; }

        /// <summary>
        /// 重新播放当前这一条（结束面板的「重新播放」、以及菜单里的「重新播放」）。
        /// 走的是与 <c>/replay play</c> 完全相同的路径 —— 会重新加载一次关卡场景。
        /// </summary>
        public static string RestartCurrent()
        {
            var name = CurrentName;
            if (string.IsNullOrEmpty(name))
                return "[replay] 没有可重播的回放。";
            ReplayEndPanel.Close();
            wantsReplayPage = false;

            // ⛔ **必须先 Stop**。Start() 开头就是
            //    `if (IsActive) return "[replay] 已经在播放了…"`，
            //    而 Playing 阶段 IsActive 为 true —— 不先收尾的话这里会**静默无效**
            //    （返回一句提示字符串，没人看；菜单按钮就成了「点了没反应」）。
            Stop("restart");

            Plugin.Log?.LogInfo("[Replay] 重新播放：" + name);
            return Start(name);
        }

        /// <summary>
        /// 「菜单 → 结束播放」：结束本次播放并**弹出回放结束面板**（导出 / 重新播放 / 退出），
        /// 而不是直接退出关卡。
        ///
        /// <para>与「播完 / 通关 / 失败」走同一个 <see cref="FinishPlayback"/>，
        /// 所以收尾完全一致：面板上再选「退出」才真的离开关卡回回放页。</para>
        /// </summary>
        public static void FinishFromMenu()
        {
            if (!IsActive)
                return;
            Plugin.Log?.LogInfo("[Replay] 菜单请求结束播放");
            FinishPlayback("manual-end");
        }

        /// <summary>
        /// 结束播放并回到**回放页**。
        ///
        /// <para>做法：置一个一次性标志，然后走游戏自己的 <c>LevelController.ExitLevel()</c> ——
        /// 它内部会调 <c>Scene.GotoMapOrMainmenu()</c>，由
        /// <c>ReplayPatches.GotoMapOrMainmenuPrefix</c> 把落点换成回放页（同帧把面板叠上去，
        /// 不会先闪一下插件页），关掉回放页时再回主菜单。</para>
        ///
        /// <para>为什么不在模式里直接操作场景切换：退出关卡要清的东西很多
        /// （保档 / Dispose / 卸场景 / 重置光照），照抄一遍必然会漏。</para>
        /// </summary>
        public static void EndToReplayPage()
        {
            ReplayEndPanel.Close();
            var ctrl = bound;
            phase = Phase.Ended;
            stopReason = "manual-end";
            elapsedSeconds = Time.realtimeSinceStartup - phaseStart;
            lastSummary = BuildSummary();
            ReplayHud.Hide();
            FakePointer.End();
            // 真正的「离开这次回放」：交还存档控制权、清掉窗口换算。
            // （结束面板那段时间护栏继续开着 —— 关卡还在后面跑，钱还得继续花在影子上。）
            ReplayViewport.Clear();
            ReplaySaveGuard.End();

            ReplayPage.RequestOpen();
            wantsReplayPage = true;

            if (ctrl == null || ctrl.Pointer == IntPtr.Zero)
            {
                // 关卡已经不在（罕见）：直接切页面，不走 ExitLevel。
                try { MainManager.Instance.Scene.GotoMapOrMainmenu(); }
                catch (Exception e) { Note("结束播放时切页面失败：" + Describe(e)); }
                return;
            }
            try
            {
                // 只发出去、不等它 —— 后续全靠 ExitLevel 内部自己 await。
                _ = ctrl.ExitLevel();
                Plugin.Log?.LogInfo("[Replay] 结束播放：已请求退出关卡（回到回放页）");
            }
            catch (Exception e)
            {
                wantsReplayPage = false;
                Note("结束播放时退出关卡失败：" + Describe(e));
            }
        }

        // 供自测报告使用
        public static int InjectedEventCount => injectedCount;
        public static int TotalEventCount => file != null ? file.Events.Count : 0;
        public static int VerifiedComparisons => fpCompared;
        public static int VerifiedMismatches => fpMismatch;

        /// <summary>本次播放中**第一次**出现状态不一致的 tick（0 = 没出现过）。</summary>
        public static int FirstMismatchTick => fpMismatchTicks.Count > 0 ? fpMismatchTicks[0] : 0;
        public static bool FinishedCleanly => phase == Phase.Ended;
        public static bool FailedNow => phase == Phase.Failed;
        public static int SkippedNoEntity => skippedNoEntity;
        public static int SkippedNoController => skippedNoController;
        public static string LastSummary => lastSummary;
        public static string StopReason => stopReason;
        public static float ElapsedSeconds => elapsedSeconds;
        public static int CursorIndex => cursor;
        public static int CurrentTick => currentTick;

        public static string Describe()
        {
            if (phase == Phase.Idle)
                return "未播放";
            if (phase == Phase.Failed)
                return "播放失败：" + failReason;
            if (phase == Phase.Ended)
                return "已结束（" + stopReason + "，停在 tick " + currentTick + "/"
                    + (file != null ? file.LastTick : 0) + "，注入 " + injectedCount + "/"
                    + (file != null ? file.Events.Count : 0) + "）";
            var name = CurrentName;
            switch (phase)
            {
                case Phase.GotoScene:
                case Phase.WaitScene:
                    return "正在进入关卡场景 " + name;
                case Phase.Playing:
                    return "播放中 " + name
                        + " tick=" + currentTick + "/" + (file != null ? file.LastTick : 0)
                        + " 事件=" + cursor + "/" + (file != null ? file.Events.Count : 0)
                        + " 指纹=" + fpMatched + "/" + fpCompared
                        + (fpMismatch > 0 ? "（不一致 " + fpMismatch + "）" : "");
                default:
                    return "已结束 " + name;
            }
        }

        /// <summary>结束时打印的多行小结。</summary>
        public static string BuildSummary()
        {
            var sb = new StringBuilder();
            sb.Append("[replay] 播放结束：").Append(file != null ? System.IO.Path.GetFileName(file.Path) : "?");
            sb.Append('\n');
            sb.Append("  停顿在 tick ").Append(currentTick).Append(" / ").Append(file != null ? file.LastTick : 0).Append('\n');
            sb.Append("  注入事件 ").Append(injectedCount).Append(" / ").Append(file != null ? file.Events.Count : 0);
            if (skippedNoEntity > 0)
                sb.Append("（实体不存在 ").Append(skippedNoEntity).Append(" 个）");
            if (skippedNoController > 0)
                sb.Append("（找不到实体控制器 ").Append(skippedNoController).Append(" 个）");
            if (defMismatchCount > 0)
                sb.Append("（实体 ID 错位 ").Append(defMismatchCount).Append(" 次，其中按定义+坐标找回 ")
                  .Append(recoveredCount).Append(" 次、因对不上而真跳过 ").Append(skippedWrongTarget).Append(" 次）");
            if (positionFallbackCount > 0)
                sb.Append("（其中 " + positionFallbackCount
                    + " 次连定义都找不到，改用记录坐标当锚点 —— 游戏会自己就近选目标）");
            if (layerRecoveredCount > 0)
                sb.Append("（其中 " + layerRecoveredCount + " 次靠「分层」在同格多器械里挑对了）");
            if (heldFlagFixes > 0)
                sb.Append("（按录制时的值改正了 " + heldFlagFixes + " 次蓝图的「即时触发」标记）");
            if (invokeErrors > 0)
                sb.Append("（有 ").Append(invokeErrors).Append(" 个注入抛异常）");
            sb.Append('\n');
            if (injectedByKind.Count > 0)
            {
                sb.Append("  注入分类 : ");
                foreach (var kv in injectedByKind)
                    sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
                sb.Append('\n');
            }
            if (missByTarget.Count > 0)
            {
                sb.Append("  目标找不到的分类（交互 + 目标定义，只列前 8 名）：\n");
                var list = new List<KeyValuePair<string, int>>(missByTarget);
                list.Sort((x, y) => y.Value.CompareTo(x.Value));
                for (int i = 0; i < list.Count && i < 8; i++)
                    sb.Append("    ").Append(list[i].Key).Append(" × ").Append(list[i].Value).Append('\n');
            }
            sb.Append("  指纹核对 ").Append(fpMatched).Append(" / ").Append(fpCompared);
            if (fpMismatch > 0)
                sb.Append("   ❌ 有 ").Append(fpMismatch).Append(" 条不一致");
            else if (fpCompared > 0)
                sb.Append("   ✅ 全部一致");
            sb.Append('\n');
            for (int i = 0; i < fpReports.Count; i++)
                sb.Append("  ").Append(fpReports[i]).Append('\n');
            if (fpMismatchTicks.Count > 0)
            {
                sb.Append("  不一致 tick 列表：");
                for (int i = 0; i < fpMismatchTicks.Count; i++)
                {
                    if (i > 0)
                        sb.Append(' ');
                    sb.Append(fpMismatchTicks[i]);
                }
                if (fpMismatchTicks.Count >= 400)
                    sb.Append(" …（已达上限 400）");
                sb.Append('\n');
            }

            // ---- 协议 / 环境（协议 v10）----
            //
            // 这一段回答的是「这份回放是按什么规范录的、当时的游戏环境是什么、现在差在哪」。
            // 它必须进小结：环境差异是「回放与实战不符」最常见的解释之一，
            // 而从画面上完全看不出来。
            if (file != null)
            {
                sb.Append("  协议      : ")
                  .Append(file.Policy != null ? file.Policy.Describe() : "-").Append('\n');
                if (file.HasEnv)
                    sb.Append("  录制环境  : ").Append(file.Environment.Describe()).Append('\n');
                else
                    sb.Append("  录制环境  : （v")
                      .Append(file.Version).Append(" 文件没有环境记录，只能看到游戏版本 ")
                      .Append(file.Game ?? "?").Append("）\n");

                if (envDataVersionDiff.Count > 0)
                {
                    // 这一类不是「略有偏差」而是「必然失败」：游戏读档时会直接判存档不匹配。
                    sb.Append("  ⚠ 内容 mod 关卡数据版本不同（关键帧可能载不进去）：\n");
                    for (int i = 0; i < envDataVersionDiff.Count; i++)
                        sb.Append("    · ").Append(envDataVersionDiff[i]).Append('\n');
                }
                if (envDiff.Count > 0)
                {
                    sb.Append("  ⚠ 环境差异 ").Append(envDiff.Count)
                      .Append(" 项（只提示，不阻止播放）：\n");
                    for (int i = 0; i < envDiff.Count && i < 10; i++)
                        sb.Append("    · ").Append(envDiff[i]).Append('\n');
                    if (envDiff.Count > 10)
                        sb.Append("    · …还有 ").Append(envDiff.Count - 10).Append(" 项\n");
                }
                if (file.Warnings.Count > 0)
                {
                    for (int i = 0; i < file.Warnings.Count; i++)
                        sb.Append("  ⚠ 文件自检: ").Append(file.Warnings[i]).Append('\n');
                }
            }
            if (fpUnverifiedFields > 0)
            {
                // 「没法核对」必须单独说 —— 否则「指纹 3/3 一致」会被读成「全都验证过了」。
                sb.Append("  ⚠ 有 ").Append(fpUnverifiedFields)
                  .Append(" 个指纹字段没法核对（文件里有、本 mod 算不出来）：")
                  .Append(string.Join("、", fpUnverifiedNames.ToArray()));
                if (fpUnverifiedFields > fpUnverifiedNames.Count)
                    sb.Append(" 等");
                sb.Append('\n');
            }
            for (int i = 0; i < errors.Count; i++)
                sb.Append("  ! ").Append(errors[i]).Append('\n');
            if (pointerQueries > 0)
                sb.Append("  Global.Input 指针位置查询被改写 ").Append(pointerQueries).Append(" 次\n");
            if (ReplayPointerAction.Enabled)
            {
                sb.Append("  回放的指针动作（POST_POINTER_ACTION）").Append(ReplayPointerAction.Fired).Append(" 次");
                if (ReplayPointerAction.FiredFromTrack > 0)
                    sb.Append("，其中由逐 tick 轨迹派发 ").Append(ReplayPointerAction.FiredFromTrack).Append(" 次");
                sb.Append('\n');
            }
            // v9：逐 tick 轨迹与选项对话框。这两条是「打砖块/重装兵器/矿车/碾压墙」和
            // 「梦魇与上锁的箱子」能正确重现的前提，所以必须报出「到底有没有数据、被用了几次」。
            sb.Append("  指针轨迹  : ").Append(ReplayPointerTrajectory.DescribePlayback())
                .Append("；按键掩码最后=").Append(CurrentPointerMask)
                .Append("（有轨迹时以轨迹为准，否则由事件推出）\n");
            sb.Append("  选项对话框: ").Append(ReplayDialogs.Describe()).Append('\n');
            // 「重演一局不许改动存档」这条护栏必须报出来：补丁没挂上 / 影子一次都没命中，
            // 都说明存档还在被回放改（而这从游戏表现上完全看不出来）。
            sb.Append("  存档护栏  : 补丁 ").Append(ReplayHooks.SaveGuardStatus)
                .Append("，").Append(ReplaySaveGuard.Describe()).Append('\n');
            FakePointer.AppendReport(sb);
            return sb.ToString().TrimEnd('\n');
        }
        #endregion

        #region 启动 / 停止
        /// <summary>开始播放。返回给控制台的消息。</summary>
        public static string Start(string nameOrPath)
        {
            if (IsActive)
                return "[replay] 已经在播放了，先 /replay stopplay 停掉。";

            var f = ReplayFile.Load(nameOrPath, out var err);
            if (f == null)
                return "[replay] " + err;

            EnsureMethods();

            // 播放期间不能再录制（否则注入的动作会被记成新回放）。
            try
            {
                if (ReplayRecorder.IsArmed)
                {
                    ReplayRecorder.StopManual();
                    Plugin.Log?.LogInfo("[Replay] 开始播放，已结束进行中的录制");
                }
            }
            catch (Exception) { }

            file = f;
            cursor = 0;
            currentTick = f.StartTick;
            injectedCount = 0;
            skippedNoEntity = 0;
            skippedNoController = 0;
            missByTarget.Clear();
            recoveredCount = 0;
            positionFallbackCount = 0;
            layerRecoveredCount = 0;
            defMismatchCount = 0;
            skippedWrongTarget = 0;
            heldFlagFixes = 0;
            invokeErrors = 0;
            fpCompared = 0;
            fpMatched = 0;
            fpMismatch = 0;
            fpIndexForCheck = 0;
            fpMismatchTicks.Clear();
            injectedByKind.Clear();
            lastSummary = null;
            stopReason = null;
            elapsedSeconds = 0f;
            fpReports.Clear();
            fpUnverifiedFields = 0;
            fpUnverifiedNames.Clear();
            envDiff.Clear();
            envDataVersionDiff.Clear();
            verifyOptions = BuildVerifyOptions(f);
            CheckEnvironment(f);
            errors.Clear();
            failReason = null;
            bound = null;
            injecting = false;
            pointerScreen = Vector2.zero;
            trackCovered = false;
            eventMask = 0;
            maskNow = 0;
            maskPrev = 0;
            cycleStartMask = 0;
            pointerQueries = 0;
            waitFrames = 0;
            tickLimit = GetTickLimit();
            phaseStart = Time.realtimeSinceStartup;
            phase = Phase.GotoScene;
            ReplayHud.Reset();
            ReplayProbe.Reset();
            ReplayPointerAction.Reset();
            ReplaySceneEntry.Reset();

            // 存档护栏：回放里挣的/花的钱只进影子，统计与解锁等一律不写。
            // 补丁**故意延迟到这里才装**（游戏早就初始化完了，反射 MVZ2Logic 的类型是安全的）。
            //
            // ⚠️ 但**只装补丁，不开护栏**：0.7.0 上实测，护栏在「进关卡场景」这段时间里
            // 处于开启状态会让进程直接死掉（Fatal error. 0xC0000005）。
            // 现场（临时直写文件的诊断，每次都 flush）：
            //     GotoScene: begin
            //     guard: Data.GetMoney        <-- 此后无任何输出
            // 也就是崩在「回放自己的前缀返回 false、丢掉逻辑存储层 GetMoney 的原方法」这一步。
            // 关掉护栏（前缀直接 return true）就不会崩，而**只装补丁不开护栏**也没问题 ——
            // 所以启用被挪到了关卡进入完成的那个时刻（见 WaitScene → Phase.Playing）。
            ReplayHooks.EnsureSaveGuardHooks();
#if DIAGNOSTICS
            // 只留传送带守卫（它接管的是 UpdateFixed 那几行，实测无害）。
            // 原先这里还有三个探针（Addressables 定位记录 / Locate 改写 / IsAssignableFrom 守卫），
            // 2026-10-16 全部删除 —— 它们挂上任何一个都会让进程以 .NET Runtime id=1023
            // （coreclr internal error）死掉。见 docs/DESIGN.md §11.4。
            ReplayConveyorGuard.Ensure();
#endif
            // v9：用**录制时的金币**给影子播种。金币不在关卡关键帧里，但它会参与关卡逻辑的
            // **分支**（上锁的箱子按「钱够不够」决定要不要生成「付钱」那一项），
            // 不播种的话「玩家看回放时没那么多钱」会让选项列表都不一样。
            //
            // 注意：这里只是**记住**要播种的值，真正给影子赋值要等护栏启用时（下一段）。
            // 把「录制时的窗口尺寸」记下来：事件里的 sx/sy 是像素，
            // 换个窗口尺寸再播就指向别的格子了（见 ReplayViewport）。
            ReplayViewport.Capture(f);

            // v9：逐 tick 指针轨迹 + 选项对话框。两者都没有（旧文件 / 普通关卡）时
            // 自动退回「按事件派发」与「对话框交给玩家」的老行为。
            ReplayPointerTrajectory.LoadPlayback(f.PointerSamples);
            ReplayDialogs.BeginPlayback(f);

            FakePointer.Begin(f);
            ReplayEndPanel.Close();
            wantsReplayPage = false;
            // 「回放页关掉后去哪」属于上一次播放的收尾状态，新一场开播时必须清掉。
            ReplayPage.SetReturnTo(null);

            Plugin.Log?.LogInfo("[Replay] 开始播放 " + f.Path + " —— " + f.Describe());
            var sb = new StringBuilder();
            sb.Append("[replay] 开始播放：").Append(System.IO.Path.GetFileName(f.Path)).Append('\n');
            sb.Append("  ").Append(f.Describe()).Append('\n');
            // 播放策略**来自文件**（不是配置）：把它打出来，用户与排障时都能一眼看到
            // 「这份回放声称该怎么播」。
            sb.Append("  协议      : ").Append(f.Policy.Describe()).Append('\n');
            if (f.HasEnv)
                sb.Append("  录制环境  : ").Append(f.Environment.Describe()).Append('\n');
            if (envDataVersionDiff.Count > 0)
                sb.Append("  ⚠ 内容 mod 关卡数据版本不同：")
                  .Append(string.Join("；", envDataVersionDiff.ToArray())).Append('\n');
            if (envDiff.Count > 0)
            {
                sb.Append("  ⚠ 环境与录制时不同（仍会播放）：\n");
                for (int i = 0; i < envDiff.Count && i < 6; i++)
                    sb.Append("    · ").Append(envDiff[i]).Append('\n');
                if (envDiff.Count > 6)
                    sb.Append("    · …还有 ").Append(envDiff.Count - 6).Append(" 项\n");
            }
            for (int i = 0; i < f.Warnings.Count; i++)
                sb.Append("  ! ").Append(f.Warnings[i]).Append('\n');
            sb.Append("  （正在进入关卡场景，稍等）");
            return sb.ToString();
        }

        /// <summary>停止播放（保留当前关卡状态）。</summary>
        public static string Stop(string reason)
        {
            if (phase == Phase.Idle)
                return "[replay] 当前没有在播放。";
            var was = phase;
            phase = Phase.Ended;
            stopReason = reason;
            elapsedSeconds = Time.realtimeSinceStartup - phaseStart;
            ReplayHud.Hide();
            FakePointer.End();
            ReplayViewport.Clear();
            ReplayDialogs.EndPlayback();
            ReplayPointerTrajectory.ResetPlayback();
            ReplaySaveGuard.End();
            ReplaySceneEntry.Reset();
            bound = null;
            cursor = 0;
            lastSummary = BuildSummary();
            RecordHealth(false);
            Plugin.Log?.LogInfo("[Replay] 播放停止：" + reason + "，耗时 " + elapsedSeconds.ToString("F1") + " 秒");
            if (was != Phase.Playing)
                return "[replay] 播放已中止（阶段 " + was + "）：" + reason;
            return lastSummary;
        }

        private static void Fail(string reason)
        {
            failReason = reason;
            phase = Phase.Failed;
            ReplayHud.Hide();
            FakePointer.End();
            ReplayViewport.Clear();
            ReplayDialogs.EndPlayback();
            ReplayPointerTrajectory.ResetPlayback();
            ReplaySaveGuard.End();
            ReplaySceneEntry.Reset();
            bound = null;
            RecordHealth(true);
            Plugin.Log?.LogError("[Replay] 播放失败：" + reason);
            try { Global.Debugs?.Print("[replay] 播放失败：" + reason + "\n"); } catch (Exception) { }
        }

        /// <summary>关卡被销毁（退出关卡 / 重载）时收尾。</summary>
        public static void NotifyLevelDispose(LevelController ctrl)
        {
            // 只认「正在播的那个关卡」。场景切换时旧关卡也会被销毁，
            // 那时 bound 还是 null（或指向别的东西），不能因此把刚开头的播放掐掉。
            if (bound == null || ctrl == null || bound.Pointer != ctrl.Pointer)
                return;
            if (phase != Phase.Playing)
                return;            Plugin.Log?.LogWarning("[Replay] 播放中的关卡被销毁（Dispose）——"
                + " 阶段=" + phase + " 已跑 " + currentTick + "/" + (file != null ? file.LastTick : 0)
                + " 注入=" + injectedCount + "/" + (file != null ? file.Events.Count : 0)
                + " 指纹=" + fpMatched + "/" + fpCompared
                + " 真实耗时=" + (Time.realtimeSinceStartup - phaseStart).ToString("F1") + " 秒");            Stop("level-dispose");
        }
        #endregion

        #region 每帧驱动
        /// <summary>
        /// 由 <c>MainSceneController.Update</c> 后缀每帧调用。负责场景切换、载入关键帧这些
        /// 「进关卡之前」的阶段；进入 Playing 之后这里就只是兜底。
        /// </summary>
        public static void Tick(MainSceneController scene)
        {
            try
            {
                if (IsPlaying)
                    ReplayConveyorGuard.Beat(CurrentTick, "scene-tick");
                DrivePhase(scene);
                if (IsPlaying)
                    ReplayConveyorGuard.Beat(CurrentTick, "scene-tick-after");

                // 回放的选项对话框是**阻塞式**的（游戏自己 PauseGame 了，逻辑 tick 不再前进），
                // 所以它的自动应答必须靠**帧**驱动，不能挂在 tick 上。
                if (IsPlaying)
                    ReplayDialogs.OnFrame();

                // 菜单里「重新播放 / 结束播放」的文案：每帧按一遍。
                // 只在菜单真的开着时才动手（见 EnforceMenuLabels 的说明）。
                if (IsPlaying)
                    ReplayPatches.EnforceMenuLabels();
            }
            catch (Exception e)
            {
                Fail("驱动阶段出错：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void DrivePhase(MainSceneController scene)
        {
            var main = MainManager.Instance;
            if (main == null)
                return;

            switch (phase)
            {
                case Phase.GotoScene:
                    {
                        if (Time.realtimeSinceStartup - phaseStart > GotoSceneTimeoutSec)
                        {
                            Fail("进入关卡场景超时");
                            return;
                        }
                        if (scene == null)
                            return;

                        // 一律重新加载一次关卡场景（即使现在就站在关卡里）。
                        // 理由：<c>LoadGame</c> 假定面对的是**刚 Awake 出来、干净的** LevelController。
                        // 游戏自己走的就是这个路子（ReloadLevel → GotoLevelSceneAsync → InitLevel/LoadLevel）。
                        //
                        // ⚠️ 一定要记下**旧**控制器的地址：GotoLevelSceneAsync 是异步的，
                        // 它 await 完新场景加载才会 SetLevelController(新ctrl)。如果不记，
                        // WaitScene 会因为「场上已经有控制器」（旧的）而立刻通过，
                        // 结果把关键帧 LoadGame 到**旧**控制器上 —— 那个控制器的
                        // builtinCollisionSystem 已经被 level.Init() 填满了，
                        // CreateFromSerializable → ReadCollisionFromSerializable 会
                        // entities.Add(id) 撞键并抛：
                        //   ArgumentException: An item with the same key has already been added.
                        //   Key: [7, PVZEngine.Collisions.BuiltinCollisionEntity]
                        // 于是 LoadGame 返回 false（该异常只落在 Unity 的 Player.log 里，
                        // 不在 BepInEx\LogOutput.log 里，很容易误判成「没抛异常」）。
                        oldControllerPointer = IntPtr.Zero;
                        try
                        {
                            var prev = main.LevelManager != null ? main.LevelManager.GetLevelController() : null;
                            if (prev != null && prev.Pointer != IntPtr.Zero)
                                oldControllerPointer = prev.Pointer;
                        }
                        catch (Exception) { }
                        Plugin.Log?.LogInfo("[Replay] 请求进入关卡场景（旧控制器 = "
                            + (oldControllerPointer == IntPtr.Zero ? "无" : "0x" + oldControllerPointer.ToInt64().ToString("X"))
                            + "）");

                        // 进关卡**走游戏自己的控制台指令**。
                        //
                        // ⚠️ 为什么不用 IGlobalScene.GotoLevelCoroutine()（0.7.0 起会崩）：
                        // 那条路是 managed → il2cpp_runtime_invoke → 原生 async 场景加载，
                        // 在 0.7.0 上必崩（Fatal error. 0xC0000005，现场见 ErrorLog）：
                        //     at IL2CPP.il2cpp_runtime_invoke
                        //     at DMD<SceneLoadingManager::LoadSceneAsync>
                        //     at DMD<MVZ2.Level.LevelManager::GotoLevelSceneAsync>
                        //     at IGlobalScene.GotoLevelCoroutine
                        //     at ReplayPlayer.DrivePhase
                        // 而**同一条** SceneLoadingManager.LoadSceneAsync 链路，只要整条都留在
                        // il2cpp 原生侧（指令 → 协程 → GotoLevelCoroutine）就完全正常 ——
                        // 游戏自己的「开始关卡」「继续关卡」「重新开始」都走它。
                        // 所以这里让游戏自己去调：把它当**唯一受支持的场景切换入口**。
                        //
                        // 代价：指令在场景加载完之后会顺带跑 LevelManager.InitLevel()
                        // （把关卡 Init 出来 / 从 `.lvl` 续关），控制器就不再是「刚 Awake 的干净状态」，
                        // 直接 LoadGame 会撞碰撞系统的键。所以关键帧的载入改由
                        // ReplayPatches 对 LevelManager.InitLevel 的**前缀接管**完成
                        // （见 ReplaySceneEntry / OnLevelManagerInitLevel）。
                        if (file == null)
                        {
                            Fail("没有正在播放的回放文件");
                            return;
                        }
                        if (ParseId(file.Area) == null || ParseId(file.Stage) == null)
                        {
                            Fail("关卡 id 不合法：" + file.Area + " / " + file.Stage);
                            return;
                        }

                        // 挑指令：**已经在关卡里**时不能再 `/gotolevel`（它自己会抛
                        // 「COMMAND_CANNOT_BE_CALLED_IN_LEVEL」），改用游戏自己的
                        // 「重新开始」——`/load` → LevelController.ReloadLevel()：
                        //     Saves.SaveToFile() → Dispose() → GotoLevelSceneAsync()
                        //     → LevelManager.InitLevel(startAreaID, startStageID, exitTarget)
                        // 最后那一步照样被我们的前缀接管，所以关键帧仍然是我们的。
                        bool inLevel;
                        try
                        {
                            var cur = main.LevelManager != null ? main.LevelManager.GetLevelController() : null;
                            inLevel = cur != null && cur.Pointer != IntPtr.Zero;
                        }
                        catch (Exception) { inLevel = false; }

                        var gotoCommand = inLevel
                            ? "/load"
                            : ("/gotolevel " + file.Stage + " " + file.Area);
                        try
                        {
                            ReplaySceneEntry.Begin(ParseId(file.Area), ParseId(file.Stage));
                            Plugin.Log?.LogInfo("[Replay] 执行 " + gotoCommand + " 进关卡");
                            main.DebugManager.ExecuteCommand(gotoCommand, 1);
                            Plugin.Log?.LogInfo("[Replay] " + gotoCommand + " 已返回（接管状态="
                                + ReplaySceneEntry.State + "）");
                        }
                        catch (Exception e)
                        {
                            ReplaySceneEntry.Abort();
                            Fail("进关卡指令失败：" + Describe(e));
                            return;
                        }
                        phaseStart = Time.realtimeSinceStartup;
                        phase = Phase.WaitScene;
                        break;
                    }

                case Phase.WaitScene:
                    {
                        if (Time.realtimeSinceStartup - phaseStart > WaitSceneTimeoutSec)
                        {
                            Fail("等待关卡场景就绪超时 —— /gotolevel 没跑到 LevelManager.InitLevel，"
                                + "或场景加载卡住了");
                            return;
                        }

                        // 关键帧的载入由 ReplaySceneEntry **自己轮询**完成：
                        // 0.7.0 上 `/load` 的 `await UnloadSceneAsync(旧场景)` 续体不会恢复，
                        // 所以不能只等 LevelManager.InitLevel 的前缀（见 ReplaySceneEntry 的长注释）。
                        ReplaySceneEntry.Tick();

                        if (ReplaySceneEntry.State == SceneEntryState.Waiting)
                            return;
                        if (ReplaySceneEntry.State == SceneEntryState.Failed)
                        {
                            Fail("载入关键帧失败：" + ReplaySceneEntry.Failure);
                            return;
                        }
                        if (ReplaySceneEntry.State != SceneEntryState.Done)
                        {
                            Fail("内部状态错误：WaitScene 时 ReplaySceneEntry = " + ReplaySceneEntry.State);
                            return;
                        }

                        // 载入完成后再等几帧，让 StartLevelIntro / Awake_* 收尾。
                        waitFrames++;
                        if (waitFrames < 3)
                            return;

                        Plugin.Log?.LogInfo("[Replay] 关卡已按关键帧恢复（控制器 = 0x"
                            + (bound != null && bound.Pointer != IntPtr.Zero
                                ? bound.Pointer.ToInt64().ToString("X") : "?")
                            + "），开始按 tick 播放");

                        // 现在才启用存档护栏（理由见 Start 里的长注释：0.7.0 上在进关卡期间
                        // 开启护栏会让进程直接死掉，而只装补丁不开护栏是安全的）。
                        if (ReplayConfig.SaveGuardOn)
                        {
                            ReplaySaveGuard.Begin();
                            if (file != null && file.HasStartMoney)
                                ReplaySaveGuard.SeedMoney(file.StartMoney);
                        }
                        else
                        {
                            Plugin.Log?.LogWarning("[Replay] [General] SaveGuard = false："
                                + "护栏未启用（回放可能会改动存档）");
                        }

                        phase = Phase.Playing;
                        phaseStart = Time.realtimeSinceStartup;
                        ReplayConveyorGuard.Beat(0, "=== 进入 Playing ===");
                        break;
                    }
            }
        }
        #endregion

        #region 载入关键帧
        /// <summary>
        /// 替玩家点掉「关卡已载入」对话框并解除暂停。
        ///
        /// <para>游戏有两条路会弹它并 <c>PauseGame()</c>：</para>
        /// <list type="number">
        /// <item>我们播放关键帧之后（<see cref="ApplyKeyframeTo"/>）；</item>
        /// <item><b>游戏自己</b>从关卡存档续关 —— 存档路径
        /// <c>…\userdata\userN\mvz2\level\&lt;stage&gt;.lvl</c>，由
        /// <c>LevelManager.InitLevel</c> 发现 <c>HasLevelState</c> 后走 <c>LoadLevel</c>。</item>
        /// </list>
        /// <para>第 ② 种会把逻辑 tick 冻住（暂停时引擎不跑 tick），而自测的热身/动作阶段
        /// 都是按 tick 计数的 —— 不处理就永远完不成。表现是日志停在
        /// 「录制已开始: …」之后再无下文。</para>
        /// </summary>
        public static bool ResumeFromLoadedDialog(LevelController ctrl)
        {
            if (ctrl == null || ctrl.Pointer == IntPtr.Zero)
                return false;
            // 这一步很关键：EnsureMethods 原本只在 Start()（= 开始播放）里调，
            // 而「从关卡存档续关」时可能**根本没在播放**（例如自测的录制阶段），
            // 那样 mDialogResume 还是 null，这里就会静默地什么都不做 ——
            // 症状是反复打印「尝试关掉对话框」但游戏始终不解除暂停。
            EnsureMethods();
            try
            {
                if (mDialogResume == null)
                {
                    Plugin.Log?.LogWarning("[Replay] 找不到 UI_OnLevelLoadedDialogOptionClickedCallback，无法解除暂停");
                    return false;
                }
                var btn = LevelLoadedDialog.ButtonType.Resume;
                // 把这个值打进日志：如果枚举顺序与反编译源码不一致，
                // 传错值会直接走 default 分支（= 退出关卡），症状是「刚恢复关键帧就 level-dispose」。
                Plugin.Log?.LogInfo("[Replay] 请求关闭「关卡已载入」对话框：ButtonType=" + btn + "(" + (int)btn + ")");
                mDialogResume.Invoke(ctrl, new object[] { btn });
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 关掉「关卡已载入」对话框失败：" + Describe(e));
                // 兜底：至少别停着
                try { ctrl.GetUIPreset(); } catch (Exception) { }
                return false;
            }
        }

        /// <summary>
        /// 把回放的关键帧载入到**指定的**关卡控制器上。
        ///
        /// <para>调用点是 <see cref="ReplaySceneEntry.OnInitLevel"/> —— 也就是
        /// <c>LevelManager.InitLevel</c> 的前缀里，游戏自己「关卡刚开始」那一刻：
        /// 控制器刚从新加载的场景里 Awake 出来，**还没被 <c>LevelController.InitLevel()</c>
        /// 填过碰撞系统**，正好满足 <c>LoadGame</c> 的要求。</para>
        ///
        /// <para>⚠️ 不能把关键帧载到「已经 Init 过」的控制器上：<c>CreateFromSerializable</c> →
        /// <c>ReadCollisionFromSerializable</c> 会 <c>entities.Add(id)</c> 撞键并抛
        /// <c>ArgumentException: Key: [N, BuiltinCollisionEntity]</c>，而那个异常只落在
        /// Unity 的 Player.log 里（不进 BepInEx 日志），<c>LoadGame</c> 只是返回 false。</para>
        /// </summary>
        /// <summary>
        /// 把关键帧 BSON 里的音乐字段剔掉（<c>[General] RestoreMusic = false</c> 时用）。
        ///
        /// <para>只动 <c>musicID</c> / <c>musicTime</c> / <c>musicVolume</c> 三个键：
        /// 它们对应 <c>SerializableLevelController</c> 上的三个同名字段，
        /// 置成 null / 0 / 1 之后游戏反序列化时会跳过「切曲目」这一步，
        /// 也就不会去 Addressables 加载 <c>main_assets_mainmusic_*.bundle</c>。</para>
        /// </summary>
        private static string StripMusic(string bson)
        {
            if (string.IsNullOrEmpty(bson))
                return bson;
            var s = System.Text.RegularExpressions.Regex.Replace(
                bson, "\"musicID\"\\s*:\\s*\"[^\"]*\"", "\"musicID\" : null");
            s = System.Text.RegularExpressions.Regex.Replace(
                s, "\"musicTime\"\\s*:\\s*[-0-9.eE]+", "\"musicTime\" : 0");
            s = System.Text.RegularExpressions.Regex.Replace(
                s, "\"musicVolume\"\\s*:\\s*[-0-9.eE]+", "\"musicVolume\" : 1");
            return s;
        }

        internal static bool ApplyKeyframeTo(LevelController ctrl, NamespaceID area, NamespaceID stage,
            MVZ2Logic.Level.LevelExitTarget exitTarget)
        {
            if (ctrl == null || ctrl.Pointer == IntPtr.Zero)
            {
                Fail("关卡控制器不存在");
                return false;
            }
            if (file == null)
            {
                Fail("没有正在播放的回放文件");
                return false;
            }

            Plugin.Log?.LogInfo("[Replay] 开始载入关键帧");

            SerializableLevelController seri;
            try
            {
                if (mFromBson == null)
                {
                    Fail("找不到 SerializeHelper.FromBson");
                    return false;
                }
                // tick 0 的关键帧游戏载不进去（LoadGame 直接返回 false）。
                // 录制侧已经改成不收 tick 0 的关键帧了，但旧文件里可能还有。
                if (file.KeyframeTick < 1)
                {
                    Fail("这个回放的关键帧在 tick 0，游戏无法载入这种存档 —— 请用当前版本重新录一遍");
                    return false;
                }
                // 注意：FromBson<T> 在 interop 里是**泛型方法**，而它背后的
                // MethodInfoStoreGeneric_… 类是 internal 的，不能直接引用，
                // 所以这里靠反射把泛型实参补上再调。
                //
                // 关掉 RestoreMusic 时先把音乐字段从 BSON 里剔掉：关键帧里的 musicID
                // 会让游戏去 Addressables 加载 main_assets_mainmusic_*.bundle，
                // 而 0.7.0 正式版的崩溃现场恰好就落在那次加载的依赖解析上。
                var bson = file.KeyframeBson;
                if (!ReplayConfig.RestoreMusicOn)
                {
                    bson = StripMusic(bson);
                    Plugin.Log?.LogInfo("[Replay] [General] RestoreMusic = false：关键帧里的音乐字段已剔除");
                }
                seri = (SerializableLevelController)mFromBson.Invoke(null, new object[] { bson });
            }
            catch (Exception e)
            {
                Fail("关键帧反序列化失败：" + Describe(e));
                return false;
            }
            if (seri == null)
            {
                Fail("关键帧反序列化返回 null");
                return false;
            }
            Plugin.Log?.LogInfo("[Replay] 关键帧反序列化完成");

            bool ok;
            try
            {
                ctrl.SetActive(true);
                ctrl.SetStartStage(area, stage);
                var main = MainManager.Instance;
                if (main == null)
                {
                    Fail("MainManager 不可用");
                    return false;
                }
                ok = ctrl.LoadGame(seri, main.Game, area, stage);
                Plugin.Log?.LogInfo("[Replay] LoadGame 返回 " + ok);
                // 原本 InitLevel 的最后一步（我们跳过了它的函数体，所以要补上）。
                // 它决定「退出关卡后去哪」，属于关卡运行时状态的一部分。
                ctrl.SetExitTarget(exitTarget);
                Plugin.Log?.LogInfo("[Replay] SetExitTarget 完成");
            }
            catch (Exception e)
            {
                Fail("LoadGame 抛异常：" + Describe(e));
                return false;
            }
            if (!ok)
            {
                // LoadGame 里所有失败路径都是「catch → Debug.LogException → return false」，
                // 而 Debug.LogException 只进 Unity 的 Player.log（LocalLow\Cuerzor\…），
                // **不进 BepInEx\LogOutput.log**，所以这里要把话说明白，免得又白查一轮。
                Fail("LoadGame 返回 false。真正的原因在 Unity 的 Player.log 里搜 \"LogException\"："
                    + "若看到 ArgumentException「An item with the same key has already been added."
                    + " Key: [N, BuiltinCollisionEntity]」，说明关键帧被 LoadGame 到了"
                    + "**已经 Init 过**的旧控制器上（正常应当等 GotoLevelSceneAsync 换出新控制器）。");
                return false;
            }

            bound = ctrl;
            currentTick = file.StartTick;
            cursor = 0;

            // 关键帧录的是「关卡刚开始」那一刻，游戏照例会把「关卡已载入」对话框弹出来并暂停。
            // 这里替玩家点「继续」，把暂停解除。
            ResumeFromLoadedDialog(ctrl);

            try
            {
                ReplayHud.Ensure(ctrl);
                ReplayHud.SetProgress(currentTick, file.LastTick);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] REC 标签初始化失败：" + e.GetType().Name + ": " + e.Message);
            }

            Plugin.Log?.LogInfo("[Replay] 关键帧已恢复，开始按 tick 注入（tick " + file.StartTick
                + ".." + file.LastTick + "，事件 " + file.Events.Count + " 条）");
            try
            {
                var eng0 = bound.GetEngine();
                if (eng0 != null)
                    Plugin.Log?.LogInfo("[Replay] 关键帧里的实体 id：" + SurveyEntityIds(eng0));
            }
            catch (Exception) { }
            if (ReplayProbe.Enabled)
            {
                try
                {
                    Plugin.Log?.LogInfo("[ReplayProbe] 卡片槽（来自关键帧）：" + ReplayProbe.SeedPacks(bound.GetEngine()));
                    Plugin.Log?.LogInfo("[ReplayProbe] 载入后手持物：[" + ReplayProbe.Held(bound.GetEngine()) + "]");
                }
                catch (Exception) { }
            }
            CheckKeyframeRoundtrip();
            return true;
        }

        /// <summary>
        /// 检查「载入后状态 == 关键帧状态」。
        ///
        /// <para>做法：用**录制时同一套算法**（<see cref="ReplayRecorder.ComputeFingerprint"/>）
        /// 算一遍载入后的状态，去跟文件里 tick == startTick 的那条指纹比。</para>
        ///
        /// <para>为什么不能简单地重新序列化一遍再逐字符比 BSON：<c>SerializableLevel.components</c>
        /// 是个字典，MongoDB 按枚举顺序写，而字典的枚举顺序在两次运行里不保证一样 ——
        /// 实测原关键帧以 <c>"model"</c> 开头、重新序列化以 <c>"level"</c> 开头，
        /// 一开头就“不一致”，全是噪声。</para>
        /// </summary>
        private static void CheckKeyframeRoundtrip()
        {
            try
            {
                var lvl = bound.GetEngine();
                if (lvl == null)
                    return;
                var fp = ReplayRecorder.ComputeFingerprint(lvl, file.StartTick, out _);
                if (fp == null)
                    return;

                ReplayFingerprint expected = null;
                for (int i = 0; i < file.Fingerprints.Count; i++)
                {
                    if (file.Fingerprints[i].Tick == file.StartTick)
                    {
                        expected = file.Fingerprints[i];
                        break;
                    }
                }
                if (expected == null)
                {
                    Plugin.Log?.LogInfo("[Replay] 载入后状态：" + fp
                        + "（文件里没有 tick=" + file.StartTick + " 的指纹，无法精确比对 —— 旧格式录的回放）");
                    return;
                }

                var diff = CompareFingerprint(expected, fp);
                if (diff == null)
                {
                    Plugin.Log?.LogInfo("[Replay] 载入后状态与关键帧一致 ✅ " + fp);
                }
                else
                {
                    Plugin.Log?.LogWarning("[Replay] 载入后状态与关键帧不一致 ❌ " + diff
                        + "\n  关键帧: " + expected + "\n  载入后: " + fp);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 载入后状态比对失败：" + Describe(e));
            }
        }
        #endregion

        #region tick 注入
        /// <summary><c>LevelEngine.Update</c> 前缀：把到期的动作事件派发出去。</summary>
        public static void InjectDue(LevelEngine lvl)
        {
            if (phase != Phase.Playing || injecting)
                return;
            try
            {
                if (lvl == null || bound == null || bound.Pointer == IntPtr.Zero)
                    return;
                var engine = bound.GetEngine();
                if (engine == null || engine.Pointer != lvl.Pointer)
                    return;

                int tick = ToTick(lvl.GetLevelTime());
                currentTick = tick;

                // v9：把「这一 tick **内部**」的指针状态定下来。
                //
                // ⚠️ 时序（这里曾经差了一整个 tick，后果是重装兵器多发一颗子弹）：
                // Unity 的 Input 在**一帧内是常量**，而采样点与逻辑 tick 在同一帧：
                //     帧 F：Input = I(F)
                //       GameUpdater.Update() → 跑 tick T（tick 内读 Input 看到的就是 I(F)）
                //       采样点（tick 后）     → sample@T = I(F)
                //     即 **tick T 内部读到的输入 == sample@T**。
                // 所以这里用「第 T 条」采样，而不是「第 T−1 条」。
                //
                // ⚠️ 并且要**在 tick 逻辑之前**把 POST_POINTER_ACTION 发出去：
                // 打砖块挡板是**绝对赋值**（`position = ScreenToLawnPositionByY(screenPos, 32)`），
                // 游戏里它在逻辑 tick 之前就已经被摆到 I(F) 了。若拖到 tick 之后才摆，
                // 挡板就慢一帧、珠子会在另一个 tick 落空（实测：录制 `breakout_pearl=2`、回放只剩 1）。
                // 放在前缀里连同事件一起发，恰好就是「先输入层、再路由事件」的顺序。
                trackCovered = ReplayPointerTrajectory.BeginTick(tick,
                    out var trackScreen, out var trackPrev, out var trackCur);
                if (trackCovered)
                {
                    var trackNow = ReplayViewport.ToCurrent(trackScreen);
                    ReplayPointerAction.FirePhases(MainManager.Instance,
                        trackNow.x, trackNow.y, trackPrev, trackCur);
                }

                // 没有轨迹数据时的兜底：本 tick **开始**时的按键状态当作「上一 tick」。
                // （本轮的事件会在 Dispatch 里通过 UpdateEventMask 追加进来，
                //   到 AfterTick 时 eventMask 就是「本 tick 结束时」。）
                cycleStartMask = eventMask;

                // 先推进假鼠标（纯视觉），再派发事件 —— 这样「到站」的那一刻
                // 正好就是动作发生的那一个 tick。
                FakePointer.Tick(bound, engine, tick);

                var events = file.Events;
                while (cursor < events.Count && events[cursor].Tick <= tick)
                {
                    var ev = events[cursor];
                    cursor++;
                    Dispatch(ev);
                }
            }
            catch (Exception e)
            {
                Note("注入出错：" + Describe(e));
                invokeErrors++;
            }
        }

        /// <summary><c>LevelEngine.Update</c> 后缀：核对指纹、刷新 REC 标签。</summary>
        public static void AfterTick(LevelEngine lvl)
        {
            if (phase != Phase.Playing)
                return;
            try
            {
                if (lvl == null || bound == null || bound.Pointer == IntPtr.Zero)
                    return;
                var engine = bound.GetEngine();
                if (engine == null || engine.Pointer != lvl.Pointer)
                    return;

                int tick = ToTick(lvl.GetLevelTime());
                currentTick = tick;

                // 逐 tick 心跳（写独立文件 + AutoFlush）：崩溃前最后一行 = 真正的死亡 tick。
                // 见 ReplayConveyorGuard 里「为什么不能只靠 BepInEx 日志」。
                ReplayConveyorGuard.Beat(tick, "playing");

                // v9：按键状态的**兜底**：本 tick 没有轨迹数据时，`TryGetPointerPhase` /
                // `CurrentPointerMask` 会把这两个值当作本轮答案，而游戏的
                // `UpdateHoldAndStreak()` 每帧都要问「鼠标还按着吗」——
                // 拖动（Down → … → Up）中间那几个 tick 一旦变成「没按」，拖动就断了。
                //
                // 有轨迹时不需要这两行（那边直接读 `ReplayPointerTrajectory`）。
                if (!trackCovered)
                {
                    maskPrev = cycleStartMask;
                    maskNow = eventMask;
                }

                CheckFingerprints(lvl, tick);
                ReplayConveyorGuard.Beat(tick, "after-fp-in");
                ReplayHud.SetProgress(tick, file.LastTick);
                ReplayConveyorGuard.Beat(tick, "playing-after");

                // 调试用：只播到指定 tick 就收尾。
                if (tickLimit > 0 && tick >= tickLimit)
                {
                    Plugin.Log?.LogInfo("[Replay] 已到 PlayTickLimit=" + tickLimit + "，收尾");
                    FinishPlayback("tick-limit");
                    return;
                }

                // 片尾：所有事件都放完了，且已经跑过录制结束时的 tick。
                if (cursor >= file.Events.Count && tick >= file.LastTick)
                {
                    Plugin.Log?.LogInfo("[Replay] 回放播放完毕（tick " + tick + "）");
                    FinishPlayback("finished");
                }
            }
            catch (Exception e)
            {
                Note("tick 收尾出错：" + Describe(e));
            }
        }

        /// <summary>
        /// 找回实体事件的「锚点实体」。分级匹配：**(定义+分层)** → **(定义)** → **(任意)**。
        ///
        /// <para><b>为什么不能只认 ID：</b>实体 ID 是关卡内自增的，只要录制与回放之间有
        /// **任何一处生成时机不同（实测差 1~2 个 tick 就够）**，后面的 ID 就整体错位：
        /// 轻则 <c>FindEntityByID</c> 返回 null（事件被丢弃），重则返回**另外一个实体**
        /// （静默地把动作做到错的东西上，比丢掉更糟）。</para>
        ///
        /// <para><b>为什么「分层」是必需的（镐子挖错株）：</b>同一格最多能同时站 4 个可被
        /// 镐子挖掉的东西 —— <c>承载位 / 保护位 / 主要位 / 工具位</c> —— 它们**坐标完全相同**
        /// （同一个格子），所以「同定义 + 离记录坐标最近」根本分不出是哪一个。
        /// 而游戏的目标判定恰恰**从锚点身份出发**：</para>
        /// <code>
        /// // VanillaEntityExt.FindPointerTargetEntity（实体路径，电脑端走的就是这条）
        /// var protectTargets = entity.GetProtectingTargets();  // ← 只有锚点是「保护位」才有非空结果
        /// if (firstValidMainTarget != null &amp;&amp; (!canUseOnProtector || pointerPositionY &gt;= 0.5f))
        ///     return firstValidMainTarget;                     // 只能挖里面那个 → 返回它
        /// return entity;                                       // 否则就是锚点自己
        /// </code>
        /// <para>锚点从「保护位」变成「主要位」，<c>GetProtectingTargets()</c> 就直接是空数组、
        /// 函数立刻返回锚点自己 —— 于是镐子挖到另一株。加上分层这一维就能唯一确定同格里的哪一个。</para>
        ///
        /// <para><b>电脑端与移动端的判定不同，本 mod 只做电脑端。</b>两者连**锚点是怎么来的**都不一样：</para>
        /// <list type="bullet">
        /// <item><b>电脑端（鼠标）</b>：<c>LevelRaycaster</c> 用 <c>castRadius &lt;= 0</c> 的**精确射线**
        /// （<c>Physics2D.GetRayIntersectionAll</c>），锚点 = 光标那一个像素落在谁身上；
        /// 且 <c>ToEntityHeldItemBehaviour.IsValidFor</c> 只在 <c>pointer.type == MOUSE</c> 时接受
        /// <c>HeldItemTargetEntity</c> —— 也就是**电脑端走的是实体路径**。
        /// 鼠标有像素级精度，所以哪怕同格三层俱全，**蹭着保护位露在外面那条边**也能选中它
        /// （这也是上面那段 <c>pointerPositionY</c> 分支在电脑端唯一的用武之地：
        /// 已经点中保护位时，偏上就挖里面那个、偏下就挖保护位自己）。</item>
        /// <item><b>移动端（触摸）</b>：<c>SetHeldItem(..., radius)</c> 给的是**胖手指圆投射**
        /// （<c>Physics2D.CircleCastAll</c>），锚点退化成**格子**，于是走
        /// <c>VanillaGridExt.FindPointerTargetEntity(grid, pointerPosition, ...)</c> ——
        /// 那条路径才是"拿指针高度和 <c>0.5</c> 比大小、上半选主要位 / 下半选保护位"，
        /// 并且还会把 <c>rangeMin/rangeMax</c> 交给高亮（只亮可用的那半格）。</item>
        /// </list>
        /// <para>⇒ 不要拿"上下半格"去解释电脑端的现象，也不要用它来推锚点。
        /// 电脑端要还原的只有两件事：**当时点中的是哪一个实体**（本函数用定义 + 分层 + 坐标定位）
        /// 与**当时的指针位置**（<c>wx/wy/wz</c>，供 <c>LocalPointerPosition.y</c> 与
        /// 那几个直接问 <c>Global.Input</c> 的行为使用）。</para>
        ///
        /// <para>全部失败时返回 null（宁可跳过，也不要打错目标）。</para>
        /// </summary>
        private static Entity ResolveEntity(LevelEngine engine, ReplayEvent ev, out bool idWasWrong, out string failNote)
        {
            idWasWrong = false;
            failNote = null;
            var wantDef = ev.EntityDef;
            var wantLayer = ev.EntityLayer;

            Entity byId = null;
            try
            {
                var e = engine.FindEntityByID(ev.EntityId);
                // interop 里方法返回引用类型时可能给出「包着空指针的假对象」，所以还要看 Pointer。
                if (e != null && e.Pointer != IntPtr.Zero)
                    byId = e;
            }
            catch (Exception)
            {
            }

            // ① ID 命中，定义（有记录就还要）分层也对得上 → 直接用。
            if (byId != null && (string.IsNullOrEmpty(wantDef) || SafeDefId(byId) == wantDef)
                && LayerMatches(engine, byId, wantLayer))
                return byId;

            if (ev.HasEntityPos)
            {
                var target = new Vector3(ev.Ex, ev.Ey, ev.Ez);
                var best = FindBestAnchor(engine, wantDef, wantLayer, target, out var tier, out var info);
                if (best != null)
                {
                    recoveredCount++;
                    idWasWrong = byId != null;
                    if (tier <= 1)
                        defMismatchCount++;
                    else
                        positionFallbackCount++;
                    if (tier == 0)
                        layerRecoveredCount++;
                    int shown = tier == 0 ? layerRecoveredCount : (tier == 1 ? defMismatchCount : positionFallbackCount);
                    if (shown <= 3)
                        Plugin.Log?.LogWarning("[Replay] 实体 id=" + ev.EntityId
                            + " 在回放里对不上（录下的是 " + (wantDef ?? "?")
                            + (string.IsNullOrEmpty(wantLayer) ? "，非分层实体" : "，分层 " + wantLayer)
                            + "），按 " + TierName(tier) + " 找回："
                            + (SafeDefId(best) ?? "?") + "#" + SafeId(best)
                            + "（分层 " + (ReplayRecorder.LayerKeyOf(engine, best) ?? "?") + "，"
                            + (info ?? "?") + "）（tick " + ev.Tick + "）");
                    return best;
                }
                lastNearestInfo = info ?? lastNearestInfo;
            }

            if (byId != null)
            {
                // ID 找到了但定义对不上，而兜底也失败 —— 这多半已经打错东西了，跳过。
                defMismatchCount++;
                skippedWrongTarget++;
                failNote = "id 指向的是 " + (SafeDefId(byId) ?? "?") + "（分层 "
                    + (ReplayRecorder.LayerKeyOf(engine, byId) ?? "?")
                    + "），与录下的 " + wantDef + "+" + (wantLayer ?? "?")
                    + " 不同，且兜底失败（" + (lastNearestInfo ?? "?") + "）";
                if (defMismatchCount <= 3)
                    Plugin.Log?.LogWarning("[Replay] 实体 id=" + ev.EntityId + " 是 " + SafeDefId(byId)
                        + "，而录下的是 " + wantDef + "+" + (wantLayer ?? "?")
                        + "；兜底也找不到 → 跳过（tick " + ev.Tick + "）"
                        + "  最近同定义：" + (lastNearestInfo ?? "-"));
                return null;
            }
            failNote = "id 在关卡里不存在，且兜底失败（" + (lastNearestInfo ?? "?") + "）";
            return null;
        }

        private static string TierName(int tier)
        {
            switch (tier)
            {
                case 0: return "定义+分层";
                case 1: return "定义";
                default: return "记录坐标";
            }
        }

        private static string SafeDefId(Entity e)
        {
            return ReplayRecorder.DefIdOf(e);
        }

        private static long SafeId(Entity e)
        {
            try { return e != null ? e.ID : -1; } catch (Exception) { return -1; }
        }

        /// <summary>
        /// 候选实体的分层与录下的分层是否相容。
        /// 录下的分层是 null（旧文件 / 计算失败）时**不做判别** —— 不能因为读不到就否掉。
        /// </summary>
        private static bool LayerMatches(LevelEngine engine, Entity e, string wantLayer)
        {
            if (wantLayer == null)
                return true;
            var got = ReplayRecorder.LayerKeyOf(engine, e);
            if (got == null)
                return true;
            return string.Equals(got, wantLayer, StringComparison.Ordinal);
        }

        /// <summary>
        /// 三级锚点搜索：优先 (定义+分层)，其次 (定义)，最后 (任意实体)；
        /// 同一级内取离记录坐标最近的，距离再相同就取 ID 最小的（保证跨运行可复现）。
        ///
        /// <para>全部限制在 <see cref="EntityMatchRadius"/> 以内 —— 超出半径的候选不如没有，
        /// 拿它当锚点只会让游戏就近挑到一个完全无关的目标。</para>
        /// </summary>
        private static Entity FindBestAnchor(LevelEngine engine, string wantDef, string wantLayer,
            Vector3 target, out int tier, out string info)
        {
            tier = -1;
            info = null;
            var all = ReplayRecorder.EnumerateEntities(engine);
            if (all == null)
            {
                info = "枚举失败";
                return null;
            }

            Entity best = null;
            int bestRank = int.MaxValue;
            float bestSqr = float.MaxValue;
            long bestId = long.MaxValue;
            int seenDef = 0, seenDefLayer = 0;
            bool hasDef = !string.IsNullOrEmpty(wantDef);

            for (int i = 0; i < all.Length; i++)
            {
                var e = all[i];
                if (e == null || e.Pointer == IntPtr.Zero)
                    continue;
                Vector3 p;
                try { p = e.Position; } catch (Exception) { continue; }
                float dx = p.x - target.x, dy = p.y - target.y, dz = p.z - target.z;
                float sqr = dx * dx + dy * dy + dz * dz;
                if (sqr > EntityMatchRadius * EntityMatchRadius)
                    continue;

                int rank;
                if (hasDef && SafeDefId(e) == wantDef)
                {
                    seenDef++;
                    // 分层只在定义对上时才需要算（贵），也只有这一层用得到它。
                    if (wantLayer != null && string.Equals(ReplayRecorder.LayerKeyOf(engine, e), wantLayer, StringComparison.Ordinal))
                    {
                        seenDefLayer++;
                        rank = 0;
                    }
                    else
                    {
                        rank = 1;
                    }
                }
                else
                {
                    rank = 2;
                }

                long id = SafeId(e);
                if (rank < bestRank
                    || (rank == bestRank && sqr < bestSqr)
                    || (rank == bestRank && sqr == bestSqr && id < bestId))
                {
                    bestRank = rank;
                    bestSqr = sqr;
                    best = e;
                    bestId = id;
                }
            }

            if (best == null)
            {
                info = string.IsNullOrEmpty(wantDef) ? "半径内一个实体都没有"
                    : (wantDef + "：半径内一个都没有");
                return null;
            }

            tier = bestRank;
            var dist = Mathf.Sqrt(bestSqr).ToString("F1");
            if (bestRank == 0)
                info = "同定义同分层 " + seenDefLayer + " 个，距离 " + dist + " 单位";
            else if (bestRank == 1)
                info = "同定义 " + seenDef + " 个（分层都对不上的 " + (seenDef - seenDefLayer) + " 个），距离 " + dist + " 单位";
            else
                info = "同定义的一个都没找到，退到任意实体，距离 " + dist + " 单位";
            return best;
        }

        /// <summary>最近一次「按定义找实体」的结果描述（排障用）。</summary>
        private static string lastNearestInfo;

        /// <summary>
        /// 把本次播放的结果写进回放健康记录（列表里名字的颜色、下次播放前的弹窗）。
        ///
        /// <para><b>规则</b>（需求）：出现任何问题 → <b>红</b>；
        /// 没问题且已经跑到总时长的一半以上 → <b>白</b>；
        /// 跑得不够久（提前退出/中途被停）→ **不动**，保留上次的判断 ——
        /// 没看到的事不能当成「没问题」。</para>
        ///
        /// <para>写盘是读-改-写整个索引文件，但一次播放只会在收尾时调一次，开销可忽略。</para>
        /// </summary>
        private static void RecordHealth(bool failed)
        {
            try
            {
                var n = CurrentName;
                if (string.IsNullOrEmpty(n))
                    return;
                var dir = ReplayConfig.ResolveOutputDir();
                if (string.IsNullOrEmpty(dir))
                    return;

                if (failed || fpMismatch > 0)
                {
                    // 出过问题 → 红标，并**清掉**之前的「已验证」记录
                    // （不一样的环境/数据没法互相担保）。
                    ReplayIndex.UpdateHealth(dir, n, ReplayHealth.Bad, fpMismatch, FirstMismatchTick, null);
                    return;
                }

                int st = file != null ? file.StartTick : 0;
                int lt = file != null ? file.LastTick : 0;
                // 降级门槛：跑到总时长的 **20%** 且一条指纹都没错，就认为「这条在当前环境里能正常播」。
                //
                // 原来是 50%，实测太严：一条两万 tick 的回放要跑满一万 tick 才算数，
                // 而玩家看一眼就走了 —— 结果黄标永远降不下来（用户实测反馈）。
                // 20% 仍能覆盖「关键帧载入 + 前几波」那段最容易翻车的地方。
                if (lt > st && currentTick >= st + (lt - st) / 5)
                {
                    // 跑到门槛以上、一条指纹都没错 → 白标，并记住「是在这个环境下验证的」。
                    // 列表据此把「环境不同 / 旧格式」这类静态推断的黄标降级。
                    var env = ReplayLibrary.CurrentEnvHash();
                    if (string.IsNullOrEmpty(env))
                        env = ReplayInfo.VerifiedEnvUnknown;
                    ReplayIndex.UpdateHealth(dir, n, ReplayHealth.Ok, 0, 0, env);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 写回放健康度失败："
                    + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void FinishPlayback(string reason)
        {
            // 只允许「还在跑的时候」收尾一次。重复调用（例如结束面板开着的时候又触发一遍）
            // 会把面板重弹一次、还会把小结冲掉。
            if (!IsActive)
                return;

            phase = Phase.Ended;
            stopReason = reason;
            elapsedSeconds = Time.realtimeSinceStartup - phaseStart;
            lastSummary = BuildSummary();
            RecordHealth(false);

            // 收尾时把 REC 标签和假鼠标收掉 —— 和 Stop()/Fail() 保持一致。
            // （不回放期间它们本来还在屏幕上留着，REC 会一直停在最后一帧的数字上。）
            ReplayHud.Hide();
            FakePointer.End();

            try { Global.Debugs?.Print(lastSummary + "\n"); } catch (Exception) { }
            // 回放结束了，把结束面板弹出来（导出 / 重新播放 / 退出）。
            ReplayEndPanel.Show(reason);
        }

        /// <summary>
        /// 「关卡通关」被拦截时的收尾。
        ///
        /// <para>⛔ 必须拦：原版 <c>OnEngineClearCallback</c> 会
        /// <c>RemoveLevelState()</c>（删掉该关卡的存档文件！）+ 解锁关卡 +
        /// 写难度记录 + 走通关对话与退场动画。回放只是在「重演一局」，
        /// 拿它去改玩家的存档是错的。</para>
        /// </summary>
        public static void NotifyLevelCleared()
        {
            if (phase != Phase.Playing)
                return;
            Plugin.Log?.LogInfo("[Replay] 回放里关卡通关了（已拦下原版的解锁/存档/退场流程）");
            FinishPlayback("level-cleared");
        }

        /// <summary>
        /// 「关卡失败」被拦截时的收尾。
        ///
        /// <para>原版会弹自己的「重试 / 返回」对话框并暂停关卡；回放里我们弹自己的面板，
        /// 两条对话框兼存只会让人无所适从。</para>
        /// </summary>
        public static void NotifyGameOver()
        {
            if (phase != Phase.Playing)
                return;
            Plugin.Log?.LogInfo("[Replay] 回放里关卡失败了（已拦下原版的失败对话框）");
            FinishPlayback("game-over");
        }

        /// <summary>
        /// 把当前关卡里存在的实体列出来。只用于排障「实体 ID 对不上」。
        /// </summary>
        private static string SurveyEntityIds(LevelEngine ent)
        {
            var all = ReplayRecorder.EnumerateEntities(ent);
            if (all == null)
                return "（枚举失败）";
            var sb = new StringBuilder();
            int count = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var e = all[i];
                if (e == null || e.Pointer == IntPtr.Zero)
                    continue;
                if (count > 0)
                    sb.Append(',');
                try { sb.Append(e.ID); } catch (Exception) { sb.Append('?'); }
                count++;
                if (count >= 40)
                {
                    sb.Append(",...共").Append(all.Length);
                    break;
                }
            }
            return count == 0 ? "（空）" : sb.ToString();
        }

        private static int GetTickLimit()
        {
#if DIAGNOSTICS
            try
            {
                return ReplayConfig.SelfTestPlayTickLimit != null
                    ? ReplayConfig.SelfTestPlayTickLimit.Value : 0;
            }
            catch (Exception)
            {
                return 0;
            }
#else
            // 发布构建里没有自测（Diagnostics\ 整份不编译），也就没有「停在第 N tick」这个开关。
            return 0;
#endif
        }

        /// <summary>
        /// 核对用的采样策略：**从文件推导**，不看配置。
        ///
        /// <para>规则很简单 —— 文件说自己是调试文件，我们就把调试采样器也跑起来，
        /// 这样那些 <c>rx</c>/<c>ei</c>/<c>lk</c> 字段才有人去算、才比得上。
        /// 反过来，普通文件只跑常规采样器（多算也不会被比 —— 比对只认**文件里有**的键）。</para>
        ///
        /// <para><c>FullFingerprint</c> 一律给 true：多算出来的字段不会进比对（见
        /// <see cref="CompareFingerprint"/>），但万一下一个版本的回放带了我们没算的字段，
        /// 多算总好过没得比。</para>
        /// </summary>
        private static ReplayRecordOptions BuildVerifyOptions(ReplayFile f)
        {
            var p = f != null ? f.Policy : null;
            bool debug = p != null && p.Debug;
            return new ReplayRecordOptions
            {
                Debug = debug,
                DebugEveryTick = debug,
                DebugRawRng = debug,
                DebugEntityDetail = debug,
                DebugLevelScalars = debug,
                FullFingerprint = true,
            };
        }

        /// <summary>
        /// 比对「录制时的环境」与「当前环境」。
        ///
        /// <para><b>只警告、不阻止播放</b>（既定策略）：环境不同多半仍能播，
        /// 硬拦只会把「换了台机器想看看回放」的用户挡在门外。但**必须说明原因** ——
        /// 因为环境不同正是「回放与实战不符」最常见的解释之一，
        /// 而玩家从画面里根本看不出来。</para>
        ///
        /// <para>内容 mod 的关卡数据版本单独拎出来：它不一致会让关键帧**根本载不进去**，
        /// 属于「不是『略有偏差』而是『必然失败』」的那一类。</para>
        /// </summary>
        private static void CheckEnvironment(ReplayFile f)
        {
            envDiff.Clear();
            envDataVersionDiff.Clear();
            try
            {
                if (f == null || !f.HasEnv)
                    return;
                var now = ReplayEnvironment.Capture();
                envDiff.AddRange(f.Environment.Diff(now));
                envDataVersionDiff.AddRange(f.Environment.ContentModVersionMismatches(now));

                if (envDiff.Count > 0)
                {
                    Plugin.Log?.LogWarning("[Replay] 回放环境与当前环境不同（仍会播放）：");
                    for (int i = 0; i < envDiff.Count && i < 12; i++)
                        Plugin.Log?.LogWarning("[Replay]   · " + envDiff[i]);
                    if (envDiff.Count > 12)
                        Plugin.Log?.LogWarning("[Replay]   · …还有 " + (envDiff.Count - 12) + " 项");
                }
                if (envDataVersionDiff.Count > 0)
                {
                    Plugin.Log?.LogWarning("[Replay] ⚠ 内容 mod 的关卡数据版本不同，关键帧可能载不进去："
                        + string.Join("；", envDataVersionDiff.ToArray()));
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 环境比对失败：" + e.GetType().Name);
            }
        }

        private static void CheckFingerprints(LevelEngine lvl, int tick)
        {
            var fps = file.Fingerprints;
            while (fpIndexForCheck < fps.Count && fps[fpIndexForCheck].Tick < tick)
                fpIndexForCheck++;                       // 错过的（例如暂停期间）直接跳过
            if (fpIndexForCheck >= fps.Count)
                return;
            var expected = fps[fpIndexForCheck];
            if (expected.Tick != tick)
                return;
            fpIndexForCheck++;

            var actual = ReplayRecorder.ComputeFingerprint(lvl, tick, out _, verifyOptions);
            if (actual == null)
                return;
            fpCompared++;

            // 先记下「文件里有、我们这次没算出来」的字段：它们既不算一致也不算不一致。
            CountUnverifiedFields(expected, actual);

            var diff = CompareFingerprint(expected, actual);
            if (diff == null)
            {
                fpMatched++;
                return;
            }
            fpMismatch++;
            if (fpMismatchTicks.Count < 400)
                fpMismatchTicks.Add(tick);
            // 第一条不一致最值钱，直接落盘（报告只在最后写，中途卡死也能拿到线索）。
            if (fpMismatch == 1)
                Plugin.Log?.LogWarning("[Replay] 首次状态不一致：tick " + tick + "  " + diff
                    + "  （此时已注入 " + cursor + "/" + file.Events.Count + " 条事件）");
            else if (fpMismatch <= 5)
                Plugin.Log?.LogInfo("[Replay] 第 " + fpMismatch + " 条不一致：tick " + tick + "  " + diff);
            if (fpReports.Count < MaxReports)
                fpReports.Add("tick " + tick + " 不一致：" + diff);
            else if (fpReports.Count == MaxReports)
                fpReports.Add("...（还有更多不一致，已省略）");
        }

        /// <summary>
        /// 比一条指纹。参数顺序是 <c>(录制, 回放)</c>，所以消息里的 <c>→</c> 一律是「录制 → 回放」。
        /// </summary>
        private static string CompareFingerprint(ReplayFingerprint a, ReplayFingerprint b)
        {
            var sb = new StringBuilder();
            if (a.Enemies != b.Enemies) sb.Append("能量 ").Append(a.Enemies).Append("→").Append(b.Enemies).Append(' ');
            if (a.Wave != b.Wave) sb.Append("波次 ").Append(a.Wave).Append("→").Append(b.Wave).Append(' ');
            if (a.Flags != b.Flags) sb.Append("旗数 ").Append(a.Flags).Append("→").Append(b.Flags).Append(' ');
            if (a.Cleared != b.Cleared) sb.Append("已清关 ").Append(a.Cleared).Append("→").Append(b.Cleared).Append(' ');
            // 实体集合：这一项不一致就说明「同一个 id 在两边不是同一个东西」——比 RNG 分歧更早、更准确。
            if (a.EntityCount >= 0 && a.EntityCount != b.EntityCount)
                sb.Append("实体数 ").Append(a.EntityCount).Append("→").Append(b.EntityCount).Append(' ');
            if (!string.IsNullOrEmpty(a.EntityHash) && !string.Equals(a.EntityHash, b.EntityHash, StringComparison.Ordinal))
                sb.Append("实体集合 ").Append(a.EntityHash).Append("→").Append(b.EntityHash).Append(' ');
            // 可读版：直接说「哪一类实体数量变了」。少了 redstone 就是「资源没生成」这类问题的直接证据。
            if (!string.IsNullOrEmpty(a.EntityDefs) && !string.IsNullOrEmpty(b.EntityDefs)
                && !string.Equals(a.EntityDefs, b.EntityDefs, StringComparison.Ordinal))
                sb.Append("实体构成 [").Append(a.EntityDefs).Append("]→[").Append(b.EntityDefs).Append("] ");
            if (!string.Equals(a.Rng, b.Rng, StringComparison.Ordinal))
            {
                sb.Append("RNG ").Append(a.Rng).Append("→").Append(b.Rng);
                var who = DivergentRngStreams(a, b);
                if (who != null)
                    sb.Append("（先跑偏的是 ").Append(who).Append(" 流）");
                sb.Append(' ');
            }

            // ---- 附加字段（协议 v10）----
            //
            // **通用**比对：只认「录制侧有」的键。这样「加一种新指纹」＝注册一个采样器，
            // 播放侧的比对代码一行都不用改（见 ReplaySamplers）。
            //
            // 录制侧没有的键我们**不去猜** —— 那是「这条指纹只记了一部分」（旧文件 / 采样器被关掉），
            // 不是「回放算错了」。
            for (int i = 0; i < a.Extra.Count; i++)
            {
                var kv = a.Extra[i];
                var mine = b.ExtraOf(kv.Key);
                if (mine == null)
                    continue; // 没算出来 → 由 CountUnverifiedFields 单独计数
                if (string.Equals(mine, kv.Value, StringComparison.Ordinal))
                    continue;

                // 调试字段（实体明细 / 原始 RNG）动辄几百字符，整串贴出来没人看 ——
                // 直接给出**第一个不同处**：排查「谁先变」时这一条就是答案。
                if (kv.Value != null && kv.Value.Length > 72)
                {
                    sb.Append(kv.Key).Append(' ')
                      .Append(FirstDifference(kv.Value, mine) ?? "（内容不同）").Append(' ');
                }
                else
                {
                    sb.Append(kv.Key).Append(" [").Append(kv.Value).Append("]→[")
                      .Append(mine).Append("] ");
                }
            }
            return sb.Length == 0 ? null : sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 统计「文件里有、我们没能算出来」的指纹字段。
        ///
        /// <para>它们既不是一致也不是不一致 —— 单独报出来，避免被误读成「全都对上了」。
        /// 常见来源：别的版本写的字段、第三方采样器写的字段、或者本场把某个采样器关掉了。</para>
        /// </summary>
        private static void CountUnverifiedFields(ReplayFingerprint recorded, ReplayFingerprint actual)
        {
            for (int i = 0; i < recorded.Extra.Count; i++)
            {
                var key = recorded.Extra[i].Key;
                if (actual.ExtraOf(key) != null)
                    continue;
                fpUnverifiedFields++;
                if (fpUnverifiedNames.Count < 8 && !fpUnverifiedNames.Contains(key))
                    fpUnverifiedNames.Add(key);
            }
        }

        /// <summary>
        /// 两个长字符串的**第一个不同处**（含两侧各一小段上下文）。
        ///
        /// <para>用于实体明细（<c>id:定义:血量:坐标:分层</c> 用 <c>;</c> 连接）与原始 RNG
        /// （用 <c>|</c> 分流的 <c>x,y,z,w</c>）：整串对比只能说「不一样」，
        /// 而「第 37 个字符开始不同」能直接指出是**哪个实体的哪个量**。</para>
        /// </summary>
        private static string FirstDifference(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return null;
            int n = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < n && a[i] == b[i])
                i++;
            if (i == n && a.Length == b.Length)
                return null;
            return "首个不同（第 " + i + " 字符）：" + Token(a, i) + " → " + Token(b, i);
        }

        /// <summary>取位置 <paramref name="at"/> 所在的「一段」（按 <c>;</c> / <c>|</c> / <c>,</c> 切）。</summary>
        private static string Token(string s, int at)
        {
            if (string.IsNullOrEmpty(s))
                return "-";
            if (at >= s.Length)
                return "(结尾)";
            int start = at;
            while (start > 0 && s[start - 1] != ';' && s[start - 1] != '|' && s[start - 1] != ',')
                start--;
            int end = at;
            while (end < s.Length && s[end] != ';' && s[end] != '|' && s[end] != ',')
                end++;
            if (end - start > 60)
                end = start + 60;
            return s.Substring(start, end - start);
        }

        /// <summary>
        /// 指出 7 条 RNG 流里哪几条不一样（录制侧与回放侧都有分流的哈希时才有意义）。
        /// 合并哈希只能告诉你「有 RNG 跑偏」，分流才能把范围缩到具体子系统：
        /// spawn = 出怪/波次、round = 波次计数、effect = 特效、entity = 实体行为、
        /// conveyor = 传送带、misc = 其它、level = 关卡通用。
        /// </summary>
        private static string DivergentRngStreams(ReplayFingerprint a, ReplayFingerprint b)
        {
            var x = a.RngStreams;
            var y = b.RngStreams;
            if (x == null || y == null || x.Length != y.Length)
                return null;
            var names = ReplayFingerprint.RngStreamNames;
            var sb = new StringBuilder();
            for (int i = 0; i < x.Length; i++)
            {
                if (string.Equals(x[i], y[i], StringComparison.Ordinal))
                    continue;
                if (sb.Length > 0)
                    sb.Append('/');
                sb.Append(i < names.Length ? names[i] : ("#" + i));
            }
            return sb.Length == 0 ? null : sb.ToString();
        }
        #endregion

        #region 事件派发
        /// <summary>
        /// 从一条录制事件更新**兜底**的按键掩码。
        ///
        /// <para>只能看出「按下 / 抬起」的边沿（<c>Down</c> 置位、<c>Up</c> 清零），
        /// 中间没有事件的那段则沿用上一次的值 —— 这对「按住不放」恰好是对的，
        /// 而且正是游戏每帧的 <c>UpdateHoldAndStreak</c> 所依赖的。</para>
        ///
        /// <para>有逐 tick 轨迹时以轨迹为准（那是真实按键状态，<see cref="CurrentPointerMask"/> 会优先取）。</para>
        /// </summary>
        private static void UpdateEventMask(ReplayEvent ev)
        {
            if (ev.Button < 0 || ev.Button > 2)
                return;
            int bit = 1 << ev.Button;
            switch (ev.Interaction)
            {
                case (int)MVZ2Logic.Inputs.PointerInteraction.Down:
                case (int)MVZ2Logic.Inputs.PointerInteraction.Hold:
                case (int)MVZ2Logic.Inputs.PointerInteraction.BeginDrag:
                case (int)MVZ2Logic.Inputs.PointerInteraction.Drag:
                case (int)MVZ2Logic.Inputs.PointerInteraction.Streak:
                    eventMask |= bit;
                    break;
                case (int)MVZ2Logic.Inputs.PointerInteraction.Up:
                case (int)MVZ2Logic.Inputs.PointerInteraction.Release:
                case (int)MVZ2Logic.Inputs.PointerInteraction.Click:
                case (int)MVZ2Logic.Inputs.PointerInteraction.EndDrag:
                case (int)MVZ2Logic.Inputs.PointerInteraction.Drop:
                    eventMask &= ~bit;
                    break;
            }
        }

        private static void Dispatch(ReplayEvent ev)
        {
            injecting = true;
            try
            {
                if (injectedByKind.TryGetValue(ev.Kind, out var n))
                    injectedByKind[ev.Kind] = n + 1;
                else
                    injectedByKind[ev.Kind] = 1;

                // 按键状态的兜底值：本 tick 没有轨迹数据时，
                // InputHelper.IsPointerHolding 类的查询就靠它回答。
                UpdateEventMask(ev);

                // 先给**输入层的全局回调**发一遍指针动作，再走路由事件。
                // 为什么两条都要发：它们彼此不重叠（详见 ReplayPointerAction）——
                // 「右键取消手持物」「碎石墙/打砖块」只挂在前者上。
                //
                // ⚠️ 但**本 tick 有轨迹数据**时不能再走这条：AfterTick 已经每 tick 统一发过一次，
                // 两条都发会让同一次按下被处理两遍（碾压墙一次点击扣两次进度）。
                // 反之，本 tick 没有轨迹数据（轨迹是后段才开始录的）时必须走这条 ——
                // 否则那一段的「按下」谁也收不到，等于把输入整个吞掉了。
                if (!trackCovered && ReplayPointerAction.Enabled)
                {
                    switch (ev.Kind)
                    {
                        case "grid":
                        case "entity":
                        case "lawn":
                        case "blueprint":
                            {
                                // ⚠️ 必须先换算成**当前窗口**的坐标：事件里存的是录制时的像素，
                                // 而「像素 → 世界坐标」里含屏幕高，不换的话换个窗口尺寸就点到别的格子上了。
                                var fp = ReplayViewport.ToCurrent(ev.Sx, ev.Sy);
                                ReplayPointerAction.Fire(MainManager.Instance, ev.PointerId,
                                    ev.Interaction, fp.x, fp.y);
                            }
                            break;
                    }
                }

                switch (ev.Kind)
                {
                    case "grid":
                        {
                            var args = BuildArgs(ev);
                            // 探针：grid Down 是「把器械放下去」这个动作。
                            // 放置成功后游戏会 ResetHeldItem，所以对比注入前后的手持物就能
                            // 区分「选卡没生效」和「游戏拒绝了放置」。见 ReplayProbe。
                            if (IsProbeDown(ev))
                                ReplayProbe.Write("grid  Down  t=" + ev.Tick + " lane=" + ev.Lane + " col=" + ev.Column
                                    + " 能量=" + EnergyOf(bound) + " 手持=[" + ReplayProbe.Held(bound.GetEngine()) + "]"
                                    + " 格子上=[" + ReplayProbe.GridAt(bound.GetEngine(), ev.Lane, ev.Column) + "]");
                            mGrid.Invoke(bound, new object[] { ev.Lane, ev.Column, args });
                            if (IsProbeDown(ev))
                                ReplayProbe.Write("     → 注入后 手持=[" + ReplayProbe.Held(bound.GetEngine()) + "]");
                            injectedCount++;
                            break;
                        }
                    case "entity":
                        {
                            var ent = bound.GetEngine();
                            if (ent == null)
                            {
                                skippedNoEntity++;
                                return;
                            }

                            var entity = ResolveEntity(ent, ev, out var idWasWrong, out var failNote);
                            if (entity == null)
                            {
                                skippedNoEntity++;
                                NoteMiss(ev);
                                if (skippedNoEntity <= 5)
                                    Plugin.Log?.LogWarning("[Replay] 实体事件目标无法确定 id=" + ev.EntityId
                                        + (string.IsNullOrEmpty(ev.EntityDef) ? "" : " def=" + ev.EntityDef)
                                        + "（tick " + ev.Tick + "）—— " + failNote
                                        + "\n  当前关卡里存在的 id：" + SurveyEntityIds(ent));
                                return;
                            }

                            var ctrl = bound.GetEntityController(entity);
                            if (ctrl == null || ctrl.Pointer == IntPtr.Zero)
                            {
                                skippedNoController++;
                                NoteMiss(ev);
                                if (skippedNoController <= 5)
                                    Plugin.Log?.LogWarning("[Replay] 实体 id=" + ev.EntityId + " 没有控制器（tick " + ev.Tick + "）");
                                return;
                            }
                            // 探针：Down 事件打印「录制 vs 解析」的身份 + 那个格子的全部实体。
                            // 查「同格多器械挖错株」时靠它一眼看出锚点有没有选对。
                            if (IsProbeDown(ev))
                                ReplayProbe.Write(ReplayProbe.EntityLine(ev, ent, entity));
                            var args = BuildArgs(ev);
                            mEntity.Invoke(bound, new object[] { ctrl, args });
                            ApplyHeldFlags(ev);
                            if (IsProbeDown(ev))
                                ReplayProbe.Write("     → 注入后 手持=[" + ReplayProbe.Held(bound.GetEngine()) + "]");
                            injectedCount++;
                            break;
                        }
                    case "blueprint":
                        {
                            var args = BuildArgs(ev);
                            // 探针：blueprint Down 是「选卡」。注入后手持物应该变成那张卡。
                            if (IsProbeDown(ev))
                                ReplayProbe.Write("blueprint Down t=" + ev.Tick + " idx=" + ev.Index
                                    + " 能量=" + EnergyOf(bound) + " 手持=[" + ReplayProbe.Held(bound.GetEngine()) + "]");
                            mBlueprint.Invoke(bound, new object[] { ev.Index, args, ev.Conveyor });
                            ApplyHeldFlags(ev);
                            if (IsProbeDown(ev))
                                ReplayProbe.Write("     → 注入后 手持=[" + ReplayProbe.Held(bound.GetEngine()) + "]");
                            injectedCount++;
                            break;
                        }
                    case "lawn":
                        {
                            var args = BuildArgs(ev);
                            mLawn.Invoke(bound, new object[] { (LawnArea)ev.Area, args });
                            injectedCount++;
                            break;
                        }
                    case "bpkey":
                        mBlueprintKey.Invoke(bound, new object[] { ev.Index, ev.Conveyor, ev.Key });
                        ApplyHeldFlags(ev);
                        injectedCount++;
                        break;
                    case "tool":
                        {
                            var m = ToolMethod(ev.Tool);
                            if (m == null)
                            {
                                Note("未知工具事件：" + ev.Tool);
                                return;
                            }
                            m.Invoke(bound, null);
                            injectedCount++;
                            break;
                        }
                    // v1 遗留：只记了「点击之后手持物变成了什么」。补不出点击本身，
                    // 就按结果把状态摆正（set → 按工具点一次；reset → 直接清空）。
                    case "set":
                        {
                            var m = ToolMethodFromType(ev.Type);
                            if (m != null)
                            {
                                m.Invoke(bound, null);
                                injectedCount++;
                            }
                            break;
                        }
                    case "reset":
                        {
                            var engine = bound.GetEngine();
                            if (engine != null)
                                LogicLevelExt.ResetHeldItem(engine);
                            injectedCount++;
                            break;
                        }
                    default:
                        break;
                }
            }
            catch (Exception e)
            {
                invokeErrors++;
                Note("派发 " + ev + " 失败：" + Describe(e));
            }
            finally
            {
                injecting = false;
            }
        }

        private static MethodInfo ToolMethod(string tool)
        {
            if (string.IsNullOrEmpty(tool))
                return null;
            switch (tool.ToLowerInvariant())
            {
                case "pickaxe": return mClickPickaxe;
                case "starshard": return mClickStarshard;
                case "trigger": return mClickTrigger;
                default: return null;
            }
        }

        private static MethodInfo ToolMethodFromType(string type)
        {
            if (string.IsNullOrEmpty(type))
                return null;
            int i = type.IndexOf(':');
            return ToolMethod(i >= 0 ? type.Substring(i + 1) : type);
        }

        /// <summary>
        /// 拼一份 <c>PointerInteractionArgs</c>。
        ///
        /// 这是整个播放里最需要小心的一处：早期内测版把 5 个指针回调的参数换成了这个结构体，
        /// 里面既有 <c>pointerId</c> / <c>position</c> 这类平铺字段，也有原本那套
        /// <c>pointerCurrentRaycast</c> / <c>eventData</c>。谁被真正读到取决于回调体的写法，
        /// 所以这里**全部填满**，两份都能用。
        /// </summary>
        /// <summary>探针只关心 Down（MOUSE 下只有 Down 是有效动作，见 PointerHelper.IsInvalidReleaseAction）。</summary>
        private static bool IsProbeDown(ReplayEvent ev)
        {
            return ReplayProbe.Enabled && ev.Interaction == (int)MVZ2Logic.Inputs.PointerInteraction.Down;
        }

        private static string EnergyOf(LevelController ctrl)
        {
            try
            {
                var lvl = ctrl != null ? ctrl.GetEngine() : null;
                return lvl != null ? ((int)lvl.Energy).ToString() : "?";
            }
            catch (Exception) { return "?"; }
        }

        private static PointerInteractionArgs BuildArgs(ReplayEvent ev)
        {
            var args = new PointerInteractionArgs();
            // 录制时的像素 → 当前窗口的像素（尺寸没变时是恒等），见 ReplayViewport。
            var p = ReplayViewport.ToCurrent(ev.Sx, ev.Sy);
            try
            {
                args.pointerId = ev.PointerId;
                args.interaction = (MVZ2Logic.Inputs.PointerInteraction)ev.Interaction;
                args.button = (PointerEventData.InputButton)ev.Button;
                args.position = p;
                args.delta = Vector2.zero;

                var rr = MakeRaycast(ev, p);
                args.pointerCurrentRaycast = rr;
                args.eventData = MakeEventData(ev, rr, p);
            }
            catch (Exception e)
            {
                Note("拼指针参数失败：" + e.GetType().Name + ": " + e.Message);
            }
            // 存**原值**（录制坐标系）：换算留到读取时做，这样播放中途改窗口尺寸也不会偏。
            //
            // ⚠️ 只有**带坐标**的事件才写。bpkey / tool 这类事件没有位置（v1 起就没有），
            // 无条件写会把指针按到 (0,0)：魔人经卷/剑是在 Down 事件里读实时指针定落点的，
            // 于是它们的落点会被报到屏幕左下角。那种情况下保持上一次的值（轨迹会把它盖对）。
            if (ev.HasPos)
                pointerScreen = new Vector2(ev.Sx, ev.Sy);
            return args;
        }

        /// <summary>
        /// 把「拿起蓝图那一刻」的两个标记按回手持物上。
        ///
        /// <para>不做的话，<c>instantTrigger</c> 会按**当前**的「交换触发」选项重算一遍 ——
        /// 而那个选项是玩家选项、不在关卡关键帧里，录/放两边设置不同就会让器械行为反过来。
        /// 详见 <see cref="ReplayHeldFlags"/>。</para>
        /// </summary>
        private static void ApplyHeldFlags(ReplayEvent ev)
        {
            if (!ev.HasHeldFlags)
                return;
            var lvl = bound != null ? bound.GetEngine() : null;
            if (lvl == null)
                return;
            int changed = ReplayHeldFlags.Apply(lvl, ev.InstantTrigger, ev.InstantEvoke);
            if (changed > 0)
                heldFlagFixes++;
        }

        private static RaycastResult MakeRaycast(ReplayEvent ev, Vector2 screen)
        {
            var rr = new RaycastResult();
            var world = new Vector3(ev.Wx, ev.Wy, ev.Wz);
            try { rr.worldPosition = world; } catch (Exception) { }
            try { rr.worldNormal = new Vector3(0f, 1f, 0f); } catch (Exception) { }
            try { rr.screenPosition = new Vector3(screen.x, screen.y, 0f); } catch (Exception) { }
            return rr;
        }

        private static PointerEventData MakeEventData(ReplayEvent ev, RaycastResult rr, Vector2 screen)
        {
            try
            {
                var es = EventSystem.current;
                var ped = new PointerEventData(es);
                ped.pointerId = ev.PointerId;
                ped.position = screen;
                ped.pressPosition = screen;
                ped.delta = Vector2.zero;
                ped.button = (PointerEventData.InputButton)ev.Button;
                ped.pointerCurrentRaycast = rr;
                return ped;
            }
            catch (Exception)
            {
                return null;
            }
        }
        #endregion

        #region Global.Input 指针位置改写
        /// <summary>
        /// 回放期间把「当前指针位置」报告成回放到的那一刻的位置。
        ///
        /// 少数手持物行为（剑的挥砍、以及 <c>CombatHeldItemBehaviour</c> 里那几处）
        /// 不看回调参数、直接问 <c>Global.Input.GetPointerScreenPosition()</c>，
        /// 那是**实时输入** —— 不接管的话玩家鼠标停在哪就会影响回放表现。
        /// </summary>
        public static bool TryOverridePointerPosition(int type, int button, out Vector2 pos)
        {
            pos = Vector2.zero;
            if (phase != Phase.Playing)
                return false;

            // ⚠️ 这里**曾经**有「注入期间不接管」（`|| injecting` 就返回 false）的短路，
            // 那是个 bug：魔人经卷（`CombatHeldItemBehaviour`）正是在 **Down 事件里**
            // 读实时指针来定落点（`SetDragStartPosition(Global.Input.GetPointerScreenPosition(...))`），
            // 于是回放里落点跟着**旁观者的鼠标**走。
            //
            // 现在的规则：
            //   本 tick 有轨迹（trackCovered）→ 轨迹的位置（打砖块挡板 / 重装兵器朝向每帧读的就是它）；
            //   其余时刻 → **不接管**，直接去读事件的原始位置 PointerScreen。
            //
            // 为什么不是「没有轨迹就返回 false 让游戏读实时输入」：PointerScreen 是**录到的**值，
            // 一定比旁观者的鼠标更接近真实。而轨迹刚开始的那几个 tick 两者本来就一致。
            if (trackCovered)
                pos = ReplayViewport.ToCurrent(ReplayPointerTrajectory.CurrentScreen);
            else
                pos = PointerScreen;   // 已换算到当前窗口

            pointerQueries++;
            return true;
        }
        #endregion

        #region 工具
        private static int ToTick(long tick)
        {
            if (tick < 0) return 0;
            if (tick > int.MaxValue) return int.MaxValue;
            return (int)tick;
        }

        private static NamespaceID ParseId(string text)
        {
            try
            {
                if (string.IsNullOrEmpty(text))
                    return null;
                return NamespaceID.Parse(text, "mvz2");
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Note(string message)
        {
            if (errors.Count < MaxReports)
                errors.Add(message);
            Plugin.Log?.LogWarning("[Replay] " + message);
        }

        private static string Describe(Exception e)
        {
            var sb = new StringBuilder();
            var cur = e;
            int depth = 0;
            while (cur != null && depth < 5)
            {
                if (depth > 0)
                    sb.Append("  <-  ");
                sb.Append(cur.GetType().Name).Append(": ").Append(cur.Message);
                cur = cur.InnerException;
                depth++;
            }
            return sb.ToString();
        }

        private static void EnsureMethods()
        {
            if (methodsReady)
                return;
            methodsReady = true;
            mGrid = AccessTools.Method(typeof(LevelController), "UI_OnGridPointerInteractionCallback",
                new[] { typeof(int), typeof(int), typeof(PointerInteractionArgs) });
            mEntity = AccessTools.Method(typeof(LevelController), "UI_OnEntityPointerInteractionCallback",
                new[] { typeof(EntityController), typeof(PointerInteractionArgs) });
            mBlueprint = AccessTools.Method(typeof(LevelController), "UI_OnBlueprintPointerInteractionCallback",
                new[] { typeof(int), typeof(PointerInteractionArgs), typeof(bool) });
            mLawn = AccessTools.Method(typeof(LevelController), "UI_OnRaycastReceiverPointerInteractionCallback",
                new[] { typeof(LawnArea), typeof(PointerInteractionArgs) });
            mBlueprintKey = AccessTools.Method(typeof(LevelController), "OnBlueprintKey",
                new[] { typeof(int), typeof(bool), typeof(int) });
            mClickPickaxe = AccessTools.Method(typeof(LevelController), "ClickPickaxe");
            mClickStarshard = AccessTools.Method(typeof(LevelController), "ClickStarshard");
            mClickTrigger = AccessTools.Method(typeof(LevelController), "ClickTrigger");
            mDialogResume = AccessTools.Method(typeof(LevelController), "UI_OnLevelLoadedDialogOptionClickedCallback");

            // FromBson<T>：interop 把它生成成普通泛型方法，直接用反射补上类型实参。
            try
            {
                var open = AccessTools.Method(typeof(SerializeHelper), "FromBson");
                if (open != null && open.IsGenericMethodDefinition)
                    mFromBson = open.MakeGenericMethod(typeof(SerializableLevelController));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Replay] 准备 FromBson<SerializableLevelController> 失败：" + Describe(e));
            }

            var missing = new List<string>();
            if (mGrid == null) missing.Add("UI_OnGridPointerInteractionCallback");
            if (mEntity == null) missing.Add("UI_OnEntityPointerInteractionCallback");
            if (mBlueprint == null) missing.Add("UI_OnBlueprintPointerInteractionCallback");
            if (mLawn == null) missing.Add("UI_OnRaycastReceiverPointerInteractionCallback");
            if (mBlueprintKey == null) missing.Add("OnBlueprintKey");
            if (mClickPickaxe == null) missing.Add("ClickPickaxe");
            if (mClickStarshard == null) missing.Add("ClickStarshard");
            if (mClickTrigger == null) missing.Add("ClickTrigger");
            if (mDialogResume == null) missing.Add("UI_OnLevelLoadedDialogOptionClickedCallback");
            if (mFromBson == null) missing.Add("SerializeHelper.FromBson<T>");
            if (missing.Count > 0)
                Plugin.Log?.LogError("[Replay] 播放器找不到这些方法：" + string.Join(", ", missing.ToArray()));
        }
        #endregion
    }
}
