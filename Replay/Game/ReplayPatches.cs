using System;
using DSHCore.Ui;
using MVZ2.Level;
using MVZ2.Level.Entities;
using MVZ2.Level.HeldItems;
using MVZ2.Level.Inputing;
using MVZ2.Managers;
using MVZ2.Scenes;
using MVZ2Logic.HeldItems;
using MVZ2Logic.Level;
using PVZEngine.Entities;
using PVZEngine.Level;
using UnityEngine;

// DSHCore 的 UiLog 是 internal，跨程序集用不了 —— 别名到本 mod 自己的那份。
using UiLog = Replay.ReplayLog;

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
    /// 这 5 个点就是玩家输入影响仿真的全部入口（0.7.0 枚举，早期内测版 / 早期内测版 / 0.7.0 复核签名均未变）。
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

        /// <summary>
        /// <c>LevelManager.InitLevel</c> 前缀：回放进关卡时接管。
        ///
        /// <para>回放不再自己切场景，而是发游戏自己的 <c>/gotolevel</c>（那条链路在 0.7.0 上
        /// 才不会崩，详见 <see cref="ReplaySceneEntry"/> 与 DESIGN.md）。<c>/gotolevel</c> 的协程在
        /// 场景加载完之后必然会走到这里，而**这一刻**的控制器正好是
        /// 「刚 Awake、还没被 Init 过」——唯一能让 <c>LoadGame</c> 成功的状态。</para>
        ///
        /// <para>返回 false 跳过原方法：避免它 <c>SaveToFile()</c>（回放不许写存档）
        /// 与从 <c>.lvl</c> 续关。不是回放发起的 <c>InitLevel</c> 原样放行。</para>
        /// </summary>
        public static bool LevelManagerInitLevelPrefix(MVZ2.Level.LevelManager __instance,
            PVZEngine.NamespaceID areaID, PVZEngine.NamespaceID stageID,
            MVZ2Logic.Level.LevelExitTarget exitTarget)
        {
            Plugin.Log?.LogInfo("[Replay] InitLevel 前缀（接管状态=" + ReplaySceneEntry.State + "）");
            return ReplaySceneEntry.OnInitLevel(__instance, areaID, stageID, exitTarget);
        }

        /// <summary>主场景每帧（自测 / 播放器的驱动点）。</summary>
        public static void SceneUpdatePostfix(MVZ2.Scenes.MainSceneController __instance)
        {
            // 分界心跳（排查用；ConveyorGuardLog 关着时什么都不做）。
            // 用途：崩溃是原生访问冲突、托管侧什么都看不到，只能靠「最后一条标记」夹逼。
            var bt = ReplayPlayer.CurrentTick;
            ReplayConveyorGuard.Beat(bt, "frame-in");
#if DIAGNOSTICS
            ReplaySelfTest.Tick(__instance);
#endif
            ReplayConveyorGuard.Beat(bt, "frame-selftest");
#if DIAGNOSTICS
            ReplayGuiTest.Tick(__instance);
#endif
            ReplayConveyorGuard.Beat(bt, "frame-guitest");
            ReplayPlayer.Tick(__instance);
            ReplayConveyorGuard.Beat(bt, "frame-player");
            // 减速按钮的看门狗：关卡被重建（场景重载）后按钮会随旧控制器一起消失，
            // 需要在下一帧补一个（与 LevelHud 的做法一致）。
            ReplaySlowDown.Tick();
            ReplayConveyorGuard.Beat(bt, "frame-out");
            // 回放页（插件页子页面）的心跳由 DSHCore.Ui.AddonPages 自己驱动，
            // 这里不再需要调 —— 那是「插件页子页面」这套行为的一部分。
        }
        #endregion

        #region 回放接管的闸门
        //
        // 背景（见 DESIGN.md §4.4.17 与 ReplaySceneEntry 的长注释）：
        // 回放靠游戏自己的指令进关卡（关卡外 /gotolevel、关卡内 /load），
        // 然后在 ReplaySceneEntry 的轮询里把关键帧 LoadGame 到新控制器上。
        //
        // 但 /load 那条链里 LevelController.ReloadLevel() 的
        //     await LevelManager.GotoLevelSceneAsync();
        // 续体**是会恢复的，只是很晚**：等我们轮询载完关键帧之后（实测同一帧内），
        // 它直接调进 LevelController.InitLevel + StartLevelIntro，把关卡整个重建
        // （currentEntityID 13 → 7、实体变成预览敌人），关键帧白载。
        //
        // 而且那一跳**没有**经过 LevelManager.InitLevel 的托管桩（实测它的前缀一次都没打，
        // 只有启动时那一次 #1），所以只拦 LevelManager.InitLevel 根本拦不住。
        // 下面的两个前缀直接拦在「真正重建关卡」的方法上：谁调、从哪跳进来都不怕。

        /// <summary>
        /// <c>LevelController.InitLevel</c> 的前缀 —— <b>真正重建关卡的那一步</b>。
        ///
        /// <para>接管中（<see cref="SceneEntryState.Waiting"/>）时就在这里把关键帧载进去
        /// （此刻控制器刚 Awake、还没被 Init 过碰撞系统，是 <c>LoadGame</c> 唯一能成功的时机），
        /// 然后返回 false 跳过原函数体；已接管（<see cref="SceneEntryState.Done"/>）时直接跳过，
        /// 防止迟到的调用把状态冲掉。</para>
        /// </summary>
        public static bool LevelControllerInitLevelPrefix(LevelController __instance)
        {
            return ReplaySceneEntry.OnControllerInitLevel(__instance);
        }

        /// <summary>
        /// <c>LevelController.StartLevelIntro</c> 的前缀：回放接管过的控制器上跳过它
        /// （那是「新开一局」的流程：设起始镜头 + 开场对话 + <c>level.BeginLevel()</c>）。
        /// </summary>
        public static bool LevelControllerStartLevelIntroPrefix(LevelController __instance)
        {
            return ReplaySceneEntry.OnStartLevelIntro(__instance);
        }
        #endregion

        #region 假鼠标（纯视觉通道）

        /// <summary>
        /// <c>LevelUI.SetHeldItemPosition(Vector2)</c> 前缀：把「手持物模型的世界坐标」
        /// 换成**假鼠标**所在位置算出来的世界坐标。
        ///
        /// <para><b>为什么挂这里，而不是继续换指针值：</b>手持物模型的位置是
        /// <c>LevelController.UpdateHeldItemPosition</c> 里
        /// <c>levelCamera.Camera.ScreenToWorldPoint(Main.InputManager.GetPointerScreenPosition())</c>
        /// 算出来的，而那条链最终落到 <c>InputHelper.GetPointerPosition</c> ——
        /// **一个只有一行的静态方法，IL2CPP 完全可以把它内联进调用方**。
        /// 内联后我们的前缀在那个调用点上根本不会执行，于是「手持蓝图时指针瞬移」
        /// （其它消费者走的是别的调用点，所以看起来只有手持物不跟随）。</para>
        ///
        /// <para>挂 <c>SetHeldItemPosition</c> 就没有这个问题：它是**纯表现**的终点，
        /// 游戏算完世界坐标之后我们直接换掉，与谁算的、有没有内联都无关。</para>
        ///
        /// <para><b>不碰仿真</b>：世界坐标只喂给模型变换。指针值本身（<c>Global.Input</c> /
        /// 矿车 / 喷瓜 / 打砖块读的那个）仍然是事件原值，指纹不可能受影响。</para>
        /// </summary>
        public static void HeldItemWorldPrefix(ref Vector2 worldPos)
        {
            // 游戏用 (-1000,-1000) 表示「把模型藏起来」（触屏且没按住时）——
            // 那种情况必须原样放过，否则手上没东西也会冒出一个模型。
            if (worldPos.x <= -999f && worldPos.y <= -999f)
                return;
            if (!FakePointer.Active)
                return;

            try
            {
                var ctrl = LevelControllerInstance();
                if (ctrl == null)
                    return;
                var cam = ctrl.GetCamera();
                if (cam == null || cam.Pointer == IntPtr.Zero)
                    return;
                var p = FakePointer.ScreenPos;
                // 与游戏自己的算法完全同一条（它也是 ScreenToWorldPoint(Vector2)，z = 0）。
                var w = cam.ScreenToWorldPoint(new Vector3(p.x, p.y, 0f));
                worldPos = new Vector2(w.x, w.y);
                FakePointer.NoteHeldSync();
            }
            catch (Exception) { }
        }

        private static MVZ2.Level.LevelController LevelControllerInstance()
        {
            var main = MainManager.Instance;
            var lm = main != null ? main.LevelManager : null;
            var ctrl = lm != null ? lm.GetLevelController() : null;
            return ctrl != null && ctrl.Pointer != IntPtr.Zero ? ctrl : null;
        }
        #endregion

        #region 回放模式的菜单（重新播放 / 结束播放）
        /// <summary>回放期间菜单里「重新开始」的文案。</summary>
        private const string LabelReplay = "重新播放";
        /// <summary>回放期间菜单里「返回地图」的文案。</summary>
        private const string LabelEndPlay = "结束播放";

        /// <summary>
        /// <c>OptionsDialogController.UpdateRestartButton</c> 后缀：回放期间把「重新开始」改成
        /// 「重新播放」。
        ///
        /// <para>为什么挂后缀而不是自己算完再写：原版按钮文案由
        /// <c>UpdateMainPageWidgets</c> 统一刷新（切页 / 改语言 / 重复打开都会跑），
        /// 我们在它写完之后**立刻覆盖**，就不会出现「先对后错」。</para>
        /// </summary>
        public static void OptionsRestartLabelPostfix(MVZ2.Options.OptionsDialogController __instance)
        {
            SetOptionButtonText(__instance, MVZ2.UI.OptionsDialogMainPage.TextButtonType.Restart,
                UiLocale.Get(LabelReplay));
        }

        /// <summary><c>OptionsDialogController.UpdateLeaveLevelButton</c> 后缀：回放期间改成「结束播放」。</summary>
        public static void OptionsLeaveLabelPostfix(MVZ2.Options.OptionsDialogController __instance)
        {
            SetOptionButtonText(__instance, MVZ2.UI.OptionsDialogMainPage.TextButtonType.LeaveLevel,
                UiLocale.Get(LabelEndPlay));
        }

        /// <summary>
        /// 菜单主页面按钮点击的前缀：回放期间「重新播放 / 结束播放」由我们接管。
        ///
        /// <para>原版这两个按钮会分别去 <c>ShowRestartConfirmDialog</c>（走
        /// <c>RestartLevel</c> → 清关卡存档 + 重载）与 <c>ExitLevel</c>（落点 = 地图/主菜单）。
        /// 回放要的是「重播这条回放」与「结束播放（弹结束面板）」，所以整段拦下。</para>
        ///
        /// <para>两个回调都先 <see cref="CloseLevelOptionsMenu"/> —— 否则菜单会一直挂在屏幕上
        /// （结束面板只是叠在它上面，玩家能看到底下那半透明的菜单）。</para>
        /// </summary>
        public static bool OptionsMainButtonPrefix(MVZ2.UI.OptionsDialogMainPage.ButtonType type)
        {
            if (!ReplayPlayer.IsActive)
                return true;

            if (type == MVZ2.UI.OptionsDialogMainPage.ButtonType.Restart)
            {
                ConfirmInLevel(LabelReplay, "确认要重新播放吗？\n当前进度会从头开始。", () =>
                {
                    CloseLevelOptionsMenu();
                    ReplayPlayer.RestartCurrent();
                });
                return false;
            }
            if (type == MVZ2.UI.OptionsDialogMainPage.ButtonType.LeaveLevel)
            {
                ConfirmInLevel(LabelEndPlay, "确认要结束播放吗？\n结束后会弹出回放结束面板，可以导出、重播或退出。", () =>
                {
                    CloseLevelOptionsMenu();
                    ReplayPlayer.FinishFromMenu();
                });
                return false;
            }
            return true;
        }

        /// <summary>
        /// 把回放期间打开的那个关卡菜单面板收起来。
        ///
        /// <para><b>为什么必须自己 <c>ResumeGame</c>：</b>真正把菜单框体收起来的是
        /// <c>LevelController.ResumeGame</c> 里的 <c>ui.SetOptionsDialogActive(false)</c>；
        /// 光调 <c>optionsDialogController.Close()</c> 只清 context、不收框体。
        /// 而 <c>ResumeGame</c> 里有一条 <c>if (Main.Scene.HasDialog()) return false;</c> ——
        /// 我们的确认框是**先收起来再回调**的（所以此刻还过得去），但也不能依赖
        /// <c>Close()</c> 触发的那条 <c>ResumeGameDelayed</c> 协程：它要下一帧才跑，
        /// 那时结束面板已经弹出来了，必然被这条挡住，菜单就留在屏幕上了。</para>
        /// </summary>
        public static void CloseLevelOptionsMenu()
        {
            try
            {
                var main = MainManager.Instance;
                var lm = main != null ? main.LevelManager : null;
                var ctrl = lm != null ? lm.GetLevelController() : null;
                if (ctrl == null || ctrl.Pointer == IntPtr.Zero)
                    return;

                var opts = ctrl.optionsDialogController;
                if (opts == null || opts.Pointer == IntPtr.Zero)
                    return;

                bool wasOpen = opts.IsOpen();

                // ① 暂停中就走正路：解暂停 + 收框体 + 恢复音乐。
                if (ctrl.IsGamePaused())
                    ctrl.ResumeGame(0);

                // ② 兜底：游戏没在跑时（通关/失败之后打开菜单）上面那条会 early return，
                //    那就自己把状态和节点收掉（下次 Open() 时 ShowOptionsDialog 会再 SetActive(true)）。
                if (wasOpen && opts.IsOpen())
                    opts.Close();
                try
                {
                    if (opts.gameObject.activeSelf)
                        opts.gameObject.SetActive(false);
                }
                catch (Exception) { }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Replay] 收起菜单面板失败：" + Fmt(e));
            }
        }

        /// <summary>
        /// 用游戏自己的对话框弹一个「是 / 否」确认，确认后执行 <paramref name="onYes"/>。
        /// 与 <c>OptionsDialogController.ButtonType.LeaveLevel</c> 原来的做法完全一致
        /// （原版就是用 <c>Main.Scene.ShowDialogSelectAsync</c> 问一句再退出）。
        /// </summary>
        private static void ConfirmInLevel(string title, string desc, Action onYes)
        {
            try
            {
                var main = MainManager.Instance;
                if (main == null || main.Scene == null)
                    return;
                var cb = new Action<bool>(yes =>
                {
                    if (!yes)
                        return;
                    try { onYes(); }
                    catch (Exception e) { Plugin.Log?.LogError("[Replay] 菜单确认后执行失败：" + Fmt(e)); }
                });
                // ⚠️ 必须钉住：ShowDialogSelect 的回调最终落在 il2cpp 的委托字段上，
                // 只在调用点建一个局部委托的话随时可能被 GC 掉（症状：点「是」毫无反应，且不报错）。
                pinnedConfirm = cb;
                // 走游戏原生的对话框，它不会替我们取词。
                main.Scene.ShowDialogSelect(UiLocale.Get(title), UiLocale.Get(desc), cb);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Replay] 弹确认框失败：" + Fmt(e));
            }
        }

        private static Action<bool> pinnedConfirm;

        /// <summary>
        /// 自测用：相当于玩家在「是 / 否」确认框里点了某个按钮。
        /// 走的是**和真实点击完全相同**的那个回调（<see cref="pinnedConfirm"/>）。
        /// </summary>
        public static bool SelfTestConfirm(bool yes)
        {
            var cb = pinnedConfirm;
            if (cb == null)
                return false;
            cb(yes);
            return true;
        }

        /// <summary>把异常摊平成一行（本文件局部用，不引 ReplayPlayer 的私有同名方法）。</summary>
        private static string Fmt(Exception e)
        {
            if (e == null)
                return "?";
            var s = e.GetType().Name + "：" + e.Message;
            if (e.InnerException != null)
                s += "  <- " + e.InnerException.GetType().Name + "：" + e.InnerException.Message;
            return s;
        }

        /// <summary>
        /// 回放中拦住「暂停 / 选项」热键：结束面板开着的时候不要让它被别的对话框盖住。
        ///
        /// <para>⛔ 判据只能是「结束面板开着没有」，**不能**再叠一个 <c>IsActive</c> ——
        /// 面板是在 <c>FinishPlayback</c> 里弹的，那时 <c>phase</c> 已经是 <c>Ended</c>、
        /// <c>IsActive</c> 为 false，写成 <c>!IsActive || !IsOpen</c> 的话这里永远返回 true
        /// （= 不拦），玩家可以按 Esc 把设置菜单叠到结束面板上。</para>
        /// </summary>
        public static bool OptionsKeyBlockPrefix()
        {
            try { return !ReplayEndPanel.IsOpen; }
            catch (Exception) { return true; }
        }

        /// <summary>
        /// 回放中拦住原版的**通关**流程。
        ///
        /// <para>⛔ 不拦会出真事：原版会 <c>RemoveLevelState()</c>（**删掉该关卡的存档文件**）、
        /// 解锁关卡、写难度记录，再走通关对话 + 退场动画。回放只是在重演一局，
        /// 拿它去改玩家的存档是错的。</para>
        /// </summary>
        public static bool EngineClearPrefix()
        {
            if (!ReplayPlayer.IsActive)
                return true;
            ReplayPlayer.NotifyLevelCleared();
            return false;
        }

        /// <summary>回放中拦住原版的**失败**对话框（否则会和我们的结束面板并排出现）。</summary>
        public static bool GameOverDialogPrefix()
        {
            if (!ReplayPlayer.IsActive)
                return true;
            ReplayPlayer.NotifyGameOver();
            return false;
        }

        /// <summary>
        /// <c>MainSceneController.GotoMapOrMainmenu</c> 前缀：本次要回**回放页**时改去回放页。
        ///
        /// <para>这是「结束播放」的落点开关。<c>ExitLevel()</c> 与 <c>GotoMapOrMainmenu</c>
        /// 是游戏里唯一的「打完一局去哪」决策点，改这里就不用在退出流程里插一脚。</para>
        ///
        /// <para><b>为什么要在这里就把面板打开（而不是挂个单等下一帧）：</b>
        /// 回放页的壳是「插件页 Index」的克隆体，而 <c>DisplayAddons</c> 会把插件页自己的
        /// 索引（资源包 / 语言）显示出来 —— 面板晚一帧开就是**一帧插件页**，
        /// 玩家看到的是「先闪一下插件页，再变成回放页」。同一帧里
        /// <c>DisplayAddons</c> → <c>Open</c>（内部会把索引藏起来）就没有这一帧。</para>
        ///
        /// <para><b>黑色屏幕的那个 bug：</b>以前传的 <c>onReturn</c> 是 <c>null</c>，
        /// 于是插件页的「返回」按钮 <c>OnReturnClick?.Invoke()</c> 什么都不做、只把页面
        /// <c>Hide()</c> 掉 —— 页面全灭、没有任何东西顶上来，就是黑屏。
        /// 现在把「返回」接到主菜单（<c>DisplayMainmenuToBasement</c>），
        /// 并且让**回放页自己的「返回」**也直接走这条路（见 <c>ReplayPage.SetReturnTo</c>）：
        /// 玩家是从关卡直接落到回放页的，从没经过插件页的索引，退出时自然该回主菜单。</para>
        /// </summary>
        public static bool GotoMapOrMainmenuPrefix(MainSceneController __instance)
        {
            if (!ReplayPlayer.WantsReplayPage)
                return true;
            ReplayPlayer.ConsumeReplayPage();
            try
            {
                if (__instance == null)
                    return true;

                var backToMain = new Action(() =>
                {
                    try { __instance.DisplayMainmenuToBasement(); }
                    catch (Exception e) { Plugin.Log?.LogError("[Replay] 回主菜单失败：" + Fmt(e)); }
                });

                // 背景就用插件页（回放页的壳本来就是从它那儿克隆的，版式一致）。
                __instance.DisplayAddons(backToMain);

                // 关掉回放页时不再亮出插件页的索引，而是直接回主菜单。
                ReplayPage.SetReturnTo(backToMain);

                // 同一帧就把面板亮出来 —— 玩家看不到插件页那一瞬。
                ReplayPage.RequestOpen();
                ReplayPage.PumpNow();

                Plugin.Log?.LogInfo("[Replay] 结束播放的落点：回放页（同帧打开，返回时回主菜单）");
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Replay] 切到回放页失败，退回原版行为：" + Fmt(e));
                ReplayPage.SetReturnTo(null);
                return true;
            }
        }

        /// <summary>
        /// 给设置界面主页上的某个文本按钮改字。
        ///
        /// <para>走的是原版公开的 <c>OptionsDialogMainPage.SetButtonText</c>（内部按枚举查表），
        /// 所以拿到 <c>OptionsDialogController</c> 之后要往下走两级：
        /// <c>controller.ui</c> → <c>OptionsDialog.Main</c>。</para>
        ///
        /// <para>⛔ <b>必须用传进来的那个控制器</b>，不能自己去场景里找：主菜单那套里也有一个
        /// <c>OptionsDialogController</c>，<c>UiFinder.FindOne</c> 会随机命中它 —— 那个的
        /// <c>ui.Main</c> 挂在未激活的节点下，写进去的文案没人看得见（症状：菜单里还是
        /// 「重新开始 / 返回主界面」，且毫无报错）。</para>
        /// </summary>
        private static void SetOptionButtonText(MVZ2.Options.OptionsDialogController ctrl,
            MVZ2.UI.OptionsDialogMainPage.TextButtonType type, string text)
        {
            if (!ReplayPlayer.IsActive)
                return;
            try
            {
                var page = FindOptionsMainPage(ctrl);
                if (page == null)
                {
                    UiLog.WarnOnce("rp-optpage", "找不到设置界面主页，回放菜单文案改不了。");
                    return;
                }
                page.SetButtonText(type, text);
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("rp-opttext", "改回放菜单文案失败：" + e.GetType().Name + "：" + e.Message);
            }
        }

        private static MVZ2.UI.OptionsDialogMainPage FindOptionsMainPage(MVZ2.Options.OptionsDialogController ctrl)
        {
            try
            {
                if (ctrl == null || ctrl.Pointer == IntPtr.Zero)
                    return null;
                var optUi = ctrl.ui;
                if (optUi == null)
                    return null;
                return optUi.Main;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string cachedReplayLabel;
        private static string cachedEndLabel;

        /// <summary>
        /// 每帧把回放菜单里那两个按钮的文案**再按一遍**（幂等）。
        ///
        /// <para><b>为什么不能只靠 <c>UpdateRestartButton</c> / <c>UpdateLeaveLevelButton</c> 的后缀：</b>
        /// 那两个后缀只在「原版刷新主页控件」的那一瞬间生效。之后还有别的东西会重写这几个字 ——
        /// 典型是 <c>TextMeshProUGUITranslator</c>（节点被 enable 或切语言时会拿
        /// <c>Translate()</c> 的结果覆盖 <c>text</c>），以及原版自己的
        /// <c>UpdateMainPageButtonText</c>。表现就是「菜单打开着，字还是『重新开始 / 返回』」。</para>
        ///
        /// <para><b>为什么放在每帧：</b>菜单开着的时候帧数很低（游戏暂停），
        /// 一次两行字符串赋值的开销可以忽略；而且这样无论谁在什么时候覆盖，下一帧就回正。
        /// 菜单没开时（绝大多数时间）只有一次 <c>IsOpen()</c> 的原生调用。</para>
        /// </summary>
        public static void EnforceMenuLabels()
        {
            if (!ReplayPlayer.IsActive)
                return;
            try
            {
                var ctrl = LevelControllerInstance();
                if (ctrl == null)
                    return;
                var opts = ctrl.optionsDialogController;
                if (opts == null || opts.Pointer == IntPtr.Zero)
                    return;
                if (!opts.IsOpen())
                    return;
                var page = FindOptionsMainPage(opts);
                if (page == null)
                    return;

                if (cachedReplayLabel == null)
                    cachedReplayLabel = UiLocale.Get(LabelReplay);
                if (cachedEndLabel == null)
                    cachedEndLabel = UiLocale.Get(LabelEndPlay);

                page.SetButtonText(MVZ2.UI.OptionsDialogMainPage.TextButtonType.Restart, cachedReplayLabel);
                page.SetButtonText(MVZ2.UI.OptionsDialogMainPage.TextButtonType.LeaveLevel, cachedEndLabel);
            }
            catch (Exception) { }
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

        /// <summary>
        /// 回放期间**掐掉游戏自己的鼠标轮询**（<c>InputManager.UpdatePointer</c>）。
        ///
        /// <para><b>为什么必须掐：</b>那是第二条输入通路（
        /// <c>UpdatePointer → FlushPointerCaches → POST_POINTER_ACTION</c>）的源头，
        /// 它每帧读的都是**旁观的真人**的鼠标。不掐的话，回放期间
        /// 「右键取消手持物」「碾压墙每次按下扣进度」都会被旁观者直接触发 ——
        /// 既是体验问题，也是**确定性**漏洞（两个人看得不一样）。</para>
        ///
        /// <para>只掐 <c>UpdatePointer</c>、不动 <c>UpdateKeys</c>：
        /// 键盘（暂停 / 加速 / 血条 / Esc）照旧可用，UI 也照旧走 EventSystem。
        /// 回放自己需要的指针动作由 <c>AfterTick</c> 按轨迹发（见 <see cref="ReplayPointerAction.FirePhases"/>）。</para>
        /// </summary>
        public static bool UpdatePointerPrefix()
        {
            return !ReplayPlayer.IsPlaying;
        }

        // ── 按键状态接管 ──────────────────────────────────────────────
        //
        // 这些方法都很短，IL2CPP 有可能把里层内联进外层 ⇒ **每一层都挂**，
        // 播放小结里各自的命中次数能看出哪一层真的生效了（全是 0 就说明被内联了）。
        // 同时命中是安全的：答案都是从同一个掩码算出来的，重复覆盖不会累加。

        public static bool IsPointerOfPhasePrefix(int pointerId, MVZ2Logic.Inputs.PointerPhase phase, ref bool __result)
        {
            // 鼠标的 pointerId 是 -button-1（见 InputHelper）。负数才是鼠标。
            if (pointerId >= 0)
                return true;
            if (ReplayPlayer.TryGetPointerPhase(MVZ2Logic.Inputs.PointerTypes.MOUSE, -pointerId - 1, (int)phase, out var r))
            {
                __result = r;
                return false;
            }
            return true;
        }

        public static bool IsPointerDownByIdPrefix(int pointerId, ref bool __result)
        {
            return ById(pointerId, 1, ref __result);
        }

        public static bool IsPointerHoldingByIdPrefix(int pointerId, ref bool __result)
        {
            return ById(pointerId, 2, ref __result);
        }

        public static bool IsPointerUpByIdPrefix(int pointerId, ref bool __result)
        {
            return ById(pointerId, 3, ref __result);
        }

        private static bool ById(int pointerId, int phase, ref bool __result)
        {
            if (pointerId >= 0)
                return true;
            if (ReplayPlayer.TryGetPointerPhase(MVZ2Logic.Inputs.PointerTypes.MOUSE, -pointerId - 1, phase, out var r))
            {
                __result = r;
                return false;
            }
            return true;
        }

        public static bool IsPointerDownByButtonPrefix(int type, int button, ref bool __result)
        {
            return ByButton(type, button, 1, ref __result);
        }

        public static bool IsPointerHoldingByButtonPrefix(int type, int button, ref bool __result)
        {
            return ByButton(type, button, 2, ref __result);
        }

        public static bool IsPointerUpByButtonPrefix(int type, int button, ref bool __result)
        {
            return ByButton(type, button, 3, ref __result);
        }

        private static bool ByButton(int type, int button, int phase, ref bool __result)
        {
            if (ReplayPlayer.TryGetPointerPhase(type, button, phase, out var r))
            {
                __result = r;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 选项对话框的挂钩（详见 <see cref="ReplayDialogs"/>）。
    ///
    /// <para><b>为什么挂在 <c>CustomDialog</c> 上而不是调用方：</b>关卡里的对话框
    /// （<c>SlenderMan.ChooseFate</c> / <c>LockedChest</c> 的赎金 / 对话系统的选项）
    /// 最终都汇到 <c>MainScene → MainSceneUI → CustomDialog</c> 这一条，
    /// 挂在这里一处就全盖住了；挂在调用方则要跟每一个 Boss 的行为实现。</para>
    ///
    /// <para><b>为什么不用 <c>ref Action&lt;int&gt;</c> 劫持回调：</b>那个委托是
    /// 游戏原生代码里的，从托管侧读/调都不可靠。改成反射调
    /// <c>OnButtonRowItemClickCallback</c>（真按钮被点时走的就是它），
    /// 后面的链路全部照原样发生。</para>
    /// </summary>
    internal static class ReplayDialogPatches
    {
        /// <summary>
        /// <c>Il2CppStringArray</c> → <c>string[]</c>。
        ///
        /// <para>⚠️ interop 里 <c>string[]</c> 参数的**真实类型**是
        /// <c>Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray</c>。
        /// 前缀里写成 <c>string[]</c> 时 Harmony **不会报错**，但那个形参拿不到值
        /// （绑不上就默默给默认值）。自测的绑定校验就是专门用来抳这类问题的。</para>
        /// </summary>
        private static string[] ToArray(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray arr)
        {
            if (arr == null)
                return null;
            var result = new string[arr.Length];
            for (int i = 0; i < arr.Length; i++)
                result[i] = arr[i];
            return result;
        }

        /// <summary>对话框被创建。</summary>
        public static void SetDialogPrefix(MVZ2.UI.CustomDialog __instance, string titleText, string descText,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray options)
        {
            try
            {
                var texts = ToArray(options);
                if (ReplayPlayer.IsPlaying)
                {
                    ReplayDialogs.OnDialogOpened(__instance, titleText, descText, texts);
                    return;
                }
                if (ReplayRecorder.IsArmed)
                {
                    ReplayDialogs.RecordOpen(ReplayRecorder.CurrentTickNow, titleText, descText, texts);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 记录/接管对话框出错：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>某个选项按钮被点（真人点的，或我们反射调进来的）。</summary>
        public static void OptionClickPrefix(MVZ2.UI.CustomDialog __instance, MVZ2.UI.ButtonRow row, int index)
        {
            try
            {
                // 回放时我们自己调这个方法，不该记成「玩家的选择」。
                if (!ReplayRecorder.IsArmed)
                    return;
                ReplayDialogs.RecordChoice(row, index);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 记录对话框选择出错：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// <c>ButtonRow.UpdateButtons(string[] texts)</c> —— 记下「这一行各按钮的文字」。
        ///
        /// <para><b>为什么靠它而不是靠 <c>CustomDialog</c> 的私有字段：</b>
        /// Il2CppInterop **不会**为私有字段生成托管字段（实测 <c>countPerRow</c> /
        /// <c>buttonRowList</c> 用 <c>AccessTools.Field</c> 都找不到；它们是**属性**），所以
        /// 「选了第几项」和「该点第几项」都改成按**选项文字**定位。
        /// 这个方法拿到了每行的文字，于是「文字 → (行, 列)」可以反查出来，
        /// 连「每行几项」都不必知道。</para>
        /// </summary>
        public static void RowTextsPrefix(MVZ2.UI.ButtonRow __instance,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray buttonTexts)
        {
            try
            {
                ReplayDialogs.RecordRowTexts(__instance, ToArray(buttonTexts));
            }
            catch (Exception) { }
        }
    }
}
