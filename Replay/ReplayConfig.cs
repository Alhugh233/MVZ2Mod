using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using MVZ2.Managers;

namespace Replay
{
    /// <summary>
    /// BepInEx 配置（<c>BepInEx/config/qoznos.mvz2.replay.cfg</c>）。
    /// </summary>
    internal static class ReplayConfig
    {
        public static ConfigEntry<bool> AutoRecord;
        public static ConfigEntry<bool> AutoSave;
        public static ConfigEntry<string> OutputDir;
        public static ConfigEntry<int> FingerprintInterval;
        public static ConfigEntry<bool> FullFingerprint;
        public static ConfigEntry<bool> WritePlainText;
        public static ConfigEntry<bool> DiscardEmpty;
        public static ConfigEntry<bool> ProbeEnabled;
        public static ConfigEntry<int> ProbeMaxLines;
        public static ConfigEntry<bool> PointerAction;
        public static ConfigEntry<int> MaxReplays;

        // ---- 回放页 / 结束面板（见 Ui/ReplayPage.cs、Ui/ReplayEndPanel.cs）----

        // ---- 视觉（见 FakePointer.cs）----
        public static ConfigEntry<bool> FakeMouse;
        public static ConfigEntry<int> FakeMouseTicks;
        public static ConfigEntry<string> FakeMouseEase;
        public static ConfigEntry<float> FakeMouseScale;
        public static ConfigEntry<bool> FakeMouseGameCursor;

        // ---- 自测（见 ReplaySelfTest.cs）----
        public static ConfigEntry<bool> SelfTest;
        public static ConfigEntry<string> SelfTestStage;
        public static ConfigEntry<string> SelfTestArea;
        public static ConfigEntry<int> SelfTestStartupTimeoutSec;
        public static ConfigEntry<int> SelfTestLevelTimeoutSec;
        public static ConfigEntry<int> SelfTestRetrySec;
        public static ConfigEntry<int> SelfTestForceStartSec;
        public static ConfigEntry<int> SelfTestHeartbeatSec;
        public static ConfigEntry<int> SelfTestWarmupTicks;
        public static ConfigEntry<string> SelfTestActions;
        public static ConfigEntry<int> SelfTestActionGapTicks;
        public static ConfigEntry<int> SelfTestCooldownTicks;
        public static ConfigEntry<bool> SelfTestAutoQuit;
        public static ConfigEntry<bool> SelfTestDeleteLevelState;
        public static ConfigEntry<bool> SelfTestPlayback;
        public static ConfigEntry<int> SelfTestPlaybackTimeoutSec;
        public static ConfigEntry<string> SelfTestPlayFile;
        public static ConfigEntry<int> SelfTestPlayTickLimit;
        public static ConfigEntry<int> SelfTestPlayShotTick;
        public static ConfigEntry<int> SelfTestPlayMenuTick;

        // ---- GUI 自测（见 Ui/ReplayGuiTest.cs）----
        public static ConfigEntry<bool> GuiTest;
        public static ConfigEntry<int> GuiTestHoldFrames;
        public static ConfigEntry<bool> GuiTestAutoQuit;
        // ---- 指令 ----
        /// <summary>是否注册 <c>/replay</c> 控制台指令。默认关（见 <see cref="GetCommands"/>）。</summary>
        public static ConfigEntry<bool> Commands;

        private static string resolvedOutputDir;
        private static bool keywordFallbackLogged;

        /// <summary>默认输出位置（= 与原版存档同级，见 <see cref="ResolveOutputDir"/>）。</summary>
        private const string DefaultOutputDir = "save";
        /// <summary>找不到游戏内置命名空间时的兜底（原版就是 mvz2）。</summary>
        private const string FallbackSpaceName = "mvz2";

        public static void Init(ConfigFile cfg)
        {
            if (cfg == null)
                return;

            AutoRecord = cfg.Bind(
                "General", "AutoRecord", true,
                "进入关卡时自动开始录制，离开关卡 / 退出游戏时写出 .mrep。\n"
                + "**默认关着** —— 平时玩关卡不会产生任何回放文件。\n"
                + "要录回放就把它打开（或者把 Commands 打开后用 /replay rec 手动开始）。");

            AutoSave = cfg.Bind(
                "General", "AutoSave", false,
                "录制过程中**每 20 秒就把回放写一次盘**（防止崩溃/强退丢掉整段录制）。\n"
                + "**默认关着** —— 只在录制**结束**时写一次（离开关卡 / 退出游戏 / 手动停止），\n"
                + "也就是「关卡里不会自己冒出回放文件」。\n"
                + "开着它的话每 20 秒就会在回放目录里落一次盘。");

            OutputDir = cfg.Bind(
                "General", "OutputDir", DefaultOutputDir,
                "回放文件输出目录。\n"
                + "可填以下**位置关键字**（跟着原版存档走，按当前用户区分）：\n"
                + "  save     = <存档根>/user<N>/replays            （默认）\n"
                + "  savemod  = <存档根>/user<N>/<空间名>/replays    （与 user.dat、level/ 同级）\n"
                + "  saveroot = <存档根>/replays\n"
                + "  game     = <游戏根>/BepInEx/replays             （旧行为）\n"
                + "也可以直接给路径：绝对路径原样用，相对路径以游戏根目录为基准。\n"
                + "存档根 = %USERPROFILE%/AppData/LocalLow/Cuerzor/<游戏名>/userdata。");

            FingerprintInterval = cfg.Bind(
                "General", "FingerprintInterval", 150,
                "每多少逻辑 tick（30 tick = 1 秒）采样一条状态指纹，用于校验回放一致性。\n"
                + "设为 0 关闭采样。");

            FullFingerprint = cfg.Bind(
                "General", "FullFingerprint", true,
                "指纹里是否额外保存「7 条 RNG 流各自的哈希」（r0..r6）和「实体定义数量直方图」（ed）。\n"
                + "默认开启。这两项的作用：不一致时能直接告诉你是**哪一条 RNG 流**、**哪一类实体**\n"
                + "先跑偏，而不是只报一个总的哈希对不上。代价约每条指纹多 150 字节。\n"
                + "旧的「完整 RNG 状态（x/y/z/w）」字段已经删掉 —— 它是嵌套数组，\n"
                + "FlatJson 那种单行解析器读不了，留着只会制造解析错误。");

            WritePlainText = cfg.Bind(
                "General", "WritePlainText", false,
                "除了 gzip 的 .mrep，是否再写一份未压缩的 .mrep.json（方便直接看内容）。");

            DiscardEmpty = cfg.Bind(
                "General", "DiscardEmpty", true,
                "一条动作都没录到时不要产生文件（避免自动录制留下一堆空文件）。");

            ProbeEnabled = cfg.Bind(
                "General", "ProbeEnabled", false,
                "打开回放探针：注入 blueprint / grid 事件时，把「游戏当时认为的状态」写进日志\n"
                + "（能量、手持物、卡片列表、目标格子上的实体）。\n"
                + "为什么需要：grid/blueprint 事件注入后**游戏可能什么都不做**\n"
                + "（手持物不对 / 格子不可放 / 能量不够），而回放器从外部看不出区别 ——\n"
                + "只能看到「事件发出去了」。探针把判断所需的状态直接打出来。\n"
                + "排查「某些动作没复现」时临时打开，平时关着（会刷屏）。");

            ProbeMaxLines = cfg.Bind(
                "General", "ProbeMaxLines", 600,
                "探针最多写多少行日志（防止刷爆）。");

            PointerAction = cfg.Bind(
                "General", "PointerAction", true,
                "回放时是否把录到的鼠标动作也发给**输入层的全局回调**\n"
                + "（LogicCallbacks.POST_POINTER_ACTION，与 InputManager 同一条路径）。\n"
                + "必须开着：游戏有两条下游通路，我们平时只重放了「路由到格子/实体/卡片」的那一条，\n"
                + "而「右键取消手持物」以及碎石墙/打砖块这类「按下鼠标就有效果」的内容挂在另一条上。\n"
                + "少这一次取消，会让后续一次点卡从「选卡」变成「取消手持物」，于是那个器械放不下去。");

            MaxReplays = cfg.Bind(
                "General", "MaxReplays", 50,
                "最多保留多少个回放（超出后自动删掉**最旧的**）。\n"
                + "每写出一份回放就会清一次；回放页面里也会显示当前数量。\n"
                + "设为 0 = 不限制。统计范围是 OutputDir（默认 save）指定的位置。");

            Commands = cfg.Bind(
                "General", "Commands", false,
                "是否注册 /replay 控制台指令（rec / stop / play / playlast / list / status / dir）。\n"
                + "默认**关着**：那是个排障入口，普通玩家用「主菜单 → 更多选项 → 插件 → 回放」就够了，\n"
                + "开着只会让控制台多出一条与游戏本身无关的指令。\n"
                + "关着的时候敲 /replay 会按原版流程报「找不到指令」。");

            // ---------------- 视觉 ----------------
            FakeMouse = cfg.Bind(
                "Visual", "FakeMouse", true,
                "回放时画一个「假鼠标」，让指针全程沿轨迹平滑移动，而不是原地不动然后瞬移。\n"
                + "为什么需要：回放里只有离散的动作路点（点格子/点实体/点卡），\n"
                + "**数字键选卡与工具按钮根本没有坐标** —— 不插值的话，选卡之后\n"
                + "手持物会一直停在「上一次点击的位置」，直到下一次点击才瞬移过去。\n"
                + "另外**没有手持任何物品时游戏本来就不画指针**（那是系统光标在顶），\n"
                + "所以这里额外画一个「游戏光标样式」的贴图当假鼠标。\n"
                + "关掉 = 回到旧行为（手感差但绝对不碰任何东西）。");

            FakeMouseTicks = cfg.Bind(
                "Visual", "FakeMouseTicks", 0,
                "鼠标走完「上一个路点 → 下一个路点」这段距离用多少 tick（30 tick = 1 秒）。\n"
                + "**0（默认）= 占满整个区间**：上一个动作刚做完就开始往下一个位置走，全程不停。\n"
                + "填正数（例如 12）就变成「先停着、最后 N 个 tick 一口气冲过去」的旧行为 ——\n"
                + "两下之间间隔很长时会有一大段时间鼠标完全不动，一般不需要。");

            FakeMouseEase = cfg.Bind(
                "Visual", "FakeMouseEase", "quadinout",
                "缓动曲线。\n"
                + "linear = 匀速（**最符合「全程不静止」**，默认）；\n"
                + "quadout = 二次缓出（起步快、到站减速，最像人手，但区间末尾会几乎停下来）；\n"
                + "quadinout = 二次缓入缓出；cubicout = 三次缓出（更夸张）。");

            FakeMouseScale = cfg.Bind(
                "Visual", "FakeMouseScale", 1.0f,
                "假鼠标那张贴图的显示大小倍率。\n"
                + "1.0 = 和游戏原生的硬件光标一样大（贴图原尺寸）。\n"
                + "机器 DPI / 分辨率不同时，游戏光标本来就会显得偏小或偏大，\n"
                + "这里可以单独把「回放里画出来的那个鼠标」调到你看着舒服的尺寸。");

            FakeMouseGameCursor = cfg.Bind(
                "Visual", "FakeMouseGameCursor", true,
                "假鼠标用**游戏自己的光标贴图**（从 CursorManager 里取，外观与游戏原生光标一致），\n"
                + "还是用 mod 自绘的那个箭头。\n"
                + "游戏贴图取不到时会自动退回自绘箭头；这个开关只是给「贴图在你那里看着不对」时的退路。");

            // ---------------- 自测 ----------------
            SelfTest = cfg.Bind(
                "SelfTest", "Enabled", false,
                "打开内置自测：启动后自动执行\n"
                + "  等启动帧 → /gotolevel 进关卡 → 等录制就绪 → 按 tick 间隔注入输入 →\n"
                + "  停止录制 → 校验 .mrep → 写报告（BepInEx\\_replay_selftest.txt）" + "\n"
                + "报告里会列出每一项的 OK/FAIL。默认完成后自动退出游戏，方便脚本化验证。");

            SelfTestStage = cfg.Bind(
                "SelfTest", "Stage", "mvz2:halloween_2",
                "自测进哪个关卡。\n"
                + "默认挑一个**本机没有存档**的关卡：有存档时游戏会走 LoadLevel，\n"
                + "而存档首行要与当前 mod 的 LevelDataVersion 列表一致，否则只弹不一致对话框、关卡根本不开始。");

            SelfTestArea = cfg.Bind(
                "SelfTest", "Area", "mvz2:halloween",
                "对应的大区 id。");

            SelfTestStartupTimeoutSec = cfg.Bind(
                "SelfTest", "StartupTimeoutSec", 300,
                "等待主菜单就绪的最长秒数。调试版游戏冷启动很慢（几分钟），这个值别太小。");

            SelfTestLevelTimeoutSec = cfg.Bind(
                "SelfTest", "LevelTimeoutSec", 180,
                "等待关卡开始的最长秒数；超时判 FAIL（报告里会带上 ctrl/engine/started/页面 等诊断）。");

            SelfTestRetrySec = cfg.Bind(
                "SelfTest", "RetrySec", 20,
                "等关卡时的重试间隔（秒）。\n"
                + "没引擎就重新执行 /gotolevel；引擎在但一直没开始就等 ForceStartSec 后强制开局。");

            SelfTestForceStartSec = cfg.Bind(
                "SelfTest", "ForceStartSec", 40,
                "引擎已创建但一直没进入「已开始」（通常卡在选卡面板 / 开场对话）时，\n"
                + "等这么久之后就 BlueprintChoosePart.ApplyChoose() + GameStartInstantTransition() 强制开局。");

            SelfTestHeartbeatSec = cfg.Bind(
                "SelfTest", "HeartbeatSec", 5,
                "每隔多少秒往报告/日志里写一条进度（带当前页面/等待秒数），方便判断卡在哪。\n"
                + "按时间而不是帧：游戏加载阶段的帧率极低（上百帧可能要几分钟）。");

            SelfTestWarmupTicks = cfg.Bind(
                "SelfTest", "WarmupTicks", 60,
                "录制开始后先等多少逻辑 tick 再注入输入。");

            SelfTestActions = cfg.Bind(
                "SelfTest", "Actions", "bpkey0,bpkey1,pickaxe,starshard,trigger",
                "要注入的动作序列，逗号分隔。\n"
                + "可用：bpkey0 / bpkey1（选卡热键）、pickaxe / starshard / trigger（工具）。");

            SelfTestActionGapTicks = cfg.Bind(
                "SelfTest", "ActionGapTicks", 30,
                "相邻两个动作之间隔多少逻辑 tick。");

            SelfTestCooldownTicks = cfg.Bind(
                "SelfTest", "CooldownTicks", 90,
                "最后一个动作之后再录多少 tick 才停止（让指纹多采几条）。");

            SelfTestAutoQuit = cfg.Bind(
                "SelfTest", "AutoQuit", true,
                "报告写完后自动退出游戏。");

            SelfTestDeleteLevelState = cfg.Bind(
                "SelfTest", "DeleteLevelState", false,
                "等待关卡开始时是否删掉该关卡的存档再重试。\n"
                + "⚠️ 会丢弃该关卡进行中的进度，只在你自己确认过的时候开。");

            SelfTestPlayback = cfg.Bind(
                "SelfTest", "Playback", true,
                "录完并校验完 .mrep 之后，**立刻播放刚录的这一条**，\n"
                + "用录制时采到的状态指纹去核对回放是否复现出一模一样的状态。\n"
                + "这是「回放录像与游玩录像一致」唯一能自动化验证的地方。");

            SelfTestPlaybackTimeoutSec = cfg.Bind(
                "SelfTest", "PlaybackTimeoutSec", 180,
                "等待回放播完的最长秒数（含重新加载关卡场景的时间）。");

            SelfTestPlayFile = cfg.Bind(
                "SelfTest", "PlayFile", "",
                "只想验证播放时用这个：填一个回放名字（或其完整路径）。\n"
                + "填了它就**跳过录制**，启动后直接播放这个文件，播完把报告写出来。\n"
                + "与游戏中手敲 /replay play 等价，但是全自动、有完整报告，方便反复调试。");

            SelfTestPlayTickLimit = cfg.Bind(
                "SelfTest", "PlayTickLimit", 0,
                "只播放到第多少 tick 就收尾（0 = 限制）。\n"
                + "排查分歧时用它把一次验证缩短到几秒，不用每次等完整局。");

            SelfTestPlayShotTick = cfg.Bind(
                "SelfTest", "PlayShotTick", 0,
                "播放验收时跑到第几 tick 抓一张截图（0 = 不抓）。\n"
                + "会在该 tick 与之后 15 个 tick 各抓一张（_rp_play0.png / _rp_play1.png），\n"
                + "用来目视确认「假鼠标在动、位置/尺寸/层级都对」——\n"
                + "指纹能证明状态一致，但证明不了画得对不对。");

            SelfTestPlayMenuTick = cfg.Bind(
                "SelfTest", "PlayMenuTick", 0,
                "播放验收时跑到第几 tick 打开一次**关卡内选项菜单**，\n"
                + "把「重新播放 / 结束播放」两个按钮的**实际文案**写进报告并截图 _rp_menu.png（0 = 不做）。\n"
                + "为什么需要：那两个文案是我们用 Harmony 后缀**覆盖**原版写进去的，\n"
                + "而原版每次刷新页面都会重写一遍 —— 「代码看起来对」不能说明最终显示的是哪个。");

            // ---------------- GUI 自测 ----------------
            GuiTest = cfg.Bind(
                "GuiTest", "Enabled", false,
                "打开「回放页面」的自动化验收：启动后自动进主菜单 → 切到插件页 →\n"
                + "  检查入口按钮 → 打开回放面板 → 截图 + 布局 dump → 弹一次删除确认 → 再截图。\n"
                + "报告写到 <游戏目录>\\BepInEx\\_replay_gui.txt，截图写到 _rp_shot*.png。\n"
                + "为什么要有它：面板「节点存在、尺寸也对」不等于「看得见」，必须看图；\n"
                + "而 ScreenCapture 是异步的，得留足帧数再拍。");

            GuiTestHoldFrames = cfg.Bind(
                "GuiTest", "HoldFrames", 90,
                "每一步之后等多少帧再截图/取样。ScreenCapture.CaptureScreenshot 在**帧末**才写盘，\n"
                + "等太短拍到的是变化前的画面（本仓库踩过：拍到的是加载画面）。");

            GuiTestAutoQuit = cfg.Bind(
                "GuiTest", "AutoQuit", true,
                "GUI 自测跑完后自动退出游戏，方便脚本化。");
        }

        /// <summary>
        /// 解析输出目录。
        ///
        /// <para>支持「位置关键字」（见 <see cref="ResolveLocationKeyword"/>）；不是关键字时
        /// 当字面路径处理（绝对路径原样、相对路径以游戏根目录为基准）。</para>
        ///
        /// <para><b>为什么默认跟着存档走：</b>回放是「属于某个用户某一局」的数据，
        /// 而 BepInEx 目录在游戏安装目录里 —— 既会被版本更新洗掉，也不能区分用户。
        /// 放在 <c>userdata/user&lt;N&gt;/</c> 下面就跟 <c>user.dat</c>、<c>level/</c> 一起对待了。</para>
        /// </summary>
        public static string ResolveOutputDir()
        {
            string raw = DefaultOutputDir;
            try
            {
                if (OutputDir != null && !string.IsNullOrWhiteSpace(OutputDir.Value))
                    raw = OutputDir.Value.Trim();
            }
            catch (Exception)
            {
            }

            if (IsLocationKeyword(raw))
            {
                // 关键字路径**不缓存**：玩家可以在游戏里切换用户（user0 → user1），
                // 缓存下来就会继续写到上一个用户的目录里。
                var keyword = ResolveLocationKeyword(raw);
                if (keyword != null)
                {
                    TryMigrateLegacyReplays(keyword);
                    return keyword;
                }

                // 游戏还没初始化完（例如 Plugin.Load 阶段那行日志）。
                // ⚠️ 这里**绝不能**当成相对路径去拼，否则会得到 `<游戏根>/save` 这种
                // 看着像路径、实际是乱码目录的东西。等真正写盘时游戏早就起来了，
                // 那时关键字一定能解析成功；现在退到旧位置只是为了有个合理值可打印。
                if (!keywordFallbackLogged)
                {
                    keywordFallbackLogged = true;
                    Plugin.Log?.LogWarning("[Replay] 输出目录关键字 '" + raw
                        + "' 暂时解析不了（游戏未初始化），先用 BepInEx/replays 顶着；写盘时会重新解析。");
                }
                return Path.Combine(GameRoot(), "BepInEx", "replays");
            }

            if (resolvedOutputDir != null)
                return resolvedOutputDir;
            string root = GameRoot();
            try
            {
                resolvedOutputDir = Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(root, raw));
            }
            catch (Exception)
            {
                resolvedOutputDir = Path.Combine(root, "BepInEx", "replays");
            }
            return resolvedOutputDir;
        }

        /// <summary>旧的位置（&lt;游戏根&gt;/BepInEx/replays），用于兼容与迁移。</summary>
        public static string LegacyOutputDir()
        {
            return Path.Combine(GameRoot(), "BepInEx", "replays");
        }

        /// <summary>
        /// 允许搜索回放的位置：当前位置优先，再兜旧位置。
        ///
        /// <para>为什么不只搜当前位置：输出目录改成跟着存档走后，旧文件会「突然消失」。
        /// 一次性迁移（见 <see cref="TryMigrateLegacyReplays"/>）之外还留一个搜索兜底，
        /// 这样即便迁移因权限/占用失败，老回放依然找得到、放得了。</para>
        /// </summary>
        public static System.Collections.Generic.List<string> OutputSearchDirs()
        {
            var list = new System.Collections.Generic.List<string>();
            var cur = ResolveOutputDir();
            if (!string.IsNullOrEmpty(cur))
                list.Add(cur);
            var legacy = LegacyOutputDir();
            if (!string.IsNullOrEmpty(legacy) && !SamePath(legacy, cur))
                list.Add(legacy);
            return list;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'),
                                     Path.GetFullPath(b).TrimEnd('\\', '/'),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool legacyMigrationTried;

        /// <summary>
        /// 把旧位置里的回放文件搬到新位置。只在第一次成功解析出关键字路径时跑一次
        /// （游戏没初始化时解析不出路径，而那正是 Plugin.Load 阶段）。
        ///
        /// <para>只搬 <c>.mrep</c> / <c>.mrep.json</c>，**不覆盖**、**不删**：
        /// 目标已存在就跳过，出错只记日志。</para>
        /// </summary>
        private static void TryMigrateLegacyReplays(string newDir)
        {
            if (legacyMigrationTried)
                return;
            legacyMigrationTried = true;
            try
            {
                var legacy = LegacyOutputDir();
                if (SamePath(legacy, newDir) || !Directory.Exists(legacy))
                    return;
                Directory.CreateDirectory(newDir);
                int moved = 0, skipped = 0;
                foreach (var f in Directory.GetFiles(legacy))
                {
                    var name = Path.GetFileName(f);
                    if (!name.EndsWith(".mrep", StringComparison.OrdinalIgnoreCase)
                        && !name.EndsWith(".mrep.json", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var dst = Path.Combine(newDir, name);
                    if (File.Exists(dst))
                    {
                        skipped++;
                        continue;
                    }
                    try
                    {
                        File.Move(f, dst);
                        moved++;
                    }
                    catch (Exception)
                    {
                        skipped++;
                    }
                }
                if (moved > 0 || skipped > 0)
                    Plugin.Log?.LogInfo("[Replay] 回放目录已改到存档目录：搬走 " + moved
                        + " 个，跳过 " + skipped + " 个 -> " + newDir);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 迁移旧回放目录失败（不影响使用，旧位置仍可搜索）："
                    + e.GetType().Name + ": " + e.Message);
            }
        }

        private static bool IsLocationKeyword(string raw)
        {            if (string.IsNullOrEmpty(raw))
                return false;
            switch (raw.ToLowerInvariant())
            {
                case "save":
                case "savemod":
                case "saveroot":
                case "game":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 把位置关键字解析成绝对路径；游戏还没初始化、拿不到存档目录时返回 null。
        /// </summary>
        private static string ResolveLocationKeyword(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;
            switch (raw.ToLowerInvariant())
            {
                case "save":
                    {
                        var user = TryGetUserSaveDir();
                        return user != null ? Path.Combine(user, "replays") : null;
                    }
                case "savemod":
                    {
                        var user = TryGetUserSaveDir();
                        return user != null ? Path.Combine(user, TryGetBuiltinSpace(), "replays") : null;
                    }
                case "saveroot":
                    {
                        var root = TryGetSaveRoot();
                        return root != null ? Path.Combine(root, "replays") : null;
                    }
                case "game":
                    return Path.Combine(GameRoot(), "BepInEx", "replays");
                default:
                    return null;
            }
        }

        /// <summary>存档根（<c>&lt;persistentDataPath&gt;/userdata</c>）；游戏没起来时返回 null。</summary>
        private static string TryGetSaveRoot()
        {
            try
            {
                var main = MainManager.Instance;
                var sm = main != null ? main.SaveManager : null;
                if (sm == null || sm.Pointer == IntPtr.Zero)
                    return null;
                var dir = sm.GetSaveDataRoot();
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>当前用户的存档目录（<c>…/userdata/user&lt;N&gt;</c>）；游戏没起来时返回 null。</summary>
        private static string TryGetUserSaveDir()
        {
            try
            {
                var main = MainManager.Instance;
                var sm = main != null ? main.SaveManager : null;
                if (sm == null || sm.Pointer == IntPtr.Zero)
                    return null;
                int index = sm.GetCurrentUserIndex();
                var dir = sm.GetUserSaveDataDirectory(index);
                return string.IsNullOrEmpty(dir) ? null : dir;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>游戏的内置命名空间（原版存档目录名，通常是 mvz2）。</summary>
        private static string TryGetBuiltinSpace()
        {
            try
            {
                var main = MainManager.Instance;
                var game = main != null ? main.Game : null;
                if (game != null)
                {
                    var ns = game.DefaultNamespace;
                    if (!string.IsNullOrEmpty(ns))
                        return ns;
                }
            }
            catch (Exception)
            {
            }
            return FallbackSpaceName;
        }

        private static string GameRoot()
        {
            string root = null;
            try { root = Paths.GameRootPath; } catch (Exception) { }
            if (!string.IsNullOrEmpty(root))
                return root;
            try { return Environment.CurrentDirectory; } catch (Exception) { return "."; }
        }

        public static string Describe()
        {
            var interval = FingerprintInterval != null ? FingerprintInterval.Value : 0;
            return "auto=" + (AutoRecord != null && AutoRecord.Value)
                + " dir=" + ResolveOutputDir()
                + " fp=" + (interval > 0 ? interval.ToString() + "tick" : "off")
                + " max=" + GetMaxReplays()
                + " cmd=" + GetCommands()
                + " autosave=" + GetAutoSave()
                + " selftest=" + (SelfTest != null && SelfTest.Value);
        }

        /// <summary>
        /// 是否注册 <c>/replay</c> 控制台指令。**默认关**。
        ///
        /// <para>那是个排障入口：普通玩家有「主菜单 → 更多选项 → 插件 → 回放」这条正路，
        /// 多一条与游戏本身无关的指令只会让控制台变乱。</para>
        /// </summary>
        public static bool GetCommands()
        {
            try { return Commands != null && Commands.Value; }
            catch (Exception) { return false; }
        }

        /// <summary>最多保留多少个回放（0 = 不限制）。配置读不到时按 20 处理。</summary>
        public static int GetMaxReplays()
        {
            try
            {
                var v = MaxReplays != null ? MaxReplays.Value : 20;
                return v < 0 ? 0 : v;
            }
            catch (Exception)
            {
                return 20;
            }
        }

        public static bool GetFakeMouse()
        {
            try { return FakeMouse == null || FakeMouse.Value; }
            catch (Exception) { return true; }
        }

        /// <summary>
        /// 鼠标走完两个路点之间那段距离用多少 tick。
        /// <para><b>0（默认）= 占满整个区间</b>，即上一个动作一结束就开始往下一个位置走，
        /// 全程不停 —— 这是「鼠标不静止」的关键。正数则恢复旧行为（先停着、最后 N tick 冲过去）。</para>
        /// </summary>
        public static int GetFakeMouseTicks()
        {
            try
            {
                var v = FakeMouseTicks != null ? FakeMouseTicks.Value : 0;
                if (v < 0) v = 0;
                if (v > 120) v = 120;
                return v;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>假鼠标的缓动曲线名（linear / quadinout / quadout / cubicout）。</summary>
        public static string GetFakeMouseEase()
        {
            try
            {
                var s = FakeMouseEase != null ? FakeMouseEase.Value : null;
                return string.IsNullOrWhiteSpace(s) ? "linear" : s.Trim().ToLowerInvariant();
            }
            catch (Exception) { return "linear"; }
        }

        /// <summary>假鼠标是否用游戏自己的光标贴图（关掉就用 mod 自绘的箭头）。默认开。</summary>
        public static bool GetFakeMouseGameCursor()
        {
            try { return FakeMouseGameCursor == null || FakeMouseGameCursor.Value; }
            catch (Exception) { return true; }
        }

        /// <summary>
        /// 假鼠标贴图的显示大小倍率。1.0 = 贴图原尺寸（与游戏原生硬件光标等大）。
        /// 越界值一律夹到 0.1..8，避免配置写错时画出一个盖满屏幕的方块。
        /// </summary>
        public static float GetFakeMouseScale()
        {
            try
            {
                var v = FakeMouseScale != null ? FakeMouseScale.Value : 1f;
                if (float.IsNaN(v) || v <= 0.001f)
                    return 1f;
                if (v < 0.1f) v = 0.1f;
                if (v > 8f) v = 8f;
                return v;
            }
            catch (Exception) { return 1f; }
        }

        /// <summary>
        /// 录制过程中是否允许「每 20 秒自动落盘一次」。**默认关**。
        ///
        /// <para>关着的时候回放只在**录制结束**时写一次（离开关卡 / 退出游戏 / 手动停止），
        /// 也就是「在关卡里玩的时候不会自己冒出回放文件」。</para>
        /// </summary>
        public static bool GetAutoSave()
        {
            try { return AutoSave != null && AutoSave.Value; }
            catch (Exception) { return false; }
        }
    }
}
