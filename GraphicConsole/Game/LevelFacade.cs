using System;
using System.Collections;
using System.Collections.Generic;
using MVZ2.GameContent.Buffs;
using MVZ2.Managers;
using MVZ2Logic;
using MVZ2Logic.Difficulties;
using MVZ2Logic.Level;
using PVZEngine;
using PVZEngine.Buffs;
using PVZEngine.Difficulties;
using PVZEngine.Level;

namespace GraphicConsole
{
    /// <summary>
    /// 关卡内页面用的游戏 API 薄封装。
    ///
    /// 三条硬规则（都是为了"最大兼容性"）：
    ///   1. **只写 level 级属性**。<c>StageDefinition</c> / <c>AreaDefinition</c> 是全局共享对象，
    ///      往它们身上写会污染所有使用该关卡的场合（其它存档、无尽模式、街机）。
    ///      level 的属性回退链是「自身 → Stage → Area」，写自身既能盖住、又不外溢。
    ///   2. **绝不碰全局 options**。<c>LogicOptionExt.SetDifficulty</c> 之类会写进全局配置文件、
    ///      跨用户跨存档生效 —— 面板一律不用。
    ///   3. 四个作弊开关直接复用**原版已有的 debug buff**（`/cheat` 用的就是它们），
    ///      不新增任何定义，于是完全避开 metadata v31 下注入类的各种坑。
    ///
    /// 关于"面板改的东西会不会存进 .lvl"：**会**，而且这是有意的（前作存档修改器就是靠改 lvl
    /// 实现的）。「重载」走的是「保存关卡 → 重新加载」，所以进度保留；想清干净就用重开。
    /// </summary>
    internal static class LevelFacade
    {
        public static MainManager Main { get { return MainManager.Instance; } }

        public static LevelEngine Current
        {
            get
            {
                try
                {
                    var global = Global.Level;
                    return global == null ? null : global.GetLevel();
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public static bool InLevel { get { return Current != null; } }

        public static MVZ2.Level.LevelController Controller
        {
            get
            {
                try
                {
                    var main = Main;
                    if (main == null || main.LevelManager == null)
                        return null;
                    return main.LevelManager.GetLevelController();
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        #region 原版 debug buff
        // 这些 ID 直接取自 MVZ2.GameContent.Buffs.VanillaBuffID（编译期常量 = mvz2:debug_xxx），
        // 不是自己拼的字符串，避免改名后静默失效。
        public static NamespaceID BuffGodMode { get { return VanillaBuffID.Level.debugGodmode; } }
        public static NamespaceID BuffEnergy { get { return VanillaBuffID.Level.debugEnergy; } }
        public static NamespaceID BuffStarshard { get { return VanillaBuffID.Level.debugStarshard; } }
        public static NamespaceID BuffNoRecharge { get { return VanillaBuffID.Level.debugNoRecharge; } }

        /// <summary>
        /// interop 的代理类不声明它实现的接口，所以不能把 LevelEngine 直接传给参数类型是
        /// <c>IBuffTarget</c> 的扩展方法 —— 必须按指针手工包一个壳。
        /// </summary>
        private static IBuffTarget AsBuffTarget(LevelEngine level)
        {
            return new IBuffTarget(level.Pointer);
        }

        public static bool HasBuff(LevelEngine level, NamespaceID id)
        {
            try
            {
                return BuffTargetExt.HasBuff(AsBuffTarget(level), id);
            }
            catch (Exception e)
            {
                LogOnce("hasbuff", "读 buff 失败：" + e.Message);
                return false;
            }
        }

        public static void SetBuff(LevelEngine level, NamespaceID id, bool on)
        {
            try
            {
                if (on)
                    BuffTargetExt.AddBuff(AsBuffTarget(level), id);
                else
                    BuffTargetExt.RemoveBuffs(AsBuffTarget(level), id);
            }
            catch (Exception e)
            {
                LogOnce("setbuff-" + (id == null ? "?" : id.Path), "开关 buff 失败：" + e.Message);
            }
        }
        #endregion

        #region 简单开关
        public static bool GetGodMode(LevelEngine l) { return Bool(l, "godmode", x => x.IsGodMode()); }
        public static void SetGodMode(LevelEngine l, bool v) { Act(l, "godmode", x => x.SetGodMode(v)); }

        public static bool GetPauseDisabled(LevelEngine l) { return Bool(l, "pause", x => x.IsPauseDisabled()); }

        /// <summary>
        /// 禁用暂停。
        ///
        /// ⚠️ 光设属性是不够的：那只是"让游戏以后不再允许暂停"，
        /// 而如果**此刻正停着**（例如面板打开导致的暂停），游戏不会自己恢复 ——
        /// 这就是"禁用暂停时没有立即使游戏恢复运行"的原因。
        /// 所以设完之后立刻主动恢复一次（ResumeGame 是 public，且内部会校验 pauseLevel）。
        /// </summary>
        public static void SetPauseDisabled(LevelEngine l, bool v)
        {
            Act(l, "pause", x => x.SetPauseDisabled(v));
            if (!v)
                return;
            var controller = Controller;
            if (controller == null)
                return;
            try
            {
                if (controller.IsGamePaused())
                {
                    var resumed = controller.ResumeGame(0);
                    Plugin.Log?.LogInfo("[Level] 禁用暂停：主动恢复关卡运行 = " + resumed);
                }
            }
            catch (Exception e)
            {
                LogOnce("resume", "恢复关卡运行失败：" + e.Message);
            }
        }

        public static bool GetIgnoreHugeWave(LevelEngine l) { return Bool(l, "hugewave", x => x.IgnoreHugeWaveEvent()); }
        public static void SetIgnoreHugeWave(LevelEngine l, bool v) { Act(l, "hugewave", x => x.SetIgnoreHugeWaveEvent(v)); }

        /// <summary>
        /// 自动收集：原版只给了 getter（<c>LogicStageProps.IsAutoCollectAll</c>），没有 setter，
        /// 所以直接写属性。<c>PropertyMeta&lt;T&gt; → PropertyKey&lt;T&gt;</c> 在 interop 里保留了
        /// 隐式转换（op_Implicit），可以这么写。
        /// </summary>
        public static bool GetAutoCollect(LevelEngine l)
        {
            try
            {
                // 显式指定泛型参数：泛型推断不会应用用户定义的转换，不写会推不出来。
                return l.GetProperty<bool>(LogicStageProps.AUTO_COLLECT_ALL);
            }
            catch (Exception e)
            {
                LogOnce("autocollect", "读自动收集失败：" + e.Message);
                return false;
            }
        }

        public static void SetAutoCollect(LevelEngine l, bool v)
        {
            try
            {
                l.SetProperty(LogicStageProps.AUTO_COLLECT_ALL, v);
            }
            catch (Exception e)
            {
                LogOnce("autocollect", "写自动收集失败：" + e.Message);
            }
        }
        #endregion

        #region 数值
        public static int GetCurrentFlag(LevelEngine l) { return Int(l, "flag", x => x.CurrentFlag); }
        public static void SetCurrentFlag(LevelEngine l, int v)
        {
            Act(l, "flag", x =>
            {
                x.CurrentFlag = v;
                x.UpdateLevelName();
            });
        }

        public static int GetCurrentWave(LevelEngine l) { return Int(l, "wave", x => x.CurrentWave); }
        public static void SetCurrentWave(LevelEngine l, int v) { Act(l, "wave", x => x.CurrentWave = v); }

        public static int GetMaxEnergy(LevelEngine l) { return (int)Float(l, "maxenergy", x => x.GetMaxEnergy()); }
        public static void SetMaxEnergy(LevelEngine l, int v) { Act(l, "maxenergy", x => x.SetMaxEnergy(v)); }

        public static int GetStarshardSlots(LevelEngine l) { return Int(l, "starshardslots", x => x.GetStarshardSlotCount()); }
        public static void SetStarshardSlots(LevelEngine l, int v)
        {
            Act(l, "starshardslots", x =>
            {
                x.SetStarshardSlotCount(v);
                // 上限调小后当前值可能超了，夹一下（原版 SetStarshardCount 自带 clamp）。
                x.SetStarshardCount(x.GetStarshardCount());
            });
        }

        public static float GetSpawnPointAddition(LevelEngine l) { return Float(l, "spawnadd", x => x.GetSpawnPointAddition()); }
        /// <summary>
        /// ⚠️ 只能写 **level 实例**。原版给这三个属性提供的 setter 是
        /// <c>SetSpawnPointXxx(this StageDefinition, float)</c> —— 往 StageDefinition（全局共享对象）上写，
        /// 会污染所有使用该关卡的场合（其它存档、无尽模式、街机）。
        /// level 的属性回退链是「自身 → Stage → Area」，写自身既盖得住又不外溢。
        /// </summary>
        public static void SetSpawnPointAddition(LevelEngine l, float v) { Act(l, "spawnadd", x => x.SetProperty(LogicLevelProps.SPAWN_POINTS_ADDITION, v)); }

        public static float GetSpawnPointMultiplier(LevelEngine l) { return Float(l, "spawnmul", x => x.GetSpawnPointMultiplier()); }
        public static void SetSpawnPointMultiplier(LevelEngine l, float v) { Act(l, "spawnmul", x => x.SetProperty(LogicLevelProps.SPAWN_POINTS_MUTLIPLIER, v)); }

        public static float GetSpawnPointPower(LevelEngine l) { return Float(l, "spawnpow", x => x.GetSpawnPointPower()); }
        public static void SetSpawnPointPower(LevelEngine l, float v) { Act(l, "spawnpow", x => x.SetProperty(LogicLevelProps.SPAWN_POINTS_POWER, v)); }

        /// <summary>
        /// 出怪点数的硬上限。原版 <c>LogicLevelExt.CalculateSpawnPoints</c> 里
        /// <c>maxPoints = 500</c> 是写死的，所以三个参数怎么调都会被顶在 500。
        /// </summary>
        public const float SpawnPointHardCap = 500f;
        #endregion

        #region 背景 / 关卡 / 难度 / 音乐
        public static NamespaceID GetAreaID(LevelEngine l)
        {
            try
            {
                return l == null ? null : l.AreaID;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static NamespaceID GetStageID(LevelEngine l)
        {
            try
            {
                return l == null ? null : l.StageID;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static NamespaceID GetDifficulty(LevelEngine l)
        {
            try
            {
                return l == null ? null : l.Difficulty;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static NamespaceID GetMusicID(LevelEngine l)
        {
            try
            {
                return l == null ? null : l.GetMusicID();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 切换关卡音乐。
        ///
        /// ⚠️ <c>level.SetMusicID(id)</c> **只写属性，不播放** —— 实测改完音乐毫无反应。
        /// 原版真正的播放口是 <c>LevelController.CurrentMusic</c> 的 setter
        /// （内部走 <c>Music.Play(id)</c> / <c>Music.Stop()</c>，无效 id 时自动停）。
        /// 所以这里写完属性后要再叫一次控制器。
        /// </summary>
        public static void SetMusicID(LevelEngine l, NamespaceID id)
        {
            Act(l, "music", x => x.SetMusicID(id));
            if (!NamespaceID.IsValid(id))
                return;
            var controller = Controller;
            if (controller == null)
                return;
            try
            {
                controller.CurrentMusic = id;
                Plugin.Log?.LogInfo("[Level] 音乐 → " + id.ToString() + "（已立即播放）");
            }
            catch (Exception e)
            {
                LogOnce("music-play", "立即播放音乐失败：" + e.Message);
            }
        }

        public static List<NamespaceID> ListAreas()
        {
            return ListDefinitions(l => ContentProviderHelper.GetAllAreaDefinitions(l.Content));
        }

        public static List<NamespaceID> ListStages()
        {
            return ListDefinitions(l => ContentProviderHelper.GetAllStageDefinitions(l.Content));
        }

        public static List<NamespaceID> ListDifficulties()
        {
            return ListDefinitions(l => ContentProviderHelper.GetAllDifficultyDefinitions(l.Content));
        }

        public static List<NamespaceID> ListMusics()
        {
            var result = new List<NamespaceID>();
            try
            {
                var main = Main;
                if (main == null || main.ResourceManager == null)
                    return result;
                var ids = main.ResourceManager.GetAllMusicID();
                if (ids == null)
                    return result;
                for (var i = 0; i < ids.Length; i++)
                {
                    var id = ids[i];
                    if (NamespaceID.IsValid(id) && !Contains(result, id))
                        result.Add(id);
                }
            }
            catch (Exception e)
            {
                LogOnce("listmusic", "列音乐失败：" + e.Message);
            }
            return result;
        }

        /// <summary>下拉框标签：统一「名称 (id)」格式，名称走游戏自己的本地化访问器。</summary>
        public static List<string> LabelsFor(List<NamespaceID> ids, Func<NamespaceID, string> nameOf)
        {
            var labels = new List<string>();
            if (ids == null)
                return labels;
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                labels.Add(NameResolver.ForDropdown(nameOf(id), id));
            }
            return labels;
        }

        private static List<NamespaceID> ListDefinitions(Func<LevelEngine, PVZEngine.Definition[]> getter)
        {
            var result = new List<NamespaceID>();
            try
            {
                var level = Current;
                if (level == null || level.Content == null)
                    return result;
                var defs = getter(level);
                if (defs == null)
                    return result;
                for (var i = 0; i < defs.Length; i++)
                {
                    var def = defs[i];
                    if (def == null)
                        continue;
                    var id = def.GetID();
                    if (NamespaceID.IsValid(id) && !Contains(result, id))
                        result.Add(id);
                }
            }
            catch (Exception e)
            {
                LogOnce("listdefs", "列定义失败：" + e.Message);
            }
            return result;
        }

        /// <summary>
        /// 换难度：先摘掉旧难度 buff，再设新难度 + 挂新难度 buff。
        ///
        /// 为什么不能只 <c>level.SetDifficulty</c>：关卡的难度效果是**关卡级 buff** 实现的
        /// （<c>DifficultyGlobalCallbacks</c> 在 PRE_BATTLE / POST_LEVEL_START 时按难度挂/摘 buff），
        /// 那个函数是私有的、且需要一个 <c>LevelCallbackParams</c>，调不到。
        /// 于是这里按同样的规则自己换一次 buff —— 用原版自己的
        /// <c>LogicDifficultyProps.GetBuffID / GetIZombieBuffID</c>，不自造 buff。
        /// </summary>
        public static bool ApplyDifficulty(LevelEngine level, NamespaceID difficulty)
        {
            if (level == null || !NamespaceID.IsValid(difficulty))
                return false;
            try
            {
                var oldID = level.Difficulty;
                if (SameId(oldID, difficulty))
                    return true;

                var isIZombie = level.IsIZombie();
                var oldBuff = DifficultyBuffID(level, oldID, isIZombie);
                if (NamespaceID.IsValid(oldBuff))
                    BuffTargetExt.RemoveBuffs(AsBuffTarget(level), oldBuff);

                level.SetDifficulty(difficulty);
                var newBuff = DifficultyBuffID(level, difficulty, isIZombie);
                if (NamespaceID.IsValid(newBuff))
                    BuffTargetExt.AddBuff(AsBuffTarget(level), newBuff);

                Plugin.Log?.LogInfo("[Level] 难度 → " + difficulty.ToString()
                    + "（buff: " + (NamespaceID.IsValid(oldBuff) ? oldBuff.ToString() : "无")
                    + " → " + (NamespaceID.IsValid(newBuff) ? newBuff.ToString() : "无") + "）");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Level] 切换难度失败：" + e);
                return false;
            }
        }

        private static NamespaceID DifficultyBuffID(LevelEngine level, NamespaceID difficulty, bool isIZombie)
        {
            if (!NamespaceID.IsValid(difficulty))
                return null;
            try
            {
                var def = ContentProviderHelper.GetDifficultyDefinition(level.Content, difficulty);
                if (def == null)
                    return null;
                return isIZombie ? def.GetIZombieBuffID() : def.GetBuffID();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 重载关卡 —— **先保存当前关卡数据，再重新载入这个关卡**。
        ///
        /// 完全走官方那条路：<c>LevelController.ReloadLevel()</c>，它的实现就是
        /// <code>
        /// Saves.SaveToFile();                 // 保持用户存档
        /// Dispose();                          // 拆掉当前关卡
        /// await LevelManager.GotoLevelSceneAsync();
        /// LevelManager.InitLevel(startAreaID, startStageID, exitTarget: exitTarget);
        /// </code>
        /// 而 <c>LevelManager.InitLevel</c> 的关键分支是 <c>HasLevelState(stageID)</c>：
        /// 有状态存档 → <c>LoadLevel</c>（**进度保留**）；没有 → 全新关卡。
        ///
        /// ⚠️ 而状态存档的路径是
        /// <c>&lt;用户存档&gt;/&lt;stage.Space&gt;/level/&lt;stage.Path&gt;.lvl</c>
        /// —— **文件名由 stageID 决定**。所以：
        ///   · 只改「背景」→ stage 没变 → 文件名不变 → 存档能命中 → **进度保留、背景即时换掉**；
        ///   · 改「关卡」→ 文件名变了 → 那个文件不存在 → 按新关卡从头开始。
        ///     这是原版数据模型决定的（不是实现偷懒），日志里会写明走了哪条路。
        ///
        /// 顺序很重要：
        ///   ① <c>LevelManager.SaveLevel()</c> 必须**在 SetStartStage 之前** ——
        ///      它按 <c>GetStartStageID()</c> 决定写哪个文件，先写才能把当前进度
        ///      落到**原关卡**的文件里（改了关卡时原关卡的进度也不会丢）。
        ///   ② <c>SetStartStage(target)</c> 必须在 <c>ReloadLevel()</c> 之前 ——
        ///      官方重载读的是 <c>startAreaID / startStageID</c>，那才决定了"进哪一关、改哪个文件"。
        /// </summary>
        public static bool ReloadLevel(NamespaceID area, NamespaceID stage)
        {
            var controller = Controller;
            var levelManager = Main == null ? null : Main.LevelManager;
            var level = Current;
            if (controller == null || levelManager == null || level == null)
                return false;

            try
            {
                var curArea = GetAreaID(level);
                var curStage = GetStageID(level);
                var targetArea = NamespaceID.IsValid(area)
                    ? area
                    : (NamespaceID.IsValid(curArea) ? curArea : controller.GetStartAreaID());
                var targetStage = NamespaceID.IsValid(stage) ? stage : curStage;
                var stageChanged = !SameId(targetStage, curStage);

                // ① 先保存当前关卡状态（写进**当前** startStageID 对应的文件）
                try
                {
                    levelManager.SaveLevel();
                    Plugin.Log?.LogInfo("[Level] 重载：已保存当前关卡状态（startStage="
                        + Text(controller.GetStartStageID()) + "）");
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning("[Level] 重载前保存关卡失败（继续重载）：" + e.Message);
                }

                // ② 应用「背景 / 关卡」选择
                controller.SetStartStage(targetArea, targetStage);
                Plugin.Log?.LogInfo("[Level] 重载：目标 = " + Text(targetArea) + " / " + Text(targetStage)
                    + (stageChanged ? "（关卡 id 变了 → 状态存档文件名对不上，将从头开始）"
                                    : "（关卡未变 → 会命中状态存档，进度保留）"));

                // ③ 交给官方重载（内部会 Dispose + 回关卡场景 + InitLevel）
                return BeginOfficialReload(controller, "面板重载");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Level] 重载关卡失败：" + e);
                return false;
            }
        }

        private static string Text(NamespaceID id)
        {
            return NamespaceID.IsValid(id) ? id.ToString() : "（无）";
        }

        // ---- 关卡切换（帧驱动，见 BeginLevelSwitch 的说明）----
        private static Il2CppSystem.Threading.Tasks.Task switchTask;
        private static NamespaceID switchInitArea;
        private static NamespaceID switchInitStage;
        /// <summary>true = 我们自己调 InitLevel；false = Task 内部自己会调（官方 ReloadLevel）。</summary>
        private static bool switchInitsLevel;
        private static string switchLabel = string.Empty;
        private static int switchFrames;

        /// <summary>是否正在切换关卡。</summary>
        public static bool SwitchingLevel { get { return switchTask != null; } }

        /// <summary>
        /// 开始一次「切场景 → 重新初始化关卡」。
        ///
        /// ⚠️⚠️ 这里**绝不能**写成 <c>async void</c> + <c>await task</c>。踩过的坑：
        /// 在 Harmony 补丁里启动的 async 方法，<c>SynchronizationContext.Current</c> 为 null，
        /// 于是 await 的续体被丢到**线程池线程**上跑，续体里的 <c>LevelManager.InitLevel</c>
        /// 从非主线程进 il2cpp → <c>Fatal error 0xC0000005</c> 直接崩游戏。
        /// 官方自己也是这么规避的：<c>MainmenuController.GotoDebugStage</c> 里
        /// <c>while (!task.IsCompleted) yield return null;</c> 在协程里轮询 Task。
        /// 我们在 <see cref="TickPending"/>（由面板心跳每帧调用、必在主线程）里做同一件事。
        /// </summary>
        public static bool BeginLevelSwitch(NamespaceID area, NamespaceID stage, string label)
        {
            var main = Main;
            if (main == null || main.LevelManager == null)
                return false;
            if (switchTask != null)
            {
                Plugin.Log?.LogWarning("[Level] 上一次关卡切换还没结束，忽略新的请求（" + label + "）");
                return false;
            }
            try
            {
                switchInitArea = area;
                switchInitStage = stage;
                switchInitsLevel = true;
                switchLabel = label;
                switchFrames = 0;
                switchTask = main.LevelManager.GotoLevelSceneAsync();
                Plugin.Log?.LogInfo("[Level] 开始切换关卡（" + label + "）→ " + Text(area) + " / " + Text(stage));
                return true;
            }
            catch (Exception e)
            {
                switchTask = null;
                Plugin.Log?.LogError("[Level] 切换关卡失败：" + e);
                return false;
            }
        }

        /// <summary>
        /// 接管官方的 <c>LevelController.ReloadLevel()</c>（它自己会 Dispose + 回关卡场景 +
        /// <c>InitLevel(startAreaID, startStageID)</c>），我们只在主线程轮询它什么时候结束。
        /// 避免自己重写一遍流程 —— 官方那条路才带 <c>Saves.SaveToFile()</c> 与正确的 exitTarget。
        /// </summary>
        public static bool BeginOfficialReload(MVZ2.Level.LevelController controller, string label)
        {
            if (controller == null)
                return false;
            if (switchTask != null)
            {
                Plugin.Log?.LogWarning("[Level] 上一次关卡切换还没结束，忽略新的请求（" + label + "）");
                return false;
            }
            try
            {
                switchInitsLevel = false;
                switchLabel = label;
                switchFrames = 0;
                switchTask = controller.ReloadLevel();
                Plugin.Log?.LogInfo("[Level] 已请求官方重载（" + label + "）");
                return true;
            }
            catch (Exception e)
            {
                switchTask = null;
                Plugin.Log?.LogError("[Level] 请求官方重载失败：" + e);
                return false;
            }
        }

        /// <summary>每帧在主线程轮询关卡切换 Task（见 <see cref="BeginLevelSwitch"/> 的说明）。</summary>
        public static void TickPending()
        {
            var task = switchTask;
            if (task == null)
                return;
            switchFrames++;
            try
            {
                if (!task.IsCompleted)
                {
                    // 关卡加载一般 1~3 秒；超过 30 秒肯定是卡住了，放弃并解锁，免得永远不能再切。
                    if (switchFrames > 1800)
                    {
                        switchTask = null;
                        Plugin.Log?.LogError("[Level] 关卡切换超时（" + switchLabel + "），已放弃");
                    }
                    return;
                }
                switchTask = null;
                if (switchInitsLevel)
                {
                    // 自测进关卡这条路：Task 只切了场景，InitLevel 要我们自己叫。
                    var main = Main;
                    if (main == null || main.LevelManager == null)
                        return;
                    if (NamespaceID.IsValid(switchInitStage))
                        main.LevelManager.InitLevel(switchInitArea, switchInitStage);
                }
                Plugin.Log?.LogInfo("[Level] 关卡切换完成（" + switchLabel + "）→ " + switchFrames + " 帧");
            }
            catch (Exception e)
            {
                switchTask = null;
                Plugin.Log?.LogError("[Level] 关卡切换收尾失败：" + e);
            }
        }
        #endregion

        #region 工具
        private static readonly HashSet<string> loggedKeys = new HashSet<string>();

        private static void LogOnce(string key, string message)
        {
            if (loggedKeys.Add(key))
                Plugin.Log?.LogWarning("[Level] " + message);
        }

        public static bool SameId(NamespaceID a, NamespaceID b)
        {
            if (a == null || b == null)
                return a == null && b == null;
            return a.SpaceName == b.SpaceName && a.Path == b.Path;
        }

        public static bool Contains(List<NamespaceID> list, NamespaceID id)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (SameId(list[i], id))
                    return true;
            }
            return false;
        }

        public static int IndexOf(List<NamespaceID> list, NamespaceID id)
        {
            if (id == null)
                return -1;
            for (var i = 0; i < list.Count; i++)
            {
                if (SameId(list[i], id))
                    return i;
            }
            return -1;
        }

        private static bool Bool(LevelEngine l, string key, Func<LevelEngine, bool> get)
        {
            if (l == null)
                return false;
            try
            {
                return get(l);
            }
            catch (Exception e)
            {
                LogOnce("get-" + key, "读「" + key + "」失败：" + e.Message);
                return false;
            }
        }

        private static int Int(LevelEngine l, string key, Func<LevelEngine, int> get)
        {
            if (l == null)
                return 0;
            try
            {
                return get(l);
            }
            catch (Exception e)
            {
                LogOnce("get-" + key, "读「" + key + "」失败：" + e.Message);
                return 0;
            }
        }

        private static float Float(LevelEngine l, string key, Func<LevelEngine, float> get)
        {
            if (l == null)
                return 0f;
            try
            {
                return get(l);
            }
            catch (Exception e)
            {
                LogOnce("get-" + key, "读「" + key + "」失败：" + e.Message);
                return 0f;
            }
        }

        private static void Act(LevelEngine l, string key, Action<LevelEngine> act)
        {
            if (l == null)
                return;
            try
            {
                act(l);
            }
            catch (Exception e)
            {
                LogOnce("set-" + key, "写「" + key + "」失败：" + e.Message);
            }
        }
        #endregion
    }
}
