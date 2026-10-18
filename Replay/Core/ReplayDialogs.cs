using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MVZ2.UI;

namespace Replay
{
    /// <summary>
    /// 回放里的**选项对话框**（梦魇的「选择命运」、上锁的箱子的「赎金」…）。
    ///
    /// ── 问题 ─────────────────────────────────────────────────────────
    /// <para>这类对话框是**阻塞式**的：调用方先 <c>level.PauseGame(100)</c> 再
    /// <c>level.ShowDialog(title, desc, options, onSelect)</c>，等真人点了才在回调里
    /// <c>ResumeGameDelayed(100)</c>。于是回放里变成「让观众来选」——而且因为逻辑 tick
    /// 被暂停，回放会**永久卡住**等一个点击。</para>
    ///
    /// ── 为什么不能只记「选了第几项」 ───────────────────────────────────
    /// <para>上锁的箱子的选项列表是**按玩家当前金币/星之碎片动态生成**的
    /// （<c>FillRansomOptionList</c>：钱够才有「付钱」、碎片够才有「付碎片」、最后总是「拒绝」）。
    /// 金币是**全局存档**里的钱、不在关卡关键帧里，所以「录的时候有钱、放的时候没钱」
    /// 会让选项个数都不一样 —— 只记下标会选错项，甚至越界。</para>
    ///
    /// <para>三道防线：① 录制时把**起始金币**写进 header（<c>mn</c>），回放时用它给
    /// 金币影子播种，让选项列表的生成条件与录制时一致（见 <see cref="ReplaySaveGuard.SeedMoney"/>）；
    /// ② 事件里记的是**所选那一项的文本**，回放时按文本在实时列表里定位；
    /// ③ 文本在实时列表里找不到时**宁可不点**（返回 −1，交给玩家），绝不按下标硬猜 ——
    /// 上锁的箱子里「拒绝」和「付光所有钱」的后果差得很远。</para>
    ///
    /// ── 回放时怎么「点」 ─────────────────────────────────────────────
    /// <para>不直接调回调（那个 <c>Action&lt;int&gt;</c> 是原生委托，从托管侧调用不可靠），
    /// 而是反射调 <c>CustomDialog.OnButtonRowItemClickCallback(row, column)</c> ——
    /// 这就是真按钮被点时走的那一个方法，后面的链路（<c>OnOptionSelect</c> → 展开
    /// <c>ShowDialogTask</c> 的 lambda → <c>SetInteractable</c> / 关框 / 调用方回调）
    /// 全部照原样发生。</para>
    ///
    /// <para><b>⚠️ 不能读 <c>CustomDialog</c> 的私有字段。</b>Il2CppInterop **不会**为
    /// 私有字段生成托管字段（实测 <c>countPerRow</c> / <c>buttonRowList</c> 全都
    /// <c>AccessTools.Field</c> 找不到）。所以这里改用两条**公开**通道：</para>
    /// <list type="bullet">
    /// <item><c>CustomDialog.SetDialog(title, desc, options, …)</c> —— 给出完整的选项列表；</item>
    /// <item><c>ButtonRow.UpdateButtons(string[] texts)</c> —— 给出「每一行里各按钮的文字」，
    /// 于是「选项文本 → (行, 列)」可以反查出来，不需要知道每行几项。</item>
    /// </list>
    /// </summary>
    internal static class ReplayDialogs
    {
        /// <summary>选项文本在文件里的分隔符（U+0001，正常文本里不可能出现）。</summary>
        private const char Separator = '\u0001';

        /// <summary>回放时判定「这个框就是回放里那一个」的 tick 容差。</summary>
        private const int TickTolerance = 2;

        /// <summary>一行按钮的文字（按行对象索引）。</summary>
        private sealed class RowTexts
        {
            public ButtonRow Row;
            public string[] Texts;
        }

        #region 录制侧
        private static readonly List<ReplayDialogRecord> records = new List<ReplayDialogRecord>();
        private static ReplayDialogRecord pendingRecord;
        private static readonly Dictionary<IntPtr, RowTexts> rowTexts = new Dictionary<IntPtr, RowTexts>();
        private static string[] openOptions = Array.Empty<string>();

        internal static int RecordedCount => records.Count;

        /// <summary>对话框刚被 <c>CustomDialog.SetDialog</c> 创建（录制侧）。</summary>
        internal static void RecordOpen(int tick, string title, string desc, string[] options)
        {
            var rec = new ReplayDialogRecord
            {
                Tick = tick,
                Title = title,
                Desc = desc,
                Options = options != null ? (string[])options.Clone() : Array.Empty<string>(),
                Chosen = -1,
            };
            records.Add(rec);
            pendingRecord = rec;
            openOptions = rec.Options;
            rowTexts.Clear();
        }

        /// <summary><c>ButtonRow.UpdateButtons</c> 给出的「这一行各按钮的文字」。</summary>
        internal static void RecordRowTexts(ButtonRow row, string[] texts)
        {
            if (row == null || texts == null)
                return;
            // 只在「有正在等待的对话框」时记：否则菜单里的其它 ButtonRow 会把表撑满，
            // 回放时按文本反查就可能点到别的框的行上。
            if (pendingRecord == null && !HasPendingAnswer)
                return;
            try
            {
                rowTexts[row.Pointer] = new RowTexts { Row = row, Texts = (string[])texts.Clone() };
            }
            catch (Exception) { }
        }

        /// <summary>玩家点了第 <paramref name="index"/> 项（**行内**下标）。</summary>
        internal static void RecordChoice(ButtonRow row, int index)
        {
            var rec = pendingRecord;
            if (rec == null)
                return;
            pendingRecord = null;

            string text = TextAt(row, index);
            if (text == null)
            {
                Plugin.Log?.LogWarning("[Replay] 对话框：拿不到被点那一项的文字（行内下标 " + index
                    + "），这条记录作废（回放里会停下来等玩家点）");
                return;
            }
            rec.ChosenName = text;
            // Chosen 只是给人看的（小结/日志）；判定一律以文本为准。
            for (int i = 0; i < rec.Options.Length; i++)
            {
                if (string.Equals(rec.Options[i], text, StringComparison.Ordinal))
                {
                    rec.Chosen = i;
                    break;
                }
            }
        }

        private static string TextAt(ButtonRow row, int index)
        {
            if (row == null || index < 0)
                return null;
            try
            {
                if (rowTexts.TryGetValue(row.Pointer, out var rt) && rt.Texts != null
                    && index < rt.Texts.Length)
                    return rt.Texts[index];
            }
            catch (Exception) { }
            return null;
        }

        internal static void ResetRecording()
        {
            records.Clear();
            pendingRecord = null;
            rowTexts.Clear();
            openOptions = Array.Empty<string>();
        }

        internal static void AppendRecordLines(List<string> target)
        {
            for (int i = 0; i < records.Count; i++)
            {
                var r = records[i];
                var sb = new StringBuilder(192);
                sb.Append("{\"t\":").Append(Json.Num(r.Tick));
                sb.Append(",\"k\":\"dlg\"");
                sb.Append(",\"title\":").Append(Json.Str(r.Title ?? ""));
                sb.Append(",\"desc\":").Append(Json.Str(r.Desc ?? ""));
                sb.Append(",\"op\":").Append(Json.Str(Join(r.Options)));
                sb.Append(",\"sel\":").Append(Json.Num(r.Chosen));
                if (!string.IsNullOrEmpty(r.ChosenName))
                    sb.Append(",\"selName\":").Append(Json.Str(r.ChosenName));
                sb.Append('}');
                target.Add(sb.ToString());
            }
        }

        internal static string Join(string[] options)
        {
            if (options == null || options.Length == 0)
                return "";
            var sb = new StringBuilder(64);
            for (int i = 0; i < options.Length; i++)
            {
                if (i > 0)
                    sb.Append(Separator);
                sb.Append(options[i]);
            }
            return sb.ToString();
        }

        internal static string[] Split(string text)
        {
            if (string.IsNullOrEmpty(text))
                return Array.Empty<string>();
            return text.Split(Separator);
        }
        #endregion

        #region 回放侧
        private static ReplayFile file;
        private static int cursor;
        private static bool armed;
        private static CustomDialog pendingDialog;
        private static ReplayDialogRecord pendingMatch;
        private static string[] pendingOptions = Array.Empty<string>();
        private static DateTime pendingSince;

        private static int autoAnswered;
        private static int mismatched;
        private static int unavailable;
        private static int skippedNoRecord;

        private static bool HasPendingAnswer => pendingDialog != null;

        /// <summary>回放开始：吃进文件里的对话框记录。</summary>
        internal static void BeginPlayback(ReplayFile source)
        {
            file = source;
            cursor = 0;
            armed = source != null && source.Dialogs.Count > 0;
            pendingDialog = null;
            pendingMatch = null;
            pendingOptions = Array.Empty<string>();
            autoAnswered = 0;
            mismatched = 0;
            unavailable = 0;
            skippedNoRecord = 0;
            pendingSince = default;
            rowTexts.Clear();
        }

        internal static void EndPlayback()
        {
            armed = false;
            pendingDialog = null;
            pendingMatch = null;
            rowTexts.Clear();
        }

        /// <summary>配置：对话框在回放里显示多久（秒）。</summary>
        private static float HoldSeconds
        {
            get
            {
                try
                {
                    if (ReplayConfig.DialogHoldSeconds != null)
                        return ReplayConfig.DialogHoldSeconds.Value;
                }
                catch (Exception) { }
                return 2f;
            }
        }

        private static bool AutoAnswerWanted
        {
            get
            {
                try
                {
                    return ReplayConfig.DialogAutoAnswer == null || ReplayConfig.DialogAutoAnswer.Value;
                }
                catch (Exception) { return true; }
            }
        }

        /// <summary><c>CustomDialog.SetDialog</c> 前缀里调。</summary>
        internal static void OnDialogOpened(CustomDialog dialog, string title, string desc, string[] options)
        {
            rowTexts.Clear();
            openOptions = options ?? Array.Empty<string>();

            if (!armed || !AutoAnswerWanted)
                return; // 没在播、或这条回放里没有对话框记录 → 交给玩家（宁可让观众选，也别乱选）

            // 逻辑 tick 在对话框打开的那一刻冻结，所以录制的 tick 与当前 tick 必然相等。
            // 用容差判定，避免因为「事件打戳位置差一格」而拒绝回答。
            int now = ReplayPlayer.CurrentTick;
            var rec = Peek();
            if (rec == null)
            {
                skippedNoRecord++;
                return;
            }
            if (Math.Abs(rec.Tick - now) > TickTolerance)
            {
                // 不是我们等的那一个（例如玩家在回放中途自己开了个确认框）→ 不抢答。
                Plugin.Log?.LogInfo("[Replay] 对话框 tick 不匹配（录制 " + rec.Tick + " / 现在 " + now
                    + "），交给玩家：'" + Short(title) + "'");
                return;
            }
            cursor++;
            if (string.IsNullOrEmpty(rec.ChosenName))
            {
                unavailable++;
                Plugin.Log?.LogWarning("[Replay] 回放里这个对话框录制时**没有选中项**（录制中途结束？），"
                    + "无法自动应答：'" + Short(title) + "' 选项=[" + Join(openOptions) + "]");
                return;
            }
            pendingDialog = dialog;
            pendingMatch = rec;
            pendingOptions = openOptions;
            pendingSince = DateTime.UtcNow;
            Plugin.Log?.LogInfo("[Replay] 对话框（回放）：'" + Short(title) + "' 选项=["
                + Join(openOptions) + "] 录制选择=" + Short(rec.ChosenName)
                + "（第 " + rec.Chosen + " 项），" + HoldSeconds.ToString("0.#") + " 秒后自动选择");
        }

        /// <summary>每帧（<c>MainSceneController.Update</c> 之后）驱动自动应答的计时。</summary>
        internal static void OnFrame()
        {
            if (pendingDialog == null)
                return;
            try
            {
                double elapsed = (DateTime.UtcNow - pendingSince).TotalSeconds;
                if (elapsed < HoldSeconds)
                    return;

                var rec = pendingMatch;
                var dialog = pendingDialog;
                var options = pendingOptions;
                pendingDialog = null;
                pendingMatch = null;
                if (dialog == null || rec == null)
                    return;

                // 先把「能不能回答」判清楚：判不出来就交给玩家，绝不下标硬猜。
                int index = ResolveIndex(rec.Options, rec.ChosenName, options, true);
                if (index < 0)
                {
                    unavailable++;
                    return;
                }
                if (!SameList(rec.Options, options))
                    mismatched++;
                if (!ClickByIndex(dialog, index, rec.ChosenName))
                {
                    Plugin.Log?.LogError("[Replay] 自动点击对话框选项失败（'" + Short(rec.ChosenName)
                        + "'）。这一局会停在这里等玩家点。");
                    unavailable++;
                    return;
                }
                autoAnswered++;
            }
            catch (Exception e)
            {
                pendingDialog = null;
                pendingMatch = null;
                Plugin.Log?.LogError("[Replay] 自动应答对话框出错：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static ReplayDialogRecord Peek()
        {
            if (file == null || cursor >= file.Dialogs.Count)
                return null;
            return file.Dialogs[cursor];
        }

        /// <summary>
        /// 决定「点第几项」——一律**按文本**在实时列表里找。
        ///
        /// <para>这是纯逻辑版（不碰 UI），也是自测直接验证的那一个。</para>
        /// <para>返回 −1 = 「无法回答」：录制选的那一项在实时列表里根本没有
        /// （典型场景：玩家看回放时钱不够，上锁的箱子没生成「付钱」那一项）。
        /// 这时宁可把球交回玩家手里，也不要按下标硬猜。</para>
        /// </summary>
        /// <param name="log">是否把差异打进日志（自测里关掉，免得刷屏）。</param>
        internal static int ResolveIndex(string[] recOptions, string chosenName, string[] live, bool log)
        {
            recOptions = recOptions ?? Array.Empty<string>();
            live = live ?? Array.Empty<string>();

            if (string.IsNullOrEmpty(chosenName))
            {
                if (log)
                    Plugin.Log?.LogError("[Replay] 对话框记录里没有「选了哪一项」的文字，无法自动应答："
                        + "\n  录制=[" + Join(recOptions) + "]"
                        + "\n  现在=[" + Join(live) + "]");
                return -1;
            }

            int index = IndexOf(live, chosenName);
            if (index < 0)
            {
                if (log)
                    Plugin.Log?.LogError("[Replay] 对话框选项列表与录制时**不兼容**：录制选的 '"
                        + Short(chosenName) + "' 在现在的列表里不存在，不猜下标（交给玩家）。"
                        + "\n  录制=[" + Join(recOptions) + "]"
                        + "\n  现在=[" + Join(live) + "]");
                return -1;
            }

            if (log && !SameList(recOptions, live))
                Plugin.Log?.LogWarning("[Replay] 对话框选项列表与录制时不同，已按**文本**找回第 " + index
                    + " 项：'" + Short(chosenName) + "'"
                    + "\n  录制=[" + Join(recOptions) + "]"
                    + "\n  现在=[" + Join(live) + "]");
            return index;
        }

        private static int IndexOf(string[] list, string text)
        {
            if (list == null || string.IsNullOrEmpty(text))
                return -1;
            for (int i = 0; i < list.Length; i++)
            {
                if (string.Equals(list[i], text, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        private static bool SameList(string[] a, string[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
        #endregion

        #region 点击
        private static bool reflectReady;
        private static bool reflectBroken;
        private static MethodInfo mClick;

        private static void EnsureReflect()
        {
            if (reflectReady || reflectBroken)
                return;
            reflectReady = true;
            try
            {
                mClick = AccessTools.Method(typeof(CustomDialog), "OnButtonRowItemClickCallback",
                    new[] { typeof(ButtonRow), typeof(int) });
                if (mClick == null)
                {
                    reflectBroken = true;
                    Plugin.Log?.LogError("[Replay] 找不到 CustomDialog.OnButtonRowItemClickCallback(ButtonRow,int)，"
                        + "回放里无法自动应答对话框");
                }
            }
            catch (Exception e)
            {
                reflectBroken = true;
                Plugin.Log?.LogError("[Replay] 对话框反射初始化失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 按**选项文字**找到它所在的（行, 列）并反射调用点击回调 ——
        /// 等价于真人点了那个按钮。
        ///
        /// <para>行/列是靠 <c>ButtonRow.UpdateButtons</c> 记下来的每行文字反查的，
        /// 所以**不需要**知道 <c>countPerRow</c>（那是私有字段，interop 拿不到）。</para>
        /// </summary>
        private static bool ClickByIndex(CustomDialog self, int realIndex, string text)
        {
            EnsureReflect();
            if (reflectBroken)
                return false;
            try
            {
                foreach (var kv in rowTexts)
                {
                    var rt = kv.Value;
                    if (rt == null || rt.Texts == null || rt.Row == null)
                        continue;
                    for (int col = 0; col < rt.Texts.Length; col++)
                    {
                        if (!string.Equals(rt.Texts[col], text, StringComparison.Ordinal))
                            continue;
                        mClick.Invoke(self, new object[] { rt.Row, col });
                        return true;
                    }
                }
                Plugin.Log?.LogError("[Replay] 对话框里找不到文字为 '" + Short(text)
                    + "' 的按钮（已记录行数 " + rowTexts.Count + "，目标下标 " + realIndex + "）");
                return false;
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                Plugin.Log?.LogError("[Replay] 反射点击对话框失败：" + inner.GetType().Name + ": " + inner.Message);
                return false;
            }
        }
        #endregion

        #region 报告
        /// <summary>自动应答所需的反射状态（写进报告：静默失败是最难查的一类）。</summary>
        internal static string ClickPathStatus
        {
            get
            {
                EnsureReflect();
                if (reflectBroken)
                    return "❌ 不可用（见日志）";
                return "OnButtonRowItemClickCallback=有（行列由 ButtonRow.UpdateButtons 的文字反查）";
            }
        }

        internal static void EnsureReflectForTest()
        {
            EnsureReflect();
        }

        /// <summary>
        /// 自测：验证「选项列表与录制时不同」时的定位 ——
        /// 这正是上锁的箱子会踩到的场景（选项列表按玩家当前金币/碎片动态生成，
        /// 而金币来自全局存档、不在关卡关键帧里）。
        /// </summary>
        internal static bool SelfCheck(out string detail)
        {
            var sb = new StringBuilder(320);
            bool ok = true;
            string[] payReject = { "支付 100 金币", "拒绝" };

            // ① 列表一致 → 正常找到
            ok &= Expect(sb, "列表一致", ResolveIndex(payReject, "拒绝", payReject, false), 1);

            // ② 玩家看回放时钱不够 → 实时列表少了「付钱」那一项；按文本仍要能找回
            ok &= Expect(sb, "少了「付钱」项时按文本找回",
                ResolveIndex(payReject, "拒绝", new[] { "拒绝" }, false), 0);

            // ③ 录制选的正是「付钱」，但实时没有这一项 → **必须拒绝回答**（不能猜成「拒绝」）
            ok &= Expect(sb, "录制选的项实时不存在 → 不猜",
                ResolveIndex(payReject, "支付 100 金币", new[] { "拒绝" }, false), -1);

            // ④ 顺序变了也要能找回
            ok &= Expect(sb, "顺序不同时按文本找回",
                ResolveIndex(payReject, "支付 100 金币", new[] { "拒绝", "支付 100 金币" }, false), 1);

            // ⑤ 记录里没有选中项文字 → 无法回答（而不是乱选一个）
            ok &= Expect(sb, "没有选中项文字 → 不猜",
                ResolveIndex(payReject, null, payReject, false), -1);

            // ⑥ 行文字 → 列号反查（取代「读私有字段 countPerRow」的那套）
            ok &= SelfCheckRowLookup(sb);

            detail = sb.ToString().TrimEnd('；', ' ') + "；反射：" + ClickPathStatus;
            return ok;
        }

        /// <summary>用两条假行验证「按文本反查列号」这套映射。</summary>
        private static bool SelfCheckRowLookup(StringBuilder sb)
        {
            var saved = new Dictionary<IntPtr, RowTexts>(rowTexts);
            rowTexts.Clear();
            bool ok = true;
            try
            {
                // 键用假指针、Row 留 null —— 这一步只验「找到第几列」的算法。
                rowTexts[(IntPtr)1] = new RowTexts { Row = null, Texts = new[] { "A", "B" } };
                rowTexts[(IntPtr)2] = new RowTexts { Row = null, Texts = new[] { "C", "D" } };

                ok &= Expect(sb, "第 1 行第 2 列", FindColumn("B"), 1);
                ok &= Expect(sb, "第 2 行第 1 列", FindColumn("C"), 0);
                ok &= Expect(sb, "找不到的文字", FindColumn("Z"), -1);
            }
            finally
            {
                rowTexts.Clear();
                foreach (var kv in saved)
                    rowTexts[kv.Key] = kv.Value;
            }
            return ok;
        }

        /// <summary>自测辅助：反查某段文字在第几列（返回 −1 = 没有）。</summary>
        private static int FindColumn(string text)
        {
            foreach (var kv in rowTexts)
            {
                var texts = kv.Value.Texts;
                for (int col = 0; col < texts.Length; col++)
                {
                    if (string.Equals(texts[col], text, StringComparison.Ordinal))
                        return col;
                }
            }
            return -1;
        }

        private static bool Expect(StringBuilder sb, string what, int actual, int want)
        {
            bool ok = actual == want;
            sb.Append(ok ? "OK " : "✗ ").Append(what);
            if (!ok)
                sb.Append("（得到 ").Append(actual).Append("，应为 ").Append(want).Append('）');
            sb.Append("；");
            return ok;
        }

        internal static string Describe()
        {
            if (autoAnswered == 0 && mismatched == 0 && unavailable == 0 && skippedNoRecord == 0)
                return "这条回放里没有选项对话框";
            var sb = new StringBuilder(128);
            sb.Append("自动应答 ").Append(autoAnswered).Append(" 个");
            if (mismatched > 0)
                sb.Append("，选项列表与录制时不同 ").Append(mismatched).Append(" 个（已按文本找回）");
            if (unavailable > 0)
                sb.Append("，无法应答 ").Append(unavailable).Append(" 个（交给玩家）");
            if (skippedNoRecord > 0)
                sb.Append("，录制的对话框已用尽后又冒出 ").Append(skippedNoRecord).Append(" 个（交给玩家）");
            return sb.ToString();
        }

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            s = s.Replace('\n', ' ');
            return s.Length <= 40 ? s : s.Substring(0, 40) + "…";
        }
        #endregion
    }
}
