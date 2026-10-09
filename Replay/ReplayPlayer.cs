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
            LoadGame = 3,
            Playing = 4,
            Ended = 5,
            Failed = 6,
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
        /// 当前「游戏认为的指针位置」（已换算到当前窗口，与 <c>Input.mousePosition</c> 同制）。
        ///
        /// <para>⚠️ 这是**事件原值**，会被 <c>Global.Input</c> 的那几个查询读到，
        /// 而其中 <c>MinecartRideable</c> / <c>Snipenser</c> / <c>BreakoutBoard</c>
        /// 会拿它去改仿真 —— **绝不能**把它换成假鼠标的平滑值。
        /// 假鼠标只允许在 <c>LevelController.UpdateHeldItemPosition</c> 那一层临时替换
        /// （见 <see cref="FakePointer"/> 的类注释）。</para>
        /// </summary>
        public static Vector2 PointerScreen => ReplayViewport.ToCurrent(pointerScreen);

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
        private static MethodInfo mGotoLevelSceneAsync;
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
                case Phase.LoadGame:
                    return "正在恢复关键帧 " + name;
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
            for (int i = 0; i < errors.Count; i++)
                sb.Append("  ! ").Append(errors[i]).Append('\n');
            if (pointerQueries > 0)
                sb.Append("  Global.Input 指针位置查询被改写 ").Append(pointerQueries).Append(" 次\n");
            if (ReplayPointerAction.Enabled)
                sb.Append("  回放的指针动作（POST_POINTER_ACTION）").Append(ReplayPointerAction.Fired).Append(" 次\n");
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
            if (mGotoLevelSceneAsync == null)
                return "[replay] 找不到 MainSceneController.GotoLevelSceneAsync，无法进入关卡场景。";

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
            errors.Clear();
            failReason = null;
            bound = null;
            injecting = false;
            pointerScreen = Vector2.zero;
            pointerQueries = 0;
            waitFrames = 0;
            tickLimit = GetTickLimit();
            phaseStart = Time.realtimeSinceStartup;
            phase = Phase.GotoScene;
            ReplayHud.Reset();
            ReplayProbe.Reset();
            ReplayPointerAction.Reset();

            // 存档护栏：回放里挣的/花的钱只进影子，统计与解锁等一律不写。
            // 补丁**故意延迟到这里才装**（游戏早就初始化完了，反射 MVZ2Logic 的类型是安全的）。
            ReplayHooks.EnsureSaveGuardHooks();
            ReplaySaveGuard.Begin();
            // 把「录制时的窗口尺寸」记下来：事件里的 sx/sy 是像素，
            // 换个窗口尺寸再播就指向别的格子了（见 ReplayViewport）。
            ReplayViewport.Capture(f);

            FakePointer.Begin(f);
            ReplayEndPanel.Close();
            wantsReplayPage = false;
            // 「回放页关掉后去哪」属于上一次播放的收尾状态，新一场开播时必须清掉。
            ReplayPage.SetReturnTo(null);

            Plugin.Log?.LogInfo("[Replay] 开始播放 " + f.Path + " —— " + f.Describe());
            var sb = new StringBuilder();
            sb.Append("[replay] 开始播放：").Append(System.IO.Path.GetFileName(f.Path)).Append('\n');
            sb.Append("  ").Append(f.Describe()).Append('\n');
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
            ReplaySaveGuard.End();
            bound = null;
            cursor = 0;
            lastSummary = BuildSummary();
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
            ReplaySaveGuard.End();
            bound = null;
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
            if (phase != Phase.Playing && phase != Phase.LoadGame)
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
                DrivePhase(scene);
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
                            + "，等它被换掉再 LoadGame）");
                        try
                        {
                            mGotoLevelSceneAsync.Invoke(scene, null);
                        }
                        catch (Exception e)
                        {
                            Fail("调用 GotoLevelSceneAsync 失败：" + Describe(e));
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
                            Fail("等待关卡场景就绪超时");
                            return;
                        }
                        var ctrl = main.LevelManager != null ? main.LevelManager.GetLevelController() : null;
                        if (ctrl == null || ctrl.Pointer == IntPtr.Zero)
                            return;
                        // 必须等**新**控制器。只见「有控制器」是不够的 —— 场景切换期间旧控制器
                        // 一直在场，那时 LoadGame 会撞碰撞系统的键（详见 GotoScene 里的注释）。
                        if (oldControllerPointer != IntPtr.Zero && ctrl.Pointer == oldControllerPointer)
                            return;
                        // 场景刚 Awake，再等几帧让 Awake_* 全部跑完、
                        // 并让 GotoLevelSceneAsync 把旧场景卸掉，与游戏自己的
                        // GotoLevelCoroutine → InitLevel 之间那一帧边界一致。
                        waitFrames++;
                        if (waitFrames < 3)
                            return;
                        Plugin.Log?.LogInfo("[Replay] 关卡场景已就绪（新控制器 = 0x" + ctrl.Pointer.ToInt64().ToString("X") + "）");
                        phase = Phase.LoadGame;
                        break;
                    }

                case Phase.LoadGame:
                    {
                        if (!LoadKeyframe(main))
                            return;
                        phase = Phase.Playing;
                        phaseStart = Time.realtimeSinceStartup;
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
        /// <item>我们播放关键帧之后（<see cref="LoadKeyframe"/>）；</item>
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

        private static bool LoadKeyframe(MainManager main)
        {
            var ctrl = main.LevelManager != null ? main.LevelManager.GetLevelController() : null;
            if (ctrl == null || ctrl.Pointer == IntPtr.Zero)
            {
                Fail("关卡控制器不存在");
                return false;
            }

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
                seri = (SerializableLevelController)mFromBson.Invoke(null, new object[] { file.KeyframeBson });
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

            var area = ParseId(file.Area);
            var stage = ParseId(file.Stage);
            if (area == null || stage == null)
            {
                Fail("关卡 id 不合法：" + file.Area + " / " + file.Stage);
                return false;
            }

            bool ok;
            try
            {
                ctrl.SetActive(true);
                ctrl.SetStartStage(area, stage);
                ok = ctrl.LoadGame(seri, main.Game, area, stage);
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

                CheckFingerprints(lvl, tick);
                ReplayHud.SetProgress(tick, file.LastTick);

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
            try
            {
                return ReplayConfig.SelfTestPlayTickLimit != null
                    ? ReplayConfig.SelfTestPlayTickLimit.Value : 0;
            }
            catch (Exception)
            {
                return 0;
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

            var actual = ReplayRecorder.ComputeFingerprint(lvl, tick, out _);
            if (actual == null)
                return;
            fpCompared++;

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
            return sb.Length == 0 ? null : sb.ToString().TrimEnd();
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
        private static void Dispatch(ReplayEvent ev)
        {
            injecting = true;
            try
            {
                if (injectedByKind.TryGetValue(ev.Kind, out var n))
                    injectedByKind[ev.Kind] = n + 1;
                else
                    injectedByKind[ev.Kind] = 1;

                // 先给**输入层的全局回调**发一遍指针动作，再走路由事件。
                // 为什么两条都要发：它们彼此不重叠（详见 ReplayPointerAction）——
                // 「右键取消手持物」「碎石墙/打砖块」只挂在前者上。
                if (ReplayPointerAction.Enabled)
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
        /// 这是整个播放里最需要小心的一处：test-9 把 5 个指针回调的参数换成了这个结构体，
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
            // 注意读的是 PointerScreen（会换算到当前窗口），不是裸字段：
            // 一次播放里事件之间可能隔着几千帧，玩家中途改窗口尺寸是常事。
            pos = PointerScreen;
            if (phase != Phase.Playing || injecting)
                return false;
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
            mGotoLevelSceneAsync = AccessTools.Method(typeof(MainSceneController), "GotoLevelSceneAsync");

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
            if (mGotoLevelSceneAsync == null) missing.Add("MainSceneController.GotoLevelSceneAsync");
            if (mFromBson == null) missing.Add("SerializeHelper.FromBson<T>");
            if (missing.Count > 0)
                Plugin.Log?.LogError("[Replay] 播放器找不到这些方法：" + string.Join(", ", missing.ToArray()));
        }
        #endregion
    }
}
