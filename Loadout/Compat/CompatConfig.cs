using System;
using BepInEx.Configuration;

namespace Loadout
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
        /// 只做「存档修改器也能直接做」的事：写原版**已有**的关卡属性、挂原版**已有**的
        /// debug buff、改卡槽/卡池、换掉某张卡的冷却定义（只写内存）。
        /// 不挂任何会改行为的补丁，也**不落盘自定义存档数据**（标记只在本次会话内存里）。
        /// </summary>
        Safe = 0,

        /// <summary>
        /// 默认档。在 Safe 之上额外：
        ///   ① 会被官方方法结果影响的补丁（面板暂停 / 选卡显示全部蓝图 / 蜘蛛图标入口）；
        ///   ② 只在生成期改自家标记对象的补丁（蓝图血量不睡觉 / 逆阵营放置 / 无条件放置）；
        ///   ③ 传送带行为接管；
        ///   ④ **自定义存档数据落盘**（标记、冷却备份写进 .loadoutflags）。
        /// 这些都不与其它 mod 直接竞争。
        /// </summary>
        Standard = 1,

        /// <summary>
        /// 全部功能。留给「需要替换、接管或可能与其它 mod 直接竞争」的功能（目前没有）。
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

        /// <summary>
        /// 传送带卡池接管：把卡池写到关卡属性上（原版只写在关卡定义上），
        /// 并在关卡自己没有传送带行为时由 <c>ConveyorRig</c> 自己建一个、每帧驱动。
        /// 会让关卡**换一种玩法**（抽卡节奏不再由关卡定义决定）—— 属 Full 档。
        /// </summary>
        ConveyorTakeover,

        /// <summary>
        /// 逆阵营放置：被卡片标记的器械生成时翻到右阵营。
        /// 补丁点在生成漏斗 <c>LevelEngine.SpawnSourced</c>，**真的会改场上的敌我关系** —— 属 Full 档。
        /// </summary>
        ReverseFaction,

        /// <summary>
        /// 蓝图的两条生成期改动：**血量**（把指定蓝图的器械上限顶到面板设的数值）与
        /// **白天不睡觉**（把「夜用」器械刚中的 <c>NocturnalBuff</c> 摘掉）。
        ///
        /// <para>补丁点与 <see cref="ReverseFaction"/> 同一处，但**只对被标记的那几张卡生效**，
        /// 不改变任何官方方法的返回值 —— 属 Standard 档。</para>
        /// </summary>
        BlueprintOverrides,

        /// <summary>
        /// 无条件放置：短路官方那道「放得下吗」的判定（<c>PlacementDefinition.GetPlaceError</c>），
        /// 并在放置时自己把实体生成出来。
        /// 只对**被标记的卡**生效、不与其它 mod 竞争 —— 属 Standard 档。
        /// </summary>
        UnconditionalPlace,

        /// <summary>
        /// 「禁用冷却」：把卡槽里每张卡的冷却换成「无」，并在开启前**备份**原值（含玩家改过的值），
        /// 关闭时换回去。仅仅是把已有的冷却定义换一个、再写回属性 ——
        /// 存档修改器能直接做到，属 <b>Safe</b> 档。
        /// </summary>
        NoRecharge,

        /// <summary>
        /// **把自定义数据写进存档**（关卡存档旁边的 <c>.loadoutflags</c>：标记 + 冷却备份）。
        ///
        /// <para>关掉它 = 所有标记只在**本次会话的内存**里生效，不落盘（属 Safe 档的行为）。
        /// 这是需求明确要求的：「禁用冷却在存档里写自定义数据的功能仅在 Standard 下实现，
        /// Safe 模式只写到内存」—— 同一条门槛也管其它标记。</para>
        /// </summary>
        SaveCustomData,
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
        private static ConfigEntry<string> reverseFactionEntry;
        private static ConfigEntry<string> blueprintOverridesEntry;
        private static ConfigEntry<string> unconditionalPlaceEntry;
        private static ConfigEntry<string> noRechargeEntry;
        private static ConfigEntry<string> saveCustomDataEntry;
        private static ConfigEntry<string> panelHotkeyEntry;
        private static ConfigEntry<string> selfTestEntry;

        private static CompatLevel level = CompatLevel.Standard;

        /// <summary>当前等级（已解析）。</summary>
        internal static CompatLevel Level { get { return level; } }

        /// <summary>
        /// <b>仅自测用</b>：临时把总等级顶上去（只改内存，不碰配置文件）。
        /// 自测要验「传送带卡池接管」这类 Full 档功能时用。
        /// </summary>
        internal static void DebugSetLevel(CompatLevel value)
        {
            level = value;
            Plugin.Log?.LogInfo("[Compat] （自测）兼容性等级临时改为 " + level);
            LogFeatureState();
        }

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
            get
            {
                var v = selfTestEntry == null ? string.Empty : (selfTestEntry.Value ?? string.Empty).Trim();
                // 取值表现在是 off/menu/level/all（见 SelfTestValues）：把 off 归一成「关闭」，
                // 其余调用点就不必认识这个新词。
                if (v.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                    v.Equals("none", StringComparison.OrdinalIgnoreCase))
                    return string.Empty;
                return v;
            }
        }

        // ------------------------------------------------------------------
        // 设置界面用（「更多选项 → 游戏」里那一行下拉框）
        // ------------------------------------------------------------------

        /// <summary>下拉框的选项顺序（下标 ↔ 等级）。</summary>
        internal static readonly CompatLevel[] Levels =
        {
            CompatLevel.Safe,
            CompatLevel.Standard,
            CompatLevel.Full,
        };

        /// <summary>下拉框选项的显示文案。</summary>
        internal static string LevelName(CompatLevel value)
        {
            switch (value)
            {
                case CompatLevel.Safe: return "Safe（只写内存，不落盘）";
                case CompatLevel.Full: return "Full（全部功能）";
                default: return "Standard（默认）";
            }
        }

        /// <summary>当前等级在 <see cref="Levels"/> 里的下标。</summary>
        internal static int LevelIndex
        {
            get
            {
                for (var i = 0; i < Levels.Length; i++)
                {
                    if (Levels[i] == level)
                        return i;
                }
                return 1;
            }
        }

        /// <summary>
        /// 按下拉框下标设置等级。
        ///
        /// <para>写的是**同一个** <c>CompatLevel</c> 配置项 —— 所以配置文件与设置界面是同一份数据，
        /// 谁改都立刻生效（<c>SettingChanged</c> → <see cref="OnLevelChanged"/>）。</para>
        /// </summary>
        internal static void SetLevelIndex(int index)
        {
            if (index < 0 || index >= Levels.Length)
                return;
            SetLevel(Levels[index]);
        }

        /// <summary>这个功能至少要什么等级才生效。</summary>
        internal static CompatLevel RequiredLevel(GcFeature feature)
        {
            return MinLevel(feature);
        }

        /// <summary>等级不足时，拿它当悬停提示（告诉玩家去哪儿调）。</summary>
        internal static string DisabledHint(GcFeature feature)
        {
            return NameOf(feature) + " 需要兼容性等级 " + MinLevel(feature)
                + "（当前 " + level + "）——\n"
                + "在「设置 → 更多选项 → 游戏」里把「兼容性等级」改上去就生效，不用重启游戏。";
        }

        private const string DefaultLevelText = "Standard";

        // ------------------------------------------------------------------
        // 选项表 —— 只给字符串说明的话，MVZ2ModManager 只能画一个文本框；
        // 递上 AcceptableValueList 后它会写成 cfg 里的 `# Acceptable values: …`
        // 并渲染成**下拉框**（越界值的行为：BepInEx 会**换成列表第一项**，不报错
        // —— 所以顺序有意义：Auto 在第一位 = 非法值退回「跟随等级」，最安全）。
        // ------------------------------------------------------------------

        /// <summary>兼容性等级的取值（顺序与 <see cref="Levels"/> 无关，下拉框里就按这个顺序）。</summary>
        private static readonly AcceptableValueList<string> LevelValues =
            new AcceptableValueList<string>("Safe", "Standard", "Full");

        /// <summary>单项开关的取值。Auto 放第一位：非法值会退回 Auto。</summary>
        private static readonly AcceptableValueList<string> ModeValues =
            new AcceptableValueList<string>("Auto", "On", "Off");

        /// <summary>自测模式的取值。off 放第一位：非法值退回关闭（不会意外开自测）。</summary>
        private static readonly AcceptableValueList<string> SelfTestValues =
            new AcceptableValueList<string>("off", "menu", "level", "all");

        /// <summary>带取值范围的配置说明。</summary>
        private static ConfigDescription D(string text, AcceptableValueBase values)
        {
            return values == null ? new ConfigDescription(text) : new ConfigDescription(text, values);
        }

        internal static void Init(ConfigFile cfg)
        {
            levelEntry = cfg.Bind(
                "Compat", "CompatLevel", DefaultLevelText,
                D("兼容性等级：Safe / Standard / Full。\n" +
                "Safe     = 只做「存档修改器也能直接做」的事：写原版已有的关卡属性、挂原版 debug buff、\n" +
                "           改卡槽/卡池、换掉某张卡的冷却定义（只写内存，不落盘自定义数据）。\n" +
                "Standard = 默认。额外挂会改原版行为的补丁（面板暂停 / 选卡显示所有蓝图 / 蜘蛛图标入口 /\n" +
                "           蓝图血量不睡觉 / 逆阵营 / 无条件放置 / 传送带接管），并允许自定义数据落盘。\n" +
                "Full     = 全部功能（留给需要替换、接管或可能与其它 mod 竞争的功能，目前没有）。",
                    LevelValues));

            speedZeroEntry = cfg.Bind(
                "Compat.Features", "PanelSpeedZeroPause", "Auto",
                D("Auto / On / Off。面板打开时让 LevelController.GetGameSpeed 返回 0" +
                "（冻结关卡逻辑，保留蓝图/镐子/星之碎片等游戏内操作）。", ModeValues));

            allBlueprintsEntry = cfg.Bind(
                "Compat.Features", "AllBlueprintsInChoose", "Auto",
                D("Auto / On / Off。选卡界面显示所有（含不在图鉴内的）实体蓝图。只追加、不替换官方排序结果。",
                    ModeValues));

            iconEntry = cfg.Bind(
                "Compat.Features", "DebugIconOpensPanel", "Auto",
                D("Auto / On / Off。把左上角的半透明蜘蛛图标（原版调试图标）改为打开面板。", ModeValues));

            conveyorEntry = cfg.Bind(
                "Compat.Features", "ConveyorTakeover", "Auto",
                D("Auto / On / Off。传送带卡池接管：把卡池写到关卡属性上（原版只写在关卡定义上）。属 Full 档。",
                    ModeValues));

            reverseFactionEntry = cfg.Bind(
                "Compat.Features", "ReverseFaction", "Auto",
                D("Auto / On / Off。蓝图页的「逆阵营放置」：被标记的器械生成时翻到右阵营。属 Full 档。",
                    ModeValues));

            blueprintOverridesEntry = cfg.Bind(
                "Compat.Features", "BlueprintOverrides", "Auto",
                D("Auto / On / Off。蓝图页/传送带页的「血量」与「白天不睡觉」：\n" +
                "被标记的蓝图生成时把血量顶到设定值、并摘掉「夜用」器械的睡眠 buff。属 Standard 档。",
                    ModeValues));

            unconditionalPlaceEntry = cfg.Bind(
                "Compat.Features", "UnconditionalPlace", "Auto",
                D("Auto / On / Off。蓝图页/传送带页的「无条件放置」：短路官方那道「放得下吗」的判定。\n" +
                "费用与冷却照扣。属 Standard 档。", ModeValues));

            noRechargeEntry = cfg.Bind(
                "Compat.Features", "NoRecharge", "Auto",
                D("Auto / On / Off。「禁用冷却」：把卡槽里每张卡的冷却换成「无」，开启前备份原值（含玩家改过的）。\n" +
                "属 Safe 档（只是换掉已有的冷却定义并写属性）。", ModeValues));

            saveCustomDataEntry = cfg.Bind(
                "Compat.Features", "SaveCustomData", "Auto",
                D("Auto / On / Off。把自定义数据（蓝图标记、冷却备份）写进关卡存档旁边的 .loadoutflags。\n" +
                "关闭时一切只在本次会话内存里生效，不落盘。属 Standard 档。", ModeValues));

            panelHotkeyEntry = cfg.Bind(
                "General", "PanelHotkey", "",
                "打开图形化控制台面板的按键（UnityEngine.KeyCode 名，如 F5 / BackQuote）。\n" +
                "**默认留空 = 不注册任何热键** —— 原版 debug 模式下 F1~F7 全被官方的调试键占用\n" +
                "（F2=保存关卡、F3=重载关卡、F7=高级暂停…），注册热键会与它们打架。\n" +
                "默认入口是左上角那个半透明蜘蛛图标，或 /loadout open。");

            selfTestEntry = cfg.Bind(
                "General", "SelfTest", "off",
                D("自测脚手架（排障用）：off = 关闭。\n" +
                "menu  = 在主界面自动打开面板、把运行时布局打进 BepInEx 日志、再关闭；\n" +
                "level = 额外自动进第一关，并在关卡内再 dump 一次；\n" +
                "all   = 两者都做。", SelfTestValues));

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
                case GcFeature.ConveyorTakeover:
                case GcFeature.ReverseFaction:
                case GcFeature.BlueprintOverrides:
                case GcFeature.UnconditionalPlace:
                case GcFeature.SaveCustomData:
                    return CompatLevel.Standard;
                case GcFeature.NoRecharge:
                    return CompatLevel.Safe;
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
                case GcFeature.ReverseFaction: return reverseFactionEntry;
                case GcFeature.BlueprintOverrides: return blueprintOverridesEntry;
                case GcFeature.UnconditionalPlace: return unconditionalPlaceEntry;
                case GcFeature.NoRecharge: return noRechargeEntry;
                case GcFeature.SaveCustomData: return saveCustomDataEntry;
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
                case GcFeature.ReverseFaction: return "逆阵营放置";
                case GcFeature.BlueprintOverrides: return "蓝图血量/白天不睡觉";
                case GcFeature.UnconditionalPlace: return "无条件放置";
                case GcFeature.NoRecharge: return "禁用冷却";
                case GcFeature.SaveCustomData: return "自定义数据落盘";
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

        /// <summary>
        /// 把等级写回配置文件（设置页的「兼容性等级」下拉框也走这里）。
        ///
        /// <para>写的是**同一个**配置项 —— 所以设置界面、配置文件、代码里那份状态是同一份数据。
        /// 写入会触发 <c>SettingChanged</c> → <see cref="OnLevelChanged"/>（重解析 + 打日志）。</para>
        /// </summary>
        internal static void SetLevel(CompatLevel value)
        {
            // 先落到内存：即便配置项因为某种原因不可写，本次切换也要生效。
            var before = level;
            level = value;
            if (levelEntry != null)
            {
                try { levelEntry.Value = value.ToString(); }
                catch (Exception) { }
            }
            if (before == value && levelEntry == null)
                LogFeatureState();
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
