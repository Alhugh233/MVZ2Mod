using System;
using BepInEx.Configuration;

namespace GraphicConsole
{
    /// <summary>
    /// 兼容性等级 —— 本 mod 的「危险功能」总闸。
    ///
    /// 背景：本 mod 是测试工具，目标是**尽可能不干扰其它 mod**。但有些功能天生要
    /// 「接管」游戏里别的方法（例如面板打开时把游戏速度压到 0、把选卡列表补全），
    /// 这类接管一旦和别的 mod 的补丁撞上就难查。
    /// 于是把它们分级：等级越低，接管越少；出问题时降一级就能定位。
    ///
    /// 注意：补丁本身**始终会挂上**，是否生效在**运行时**判断（见 <see cref="IsEnabled"/>）。
    /// 这样改等级立刻生效，不需要重启游戏，也不会因为「补丁没挂」而留下行为空洞。
    /// </summary>
    internal enum CompatLevel
    {
        /// <summary>
        /// 只做面板本身：读写原版**已有**的关卡属性、调用原版**已有**的接口、克隆原版 UI。
        /// 不改变任何既有方法的返回值/参数，理论上不可能与别的 mod 冲突。
        /// 代价：面板打开时不冻结关卡、选卡列表仍是原样、左上角图标不接管。
        /// </summary>
        Safe = 0,

        /// <summary>
        /// 默认档。在 Safe 之上额外接管：
        ///   ① 面板打开时把游戏速度压到 0（冻结关卡逻辑但保留全部游戏内操作）
        ///   ② 选卡界面显示所有实体蓝图（在官方排序结果**之后追加**，不替换）
        ///   ③ 左上角蜘蛛图标作为面板入口
        /// 这三条都只做「追加 / 只读 / 只改返回值」的改动，不改动官方原有行为。
        /// </summary>
        Standard = 1,

        /// <summary>
        /// 全部功能。包含需要**替换**官方行为、可能与其他 mod 直接竞争的功能
        /// （目前只有预留的「传送带卡池接管」）。
        /// </summary>
        Full = 2,
    }

    /// <summary>单个功能的开关。<see cref="Auto"/> = 跟随兼容性等级。</summary>
    internal enum FeatureMode
    {
        Auto = 0,
        On = 1,
        Off = 2,
    }

    /// <summary>可以被兼容性等级 / 单项开关关掉的功能。</summary>
    internal enum GcFeature
    {
        /// <summary>
        /// 面板打开时把游戏速度压到 0（postfix <c>LevelController.GetGameSpeed</c>）。
        ///
        /// 为什么用这个而不是原版控制台那套暂停：把 <c>DebugConsoleController.IsActive</c>
        /// 伪装成 true 会让 <c>IsGameRunning()</c> 变 false —— 于是 <c>UpdateLogic</c> 整段跳过、
        /// 键盘与草坪射线全被 Input 层挡住，"面板开着就没法玩"。而 <c>GetGameSpeed()</c>
        /// 只影响**推进速度**（<c>UpdateLogicRunning</c> 里 <c>times=(int)speed</c> 为 0 就不推进），
        /// <c>IsGameRunning()</c> 与 <c>IsInputDisabled()</c> 都不看它，
        /// 所以关卡冻结、但蓝图 / 镐子 / 星之碎片照常可用 —— 正是需求要的
        /// 「将游戏速度调至 0，等效速度倍率为 0 的加速」。
        /// </summary>
        PanelSpeedZeroPause,

        /// <summary>
        /// 选卡界面显示所有实体蓝图：在 <c>AlmanacManager.GetOrderedContraptionsByAlmanac</c>
        /// 之后往列表里**追加**图鉴里没有的器械（该函数在全工程只有一个调用点，就是选卡界面）。
        /// </summary>
        AllBlueprintsInChoose,

        /// <summary>
        /// 左上角那个半透明蜘蛛图标（原版调试图标）改为**打开面板**。
        /// 原理：复制一份原图标、绑到面板，并隐藏原图标。
        /// </summary>
        DebugIconOpensPanel,

        /// <summary>传送带卡池接管（尚未实现，预留；实现后属于 Full 档）。</summary>
        ConveyorTakeover,
    }

    /// <summary>配置读写 + 功能开关判定。</summary>
    internal static class CompatConfig
    {
        // ---- BepInEx 配置项 ----------------------------------------------
        // 用 string 而不是 enum 绑定：BepInEx 的枚举转换器行为随版本变化，
        // 自己解析最稳，而且配置文件里的值对人的可读性更好。
        private static ConfigEntry<string> levelEntry;
        private static ConfigEntry<string> speedZeroEntry;
        private static ConfigEntry<string> allBlueprintsEntry;
        private static ConfigEntry<string> iconEntry;
        private static ConfigEntry<string> conveyorEntry;
        private static ConfigEntry<string> panelHotkeyEntry;
        private static ConfigEntry<string> selfTestEntry;

        private static CompatLevel level = CompatLevel.Standard;

        /// <summary>当前等级（已解析）。</summary>
        internal static CompatLevel Level { get { return level; } }

        /// <summary>面板热键原始字符串。空串 = 不注册热键。</summary>
        internal static string PanelHotkeyText
        {
            get { return panelHotkeyEntry == null ? string.Empty : (panelHotkeyEntry.Value ?? string.Empty).Trim(); }
        }

        /// <summary>
        /// 自测模式（见 <see cref="SelfTest"/>）：空 = 关闭。
        /// 取值 <c>menu</c> / <c>level</c> / <c>all</c>，会自动开关面板并把运行时布局打进日志。
        /// </summary>
        internal static string SelfTestMode
        {
            get { return selfTestEntry == null ? string.Empty : (selfTestEntry.Value ?? string.Empty).Trim(); }
        }

        private const string DefaultLevelText = "Standard";

        internal static void Init(ConfigFile cfg)
        {
            levelEntry = cfg.Bind(
                "Compat", "CompatLevel", DefaultLevelText,
                "兼容性等级：Safe / Standard / Full。\n" +
                "Safe     = 只用原版已有的属性与接口，不挂任何会改行为的补丁（最不可能与其它 mod 冲突）。\n" +
                "Standard = 默认。额外接管「面板打开时游戏速度=0」「选卡显示所有蓝图」「蜘蛛图标作为入口」。\n" +
                "Full     = 全部功能，含需要替换官方行为的危险功能（如传送带卡池接管）。");

            speedZeroEntry = cfg.Bind(
                "Compat.Features", "PanelSpeedZeroPause", "Auto",
                "Auto / On / Off。面板打开时让 LevelController.GetGameSpeed 返回 0" +
                "（冻结关卡逻辑，保留蓝图/镐子/星之碎片等游戏内操作）。");

            allBlueprintsEntry = cfg.Bind(
                "Compat.Features", "AllBlueprintsInChoose", "Auto",
                "Auto / On / Off。选卡界面显示所有（含不在图鉴内的）实体蓝图。只追加、不替换官方排序结果。");

            iconEntry = cfg.Bind(
                "Compat.Features", "DebugIconOpensPanel", "Auto",
                "Auto / On / Off。把左上角的半透明蜘蛛图标（原版调试图标）改为打开面板。");

            conveyorEntry = cfg.Bind(
                "Compat.Features", "ConveyorTakeover", "Auto",
                "Auto / On / Off。传送带卡池接管（尚未实现，预留）。属 Full 档。");

            panelHotkeyEntry = cfg.Bind(
                "General", "PanelHotkey", "",
                "打开图形化控制台面板的按键（UnityEngine.KeyCode 名，如 F5 / BackQuote）。\n" +
                "**默认留空 = 不注册任何热键** —— 原版 debug 模式下 F1~F7 全被官方的调试键占用\n" +
                "（F2=保存关卡、F3=重载关卡、F7=高级暂停…），注册热键会与它们打架。\n" +
                "默认入口是左上角那个半透明蜘蛛图标，或 /gc open。");

            selfTestEntry = cfg.Bind(
                "General", "SelfTest", "",
                "自测脚手架（排障用）：空 = 关闭。\n" +
                "menu  = 在主界面自动打开面板、把运行时布局打进 BepInEx 日志、再关闭；\n" +
                "level = 额外自动进第一关，并在关卡内再 dump 一次；\n" +
                "all   = 两者都做。");

            levelEntry.SettingChanged += OnLevelChanged;
            level = ParseLevel(levelEntry.Value, out var note);
            Plugin.Log?.LogInfo("[Compat] 兼容性等级 = " + level + note);
            LogFeatureState();
        }

        private static void OnLevelChanged(object sender, EventArgs e)
        {
            level = ParseLevel(levelEntry.Value, out var note);
            Plugin.Log?.LogInfo("[Compat] 兼容性等级已改为 " + level + note);
            LogFeatureState();
        }

        private static CompatLevel ParseLevel(string text, out string note)
        {
            note = string.Empty;
            if (string.IsNullOrEmpty(text))
                return CompatLevel.Standard;
            switch (text.Trim().ToLowerInvariant())
            {
                case "safe":
                case "0":
                    return CompatLevel.Safe;
                case "standard":
                case "1":
                    return CompatLevel.Standard;
                case "full":
                case "2":
                    return CompatLevel.Full;
                default:
                    note = "（无法识别「" + text + "」，按 Standard 处理）";
                    return CompatLevel.Standard;
            }
        }

        /// <summary>某个功能至少要达到的等级。</summary>
        private static CompatLevel MinLevel(GcFeature feature)
        {
            switch (feature)
            {
                case GcFeature.PanelSpeedZeroPause:
                case GcFeature.AllBlueprintsInChoose:
                case GcFeature.DebugIconOpensPanel:
                    return CompatLevel.Standard;
                case GcFeature.ConveyorTakeover:
                    return CompatLevel.Full;
                default:
                    return CompatLevel.Full;
            }
        }

        private static ConfigEntry<string> EntryOf(GcFeature feature)
        {
            switch (feature)
            {
                case GcFeature.PanelSpeedZeroPause: return speedZeroEntry;
                case GcFeature.AllBlueprintsInChoose: return allBlueprintsEntry;
                case GcFeature.DebugIconOpensPanel: return iconEntry;
                case GcFeature.ConveyorTakeover: return conveyorEntry;
                default: return null;
            }
        }

        internal static string NameOf(GcFeature feature)
        {
            switch (feature)
            {
                case GcFeature.PanelSpeedZeroPause: return "面板暂停（速度=0）";
                case GcFeature.AllBlueprintsInChoose: return "选卡显示所有蓝图";
                case GcFeature.DebugIconOpensPanel: return "蜘蛛图标作为面板入口";
                case GcFeature.ConveyorTakeover: return "传送带卡池接管";
                default: return feature.ToString();
            }
        }

        /// <summary>该功能当前是否生效。总闸 = 等级 + 单项开关。</summary>
        internal static bool IsEnabled(GcFeature feature)
        {
            var entry = EntryOf(feature);
            if (entry != null)
            {
                switch (ParseMode(entry.Value))
                {
                    case FeatureMode.On: return true;
                    case FeatureMode.Off: return false;
                }
            }
            return level >= MinLevel(feature);
        }

        private static FeatureMode ParseMode(string text)
        {
            if (string.IsNullOrEmpty(text))
                return FeatureMode.Auto;
            switch (text.Trim().ToLowerInvariant())
            {
                case "on":
                case "true":
                case "1":
                    return FeatureMode.On;
                case "off":
                case "false":
                case "0":
                    return FeatureMode.Off;
                default:
                    return FeatureMode.Auto;
            }
        }

        /// <summary>把各功能的开关状态打一行日志（改等级时也打，便于定位）。</summary>
        internal static void LogFeatureState()
        {
            var log = Plugin.Log;
            if (log == null)
                return;
            foreach (GcFeature f in Enum.GetValues(typeof(GcFeature)))
            {
                log.LogInfo("[Compat]   " + NameOf(f) + " = " + (IsEnabled(f) ? "启用" : "关闭")
                    + "（最低等级 " + MinLevel(f) + "）");
            }
        }

        /// <summary>把等级写回配置文件（设置页用）。</summary>
        internal static void SetLevel(CompatLevel value)
        {
            if (levelEntry == null)
                return;
            levelEntry.Value = value.ToString();
            // SettingChanged 会同步 level 字段
        }

        /// <summary>把热键写回配置文件。</summary>
        internal static void SetPanelHotkey(string keyName)
        {
            if (panelHotkeyEntry == null)
                return;
            panelHotkeyEntry.Value = keyName ?? string.Empty;
        }

        /// <summary>
        /// 解析热键名 → KeyCode。
        /// ⚠️ **空串 / 无法解析一律返回 <c>None</c>（= 不注册热键）**，而不是"退回 F2"：
        /// 默认不该占键，原版 debug 模式下 F1~F7 全是官方调试键。
        /// </summary>
        internal static UnityEngine.KeyCode ResolveHotkey()
        {
            var text = PanelHotkeyText;
            if (string.IsNullOrEmpty(text))
                return UnityEngine.KeyCode.None;
            try
            {
                return (UnityEngine.KeyCode)Enum.Parse(typeof(UnityEngine.KeyCode), text, true);
            }
            catch (Exception)
            {
                Plugin.Log?.LogWarning("[Compat] 无法解析热键「" + text + "」，本次不注册热键");
                return UnityEngine.KeyCode.None;
            }
        }
    }
}
