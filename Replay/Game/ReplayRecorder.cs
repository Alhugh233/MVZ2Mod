using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using MVZ2.Level;
using MVZ2.Level.Entities;
using MVZ2.Managers;
using MVZ2Logic.Serialization;
using PVZEngine;
using PVZEngine.Entities;
using PVZEngine.Level;
using PVZEngine.Tools.Random;
using UnityEngine;

namespace Replay
{
    /// <summary>
    /// 回放录制器：抓起始关键帧 → 收动作事件 → 采状态指纹 → 写 <c>.mrep</c>。
    ///
    /// 所有方法都在 Unity 主线程上经 Harmony 补丁调用，不做跨线程同步。
    ///
    /// ── 时间轴 ────────────────────────────────────────────────────────
    /// 时间轴单位是游戏自己的逻辑 tick（<see cref="LevelEngine.GetLevelTime"/>，30 tick/秒）。
    /// 因为加速/慢动作只改变「一帧跑几个 tick」，不改变每个 tick 的内容，
    /// 所以按 tick 记录天然就是「不记录暂停/加速」的。
    ///
    /// ── 为什么事件要「关键帧抓取成功之后」才收 ──────────────────────────
    /// 关键帧是某一 tick 末尾的完整状态。在这之前发生的动作已经被包含进关键帧里了，
    /// 再记一遍会导致回放时重复执行。因此用 <c>keyframeState</c> 作为闸门：
    /// 抓取成功前丢弃事件（它们已在关键帧里），成功后开始记录。
    /// </summary>
    internal static class ReplayRecorder
    {
        /// <summary>关键帧抓取状态。</summary>
        private enum KeyframeState
        {
            /// <summary>还没抓；此阶段的事件全部丢弃（它们已包含在关键帧里）。</summary>
            Pending = 0,
            /// <summary>已抓到，可以录事件了。</summary>
            Ok = 1,
            /// <summary>抓取失败（例如 SaveGame 抛异常）；继续录事件，但文件标 hasKeyframe=false。</summary>
            Failed = 2,
        }

        /// <summary>关键帧抓取失败后，还重试多少个 tick 就放弃。</summary>
        private const int KeyframeRetryTicks = 5;

        /// <summary>
        /// 自动落盘间隔（tick）。600 tick = 20 秒。
        /// <para>要不要用它由 <see cref="ReplayConfig.GetAutoSave"/> 决定（**默认关**）。</para>
        /// </summary>
        private const int AutoSaveTicks = 600;

        /// <summary>
        /// 文件格式版本。
        ///
        /// <para><b>现在这个值住在 <see cref="ReplayProtocol"/> 里</b>，因为「文件格式」是协议的事，
        /// 而协议要能被别的工具（解析器 / 迁移工具）照着实现。这里保留一个转发只是为了可读性。</para>
        ///
        /// <para>版本历史见 <see cref="ReplayProtocol.FormatVersion"/> 的注释。
        /// 加/删/改行结构才 +1；只加 header 字段不动 <c>ver</c> 也可以（读者按「缺字段 = 旧行为」处理），
        /// 但 v10 那次一次性把环境、调试、策略这套东西都加了进去，所以整体 +1。</para>
        /// </summary>
        private const int FormatVersion = ReplayProtocol.FormatVersion;

        #region 会话状态
        private static readonly List<string> eventLines = new List<string>();
        private static readonly List<string> fingerprintLines = new List<string>();

        private static LevelController bound;
        private static IntPtr boundPtr;
        private static string name;
        private static string reason;
        private static bool armed;
        private static KeyframeState keyframeState;
        private static string keyframeBson;
        private static string keyframeError;
        private static int startTick;
        private static int lastTick;
        private static int nextFingerprintTick;
        private static int nextAutoSaveTick;
        private static int funnelDepth;

        // header 字段（抓取关键帧时填）
        private static string hStage;
        private static string hArea;
        private static string hDifficulty;
        private static int hSeed;

        /// <summary>
        /// 开录那一刻的**全局金币**（写进 header 的 <c>mn</c>）。
        ///
        /// <para>金币不在关卡关键帧里（它是 <c>Global.Saves</c> 的钱），但它会参与关卡逻辑的**分支**：
        /// 上锁的箱子按「钱够不够」决定要不要生成「付钱」那一项，于是选项个数都可能不同。
        /// 记下来，回放时用它给金币影子播种（<see cref="ReplaySaveGuard.SeedMoney"/>），
        /// 那些分支才会与录制时一致。</para>
        ///
        /// <para><see cref="hMoneyKnown"/> 为 false（读不到，或旧文件）时不播种，行为不变。</para>
        /// </summary>
        private static int hMoney;
        private static bool hMoneyKnown;

        /// <summary>
        /// 开录那一刻的窗口尺寸（写进 header 的 <c>sw</c>/<c>sh</c>）。
        ///
        /// <para>事件里的屏幕坐标必须配上它才能跨窗口尺寸重放：
        /// 「像素 → 世界坐标」换算里含屏幕高，不记下来的话换个窗口再播，
        /// 同一个像素就指向别的格子了（见 <see cref="ReplayViewport"/>）。</para>
        /// </summary>
        private static float hScreenW;
        private static float hScreenH;

        private static string lastWrittenPath;

        /// <summary>
        /// <c>.mrep</c> 头里 <c>game</c> 字段的缓存（见 <see cref="GameVersion"/>）。
        /// </summary>
        private static string gameVersion;

        /// <summary>
        /// 本场录制的**录制策略快照**（从配置文件读一次，整场不变）。
        ///
        /// <para>协议里「录制行为取决于配置文件」就是这一条：录出来什么，只看它。
        /// 冻结成快照而不是每次读配置，是为了避免玩家中途改配置让同一个文件内部不一致。</para>
        /// </summary>
        private static ReplayRecordOptions recordOptions = new ReplayRecordOptions();

        /// <summary>本场录制的环境快照（写进 <c>env</c> 行）。</summary>
        private static ReplayEnvironment environment;

        /// <summary>本场录制要写出的 <c>env</c> 行（摘要 + 每个条目一行），在 Begin 时拼好。</summary>
        private static readonly List<string> envLines = new List<string>();
        #endregion

        #region 对外状态
        public static bool IsArmed => armed;
        public static string LastWrittenPath => lastWrittenPath;
        public static string CurrentName => name;

        /// <summary>已记录的动作事件数（自测与排障用）。</summary>
        public static int EventCount => eventLines.Count;

        /// <summary>
        /// 当前逻辑 tick（录制中）。对话框记录要按 tick 打戳，所以暴露出来。
        /// 没在录、或拿不到关卡时返回 <see cref="lastTick"/>。
        /// </summary>
        public static int CurrentTickNow => bound != null ? CurrentTick(bound) : lastTick;

        public static string Describe()
        {
            if (!armed)
                return "未录制";
            if (keyframeState == KeyframeState.Pending)
                return "录制中(等关键帧) " + name;
            return "录制中 " + name
                + " tick=" + lastTick
                + " 事件=" + eventLines.Count
                + " 指纹=" + fingerprintLines.Count;
        }
        #endregion

        #region 漏斗重入闸门
        /// <summary>
        /// 进入「手持物指针事件」漏斗。
        /// 漏斗内部会调用 <c>HeldItemComponent.SetHeldItem</c>（例如点卡选中），
        /// 那一层不该被当成独立的手持物切换来记录，否则回放会多做一次。
        /// </summary>
        public static void EnterFunnel()
        {
            funnelDepth++;
        }

        public static void ExitFunnel()
        {
            if (funnelDepth > 0)
                funnelDepth--;
        }

        public static bool InFunnel => funnelDepth > 0;
        #endregion

        #region 生命周期（由补丁调用）

        /// <summary>
        /// 关卡已开始（<c>StartGame</c> / <c>LoadGame</c> 之后）。
        /// 自动录制模式在这里决定是否开录。
        /// </summary>
        public static void NotifyLevelReady(LevelController ctrl, bool loadedFromSave)
        {
            try
            {
                if (ReplayPlayer.SuppressRecording)
                    return; // 播放器自己调用的 LoadGame，不是玩家开的局
                if (ctrl == null)
                    return;
                if (armed && bound != null && bound.Pointer == ctrl.Pointer)
                    return;

                bool auto = false;
                try { auto = ReplayConfig.AutoRecord != null && ReplayConfig.AutoRecord.Value; }
                catch (Exception) { }

                if (!auto)
                {
                    Plugin.Log?.LogInfo("[Replay] 关卡已就绪（自动录制已关闭，用 /replay rec 手动开始）");
                    return;
                }

                Begin(null, loadedFromSave ? "auto-load" : "auto-start", ctrl);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Replay] NotifyLevelReady 出错: " + e);
            }
        }

        /// <summary>手动开始录制（<c>/replay rec</c>）。</summary>
        public static bool BeginManual(string replayName, LevelController ctrl)
        {
            if (ctrl == null)
                return false;
            return Begin(replayName, "manual", ctrl);
        }

        /// <summary>每逻辑 tick（<c>LevelEngine.Update</c> 之后）。</summary>
        public static void NotifyTick(LevelEngine lvl)
        {
            try
            {
                if (ReplayPlayer.SuppressRecording)
                    return;
                if (!armed || lvl == null)
                    return;
                if (bound == null || bound.Pointer == IntPtr.Zero)
                {
                    Finish("lost-controller", true);
                    return;
                }
                var engine = bound.GetEngine();
                if (engine == null || engine.Pointer != lvl.Pointer)
                    return; // 不是我们正在录的那个关卡

                int tick = ToTick(lvl.GetLevelTime());
                lastTick = tick;

                // 关键帧兜底重试：StartGame 那一刻 SaveGame() 可能还没法序列化（进度条等），
                // 这里再试几次；期间的事件会被丢弃（它们已在关键帧状态里）。
                if (keyframeState == KeyframeState.Pending)
                {
                    if (tick - startTick <= KeyframeRetryTicks)
                    {
                        TryCaptureKeyframe(bound, lvl);
                    }
                    if (keyframeState == KeyframeState.Pending)
                    {
                        keyframeState = KeyframeState.Failed;
                        Plugin.Log?.LogWarning("[Replay] 关键帧抓取失败，改为只录事件（缺少起始状态）：" + keyframeError);
                    }
                }

                int interval = GetFingerprintInterval();
                if (interval > 0 && tick >= nextFingerprintTick)
                {
                    SampleFingerprint(lvl, tick);
                    nextFingerprintTick = tick + interval;
                }

                // 「关卡里自动写出回放」只在配置打开时才做（默认关，见 GetAutoSave）。
                // 关着的时候文件只在**录制结束**时落盘一次。
                if (ReplayConfig.GetAutoSave() && tick >= nextAutoSaveTick)
                {
                    nextAutoSaveTick = tick + AutoSaveTicks;
                    Write("autosave");
                }

                // 逐 tick 指针轨迹（v9）。只有「这一关存在会读实时指针的内容」时才真的采，
                // 判定的依据是 ReplayCompat 里注册的消费者（原版 4 个 + 第三方注册的）。
                ReplayPointerTrajectory.RecordTick(lvl, tick);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Replay] NotifyTick 出错: " + e);
            }
        }

        /// <summary>关卡控制器被释放（退出关卡 / 重载）。</summary>
        public static void NotifyLevelDispose(LevelController ctrl)
        {
            try
            {
                if (!armed)
                    return;
                if (bound == null || ctrl == null || bound.Pointer != ctrl.Pointer)
                    return;
                Finish("level-dispose", true);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Replay] NotifyLevelDispose 出错: " + e);
            }
        }

        /// <summary>游戏退出。</summary>
        public static void NotifyQuit()
        {
            try
            {
                Finish("quit", true);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Replay] NotifyQuit 出错: " + e);
            }
        }

        /// <summary>手动停止（<c>/replay stop</c>）。</summary>
        public static bool StopManual()
        {
            if (!armed)
                return false;
            Finish("manual-stop", true);
            return true;
        }
        #endregion

        #region 动作记录（由补丁调用）
        private static bool CanRecord()
        {
            // 回放中：注入的动作绝不能再被录一遍（否则会写出一个「自己演给自己看」的新回放）。
            if (ReplayPlayer.SuppressRecording)
                return false;
            // Pending = 关键帧还没抓到 → 动作已经包含在关键帧里，不能重复记。
            return armed && keyframeState != KeyframeState.Pending;
        }

        /// <summary>
        /// 是否是「噪音」交互。
        ///
        /// <c>DoHeldItemPointerEvent</c> 对**每一次**指针移动都会调用（Hover/Enter/Stay/Exit），
        /// 而其中只有 <c>Hold</c>/<c>Streak</c> 会影响仿真（长按拾取、按住给推车蓄力）。
        /// 其余悬浮类事件全部是纯表现 —— 记录它们会让「鼠标划一圈草坪」产生几百条事件。
        /// 参考 <c>PointerInteraction</c>：Hover=0 Enter=1 Stay=2 Exit=3。
        /// </summary>
        private static bool IsNoise(int interaction)
        {
            return interaction >= 0 && interaction <= 3;
        }

        private static string Base(string kind, int tick, int pointerId, int button, int interaction)
        {
            var sb = new StringBuilder(96);
            sb.Append("{\"t\":").Append(Json.Num(tick));
            sb.Append(",\"k\":").Append(Json.Str(kind));
            return sb.ToString();
        }

        private static void Commit(StringBuilder sb)
        {
            sb.Append('}');
            eventLines.Add(sb.ToString());
        }

        /// <summary>
        /// 记录指针位置。
        ///
        /// <para>v2 起必录：<c>HeldItemTarget*</c> 上的 <c>LocalPointerPosition</c> /
        /// <c>ScreenPosition</c> 是**会影响仿真**的 —— 典型是
        /// <c>ToEntityHeldItemBehaviour</c> / <c>EmptyHandEntityHeldItemBehaviour</c> 拿它去
        /// <c>FindPointerTargetEntity</c> 挑目标实体。位置对不上就会打到另一个实体上。</para>
        /// </summary>
        private static void AppendPos(StringBuilder sb, Vector2 screen, Vector3 world)
        {
            sb.Append(",\"sx\":").Append(Json.Num(screen.x));
            sb.Append(",\"sy\":").Append(Json.Num(screen.y));
            sb.Append(",\"wx\":").Append(Json.Num(world.x));
            sb.Append(",\"wy\":").Append(Json.Num(world.y));
            sb.Append(",\"wz\":").Append(Json.Num(world.z));
        }

        /// <summary>
        /// 把「这个事件之后手持蓝图上的两个标记」写进事件行（v8 起）。
        ///
        /// <para><b>为什么必须记：</b><c>instantTrigger</c> 是
        /// 「是否手持触发器」与**玩家选项**「交换触发」的异或，而那个选项
        /// **不在关卡关键帧里、关卡内随手可改**。不记下来的话，录/放两边设置不同
        /// 就会让器械行为整个反过来（详见 <see cref="ReplayHeldFlags"/>）。</para>
        ///
        /// <para>手上不是蓝图（或读不到）时**什么都不写** —— 回放据此区分
        /// 「这个事件不需要干预」与「这个事件的标记是 false」。</para>
        /// </summary>
        private static void AppendHeldFlags(StringBuilder sb, LevelController ctrl)
        {
            var lvl = ctrl != null ? TryEngine(ctrl) : null;
            if (lvl == null)
                return;
            if (!ReplayHeldFlags.TryRead(lvl, out var itg, out var evk))
                return;
            sb.Append(",\"itg\":").Append(Json.Bool(itg));
            sb.Append(",\"evk\":").Append(Json.Bool(evk));
        }

        /// <summary>格子上的手持物指针事件。</summary>
        public static void RecordGrid(LevelController ctrl, int lane, int column, int pointerId, int button, int interaction, Vector2 screen, Vector3 world)
        {
            if (!CanRecord()) return;
            if (IsNoise(interaction)) return;
            int tick = CurrentTick(ctrl);
            var sb = new StringBuilder(192);
            sb.Append(Base("grid", tick, pointerId, button, interaction));
            sb.Append(",\"lane\":").Append(Json.Num(lane));
            sb.Append(",\"col\":").Append(Json.Num(column));
            sb.Append(",\"pid\":").Append(Json.Num(pointerId));
            sb.Append(",\"btn\":").Append(Json.Num(button));
            sb.Append(",\"ia\":").Append(Json.Num(interaction));
            AppendPos(sb, screen, world);
            Commit(sb);
        }

        /// <summary>
        /// 实体上的手持物指针事件。
        ///
        /// <para>除了实体 ID，还记下目标的**定义 ID 与坐标**（v2 后期加入）。
        /// 原因：实体 ID 是关卡内自增的，**任何一处生成时机不同（哪怕只差一两个 tick）
        /// 都会让后续全部 ID 错位**，而那之后 <c>FindEntityByID</c> 要么找不到、
        /// 要么拿到完全不相干的实体。有了定义 ID + 坐标，回放就能按
        /// 「同一种东西里离记录坐标最近的那个」把目标找回来。</para>
        /// </summary>
        /// <summary>
        /// 一次「实体指针事件」的锚点身份。在事件**发生之前**抓取。
        ///
        /// <para>为什么提前抓：镐子会 <c>entity.Die()</c>、拾取物会被 <c>Collect()</c>，
        /// 事件跑完后目标可能已经不在场上了，那时再去读它的 ID / 坐标 / 分层就晚了。</para>
        /// </summary>
        private struct EntityAnchor
        {
            public bool Valid;
            public long Id;
            public string Def;
            public string Layer;
            public Vector3 Pos;

            public static EntityAnchor Empty => default;
        }

        private static EntityAnchor pendingAnchor;

        /// <summary>抓下锚点身份（由 <c>ReplayPatches.EntityPrefix</c> 调用）。</summary>
        public static void CaptureEntityAnchor(EntityController entityCtrl)
        {
            pendingAnchor = EntityAnchor.Empty;
            try
            {
                var ent = entityCtrl != null ? entityCtrl.Entity : null;
                if (ent == null || ent.Pointer == IntPtr.Zero)
                    return;
                LevelEngine lvl = null;
                try { lvl = ent.Level; } catch (Exception) { }
                var a = new EntityAnchor { Valid = true };
                // 定义 ID + 坐标 + 分层：ID 会错位，这三个字段是回放时把目标找回来的索。
                try { a.Id = ent.ID; } catch (Exception) { }
                try { a.Def = Ids.Ns(EngineEntityExt.GetDefinitionID(ent)); } catch (Exception) { }
                try { a.Pos = ent.Position; } catch (Exception) { }
                a.Layer = LayerKeyOf(lvl, ent);
                pendingAnchor = a;
            }
            catch (Exception)
            {
                pendingAnchor = EntityAnchor.Empty;
            }
        }

        #region 格子分层
        /// <summary>格子分层的名字。顺序固定 —— 分层键是要写进回放、跨运行比对的，顺序必须稳定。</summary>
        private static readonly string[] LayerNames = { "carrier", "protector", "main", "tool" };
        private static NamespaceID[] layerIds;
        private static bool layerIdsTried;

        private static NamespaceID LayerId(int index)
        {
            if (!layerIdsTried)
            {
                layerIdsTried = true;
                try
                {
                    string nsp = null;
                    var main = MainManager.Instance;
                    if (main != null && main.Game != null)
                        nsp = main.Game.DefaultNamespace;
                    layerIds = new NamespaceID[LayerNames.Length];
                    for (int i = 0; i < LayerNames.Length; i++)
                        layerIds[i] = NamespaceID.Parse(LayerNames[i], nsp);
                }
                catch (Exception e)
                {
                    layerIds = null;
                    Plugin.Log?.LogWarning("[Replay] 解析格子分层名失败（回放将无法按分层区分同格器械）："
                        + e.GetType().Name + ": " + e.Message);
                }
            }
            if (layerIds == null || index < 0 || index >= layerIds.Length)
                return null;
            return layerIds[index];
        }

        /// <summary>
        /// 实体在它所在格子里占的分层键，形如 <c>main</c> / <c>protector,carrier</c>；
        /// 不在任何分层上（敌人、飞行中的拾取物……）返回空串。失败返回 null。
        ///
        /// <para><b>为什么需要它（镐子挖错格的真正原因）：</b>同一格最多能同时站 4 个可被挖掉的
        /// 器械 —— 承载位 / 保护位 / 主要位 / 工具位 —— 而它们**坐标完全相同**（同一个格子），
        /// 所以「同定义 + 离记录坐标最近」分不出是哪一个。游戏自己恰恰是从**锚点身份**出发判定的：</para>
        /// <code>
        /// // VanillaEntityExt.FindPointerTargetEntity（实体路径 —— 电脑端走的就是这条）
        /// var protectTargets = entity.GetProtectingTargets();      // 只有锚点是保护位才有结果
        /// if (firstValidMainTarget != null &amp;&amp; (!canUseOnProtector || pointerPositionY &gt;= 0.5f))
        ///     return firstValidMainTarget;
        /// return entity;                                           // 否则就是锚点自己
        /// </code>
        /// <para>锚点选错 → <c>GetProtectingTargets()</c> 的结果就完全不同 → 镐子挖到另一株。
        /// 电脑端（本 mod 只做电脑端）锚点来自**鼠标的精确射线**（<c>castRadius &lt;= 0</c>），
        /// 鼠标可以蹭着保护位露在外面的那条边点中它 —— 那条"上下半格"的规则是**移动端**
        /// （胖手指圆投射 → 退化成格子 → <c>VanillaGridExt</c> 那条路径）的东西，别拿它推电脑端。
        /// 详见 <c>ReplayPlayer.ResolveEntity</c> 的注释。</para>
        /// </summary>
        public static string LayerKeyOf(LevelEngine lvl, Entity e)
        {
            if (lvl == null || e == null)
                return null;
            try
            {
                var p = e.Position;
                var grid = lvl.GetGrid(lvl.GetColumn(p.x), lvl.GetLane(p.z));
                if (grid == null || grid.Pointer == IntPtr.Zero)
                    return "";
                var sb = new StringBuilder(24);
                for (int i = 0; i < LayerNames.Length; i++)
                {
                    var id = LayerId(i);
                    if (id == null)
                        continue;
                    bool on;
                    try { on = grid.IsEntityOnLayer(e, id); } catch (Exception) { continue; }
                    if (!on)
                        continue;
                    if (sb.Length > 0)
                        sb.Append(',');
                    sb.Append(LayerNames[i]);
                }
                return sb.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }
        #endregion

        public static void RecordEntity(LevelController ctrl,
            int pointerId, int button, int interaction, Vector2 screen, Vector3 world)
        {
            if (!CanRecord()) return;
            if (IsNoise(interaction)) return;
            int tick = CurrentTick(ctrl);
            var a = pendingAnchor;
            pendingAnchor = EntityAnchor.Empty;
            if (!a.Valid)
                a.Id = 0;
            var sb = new StringBuilder(256);
            sb.Append(Base("entity", tick, pointerId, button, interaction));
            sb.Append(",\"id\":").Append(Json.Num(a.Id));
            if (!string.IsNullOrEmpty(a.Def))
                sb.Append(",\"def\":").Append(Json.Str(a.Def));
            if (a.Layer != null)
                sb.Append(",\"lyr\":").Append(Json.Str(a.Layer));
            sb.Append(",\"ex\":").Append(Json.Num(a.Pos.x));
            sb.Append(",\"ey\":").Append(Json.Num(a.Pos.y));
            sb.Append(",\"ez\":").Append(Json.Num(a.Pos.z));
            sb.Append(",\"pid\":").Append(Json.Num(pointerId));
            sb.Append(",\"btn\":").Append(Json.Num(button));
            sb.Append(",\"ia\":").Append(Json.Num(interaction));
            AppendPos(sb, screen, world);
            // 实体路径也包含「捡起掉在地上的卡」（blueprint_pickup），一样会产生手持蓝图。
            AppendHeldFlags(sb, ctrl);
            Commit(sb);
        }

        /// <summary>点选蓝图（选卡槽）。</summary>
        public static void RecordBlueprint(LevelController ctrl, int index, bool conveyor, int pointerId, int button, int interaction, Vector2 screen, Vector3 world)
        {
            if (!CanRecord()) return;
            if (IsNoise(interaction)) return;
            int tick = CurrentTick(ctrl);
            var sb = new StringBuilder(192);
            sb.Append(Base("blueprint", tick, pointerId, button, interaction));
            sb.Append(",\"idx\":").Append(Json.Num(index));
            sb.Append(",\"cv\":").Append(Json.Bool(conveyor));
            sb.Append(",\"pid\":").Append(Json.Num(pointerId));
            sb.Append(",\"btn\":").Append(Json.Num(button));
            sb.Append(",\"ia\":").Append(Json.Num(interaction));
            AppendPos(sb, screen, world);
            AppendHeldFlags(sb, ctrl);
            Commit(sb);
        }

        /// <summary>蓝图热键。</summary>
        public static void RecordBlueprintKey(LevelController ctrl, int index, bool conveyor, int key)
        {
            if (!CanRecord()) return;
            int tick = CurrentTick(ctrl);
            var sb = new StringBuilder(128);
            sb.Append(Base("bpkey", tick, 0, 0, -1));
            sb.Append(",\"idx\":").Append(Json.Num(index));
            sb.Append(",\"cv\":").Append(Json.Bool(conveyor));
            sb.Append(",\"key\":").Append(Json.Num(key));
            AppendHeldFlags(sb, ctrl);
            Commit(sb);
        }

        /// <summary>草坪区域的手持物指针事件（取消手持物等）。</summary>
        public static void RecordLawn(LevelController ctrl, int area, int pointerId, int button, int interaction, Vector2 screen, Vector3 world)
        {
            if (!CanRecord()) return;
            if (IsNoise(interaction)) return;
            int tick = CurrentTick(ctrl);
            var sb = new StringBuilder(192);
            sb.Append(Base("lawn", tick, pointerId, button, interaction));
            sb.Append(",\"area\":").Append(Json.Num(area));
            sb.Append(",\"pid\":").Append(Json.Num(pointerId));
            sb.Append(",\"btn\":").Append(Json.Num(button));
            sb.Append(",\"ia\":").Append(Json.Num(interaction));
            AppendPos(sb, screen, world);
            Commit(sb);
        }

        /// <summary>
        /// 工具按钮 / 工具热键的点击。
        ///
        /// <para>v1 记的是「点击之后手持物变成了什么」（set/reset），那是个**有损的派生量**：
        /// 看不出玩家点的是哪个工具，也分不清「本来就是这把工具 → 点一下取消」和
        /// 「换了另一把工具」。v2 直接记**点击本身**，回放时调同一个
        /// <c>ClickPickaxe</c> / <c>ClickStarshard</c> / <c>ClickTrigger</c>，
        /// 取消、被挡下、没解锁这些分支全部由游戏自己重新判定，无歧义。</para>
        /// </summary>
        public static void RecordToolClick(LevelController ctrl, string toolName)
        {
            if (!CanRecord()) return;
            int tick = lastTick;
            var lvl = bound != null ? TryEngine(bound) : null;
            if (lvl != null)
                tick = ToTick(lvl.GetLevelTime());

            var sb = new StringBuilder(96);
            sb.Append(Base("tool", tick, 0, 0, -1));
            sb.Append(",\"name\":").Append(Json.Str(toolName));
            Commit(sb);
        }
        #endregion

        #region 内部实现
        private static int ToTick(long tick)
        {
            if (tick < 0) return 0;
            if (tick > int.MaxValue) return int.MaxValue;
            return (int)tick;
        }

        private static int CurrentTick(LevelController ctrl)
        {
            var lvl = TryEngine(ctrl);
            return lvl != null ? ToTick(lvl.GetLevelTime()) : lastTick;
        }

        private static LevelEngine TryEngine(LevelController ctrl)
        {
            try { return ctrl != null ? ctrl.GetEngine() : null; }
            catch (Exception) { return null; }
        }

        private static int GetFingerprintInterval()
        {
            int v = recordOptions != null ? recordOptions.EffectiveInterval : 0;
            if (v > 0)
                return v;
            // 0 = 不采指纹。这里返回 0 会让 `tick >= nextFingerprintTick` 恒真，
            // 所以调用点必须用 AnyFingerprint 先判一次（见 NotifyTick）。
            return 0;
        }

        private static void ResetSession()
        {
            eventLines.Clear();
            fingerprintLines.Clear();
            envLines.Clear();
            ReplayPointerTrajectory.ResetRecording();
            ReplayDialogs.ResetRecording();
            bound = null;
            boundPtr = IntPtr.Zero;
            name = null;
            reason = null;
            armed = false;
            keyframeState = KeyframeState.Pending;
            keyframeBson = null;
            keyframeError = null;
            startTick = 0;
            lastTick = 0;
            nextFingerprintTick = 0;
            nextAutoSaveTick = 0;
            funnelDepth = 0;
            hStage = null;
            hArea = null;
            hDifficulty = null;
            hSeed = 0;
            hScreenW = 0f;
            hScreenH = 0f;
            hMoney = 0;
            hMoneyKnown = false;
            environment = null;
            recordOptions = new ReplayRecordOptions();
        }

        private static bool Begin(string replayName, string armReason, LevelController ctrl)
        {
            if (ctrl == null)
                return false;

            if (armed)
                Finish("restart", true);

            ResetSession();

            // ---- 协议：录制行为**从配置文件**取一次快照；环境也只在这个时刻采一次 ----
            //
            // 这是协议里最关键的一条分工：录出来什么，只看配置；播的时候怎么播，只看文件。
            // 快照而不是每次读配置 → 玩家中途改配置不会让同一个文件内部自相矛盾。
            recordOptions = ReplayRecordOptions.FromConfig();
            environment = ReplayEnvironment.Capture();
            BuildEnvLines();
            if (recordOptions.Debug)
            {
                Plugin.Log?.LogWarning("[Replay] ⚠ 调试模式已开启：本场回放会逐 tick 记录大量指纹数据"
                    + "（仅供回放 mod 开发用）。" + recordOptions.Describe());
            }

            bound = ctrl;
            boundPtr = ctrl.Pointer;
            reason = armReason;
            armed = true;
            keyframeState = KeyframeState.Pending;

            var lvl = TryEngine(ctrl);
            startTick = lvl != null ? ToTick(lvl.GetLevelTime()) : 0;
            lastTick = startTick;
            nextFingerprintTick = startTick + GetFingerprintInterval();
            nextAutoSaveTick = startTick + AutoSaveTicks;

            // 窗口尺寸按「开录那一刻」记：中途改窗口尺寸的话这局回放本来就不一致了，
            // 记最早的至少能保证前半段是对的。
            hScreenW = Screen.width;
            hScreenH = Screen.height;

            // 开录时的金币：回放用它给金币影子播种，好让「钱够不够」这类分支和录制时一致。
            // 读不到就标 unknown，回放退回「按真存档播种」的老行为。
            try
            {
                var save = MVZ2Logic.Global.Saves;
                hMoney = MVZ2Logic.Saves.LogicSaveExt.GetMoney(save);
                hMoneyKnown = true;
            }
            catch (Exception me)
            {
                Plugin.Log?.LogWarning("[Replay] 读不到开录时的金币（回放会按真存档给影子播种）："
                    + me.GetType().Name);
            }

            if (lvl != null)
            {
                hSeed = lvl.Seed;
                hStage = Ids.Ns(lvl.StageID);
                hArea = Ids.Ns(lvl.AreaID);
                hDifficulty = Ids.Ns(lvl.Difficulty);
            }

            name = SanitizeName(string.IsNullOrEmpty(replayName) ? BuildAutoName(lvl) : replayName);

            startedAt = DateTime.Now;
            TryCaptureKeyframe(ctrl, lvl);

            // 关键帧那一刻也采一条指纹。
            // 它让回放能用**同一套算法**精确校验「LoadGame 之后状态真的回到了关键帧那一刻」——
            // 目前只有指纹覆盖的字段（能量/波次/旗数/通关 + 7 条 RNG）能被这样检查，
            // 但这几个恰好就是决定仿真的那几个。
            if (lvl != null && keyframeState == KeyframeState.Ok)
                SampleFingerprint(lvl, startTick);

            Plugin.Log?.LogInfo("[Replay] 开始录制 '" + name + "' (" + reason + ") startTick=" + startTick
                + " stage=" + (hStage ?? "?") + " seed=" + hSeed
                + " keyframe=" + (keyframeState == KeyframeState.Ok ? "OK" : (keyframeState == KeyframeState.Failed ? "FAILED" : "pending")));
            if (keyframeState == KeyframeState.Failed)
                Plugin.Log?.LogWarning("[Replay] 关键帧抓取失败：" + keyframeError);
            return true;
        }

        private static void Finish(string finishReason, bool write)
        {
            if (!armed)
                return;
            armed = false;
            funnelDepth = 0;
            if (write)
                Write(finishReason);
            Plugin.Log?.LogInfo("[Replay] 结束录制 '" + name + "' (" + finishReason + ") 事件=" + eventLines.Count
                + " 指纹=" + fingerprintLines.Count + " 末tick=" + lastTick
                + (lastWrittenPath != null ? " -> " + lastWrittenPath : ""));
            bound = null;
            boundPtr = IntPtr.Zero;
        }

        private static void TryCaptureKeyframe(LevelController ctrl, LevelEngine lvl)
        {
            if (keyframeState == KeyframeState.Ok)
                return;
            try
            {
                // ⚠️ **tick 0 的关键帧是废的**：实测把它交给 LoadGame 会直接返回 false
                // （游戏弹「载入失败」）。原因应该是第 0 tick 之前有些部件
                // （进度条 / 蓝图选择 / 工具 UI …）还没序列化完整。
                // 所以这里直接拒收，等第一个 tick 跑完（levelTime >= 1）再抓 ——
                // NotifyTick 的兜底重试会补上，startTick 也会在那时修正。
                if (lvl != null && lvl.GetLevelTime() < 1)
                    return;

                var seri = ctrl.SaveGame();
                if (seri == null)
                    throw new InvalidOperationException("SaveGame() 返回 null");
                keyframeBson = SerializeHelper.ToBson(seri, false);
                if (string.IsNullOrEmpty(keyframeBson))
                    throw new InvalidOperationException("ToBson() 返回空");
                keyframeState = KeyframeState.Ok;
                if (lvl != null)
                    startTick = ToTick(lvl.GetLevelTime());
            }
            catch (Exception e)
            {
                keyframeError = e.GetType().Name + ": " + e.Message;
                // 保持 Pending，由 NotifyTick 决定何时放弃
            }
        }

        private static ulong Fnv1a(ulong h, int value)
        {
            unchecked
            {
                uint v = (uint)value;
                for (int i = 0; i < 4; i++)
                {
                    h ^= (byte)(v >> (i * 8));
                    h *= 1099511628211UL;
                }
            }
            return h;
        }

        /// <summary>
        /// 7 条 RNG 流的合并哈希（协议里的 <c>r</c>）。
        /// 单独抽出来是为了让采样器与「录制/播放之外」的调用点（自测）用同一套算法。
        /// </summary>
        internal static string RngMergedHash(SerializableLevel rngs)
        {
            if (rngs == null)
                return null;
            ulong h = 14695981039346656037UL;
            h = HashRng(h, rngs.levelRandom);
            h = HashRng(h, rngs.entityRandom);
            h = HashRng(h, rngs.effectRandom);
            h = HashRng(h, rngs.roundRandom);
            h = HashRng(h, rngs.spawnRandom);
            h = HashRng(h, rngs.conveyorRandom);
            h = HashRng(h, rngs.miscRandom);
            return Json.Hex(h);
        }

        /// <summary>
        /// 7 条 RNG 流**各自**从 FNV 初始值起算的哈希（协议里的 <c>r0</c>..<c>r6</c>）。
        ///
        /// <para>合并哈希只能告诉你「有 RNG 跑偏」，分流才能点名是**哪一条**，
        /// 从而把范围缩到用它的那个子系统（spawn = 出怪、round = 波次计数、…）。</para>
        /// </summary>
        internal static string[] RngStreamHashes(SerializableLevel rngs)
        {
            if (rngs == null)
                return null;
            const ulong offset = 14695981039346656037UL;
            var streams = new string[ReplayFingerprint.RngStreamNames.Length];
            streams[0] = Json.Hex(HashRng(offset, rngs.levelRandom));
            streams[1] = Json.Hex(HashRng(offset, rngs.entityRandom));
            streams[2] = Json.Hex(HashRng(offset, rngs.effectRandom));
            streams[3] = Json.Hex(HashRng(offset, rngs.roundRandom));
            streams[4] = Json.Hex(HashRng(offset, rngs.spawnRandom));
            streams[5] = Json.Hex(HashRng(offset, rngs.conveyorRandom));
            streams[6] = Json.Hex(HashRng(offset, rngs.miscRandom));
            return streams;
        }

        private static ulong HashRng(ulong h, SerializableRNG rng)
        {
            if (rng == null)
                return Fnv1a(h, 0x5EED);
            h = Fnv1a(h, rng.x);
            h = Fnv1a(h, rng.y);
            h = Fnv1a(h, rng.z);
            h = Fnv1a(h, rng.w);
            return h;
        }

        private static ulong Fnv1aLong(ulong h, long value)
        {
            unchecked
            {
                ulong u = (ulong)value;
                for (int i = 0; i < 8; i++)
                {
                    h ^= (byte)(u >> (i * 8));
                    h *= 1099511628211UL;
                }
            }
            return h;
        }

        private static ulong Fnv1aStr(ulong h, string s)
        {
            if (string.IsNullOrEmpty(s))
                return Fnv1a(h, 0xDEAD);
            unchecked
            {
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= (byte)(s[i] & 0xFF);
                    h *= 1099511628211UL;
                    h ^= (byte)((s[i] >> 8) & 0xFF);
                    h *= 1099511628211UL;
                }
            }
            return h;
        }

        /// <summary>
        /// 枚举关卡里所有存活实体。
        ///
        /// <para>用引擎自己的 <c>LevelEngine.GetEntities()</c>（传空过滤表 = 全部）。
        /// 之前这里是用 <c>FindEntityByID</c> 从 1 逐个探测的 —— 那只是因为没找到这个接口，
        /// 代价高且需要猜上界，现已换成正经枚举。</para>
        /// </summary>
        public static Entity[] EnumerateEntities(LevelEngine lvl)
        {
            if (lvl == null)
                return null;
            try
            {
                return lvl.GetEntities(EmptyTypes);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 枚举实体失败：" + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> emptyTypes;
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> EmptyTypes
        {
            get
            {
                if (emptyTypes == null)
                    emptyTypes = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(0);
                return emptyTypes;
            }
        }

        /// <summary>读一个实体的定义 ID（interop 里扩展方法是普通静态方法）。</summary>
        public static string DefIdOf(Entity e)
        {
            try { return e != null ? Ids.Ns(EngineEntityExt.GetDefinitionID(e)) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 实体集合指纹：所有存活实体的 <c>(id, 定义ID)</c> 按 id 排序后的哈希，加实体数量。
        ///
        /// <para><b>为什么要单独算它：</b>「实体 ID 错位」（同一个 id 在两边指向不同的东西）
        /// 是回放不复现的头号原因（详见 DESIGN §4.5.1）。只比 RNG/能量的指纹只能告诉你
        /// 「已经不一致」，而这一项能告诉你**从哪一个 tick 开始实体集合就不一样了**，
        /// 也就把原因锁定到那一个 tick 附近的具体动作上。</para>
        ///
        /// <para>按 id 排序后再哈希：引擎的枚举顺序是按位置比较器排的，
        /// 位置一变顺序就变，不排序会引入噪声。</para>
        /// </summary>
        internal static void HashEntities(LevelEngine lvl, ref ulong h, out int count, out string defHistogram)
        {
            count = 0;
            defHistogram = null;
            h = Fnv1aStr(h, "ents");
            var all = EnumerateEntities(lvl);
            if (all == null)
                return;
            var pairs = new List<KeyValuePair<long, string>>(all.Length);
            var defCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < all.Length; i++)
            {
                var e = all[i];
                if (e == null || e.Pointer == IntPtr.Zero)
                    continue;
                long id;
                try { id = e.ID; } catch (Exception) { continue; }
                var def = DefIdOf(e);
                pairs.Add(new KeyValuePair<long, string>(id, def));
                if (!string.IsNullOrEmpty(def))
                {
                    defCounts.TryGetValue(def, out var c);
                    defCounts[def] = c + 1;
                }
            }
            pairs.Sort((a, b) => a.Key.CompareTo(b.Key));
            count = pairs.Count;
            for (int i = 0; i < pairs.Count; i++)
            {
                h = Fnv1aLong(h, pairs[i].Key);
                h = Fnv1aStr(h, pairs[i].Value);
            }

            // 直方图：按定义名排序后拼成 "def=n,def=n"，方便肉眼/工具直接对比。
            if (defCounts.Count > 0)
            {
                var keys = new List<string>(defCounts.Keys);
                keys.Sort(StringComparer.Ordinal);
                var sb = new StringBuilder(keys.Count * 20);
                for (int i = 0; i < keys.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    sb.Append(keys[i]).Append('=').Append(defCounts[keys[i]]);
                }
                defHistogram = sb.ToString();
            }
            else
            {
                defHistogram = "";
            }
        }

        private static void SampleFingerprint(LevelEngine lvl, int tick)
        {
            try
            {
                var fp = ComputeFingerprint(lvl, tick, out _, recordOptions);
                if (fp == null)
                    return;
                fingerprintLines.Add(FormatFingerprintLine(fp));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 采样指纹失败: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 把一条指纹拼成协议行（<c>k:"fp"</c>）。
        ///
        /// <para><b>写什么由指纹自己说了算</b>，不再在这里判配置：
        /// 某个字段有没有被采样器填上，就是它该不该出现在文件里的唯一依据。
        /// 这样「录制行为 = 配置（经采样器）」与「文件内容 = 事实」不会被搅在一起，
        /// 读者也只需要按「有就比、没有就跳过」处理。</para>
        /// </summary>
        internal static string FormatFingerprintLine(ReplayFingerprint fp)
        {
            var sb = new StringBuilder(192);
            sb.Append("{\"t\":").Append(Json.Num(fp.Tick));
            sb.Append(",\"k\":").Append(Json.Str(ReplayProtocol.KindFingerprint));
            sb.Append(",\"en\":").Append(Json.Num(fp.Enemies));
            sb.Append(",\"w\":").Append(Json.Num(fp.Wave));
            sb.Append(",\"fl\":").Append(Json.Num(fp.Flags));
            sb.Append(",\"cl\":").Append(Json.Bool(fp.Cleared));
            if (fp.EntityCount >= 0)
                sb.Append(",\"ec\":").Append(Json.Num(fp.EntityCount));
            if (!string.IsNullOrEmpty(fp.EntityHash))
                sb.Append(",\"eh\":").Append(Json.Str(fp.EntityHash));
            if (!string.IsNullOrEmpty(fp.EntityDefs))
                sb.Append(",\"ed\":").Append(Json.Str(fp.EntityDefs));
            if (!string.IsNullOrEmpty(fp.Rng))
                sb.Append(",\"r\":").Append(Json.Str(fp.Rng));
            if (fp.RngStreams != null)
            {
                // 7 条 RNG 流各自的哈希（字符串值，FlatJson 能直接读；**不要**写成嵌套数组）。
                for (int i = 0; i < fp.RngStreams.Length; i++)
                {
                    if (string.IsNullOrEmpty(fp.RngStreams[i]))
                        continue;
                    sb.Append(",\"r").Append(i).Append("\":").Append(Json.Str(fp.RngStreams[i]));
                }
            }
            // 附加字段（调试字段 / 第三方采样器字段）。
            for (int i = 0; i < fp.Extra.Count; i++)
            {
                var kv = fp.Extra[i];
                if (string.IsNullOrEmpty(kv.Key))
                    continue;
                sb.Append(',').Append(Json.Str(kv.Key)).Append(':').Append(Json.Str(kv.Value));
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 算一次状态指纹。**录制与回放核对共用同一份实现**，否则两边算法一跑偏，
        /// 「一致」就变成了自证清白的废话。失败返回 null。
        ///
        /// <para>实现本身只是「跑一遍已注册的采样器」（见 <see cref="ReplaySamplers"/>）——
        /// 于是「加一种新指纹」不需要改录制与播放的任何一行代码。</para>
        /// </summary>
        public static ReplayFingerprint ComputeFingerprint(LevelEngine lvl, int tick, out SerializableLevel rngs)
        {
            return ComputeFingerprint(lvl, tick, out rngs, null);
        }

        /// <summary>同上，可指定策略（调试采样器要不要跑由 <see cref="ReplayRecordOptions.Debug"/> 决定）。</summary>
        public static ReplayFingerprint ComputeFingerprint(LevelEngine lvl, int tick,
            out SerializableLevel rngs, ReplayRecordOptions options)
        {
            rngs = null;
            if (lvl == null)
                return null;
            try
            {
                var opts = options ?? new ReplayRecordOptions();
                var fp = new ReplayFingerprint { Tick = tick };
                var ctx = new ReplaySamplerContext
                {
                    Level = lvl,
                    Tick = tick,
                    Options = opts,
                    Fp = fp,
                };

                var samplers = ReplaySamplers.Active(opts.Debug);
                for (int i = 0; i < samplers.Length; i++)
                {
                    try
                    {
                        samplers[i].Sample(ctx);
                    }
                    catch (Exception se)
                    {
                        // 单个采样器坏掉不该让整条指纹消失（否则会出现「这条指纹缺字段」的假象）。
                        Plugin.Log?.LogWarning("[Replay] 采样器 " + samplers[i].Name + " 出错："
                            + se.GetType().Name + ": " + se.Message);
                    }
                }

                rngs = ctx.RngSnapshot();
                return fp;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 算指纹失败: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        private static string BuildAutoName(LevelEngine lvl)
        {
            string stage = hStage;
            if (string.IsNullOrEmpty(stage) && lvl != null)
                stage = Ids.Ns(lvl.StageID);
            if (string.IsNullOrEmpty(stage))
                stage = "level";
            stage = stage.Replace(':', '_').Replace('/', '_');
            return stage + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// header 里的 <c>created</c>：**录制开始**的时刻（本地时间，ISO 8601 "s" 格式）。
        ///
        /// <para>⚠️ 不能写 <c>DateTime.Now</c>。很久以前就是这么写的，而回放是在
        /// **离开关卡时**才写盘的 —— 于是一局 16 分钟的回放，<c>created</c> 会比文件名的
        /// 时间戳晚 16 分钟（实测：文件名 <c>..._225128</c>、created <c>23:05:14</c>）。
        /// 「录制时刻」是这条回放的一个客观属性，跟什么时候写盘无关。</para>
        /// </summary>
        private static string CreatedStamp()
        {
            var t = startedAt == default(DateTime) ? DateTime.Now : startedAt;
            return t.ToString("s", CultureInfo.InvariantCulture);
        }

        private static DateTime startedAt;

        private static string SanitizeName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "replay_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ' ')
                    sb.Append(c == ' ' ? '_' : c);
            }
            return sb.Length > 0 ? sb.ToString() : "replay";
        }

        /// <summary>
        /// 游戏自己的版本串（<c>UnityEngine.Application.version</c>，就是标题界面显示的那个），
        /// 用于 <c>.mrep</c> 头的 <c>game</c> 字段。
        ///
        /// <para><b>刻意不写死。</b>这一栏的全部意义就是「这份回放是在哪个游戏版本上录的」——
        /// 写死就得每次游戏更新手动改一遍，漏改的新回放会顶着旧版本号，比不写更误导。
        /// 直接问游戏则永远跟着走。</para>
        ///
        /// <para>取到就缓存：<see cref="BuildHeader"/> 也会在 <c>application-quit</c> 路径上被调用，
        /// 那时 il2cpp 正在拆除，不该再去发起原生调用。</para>
        /// </summary>
        private static string GameVersion()
        {
            if (gameVersion != null)
                return gameVersion;
            string v = null;
            try { v = Application.version; }
            catch (Exception) { }
            return gameVersion = string.IsNullOrEmpty(v) ? "unknown" : v;
        }

        /// <summary>
        /// 把环境快照拼成 <c>env</c> 行：**一行摘要**（游戏/引擎/平台/tps/哈希+条目数），
        /// 之后每个条目一行。
        ///
        /// <para>为什么摘要单独一行而不是塞进 header：header 是「读取时立刻要用的索引」，
        /// 越长越难读；而环境条目数随玩家装的 mod 变化、没有上界，塞进去会让 header 无限膨胀。
        /// 分开以后，只要 header 就能打开回放库列表，要细节时再读这几行。</para>
        /// </summary>
        private static void BuildEnvLines()
        {
            envLines.Clear();
            var env = environment;
            if (env == null)
                return;

            var head = new StringBuilder(160);
            head.Append("{\"k\":").Append(Json.Str(ReplayProtocol.KindEnv));
            head.Append(",\"t\":\"summary\"");
            head.Append(",\"game\":").Append(Json.Str(env.GameVersion));
            head.Append(",\"unity\":").Append(Json.Str(env.UnityVersion));
            head.Append(",\"plat\":").Append(Json.Str(env.Platform));
            head.Append(",\"tps\":").Append(Json.Num(env.Tps));
            head.Append(",\"hash\":").Append(Json.Str(env.Hash));
            head.Append(",\"n\":").Append(Json.Num(env.Entries.Count));
            head.Append('}');
            envLines.Add(head.ToString());

            for (int i = 0; i < env.Entries.Count; i++)
            {
                var e = env.Entries[i];
                var sb = new StringBuilder(128);
                sb.Append("{\"k\":").Append(Json.Str(ReplayProtocol.KindEnv));
                sb.Append(",\"kind\":").Append(Json.Str(e.Kind));
                sb.Append(",\"id\":").Append(Json.Str(e.Id));
                if (!string.IsNullOrEmpty(e.Name))
                    sb.Append(",\"name\":").Append(Json.Str(e.Name));
                sb.Append(",\"ver\":").Append(Json.Str(e.Version));
                if (e.DataVersion >= 0)
                    sb.Append(",\"dv\":").Append(Json.Num(e.DataVersion));
                if (!string.IsNullOrEmpty(e.Note))
                    sb.Append(",\"note\":").Append(Json.Str(e.Note));
                if (!e.Hashable)
                    sb.Append(",\"h\":false");
                sb.Append('}');
                envLines.Add(sb.ToString());
            }
        }

        private static string BuildHeader(string endReason)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\"magic\":").Append(Json.Str(ReplayProtocol.Magic));
            // spec = 规范文本的版本；ver = 文件结构的版本。读者只按 ver 决定能不能读。
            sb.Append(",\"spec\":").Append(Json.Num(ReplayProtocol.SpecVersion));
            sb.Append(",\"ver\":").Append(Json.Num(FormatVersion));
            sb.Append(",\"mod\":").Append(Json.Str(Plugin.VERSION));
            sb.Append(",\"game\":").Append(Json.Str(GameVersion()));
            sb.Append(",\"tps\":30");
            sb.Append(",\"created\":").Append(Json.Str(CreatedStamp()));
            sb.Append(",\"end\":").Append(Json.Str(endReason ?? reason));

            // ---- 环境摘要（明细在 env 行）----
            var env = environment;
            sb.Append(",\"unity\":").Append(Json.Str(env != null ? env.UnityVersion : "unknown"));
            sb.Append(",\"plat\":").Append(Json.Str(env != null ? env.Platform : "unknown"));
            sb.Append(",\"envHash\":").Append(Json.Str(env != null ? env.Hash : ""));
            sb.Append(",\"envCount\":").Append(Json.Num(env != null ? env.Entries.Count : 0));

            sb.Append(",\"stage\":").Append(Json.Str(hStage));
            sb.Append(",\"area\":").Append(Json.Str(hArea));
            sb.Append(",\"difficulty\":").Append(Json.Str(hDifficulty));
            sb.Append(",\"seed\":").Append(Json.Num(hSeed));
            sb.Append(",\"sw\":").Append(Json.Num(hScreenW));
            sb.Append(",\"sh\":").Append(Json.Num(hScreenH));
            if (hMoneyKnown)
                sb.Append(",\"mn\":").Append(Json.Num(hMoney));
            sb.Append(",\"startTick\":").Append(Json.Num(startTick));
            sb.Append(",\"lastTick\":").Append(Json.Num(lastTick));
            sb.Append(",\"hasKeyframe\":").Append(Json.Bool(keyframeState == KeyframeState.Ok));
            if (keyframeState != KeyframeState.Ok && !string.IsNullOrEmpty(keyframeError))
                sb.Append(",\"keyframeError\":").Append(Json.Str(keyframeError));
            sb.Append(",\"eventCount\":").Append(Json.Num(eventLines.Count));
            sb.Append(",\"fingerprintCount\":").Append(Json.Num(fingerprintLines.Count));
            sb.Append(",\"ptrCount\":").Append(Json.Num(ReplayPointerTrajectory.SampleCount));
            sb.Append(",\"dlgCount\":").Append(Json.Num(ReplayDialogs.RecordedCount));

            // ---- 协议 v10：这份文件是**照哪一套参数**录的（播放策略全部从这里读）----
            var opts = recordOptions ?? new ReplayRecordOptions();
            sb.Append(",\"fpt\":").Append(Json.Num(opts.EffectiveInterval));
            sb.Append(",\"debug\":").Append(Json.Bool(opts.Debug));
            sb.Append(",\"caps\":").Append(Json.Str(opts.Caps()));
            sb.Append(",\"samplers\":").Append(Json.Str(opts.SamplerNames()));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>把当前的录制内容写盘（可重复调用，会整体重写）。</summary>
        private static void Write(string endReason)
        {
            try
            {
                bool discard = false;
                try { discard = ReplayConfig.DiscardEmpty != null && ReplayConfig.DiscardEmpty.Value; }
                catch (Exception) { }
                if (discard && eventLines.Count == 0)
                    return;

                var text = BuildText(endReason);
                var dir = ReplayConfig.ResolveOutputDir();
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, name + ".mrep");

                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
                {
                    writer.Write(text);
                }

                lastWrittenPath = path;

                // 明文副本：由**本次录制的策略**决定（不是现读配置 —— 否则中途改配置
                // 会让「同一个文件」的落盘形态前后不一）。
                bool plain = recordOptions != null && recordOptions.WritePlainText;
                if (plain)
                {
                    var plainPath = Path.Combine(dir, name + ".mrep.json");
                    File.WriteAllText(plainPath, text, new UTF8Encoding(false));
                }

                Plugin.Log?.LogInfo("[Replay] 已写出回放: " + path + " (" + text.Length + " 字符, 事件 " + eventLines.Count + ")");

                // 按上限清理最旧的（MaxReplays，默认 20）。放在写出**之后**：
                // 万一新文件因为某种原因没写成功，也不该先把别人的删掉。
                try { ReplayLibrary.Prune(); }
                catch (Exception pe) { Plugin.Log?.LogWarning("[Replay] 清理旧回放出错: " + pe.GetType().Name); }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Replay] 写回放文件失败: " + e);
            }
        }

        private static string BuildText(string endReason)
        {
            var sb = new StringBuilder(64 * 1024 + eventLines.Count * 96 + fingerprintLines.Count * 128);
            sb.Append(BuildHeader(endReason)).Append('\n');

            // 环境（协议 v10）：摘要 + 每个条目一行。**必须紧跟在 header 之后、关键帧之前** ——
            // 读者会在「还没解析关键帧」的时候就想拿到环境（例如回放库列表要先显示版本警示）。
            for (int i = 0; i < envLines.Count; i++)
                sb.Append(envLines[i]).Append('\n');

            // 关键帧（可能为空）
            sb.Append("{\"k\":").Append(Json.Str(ReplayProtocol.KindKeyframe))
              .Append(",\"tick\":").Append(Json.Num(startTick));
            if (keyframeState == KeyframeState.Ok && !string.IsNullOrEmpty(keyframeBson))
                sb.Append(",\"state\":").Append(Json.Str(keyframeBson));
            else
                sb.Append(",\"state\":null,\"error\":").Append(Json.Str(keyframeError));
            sb.Append("}\n");

            for (int i = 0; i < eventLines.Count; i++)
                sb.Append(eventLines[i]).Append('\n');
            for (int i = 0; i < fingerprintLines.Count; i++)
                sb.Append(fingerprintLines[i]).Append('\n');

            // v9 的两类「非动作事件」：逐 tick 指针轨迹 + 选项对话框。
            // 它们不进 Events（不参与按 tick 派发），所以放在最后 —— 顺序对它们无意义，
            // 但「事件行必须按 tick 升序」这个前提必须保持（否则回放的注入游标会漏掉后面的行）。
            var extra = new List<string>();
            ReplayPointerTrajectory.AppendRecordLines(extra);
            ReplayDialogs.AppendRecordLines(extra);
            for (int i = 0; i < extra.Count; i++)
                sb.Append(extra[i]).Append('\n');

            return sb.ToString();
        }
        #endregion
    }
}
