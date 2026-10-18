using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using MVZ2.GameContent.Stages;
using MVZ2.Level;
using MVZ2.Level.Blueprints;
using PVZEngine.Level;
using UnityEngine;

namespace Replay
{
    /// <summary>
    /// 传送带蓝图控制器的**守卫 + 逐步骤探针**。
    ///
    /// <para><b>为什么要它：</b>播放回放时游戏会原生访问冲突（<c>GameAssembly.dll</c>
    /// <c>0xC0000005</c>，<c>BepInEx\ErrorLog.log</c> 里什么都没有 ⇒ 不是托管异常）。
    /// 用 Il2CppDumper 的 <c>dump.cs</c> 把 Windows 事件日志里的错误偏移
    /// <c>0x01818E5B</c> 反查回去，落在
    /// <c>MVZ2.Level.Blueprints.ConveyorBlueprintController.UpdateFixed()</c>
    /// （RVA <c>0x1818DF0</c>，偏移 <c>+0x6B</c>）里。</para>
    ///
    /// <para><b>为什么正好是「第 11 天」这类关卡：</b>它们挂 <c>ConveyorStageBehaviour</c>，
    /// 而 <c>CONVEYOR_INTERVAL = 120</c> —— 传送带**每 120 tick 刷一次**。
    /// 玩家报的「119 tick 闪退」就是**第一次刷新**：那一刻
    /// <c>ConveyRandomSeedPack()</c> 造出第一个 <c>ConveyorBlueprintController</c>，
    /// 紧接着的 <c>UpdateLogic</c> 就会调它的 <c>UpdateFixed()</c>。</para>
    ///
    /// <para><b>这个方法体内只有四处解引用</b>，探针按顺序给每一处打标记，
    /// 于是「最后一条标记是什么」就等价于「崩在哪一句」：
    /// <list type="number">
    /// <item><c>base.UpdateFixed()</c> → <c>ui.Model</c> → <c>model.UpdateFixed()</c>；</item>
    /// <item><c>Controller.BlueprintController</c>（接口取部件）；</item>
    /// <item><c>GetConveyorSpeed()</c>（上面那个部件上的字段）；</item>
    /// <item><c>GetConveyorBlueprintController(index - 1)</c> 与 <c>prevSeed.Position</c>。</item>
    /// </list>
    /// 另外探针**自己把这几句重写一遍**（原实现直接跳过）：
    /// 它一旦跑通，崩溃也就没了；哪一步在先崩，日志里一目了然。</para>
    ///
    /// <para>⚠️ 惰性安装（第一次播放时才挂），与 <see cref="ReplaySaveGuard"/> 同理：
    /// 在 <c>Plugin.Load()</c> 阶段反射游戏类型有触发 <c>.cctor</c> 的前科。</para>
    /// </summary>
    internal static class ReplayConveyorGuard
    {
        /// <summary>安装结果（写进播放小结，挂失败必须显式说出来）。</summary>
        public static string Status { get; private set; } = "未安装";

        private static bool installed;
        private static int lines;

        public static void Ensure()
        {
            if (installed)
                return;
            installed = true;

            int ok = 0, fail = 0;
            ReplayHooks.PatchIn(typeof(ConveyorBlueprintController), "UpdateFixed", Type.EmptyTypes,
                typeof(ReplayConveyorGuard), nameof(UpdateFixedPrefix), null, ref ok, ref fail);
            ReplayHooks.PatchIn(typeof(ConveyorBlueprintController), "UpdateFrame", new[] { typeof(float) },
                typeof(ReplayConveyorGuard), nameof(UpdateFramePrefix), null, ref ok, ref fail);

            // 刷新的整条链路都挂上里程碑：崩之前最后一条标记就是现场。
            // ① 关卡行为（每 120 tick 抽一张）
            Step(typeof(MVZ2.GameContent.Stages.ConveyorStageBehaviour), "Update",
                nameof(Step1), nameof(Step1x), ref ok, ref fail);
            // ② 抽卡（卡池 → 权重随机 → 放上传送带）
            Step(typeof(PVZEngine.Level.LevelEngine), "ConveyRandomSeedPack",
                nameof(Step2), nameof(Step2x), ref ok, ref fail);
            Step(typeof(PVZEngine.Level.LevelEngine), "DrawConveyorSeed",
                nameof(Step3), nameof(Step3x), ref ok, ref fail);
            Step(typeof(PVZEngine.Level.LevelEngine), "RefillConveyorPool",
                nameof(Step4), nameof(Step4x), ref ok, ref fail);
            Step(typeof(PVZEngine.Level.LevelEngine), "AddConveyorSeedPack",
                nameof(Step5), nameof(Step5x), ref ok, ref fail);
            // ③ 控制器 + UI（克隆蓝图、Init、模型）
            Step(typeof(LevelBlueprintController), "CreateConveyorSeedController",
                nameof(Step6), nameof(Step6x), ref ok, ref fail);
            Step(typeof(LevelBlueprintController), "Engine_OnConveyorSeedPackAddedCallback",
                nameof(Step7), nameof(Step7x), ref ok, ref fail);
            Step(typeof(ConveyorBlueprintController), "Init",
                nameof(Step8), nameof(Step8x), ref ok, ref fail);
            // ④ UI 相位（刷新之后的每一帧）—— 最后两个嫌疑人
            Step(typeof(Conveyor), "SetBlueprintNormalizedPosition",
                nameof(Step9), nameof(Step9x), ref ok, ref fail);
            Step(typeof(ConveyorBlueprintController), "UpdateFrame",
                nameof(Step10), nameof(Step10x), ref ok, ref fail);

            Status = "已挂 " + ok + " 个" + (fail > 0 ? "，❌ 失败 " + fail + " 个" : string.Empty);
            Plugin.Log?.LogInfo("[Replay] 传送带守卫：" + Status);
            Mark("=== 守卫安装 " + Status + " ===");
        }

        // ------------------------------------------------------------------
        // 里程碑探针：进入 / 离开各打一行
        // ------------------------------------------------------------------

        /// <summary>挂一对「进入/离开」前缀后缀。名字找不到也只会记进失败计数（Harmony 不抛）。</summary>
        private static void Step(Type type, string method, string prefix, string postfix, ref int ok, ref int fail)
        {
            ReplayHooks.PatchIn(type, method, null, typeof(ReplayConveyorGuard), prefix, postfix, ref ok, ref fail);
        }
        public static void Step1() { E(1); }
        public static void Step1x() { X(1); }
        public static void Step2() { E(2); }
        public static void Step2x() { X(2); }
        public static void Step3() { E(3); }
        public static void Step3x() { X(3); }
        public static void Step4() { E(4); }
        public static void Step4x() { X(4); }
        public static void Step5() { E(5); }
        public static void Step5x() { X(5); }
        public static void Step6() { E(6); }
        public static void Step6x() { X(6); }
        public static void Step7() { E(7); }
        public static void Step7x() { X(7); }
        public static void Step8() { E(8); }
        public static void Step8x() { X(8); }

        /// <summary>⑨ 这一步带参数：崩溃时最想知道的就是 index 与 slotCount 对不对得上。</summary>
        public static void Step9(int index, float position)
        {
            if (NearCrash())
                Mark("→ ⑨SetBlueprintNormalizedPosition idx=" + index
                    + " pos=" + position.ToString("0.###"));
        }
        public static void Step9x() { X(9); }
        public static void Step10(float deltaTime) { E(10); }
        public static void Step10x() { X(10); }

        private static readonly string[] StepNames =
        {
            "",
            "①ConveyorStageBehaviour.Update",
            "②ConveyRandomSeedPack",
            "③DrawConveyorSeed",
            "④RefillConveyorPool",
            "⑤AddConveyorSeedPack",
            "⑥CreateConveyorSeedController",
            "⑦OnConveyorSeedPackAdded",
            "⑧Conveyor.Init",
            "⑨SetBlueprintNormalizedPosition",
            "⑩Conveyor.UpdateFrame",
        };

        private static void E(int i)
        {
            if (NearCrash())
                Mark("→ " + StepNames[i]);
        }

        private static void X(int i)
        {
            if (NearCrash())
                Mark("← " + StepNames[i]);
        }

        /// <summary>只在「第一次刷新」附近打标记（不这么限就每帧刷屏）。</summary>
        private static bool NearCrash()
        {
            try
            {
                var t = ReplayPlayer.CurrentTick;
                return t >= 100 && t <= 140;
            }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------
        // 输出：**自己写文件 + 逐行 flush**
        // ------------------------------------------------------------------

        /// <summary>
        /// <para><b>⚠️ 为什么不能只靠 <c>Plugin.Log</c>：</b>BepInEx 的磁盘日志监听器
        /// 默认 <c>InstantFlushing = false</c>（<c>StreamWriter.AutoFlush</c> 同样是假），
        /// 而原生访问冲突是**整个进程直接消失**，来不及冲缓冲 ——
        /// 最后约 4KB（≈40 行）日志会整段丢失。
        /// 结果就是「日志尾巴永远停在同一个地方」，看上去像崩在那里，其实不是。</para>
        ///
        /// <para>所以探针自己拿一个 <c>AutoFlush = true</c> 的写入器，
        /// 每一行落盘之后才继续 —— 崩溃前最后那行就是真正的现场。</para>
        /// </summary>
        private static System.IO.StreamWriter writer;

        private static string LogPath()
        {
            try { return System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "BepInEx", "_replay_conveyor.txt"); }
            catch (Exception) { return null; }
        }

        private static void WriteLine(string line)
        {
            try
            {
                if (writer == null)
                {
                    var p = LogPath();
                    if (p == null)
                        return;
                    writer = new System.IO.StreamWriter(p, true, new System.Text.UTF8Encoding(false));
                    writer.AutoFlush = true;
                    writer.WriteLine("===== " + DateTime.Now.ToString("HH:mm:ss") + " 新的一次播放 =====");
                }
                writer.WriteLine(line);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 每个**逻辑 tick** 心跳一次（由 <c>ReplayPlayer.AfterTick</c> 调）。
        /// 它的唯一目的是回答「到底死在哪个 tick」—— 传送带那条线如果一次都没进，
        /// 心跳的最后一行也能把范围缩到具体 tick。
        ///
        /// <para>发布构建里这是个空方法（<c>DIAGNOSTICS</c> 未定义）：它是排查工具，
        /// 留着会让每个 tick 都多一次配置读取 + 判断。调用点照旧保留，
        /// 这样「诊断构建」与「发布构建」的调用位置完全一致，不会因为要排查而改变代码结构。</para>
        /// </summary>
        public static void Beat(int tick, string note)
        {
#if DIAGNOSTICS
            if (!ReplayConfig.ConveyorGuardLogOn || tick > 400)
                return;
            WriteLine("beat t=" + tick + " " + note);
#endif
        }

        // ------------------------------------------------------------------
        // UpdateFixed：自己实现，逐步骤打标记
        // ------------------------------------------------------------------

        /// <summary>
        /// 原实现（照抄 0.7.0 源码，用于对照）：
        /// <code>
        /// public override void UpdateFixed()
        /// {
        ///     base.UpdateFixed();                                  // model.UpdateFixed()
        ///     var index = Index;
        ///     Position -= Controller.BlueprintController.GetConveyorSpeed() / 45f;
        ///     float minPosition = 0;
        ///     if (index > 0)
        ///     {
        ///         var prevSeed = Controller.BlueprintController.GetConveyorBlueprintController(index - 1);
        ///         if (prevSeed != null) minPosition = prevSeed.Position + 1;
        ///     }
        ///     Position = Mathf.Max(Position, minPosition);
        /// }
        /// </code>
        /// </summary>
        public static bool UpdateFixedPrefix(ConveyorBlueprintController __instance)
        {
            if (!ReplayConfig.ConveyorGuardOn)
                return true; // 守卫关着 → 走原实现
            if (__instance == null)
                return false;

            var watch = Watching();
            try
            {
                var index = __instance.Index;
                if (watch)
                {
                    Mark("fixed#1 进入 idx=" + index
                        + " ctrl=" + Ptr(__instance.Controller)
                        + " ui=" + Ptr(__instance.GetBlueprintUI())
                        + " seed=" + Ptr(__instance.SeedPack));
                }

                // ---- ① base.UpdateFixed()：ui.Model + model.UpdateFixed()
                var model = __instance.GetModel();
                if (watch)
                    Mark("fixed#2 model=" + Ptr(model));
                if (model != null)
                    model.UpdateFixed();
                if (watch)
                    Mark("fixed#3 base 完成");

                // ---- ② 取部件（原实现这里是接口调用，最可能的崩溃点）
                var ctrl = __instance.Controller;
                var lc = ctrl != null ? ctrl.TryCast<LevelController>() : null;
                var part = lc != null ? lc.BlueprintController : null;
                if (watch)
                    Mark("fixed#4 ctrl=" + Ptr(ctrl) + " lc=" + Ptr(lc) + " part=" + Ptr(part));

                if (lc == null || part == null)
                {
                    Mark("fixed!! Controller/部件为空 → 跳过（否则这里就是原生访问冲突）"
                        + " idx=" + index + " ctrl=" + Ptr(ctrl));
                    return false;
                }

                // ---- ③ 速度
                var speed = part.GetConveyorSpeed();
                if (watch)
                    Mark("fixed#5 speed=" + speed.ToString("0.###"));
                __instance.Position = __instance.Position - speed / 45f;

                // ---- ④ 前一个蓝图（只在 index > 0 时才碰）
                float minPosition = 0f;
                if (index > 0)
                {
                    var prevSeed = part.GetConveyorBlueprintController(index - 1);
                    if (watch)
                        Mark("fixed#6 prev=" + Ptr(prevSeed));
                    if (prevSeed != null)
                        minPosition = prevSeed.Position + 1f;
                }
                __instance.Position = Mathf.Max(__instance.Position, minPosition);
                if (watch)
                    Mark("fixed#7 完成 pos=" + __instance.Position.ToString("0.###"));
            }
            catch (Exception e)
            {
                Mark("fixed!! 托管异常 " + e.GetType().Name + "：" + e.Message);
            }

            return false; // 自己已经做完了
        }

        // ------------------------------------------------------------------
        // UpdateFrame：同一批解引用，只做「为空就跳过」的保护
        // ------------------------------------------------------------------

        /// <summary>
        /// 原实现会依次碰 <c>ui</c>、<c>Controller.GetTwinkleAlpha()</c>、
        /// <c>Level</c>（= <c>Controller.GetEngine()</c>）、
        /// <c>Controller.BlueprintController</c> —— 与 <c>UpdateFixed</c> 是同一批解引用。
        /// 这里只在**取不到控制器**时跳过，其余一律放行（不要改变正常行为）。
        /// </summary>
        public static bool UpdateFramePrefix(ConveyorBlueprintController __instance)
        {
            if (!ReplayConfig.ConveyorGuardOn)
                return true;
            if (__instance == null)
                return false;

            try
            {
                var ctrl = __instance.Controller;
                var lc = ctrl != null ? ctrl.TryCast<LevelController>() : null;
                var part = lc != null ? lc.BlueprintController : null;
                if (lc == null || part == null)
                {
                    Mark("frame!! Controller/部件为空 → 跳过 idx=" + __instance.Index
                        + " ctrl=" + Ptr(ctrl));
                    return false;
                }
            }
            catch (Exception e)
            {
                Mark("frame!! 托管异常 " + e.GetType().Name + "：" + e.Message);
            }
            return true;
        }

        // ------------------------------------------------------------------
        // 工具
        // ------------------------------------------------------------------

        /// <summary>要不要打标记：只在播放中、且没超出上限时打。</summary>
        private static bool Watching()
        {
            if (!ReplayConfig.ConveyorGuardLogOn || lines >= 4000)
                return false;
            try { return ReplayPlayer.IsActive; }
            catch (Exception) { return false; }
        }

        private static void Mark(string text)
        {
            lines++;
            int tick = 0;
            try { tick = ReplayPlayer.CurrentTick; } catch (Exception) { }
            var line = "t=" + tick + " " + text;
            Plugin.Log?.LogInfo("[Replay][传送带探针] " + line);
            WriteLine(line);
        }

        private static string Ptr(object o)
        {
            if (o == null)
                return "null";
            var b = o as Il2CppObjectBase;
            if (b == null)
                return o.GetType().Name;
            try
            {
                var p = b.Pointer;
                return p == IntPtr.Zero ? "0" : "0x" + p.ToInt64().ToString("X");
            }
            catch (Exception)
            {
                return "?";
            }
        }
    }
}
