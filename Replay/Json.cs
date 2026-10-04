using System;
using System.Globalization;
using System.Text;
using PVZEngine;

namespace Replay
{
    /// <summary>
    /// 极简 JSON 输出助手。
    ///
    /// 为什么不直接用游戏的 <c>SerializeHelper.ToBson</c>：那是给游戏自己的类型用的
    /// （BSON 需要预先注册序列化器）。我们的事件记录是一堆 {string, int} 的扁平结构，
    /// 手写反而更快、更可控、也更好读。
    ///
    /// 转义规则按 RFC 8259：<c>"</c>、<c>\</c>、U+0000..U+001F 必须转义，<c>/</c> 不需要。
    /// </summary>
    internal static class Json
    {
        public static string Str(string s)
        {
            if (s == null)
                return "null";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
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
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);
        public static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);
        public static string Num(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        public static string Bool(bool v) => v ? "true" : "false";
        public static string Hex(ulong v) => v.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// NamespaceID 的字符串化。
    ///
    /// ⚠️ interop 下 <c>NamespaceID.ToString()</c> 只会打印**类型名**，
    /// 所以必须自己拼 <c>SpaceName + ":" + Path</c>（本仓库已在 Loadout 踩过一次）。
    /// 原生指针为空时 interop 一般返回 null，但仍加一层防御。
    /// </summary>
    internal static class Ids
    {
        public static string Ns(NamespaceID id)
        {
            if (id == null)
                return null;
            try
            {
                if (id.Pointer == IntPtr.Zero)
                    return null;
                var space = id.SpaceName;
                var path = id.Path;
                if (string.IsNullOrEmpty(space) || string.IsNullOrEmpty(path))
                    return null;
                return space + ":" + path;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
