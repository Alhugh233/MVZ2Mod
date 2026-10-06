using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DSHCore.Ui;
using MVZ2.Managers;
using MVZ2Logic.Scenes;
using UnityEngine;
using UnityEngine.UI;
using Application = UnityEngine.Application;

namespace Replay
{
    /// <summary>
    /// 「回放页面」的自动化验收（配置 <c>GuiTest.Enabled</c>）。
    ///
    /// <para><b>为什么必须单独做一套：</b>本仓库的教训是「节点存在 + 尺寸正确」<b>不等于</b>
    /// 「看得见」。面板类改动必须两条腿走路：
    /// ① 数字 —— 把每个节点的**真实屏幕矩形**dump 出来（落在屏幕内、行与行不重叠）；
    /// ② 画面 —— 真的截一张图人肉看一眼（被上层挡住、sprite 是空心的、字色和底色一样，
    /// 这三类问题 dump 永远证不出来）。</para>
    ///
    /// <para><b>流程：</b>等主菜单 → 程序化切到「插件」页 → 等入口装好 → **真的调用一次
    /// 入口按钮的 <c>onClick</c>** → 面板打开 → 截图 + dump → 弹一次删除确认 → 截图 + dump
    /// → 收起 → 截图（看插件页上追加了入口没有）→ 写报告（<c>_replay_gui.txt</c>）→ 退出。</para>
    ///
    /// <para>截图写在 <c>&lt;游戏目录&gt;/BepInEx/_rp_shot*.png</c>。
    /// ⚠️ <c>ScreenCapture.CaptureScreenshot</c> 是**帧末异步写盘**，
    /// 所以每一拍之后都要等足 <c>GuiTest.HoldFrames</c> 帧（默认 90），否则拍到的是上一拍。</para>
    /// </summary>
    internal static class ReplayGuiTest
    {
        private enum Phase
        {
            WaitMenu = 0,
            GotoAddons,
            WaitAddons,
            DumpTrees,
            WaitEntry,
            OpenPage,
            SettlePage,
            ShotPage,
            Confirm,
            SettleConfirm,
            ShotConfirm,
            CloseAll,
            SettleClosed,
            ShotClosed,
            Report,
            Done,
        }

        private static readonly List<string> report = new List<string>();
        private static Phase phase = Phase.WaitMenu;
        private static int frames;
        private static int phaseFrames;
        private static bool finished;
        private static int shotsTaken;

        public static bool Enabled
        {
            get
            {
                try { return ReplayConfig.GuiTest != null && ReplayConfig.GuiTest.Value; }
                catch (Exception) { return false; }
            }
        }

        private static int Hold
        {
            get
            {
                try
                {
                    var v = ReplayConfig.GuiTestHoldFrames != null ? ReplayConfig.GuiTestHoldFrames.Value : 90;
                    return v < 30 ? 30 : v;
                }
                catch (Exception) { return 90; }
            }
        }

        public static void Tick(MVZ2.Scenes.MainSceneController scene)
        {
            if (finished || !Enabled || scene == null)
                return;

            try
            {
                frames++;
                phaseFrames++;

                if (phase == Phase.WaitMenu && report.Count == 0)
                    Begin();

                switch (phase)
                {
                    case Phase.WaitMenu:
                        StepWaitMenu(scene);
                        break;
                    case Phase.GotoAddons:
                        StepGotoAddons(scene);
                        break;
                    case Phase.WaitAddons:
                        if (WaitPage(scene, MainScenePageType.Addons, "插件页"))
                            Next(Phase.DumpTrees);
                        break;
                    case Phase.DumpTrees:
                        // 把几个原版页面的真实结构打出来 —— 往里插东西之前必须知道它们怎么排的。
                        Note(ReplayTreeDump.DumpByType<MVZ2.Addons.AddonsUI>("插件页（AddonsUI）"));
                        Note(ReplayTreeDump.DumpByType<MVZ2.Customize.CustomizeUI>("自定义页（CustomizeUI，版式参照）"));
                        Note(ReplayTreeDump.DumpByType<MVZ2.Archives.ArchiveUI>("对话档案页（ArchiveUI）"));
                        Next(Phase.WaitEntry);
                        break;
                    case Phase.WaitEntry:
                        // 入口是每 20 帧检查一次装上的，给它 3 秒。
                        if (ReplayPage.EntryInstalled)
                        {
                            Note("入口按钮已装上：" + Describe(ReplayPage.EntryNode));
                            Next(Phase.OpenPage);
                        }
                        else if (phaseFrames > 180)
                        {
                            Note("❌ 3 秒内没等到入口按钮（插件页上没装成功）");
                            Next(Phase.CloseAll);
                        }
                        break;
                    case Phase.OpenPage:
                        ClickEntry();
                        break;
                    case Phase.SettlePage:
                        Settle("回放面板", Phase.ShotPage);
                        break;
                    case Phase.ShotPage:
                        Shoot("page");
                        Next(Phase.Confirm);
                        break;
                    case Phase.Confirm:
                        Note("删除二次确认：" + ReplayPage.TestDeleteConfirm());
                        Next(Phase.SettleConfirm);
                        break;
                    case Phase.SettleConfirm:
                        Settle("删除确认面板", Phase.ShotConfirm);
                        break;
                    case Phase.ShotConfirm:
                        Shoot("confirm");
                        Next(Phase.CloseAll);
                        break;
                    case Phase.CloseAll:
                        if (ReplayPage.IsOpen)
                            ReplayPage.Close();
                        Next(Phase.SettleClosed);
                        break;
                    case Phase.SettleClosed:
                        Settle("收起后的插件页", Phase.ShotClosed);
                        break;
                    case Phase.ShotClosed:
                        Shoot("closed");
                        Next(Phase.Report);
                        break;
                    case Phase.Report:
                        Finish();
                        break;
                }
            }
            catch (Exception e)
            {
                Note("❌ GUI 自测异常：" + e.GetType().Name + ": " + e.Message);
                try { Finish(); }
                catch (Exception) { }
            }
        }

        // ------------------------------------------------------------------
        // 步骤
        // ------------------------------------------------------------------

        private static void Begin()
        {
            report.Clear();
            report.Add("=== MVZ2 Replay GUI self-test ===");
            report.Add("time   : " + DateTime.Now.ToString("s"));
            report.Add("游戏根 : " + GameRoot());
            report.Add("保持帧 : " + Hold);
            report.Add("");
            Plugin.Log?.LogInfo("[GuiTest] 开始");
            WriteReport();
        }

        private static void StepWaitMenu(MVZ2.Scenes.MainSceneController scene)
        {
            var page = Page(scene);
            if (page == MainScenePageType.Titlescreen)
            {
                // 标题界面在等玩家点「点击以开始」。程序化跳过（与玩家那一击等价）。
                try { scene.DisplayMainmenu(); }
                catch (Exception e) { Note("DisplayMainmenu 失败：" + e.GetType().Name); }
                return;
            }
            if (page != MainScenePageType.Mainmenu)
            {
                if (phaseFrames > 1800)
                    Note("等待主菜单已超过 60 秒，当前页 " + page);
                return;
            }
            Note("主菜单就绪");
            Next(Phase.GotoAddons);
        }

        private static void StepGotoAddons(MVZ2.Scenes.MainSceneController scene)
        {
            try
            {
                // 用 DisplayPage 而不是 DisplayAddons(Action)：
                // 后者要传一个 IL2CPP 委托，而这里根本不需要 onReturn（我们不按「返回」）。
                scene.DisplayPage(MainScenePageType.Addons);
                Note("已请求切到插件页（DisplayPage(Addons)）");
            }
            catch (Exception e)
            {
                Note("❌ DisplayPage(Addons) 失败：" + e.GetType().Name + ": " + e.Message);
                Next(Phase.CloseAll);
                return;
            }
            Next(Phase.WaitAddons);
        }

        private static bool WaitPage(MVZ2.Scenes.MainSceneController scene, MainScenePageType want, string what)
        {
            if (Page(scene) == want)
            {
                Note("已进入" + what);
                return true;
            }
            if (phaseFrames > 600)
            {
                Note("❌ 等" + what + "超时（当前 " + Page(scene) + "）");
                return true;   // 继续往下跑，报告里能看出问题
            }
            return false;
        }

        /// <summary>真的调一次入口按钮的 onClick —— 这样「装上去了」和「点了有反应」是分开验证的两件事。</summary>
        private static void ClickEntry()
        {
            var entry = ReplayPage.EntryNode;
            if (!UiFinder.IsAlive(entry))
            {
                Note("❌ 入口按钮没了，直接打开面板");
                ReplayPage.Open();
                Next(Phase.SettlePage);
                return;
            }
            var button = UiClone.FindButton(entry);
            if (button == null)
            {
                Note("❌ 入口按钮上没有 Button 组件（点击回调不会生效）→ 直接打开面板");
                ReplayPage.Open();
                Next(Phase.SettlePage);
                return;
            }
            try
            {
                button.onClick.Invoke();
                Note("已点击入口按钮（onClick.Invoke）→ IsOpen=" + ReplayPage.IsOpen);
            }
            catch (Exception e)
            {
                Note("❌ 点击入口按钮抛异常：" + e.GetType().Name + ": " + e.Message);
                ReplayPage.Open();
            }
            Next(Phase.SettlePage);
        }

        /// <summary>等足 Hold 帧（截图要帧末才写盘），然后 dump 一次布局。</summary>
        private static bool Settle(string what, Phase next)
        {
            if (phaseFrames < Hold)
                return true;

            var sb = new StringBuilder(2048);
            sb.Append("--- ").Append(what).Append(" 布局 dump ---\n");
            ReplayPage.DumpLayout(sb);
            Note(sb.ToString().TrimEnd('\n'));

            // 面板子树只在第一拍 dump 一次：件数多，每次打一遍会把报告撑爆。
            if (!panelTreeDumped)
            {
                panelTreeDumped = true;
                var shell = ReplayPage.PanelNode;
                if (UiFinder.IsAlive(shell))
                    Note(ReplayTreeDump.Dump("回放页顶部（标题）", UiKit.Child(shell, "Top")));
                Note(ReplayTreeDump.Dump("回放面板子树", ReplayPage.PanelBodyNode));

                // 入口按钮与它旁边的原版按钮的**结构对照**：两者字号/自动缩放不一样时一眼能看出。
                var entry = ReplayPage.EntryNode;
                if (UiFinder.IsAlive(entry) && entry.transform.parent != null)
                    Note(ReplayTreeDump.Dump("插件页列表内容（入口 vs 原版按钮）",
                        entry.transform.parent.gameObject));
            }

            Next(next);
            return false;
        }

        private static bool panelTreeDumped;

        private static void Shoot(string tag)
        {
            var root = GameRoot();
            if (string.IsNullOrEmpty(root))
            {
                Note("❌ 找不到游戏目录，跳过截图 " + tag);
                return;
            }
            try
            {
                var path = Path.Combine(root, "BepInEx", "_rp_shot" + shotsTaken + "_" + tag + ".png");
                ScreenCapture.CaptureScreenshot(path);
                shotsTaken++;
                Note("截图（帧末异步写盘）：" + path);
            }
            catch (Exception e)
            {
                Note("❌ 截图失败：" + e.GetType().Name);
            }
        }

        private static void Finish()
        {
            Note("");
            Note("=== 结论 ===");
            Note(ReplayPage.EntryInstalled
                ? "入口已装上；请看上面的矩形与 _rp_shot*.png"
                : "❌ 入口没装上；请看上面的矩形与 _rp_shot*.png");
            phase = Phase.Done;
            finished = true;
            WriteReport();
            Plugin.Log?.LogInfo("[GuiTest] 完成，报告：" + ReportPath());

            if (AutoQuit())
            {
                Plugin.Log?.LogInfo("[GuiTest] 自动退出游戏");
                Application.Quit();
            }
        }

        // ------------------------------------------------------------------
        // 工具
        // ------------------------------------------------------------------

        private static void Next(Phase p)
        {
            phase = p;
            phaseFrames = 0;
        }

        private static void Note(string line)
        {
            if (!string.IsNullOrEmpty(line))
                report.Add("[" + frames + "] " + line);
            WriteReport();
        }

        private static bool AutoQuit()
        {
            try { return ReplayConfig.GuiTestAutoQuit == null || ReplayConfig.GuiTestAutoQuit.Value; }
            catch (Exception) { return true; }
        }

        private static MainScenePageType Page(MVZ2.Scenes.MainSceneController scene)
        {
            try { return (MainScenePageType)scene.currentPage; }
            catch (Exception) { return MainScenePageType.None; }
        }

        private static string GameRoot()
        {
            try
            {
                var data = Application.dataPath;
                return string.IsNullOrEmpty(data) ? null : Path.GetDirectoryName(data);
            }
            catch (Exception) { return null; }
        }

        private static string ReportPath()
        {
            var root = GameRoot();
            return string.IsNullOrEmpty(root)
                ? null
                : Path.Combine(Path.Combine(root, "BepInEx"), "_replay_gui.txt");
        }

        private static void WriteReport()
        {
            var path = ReportPath();
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                var head = "结果  : " + (finished ? "完成" : "进行中") + "\n"
                    + "阶段  : " + phase + "\n\n";
                File.WriteAllText(path, head + string.Join("\n", report.ToArray()), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        private static string Describe(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return "（无）";
            try
            {
                var rt = go.transform.TryCast<RectTransform>();
                if (rt == null)
                    return UiFinder.PathOf(go.transform);
                var canvas = go.GetComponentInParent<Canvas>();
                var cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null : canvas.worldCamera;
                // ⚠️ 必须用 Il2CppStructArray：托管 Vector3[] 会被隐式转成一份**拷贝**，
                // 被调方写的是那份拷贝 —— 读回来永远全 0。
                var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
                rt.GetWorldCorners(corners);
                var min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                var max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
                return UiFinder.PathOf(go.transform)
                    + "  x=" + Mathf.RoundToInt(Mathf.Min(min.x, max.x))
                    + ".." + Mathf.RoundToInt(Mathf.Max(min.x, max.x))
                    + " y=" + Mathf.RoundToInt(Mathf.Min(min.y, max.y))
                    + ".." + Mathf.RoundToInt(Mathf.Max(min.y, max.y))
                    + " (" + Mathf.RoundToInt(Mathf.Abs(max.x - min.x))
                    + "x" + Mathf.RoundToInt(Mathf.Abs(max.y - min.y)) + ")";
            }
            catch (Exception e)
            {
                return "测量失败 " + e.GetType().Name;
            }
        }
    }
}
