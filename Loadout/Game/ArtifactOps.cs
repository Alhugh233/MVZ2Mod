using System;
using System.Collections.Generic;
using MVZ2.Managers;
using MVZ2Logic;
using MVZ2Logic.Artifacts;
using MVZ2Logic.Games;
using MVZ2Logic.Level;
using PVZEngine;
using PVZEngine.Level;
using UnityEngine;

namespace Loadout
{
    /// <summary>
    /// 页面3（制品）用的游戏侧封装。
    ///
    /// <para>制品挂在关卡的 <c>ArtifactList</c> 上，**随 .lvl 存盘**，所以面板对它的改动是持久的。</para>
    ///
    /// <para>⚠️ <b>删除必须自己清光环</b>：官方 <c>ArtifactList.SetArtifact(null)</c> 只调
    /// <c>Definition.PostRemove</c>，而光环 buff 的清理（<c>AuraEffect.ClearBuffs</c>）是私有的，
    /// 唯一公开入口是 <c>AuraEffect.PostRemove()</c>。不清就会留下永久 buff
    /// （例：删掉牛角后每张卡上还挂着 <c>mvz2:cow_horn</c>）。</para>
    /// </summary>
    internal static class ArtifactOps
    {
        private static MainManager Main { get { return MainManager.Instance; } }

        #region 查询
        public static int GetSlotCount(LevelEngine level)
        {
            try { return level == null ? 0 : level.GetArtifactSlotCount(); }
            catch (Exception) { return 0; }
        }

        public static Artifact GetAt(LevelEngine level, int slot)
        {
            try { return level == null ? null : level.GetArtifactAt(slot); }
            catch (Exception) { return null; }
        }

        public static NamespaceID IdOf(Artifact artifact)
        {
            try { return artifact == null || artifact.Definition == null ? null : artifact.Definition.GetID(); }
            catch (Exception) { return null; }
        }

        public static string NameOf(NamespaceID id)
        {
            try
            {
                var main = Main;
                if (main == null || main.ResourceManager == null)
                    return Text(id);
                var name = main.ResourceManager.GetArtifactName(id);
                return string.IsNullOrEmpty(name) ? Text(id) : name;
            }
            catch (Exception) { return Text(id); }
        }

        public static Sprite IconOf(NamespaceID id)
        {
            try
            {
                var main = Main;
                var level = LevelFacade.Current;
                if (main == null || level == null)
                    return null;
                var def = level.Content.GetArtifactDefinition(id);
                if (def == null)
                    return null;
                return main.GetSprite(def.GetSpriteReference());
            }
            catch (Exception) { return null; }
        }

        /// <summary>全部制品 id（按名称排序）。</summary>
        public static List<NamespaceID> AllArtifactIDs()
        {
            var result = new List<NamespaceID>();
            try
            {
                var level = LevelFacade.Current;
                var game = Global.Game;
                if (level == null || game == null)
                    return result;
                // 注：interop 里 IGlobalGame 没列出 IGameContent 接口（跨程序集接口被生成器漏了），
                // 所以不能直接用扩展方法，要拿个真正声明成 IGameContent 的接收器。
                var content = level.Content;
                if (content == null)
                    return result;
                var defs = content.GetAllArtifactDefinitions();
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
                LogOnce("all", "枚举制品失败：" + e.Message);
            }
            return result;
        }

        /// <summary>已解锁的制品（选择窗口用这个筛）。</summary>
        public static bool IsUnlocked(NamespaceID id)
        {
            try
            {
                var saves = Global.Saves;
                return saves != null && saves.IsArtifactUnlocked(id);
            }
            catch (Exception) { return true; }
        }
        #endregion

        #region 增删
        /// <summary>
        /// 添加 <paramref name="count"/> **份**（每份都是独立实例，光环各自叠加）。
        /// 槽位不够会自动扩容。
        /// </summary>
        public static int Add(LevelEngine level, NamespaceID id, int count)
        {
            if (level == null || !NamespaceID.IsValid(id) || count <= 0)
                return 0;
            var added = 0;
            try
            {
                var def = level.Content.GetArtifactDefinition(id);
                if (def == null)
                    return 0;
                for (var i = 0; i < count; i++)
                {
                    var slot = level.GetArtifactSlotCount();
                    level.SetArtifactSlotCount(slot + 1);
                    level.ReplaceArtifact(slot, def);
                    added++;
                }
                Plugin.Log?.LogInfo("[Artifact] 添加 " + NameOf(id) + " ×" + added);
            }
            catch (Exception e)
            {
                LogOnce("add", "添加制品失败：" + e.GetType().Name + ": " + e.Message);
            }
            return added;
        }

        /// <summary>删掉某一槽（先清光环，再摘制品，最后把这一格从数组里剔掉）。</summary>
        public static bool RemoveAt(LevelEngine level, int slot)
        {
            if (level == null || slot < 0 || slot >= GetSlotCount(level))
                return false;
            try
            {
                var artifact = level.GetArtifactAt(slot);
                var name = NameOf(IdOf(artifact));
                DisposeAuras(artifact);
                level.SetArtifact(slot, null);

                // 槽位数组里留个空位没有意义（HUD 会照显示空框）→ 把后面的往前挪一格并缩容。
                var count = level.GetArtifactSlotCount();
                for (var i = slot; i < count - 1; i++)
                    level.SetArtifact(i, level.GetArtifactAt(i + 1));
                level.SetArtifact(count - 1, null);
                level.SetArtifactSlotCount(count - 1);
                Plugin.Log?.LogInfo("[Artifact] 移除 " + name + "（第 " + (slot + 1) + " 格）");
                return true;
            }
            catch (Exception e)
            {
                LogOnce("remove", "移除制品失败：" + e.Message);
                return false;
            }
        }

        public static void Clear(LevelEngine level)
        {
            if (level == null)
                return;
            try
            {
                var count = level.GetArtifactSlotCount();
                for (var i = 0; i < count; i++)
                    DisposeAuras(level.GetArtifactAt(i));
                for (var i = 0; i < count; i++)
                    level.SetArtifact(i, null);
                Plugin.Log?.LogInfo("[Artifact] 已清空（" + count + " 格）");
            }
            catch (Exception e)
            {
                LogOnce("clear", "清空制品失败：" + e.Message);
            }
        }

        /// <summary>清掉某个制品实例的全部光环 buff（见类注释里的说明）。</summary>
        public static void DisposeAuras(Artifact artifact)
        {
            if (artifact == null)
                return;
            try
            {
                var n = artifact.GetAuraEffectCount();
                for (var i = 0; i < n; i++)
                {
                    var aura = artifact.GetAuraEffectAt(i);
                    if (aura != null)
                        aura.PostRemove();
                }
            }
            catch (Exception e)
            {
                LogOnce("aura", "清光环失败：" + e.GetType().Name);
            }
        }
        #endregion

        #region 单制品属性
        public static int GetNumber(Artifact artifact)
        {
            try { return artifact == null ? -1 : artifact.GetNumber(); }
            catch (Exception) { return -1; }
        }

        public static bool SetNumber(Artifact artifact, int value)
        {
            if (artifact == null)
                return false;
            try
            {
                artifact.SetNumber(value);
                return true;
            }
            catch (Exception) { return false; }
        }

        public static bool IsInactive(Artifact artifact)
        {
            try { return artifact != null && artifact.IsInactive(); }
            catch (Exception) { return false; }
        }

        public static bool SetInactive(Artifact artifact, bool value)
        {
            if (artifact == null)
                return false;
            try
            {
                artifact.SetInactive(value);
                return true;
            }
            catch (Exception) { return false; }
        }

        public static bool IsGlowing(Artifact artifact)
        {
            try { return artifact != null && artifact.GetGlowing(); }
            catch (Exception) { return false; }
        }

        public static string GetDisplayText(Artifact artifact)
        {
            try { return artifact == null ? null : artifact.GetDisplayText(); }
            catch (Exception) { return null; }
        }
        #endregion

        #region 工具
        private static bool Contains(List<NamespaceID> list, NamespaceID id)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].Equals(id))
                    return true;
            }
            return false;
        }

        private static string Text(NamespaceID id)
        {
            if (id == null)
                return "（无）";
            try { return id.SpaceName + ":" + id.Path; }
            catch (Exception) { return "?"; }
        }

        /// <summary>诊断用：往关卡里塞几个制品（自测里用来让制品页有东西可画）。</summary>
        public static string DebugFill(LevelEngine level, int count)
        {
            if (level == null)
                return "不在关卡内";
            try
            {
                // ⚠️ 必须先清零槽位再放：读档会把「槽位数量」带进来（实测开局就有 3 格），
                //    那 3 格是空的，我们再加就落到 3/4/5 —— 面板显示的是 0..count-1，
                //    于是看起来「一个图标都没有」（自测里踩过这个坑）。
                Clear(level);
                level.SetArtifactSlotCount(0);

                var ids = AllArtifactIDs();
                if (ids.Count == 0)
                    return "没有可用的制品定义";
                var names = new List<string>();
                for (var i = 0; i < count; i++)
                {
                    var id = ids[i % ids.Count];
                    if (Add(level, id, 1) <= 0)
                        break;
                    names.Add(NameOf(id));
                }
                return "已加 " + names.Count + " 个制品：" + string.Join(", ", names.ToArray());
            }
            catch (Exception e)
            {
                return "加制品失败：" + e.GetType().Name + ": " + e.Message;
            }
        }

        private static readonly HashSet<string> logged = new HashSet<string>();
        private static void LogOnce(string key, string message)
        {
            if (logged.Add(key))
                Plugin.Log?.LogWarning("[Artifact] " + message);
        }
        #endregion
    }
}
