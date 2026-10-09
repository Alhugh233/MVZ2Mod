using System;
using DSHCore.Ui;
using PVZEngine;
using UnityEngine;

namespace Loadout
{
    /// <summary>
    /// 自测脚手架（**排障用**，配置里 <c>General.SelfTest</c> 留空默认关闭）。
    ///
    /// <para>为什么需要它：面板的视觉问题（控件被挤、文字溢出、内容把框体顶出屏幕、
    /// 补齐弹窗没画出来）从代码静态看不出来，必须看运行时真实的 RectTransform 与画面。
    /// 打开后它会：</para>
    ///
    /// <list type="number">
    /// <item>等主菜单起来 → 自动开面板 → 等布局结算 → 把布局 dump 进日志；</item>
    /// <item>把第一个补齐字段**聚焦并展开候选**，再截一张图
    /// （「节点存在」≠「看得见」，只有截图能证明）；</item>
    /// <item>自动进第一关（与主菜单「开始游戏」同一条官方路线），在关卡内再来一遍；</item>
    /// <item>打一行 <c>[SelfTest] DONE</c> 收尾（供脚本等待）。</item>
    /// </list>
    ///
    /// <para>⚠️ <b>绝不用 <c>async</c>/<c>await</c></b>：Harmony 补丁里 await 的续体会被丢到线程池线程，
    /// 那一刻调 il2cpp 会直接崩游戏。所以全部用**帧计数状态机**。</para>
    /// </summary>
    internal static class SelfTest
    {
        private const int LayoutFrames = 30;    // 开面板后等布局结算
        private const int HoldFrames = 100;     // 展开补齐后保持的帧数（截图需要 ≥20 帧）
        private const int ShotDelayFrames = 30; // 截图是异步写盘，再等一会

        private static string mode = string.Empty;
        private static int step;
        private static int frame;
        private static int pageCursor;
        private static string pendingTag;
        private static bool loggedDump;

        public static bool Enabled { get { return !string.IsNullOrEmpty(mode); } }

        public static string Mode { get { return mode; } }

        public static void Init()
        {
            mode = (CompatConfig.SelfTestMode ?? string.Empty).Trim().ToLowerInvariant();
            if (mode != "menu" && mode != "level" && mode != "all")
                mode = string.Empty;
            step = 0;
            frame = 0;
            pageCursor = 0;
            loggedDump = false;
            if (Enabled)
            {
                Plugin.Log?.LogInfo("[SelfTest] 已启用（模式 " + mode + "）");
                Plugin.Log?.LogInfo("[SelfTest] UI 素材是否就绪=" + UiSkin.IsReady);
            }
        }

        /// <summary>每帧调用（由面板心跳转发）。</summary>
        public static void Tick()
        {
            if (!Enabled)
                return;
            try
            {
                frame++;
                switch (step)
                {
                    case 0: // 等游戏 UI 起来（标题界面早期素材还没就绪）
                        if (UiSkin.Prepare())
                        {
                            Plugin.Log?.LogInfo("[SelfTest] 素材就绪，打开面板（关卡外）");
                            DumpArchive();
                            step = 1;
                            frame = 0;
                        }
                        break;

                    case 1: // 开面板
                        if (!LoadoutPanel.Open())
                        {
                            Plugin.Log?.LogError("[SelfTest] 面板打不开：" + LoadoutPanel.LastError);
                            Finish();
                            return;
                        }
                        step = 2;
                        frame = 0;
                        break;

                    case 2: // 等布局 + 等资源侧就绪
                        if (frame >= LayoutFrames && (ProgressPage.SourceCount > 0 || frame > 360))
                        {
                            Plugin.Log?.LogInfo("[SelfTest] 关卡外：解锁条件来源条目=" + ProgressPage.SourceCount
                                + "（等第 " + frame + " 帧）");
                            Plugin.Log?.LogInfo("[SelfTest] " + ProgressOps.DebugBatchLockTest());
                            Dump("关卡外");
                            DiagShots.Capture("menua");
                            pendingTag = "menua";
                            step = 3;
                            frame = 0;
                        }
                        break;

                    case 3: // 展开补齐候选
                        if (frame >= ShotDelayFrames)
                        {
                            var state = UiCompleter.DebugOpenFirst();
                            Plugin.Log?.LogInfo("[SelfTest] 已展开候选列表：\n" + state);
                            // 弹窗是展开时才建的，所以它的布局只能在这之后量。
                            foreach (var line in PanelDump.PopupReport().Split('\n'))
                                Plugin.Log?.LogInfo(line);
                            step = 4;
                            frame = 0;
                        }
                        break;

                    case 4:
                        if (frame >= HoldFrames)
                        {
                            DiagShots.Capture("menub");
                            pendingTag = "menub";
                            step = 5;
                            frame = 0;
                        }
                        break;

                    case 5: // 关卡外结束
                        if (frame >= ShotDelayFrames)
                        {
                            if (LoadoutPanel.IsOpen)
                                LoadoutPanel.Close();
                            Plugin.Log?.LogInfo("[SelfTest] 关卡外完成（截图 " + pendingTag + "）");
                            if (mode == "menu")
                            {
                                Finish();
                                return;
                            }
                            step = 6;
                            frame = 0;
                        }
                        break;

                    case 6: // 进第一关
                        if (!EnterLevel())
                        {
                            Plugin.Log?.LogError("[SelfTest] 无法进入关卡，直接收尾");
                            Finish();
                            return;
                        }
                        step = 7;
                        frame = 0;
                        break;

                    case 7: // 等关卡起来
                        if (LevelFacade.InLevel && !LevelFacade.SwitchingLevel)
                        {
                            if (frame < 90)
                                break;      // 再等一会，等关卡 UI 初始化完
                            Plugin.Log?.LogInfo("[SelfTest] 已在关卡内，打开面板");
                            if (!LoadoutPanel.Open())
                            {
                                Plugin.Log?.LogError("[SelfTest] 关卡内面板打不开：" + LoadoutPanel.LastError);
                                Finish();
                                return;
                            }
                            step = 8;
                            frame = 0;
                        }
                        break;

                    case 8:
                        if (frame >= LayoutFrames)
                        {
                            Plugin.Log?.LogInfo("[SelfTest] " + ChooseProbe.Dump());
                            // 关卡是直接起起来的（没走选卡流程），卡槽是空的 —— 先填点东西，
                            // 否则蓝图页 / 制品页没有可画的内容，等于没测。
                            var level = LevelFacade.Current;
                            Plugin.Log?.LogInfo("[SelfTest] 填卡槽：" + BlueprintOps.DebugFillSlots(level, 6));
                            Plugin.Log?.LogInfo("[SelfTest] 填制品：" + ArtifactOps.DebugFill(level, 3));
                            Dump("关卡内");
                            DiagShots.Capture("levela");
                            pendingTag = "levela";
                            pageCursor = 1;
                            step = 20;
                            frame = 0;
                        }
                        break;

                    case 20: // 关卡内每一页都走一遗（建页 / 字段 / 报告 / 截图）
                        if (frame < LayoutFrames)
                            break;
                        if (pageCursor < LoadoutPanel.PageCount)
                        {
                            var index = pageCursor;
                            pageCursor++;
                            if (!LoadoutPanel.ShowPage(index))
                            {
                                Plugin.Log?.LogError("[SelfTest] 切到第 " + (index + 1) + " 页失败");
                                step = 9;
                                frame = 0;
                                break;
                            }
                            var tag = "page" + (index + 1);
                            Plugin.Log?.LogInfo("[SelfTest] ===== 页 " + (index + 1) + "/"
                                + LoadoutPanel.PageCount + "「" + LoadoutPanel.PageName + "」 =====");
                            foreach (var line in PanelDump.Report().Split('\n'))
                                Plugin.Log?.LogInfo(line);
                            foreach (var line in LoadoutPanel.PageReport().Split('\n'))
                                Plugin.Log?.LogInfo("[SelfTest] " + line);
                            DiagShots.Capture(tag);
                            pendingTag = tag;
                            frame = 0;
                            break;
                        }
                        LoadoutPanel.ShowPage(0);
                        step = 9;
                        frame = 0;
                        break;

                    case 9:
                        if (frame >= ShotDelayFrames)
                        {
                            var state = UiCompleter.DebugOpenFirst();
                            Plugin.Log?.LogInfo("[SelfTest] 关卡内已展开候选列表：\n" + state);
                            // 关闭判据：点浮层/本格内不该关、点别处该关。
                            Plugin.Log?.LogInfo("[SelfTest] " + UiCompleter.DebugCloseSimulation());
                            // 翻页是纯逻辑 → 用行为模拟而不是手点来验证（逐页/不循环/不跳）。
                            foreach (var line in UiCompleter.DebugPageSimulation().Split('\n'))
                                Plugin.Log?.LogInfo("[SelfTest] " + line);
                            step = 10;
                            frame = 0;
                        }
                        break;

                    case 10:
                        if (frame >= HoldFrames)
                        {
                            // 弹窗是展开时才建的，而且候选项的矩形要等下一轮排版才有值
                            // （刚 Instantiate 出来时是 0x0）→ 所以在这里量。
                            foreach (var line in PanelDump.PopupReport().Split('\n'))
                                Plugin.Log?.LogInfo(line);
                            DiagShots.Capture("levelb");
                            pendingTag = "levelb";
                            step = 11;
                            frame = 0;
                        }
                        break;

                    case 11:
                        if (frame >= ShotDelayFrames)
                        {
                            // 重载里最危险的一步（把状态存档搬到目标关卡名下并改写内部 id）用探针 id
                            // 跑一遍：源文件不该被动、目标文件该写出来、内部 id 该是目标。
                            Plugin.Log?.LogInfo("[SelfTest] " + LevelFacade.DebugCarryOverTest());
                            if (LoadoutPanel.IsOpen)
                                LoadoutPanel.Close();
                            Plugin.Log?.LogInfo("[SelfTest] 关卡内完成（截图 " + pendingTag + "）");
                            step = 22;
                            frame = 0;
                        }
                        break;

                    case 22: // 蓝图页：打开「调整选卡」浮层（克隆原版选卡面板的那一套）
                        if (frame < LayoutFrames)
                            break;
                        // 浮层是挂在面板对话框上的 —— 面板没打开的话它整块都是隐藏的
                        // （activeSelf=1 但 activeInHierarchy=0），dump 会全部失真。
                        if (!LoadoutPanel.IsOpen)
                            LoadoutPanel.Open();
                        if (LoadoutPanel.PageIndex != 1)
                            LoadoutPanel.ShowPage(1);
                        // 镜像行的单选置灰（点两次 = 选上再取消，验完把状态还原）。
                        Plugin.Log?.LogInfo("[SelfTest] ===== 镜像行单选 =====\n"
                            + BlueprintPage.DebugPickSlot(1));
                        // 打开本页时（一张都没选）二三行**必须立刻是灰的**（本轮修的 bug）。
                        Plugin.Log?.LogInfo("[SelfTest] ===== 二三行置灰（未选中） =====\n"
                            + BlueprintPage.DebugGate());
                        // 选中一张 → 该能改的就可改；等级不够的（逆阵营/无条件/传送带）仍然灰。
                        Plugin.Log?.LogInfo("[SelfTest] ===== 选中槽1 后 =====\n"
                            + BlueprintPage.DebugSelect(1));
                        Plugin.Log?.LogInfo("[SelfTest] " + BlueprintPage.DebugSelect(-1));
                        // 逆阵营放置的观感：镜像框里这张卡应该**左右翻转**（sx=-1），不是染红。
                        Plugin.Log?.LogInfo("[SelfTest] ===== 逆阵营 = 图标左右镜像 =====\n"
                            + BlueprintPage.DebugMirror());
                        // 「兼容性等级」那一行设置项到底会不会出现在设置界面里。
                        Plugin.Log?.LogInfo("[SelfTest] ===== 兼容性等级设置项 =====\n"
                            + CompatOptions.Report());
                        // 血量 / 白天不睡觉：标记 → 生成期速查表（映射 + 按卡隔离）。
                        Plugin.Log?.LogInfo("[SelfTest] ===== 目标映射 =====\n"
                            + BlueprintPage.DebugFlagMap(0));
                        // 选卡界面右上角那个「全部蓝图」切换按钮（只在 debug 存档出现）。
                        Plugin.Log?.LogInfo("[SelfTest] ===== X 按钮结构 =====\n"
                            + BlueprintToggleButton.DebugDumpX());
                        Plugin.Log?.LogInfo("[SelfTest] ===== 选卡界面切换按钮 =====\n"
                            + BlueprintToggleButton.DebugForceShow());
                        Plugin.Log?.LogInfo("[SelfTest] " + BlueprintToggleButton.DebugToggle());
                        Plugin.Log?.LogInfo("[SelfTest] " + BlueprintToggleButton.DebugToggle());
                        // 这个按钮的 onClick 收不到点击 —— 用真射线看是谁挡在上面。
                        Plugin.Log?.LogInfo("[SelfTest] ===== 切换按钮射线 =====\n"
                            + BlueprintToggleButton.DebugRaycast());
                        // 真正要紧的：点落在按钮矩形里会不会切换（与 Frame 同一个函数）。
                        Plugin.Log?.LogInfo("[SelfTest] ===== 切换按钮点击通路 =====\n"
                            + BlueprintToggleButton.DebugClick());
                        // 点选走的是哪条通路（Frame 每帧 / Tick 30 帧一次）。
                        Plugin.Log?.LogInfo("[SelfTest] " + BlueprintPage.DebugClickPath());
                        // 镜像框里的卡也要有悬停提示。
                        Plugin.Log?.LogInfo("[SelfTest] 镜像框 " + BlueprintPage.DebugMirrorTooltips());
                        Plugin.Log?.LogInfo("[SelfTest] " + ChooseProbe.DumpPicker());
                        Plugin.Log?.LogInfo("[SelfTest] ===== 选卡浮层 =====\n"
                            + BlueprintPage.DebugOpenPicker() + "\n" + BlueprintPage.DebugPickInteract());
                        Plugin.Log?.LogInfo("[SelfTest] " + BlueprintPage.DebugGridWhere());
                        Plugin.Log?.LogInfo("[SelfTest] " + BlueprintPage.DebugClickAt(2));
                        step = 23;
                        frame = 0;
                        break;

                    case 23:
                        if (frame < ShotDelayFrames)
                            break;
                        Plugin.Log?.LogInfo("[SelfTest] " + BlueprintPage.DebugCompareIcon());
                        // 网格里的卡要挂上悬停提示（蓝图名 + 说明，没说明就用 id）。
                        Plugin.Log?.LogInfo("[SelfTest] ===== 蓝图提示 =====\n" + BlueprintPage.DebugTooltips());
                        // ★ 每一页都要有提示 —— 翻页只切 activeSelf，不会再走我们的刷新，
                        //   所以「翻页后 tooltip 不显示」这个 bug 就是这里看出来的。
                        Plugin.Log?.LogInfo("[SelfTest] ===== 浮层每页提示覆盖 =====\n"
                            + BlueprintPage.DebugTooltipCoverage());
                        Plugin.Log?.LogInfo("[SelfTest] 飞之前 " + BlueprintPage.DebugGridAlive());
                        DiagShots.Capture("pick");
                        pendingTag = "pick";
                        Plugin.Log?.LogInfo("[SelfTest] " + BlueprintPage.DebugPickState());
                        step = 24;
                        frame = 0;
                        break;

                    case 24: // 面板**继续开着**，只把游戏切成传送带 —— 看页集合会不会自己跟过来
                        {
                            if (frame < ShotDelayFrames)
                                break;
                            Plugin.Log?.LogInfo("[SelfTest] 加一张后 " + BlueprintPage.DebugGridAlive());
                            // 记下面板高度，一会儿和传送带页比（需求：切模式不能改高度）。
                            classicHeight = PanelHeight();
                            BlueprintPickWindow.HideAll();
                            // 卡池接管是 Full 档功能，自测里临时顶上去（只改内存）。
                            CompatConfig.DebugSetLevel(CompatLevel.Full);
                            // 等级刚改完、页面还没重铺 → 按钮还是灰的（这一行是「改之前」的证据）。
                            Plugin.Log?.LogInfo("[SelfTest] ===== 等级提到 Full，尚未重铺 =====\n"
                                + BlueprintPage.DebugGate());
                            // 页面的慢速刷新（Tick，30 帧一次）会重铺一次 → 被等级挡住的按钮变可点。
                            Plugin.Log?.LogInfo("[SelfTest] ===== 重铺一次后 =====\n"
                                + BlueprintPage.DebugRefresh());
                            // ★ 真的走一遍放置生成：标记只该作用在「那张卡放出来的实体」上，
                            //   而自然生成那条路（同一个实体 id）必须一点变化都没有。
                            Plugin.Log?.LogInfo("[SelfTest] ===== 放置探针 =====\n"
                                + BlueprintPage.DebugPlaceProbe());
                            var lv = LevelFacade.Current;
                            // 故意**不**调 LoadoutPanel.SyncPageSet()：这里要测的是
                            // 「按钮回调之外，Tick 里的兜底也会把页集合同步过来」。
                            // 走真正的按钮路径（含给本关加传送带行为）。
                            Plugin.Log?.LogInfo("[SelfTest] 切传送带模式（面板开着）：" + ConveyorRig.Enable(lv)
                                + "  " + ConveyorRig.Describe());
                            step = 25;
                            frame = 0;
                        }
                        break;

                    case 25:
                        if (frame < LayoutFrames)
                            break;
                        // 页集合应该已经被 Tick 里的 SyncPageSet 换掉了（"点按钮后立即切页"）。
                        var rep = LoadoutPanel.PageReport();
                        var isConveyor = rep != null && rep.IndexOf("卡池=") >= 0;
                        Plugin.Log?.LogInfo("[SelfTest] 切模式后未手动同步：页=" + LoadoutPanel.PageName
                            + "  共 " + LoadoutPanel.PageCount + " 页  判据 立即换页="
                            + (isConveyor ? "OK" : "NG（还是自选卡那一套）"));
                        if (!LoadoutPanel.Open())
                        {
                            Plugin.Log?.LogError("[SelfTest] 传送带面板打不开：" + LoadoutPanel.LastError);
                            Finish();
                            return;
                        }
                        Plugin.Log?.LogInfo("[SelfTest] 传送带页集合：共 " + LoadoutPanel.PageCount + " 页");
                        LoadoutPanel.ShowPage(1);
                        step = 26;
                        frame = 0;
                        break;

                    case 26:
                        if (frame < LayoutFrames)
                            break;
                        {
                            // 需求：从自选卡切到传送带**不能改变面板高度**。
                            var h = PanelHeight();
                            Plugin.Log?.LogInfo("[SelfTest] 面板高度 自选卡=" + classicHeight + " 传送带=" + h
                                + "  判据 切模式不改高度="
                                + (classicHeight > 0f && Mathf.Abs(classicHeight - h) < 0.5f ? "OK" : "NG"));
                        }
                        Plugin.Log?.LogInfo("[SelfTest] ===== 页「" + LoadoutPanel.PageName + "」（传送带） =====");
                        foreach (var line in PanelDump.Report().Split('\n'))
                            Plugin.Log?.LogInfo(line);
                        foreach (var line in LoadoutPanel.PageReport().Split('\n'))
                            Plugin.Log?.LogInfo("[SelfTest] " + line);
                        Plugin.Log?.LogInfo("[SelfTest] ===== 卡池选卡浮层 =====\n" + ConveyorPage.DebugOpenPicker());
                        // 选中一个 → 功能才该生效（“只有无条件放置对所有蓝图生效”）。
                        Plugin.Log?.LogInfo("[SelfTest] " + ConveyorPage.DebugSelectFirst());
                        step = 27;
                        frame = 0;
                        break;

                    case 27:
                        if (frame < ShotDelayFrames)
                            break;
                        DiagShots.Capture("conveyor");
                        pendingTag = "conveyor";
                        step = 28;
                        frame = 0;
                        break;

                    case 28:
                        if (frame < ShotDelayFrames)
                            break;
                        BlueprintPickWindow.HideAll();
                        // ===== 第三批需求的证据（都在面板开着、关卡在跑的时候取）=====
                        Plugin.Log?.LogInfo("[SelfTest] ===== 传送带页 = 卡槽镜像 =====\n"
                            + Phase3Probe.ConveyorMirror());
                        Plugin.Log?.LogInfo("[SelfTest] ===== 无限能量/星之碎片（补满 + 消耗即回满）=====\n"
                            + Phase3Probe.Refill());
                        Plugin.Log?.LogInfo("[SelfTest] ===== 出怪点数/倍数/幂数（填的就是生效的）=====\n"
                            + Phase3Probe.SpawnPoints());
                        Plugin.Log?.LogInfo("[SelfTest] ===== 禁用冷却（原版 NO_RECHARGE）=====\n"
                            + Phase3Probe.NoRechargeRound());
                        Plugin.Log?.LogInfo("[SelfTest] ===== 无条件放置（真门口：放行 + 真的生成）=====\n"
                            + Phase3Probe.UnconditionalPlaceRound());
                        Plugin.Log?.LogInfo("[SelfTest] ===== 模仿者 → 原版显示重画 =====\n"
                            + Phase3Probe.VanillaSyncRound());
                        Plugin.Log?.LogInfo("[SelfTest] ===== 选卡切换按钮图标 =====\n"
                            + BlueprintToggleButton.DebugIcon());
                        // 基础页的置灰状态必须**在本页是当前页**时读（页一换，上行控件就被清了）。
                        LoadoutPanel.ShowPage(0);
                        step = 29;
                        frame = 0;
                        break;

                    case 29:
                        if (frame < LayoutFrames)
                            break;
                        DiagShots.Capture("basic");
                        pendingTag = "basic";
                        Plugin.Log?.LogInfo("[SelfTest] ===== 基础页功能定档（Safe/Standard + 置灰原因）=====\n"
                            + LevelBasicPage.DebugGate());
                        if (LoadoutPanel.IsOpen)
                            LoadoutPanel.Close();
                        Plugin.Log?.LogInfo("[SelfTest] 传送带完成（截图 conveyor/basic）");
                        Finish();
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[SelfTest] 异常：" + e.GetType().Name + ": " + e.Message);
                Finish();
            }
        }

        private static void Dump(string where)
        {
            Plugin.Log?.LogInfo("[SelfTest] ===== 布局 dump：" + where + " =====");
            foreach (var line in PanelDump.DumpAll().Split('\n'))
                Plugin.Log?.LogInfo(line);
            foreach (var line in PanelDump.Report().Split('\n'))
                Plugin.Log?.LogInfo(line);
            loggedDump = true;
        }

        /// <summary>
        /// 档案面板的 dump（进度页要复用的「搜索框 + 可多选列表」就是它）。
        /// 只在关卡外有意义 —— 档案页是 Main 场景的页。
        /// </summary>
        private static void DumpArchive()
        {
            try
            {
                foreach (var line in ArchiveProbe.Dump().Split('\n'))
                    Plugin.Log?.LogInfo(line);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[SelfTest] 档案 dump 失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>进入第一关（与官方主菜单「开始游戏」同一条路）。</summary>
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
            Plugin.Log?.LogInfo("[SelfTest] 请求进入第一关 " + Id(area) + " / " + Id(stage));
            return LevelFacade.BeginLevelSwitch(area, stage, "自测");
        }

        /// <summary>NamespaceID 在 interop 里的 ToString 只会打印类型名，自己拼。</summary>
        private static string Id(NamespaceID id)
        {
            if (id == null)
                return "（空）";
            try { return id.SpaceName + ":" + id.Path; }
            catch (Exception) { return "（无法读取）"; }
        }

        /// <summary>
        /// 面板的屏幕高度（需求「切模式不改高度」靠它比）。
        ///
        /// <para>取的是**内容宿主** <c>LoadoutPanel.Content</c> —— 它就是 <c>PanelDump</c> 里
        /// 「面板完整在屏内」量的那个矩形，高度完全由页面加了几行、每行多高决定，
        /// 正是要比较的东西。对话框根（外壳）是拉伸到整屏的，量它恒等于屏高，比不出任何东西。</para>
        /// </summary>
        private static float PanelHeight()
        {
            try
            {
                var content = LoadoutPanel.Content;
                if (content == null)
                    return -1f;
                return Mathf.Round(PanelDump.ScreenRect(content).height);
            }
            catch (Exception) { return -1f; }
        }

        /// <summary>自选卡那一套时的面板高度（切模式前记录）。</summary>
        private static float classicHeight;

        private static void Finish()
        {
            step = -1;
            Plugin.Log?.LogInfo("[SelfTest] dump 已写入=" + (loggedDump ? 1 : 0)
                + "  蓝图补全=" + (AllBlueprintsPatch.ShowAll ? "全部" : "原版"));
            Plugin.Log?.LogInfo("[SelfTest] DONE");
        }
    }
}
