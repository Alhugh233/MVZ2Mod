using System;
using System.Collections.Generic;
using MVZ2.Managers;
using MVZ2Logic;
using MVZ2Logic.Saves;
using MVZ2Logic.Unlocks;
using PVZEngine;
using PVZEngine.Level;

namespace GraphicConsole
{
    /// <summary>
    /// 关卡外页面（进度）用的游戏 API 薄封装。
    ///
    /// 关卡外没有 <c>LevelEngine</c>，所以这一套全走**用户存档**：
    ///   <c>Global.Saves</c>（<c>IGlobalSaveData</c>）与 <c>Main.ResourceManager</c>。
    /// 与关卡内那一套（<see cref="LevelFacade"/>）的 API 面完全不同，所以分开放。
    ///
    /// 注意：这些操作**直接写用户存档**，是不可逆的（尤其解锁：原版没有「反解锁成就」的 API，
    /// 成就判定靠条件重算而不是解锁表）。面板会把这一点提示给用户。
    /// </summary>
    internal static class ProgressOps
    {
        private static MainManager Main { get { return MainManager.Instance; } }

        #region 绿宝石
        public static int GetMoney()
        {
            try
            {
                var saves = Global.Saves;
                return saves == null ? 0 : saves.GetMoney();
            }
            catch (Exception e)
            {
                LogOnce("money", "读绿宝石失败：" + e.Message);
                return 0;
            }
        }

        public static void SetMoney(int value)
        {
            try
            {
                var saves = Global.Saves;
                if (saves == null)
                    return;
                saves.SetMoney(value);
                SaveNow();
            }
            catch (Exception e)
            {
                LogOnce("money", "写绿宝石失败：" + e.Message);
            }
        }
        #endregion

        #region 解锁
        public static NamespaceID[] GetAllUnlocks()
        {
            try
            {
                var saves = Global.Saves;
                if (saves == null)
                    return new NamespaceID[0];
                var list = saves.GetAllUnlocks();
                return list ?? new NamespaceID[0];
            }
            catch (Exception e)
            {
                LogOnce("unlocks", "读解锁列表失败：" + e.Message);
                return new NamespaceID[0];
            }
        }

        public static bool IsUnlocked(NamespaceID id)
        {
            try
            {
                var saves = Global.Saves;
                return saves != null && saves.IsUnlocked(id);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void Unlock(NamespaceID id)
        {
            try
            {
                var saves = Global.Saves;
                if (saves == null || !NamespaceID.IsValid(id))
                    return;
                if (!saves.IsUnlocked(id))
                    saves.Unlock(id);
                SaveNow();
            }
            catch (Exception e)
            {
                LogOnce("unlock-" + (id == null ? "?" : id.Path), "解锁失败：" + e.Message);
            }
        }

        public static void Relock(NamespaceID id)
        {
            try
            {
                var saves = Global.Saves;
                if (saves == null || !NamespaceID.IsValid(id))
                    return;
                if (saves.IsUnlocked(id))
                    saves.Relock(id);
                SaveNow();
            }
            catch (Exception e)
            {
                LogOnce("relock-" + (id == null ? "?" : id.Path), "取消解锁失败：" + e.Message);
            }
        }

        /// <summary>
        /// 解锁全部关卡：按「关卡通关解锁」的命名规则生成 ID 再逐个解锁。
        /// 只动关卡，不牵连成就与商店 —— 所以这个按钮是精确的。
        /// </summary>
        public static int UnlockAllLevels()
        {
            var count = 0;
            try
            {
                var stages = GetAllStageIDs();
                var saves = Global.Saves;
                if (saves == null)
                    return 0;
                for (var i = 0; i < stages.Count; i++)
                {
                    var stageID = stages[i];
                    var unlockID = LogicUnlockID.GetLevelClearUnlock(stageID);
                    if (!NamespaceID.IsValid(unlockID))
                        continue;
                    if (saves.IsUnlocked(unlockID))
                        continue;
                    saves.Unlock(unlockID);
                    count++;
                }
                SaveNow();
                Plugin.Log?.LogInfo("[Progress] 解锁全部关卡：新增 " + count + " 项（候选关卡 " + stages.Count + "）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Progress] 解锁全部关卡失败：" + e);
            }
            return count;
        }

        /// <summary>
        /// 解锁全部成就。
        ///
        /// ⚠️ 实现说明（这一点必须讲清楚）：原版**没有**「按成就解锁」的独立入口 ——
        /// 成就是由条件（<c>AchievementMeta.Unlock</c>）判定的，条件本身在
        /// <c>ResourceManager.AddConditionListUnlocks</c> 里注册成了普通的 unlock。
        /// 所以「解锁全部成就」实际做的是「解锁全部已注册的解锁条件」，
        /// 它会**连带**把关卡解锁与商店解锁一起打开。对测试用途这是最省事也最稳的做法，
        /// 但按钮的行为比名字宽 —— 面板上给了说明。
        /// </summary>
        public static int UnlockAllAchievements()
        {
            var count = 0;
            try
            {
                var game = Global.Game;
                var saves = Global.Saves;
                if (game == null || saves == null)
                    return 0;
                var all = game.GetAllUnlockConditions();
                if (all == null)
                    return 0;
                for (var i = 0; i < all.Length; i++)
                {
                    var id = all[i];
                    if (!NamespaceID.IsValid(id))
                        continue;
                    if (saves.IsUnlocked(id))
                        continue;
                    saves.Unlock(id);
                    count++;
                }
                SaveNow();
                Plugin.Log?.LogInfo("[Progress] 解锁全部成就（＝全部解锁条件）：新增 " + count + " 项（候选 " + all.Length + "）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Progress] 解锁全部成就失败：" + e);
            }
            return count;
        }

        private static List<NamespaceID> GetAllStageIDs()
        {
            var result = new List<NamespaceID>();
            try
            {
                var game = Global.Game;
                if (game == null)
                    return result;
                // 关卡外没有 LevelEngine，拿不到 level.Content；
                // 但 Global.Game 的实现类同时就是 IGameContent（同一份 native 对象），
                // 所以按指针包一个 IGameContent 壳来复用官方的定义枚举。
                var content = new IGameContent(game.Pointer);
                var defs = ContentProviderHelper.GetAllStageDefinitions(content);
                if (defs == null)
                    return result;
                for (var i = 0; i < defs.Length; i++)
                {
                    var def = defs[i];
                    if (def == null)
                        continue;
                    var id = def.GetID();
                    if (NamespaceID.IsValid(id) && !LevelFacade.Contains(result, id))
                        result.Add(id);
                }
            }
            catch (Exception e)
            {
                LogOnce("stages", "列关卡失败：" + e.Message);
            }
            return result;
        }
        #endregion

        #region 商店
        /// <summary>
        /// 「移除商店已购买项」：把所有商品的各阶段解锁条件重新锁上，
        /// 效果等于"商店回到未购买状态"（<c>SaveManager.EvaluateUnlockedProducts</c>
        /// 会据此重算 <c>unlockedProductsCache</c>）。
        ///
        /// ⚠️ 副作用（面板上提示了）：<c>ProductStageMeta.Unlocks</c> 是**商品自带的解锁项**，
        /// 重新锁上会把这些附带解锁一并收回（例如"买完解锁某器械/图鉴条目"）。
        /// </summary>
        public static int RemovePurchasedStoreItems()
        {
            var count = 0;
            try
            {
                var main = Main;
                var saves = Global.Saves;
                if (main == null || main.ResourceManager == null || saves == null)
                    return 0;
                var products = main.ResourceManager.GetAllProductsID();
                if (products == null)
                    return 0;
                for (var i = 0; i < products.Length; i++)
                {
                    var productID = products[i];
                    if (!NamespaceID.IsValid(productID))
                        continue;
                    var meta = main.ResourceManager.GetProductMeta(productID);
                    if (meta == null)
                        continue;
                    var stages = meta.Stages;
                    if (stages == null)
                        continue;
                    for (var s = 0; s < stages.Length; s++)
                    {
                        var stage = stages[s];
                        if (stage == null)
                            continue;
                        var unlockID = stage.Unlocks;
                        if (!NamespaceID.IsValid(unlockID))
                            continue;
                        if (!saves.IsUnlocked(unlockID))
                            continue;
                        saves.Relock(unlockID);
                        count++;
                    }
                }
                SaveNow();
                Plugin.Log?.LogInfo("[Progress] 移除商店已购买项：收回 " + count + " 个商品解锁");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Progress] 移除商店已购买项失败：" + e);
            }
            return count;
        }
        #endregion

        private static void SaveNow()
        {
            try
            {
                var main = Main;
                if (main != null && main.SaveManager != null)
                    main.SaveManager.SaveToFile();
            }
            catch (Exception e)
            {
                LogOnce("save", "保存存档失败：" + e.Message);
            }
        }

        private static readonly HashSet<string> loggedKeys = new HashSet<string>();

        private static void LogOnce(string key, string message)
        {
            if (loggedKeys.Add(key))
                Plugin.Log?.LogWarning("[Progress] " + message);
        }
    }
}
