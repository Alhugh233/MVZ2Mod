using System;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace MVZ2LabMod
{
    /// <summary>
    /// 磁盘文件资源提供器：把 StreamingAssets/Mods/ 下的 XML 文件作为 TextAsset 提供给
    /// Addressables。
    ///
    /// 实现方式：直接实现 IResourceProvider 接口（ClassInjector RegisterTypeOptions.Interfaces，
    /// 按名称/参数个数绑定），而不是继承 ResourceProviderBase 重写抽象 Provide——
    /// 后者在抽象基类的 vtable 槽绑定上有缺陷（槽保留抽象 thunk，运行时抛
    /// "Attempting to call abstract method Provide"）。
    /// </summary>
    public class ModFileProvider : Il2CppSystem.Object
    {
        public const string ModProviderId = "mvz2_lab_file";

        /// <summary>ClassInjector 注入要求存在无参构造器。</summary>
        public ModFileProvider()
        {
        }

        // ---- IResourceProvider 接口实现（按名绑定）----

        public string ProviderId => ModProviderId;

        public ProviderBehaviourFlags BehaviourFlags => ProviderBehaviourFlags.None;

        public Il2CppSystem.Type GetDefaultType(IResourceLocation location)
        {
            return Il2CppType.Of<TextAsset>();
        }

        public bool CanProvide(Il2CppSystem.Type t, IResourceLocation location)
        {
            // 匹配主链是 ProviderId（我们只为自己创建的 location 服务，均为文本文件）。
            return true;
        }

        public void Release(IResourceLocation location, Il2CppSystem.Object obj)
        {
            // 无资源需要释放。
        }

        public void Provide(ProvideHandle provideHandle)
        {
            try
            {
                var path = provideHandle.Location.InternalId;
                Plugin.Log?.LogInfo("[ModFileProvider] Providing TextAsset: " + path);
                var text = File.ReadAllText(path);
                var asset = new TextAsset(text);
                provideHandle.Complete(asset, true, null);
                Plugin.Log?.LogInfo("[ModFileProvider] Provided OK: " + path + " (" + text.Length + " chars)");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ModFileProvider] Provide failed: " + e);
                try
                {
                    provideHandle.Complete<TextAsset>(null, false, new Il2CppSystem.Exception(e.ToString()));
                }
                catch
                {
                    provideHandle.Complete<TextAsset>(null, false, null);
                }
            }
        }
    }
}
