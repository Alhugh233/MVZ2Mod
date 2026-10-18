using System;
using System.Collections.Generic;
using System.Text;
using MVZ2Logic.Inputs;
using PVZEngine.Level;
using UnityEngine;

namespace Replay
{
    /// <summary>
    /// 逐 tick 的**指针轨迹**：录制时采样，回放时重放。
    ///
    /// ── 为什么需要它 ─────────────────────────────────────────────────
    /// <para>回放的原有模型是「离散事件」：只有发生了动作（放下器械 / 点卡 / 工具）才记一条。
    /// 但有一类关卡行为**不看事件参数**，而是每帧直接问「鼠标现在在哪、哪个键按着」：</para>
    /// <list type="bullet">
    /// <item><c>BreakoutBoard</c>（打砖块挡板）—— 鼠标模式下每个 tick 把挡板**绝对**挪到指针处；</item>
    /// <item><c>Snipenser</c>（重装兵器）—— 用指针算朝向，且要 <c>IsPointerHolding(MOUSE,0)</c> 才开火；</item>
    /// <item><c>MinecartRideable</c>（矿车）—— 目标点每 tick 由指针决定；</item>
    /// <item><c>CrushingWalls</c>（碾压墙）—— 挂在 <c>POST_POINTER_ACTION</c> 上，每次按下扣一次进度。</item>
    /// </list>
    ///
    /// ── 时序：为什么每个 tick 该用**它自己那条**采样（这里有血泪） ──────
    /// <para>Unity 的 <c>Input</c> 在**一帧内是常量**。游戏主循环是
    /// 「<c>GameUpdater.Update()</c> 先跑 N 个 tick，再 <c>UpdateFrame</c>（输入在其中）」，
    /// 而 <c>InputManager.UpdatePointer()</c> 是 MonoBehaviour 自己的 <c>Update</c>。</para>
    ///
    /// <code>
    /// 帧 F：Input 定成 I(F)
    ///   GameUpdater.Update()      → 跑 tick T（（tick 内读 Input 看到的就是 I(F)）
    ///   我们的采样点（tick 后）    → sample@T = I(F)
    ///   InputManager.UpdatePointer→ 发 POST_POINTER_ACTION，带 I(F)
    /// </code>
    ///
    /// <para>⇒ **tick T 内部读到的输入 == sample@T**。
    /// 所以 tick T 用的必须是「第 T 条采样」，而不是「第 T−1 条」。</para>
    ///
    /// <para>⚠️ 这一点搞错后果严重且很隐蔽：<c>Snipenser</c> 的「按住才开火」会晚整整一个 tick，
    /// 于是回放里**多发一颗子弹**（实测：tick 300 时录制 <c>arrow=3</c>、回放 <c>arrow=4</c>），
    /// 之后实体 RNG 整条流错位、指纹全线对不上。</para>
    ///
    /// <para>而 <c>POST_POINTER_ACTION</c> 那一路（打砖块挡板 / 碾压墙）用的是
    /// 「tick 结束后」的位置，恰好也是 sample@T —— 两边同一条，不会自相矛盾。</para>
    ///
    /// ── 覆盖不到的地方**绝不编数据** ────────────────────────────────
    /// <para>轨迹是按需录制的（见 <see cref="ReplayCompat"/>），所以**轨迹开始之前的那一段没有数据**。
    /// 那时必须**完全不接管**指针（<see cref="TickCovered"/> 为 false），让游戏照旧读实时输入 ——
    /// 也就是退回改造之前的行为。曾经踩过的坑：用「整条文件里有没有轨迹」来判断，
    /// 于是「前半段没有轨迹」的回放（例如 2-11，碾压墙要等 Boss 出场才生成）
    /// 会在前半段**凭空造出一整条录制时不存在的指针动作流**。</para>
    ///
    /// ── 按需录制 ─────────────────────────────────────────────────────
    /// <para>只有 <see cref="ReplayCompat.LevelNeedsPointer"/> 为真的关卡才录，否则每局都会白记几千条
    /// 没人读的采样。判定一旦为真就**本关一直有效**（实体中途消失再出现不会留下空洞）。</para>
    /// </summary>
    internal static class ReplayPointerTrajectory
    {
        /// <summary>鼠标左 / 右 / 中键的位掩码。</summary>
        public const int BitLeft = 1;
        public const int BitRight = 2;
        public const int BitMiddle = 4;

        /// <summary>位置变化多少像素以上才算「变了」（小于它的抖动不写盘，省体积）。</summary>
        private const float PositionEpsilon = 0.5f;

        /// <summary>每隔多少个 tick 复查一次「这一关还需不需要录」（15 tick = 0.5 秒）。</summary>
        private const int CheckIntervalTicks = 15;

        internal struct Sample
        {
            /// <summary>采样时的逻辑 tick（= 这个 tick **结束时**的状态）。</summary>
            public int Tick;
            /// <summary>屏幕坐标（录制坐标系，左下角原点，与 <c>Input.mousePosition</c> 同制）。</summary>
            public float X;
            public float Y;
            /// <summary><see cref="BitLeft"/> / <see cref="BitRight"/> / <see cref="BitMiddle"/> 的或。</summary>
            public int Mask;

            public override string ToString()
            {
                return "t=" + Tick + " (" + X.ToString("0.#") + "," + Y.ToString("0.#") + ") mb=" + Mask;
            }
        }

        #region 录制侧状态
        private static readonly List<string> recordLines = new List<string>();
        /// <summary>是否已经开始录（判定为真之后就一直为真）。</summary>
        private static bool latched;
        private static int nextCheckTick = int.MinValue;
        private static bool haveLast;
        private static float lastX, lastY;
        private static int lastMask;
        private static int sampleCount;
        #endregion

        #region 回放侧状态
        private static Sample[] samples = Array.Empty<Sample>();
        private static int cursor;
        /// <summary>游标当前代表的那条采样是否已经有效（false = 还没到第一条）。</summary>
        private static bool haveCurrent;
        private static Sample current;
        /// <summary>本 tick 结束（= 本 tick 内部可见）的掩码，与上一 tick 结束的掩码。</summary>
        private static int curMask;
        private static int prevMask;
        /// <summary>本 tick 有没有轨迹数据覆盖（false ⇒ 调用方必须退回「按事件」的老行为）。</summary>
        private static bool covered;
        #endregion

        #region 对外状态
        /// <summary>这一关是否已经在录轨迹。</summary>
        internal static bool Sampling => latched;

        /// <summary>录制到的采样条数。</summary>
        internal static int SampleCount => sampleCount;

        /// <summary>
        /// 回放：**本 tick** 是否由轨迹覆盖。
        ///
        /// <para>⚠️ 接管指针必须一律以这个为准，**不要**用「文件里有没有轨迹」：
        /// 一个文件的轨迹可能只覆盖后半段（按需录制的判定在实体出现时才成立）。</para>
        /// </summary>
        internal static bool TickCovered => covered;

        /// <summary>回放：当前指针（**录制坐标系**，取值方自己换算窗口尺寸）。</summary>
        internal static Vector2 CurrentScreen => new Vector2(current.X, current.Y);

        /// <summary>回放：本 tick 内部的按键掩码（= sample@T）。</summary>
        internal static int CurrentMask => curMask;

        /// <summary>回放：上一 tick 结束时的按键掩码（= sample@(T−1)），用来算按下/抬起的边沿。</summary>
        internal static int PreviousMask => prevMask;
        #endregion

        #region 生命周期
        internal static void ResetRecording()
        {
            recordLines.Clear();
            latched = false;
            nextCheckTick = int.MinValue;
            haveLast = false;
            lastMask = 0;
            sampleCount = 0;
        }

        internal static void ResetPlayback()
        {
            samples = Array.Empty<Sample>();
            cursor = 0;
            haveCurrent = false;
            current = default;
            curMask = 0;
            prevMask = 0;
            covered = false;
        }

        /// <summary>把录制到的采样写进回放文件（在事件/指纹之后追加）。</summary>
        internal static void AppendRecordLines(List<string> target)
        {
            for (int i = 0; i < recordLines.Count; i++)
                target.Add(recordLines[i]);
        }
        #endregion

        #region 录制
        /// <summary>是否允许逐 tick 采样（配置开关）。</summary>
        private static bool RecordingWanted
        {
            get
            {
                try
                {
                    return ReplayConfig.PointerTrajectory == null || ReplayConfig.PointerTrajectory.Value;
                }
                catch (Exception) { return true; }
            }
        }

        /// <summary>
        /// 每个逻辑 tick 末尾（<c>LevelEngine.Update</c> 之后）调用。
        /// 只有判定为「这一关需要指针」时才真的采样。
        /// </summary>
        internal static void RecordTick(LevelEngine level, int tick)
        {
            if (!RecordingWanted || level == null)
                return;
            try
            {
                if (!latched)
                {
                    if (!ReplayCompat.AnyConsumers)
                        return;
                    if (tick < nextCheckTick)
                        return;
                    nextCheckTick = tick + CheckIntervalTicks;
                    if (!ReplayCompat.LevelNeedsPointer(level))
                        return;
                    latched = true;
                    Plugin.Log?.LogInfo("[Replay] 这一关有会读实时指针的内容（"
                        + ReplayCompat.DescribeFirstMatch(level) + "），从 tick " + tick
                        + " 起逐 tick 记录指针轨迹");
                }

                float x, y;
                try
                {
                    var p = InputHelper.GetPointerPosition(PointerTypes.MOUSE, MouseButtons.LEFT);
                    x = p.x;
                    y = p.y;
                }
                catch (Exception)
                {
                    return; // 拿不到指针就这一 tick 不采
                }

                int mask = 0;
                if (ReadButton(MouseButtons.LEFT)) mask |= BitLeft;
                if (ReadButton(MouseButtons.RIGHT)) mask |= BitRight;
                if (ReadButton(MouseButtons.MIDDLE)) mask |= BitMiddle;

                if (haveLast && mask == lastMask
                    && Math.Abs(x - lastX) < PositionEpsilon
                    && Math.Abs(y - lastY) < PositionEpsilon)
                    return; // 没变就不写

                haveLast = true;
                lastX = x;
                lastY = y;
                lastMask = mask;

                var sb = new StringBuilder(72);
                sb.Append("{\"t\":").Append(Json.Num(tick));
                sb.Append(",\"k\":\"ptr\"");
                sb.Append(",\"sx\":").Append(Json.Num(x));
                sb.Append(",\"sy\":").Append(Json.Num(y));
                sb.Append(",\"mb\":").Append(Json.Num(mask));
                sb.Append('}');
                recordLines.Add(sb.ToString());
                sampleCount++;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 指针轨迹采样出错（本关后续不再重试）："
                    + e.GetType().Name + ": " + e.Message);
                latched = false;
                nextCheckTick = tick + 100000;
            }
        }

        private static bool ReadButton(int button)
        {
            try { return InputHelper.IsPointerHolding(PointerTypes.MOUSE, button); }
            catch (Exception) { return false; }
        }
        #endregion

        #region 回放
        /// <summary>回放开始：吃进文件里的采样（已按 tick 升序）。</summary>
        internal static void LoadPlayback(List<Sample> loaded)
        {
            ResetPlayback();
            if (loaded == null || loaded.Count == 0)
                return;
            loaded.Sort(CompareByTick);
            samples = loaded.ToArray();
        }

        private static int CompareByTick(Sample a, Sample b)
        {
            return a.Tick.CompareTo(b.Tick);
        }

        /// <summary>把游标推进到「tick ≤ <paramref name="tickInclusive"/> 的最后一条采样」。单调，幂等。</summary>
        private static void SeekThrough(int tickInclusive)
        {
            while (cursor < samples.Length && samples[cursor].Tick <= tickInclusive)
            {
                current = samples[cursor];
                haveCurrent = true;
                cursor++;
            }
        }

        /// <summary>
        /// tick <paramref name="tick"/> **开始**时调（<c>LevelEngine.Update</c> 前缀）。
        ///
        /// <para>把游标推进到「tick <paramref name="tick"/> 结束」的那条采样 —— 也就是这一 tick
        /// 内部该看到的输入（理由见类注释的时序推导），并把它报出来。
        /// 返回 false = 这一 tick **没有**轨迹数据（调用方必须退回「按事件」的老行为）。</para>
        ///
        /// <para>⚠️ 调用方必须拿着 <paramref name="screen"/> 在 **tick 逻辑之前**派发
        /// <c>POST_POINTER_ACTION</c>：打砖块挡板是**绝对赋值**，游戏里它在逻辑 tick 之前就被
        /// 摆到 I(F) 了；如果在 tick 之后才摆，挡板就慢一帧，珠子会在另一个 tick 落空
        /// （实测：录制 `breakout_pearl=2`，回放只剩 1）。</para>
        /// </summary>
        internal static bool BeginTick(int tick, out Vector2 screen, out int prev, out int cur)
        {
            screen = Vector2.zero;
            prev = cur = 0;
            covered = false;
            if (samples.Length == 0)
                return false;

            int beforeMask = current.Mask;
            bool hadBefore = haveCurrent;
            SeekThrough(tick);
            if (!haveCurrent)
                return false;   // 还没到第一条采样

            screen = new Vector2(current.X, current.Y);
            cur = current.Mask;
            // 第一条采样之前没有「上一个 tick」→ 直接用自己，避免凭空冒出一个按下。
            prev = hadBefore ? beforeMask : cur;
            curMask = cur;
            prevMask = prev;
            covered = true;
            return true;
        }

        /// <summary>
        /// 由「上一个 tick 按着吗 / 这一 tick 按着吗」推出 <c>PointerPhase</c> 意义上的真假。
        /// 纯函数，自测可直接验证（它是「重装兵器按住才开火」「右键取消只生效一次」的唯一依据）。
        /// </summary>
        /// <param name="phaseValue">None=0 / Press=1 / Hold=2 / Release=3。</param>
        internal static bool EdgeOf(bool was, bool now, int phaseValue)
        {
            switch (phaseValue)
            {
                case 1: return now && !was;    // Press   = 上升沿
                case 2: return now;            // Hold    = 此刻按着
                case 3: return !now && was;    // Release = 下降沿
                default: return false;         // None
            }
        }

        internal static string DescribePlayback()
        {
            if (samples.Length == 0)
                return "无轨迹（这条回放没有逐 tick 指针采样）";
            return samples.Length + " 条采样，tick " + samples[0].Tick + ".." + samples[samples.Length - 1].Tick;
        }

        #region 自测：用合成采样验证「tick → 采样」的对应关系
        /// <summary>
        /// 自测：把几条合成采样喂进来，验证 <see cref="BeginTick"/> 的
        /// 「tick → 采样」对应关系与边沿推导。
        ///
        /// <para>这条检查是专门为「tick T 该用第 T 条采样」这个**时序**准备的 ——
        /// 弄错就是 ±1 tick 的延迟，实测会让重装兵器多发一颗子弹、打砖块的珠子落空。
        /// 纯逻辑，不碰 UI/引擎。</para>
        /// </summary>
        internal static bool SelfCheck(out string detail)
        {
            var sb = new StringBuilder(256);
            bool ok = true;

            var savedSamples = samples;
            int savedCursor = cursor;
            bool savedHave = haveCurrent;
            var savedCur = current;
            int savedPrevMask = prevMask;
            int savedCurMask = curMask;
            bool savedCovered = covered;
            try
            {
                samples = new[]
                {
                    new Sample { Tick = 10, X = 100, Y = 100, Mask = 0 },
                    new Sample { Tick = 20, X = 200, Y = 100, Mask = BitLeft },
                    new Sample { Tick = 30, X = 200, Y = 100, Mask = BitLeft | BitRight },
                };
                cursor = 0; haveCurrent = false; current = default;
                prevMask = 0; curMask = 0; covered = false;

                // ① 轨迹开始之前：不覆盖，绝不接管
                ok &= Expect(sb, "tick 5（第一条之前）不覆盖",
                    BeginTick(5, out _, out _, out _), false);

                // ② tick 10：用**第 10 条**采样（不是「上一条」）
                ok &= Expect(sb, "tick 10 被覆盖", BeginTick(10, out var s10, out var p10, out var c10), true);
                ok &= ExpectX(sb, "tick 10 位置 = 第 10 条", s10.x, 100f);
                ok &= ExpectI(sb, "tick 10 掩码 = 第 10 条", c10, 0);
                ok &= ExpectI(sb, "tick 10 无按下边沿", p10, c10);

                // ③ tick 20：位置是**第 20 条**（不是第 10 条的残留）；左键 0→1 是「刚按下」
                ok &= Expect(sb, "tick 20 被覆盖", BeginTick(20, out var s20, out var p20, out var c20), true);
                ok &= ExpectX(sb, "tick 20 位置 = 第 20 条", s20.x, 200f);
                ok &= ExpectI(sb, "tick 20 掩码 = 左键", c20, BitLeft);
                ok &= Expect(sb, "tick 20 左键刚按下", EdgeOf(p20 != 0, c20 != 0, 1), true);

                // ④ tick 25（没有采样）：沿用 tick 20 的状态，且**不产生边沿**
                ok &= Expect(sb, "tick 25 被覆盖（沿用上一条）",
                    BeginTick(25, out var s25, out var p25, out var c25), true);
                ok &= ExpectX(sb, "tick 25 位置沿用第 20 条", s25.x, 200f);
                ok &= Expect(sb, "tick 25 没有新边沿", EdgeOf(p25 != 0, c25 != 0, 1), false);

                // ⑤ tick 30：右键 0→1（左键保持）
                ok &= Expect(sb, "tick 30 被覆盖", BeginTick(30, out _, out var p30, out var c30), true);
                ok &= ExpectI(sb, "tick 30 掩码 = 左+右", c30, BitLeft | BitRight);
                ok &= Expect(sb, "tick 30 右键刚按下",
                    EdgeOf((p30 & BitRight) != 0, (c30 & BitRight) != 0, 1), true);
                ok &= Expect(sb, "tick 30 左键不是新按下",
                    EdgeOf((p30 & BitLeft) != 0, (c30 & BitLeft) != 0, 1), false);
            }
            finally
            {
                samples = savedSamples;
                cursor = savedCursor;
                haveCurrent = savedHave;
                current = savedCur;
                prevMask = savedPrevMask;
                curMask = savedCurMask;
                covered = savedCovered;
            }

            detail = sb.ToString().TrimEnd('；', ' ');
            return ok;
        }

        private static bool Expect(StringBuilder sb, string what, bool actual, bool want)
        {
            bool ok = actual == want;
            sb.Append(ok ? "OK " : "✗ ").Append(what).Append('；');
            return ok;
        }

        private static bool ExpectX(StringBuilder sb, string what, float actual, float want)
        {
            bool ok = Math.Abs(actual - want) < 0.01f;
            sb.Append(ok ? "OK " : "✗ ").Append(what);
            if (!ok)
                sb.Append("（得到 ").Append(actual.ToString("0.#")).Append("，应为 ").Append(want.ToString("0.#")).Append('）');
            sb.Append('；');
            return ok;
        }

        private static bool ExpectI(StringBuilder sb, string what, int actual, int want)
        {
            bool ok = actual == want;
            sb.Append(ok ? "OK " : "✗ ").Append(what);
            if (!ok)
                sb.Append("（得到 ").Append(actual).Append("，应为 ").Append(want).Append('）');
            sb.Append('；');
            return ok;
        }
        #endregion
        #endregion
    }
}
