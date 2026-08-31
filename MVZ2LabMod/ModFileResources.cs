using System;
using System.IO;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace MVZ2LabMod
{
    /// <summary>
    /// 运行时文件资源装配（替代 UABEA 改 bundle 的方案）：
    /// 1. 扫描 StreamingAssets/Mods/mvz2_lab/Content/metas/*.xml
    /// 2. 为每个文件创建 ResourceLocationBase（InternalId=绝对路径，ProviderId=mvz2_lab_file）
    /// 3. 挂到 ResourceLocationMap 的 "Meta" label 上，作为 ModInfo 的 locator
    ///
    /// 游戏的 ResourceManager.LoadMetaLists 会通过 locator.Locate("Meta", typeof(TextAsset))
    /// 拿到这些位置 → Addressables.LoadAssetAsync&lt;TextAsset&gt; → ModFileProvider 读文件
    /// → LoadSingleMetaList 按 PrimaryKey 的 Path（如 "stages"）分发给 MetaXMLParser。
    /// </summary>
    public static class ModFileResources
    {
        public const string MetaLabel = "Meta";

        private static ModFileProvider providerInstance;

        /// <summary>共享 Provider 实例（供 Harmony 补丁强制注入用）。</summary>
        public static ModFileProvider ProviderInstance
        {
            get
            {
                if (providerInstance == null)
                    providerInstance = new ModFileProvider();
                return providerInstance;
            }
        }

        /// <summary>注册自定义 Provider（幂等）：通过 Harmony 补丁在 GetResourceProvider 返回 null 时强制注入。</summary>
        public static void RegisterProvider()
        {
            ProviderPatch.Apply();
            Plugin.Log?.LogInfo("[ModFileResources] Provider patch applied: " + ModFileProvider.ModProviderId);
        }

        /// <summary>
        /// 构建 ModInfo 用的 ResourceLocationMap，把 metas 目录下所有 xml 挂到 "Meta" label。
        /// 注意代理程序集丢失了接口声明，返回给游戏前需 new IResourceLocator(map.Pointer) 包装。
        /// </summary>
        public static ResourceLocationMap BuildLocator()
        {
            var map = new ResourceLocationMap(LabMod.spaceName, 0);
            var streaming = Application.streamingAssetsPath;
            var metasDir = Path.Combine(streaming, "Mods", LabMod.spaceName, "Content", "metas");
            Plugin.Log?.LogInfo("[ModFileResources] Scanning metas dir: " + metasDir);
            if (!Directory.Exists(metasDir))
            {
                Plugin.Log?.LogWarning("[ModFileResources] metas dir NOT found: " + metasDir);
                return map;
            }
            var files = Directory.GetFiles(metasDir, "*.xml");
            Plugin.Log?.LogInfo("[ModFileResources] Found " + files.Length + " xml file(s)");
            foreach (var file in files)
            {
                var keyName = Path.GetFileNameWithoutExtension(file); // "stages" / "arcade"
                var loc = new ResourceLocationBase(
                    keyName,                        // name
                    file,                           // id（InternalId = 绝对文件路径）
                    ModFileProvider.ModProviderId,  // providerId
                    Il2CppType.Of<TextAsset>(),     // resource type
                    new IResourceLocation[0]);      // 无依赖
                // 用真正的 il2cpp 字符串做 key（保证与游戏侧 Locate("Meta") 值相等匹配）。
                Il2CppSystem.String key = MetaLabel;
                map.Add(key, new IResourceLocation(loc.Pointer));
                Plugin.Log?.LogInfo("[ModFileResources] Mapped \"" + MetaLabel + "\" <= " + keyName + " (" + file + ")");
            }
            return map;
        }
    }

    /// <summary>
    /// Addressables 的 GetResourceProvider 在 providers 列表里找不到匹配时会返回 null。
    /// 此补丁在返回 null 且 location 属于本 Mod 时强制返回我们的 ModFileProvider。
    /// </summary>
    internal static class ProviderPatch
    {
        private static bool applied;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            var harmony = new Harmony("qoznos.mvz2.labmod.providerpatch");
            harmony.Patch(
                AccessTools.Method(typeof(ResourceManager), "GetResourceProvider"),
                postfix: new HarmonyMethod(typeof(ProviderPatch), nameof(Postfix)));
        }

        [HarmonyPostfix]
        private static void Postfix(Il2CppSystem.Type t, IResourceLocation location, ref IResourceProvider __result)
        {
            try
            {
                SelfTestVtableBinding();
                if (__result != null)
                    return;
                if (location == null || location.ProviderId != ModFileProvider.ModProviderId)
                    return;
                __result = new IResourceProvider(ModFileResources.ProviderInstance.Pointer);
                Plugin.Log?.LogInfo("[ProviderPatch] Injected ModFileProvider for: " + location.InternalId);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ProviderPatch] error: " + e);
            }
        }

        private static bool selfTested;
        private static void SelfTestVtableBinding()
        {
            if (selfTested)
                return;
            selfTested = true;
            var t = typeof(ModFileProvider);
            var found = t.GetMethod("Provide",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly,
                null, new Type[] { typeof(UnityEngine.ResourceManagement.ResourceProviders.ProvideHandle) }, null);
            Plugin.Log?.LogInfo("[SelfTest] GetMethod(Provide,[ProvideHandle]) = " + (found == null ? "NULL" : found.ToString()));
            foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                Plugin.Log?.LogInfo("[SelfTest] declared: " + m);
        }
    }
}
