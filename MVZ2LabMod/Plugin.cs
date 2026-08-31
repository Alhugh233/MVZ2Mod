using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2.Modding;
using MVZ2Logic.Games;
using UnityEngine.AddressableAssets.ResourceLocators;

namespace MVZ2LabMod
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BasePlugin
    {
        public const string GUID = "qoznos.mvz2.labmod";
        public const string NAME = "MVZ2 Laboratory Content";
        public const string VERSION = "0.1.1";

        internal static new ManualLogSource Log;

        public override void Load()
        {
            Log = base.Log;

            // 修复 ClassInjector 无法处理基类泛型虚方法 vtable 的限制（必须在注册类型前应用）。
            ClassInjectorFix.Apply();

            // 修复 0.7.0 ModManager.LoadMods 重建 Logic 导致抽象方法槽崩溃的问题。
            ModLogicFix.Apply();

            // 诊断：观察 SaveManager.LoadModData 的调用参数。
            SaveManagerPatch.Apply();

            // 诊断：追踪游戏对 Mod 实例的原生方法调用（定位崩溃点）。
            ModCallTracer.Apply();

            // 注册运行时 IL2CPP 子类（必须在任何实例化之前）。
            ClassInjector.RegisterTypeInIl2Cpp<LabMod>();
            // 诊断：转储 vtable 槽绑定状态（Slot 21/22 是否指向托管 trampoline）。
            VTableDiag.Dump("LabMod", typeof(LabMod), 15, 24);
            VTableDiag.Dump("Mod", typeof(MVZ2Logic.Modding.Mod), 15, 24);
            ClassInjector.RegisterTypeInIl2Cpp<Stages.RealPrologueStage>();
            // ModFileProvider 直接实现 IResourceProvider 接口（抽象基类 vtable 绑定有缺陷）。
            ClassInjector.RegisterTypeInIl2Cpp<ModFileProvider>(new RegisterTypeOptions
            {
                Interfaces = new Il2CppInterfaceCollection(new[] { typeof(UnityEngine.ResourceManagement.ResourceProviders.IResourceProvider) })
            });

            // XML 载入链路验证补丁。
            MetaValidation.Apply();

            // 注册磁盘文件 Provider（幂等，实际在 OnRegisterModInfos 时执行）。
            ModFileResources.RegisterProvider();

            // 诊断：验证注入类的虚方法 vtable 分派是否正常。
            try
            {
                var testMod = LabMod.CreateInstance();
                MVZ2Logic.Modding.Mod asBase = testMod;
                var sd1 = asBase.CreateSaveData();
                Log.LogInfo("[SelfTest] CreateSaveData 虚调用 OK: " + (sd1 == null ? "null" : sd1.ToString()));
            }
            catch (Exception e)
            {
                Log.LogError("[SelfTest] CreateSaveData 虚调用失败: " + e);
            }
            try
            {
                var testMod = LabMod.CreateInstance();
                MVZ2Logic.Modding.Mod asBase = testMod;
                var sd2 = asBase.LoadSaveData("{}");
                Log.LogInfo("[SelfTest] LoadSaveData 虚调用 OK: " + (sd2 == null ? "null" : sd2.ToString()));
            }
            catch (Exception e)
            {
                Log.LogError("[SelfTest] LoadSaveData 虚调用失败: " + e);
            }

            // 订阅 0.7.0 官方的外部 Mod 注册点。
            // 用 DelegateSupport 把托管委托桥接成 Il2CppSystem.Func。
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
        /// 向游戏注册本 Mod 的 ModInfo。该函数在 ModManager 初始化 mod 列表时被调用。
        /// 签名必须与 il2cpp 代理完全一致（Il2CppSystem 的 Func/List/Task）。
        /// </summary>
        private static Il2CppSystem.Threading.Tasks.Task OnRegisterModInfos(
            IGlobalGame game, Il2CppSystem.Collections.Generic.List<ModInfo> results)
        {
            Log.LogInfo("OnRegisterModInfos called, adding ModInfo " + LabMod.spaceName);
            // locator 不能为 null：ResourceManager.Init 会对每个 Mod 调
            // GetLabeledResourceLocations → locator.Locate(...)，null 直接 NRE。
            // 现方案：ResourceLocationMap + ModFileProvider 从磁盘读 XML（不碰 bundle）。
            // 注意：生成器 1.4.5 丢失了代理类上的接口声明，
            // 需按官方文档用 new InterfaceType(obj.Pointer) 包装成接口代理。
            var map = ModFileResources.BuildLocator();
            var locator = new IResourceLocator(map.Pointer);
            results.Add(new ModInfo(LabMod.spaceName, locator)
            {
                // 与 vanilla 保持一致，避免存档版本不匹配警告。
                LevelDataVersion = 4, // = LevelManager.CURRENT_DATA_VERSION
                DisplayName = "MVZ2 Laboratory",
                IsBuiltin = false,
                CatalogPath = null,
                Logic = new LabMod(),
                // 定义不走程序集扫描（在 LabMod.OnInit 里手动 AddDefinition），传空数组。
                Assemblies = new Il2CppReferenceArray<Il2CppSystem.Reflection.Assembly>(0)
            });
            Log.LogInfo("OnRegisterModInfos: ModInfo added, Logic created OK");
            return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }
    }
}
