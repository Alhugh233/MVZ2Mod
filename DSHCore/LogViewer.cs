using BepInEx.Logging;

namespace DSHCore
{
    /// <summary>
    /// 本地网页日志查看器 —— 对外门面。
    ///
    /// <para><b>默认不自动开启</b>：日志从游戏启动就开始捕获进内存环形缓冲（不监听端口、
    /// 不弹浏览器），需要时在游戏内输入 <c>/dshlog</c> 开启网页 —— 打开就能看到从游戏
    /// 启动开始的完整历史，而不是「开了之后才有的那几行」。这比默认自动开更合算：
    /// 没在调试的时候不会占端口、不会弹浏览器。</para>
    ///
    /// <para>想在启动时就自动开好，把配置 <c>[LogViewer] Enabled</c> 设成 <c>true</c>。</para>
    ///
    /// <para>实现要点与踩坑记录见 <c>DSHCore/Diagnostics/</c> 下各文件的注释，
    /// 以及 <c>AI_GUIDE.md</c> 的对应章节。</para>
    ///
    /// <para>本类只是转发 —— 真正的逻辑在 <c>DSHCore.Diagnostics.LogPipeline</c>
    /// （internal，对依赖本库的 mod 不可见）。</para>
    /// </summary>
    public static class LogViewer
    {
        /// <summary>网页服务（HTTP 监听）是否正在运行。</summary>
        public static bool IsRunning => Diagnostics.LogPipeline.IsServerRunning;

        /// <summary>
        /// 日志捕获是否已启动。插件加载后一直为 true —— 即使网页没开，
        /// 日志也在往内存环形缓冲里记。
        /// </summary>
        public static bool IsCapturing => Diagnostics.LogPipeline.IsStarted;

        /// <summary>带访问令牌的本地 URL；服务未运行时为空串。</summary>
        public static string Url => Diagnostics.LogPipeline.Url ?? string.Empty;

        /// <summary>当前监听模式：<c>loopback</c> / <c>lan</c> / <c>—</c>（未运行）。</summary>
        public static string AccessMode => Diagnostics.LogPipeline.ServerAccessMode;

        /// <summary>因为队列溢出而丢弃的日志条数。持续增长说明日志量过大，可调大 QueueCapacity。</summary>
        public static long DroppedCount => Diagnostics.LogPipeline.DroppedCount;

        /// <summary>已捕获的日志总条数。</summary>
        public static long CapturedCount => Diagnostics.LogPipeline.EmittedCount;

        /// <summary>当前连接着的浏览器数量。</summary>
        public static int ClientCount => Diagnostics.LogPipeline.ClientCount;

        /// <summary>
        /// 开启网页服务（幂等）。已开启时直接返回 true。
        /// 这就是游戏内 <c>/dshlog</c> 调的东西，其它 mod 也可以直接调。
        /// </summary>
        /// <param name="openBrowser">是否顺带用默认浏览器打开。</param>
        /// <param name="message">结果说明（失败原因 / 已启动的地址）。</param>
        public static bool StartServer(out string message, bool openBrowser = true)
            => Diagnostics.LogPipeline.StartServer(out message, openBrowser);

        /// <summary>关闭网页服务并释放端口（幂等）。内存历史保留，下次开启仍在。</summary>
        public static bool StopServer(out string message)
            => Diagnostics.LogPipeline.StopServer(out message);

        /// <summary>用系统默认浏览器打开查看器。服务未运行时返回 false。</summary>
        public static bool OpenInBrowser() => Diagnostics.LogPipeline.TryOpenInBrowser();

        /// <summary>清空内存里的历史（磁盘日志不受影响）。</summary>
        public static void ClearHistory() => Diagnostics.LogPipeline.ClearHistory();

        /// <summary>隐藏 BepInEx 控制台窗口。不是本进程创建的窗口时返回 false 且不做任何事。</summary>
        public static bool HideConsole() => Diagnostics.ConsoleWindow.Hide();

        /// <summary>重新显示 BepInEx 控制台窗口。</summary>
        public static bool ShowConsole() => Diagnostics.ConsoleWindow.Show();

        /// <summary>在显示 / 隐藏之间切换，返回切换后是否可见。</summary>
        public static bool ToggleConsole() => Diagnostics.ConsoleWindow.Toggle();

        /// <summary>停止查看器并释放端口。一般不需要手动调用（插件卸载 / 进程退出时会自动调）。</summary>
        public static void Shutdown() => Diagnostics.LogPipeline.Stop();

        /// <summary>把一条自定义诊断推进查看器（不想建 ManualLogSource 时用）。</summary>
        public static void Report(string source, LogLevel level, string message)
            => Diagnostics.LogPipeline.ReportExternal(source, level, message);
    }
}
