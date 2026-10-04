using System;
using System.Collections.Generic;
using HarmonyLib;
using MVZ2.Almanacs;
using PVZEngine;
using PVZEngine.Entities;
using PVZEngine.Level;

namespace Loadout
{
    /// <summary>
    /// 「选卡界面显示所有实体蓝图」+ 一个**全部蓝图 / 原版蓝图**切换开关。
    ///
    /// 挂点的选择（这一步很关键）：
    ///   <c>AlmanacManager.GetOrderedContraptionsByAlmanac(contraptionsID, appendList)</c>
    ///   在全工程**只有选卡链路**会调（<c>LevelBlueprintChooseController.RefreshBlueprintsForChoose</c>）。
    ///   它把结果 **追加** 到调用方传进来的 <c>appendList</c>，调用方随后
    ///   <c>choosingBlueprints = ...ToArray()</c>（要下标 ↔ id 映射，所以追加必须在这一步做）。
    ///
    /// 所以一个 **postfix** 往 <c>appendList</c> 里补上「图鉴之外的那些实体」就完事了：
    ///   - 不替换官方结果，官方的排序/分组/隐藏项过滤全部保持原样（追加在后面）；
    ///   - 不碰任何私有字段、不构造 il2cpp 接口参数（那两件事都很脆）。
    ///
    /// ## 「所有实体」怎么枚举（0.7.0 的正确做法）
    /// 0.7.0 里蓝图叫 <c>SeedDefinition</c>，而**每个实体都能有一个种子**：
    ///   <c>MVZ2Logic.Games.LogicGameDefinitionsExt.GetEntitySeedDefinition(game, entityID)</c>
    /// 会对没有显式 <c>blueprints.xml</c> 条目的实体**当场造一个默认种子**（名字取实体名）。
    /// 所以这里对**每一个实体定义**都问一次种子，拿到的 id 就是该进选卡列表的蓝图 id。
    ///
    /// ⚠️ 之前只收 <c>EntityTypes.PLANT</c> 的实体、并且直接用**实体 id** 当蓝图 id ——
    /// 那会漏掉「非 PLANT 但仍有蓝图」的条目，也正是「选卡页没显示全部实体蓝图」的原因。
    ///
    /// 兼容性：只追加、不改官方行为，但仍然是行为改动 → Standard 档。
    /// 选卡界面上多出来的那个按钮可以随时切回原版范围（见 <see cref="BlueprintListToggle"/>）。
    /// </summary>
    internal static class AllBlueprintsPatch
    {
        private static bool applied;
        private static bool loggedOnce;

        /// <summary>
        /// 是否显示「全部实体蓝图」。**默认关**（= 与原版一致），
        /// 而且只允许在**原版 debug 存档**里打开 —— 见 <see cref="IsAllowed"/>。
        ///
        /// <para>早期版本默认是 <c>true</c>，于是**非 debug 存档也会多出一大堆蓝图**，
        /// 不符合「所有功能仅在原版 debug 存档下生效」的设计意图。</para>
        /// </summary>
        private static bool showAll;

        /// <summary>
        /// 官方结果 + 我们追加的那一段，**按官方顺序**记一份（切换模式后实时重建要用）。
        /// 直接在 postfix 里拷 <c>appendList</c> 是最省事的 —— 那正是“排序之后的完整清单”，
        /// 不需要再去枚举官方那个 <c>IEnumerable</c> 入参（interop 的
        /// <c>Il2CppSystem.Collections.Generic.IEnumerator&lt;T&gt;</c> **只有 Current、没有 MoveNext**，
        /// 在托管侧根本遍历不了）。
        /// </summary>
        private static readonly List<NamespaceID> lastOrdered = new List<NamespaceID>();
        /// <summary>上面那份里属于官方的条目数（剩下的是我们追加的）。</summary>
        private static int officialCount;

        /// <summary>「非 debug 存档被拦下」这件事只记一次日志。</summary>
        private static bool deniedLogged;

        public static bool ShowAll { get { return showAll && IsAllowed(); } }
        public static int LastOrderedCount { get { return lastOrdered.Count; } }
        public static int LastOfficialCount { get { return officialCount; } }

        /// <summary>
        /// 现在**可以**显示全部蓝图吗：原版 debug 存档（或编辑器调试模式）。
        /// 非 debug 存档一律按「原版蓝图」处理，选卡界面上的切换按钮也不会出现。
        /// </summary>
        public static bool IsAllowed()
        {
            try { return LevelFacade.IsDebugSave(); }
            catch (Exception) { return false; }
        }

        /// <summary>上面那份清单（只读用；别改它）。</summary>
        public static List<NamespaceID> Ordered() { return lastOrdered; }

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            try
            {
                var target = AccessTools.Method(typeof(AlmanacManager), "GetOrderedContraptionsByAlmanac");
                if (target == null)
                {
                    Plugin.Log?.LogError("[AllBlueprints] 找不到 AlmanacManager.GetOrderedContraptionsByAlmanac");
                    return;
                }
                var harmony = new Harmony(Plugin.GUID + ".allblueprints");
                harmony.Patch(target, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(AllBlueprintsPatch), nameof(Postfix))));
                Plugin.Log?.LogInfo("[AllBlueprints] 已挂选卡蓝图补全补丁（默认显示全部实体蓝图）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[AllBlueprints] 挂补丁失败：" + e);
            }
        }

        [HarmonyPostfix]
        private static void Postfix(Il2CppSystem.Collections.Generic.List<NamespaceID> appendList)
        {
            try
            {
                if (appendList == null)
                {
                    LogOnce("null", "[AllBlueprints] appendList 注入失败（参数类型不匹配？），功能未生效");
                    return;
                }
                var appended = 0;
                if (CompatConfig.IsEnabled(GcFeature.AllBlueprintsInChoose) && showAll && IsAllowed())
                    appended = AppendAllSeeds(appendList);
                else if (showAll && !IsAllowed() && !deniedLogged)
                {
                    deniedLogged = true;
                    Plugin.Log?.LogInfo("[AllBlueprints] 当前不是原版 debug 存档 → 只显示原版蓝图"
                        + "（需求：所有功能仅在 debug 存档下生效）");
                }

                // 记下官方顺序（含我们追加的尾巴），供切换模式时重建。
                officialCount = appendList.Count - appended;
                lastOrdered.Clear();
                for (var i = 0; i < appendList.Count; i++)
                    lastOrdered.Add(appendList[i]);

                if (!loggedOnce)
                {
                    loggedOnce = true;
                    Plugin.Log?.LogInfo("[AllBlueprints] 选卡清单：官方 " + officialCount
                        + " 项 + 追加 " + appended + " 项 = " + appendList.Count + " 项（默认“全部蓝图”模式）");
                    Plugin.Log?.LogInfo("[AllBlueprints] 追加样例：" + Sample(lastOrdered, officialCount));
                }
            }
            catch (Exception)
            {
                // 原生调用栈上不能抛。
            }
        }

        /// <summary>把「所有实体」的种子 id 追加进列表，返回追加数。</summary>
        private static int AppendAllSeeds(Il2CppSystem.Collections.Generic.List<NamespaceID> appendList)
        {
            var game = MVZ2Logic.Global.Game;
            if (game == null)
                return 0;
            // interop 代理类不声明它实现的接口 → 按指针包壳。
            var content = new IGameContent(game.Pointer);

            // ---- 首选：枚举全部 <c>EntitySeedDefinition</c> ----
            // 这才是「有蓝图的实体」的**权威清单**（由 blueprints.xml 的 <entities> 生成），
            // 与图鉴无关 —— 正好是需求要的「不在图鉴内的实体蓝图」。
            // 之前用 GetAllEntityDefinitions + 类型过滤的做法是错的：341 个实体里有 273 个是
            // 弹幕/特效这种根本没有蓝图的实体（样例：mvz2:soul_effect、mvz2:cleaving_particles）。
            var seeds = CollectSeedIDs(content);
            if (seeds.Count > 0)
            {
                var addedBySeed = 0;
                for (var i = 0; i < seeds.Count; i++)
                {
                    var id = seeds[i];
                    if (!NamespaceID.IsValid(id) || Contains(appendList, id))
                        continue;
                    appendList.Add(id);
                    addedBySeed++;
                }
                if (!loggedOnce)
                    Plugin.Log?.LogInfo("[AllBlueprints] 注册的蓝图定义 " + seeds.Count + " 个，追加 " + addedBySeed + " 项");
                return addedBySeed;
            }
            // 枚举不到定义组时的退路：逐个实体问“它有没有蓝图”。
            // （ContentProviderHelper.GetSeedDefinition 是“取或新建”语义：对**任何**实体都能给出一份
            //   默认种子，所以这条路会连弹幕/特效实体也收进来。只在定义组不可用时用。）
            LogOnce("noseed", "[AllBlueprints] 枚举不到 seed 定义组，改为逐个实体问蓝图（会包含没有实际卡面的实体）");
            return AppendEntitiesWithSeed(content, appendList);
        }

        /// <summary>
        /// 枚举游戏里**注册的全部蓝图（种子）定义**。
        ///
        /// 做法：<c>IGameContent.GetDefinitions("seed")</c> —— 定义组里的 "seed" 列表。
        /// 实测（自测日志）：全内容定义 1981 个，其中 <c>seed=360</c>、<c>entity=341</c>；
        /// 这 360 个就是官方图鉴之外也有卡面的那些蓝图（图鉴只列 80 项）。
        ///
        /// ⚠️ 走过的弯路：<c>TryCast&lt;EntitySeedDefinition&gt;()</c> 在这 1981 个里**一个都转不出来**
        /// （360 个种子定义不是那个运行时类型），所以“遍历全部定义找种子”这条路行不通；
        /// 而 <c>ContentProviderHelper.GetSeedDefinition(content, id)</c> 是**取或新建**语义，
        /// 对任何实体都会给出一份默认种子（于是 341 个实体全“有蓝图”，连弹幕/特效也算）——
        /// 能跑但会往选卡列表里塞一堆没有实际卡面的条目，所以只当退路。
        /// </summary>
        private static List<NamespaceID> CollectSeedIDs(IGameContent content)
        {
            var result = new List<NamespaceID>();
            try
            {
                var seeds = content.GetDefinitions<PVZEngine.SeedPacks.SeedDefinition>("seed");
                if (seeds == null)
                    return result;
                var empty = 0;
                for (var i = 0; i < seeds.Length; i++)
                {
                    var seed = seeds[i];
                    if (seed == null)
                        continue;
                    var id = seed.id;
                    if (!NamespaceID.IsValid(id))
                        continue;
                    if (!Contains(result, id))
                        result.Add(id);
                    else
                        empty++;
                }
                if (empty > 0 && !loggedOnce)
                    Plugin.Log?.LogInfo("[AllBlueprints] seed 定义里有 " + empty + " 个重复 id（已去重）");
            }
            catch (Exception e)
            {
                LogOnce("defs", "[AllBlueprints] 枚举 seed 定义组失败：" + e.GetType().Name + ": " + e.Message);
            }
            return result;
        }

        private static bool Contains(List<NamespaceID> list, NamespaceID id)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (SameId(list[i], id))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 逐个实体问「它有没有蓝图」：<c>ContentProviderHelper.GetSeedDefinition(content, entityID)</c>。
        /// 这是判断“能不能当卡”最直接的问法 —— 弹幕/特效这种实体会返回 null。
        /// 同时按实体类型统计，方便对照“哪些类型确实有卡”。
        /// </summary>
        private static int AppendEntitiesWithSeed(IGameContent content, Il2CppSystem.Collections.Generic.List<NamespaceID> appendList)
        {
            var defs = ContentProviderHelper.GetAllEntityDefinitions(content);
            if (defs == null)
                return 0;
            var added = 0;
            var plantTotal = 0;
            var plantWithSeed = 0;
            var otherTotal = 0;
            var otherWithSeed = 0;
            for (var i = 0; i < defs.Length; i++)
            {
                var def = defs[i];
                if (def == null)
                    continue;
                var id = def.GetID();
                if (!NamespaceID.IsValid(id))
                    continue;
                var isPlant = def.Type == EntityTypes.PLANT;
                if (isPlant)
                    plantTotal++;
                else
                    otherTotal++;

                var seed = ResolveSeed(content, id);
                if (seed == null)
                    continue;
                if (isPlant)
                    plantWithSeed++;
                else
                    otherWithSeed++;

                var seedId = id;
                try
                {
                    var sid = seed.id;
                    if (NamespaceID.IsValid(sid))
                        seedId = sid;
                }
                catch (Exception)
                {
                }
                if (Contains(appendList, seedId))
                    continue;
                appendList.Add(seedId);
                added++;
            }
            if (!loggedOnce)
                Plugin.Log?.LogInfo("[AllBlueprints] 实体定义 " + defs.Length
                    + " 个：PLANT " + plantTotal + " 个（有蓝图 " + plantWithSeed + "），"
                    + "非 PLANT " + otherTotal + " 个（有蓝图 " + otherWithSeed + "）；追加 " + added + " 项");
            return added;
        }

        /// <summary>问游戏这个 id 有没有蓝图定义（拿不到就返回 null）。</summary>
        private static PVZEngine.SeedPacks.SeedDefinition ResolveSeed(IGameContent content, NamespaceID id)
        {
            try
            {
                return ContentProviderHelper.GetSeedDefinition(content, id);
            }
            catch (Exception e)
            {
                LogOnce("getsseed", "[AllBlueprints] GetSeedDefinition 失败：" + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>切换「全部蓝图 / 原版蓝图」并尝试立刻重建选卡列表。</summary>
        public static bool ToggleShowAll()
        {
            if (!IsAllowed())
            {
                Plugin.Log?.LogInfo("[AllBlueprints] 非 debug 存档，不允许切换到「全部蓝图」");
                showAll = false;
                return false;
            }
            showAll = !showAll;
            var ok = RebuildLive();
            Plugin.Log?.LogInfo("[AllBlueprints] 已切换为「" + (showAll ? "全部蓝图" : "原版蓝图") + "」"
                + (ok ? "，选卡列表已重建" : "，列表未即时重建（下次进入选卡界面时生效）"));
            return showAll;
        }

        /// <summary>
        /// 立刻按当前模式重建选卡列表。
        ///
        /// 做法（绕开了 interop 那个没法从托管侧构造的 <c>IEnumerable</c> 入参）：
        ///   ① 用 postfix 记下来的**官方顺序清单**拼出新的 id 数组
        ///      （“原版蓝图”模式就把尾巴上我们追加的那一段剪掉）；
        ///   ② 直接写 <c>choosingBlueprints</c>（它是 <c>Il2CppReferenceArray&lt;NamespaceID&gt;</c>，
        ///      可以从托管数组构造）；
        ///   ③ 调 <c>RefreshChooseBlueprintPanelElements()</c> 重建面板元素。
        ///
        /// ⚠️ ②③ 是否足够刷新视觉上的卡片列表，要看运行时（日志里会打 choosingBlueprints
        /// 的前后数量）；不够的话下一步再改成“重新进入选卡流程”。
        /// </summary>
        public static bool RebuildLive()
        {
            try
            {
                var controller = LevelFacade.Controller;
                if (controller == null)
                    return false;
                var part = controller.BlueprintChoosePart;
                if (part == null)
                    return false;

                var ids = new List<NamespaceID>();
                var take = ShowAll ? lastOrdered.Count : Math.Min(officialCount, lastOrdered.Count);
                for (var i = 0; i < take; i++)
                    ids.Add(lastOrdered[i]);
                if (ids.Count == 0)
                {
                    Plugin.Log?.LogWarning("[AllBlueprints] 还没有记下官方清单（没进过选卡界面？），无法即时重建");
                    return false;
                }

                var before = CountChoosing(part);
                part.choosingBlueprints = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<NamespaceID>(ids.ToArray());
                part.RefreshChooseBlueprintPanelElements();
                var after = CountChoosing(part);
                Plugin.Log?.LogInfo("[AllBlueprints] 重建选卡列表：choosingBlueprints " + before + " → " + after
                    + " 项（模式=" + (ShowAll ? "全部" : "原版") + "）");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[AllBlueprints] 重建选卡列表失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private static int CountChoosing(MVZ2.Level.LevelBlueprintChooseController part)
        {
            try
            {
                var arr = part.choosingBlueprints;
                return arr == null ? -1 : arr.Length;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static string Sample(List<NamespaceID> list, int from)
        {
            var parts = new List<string>();
            if (from < 0)
                from = 0;
            var start = Math.Max(from, list.Count - 8);
            for (var i = start; i < list.Count; i++)
                parts.Add(list[i].ToString());
            return string.Join(", ", parts.ToArray());
        }

        private static bool Contains(Il2CppSystem.Collections.Generic.List<NamespaceID> list, NamespaceID id)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (SameId(list[i], id))
                    return true;
            }
            return false;
        }

        private static bool SameId(NamespaceID a, NamespaceID b)
        {
            if (a == null || b == null)
                return false;
            if (ReferenceEquals(a, b))
                return true;
            return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
        }

        private static readonly HashSet<string> logged = new HashSet<string>();

        private static void LogOnce(string key, string message)
        {
            if (!logged.Add(key))
                return;
            Plugin.Log?.LogWarning(message);
        }
    }
}
