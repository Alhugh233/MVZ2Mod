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
                        Finish();
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

                        // 样例设置项：把「会不会出现在对应界面里」的判据也留进日志。
                        if (GalleryConfig.SampleOptions)
                            Plugin.Log?.LogInfo("[SelfTest] 模组设置页样例：\n" + SampleOptions.Report());
                        if (GalleryConfig.SampleVanillaOptions)
                            Plugin.Log?.LogInfo("[SelfTest] 原版设置界面样例：\n" + SampleVanillaOptions.Report());

                        // 画廊 9 页验完了，接着验**插件页子页面**那条路：
                        // 切到插件页 → 等入口装好 → 点开「模组设置（DSHCore）」→ dump + 截图。
                        Next(7);
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

                // ---- 插件页子页面：模组设置（DSHCore） ----
                //
                // 这一段验的是「在插件页里加一个新子页面」这一整套行为：
                // 入口按钮会被自动装上 → 点开 → 页面壳克隆 + 左栏模组列表 + 右栏设置项。
                // 它是 DSHCore 的能力（AddonPage），所以判据放在这里而不是某个 mod 自己的自测里。

                case 7:
                    // 先收起画廊面板，再把主场景切到插件页。
                    GalleryPanel.Close();
                    try
                    {
                        var scene = UiGame.Scene;
                        if (scene == null)
                        {
                            Plugin.Log?.LogWarning("[SelfTest] 主场景还没起来，跳过模组设置页自测。");
                            Finish();
                            return;
                        }
                        // ⚠ 必须给「返回」回调。官方 MainScenePage.Return() 是
                        //   `OnReturnClick?.Invoke(); Hide();` —— 传 null 时返回只会把页面
                        //   藏起来、没有任何东西顶上来，就是一整屏黑（自测里踩过）。
                        //   正常入口（主菜单的「插件」按钮）自己会传，程序化调用别漏。
                        scene.DisplayAddons(ReturnToMainmenu(scene));
                        Plugin.Log?.LogInfo("[SelfTest] 已请求切到插件页（DisplayAddons + 返回回调）");
                        Plugin.Log?.LogInfo("[SelfTest] 判据 外部译文文件："
                            + (ProbeExternalLocale() ? "通过" : "失败"));
                        Plugin.Log?.LogInfo("[SelfTest] 判据 游戏内指令分派："
                            + (ProbeTransCommand() ? "通过" : "失败"));
                        Plugin.Log?.LogInfo("[SelfTest] 本地化状态：\n" + DSHCore.Ui.UiLocale.Report());
                    }
                    catch (Exception e)
                    {
                        Plugin.Log?.LogWarning("[SelfTest] 切插件页失败：" + e.GetType().Name + ": " + e.Message);
                        Finish();
                        return;
                    }
                    Next(8);
                    return;

                case 8:
                    // 等：① 真的停在插件页；② 模组设置页的入口已经装上。
                    if (stateFrame > 600)
                    {
                        Plugin.Log?.LogWarning("[SelfTest] 10 秒内没等到模组设置页入口，放弃。\n"
                            + DSHCore.Ui.AddonPages.Report());
                        Finish();
                        return;
                    }
                    if (!DSHCore.Ui.AddonPages.IsAddonsPage())
                        return;
                    {
                        var page = DSHCore.Ui.AddonPages.Get("ModSettings");
                        if (page == null || !page.EntryInstalled)
                            return;

                        Plugin.Log?.LogInfo("[SelfTest] 插件页子页面入口：\n" + page.DescribeEntry());
                        var entered = page.Open();
                        Plugin.Log?.LogInfo("[SelfTest] 打开模组设置页：" + (entered ? "成功" : "失败")
                            + (entered ? "" : "（" + page.LastError + "）"));
                        if (!entered)
                        {
                            Finish();
                            return;
                        }
                        // 默认选中项可能不是本 mod —— 为了截图可复现，显式选中 uig。
                        var settings = page as DSHCore.Ui.ModSettingsPage;
                        if (settings != null)
                            Plugin.Log?.LogInfo("[SelfTest] 切到 uig：" + (settings.SelectNamespace("uig") ? "OK" : "找不到"));
                    }
                    Next(10);
                    return;

                case 10:
                    if (stateFrame < 90)
                        return;
                    {
                        var page = DSHCore.Ui.AddonPages.Get("ModSettings") as DSHCore.Ui.ModSettingsPage;
                        Plugin.Log?.LogInfo("[SelfTest] 模组设置页布局 dump：\n"
                            + (page == null ? "（拿不到页面对象）" : page.DumpLayout()));
                    }
                    Shot("modsettings");
                    Next(11);
                    return;

                case 11:
                    if (stateFrame < 30)
                        return;
                    {
                        var page = DSHCore.Ui.AddonPages.Get("ModSettings");
                        if (page != null)
                            page.Close();
                        Plugin.Log?.LogInfo("[SelfTest] 插件页子页面状态：\n" + DSHCore.Ui.AddonPages.Report());
                    }
                    Finish();
                    return;
            }
        }

        /// <summary>
        /// 自测收尾：写 ASCII 结束标记（脚本拿它当等待条件）。
        /// </summary>
        private static void Finish()
        {
            Plugin.Log?.LogInfo("[SelfTest] SELFTEST_DONE");
            Plugin.Log?.LogInfo("[SelfTest] 自测结束。");
            state = 9;
        }

        /// <summary>
        /// 「插件页返回」要去的目标：回主菜单（与官方主菜单里那个「插件」按钮传的回调一致）。
        ///
        /// <para>注意 <c>DisplayAddons</c> 收的是 <c>Il2CppSystem.Action</c>，
        /// 托管 lambda 必须转成 il2cpp 委托并**保活**（il2cpp 侧只存裸函数指针）。</para>
        /// </summary>
        private static Il2CppSystem.Action ReturnToMainmenu(MVZ2.Scenes.MainSceneController scene)
        {
            var managed = new Action(() =>
            {
                try { scene.DisplayMainmenuToBasement(); }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning("[SelfTest] 回主菜单失败：" + e.GetType().Name);
                }
            });
            var il2cpp = Il2CppInterop.Runtime.DelegateSupport
                .ConvertDelegate<Il2CppSystem.Action>(managed);
            UiClone.KeepAlive(scene.gameObject, managed, il2cpp);
            return il2cpp;
        }

        /// <summary>
        /// 外部译文文件（<c>BepInEx/config/DSHCore/lang/</c>）的探针。
        ///
        /// <para>这条路径**是给玩家用的**：平时不会有人自动去改那个文件，所以放进自测里跑一遍。
        /// 断言三件事 —— ①文件里的新键能查到；②与代码表同名的键**以文件为准**；
        /// ③`WriteTemplate` 导出的模板（值全空）不会把界面变成空白（空值 = 还没译 → 放行）。</para>
        ///
        /// <para>跑完无论成败都把文件删掉（玩家的 lang 目录不该留下我们塞的东西）。</para>
        /// </summary>
        private static bool ProbeExternalLocale()
        {
            const string Lang = "zh-Hans";
            const string NewKey = "DSHCore.LocaleSelfTest.NewKey";
            const string CodeKey = "MVZ2 Loadout";
            const string FallKey = "MVZ2 Laboratory";       // 只存在于代码表，用来验「模板空值放行」

            var codeBefore = UiLocale.In(CodeKey, Lang);
            var fallBefore = UiLocale.In(FallKey, Lang);
            var dir = System.IO.Path.Combine(UiLocale.ExternalDirectory, Lang + ".d");
            var path = System.IO.Path.Combine(dir, "zz_selftest.txt");
            var mainPath = System.IO.Path.Combine(UiLocale.ExternalDirectory, Lang + ".txt");
            var mainExisted = System.IO.File.Exists(mainPath);
            var okNew = false;
            var okPrec = false;
            var okTemplate = false;
            var okBack = false;
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(path,
                    "# 自测用，跑完会删掉\n"
                    + NewKey + " = 外部文件命中\n"
                    + CodeKey + " = 外部文件优先\n",
                    new System.Text.UTF8Encoding(false));

                UiLocale.ReloadExternal();

                var gotNew = UiLocale.In(NewKey, Lang);
                var gotCode = UiLocale.In(CodeKey, Lang);
                okNew = gotNew == "外部文件命中";
                okPrec = gotCode == "外部文件优先";

                Plugin.Log?.LogInfo("[SelfTest] 外部译文文件：新键 " + (okNew ? "OK" : "FAIL")
                    + "（" + gotNew + "）；覆盖代码表 " + (okPrec ? "OK" : "FAIL")
                    + "（" + codeBefore + " → " + gotCode + "）");

                // 导出模板：值全空 → 不该抢走代码表的译文（否则导出一次界面就全白了）。
                // 已存在真文件时不写（WriteTemplate 也不覆盖），跳过而不是 FAIL。
                if (mainExisted)
                {
                    okTemplate = true;
                    Plugin.Log?.LogInfo("[SelfTest] 导出模板：跳过（" + mainPath + " 已存在）");
                }
                else
                {
                    var written = UiLocale.WriteTemplate(Lang);
                    UiLocale.ReloadExternal();
                    var gotFall = UiLocale.In(FallKey, Lang);
                    var size = System.IO.File.Exists(mainPath)
                        ? new System.IO.FileInfo(mainPath).Length : 0;
                    okTemplate = written != null && size > 0 && gotFall == fallBefore;
                    Plugin.Log?.LogInfo("[SelfTest] 导出模板：" + (okTemplate ? "OK" : "FAIL")
                        + "（" + System.IO.Path.GetFileName(mainPath) + " " + size + " 字节；空值放行 "
                        + fallBefore + " → " + gotFall + "）");
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SelfTest] 外部译文自测异常：" + e.GetType().Name + ": " + e.Message);
            }

            // 收尾：删文件 → 清缓存 → 应当退回代码表的值。
            try
            {
                if (System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
                if (!mainExisted && System.IO.File.Exists(mainPath))
                    System.IO.File.Delete(mainPath);
                if (System.IO.Directory.Exists(dir) && System.IO.Directory.GetFileSystemEntries(dir).Length == 0)
                    System.IO.Directory.Delete(dir);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SelfTest] 清理自测译文失败：" + e.GetType().Name);
            }
            try
            {
                UiLocale.ReloadExternal();
                var back = UiLocale.In(CodeKey, Lang);
                okBack = back == codeBefore;
                Plugin.Log?.LogInfo("[SelfTest] 外部译文文件已清理，回退代码表 "
                    + (okBack ? "OK" : "FAIL") + "（" + back + "）");
            }
            catch (Exception)
            {
            }

            return okNew && okPrec && okTemplate && okBack;
        }

        /// <summary>下拉框那一页的下标（探针只在那一页跑）。</summary>
        private static int DropdownPage
        {
            get { return GalleryPages.IndexOf("下拉框"); }
        }

        /// <summary>
        /// 游戏内指令 <c>/dshtrans</c> 的探针（两段，见下）。
        ///
        /// <para>①**分派表**：用反射直接调 <c>TransCommand.Execute/Suggest</c>，
        /// 断言「不是我的指令返回 null」「自己的子指令有内容」「Tab 能补出指令名与子指令」。
        /// 它是纯函数（字符串进、字符串出），所以能离线断言。</para>
        ///
        /// <para>②**端到端**：真的往游戏控制台入口（<c>Global.Debugs.ExecuteCommand</c>）提交一条，
        /// 然后用 <c>LogViewer</c> 的捕获计数判据 —— prefix 命中时会打一条
        /// <c>[dsh] 收到：…</c>，没命中则什么都不打（原版只往控制台 UI 里写）。
        /// 这样不必去解析控制台文本也能证明「从控制台进来能走到我们的分派」。</para>
        /// </summary>
        private static bool ProbeTransCommand()
        {
            const string TypeName = "DSHCore.Diagnostics.TransCommand";
            var okDispatch = false;
            var okSuggest = false;
            var okConsole = false;

            try
            {
                var type = HarmonyLib.AccessTools.TypeByName(TypeName);
                var execute = type == null ? null : HarmonyLib.AccessTools.Method(type, "Execute");
                var suggest = type == null ? null : HarmonyLib.AccessTools.Method(type, "Suggest");
                if (execute == null || suggest == null)
                {
                    Plugin.Log?.LogWarning("[SelfTest] 找不到 " + TypeName + " 的 Execute/Suggest（指令没接进去？）");
                    return false;
                }

                var help = execute.Invoke(null, new object[] { new[] { "dshtrans", "help" } }) as string;
                var status = execute.Invoke(null, new object[] { new[] { "dshtrans" } }) as string;
                var keys = execute.Invoke(null, new object[] { new[] { "dshtrans", "keys" } }) as string;
                var notMine = execute.Invoke(null, new object[] { new[] { "dshlog", "status" } });
                var empty = execute.Invoke(null, new object[] { new string[0] });

                okDispatch = !string.IsNullOrEmpty(help) && help.Contains("write")
                    && !string.IsNullOrEmpty(status) && status.Contains("外部目录")
                    && !string.IsNullOrEmpty(keys) && keys.Length > 0
                    && notMine == null && empty == null;

                var s1 = suggest.Invoke(null, new object[] { new[] { "ds" } }) as string[];
                var s2 = suggest.Invoke(null, new object[] { new[] { "dshtrans", "w" } }) as string[];
                var s3 = suggest.Invoke(null, new object[] { new[] { "dshl" } }) as string[];
                okSuggest = s1 != null && Array.IndexOf(s1, "dshtrans") >= 0
                    && s2 != null && Array.IndexOf(s2, "write") >= 0
                    && s3 != null && s3.Length == 0;    // 前缀对不上就别乱补

                Plugin.Log?.LogInfo("[SelfTest] 指令 /dshtrans 分派 " + (okDispatch ? "OK" : "FAIL")
                    + "（help=" + (help == null ? "null" : help.Length + " 字符")
                    + "，status=" + (status == null ? "null" : status.Length + " 字符")
                    + "，别人的指令 → " + (notMine == null ? "null" : "**非 null**") + "）");
                Plugin.Log?.LogInfo("[SelfTest] 指令 Tab 补全 " + (okSuggest ? "OK" : "FAIL")
                    + "（/ds → [" + Join(s1) + "]；/dshtrans w → [" + Join(s2) + "]" + "）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SelfTest] 指令分派探针异常：" + e.GetType().Name + ": " + e.Message);
            }

            // ② 端到端：清空捕获 → 提交 → 看有没有新记录（只有 prefix 命中才会打日志）。
            try
            {
                var debug = MVZ2Logic.Global.Debugs;
                if (debug == null)
                {
                    Plugin.Log?.LogInfo("[SelfTest] 指令控制台端到端：跳过（控制台未就绪）");
                }
                else
                {
                    DSHCore.LogViewer.ClearHistory();
                    var before = DSHCore.LogViewer.CapturedCount;
                    debug.ExecuteCommand("/dshtrans help", 1);
                    var after = DSHCore.LogViewer.CapturedCount;
                    okConsole = after > before;
                    Plugin.Log?.LogInfo("[SelfTest] 指令控制台端到端 " + (okConsole ? "OK" : "FAIL")
                        + "（捕获 " + before + " → " + after + "）");
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[SelfTest] 指令控制台端到端异常：" + e.GetType().Name + ": " + e.Message);
            }

            return okDispatch && okSuggest;
        }

        private static string Join(string[] items)
        {
            return items == null || items.Length == 0 ? "" : string.Join("、", items);
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
