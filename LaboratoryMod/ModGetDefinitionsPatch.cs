using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2Logic.Modding;
using PVZEngine;

namespace LaboratoryMod
{
    /// <summary>
    /// 绕过官方 Mod.GetDefinitions 的原生 LINQ（DefinitionGroup.GetDefinitions 内部
    /// SelectMany + ToArray）。metadata v31 注入环境下该 LINQ 对注入 DefinitionGroup
    /// 非确定性抛 MethodAccessException / 返回空 → game.AddMod 拷不到本 mod 的 defs
    /// → GlobalGame 查不到 mvz2_lab:* → 街机列表隐藏 + ChangeStage MissingDefinition。
    ///
    /// 方案：拦截 Mod.GetDefinitions() 实例调用，对 mvz2_lab（Plugin.AllDefinitions
    /// 手工跟踪，非 LINQ）直接返回托管 Definition[]。vanilla 等其它 mod 走原逻辑。
    /// 时机：LoadStagesPatch.Prefix 内（LoadAll 的 LoadDefinitions 阶段）——AddDefinition
    /// 之后、官方 LoadStageProperties / game.AddMod（Load 之后）之前，patch 已生效。
    /// </summary>
    internal static class ModGetDefinitionsPatch
    {
        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            var harmony = new Harmony("qoznos.mvz2.labmod.getdefinitionspatch");
            var target = AccessTools.Method(typeof(Mod), "GetDefinitions");
            if (target == null)
            {
                Plugin.Log?.LogError("[ModGetDefinitionsPatch] Mod.GetDefinitions not found!");
                return;
            }
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(ModGetDefinitionsPatch), nameof(Prefix)));
            // 官方 MainManager.LoadManagersInit → ModManager.PostReloadMods 会对每个
            // mod 调 Mod.PostReloadMods(game)：其实现遍历 mod.definitionGroup.GetDefinitions()
            // （DefinitionGroup.GetDefinitions 内部原生 LINQ SelectMany + ToArray）——
            // 不走 Mod.GetDefinitions() 虚方法，故上面的 patch 拦不到。mvz2_lab 的
            // defs 全部不实现 ICachedDefinition，官方遍历结果无任何操作；但注入 defs
            // 参与 LINQ 枚举时 v31 非确定 MethodAccessException（本进程曾崩在
            // PostReloadMods）。因此对本 mod 直接跳过官方实现。
            var target2 = AccessTools.Method(typeof(Mod), "PostReloadMods");
            if (target2 == null)
            {
                Plugin.Log?.LogError("[ModGetDefinitionsPatch] Mod.PostReloadMods not found!");
            }
            else
            {
                harmony.Patch(target2,
                    prefix: new HarmonyMethod(typeof(ModGetDefinitionsPatch), nameof(PostReloadModsPrefix)));
            }
            Plugin.Log?.LogInfo("ModGetDefinitionsPatch applied");
        }

        [HarmonyPrefix]
        private static bool Prefix(Mod __instance, ref Il2CppReferenceArray<Definition> __result)
        {
            try
            {
                if (__instance == null || __instance.Namespace != LabMod.spaceName)
                    return true; // 其它 mod 走原逻辑（vanilla 等 LINQ 数据原生，安全）
                lock (Plugin.AllDefinitions)
                {
                    var list = Plugin.AllDefinitions;
                    var arr = new Il2CppReferenceArray<Definition>(list.Count);
                    for (int i = 0; i < list.Count; i++)
                    {
                        arr[i] = list[i];
                    }
                    __result = arr;
                }
                Plugin.Log?.LogInfo("[ModGetDefinitionsPatch] GetDefinitions intercepted, returned "
                    + Plugin.AllDefinitions.Count + " defs");
                return false; // 跳过原 LINQ 方法
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ModGetDefinitionsPatch] Prefix error: " + e);
                return true;
            }
        }

        [HarmonyPrefix]
        private static bool PostReloadModsPrefix(Mod __instance)
        {
            try
            {
                if (__instance == null || __instance.Namespace != LabMod.spaceName)
                    return true; // vanilla 等其它 mod 走原逻辑（原生 defs LINQ 安全）
                Plugin.Log?.LogInfo("[ModGetDefinitionsPatch] PostReloadMods skipped for mvz2_lab");
                return false; // 跳过官方遍历（本 mod defs 无 ICachedDefinition，无操作）
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ModGetDefinitionsPatch] PostReloadModsPrefix error: " + e);
                return true;
            }
        }
    }
}
