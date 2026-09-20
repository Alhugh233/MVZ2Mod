using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace DSHCore.Diagnostics
{
    /// <summary>
    /// 日志管道：把 BepInEx 的日志事件接进来，喂给环形缓冲 + 网页查看器。
    ///
    /// <para><b>线程模型（最重要的一条约定）</b></para>
    /// <list type="number">
    /// <item><description>
    /// <see cref="BepInExLogListener.LogEvent"/> 在**发起日志的那个线程**上被调用，
    /// 可能是 Unity 主线程、BepInEx 加载线程、或别的东西。所以它只做一件事：
    /// 把记录塞进 <see cref="ConcurrentQueue{T}"/> 并释放信号量。**绝不阻塞、
    /// 绝不做 I/O** —— 在这里卡住就是卡住整个游戏。
    /// </description></item>
    /// <item><description>
    /// 独立的 <see cref="Thread"/>（后台线程）负责取队列、写环形缓冲、广播给 SSE 客户端。
    /// </description></item>
    /// <item><description>
    /// <b>这个后台线程以及所有 HTTP 线程都不得触碰任何 IL2CPP / 游戏类型。</b>
    /// 托管线程没有 attach 到 IL2CPP 域时调用游戏 API 会直接崩（无托管栈）。
    /// 本文件与 <see cref="LogViewerServer"/> 里的代码全部是纯托管逻辑 —— 这是刻意的。
    /// </description></item>
    /// </list>
    ///
    /// <para><b>背压策略</b>：队列有上限，满了就丢新记录并计数。
    /// 宁可丢日志，也不能让日志把游戏拖死 —— 这是日志系统的常识性取舍。</para>
    /// </summary>
    internal static class LogPipeline
    {
        /// <summary>丢弃计数上报的最小间隔，避免日志风暴时刷屏。</summary>
        private static readonly TimeSpan InternalWarningInterval = TimeSpan.FromSeconds(30);

        private static readonly object Gate = new object();
        private static readonly ConcurrentQueue<LogRecord> Queue = new ConcurrentQueue<LogRecord>();
        private static readonly SemaphoreSlim Signal = new SemaphoreSlim(0);

        private static LogRingBuffer _ring;
        private static LogViewerServer _server;
        private static LogViewerConfig _config;
        private static BepInExLogListener _listener;
        private static Thread _worker;
        private static CancellationTokenSource _cts;
        private static bool _started;
        private static string _configDir;
        private static ManualLogSource _log;

        private static int _queued;
        private static long _nextId;
        private static long _dropped;
        private static long _emitted;
        private static DateTimeOffset _lastDropWarning;

        /// <summary>本次进程会话的标识。浏览器刷新后用它判断「还是同一局游戏」。</summary>
        public static readonly string SessionId = Guid.NewGuid().ToString("N");

        public static readonly DateTimeOffset SessionStartedAt = DateTimeOffset.UtcNow;

        /// <summary>日志**捕获**是否已启动（插件 Load 时就会启动，与网页服务无关）。</summary>
        public static bool IsStarted => _started;

        /// <summary>网页服务（HTTP 监听）是否正在运行。由配置或游戏内指令决定。</summary>
        public static bool IsServerRunning => _server != null;

        /// <summary>监听模式：loopback（仅本机）/ lan / —（未运行）。</summary>
        public static string ServerAccessMode => _server?.AccessMode ?? "—";

        /// <summary>带令牌的地址；服务未运行时为空串。</summary>
        public static string Url => _server?.Url;

        public static int ClientCount => _server?.ClientCount ?? 0;

        public static long DroppedCount => Interlocked.Read(ref _dropped);

        public static long EmittedCount => Interlocked.Read(ref _emitted);

        public static int RingCount => _ring?.Count ?? 0;

        public static int RingCapacity => _ring?.Capacity ?? 0;

        public static int QueueDepth => Volatile.Read(ref _queued);

        /// <summary>
        /// 启动**日志捕获**（幂等）。
        ///
        /// <para>这一步与网页服务是分开的：捕获从插件加载就开始（只占一个环形缓冲和一个
        /// 后台线程，不监听端口、不开浏览器），所以**游戏启动阶段的日志也会被记下来**。
        /// 等你用游戏内指令 <c>/dshlog</c> 把网页开起来时，能看到的是从游戏启动开始的
        /// 完整历史 —— 而不是「开了之后才有的那几行」。调试时这一点很关键。</para>
        ///
        /// <para>网页服务是否随启动一起开，看 <see cref="LogViewerConfig.Enabled"/>
        /// （默认 false，即只在指令里开）。</para>
        /// </summary>
        /// <param name="configDir">
        /// BepInEx 的 config 目录。只有一件事用它：允许用
        /// <c>&lt;config&gt;/DSHCore/logviewer.html</c> 覆盖内嵌的查看器页面（改前端不用重编译）。
        /// </param>
        public static void Start(LogViewerConfig config, string configDir, ManualLogSource log)
        {
            lock (Gate)
            {
                if (_started)
                    return;

                _config = config ?? new LogViewerConfig();
                _configDir = configDir;
                _log = log;

                _ring = new LogRingBuffer(_config.RingBufferCapacity);

                _cts = new CancellationTokenSource();

                _worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "DSHCore.LogViewer.Drain",
                };
                _worker.Start();

                // 挂在 BepInEx 的全局监听表上 —— 这样捕获的是**所有插件**的日志，
                // 不只 DSHCore 自己的。DSHCore 作为前置先加载，所以后续插件
                // 产生的日志都会经过这里（监听表是在记录日志时才遍历的）。
                _listener = new BepInExLogListener();
                Logger.Listeners.Add(_listener);

                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

                _started = true;
            }

            // 隐藏控制台窗口与网页服务无关（可以只藏窗口、不开网页），所以放外面。
            if (_config.HideConsoleWindow)
                HideConsoleWindow();

            if (_config.Enabled)
            {
                if (StartServer(out var message))
                    _log?.LogInfo(message);
                else
                    _log?.LogWarning(message);
            }
            else
            {
                _log?.LogInfo("日志查看器待命（捕获已开启，共 " + RingCapacity + " 条环形缓冲）。" +
                              "游戏内输入 /" + LogViewerCommand.Name + " 开启网页。");
            }
        }

        /// <summary>
        /// 启动网页服务（幂等）。由配置的自动启动或游戏内指令调用。
        /// </summary>
        /// <param name="openBrowser">启动后是否用默认浏览器打开。</param>
        /// <param name="message">给调用方（日志 / 游戏内控制台）看的说明文字。</param>
        public static bool StartServer(out string message, bool openBrowser = true)
        {
            lock (Gate)
            {
                if (_server != null)
                {
                    message = "日志查看器已在运行：" + _server.Url;
                    return true;
                }

                var config = _config ?? new LogViewerConfig();
                var configDir = _configDir;

                try
                {
                    var server = new LogViewerServer(
                        config.AccessToken,
                        config.LanAccess,
                        configDir,
                        Snapshot,
                        BuildStatusJson);
                    server.SetConsoleToggleAllowed(config.AllowConsoleToggle);
                    server.Start(config.Port, config.PortFallbackCount);

                    _server = server;
                }
                catch (Exception ex)
                {
                    // 查看器起不来绝不能影响游戏 —— 降级为「只有磁盘日志」
                    message = "日志查看器启动失败（不影响游戏）：" + ex.Message;
                    return false;
                }

                var sb = new StringBuilder(200);
                sb.Append("日志查看器已启动：").Append(_server.Url);
                if (_server.LanUrls.Count > 0)
                {
                    sb.Append("\n局域网地址：");
                    sb.Append(string.Join("  ", _server.LanUrls));
                }
                message = sb.ToString();

                // 注意：这里是**在锁内**开浏览器 —— 开浏览器可能耗时几百毫秒，
                // 但这里已经是游戏主线程且是用户显式触发的操作，不影响正确性；
                // 换成异步会引入「浏览器比服务器先就绪」的竞争。
                if (openBrowser)
                    TryOpenInBrowser(_log);

                return true;
            }
        }

        /// <summary>关闭网页服务并释放端口（幂等）。内存历史保留，下次开启仍能看。返回是否真的关了。</summary>
        public static bool StopServer(out string message)
        {
            lock (Gate)
            {
                if (_server == null)
                {
                    message = "日志查看器当前未运行。";
                    return false;
                }

                message = "日志查看器已关闭（释放端口 " + _server.Port + "；内存历史保留 " + RingCount + " 条）。";

                try { _server.Dispose(); } catch { }
                _server = null;
                return true;
            }
        }

        /// <summary>隐藏 BepInEx 控制台窗口；不是本进程的窗口时什么也不做。</summary>
        public static void HideConsoleWindow()
        {
            if (ConsoleWindow.Hide())
                _log?.LogInfo("已隐藏 BepInEx 控制台窗口（输入 /" + LogViewerCommand.Name + " console 或改 BepInEx.cfg 可以恢复）。");
            else if (ConsoleWindow.IsAvailable && !ConsoleWindow.OwnsConsole)
                _log?.LogInfo("控制台窗口属于父进程（你是从终端启动的游戏），未隐藏。");
        }

        /// <summary>
        /// 停止管道并释放 HTTP 服务（幂等）。关卡切换之类不需要调它，
        /// 只在插件卸载 / 进程退出时用。
        /// </summary>
        public static void Stop()
        {
            lock (Gate)
            {
                if (!_started)
                    return;
                _started = false;

                try { AppDomain.CurrentDomain.ProcessExit -= OnProcessExit; } catch { }

                if (_listener != null)
                {
                    try { Logger.Listeners.Remove(_listener); } catch { }
                    try { _listener.Dispose(); } catch { }
                    _listener = null;
                }

                try { _cts?.Cancel(); } catch { }

                // 让后台线程有机会退出；它是后台线程，即使没退出也不会阻止进程结束
                try { _worker?.Join(TimeSpan.FromSeconds(1)); } catch { }
                _worker = null;
                try { _cts?.Dispose(); } catch { }
                _cts = null;

                _server?.Dispose();
                _server = null;
            }
        }

        public static LogRecord[] Snapshot(int limit) => _ring?.Snapshot(limit) ?? Array.Empty<LogRecord>();

        public static void ClearHistory() => _ring?.Clear();

        /// <summary>
        /// 把最近 <paramref name="limit"/> 条记录序列化成 JSON 数组。
        /// </summary>
        public static string SnapshotJson(int limit)
        {
            var items = Snapshot(limit);
            var sb = new StringBuilder(items.Length * 128 + 2);
            sb.Append('[');
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                items[i].WriteJson(sb, withDetail: true);
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary><c>GET /api/status</c> 的响应。</summary>
        public static string BuildStatusJson()
        {
            var sb = new StringBuilder(320);
            sb.Append("{\"session\":").Append(Json.Quoted(SessionId));
            sb.Append(",\"startedAt\":").Append(Json.Quoted(
                SessionStartedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
            sb.Append(",\"pid\":").Append(Environment.ProcessId);
            sb.Append(",\"capturing\":").Append(IsStarted ? "true" : "false");
            sb.Append(",\"running\":").Append(IsServerRunning ? "true" : "false");
            // 注意：这里**不能**返回带令牌的 URL（见 LogViewerServer.DisplayUrl 的注释）
            sb.Append(",\"url\":").Append(Json.Quoted(_server?.DisplayUrl ?? string.Empty));
            sb.Append(",\"accessMode\":").Append(Json.Quoted(_server?.AccessMode ?? "off"));
            sb.Append(",\"evaluated\":").Append(EmittedCount);
            sb.Append(",\"dropped\":").Append(DroppedCount);
            sb.Append(",\"bufferCount\":").Append(RingCount);
            sb.Append(",\"bufferCapacity\":").Append(RingCapacity);
            sb.Append(",\"queueDepth\":").Append(QueueDepth);
            sb.Append(",\"clients\":").Append(ClientCount);
            sb.Append(",\"consoleVisible\":").Append(ConsoleWindow.IsVisible ? "true" : "false");
            sb.Append(",\"consoleAvailable\":").Append(ConsoleWindow.IsAvailable ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>把 BepInEx 日志事件转换成记录并入队。</summary>
        private static void Enqueue(LogRecord record)
        {
            var config = _config;
            int capacity = config?.QueueCapacity ?? 20000;

            if (Volatile.Read(ref _queued) >= capacity)
            {
                Interlocked.Increment(ref _dropped);
                WarnAboutDrops();
                return;
            }

            Queue.Enqueue(record);
            Interlocked.Increment(ref _queued);
            Interlocked.Increment(ref _emitted);

            // Release 在 Enqueue 之后：worker 可能被唤醒时队列已非空
            Signal.Release();
        }

        /// <summary>队列溢出时给一次提示，但不要每条都提示（否则日志风暴会把日志淹了）。</summary>
        private static void WarnAboutDrops()
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastDropWarning < InternalWarningInterval)
                return;
            _lastDropWarning = now;

            try
            {
                Logger.CreateLogSource("DSHCore.LogViewer").LogWarning(
                    "日志队列已满，丢弃了 " + DroppedCount + " 条记录（查看器仍在工作）。");
            }
            catch { }
        }

        private static void WorkerLoop()
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Signal.Wait(token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                Drain();
            }
        }

        private static void Drain()
        {
            // 一次把队列排空即可：环形缓冲与 SSE 客户端各自是线程安全的
            while (Queue.TryDequeue(out var record))
            {
                Interlocked.Decrement(ref _queued);
                try
                {
                    _ring?.Add(record);
                    _server?.Broadcast(record);
                }
                catch
                {
                    // 单条记录处理失败绝不能拖垮工作线程
                }
            }
        }

        /// <summary>
        /// 在系统浏览器里打开查看器。用 <c>rundll32 url.dll</c> 而不是
        /// <c>UseShellExecute</c>：IL2CPP 宿主里直接起进程的路子更容易被拦，
        /// 而 rundll32 是 Windows 上最朴素可靠的一种。
        /// </summary>
        public static bool TryOpenInBrowser() => TryOpenInBrowser(_log);

        /// <summary>
        /// 在系统浏览器里打开查看器。用 <c>rundll32 url.dll</c> 而不是
        /// <c>UseShellExecute</c>：IL2CPP 宿主里直接起进程的路子更容易被拦，
        /// 而 rundll32 是 Windows 上最朴素可靠的一种。
        /// </summary>
        public static bool TryOpenInBrowser(ManualLogSource log)
        {
            var url = Url;
            if (string.IsNullOrEmpty(url))
                return false;

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "rundll32.exe",
                    Arguments = "url.dll,FileProtocolHandler " + url,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                log?.LogWarning("打不开浏览器（请手动访问 " + url + "）：" + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 供给外部（其它 mod / <c>LogViewer.Report</c>）直接推一条记录进来。
        /// 走的是和 BepInEx 监听器完全相同的路径。
        /// </summary>
        public static void ReportExternal(string source, LogLevel level, string message)
        {
            try
            {
                Enqueue(new LogRecord
                {
                    Id = Interlocked.Increment(ref _nextId),
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = LevelName(level),
                    Source = string.IsNullOrEmpty(source) ? "?" : source,
                    Message = message ?? string.Empty,
                });
            }
            catch
            {
                // 外部上报失败不应该影响调用方
            }
        }

        /// <summary>
        /// 把 <see cref="LogLevel"/>（[Flags] 枚举）映射成单一名。
        /// 组合值（如 <c>Warning|Error</c>）取**最严重**的那个，
        /// 而不是 <c>ToString()</c> 出的 "Warning, Error"。
        /// </summary>
        private static string LevelName(LogLevel level)
        {
            if ((level & LogLevel.Fatal) != 0) return "Fatal";
            if ((level & LogLevel.Error) != 0) return "Error";
            if ((level & LogLevel.Warning) != 0) return "Warning";
            if ((level & LogLevel.Message) != 0) return "Message";
            if ((level & LogLevel.Info) != 0) return "Info";
            if ((level & LogLevel.Debug) != 0) return "Debug";
            return level.ToString();
        }

        /// <summary>把日志载荷转成一行文本。异常只取 Message，完整堆栈另放 detail。</summary>
        private static string Describe(object data)
        {
            if (data == null)
                return string.Empty;
            if (data is string s)
                return s;
            if (data is Exception ex)
                return ex.GetType().Name + ": " + ex.Message;
            return data.ToString();
        }

        private static void OnProcessExit(object sender, EventArgs e) => Stop();

        /// <summary>
        /// BepInEx 日志监听器。挂在 <see cref="Logger.Listeners"/> 上，
        /// 因此能收到所有插件的日志。
        /// </summary>
        private sealed class BepInExLogListener : ILogListener
        {
            /// <summary>
            /// 不做二次过滤：BepInEx 自己的 <c>Logger.ListenedLogLevels</c> +
            /// <c>[Logging.Disk] LogLevels</c> 已经过滤过一轮了。这里再过滤一次
            /// 只会制造「改了配置却看不到 Debug」的困惑。
            /// </summary>
            public LogLevel LogLevelFilter => LogLevel.All;

            public void LogEvent(object sender, LogEventArgs eventArgs)
            {
                if (eventArgs == null)
                    return;

                try
                {
                    var record = new LogRecord
                    {
                        Id = Interlocked.Increment(ref _nextId),
                        Timestamp = DateTimeOffset.UtcNow,
                        Level = LevelName(eventArgs.Level),
                        Source = eventArgs.Source?.SourceName ?? "?",
                        Message = Describe(eventArgs.Data),
                    };

                    // 异常对象单独留出 detail，前端可以展开看堆栈
                    if (eventArgs.Data is Exception ex)
                        record.Detail = ex.ToString();

                    Enqueue(record);
                }
                catch
                {
                    // 监听器里抛异常会污染日志系统，任何情况都吞掉
                }
            }

            public void Dispose() { }
        }
    }
}
