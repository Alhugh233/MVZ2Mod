using System;
using System.Globalization;
using System.Text;

namespace DSHCore.Diagnostics
{
    /// <summary>
    /// 一条日志记录 —— 发给网页查看器的载荷。
    ///
    /// 刻意写成只有公有字段的普通类（不用 record / 不挂特性）：序列化完全手写，
    /// 见 <see cref="WriteJson"/>。理由见 <see cref="Json"/> 的注释。
    /// </summary>
    internal sealed class LogRecord
    {
        /// <summary>会话内自增序号（从 1 开始）。前端用它去重、排序。</summary>
        public long Id;

        public DateTimeOffset Timestamp;

        /// <summary>Fatal / Error / Warning / Message / Info / Debug 之一。</summary>
        public string Level = string.Empty;

        /// <summary>日志来源（BepInEx 的 <c>ILogSource.SourceName</c>），用于按插件过滤。</summary>
        public string Source = string.Empty;

        public string Message = string.Empty;

        /// <summary>附加信息（异常堆栈等）。没有时为 null。</summary>
        public string Detail;

        public void WriteJson(StringBuilder sb, bool withDetail)
        {
            sb.Append("{\"id\":").Append(Id);

            // 本地时间给人看，UTC 时间给程序处理（跨时区比对时不会歧义）
            sb.Append(",\"time\":\"")
              .Append(Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append("\",\"ts\":\"")
              .Append(Timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))
              .Append('"');

            sb.Append(",\"level\":\"");
            Json.AppendEscaped(sb, Level);
            sb.Append("\",\"source\":\"");
            Json.AppendEscaped(sb, Source);
            sb.Append("\",\"message\":\"");
            Json.AppendEscaped(sb, Message);
            sb.Append('"');

            if (withDetail && !string.IsNullOrEmpty(Detail))
            {
                sb.Append(",\"detail\":\"");
                Json.AppendEscaped(sb, Detail);
                sb.Append('"');
            }

            sb.Append('}');
        }
    }

    /// <summary>
    /// 手写 JSON 字符串转义。
    ///
    /// <para>为什么不用 <c>System.Text.Json</c>：</para>
    /// <para>
    /// 它是 BepInEx 自带 .NET 运行时（<c>dotnet/</c>）里的程序集。虽然实测存在，
    /// 但这是一个**前置库** —— 它一旦因为程序集解析失败而加载不了，所有依赖它的 mod
    /// 全部失效。而我们的 schema 只有 5 个字段、完全固定，手写的收益（零依赖、
    /// 零反射）远大于成本。DSHCore 里凡是能避免的运行期依赖一律避免。
    /// </para>
    /// </summary>
    internal static class Json
    {
        /// <summary>
        /// 把 <paramref name="s"/> 转义后追加（**不含**首尾引号）。
        ///
        /// 规则按 RFC 8259：<c>"</c> 与 <c>\</c> 必须转义，U+0000..U+001F 必须转义。
        /// 不转义 <c>/</c>（JSON 允许，且不转义更好读）；不处理 U+2028/2029
        /// （那是 JS 字符串字面量的坑，我们是 <c>JSON.parse</c>，不受影响）。
        /// </summary>
        public static void AppendEscaped(StringBuilder sb, string s)
        {
            if (string.IsNullOrEmpty(s))
                return;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            // 其余控制字符走 \u00XX
                            sb.Append("\\u")
                              .Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
        }

        /// <summary>把字符串转义并按 JSON 字符串返回（含首尾引号）。仅用于小字符串。</summary>
        public static string Quoted(string s)
        {
            var sb = new StringBuilder((s?.Length ?? 0) + 2);
            sb.Append('"');
            AppendEscaped(sb, s);
            sb.Append('"');
            return sb.ToString();
        }
    }
}
