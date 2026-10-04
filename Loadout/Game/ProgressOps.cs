using System;
using System.Collections.Generic;
using MVZ2.Managers;
using MVZ2Logic;
using MVZ2Logic.Saves;
using MVZ2Logic.Unlocks;
using PVZEngine;
using PVZEngine.Level;
using UnityEngine;

namespace Loadout
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

        /// <summary>
        /// 自测：验证「批量锁定不会被批末的官方重算反悔」。
        ///
        /// <para>做法（**不依赖存档现状**，跑完恢复原状）：挑一个解锁条件定义，先把它解锁成
        /// “当前唯一的已解锁项”，再走一次真正的「批内锁定 → EndBatch」，断言它仍然是未解锁。
        /// 早期实现把“批内第一个写过的 id”当探针用，那种情况下正好会把它又解锁回去 ——
        /// 这个自测就是盯着那个行为的。</para>
        /// </summary>
        public static string DebugBatchLockTest()
        {
            var sb = new System.Text.StringBuilder(200);
            NamespaceID probe = null;
            var before = false;
            try
            {
                var ids = GetAllUnlockConditions();
                if (ids.Count == 0)
                    return "批量锁定自测：拿不到解锁条件定义，跳过";
                probe = ids[0];
                before = IsUnlocked(probe);
                var text = Text(probe);

                // 先让它成为“当前唯一的已解锁项”（若本来就没解锁）
                if (!before)
                    Unlock(probe);

                BeginBatch();
                Relock(probe);
                EndBatch();

                var after = IsUnlocked(probe);
                sb.Append("批量锁定自测：").Append(text)
                  .Append("  批前已解锁=").Append(before ? 1 : 0)
                  .Append("  批后已解锁=").Append(after ? 1 : 0)
                  .Append("  判据=").Append(!after ? "OK" : "NG");
            }
            catch (Exception e)
            {
                sb.Append("批量锁定自测失败：").Append(e.GetType().Name);
            }
            finally
            {
                try
                {
                    if (probe != null)
                    {
                        if (before)
                            Unlock(probe);
                        else
                            Relock(probe);
                    }
                    sb.Append("  已恢复=").Append(probe != null && IsUnlocked(probe) == before ? 1 : 0);
                }
                catch (Exception) { }
            }
            return sb.ToString();
        }

        private static string Text(NamespaceID id)
        {
            if (id == null)
                return "（无）";
            try { return id.SpaceName + ":" + id.Path; }
            catch (Exception) { return "?"; }
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

        /// <summary>
        /// **全部**解锁条件的定义（关卡 / 商店 / 成就 / 图鉴 / 角色 / 音乐 …）——
        /// 来源是 <c>ResourceManager.unlockConditionList</c>（定义侧，实测 155 项），不是用户存档。
        ///
        /// <para>为什么不只用 <see cref="GetAllUnlocks"/>（存档侧）：那侧只有在存档完整加载后才非空，
        /// 而且它只回答"已解锁了哪些"；而页面需要的是"**所有可操作的条目** + 哪几条已解锁"。
        /// 而且早期的实现在标题界面拿到过 0 项 —— 定义侧才是稳定的来源。</para>
        /// </summary>
        public static List<NamespaceID> GetAllUnlockConditions()
        {
            var result = new List<NamespaceID>();
            try
            {
                var main = LevelFacade.Main;
                if (main == null)
                {
                    LogOnce("unlockconds-main", "读解锁条件定义失败：主场景还没就绪（Main 为 null）");
                    return result;
                }
                if (main.ResourceManager == null)
                {
                    LogOnce("unlockconds-rm", "读解锁条件定义失败：ResourceManager 为 null（资源侧未就绪）");
                    return result;
                }
                var list = main.ResourceManager.GetAllUnlockConditions();
                if (list == null)
                {
                    LogOnce("unlockconds-null", "读解锁条件定义：GetAllUnlockConditions 返回 null");
                    return result;
                }
                for (var i = 0; i < list.Length; i++)
                {
                    var id = list[i];
                    if (id != null && !Contains(result, id))
                        result.Add(id);
                }
            }
            catch (Exception e)
            {
                LogOnce("unlockconds", "读解锁条件定义失败：" + e.GetType().Name + ": " + e.Message);
            }
            return result;
        }

        private static bool Contains(List<NamespaceID> list, NamespaceID id)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].Equals(id))
                    return true;
            }
            return false;
        }

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

        /// <summary>
        /// 批量写解锁时用的开关。
        ///
        /// <para><b>为什么非要它：</b>官方的 <c>SaveManager.Unlock/Relock</c> 内部每次都会调
        /// <c>EvaluateUnlocks(false)</c> —— 那是**全量重算**（遍历所有实体/制品/商店/诅咒/成就定义，
        /// 每条都重算解锁条件）。批量解锁 155 条 = 重算 155 遍，实测点击按钮会卡死好几秒。
        /// 批内改成直接写 <c>ModSaveData</c>（HashSet 的 Add/Remove），批末再补一次官方评估
        /// （<c>EvaluateUnlocks</c> 是私有的，而 <c>Unlock()</c> 无条件会调它 —— 所以补一次
        /// **幂等**的 <c>Unlock</c> 就等价于“手动调一次评估”）。</para>
        /// </summary>
        private static bool _batch;
        private static int _batchCount;

        /// <summary>进入批量模式：批内的解锁/锁定不落盘、不重算（必须配 <see cref="EndBatch"/>）。</summary>
        public static void BeginBatch()
        {
            _batch = true;
            _batchCount = 0;
        }

        /// <summary>
        /// 结束批量：做**一次**官方重算（含成就弹出提示）＋**一次**落盘。
        ///
        /// <para><b>借来跑重算的那个 id 必须是「本来就已经解锁」的</b>。
        /// 早期实现记的是「批内第一个写过的 id」—— 那对解锁批是无害的，但对**锁定批**
        /// 就等于把刚锁掉的第一条又 <c>Unlock</c> 回去（实测症状：锁定按钮对勾选的
        /// 第一条不生效，而解锁按钮看起来正常）。</para>
        /// </summary>
        public static void EndBatch()
        {
            _batch = false;
            var count = _batchCount;
            _batchCount = 0;
            try
            {
                // 幂等：先找一个现在就在解锁集合里的 id，用它的副作用跑一次全量评估。
                var probe = AnyUnlockedId();
                if (probe != null)
                    Global.Saves?.Unlock(probe);
                else if (count > 0)
                    Plugin.Log?.LogWarning("[ProgressOps] 批内 " + count
                        + " 次写完成后没有任何已解锁项，跳过官方重算（无可展示的变化）");
            }
            catch (Exception e)
            {
                LogOnce("batch-eval", "批量结束后触发官方重算失败：" + e.GetType().Name);
            }
            SaveNow();
        }

        /// <summary>当前解锁集合里的任意一条（拿不到返回 null）。</summary>
        private static NamespaceID AnyUnlockedId()
        {
            try
            {
                var saves = Global.Saves;
                if (saves == null)
                    return null;
                var all = saves.GetAllUnlocks();
                if (all == null)
                    return null;
                for (var i = 0; i < all.Length; i++)
                {
                    if (NamespaceID.IsValid(all[i]) && saves.IsUnlocked(all[i]))
                        return all[i];
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>批内直接写存档数据（绕开官方每次调用都做的全量重算）。</summary>
        private static bool WriteUnlockDirect(NamespaceID id, bool value)
        {
            var saves = Global.Saves;
            if (saves == null || !NamespaceID.IsValid(id))
                return false;
            var mod = saves.GetModSaveData(id.SpaceName);
            if (mod == null)
                return false;
            if (value)
                mod.Unlock(id.Path);
            else
                mod.Relock(id.Path);
            _batchCount++;
            return true;
        }

        public static void Unlock(NamespaceID id)
        {
            try
            {
                if (_batch)
                {
                    WriteUnlockDirect(id, true);
                    return;
                }
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
                if (_batch)
                {
                    WriteUnlockDirect(id, false);
                    return;
                }
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
            var t0 = Time.realtimeSinceStartup;
            BeginBatch();
            try
            {
                var stages = GetAllStageIDs();
                for (var i = 0; i < stages.Count; i++)
                {
                    var stageID = stages[i];
                    var unlockID = LogicUnlockID.GetLevelClearUnlock(stageID);
                    if (!NamespaceID.IsValid(unlockID))
                        continue;
                    if (IsUnlocked(unlockID))
                        continue;
                    WriteUnlockDirect(unlockID, true);
                    count++;
                }
                Plugin.Log?.LogInfo("[Progress] 解锁全部关卡：新增 " + count + " 项（候选关卡 " + stages.Count + "）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Progress] 解锁全部关卡失败：" + e);
            }
            finally
            {
                EndBatch();
            }
            Plugin.Log?.LogInfo("[Progress] 解锁全部关卡耗时 " + Elapsed(t0) + " ms（已合并为一次官方重算）");
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
            var t0 = Time.realtimeSinceStartup;
            BeginBatch();
            try
            {
                var game = Global.Game;
                if (game == null)
                    return 0;
                var all = game.GetAllUnlockConditions();
                if (all == null)
                    return 0;
                for (var i = 0; i < all.Length; i++)
                {
                    var id = all[i];
                    if (!NamespaceID.IsValid(id))
                        continue;
                    if (IsUnlocked(id))
                        continue;
                    WriteUnlockDirect(id, true);
                    count++;
                }
                Plugin.Log?.LogInfo("[Progress] 解锁全部成就（＝全部解锁条件）：新增 " + count + " 项（候选 " + all.Length + "）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Progress] 解锁全部成就失败：" + e);
            }
            finally
            {
                EndBatch();
            }
            Plugin.Log?.LogInfo("[Progress] 解锁全部成就耗时 " + Elapsed(t0) + " ms（已合并为一次官方重算）");
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
            var t0 = Time.realtimeSinceStartup;
            BeginBatch();
            try
            {
                var main = Main;
                if (main == null || main.ResourceManager == null)
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
                        if (!IsUnlocked(unlockID))
                            continue;
                        WriteUnlockDirect(unlockID, false);
                        count++;
                    }
                }
                Plugin.Log?.LogInfo("[Progress] 移除商店已购买项：收回 " + count + " 个商品解锁");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Progress] 移除商店已购买项失败：" + e);
            }
            finally
            {
                EndBatch();
            }
            Plugin.Log?.LogInfo("[Progress] 移除商店已购买项耗时 " + Elapsed(t0) + " ms（已合并为一次官方重算）");
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

        private static int Elapsed(float start)
        {
            try { return Mathf.RoundToInt((Time.realtimeSinceStartup - start) * 1000f); }
            catch (Exception) { return 0; }
        }

        private static readonly HashSet<string> loggedKeys = new HashSet<string>();

        private static void LogOnce(string key, string message)
        {
            if (loggedKeys.Add(key))
                Plugin.Log?.LogWarning("[Progress] " + message);
        }
    }
}
