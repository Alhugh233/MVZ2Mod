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

        /// <summary>自动落盘间隔（tick）。600 tick = 20 秒。</summary>
        private const int AutoSaveTicks = 600;

        /// <summary>
        /// 文件格式版本。
        ///   1 = 早期草案（无指针位置，工具点击记成派生的 set/reset）
        ///   2 = 指针事件带位置、工具点击记成 tool
        ///   3 = 实体事件额外带 def（定义 ID）与 ex/ey/ez（记时坐标）
        /// </summary>
        private const int FormatVersion = 6;

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

        private static string lastWrittenPath;
        #endregion

        #region 对外状态
        public static bool IsArmed => armed;
        public static string LastWrittenPath => lastWrittenPath;
        public static string CurrentName => name;

        /// <summary>已记录的动作事件数（自测与排障用）。</summary>
        public static int EventCount => eventLines.Count;

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

                if (tick >= nextAutoSaveTick)
                {
                    nextAutoSaveTick = tick + AutoSaveTicks;
                    Write("autosave");
                }
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
            try { return ReplayConfig.FingerprintInterval != null ? ReplayConfig.FingerprintInterval.Value : 30; }
            catch (Exception) { return 30; }
        }

        private static void ResetSession()
        {
            eventLines.Clear();
            fingerprintLines.Clear();
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
        }

        private static bool Begin(string replayName, string armReason, LevelController ctrl)
        {
            if (ctrl == null)
                return false;

            if (armed)
                Finish("restart", true);

            ResetSession();
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

            if (lvl != null)
            {
                hSeed = lvl.Seed;
                hStage = Ids.Ns(lvl.StageID);
                hArea = Ids.Ns(lvl.AreaID);
                hDifficulty = Ids.Ns(lvl.Difficulty);
            }

            name = SanitizeName(string.IsNullOrEmpty(replayName) ? BuildAutoName(lvl) : replayName);

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
        private static void HashEntities(LevelEngine lvl, ref ulong h, out int count, out string defHistogram)
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
                var fp = ComputeFingerprint(lvl, tick, out _);
                if (fp == null)
                    return;
                var sb = new StringBuilder(160);
                sb.Append("{\"t\":").Append(Json.Num(fp.Tick));
                sb.Append(",\"k\":\"fp\"");
                sb.Append(",\"en\":").Append(Json.Num(fp.Enemies));
                sb.Append(",\"w\":").Append(Json.Num(fp.Wave));
                sb.Append(",\"fl\":").Append(Json.Num(fp.Flags));
                sb.Append(",\"cl\":").Append(Json.Bool(fp.Cleared));
                sb.Append(",\"ec\":").Append(Json.Num(fp.EntityCount));
                sb.Append(",\"eh\":").Append(Json.Str(fp.EntityHash));
                sb.Append(",\"r\":").Append(Json.Str(fp.Rng));

                if (ReplayConfig.FullFingerprint != null && ReplayConfig.FullFingerprint.Value)
                {
                    // 实体定义直方图：不一致时能直接看出「少了哪一类实体」。
                    sb.Append(",\"ed\":").Append(Json.Str(fp.EntityDefs));
                    // 7 条 RNG 流各自的哈希（字符串值，FlatJson 能直接读；不要写成嵌套数组）。
                    if (fp.RngStreams != null)
                    {
                        for (int i = 0; i < fp.RngStreams.Length; i++)
                            sb.Append(",\"r").Append(i).Append("\":").Append(Json.Str(fp.RngStreams[i]));
                    }
                }

                sb.Append('}');
                fingerprintLines.Add(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Replay] 采样指纹失败: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 算一次状态指纹。**录制与回放核对共用同一份实现**，否则两边算法一跑偏，
        /// 「一致」就变成了自证清白的废话。失败返回 null。
        /// </summary>
        public static ReplayFingerprint ComputeFingerprint(LevelEngine lvl, int tick, out SerializableLevel rngs)
        {
            rngs = null;
            try
            {
                var serializable = new SerializableLevel();
                lvl.WriteRandomToSerializable(serializable);
                rngs = serializable;

                ulong h = 14695981039346656037UL;
                h = HashRng(h, serializable.levelRandom);
                h = HashRng(h, serializable.entityRandom);
                h = HashRng(h, serializable.effectRandom);
                h = HashRng(h, serializable.roundRandom);
                h = HashRng(h, serializable.spawnRandom);
                h = HashRng(h, serializable.conveyorRandom);
                h = HashRng(h, serializable.miscRandom);

                // 再加一条「每条流各自从 FNV 初始值起算」的哈希。
                // 合并哈希只能告诉你「有 RNG 跑偏」，分开存才能点名是**哪一条** ——
                // level/entity/effect/round/spawn/conveyor/misc 对应不同的子系统，
                // 一下就能把范围缩小。
                const ulong offset = 14695981039346656037UL;
                var streams = new string[7];
                streams[0] = Json.Hex(HashRng(offset, serializable.levelRandom));
                streams[1] = Json.Hex(HashRng(offset, serializable.entityRandom));
                streams[2] = Json.Hex(HashRng(offset, serializable.effectRandom));
                streams[3] = Json.Hex(HashRng(offset, serializable.roundRandom));
                streams[4] = Json.Hex(HashRng(offset, serializable.spawnRandom));
                streams[5] = Json.Hex(HashRng(offset, serializable.conveyorRandom));
                streams[6] = Json.Hex(HashRng(offset, serializable.miscRandom));

                ulong eh = 14695981039346656037UL;
                HashEntities(lvl, ref eh, out var entityCount, out var entityDefs);

                return new ReplayFingerprint
                {
                    Tick = tick,
                    Enemies = (int)lvl.Energy,
                    Wave = lvl.CurrentWave,
                    Flags = lvl.CurrentFlag,
                    Cleared = lvl.IsCleared,
                    EntityCount = entityCount,
                    EntityHash = Json.Hex(eh),
                    EntityDefs = entityDefs,
                    RngStreams = streams,
                    Rng = Json.Hex(h),
                };
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

        private static string BuildHeader(string endReason)
        {
            var sb = new StringBuilder(384);
            sb.Append("{\"magic\":\"mrep\"");
            sb.Append(",\"ver\":").Append(Json.Num(FormatVersion));
            sb.Append(",\"mod\":").Append(Json.Str(Plugin.VERSION));
            sb.Append(",\"game\":\"0.7.0 test-9\"");
            sb.Append(",\"tps\":30");
            sb.Append(",\"created\":").Append(Json.Str(DateTime.Now.ToString("s", CultureInfo.InvariantCulture)));
            sb.Append(",\"end\":").Append(Json.Str(endReason ?? reason));
            sb.Append(",\"stage\":").Append(Json.Str(hStage));
            sb.Append(",\"area\":").Append(Json.Str(hArea));
            sb.Append(",\"difficulty\":").Append(Json.Str(hDifficulty));
            sb.Append(",\"seed\":").Append(Json.Num(hSeed));
            sb.Append(",\"startTick\":").Append(Json.Num(startTick));
            sb.Append(",\"lastTick\":").Append(Json.Num(lastTick));
            sb.Append(",\"hasKeyframe\":").Append(Json.Bool(keyframeState == KeyframeState.Ok));
            if (keyframeState != KeyframeState.Ok && !string.IsNullOrEmpty(keyframeError))
                sb.Append(",\"keyframeError\":").Append(Json.Str(keyframeError));
            sb.Append(",\"eventCount\":").Append(Json.Num(eventLines.Count));
            sb.Append(",\"fingerprintCount\":").Append(Json.Num(fingerprintLines.Count));
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

                bool plain = false;
                try { plain = ReplayConfig.WritePlainText != null && ReplayConfig.WritePlainText.Value; }
                catch (Exception) { }
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

            // 关键帧（可能为空）
            sb.Append("{\"k\":\"keyframe\",\"tick\":").Append(Json.Num(startTick));
            if (keyframeState == KeyframeState.Ok && !string.IsNullOrEmpty(keyframeBson))
                sb.Append(",\"state\":").Append(Json.Str(keyframeBson));
            else
                sb.Append(",\"state\":null,\"error\":").Append(Json.Str(keyframeError));
            sb.Append("}\n");

            for (int i = 0; i < eventLines.Count; i++)
                sb.Append(eventLines[i]).Append('\n');
            for (int i = 0; i < fingerprintLines.Count; i++)
                sb.Append(fingerprintLines[i]).Append('\n');

            return sb.ToString();
        }
        #endregion
    }
}
