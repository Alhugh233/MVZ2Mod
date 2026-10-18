using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Replay
{
    /// <summary>
    /// **录制策略**：本次录制要录什么、录多细。
    ///
    /// <para><b>来源 = 配置文件</b>（<c>[General]</c> / <c>[Debug]</c>）。这是协议里定死的一条：
    /// 录出来的文件长什么样，只由配置决定；而播的时候只由文件决定。
    /// 两者不共用状态源，但都要**显式**（见 <see cref="ReplayPlaybackPolicy"/>）。</para>
    ///
    /// <para>在「开始录制」那一刻快照一次（<see cref="FromConfig"/>），整场录制不再变 ——
    /// 否则玩家中途改配置会让同一个文件内部不一致。</para>
    /// </summary>
    public sealed class ReplayRecordOptions
    {
        // ---- 常规 ----

        /// <summary>每多少逻辑 tick 采一条指纹。调试模式下会被 <see cref="DebugEveryTick"/> 覆盖成 1。</summary>
        public int FingerprintInterval = 150;

        /// <summary>指纹里是否带「实体定义直方图」与「7 条 RNG 流各自的哈希」（更易定位分歧）。</summary>
        public bool FullFingerprint = true;

        /// <summary>是否录制逐 tick 指针轨迹（只有本关存在「会读实时指针」的内容时才真的写）。</summary>
        public bool PointerTrajectory = true;

        /// <summary>是否录制选项对话框的选项列表与玩家的选择。</summary>
        public bool RecordDialogs = true;

        /// <summary>除了 gzip 的 <c>.mrep</c>，再写一份未压缩的 <c>.mrep.json</c>。</summary>
        public bool WritePlainText;

        // ---- 调试模式（研发专用，默认关）----

        /// <summary>
        /// 调试模式。打开后：逐 tick 采指纹、指纹带上**全部**可采的字段
        /// （原始 RNG 状态、每个实体的明细、关卡内部标量），文件会大很多。
        /// 它**只给回放 mod 自己的开发/排障用**。</summary>
        public bool Debug;

        /// <summary>调试模式下逐 tick 采样（而不是每 <see cref="FingerprintInterval"/> 个 tick）。</summary>
        public bool DebugEveryTick = true;

        /// <summary>调试模式下把「原始 RNG 状态」（7 条流 × 4 个 int32）写进指纹。</summary>
        public bool DebugRawRng = true;

        /// <summary>调试模式下把「每个实体的明细」（id / 定义 / 血量 / 坐标 / 分层）写进指纹。</summary>
        public bool DebugEntityDetail = true;

        /// <summary>调试模式下把「关卡内部标量」（levelTime / currentEntityID / 实体回收池…）写进指纹。</summary>
        public bool DebugLevelScalars = true;

        /// <summary>调试模式下每条指纹最多写多少个实体（0 = 不限）。</summary>
        public int DebugEntityLimit;

        /// <summary>实际用的采样间隔。</summary>
        public int EffectiveInterval
        {
            get
            {
                if (Debug && DebugEveryTick)
                    return 1;
                return FingerprintInterval > 0 ? FingerprintInterval : 0;
            }
        }

        /// <summary>要不要采指纹（间隔 0 = 不采）。</summary>
        public bool AnyFingerprint => EffectiveInterval > 0;

        /// <summary>从配置文件读一份快照。</summary>
        public static ReplayRecordOptions FromConfig()
        {
            var o = new ReplayRecordOptions();
            o.FingerprintInterval = CfgInt(ReplayConfig.FingerprintInterval, 150);
            o.FullFingerprint = CfgBool(ReplayConfig.FullFingerprint, true);
            o.PointerTrajectory = CfgBool(ReplayConfig.PointerTrajectory, true);
            o.RecordDialogs = CfgBool(ReplayConfig.DialogAutoAnswer, true);
            o.WritePlainText = CfgBool(ReplayConfig.WritePlainText, false);

            o.Debug = CfgBool(ReplayConfig.DebugMode, false);
            o.DebugEveryTick = CfgBool(ReplayConfig.DebugEveryTick, true);
            o.DebugRawRng = CfgBool(ReplayConfig.DebugRawRng, true);
            o.DebugEntityDetail = CfgBool(ReplayConfig.DebugEntityDetail, true);
            o.DebugLevelScalars = CfgBool(ReplayConfig.DebugLevelScalars, true);
            o.DebugEntityLimit = Math.Max(0, CfgInt(ReplayConfig.DebugEntityLimit, 0));

            // 调试模式隐含「要完整信息」：不这样的话调试文件里反而缺 ed / r0..r6，很反直觉。
            if (o.Debug)
                o.FullFingerprint = true;
            return o;
        }

        /// <summary>本文件用到的可选特性（header 的 <c>caps</c>）。</summary>
        public string Caps()
        {
            // 指针轨迹要等关卡开始时才判定「本关需不需要」，这里先按配置声明；
            // 真正有没有 ptr 行由 header 的 ptrCount 兜底（读者两者以 count 为准）。
            var list = new List<string>(4);
            if (PointerTrajectory) list.Add(ReplayProtocol.CapPointer);
            if (RecordDialogs) list.Add(ReplayProtocol.CapDialog);
            list.Add(ReplayProtocol.CapHeldFlags);
            list.Add(ReplayProtocol.CapViewport);
            if (Debug) list.Add(ReplayProtocol.CapDebug);
            return ReplayProtocol.JoinCaps(list.ToArray());
        }

        /// <summary>本次录制实际启用的采样器名（header 的 <c>samplers</c>）。</summary>
        public string SamplerNames()
        {
            return ReplaySamplers.Names(AnyDebugSampler());
        }

        /// <summary>调试模式下还有没有「只属于调试」的采样器要跑。</summary>
        private bool AnyDebugSampler()
        {
            return Debug && (DebugRawRng || DebugEntityDetail || DebugLevelScalars);
        }

        public string Describe()
        {
            var sb = new StringBuilder(96);
            sb.Append("指纹间隔=").Append(EffectiveInterval);
            sb.Append(" 完整指纹=").Append(FullFingerprint ? "是" : "否");
            sb.Append(" 指针轨迹=").Append(PointerTrajectory ? "是" : "否");
            sb.Append(" 对话框=").Append(RecordDialogs ? "是" : "否");
            sb.Append(" 调试=").Append(Debug ? "是" : "否");
            if (Debug)
                sb.Append("（原始RNG=").Append(DebugRawRng ? "是" : "否")
                  .Append(" 实体明细=").Append(DebugEntityDetail ? "是" : "否")
                  .Append(" 关卡标量=").Append(DebugLevelScalars ? "是" : "否")
                  .Append('）');
            return sb.ToString();
        }

        private static int CfgInt(BepInEx.Configuration.ConfigEntry<int> e, int fallback)
        {
            try { return e != null ? e.Value : fallback; }
            catch (Exception) { return fallback; }
        }

        private static bool CfgBool(BepInEx.Configuration.ConfigEntry<bool> e, bool fallback)
        {
            try { return e != null ? e.Value : fallback; }
            catch (Exception) { return fallback; }
        }
    }

    /// <summary>
    /// **播放策略**：这一份回放该怎么播、怎么核对。
    ///
    /// <para><b>来源 = 回放文件本身</b>（header 的 <c>fpt</c>/<c>debug</c>/<c>caps</c>/<c>samplers</c>
    /// 与文件里实际存在哪些记录）。播放**不去读配置文件**决定语义行为 ——
    /// 这样「拿别人的回放文件」与「拿自己的」行为完全一致，且改配置不会让老回放播得不一样。</para>
    ///
    /// <para>配置只影响**表现**（假鼠标、对话框停留秒数、探针），不影响语义。</para>
    /// </summary>
    public sealed class ReplayPlaybackPolicy
    {
        public int FormatVersion;
        public int SpecVersion;

        /// <summary>录制时用的采样间隔（header 的 <c>fpt</c>）。0 / 缺失 = 未知。</summary>
        public int RecordedFingerprintInterval;

        /// <summary>这是调试模式文件：逐 tick 指纹 + 全部附加字段。</summary>
        public bool Debug;

        /// <summary>文件里实际有 <c>ptr</c> 行。</summary>
        public bool HasPointerTrajectory;

        /// <summary>文件里实际有 <c>dlg</c> 行。</summary>
        public bool HasDialogs;

        /// <summary>录制时的窗口尺寸可用（<c>sw</c>/<c>sh</c> 有效）。</summary>
        public bool HasViewport;

        /// <summary>动作事件带手持标记（<c>itg</c>/<c>evk</c>）。</summary>
        public bool HasHeldFlags;

        /// <summary>录制时启用的采样器名（header 的 <c>samplers</c>，逗号分隔）。</summary>
        public string Samplers = string.Empty;

        /// <summary>header 的 <c>caps</c> 原样。</summary>
        public string Caps = string.Empty;

        /// <summary>时间轴单位（tick/秒）。</summary>
        public int Tps = 30;

        /// <summary>文件里的指纹条数（用于「有没有东西可核」的判断）。</summary>
        public int FingerprintCount;

        /// <summary>要不要核对指纹。</summary>
        public bool VerifyFingerprints => FingerprintCount > 0;

        /// <summary>本次播放要不要采「调试附加字段」（文件说它是调试文件就要）。</summary>
        public bool WantDebugFields => Debug;

        /// <summary>
        /// 文件自述与内容不一致的地方（人读；空列表 = 自洽）。
        ///
        /// <para>它只描述「文件本身有没有说清自己」——例如「caps 说有指针轨迹但其实一条
        /// <c>ptr</c> 行都没有」。与「回放是否复现了实战」是两回事。</para>
        /// </summary>
        public List<string> SelfCheckWarnings = new List<string>();

        /// <summary>
        /// 从回放文件推导出播放策略。
        ///
        /// <para><c>internal</c> 是刻意的：<see cref="ReplayFile"/> 本身还不打算开放成公开 API
        /// （它连着事件/指纹等一整套内部类型）。第三方要「改播放行为」应当注册**采样器**
        /// （<see cref="ReplaySamplers"/>）或**环境来源**（<see cref="ReplayEnvSources"/>），
        /// 那两条路已经把「不改核心代码就能扩展」覆盖了。</para>
        /// </summary>
        internal static ReplayPlaybackPolicy FromFile(ReplayFile f)
        {
            var p = new ReplayPlaybackPolicy();
            if (f == null)
                return p;

            p.FormatVersion = f.Version;
            p.SpecVersion = f.SpecVersion;
            p.RecordedFingerprintInterval = f.FingerprintInterval;
            p.Debug = f.Debug;
            p.Caps = f.Caps ?? string.Empty;
            p.Samplers = f.Samplers ?? string.Empty;
            p.Tps = f.Tps > 0 ? f.Tps : 30;
            p.FingerprintCount = f.Fingerprints.Count;

            // 「文件里到底有没有这类记录」以实际计数为准（caps 只是声明，可能被手改坏）。
            p.HasPointerTrajectory = f.PointerSamples.Count > 0
                || ReplayProtocol.HasCap(p.Caps, ReplayProtocol.CapPointer);
            p.HasDialogs = f.Dialogs.Count > 0
                || ReplayProtocol.HasCap(p.Caps, ReplayProtocol.CapDialog);
            // 手持标记只能按**实际事件**判：caps 里的声明在「这一局没有蓝图事件」时是空话。
            p.HasHeldFlags = f.HasHeldFlagsInEvents;

            p.HasViewport = f.ScreenW > 0f && f.ScreenH > 0f;
            // 有 ptr 行却声明没有 ↔ 反之：都算「文件自述与内容不符」，交给上层提示。
            p.SelfCheckWarnings = BuildSelfCheck(f, p);
            return p;
        }

        private static List<string> BuildSelfCheck(ReplayFile f, ReplayPlaybackPolicy p)
        {
            var list = new List<string>();
            if (f.FingerprintInterval > 1 && f.Fingerprints.Count > 1)
            {
                // 抽查前几条的间隔：调试模式之外，间隔应当与 fpt 一致。
                int expected = f.FingerprintInterval;
                for (int i = 1; i < f.Fingerprints.Count && i <= 4; i++)
                {
                    int gap = f.Fingerprints[i].Tick - f.Fingerprints[i - 1].Tick;
                    if (gap != expected && !(f.Debug && gap == 1))
                    {
                        list.Add("指纹间隔与 header 声明的 " + expected + " 不符（第 " + i
                            + " 条起是 " + gap + "）");
                        break;
                    }
                }
            }
            if (p.Debug && f.Fingerprints.Count > 0 && p.RecordedFingerprintInterval != 1)
                list.Add("标记为调试文件，但 header 的 fpt = " + p.RecordedFingerprintInterval + "（应为 1）");
            if (ReplayProtocol.HasCap(p.Caps, ReplayProtocol.CapPointer) && f.PointerSamples.Count == 0)
                list.Add("caps 声明有指针轨迹，但文件里一条 ptr 行也没有");
            if (!f.HasSamplersField)
                list.Add("header 没有 samplers 字段（旧文件或手工编辑过）");
            return list;
        }

        public string Describe()
        {
            var sb = new StringBuilder(128);
            sb.Append("协议 v").Append(SpecVersion).Append("／格式 v").Append(FormatVersion);
            sb.Append(" tps=").Append(Tps);
            sb.Append(" 指纹间隔=").Append(RecordedFingerprintInterval);
            sb.Append(" 指纹=").Append(FingerprintCount).Append(" 条");
            sb.Append(" 调试=").Append(Debug ? "是" : "否");
            sb.Append(" 特性=").Append(string.IsNullOrEmpty(Caps) ? "（无）" : Caps);
            return sb.ToString();
        }
    }
}
