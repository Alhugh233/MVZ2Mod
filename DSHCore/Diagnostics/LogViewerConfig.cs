using System;
using System.Security.Cryptography;
using BepInEx.Configuration;

namespace DSHCore.Diagnostics
{
    /// <summary>
    /// 网页日志查看器的配置（落在 <c>BepInEx/config/qoznos.mvz2.core.cfg</c>）。
    ///
    /// <para>默认是「按需开启」：日志从游戏启动就开始捕获进环形缓冲，但 HTTP 服务只在
    /// 配置 <c>Enabled = true</c> 或游戏内输入 <c>/dshlog</c> 时才起来。</para>
    ///
    /// <para>命名对齐 RitsuLib 的同名概念（DebugLogViewer），便于跨项目对照：
    /// 端口、端口回退次数、RingBuffer 容量、队列容量、访问令牌、LAN 开关、自动开浏览器。</para>
    /// </summary>
    internal sealed class LogViewerConfig
    {
        /// <summary>默认端口。与 RitsuLib 的 DebugLogViewer 保持一致，便于记忆。</summary>
        public const int DefaultPort = 18742;

        public bool Enabled;
        public bool AutoOpen = true;
        public bool LanAccess;
        public int Port = DefaultPort;
        public int PortFallbackCount = 10;
        public int RingBufferCapacity = 5000;
        public int QueueCapacity = 20000;
        public string AccessToken = string.Empty;
        public bool HideConsoleWindow;
        public bool AllowConsoleToggle = true;

        public static LogViewerConfig Bind(ConfigFile config)
        {
            var result = new LogViewerConfig();
            if (config == null)
            {
                // 没有配置文件时（理论上不会发生）也要能跑：生成一个临时令牌
                result.AccessToken = NewToken();
                return result;
            }

            const string Section = "LogViewer";

            result.Enabled = config.Bind(
                Section, "Enabled", false,
                "是否在游戏启动时就自动开好网页查看器。" +
                "默认 false：日志照样从头捕获，但不开端口也不弹浏览器，" +
                "等你用游戏内指令 /" + LogViewerCommand.Name + " 再开（打开就能看到从启动开始的完整历史）。" +
                "开着它会在启动时抢端口/弹浏览器，只在确实需要时才设 true。").Value;

            result.AutoOpen = config.Bind(
                Section, "AutoOpen", true,
                "启动网页服务时自动用默认浏览器打开（包括用 /" + LogViewerCommand.Name +
                " 指令开启的情况）。").Value;

            result.Port = Clamp(config.Bind(
                Section, "Port", DefaultPort,
                Desc("HTTP 监听端口。被占用时会依次尝试后面 " + 10 + " 个端口（见 PortFallbackCount）。",
                    new AcceptableValueRange<int>(1, 65535))).Value, 1, 65535);

            result.PortFallbackCount = Clamp(config.Bind(
                Section, "PortFallbackCount", 10,
                Desc("端口被占用时最多往后顺延几个端口。",
                    new AcceptableValueRange<int>(0, 200))).Value, 0, 200);

            result.LanAccess = config.Bind(
                Section, "LanAccess", false,
                "是否监听所有网卡（局域网内其它设备可用带令牌的 URL 访问）。" +
                "默认只监听 127.0.0.1。注意：开启后同网段的机器只要拿到令牌就能看到你的日志。").Value;

            result.RingBufferCapacity = Clamp(config.Bind(
                Section, "RingBufferCapacity", 5000,
                Desc("内存里保留多少条历史日志。这段缓冲**始终**在记（不管网页开没开），" +
                    "所以开启网页时能立刻看到从游戏启动开始的日志。",
                    new AcceptableValueRange<int>(256, 200000))).Value, 256, 200000);

            result.QueueCapacity = Clamp(config.Bind(
                Section, "QueueCapacity", 20000,
                Desc("待处理日志队列上限。写日志过快时超出部分会被丢弃并计数，以免拖慢游戏。",
                    new AcceptableValueRange<int>(256, 200000))).Value, 256, 200000);

            result.HideConsoleWindow = config.Bind(
                Section, "HideConsoleWindow", false,
                "插件加载完成后自动隐藏 BepInEx 控制台窗口（用 /" + LogViewerCommand.Name +
                " console 可以随时叫回来）。本仓库自带的 BepInEx.cfg 已经直接把控制台关了，" +
                "所以这一项主要给重新开启控制台的人用。").Value;

            result.AllowConsoleToggle = config.Bind(
                Section, "AllowConsoleToggle", true,
                "允许网页查看器通过 /api/console 显示或隐藏控制台窗口。").Value;

            var token = config.Bind(
                Section, "AccessToken", string.Empty,
                "访问令牌。/api/* 接口都要求带上它（网页会自动从 URL 里取）。" +
                "留空则启动时自动生成并写回本文件。").Value;

            if (string.IsNullOrWhiteSpace(token))
            {
                token = NewToken();
                try
                {
                    config.Bind(Section, "AccessToken", token).Value = token;
                    config.Save();
                }
                catch
                {
                    // 写不回去也无所谓，本次会话用内存里这个令牌就是了
                }
            }
            result.AccessToken = token;

            return result;
        }

        private static string NewToken() =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        /// <summary>
        /// 造一个配置说明。
        ///
        /// <para><b>为什么要带 <paramref name="values"/></b>：BepInEx 会把它写成 cfg 里的
        /// <c># Acceptable value range: From a to b</c> / <c># Acceptable values: a, b, c</c> 两行注释，
        /// 而 MVZ2ModManager 就是读这两行来决定画**滑条**还是**下拉框**的
        /// （只给字符串说明的话它只能画一个文本框）。</para>
        ///
        /// <para>越界时的行为（已反编译确认，BepInEx 6 be.788）：<c>AcceptableValueRange.Clamp</c>
        /// 夹到区间上，<c>AcceptableValueList.Clamp</c> **换成列表第一项** —— 两者都**不抛异常**，
        /// 所以即使玩家手改了一个非法值，插件也能正常加载。</para>
        /// </summary>
        private static ConfigDescription Desc(string text, AcceptableValueBase values)
        {
            return values == null ? new ConfigDescription(text) : new ConfigDescription(text, values);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
