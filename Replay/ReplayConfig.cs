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
        public static ConfigEntry<string> OutputDir;
        public static ConfigEntry<int> FingerprintInterval;
        public static ConfigEntry<bool> FullFingerprint;
        public static ConfigEntry<bool> WritePlainText;
        public static ConfigEntry<bool> DiscardEmpty;
        public static ConfigEntry<bool> ProbeEnabled;
        public static ConfigEntry<int> ProbeMaxLines;
        public static ConfigEntry<bool> PointerAction;
        public static ConfigEntry<int> MaxReplays;
        public static ConfigEntry<bool> GuiEnabled;

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

        // ---- GUI 自测（见 Ui/ReplayGuiTest.cs）----
        public static ConfigEntry<bool> GuiTest;
        public static ConfigEntry<int> GuiTestHoldFrames;
        public static ConfigEntry<bool> GuiTestAutoQuit;

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
                "进入关卡时自动开始录制，关卡结束/退出关卡时写出 .mrep。\n"
                + "关掉它就只能用 /replay rec 手动开始。");

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
                "General", "FingerprintInterval", 30,
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
                "General", "WritePlainText", true,
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
                "General", "MaxReplays", 20,
                "最多保留多少个回放（超出后自动删掉**最旧的**）。\n"
                + "每写出一份回放就会清一次；回放页面里也会显示当前数量。\n"
                + "设为 0 = 不限制。统计范围是 OutputDir（默认 save）指定的位置。");

            GuiEnabled = cfg.Bind(
                "General", "GuiEnabled", true,
                "是否在「主菜单 → 更多选项 → 插件」页里加一个「回放」入口。\n"
                + "关掉它就只剩 /replay 控制台指令（不影响录制与播放）。");

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
                + " selftest=" + (SelfTest != null && SelfTest.Value);
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

        /// <summary>是否安装「插件 → 回放」的 GUI 入口。默认开。</summary>
        public static bool GetGuiEnabled()
        {
            try { return GuiEnabled == null || GuiEnabled.Value; }
            catch (Exception) { return true; }
        }
    }
}
