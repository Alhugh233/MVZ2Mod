using HarmonyLib;
using MVZ2.Saves;

namespace DSHCore
{
    /// <summary>
    /// 存档读写兜底补丁（按 CoreRegistry 泛化）。
    /// SaveManager.LoadModData / WriteSaveData 会对注入 Mod 的 Logic 做 IL2CPP
    /// vtable 虚调用（LoadSaveData / CreateSaveData），若 vtable 抽象槽被 Class::Init
    /// 重置为 thunk 则抛 EntryPointNotFoundException。在调用前对已注册的注入类型
    /// 统一执行 VTableRebind.Ensure（幂等，开销极小）。
    /// </summary>
    internal static class SaveManagerPatch
    {
        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            var harmony = new Harmony("qoznos.mvz2.core.savemanagerpatch");
            var loadTarget = AccessTools.Method(typeof(SaveManager), "LoadModData");
            if (loadTarget != null)
            {
                harmony.Patch(
                    loadTarget,
                    prefix: new HarmonyMethod(typeof(SaveManagerPatch), nameof(Prefix)));
                Plugin.Log?.LogInfo("SaveManager.LoadModData patch applied");
            }
            else
            {
                Plugin.Log?.LogWarning("SaveManager.LoadModData not found, patch skipped");
            }

            var writeTarget = AccessTools.Method(typeof(SaveManager), "WriteSaveData");
            if (writeTarget != null)
            {
                harmony.Patch(
                    writeTarget,
                    prefix: new HarmonyMethod(typeof(SaveManagerPatch), nameof(Prefix)));
                Plugin.Log?.LogInfo("SaveManager.WriteSaveData patch applied");
            }
            else
            {
                Plugin.Log?.LogWarning("SaveManager.WriteSaveData not found, patch skipped");
            }
        }

        [HarmonyPrefix]
        private static void Prefix()
        {
            foreach (var reg in CoreRegistry.GetAll())
            {
                if (reg.LogicType != null)
                    VTableRebind.Ensure(reg.LogicType);
            }
        }
    }
}
