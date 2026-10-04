using System;
using MVZ2Logic.Level;
using PVZEngine.Level;

namespace Loadout
{
    /// <summary>
    /// 「隐藏HUD」—— 把关卡 HUD 一次藏干净，方便截图 / 看场地。
    ///
    /// 分两类，因为原版只给第一类开了官方接口：
    ///
    /// **① 有官方链路的五项**（需求列的：蓝图（含传送带）、能量、星之碎片、铁镐、驱动）
    /// <code>
    /// level.SetEnergyActive(bool)  → IUIComponent.SetEnergyActive
    ///                              → LevelController.EnergyActive { set; }
    ///                              → LevelUIPreset.SetEnergyActive
    /// </code>
    /// 关键是**中间那一步**：<c>LevelController</c> 自己缓存了
    /// <c>energyActive / blueprintsActive / pickaxeActive / starshardActive / triggerActive</c>
    /// 五个字段。之前我们绕过控制器、直接对场景里的 <c>LevelUIPreset</c> 调 SetActive，
    /// 控制器的缓存值于是仍然是 <c>true</c> —— 只要游戏之后重新走一次那条链路
    /// （进关卡 / 读档 / 关卡脚本），显示状态就被”刷回去“，表现为「隐藏GUI没生效」。
    ///
    /// **② 没有官方接口的项**（需求追加：关卡进度显示、菜单与加速按钮、拾取金币的金币数量）
    /// 这些只能拿 <c>LevelController.GetUIPreset()</c> 直接改：
    ///   · 进度条 → <c>level.LevelProgressVisible = false</c>（原版意图值，<c>RefreshProgressBar()</c>
    ///     就是读它，见 <c>LevelController_ProgressBar.cs</c>）**加上**
    ///     <c>preset.SetProgressBarVisible(false)</c>（立刻生效）。只按后者的话，只要游戏之后
    ///     走一次 UI 初始化 / 读档（<c>RefreshUIAtLevelStart</c>），进度条就会自己回来；
    ///   · 菜单 / 加速按钮 → <c>preset.menuButton / speedUpButton .gameObject.SetActive(false)</c>
    ///     （原版这两处只挂了 onClick，没有任何地方会重新 SetActive，压一次就够，仍然每帧压）；
    ///   · 金币数量 → 每帧 <c>preset.HideMoney()</c> + <c>level.SetMoneyFade(false)</c>（拾取金币时
    ///     <c>LevelController.Update/ShowMoney()</c> 会把它重新显出来，所以必须每帧压，不能只压一次）。
    ///
    /// ## 为什么要记住原值
    /// 有些关卡本来就故意关掉其中几项（例如 <c>IZombieBehaviour</c> 关掉星之碎片/铁镐/驱动，
    /// <c>StarshardTutorialStage</c> 关掉蓝图与铁镐）。如果“取消隐藏”时一律设成 true，
    /// 就会把这些关卡的设计破坏掉。所以隐藏前先读一次实际值，取消隐藏时按原值恢复。
    ///
    /// ## 为什么隐藏期间每帧重压一次
    /// <c>LevelController.StartGame_UI</c> / 关卡脚本 / 读档 / 拾取金币都会重新设置这些值。
    /// 隐藏状态下每帧压一次最省心（开销就十几次属性写）。
    /// </summary>
    internal static class LevelHud
    {
        public static bool Hidden;

        private static bool saved;
        private static bool savedEnergy = true;
        private static bool savedBlueprints = true;
        private static bool savedPickaxe = true;
        private static bool savedStarshard = true;
        private static bool savedTrigger = true;
        // ---- ② 类（直接改 LevelUIPreset / level 属性）----
        private static bool savedProgressFlag = true;
        private static bool savedProgressRoot = true;
        private static bool savedMenu = true;
        private static bool savedSpeedUp = true;

        private static bool warnedOnce;

        public static void Set(bool hidden)
        {
            var level = LevelFacade.Current;
            if (level == null)
            {
                Hidden = false;
                saved = false;
                return;
            }
            if (hidden && !Hidden)
                Capture(level);
            Hidden = hidden;
            if (hidden)
                Apply(level);
            else
                Restore(level);
            Plugin.Log?.LogInfo("[Hud] 关卡 HUD " + (hidden ? "已隐藏" : "已恢复")
                + "（蓝图/能量/星之碎片/铁镐/驱动/进度条/菜单按钮/加速按钮/金币数量）");
        }

        public static void Toggle()
        {
            Set(!Hidden);
        }

        /// <summary>每帧调用；只在隐藏状态做事。</summary>
        public static void Tick()
        {
            if (!Hidden)
                return;
            var level = LevelFacade.Current;
            if (level == null)
                return;
            Apply(level);
        }

        private static void Capture(LevelEngine level)
        {
            saved = true;
            savedEnergy = Read(level, "energy", x => x.IsEnergyActive(), true);
            savedBlueprints = Read(level, "blueprints", x => x.AreBlueprintsActive(), true);
            savedPickaxe = Read(level, "pickaxe", x => x.IsPickaxeActive(), true);
            savedStarshard = Read(level, "starshard", x => x.IsStarshardActive(), true);
            savedTrigger = Read(level, "trigger", x => x.IsTriggerActive(), true);
            var ui = Preset();
            savedProgressFlag = ProgressFlag(level);
            savedProgressRoot = Active(ui, "progress");
            savedMenu = ButtonActive(ui, true);
            savedSpeedUp = ButtonActive(ui, false);
        }

        private static void Apply(LevelEngine level)
        {
            Write(level, "energy", x => x.SetEnergyActive(false));
            Write(level, "blueprints", x => x.SetBlueprintsActive(false));
            Write(level, "pickaxe", x => x.SetPickaxeActive(false));
            Write(level, "starshard", x => x.SetStarshardActive(false));
            Write(level, "trigger", x => x.SetTriggerActive(false));
            var ui = Preset();
            // 进度条要压**两个地方**：
            //   ① level.LevelProgressVisible —— 原版的「意图」值。它只在两处被读：
            //      LevelController.Awake_ProgressBar()（关）与 RefreshProgressBar()（按此值设）。后者
            //      只在 UI 初始化 / 读档时调用，所以改了属性就能堵住「之后重刷又显示出来」。
            //   ② ui.SetProgressBarVisible(false) —— 立刻生效（progressBarRoot.SetActive）。
            Guard("progress", () =>
            {
                level.LevelProgressVisible = false;
                ui.SetProgressBarVisible(false);
            });
            Guard("menu", () => SetButtonActive(ui, true, false));
            Guard("speedup", () => SetButtonActive(ui, false, false));
            // 金币数量：拾取时会重新显出来，所以每帧压一次。
            Guard("money", () =>
            {
                ui.HideMoney();
                level.SetMoneyFade(false);
            });
        }

        private static void Restore(LevelEngine level)
        {
            if (!saved)
                return;
            saved = false;
            Write(level, "energy", x => x.SetEnergyActive(savedEnergy));
            Write(level, "blueprints", x => x.SetBlueprintsActive(savedBlueprints));
            Write(level, "pickaxe", x => x.SetPickaxeActive(savedPickaxe));
            Write(level, "starshard", x => x.SetStarshardActive(savedStarshard));
            Write(level, "trigger", x => x.SetTriggerActive(savedTrigger));
            var ui = Preset();
            Guard("progress", () =>
            {
                // 按隐藏前的「意图 + 实际可见」两个值分别还原，免得把关卡本来的设计改掉
                // （有些关卡 Awake 后就没让进度条显示出来，虽然 LevelProgressVisible 仍是 true）。
                level.LevelProgressVisible = savedProgressFlag;
                ui.SetProgressBarVisible(savedProgressRoot);
            });
            Guard("menu", () => SetButtonActive(ui, true, savedMenu));
            Guard("speedup", () => SetButtonActive(ui, false, savedSpeedUp));
            // 把金币面板交还给游戏（下一次拾取会自然把它显示出来）。
            Guard("money", () =>
            {
                level.SetMoneyFade(true);
                level.ShowMoney();
            });
        }

        /// <summary>关卡 UI 预设 —— 进度条 / 菜单 / 加速 / 金币面板这些只能从这里拿。</summary>
        private static MVZ2.Level.UI.LevelUIPreset Preset()
        {
            try
            {
                var controller = LevelFacade.Controller;
                return controller == null ? null : controller.GetUIPreset();
            }
            catch (Exception e)
            {
                Warn("preset", e);
                return null;
            }
        }

        private static bool Active(MVZ2.Level.UI.LevelUIPreset ui, string what)
        {
            try
            {
                return ui == null || ui.progressBarRoot == null || ui.progressBarRoot.activeSelf;
            }
            catch (Exception e)
            {
                Warn(what, e);
                return true;
            }
        }

        /// <summary>原版自己维护的「关卡进度条该不该显示」（RefreshProgressBar 就是读它）。</summary>
        private static bool ProgressFlag(LevelEngine level)
        {
            try
            {
                return level.LevelProgressVisible;
            }
            catch (Exception e)
            {
                Warn("progressFlag", e);
                return true;
            }
        }

        /// <summary>菜单按钮 / 加速按钮的显隐（<paramref name="menu"/> 选哪一个）。</summary>
        private static bool ButtonActive(MVZ2.Level.UI.LevelUIPreset ui, bool menu)
        {
            try
            {
                var button = menu ? ui.menuButton : ui.speedUpButton;
                if (button == null)
                    return true;
                var go = button.gameObject;
                return go == null || go.activeSelf;
            }
            catch (Exception e)
            {
                Warn(menu ? "menu" : "speedup", e);
                return true;
            }
        }

        private static void SetButtonActive(MVZ2.Level.UI.LevelUIPreset ui, bool menu, bool active)
        {
            var button = menu ? ui.menuButton : ui.speedUpButton;
            if (button == null)
                return;
            var go = button.gameObject;
            if (go != null)
                go.SetActive(active);
        }

        private static void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Warn(what, e);
            }
        }

        private static bool Read(LevelEngine level, string what, Func<LevelEngine, bool> get, bool fallback)
        {
            try
            {
                return get(level);
            }
            catch (Exception e)
            {
                Warn(what, e);
                return fallback;
            }
        }

        private static void Write(LevelEngine level, string what, Action<LevelEngine> set)
        {
            try
            {
                set(level);
            }
            catch (Exception e)
            {
                Warn(what, e);
            }
        }

        private static void Warn(string what, Exception e)
        {
            if (warnedOnce)
                return;
            warnedOnce = true;
            Plugin.Log?.LogWarning("[Hud] 操作「" + what + "」失败：" + e.GetType().Name + ": " + e.Message);
        }

        /// <summary>离开关卡时复位，免得下一个关卡一开始就是隐藏的。</summary>
        public static void ResetOnLevelChange()
        {
            if (Hidden)
            {
                // 离开前先把 HUD 恢复（此时还拿得到旧 level）。
                Restore(LevelFacade.Current);
            }
            Hidden = false;
            saved = false;
        }

        public static string Describe()
        {
            var level = LevelFacade.Current;
            if (level == null)
                return "HUD: 不在关卡内";
            var ui = Preset();
            return "HUD 隐藏=" + (Hidden ? 1 : 0)
                + "  蓝图=" + State(() => level.AreBlueprintsActive())
                + " 能量=" + State(() => level.IsEnergyActive())
                + " 星之碎片=" + State(() => level.IsStarshardActive())
                + " 铁镐=" + State(() => level.IsPickaxeActive())
                + " 驱动=" + State(() => level.IsTriggerActive())
                + " 进度条=" + OnOff(ui == null || ui.progressBarRoot == null || ui.progressBarRoot.activeSelf)
                + "(标志" + OnOff(ProgressFlag(level)) + ")"
                + " 菜单按钮=" + OnOff(ButtonActive(ui, true))
                + " 加速按钮=" + OnOff(ButtonActive(ui, false));
        }

        private static string OnOff(bool on)
        {
            return on ? "on" : "off";
        }

        private static string State(Func<bool> get)
        {
            try
            {
                return get() ? "on" : "off";
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}
