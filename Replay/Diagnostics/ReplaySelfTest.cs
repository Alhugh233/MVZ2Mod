using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using DSHCore.Ui;
using HarmonyLib;
using MVZ2.Level;
using MVZ2.Managers;
using MVZ2.Scenes;
using MVZ2Logic.Scenes;
using PVZEngine.Level;
using UnityEngine;
using Application = UnityEngine.Application;

namespace Replay
{
    /// <summary>
    /// 内置自测（由配置 <c>SelfTest.Enabled</c> 打开）。
    ///
    /// 目的：**不依赖任何人工操作**地验证整条录制链路。
    /// 由 <c>MainSceneController.Update</c> 的 postfix 每帧驱动一个状态机：
    ///
    ///   1. 等主菜单就绪（若停在标题界面，用 <c>DisplayMainmenu()</c> 程序化跳过
    ///      —— 与玩家点一下「点击以开始」等价，但不需要人去点，也不碰操作系统输入）
    ///   2. 用游戏自己的调试指令 <c>/gotolevel &lt;stage&gt; &lt;area&gt;</c> 进关卡
    ///   3. 等录制手臂就绪；超时则重试进关，再不行就 <c>ApplyChoose + GameStartInstantTransition</c>
    ///      直接开局（用于「关卡存档首行校验不通过」这类走不下去的情况）
    ///   4. 按 tick 间隔注入**真实输入入口**：选卡热键（<c>LevelController.OnBlueprintKey</c>）
    ///      与工具（<c>ClickPickaxe/ClickStarshard/ClickTrigger</c>）
    ///   5. 冷却若干 tick 后停止录制
    ///   6. 校验写出的 <c>.mrep</c>（gzip 解压 → header 字段 → 逐行 JSON 形状 → 事件/指纹计数）
    ///   7. 报告写到 <c>&lt;游戏目录&gt;\BepInEx\_replay_selftest.txt</c>，可选自动退出游戏
    ///
    /// 注入用的是反射调用**私有的输入入口方法**，所以会**真正经过 Harmony 补丁**
    /// ——这正好端到端地验证了「挂钩点 → 记录器 → 写出文件」。
    /// </summary>
    internal static class ReplaySelfTest
    {
        private enum Phase
        {
            WaitMenu = 0,
            GotoLevel = 1,
            WaitLevel = 2,
            Warmup = 3,
            Actions = 4,
            CoolDown = 5,
            StopAndVerify = 6,
            Quit = 7,
            Done = 8,
            VerifyPlayback = 9,
            /// <summary>回放结束面板的三个按钮（导出 / 重新播放 / 退出）依次实测。</summary>
            EndPanel = 10,
        }

        private const string ReportFileName = "_replay_selftest.txt";
        private const int MaxEventsListed = 40;
        private const int MainmenuPage = (int)MainScenePageType.Mainmenu;

        private static readonly List<string> report = new List<string>();
        private static Phase phase = Phase.WaitMenu;
        private static int frames;

        /// <summary>
        /// 「本阶段开始那一刻的关卡逻辑 tick」。
        ///
        /// <para><see cref="Phase.Actions"/> 与 <see cref="Phase.CoolDown"/> 的间隔一律按它算，
        /// 也就是配置里 <c>ActionGapTicks</c> / <c>CooldownTicks</c> 名字里写的那个 tick
        /// —— 不是帧（见 <see cref="EnterPhase"/>）。</para>
        /// </summary>
        private static int phaseTick;
        private static bool started;
        private static bool finished;
        private static string failReason;

        private static DateTime phaseStart = DateTime.Now;
        private static int armedAtTick = -1;
        private static readonly List<string> actionLog = new List<string>();
        private static int actionsDone;
        private static int verdictFail;
        private static int retries;
        private static int forceAttempts;
        private static bool forcedStart;
        private static bool deletedLevelState;
        private static bool playOnly;
        private static string playFileName;
        private static DateTime lastHeartbeatAt = DateTime.MinValue;
        private static int lastPageSeen = -1;

        // 播放验收时的截图（见 ReplayConfig.SelfTestPlayShotTick）。
        private static int shotState;      // 0 未抓 / 1 已抓第一张 / 2 抓完
        private static int shotNext;

        public static bool Enabled
        {
            get
            {
                try { return ReplayConfig.SelfTest != null && ReplayConfig.SelfTest.Value; }
                catch (Exception) { return false; }
            }
        }

        #region 每帧驱动
        public static void Tick(MainSceneController scene)
        {
            if (finished || !Enabled)
                return;

            try
            {
                if (!started)
                {
                    started = true;
                    phaseStart = DateTime.Now;
                    playFileName = CfgString(ReplayConfig.SelfTestPlayFile, "").Trim();
                    playOnly = playFileName.Length > 0;
                    report.Clear();
                    report.Add("=== MVZ2 Replay self-test ===");
                    report.Add("time        : " + DateTime.Now.ToString("s"));
                    report.Add("模式        : " + (playOnly ? "只播放（" + playFileName + "）" : "录制 + 播放"));
                    report.Add("stage/area  : " + CfgString(ReplayConfig.SelfTestStage, "mvz2:halloween_1")
                        + " / " + CfgString(ReplayConfig.SelfTestArea, "mvz2:halloween"));
                    report.Add("autoRecord  : " + (ReplayConfig.AutoRecord != null && ReplayConfig.AutoRecord.Value));
                    // v9：把自测探针注册进 ReplayCompat（开关默认关，见 PointerProbeNeed）。
                    EnsureProbeRegistered();
                    WriteReport();
                    Plugin.Log?.LogInfo("[SelfTest] 开始（" + (playOnly ? "只播放 " + playFileName : "阶段机启动") + "）");
                }

                frames++;
                var main = MainManager.Instance;
                if (main == null)
                    return;

                int page = GetPage(scene);
                if (page != lastPageSeen)
                {
                    lastPageSeen = page;
                    Note("当前页面 = " + DescribePage(page) + " (" + page + ")");
                }

                switch (phase)
                {
                    case Phase.WaitMenu:
                        StepWaitMenu(scene, page);
                        break;

                    case Phase.GotoLevel:
                        {
                            var ctrl0 = GetController();
                            if (ctrl0 != null && IsStarted(ctrl0))
                            {
                                Note("已经在关卡里，跳过 gotolevel");
                                OnLevelEntered(ctrl0);
                                break;
                            }
                            IssueGotoLevel(main);
                            EnterPhase(Phase.WaitLevel);
                            break;
                        }

                    case Phase.WaitLevel:
                        StepWaitLevel(main);
                        break;

                    case Phase.Warmup:
                        {
                            var ctrl = GetController();
                            if (ctrl == null) { Fail("关卡中途消失"); break; }
                            if (!IsRunning(ctrl)) { TryUnpause(ctrl); return; }
                            if (Ticks(ctrl) - armedAtTick >= CfgInt(ReplayConfig.SelfTestWarmupTicks, 60))
                            {
                                Note("热身完成，开始注入输入");
                                EnterPhase(Phase.Actions);
                            }
                            break;
                        }

                    case Phase.Actions:
                        {
                            var ctrl = GetController();
                            if (ctrl == null) { Fail("关卡中途消失"); break; }
                            if (!IsRunning(ctrl)) { TryUnpause(ctrl); return; }

                            var items = CfgString(ReplayConfig.SelfTestActions, "bpkey0,bpkey1,pickaxe,starshard,trigger").Split(',');
                            if (actionsDone >= items.Length)
                            {
                                Note("动作注入完成，进入冷却");
                                EnterPhase(Phase.CoolDown);
                                break;
                            }
                            // 按 tick 计间隔：第 k 个动作落在 phaseTick + (k+1)*gap。
                            // 用帧会让录制长度随帧率飘（低帧率下一帧顶好几 tick）。
                            var gap = Math.Max(CfgInt(ReplayConfig.SelfTestActionGapTicks, 30), 1);
                            if (Ticks(ctrl) - phaseTick < (actionsDone + 1) * gap)
                                break;

                            var item = items[actionsDone].Trim();
                            var before = ReplayRecorder.EventCount;
                            Inject(ctrl, item);
                            var after = ReplayRecorder.EventCount;
                            // 选卡类动作额外报一下「手上到底有没有拿到蓝图」：
                            // 拿不到就不会写 itg/evk，而那是「录制内容如此」而不是「功能失效」。
                            var held = "";
                            if (item.StartsWith("bpkey", StringComparison.Ordinal))
                            {
                                held = "  手持=" + (ReplayHeldFlags.LastType ?? "?")
                                    + (ReplayHeldFlags.LastSkipReason == null
                                        ? "（已记录 itg/evk）"
                                        : "（没记 itg/evk：" + ReplayHeldFlags.LastSkipReason + "）");
                            }
                            actionLog.Add("  " + item.PadRight(12) + " 记录事件 +" + (after - before)
                                + (after - before > 0 ? "" : "   (无变化)") + held);
                            actionsDone++;
                            WriteReport();
                            break;
                        }

                    case Phase.CoolDown:
                        {
                            var ctrl = GetController();
                            if (ctrl == null) { Fail("关卡中途消失"); break; }
                            if (!IsRunning(ctrl)) { TryUnpause(ctrl); return; }   // 暂停时 tick 不前进，不能干等
                            // 按 tick 计：默认总长（热身 60 + 动作 5×30 + 冷却 90 = 300 tick）
                            // 才能盖过默认 150 的指纹采样间隔，采到指纹。
                            if (Ticks(ctrl) - phaseTick >= CfgInt(ReplayConfig.SelfTestCooldownTicks, 90))
                                phase = Phase.StopAndVerify;
                            break;
                        }

                    case Phase.StopAndVerify:
                        {
                            var path = StopAndVerify();
                            if (CfgBool(ReplayConfig.SelfTestPlayback, true) && !string.IsNullOrEmpty(path))
                            {
                                report.Add("");
                                report.Add("--- 回放播放验证 ---");
                                try
                                {
                                    report.Add(ReplayPlayer.Start(path));
                                }
                                catch (Exception e)
                                {
                                    report.Add("  启动播放失败: " + Short(e));
                                    Check(false, "能启动播放");
                                }
                                WriteReport();
                                EnterPhase(Phase.VerifyPlayback);
                            }
                            else
                            {
                                EnterPhase(CfgBool(ReplayConfig.SelfTestAutoQuit, true) ? Phase.Quit : Phase.Done);
                            }
                            break;
                        }

                    case Phase.VerifyPlayback:
                        {
                            // 播放期间游戏会重新加载一次关卡场景，所以这里只是等它自己跑完。
                            if (ReplayPlayer.IsActive)
                            {
                                var elapsed = (DateTime.Now - phaseStart).TotalSeconds;
                                int timeout = CfgInt(ReplayConfig.SelfTestPlaybackTimeoutSec, 180);
                                Heartbeat("等回放播完", elapsed, GetController());

                                PlayShotCheck();
                                PlayMenuAudit();
                                ResizeProbe();

                                // 关卡在播放途中结束（失败/通关）时游戏会弹对话框并 PauseGame()，
                                // 而**暂停时逻辑 tick 不前进** → 播放一直卡到超时。
                                // 实测白卡了 300 秒，连小结（里面有最值钱的「目标找不到分类」）都拿不到。
                                // 所以这里一发现 tick 长时间不动就主动收尾。
                                if (PlaybackStalled())
                                {
                                    Plugin.Log?.LogWarning("[Replay] [SelfTest] 回放卡在 tick " + ReplayPlayer.CurrentTick
                                        + " 已 " + (int)(DateTime.Now - lastPlayTickAt).TotalSeconds
                                        + " 秒不动 —— 关卡多半已经结束或被暂停。提前收尾，保证小结能写出来。");
                                    Note("回放停滞：tick 停在 " + ReplayPlayer.CurrentTick + "，提前收尾");
                                    ReplayPlayer.Stop("selftest-stalled");
                                    // 刻意不 return：让下面那段「收尾 + 写小结」照常执行。
                                }
                                else
                                {
                                    if (elapsed > timeout)
                                    {
                                        ReplayPlayer.Stop("selftest-timeout");
                                        Fail("等待回放播完超时（" + timeout + " 秒）");
                                    }
                                    return;
                                }
                            }

                            report.Add(ReplayPlayer.LastSummary ?? ReplayPlayer.BuildSummary());
                            report.Add("  播放器状态: " + ReplayPlayer.Describe());
                            report.Add("  停下原因  : " + (ReplayPlayer.StopReason ?? "?"));
                            report.Add("  真实耗时  : " + ReplayPlayer.ElapsedSeconds.ToString("F1") + " 秒");
                            // 结束面板（导出 / 重新播放 / 退出）：它是回放收尾的用户可见界面，
                            // 必须实测过才算做完了。
                            report.Add("  结束面板  : " + ReplayEndPanel.Describe());
                            Shot("_rp_end.png");
                            Check(ReplayEndPanel.ShowCount >= 1, "回放结束时弹出了结束面板");
                            Check(!ReplayPlayer.FailedNow, "回放播放成功（没报错）");
                            if (playOnly)
                            {
                                Check(ReplayPlayer.InjectedEventCount >= 1, "回放里至少注入了 1 个事件");
                                Check(ReplayPlayer.VerifiedComparisons >= 1, "回放里至少核对了 1 条指纹");
                                Check(ReplayPlayer.VerifiedMismatches == 0,
                                    "回放状态与录制时完全一致（指纹不一致 " + ReplayPlayer.VerifiedMismatches
                                    + " 条，停在 tick " + ReplayPlayer.CurrentTick + "）");
                            }
                            else
                            {
                                Check(ReplayPlayer.InjectedEventCount >= 1, "回放里至少注入了 1 个事件");
                                Check(ReplayPlayer.VerifiedComparisons >= 1, "回放里至少核对了 1 条指纹");
                                Check(ReplayPlayer.VerifiedMismatches == 0,
                                    "回放状态与录制时完全一致（指纹不一致 " + ReplayPlayer.VerifiedMismatches + " 条）");
                            }
                            SaveGuardCheck();
                            FakeMouseCheck();
                            HeldFlagCheck();
                            CompatCheck();
                            WriteReport();
                            EnterPhase(Phase.EndPanel);
                            break;
                        }

                    case Phase.EndPanel:
                        StepEndPanel();
                        break;

                    case Phase.Quit:
                        if (frames > 120)
                        {
                            Note("自动退出游戏");
                            WriteReport();
                            finished = true;
                            try { Application.Quit(); }
                            catch (Exception) { }
                        }
                        break;

                    case Phase.Done:
                        break;
                }
            }
            catch (Exception e)
            {
                try { Fail("自测异常: " + e.GetType().Name + ": " + e.Message); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// 回放结束面板的三个按钮逐个点一遍（走的是和玩家点击**完全相同**的那个委托）。
        ///
        /// <para>顺序有讲究：「导出」只是副作用（写文件 + 弹一个单按钮提示，关掉后还会把面板弹回来），
        /// 可以原地测；「重新播放」会把播放从头再来一遍，没法在同一轮里接着往下检查；
        /// 「退出」是终结动作，所以放最后。</para>
        ///
        /// <para>本轮实测「导出（结果写回面板正文）→ 退出 → 落点回放页 → 返回主菜单」。
        /// 「重新播放」走 <c>ReplayPlayer.RestartCurrent()</c>（内部就是 <c>Stop</c> + <c>Start</c>，
        /// 而 <c>Start</c> 在本次自测开头已经跑通过），这里不重复跑。</para>
        ///
        /// <para>⚠️「导出」那一步走 <c>SelfTestExport</c> 而不是点按钮：现在是游戏原生的
        /// 「另存为」对话框（阻塞式、系统级），自动化里一弹就再也回不来了。
        /// 只把「选路径」换掉，导出与结果回写这条链照测。</para>
        ///
        /// <para>⚠️ <c>SelfTestChoose</c> 是直接调我们的 <c>onSelect</c> 委托，**绕过了
        /// CustomDialog 自己的「按钮 → 隐藏」那一步**；而游戏显示对话框又是排队的。
        /// 所以两次模拟点击之间要留足帧数，否则会出现「面板已经换了、旧的那个还挂在屏幕上」
        /// 这种只属于自测的假象（真人点击不会）。</para>
        /// </summary>
        private const int EndPanelStepFrames = 90;

        /// <summary>
        /// 实测「回放改不了存档」：金币（走影子）与统计数据（直接拦）两条路都验一遍。
        ///
        /// <para><b>为什么偏偏挑在**这里**测：</b>此刻 <c>FinishPlayback</c> 已经跑过、
        /// 结束面板正开着、关卡还在后面继续跑 —— 也就是护栏**必须还开着**的那个状态
        /// （再往后一步点「退出」就交还给游戏了，那时验不出来）。</para>
        ///
        /// <para>这条从游戏表现上完全看不出来：回放里的钱照样涨跌、统计照样累加，
        /// 只是那些变化全进了 mod 自己的副本。所以只能这样直接对着存档读一遍。</para>
        /// </summary>
        private static void SaveGuardCheck()
        {
            report.Add("");
            report.Add("--- 存档护栏（回放不许改动存档）---");
            report.Add("  补丁安装  : " + ReplayHooks.SaveGuardStatus);
            report.Add("  护栏状态  : " + ReplaySaveGuard.Describe());
            Check(ReplaySaveGuard.Active, "播放结束后护栏还开着（结束面板期间关卡仍在跑）");

            var ctrl = GetController();
            var engine = ctrl != null ? ctrl.GetEngine() : null;

            // ① 金币：回放里加减钱只进影子，真存档一个字节不动。
            if (engine != null)
            {
                try
                {
                    int real0 = ReplaySaveGuard.WithoutGuard(() => MVZ2Logic.Level.LogicLevelExt.GetMoney(engine));
                    int shadow0 = MVZ2Logic.Level.LogicLevelExt.GetMoney(engine);
                    MVZ2Logic.Level.LogicLevelExt.AddMoney(engine, 1234);
                    int shadow1 = MVZ2Logic.Level.LogicLevelExt.GetMoney(engine);
                    int real1 = ReplaySaveGuard.WithoutGuard(() => MVZ2Logic.Level.LogicLevelExt.GetMoney(engine));
                    report.Add("  金币      : 真=" + real0 + " 影子=" + shadow0
                        + " → 影子上 +1234 = " + shadow1 + "，真值仍=" + real1);
                    Check(shadow1 == shadow0 + 1234, "回放里挣/花的钱只加在影子上（界面照样看得见变化）");
                    Check(real1 == real0, "回放里改的钱没有漏进真存档");
                }
                catch (Exception e)
                {
                    report.Add("  金币      : 取值失败 " + Short(e));
                    Check(false, "金币影子能取值");
                }
            }
            else
            {
                report.Add("  金币      : 关卡引擎不在，跳过");
            }

            // ② 统计数据：写入被直接拦下。
            try
            {
                var main = MainManager.Instance;
                var sm = main != null ? main.SaveManager : null;
                if (sm == null)
                {
                    report.Add("  统计      : 拿不到 SaveManager，跳过");
                }
                else
                {
                    var cat = PVZEngine.NamespaceID.Parse("replay_selftest", "mvz2");
                    var ent = PVZEngine.NamespaceID.Parse("probe", "mvz2");
                    long s0 = sm.GetStat(cat, ent);
                    sm.SetStat(cat, ent, s0 + 7);
                    long s1 = sm.GetStat(cat, ent);
                    report.Add("  统计      : replay_selftest/probe = " + s0
                        + " → 写入 +7 之后 = " + s1);
                    Check(s1 == s0, "回放里的统计数据写入被拦下（不会漏进存档）");
                }
            }
            catch (Exception e)
            {
                report.Add("  统计      : 取值失败 " + Short(e));
                Check(false, "统计拦截能取值");
            }

            // ③ 屏幕坐标换算：换个窗口尺寸也要指向同一个世界点。
            bool vpOk = ReplayViewport.SelfCheck(out var vpDetail);
            report.Add("  窗口换算  : " + vpDetail);
            Check(vpOk, "屏幕坐标换算在世界维度上无损（换窗口尺寸也能对上操作位置）");
        }

        /// <summary>
        /// 实测「蓝图上的即时触发标记」这条链。
        ///
        /// <para>为什么要单独测属性读写：那个标记是**注册过的属性键**才有意义的。键没注册时
        /// <c>SetProperty</c> 只会默默失败，于是「回放时把标记按回去」就成了**静默无效**，
        /// 而它从游戏表现上完全看不出来（要等某个器械行为不一样了才会发现）。</para>
        ///
        /// <para>自测注入的那几个动作不一定会真的拿起蓝图（能量 / 卡是否有效由游戏决定），
        /// 所以这里如实把「手上是什么」也报出来：拿不到蓝图就不会写 itg/evk，
        /// 那是**录制内容如此**，不是功能失效。</para>
        /// </summary>
        private static void HeldFlagCheck()
        {
            report.Add("");
            report.Add("--- 蓝图即时触发标记（与「交换触发」选项解耦）---");
            bool plumb = ReplayHeldFlags.SelfCheck(out var detail);
            report.Add("  属性读写 : " + detail);
            Check(plumb, "蓝图即时触发属性真的能写进去、读回来（键已注册）");
            report.Add("  录制时   : 自测最后一个选卡动作之后手持=" + (ReplayHeldFlags.LastType ?? "没读到")
                + (ReplayHeldFlags.LastSkipReason == null
                    ? "  → 事件里已带上 itg/evk"
                    : "  → 事件里没有 itg/evk（" + ReplayHeldFlags.LastSkipReason + "）"));
        }

        /// <summary>
        /// 实测「假鼠标的锚点在贴图左上角」。
        ///
        /// <para>背景：游戏箭头的光点存在 <c>CursorData.hotspot</c>，而那是交给
        /// <c>Cursor.SetCursor</c> 的 —— 那个重载的热点**以贴图左上角为原点**。
        /// 而 <c>RectTransform.pivot</c> 用的是**左下角**原点，两者差一个上下翻转。
        /// 漏了那一步的话，光标会整整偏出一个贴图高度（看起来就像「锚点在贴图正中」）。</para>
        /// </summary>
        private static void FakeMouseCheck()
        {
            report.Add("");
            report.Add("--- 假鼠标锚点 ---");
            bool anchorOk = FakePointer.SelfCheckAnchor(out var anchorDetail);
            report.Add("  " + anchorDetail);
            Check(anchorOk, "假鼠标的锚点在贴图左上角（与系统光标热点同制）");
        }

        // ── 播放中途改窗口尺寸（回归验证「假鼠标与分辨率无关」）──
        //
        // 用户报的 bug：改窗口大小之后假鼠标与操作位置对不上。成因有两个：
        //   ① 把换算结果在播放开始时**烘**进了路点表 / 缓存的指针位置；
        //   ② 锚点算错（热点左上角原点 vs 枢轴左下角原点）。
        // 这里真的改一次分辨率再验算 —— 不这么做的话，「看起来修好了」和「真的修好了」
        // 从报告上分不出来。改完立刻还原（后续还有截图与布局检定）。
        private static int resizeStage;
        private static int resizeAtFrame;
        private static int resizeOrigW, resizeOrigH;
        /// <summary>这一轮是不是塞了假路点（见 <see cref="FakePointer.SelfTestSetTrack"/>），用完要还原。</summary>
        private static bool resizeSynthetic;
        private const int ResizeProbeW = 800;
        private const int ResizeProbeH = 480;

        private static void ResizeProbe()
        {
            try
            {
                if (resizeStage == 0)
                {
                    // 等假鼠标真的在动再动手：路点表在 Start 里就建好了，但节点
                    // 要等关卡场景 LoadGame 完 + 第一个 tick 才会创建（那时才有 rect 可验）。
                    if (!ReplayPlayer.IsPlaying || !FakePointer.Active)
                        return;
                    if (ReplayPlayer.CurrentTick < 30 || frames < 60)
                        return;
                    if (!FakePointer.NodeReady)
                        return;
                    resizeOrigW = Screen.width;
                    resizeOrigH = Screen.height;
                    report.Add("");
                    report.Add("--- 窗口尺寸换算（播放中途改大小）---");
                    report.Add("  请求 : " + resizeOrigW + "x" + resizeOrigH + " -> " + ResizeProbeW + "x" + ResizeProbeH);
                    resizeAtFrame = frames;
                    resizeStage = 1;
                    WriteReport();
                    Screen.SetResolution(ResizeProbeW, ResizeProbeH, Screen.fullScreenMode);
                    return;
                }

                if (resizeStage == 1)
                {
                    if (frames - resizeAtFrame < 45)
                        return;

                    bool took = Math.Abs(Screen.width - ResizeProbeW) <= 2 && Math.Abs(Screen.height - ResizeProbeH) <= 2;

                    // ⚠️ 自测注入的动作（数字键选卡 / 工具按钮）**一个坐标都没有**，
                    // 所以「带坐标的路点表」在这种回放里是空的，换算路径根本不会被执行。
                    // 临时塞一条假路点（坐标是录制坐标系的）把那条路走通 —— 用完立刻还原。
                    // 有真路点时当然优先用真的。
                    bool synthetic = false;
                    if (!FakePointer.HasTrack)
                    {
                        float rw = ReplayViewport.RecordWidth;
                        float rh = ReplayViewport.RecordHeight;
                        if (rw <= 1f || rh <= 1f)
                        {
                            // 旧格式文件没有窗口尺寸，没法验 —— 如实报出来。
                            report.Add("  跳过：这个回放文件没记窗口尺寸（ver < 7）");
                            resizeStage = 3;
                            resizeAtFrame = frames;
                            WriteReport();
                            Screen.SetResolution(resizeOrigW, resizeOrigH, Screen.fullScreenMode);
                            return;
                        }
                        FakePointer.SelfTestSetTrack(new[]
                        {
                            new Vector2(rw * 0.35f, rh * 0.62f),
                            new Vector2(rw * 0.70f, rh * 0.35f),
                        });
                        synthetic = true;
                    }

                    var raw = FakePointer.RawScreenPos;
                    var now = FakePointer.ScreenPos;
                    var expectLive = ReplayViewport.ToSize(raw, Screen.width, Screen.height);
                    var expectBaked = ReplayViewport.ToSize(raw, resizeOrigW, resizeOrigH);

                    report.Add("  现在窗口 : " + Screen.width + "x" + Screen.height
                        + "（分辨率真的改了=" + took + "）");
                    report.Add("  原始坐标 : " + FmtPoint(raw) + "（录制坐标系" 
                        + (synthetic ? "，**自测塞的假路点**：这条回放的事件没有坐标" : "") + "）");
                    report.Add("  假鼠标   : " + FmtPoint(now));
                    report.Add("  应为（按**当前**尺寸换算）: " + FmtPoint(expectLive));
                    report.Add("  若烘死（按录制时尺寸）    : " + FmtPoint(expectBaked));

                    // 第一条是最关键的：换成当前尺寸的结果必须和实际用的一致。
                    // 旧实现（把换算烘进路点表）在这里会直接对不上。
                    Check(PointDistance(now, expectLive) < 1f,
                        "假鼠标位置按**当前**窗口尺寸换算（不是播放开始时烘好的）");
                    if (took)
                    {
                        Check(PointDistance(now, expectBaked) > 1f,
                            "没有沿用录制时尺寸的结果（窗口一改，像素就变 —— 但它指的仍是同一格）");
                    }
                    else
                    {
                        report.Add("  （分辨率没改成功，跳过「没有沿用旧尺寸」那条 —— 换算公式本身仍已验过）");
                    }

                    // 节点落点的检定要**再等几个逻辑 tick** 才做：摆放发生在 tick 上
                    // （FakePointer.Tick → UpdateVisual → PlaceAt），
                    // 刚换完路点表就去看，看到的还是上一次摆放的位置。
                    resizeSynthetic = synthetic;
                    resizeAtFrame = frames;
                    resizeStage = 2;
                    WriteReport();
                    return;
                }

                if (resizeStage == 2)
                {
                    if (frames - resizeAtFrame < 15)
                        return;

                    // 完整链路：录制坐标 → 当前窗口像素 → 相机反投影 → 节点世界坐标。
                    bool placed = FakePointer.SelfCheckPlacement(out var placeDetail);
                    report.Add("  节点落点 : " + placeDetail);
                    Check(placed, "假鼠标节点的落点与「换算后的像素」一致");

                    if (resizeSynthetic)
                        FakePointer.SelfTestClearTrack();

                    resizeAtFrame = frames;
                    resizeStage = 3;
                    WriteReport();
                    Screen.SetResolution(resizeOrigW, resizeOrigH, Screen.fullScreenMode);
                    return;
                }

                if (resizeStage == 3)
                {
                    if (frames - resizeAtFrame < 25)
                        return;
                    bool back = Math.Abs(Screen.width - resizeOrigW) <= 2 && Math.Abs(Screen.height - resizeOrigH) <= 2;
                    report.Add("  已还原到 " + Screen.width + "x" + Screen.height + "（成功=" + back + "）");
                    Check(back, "窗口尺寸已还原（后面的截图与布局检定不受影响）");
                    resizeStage = 4;
                    WriteReport();
                }
            }
            catch (Exception e)
            {
                resizeStage = 4;
                report.Add("  窗口尺寸测试出错：" + Short(e));
                try { Screen.SetResolution(resizeOrigW, resizeOrigH, Screen.fullScreenMode); } catch (Exception) { }
            }
        }

        private static string FmtPoint(Vector2 p)
        {
            return "(" + p.x.ToString("0.#") + ", " + p.y.ToString("0.#") + ")";
        }

        private static float PointDistance(Vector2 a, Vector2 b)
        {
            float dx = a.x - b.x;
            float dy = a.y - b.y;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        private static void StepEndPanel()
        {
            try
            {
                if (endStage == 0)
                {
                    if (frames - endWaitFrame < EndPanelStepFrames)
                        return;
                    endStage = 1;
                    endWaitFrame = frames;

                    // 记下文件路径：「导入」测试要用它当源文件（这时候还没有退出关卡）。
                    probeMrepPath = ReplayPlayer.CurrentPath;

                    report.Add("--- 结束面板按钮 ---");
                    // 需求：结束面板上的「导出 .mrep」已移除（功能移入回放页的【更多】）。
                    var opts = ReplayEndPanel.Options;
                    var hasExport = false;
                    for (int i = 0; i < opts.Length; i++)
                    {
                        if (opts[i] != null && opts[i].IndexOf("导出", StringComparison.Ordinal) >= 0)
                            hasExport = true;
                    }
                    report.Add("  面板按钮 : [" + string.Join(", ", opts) + "]");
                    Check(!hasExport, "结束面板不再有「导出」按钮（已移入回放页的【更多】）");

                    // 导出机制本身仍然要验（它现在是【更多】面板在调）：
                    // 不弹原生对话框，直接走 ReplayLibrary.Export。
                    var ok = false;
                    var exportErr = (string)null;
                    try { ok = ReplayLibrary.Export(probeMrepPath, ExportProbePath(), out exportErr); }
                    catch (Exception e) { exportErr = Short(e); }
                    report.Add("  导出机制 : " + (ok ? "已写出" : "失败") + " → " + ExportProbePath()
                        + (ok ? "" : "（" + (exportErr ?? "?") + "）"));
                    Check(ok, "导出真的能写出 .mrep（【更多】面板靠它）");
                    return;
                }

                if (endStage == 1)
                {
                    if (frames - endWaitFrame < EndPanelStepFrames)
                        return;
                    endStage = 2;
                    endWaitFrame = frames;
                    // 面板本体还在（输入仍然被拦）。
                    var info = ReplayEndPanel.Describe() ?? string.Empty;
                    report.Add("  导出后 : 面板开着 = " + ReplayEndPanel.IsOpen + "，描述=" + info);
                    Check(ReplayEndPanel.IsOpen, "导出（外部写入）后面板仍在（输入仍然被拦）");
                    return;
                }

                if (endStage == 2)
                {
                    if (frames - endWaitFrame < EndPanelStepFrames)
                        return;

                    // ── 第 1 轮：点「重新播放」──────────────────────────────
                    // 它走的正是菜单里那个「点了没反应」的 RestartCurrent()，
                    // 所以这一步同时覆盖了两个入口。
                    if (endPanelRuns <= 1)
                    {
                        endStage = 9;
                        endWaitFrame = frames;
                        endWaitFrameAt = DateTime.Now;
                        var ok = ReplayEndPanel.SelfTestChoose(0);
                        report.Add("  点「重新播放」 : " + (ok ? "已点击" : "面板没开着")
                            + "；播放器状态 = " + ReplayPlayer.Describe());
                        // 真人点击时 MainSceneUI 会先把框体收起来；我们直接调回调，得自己补上
                        // （否则那个「回放结束」框体会一直挂在新一场播放的关卡上）。
                        ReplayEndPanel.SelfTestHideDialog();
                        Check(ok, "结束面板的「重新播放」按钮能点");
                        Check(ReplayPlayer.IsActive,
                            "「重新播放」真的重开了一场（以前这里静默无效）");
                        // 下一场结束时改走「菜单 → 结束播放」，验证那条路也会弹结束面板。
                        menuDrivenEnd = true;
                        WriteReport();
                        EnterPhase(Phase.VerifyPlayback);
                        return;
                    }

                    // ── 第 2 轮：点「退出」────────────────────────────────
                    endStage = 3;
                    endWaitFrameAt = DateTime.Now;
                    var quit = ReplayEndPanel.SelfTestChoose(1);
                    report.Add("  点「退出」 : " + (quit ? "已点击" : "面板没开着")
                        + "；面板已收起 = " + !ReplayEndPanel.IsOpen);
                    // 真人点击时 MainSceneUI 会先收起框体再调回调；我们直接调回调，
                    // 所以要自己补上，否则那个框体会一直挂在屏幕上（自测假象）。
                    ReplayEndPanel.SelfTestHideDialog();
                    Check(quit, "结束面板的「退出」按钮能点");
                    Check(!ReplayEndPanel.IsOpen, "点「退出」后面板收起");
                    return;
                }

                if (endStage == 3)
                {
                    // 退出要花几秒：卸关卡 → 场景切换 → 回放页（同帧打开），再等对话框排完队。
                    if ((DateTime.Now - endWaitFrameAt).TotalSeconds <= 25)
                        return;
                    report.Add("  退出后落点 : 回放页开着 = " + ReplayPage.IsOpen
                        + "，待打开标记已消费 = " + !ReplayPlayer.WantsReplayPage
                        + "，页面=" + DescribePage(lastPageSeen));
                    Check(ReplayPage.IsOpen,
                        "「结束播放 / 退出」之后回到的是回放页，而不是地图");
                    // 截图必须在**换帧之后**再拍：ScreenCapture 是帧末才写盘的，
                    // 同帧紧接着 Close() 的话拍到的已经是「退回主菜单」之后那一帧了。
                    Shot("_rp_page.png");
                    endStage = 4;
                    endWaitFrameAt = DateTime.Now;
                    return;
                }

                if (endStage == 4)
                {
                    // 等截图落盘，再关回放页 —— 关掉之后应当直接回主菜单（以前这里是黑屏）。
                    if ((DateTime.Now - endWaitFrameAt).TotalSeconds <= 3)
                        return;
                    report.Add("  关回放页前的页面 = " + DescribePage(lastPageSeen));
                    ReplayPage.Close();
                    endStage = 5;
                    endWaitFrameAt = DateTime.Now;
                    return;
                }

                if (endStage == 5)
                {
                    if ((DateTime.Now - endWaitFrameAt).TotalSeconds <= 5)
                        return;
                    var page = DescribePage(lastPageSeen);
                    report.Add("  从回放页返回 : 页面=" + page + "（不应该是空/黑屏）");
                    Check(lastPageSeen >= MainmenuPage,
                        "从回放页返回后停在主页面（不是黑屏）");
                    Shot("_rp_mainmenu.png");

                    // 导入：原生「打开文件」对话框没法自动化，所以绕开它，
                    // 直接把一个外部 .mrep 喂给 ReplayLibrary.Import，验「复制进回放目录」这段。
                    report.Add("  导入测试 : " + ImportProbe());
                    Check(importProbeOk, "「导入」能把外部 .mrep 收进回放目录");

                    endStage = 6;
                    WriteReport();
                    EnterPhase(CfgBool(ReplayConfig.SelfTestAutoQuit, true) ? Phase.Quit : Phase.Done);
                }
            }
            catch (Exception e)
            {
                report.Add("  结束面板按钮实测出错: " + Short(e));
                endStage = 5;
                WriteReport();
                EnterPhase(CfgBool(ReplayConfig.SelfTestAutoQuit, true) ? Phase.Quit : Phase.Done);
            }
        }

        /// <summary>自测导出用的目标路径（放在游戏目录里，别往桌面扔东西）。</summary>
        private static string ExportProbePath()
        {
            return Path.Combine(ExportProbeDir(), "selftest_export.mrep");
        }

        private static string ExportProbeDir()
        {
            try
            {
                var root = Path.Combine(Paths.GameRootPath, "BepInEx", "_rp_export");
                Directory.CreateDirectory(root);
                return root;
            }
            catch (Exception)
            {
                return ".";
            }
        }

        /// <summary>
        /// 「导入」的可自动化部分：原生「打开文件」对话框弹不出来（系统级、阻塞式），
        /// 所以只把「选文件」那一步换掉 —— 把导出出来的那份当成玩家挑的文件，
        /// 走 <see cref="ReplayLibrary.Import"/> 收进回放目录，再回列表里找它。
        /// </summary>
        private static string ImportProbe()
        {
            importProbeOk = false;
            try
            {
                var staged = Path.Combine(ExportProbeDir(), "import_probe.mrep");
                if (!string.IsNullOrEmpty(probeMrepPath) && File.Exists(probeMrepPath))
                    File.Copy(probeMrepPath, staged, true);
                if (!File.Exists(staged))
                {
                    importProbeOk = true;   // 没有源文件，不算失败
                    return "跳过（没有可当源文件的回放）";
                }

                string err;
                if (!ReplayLibrary.Import(staged, out err))
                    return "失败：" + (err ?? "?");

                var all = ReplayLibrary.LoadAll();
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].Name != null
                        && all[i].Name.StartsWith("import_probe", StringComparison.OrdinalIgnoreCase))
                    {
                        importProbeOk = true;
                        return "成功 → " + all[i].Name + "（收件目录 " + ReplayConfig.ResolveOutputDir() + "）";
                    }
                }
                return "导入返回成功，但回放列表里找不到 import_probe";
            }
            catch (Exception e)
            {
                return "出错：" + Short(e);
            }
        }

        private static string probeMrepPath;
        private static bool importProbeOk;

        private static int endStage;
        private static int endWaitFrame;
        private static DateTime endWaitFrameAt;
        /// <summary>进入结束面板阶段的次数：第 1 轮点「重新播放」，第 2 轮点「退出」。</summary>
        private static int endPanelRuns;
        /// <summary>跳健康忠告页的次数（只在第 1 次写日志，免得刷屏）。</summary>
        private static int healthWarningSkips;

        private static void StepWaitMenu(MainSceneController scene, int page)
        {
            // 健康游戏忠告页（0.7.1 起才会真的出现）：它要玩家点一下「确定」，
            // 不跳过的话自测会一直卡在 WaitMenu（实测 15 秒后仍在 HealthWarning）。
            // 用游戏自己的 DisplayMainmenu() 走正规路径，不做操作系统级的鼠标注入。
            if (page == (int)MainScenePageType.HealthWarning)
            {
                if (healthWarningSkips++ == 0)
                    Note("停在健康忠告页，调用 DisplayMainmenu() 程序化跳过");
                try { scene.DisplayMainmenu(); }
                catch (Exception e) { Note("DisplayMainmenu 失败: " + Short(e)); }
                return;
            }

            // 标题界面在等玩家点「点击以开始」。用程序化的 DisplayMainmenu() 代替那一下点击
            // ——仍然是游戏自己的代码路径，不是操作系统级的鼠标注入。
            if (page == (int)MainScenePageType.Titlescreen)
            {
                Note("停在标题界面，调用 DisplayMainmenu() 程序化跳过");
                try { scene.DisplayMainmenu(); }
                catch (Exception e) { Note("DisplayMainmenu 失败: " + Short(e)); }
                return;
            }

            if (page >= MainmenuPage)
            {
                Note("主菜单就绪");
                if (playOnly)
                {
                    report.Add("");
                    report.Add("--- 回放播放验证（只播放模式） ---");
                    report.Add("  文件: " + playFileName);
                    try
                    {
                        report.Add(ReplayPlayer.Start(playFileName));
                    }
                    catch (Exception e)
                    {
                        report.Add("  启动播放失败: " + Short(e));
                        Check(false, "能启动播放");
                    }
                    WriteReport();
                    EnterPhase(Phase.VerifyPlayback);
                    return;
                }
                EnterPhase(Phase.GotoLevel);
                return;
            }

            var elapsed = (DateTime.Now - phaseStart).TotalSeconds;
            Heartbeat("等主菜单", elapsed, null);

            int timeout = CfgInt(ReplayConfig.SelfTestStartupTimeoutSec, 300);
            if (elapsed > timeout)
            {
                Fail("等待主菜单超时（" + timeout + " 秒）。当前页面 = " + DescribePage(page));
            }
        }

        private static void StepWaitLevel(MainManager main)
        {
            var ctrl = GetController();
            if (ctrl != null && IsStarted(ctrl))
            {
                Note("关卡已开始（等待 " + (int)(DateTime.Now - phaseStart).TotalSeconds + " 秒）"
                    + (forcedStart ? " [强制开局]" : ""));
                OnLevelEntered(ctrl);
                return;
            }

            var elapsed = (DateTime.Now - phaseStart).TotalSeconds;
            Heartbeat("等关卡", elapsed, ctrl);

            int retrySec = Math.Max(CfgInt(ReplayConfig.SelfTestRetrySec, 20), 5);
            int forceSec = Math.Max(CfgInt(ReplayConfig.SelfTestForceStartSec, 40), 5);
            int timeoutSec = CfgInt(ReplayConfig.SelfTestLevelTimeoutSec, 180);

            if (elapsed > timeoutSec)
            {
                Fail("等待关卡开始超时（" + timeoutSec + " 秒）。ctrl=" + (ctrl != null)
                    + " engine=" + (ctrl != null && TryEngine(ctrl) != null)
                    + " started=" + (ctrl != null && IsStarted(ctrl))
                    + " 页面=" + DescribePage(lastPageSeen)
                    + " gotolevel重试=" + retries + " 强制开局=" + forceAttempts);
                return;
            }

            // ① 还没有引擎：周期性地重新执行 /gotolevel
            if (ctrl == null || TryEngine(ctrl) == null)
            {
                if (elapsed >= retrySec * (retries + 1))
                {
                    retries++;
                    Note("第 " + retries + " 次重试：重新执行 /gotolevel（ctrl=" + (ctrl != null) + "）");

                    // 关卡存档的「mod 数据版本」对不上时，游戏只会弹一个不一致对话框，
                    // **引擎根本不会建出来** —— 日志里连一行报错都没有，
                    // 表面症状就是 engine=False 一直卡到超时。
                    // 自测专用开关：确认卡住之后就把该关卡的存档删掉再来一次。
                    if (CfgBool(ReplayConfig.SelfTestDeleteLevelState, false)
                        && !deletedLevelState && retries >= 2)
                        deletedLevelState = TryDeleteLevelState(main);

                    IssueGotoLevel(main);
                }
                return;
            }

            // ② 引擎在但一直没「开始」：多半卡在选卡面板 / 开场对话。
            //    注意：不要一看到引擎就立刻强制开局，先给游戏自己走流程的时间。
            if (forceAttempts < 3 && elapsed >= forceSec + forceAttempts * 20)
            {
                forceAttempts++;
                Note("第 " + forceAttempts + " 次强制开局");
                ForceStart(ctrl);
            }
        }

        /// <summary>
        /// 删掉自测关卡的存档（<c>[SelfTest] DeleteLevelState</c>）。
        ///
        /// <para>只在「关卡一直建不出引擎」时用：那通常是存档首行的 mod 数据版本与当前不一致，
        /// 而游戏对此**只会弹一个对话框、不写任何日志**，从外面看就是卡死。</para>
        ///
        /// <para>⚠️ 会丢该关卡进行中的进度。</para>
        /// </summary>
        private static bool TryDeleteLevelState(MainManager main)
        {
            try
            {
                if (main == null)
                    return false;
                var name = CfgString(ReplayConfig.SelfTestStage, "mvz2:halloween_2");
                var stage = PVZEngine.NamespaceID.Parse(name, "mvz2");
                if (!PVZEngine.NamespaceID.IsValid(stage))
                {
                    Note("删关卡存档失败：关卡 ID 解析不了（" + name + "）");
                    return false;
                }
                main.LevelManager.RemoveLevelState(stage);
                Note("已删掉关卡存档 " + name + "（存档版本对不上时关卡根本不会开始）");
                return true;
            }
            catch (Exception e)
            {
                Note("删关卡存档失败：" + Short(e));
                return false;
            }
        }

        /// <summary>周期性把进度写进报告，用于定位「卡在哪一步」。</summary>
        private static void Heartbeat(string what, double elapsed, LevelController ctrl)
        {
            int interval = Math.Max(CfgInt(ReplayConfig.SelfTestHeartbeatSec, 5), 1);
            if ((DateTime.Now - lastHeartbeatAt).TotalSeconds < interval)
                return;
            lastHeartbeatAt = DateTime.Now;
            // 播放阶段把「播到哪一 tick 了」也打出来：这样即使播放中途「不动了」，
            // 只看日志就能判断是卡住了（tick 不涨）还是正常在放（tick 在涨）。
            var play = ReplayPlayer.IsActive
                ? " 播放tick=" + ReplayPlayer.CurrentTick + " 已注=" + ReplayPlayer.InjectedEventCount
                : "";
            Note("…" + what + " " + (int)elapsed + "s"
                + " 页面=" + DescribePage(lastPageSeen)
                + " ctrl=" + (ctrl != null)
                + " engine=" + (ctrl != null && TryEngine(ctrl) != null)
                + " started=" + (ctrl != null && IsStarted(ctrl))
                + " gotolevel重试=" + retries
                + " 强制开局=" + forceAttempts
                + play);
        }

        private static void ForceStart(LevelController ctrl)
        {
            try
            {
                var choose = ctrl.BlueprintChoosePart;
                if (choose != null)
                {
                    Note("  调用 BlueprintChoosePart.ApplyChoose()");
                    choose.ApplyChoose();
                }
            }
            catch (Exception e)
            {
                Note("  ApplyChoose 失败: " + Short(e));
            }

            try
            {
                var mi = AccessTools.Method(typeof(LevelController), "GameStartInstantTransition");
                if (mi != null)
                {
                    Note("  调用 GameStartInstantTransition()（私有，反射）");
                    mi.Invoke(ctrl, null);
                    forcedStart = true;
                    return;
                }
                Note("  找不到 GameStartInstantTransition，退回 StartGame()");
            }
            catch (Exception e)
            {
                Note("  GameStartInstantTransition 失败: " + Short(e));
            }

            try
            {
                Note("  调用 StartGame()");
                ctrl.StartGame();
                forcedStart = true;
            }
            catch (Exception e)
            {
                Note("  StartGame 失败: " + Short(e));
            }
        }

        private static void IssueGotoLevel(MainManager main)
        {
            var stage = CfgString(ReplayConfig.SelfTestStage, "mvz2:halloween_1");
            var area = CfgString(ReplayConfig.SelfTestArea, "mvz2:halloween");
            var cmd = "/gotolevel " + stage + " " + area;
            Note("执行指令: " + cmd);
            try { main.DebugManager.ExecuteCommand(cmd, 1); }
            catch (Exception e) { Note("ExecuteCommand 抛异常: " + Short(e)); }
        }
        #endregion

        #region 各阶段实现
        /// <summary>
        /// 播放验收的视觉采样：跑到指定 tick 时抓两张相隔 15 tick 的截图。
        ///
        /// <para><b>为什么需要它：</b>指纹能证明「状态一致」，但证明不了「画得对」——
        /// 假鼠标的位置/尺寸/层级只能靠看图。两张图相隔 15 tick 是为了看出
        /// **它到底动没动**。</para>
        ///
        /// <para><c>ScreenCapture.CaptureScreenshot</c> 在**帧末**写盘，所以两张图之间
        /// 隔 15 tick 而不是同一帧连拍。</para>
        /// </summary>
        private static void PlayShotCheck()
        {
            try
            {
                int at = CfgInt(ReplayConfig.SelfTestPlayShotTick, 0);
                if (at <= 0 || shotState >= 2)
                    return;
                int tick = ReplayPlayer.CurrentTick;
                if (shotState == 0)
                {
                    if (tick < at)
                        return;
                    shotState = 1;
                    shotNext = tick + 15;
                    Shot("_rp_play0.png");
                    return;
                }
                if (tick < shotNext)
                    return;
                shotState = 2;
                Shot("_rp_play1.png");
            }
            catch (Exception e)
            {
                Note("播放截图出错: " + Short(e));
                shotState = 2;
            }
        }

        private static void Shot(string file)
        {
            try
            {
                var root = Paths.GameRootPath;
                var path = Path.Combine(root, "BepInEx", file);
                ScreenCapture.CaptureScreenshot(path);
                report.Add("  截图 " + file + " @tick " + ReplayPlayer.CurrentTick);
                WriteReport();
            }
            catch (Exception e)
            {
                report.Add("  截图 " + file + " 失败: " + Short(e));
            }
        }

        // ------------------------------------------------------------------
        // 播放中途的「菜单文案」验收
        // ------------------------------------------------------------------

        private static int menuStage;      // 0 未做 1 已开 2 已关
        private static int menuWaitFrame;
        /// <summary>当前这一关的 LevelController 实例；变了说明关卡场景被重载过。</summary>
        private static int menuCtrlId;
        private static int menuStableFrames;
        /// <summary>下一场播放结束时改走「菜单 → 结束播放」那条路（见 <see cref="DriveMenuEndPlay"/>）。</summary>
        private static bool menuDrivenEnd;

        /// <summary>
        /// 在播放中途把关卡内的选项菜单打开一次，记录「重新播放 / 结束播放」两个按钮的文案。
        ///
        /// <para><b>为什么必须实测：</b>这两个文案是我们用 Harmony 后缀**覆盖**原版写进去的，
        /// 而原版每次刷新页面都会重写一遍 —— 「代码看起来对」完全不能说明最终显示的是哪个。</para>
        /// </summary>
        private static void PlayMenuAudit()
        {
            try
            {
                if (menuStage >= 2)
                    return;
                if (menuStage == 0)
                {
                    var ctrl = GetController();
                    if (ctrl == null)
                        return;

                    // ⚠ 回放播放期间游戏会**重载一次关卡场景**（新 LevelController + 新 LevelUI）。
                    // 如果在这之前就把菜单打开，3 帧后场景一换，菜单连同控制器一起被丢掉，
                    // 看起来就像「刚打开就被关掉」。所以先等控制器实例稳定若干帧再动手。
                    int id = ctrl.GetInstanceID();
                    if (id != menuCtrlId)
                    {
                        menuCtrlId = id;
                        menuStableFrames = 0;
                        return;
                    }
                    menuStableFrames++;
                    if (menuStableFrames < 20)
                        return;

                    int at = CfgInt(ReplayConfig.SelfTestPlayMenuTick, 0);
                    if (at <= 0 || ReplayPlayer.CurrentTick < at)
                        return;

                    // 直接调 ShowOptionsDialog —— 走 OnOptionsKey 的话会被
                    // `isGameOver || !isGameStarted || levelLoaded` 提前 return 掉（自测时
                    // 关卡是「重放加载」的状态，这几个标志位不一定是正常值）。
                    var m = AccessTools.Method(typeof(LevelController), "ShowOptionsDialog", Type.EmptyTypes);
                    if (m == null)
                    {
                        report.Add("  菜单验收: 找不到 ShowOptionsDialog，跳过");
                        menuStage = 2;
                        return;
                    }
                    m.Invoke(ctrl, null);
                    menuStage = 1;
                    menuWaitFrame = frames;
                    return;
                }

                // 已经打开，等布局/文案都刷过几帧再读。
                // （暂停时逻辑 tick 不动，所以这里按**帧**等，不能按 tick。）
                if (frames - menuWaitFrame < 25)
                    return;

                report.Add("--- 回放中的关卡菜单 ---");
                DumpOptionsTexts();
                Shot("_rp_menu.png");
                menuStage = 2;

                // 第 1 轮只验证「菜单能收起来」——收完之后继续播到自然结束。
                // 第 2 轮验证「菜单 → 结束播放」这条真正的用户路径（应当弹结束面板 + 收起菜单）。
                if (menuDrivenEnd)
                {
                    DriveMenuEndPlay();
                    return;
                }
                try
                {
                    // 用生产代码里的那个收尾（解暂停 + 收框体），别自己拼 ——
                    // 光 Close() 不会把框体收起来（见 CloseLevelOptionsMenu 的注释）。
                    ReplayPatches.CloseLevelOptionsMenu();
                    report.Add("  收起菜单 : " + DescribeOptionsMenu());
                    Check(MenuClosed(), "菜单面板能被收起来（收完不会留在屏幕上）");
                }
                catch (Exception e)
                {
                    report.Add("  关菜单失败: " + Short(e));
                }
            }
            catch (Exception e)
            {
                report.Add("  菜单验收出错: " + Short(e));
                menuStage = 2;
            }
        }

        /// <summary>
        /// 「菜单 → 结束播放」那条路：拦下按钮 → 确认框点「是」→ 应当**弹出结束面板**且**菜单收起**。
        ///
        /// <para>走的是与真实点击完全相同的两个回调（<c>OptionsMainButtonPrefix</c> +
        /// 确认框的 <c>pinnedConfirm</c>），所以这条验收是真的把用户路径跑了一遍，
        /// 不是只看代码。</para>
        /// </summary>
        private static void DriveMenuEndPlay()
        {
            menuDrivenEnd = false;
            try
            {
                var intercepted = !ReplayPatches.OptionsMainButtonPrefix(
                    MVZ2.UI.OptionsDialogMainPage.ButtonType.LeaveLevel);
                report.Add("  点菜单「结束播放」 : " + (intercepted ? "已被回放接管" : "**没被接管**"));
                Check(intercepted, "菜单里的「结束播放」被回放接管");

                var answered = ReplayPatches.SelfTestConfirm(true);
                report.Add("  确认框点「是」 : " + (answered ? "已确认" : "确认框没弹出来"));
                Check(answered, "「结束播放」会先弹一个确认框");

                report.Add("  结束后 : 结束面板=" + ReplayEndPanel.Describe()
                    + "，菜单 " + DescribeOptionsMenu());
                Check(ReplayEndPanel.IsOpen, "菜单里结束播放弹出了回放结束面板");
                Check(MenuClosed(), "菜单里结束播放会同时把菜单面板收起来");
                Shot("_rp_menuend.png");
            }
            catch (Exception e)
            {
                report.Add("  菜单结束播放验收出错: " + Short(e));
            }
        }

        /// <summary>关卡菜单现在收干净了没有（控制器状态 + 节点可见性都得是关）。</summary>
        private static bool MenuClosed()
        {
            try
            {
                var ctrl = GetController();
                if (ctrl == null)
                    return true;
                var opts = ctrl.optionsDialogController;
                if (opts == null || opts.Pointer == IntPtr.Zero)
                    return true;
                return !opts.IsOpen() && !opts.gameObject.activeSelf;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static string DescribeOptionsMenu()
        {
            try
            {
                var ctrl = GetController();
                var opts = ctrl != null ? ctrl.optionsDialogController : null;
                if (opts == null || opts.Pointer == IntPtr.Zero)
                    return "拿不到控制器";
                return "打开=" + opts.IsOpen() + " 节点可见=" + opts.gameObject.activeSelf;
            }
            catch (Exception e)
            {
                return "读取失败 " + Short(e);
            }
        }

        /// <summary>
        /// 把菜单里所有文本打出来。
        ///
        /// <para>注意<b>一定要用关卡自己那个 OptionsDialogController</b>
        /// （<c>LevelController.optionsDialogController</c>）。场景里同时存在主菜单那一套，
        /// 用 <c>UiFinder.FindOne&lt;OptionsDialogController&gt;()</c> 会随机拿到主菜单的，
        /// 那个的 <c>ui.Main</c> 挂在未激活的节点下，读出来永远是原版文案。</para>
        ///
        /// <para>也不按枚举去查那两个按钮对象（原版 <c>textButtonDict</c> 是私有字段，
        /// interop 程序集里查不到），直接把整棵树上的 TMP 文本列出来 —— 这一页本来也没几个字。</para>
        /// </summary>
        private static void DumpOptionsTexts()
        {
            try
            {
                var ctrl = GetController();
                if (ctrl == null)
                {
                    report.Add("  拿不到 LevelController");
                    return;
                }

                var mine = ctrl.optionsDialogController;
                if (mine == null)
                {
                    report.Add("  拿不到关卡的设置界面控制器");
                    return;
                }
                var page = mine.ui != null ? mine.ui.Main : null;
                if (page == null)
                {
                    report.Add("  拿不到设置界面主页");
                    return;
                }
                var go = page.gameObject;
                report.Add("  菜单已打开 : " + mine.IsOpen()
                    + "，主页可见 = " + go.activeInHierarchy);

                var texts = go.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true);
                int n = 0;
                var sb = new StringBuilder();
                for (int i = 0; i < texts.Length; i++)
                {
                    var t = texts[i];
                    if (t == null || t.Pointer == IntPtr.Zero)
                        continue;
                    var s = t.text;
                    if (string.IsNullOrEmpty(s))
                        continue;
                    if (n > 0)
                        sb.Append(" / ");
                    sb.Append('「').Append(s.Replace('\n', ' ')).Append('」');
                    n++;
                    if (n >= 16)
                        break;
                }
                report.Add("  菜单文本 : " + (n == 0 ? "（一个都没有）" : sb.ToString()));
            }
            catch (Exception e)
            {
                report.Add("  读菜单文案失败: " + Short(e));
            }
        }

        private static void EnterPhase(Phase next)
        {
            phase = next;
            phaseStart = DateTime.Now;
            frames = 0;
            lastHeartbeatAt = DateTime.MinValue;

            // Actions / CoolDown 的间隔按**逻辑 tick** 计（不是帧）。
            // 帧率一变，同样一个「30」在低帧率下能顶几百 tick、在高帧率下只顶几十 tick，
            // 录制长度就跟着飘 —— 曾因此在快机器上录不到指纹间隔（150 tick）而误报失败。
            // tick 只跟游戏里的时间走，与帧率无关。
            if (next == Phase.Actions || next == Phase.CoolDown)
                phaseTick = Ticks(GetController());

            if (next == Phase.VerifyPlayback)
            {
                // 重置「回放停滞」检测，否则会带着上一个阶段的旧时间戳。
                lastPlayTick = -1;
                lastPlayTickAt = DateTime.Now;
                shotState = 0;
                shotNext = 0;
                menuStage = 0;
                menuWaitFrame = 0;
                menuCtrlId = 0;
                menuStableFrames = 0;
            }
            if (next == Phase.EndPanel)
            {
                // frames 在换阶段时归零，所以这两个也得跟着归零，否则差值恒为负、永远等不到。
                endStage = 0;
                endWaitFrame = 0;
                // 第 1 轮（自然播完）走「重新播放」，把下一场交给「菜单 → 结束播放」那条路。
                endPanelRuns++;
            }
        }

        private static void OnLevelEntered(LevelController ctrl)
        {
            // v9：先趁着**探针还没打开**问一次「这一关需不需要指针轨迹」。
            // 自测关卡里没有任何会读实时指针的内容，所以答案必须是 false——
            // 否则「按需录制」这条省体积的设计就没生效（普通关卡会白白多记几千条）。
            if (!probeEvaluated)
            {
                var lvl0 = EngineOf(ctrl);
                if (lvl0 != null)
                {
                    probeEvaluated = true;
                    needPointerWithoutProbe = ReplayCompat.LevelNeedsPointer(lvl0);
                    Note("探针前判定：这一关需要指针轨迹 = " + needPointerWithoutProbe + "（预期 false）");
                }
            }
            // 之后把探针打开，让采样真的开始 —— 这样才能验完整条管道。
            trajectoryProbe = true;

            if (!ReplayRecorder.IsArmed)
            {
                Note("录制未自动开始，手动触发 BeginManual");
                ReplayRecorder.BeginManual("selftest", ctrl);
            }
            if (!ReplayRecorder.IsArmed)
            {
                Fail("无法开始录制（BeginManual 返回 false）");
                return;
            }
            armedAtTick = Ticks(ctrl);
            Note("录制已开始: " + ReplayRecorder.Describe() + " startTick=" + armedAtTick);
            EnterPhase(Phase.Warmup);
        }

        #region v9 兼容性探针
        /// <summary>自测探针开关（见 <see cref="PointerProbeNeed"/>）。</summary>
        private static bool trajectoryProbe;
        /// <summary>「探针生效之前」这一关需不需要指针轨迹（预期 false）。</summary>
        private static bool needPointerWithoutProbe;
        private static bool probeEvaluated;
        private static bool probeRegistered;

        /// <summary>拿关卡引擎（拿不到返回 null）。</summary>
        private static LevelEngine EngineOf(LevelController ctrl)
        {
            try { return ctrl != null ? ctrl.GetEngine() : null; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 给 <see cref="ReplayCompat"/> 注册的自测判定。
        ///
        /// <para>自测关卡（<c>mvz2:halloween_2</c>）本身没有任何会读实时指针的内容，
        /// 所以「按实体生效」的按需录制在这里默认**不会**开。为了能验证整条管道
        /// （判定 → 采样 → 写文件 → 回放读取），自测用一个可开关的判定自己把开关打开；
        /// 关掉时它返回 false，用来验证「不需要的关卡一条都不多写」。</para>
        /// </summary>
        private static bool PointerProbeNeed(LevelEngine level) => trajectoryProbe;

        private static void EnsureProbeRegistered()
        {
            if (probeRegistered)
                return;
            probeRegistered = true;
            ReplayCompat.RegisterPointerConsumer(PointerProbeNeed, "自测探针（自测关掉时返回 false）");
        }

        /// <summary>
        /// v9 新机制的检查。分两类：
        /// <list type="bullet">
        /// <item><b>纯逻辑</b>（不碰 UI）：掩码→相位的边沿推导、选项列表变化时的下标找回。
        /// 这两条错了的表现非常隐蔽（重装兵器不开火、上锁的箱子选错项）。</item>
        /// <item><b>管道</b>：补丁真的都挂上了、按需录制真的生效、轨迹真的被写进文件。</item>
        /// </list>
        ///
        /// <para>⚠️ <b>覆盖不到的部分：</b>打砖块/重装兵器/矿车/碾压墙/梦魇与上锁的箱子
        /// 都在别的关卡里，自测关没有这些实体，所以「挡板跟着轨迹走」这类**效果**
        /// 要靠另录一局亲自验。这里只能保证管道是通的。</para>
        /// </summary>
        private static void CompatCheck()
        {
            report.Add("--- 回放的输入兼容（v9：逐 tick 指针轨迹 / 选项对话框）---");

            report.Add("  补丁安装  : " + ReplayHooks.PatchStatus);
            Check(!ReplayHooks.HasPatchProblems,
                "所有补丁都挂上了、且形参都绑得上（Harmony 挂失败与形参绑不上都不抛异常，只能这样查）");

            var consumers = ReplayCompat.DescribeConsumers();
            report.Add("  指针消费者: " + consumers.Count + " 个。" + ReplayCompat.Describe());
            Check(consumers.Count >= 5,
                "原版的 4 个「会读实时指针」的内容 + 自测探针都已注册");            if (probeEvaluated)
            {
                Check(!needPointerWithoutProbe,
                    "按需录制：探针关掉时，这一关（没有任何指针消费者）判定为**不需要**轨迹");
            }
            else
            {
                report.Add("  （只播放模式，没进关卡，按需录制的判定未验证）");
            }

            string detail;
            var phaseOk = ReplayPlayer.SelfCheckPointerPhases(out detail);
            report.Add("  按键相位  : " + detail);
            Check(phaseOk, "掩码→相位（按下/保持/抬起）的边沿推导正确");

            // ---- 关卡内的减速按钮（ReplaySlowDown）----
            // 只验「建出来了 + 开关真的改变倍率 + 倍率是加速倍率的倒数」；
            // 画面对不对（图标镜像、位置在加速按钮正下方）要看 GUI 自测的截图。
            var ffm = ReplaySlowDown.FastForwardMultiplier;
            report.Add("  减速按钮  : 已建立=" + ReplaySlowDown.ButtonReady
                + "；图标已镜像=" + ReplaySlowDown.IconMirrored
                + "；热键已清空=" + ReplaySlowDown.HotkeyCleared
                + "；加速倍率=" + ffm.ToString("0.###")
                + "；减速倍率=" + ReplaySlowDown.Multiplier.ToString("0.###")
                + "；" + ReplaySlowDown.LastInfo);
            // 位置对不对只能看数字：按钮应当紧贴在加速按钮**正下方**（x 相同、y 更低、
            // 且不重叠）。截图只能证明"画出来了"，证明不了这两个数字关系。
            report.Add("  减速位置  : " + ReplaySlowDown.DescribeLayout());
            // ⚠️ 这三条只在**功能真的开着**的时候才有意义：`[General] SlowDownButton = false`
            // 时按钮就是故意不建的，再判它「没建出来」是自测在制造假失败
            // （实测：整套自测里那 6 个 FAIL 全是这三条 × 播放两轮）。
            if (!ReplayConfig.SlowDownButtonOn)
            {
                report.Add("  [跳过] 减速按钮的三项检查："
                    + "[General] SlowDownButton = false（配置里就是关的）");
            }
            else
            {
                Check(ReplaySlowDown.ButtonReady, "关卡内建出了减速按钮（应在加速按钮正下方）");
                Check(ReplaySlowDown.PlacedCorrectly,
                    "减速按钮真的在加速按钮正下方（x 对齐、y 更低、不重叠）");
                Check(ReplaySlowDown.IconMirrored, "减速按钮的图标找到了并做了镜像（找不到也不影响用，但说明 prefab 结构变了）");
            }
            Check(Mathf.Abs(ReplaySlowDown.Multiplier * ffm - 1f) < 0.001f,
                "减速倍率是加速倍率的倒数");

            ReplaySlowDown.Set(true);
            var slowOn = ReplaySlowDown.Enabled && ReplaySlowDown.Multiplier < 0.999f;
            ReplaySlowDown.Set(false);
            var slowOff = !ReplaySlowDown.Enabled;
            report.Add("  减速开关  : 开启生效=" + slowOn + "；关闭生效=" + slowOff);
            Check(slowOn && slowOff, "减速开关能开能关（且不注册热键）");

            // ⚠️ 这一条是「tick T 用第 T 条采样」的唯一自动化守门员。
            // 差一个 tick 的表现是重装兵器多发一颗子弹、之后整局错位 —— 光看代码看不出来。
            var trackOk = ReplayPointerTrajectory.SelfCheck(out detail);
            report.Add("  轨迹对应  : " + detail);
            Check(trackOk, "逐 tick 轨迹的「tick → 采样」对应关系与边沿推导正确（不允许差一个 tick）");

            var dlgOk = ReplayDialogs.SelfCheck(out detail);
            report.Add("  对话框下标: " + detail);
            Check(dlgOk, "选项列表与录制时不同时（例如玩家钱不够），仍能按文本找回正确的选项");
            Check(ReplayDialogs.ClickPathStatus.IndexOf("❌", StringComparison.Ordinal) < 0,
                "自动应答对话框所需的反射目标解析到了");

            var f = ReplayPlayer.CurrentFile;
            if (f != null)
            {
                report.Add("  轨迹/对话 : " + ReplayPointerTrajectory.DescribePlayback());
                report.Add("  文件内容  : 指针采样=" + f.PointerSamples.Count
                    + " 对话框=" + f.Dialogs.Count
                    + " 起始金币=" + (f.HasStartMoney ? f.StartMoney.ToString() : "无（旧文件）"));
                // 这一条验的是**录制**侧（探针是否把轨迹写进文件）。
                // 只播放模式不录制，本来就不该有采样 —— 判它「没采到」是自测在制造假失败。
                if (playOnly)
                    report.Add("  [跳过] 指针采样检查：只播放模式不录制（录制版的这条由录制流程自己验）");
                else
                    Check(f.PointerSamples.Count > 0,
                        "探针打开后，逐 tick 指针轨迹真的被采到并写进文件了（按需录制生效）");
                Check(f.PointerSamples.Count == 0 || f.PointerSamples[0].Tick <= f.LastTick,
                    "轨迹的 tick 都落在回放区间内");
            }

            report.Add("  说明      : 本自测关卡里没有打砖块/重装兵器/矿车/碾压墙/选项对话框，"
                + "所以只验「管道通不通」；那些关卡的**实际效果**要另录一局验证。");
        }
        #endregion

        /// <summary>注入一个真实输入入口（会经过我们的 Harmony 补丁）。</summary>
        private static void Inject(LevelController ctrl, string item)
        {
            switch (item)
            {
                case "bpkey0":
                    Invoke(ctrl, "OnBlueprintKey", new[] { typeof(int), typeof(bool), typeof(int) }, 0, false, 0x31);
                    break;
                case "bpkey1":
                    Invoke(ctrl, "OnBlueprintKey", new[] { typeof(int), typeof(bool), typeof(int) }, 1, false, 0x32);
                    break;
                case "pickaxe":
                    Invoke(ctrl, "ClickPickaxe", Type.EmptyTypes);
                    break;
                case "starshard":
                    Invoke(ctrl, "ClickStarshard", Type.EmptyTypes);
                    break;
                case "trigger":
                    Invoke(ctrl, "ClickTrigger", Type.EmptyTypes);
                    break;
                default:
                    Note("跳过未知动作: " + item);
                    break;
            }
        }

        private static void Invoke(LevelController ctrl, string method, Type[] types, params object[] args)
        {
            try
            {
                var mi = AccessTools.Method(typeof(LevelController), method, types);
                if (mi == null)
                {
                    Note("找不到注入目标: " + method);
                    return;
                }
                mi.Invoke(ctrl, args);
            }
            catch (Exception e)
            {
                Note("注入 " + method + " 失败: " + Short(e));
            }
        }

        private static string StopAndVerify()
        {
            Note("停止录制");
            ReplayRecorder.StopManual();

            var path = ReplayRecorder.LastWrittenPath;
            report.Add("");
            report.Add("--- 动作注入结果 ---");
            if (actionLog.Count == 0)
                report.Add("  (无)");
            else
                report.AddRange(actionLog);

            report.Add("");
            report.Add("--- 输出文件 ---");
            if (string.IsNullOrEmpty(path))
            {
                report.Add("  未产生文件（没有任何事件被记录）");
                Check(false, "没有写出 .mrep 文件");
                return null;
            }

            report.Add("  path : " + path);
            if (!File.Exists(path))
            {
                report.Add("  文件不存在！");
                Check(false, "写出的文件找不到");
                return null;
            }
            var fi = new FileInfo(path);
            report.Add("  size : " + fi.Length + " bytes");
            Check(true, "写出了回放文件");

            string text;
            try
            {
                text = ReadGzip(path);
            }
            catch (Exception e)
            {
                report.Add("  解压失败: " + Short(e));
                Check(false, "gzip 解压失败");
                return null;
            }

            var nonEmpty = new List<string>();
            foreach (var l in text.Split('\n'))
            {
                var t = l.Trim();
                if (t.Length > 0)
                    nonEmpty.Add(t);
            }
            report.Add("  lines: " + nonEmpty.Count);
            Check(nonEmpty.Count >= 3, "至少含 header / keyframe / 若干事件");

            var header = nonEmpty.Count > 0 ? nonEmpty[0] : "";
            report.Add("  header: " + Truncate(header, 400));
            Check(header.Contains("\"magic\":\"mrep\""), "header 含 magic=mrep");
            Check(header.Contains("\"tps\":30"), "header 含 tps=30");
            Check(header.Contains("\"hasKeyframe\":true"), "关键帧抓取成功");

            // ---- 协议（v10）：版本、环境、录制参数自述 ----
            //
            // 这一段是**协议能不能被别的工具读**的守门人：只要这些字段是齐的、
            // 且计数与文件内容对得上，外部工具就能只靠 header + env 行解析一份回放。
            Check(header.Contains("\"spec\":" + ReplayProtocol.SpecVersion), "header 含 spec 且等于当前规范版本");
            Check(header.Contains("\"ver\":" + ReplayProtocol.FormatVersion), "header 含 ver 且等于当前格式版本");
            Check(header.Contains("\"caps\":\""), "header 含 caps");
            Check(header.Contains("\"samplers\":\""), "header 含 samplers");
            Check(header.Contains("\"fpt\":"), "header 含 fpt（录制时实际用的指纹间隔）");
            Check(header.Contains("\"envHash\":\"") && !header.Contains("\"envHash\":\"\""), "header 含非空环境哈希");

            // `debug` 必须与 **配置**一致（自测会在两种模式下都跑，所以不能硬写成 false）。
            bool debugCfg = CfgBool(ReplayConfig.DebugMode, false);
            Check(header.Contains("\"debug\":" + Json.Bool(debugCfg)),
                "header 的 debug 与 [Debug] Enabled（" + debugCfg + "）一致");
            if (debugCfg)
            {
                // 调试模式的三条硬性表现：逐 tick、声明 dbg 能力、跑全部采样器。
                Check(header.Contains("\"fpt\":1"), "调试模式下 fpt=1（逐 tick 采样）");
                Check(header.Contains("dbg"), "调试模式下 caps/samplers 里带 dbg");
                Check(header.Contains("dbg.rngRaw") && header.Contains("dbg.entities")
                    && header.Contains("dbg.level"), "调试模式下全部调试采样器都在 samplers 里");
            }

            // 第 2 行必须是 env 摘要；keyframe 必须紧跟 env 块之后（协议 §2.1）。
            var envSummary = nonEmpty.Count > 1 ? nonEmpty[1] : "";
            report.Add("  env 摘要: " + Truncate(envSummary, 300));
            Check(envSummary.Contains("\"k\":\"env\"") && envSummary.Contains("\"t\":\"summary\""),
                "第 2 行是 env 摘要（协议规定 env 必须在 keyframe 之前）");

            int envEntries = 0, keyframeIndex = -1;
            for (int i = 0; i < nonEmpty.Count; i++)
            {
                if (nonEmpty[i].Contains("\"k\":\"keyframe\""))
                {
                    keyframeIndex = i;
                    break;
                }
                if (nonEmpty[i].Contains("\"k\":\"env\"") && nonEmpty[i].Contains("\"kind\":\""))
                    envEntries++;
            }
            var keyframe = keyframeIndex >= 0 ? nonEmpty[keyframeIndex] : "";
            report.Add("  keyframe 行号: " + (keyframeIndex + 1) + "（env 条目 " + envEntries + " 行）");
            Check(keyframeIndex > 0, "文件里有 keyframe 行");
            Check(keyframe.Contains("\"state\":\"{"), "关键帧带状态 BSON");
            Check(envEntries >= 3, "env 块至少有 3 个条目（本体 + 运行环境）");
            Check(header.Contains("\"envCount\":" + envEntries),
                "header 的 envCount 与实际的 env 条目行数一致");

            int badLines = 0, events = 0, fingerprints = 0;
            var kinds = new Dictionary<string, int>();
            var eventSamples = new List<string>();
            // 从第 2 行开始：第 0 行是 header（没有 "k"），第 1 行是 keyframe。
            for (int i = 1; i < nonEmpty.Count; i++)
            {
                var l = nonEmpty[i];
                if (!l.StartsWith("{") || !l.EndsWith("}"))
                {
                    badLines++;
                    if (badLines <= 3)
                        report.Add("  ! 形状可疑: " + Truncate(l, 200));
                    continue;
                }
                var m = Regex.Match(l, "\"k\"\\s*:\\s*\"([a-z]+)\"");
                if (!m.Success)
                {
                    badLines++;
                    if (badLines <= 3)
                        report.Add("  ! 缺 k 字段: " + Truncate(l, 200));
                    continue;
                }
                var k = m.Groups[1].Value;
                if (!kinds.ContainsKey(k))
                    kinds[k] = 0;
                kinds[k]++;
                if (k == "fp")
                {
                    fingerprints++;
                }
                else if (k == "keyframe" || k == ReplayProtocol.KindEnv
                    || k == ReplayProtocol.KindPointer || k == ReplayProtocol.KindDialog)
                {
                    // 不是动作记录：keyframe / env / ptr / dlg 各有自己的计数项。
                }
                else
                {
                    events++;
                    if (eventSamples.Count < MaxEventsListed)
                        eventSamples.Add("  " + l);
                }
            }
            report.Add("  events      : " + events);
            report.Add("  fingerprints: " + fingerprints);
            report.Add("  kinds       : " + DescribeKinds(kinds));
            report.Add("  badLines    : " + badLines);
            Check(badLines == 0, "所有行都是单行 JSON 对象");
            Check(events >= 1, "至少记录到 1 个动作事件");
            Check(fingerprints >= 1, "至少采样到 1 条状态指纹");

            // 计数一致性（协议 §3.4 要求 header 的计数与实际行数相符）。
            int ptrLines = kinds.ContainsKey(ReplayProtocol.KindPointer) ? kinds[ReplayProtocol.KindPointer] : 0;
            int dlgLines = kinds.ContainsKey(ReplayProtocol.KindDialog) ? kinds[ReplayProtocol.KindDialog] : 0;
            {
                int evHdr = ReadHeaderInt(header, "eventCount");
                int fpHdr = ReadHeaderInt(header, "fingerprintCount");
                int ptrHdr = ReadHeaderInt(header, "ptrCount");
                int dlgHdr = ReadHeaderInt(header, "dlgCount");
                report.Add("  header 计数 : ev=" + evHdr + " fp=" + fpHdr + " ptr=" + ptrHdr + " dlg=" + dlgHdr);
                Check(evHdr == events, "header 的 eventCount 与实际事件行数一致");
                Check(fpHdr == fingerprints, "header 的 fingerprintCount 与实际指纹行数一致");
                Check(ptrHdr == ptrLines, "header 的 ptrCount 与实际 ptr 行数一致");
                Check(dlgHdr == dlgLines, "header 的 dlgCount 与实际 dlg 行数一致");
            }

            report.Add("");
            report.Add("--- 事件样本 ---");
            if (eventSamples.Count == 0)
                report.Add("  (无)");
            else
                report.AddRange(eventSamples);

            report.Add("");
            report.Add("--- 指纹样本 ---");
            int shown = 0;
            foreach (var l in nonEmpty)
            {
                if (l.Contains("\"k\":\"fp\""))
                {
                    report.Add("  " + l);
                    if (++shown >= 3)
                        break;
                }
            }
            if (shown == 0)
                report.Add("  (无)");
            return path;
        }

        private static void Check(bool ok, string what)
        {
            report.Add("  [" + (ok ? "OK  " : "FAIL") + "] " + what);
            if (!ok)
                verdictFail++;
        }
        #endregion

        #region 报告
        private static void Note(string msg)
        {
            report.Add("[f" + frames + "] " + msg);
            Plugin.Log?.LogInfo("[SelfTest] " + msg);
            WriteReport();
        }

        private static void Fail(string reason)
        {
            failReason = reason;
            report.Add("");
            report.Add("!!!! FAIL: " + reason);
            Note("FAIL: " + reason);
            EnterPhase(CfgBool(ReplayConfig.SelfTestAutoQuit, true) ? Phase.Quit : Phase.Done);
        }

        /// <summary>
        /// 写报告。
        ///
        /// ⚠️ 踩过的坑：如果外部（编辑器/文本查看器）正把这份报告打开着，
        /// 它的句柄可能不允许写入，于是 <c>FileMode.Create</c> 会抛 IOException。
        /// 之前这个异常被空 catch 吞掉 —— 症状是「日志里什么都有，报告却停在开头」，
        /// 极难排查。所以现在：重试 + 失败一定记日志。
        /// </summary>
        private static void WriteReport()
        {
            var path = Path.Combine(Paths.GameRootPath, "BepInEx", ReportFileName);
            var text = BuildReportText();
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write,
                               FileShare.ReadWrite | FileShare.Delete))
                    using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        sw.Write(text);
                    }
                    return;
                }
                catch (Exception e)
                {
                    last = e;
                    System.Threading.Thread.Sleep(30);
                }
            }
            Plugin.Log?.LogError("[Replay] 写自测报告失败（可能被别的程序打开着）: "
                + (last != null ? last.GetType().Name + ": " + last.Message : "?") + " path=" + path);
        }

        private static string BuildReportText()
        {
            var verdict = finished || phase == Phase.Quit || phase == Phase.Done
                ? (failReason == null && verdictFail == 0 ? "PASS" : "FAIL")
                : "进行中";
            var sb = new StringBuilder();
            sb.Append("结果  : ").Append(verdict).Append('\n');
            sb.Append("阶段  : ").Append(phase).Append('\n');
            if (failReason != null)
                sb.Append("原因  : ").Append(failReason).Append('\n');
            sb.Append("失败项: ").Append(verdictFail).Append('\n');
            sb.Append('\n');
            foreach (var l in report)
                sb.Append(l).Append('\n');
            return sb.ToString();
        }
        #endregion

        #region 小工具
        private static int GetPage(MainSceneController scene)
        {
            try { return scene != null ? (int)scene.currentPage : -1; }
            catch (Exception) { return -1; }
        }

        /// <summary>
        /// 把 <see cref="MainScenePageType"/> 的原始值翻成名字，只用于日志与报告文本。
        ///
        /// <para><b>必须走枚举，不能写数字字面量。</b>游戏往 <c>MainScenePageType</c> 里插新页时
        /// 会把它后面的成员整体顶掉一位 —— 0.7.0 插了 <c>HealthWarning = 1</c>，
        /// 于是 <c>Splash</c> 1→2、<c>Mainmenu</c> 3→4 …… 全表 +1。
        /// 硬编码 <c>case 1: return "Splash"</c> 这种写法不会报错，只会**静默地整体错位一位**，
        /// 让日志看起来"对得上"（顺序合理）却把每个页面都叫错名字。
        /// 用枚举成员名则要么编译期就对不上（改了名会直接编译失败），要么跟着游戏自动正确。</para>
        ///
        /// <para>新增页面会落到 <c>default</c>，打成 <c>Page&lt;N&gt;</c> —— 只是标签朴素，不会误导。</para>
        /// </summary>
        private static string DescribePage(int page)
        {
            switch ((MainScenePageType)page)
            {
                case MainScenePageType.None: return "None";
                case MainScenePageType.HealthWarning: return "HealthWarning";
                case MainScenePageType.Splash: return "Splash";
                case MainScenePageType.Titlescreen: return "Titlescreen";
                case MainScenePageType.Mainmenu: return "Mainmenu";
                case MainScenePageType.Note: return "Note";
                case MainScenePageType.Map: return "Map";
                case MainScenePageType.Almanac: return "Almanac";
                case MainScenePageType.Store: return "Store";
                case MainScenePageType.Archive: return "Archive";
                case MainScenePageType.Addons: return "Addons";
                case MainScenePageType.Customize: return "Customize";
                case MainScenePageType.MusicRoom: return "MusicRoom";
                case MainScenePageType.Arcade: return "Arcade";
                default: return page < 0 ? "?" : "Page" + page;
            }
        }

        private static LevelController GetController()
        {
            try
            {
                var main = MainManager.Instance;
                if (main == null)
                    return null;
                var lm = main.LevelManager;
                return lm != null ? lm.GetLevelController() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static PVZEngine.Level.LevelEngine TryEngine(LevelController ctrl)
        {
            try { return ctrl != null ? ctrl.GetEngine() : null; }
            catch (Exception) { return null; }
        }

        private static int lastPlayTick = -1;
        private static DateTime lastPlayTickAt;

        /// <summary>
        /// 回放是否已经停滞（逻辑 tick 长时间不前进）。
        ///
        /// <para>为什么需要：关卡在播放途中结束（失败/通关）时游戏会弹对话框并 `PauseGame()`，
        /// 暂停时 `LevelEngine.Update` 不跑 → tick 冻住 → 播放卡到超时。
        /// 而小结里最有价值的东西（「目标找不到」按交互+定义分类）只在收尾时才生成。</para>
        /// </summary>
        private static bool PlaybackStalled()
        {
            int tick = ReplayPlayer.CurrentTick;
            if (tick != lastPlayTick)
            {
                lastPlayTick = tick;
                lastPlayTickAt = DateTime.Now;
                return false;
            }
            return (DateTime.Now - lastPlayTickAt).TotalSeconds >= 20;
        }

        private static bool IsStarted(LevelController ctrl)
        {
            try { return ctrl != null && ctrl.IsGameStarted(); }
            catch (Exception) { return false; }
        }

        private static bool IsRunning(LevelController ctrl)
        {
            try { return ctrl != null && ctrl.IsGameRunning(); }
            catch (Exception) { return false; }
        }

        private static int pausedFrames;
        private static int unpauseAttempts;

        /// <summary>
        /// 游戏被暂停时的兜底：从关卡存档续关（<c>…\level\&lt;stage&gt;.lvl</c>）时游戏会弹
        /// 「关卡已载入」对话框并 <c>PauseGame()</c>，而**暂停时逻辑 tick 不前进**，
        /// 自测的热身/冷却阶段是按 tick 计数的 —— 不处理就永远完不成
        /// （日志停在「录制已开始: …」之后再无下文）。
        ///
        /// <para>等 ~2 秒（120 帧）再替玩家点「继续」，最多试 3 次，避免真的卡在别的地方时刷屏。</para>
        /// </summary>
        private static void TryUnpause(LevelController ctrl)
        {
            pausedFrames++;
            if (pausedFrames < 120)
                return;
            pausedFrames = 0;
            unpauseAttempts++;
            if (unpauseAttempts > 3)
                return;
            Plugin.Log?.LogWarning("[Replay] [SelfTest] 游戏处于暂停（第 " + unpauseAttempts
                + " 次尝试）—— 多半是「关卡已载入」对话框（从关卡存档续关）。替玩家点「继续」。");
            Note("游戏暂停中，尝试关掉「关卡已载入」对话框（第 " + unpauseAttempts + " 次）");
            ReplayPlayer.ResumeFromLoadedDialog(ctrl);
        }

        private static int Ticks(LevelController ctrl)
        {
            try
            {
                var lvl = ctrl.GetEngine();
                return lvl != null ? (int)lvl.GetLevelTime() : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string CfgString(BepInEx.Configuration.ConfigEntry<string> entry, string fallback)
        {
            try { return entry != null && !string.IsNullOrWhiteSpace(entry.Value) ? entry.Value.Trim() : fallback; }
            catch (Exception) { return fallback; }
        }

        private static bool CfgBool(BepInEx.Configuration.ConfigEntry<bool> entry, bool fallback)
        {
            try { return entry != null ? entry.Value : fallback; }
            catch (Exception) { return fallback; }
        }

        private static int CfgInt(BepInEx.Configuration.ConfigEntry<int> entry, int fallback)
        {
            try { return entry != null ? entry.Value : fallback; }
            catch (Exception) { return fallback; }
        }

        /// <summary>
        /// 从 header 那一行里抠一个整数字段出来（自测只在**校验文件**时用，不参与逻辑）。
        ///
        /// <para>刻意用正则而不是 <c>FlatJson</c>：后者是 internal 的解析器，
        /// 自测在这里要做的是「**从文本层面**验证写出来的东西能被读回来」，
        /// 复用同一份解析器会让「格式写坏了」这件事变成自证清白。</para>
        /// </summary>
        private static int ReadHeaderInt(string header, string key)
        {
            if (string.IsNullOrEmpty(header))
                return -1;
            var m = Regex.Match(header, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?[0-9]+)");
            if (!m.Success)
                return -1;
            return int.TryParse(m.Groups[1].Value, out var v) ? v : -1;
        }

        private static string DescribeKinds(Dictionary<string, int> kinds)
        {
            if (kinds.Count == 0)
                return "(空)";
            var sb = new StringBuilder();
            foreach (var kv in kinds)
            {
                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }

        private static string Short(Exception e)
        {
            var inner = e.InnerException ?? e;
            return inner.GetType().Name + ": " + inner.Message;
        }

        private static string ReadGzip(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var sr = new StreamReader(gz, Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...(" + s.Length + ")";
        }
        #endregion
    }
}
