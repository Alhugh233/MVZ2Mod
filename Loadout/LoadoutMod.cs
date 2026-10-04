using System;
using DSHCore;
using MVZ2Logic.Games;
using MVZ2Logic.Modding;
using MVZ2Logic.Saves;

namespace Loadout
{
    /// <summary>
    /// 本 mod 的 <c>Mod</c> 逻辑体 —— **只为「兼容性等级」那一行原生设置项而存在**
    /// （见 <see cref="CompatOptions"/>）。
    ///
    /// <para><b>为什么必须有一个真的 Mod</b>：给原版设置界面加一项，得走官方那条
    /// <c>game.AddMod(mod)</c> → <c>mod.GetDefinitions()</c> 的路，而没有 Mod 实例就没有
    /// <c>Mod.AddDefinition</c>。这是本 mod 里**唯一**一处「注册到游戏里」的地方；
    /// 其余功能仍然全是读/写原版已有属性 + 克隆原版 UI。</para>
    ///
    /// <para>时机是硬要求（见 <see cref="DSHCore.Ui.UiOptions"/> 的类注释）：
    /// 声明在 <c>Load()</c> → 登记在官方 <c>LoadStages</c> → 写属性在 <c>PostInit</c>。</para>
    /// </summary>
    public class LoadoutMod : Mod
    {
        /// <summary>本 mod 的命名空间（设置项 id 就是 <c>loadout:compat_level</c>）。</summary>
        public const string spaceName = "loadout";

        /// <summary>注入类标准绑定 ctor（Il2CppInterop 官方模式）。</summary>
        public LoadoutMod(IntPtr ptr) : base(ptr)
        {
        }

        internal static LoadoutMod CreateInstance()
        {
            var instance = InjectedMod.Create<LoadoutMod>(spaceName);
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
            Plugin.Log?.LogInfo("[LoadoutMod] PreInit");
            ModNamespacePin.Ensure(self, spaceName);
        }

        public override void OnInit(IGlobalGame game)
        {
            Plugin.Log?.LogInfo("[LoadoutMod] OnInit");
            ModNamespacePin.Ensure(self, spaceName);
        }

        public override void PostInit(IGlobalGame game)
        {
            Plugin.Log?.LogInfo("[LoadoutMod] PostInit");
            // 属性 key 此时才注册完 —— 写早了 SetProperty 会被静默丢弃。
            ModNamespacePin.Ensure(self, spaceName);
            var n = DSHCore.Ui.UiOptions.ApplyProperties();
            Plugin.Log?.LogInfo("[LoadoutMod] 兼容性等级设置项属性已写入 " + n + " 条");
        }

        // ------------------------------------------------------------------
        // 存档
        // ------------------------------------------------------------------

        /// <summary>
        /// 给一份**空存档**。
        ///
        /// <para>⚠ <b>这两个必须覆写</b>：注入类的 vtable 抽象槽如果不绑，存档链走到
        /// <c>SaveManager.LoadModData</c> → <c>mod.CreateSaveData()</c> 时会抛
        /// <c>EntryPointNotFoundException: Attempting to call abstract method</c>
        /// （实测：只注册 ModInfo、不覆写这两个，读档那一刻就会报）——
        /// 而且它是在 <c>LoadUserData</c> 中间抛的，会打断整个读档流程。
        /// 本 mod 不存任何东西，返回空存档即可（同 <c>ModularCurseMod</c>）。</para>
        /// </summary>
        public override ModSaveData CreateSaveData()
        {
            return new LogicSaveData(spaceName);
        }

        public override ModSaveData LoadSaveData(string json)
        {
            return new LogicSaveData(spaceName);
        }

        internal static LoadoutMod self;
    }
}
