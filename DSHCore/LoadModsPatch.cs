using HarmonyLib;
using MVZ2.Modding;

namespace DSHCore
{
    /// <summary>
    /// 在游戏 ModManager.LoadMods（0.7 官方加载入口）执行前，
    /// 对所有已注册的注入 Mod 类型执行 VTableRebind.Ensure。
    ///
    /// 背景：注入类实例由内容 mod 在 OnRegisterModInfos 中创建；游戏随后在
    /// LoadAll 里对其做原生虚调用（PreInit/OnInit/AddMod 等）。若 vtable 槽因
    /// Class::Init 被重置，分派会静默失效或崩溃。这里在游戏开始使用前兜底修复。
    /// </summary>
    internal static class LoadModsPatch
    {
        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            var harmony = new Harmony("qoznos.mvz2.core.loadmodspatch");
            harmony.Patch(
                typeof(ModManager).GetMethod(nameof(ModManager.LoadMods)),
                prefix: new HarmonyMethod(typeof(LoadModsPatch), nameof(Prefix)));
            Plugin.Log?.LogInfo("ModManager.LoadMods patch applied");
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
