using System;
using System.Collections.Generic;
using HarmonyLib;
using MVZ2Logic.Blueprints;
using MVZ2Logic.Grids;
using MVZ2Logic.HeldItems;
using PVZEngine;
using PVZEngine.Definitions;
using PVZEngine.Entities;
using PVZEngine.Grids;
using PVZEngine.Level;
using PVZEngine.SeedPacks;

namespace Loadout
{
    /// <summary>
    /// **「这次生成是哪张卡放出来的」** —— 把「按蓝图 id 一把抓」换成「按放置的那一张卡」。
    ///
    /// <para>为什么必须这么做：早期版本是靠「蓝图 id → 种子实体 id」建命中表，于是
    /// **自然生成**的同种实体（出怪的僵尸）也被改了血量 / 阵营 —— 因为两条路都是
    /// <c>LevelEngine.SpawnSourced(..., source: null, ...)</c>，在生成漏斗里分不出来。</para>
    ///
    /// <para>唯一能分辨的时刻是**放置调用**本身：官方放置链是
    /// <c>LogicGridExt.UseEntityBlueprint(grid, seedPack, data)</c> →
    /// <c>PlaceEntityBlueprint</c> → <c>PlacementDefinition.PlaceEntity</c> →
    /// <c>grid.SpawnPlacedEntity(...)</c> → <c>LevelEngine.SpawnSourced</c>。
    /// 所以在这里开一个「作用域」：进入时记下**这张卡的键**与**它应该放出来的实体 id**，
    /// 退出时清掉。生成漏斗只在这个作用域内才认账。</para>
    ///
    /// <para><b>两种子生成</b>（父实体不是本次新生成的时候，靠这两个认回来）：</para>
    /// <list type="bullet">
    /// <item><b>作用域内</b>的子生成（升级替换、多实体放置）：父实体是场地上的老实体，
    /// 看作用域就够了。</item>
    /// <item><b>作用域外</b>的子生成：只有**模仿者（命令方块）**这一种 —— 命令方块落地后
    /// 要等一段时间才变换成真正的器械，那时作用域早没了。做法是在作用域里**给它打个标签**
    /// （<c>实体 ID → 这张卡的键</c>），变换时凭父实体 ID 查回来。</item>
    /// </list>
    ///
    /// <para>⚠ 升级类放置里如果官方走的是「把老器械改造成新器械」而**不新生成实体**
    /// （<c>UpgradeSideBySidePlaceMethod</c>），那就没有任何一次生成可以挂钩 —— 标记不生效。</para>
    /// </summary>
    internal static class PlacementScope
    {
        /// <summary>一张卡的「身份 + 它该放出来的实体」。</summary>
        private struct Tag
        {
            public string Key;
            public string EntityText;
        }

        private static string currentKey;
        private static string currentEntityText;

        /// <summary>命令方块：实体 ID → 它落地时属于哪张卡。</summary>
        private static readonly Dictionary<long, Tag> tagged = new Dictionary<long, Tag>();

        private static bool applied;

        /// <summary>当前是不是在「放置某一张卡」的调用里；不是的话返回 null。</summary>
        public static string Current { get { return currentKey; } }

        public static string CurrentEntityText { get { return currentEntityText; } }

        // ------------------------------------------------------------------
        // 补丁
        // ------------------------------------------------------------------

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            try
            {
                var harmony = new Harmony(Plugin.GUID + ".placementscope");
                // ① 自选卡 / 传送带：手上有 SeedPack → 能算出「这一张卡」的键
                var withPack = AccessTools.Method(typeof(LogicGridExt), "UseEntityBlueprint",
                    new[] { typeof(LawnGrid), typeof(SeedPack), typeof(IHeldItemData) });
                if (withPack != null)
                {
                    harmony.Patch(withPack,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(PlacementScope), nameof(PrefixWithPack))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(PlacementScope), nameof(Finalizer))));
                }
                else
                {
                    Plugin.Log?.LogError("[Placement] 找不到 LogicGridExt.UseEntityBlueprint，标记不会生效");
                }

                // ② 拿着「蓝图掉落物」去放：只有 SeedDefinition，没有卡 → 退化成蓝图 id 键
                var withDefinition = AccessTools.Method(typeof(LogicGridExt), "UseEntityBlueprintDefinition");
                if (withDefinition != null)
                {
                    harmony.Patch(withDefinition,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(PlacementScope), nameof(PrefixWithDefinition))),
                        finalizer: new HarmonyMethod(AccessTools.Method(typeof(PlacementScope), nameof(Finalizer))));
                }
                Plugin.Log?.LogInfo("[Placement] 已挂放置作用域补丁（标记只对该卡放出来的实体生效）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Placement] 挂补丁失败：" + e);
            }
        }

        private static void PrefixWithPack(LawnGrid grid, SeedPack seed)
        {
            try
            {
                var level = grid == null ? null : grid.Level;
                Begin(level, seed);
            }
            catch (Exception) { }
        }

        private static void PrefixWithDefinition(SeedDefinition seedDef)
        {
            try
            {
                // 没有卡可指 → 用蓝图 id 当键（这条路上的「一张卡」信息本来就不存在）。
                currentKey = seedDef == null ? null : BlueprintKeys.OfId(SafeId(seedDef));
                currentEntityText = seedDef == null ? null : TextOf(seedDef.GetSeedEntityID());
                if (currentKey != null)
                    PruneTags();
            }
            catch (Exception) { currentKey = null; currentEntityText = null; }
        }

        /// <summary>
        /// 作用域收尾。用 **finalizer** 而不是 postfix：放置过程中抛异常时 postfix 不会跑，
        /// 作用域就会一直挂着（之后所有生成都被误判成「正在放置」）。
        /// </summary>
        private static Exception Finalizer(Exception __exception)
        {
            currentKey = null;
            currentEntityText = null;
            return __exception;   // 原样抛出
        }

        private static void Begin(LevelEngine level, SeedPack pack)
        {
            currentKey = null;
            currentEntityText = null;
            if (level == null || pack == null)
                return;
            currentKey = BlueprintKeys.OfPack(level, pack);
            currentEntityText = TextOf(pack.GetSeedEntityID());
            if (currentKey != null)
                PruneTags();
        }

        // ------------------------------------------------------------------
        // 生成漏斗用
        // ------------------------------------------------------------------

        /// <summary>刚生成的实体 → 已经算出来的键（两个补丁共用一次解析结果）。</summary>
        private static readonly Dictionary<long, string> resolved = new Dictionary<long, string>();

        /// <summary>
        /// 这次生成属于哪一张卡？不属于任何卡就返回 false。
        ///
        /// <para><b>为什么要有缓存</b>：血量/白天不睡觉与逆阵营是**两个**独立的 postfix，
        /// 都会来问一次。而「命令方块的标签」是一次性的（查过就删），
        /// 两次解析会得到不同结果 —— 所以第一个问到的把答案放这儿，第二个直接取。</para>
        /// </summary>
        public static bool Resolve(Entity entity, ILevelSourceReference source, out string key)
        {
            key = null;
            if (entity == null)
                return false;
            try
            {
                lock (resolved)
                {
                    if (resolved.TryGetValue(entity.ID, out key))
                        return key != null;
                }

                var result = Compute(entity, source);
                lock (resolved)
                {
                    if (resolved.Count > 64)
                        resolved.Clear();
                    resolved[entity.ID] = result;
                }
                key = result;
                return result != null;
            }
            catch (Exception)
            {
                key = null;
                return false;
            }
        }

        private static string Compute(Entity entity, ILevelSourceReference source)
        {
            if (source == null)
            {
                // 根生成：只有「正在放置某一张卡」这一条路才可能命中。
                if (currentKey == null)
                    return null;
                if (IsTargetEntity(entity, currentEntityText))
                    return currentKey;
                // 不是目标实体（模仿者的命令方块）→ 打个标签，等它变换时接回来。
                Remember(entity);
                return null;
            }

            string inherited, expected;
            if (!TryInherit(source, out inherited, out expected))
                return null;
            return IsTargetEntity(entity, expected) ? inherited : null;
        }

        /// <summary>这个实体是不是「这张卡应该放出来的那个」（id 文本相同）。</summary>
        public static bool IsTargetEntity(Entity entity, string entityText)
        {
            if (entity == null || string.IsNullOrEmpty(entityText))
                return false;
            try
            {
                var def = entity.Definition;
                return def != null && TextOf(def.GetID()) == entityText;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 记下「这个刚生成的东西不是目标实体」—— 典型就是模仿者的命令方块。
        /// 它之后变换出来的目标实体靠这个标签把标记接回去。
        /// </summary>
        public static void Remember(Entity root)
        {
            if (root == null || currentKey == null)
                return;
            try
            {
                lock (tagged)
                {
                    if (tagged.Count > 256)
                        tagged.Clear();
                    tagged[root.ID] = new Tag { Key = currentKey, EntityText = currentEntityText };
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 子生成（父实体不是本次新生成的）该继承哪张卡的标记。
        ///
        /// <para><paramref name="entityText"/> 是**预期的目标实体 id 文本** ——
        /// 调用方拿它和刚生成的那个实体对一下，对不上就说明这不是我们要的那一个
        /// （例如模仿者变换失败变成了错误方块）。</para>
        /// </summary>
        public static bool TryInherit(ILevelSourceReference source, out string key, out string entityText)
        {
            key = null;
            entityText = null;
            if (source == null)
                return false;

            // 作用域内：升级替换、一次放置生成多个实体等。
            if (currentKey != null)
            {
                key = currentKey;
                entityText = currentEntityText;
                return true;
            }

            try
            {
                var entityRef = source.TryCast<EntitySourceReference>();
                if (entityRef == null)
                    return false;
                lock (tagged)
                {
                    Tag tag;
                    if (!tagged.TryGetValue(entityRef.ID, out tag))
                        return false;
                    tagged.Remove(entityRef.ID);   // 一次性：变换只会发生一次
                    key = tag.Key;
                    entityText = tag.EntityText;
                    return !string.IsNullOrEmpty(key);
                }
            }
            catch (Exception)
            {
                key = null;
                entityText = null;
                return false;
            }
        }

        /// <summary>换关的时候把标签清掉（实体 ID 在新关卡里没有意义）。</summary>
        public static void Reset()
        {
            lock (tagged)
                tagged.Clear();
            lock (resolved)
                resolved.Clear();
            currentKey = null;
            currentEntityText = null;
        }

        // ------------------------------------------------------------------
        // 自测用：真的走一遍放置生成
        // ------------------------------------------------------------------

        /// <summary>
        /// 自测用：**按某一张卡的作用域**在格子上生成一个实体（走官方放置生成路径
        /// <c>LawnGrid.SpawnPlacedEntity</c> → <c>LevelEngine.SpawnSourced</c>）。
        ///
        /// <para>这就是 <c>UseEntityBlueprint</c> 干的事，只是把「卡」直接递进来 ——
        /// 用它才能在不真的用鼠标放卡的前提下验「标记只对该卡放出来的实体生效」。</para>
        /// </summary>
        public static Entity DebugSpawnScoped(LevelEngine level, SeedPack pack, LawnGrid grid)
        {
            if (level == null || pack == null || grid == null)
                return null;
            Begin(level, pack);
            try
            {
                return LogicGridExt.SpawnPlacedEntity(grid, pack.GetSeedEntityID());
            }
            finally
            {
                currentKey = null;
                currentEntityText = null;
            }
        }

        /// <summary>自测用：**不带任何作用域**在格子上生成一个实体（= 自然生成那条路）。</summary>
        public static Entity DebugSpawnUnscoped(LevelEngine level, NamespaceID entityID, LawnGrid grid)
        {
            if (level == null || !NamespaceID.IsValid(entityID) || grid == null)
                return null;
            return LogicGridExt.SpawnPlacedEntity(grid, entityID);
        }

        /// <summary>自测用：当前作用域里的键（日志用）。</summary>
        public static string DebugCurrent
        {
            get { return currentKey == null ? "（无）" : BlueprintKeys.Describe(currentKey); }
        }

        private static void PruneTags()        {
            lock (tagged)
            {
                if (tagged.Count > 256)
                    tagged.Clear();
            }
        }

        private static NamespaceID SafeId(Definition def)
        {
            try { return def.GetID(); }
            catch (Exception) { return null; }
        }

        private static string TextOf(NamespaceID id)
        {
            if (!NamespaceID.IsValid(id))
                return null;
            try { return id.SpaceName + ":" + id.Path; }
            catch (Exception) { return null; }
        }
    }
}
