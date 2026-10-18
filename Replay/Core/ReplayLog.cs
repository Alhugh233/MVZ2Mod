using System;
using System.Collections.Generic;

namespace Replay
{
    /// <summary>
    /// 本 mod 的日志小工具。
    ///
    /// <para>存在的理由只有一个：<c>DSHCore.Ui.UiLog</c> 是 <c>internal</c> 的，
    /// 跨程序集用不了。UI 代码里「同一个原因只该说一次」的场合特别多
    /// （素材没就绪、按钮模板找不到……这些在每帧的检查里会被反复碰到），
    /// 所以复制一份去重逻辑过来，接口保持一致，代码可以原样搬。</para>
    /// </summary>
    internal static class ReplayLog
    {
        private static readonly HashSet<string> Warned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>同一个 <paramref name="key"/> 只警告一次。</summary>
        public static void WarnOnce(string key, string message)
        {
            if (key == null)
                key = message ?? "";
            if (!Warned.Add(key))
                return;
            Warn(message);
        }

        /// <summary>每次都警告。</summary>
        public static void Warn(string message)
        {
            try { Plugin.Log?.LogWarning("[Replay] " + message); }
            catch (Exception) { }
        }

        public static void Info(string message)
        {
            try { Plugin.Log?.LogInfo("[Replay] " + message); }
            catch (Exception) { }
        }

        /// <summary>排障用：已经说过哪些 key。</summary>
        public static int WarnedCount { get { return Warned.Count; } }
    }
}
