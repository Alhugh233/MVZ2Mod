using System;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;
using Il2CppSystem.Collections.Generic;
using MVZ2Logic.Modding;
using PVZEngine;
using PVZEngine.Callbacks;

namespace DSHCore
{
    /// <summary>
    /// 注入类 <c>Mod</c> 的实例创建与初始化。
    ///
    /// ── 为什么需要「补做初始化」────────────────────────────────────────
    /// ClassInjector 的标准流程是
    /// <c>new T(ClassInjector.DerivedConstructorPointer&lt;T&gt;())</c> +
    /// <c>DerivedConstructorBody</c>，走的是**注入类的托管 ctor 链**；
    /// 原生 <c>Mod(string)</c> 里那几行字段初始化**不会执行**。缺失时表现为：
    ///   - <c>Namespace</c> 为 null → 官方按命名空间查找本 mod 内容时找不到；
    ///   - <c>definitionGroup</c> / <c>triggers</c> 为 null（或未驻留而被回收）
    ///     → 注册定义时 NRE，或之后 GetID() 值比较静默失败。
    /// 所以这里显式补上，并把两个容器交给 <see cref="Il2CppPin"/> 钉住。
    ///
    /// 注意：<c>_Namespace_k__BackingField</c> / <c>definitionGroup</c> / <c>triggers</c>
    /// 在 Il2CppInterop 代理程序集里是 **public 属性**，因此可以在本库（而非派生类）里直接赋值。
    /// </summary>
    public static class InjectedMod
    {
        /// <summary>
        /// 创建绑定到注入类 <typeparamref name="T"/> 的实例并完成初始化。
        ///
        /// 调用前提：<typeparamref name="T"/> 已 RegisterTypeInIl2Cpp
        /// （用 <see cref="ModBootstrap.Register"/> 时已自动处理）。
        /// </summary>
        public static T Create<T>(string spaceName) where T : Mod
        {
            if (string.IsNullOrEmpty(spaceName))
                throw new ArgumentException("spaceName 不能为空", nameof(spaceName));

            // 走 public ctor T(IntPtr)。注入类的 ctor 都是这个形状，
            // 用反射是为了让本方法对任意 T 泛型可用。
            var instance = (T)Activator.CreateInstance(
                typeof(T),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[] { ClassInjector.DerivedConstructorPointer<T>() },
                null);
            return Initialize(instance, spaceName);
        }

        /// <summary>
        /// 对已经 new 出来的注入类实例补做初始化。
        /// 不想用反射时可以在 mod 里自己写
        /// <c>var m = new MyMod(ClassInjector.DerivedConstructorPointer&lt;MyMod&gt;());</c>
        /// 然后调本方法。
        /// </summary>
        public static T Initialize<T>(T instance, string spaceName) where T : Mod
        {
            if (instance == null)
                throw new ArgumentNullException(nameof(instance));
            if (string.IsNullOrEmpty(spaceName))
                throw new ArgumentException("spaceName 不能为空", nameof(spaceName));

            ClassInjector.DerivedConstructorBody(instance);
            instance._Namespace_k__BackingField = spaceName;

            // 上面这行只做了「托管 → il2cpp」的一次转换，字段本身不在 il2cpp GC 的扫描范围内，
            // 字符串迟早会被回收，之后 mod.Namespace 读到空/悬垂值（症状是间歇性的、
            // 官方按命名空间找不到本 mod 的内容）。这里把它钉成 GC 根并每次生命周期节点复核。
            ModNamespacePin.Ensure(instance, spaceName);

            instance.definitionGroup = Il2CppPin.Pin(new DefinitionGroup());
            instance.triggers = Il2CppPin.Pin(new List<ITrigger>());
            return instance;
        }
    }
}
