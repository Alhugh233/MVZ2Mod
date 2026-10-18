using System;
using System.Globalization;
using System.Text;
using DSHCore.Ui;
using MVZ2.Managers;
using MVZ2.Scenes;

// DSHCore 的 UiLog 是 internal，跨程序集用不了 —— 别名到本 mod 自己的那份。
using UiLog = Replay.ReplayLog;

namespace Replay
{
    /// <summary>
    /// 回放结束时的面板：<b>重新播放 / 退出</b>。
    ///
    /// <para><b>为什么没有「导出 .mrep」：</b>那个功能已按需求移到回放页的【更多】面板里
    /// （在关卡里导出既看不到其它回放、也改不了名字）。导出的实现本身在
    /// <see cref="ReplayExporter"/>，两个界面共用。</para>
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
        private const string ReplayButton = "重新播放";
        private const string ExitButton = "退出";

        /// <summary>面板上的按钮（顺序就是点下去返回的 index）。</summary>
        private static readonly string[] MainOptions = { ReplayButton, ExitButton };

        /// <summary>当前面板上的按钮文案（自测用，断言「导出」真的不在里面了）。</summary>
        public static string[] Options { get { return MainOptions; } }

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

            var options = MainOptions;
            var onSelect = new Action<int>(index =>
            {
                // 选中项与 index 的对应就是按下标取 MainOptions（下表只在日志/报告里用）。
                var picked = index >= 0 && index < options.Length ? options[index] : ("#" + index);
                _info = "选了「" + picked + "」";
                // 注意：**不在这里把 _open 置 false** ——
                // 真正结束（重播 / 退出）由各自的处理函数自己调 Close()。
                try
                {
                    switch (index)
                    {
                        case 0: DoReplay(); break;
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

            // 走游戏原生的 ShowDialog：标题与按钮文案得自己取词（它拿到什么就画什么）。
            scene.ShowDialog(UiLocale.Get(Title), BuildDesc(reason), UiLocale.GetAll(options), onSelect);
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

            sb.Append(UiLocale.Get("结果：")).Append(ReasonText(reason));

            var total = ReplayPlayer.TotalEventCount;
            if (total > 0)
                sb.Append(UiLocale.Get("　注入 ")).Append(ReplayPlayer.InjectedEventCount).Append('/').Append(total);

            var compared = ReplayPlayer.VerifiedComparisons;
            if (compared > 0)
            {
                var bad = ReplayPlayer.VerifiedMismatches;
                sb.Append(UiLocale.Get("　一致性 ")).Append(compared - bad).Append('/').Append(compared)
                  .Append(bad == 0 ? " ✅" : UiLocale.Format("（不一致 {0}）❌", bad));
            }

            // ⚠ 收尾时**不再**有任何「上一次操作的反馈」要写进正文 ——
            // 导出已经搬走了，这里只剩两个立刻生效的按钮。
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

        /// <summary>
        /// 结束原因的人读文字（画在结束面板正文里，所以要取词）。
        ///
        /// <para>⚠ <c>case</c> 那些取值是 <see cref="ReplayPlayer.StopReason"/> 传出来的**协议串**，
        /// 绝不能翻；只有 <c>return</c> 的中文才是显示文字。</para>
        /// </summary>
        private static string ReasonText(string reason)
        {
            switch (reason)
            {
                case "finished": return UiLocale.Get("播放完毕");
                case "level-cleared": return UiLocale.Get("关卡通关");
                case "game-over": return UiLocale.Get("关卡失败");
                case "level-dispose": return UiLocale.Get("关卡已关闭");
                case "manual-end": return UiLocale.Get("已结束播放");
                case "tick-limit": return UiLocale.Get("到达调试 tick 上限");
                case "selftest-stalled": return UiLocale.Get("自测：播放停滞，提前收尾");
                case "selftest-timeout": return UiLocale.Get("自测：等待超时");
                case "application-quit": return UiLocale.Get("游戏退出");
                default: return string.IsNullOrEmpty(reason) ? UiLocale.Get("结束") : reason;
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
