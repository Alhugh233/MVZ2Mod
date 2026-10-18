using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MVZ2.GameContent.Buffs;
using MVZ2.Managers;
using MVZ2Logic;
using MVZ2Logic.Difficulties;
using MVZ2Logic.Level;
using MVZ2Logic.Options;
using PVZEngine;
using PVZEngine.Buffs;
using PVZEngine.Difficulties;
using PVZEngine.Level;
using UnityEngine;

namespace Loadout
{
    /// <summary>
    /// 关卡内页面用的游戏 API 薄封装。
    ///
    /// 三条硬规则（都是为了"最大兼容性"）：
    ///   1. **只写 level 级属性**。<c>StageDefinition</c> / <c>AreaDefinition</c> 是全局共享对象，
    ///      往它们身上写会污染所有使用该关卡的场合（其它存档、无尽模式、街机）。
    ///      level 的属性回退链是「自身 → Stage → Area」，写自身既能盖住、又不外溢。
    ///   2. **尽量不碰全局 options**。<c>LogicOptionExt.SetDifficulty</c> 之类会写进全局配置文件、
    ///      跨用户跨存档生效 —— 面板一律不用。**唯一的例外是难度**：官方的难度来源就是
    ///      那个全局设置（<c>LevelController.UpdateDifficulty()</c> 每次开局都会用它覆写
    ///      <c>level.Difficulty</c>），只写 level 属性会被改回去。所以难度只在用户
    ///      **明确点「重载」**时写一次（见 <see cref="WriteDifficultyOption"/>）。
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

        /// <summary>
        /// 当前存档是不是**原版 debug 存档**（或编辑器内部调试模式）。
        ///
        /// <para>需求（<c>Loadout.txt</c> 第一行）：「所有功能仅在原版 debug 存档下生效」。
        /// 判定直接用游戏自己的那一份 —— <c>DebugManager.CanUseDebugFeatures()</c>
        /// （原版的血条数值模式、debug 器都看它）。</para>
        /// </summary>
        public static bool IsDebugSave()
        {
            try
            {
                var main = Main;
                var debug = main == null ? null : main.DebugManager;
                return debug != null && debug.CanUseDebugFeatures();
            }
            catch (Exception)
            {
                // 读不到就按「不是 debug 存档」处理（宁可少做，不可越权）。
                return false;
            }
        }

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
        public static void SetMaxEnergy(LevelEngine l, int v)
        {
            Act(l, "maxenergy", x => x.SetMaxEnergy(v));
            // 开了无限能量时，改上限要把当前值一起顶上去（面板暂停时 buff 的每帧更新不跑）。
            CheatRefill.Energy(l);
        }

        public static float GetEnergy(LevelEngine l) { return Float(l, "energy", x => x.Energy); }
        public static void SetEnergy(LevelEngine l, float v) { Act(l, "energy", x => x.SetEnergy(v)); }

        public static int GetStarshardCount(LevelEngine l) { return Int(l, "starshardcount", x => x.GetStarshardCount()); }
        public static void SetStarshardCount(LevelEngine l, int v) { Act(l, "starshardcount", x => x.SetStarshardCount(v)); }

        public static int GetStarshardSlots(LevelEngine l) { return Int(l, "starshardslots", x => x.GetStarshardSlotCount()); }
        public static void SetStarshardSlots(LevelEngine l, int v)
        {
            Act(l, "starshardslots", x =>
            {
                x.SetStarshardSlotCount(v);
                // 上限调小后当前值可能超了，夹一下（原版 SetStarshardCount 自带 clamp）。
                x.SetStarshardCount(x.GetStarshardCount());
            });
            CheatRefill.Starshard(l);
        }

        public static float GetSpawnPointAddition(LevelEngine l) { return Float(l, "spawnadd", x => x.GetSpawnPointAddition()); }
        /// <summary>
        /// ⚠️ 只能写 **level 实例**。原版给这三个属性提供的 setter 是
        /// <c>SetSpawnPointXxx(this StageDefinition, float)</c> —— 往 StageDefinition（全局共享对象）上写，
        /// 会污染所有使用该关卡的场合（其它存档、无尽模式、街机）。
        /// level 的属性回退链是「自身 → Stage → Area」，写自身既盖得住又不外溢。
        /// </summary>
        public static void SetSpawnPointAddition(LevelEngine l, float v) { Act(l, "spawnadd", x => SetEffective(x, LogicLevelProps.SPAWN_POINTS_ADDITION, v, "spawnadd")); }

        public static float GetSpawnPointMultiplier(LevelEngine l) { return Float(l, "spawnmul", x => x.GetSpawnPointMultiplier()); }
        /// <summary>
        /// <para><b>⚠️ 常量名在 0.7.0 正式版里改过。</b>正式版以前是
        /// <c>SPAWN_POINTS_MUTLIPLIER</c>（"MUTLI"，拼错的），正式版的
        /// git 提交 <i>fix: 修复spawnPointsMultiplier常量名错误的问题</i> 把它改成了
        /// <c>SPAWN_POINTS_MULTIPLIER</c>。属性键的**名字**决定它落在哪个槽上，
        /// 所以这不是「改个拼写」——引用旧名字在正式版上直接编译不过（CS0117），
        /// 而如果只是运行期按字符串找键，就会静默写到一个没人读的键上。</para>
        ///
        /// <para>本仓库只面向 0.7.0 正式版及以后，所以直接用新名字；
        /// 要同时兼容旧版本得走 <c>PropertyKey</c> 反射按名字找，
        /// 那种复杂度不值得（旧版本已经不再部署）。</para>
        /// </summary>
        public static void SetSpawnPointMultiplier(LevelEngine l, float v) { Act(l, "spawnmul", x => SetEffective(x, LogicLevelProps.SPAWN_POINTS_MULTIPLIER, v, "spawnmul")); }

        public static float GetSpawnPointPower(LevelEngine l) { return Float(l, "spawnpow", x => x.GetSpawnPointPower()); }
        public static void SetSpawnPointPower(LevelEngine l, float v) { Act(l, "spawnpow", x => SetEffective(x, LogicLevelProps.SPAWN_POINTS_POWER, v, "spawnpow")); }

        /// <summary>
        /// 按**生效值**写这三个属性 —— 让玩家填多少就是多少。
        ///
        /// <para>问题：关卡 buff 会给这些属性挂修饰器。实测困难难度的 <c>LevelHardBuff</c> 就是
        /// <c>AddMultiple 0.2</c>（= 生效值 ×1.2），于是面板里填 1 → 写入 1 → 读回 1.2，
        /// 界面上看起来就是「刚改完就被乘了 1.2」。</para>
        ///
        /// <para>做法：对「自身值 → 生效值」这条映射**采两个样本**求出它的仿射形式
        /// <c>eff = a·own + b</c>（对原版现有的 Set/Add/AddMultiple/Multiply 四种算符都成立），
        /// 再解出 <c>own = (want - b) / a</c> 写回去。这样不论难度/关卡/装备挂了多少修饰器，
        /// 玩家输入的都是**生效值**。</para>
        ///
        /// <para>如果 <c>a ≈ 0</c>（存在 <c>Set</c> 类修饰器，自身值根本不影响生效值），
        /// 那就无法反解 —— 此时照写原值并记一行日志（而不是默默骗玩家）。</para>
        /// </summary>
        private static void SetEffective(LevelEngine level, PVZEngine.PropertyMeta<float> key,
            float want, string logKey)        {
            try
            {
                level.SetProperty(key, 0f);
                var e0 = level.GetProperty<float>(key);
                level.SetProperty(key, 1f);
                var e1 = level.GetProperty<float>(key);
                var a = e1 - e0;
                var b = e0;
                if (Mathf.Abs(a) < 0.000001f)
                {
                    level.SetProperty(key, want);
                    LogOnce(logKey + "-set", "这一项被关卡修饰器固定住了（Set 类），填的值无法生效");
                    return;
                }
                var own = (want - b) / a;
                level.SetProperty(key, own);
                var got = level.GetProperty<float>(key);
                if (Mathf.Abs(got - want) > 0.001f)
                {
                    LogOnce(logKey + "-miss", "按生效值写回后偏差较大：想要 " + Num(want)
                        + " 实际 " + Num(got) + "（修饰器可能不是线性的）");
                }
            }
            catch (Exception e)
            {
                LogOnce(logKey + "-eff", "按生效值写入失败：" + e.Message);
                throw;
            }
        }

        private static string Num(float v)
        {
            return v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }

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
            if (!NamespaceID.IsValid(id))
                return;
            // ⚠️ 值没变就**什么都别做**：官方 CurrentMusic 的 setter 无条件 Music.Play()，
            // 也就是"从头重播"。输入框失焦也会走一次提交，于是"点一下音乐输入框"
            // 就足以让音乐重头开始（用户报过）。这里两道闸：值没变不动；就算值变了，
            // 已经在放这一首也只写属性、不重播。
            var controller0 = Controller;
            if (SameId(GetMusicID(l), id))
            {
                Plugin.Log?.LogInfo("[Level] 音乐未变（" + Text(id) + "），不重播");
                return;
            }
            if (controller0 != null && SameId(controller0.CurrentMusic, id))
            {
                Act(l, "music", x => x.SetMusicID(id));
                Plugin.Log?.LogInfo("[Level] 音乐 → " + Text(id) + "（已在播放，不重播）");
                return;
            }

            Act(l, "music", x => x.SetMusicID(id));
            var controller = controller0;
            if (controller == null)
                return;
            try
            {
                controller.CurrentMusic = id;
                Plugin.Log?.LogInfo("[Level] 音乐 → " + Text(id) + "（已立即播放）");
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

                // ① **必须同时改官方难度设置**（全局 options）。
                //
                // 本类开头第 2 条规则说“绝不碰全局 options”，难度是唯一的例外，而且非例外不可：
                // 官方的难度来源就是那个设置 —— LevelController.UpdateDifficulty()
                // 会在每次开局转场 / StartGame 时用 Options.GetDifficulty() **覆写** level.Difficulty
                // （仅当 CurrentFlag <= 0，无尽关不覆写）。原版设置界面里的难度按钮走的也是
                // “改设置 + UpdateDifficulty()”这条路。只写 level 属性 → 下一次转场就被改回去
                // （实测症状：难度改完立刻变回原样）。
                WriteDifficultyOption(difficulty);

                // ② 摘旧 buff → 走官方 UpdateDifficulty（设属性 + 刷新关卡 UI 的难度名）→ 挂新 buff
                var isIZombie = level.IsIZombie();
                var oldBuff = DifficultyBuffID(level, oldID, isIZombie);
                if (NamespaceID.IsValid(oldBuff))
                    BuffTargetExt.RemoveBuffs(AsBuffTarget(level), oldBuff);

                var controller = Controller;
                var viaOfficial = false;
                if (controller != null)
                {
                    try
                    {
                        controller.UpdateDifficulty();
                        viaOfficial = true;
                    }
                    catch (Exception e)
                    {
                        LogOnce("difficulty-update", "官方 UpdateDifficulty 失败：" + e.Message);
                    }
                }
                // 无尽关（CurrentFlag > 0）官方会跳过，或官方调用失败时兜底直接写属性。
                if (!viaOfficial || !SameId(level.Difficulty, difficulty))
                    level.SetDifficulty(difficulty);

                var newBuff = DifficultyBuffID(level, difficulty, isIZombie);
                if (NamespaceID.IsValid(newBuff))
                    BuffTargetExt.AddBuff(AsBuffTarget(level), newBuff);

                Plugin.Log?.LogInfo("[Level] 难度 → " + Text(difficulty)
                    + "（buff: " + (NamespaceID.IsValid(oldBuff) ? Text(oldBuff) : "无")
                    + " → " + (NamespaceID.IsValid(newBuff) ? Text(newBuff) : "无") + "）");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Level] 切换难度失败：" + e);
                return false;
            }
        }

        /// <summary>
        /// 把难度写进官方设置（并落盘）。只有**用户明确点「重载」**时才会走到这里 ——
        /// 这是面板里唯一会动全局 options 的地方，因为官方的难度就是这样存的。
        /// </summary>
        public static void WriteDifficultyOption(NamespaceID difficulty)
        {
            if (!NamespaceID.IsValid(difficulty))
                return;
            try
            {
                var options = Main == null ? null : Main.OptionsManager;
                if (options == null)
                    return;
                var current = options.GetOptionID(LogicOptionItemID.difficulty);
                if (SameId(current, difficulty))
                    return;
                options.SetOptionID(LogicOptionItemID.difficulty, difficulty);
                options.SaveOptions();
                Plugin.Log?.LogInfo("[Level] 难度设置 → " + Text(difficulty) + "（已写入全局设置）");
            }
            catch (Exception e)
            {
                LogOnce("difficulty-option", "写难度设置失败：" + e.Message);
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
        /// 重载关卡 —— **先保存当前关卡数据，再把这个状态搬到目标关卡的存档名下，最后重新载入**。
        ///
        /// <para><b>为什么不是简单地"换一个 startStage 再重载"：</b>关卡状态的存读是
        /// <c>&lt;用户存档&gt;/&lt;命名空间&gt;/level/&lt;stage.Path&gt;.lvl</c>，
        /// 而文件**内部**还写着它自己的 <c>stageDefinitionID</c> / <c>areaDefinitionID</c>
        /// （<c>LevelEngine.CreateFromSerializable</c> 用的就是文件里那两个，不是调用参数）。于是：</para>
        /// <list type="bullet">
        /// <item>只改「背景」→ 文件名没变，命中旧存档，里面的 area 还是旧的 →
        /// <b>背景根本换不掉</b>（实测就是这样）；</item>
        /// <item>只改「关卡」→ 文件名变了、文件不存在 → 以新关卡从头开始，
        /// 当前进度被留在旧文件里。</item>
        /// </list>
        ///
        /// <para><b>正确做法（本次修正）</b>：把当前关卡的状态存档**复制到目标关卡的路径**，
        /// 并把文件里的 <c>stageDefinitionID</c> / <c>areaDefinitionID</c>（需要时连
        /// <c>difficulty</c>）改写成目标值 —— 这就是"让游戏把 A 关卡的存档当成 B 关卡来解析"，
        /// 也就是前作存档修改器干的事。旧的存档不会被破坏：原文件仍然留在原关卡名下，
        /// 目标文件若已存在则先备份成 <c>*.lvl.bak</c>。</para>
        ///
        /// <para>顺序很重要：① <c>SaveLevel()</c> 必须在改「起始关卡」之前（它按
        /// <c>GetStartStageID()</c> 决定写哪个文件）；② 改写后的文件必须在
        /// <c>InitLevel</c> 之前就位（官方重载读它）；③ <c>SetStartStage(target)</c>
        /// 决定"进哪一关、用哪个文件名"。</para>
        /// </summary>
        public static bool ReloadLevel(NamespaceID area, NamespaceID stage)
        {
            return ReloadLevel(area, stage, null);
        }

        /// <param name="difficulty">同时要应用的难度（null = 不改）。</param>
        public static bool ReloadLevel(NamespaceID area, NamespaceID stage, NamespaceID difficulty)
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
                var startStage = controller.GetStartStageID();
                var sourceStage = NamespaceID.IsValid(startStage) ? startStage : curStage;
                var targetArea = NamespaceID.IsValid(area)
                    ? area
                    : (NamespaceID.IsValid(curArea) ? curArea : controller.GetStartAreaID());
                var targetStage = NamespaceID.IsValid(stage) ? stage : curStage;
                var stageChanged = !SameId(targetStage, curStage);
                var areaChanged = !SameId(targetArea, curArea);

                // ① 先保存当前关卡状态（写进**当前** startStageID 对应的文件）
                SafeSaveLevel(levelManager, sourceStage);

                // ② 把这份状态搬到目标关卡名下（内部 id 改写成目标）
                var carried = false;
                if (stageChanged || areaChanged || NamespaceID.IsValid(difficulty))
                {
                    carried = CarryOverState(levelManager, sourceStage, targetStage, targetArea,
                        NamespaceID.IsValid(difficulty) ? difficulty : null);
                    Plugin.Log?.LogInfo("[Level] 重载：状态搬运 "
                        + (carried ? "成功" : "未做/失败")
                        + "（" + Text(sourceStage) + " → " + Text(targetStage)
                        + "  背景=" + Text(targetArea) + "）"
                        + (stageChanged ? "  ※关卡 id 变了：目标关卡将按**这份状态的**内容与进度继续"
                                        : "  ※文件名未变：进度保留"));
                }

                // ③ 难度（唯一会动全局设置的地方，见 ApplyDifficulty 的说明）
                if (NamespaceID.IsValid(difficulty))
                    ApplyDifficulty(level, difficulty);

                // ④ 交给官方重载（内部会 Dispose + 回关卡场景 + InitLevel）
                controller.SetStartStage(targetArea, targetStage);
                Plugin.Log?.LogInfo("[Level] 重载：目标 = " + Text(targetArea) + " / " + Text(targetStage));
                return BeginOfficialReload(controller, "面板重载");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Level] 重载关卡失败：" + e);
                return false;
            }
        }

        /// <summary>
        /// 安全地保存当前关卡状态：官方 <c>LevelManager.SaveLevel()</c> 是
        /// 「先 <c>FileMode.Create</c> **截断** → 写首行 → 序列化内容」，
        /// 而 <c>controller.SaveGame()</c> 在关卡早期会抛（实测 <c>WriteToSerializable_ProgressBar</c>
        /// 对 null 调 <c>ToArray</c>）—— 那一抛会留下一个**只有首行的残档**，
        /// 下次进这个关卡直接报“读档失败”（实测踩过，而且是不可逆的）。
        ///
        /// <para>所以这里先备份、抛了就还原；正常时删掉备份。</para>
        /// </summary>
        private static bool SafeSaveLevel(MVZ2.Level.LevelManager levelManager, NamespaceID stage)
        {
            var path = levelManager.GetLevelStatePath(stage);
            var bak = string.IsNullOrEmpty(path) ? null : path + ".saving";
            var hadFile = false;
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Copy(path, bak, true);
                    hadFile = true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Level] 保存前备份关卡存档失败（仍会尝试保存）：" + e.Message);
            }

            var ok = true;
            try
            {
                levelManager.SaveLevel();
                Plugin.Log?.LogInfo("[Level] 已保存当前关卡状态（startStage=" + Text(stage) + "）");
            }
            catch (Exception e)
            {
                ok = false;
                Plugin.Log?.LogWarning("[Level] 保存关卡状态失败（" + e.GetType().Name
                    + "），已还原备份，避免留下残档");
                try
                {
                    if (hadFile && bak != null)
                        File.Copy(bak, path, true);
                    else if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception e2)
                {
                    Plugin.Log?.LogError("[Level] 还原关卡存档失败：" + e2.Message);
                }
            }
            finally
            {
                try { if (bak != null && File.Exists(bak)) File.Delete(bak); }
                catch (Exception) { }
            }
            return ok;
        }

        /// <summary>
        /// 读关卡存档的文本。
        ///
        /// <para><b>必须走 <c>FileManager.ReadStringFile</c>：</b>正式版里 <c>.lvl</c> 是 **gzip**
        /// （<c>FileManager.compressed = true</c> 时 <c>OpenFileWrite</c> 走
        /// <c>OpenCompressedWrite</c>）；直接 <c>File.ReadAllText</c> 拿到的是二进制垃圾。
        /// 该方法是按魔数自判的，未压缩的旧存档也能读。</para>
        /// </summary>
        private static string ReadLevelStateText(string path)
        {
            var main = Main;
            if (main != null && main.FileManager != null)
                return main.FileManager.ReadStringFile(path);
            return File.ReadAllText(path);
        }

        /// <summary>写回关卡存档文本（保持与游戏相同的压缩形式）。</summary>
        private static void WriteLevelStateText(string path, string text)
        {
            var main = Main;
            if (main != null && main.FileManager != null)
                main.FileManager.WriteStringFile(path, text);
            else
                File.WriteAllText(path, text);
        }

        /// <summary>
        /// 把 <paramref name="fromStage"/> 的状态存档搬到 <paramref name="toStage"/> 名下，
        /// 并把文件里的关卡/背景（可选难度）id 改写成目标值。
        ///
        /// <para>目标文件已存在且不是同一个文件时，先备份为 <c>*.lvl.bak</c>（不静默覆盖用户存档）。</para>
        /// </summary>
        private static bool CarryOverState(MVZ2.Level.LevelManager levelManager,
            NamespaceID fromStage, NamespaceID toStage, NamespaceID area, NamespaceID difficulty,
            string sourcePath = null)
        {
            try
            {
                if (!NamespaceID.IsValid(toStage) || !NamespaceID.IsValid(area))
                {
                    Plugin.Log?.LogWarning("[Level] 状态搬运：目标 id 无效（stage=" + Text(toStage)
                        + " area=" + Text(area) + "），不改写文件");
                    return false;
                }
                var srcPath = string.IsNullOrEmpty(sourcePath)
                    ? levelManager.GetLevelStatePath(fromStage)
                    : sourcePath;
                var dstPath = levelManager.GetLevelStatePath(toStage);
                if (string.IsNullOrEmpty(srcPath) || string.IsNullOrEmpty(dstPath))
                    return false;
                if (!File.Exists(srcPath))
                {
                    Plugin.Log?.LogWarning("[Level] 状态搬运：源文件不存在（" + srcPath + "）");
                    return false;
                }

                var text = ReadLevelStateText(srcPath);
                text = ReplaceJsonStringField(text, "stageDefinitionID", Text(toStage));
                text = ReplaceJsonStringField(text, "areaDefinitionID", Text(area));
                if (NamespaceID.IsValid(difficulty))
                    text = ReplaceJsonStringField(text, "difficulty", Text(difficulty));

                var samePath = string.Equals(srcPath, dstPath, StringComparison.OrdinalIgnoreCase);
                if (!samePath && File.Exists(dstPath))
                {
                    var bak = dstPath + ".bak";
                    File.Copy(dstPath, bak, true);
                    Plugin.Log?.LogWarning("[Level] 状态搬运：目标存档已存在，已备份为 " + bak);
                }
                var dir = Path.GetDirectoryName(dstPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                WriteLevelStateText(dstPath, text);
                Plugin.Log?.LogInfo("[Level] 状态搬运：写 " + dstPath
                    + (samePath ? "（原文件就地改写）" : "（来自 " + srcPath + "）"));
                // 本 mod 自己的旁挂数据（蓝图标记）跟着搬，避免换关卡就丢。
                if (!samePath)
                    BlueprintFlags.CarryOver(srcPath, dstPath);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Level] 状态搬运失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 把 JSON 里**第一个** <paramref name="field"/> 的值改写成字符串
        /// <paramref name="value"/>（原值是 <c>null</c> 也照改）。
        ///
        /// <para>关卡存档是"两行 JSON"（首行 = 版本头，次行 = 内容），字段形如
        /// <c>"stageDefinitionID" : "mvz2:day"</c> —— <c>NamespaceID</c> 的序列化器
        /// 只写一个字符串，所以这里做的是**文本替换**而不是解析整棵树
        /// （我们没必要为了改一个 id 把整个存档反序列化一遍）。</para>
        /// </summary>
        private static string ReplaceJsonStringField(string json, string field, string value)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field) || string.IsNullOrEmpty(value))
                return json;
            var key = "\"" + field + "\"";
            var at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
                return json;
            var i = at + key.Length;
            while (i < json.Length && (json[i] == ' ' || json[i] == ':'))
                i++;
            if (i >= json.Length)
                return json;
            if (json[i] == '"')
            {
                var end = json.IndexOf('"', i + 1);
                if (end < 0)
                    return json;
                return json.Substring(0, i) + "\"" + value + "\"" + json.Substring(end + 1);
            }
            if (i + 4 <= json.Length && string.CompareOrdinal(json, i, "null", 0, 4) == 0)
                return json.Substring(0, i) + "\"" + value + "\"" + json.Substring(i + 4);
            return json;
        }

        /// <summary>
        /// 自测：验证「把当前关卡的状态存档搬到另一个关卡名下并改写内部 id」这条最危险的代码。
        ///
        /// <para><b>不改动任何真实存档</b>：目标 id 用一个不可能与真实关卡撞名的探针
        /// （<c>mvz2:loadout_carryover_probe</c>），跑完删掉那个文件；同时校验**源文件没被动过**。</para>
        /// </summary>
        public static string DebugCarryOverTest()
        {
            var lm = Main == null ? null : Main.LevelManager;
            var level = Current;
            var controller = Controller;
            if (lm == null || level == null || controller == null)
                return "（不在关卡内，状态搬运自测跳过）";

            var sb = new System.Text.StringBuilder(260);
            NamespaceID probeStage = null;
            var probePath = string.Empty;
            try
            {
                var fromStage = controller.GetStartStageID();
                if (!NamespaceID.IsValid(fromStage))
                    return "（拿不到当前关卡 id，状态搬运自测跳过）";

                // 先落盘当前状态（重载本来就会先做这一步）。
                // 它**可能**失败（实测开局阶段 ProgressBar 那一段还没初始化，官方 SaveGame 里
                // 会对 null 调 ToArray）—— SafeSaveLevel 会兜住并还原，不会留下残档。
                var saved = SafeSaveLevel(lm, fromStage);
                var srcPath = lm.GetLevelStatePath(fromStage);
                if (!File.Exists(srcPath))
                {
                    // 开局阶段官方 SaveLevel 会失败（ProgressBar 段没起来），于是当前关卡还没有存档 ——
                    // 那就随便借一个同目录下已有的存档来验证「搬运 + 改写」这套文件机制，
                    // 反正只写探针文件、跑完删掉。
                    srcPath = AnyStateFile(lm.GetLevelStatePath(fromStage));
                    if (srcPath == null)
                        return "（找不到任何状态存档，状态搬运自测跳过）";
                }
                var srcBefore = ReadLevelStateText(srcPath);

                probeStage = new NamespaceID("mvz2", "loadout_carryover_probe");
                probePath = lm.GetLevelStatePath(probeStage);
                var targetArea = GetAreaID(level);

                var carried = CarryOverState(lm, fromStage, probeStage, targetArea, null, srcPath);
                var dstText = File.Exists(probePath) ? ReadLevelStateText(probePath) : string.Empty;
                var srcText = ReadLevelStateText(srcPath);

                sb.Append("状态搬运自测：源=" + Text(fromStage))
                  .Append("  存档=").Append(saved ? "OK" : "失败（用旧档）")
                  .Append("  搬运=").Append(carried ? "OK" : "失败")
                  .Append("  目标文件=" ).Append(dstText.Length > 0 ? "已写" : "缺失")
                  .Append("  内部 stage=" ).Append(JsonFieldValue(dstText, "stageDefinitionID"))
                  .Append("（应 " ).Append(Text(probeStage)).Append('）')
                  .Append("  area=" ).Append(JsonFieldValue(dstText, "areaDefinitionID"))
                  .Append("  文本长度=源 " ).Append(srcBefore.Length).Append(" / 目标 " ).Append(dstText.Length)
                  .Append("  源文件未被改=" ).Append(srcText == srcBefore ? 1 : 0)
                  .Append("  判据=").Append(
                      carried
                      && string.Equals(JsonFieldValue(dstText, "stageDefinitionID"), Text(probeStage), StringComparison.Ordinal)
                      && string.Equals(JsonFieldValue(dstText, "areaDefinitionID"), Text(GetAreaID(level)), StringComparison.Ordinal)
                      && srcText == srcBefore
                          ? "OK" : "NG");
            }
            catch (Exception e)
            {
                sb.Append("（状态搬运自测失败：").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('）');
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrEmpty(probePath) && File.Exists(probePath))
                        File.Delete(probePath);
                }
                catch (Exception) { }
            }
            return sb.ToString();
        }

        /// <summary>同目录里随便找一个已有的状态存档（自测用；拿不到返回 null）。</summary>
        private static string AnyStateFile(string likeThisPath)
        {
            try
            {
                if (string.IsNullOrEmpty(likeThisPath))
                    return null;
                var dir = Path.GetDirectoryName(likeThisPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return null;
                var files = Directory.GetFiles(dir, "*.lvl");
                for (var i = 0; i < files.Length; i++)
                {
                    var name = Path.GetFileName(files[i]);
                    if (name.IndexOf("probe", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    return files[i];
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>读 JSON 里第一个该字段的字符串值（诊断/自测用）。</summary>
        private static string JsonFieldValue(string json, string field)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field))
                return "?";
            var key = "\"" + field + "\"";
            var at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0)
                return "（未找到）";
            var i = json.IndexOf('"', at + key.Length + 1);
            if (i < 0)
                return "（非字符串）";
            var end = json.IndexOf('"', i + 1);
            return end < 0 ? "（未闭合）" : json.Substring(i + 1, end - i - 1);
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

        /// <summary>是否正在等「关卡真的起来」（见 <see cref="RetryInitLevel"/>）。</summary>
        private static bool switchWaitLevel;
        private static int switchWaitFrames;

        /// <summary>是否正在切换关卡（含等关卡确实起来的那几帧）。</summary>
        public static bool SwitchingLevel { get { return switchTask != null || switchWaitLevel; } }

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
                switchWaitLevel = false;
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
                switchWaitLevel = false;
                switchLabel = label;
                switchFrames = 0;
                switchTask = controller.ReloadLevel(MVZ2Logic.Level.LevelRestartStatus.InLevel);
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
            if (switchWaitLevel)
            {
                RetryInitLevel();
                return;
            }

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
                if (switchInitsLevel && NamespaceID.IsValid(switchInitStage))
                {
                    // 自测进关卡这条路：Task 只切了场景，InitLevel 要我们自己叫。
                    // 但不能只叫一次（见 RetryInitLevel）。
                    switchWaitLevel = true;
                    switchWaitFrames = 0;
                    RetryInitLevel();
                    return;
                }
                Plugin.Log?.LogInfo("[Level] 关卡切换完成（" + switchLabel + "）→ " + switchFrames + " 帧");
            }
            catch (Exception e)
            {
                switchTask = null;
                Plugin.Log?.LogError("[Level] 关卡切换收尾失败：" + e);
            }
        }

        /// <summary>
        /// 确认关卡真的起来了（只用于 <c>switchInitsLevel</c> 那条自测路径）。
        ///
        /// <para><b>为什么要重试：</b>官方 <c>LevelManager.InitLevel</c> 的第一句就是
        /// <c>if (!controller.Exists()) return;</c> —— 而场景切完的那一帧，
        /// <c>LevelController</c> 可能还没把自己注册进 <c>LevelManager</c>。
        /// 只叫一次就会**静默地什么都不做**，于是永远进不了关卡
        /// （实测自测就卡在“等关卡起来”那一步，日志里再无任何输出）。</para>
        /// </summary>
        private static void RetryInitLevel()
        {
            try
            {
                switchWaitFrames++;
                var main = Main;
                if (main == null || main.LevelManager == null)
                    return;
                if (!main.LevelManager.IsInLevel() && switchWaitFrames <= 600)
                    main.LevelManager.InitLevel(switchInitArea, switchInitStage);
                if (!main.LevelManager.IsInLevel())
                {
                    if (switchWaitFrames > 600)
                    {
                        switchWaitLevel = false;
                        Plugin.Log?.LogError("[Level] 等关卡起来超时（" + switchLabel + "），已放弃");
                    }
                    return;
                }
                switchWaitLevel = false;
                Plugin.Log?.LogInfo("[Level] 关卡切换完成（" + switchLabel + "）→ "
                    + switchFrames + "+" + switchWaitFrames + " 帧");
            }
            catch (Exception e)
            {
                switchWaitLevel = false;
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
