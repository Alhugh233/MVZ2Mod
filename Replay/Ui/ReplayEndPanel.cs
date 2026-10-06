using System;
using System.Globalization;
using System.Text;
using MVZ2.Managers;
using MVZ2.Scenes;

// DSHCore 的 UiLog 是 internal，跨程序集用不了 —— 别名到本 mod 自己的那份。
using UiLog = Replay.ReplayLog;

namespace Replay
{
    /// <summary>
    /// 回放结束时的面板：<b>导出 .mrep / 重新播放 / 退出</b>。
    ///
    /// <para><b>为什么重新写一个而不复用 <c>ReplayPage.Confirm</c>：</b>
    /// 那个是「回放列表页」的私有浮层，而这里要的是**关卡内**的弹窗。
    /// 关卡内的正路是游戏自己的
    /// <c>MainSceneController.ShowDialog(title, desc, options, onSelect)</c> ——
    /// 它直接暴露官方 <c>CustomDialog</c>，支持任意个按钮，
    /// 而且原版「确认要重新开始关卡吗？」走的**就是**这条（所以它天然能在关卡里正常显示、
    /// 有全屏 Blocker、位置也由原版 <c>ResetPosition</c> 摆好）。</para>
    ///
    /// <para><b>「导出」的结果提示</b>：<c>ShowDialog</c> 是「选完就把对话框收起来」的语义
    /// （回调在 <c>postSelect</c> 里、**在隐藏之后**才跑），所以不能在同一次弹窗里换内容。
    /// 做法是接着弹第二个单按钮提示，它的回调再把三按钮面板弹回来。</para>
    ///
    /// <para>失败一律吞掉只记日志 —— 一个提示面板绝不能把播放收尾搞崩。</para>
    /// </summary>
    internal static class ReplayEndPanel
    {
        private const string Title = "回放结束";
        private const string ExportButton = "导出 .mrep";
        private const string ReplayButton = "重新播放";
        private const string ExitButton = "退出";

        private static bool _open;
        private static string _info = "-";
        private static int _shows;

        /// <summary>当前有没有正开着（用来拦掉玩家的输入，见 <see cref="ReplayPlayer.ShouldBlockInput"/>）。</summary>
        public static bool IsOpen { get { return _open; } }

        /// <summary>最近一次的状态描述（写进自测报告）。</summary>
        public static string Describe() { return _info; }

        public static int ShowCount { get { return _shows; } }

        /// <summary>
        /// 弹结束面板。重复调用是幂等的（已经开着就不再弹）。
        /// <paramref name="reason"/> 直接用 <see cref="ReplayPlayer.StopReason"/> 的值。
        /// </summary>
        public static void Show(string reason)
        {
            if (_open)
            {
                _info = "已经开着（reason=" + reason + "）";
                return;
            }

            _shows++;
            _open = true;
            _note = null;
            _info = "已弹出（reason=" + reason + "，第 " + _shows + " 次）";
            try
            {
                ShowMain(reason);
            }
            catch (Exception e)
            {
                _open = false;
                _info = "弹窗失败：" + e.GetType().Name + "：" + e.Message;
                UiLog.WarnOnce("rp-endpanel", "弹回放结束面板失败：" + e.GetType().Name + "：" + e.Message);
            }
        }

        /// <summary>关掉（不再显示）。注意：真正的对话框是游戏管理的，这里只清我们的状态。</summary>
        public static void Close()
        {
            _open = false;
        }

        /// <summary>
        /// 自测用：模拟玩家点了第 <paramref name="index"/> 个按钮。
        ///
        /// <para>走的是**和点击完全相同**的那个委托，所以导出/重播/退出三条路都能被自动跑到
        /// （否则只能靠人手点，回归测试覆盖不到）。</para>
        /// </summary>
        public static bool SelfTestChoose(int index)
        {
            var cb = _onSelect;
            if (!_open || cb == null)
                return false;
            cb(index);
            return true;
        }

        /// <summary>
        /// 自测用：把游戏那个 <c>CustomDialog</c> 收起来。
        ///
        /// <para><b>为什么需要：</b>真玩家点按钮时，<c>MainSceneUI</c> 传进去的那个 lambda 会先
        /// <c>dialog.gameObject.SetActive(false)</c> 再调我们的回调；而 <see cref="SelfTestChoose"/>
        /// 是直接调回调、**绕过了那一步** —— 结果就是「面板的逻辑已经处理完了、框体还挂在屏幕上」，
        /// 纯属自测假象（还会污染之后的截图）。这里把那一步补上。</para>
        /// </summary>
        public static bool SelfTestHideDialog()
        {
            try
            {
                // 直接按类型找：MainSceneUI 的 dialog 是私有序列化字段，
                // interop 侧它是**属性**不是字段，AccessTools.Field 拿不到。
                var dlg = DSHCore.Ui.UiFinder.FindOne<MVZ2.UI.CustomDialog>();
                if (dlg == null || dlg.Pointer == IntPtr.Zero)
                    return false;
                dlg.gameObject.SetActive(false);
                return true;
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("rp-endpanel-hide", "自测收对话框失败：" + e.GetType().Name);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 面板
        // ------------------------------------------------------------------

        private static void ShowMain(string reason)
        {
            var scene = Scene();
            if (scene == null)
            {
                _open = false;
                _info = "拿不到主场景控制器";
                return;
            }

            var options = new[] { ExportButton, ReplayButton, ExitButton };
            var onSelect = new Action<int>(index =>
            {
                var picked = index >= 0 && index < options.Length ? options[index] : ("#" + index);
                _info = "选了「" + picked + "」";
                // 注意：**不在这里把 _open 置 false** ——
                // 「导出」随后会把面板重弹一次（带上结果），中间那一下不应该把输入拦挡松开。
                // 真正结束（重播 / 退出）由各自的处理函数自己调 Close()。
                try
                {
                    switch (index)
                    {
                        case 0: DoExport(); break;
                        case 1: DoReplay(); break;
                        default: DoExit(); break;
                    }
                }
                catch (Exception e)
                {
                    _open = false;
                    _info = "处理选择失败：" + e.GetType().Name + "：" + e.Message;
                    UiLog.WarnOnce("rp-endpanel-sel", "结束面板处理选择失败：" + e.GetType().Name + "：" + e.Message);
                }
            });
            _onSelect = onSelect;

            scene.ShowDialog(Title, BuildDesc(reason), options, onSelect);
        }

        /// <summary>
        /// 「导出 .mrep」：弹游戏**原生的「另存为」对话框**，写到玩家自己挑的位置。
        ///
        /// <para>与「主菜单 → 切换存档 → 导出」用的是同一个系统对话框。
        /// 以前是配置一个固定目录直接拷过去 —— 玩家既不知道拷到哪了，
        /// 也没法自己选位置，所以改成原生的这个。</para>
        ///
        /// <para><b>结果为什么不是另弹一个提示框：</b>游戏的对话框是**排队显示**的，
        /// 「面板 → 提示 → 再弹回面板」这种链式弹窗很快就踩到排队时序（实测会出现
        /// 「面板已经换了、旧的那个还挂在屏幕上」）。所以导出结果直接写成面板正文里的一行，
        /// 只重弹**同一个**面板，整条链上始终只有一个对话框。</para>
        ///
        /// <para><b>对话框是阻塞式</b>的（StandaloneFileBrowser 在 Windows 上是系统公用对话框），
        /// 弹着的时候整个游戏主线程都停在那儿 —— 游戏自己的导入导出也是这个行为，一致即可。</para>
        /// </summary>
        private static void DoExport()
        {
            var path = ReplayPlayer.CurrentPath;
            if (string.IsNullOrEmpty(path))
            {
                _note = "没有可导出的文件。";
                _info = _info + "；没有可导出的文件";
                ShowMain(ReplayPlayer.StopReason);
                return;
            }

            var suggestion = SafeName(BareName(path));
            string dest = null;
            string err = null;
            bool ok = false;

            try
            {
                // 直接调游戏用的那个原生文件对话框（「切换存档 → 导出」走的也是它）。
                //
                // ⚠️ 为什么不走 `FileHelper.SaveExternalFile`：那是个 `async Task<string>`，
                //    在 interop 里变成 `Il2CppSystem.Threading.Tasks.Task<string>`
                //    （**不派生自** System.Threading.Tasks.Task，没有 GetAwaiter，await 不了）。
                //    而 SFB.SaveFilePanel 本身就是**同步返回**路径的，正是我们要的语义。
                dest = SFB.StandaloneFileBrowser.SaveFilePanel("", "", suggestion, "mrep");
            }
            catch (Exception e)
            {
                err = "打开保存对话框失败：" + e.GetType().Name + "：" + e.Message;
            }

            if (string.IsNullOrEmpty(dest))
            {
                // 玩家点了「取消」：把面板弹回来，只多一行说明。
                _note = "已取消导出。";
                _info = _info + "；导出取消";
                ShowMain(ReplayPlayer.StopReason);
                return;
            }

            try { ok = ReplayLibrary.Export(path, dest, out err); }
            catch (Exception e) { ok = false; err = e.GetType().Name + "：" + e.Message; }

            _note = ok ? "已导出到：" + dest : "导出失败：" + (err ?? "未知原因。");
            _info = _info + (ok ? "；导出成功 → " + dest : "；导出失败：" + (err ?? "?"));
            ShowMain(ReplayPlayer.StopReason);
        }

        /// <summary>
        /// 自测用：**不开原生对话框**，直接导出到指定路径，其余流程（结果写进面板正文
        /// → 面板重弹）与玩家点击完全一致。
        ///
        /// <para>为什么需要它：原生「另存为」对话框是系统级的、阻塞式的，自动化测试里
        /// 一弹就再也回不来了（整个测试进程挂死）。所以只把「选路径」那一步换掉。</para>
        /// </summary>
        public static bool SelfTestExport(string destPath)
        {
            if (!_open)
                return false;
            var path = ReplayPlayer.CurrentPath;
            string err;
            var ok = ReplayLibrary.Export(path, destPath, out err);
            _note = ok ? "已导出到：" + destPath : "导出失败：" + (err ?? "未知原因。");
            _info = _info + (ok ? "；导出成功 → " + destPath : "；导出失败：" + (err ?? "?"));
            ShowMain(ReplayPlayer.StopReason);
            return ok;
        }

        /// <summary>把可能带非法字符的名字清成能放进文件对话框的样子。</summary>
        private static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "replay";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        /// <summary>
        /// 去掉 <c>.mrep</c> / <c>.json</c> 这一串尾巴，只留裸名字。
        /// <c>ReplayPlayer.CurrentPath</c> 多半是 <c>x.mrep.json</c>，
        /// 直接 <c>GetFileNameWithoutExtension</c> 会得到 <c>x.mrep</c>，
        /// 再交给「另存为」就变成 <c>x.mrep.mrep</c> 了。
        /// </summary>
        private static string BareName(string path)
        {
            try
            {
                var n = System.IO.Path.GetFileName(path ?? string.Empty);
                if (n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    n = n.Substring(0, n.Length - 5);
                if (n.EndsWith(".mrep", StringComparison.OrdinalIgnoreCase))
                    n = n.Substring(0, n.Length - 5);
                return n;
            }
            catch (Exception)
            {
                return "replay";
            }
        }

        private static void DoReplay()
        {
            var msg = ReplayPlayer.RestartCurrent();
            _info = _info + "；" + msg;
        }

        private static void DoExit()
        {
            ReplayPlayer.EndToReplayPage();
            _info = _info + "；已请求结束播放并回到回放页";
        }

        /// <summary>上一次操作（目前只有导出）的反馈，拼在面板正文的最后一行。</summary>
        private static string _note;

        /// <summary>
        /// 钉住当前这次弹窗的托管委托。
        ///
        /// <para><b>为什么必须钉住：</b><c>CustomDialog</c> 内部把 <c>Action&lt;int&gt;</c> 存进
        /// il2cpp 字段；如果不留一个托管引用，这个委托随时可能被 GC 掉，
        /// 之后点按钮就是「什么都不发生」—— 而且不报任何错。
        /// （<c>ReplayPage.Confirm</c> 里也做了同一件事。）</para>
        /// </summary>
        private static Action<int> _onSelect;

        // ------------------------------------------------------------------
        // 文案
        // ------------------------------------------------------------------

        private static string BuildDesc(string reason)
        {
            var sb = new StringBuilder(160);
            var f = ReplayPlayer.CurrentFile;

            if (f != null)
            {
                sb.Append(ReplayInfo.PrettyStage(f.Stage));
                sb.Append("　").Append(DurationText(f));
                sb.Append('\n');
            }

            sb.Append("结果：").Append(ReasonText(reason));

            var total = ReplayPlayer.TotalEventCount;
            if (total > 0)
                sb.Append("　注入 ").Append(ReplayPlayer.InjectedEventCount).Append('/').Append(total);

            var compared = ReplayPlayer.VerifiedComparisons;
            if (compared > 0)
            {
                var bad = ReplayPlayer.VerifiedMismatches;
                sb.Append("　一致性 ").Append(compared - bad).Append('/').Append(compared)
                  .Append(bad == 0 ? " ✅" : "（不一致 " + bad + "）❌");
            }

            // 上一次操作的反馈（导出结果）直接写在正文里 —— 见 DoExport 的说明：
            // 再弹一个提示框会踩到游戏对话框的排队时序。
            if (!string.IsNullOrEmpty(_note))
                sb.Append('\n').Append(_note);

            return sb.ToString();
        }

        private static string DurationText(ReplayFile f)
        {
            try
            {
                var tps = f.Tps > 0 ? f.Tps : 30;
                var ticks = f.LastTick - f.StartTick;
                var total = ticks > 0 ? (int)Math.Round(ticks / (double)tps) : 0;
                return (total / 60).ToString(CultureInfo.InvariantCulture) + ":"
                    + (total % 60).ToString("00", CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return "0:00";
            }
        }

        private static string ReasonText(string reason)
        {
            switch (reason)
            {
                case "finished": return "播放完毕";
                case "level-cleared": return "关卡通关";
                case "game-over": return "关卡失败";
                case "level-dispose": return "关卡已关闭";
                case "manual-end": return "已结束播放";
                case "tick-limit": return "到达调试 tick 上限";
                case "selftest-stalled": return "自测：播放停滞，提前收尾";
                case "selftest-timeout": return "自测：等待超时";
                case "application-quit": return "游戏退出";
                default: return string.IsNullOrEmpty(reason) ? "结束" : reason;
            }
        }

        private static MainSceneController Scene()
        {
            try
            {
                var main = MainManager.Instance;
                if (main == null)
                    return null;
                var s = main.Scene;
                return s == null ? null : s;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
