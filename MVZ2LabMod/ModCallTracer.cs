using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MVZ2Logic.Modding;

namespace MVZ2LabMod
{
    /// <summary>
    /// 追踪游戏对 Mod 实例的所有原生方法调用（含虚方法 vtable 分派与直接调用）。
    /// 崩溃点 = 最后一条 [ModCall] 日志所标记的方法。
    /// </summary>
    internal static class ModCallTracer
    {
        public static void Apply()
        {
            var harmony = new Harmony("qoznos.mvz2.labmod.modcalltracer");
            var modType = typeof(Mod);
            var targets = new[]
            {
                "PreInit", "OnInit", "PostInit", "PostGameInit", "PostReloadMods",
                "CreateSaveData", "LoadSaveData", "PostAllSaveDataLoaded",
                "AddDefinition", "GetDefinitions", "ApplyGlobalCallbacks", "GetTriggers",
                "RegisterSerializableType", "Serialize", "Deserialize", "get_Namespace"
            };
            foreach (var name in targets)
            {
                var methods = AccessTools.GetDeclaredMethods(modType)
                    .Where(m => m.Name == name)
                    .ToArray();
                foreach (var method in methods)
                {
                    try
                    {
                        harmony.Patch(method,
                            prefix: new HarmonyMethod(typeof(ModCallTracer), nameof(TracePrefix)));
                    }
                    catch (Exception e)
                    {
                        Plugin.Log?.LogWarning("[ModCallTracer] patch failed for " + method + ": " + e.Message);
                    }
                }
            }
            Plugin.Log?.LogInfo("[ModCallTracer] applied, " + targets.Length + " targets");
        }

        [HarmonyPrefix]
        private static void TracePrefix(Mod __instance, MethodBase __originalMethod)
        {
            try
            {
                var ptr = __instance == null ? "null" : __instance.Pointer.ToString("X");
                var isLab = __instance != null && ModLogicFix.Instance != null
                    && __instance.Pointer == ModLogicFix.Instance.Pointer;
                Plugin.Log?.LogInfo("[ModCall] " + __originalMethod.Name + " ptr=" + ptr
                    + (isLab ? " <== 我们的实例" : ""));
            }
            catch
            {
                // 崩溃路径上日志绝不能抛异常
            }
        }
    }
}
