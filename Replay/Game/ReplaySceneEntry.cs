using System;
using MVZ2.Level;
using MVZ2.Managers;
using PVZEngine;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Replay
{
    /// <summary>「进关卡场景 + 载入关键帧」这一步的状态。</summary>
    internal enum SceneEntryState
    {
        /// <summary>没有在进关卡。</summary>
        Idle = 0,
        /// <summary>已经发出进关卡指令，在等新控制器出现。</summary>
        Waiting = 1,
        /// <summary>关键帧已经载入完成。</summary>
        Done = 2,
        /// <summary>失败，原因见 <see cref="ReplaySceneEntry.Failure"/>。</summary>
        Failed = 3,
    }

    /// <summary>
    /// 播放开始时「进关卡场景 + 把关键帧载进新控制器」这一件事。
    ///
    /// <para><b>① 场景切换交给游戏自己。</b>
    /// 旧实现用 <c>IGlobalScene.GotoLevelCoroutine()</c>（或 <c>LevelManager.GotoLevelSceneAsync()</c>）
    /// 自己切场景，那是 <c>managed → il2cpp_runtime_invoke → 原生 async 场景加载</c> 这一跳；
    /// 0.7.0 上那一跳会让进程直接崩掉（<c>Fatal error. 0xC0000005</c>，现场与排查见 DESIGN.md）。
    /// 现在改成发游戏自己的控制台指令（关卡外 <c>/gotolevel</c>、关卡内 <c>/load</c>），
    /// 整条场景加载链路都留在 il2cpp 原生侧 —— 和游戏自己的「开始关卡 / 继续 / 重新开始」一模一样。</para>
    ///
    /// <para><b>② 关键帧的载入由我们**轮询**接管，不依赖游戏那条 async 续体。</b>
    /// 原本是在 <c>LevelManager.InitLevel</c> 的前缀里接管（<c>/gotolevel</c> 走这条路没问题）。
    /// 但 0.7.0 上 <c>/load</c> → <c>LevelController.ReloadLevel()</c> 的
    /// <c>await Scene.UnloadSceneAsync(旧场景)</c> **续体不会恢复**：旧场景确实被卸掉了
    /// （自测里逐秒 dump 的 <c>SceneManager</c> 场景表可以证明：3 个场景 → 2 个，旧 Level 消失、
    /// 新 Level 变为已加载），可后面的 <c>LevelManager.InitLevel(...)</c> 永远等不到 ——
    /// 于是回放卡在 tick 1。那条续体在游戏内部（我们既不能改也不该赌），所以改成：
    /// **在 <see cref="Tick"/> 里自己看「新控制器出来了没有」**，出来了就把关键帧载进去。
    /// 这跟「<c>InitLevel</c> 前缀」是同一个时刻（新场景加载完、旧场景卸完、控制器刚 Awake），
    /// 只是触发者从游戏换成了我们自己。</para>
    ///
    /// <para><b>③ <c>InitLevel</c> 前缀只剩「别让它把我们的状态冲掉」。</b>
    /// 只要这次 <c>InitLevel</c> 是回放引起的（接管中 / 已接管），前缀就返回 false 跳过原函数体。
    /// 顺带避开两件事：① 它第一行的 <c>SaveManager.SaveToFile()</c>（回放不许写存档）；
    /// ② 它会发现 <c>.lvl</c> 关卡存档并 <c>LoadLevel</c>，从**玩家上次玩到一半的状态**续关，
    /// 而不是我们要的录制状态。</para>
    /// </summary>
    internal static class ReplaySceneEntry
    {
        /// <summary>轮询超时（秒）。超过就报失败，别让回放无限等。</summary>
        internal const float PollTimeoutSec = 60f;

        /// <summary>关卡场景的名字（<c>LevelManager</c> 里写死的那个）。</summary>
        private const string LevelSceneName = "Level";

        internal static SceneEntryState State { get; private set; } = SceneEntryState.Idle;

        /// <summary>失败原因（只有 <see cref="SceneEntryState.Failed"/> 时有意义）。</summary>
        internal static string Failure { get; private set; }

        private static IntPtr oldControllerPointer = IntPtr.Zero;
        /// <summary>已经被我们接管（载入过关键帧）的那个控制器。</summary>
        private static IntPtr takenControllerPointer = IntPtr.Zero;
        /// <summary>等新控制器 / 等 Addressables 就绪用过的帧数。</summary>
        private static int waitFrames;
        private static bool hadOldLevelScene;
        private static int oldLevelSceneHandle;
        private static NamespaceID areaId;
        private static NamespaceID stageId;
        private static MVZ2Logic.Level.LevelExitTarget exitTarget =
            MVZ2Logic.Level.LevelExitTarget.MapOrMainmenu;
        private static float startedAt;

        /// <summary>
        /// 播放器在发进关卡指令**之前**调。
        ///
        /// <para>要先把「旧控制器 / 旧关卡场景」记下来：新控制器是
        /// <c>GotoLevelSceneAsync</c> 加载完新场景之后才 <c>SetLevelController</c> 的，
        /// 不记的话 <see cref="Tick"/> 会以为场上那个（已经 Dispose、碰撞系统填满的）旧控制器
        /// 就是新的，于是 <c>LoadGame</c> 撞键：
        /// <c>ArgumentException: An item with the same key has already been added.</c>
        /// <c>Key: [7, PVZEngine.Collisions.BuiltinCollisionEntity]</c>。</para>
        /// </summary>
        internal static void Begin(NamespaceID area, NamespaceID stage,
            MVZ2Logic.Level.LevelExitTarget target = MVZ2Logic.Level.LevelExitTarget.MapOrMainmenu)
        {
            areaId = area;
            stageId = stage;
            exitTarget = target;
            Failure = null;
            startedAt = Time.realtimeSinceStartup;

            oldControllerPointer = IntPtr.Zero;
            try
            {
                var ctrl = GetController();
                if (ctrl != null && ctrl.Pointer != IntPtr.Zero)
                    oldControllerPointer = ctrl.Pointer;
            }
            catch (Exception) { }

            oldLevelSceneHandle = 0;
            hadOldLevelScene = TryGetLoadedLevelSceneHandle(out oldLevelSceneHandle);

            takenControllerPointer = IntPtr.Zero;
            waitFrames = 0;
            State = SceneEntryState.Waiting;
        }

        /// <summary>指令本身就发失败了，别等了。</summary>
        internal static void Abort()
        {
            State = SceneEntryState.Idle;
            Failure = null;
        }

        /// <summary>播放结束 / 中止时复位。</summary>
        internal static void Reset()
        {
            State = SceneEntryState.Idle;
            Failure = null;
            oldControllerPointer = IntPtr.Zero;
            takenControllerPointer = IntPtr.Zero;
            hadOldLevelScene = false;
        }

        /// <summary>
        /// 这个控制器是不是「归回放管」——即它的关卡已经/即将由关键帧决定。
        ///
        /// <para>用于 <see cref="OnControllerInitLevel"/> 与 <see cref="OnStartLevelIntro"/>：
        /// 回放接管之后，游戏自己（尤其是 <c>/load</c> 那条迟到的 async 续体）再跑一次
        /// 关卡初始化会把我们恢复好的状态冲掉。</para>
        /// </summary>
        internal static bool Owns(LevelController ctrl)
        {
            if (ctrl == null)
                return false;
            if (State == SceneEntryState.Waiting)
                return true; // 已经在等新控制器了，出现的一定是它
            if (State != SceneEntryState.Done)
                return false;
            try
            {
                return ctrl.Pointer != IntPtr.Zero && ctrl.Pointer == takenControllerPointer;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// <c>LevelController.InitLevel</c> 的前缀。
        ///
        /// <para><b>为什么要拦在这里，而不是只拦 <c>LevelManager.InitLevel</c>：</b>
        /// 实测 <c>/load</c>（<c>Load.Invoke</c> → <c>LevelController.ReloadLevel()</c>）里那句
        /// <c>await LevelManager.GotoLevelSceneAsync()</c> 的续体**是会恢复的，只是很晚**：
        /// 我们在轮询里载完关键帧之后（<c>0x34E51540</c>，<c>LoadGame</c> 成功、实体 7..12），
        /// 同一帧里原生侧直接调进了 <c>LevelController.InitLevel</c> + <c>StartLevelIntro</c>
        /// —— 控制器被整个重建（<c>currentEntityID</c> 13 → 7，实体变成预览敌人 1..6），
        /// 关键帧白载。而那一跳<b>没有走</b> <c>LevelManager.InitLevel</c> 的托管桩
        /// （它的前缀一次都没打出来），所以只拦 <c>LevelManager.InitLevel</c> 拦不住。</para>
        ///
        /// <para>现在直接拦「真正重建关卡」的这一步：接管中就先载关键帧、已接管就直接跳过原函数体。
        /// 无论谁调、怎么调进来的，都不可能再把我们的状态冲掉。</para>
        /// </summary>
        internal static bool OnControllerInitLevel(LevelController ctrl)
        {
            if (State == SceneEntryState.Waiting)
            {
                // 这一刻就是最好的接管时机：控制器刚 Awake、还没被 InitLevel 填过碰撞系统。
                TakeOver(ctrl);
            }
            if (Owns(ctrl))
            {
                Plugin.Log?.LogInfo("[Replay] 跳过 LevelController.InitLevel："
                    + "这个控制器的关卡由关键帧决定（接管状态=" + State + "）");
                return false;
            }
            return true;
        }

        /// <summary>
        /// <c>LevelController.StartLevelIntro</c> 的前缀。
        ///
        /// <para><c>LevelManager.InitLevel</c> 的两个分支里，只有 <c>controller.InitLevel</c> 那条
        /// （没有 <c>.lvl</c> 存档时）才会接着调 <c>StartLevelIntro</c>。被我们接管的控制器上跑它，
        /// 会做「设起始镜头 + 开场对话 + <c>level.BeginLevel()</c>」—— 那是**新开一局**的流程，
        /// 而我们要的是游戏自己「从关卡存档继续」的那套（只关掉「关卡已载入」对话框解除暂停）。
        /// 所以一并跳过。</para>
        /// </summary>
        internal static bool OnStartLevelIntro(LevelController ctrl)
        {
            if (!Owns(ctrl))
                return true;
            Plugin.Log?.LogInfo("[Replay] 跳过 LevelController.StartLevelIntro：回放已经接管这个控制器");
            return false;
        }

        /// <summary>每帧一次（由 <c>ReplayPlayer</c> 的 WaitScene 阶段驱动）。</summary>
        internal static void Tick()
        {
            if (State != SceneEntryState.Waiting)
                return;

            if (Time.realtimeSinceStartup - startedAt > PollTimeoutSec)
            {
                Fail("等新关卡控制器超时（" + PollTimeoutSec + " 秒）—— 场景加载卡住了，"
                    + "或者游戏没有把我们带进关卡");
                return;
            }

            // 等旧关卡场景卸完再动手：这正是「LevelManager.InitLevel 前缀」触发的那一刻。
            if (hadOldLevelScene && IsSceneHandleLoaded(oldLevelSceneHandle))
                return;

            LevelController ctrl;
            try
            {
                ctrl = GetController();
            }
            catch (Exception)
            {
                return;
            }
            if (ctrl == null || ctrl.Pointer == IntPtr.Zero)
                return;
            if (ctrl.Pointer == oldControllerPointer)
                return; // 还是旧的

            // 可选延迟：给关卡场景自己的（Addressables）异步初始化一点时间。
            // 见 ReplayConfig.KeyframeDelayFramesValue 的说明。
            var delay = ReplayConfig.KeyframeDelayFramesValue;
            if (delay > 0 && waitFrames < delay)
            {
                waitFrames++;
                if (waitFrames == 1)
                    Plugin.Log?.LogInfo("[Replay] 按 KeyframeDelayFrames=" + delay
                        + " 先等一会儿再载入关键帧");
                return;
            }

            TakeOver(ctrl);
        }

        /// <summary>
        /// <c>LevelManager.InitLevel</c> 的前缀。
        ///
        /// <para>返回 false = 跳过原方法。接管中（<see cref="SceneEntryState.Waiting"/>）与
        /// 已接管（<see cref="SceneEntryState.Done"/>）时都跳过 —— 前者不抢在轮询前面动手，
        /// 后者防止游戏那条迟到的续体把我们的状态冲掉。不是回放引起的 <c>InitLevel</c> 原样放行。</para>
        /// </summary>
        internal static bool OnInitLevel(MVZ2.Level.LevelManager manager, NamespaceID areaID,
            NamespaceID stageID, MVZ2Logic.Level.LevelExitTarget exitTarget)
        {
            if (State == SceneEntryState.Idle || State == SceneEntryState.Failed)
                return true;

            Plugin.Log?.LogInfo("[Replay] InitLevel 前缀：跳过游戏原本的初始化（接管状态="
                + State + "）");
            return false;
        }

        private static void TakeOver(LevelController ctrl)
        {
            try
            {
                Plugin.Log?.LogInfo("[Replay] 轮询到新控制器 = 0x"
                    + ctrl.Pointer.ToInt64().ToString("X") + "，开始载入关键帧");
                if (!ReplayPlayer.ApplyKeyframeTo(ctrl, areaId, stageId, exitTarget))
                {
                    Fail(ReplayPlayer.FailureReason ?? "载入关键帧失败");
                    return;
                }
                takenControllerPointer = ctrl.Pointer;
                State = SceneEntryState.Done;
                Plugin.Log?.LogInfo("[Replay] 已在新控制器上恢复关键帧（接管成功，控制器 = 0x"
                    + ctrl.Pointer.ToInt64().ToString("X") + "）");
            }
            catch (Exception e)
            {
                Fail(e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Fail(string reason)
        {
            Failure = reason;
            State = SceneEntryState.Failed;
            Plugin.Log?.LogError("[Replay] 进关卡接管失败：" + reason);
        }

        private static LevelController GetController()
        {
            var main = MainManager.Instance;
            if (main == null || main.LevelManager == null)
                return null;
            return main.LevelManager.GetLevelController();
        }

        private static bool TryGetLoadedLevelSceneHandle(out int handle)
        {
            handle = 0;
            try
            {
                int n = SceneManager.sceneCount;
                for (int i = 0; i < n; i++)
                {
                    var sc = SceneManager.GetSceneAt(i);
                    if (sc.isLoaded && sc.name == LevelSceneName)
                    {
                        handle = sc.handle;
                        return true;
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        private static bool IsSceneHandleLoaded(int handle)
        {
            try
            {
                int n = SceneManager.sceneCount;
                for (int i = 0; i < n; i++)
                {
                    var sc = SceneManager.GetSceneAt(i);
                    if (sc.isLoaded && sc.handle == handle)
                        return true;
                }
            }
            catch (Exception) { }
            return false;
        }
    }
}
