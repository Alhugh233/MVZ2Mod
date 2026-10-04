using System;
using DSHCore.Ui;
using HarmonyLib;

namespace UiGallery
{
    /// <summary>
    /// 每帧心跳。挂在官方本来就每帧跑的 <c>MainSceneController.Update</c> 上。
    ///
    /// <para><b>为什么不自己注入 MonoBehaviour：</b>那需要 <c>ClassInjector.RegisterTypeInIl2Cpp</c>，
    /// 而本 mod 刻意做到**零注入**（不注册类型、不注册 Definition、不注册 ModInfo）——
    /// 这样它永远不会成为别的 mod 崩溃的原因。代价是只能借用官方的心跳。</para>
    ///
    /// <para>职责只有两件：<b>自测模式</b>（自动化验证，见 <see cref="GalleryConfig.SelfTest"/>）
    /// 和定期回收保活表。</para>
    /// </summary>
    internal static class GalleryTick
    {
        private const string HarmonyId = "qoznos.mvz2.uigallery.tick";

        /// <summary>主菜单加载完成前等这么多帧再动手 —— 官方自己的初始化也在跑。</summary>
        private const int WarmupFrames = 60;

        private static bool applied;
        private static long frames;

        private static int state;
        private static int stateFrame;
        private static int pageCursor;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;

            try
            {
                var target = AccessTools.Method(typeof(MVZ2.Scenes.MainSceneController), "Update");
                if (target == null)
                {
                    Plugin.Log?.LogWarning("[Tick] 找不到 MainSceneController.Update，自测模式不可用。");
                    return;
                }

                new Harmony(HarmonyId).Patch(target, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(GalleryTick), nameof(UpdatePostfix))));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Tick] 挂心跳失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void UpdatePostfix()
        {
            try
            {
                frames++;

                // 每 10 秒左右回收一次已销毁属主的委托登记。
                if (frames % 600 == 0)
                    UiClone.PruneKeepAlive();

                if (GalleryConfig.SelfTest)
                    SelfTestTick();
            }
            catch (Exception)
            {
                // 心跳里抛异常会打断 MainSceneController.Update，必须吞掉。
            }
        }

        // ------------------------------------------------------------------
        // 自测（无头验证路径）
        // ------------------------------------------------------------------
        //
        // 目标是让"UI 基础设施在这台机器上到底能不能工作"变成一个可 grep 的结论：
        // 全程只读日志就能判断，不需要在游戏里打字。
        //   0 等待就绪 → 1 解析素材 + 打报告 → 2..N 逐页装配 + 打结果 → 9 汇总

        private static void SelfTestTick()
        {
            if (state == 9)
                return;
            if (stateFrame++ < 1)
                return;

            switch (state)
            {
                case 0:
                    if (!UiGame.IsReady || frames < WarmupFrames)
                        return;

                    // 进主菜单后还需要等一点点：OptionsDialog（复选框/滑条的素材来源）
                    // 属于主菜单页，是随页面加载出来的。
                    if (stateFrame < 30)
                        return;

                    Plugin.Log?.LogInfo("[SelfTest] 游戏 UI 已就绪，开始解析素材。");
                    UiSkin.Prepare(true);
                    Plugin.Log?.LogInfo("[SelfTest] 素材解析报告：\n" + UiSkin.Report);
                    Next(1);
                    return;

                case 1:
                    var opened = GalleryPanel.Open();
                    Plugin.Log?.LogInfo("[SelfTest] 打开画廊：" + (opened ? "成功" : "失败"));
                    Plugin.Log?.LogInfo("[SelfTest] 面板报告：\n" + GalleryPanel.Report());
                    if (!opened)
                    {
                        Plugin.Log?.LogError("[SelfTest] 画廊打不开，自测中止。");
                        state = 9;
                        return;
                    }
                    pageCursor = 0;
                    Next(2);
                    return;

                case 2:
                    // 装配一页
                    if (pageCursor >= GalleryPages.All.Length)
                    {
                        Plugin.Log?.LogInfo("[SelfTest] 全部 " + GalleryPages.All.Length + " 页装配完毕。");
                        Plugin.Log?.LogInfo("[SelfTest] 布局 dump（末页）：\n" + GalleryPanel.LayoutDump());
                        Plugin.Log?.LogInfo("[SelfTest] 最终报告：\n" + GalleryPanel.Report());

                        // 示例设置项：把"会不会出现在原版设置界面"的判据也留进日志
                        // （复刻官方渲染判据，见 SampleOptions.Report）。
                        if (GalleryConfig.SampleOptions)
                            Plugin.Log?.LogInfo("[SelfTest] 示例设置项：\n" + SampleOptions.Report());

                        // 纯 ASCII 结束标记：脚本（tools/run-selftest.ps1）拿它当等待条件，
                        // 不用去匹配中文（中文在无 BOM 的 ps1 里会被按 GBK 误读）。
                        Plugin.Log?.LogInfo("[SelfTest] SELFTEST_DONE");
                        Plugin.Log?.LogInfo("[SelfTest] 自测结束。");
                        state = 9;
                        return;
                    }

                    var index = pageCursor;
                    var ok = GalleryPanel.ShowPage(index);
                    Plugin.Log?.LogInfo("[SelfTest] 页 " + (index + 1) + "/" + GalleryPages.All.Length
                        + "「" + GalleryPages.All[index].Name + "」：" + (ok ? "OK" : "失败"));
                    Next(3);
                    return;

                case 3:
                    // 等布局稳定后再截图。**必须慢**：截图本身是异步的，而且游戏自己
                    // 在这几秒里还在加载主菜单 —— 太快拍到的只是加载画面。
                    if (stateFrame < 90)
                        return;

                    Plugin.Log?.LogInfo("[SelfTest] 布局 dump（" + GalleryPages.All[pageCursor].Name + "）：\n"
                        + GalleryPanel.LayoutDump());

                    if (pageCursor == ListPage)
                    {
                        Plugin.Log?.LogInfo("[SelfTest] 列表状态：\n" + GalleryPages.ListReport());
                    }

                    if (pageCursor == DropdownPage)
                    {
                        Next(4);
                        return;
                    }

                    Shot("p" + (pageCursor + 1));
                    pageCursor++;
                    Next(2);
                    return;

                // ---- 下拉框探针（**只为诊断，不是功能验证**）----
                //
                // 下拉框是已知不可用的（展开列表不画）。这一步的作用是：把已知状态留在日志里，
                // 让接手修复的人有一个可复现的起点，并能用离线像素差分确认"确实没画"。
                // 判据见 tools/run-selftest.ps1（按区域算像素差分）。

                case 4:
                    Shot("dd0_closed");
                    {
                        var dd = GalleryPanel.CurrentDropdown();
                        if (dd == null)
                        {
                            Plugin.Log?.LogWarning("[SelfTest] 这一页没有活着的 TMP 下拉框，跳过探针。");
                            pageCursor++;
                            Next(2);
                            return;
                        }
                        Plugin.Log?.LogInfo("[SelfTest] 准备展开 TMP 下拉框："
                            + UiFinder.PathOf(dd.transform)
                            + "  自身 activeInHierarchy=" + (dd.gameObject.activeInHierarchy ? 1 : 0));
                        UiDropdown.Show(dd);
                    }
                    Next(5);
                    return;

                case 5:
                    if (stateFrame < 20)
                        return;
                    {
                        var dd = GalleryPanel.CurrentDropdown();
                        Plugin.Log?.LogInfo("[SelfTest] TMP 下拉框·展开后（期望：列表不画）：\n"
                            + UiDropdown.Describe(dd));
                        Shot("dd1_tmpdd");
                        UiDropdown.Hide(dd);
                    }
                    Next(6);
                    return;

                case 6:
                    if (stateFrame < 20)
                        return;
                    Shot("dd2_closed_after_tmpdd");
                    pageCursor++;
                    Next(2);
                    return;
            }
        }

        /// <summary>下拉框那一页的下标（探针只在那一页跑）。</summary>
        private static int DropdownPage
        {
            get { return GalleryPages.IndexOf("下拉框"); }
        }

        /// <summary>列表那一页的下标（在那页额外打印列表状态）。</summary>
        private static int ListPage
        {
            get { return GalleryPages.IndexOf("列表"); }
        }

        /// <summary>截一张图到 <c>&lt;游戏目录&gt;\BepInEx\_uig_*.png</c>（异步写盘，只用于离线验收）。</summary>
        private static void Shot(string tag)
        {
            try
            {
                var dir = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "BepInEx");
                var path = System.IO.Path.Combine(dir, "_uig_" + Sanitize(tag) + ".png");
                UnityEngine.ScreenCapture.CaptureScreenshot(path);
                Plugin.Log?.LogInfo("[SelfTest] 截图 → " + path);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SelfTest] 截图失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static string Sanitize(string text)
        {
            var sb = new System.Text.StringBuilder(text == null ? 0 : text.Length);
            if (text != null)
            {
                for (var i = 0; i < text.Length; i++)
                {
                    var c = text[i];
                    // 文件名保持 ASCII：中文在非 UTF-8 环境下会被系统换码，之后自己都找不到。
                    sb.Append(char.IsLetterOrDigit(c) && c < 128 ? c : '_');
                }
            }
            return sb.ToString();
        }

        private static void Next(int nextState)
        {
            state = nextState;
            stateFrame = 0;
        }
    }
}
