using UnityEngine.AddressableAssets.ResourceLocators;

namespace CustomCore
{
    /// <summary>
    /// 外部内容 mod 的 ModInfo 构造辅助。
    ///
    /// XML metas 由 MetaInjection 在 ResourceManager.LoadMetaLists 时直读磁盘注入
    /// （绕过 Addressables provider 链路），因此 ResourceLocator 只需是一个非 null 的
    /// 空 locator —— ResourceManager 对其它 label（sprite/audio 等）的查询会得到空集，
    /// 本 mod 未提供这些资源时是安全的。
    /// </summary>
    public static class CoreModInfo
    {
        /// <summary>构建一个空 ResourceLocator（所有 label 查询返回空）。</summary>
        public static IResourceLocator BuildEmptyLocator(string spaceName)
        {
            var map = new ResourceLocationMap(spaceName, 0);
            return new IResourceLocator(map.Pointer);
        }
    }
}
