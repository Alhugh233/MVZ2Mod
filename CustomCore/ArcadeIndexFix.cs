using System;
using HarmonyLib;
using MVZ2.Managers;
using MVZ2.Modding;

namespace CustomCore
{
    /// <summary>
    /// arcade 条目 Index 平移（按 CoreRegistry 泛化）。
    ///
    /// 背景：ArcadeMeta.Index 是"文件内同类条目顺序"（ArcadeMetaList.FromXmlNode 按位置分配），
    /// 外部 mod 的 arcade.xml minigame 拿到 0/1/2，与 vanilla 0/1/2 撞车。
    /// 0.7 ArcadeController.GetIDListByArcadeOrder 仍按 Index 建数组 + FirstOrDefault
    /// 一槽留一个 → 我们的条目会顶掉 vanilla 条目。
    ///
    /// 修复：在 ModManager.LoadMods 之后（所有 Mod 的 arcade meta 均已注入），
    /// 对每个注册 mod 的 ArcadeMeta.Index 平移到 vanilla 之后。
    /// </summary>
    internal static class ArcadeIndexFix
    {
        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            var harmony = new Harmony("qoznos.mvz2.core.arcadeindexfix");
            harmony.Patch(
                typeof(ModManager).GetMethod(nameof(ModManager.LoadMods)),
                postfix: new HarmonyMethod(typeof(ArcadeIndexFix), nameof(Postfix)));
            Plugin.Log?.LogInfo("ArcadeIndexFix patch applied");
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                var main = MainManager.Instance;
                if (main == null || main.ResourceManager == null)
                    return;
                var res = main.ResourceManager;
                foreach (var reg in CoreRegistry.GetAll())
                {
                    ShiftIndexes(res, reg.Namespace);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ArcadeIndexFix] error: " + e);
            }
        }

        private static void ShiftIndexes(ResourceManager res, string nsp)
        {
            var vanilla = res.GetArcadeMetaList("mvz2");
            if (vanilla == null || vanilla.metas == null)
            {
                Plugin.Log?.LogWarning("[ArcadeIndexFix] vanilla arcade list unavailable for " + nsp);
                return;
            }
            var ours = res.GetArcadeMetaList(nsp);
            if (ours == null || ours.metas == null)
                return;

            // 找 vanilla minigame 的最大 Index（ArcadeMetaList 是"整文件所有类型条目"，
            // 但 GetIDListByArcadeOrder 按 type 过滤后建数组 —— 需对同 type 内错开）。
            // 稳妥做法：把本 mod 全部 arcade 条目平移一个"比 vanilla 总数大"的偏移。
            var vanillaMax = 0;
            for (var i = 0; i < vanilla.metas.Length; i++)
            {
                if (vanilla.metas[i] != null && vanilla.metas[i].Index >= vanillaMax)
                    vanillaMax = vanilla.metas[i].Index;
            }
            var offset = vanillaMax + 1;
            var changed = false;
            for (var i = 0; i < ours.metas.Length; i++)
            {
                var meta = ours.metas[i];
                if (meta == null)
                    continue;
                var old = meta.Index;
                meta._Index_k__BackingField = old + offset;
                changed = true;
            }
            if (changed)
                Plugin.Log?.LogInfo("[ArcadeIndexFix] arcade indexes for " + nsp
                    + " shifted by +" + offset);
        }
    }
}
