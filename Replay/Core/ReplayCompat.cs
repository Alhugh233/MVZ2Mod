using System;
using System.Collections.Generic;
using System.Text;
using PVZEngine;
using PVZEngine.Level;

namespace Replay
{
    /// <summary>
    /// 第三方 mod 给回放补行为的**公开注册口**。
    ///
    /// <para><b>为什么需要它：</b>有一类关卡行为**不看指针事件的参数**，而是每帧直接问
    /// 「现在鼠标在哪 / 哪个键按着」（<c>Global.Input.GetPointerScreenPosition()</c>、
    /// <c>InputHelper.IsPointerHolding(...)</c>）。这类行为在回放里必须由**逐 tick 的指针轨迹**
    /// 驱动，而轨迹是按需录制的 —— 只有「这一关里确实存在会读指针的东西」时才录，
    /// 否则每局都白记几千条。<see cref="RegisterPointerConsumer(string, string)"/> 就是声明
    /// 「我的内容会读指针」的地方。</para>
    ///
    /// <para><b>原版已内置注册</b>（见 <see cref="EnsureBuiltins"/>）：
    /// 打砖块挡板、重装兵器、矿车、碾压墙。第三方 mod 在自己的
    /// <c>Plugin.Load()</c> 里调一次注册即可，回放会自动开始录制/重放指针轨迹。</para>
    ///
    /// <para><b>时机</b>：关卡开始时求值一次，之后**每 0.5 秒**复查一次；一旦判定为需要，
    /// 本关剩下的时间就**一直**录（不因为那个实体中途消失而停）—— 否则「实体消失后再出现」
    /// 会留下一段没有轨迹的空洞。</para>
    /// </summary>
    public static class ReplayCompat
    {
        /// <summary>自定义判定：返回 true 表示这一关需要逐 tick 指针轨迹。</summary>
        /// <param name="level">当前关卡引擎（不要长期持有它）。</param>
        public delegate bool PointerNeedPredicate(LevelEngine level);

        private sealed class Entry
        {
            public string Note;
            public string IdText;          // "mvz2:snake" 形式；用 Predicate 时为空
            public NamespaceID Id;
            public bool IdResolved;
            public bool IdBroken;
            public PointerNeedPredicate Predicate;
            public bool Builtin;
        }

        private static readonly List<Entry> entries = new List<Entry>();
        private static readonly object gate = new object();
        private static bool builtinsDone;

        #region 注册
        /// <summary>
        /// 注册一个「会读实时指针」的实体定义 ID。场上出现这个实体时，本关的指针轨迹开始被记录/重放。
        /// </summary>
        /// <param name="entityID">形如 <c>mvz2:snake</c>；省略命名空间时按 <c>mvz2</c> 处理。</param>
        /// <param name="note">写进回放小结的说明，便于排障。</param>
        public static void RegisterPointerConsumer(string entityID, string note = null)
        {
            if (string.IsNullOrEmpty(entityID))
                return;
            var text = entityID.IndexOf(':') >= 0 ? entityID : ("mvz2:" + entityID);
            lock (gate)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    if (entries[i].IdText == text)
                        return; // 重复注册忽略
                }
                entries.Add(new Entry { IdText = text, Note = note });
            }
        }

        /// <summary>同 <see cref="RegisterPointerConsumer(string, string)"/>，直接给 <see cref="NamespaceID"/>。</summary>
        public static void RegisterPointerConsumer(NamespaceID entityID, string note = null)
        {
            if (entityID == null)
                return;
            RegisterPointerConsumer(entityID.SpaceName + ":" + entityID.Path, note);
        }

        /// <summary>
        /// 注册一个自定义判定。适合「不靠某个具体实体」的情况（例如「我这一关的
        /// <c>StageBehaviour</c> 会读指针」）。
        ///
        /// <para>⚠️ 这个委托**每 0.5 秒被调一次**，请保持轻量、不要在里面分配大对象。</para>
        /// </summary>
        public static void RegisterPointerConsumer(PointerNeedPredicate predicate, string note = null)
        {
            if (predicate == null)
                return;
            lock (gate)
            {
                entries.Add(new Entry { Predicate = predicate, Note = note });
            }
        }

        /// <summary>把已注册的消费者列成一行（写进回放小结 / 日志）。</summary>
        public static IReadOnlyList<string> DescribeConsumers()
        {
            EnsureBuiltins();
            var list = new List<string>();            lock (gate)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    var text = e.Predicate != null ? "自定义判定" : e.IdText;
                    list.Add(string.IsNullOrEmpty(e.Note) ? text : (text + "（" + e.Note + "）"));
                }
            }
            return list;
        }
        #endregion

        #region 求值
        /// <summary>是否已经注册过任何消费者（一个都没有时连复查都省了）。</summary>
        internal static bool AnyConsumers
        {
            get
            {
                EnsureBuiltins();
                lock (gate) { return entries.Count > 0; }
            }
        }

        /// <summary>这一关现在是否需要逐 tick 指针轨迹。</summary>
        internal static bool LevelNeedsPointer(LevelEngine level)
        {
            string ignored;
            return FirstMatch(level, out ignored);
        }

        /// <summary>
        /// 这一关「第一个」命中的消费者是谁（写成一行自然语言）。
        ///
        /// <para>只用于**日志**：开始录轨迹时打一行出来，事后排障能立刻知道是哪个内容触发的
        /// （2-11 那种「前半段没轨迹」的情况，靠这一行就能确认是不是碾压墙后来才生成）。</para>
        /// </summary>
        internal static string DescribeFirstMatch(LevelEngine level)
        {
            string what;
            if (FirstMatch(level, out what))
                return what;
            return "（未命中任何已注册的消费者）";
        }

        /// <summary>
        /// 求值核心：找到**第一个**命中的消费者。<paramref name="match"/> 是它的可读描述。
        /// <see cref="LevelNeedsPointer"/> 与 <see cref="DescribeFirstMatch"/> 共用它，避免两套判定走偏。
        /// </summary>
        private static bool FirstMatch(LevelEngine level, out string match)
        {
            match = null;
            if (level == null)
                return false;
            EnsureBuiltins();

            // 拷一份再遍历：判定函数里如果再注册会改动 entries。
            Entry[] snapshot;
            lock (gate)
            {
                if (entries.Count == 0)
                    return false;
                snapshot = entries.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                var e = snapshot[i];
                var name = string.IsNullOrEmpty(e.Note) ? e.IdText : (e.IdText + "（" + e.Note + "）");
                if (e.Predicate != null)
                {
                    name = "自定义判定" + (string.IsNullOrEmpty(e.Note) ? "" : ("（" + e.Note + "）"));
                    try
                    {
                        if (e.Predicate(level))
                        {
                            match = name;
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning("[Replay] 指针消费者判定抛异常（已忽略）："
                            + (e.Note ?? "?") + " —— " + ex.GetType().Name + ": " + ex.Message);
                    }
                    continue;
                }

                var id = ResolveId(e);
                if (id == null)
                    continue;
                try
                {
                    if (level.EntityExists(id))
                    {
                        match = name;
                        return true;
                    }
                }
                catch (Exception)
                {
                    // 关卡正在拆除时查询会抛，忽略这一次。
                }
            }
            return false;
        }

        /// <summary>
        /// 解析 <c>"mvz2:xxx"</c>。**懒解析**：<see cref="NamespaceID"/> 的构造要等游戏起来之后
        /// 才安全（本仓库踩过「提前触发 MVZ2Logic 静态构造 → 异常被永久缓存」的坑），
        /// 而这里第一次被调到已经是关卡开始了。
        /// </summary>
        private static NamespaceID ResolveId(Entry e)
        {
            if (e.IdResolved)
                return e.Id;
            e.IdResolved = true;
            try
            {
                var text = e.IdText;
                int c = text.IndexOf(':');
                var space = c >= 0 ? text.Substring(0, c) : "mvz2";
                var path = c >= 0 ? text.Substring(c + 1) : text;
                e.Id = new NamespaceID(space, path);
            }
            catch (Exception ex)
            {
                e.IdBroken = true;
                Plugin.Log?.LogWarning("[Replay] 指针消费者 ID 解析失败：" + e.IdText
                    + "（" + ex.GetType().Name + "）");
            }
            return e.Id;
        }
        #endregion

        #region 内置（原版）消费者
        /// <summary>
        /// 原版里「每帧直接读指针」的内容。**故意用字符串而不是 <c>VanillaEffectID.*</c>**：
        /// 那些静态字段会去碰 <c>VanillaMod.spaceName</c>/<c>Global.BuiltinNamespace</c>，
        /// 在插件加载阶段解析它们等于提前跑游戏的静态构造（仓库里有过先例）。
        /// </summary>
        private static void EnsureBuiltins()
        {
            if (builtinsDone)
                return;
            builtinsDone = true;

            AddBuiltin("mvz2:breakout_board", "打砖块挡板：每个 tick 把挡板挪到指针处");
            AddBuiltin("mvz2:snipenser", "重装兵器：炮口朝指针 + 按住才开火");
            AddBuiltin("mvz2:minecart_rideable", "矿车：目标点每 tick 由指针决定");
            AddBuiltin("mvz2:crushing_walls", "碾压墙：每次按下鼠标都扣进度");
        }

        private static void AddBuiltin(string id, string note)
        {
            entries.Add(new Entry { IdText = id, Note = note, Builtin = true });
        }
        #endregion

        /// <summary>给日志/报告用的一行描述。</summary>
        internal static string Describe()
        {
            var list = DescribeConsumers();
            var sb = new StringBuilder(160);
            sb.Append(list.Count).Append(" 个：");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0)
                    sb.Append('、');
                sb.Append(list[i]);
            }
            return sb.ToString();
        }
    }
}
