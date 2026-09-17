using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime.Injection;

namespace DSHCore
{
    /// <summary>
    /// 内容 mod 的「一次注册」入口：把每个 mod 都要手写一遍的
    /// <c>CoreRegistry.Register</c> + 一串
    /// <c>ClassInjector.RegisterTypeInIl2Cpp&lt;T&gt;()</c> 收敛成一次调用。
    ///
    /// 典型用法（插件 Load() 里）：
    /// <code>
    /// ModBootstrap.Register(
    ///     spaceName: "mvz2_lab",
    ///     logicType: typeof(LabMod),
    ///     xmlDefaultNamespace: "mvz2",
    ///     extraInjectedTypes: new[] {
    ///         typeof(RealPrologueStage), typeof(ZeroKSpaceStage),
    ///         typeof(UnknowPalaceStage), typeof(ZeroKSpaceCostBuff)
    ///     });
    /// </code>
    ///
    /// 顺序要点：**所有 RegisterTypeInIl2Cpp 都必须在任何实例化之前**完成，
    /// 包括 <c>new XxxBehaviour(...)</c> 这类无参托管构造里隐式触发的实例化。
    /// 本方法在插件 Load() 里调用即满足该约束。
    /// </summary>
    public static class ModBootstrap
    {
        /// <summary>本库已注册过的注入类型，避免重复注册（重复注册会抛异常）。</summary>
        private static readonly HashSet<Type> registered = new HashSet<Type>();
        private static readonly object gate = new object();

        /// <summary>
        /// 登记命名空间 + 注册注入类。
        /// </summary>
        /// <param name="spaceName">ModInfo 命名空间（如 mvz2_lab）。</param>
        /// <param name="logicType">注入的 Mod 子类；会自动注册，无需放进 extraInjectedTypes。</param>
        /// <param name="xmlDefaultNamespace">XML 无前缀 NamespaceID 的归属，默认 mvz2。</param>
        /// <param name="extraInjectedTypes">其它需要注入的类（Stage / Buff / Behaviour 等）。</param>
        public static void Register(string spaceName, Type logicType,
            string xmlDefaultNamespace = "mvz2", params Type[] extraInjectedTypes)
        {
            if (string.IsNullOrEmpty(spaceName))
                throw new ArgumentException("spaceName 不能为空", nameof(spaceName));
            if (logicType == null)
                throw new ArgumentNullException(nameof(logicType));

            CoreRegistry.Register(new CoreRegistry.RegisteredMod
            {
                Namespace = spaceName,
                LogicType = logicType,
                XmlDefaultNamespace = string.IsNullOrEmpty(xmlDefaultNamespace)
                    ? "mvz2" : xmlDefaultNamespace
            });

            var names = new StringBuilder();
            if (RegisterInjected(logicType))
                Append(names, logicType);
            if (extraInjectedTypes != null)
            {
                foreach (var t in extraInjectedTypes)
                {
                    if (t == null || t == logicType)
                        continue;
                    if (RegisterInjected(t))
                        Append(names, t);
                }
            }

            Plugin.Log?.LogInfo("[ModBootstrap] " + spaceName + " ready  logic=" + logicType.Name
                + "  injected=[" + names + "]");
        }

        private static void Append(StringBuilder sb, Type t)
        {
            if (sb.Length > 0)
                sb.Append(", ");
            sb.Append(t.Name);
        }

        private static bool RegisterInjected(Type t)
        {
            lock (gate)
            {
                if (!registered.Add(t))
                    return false;
            }
            ClassInjector.RegisterTypeInIl2Cpp(t);
            return true;
        }
    }
}
