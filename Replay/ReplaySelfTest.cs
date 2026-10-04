using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using MVZ2.Level;
using MVZ2.Managers;
using MVZ2.Scenes;
using MVZ2Logic.Scenes;
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
        }

        private const string ReportFileName = "_replay_selftest.txt";
        private const int MaxEventsListed = 40;
        private const int MainmenuPage = (int)MainScenePageType.Mainmenu;

        private static readonly List<string> report = new List<string>();
        private static Phase phase = Phase.WaitMenu;
        private static int frames;
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
        private static bool playOnly;
        private static string playFileName;
        private static DateTime lastHeartbeatAt = DateTime.MinValue;
        private static int lastPageSeen = -1;

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
                            var gap = Math.Max(CfgInt(ReplayConfig.SelfTestActionGapTicks, 30), 1);
                            if (frames % gap != 0)
                                break;

                            var item = items[actionsDone].Trim();
                            var before = ReplayRecorder.EventCount;
                            Inject(ctrl, item);
                            var after = ReplayRecorder.EventCount;
                            actionLog.Add("  " + item.PadRight(12) + " 记录事件 +" + (after - before)
                                + (after - before > 0 ? "" : "   (无变化)"));
                            actionsDone++;
                            WriteReport();
                            break;
                        }

                    case Phase.CoolDown:
                        {
                            var ctrl = GetController();
                            if (ctrl != null && IsRunning(ctrl))
                                frames++;
                            else if (ctrl != null)
                                TryUnpause(ctrl);       // 暂停时 tick 不前进，不能干等
                            if (frames >= CfgInt(ReplayConfig.SelfTestCooldownTicks, 90))
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
                            WriteReport();
                            EnterPhase(CfgBool(ReplayConfig.SelfTestAutoQuit, true) ? Phase.Quit : Phase.Done);
                            break;
                        }

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

        private static void StepWaitMenu(MainSceneController scene, int page)
        {
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
        private static void EnterPhase(Phase next)
        {
            phase = next;
            phaseStart = DateTime.Now;
            frames = 0;
            lastHeartbeatAt = DateTime.MinValue;
            if (next == Phase.VerifyPlayback)
            {
                // 重置「回放停滞」检测，否则会带着上一个阶段的旧时间戳。
                lastPlayTick = -1;
                lastPlayTickAt = DateTime.Now;
            }
        }

        private static void OnLevelEntered(LevelController ctrl)
        {
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

            var keyframe = nonEmpty.Count > 1 ? nonEmpty[1] : "";
            Check(keyframe.Contains("\"k\":\"keyframe\""), "第 2 行是 keyframe");
            Check(keyframe.Contains("\"state\":\"{"), "关键帧带状态 BSON");

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
                else if (k != "keyframe")
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

        private static string DescribePage(int page)
        {
            switch (page)
            {
                case -1: return "?";
                case 0: return "None";
                case 1: return "Splash";
                case 2: return "Titlescreen";
                case 3: return "Mainmenu";
                case 4: return "Note";
                case 5: return "Map";
                default: return "Page" + page;
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
