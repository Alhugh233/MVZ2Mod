using System;
using System.Globalization;
using PVZEngine;
using PVZEngine.Level;
using PVZEngine.SeedPacks;

namespace Loadout
{
    /// <summary>
    /// 面板里「**哪一张卡**」的键 —— <see cref="BlueprintFlags"/> 用它存标记，
    /// 生成期补丁用它判断「这次生成是不是这张卡放出来的」。
    ///
    /// <para><b>两种键</b>：</para>
    /// <list type="bullet">
    /// <item><c>s:&lt;下标&gt;</c> —— 自选卡模式下**卡槽里的这一张**。
    /// 同一种蓝图可以带多张（选卡浮层允许重复），每一张各自一套设置 ——
    /// 这正是需求「玩家应该要能携带三个血量不同的僵尸的蓝图」的前提。</item>
    /// <item><c>i:&lt;蓝图 id&gt;</c> —— 传送带模式下**卡池里的这一条**（卡是中途抽出来的，
    /// 没有「那一格」可挂），以及「拿着一张蓝图掉落物去放」那条没有 SeedPack 的路径。</item>
    /// </list>
    ///
    /// <para><b>为什么不按「蓝图 id」一把抓</b>（早期版本就是这么写的，两个毛病）：
    /// ① 三张同种蓝图只能共享一个值；② 命中表是「蓝图 id → 实体 id」，
    /// 于是**自然生成**的同种实体（比如出怪的僵尸）也被改了血量。</para>
    ///
    /// <para>键只在**放置那一刻**解析（<see cref="PlacementScope"/>），
    /// 所以只对真的从这张卡放出来的实体生效。</para>
    /// </summary>
    internal static class BlueprintKeys
    {
        /// <summary>槽位键的前缀。</summary>
        public const string SlotTag = "s:";

        /// <summary>蓝图 id 键的前缀。</summary>
        public const string IdTag = "i:";

        /// <summary>第 <paramref name="slot"/> 格（0 起）。</summary>
        public static string Slot(int slot)
        {
            return SlotTag + slot.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>蓝图 id 文本（<c>nsp:path</c>）→ <c>i:</c> 键。</summary>
        public static string OfIdText(string idText)
        {
            return string.IsNullOrEmpty(idText) ? null : IdTag + idText;
        }

        public static string OfId(NamespaceID id)
        {
            if (!NamespaceID.IsValid(id))
                return null;
            try { return OfIdText(id.SpaceName + ":" + id.Path); }
            catch (Exception) { return null; }
        }

        /// <summary>是不是槽位键；是的话给出下标。</summary>
        public static bool TrySlot(string key, out int slot)
        {
            slot = -1;
            if (string.IsNullOrEmpty(key) || !key.StartsWith(SlotTag, StringComparison.Ordinal))
                return false;
            return int.TryParse(key.Substring(SlotTag.Length), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out slot);
        }

        /// <summary>键里的蓝图 id 文本；槽位键返回 null。</summary>
        public static string IdTextOf(string key)
        {
            if (string.IsNullOrEmpty(key) || !key.StartsWith(IdTag, StringComparison.Ordinal))
                return null;
            return key.Substring(IdTag.Length);
        }

        /// <summary>
        /// **这张卡**对应的键。
        ///
        /// <para>做的判定就一条：这张 SeedPack 是不是**正好坐在某个卡槽里** ——
        /// 是就按槽位（同种蓝图的多张互不干扰），否则退化成蓝图 id
        /// （传送带抽出来的卡、蓝图掉落物）。</para>
        /// </summary>
        public static string OfPack(LevelEngine level, SeedPack pack)
        {
            if (level == null || pack == null)
                return null;
            try
            {
                var classic = pack.TryCast<ClassicSeedPack>();
                if (classic != null)
                {
                    var count = level.GetSeedSlotCount();
                    for (var i = 0; i < count; i++)
                    {
                        var at = level.GetSeedPackAt(i);
                        if (at == null)
                            continue;
                        if (at.Pointer == classic.Pointer)
                            return Slot(i);
                    }
                }
            }
            catch (Exception) { }
            return OfId(SafeDefinition(pack));
        }

        /// <summary>槽位 -> 键（槽里没有卡就返回 null）。</summary>
        public static string OfSlot(LevelEngine level, int slot)
        {
            var pack = BlueprintOps.GetPackAt(level, slot);
            if (pack == null)
                return null;
            return Slot(slot);
        }

        private static NamespaceID SafeDefinition(SeedPack pack)
        {
            try { return pack.GetDefinitionID(); }
            catch (Exception) { return null; }
        }

        /// <summary>日志/自测用的中文描述。</summary>
        public static string Describe(string key)
        {
            if (string.IsNullOrEmpty(key))
                return "（空）";
            int slot;
            if (TrySlot(key, out slot))
                return "槽" + slot;
            var id = IdTextOf(key);
            return id == null ? key : ("蓝图 " + id);
        }
    }
}
