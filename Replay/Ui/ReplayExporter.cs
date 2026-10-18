using System;

namespace Replay
{
    /// <summary>
    /// 「导出 .mrep」这一步的共用实现。
    ///
    /// <para><b>为什么要单独拎出来：</b>导出原先只有「回放结束时」那一个入口，
    /// 现在移到了回放页的【更多】面板里（结束面板上那个按钮已按需求删除）。
    /// 与其把那一段逻辑抄第二遍，不如放在这里，两个界面都调它。</para>
    ///
    /// <para><b>走的是游戏原生的「另存为」对话框</b>（<c>StandaloneFileBrowser.SaveFilePanel</c>，
    /// 与「切换存档 → 导出」同一条路），目标由玩家自己挑。</para>
    ///
    /// <para>⚠️ 直接调 SFB 而不是 <c>FileHelper.SaveExternalFile</c>：后者是
    /// <c>async Task&lt;string&gt;</c>，在 interop 里变成 <c>Il2CppSystem.Threading.Tasks.Task&lt;string&gt;</c>
    /// （**不派生自** <c>System.Threading.Tasks.Task</c>，没有 <c>GetAwaiter</c>，await 不了），
    /// 而 <c>SaveFilePanel</c> 本身就是同步返回路径的。</para>
    ///
    /// <para>⚠️ 那个对话框在 Windows 上是**阻塞式**的系统对话框，弹着的时候整个主线程都停在那儿 ——
    /// 游戏自己的导入导出也是这个行为。所以自测**不要**走它（见 <c>ReplayPage.SelfTestExport</c>）。</para>
    /// </summary>
    internal static class ReplayExporter
    {
        /// <summary>弹出「另存为」并导出。<paramref name="cancelled"/> = 玩家点了取消。</summary>
        public static bool DialogExport(string replayPath, out string dest, out string error, out bool cancelled)
        {
            dest = null;
            error = null;
            cancelled = false;

            if (string.IsNullOrEmpty(replayPath))
            {
                error = "没有可导出的文件。";
                return false;
            }

            try
            {
                dest = SFB.StandaloneFileBrowser.SaveFilePanel("", "", BareName(replayPath), "mrep");
            }
            catch (Exception e)
            {
                error = "打开保存对话框失败：" + e.GetType().Name + "：" + e.Message;
                return false;
            }

            if (string.IsNullOrEmpty(dest))
            {
                cancelled = true;
                return false;
            }

            try
            {
                return ReplayLibrary.Export(replayPath, dest, out error);
            }
            catch (Exception e)
            {
                error = e.GetType().Name + "：" + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 去掉 <c>.mrep</c> / <c>.json</c> 这一串尾巴，只留裸名字。
        /// <c>ReplayPlayer.CurrentPath</c> 多半是 <c>x.mrep.json</c>，
        /// 直接 <c>GetFileNameWithoutExtension</c> 会得到 <c>x.mrep</c>，
        /// 再交给「另存为」就变成 <c>x.mrep.mrep</c> 了。
        /// </summary>
        public static string BareName(string path)
        {
            try
            {
                var n = System.IO.Path.GetFileName(path ?? string.Empty);
                if (n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    n = n.Substring(0, n.Length - 5);
                if (n.EndsWith(".mrep", StringComparison.OrdinalIgnoreCase))
                    n = n.Substring(0, n.Length - 5);
                return string.IsNullOrEmpty(n) ? "replay" : n;
            }
            catch (Exception)
            {
                return "replay";
            }
        }
    }
}
