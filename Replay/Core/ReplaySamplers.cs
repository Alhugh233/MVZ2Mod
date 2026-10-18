using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using PVZEngine;
using PVZEngine.Entities;
using PVZEngine.Level;
using PVZEngine.Tools.Random;

namespace Replay
{
    /// <summary>
    /// 一次指纹采样的上下文。采样器只通过它读写，于是「加一种新指纹」不需要改录制/播放的任何一行。
    ///
    /// <para>上下文由录制侧（<see cref="ReplayRecorder"/>）与播放侧（<see cref="ReplayPlayer"/>）
    /// **共用**：同一批采样器在两边跑，产出的字段才能逐字比对。
    /// 这正是「录制与播放不共用状态源、但共用**采样器**」的含义。</para>
    /// </summary>
    public sealed class ReplaySamplerContext
    {
        internal LevelEngine Level;
        internal int Tick;
        internal ReplayRecordOptions Options;
        internal ReplayFingerprint Fp;

        private SerializableLevel serialized;
        private bool serializedTried;
        private SerializableLevel rngSnapshot;
        private bool rngTried;

        /// <summary>当前关卡引擎（可能为 null，采样器要自己判）。</summary>
        public LevelEngine Engine => Level;

        /// <summary>当前逻辑 tick。</summary>
        public int CurrentTick => Tick;

        /// <summary>本场录制/播放的策略。调试采样器据此决定要不要干活。</summary>
        public ReplayRecordOptions RecordOptions => Options;

        /// <summary>本场是不是调试模式。</summary>
        public bool Debug => Options != null && Options.Debug;

        /// <summary>
        /// 写入一条自定义指纹字段。
        ///
        /// <para><b>键名规则（协议的一部分）</b>：内置键是 2 个小写字母
        /// （<c>en/w/fl/cl/ec/eh/ed/r/r0..r6</c> 与调试的 <c>rx/ei/lk</c>）。
        /// 第三方采样器请一律用 <c>x_</c> 前缀（例如 <c>x_myMod_score</c>），
        /// 避免以后协议加字段时撞名。</para>
        /// </summary>
        public void Set(string key, string value)
        {
            if (Fp == null || string.IsNullOrEmpty(key))
                return;
            // ⚠️ 内置键必须挡住。否则第三方写一个 "en"，会与内置字段一起产出**同一个 JSON 键两次**，
            // 而单遍解析器「后写的赢」→ 内置字段被字符串覆盖 → 读回来解析失败 → 变成 0，
            // 表现为「能量莫名其妙一直是 0」，极难查。宁可丢掉第三方那个字段并报一次警告。
            if (ReplayFingerprint.IsBuiltinKey(key))
            {
                WarnBuiltinKey(key);
                return;
            }
            Fp.SetExtra(key, value);
        }

        private static readonly List<string> warnedKeys = new List<string>();

        private static void WarnBuiltinKey(string key)
        {
            lock (warnedKeys)
            {
                if (warnedKeys.Contains(key))
                    return;
                warnedKeys.Add(key);
            }
            Plugin.Log?.LogWarning("[Replay] 采样器试图写内置指纹字段 '" + key
                + "'（已被忽略）。第三方字段必须用 '" + ReplayProtocol.FpExtraPrefix + "' 前缀。");
        }

        /// <summary>写一个整数（十进制）。</summary>
        public void SetNumber(string key, long value)
        {
            Set(key, value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>写一个 64 位哈希（16 位小写十六进制，与 <see cref="Json.Hex"/> 同制）。</summary>
        public void SetHex(string key, ulong value)
        {
            Set(key, Json.Hex(value));
        }

        /// <summary>
        /// 「7 条 RNG 流的快照」。比 <see cref="Serialize"/> 便宜得多（只序列化随机数），
        /// 所以**常规指纹也用它**；第一次调用时才真的分配，后续复用。
        ///
        /// <para>注意它填的是 <see cref="SerializableLevel"/> 里**只有 RNG** 的那几个字段
        /// （<c>WriteRandomToSerializable</c> 只写 seed 与 7 条流），别的字段是默认值 ——
        /// 想要完整快照请用 <see cref="Serialize"/>。</para>
        /// </summary>
        public SerializableLevel RngSnapshot()
        {
            if (rngTried)
                return rngSnapshot;
            rngTried = true;
            try
            {
                if (Level == null)
                    return rngSnapshot = null;
                var seri = new SerializableLevel();
                Level.WriteRandomToSerializable(seri);
                rngSnapshot = seri;
            }
            catch (Exception)
            {
                rngSnapshot = null;
            }
            return rngSnapshot;
        }

        /// <summary>
        /// 「本 tick 的**完整**关卡序列化快照」。第一次调用时才真的序列化（贵），后续复用。
        ///
        /// <para>给调试采样器用：<see cref="SerializableLevel"/> 里有 <c>levelTime</c>、
        /// <c>currentEntityID</c>、<c>waveState</c>、<c>energy</c>、实体列表与回收池等
        /// 一堆**在引擎上是私有、读不到**的量。</para>
        /// </summary>
        public SerializableLevel Serialize()
        {
            if (serializedTried)
                return serialized;
            serializedTried = true;
            try
            {
                serialized = Level != null ? Level.ToSerializable() : null;
            }
            catch (Exception)
            {
                serialized = null;
            }
            return serialized;
        }
    }

    /// <summary>
    /// 一种「状态指纹」的采集器。
    ///
    /// <para>录制与播放**都**跑同一批采样器；<see cref="Sample"/> 产出的字段写进指纹行，
    /// 播放时逐字比对。所以「加一种新指纹」＝注册一个采样器，录制/播放的代码一行都不用动。</para>
    /// </summary>
    public interface IReplaySampler
    {
        /// <summary>采样器名（写进 header 的 <c>samplers</c>，也是去重的键）。</summary>
        string Name { get; }

        /// <summary>只有调试模式才启用（研发专用：字段多、开销大）。</summary>
        bool DebugOnly { get; }

        /// <summary>把本采样器的字段写进 <paramref name="ctx"/>。</summary>
        void Sample(ReplaySamplerContext ctx);
    }

    /// <summary>
    /// 采样器注册表。内置 6 个：
    /// <list type="bullet">
    /// <item><c>state.basic</c> — 能量 / 波次 / 旗数 / 已清关</item>
    /// <item><c>state.entities</c> — 实体数量 + 实体集合哈希 + 定义直方图</item>
    /// <item><c>state.rng</c> — 7 条 RNG 流的合并哈希与分流哈希</item>
    /// <item><c>dbg.rngRaw</c>（调试） — 7 条流的**原始** int32 状态</item>
    /// <item><c>dbg.entities</c>（调试） — 每个实体的明细</item>
    /// <item><c>dbg.level</c>（调试） — 关卡序列化里的内部标量</item>
    /// </list>
    /// </summary>
    public static class ReplaySamplers
    {
        private static readonly List<IReplaySampler> samplers = new List<IReplaySampler>();
        private static readonly object gate = new object();
        private static bool builtinsDone;

        /// <summary>注册一个采样器。同名重复注册会被忽略（内置的优先）。</summary>
        public static void Register(IReplaySampler sampler)
        {
            if (sampler == null || string.IsNullOrEmpty(sampler.Name))
                return;
            lock (gate)
            {
                EnsureBuiltinsLocked();
                for (int i = 0; i < samplers.Count; i++)
                {
                    if (samplers[i].Name == sampler.Name)
                        return;
                }
                samplers.Add(sampler);
            }
        }

        /// <summary>
        /// 本次（录制或播放）要跑哪一批采样器。
        ///
        /// <para>返回顺序 = **字段在指纹行里的顺序**：先全部常规采样器，再全部调试采样器
        /// （各自保持注册顺序）。这样指纹行的可读顺序与「重要程度」一致。
        /// 注意**不能**靠「注册顺序里调试的在后面」来筛 —— 第三方随时可能注册一个新的常规采样器。</para>
        /// </summary>
        internal static IReplaySampler[] Active(bool debug)
        {
            lock (gate)
            {
                EnsureBuiltinsLocked();
                var list = new List<IReplaySampler>(samplers.Count);
                for (int i = 0; i < samplers.Count; i++)
                {
                    if (!samplers[i].DebugOnly)
                        list.Add(samplers[i]);
                }
                if (debug)
                {
                    for (int i = 0; i < samplers.Count; i++)
                    {
                        if (samplers[i].DebugOnly)
                            list.Add(samplers[i]);
                    }
                }
                return list.ToArray();
            }
        }

        /// <summary>把当前启用的采样器名拼成一行（header 的 <c>samplers</c>）。</summary>
        internal static string Names(bool debug)
        {
            var active = Active(debug);
            var sb = new StringBuilder(48);
            for (int i = 0; i < active.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(active[i].Name);
            }
            return sb.ToString();
        }

        private static void EnsureBuiltinsLocked()
        {
            if (builtinsDone)
                return;
            builtinsDone = true;
            // 顺序 = 指纹行里的字段顺序；**非调试的必须排在前**（Active 靠这个截断）。
            samplers.Add(new BasicSampler());
            samplers.Add(new EntitySetSampler());
            samplers.Add(new RngSampler());
            samplers.Add(new RawRngSampler());
            samplers.Add(new EntityDetailSampler());
            samplers.Add(new LevelScalarSampler());
        }

        // ------------------------------------------------------------------
        // 内置采样器
        // ------------------------------------------------------------------

        /// <summary>能量 / 波次 / 旗数 / 已清关。</summary>
        private sealed class BasicSampler : IReplaySampler
        {
            public string Name => "state.basic";
            public bool DebugOnly => false;

            public void Sample(ReplaySamplerContext ctx)
            {
                var lvl = ctx.Engine;
                if (lvl == null)
                    return;
                var fp = ctx.Fp;
                fp.Enemies = (int)lvl.Energy;
                fp.Wave = lvl.CurrentWave;
                fp.Flags = lvl.CurrentFlag;
                fp.Cleared = lvl.IsCleared;
            }
        }

        /// <summary>
        /// 实体数量 + 实体集合哈希 + 定义直方图。
        ///
        /// <para><b>为什么单独一项：</b>「实体 ID 错位」（同一个 id 在两边指向不同的东西）是
        /// 回放不复现的头号原因。只比 RNG/能量只能说「已经不一致了」，
        /// 这一项能说出「从哪个 tick 起实体集合就不一样」，把原因缩到那附近的具体动作。</para>
        /// </summary>
        private sealed class EntitySetSampler : IReplaySampler
        {
            public string Name => "state.entities";
            public bool DebugOnly => false;

            public void Sample(ReplaySamplerContext ctx)
            {
                var lvl = ctx.Engine;
                if (lvl == null)
                    return;
                ulong eh = 14695981039346656037UL;
                ReplayRecorder.HashEntities(lvl, ref eh, out var count, out var defs);
                ctx.Fp.EntityCount = count;
                ctx.Fp.EntityHash = Json.Hex(eh);
                // 直方图是可读版（「少了哪一类」直接看得出来），但每条多约 100 字节 →
                // 只在完整指纹模式或调试模式下写。
                if (ctx.RecordOptions != null && ctx.RecordOptions.FullFingerprint)
                    ctx.Fp.EntityDefs = defs;
            }
        }

        /// <summary>7 条 RNG 流的合并哈希（+ 完整模式下的分流哈希）。</summary>
        private sealed class RngSampler : IReplaySampler
        {
            public string Name => "state.rng";
            public bool DebugOnly => false;

            public void Sample(ReplaySamplerContext ctx)
            {
                var rngs = ctx.RngSnapshot();
                if (rngs == null)
                    return;

                ctx.Fp.Rng = ReplayRecorder.RngMergedHash(rngs);

                // 分流哈希：合并哈希只能告诉你「有 RNG 跑偏」，分流能点名**哪一条**。
                if (ctx.RecordOptions != null && ctx.RecordOptions.FullFingerprint)
                    ctx.Fp.RngStreams = ReplayRecorder.RngStreamHashes(rngs);
            }
        }

        /// <summary>
        /// 【调试】7 条 RNG 流的**原始** int32 状态。
        ///
        /// <para>分流哈希能说「spawn 流跑偏了」，原始值能说「它在第几次 Next 之后偏的」——
        /// 排查「同一个关键帧为什么生成出不同的东西」时这是决定性的。
        /// 代价是每条指纹多约 250 字节，所以只在调试模式写。</para>
        /// </summary>
        private sealed class RawRngSampler : IReplaySampler
        {
            public string Name => "dbg.rngRaw";
            public bool DebugOnly => true;

            public void Sample(ReplaySamplerContext ctx)
            {
                if (ctx.RecordOptions != null && !ctx.RecordOptions.DebugRawRng)
                    return;
                var rngs = ctx.RngSnapshot();
                if (rngs == null)
                    return;
                var sb = new StringBuilder(280);
                AppendRng(sb, rngs.levelRandom);
                AppendRng(sb, rngs.entityRandom);
                AppendRng(sb, rngs.effectRandom);
                AppendRng(sb, rngs.roundRandom);
                AppendRng(sb, rngs.spawnRandom);
                AppendRng(sb, rngs.conveyorRandom);
                AppendRng(sb, rngs.miscRandom);
                ctx.Set(ReplayProtocol.FpRawRng, sb.ToString());
            }

            private static void AppendRng(StringBuilder sb, SerializableRNG r)
            {
                if (sb.Length > 0)
                    sb.Append('|');
                if (r == null)
                {
                    sb.Append("null");
                    return;
                }
                sb.Append(r.x.ToString("x8", CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.y.ToString("x8", CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.z.ToString("x8", CultureInfo.InvariantCulture)).Append(',')
                  .Append(r.w.ToString("x8", CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// 【调试】每个实体的明细：<c>id:定义:血量:坐标:分层</c>。
        ///
        /// <para>集合哈希只能告诉你「集合变了」，明细能告诉你**哪个实体的哪个量**先变
        /// （例如某个僵尸的血量少了 1 → 说明有一次伤害没吃到）。</para>
        /// </summary>
        private sealed class EntityDetailSampler : IReplaySampler
        {
            public string Name => "dbg.entities";
            public bool DebugOnly => true;

            public void Sample(ReplaySamplerContext ctx)
            {
                if (ctx.RecordOptions != null && !ctx.RecordOptions.DebugEntityDetail)
                    return;
                var lvl = ctx.Engine;
                if (lvl == null)
                    return;
                var all = ReplayRecorder.EnumerateEntities(lvl);
                if (all == null)
                    return;
                int limit = ctx.RecordOptions != null ? ctx.RecordOptions.DebugEntityLimit : 0;

                var rows = new List<string>(all.Length);
                for (int i = 0; i < all.Length; i++)
                {
                    var e = all[i];
                    if (e == null || e.Pointer == IntPtr.Zero)
                        continue;
                    rows.Add(Row(lvl, e));
                }
                // 枚举顺序由位置比较器决定，位置一变顺序就变 → 排序后才是确定的指纹。
                rows.Sort(StringComparer.Ordinal);
                if (limit > 0 && rows.Count > limit)
                {
                    int omitted = rows.Count - limit;
                    rows.RemoveRange(limit, omitted);
                    rows.Add("...+" + omitted);
                }
                ctx.Set(ReplayProtocol.FpEntityDetail, string.Join(";", rows.ToArray()));
            }

            private static string Row(LevelEngine lvl, Entity e)
            {
                var sb = new StringBuilder(64);
                try { sb.Append(e.ID); } catch (Exception) { sb.Append('?'); }
                sb.Append(':').Append(Normalize(ReplayRecorder.DefIdOf(e), "?"));
                try { sb.Append(':').Append(e.Health.ToString("0.###", CultureInfo.InvariantCulture)); }
                catch (Exception) { sb.Append(":?"); }
                try
                {
                    var p = e.Position;
                    sb.Append(':')
                      .Append(p.x.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                      .Append(p.y.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                      .Append(p.z.ToString("0.##", CultureInfo.InvariantCulture));
                }
                catch (Exception) { sb.Append(":?"); }
                sb.Append(':').Append(Normalize(ReplayRecorder.LayerKeyOf(lvl, e)));
                try { if (e.State != 0) sb.Append(":st").Append(e.State); } catch (Exception) { }
                return sb.ToString();
            }

            /// <summary>
            /// 空值统一写成一个占位符。
            ///
            /// <para>不这么做的话「不在任何分层上」会写成空串，于是明细里出现
            /// <c>…:940,0,96:</c> 这种**末尾空字段** —— 而空字段在 <c>:</c> 分隔的字符串里
            /// 既可能是「空」也可能是「少写了一段」，人工看容易误判
            /// （比对本身不受影响，因为两边一致）。</para>
            /// </summary>
            private static string Normalize(string s, string fallback = "-")
            {
                return string.IsNullOrEmpty(s) ? fallback : s;
            }
        }

        /// <summary>
        /// 【调试】关卡序列化里的内部标量。
        ///
        /// <para>这些字段在 <c>LevelEngine</c> 上是私有的（interop 读不到），但都会写进
        /// <c>ToSerializable()</c>。它们能一句话解释「为什么生成的东西不一样」——
        /// 例如 <c>currentEntityID</c> 差 1 就说明多/少生成过一个实体。</para>
        /// </summary>
        private sealed class LevelScalarSampler : IReplaySampler
        {
            public string Name => "dbg.level";
            public bool DebugOnly => true;

            public void Sample(ReplaySamplerContext ctx)
            {
                if (ctx.RecordOptions != null && !ctx.RecordOptions.DebugLevelScalars)
                    return;
                var s = ctx.Serialize();
                if (s == null)
                    return;
                var sb = new StringBuilder(96);
                try
                {
                    sb.Append("lt=").Append(s.levelTime);
                    sb.Append(",eid=").Append(s.currentEntityID);
                    sb.Append(",seed=").Append(s.seed);
                    sb.Append(",wave=").Append(s.currentWave);
                    sb.Append(",flag=").Append(s.currentFlag);
                    sb.Append(",ws=").Append(s.waveState);
                }
                catch (Exception)
                {
                    return;
                }
                try
                {
                    sb.Append(",ec=").Append(s.entities != null ? s.entities.Count : -1);
                    sb.Append(",trash=").Append(s.entityTrash != null ? s.entityTrash.Count : -1);
                    sb.Append(",spid=").Append(s.currentSeedPackID);
                    sb.Append(",req=").Append(s.requireCards ? 1 : 0);
                    sb.Append(",clr=").Append(s.isCleared ? 1 : 0);
                    sb.Append(",en=").Append(s.energy.ToString("0.###", CultureInfo.InvariantCulture));
                }
                catch (Exception) { }
                ctx.Set(ReplayProtocol.FpLevelScalars, sb.ToString());
            }
        }
    }
}
