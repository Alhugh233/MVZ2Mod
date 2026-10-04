using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2Logic.Modding;
using PVZEngine;
using PVZEngine.Callbacks;

namespace DSHCore
{
    /// <summary>
    /// **注入定义（Definition）的通用设施**：让一个 mod 手工造出来的 <see cref="Definition"/>
    /// 能被游戏的官方链路看见。
    ///
    /// <para><b>为什么必须有这么一层（三条都是实测结论，不是推测）：</b></para>
    /// <list type="number">
    /// <item><b>官方那条"自动注册"的路对注入类不可用。</b>原版
    /// <c>ModLoader.LoadAssemblies</c> 靠反射读类上的 <c>DefinitionAttribute</c>
    /// （如 <c>[AutoOptionWidgetDefinition("id")]</c>）来 <c>new(nsp, name)</c> + <c>Mod.AddDefinition</c>，
    /// 但 interop 生成的特性类型**不是 <c>System.Attribute</c>**（继承 il2cpp 基类），
    /// C# 侧写不出来 → 编译期 <c>CS0616 不是特性类</c>。
    /// 所以注入定义只能**手工**创建并 <c>AddDefinition</c>。</item>
    /// <item><b><c>DefinitionGroup.GetDefinitions()</c> 不能碰。</b>它内部是
    /// <c>SelectMany + ToArray</c> 原生 LINQ，<c>DefinitionList.GetAllDefinitions&lt;T&gt;()</c>
    /// 内部是 <c>OfType&lt;T&gt;()</c>；注入定义参与枚举时在 metadata v31 下
    /// **非确定性**抛 <c>MethodAccessException</c> 或返回空数组。
    /// 而 <c>GlobalGame.AddMod(mod)</c> 正是走 <c>Mod.GetDefinitions()</c> ——
    /// 一旦返回空，本 mod 的全部内容都不会进全局表（关卡列表空、诅咒面板空）。
    /// 对策是用托管 <c>List</c> 自己记，并拦掉那三个方法。</item>
    /// <item><b>注入类实例的引用字段不被 il2cpp GC 追踪</b>（<see cref="Il2CppPin"/>）——
    /// 所以每个 new 出来的定义都要把 <c>id</c>/<c>propertyDict</c>/<c>triggers</c>
    /// 换成钉住的托管实例，否则 <c>GetID()</c> 的比较会静默失败。</item>
    /// </list>
    ///
    /// <para><b>用法（一个 mod 只需要两行）：</b></para>
    /// <code>
    /// InjectedDefinitions.Apply();                       // 装补丁（幂等，可多次调用）
    /// InjectedDefinitions.Register("my_mod");            // 声明命名空间
    /// ...
    /// InjectedDefinitions.Bind("my_mod", myInjectedMod); // 可选：把 Mod 实例也绑上（双保险）
    /// ...
    /// var def = InjectedDefinitions.Create&lt;MyOptionToggle&gt;("my_mod", "my_toggle");
    /// myMod.AddDefinition(def);                          // 之后会被自动跟踪
    /// </code>
    ///
    /// <para><b>与各 mod 自己的私有实现并存没问题</b>：本类只对**已登记**的命名空间动手，
    /// 而"是否属于本 mod"的判定只在各自的命名空间上返回 true（互不相交），
    /// 所以补丁顺序不影响结果。新 mod 请一律用本类，不要再抄一份私有实现。</para>
    /// </summary>
    public static class InjectedDefinitions
    {
        private const string HarmonyId = "qoznos.mvz2.core.injecteddefinitions";

        /// <summary>命名空间 → 该命名空间下全部定义的**托管**跟踪列表（同一个对象，可供 lock）。</summary>
        private static readonly Dictionary<string, List<Definition>> Lists
            = new Dictionary<string, List<Definition>>(StringComparer.Ordinal);

        /// <summary>注入 <c>Mod</c> 实例的 native 指针 → 命名空间。</summary>
        /// <remarks>
        /// 为什么要指针表：只比 <c>mod.Namespace</c> 字符串曾经出现过间歇性失配
        /// （注入实例的引用字段不被 GC 扫描，字符串会被回收），
        /// 那会让整套补丁对本 mod 静默失效 —— 表现成"内容一个都没注册"。
        /// 两个判据都用上才稳。
        /// </remarks>
        private static readonly Dictionary<IntPtr, string> PointerToNamespace
            = new Dictionary<IntPtr, string>();

        private static bool _applied;
        private static bool _patched;

        // ------------------------------------------------------------------
        // 登记
        // ------------------------------------------------------------------

        /// <summary>声明一个命名空间，之后该命名空间的定义会被托管跟踪、并走托管通道返回。</summary>
        public static void Register(string spaceName)
        {
            if (string.IsNullOrEmpty(spaceName))
                throw new ArgumentException("spaceName 不能为空", nameof(spaceName));
            if (Lists.ContainsKey(spaceName))
                return;
            Lists[spaceName] = new List<Definition>();
        }

        public static bool IsRegistered(string spaceName)
        {
            return !string.IsNullOrEmpty(spaceName) && Lists.ContainsKey(spaceName);
        }

        /// <summary>
        /// 把注入的 <c>Mod</c> 实例绑到命名空间（可选但推荐）。
        /// 之后即使 <c>mod.Namespace</c> 读到 null 也能正确判定归属。
        /// </summary>
        public static void Bind(string spaceName, Mod instance)
        {
            if (string.IsNullOrEmpty(spaceName) || instance == null)
                return;
            try
            {
                PointerToNamespace[instance.Pointer] = spaceName;
            }
            catch (Exception e)
            {
                LogWarn("绑定 Mod 实例失败：" + e.GetType().Name);
            }
        }

        /// <summary>该命名空间的定义列表（**同一个对象**，可直接 lock）。未登记时返回 null。</summary>
        public static List<Definition> ListFor(string spaceName)
        {
            List<Definition> list;
            return (spaceName != null && Lists.TryGetValue(spaceName, out list)) ? list : null;
        }

        /// <summary>该命名空间已跟踪的定义（快照）。</summary>
        public static Definition[] All(string spaceName)
        {
            var list = ListFor(spaceName);
            if (list == null)
                return new Definition[0];
            lock (list)
            {
                return list.ToArray();
            }
        }

        /// <summary>该命名空间已跟踪的定义数量。</summary>
        public static int Count(string spaceName)
        {
            var list = ListFor(spaceName);
            if (list == null)
                return 0;
            lock (list)
            {
                return list.Count;
            }
        }

        /// <summary>把一个定义加进跟踪表（幂等；按引用去重）。</summary>
        public static void Track(string spaceName, Definition def)
        {
            if (def == null)
                return;
            var list = ListFor(spaceName);
            if (list == null)
                return;
            lock (list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] == def)
                        return;
                }
                list.Add(def);
            }
        }

        /// <summary>按定义自己的命名空间跟踪它。</summary>
        public static void Track(Definition def)
        {
            if (def == null)
                return;
            string nsp = null;
            try { nsp = def.Namespace; }
            catch (Exception) { }
            if (!IsRegistered(nsp))
                return;
            Track(nsp, def);
        }

        // ------------------------------------------------------------------
        // 创建注入定义
        // ------------------------------------------------------------------

        /// <summary>
        /// 创建一个**注入类**定义实例，并补做原生 <c>Definition(string, string)</c> 构造器的初始化。
        ///
        /// <para>为什么不能直接 <c>new T(nsp, name)</c>：注入类的字段初始化在任何 ctor 里都不会
        /// 自动完成，而且 <c>id</c>/<c>propertyDict</c>/<c>triggers</c> 三个引用字段
        /// **不被 il2cpp GC 追踪** —— 不钉住就会被回收，之后 <c>GetID()</c> 的比较会静默失败
        /// （症状：内容"注册成功了"却查不到）。三条都要补。</para>
        ///
        /// <para><paramref name="extraFields"/> 给派生类补它自己的字段（例如
        /// <c>OptionSlider</c> 不需要，<c>BuffDefinition</c> 的 <c>modifiers</c> 需要）。
        /// 传 null 就只补 <see cref="Definition"/> 这一层。</para>
        /// </summary>
        public static T Create<T>(string spaceName, string name, Action<T> extraFields = null)
            where T : Definition
        {
            if (string.IsNullOrEmpty(spaceName) || string.IsNullOrEmpty(name))
                throw new ArgumentException("spaceName / name 不能为空");

            // 走 (IntPtr) 构造器 —— 注入类的统一入口。
            var ctor = typeof(T).GetConstructor(new[] { typeof(IntPtr) });
            if (ctor == null)
                throw new MissingMethodException(
                    typeof(T).FullName + " 缺少 (IntPtr) 构造器；注入类必须提供 "
                    + "public T(IntPtr ptr) : base(ptr)。");

            var instance = (T)ctor.Invoke(new object[] { ClassInjector.DerivedConstructorPointer<T>() });
            ClassInjector.DerivedConstructorBody(instance);

            // 补原生 ctor 的字段初始化，并全部钉住 GC 根。
            instance.id = Il2CppPin.Pin(new NamespaceID(spaceName, name));
            instance.propertyDict = Il2CppPin.Pin(new PropertyDictionary());
            instance.triggers = Il2CppPin.Pin(new Il2CppSystem.Collections.Generic.List<ITrigger>());

            if (extraFields != null)
                extraFields(instance);

            return instance;
        }

        // ------------------------------------------------------------------
        // 补丁
        // ------------------------------------------------------------------

        /// <summary>
        /// 装上三个补丁（幂等）。**必须早于任何 <c>Mod.AddDefinition</c> / <c>AddMod</c>** 调用。
        /// 一般放在插件 <c>Load()</c> 里，与 <c>ModBootstrap.Register</c> 同一处。
        /// </summary>
        public static void Apply()
        {
            if (_patched)
                return;
            _patched = true;
            _applied = true;

            try
            {
                var harmony = new Harmony(HarmonyId);

                Patch(harmony, "Mod.AddDefinition",
                    AccessTools.Method(typeof(Mod), "AddDefinition"),
                    postfix: new HarmonyMethod(typeof(InjectedDefinitions), nameof(AddDefinitionPostfix)));

                Patch(harmony, "Mod.GetDefinitions",
                    AccessTools.Method(typeof(Mod), "GetDefinitions"),
                    prefix: new HarmonyMethod(typeof(InjectedDefinitions), nameof(GetDefinitionsPrefix)));

                Patch(harmony, "Mod.PostReloadMods",
                    AccessTools.Method(typeof(Mod), "PostReloadMods"),
                    prefix: new HarmonyMethod(typeof(InjectedDefinitions), nameof(PostReloadModsPrefix)));

                LogInfo("补丁已装（AddDefinition / GetDefinitions / PostReloadMods）");
            }
            catch (Exception e)
            {
                _patched = false;
                LogError("装补丁失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>本设施是否已装过补丁。</summary>
        public static bool IsApplied
        {
            get { return _applied; }
        }

        /// <summary>状态报告（排障用）。</summary>
        public static string Report()
        {
            var sb = new StringBuilder(300);
            sb.Append("注入定义设施：").Append(_applied ? "已装补丁" : "未装补丁").Append('\n');
            foreach (var pair in Lists)
            {
                sb.Append("  ").Append(pair.Key).Append("：");
                lock (pair.Value)
                    sb.Append(pair.Value.Count);
                sb.Append(" 个定义\n");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // 判定归属
        // ------------------------------------------------------------------

        /// <summary>
        /// 取这个 <c>Mod</c> 实例所属的**已登记**命名空间；不是我们的就返回 null。
        /// 先比 native 指针、再比 <c>Namespace</c> 字符串（两个判据都用，理由见
        /// <see cref="PointerToNamespace"/>）。
        ///
        /// <para><b>外部要判定「这个 Mod 实例是不是我的」请用本方法，不要直接比
        /// <c>mod.Namespace</c>。</b>注入实例的 <c>Namespace</c> 字符串存在被 GC
        /// 回收后读到空/悬垂值的窗口（见 <see cref="ModNamespacePin"/>），
        /// 直接比会让整套补丁对本 mod 静默失效。</para>
        /// </summary>
        public static string NamespaceOf(Mod mod)
        {
            if (mod == null)
                return null;

            try
            {
                string bound;
                if (PointerToNamespace.TryGetValue(mod.Pointer, out bound) && IsRegistered(bound))
                    return bound;
            }
            catch (Exception) { }

            try
            {
                var nsp = mod.Namespace;
                if (IsRegistered(nsp))
                    return nsp;
            }
            catch (Exception) { }

            return null;
        }

        // ------------------------------------------------------------------
        // 补丁体
        // ------------------------------------------------------------------

        [HarmonyPostfix]
        private static void AddDefinitionPostfix(Mod __instance, Definition def)
        {
            try
            {
                var nsp = NamespaceOf(__instance);
                if (nsp == null || def == null)
                    return;
                Track(nsp, def);
            }
            catch (Exception e)
            {
                LogError("AddDefinition 跟踪失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        [HarmonyPrefix]
        private static bool GetDefinitionsPrefix(Mod __instance, ref Il2CppReferenceArray<Definition> __result)
        {
            var nsp = NamespaceOf(__instance);
            if (nsp == null)
                return true;    // 别人的 mod 走原逻辑

            try
            {
                var list = ListFor(nsp);
                lock (list)
                {
                    var arr = new Il2CppReferenceArray<Definition>(list.Count);
                    for (var i = 0; i < list.Count; i++)
                        arr[i] = list[i];
                    __result = arr;
                }
                return false;       // 跳过原生 LINQ
            }
            catch (Exception e)
            {
                LogError("GetDefinitions 拦截失败：" + e.GetType().Name + ": " + e.Message);
                return true;
            }
        }

        [HarmonyPrefix]
        private static bool PostReloadModsPrefix(Mod __instance)
        {
            // 官方实现会遍历 definitionGroup.GetDefinitions()（原生 LINQ）。
            // 我们的定义都不实现 ICachedDefinition，官方遍历对我们本来就是空操作，
            // 而枚举本身有崩溃风险 —— 直接跳过。
            return NamespaceOf(__instance) == null;
        }

        // ------------------------------------------------------------------
        // 辅助
        // ------------------------------------------------------------------

        private static void Patch(Harmony harmony, string label, System.Reflection.MethodInfo target,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            if (target == null)
            {
                LogError("找不到补丁目标：" + label);
                return;
            }
            try
            {
                harmony.Patch(target, prefix: prefix, postfix: postfix);
            }
            catch (Exception e)
            {
                LogError("补丁失败：" + label + " → " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void LogInfo(string msg)
        {
            try { Plugin.Log?.LogInfo("[InjectedDefinitions] " + msg); } catch (Exception) { }
        }

        private static void LogWarn(string msg)
        {
            try { Plugin.Log?.LogWarning("[InjectedDefinitions] " + msg); } catch (Exception) { }
        }

        private static void LogError(string msg)
        {
            try { Plugin.Log?.LogError("[InjectedDefinitions] " + msg); } catch (Exception) { }
        }
    }
}
