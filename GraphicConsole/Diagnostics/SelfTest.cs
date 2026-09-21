using System;

namespace GraphicConsole
{
    /// <summary>
    /// 自测脚手架 —— 让**游戏自己**走一遍面板，并把运行时布局打进日志。
    ///
    /// 存在的理由：面板的视觉问题（文字挤压、控件出屏、下拉框弹窗跑到看不见的地方）
    /// 从代码静态看是看不出来的，必须看运行时真实的 RectTransform；而面板又要靠热键/图标打开、
    /// 关卡内页面还得先真的进关卡 —— 自动化测试不能靠人手点。
    /// 所以 <c>General.SelfTest</c> 打开时：
    ///   ① 在主界面打开面板 → 等布局结算 → dump → 关闭；
    ///   ② 自动进关卡（走官方 <c>LevelManager.GotoLevelSceneAsync + InitLevel</c>，
    ///      与主菜单「开始游戏」完全同一条路）；
    ///   ③ 在关卡里再打开面板 → dump。
    ///
    /// ⚠️⚠️ 这里**刻意不用 async/await**，全部靠帧计数推进。原因是一次真实事故：
    /// 用 <c>await Task.Delay(...)</c> 做延时时，续体被调度到了**线程池线程**上执行
    /// （说明那一刻 <c>SynchronizationContext.Current</c> 为 null，await 就退化成
    /// <c>TaskScheduler.Default</c>），于是续体里对 <c>Global.Level</c> 的调用
    /// 从非主线程进了 il2cpp → 直接 <c>Fatal error 0xC0000005</c> 崩游戏。
    /// 游戏的官方代码也是同一个套路：它**从不** await 关卡切换的 Task，而是
    /// <c>while (!task.IsCompleted) yield return null;</c> 在协程里轮询 —— 见
    /// <c>MainmenuController.GotoDebugStage</c>。这里照抄这个模式（见 <see cref="Tick"/>）。
    ///
    /// 配置：<c>General.SelfTest = "" | menu | level | all</c>（默认空 = 关闭，零开销）。
    /// </summary>
    internal static class SelfTest
    {
        /// <summary>等主菜单稳定（帧）。</summary>
        private const int WarmupFrames = 240;
        /// <summary>打开面板后等布局组结算（帧）—— 构建当帧 RectTransform 还是初始尺寸。</summary>
        private const int LayoutFrames = 30;
        /// <summary>进关卡后等它加载完（帧）。</summary>
        private const int LevelWaitFrames = 300;
        /// <summary>把面板（含展开的下拉框弹窗）**保持显示**的帧数，期间分阶段截图。</summary>
        private const int HoldFrames = 300;
        /// <summary>每个保持阶段之间隔的帧数（让弹窗布局/重绘先结算完）。</summary>
        private const int ShotDelayFrames = 40;

        private static bool started;
        private static string mode = string.Empty;
        private static int step = -1;
        private static int stepFrames;
        private static int dumpCountdown;
        private static int holdCountdown;
        private static int holdStage;
        private static string dumpNote = string.Empty;

        /// <summary>保持阶段的第 <see cref="holdStage"/> 步：截图 / 打开自绘的选择浮层。</summary>
        private static void RunHoldStage()
        {
            switch (holdStage)
            {
                case 0:
                    DiagShots.Capture(Tag() + "a");
                    break;
                case 1:
                    PanelLayoutDump.ProbePicker();
                    break;
                case 2:
                    DiagShots.Capture(Tag() + "b");
                    break;
            }
            PanelLayoutDump.LogProbeState("保持阶段 " + holdStage);
        }

        /// <summary>截图文件名后缀（menu / level）。</summary>
        private static string Tag()
        {
            return LevelFacade.InLevel ? "level" : "menu";
        }

        public static void Tick()
        {
            if (!started)
            {
                var m = CompatConfig.SelfTestMode;
                if (string.IsNullOrEmpty(m))
                    return;
                mode = m.ToLowerInvariant();
                started = true;
                step = 0;
                stepFrames = 0;
                Plugin.Log?.LogInfo("[SelfTest] 已启用（模式 " + mode + "）");
            }
            if (step < 0)
                return;

            stepFrames++;

            // dump 倒计时：等布局结算后再读，否则读到的全是初始尺寸。
            if (dumpCountdown > 0)
            {
                dumpCountdown--;
                if (dumpCountdown == 0)
                {
                    // ① 原版模板原样（我们克隆过来、未经改动的副本）——
                    //    用来查"原版对话框的框体美术到底挂在哪个节点"。
                    PanelLayoutDump.DumpTemplateSources();
                    // ② 面板实际布局
                    PanelLayoutDump.Dump(ConsolePanel.RootObject, dumpNote);
                    // ③ 控件级探测：下拉格（按钮 targetGraphic）/ 原版提示组件
                    PanelLayoutDump.ProbeWidgets(ConsolePanel.RootObject, dumpNote);
                    holdCountdown = HoldFrames;
                    holdStage = 0;
                }
                return;
            }

            // 保持阶段：在**同一个面板、同一个页面**上截两张图
            //   ① 关闭态 → xxxa.png
            //   ② 打开选择浮层 → xxxb.png
            // 对两张图做像素差分，就能证明“浮层真的画出来了”
            //（当初 TMP 自带的弹窗就是这么被证实“一个像素都没画”的）。
            if (holdCountdown > 0)
            {
                holdCountdown--;
                if (holdCountdown <= HoldFrames - ShotDelayFrames * holdStage)
                {
                    RunHoldStage();
                    holdStage++;
                }
                if (holdCountdown == 0)
                {
                    if (PickerOverlay.Shared != null)
                        PickerOverlay.Shared.Close();
                    PanelLayoutDump.HideProbe();
                    ConsolePanel.Close();
                    Plugin.Log?.LogInfo("[SelfTest] dump+截图完成，已关闭面板：" + dumpNote
                        + "\n[SelfTest] " + DiagShots.LastPath);
                }
                return;
            }

            switch (step)
            {
                case 0: // 等主菜单
                    if (stepFrames < WarmupFrames)
                        return;
                    if (mode == "level")
                    {
                        Next(2);
                        return;
                    }
                    Probe("主界面（关卡外页面）");
                    Next(1);
                    return;

                case 1: // 主界面 dump 已完成
                    Next(2);
                    return;

                case 2: // 进关卡
                    if (mode == "menu")
                    {
                        Finish();
                        return;
                    }
                    if (LevelFacade.InLevel)
                    {
                        Plugin.Log?.LogInfo("[SelfTest] 已在关卡内");
                        Next(3);
                        return;
                    }
                    if (LevelFacade.SwitchingLevel)
                        return;
                    if (!EnterLevel())
                        Finish();
                    return;

                case 3: // 等关卡稳定
                    if (stepFrames < LevelWaitFrames)
                        return;
                    Probe("关卡内（页面 1「基础」）");
                    Next(4);
                    return;

                case 4:
                    Finish();
                    return;
            }
        }

        /// <summary>打开面板，并安排过若干帧后 dump + 关闭。</summary>
        private static void Probe(string note)
        {
            Plugin.Log?.LogInfo("[SelfTest] 打开面板：" + note);
            ConsolePanel.Open();
            if (!ConsolePanel.IsOpen)
                Plugin.Log?.LogError("[SelfTest] 面板没能打开：" + ConsolePanel.LastError);
            dumpNote = note;
            dumpCountdown = LayoutFrames;
        }

        /// <summary>进入第一关（与官方主菜单「开始游戏」同一条路：GotoLevelSceneAsync → InitLevel）。</summary>
        private static bool EnterLevel()
        {
            var main = LevelFacade.Main;
            if (main == null || main.Settings == null)
            {
                Plugin.Log?.LogError("[SelfTest] 找不到 Settings，无法取第一关 id");
                return false;
            }
            var area = main.Settings.FirstLevelAreaID;
            var stage = main.Settings.FirstLevelStageID;
            Plugin.Log?.LogInfo("[SelfTest] 请求进入第一关 " + area + " / " + stage);
            return LevelFacade.BeginLevelSwitch(area, stage, "自测");
        }

        private static void Next(int nextStep)
        {
            step = nextStep;
            stepFrames = 0;
        }

        private static void Finish()
        {
            step = -1;
            Plugin.Log?.LogInfo("[SelfTest] DONE\n" + ConsolePanel.Describe()
                + "\n[SelfTest] " + BlueprintListToggle.Describe()
                + "\n[SelfTest] 截图：" + DiagShots.LastPath);
        }
    }
}
