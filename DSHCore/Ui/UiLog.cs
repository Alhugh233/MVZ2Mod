using BepInEx.Logging;

namespace DSHCore.Ui
{
    /// <summary>
    /// UI 基础设施的内部日志出口。
    ///
    /// <para>全部走 DSHCore 自己的日志通道（<c>[DSHCore]</c> 前缀），这样排查 UI 问题时
    /// 可以只用 <c>grep DSHCore</c> 把相关行捞出来。等级刻意偏低：UI 素材找不到是
    /// **正常情况**（例如标题界面还没有 <c>OptionWidgets</c>），不该刷屏。</para>
    /// </summary>
    internal static class UiLog
    {
        /// <summary>已警告过的条目，避免同一件事每帧刷一行。</summary>
        private static readonly System.Collections.Generic.HashSet<string> Warned =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

        public static void Info(string message)
        {
            try { Plugin.Log?.LogInfo("[Ui] " + message); }
            catch { }
        }

        /// <summary>警告，同一个 <paramref name="key"/> 只打一次。</summary>
        public static void WarnOnce(string key, string message)
        {
            try
            {
                if (!Warned.Add(key))
                    return;
                Plugin.Log?.LogWarning("[Ui] " + message);
            }
            catch { }
        }

        public static void Warn(string message)
        {
            try { Plugin.Log?.LogWarning("[Ui] " + message); }
            catch { }
        }

        /// <summary>清空"已警告"记录（换了场景/强制重建素材时调）。</summary>
        public static void ResetOnce()
        {
            try { Warned.Clear(); }
            catch { }
        }
    }
}
