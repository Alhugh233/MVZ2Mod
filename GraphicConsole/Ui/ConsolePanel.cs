using System;
using MVZ2.Managers;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace GraphicConsole
{
    /// <summary>
    /// 面板总管：场景/关卡切换、页面装配、每帧刷新。
    ///
    /// 全部是静态状态 —— 本 mod **不注入任何 MonoBehaviour**（见 README §1.1 关于注入类的坑），
    /// 心跳来自 <see cref="PanelTickPatch"/>（挂在官方本来就每帧跑的 MainSceneController.Update 上）。
    ///
    /// ## 入口与热键
    /// **默认不注册任何热键**（配置项 <c>General.PanelHotkey</c> 留空）。
    /// 原因：原版 debug 模式下 F1~F7 全被官方调试键占用
    /// （F2=保存关卡、F3=重载关卡、F7=高级暂停…），注册热键必然打架。
    /// 默认入口是左上角那个半透明蜘蛛图标（<see cref="GraphicConsoleIcon"/>），或 <c>/gc open</c>。
    ///
    /// ## 暂停
    /// 面板不再伪装"控制台开着"。暂停由「后台暂停」开关统一管理
    /// （原版全局选项 <c>pause_on_focus_lost</c> + 面板打开时速度=0），见 <see cref="PanelPause"/>。
    /// 这样面板开着也能操作游戏（蓝图 / 镐子 / 星之碎片 / 草坪点击）。
    /// </summary>
    internal static class ConsolePanel
    {
        /// <summary>面板是否显示中。</summary>
        public static bool IsOpen { get { return shell != null && shell.IsOpen; } }

        /// <summary>面板根对象（给布局 dump 用）。</summary>
        public static GameObject RootObject { get { return shell == null ? null : shell.Root; } }

        private static PanelShell shell;
        private static bool builtForLevel;
        private static bool hasBuilt;
        private static int lastSceneHandle = int.MinValue;
        private static int tickCounter;
        private static UnityEngine.KeyCode hotkey = UnityEngine.KeyCode.None;

        public static string LastError = string.Empty;

        #region 心跳
        public static void Tick()
        {
            tickCounter++;
            try
            {
                HandleSceneChange();
                HandleLevelTransition();
                if (tickCounter % 30 == 0)
                    hotkey = CompatConfig.ResolveHotkey();

                HandleHotkey();

                if (IsOpen && shell != null)
                {
                    shell.TickDrag();
                    // 下拉格的点击兜底：不依赖 UI 事件系统（见 UiFactory.TickDropdowns）。
                    UiFactory.TickDropdowns();
                    // 页面状态同步：原版的值可能被别的途径改掉（例如游戏自己推进波数），
                    // 所以周期性拉一次。30fps 足够，不必每帧。
                    if (tickCounter % 2 == 0)
                        shell.Refresh();
                }

                LevelHud.Tick();
                // 选卡界面上的「全部蓝图 / 原版蓝图」切换按钮（面板面板显示时才建）。
                BlueprintListToggle.Tick();
                // 关卡切换（重载 / 自测进关卡）在主线程轮询 Task —— 见 LevelFacade.BeginLevelSwitch。
                LevelFacade.TickPending();
                SelfTest.Tick();
            }
            catch (Exception e)
            {
                LastError = e.ToString();
                Plugin.Log?.LogError("[Panel] Tick 异常：" + e);
            }
        }

        /// <summary>
        /// 热键。**默认没热键**（<see cref="CompatConfig.ResolveHotkey"/> 返回 None），
        /// 配了才生效；正在输入框里打字时不响应（把热键设成字母键时的保护）。
        /// </summary>
        private static void HandleHotkey()
        {
            if (hotkey == UnityEngine.KeyCode.None)
                return;
            if (!Input.GetKeyDown(hotkey))
                return;
            if (IsTypingInInputField())
                return;
            Toggle();
        }

        private static bool IsTypingInInputField()
        {
            try
            {
                var es = EventSystem.current;
                if (es == null)
                    return false;
                var selected = es.currentSelectedGameObject;
                if (selected == null)
                    return false;
                return selected.GetComponent<TMPro.TMP_InputField>() != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 关卡进出 / 关卡切换时**自动关面板**。
        ///
        /// 需求：“开着面板进入或退出关卡的时候应该自动关闭面板，防止用户把关卡内面板带到关卡外”。
        /// 场景真的换了时 <see cref="HandleSceneChange"/> 已经把面板丢了；但有两种情况不会换场景：
        ///   · 关卡切换过程中的中间态（选卡界面 / 过场 / 返回地图，都在同一个 Main 场景里）；
        ///   · 退出关卡回到地图后又被同一套 UI 预设复用。
        /// 那两种情况下面板会“跟着漂”到关卡外，而且它的页签集还是关卡内那一套（只注册了页面1）。
        /// 所以这里盯住「是否在关卡内」这个跳变，一变就关。
        /// </summary>
        private static void HandleLevelTransition()
        {
            var inLevel = LevelFacade.InLevel;
            var switching = LevelFacade.SwitchingLevel;
            if (!levelStateKnown)
            {
                levelStateKnown = true;
                lastInLevel = inLevel;
                return;
            }
            if (switching)
            {
                // 关卡正在切换（进关卡 / 重载 / 返回地图）。
                if (IsOpen)
                {
                    Close();
                    Plugin.Log?.LogInfo("[Panel] 关卡正在切换，面板已自动关闭");
                }
                return;
            }
            if (inLevel == lastInLevel)
                return;
            var entering = inLevel;
            lastInLevel = inLevel;
            if (IsOpen)
            {
                Close();
                Plugin.Log?.LogInfo("[Panel] 已" + (entering ? "进入" : "离开")
                    + "关卡，面板已自动关闭（防止跨关卡带着面板）");
            }
        }

        private static bool levelStateKnown;
        private static bool lastInLevel;

        /// <summary>
        /// 场景切换时丢掉旧面板。
        ///
        /// 判定只看**场景句柄 + 面板根是否还活着**，不比较任何控制器实例 ——
        /// 控制器实例在关卡重建时会变，用它判定会让"关掉面板再打开"偶尔误触发一次重建，
        /// 而重建中途失败就会留下一个"半成品面板"（标题与页签还是原版文案）。
        /// </summary>
        private static void HandleSceneChange()
        {
            var handle = -1;
            try
            {
                handle = SceneManager.GetActiveScene().handle;
            }
            catch (Exception)
            {
            }

            var sceneChanged = handle != lastSceneHandle;
            if (sceneChanged)
            {
                lastSceneHandle = handle;
                if (shell != null && shell.IsOpen)
                    shell.Close();
                shell = null;
                hasBuilt = false;
                UiFactory.ClearKeepAlive();
                UiFactory.ClearDropdowns();
                BlueprintListToggle.Reset();
                UiSources.Prepare(true);
                GraphicConsoleIcon.ResetOnSceneChange();
                LevelHud.ResetOnLevelChange();
                Plugin.Log?.LogInfo("[Panel] 场景已切换（handle=" + handle + "），面板引用已重置");
                return;
            }

            // 同一场景内面板根被销毁（关卡重载 / 重新初始化）→ 下一次打开时重建。
            if (hasBuilt && (shell == null || !shell.IsBuilt))
            {
                hasBuilt = false;
                shell = null;
                LevelHud.ResetOnLevelChange();
                Plugin.Log?.LogInfo("[Panel] 面板根已失效（关卡被重建？），下次打开时重建");
            }
        }
        #endregion

        #region 开关
        public static void Toggle()
        {
            if (IsOpen)
                Close();
            else
                Open();
        }

        public static void Open()
        {
            var inLevel = LevelFacade.InLevel;
            if (!EnsureShell(inLevel))
            {
                ShowFailureTip();
                return;
            }
            if (shell.IsOpen)
                return;
            shell.Open();
        }

        public static void Close()
        {
            if (shell != null)
                shell.Close();
        }

        private static bool EnsureShell(bool inLevel)
        {
            if (hasBuilt && shell != null && shell.IsBuilt && builtForLevel == inLevel)
                return true;

            if (shell != null)
                shell.Destroy();

            shell = new PanelShell();
            builtForLevel = inLevel;

            if (inLevel)
                shell.AddPage(new LevelBasicPage());
            else
                shell.AddPage(new GlobalProgressPage());

            hasBuilt = shell.Build();
            if (!hasBuilt)
                LastError = shell.BuildReport;
            return hasBuilt;
        }

        /// <summary>模板缺失时用原版控制台兜底 + 打一条明显的日志，别让用户对着"没反应"发呆。</summary>
        private static void ShowFailureTip()
        {
            Plugin.Log?.LogError("[Panel] 面板不可用：" + LastError);
            try
            {
                var main = MainManager.Instance;
                if (main == null || main.Scene == null)
                    return;
                main.Scene.Print("> /gc\n");
                main.Scene.Print("图形化控制台面板不可用：" + LastError + "\n");
                main.Scene.DisplayConsole();
            }
            catch (Exception)
            {
            }
        }
        #endregion

        /// <summary>给 /gc dump 用的诊断文本。</summary>
        public static string Describe()
        {
            var lines = new System.Collections.Generic.List<string>();
            lines.Add("面板: " + (hasBuilt ? "已构建" : "未构建")
                + "，打开=" + (IsOpen ? 1 : 0)
                + "，按关卡内装配=" + (builtForLevel ? 1 : 0));
            lines.Add("暂停: 后台暂停开关=" + (PanelPause.Enabled ? "开" : "关")
                + "，本帧速度归零=" + (PanelPause.Active ? "是" : "否"));
            lines.Add("兼容性等级: " + CompatConfig.Level);
            foreach (GcFeature f in Enum.GetValues(typeof(GcFeature)))
                lines.Add("  特性 " + CompatConfig.NameOf(f) + " = " + (CompatConfig.IsEnabled(f) ? "启用" : "关闭"));
            lines.Add("热键: " + (string.IsNullOrEmpty(CompatConfig.PanelHotkeyText)
                    ? "（未注册，用蜘蛛图标或 /gc open）"
                    : CompatConfig.PanelHotkeyText)
                + "，原版控制台仍是 '/'");
            lines.Add("缩放: " + GcLayout.Scale + "，设计尺寸 " + GcLayout.Width + "x" + GcLayout.Height);
            lines.Add("模板: " + UiSources.LastReport);
            lines.Add(GraphicConsoleIcon.Describe());
            lines.Add("在关卡内: " + (LevelFacade.InLevel ? "是" : "否"));
            lines.Add(LevelHud.Describe());
            if (shell != null)
                lines.Add("页面数: " + shell.Pages.Count + "，当前页=" + shell.CurrentIndex
                    + "，构建报告=" + shell.BuildReport);
            lines.Add("保活委托: " + UiFactory.KeepAliveCount);
            if (!string.IsNullOrEmpty(LastError))
                lines.Add("最近错误: " + LastError);
            return string.Join("\n", lines.ToArray());
        }
    }
}
