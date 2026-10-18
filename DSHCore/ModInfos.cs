using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2.Modding;
using MVZ2Logic.Games;
using UnityEngine.AddressableAssets.ResourceLocators;

namespace DSHCore
{
    /// <summary>
    /// 向游戏注册外部 mod 的 <c>ModInfo</c>（0.7 官方外部扩展点）。
    ///
    /// 官方链：<c>ModManager.RegisterModInfos</c> 触发 <c>OnRegisterModInfos</c> 事件，
    /// 各个插件在回调里往 results 里 Add 自己的 ModInfo；随后官方
    /// <c>ModLoader.LoadAll</c> 会对 <c>ModInfo.Logic</c> 依次调用
    /// Preload / PreInit / Load / OnInit / AddMod / PostInit。
    ///
    /// 本类把三处样板收敛掉：
    ///   1. 空 <c>ResourceLocator</c>（meta 由 MetaInjection 直读磁盘，不需要真实 locator）；
    ///   2. <c>ModInfo</c> 的各个必填字段（LevelDataVersion / Assemblies 等）；
    ///   3. <c>System.Func</c> → <c>Il2CppSystem.Func</c> 的委托转换 + 事件订阅。
    /// </summary>
    public static class ModInfos
    {
        /// <summary>
        /// 本 mod 的关卡存档数据版本，会随 <c>ModInfo.LevelDataVersion</c> 写进每份关卡存档头。
        ///
        /// 游戏存/读关卡时把「所有 mod 的 (命名空间, 版本号)」一起存下来，读档时**按命名空间
        /// 逐个比对**（见 <c>LevelDataIdentifierList.Compare</c>）。因此这里唯一的要求是
        /// **保持稳定**：只要本 mod 的关卡存档格式没变，就不要动这个值；
        /// 动了（或改了命名空间）会让玩家已有的本 mod 关卡存档被判「版本不匹配」而读不进去。
        ///
        /// 它**不需要**等于 vanilla 的 <c>LevelManager.CURRENT_DATA_VERSION</c>
        /// （0.7.0 正式版与各内测版均为 5；本库一直用 4，两者并存没有问题）。
        /// </summary>
        public const int LevelDataVersion = 4;

        /// <summary>
        /// 构造一个"空"的 <see cref="IResourceLocator"/>：所有 label 查询都返回空集。
        ///
        /// 为什么够用：XML metas 由 <c>MetaInjection</c> 在
        /// <c>ResourceManager.LoadMetaLists</c> 时直接从磁盘读入，**完全绕过**
        /// Addressables provider 链路。因此本 mod 若没有走 Addressables 的
        /// sprite / audio 资源，这个空 locator 就是安全的。
        /// 反过来：若你的 mod 需要游戏通过 Addressables 加载资源，就得换成真实的 locator。
        /// </summary>
        public static IResourceLocator EmptyLocator(string spaceName)
        {
            var map = new ResourceLocationMap(spaceName, 0);
            return new IResourceLocator(map.Pointer);
        }

        /// <summary>
        /// 构造本 mod 的 <see cref="ModInfo"/>。
        /// </summary>
        /// <param name="spaceName">命名空间（与 CoreRegistry 登记的一致）。</param>
        /// <param name="logic">
        /// 由 <see cref="InjectedMod.Create{T}"/> 创建的注入实例。
        /// 0.7 的 LoadMods **不再重建** Logic，所以这里给的就是最终实例。
        /// </param>
        /// <param name="displayName">显示名；留空则用命名空间。</param>
        public static ModInfo Create(string spaceName, MVZ2Logic.Modding.Mod logic,
            string displayName = null, int levelDataVersion = LevelDataVersion)
        {
            if (string.IsNullOrEmpty(spaceName))
                throw new ArgumentException("spaceName 不能为空", nameof(spaceName));

            return new ModInfo(spaceName, EmptyLocator(spaceName))
            {
                LevelDataVersion = levelDataVersion,
                DisplayName = string.IsNullOrEmpty(displayName) ? spaceName : displayName,
                IsBuiltin = false,
                CatalogPath = null,
                Logic = logic,
                // 本框架的定义都经官方 LoadStages / LoadCurseMetas 等 meta 链创建，
                // 不走"扫描程序集上的 attribute"，所以给一个空数组。
                Assemblies = new Il2CppReferenceArray<Il2CppSystem.Reflection.Assembly>(0)
            };
        }

        /// <summary>
        /// 订阅官方 ModInfo 注册点。内部完成
        /// <c>System.Func</c> → <c>Il2CppSystem.Func</c> 的委托转换，
        /// 调用方只要写一个签名匹配的普通静态方法即可：
        /// <code>
        /// ModInfos.OnRegister(OnRegisterModInfos);
        ///
        /// private static Il2CppSystem.Threading.Tasks.Task OnRegisterModInfos(
        ///     IGlobalGame game, Il2CppSystem.Collections.Generic.List&lt;ModInfo&gt; results)
        /// {
        ///     results.Add(ModInfos.Create(Nsp, InjectedMod.Create&lt;MyMod&gt;(Nsp), "My Mod"));
        ///     return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        /// }
        /// </code>
        /// 注意：回调的返回值必须是已完成 Task；异常务必自己 try/catch ——
        /// 抛到原生侧会直接崩游戏且没有可读托管栈。
        /// </summary>
        public static void OnRegister(Func<IGlobalGame,
            Il2CppSystem.Collections.Generic.List<ModInfo>,
            Il2CppSystem.Threading.Tasks.Task> handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var il2cppHandler = Il2CppInterop.Runtime.DelegateSupport
                .ConvertDelegate<Il2CppSystem.Func<IGlobalGame,
                    Il2CppSystem.Collections.Generic.List<ModInfo>,
                    Il2CppSystem.Threading.Tasks.Task>>(handler);
            ModManager.add_OnRegisterModInfos(il2cppHandler);
            Plugin.Log?.LogInfo("[ModInfos] OnRegisterModInfos 订阅完成");
        }
    }
}
