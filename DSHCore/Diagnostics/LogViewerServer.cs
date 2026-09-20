using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DSHCore.Diagnostics
{
    /// <summary>
    /// 本地网页日志查看器的 HTTP 服务。
    ///
    /// <para><b>为什么手写 HTTP，而不用 <c>System.Net.HttpListener</c>？</b></para>
    /// <para>
    /// <c>HttpListener</c> 走 Windows 的 HTTP.sys，非管理员进程要监听一个 URL 前缀
    /// 必须先 <c>netsh http add urlacl</c> 注册 —— 一个 mod 不可能要求玩家做这件事
    /// （而且不少机器上未注册时会直接抛 <c>HttpListenerException: Access is denied</c>）。
    /// 裸 <c>TcpListener</c> 监听回环端口不需要任何特权。这也是 RitsuLib 的
    /// <c>RitsuDebugLogViewerServer</c> 选择手写 HTTP/1.1 的同一个理由。
    /// </para>
    ///
    /// <para><b>为什么只支持 GET 且忽略请求头？</b></para>
    /// <para>
    /// 我们只提供「读日志」这一件事，全部是无副作用（除 /api/console、/api/clear）
    /// 的 GET。既不需要请求体，也不需要任何请求头，所以只读到第一行就够，读完立即
    /// 应答并关闭连接（响应统一带 <c>Connection: close</c>）。这比实现完整的
    /// 请求头状态机少了一大类边界 bug。
    /// </para>
    ///
    /// <para><b>线程安全</b>：本类只被 <see cref="LogPipeline"/> 的后台线程调用
    /// <see cref="Broadcast"/>；其余全是纯托管 I/O 线程。**不得触碰 IL2CPP 类型。**</para>
    /// </summary>
    internal sealed class LogViewerServer : IDisposable
    {
        private const int MaxConcurrentRequests = 64;
        private const int MaxRequestLineBytes = 8192;
        private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan SseKeepAlive = TimeSpan.FromSeconds(15);

        /// <summary>内嵌资源的名字（见 DSHCore.csproj 的 EmbeddedResource LogicalName）。</summary>
        private const string ViewerResourceName = "DSHCore.logviewer.html";

        private readonly string _token;
        private readonly string _configDir;
        private readonly Func<int, LogRecord[]> _historyProvider;
        private readonly Func<string> _statusProvider;
        private readonly ConcurrentDictionary<SseClient, byte> _clients =
            new ConcurrentDictionary<SseClient, byte>();
        private readonly SemaphoreSlim _requestSlots =
            new SemaphoreSlim(MaxConcurrentRequests, MaxConcurrentRequests);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>是否允许通过 <c>/api/console</c> 控制控制台窗口（由配置决定）。</summary>
        private bool _consoleToggleAllowed = true;

        private TcpListener _listener;
        private Task _acceptTask;
        private int _disposed;

        public LogViewerServer(
            string token,
            bool lanAccess,
            string configDir,
            Func<int, LogRecord[]> historyProvider,
            Func<string> statusProvider)
        {
            _token = token ?? string.Empty;
            LanAccessEnabled = lanAccess;
            _configDir = configDir;
            _historyProvider = historyProvider;
            _statusProvider = statusProvider;
        }

        public int Port { get; private set; }

        public bool LanAccessEnabled { get; }

        public int ClientCount => _clients.Count;

        public string AccessMode => LanAccessEnabled ? "lan" : "loopback";

        /// <summary>本机访问地址（含令牌）。只在启动日志里用一次 —— 不要放进 HTTP 响应。</summary>
        public string Url => Port > 0 ? BuildUrl("127.0.0.1") : string.Empty;

        /// <summary>
        /// 不带令牌的展示用地址。
        ///
        /// <para>为什么需要它：令牌是本会话的本地凭证。把带令牌的 URL 放进
        /// <c>/api/status</c> 的响应体，等于让它跑到截图、日志、issue 附件里 ——
        /// 玩家自己的地址栏里本来就有完整 URL，接口没有理由再吐一份。</para>
        /// </summary>
        public string DisplayUrl => Port > 0 ? "http://127.0.0.1:" + Port + "/" : string.Empty;

        public IReadOnlyList<string> LanUrls => LanAccessEnabled ? GetLanUrls() : Array.Empty<string>();

        /// <summary>
        /// 绑定端口并开始接收。端口被占用时按 <paramref name="fallbackCount"/> 依次顺延。
        /// </summary>
        public void Start(int requestedPort, int fallbackCount)
        {
            int first = Math.Min(Math.Max(requestedPort, 1), 65535);
            int attempts = Math.Min(Math.Max(fallbackCount, 0) + 1, 65536 - first);

            Exception lastError = null;
            for (int i = 0; i < attempts; i++)
            {
                int port = first + i;
                TcpListener candidate = null;
                try
                {
                    candidate = new TcpListener(
                        LanAccessEnabled ? IPAddress.Any : IPAddress.Loopback,
                        port);
                    candidate.Start();
                    _listener = candidate;
                    Port = port;
                    break;
                }
                catch (SocketException ex)
                {
                    lastError = ex;
                    try { candidate?.Stop(); } catch { }
                }
            }

            if (_listener == null)
            {
                throw new InvalidOperationException(
                    "无法绑定端口 " + first + ".." + (first + attempts - 1) + "，全部被占用。", lastError);
            }

            _acceptTask = Task.Run(AcceptLoopAsync);
        }

        /// <summary>把一条记录推给所有 SSE 客户端。由 <see cref="LogPipeline"/> 的后台线程调用。</summary>
        public void Broadcast(LogRecord record)
        {
            if (record == null || _clients.IsEmpty)
                return;

            var sb = new StringBuilder(256);
            record.WriteJson(sb, withDetail: false);
            var json = sb.ToString();

            foreach (var client in _clients.Keys)
                client.Enqueue(json);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { _cts.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;

            _clients.Clear();

            try { _cts.Dispose(); } catch { }
        }

        // ---------------------------------------------------------------
        // 接收 / 分发
        // ---------------------------------------------------------------

        private async Task AcceptLoopAsync()
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { continue; }   // 单个连接异常不应终止整个服务
                catch (Exception) { return; }

                _ = HandleAdmittedAsync(client, token);
            }
        }

        private async Task HandleAdmittedAsync(TcpClient client, CancellationToken token)
        {
            try
            {
                await _requestSlots.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CloseClient(client);
                return;
            }

            try
            {
                await HandleClientAsync(client, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                // 客户端断线、半包等都属于正常现象，不打扰日志
            }
            finally
            {
                _requestSlots.Release();
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            var stream = client.GetStream();

            var requestLine = await TryReadRequestLineAsync(stream, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(requestLine))
                return;

            var parts = requestLine.Split(' ');
            if (parts.Length < 2)
            {
                await WriteTextAsync(stream, 400, "Bad Request", "Malformed request line.").ConfigureAwait(false);
                return;
            }

            if (!string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(stream, 405, "Method Not Allowed", "Only GET is supported.").ConfigureAwait(false);
                return;
            }

            var target = parts[1];
            var path = target;
            var query = string.Empty;
            int q = target.IndexOf('?', StringComparison.Ordinal);
            if (q >= 0)
            {
                path = target.Substring(0, q);
                query = target.Substring(q + 1);
            }

            // 页面本身不带令牌也能取（否则用户没法「打开」它）；
            // 真正的数据接口一律要令牌。
            if (string.Equals(path, "/", StringComparison.Ordinal) ||
                string.Equals(path, "/index.html", StringComparison.OrdinalIgnoreCase))
            {
                await WriteViewerPageAsync(stream).ConfigureAwait(false);
                return;
            }

            if (!IsAuthorized(query))
            {
                await WriteTextAsync(stream, 401, "Unauthorized",
                    "Invalid token. Open the URL printed in the BepInEx log (it contains ?token=...).").ConfigureAwait(false);
                return;
            }

            switch (path)
            {
                case "/api/status":
                    await WriteJsonAsync(stream, _statusProvider()).ConfigureAwait(false);
                    return;

                case "/api/history":
                    await WriteJsonAsync(stream, LogPipeline.SnapshotJson(ParseLimit(query, 5000))).ConfigureAwait(false);
                    return;

                case "/api/events":
                    await HandleSseAsync(stream, token).ConfigureAwait(false);
                    return;

                case "/api/clear":
                    LogPipeline.ClearHistory();
                    await WriteJsonAsync(stream, "{\"ok\":true}").ConfigureAwait(false);
                    return;

                case "/api/console":
                    await WriteJsonAsync(stream, HandleConsoleCommand(query)).ConfigureAwait(false);
                    return;

                default:
                    await WriteTextAsync(stream, 404, "Not Found", "Not found.").ConfigureAwait(false);
                    return;
            }
        }

        /// <summary>
        /// 只读到第一行（请求行），失败返回 null。理由见类注释：我们只支持 GET、
        /// 不需要请求体、也不需要任何请求头，读完即应答并关闭连接。
        /// </summary>
        private static async Task<string> TryReadRequestLineAsync(NetworkStream stream, CancellationToken token)
        {
            var buffer = new byte[MaxRequestLineBytes];
            int length = 0;

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(RequestReadTimeout);

                while (length < buffer.Length)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(buffer, length, buffer.Length - length, timeout.Token)
                                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }
                    catch (IOException)
                    {
                        return null;
                    }

                    if (read <= 0)
                        return null;

                    int scanFrom = length;
                    length += read;

                    for (int i = scanFrom; i < length; i++)
                    {
                        if (buffer[i] != (byte)'\n')
                            continue;

                        int end = i;
                        if (end > 0 && buffer[end - 1] == (byte)'\r')
                            end--;

                        return Encoding.ASCII.GetString(buffer, 0, end);
                    }
                }
            }

            return null;
        }

        // ---------------------------------------------------------------
        // SSE
        // ---------------------------------------------------------------

        private async Task HandleSseAsync(NetworkStream stream, CancellationToken token)
        {
            var client = new SseClient();
            _clients.TryAdd(client, 0);

            try
            {
                await WriteRawAsync(stream,
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/event-stream; charset=utf-8\r\n" +
                    "Cache-Control: no-cache\r\n" +
                    "Connection: keep-alive\r\n" +
                    "X-Accel-Buffering: no\r\n\r\n").ConfigureAwait(false);

                // 首个事件带上会话状态，前端据此判断「是不是新的一局」
                await WriteRawAsync(stream, "event: session\ndata: " + _statusProvider() + "\n\n")
                    .ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);

                var builder = new StringBuilder(320);
                while (!token.IsCancellationRequested)
                {
                    string json;
                    try
                    {
                        json = await client.DequeueAsync(SseKeepAlive, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (json == null)
                    {
                        // 注释行当心跳，防止中间设备因为长时间无数据而掐断连接
                        await WriteRawAsync(stream, ": keepalive\n\n").ConfigureAwait(false);
                    }
                    else
                    {
                        builder.Clear();
                        builder.Append("event: log\ndata: ").Append(json).Append("\n\n");
                        await WriteRawAsync(stream, builder.ToString()).ConfigureAwait(false);
                    }

                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                _clients.TryRemove(client, out _);
            }
        }

        // ---------------------------------------------------------------
        // /api/console
        // ---------------------------------------------------------------

        private string HandleConsoleCommand(string query)
        {
            if (!_consoleToggleAllowed)
                return "{\"ok\":false,\"reason\":\"disabled\"}";

            if (!ConsoleWindow.IsAvailable)
                return "{\"ok\":false,\"reason\":\"no-console\"}";

            if (!ConsoleWindow.OwnsConsole)
                return "{\"ok\":false,\"reason\":\"not-our-window\"}";

            var action = GetQueryValue(query, "action") ?? "toggle";
            bool visible;
            if (string.Equals(action, "hide", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleWindow.Hide();
                visible = ConsoleWindow.IsVisible;
            }
            else if (string.Equals(action, "show", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleWindow.Show();
                visible = ConsoleWindow.IsVisible;
            }
            else
            {
                visible = ConsoleWindow.Toggle();
            }

            return "{\"ok\":true,\"visible\":" + (visible ? "true" : "false") + "}";
        }

        public void SetConsoleToggleAllowed(bool allowed) => _consoleToggleAllowed = allowed;

        // ---------------------------------------------------------------
        // 静态页面
        // ---------------------------------------------------------------

        private async Task WriteViewerPageAsync(NetworkStream stream)
        {
            byte[] html = null;

            // 1) 磁盘覆盖（改前端不用重编译）：<BepInEx>/config/DSHCore/logviewer.html
            try
            {
                if (!string.IsNullOrEmpty(_configDir))
                {
                    var overridePath = Path.Combine(_configDir, "DSHCore", "logviewer.html");
                    if (File.Exists(overridePath))
                        html = File.ReadAllBytes(overridePath);
                }
            }
            catch { }

            // 2) 内嵌资源
            if (html == null)
            {
                try
                {
                    var assembly = typeof(LogViewerServer).Assembly;
                    using (var s = assembly.GetManifestResourceStream(ViewerResourceName))
                    {
                        if (s != null)
                        {
                            using (var ms = new MemoryStream())
                            {
                                s.CopyTo(ms);
                                html = ms.ToArray();
                            }
                        }
                    }
                }
                catch { }
            }

            if (html == null)
            {
                await WriteTextAsync(stream, 503, "Service Unavailable",
                    "Viewer page is unavailable (embedded resource '" + ViewerResourceName + "' missing).")
                    .ConfigureAwait(false);
                return;
            }

            await WriteBytesAsync(stream, 200, "OK", "text/html; charset=utf-8", html).ConfigureAwait(false);
        }

        // ---------------------------------------------------------------
        // 响应工具
        // ---------------------------------------------------------------

        private static Task WriteJsonAsync(NetworkStream stream, string json) =>
            WriteBytesAsync(stream, 200, "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));

        private static Task WriteTextAsync(NetworkStream stream, int status, string reason, string text) =>
            WriteBytesAsync(stream, status, reason, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

        private static async Task WriteBytesAsync(
            NetworkStream stream, int status, string reason, string contentType, byte[] body)
        {
            var header = new StringBuilder(160);
            header.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            header.Append("Content-Type: ").Append(contentType).Append("\r\n");
            header.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            header.Append("Cache-Control: no-store\r\n");
            header.Append("Connection: close\r\n\r\n");

            await WriteRawAsync(stream, header.ToString()).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static Task WriteRawAsync(NetworkStream stream, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return stream.WriteAsync(bytes, 0, bytes.Length);
        }

        private static void CloseClient(TcpClient client)
        {
            try { client?.Close(); } catch { }
        }

        // ---------------------------------------------------------------
        // 杂项
        // ---------------------------------------------------------------

        private bool IsAuthorized(string query)
        {
            if (string.IsNullOrEmpty(_token))
                return false;

            var provided = GetQueryValue(query, "token");
            if (string.IsNullOrEmpty(provided))
                return false;

            // 定长比较，避免用响应时间泄漏令牌内容（本地工具也不该养成坏习惯）
            return FixedTimeEquals(provided, _token);
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            var x = Encoding.UTF8.GetBytes(a);
            var y = Encoding.UTF8.GetBytes(b);
            if (x.Length != y.Length)
                return false;

            int diff = 0;
            for (int i = 0; i < x.Length; i++)
                diff |= x[i] ^ y[i];
            return diff == 0;
        }

        private static int ParseLimit(string query, int fallback)
        {
            var raw = GetQueryValue(query, "limit");
            int value;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, out value))
                return Math.Min(Math.Max(value, 1), 200000);
            return fallback;
        }

        private static string GetQueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query))
                return null;

            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0)
                    continue;

                int eq = pair.IndexOf('=');
                var rawKey = eq >= 0 ? pair.Substring(0, eq) : pair;
                if (!string.Equals(Uri.UnescapeDataString(rawKey), key, StringComparison.Ordinal))
                    continue;

                var rawValue = eq >= 0 ? pair.Substring(eq + 1) : string.Empty;
                // 表单式编码里 '+' 代表空格
                return Uri.UnescapeDataString(rawValue.Replace("+", "%20"));
            }
            return null;
        }

        private string BuildUrl(string host) =>
            "http://" + host + ":" + Port + "/?token=" + Uri.EscapeDataString(_token);

        private IReadOnlyList<string> GetLanUrls()
        {
            if (Port <= 0)
                return Array.Empty<string>();

            var result = new List<string>();
            try
            {
                foreach (var address in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(address))
                        continue;

                    var bytes = address.GetAddressBytes();
                    if (bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0)
                        continue;
                    if (bytes[0] == 169 && bytes[1] == 254)
                        continue;   // APIPA 自动私有地址，没人能连上

                    var url = BuildUrl(address.ToString());
                    if (!result.Contains(url))
                        result.Add(url);
                }
            }
            catch
            {
                // 枚举网卡失败不影响查看器本身
            }
            return result;
        }

        /// <summary>
        /// 单个 SSE 客户端的待发队列。
        ///
        /// <para>有界：浏览器卡住时不能无限制堆内存。超过上限就丢最旧的 —— 实时日志
        /// 场景下「最新的」永远比「最旧的」有价值。</para>
        /// </summary>
        private sealed class SseClient
        {
            private const int MaxBacklog = 2048;

            private readonly object _gate = new object();
            private readonly Queue<string> _queue = new Queue<string>();
            private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);

            public void Enqueue(string json)
            {
                lock (_gate)
                {
                    if (_queue.Count >= MaxBacklog)
                        _queue.Dequeue();
                    _queue.Enqueue(json);
                }
                // 信号量计数可能与队列长度短暂不一致（丢旧记录时），
                // 多出来的那次唤醒只会让调用方拿到 null → 发一个心跳，无害。
                _signal.Release();
            }

            public async Task<string> DequeueAsync(TimeSpan timeout, CancellationToken token)
            {
                if (!await _signal.WaitAsync(timeout, token).ConfigureAwait(false))
                    return null;

                lock (_gate)
                {
                    return _queue.Count > 0 ? _queue.Dequeue() : null;
                }
            }
        }
    }
}
