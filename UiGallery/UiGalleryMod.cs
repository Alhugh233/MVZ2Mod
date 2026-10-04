using System;
using DSHCore;
using MVZ2Logic.Games;
using MVZ2Logic.Modding;

namespace UiGallery
{
    /// <summary>
    /// 本 mod 的 <c>Mod</c> 逻辑体 —— **只在 <see cref="GalleryConfig.SampleOptions"/> 打开时才会被创建**
    /// （默认关闭）。
    ///
    /// <para><b>为什么一个"只读展示"的工具需要它：</b>"mod 能不能给原版设置界面加一项"这件事，
    /// 必须有一个真的注册了 <c>ModInfo</c> 的 mod 去挂定义才能验证（<c>game.AddMod(mod)</c> 会按
    /// <c>mod.GetDefinitions()</c> 拷定义，没有 Mod 就没有这条路）。所以把这份验证放在 dev 工具里，
    /// 并用开关隔离 —— 本 mod 其余部分仍然是零注入。</para>
    ///
    /// <para><b>它做什么：</b>向 <c>DSHCore.Ui.UiOptions</c> 声明两条设置项
    /// （见 <see cref="SampleOptions"/>），在官方 <c>LoadStages</c> 时机登记，
    /// 然后在 <c>PostInit</c> 把属性写上（时机是硬要求，见 <see cref="DSHCore.Ui.UiOptions"/> 的类注释）。</para>
    /// </summary>
    public class UiGalleryMod : Mod
    {
        public const string spaceName = "uig";

        /// <summary>注入类标准绑定 ctor（Il2CppInterop 官方模式）。</summary>
        public UiGalleryMod(IntPtr ptr) : base(ptr)
        {
        }

        internal static UiGalleryMod CreateInstance()
        {
            var instance = InjectedMod.Create<UiGalleryMod>(spaceName);
            self = instance;
            // 把实例绑到定义设施：注入实例的 Namespace 字符串曾经间歇性读到空，
            // 只靠字符串判定归属会让整套补丁静默失效。
            InjectedDefinitions.Bind(spaceName, instance);
            ModNamespacePin.Ensure(instance, spaceName);
            return instance;
        }

        public override void PreInit(IModLoader modLoader)
        {
            // 绝不能调 base.PreInit(...)：代理虚调用会回到本方法 trampoline 直到 native 栈溢出。
            Plugin.Log?.LogInfo("[GalleryMod] PreInit");
            ModNamespacePin.Ensure(self, spaceName);
        }

        public override void OnInit(IGlobalGame game)
        {
            Plugin.Log?.LogInfo("[GalleryMod] OnInit");
            ModNamespacePin.Ensure(self, spaceName);
        }

        public override void PostInit(IGlobalGame game)
        {
            Plugin.Log?.LogInfo("[GalleryMod] PostInit");
            // 属性 key 此时才注册完 —— 写早了 SetProperty 会被静默丢弃。
            ModNamespacePin.Ensure(self, spaceName);
            var n = DSHCore.Ui.UiOptions.ApplyProperties();
            Plugin.Log?.LogInfo("[GalleryMod] 示例设置项属性已写入 " + n + " 条");
        }

        /// <summary>本 mod 的 Mod 实例（诊断用）。</summary>
        internal static UiGalleryMod self;
    }
}
