using System;
using HarmonyLib;
using MVZ2.GlobalGames;
using MVZ2.Modding;
using MVZ2Logic.Games;

namespace MVZ2LabMod
{
    /// <summary>
    /// 0.7.0 的 ModManager.LoadMods 会重建每个 ModInfo 的 Logic
    /// （重构半成品：创建的实例不是我们注册的 LabMod，
    /// 而是原生 Mod 类实例，其抽象方法槽 CreateSaveData/LoadSaveData 无实现，
    /// 存档加载时虚调用抛 EntryPointNotFoundException）。
    ///
    /// 此补丁在 LoadMods 之后把 mvz2_lab 的 Logic 换回我们的 LabMod 实例，
    /// 并手动执行 OnInit（注册关卡定义）。
    /// </summary>
    internal static class ModLogicFix
    {
        /// <summary>共享的 LabMod 实例（定义注册与存档读写用同一个实例）。</summary>
        internal static LabMod Instance;

        public static void Apply()
        {
            var harmony = new Harmony("qoznos.mvz2.labmod.modlogicfix");
            harmony.Patch(
                typeof(ModManager).GetMethod(nameof(ModManager.LoadMods)),
                prefix: new HarmonyMethod(typeof(ModLogicFix), nameof(LoadModsPrefix)),
                postfix: new HarmonyMethod(typeof(ModLogicFix), nameof(Postfix)));
            Plugin.Log?.LogInfo("ModManager.LoadMods logic fix patch applied");
        }

        [HarmonyPrefix]
        private static void LoadModsPrefix(ModManager __instance)
        {
            try
            {
                Plugin.Log?.LogInfo("[ModLogicFix] LoadMods begin");
                var infos = __instance.GetAllModInfos();
                if (infos == null) return;
                foreach (var info in infos)
                {
                    if (info == null) continue;
                    var logic = info._Logic_k__BackingField;
                    string logicInfo = logic == null ? "null" : "ptr=" + logic.Pointer.ToString("X");
                    Plugin.Log?.LogInfo("[ModLogicFix] LoadMods pre: " + info.Namespace + " logic " + logicInfo);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ModLogicFix] LoadModsPrefix error: " + e);
            }
        }

        [HarmonyPostfix]
        private static void Postfix(ModManager __instance, GlobalGame game)
        {
            try
            {
                var infos = __instance.GetAllModInfos();
                if (infos == null) return;
                foreach (var info in infos)
                {
                    if (info == null || info.Namespace != LabMod.spaceName) continue;
                    Plugin.Log?.LogInfo("[ModLogicFix] Replacing Logic for " + info.Namespace
                        + ", was " + (info.Logic == null ? "null" : info.Logic.GetType().FullName));
                    Instance = LabMod.CreateInstance();
                    // ModInfo.set_Logic 在 0.7.0 中疑似无效，直接写 backing field。
                    info._Logic_k__BackingField = Instance;
                    // new LabMod() 会触发 IL2CPP Class::Init 重建注入类 vtable，
                    // 抽象槽 21/22 会被重置为抽象 thunk——立刻重新绑定。
                    VTableRebind.Ensure();
                    // 游戏已对"错误实例"做过 PreInit/OnInit，这里对我们的实例补一次。
                    // 代理丢失了 GlobalGame 的 IGlobalGame 接口声明，需按官方文档包装。
                    Instance.OnInit(new IGlobalGame(game.Pointer));
                    // 把定义注册进游戏全局 definitionGroup（复刻 game.AddMod 的行为）。
                    // 注意：这是对注入类实例的接口分派调用——若 metadata v31 的
                    // Il2CppClass 结构体偏移与 ClassInjector 假设（v29）不一致，
                    // 这里会原生崩溃（日志止于 "接口分派测试开始"）。
                    Plugin.Log?.LogInfo("[ModLogicFix] 接口分派测试开始");
                    var iMod = new MVZ2Logic.Modding.IModLogic(Instance.Pointer);
                    var defs = iMod.GetDefinitions();
                    Plugin.Log?.LogInfo("[ModLogicFix] 接口分派 OK, definitions="
                        + (defs == null ? "null" : defs.Length.ToString()));
                    game.AddMod(Instance);
                    Plugin.Log?.LogInfo("[ModLogicFix] game.AddMod OK");

                    // 补关卡 meta 属性（ENEMY_POOL/WAVE_*/出生点数等）。
                    // 外部 Mod 不走原版 ModLoader.LoadStageProperties，必须手动补齐，
                    // 否则进关预览 CreatePreviewEnemies 空引用崩溃。
                    StageMetaApplier.Apply();

                    // 小游戏列表顺序修复：把我们的 arcade Index 平移到原版之后
                    // （此时所有 Mod 资源已加载完，原版 arcade meta 可读）。
                    StageMetaApplier.ShiftArcadeIndexes();
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ModLogicFix] Postfix error: " + e);
            }
        }
    }
}
