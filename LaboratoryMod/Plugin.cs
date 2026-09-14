using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using CustomCore;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2.Modding;
using MVZ2Logic.Games;
using PVZEngine;
using LaboratoryMod.Stages;

namespace LaboratoryMod
{
    /// <summary>
    /// MVZ2 实验室内容 Mod（依赖 CustomCore 前置插件）。
    ///
    /// 0.7 集成方式（官方链）：
    ///   1. CoreRegistry 登记 mvz2_lab（XML metas 磁盘注入、vtable 兜底生效）。
    ///   2. OnRegisterModInfos 注册 ModInfo：Logic = LabMod.CreateInstance()
    ///      （注入实例，LoadMods 不再重建 → 直接进入官方 LoadAll）。
    ///   3. LabMod.PreInit 注册 IStageDefinitionProvider（minigame 关卡）。
    ///   4. ModLoader.LoadStages 官方用 meta → provider 建关卡定义并 AddDefinition；
    ///      LoadStageProperties 官方自动应用 meta 属性（ENEMY_POOL 等）。
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency("qoznos.mvz2.core")]
    public class Plugin : BasePlugin
    {
        public const string GUID = "qoznos.mvz2.labmod";
        public const string NAME = "MVZ2 Laboratory Content";
        public const string VERSION = "0.2.0";

        internal static new ManualLogSource Log;

        /// <summary>
        /// 本 mod 全部定义的稳定跟踪（绕过官方 Mod.GetDefinitions 的原生 LINQ：
        /// metadata v31 注入环境下 DefinitionGroup.GetDefinitions 的 SelectMany
        /// 接口枚举非确定性抛 MethodAccessException / 返回空 → game.AddMod 拷不到
        /// defs → 列表消失 + ChangeStage missing。作者亦在移除 LINQ 印证此坑）。
        /// </summary>
        internal static readonly System.Collections.Generic.List<Definition> AllDefinitions
            = new System.Collections.Generic.List<Definition>();

        internal static void TrackDefinition(Definition def)
        {
            if (def == null)
                return;
            lock (AllDefinitions)
            {
                for (int i = 0; i < AllDefinitions.Count; i++)
                {
                    if (AllDefinitions[i] == def)
                        return;
                }
                AllDefinitions.Add(def);
            }
        }

        public override void Load()
        {
            Log = base.Log;

            // 登记到 CustomCore（meta 注入 + vtable 兜底按此 nsp 生效）。
            CoreRegistry.Register(new CoreRegistry.RegisteredMod
            {
                Namespace = LabMod.spaceName,
                LogicType = typeof(LabMod),
                XmlDefaultNamespace = "mvz2"
            });

            // 注册注入类（必须在任何实例化之前）。ClassInjectorFix 已由 CustomCore 应用。
            ClassInjector.RegisterTypeInIl2Cpp<LabMod>();
            ClassInjector.RegisterTypeInIl2Cpp<Stages.RealPrologueStage>();
            ClassInjector.RegisterTypeInIl2Cpp<Stages.ZeroKSpaceStage>();
            ClassInjector.RegisterTypeInIl2Cpp<Stages.UnknowPalaceStage>();
            ClassInjector.RegisterTypeInIl2Cpp<Buffs.ZeroKSpaceCostBuff>();

            // 0.7 中 ModLoader.LoadAll 会对 stageProviders.OrderBy(Priority) 做原生 LINQ
            // 接口分派，注入类实现 IStageDefinitionProvider 在 metadata v31 抛
            // MethodAccessException（与 SetPropertyObject 同源）。因此不用 provider，
            // 改由 LoadStagesPatch 在官方 LoadStages 时机直接创建关卡定义。
            LoadStagesPatch.Apply();
            // unknow_palace 出怪接管在 LabMod.PostInit 应用（此时 LoadAll 已完成、
            // AddMod 已跑完；Chainloader 早期 patch 会提前初始化 LogicLevelExt
            // 干扰官方加载 → game.AddMod 时 DefinitionGroup List 损坏崩溃）。

            // 订阅 0.7 官方的外部 Mod 注册点。
            var handler = new System.Func<IGlobalGame,
                Il2CppSystem.Collections.Generic.List<ModInfo>,
                Il2CppSystem.Threading.Tasks.Task>(OnRegisterModInfos);
            var il2cppHandler = Il2CppInterop.Runtime.DelegateSupport
                .ConvertDelegate<Il2CppSystem.Func<IGlobalGame,
                    Il2CppSystem.Collections.Generic.List<ModInfo>,
                    Il2CppSystem.Threading.Tasks.Task>>(handler);
            ModManager.add_OnRegisterModInfos(il2cppHandler);

            Log.LogInfo(NAME + " v" + VERSION + " loaded, waiting for ModManager.RegisterModInfos...");
        }

        /// <summary>
        /// 向游戏注册本 Mod 的 ModInfo。Logic 直接是注入实例（0.7 不再重建）。
        /// </summary>
        private static Il2CppSystem.Threading.Tasks.Task OnRegisterModInfos(
            IGlobalGame game, Il2CppSystem.Collections.Generic.List<ModInfo> results)
        {
            try
            {
                Log.LogInfo("OnRegisterModInfos called, adding ModInfo " + LabMod.spaceName);
                var logic = LabMod.CreateInstance();
                // 立即修复 vtable（防止 Class::Init 重置影响后续官方 LoadAll 虚调用）。
                VTableRebind.Ensure(typeof(LabMod));

                // locator 不能为 null（ResourceManager 会对每个 Mod 调 Locate）。
                // metas 由 CustomCore.MetaInjection 直读磁盘注入，locator 用空占位即可。
                var locator = CoreModInfo.BuildEmptyLocator(LabMod.spaceName);
                results.Add(new ModInfo(LabMod.spaceName, locator)
                {
                    LevelDataVersion = 4, // = LevelManager.CURRENT_DATA_VERSION（与 vanilla 保持一致）
                    DisplayName = "MVZ2 Laboratory",
                    IsBuiltin = false,
                    CatalogPath = null,
                    Logic = logic,
                    // 定义经 LoadStages 官方 meta 链创建，不走程序集扫描 attribute。
                    Assemblies = new Il2CppReferenceArray<Il2CppSystem.Reflection.Assembly>(0)
                });
                Log.LogInfo("OnRegisterModInfos: ModInfo added (mvz2_lab), Logic="
                    + (logic == null ? "null" : logic.GetType().FullName));
            }
            catch (Exception e)
            {
                Log.LogError("OnRegisterModInfos error: " + e);
            }
            return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }
    }
}
