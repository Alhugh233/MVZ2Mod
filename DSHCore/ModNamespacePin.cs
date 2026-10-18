using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using MVZ2Logic.Modding;

namespace DSHCore
{
    /// <summary>
    /// 把注入类 <c>Mod</c> 实例的 <c>Namespace</c> 字段**钉成 GC 根**并保证其始终可读。
    ///
    /// ── 为什么必须做（三重原因叠加）────────────────────────────────────
    ///   1. 我们用 <c>DerivedConstructorPointer/Body</c> 造实例，**原生 ctor 不执行**，
    ///      所以 <c>&lt;Namespace&gt;k__BackingField</c> 只能靠托管侧赋值
    ///      （<see cref="InjectedMod.Initialize"/> 里那行）；
    ///   2. 该字段属于「注入类实例的引用字段」，il2cpp GC 不扫描它
    ///      （同 <see cref="Il2CppPin"/> 注释里那条已知限制），赋进去的 il2cpp 字符串
    ///      会在若干次 GC 之后被回收，之后 <c>mod.Namespace</c> 读到空/悬垂值；
    ///   3. 游戏自己要用 <c>mod.Namespace</c>（<c>ModLoader.LoadXxxMetas</c> 里的
    ///      <c>var nsp = mod.Namespace;</c> + <c>res.GetModXxxMetas(nsp)</c>）来找本 mod
    ///      的 meta 与内容表 —— 一旦读空，本 mod 的定义/元数据全都进不了游戏。
    ///
    /// 症状是**间歇性**的：同一份代码有时一切正常，有时内容一个都没注册，
    /// 而且日志里没有任何错误。这非常容易被误判成"逻辑写错了"。
    ///
    /// ── 为什么不能简单地再赋一次值 ─────────────────────────────────────
    /// Il2CppInterop 生成的 <c>string</c> 属性 setter 走的是
    /// <c>ManagedStringToIl2Cpp(value)</c>，**每次都会新建一个 il2cpp 字符串**。
    /// 所以「先 <c>Pin(spaceName)</c> 再赋值」是无效的：钉住的那个字符串和字段里的
    /// 根本不是同一个对象。必须自己算出字段地址，把**钉住的那个指针**写进去。
    ///
    /// ── 做法（全部走已核实的 API）────────────────────────────────────
    ///   - 自建 il2cpp 字符串 + <c>il2cpp_gchandle_new(…, false)</c> **永久钉住**；
    ///   - <c>il2cpp_object_get_class</c> → 沿父类链找 <c>&lt;Namespace&gt;k__BackingField</c>
    ///     → <c>il2cpp_field_get_offset</c> 算出字段地址；
    ///   - 只比指针（不解引用旧值 —— 旧值可能是悬垂指针），不一致就用
    ///     <c>FieldWriteWbarrierStub</c>（与 interop 生成的 setter 完全同一条路径）写回。
    ///
    /// 幂等、可反复调用、线程安全。失败只记一行警告并停用该项保护，不影响其它功能。
    /// </summary>
    public static class ModNamespacePin
    {
        private sealed class PinState
        {
            /// <summary>钉住的 il2cpp 字符串指针（每个命名空间唯一，永不释放）。</summary>
            public IntPtr Str;

            /// <summary>字段 FieldInfo 与偏移（首次使用时解析一次；同一命名空间只解析一次）。</summary>
            public IntPtr Field;
            public int Offset = -1;
            public bool FieldMissing;
            public bool Warned;
        }

        private static readonly Dictionary<string, PinState> States
            = new Dictionary<string, PinState>(StringComparer.Ordinal);
        private static readonly object gate = new object();

        /// <summary>该命名空间是否已经钉过一次（诊断用）。</summary>
        public static bool IsPinned(string spaceName)
        {
            lock (gate)
            {
                PinState st;
                return spaceName != null && States.TryGetValue(spaceName, out st) && st.Str != IntPtr.Zero;
            }
        }

        /// <summary>
        /// 只看「对象里那个字段的**原始指针**」是否等于我们钉住的那根，是就返回对应命名空间，否则 null。
        ///
        /// <para><b>这是给 <c>NamespaceOf</c> 用的安全回退。</b>它只做两件事：
        /// 按算好的偏移读一个指针（<see cref="Marshal.ReadIntPtr(IntPtr, int)"/>），
        /// 然后和已知的指针做相等比较 —— <b>不解引用字符串、不发起任何原生调用</b>。</para>
        ///
        /// <para>为什么必须这么绕：<c>mod.Namespace</c> 是原生自动属性 getter，它会调
        /// <c>Il2CppStringToManaged</c> 去解引用字段里的指针。注入实例的引用字段不被 il2cpp GC 扫描，
        /// 一旦那根字符串被回收，字段里就是悬垂指针 —— 此时读属性会**在原生层直接访问冲突**，
        /// C# 的 try/catch 拦不住（0.7.0 实测：0xC0000005）。比指针则完全没有这个风险。</para>
        /// </summary>
        public static string MatchPinned(IntPtr instancePtr)
        {
            if (instancePtr == IntPtr.Zero)
                return null;

            lock (gate)
            {
                foreach (var pair in States)
                {
                    var st = pair.Value;
                    if (st.FieldMissing || st.Str == IntPtr.Zero || st.Offset < 0)
                        continue;
                    IntPtr current;
                    try { current = Marshal.ReadIntPtr(instancePtr, st.Offset); }
                    catch (Exception) { continue; }
                    if (current == st.Str)
                        return pair.Key;
                }
            }
            return null;
        }

        /// <summary>
        /// 确保 <c>instance.Namespace</c> 可读且指向 <paramref name="spaceName"/>。
        /// 幂等。返回是否成功（成功 = 字段指针已确认，或写入完成）。
        /// </summary>
        /// <remarks>
        /// 每个命名空间只解析一次字段偏移，因此同一个命名空间应固定使用同一个注入类
        /// （本仓库的约定就是「一个命名空间一个 <c>Mod</c> 派生类」）。
        /// </remarks>
        public static bool Ensure(Mod instance, string spaceName)
        {
            if (instance == null || string.IsNullOrEmpty(spaceName))
                return false;

            PinState st;
            lock (gate)
            {
                if (!States.TryGetValue(spaceName, out st))
                {
                    st = new PinState();
                    States[spaceName] = st;
                }
            }

            lock (st)
            {
                try
                {
                    if (st.Str == IntPtr.Zero)
                    {
                        st.Str = IL2CPP.ManagedStringToIl2Cpp(spaceName);
                        // 永久 GC 根：字符串与进程同生命周期，不释放。
                        IL2CPP.il2cpp_gchandle_new(st.Str, false);
                    }

                    if (st.FieldMissing)
                        return false;

                    if (st.Field == IntPtr.Zero)
                    {
                        // IL2CPP 元数据里自动属性的字段名是 <Namespace>k__BackingField；
                        // Il2CppInterop 只是把它改写成 _Namespace_k__BackingField，原生查找两个都试。
                        var klass = IL2CPP.il2cpp_object_get_class(instance.Pointer);
                        while (klass != IntPtr.Zero && st.Field == IntPtr.Zero)
                        {
                            st.Field = FindField(klass, "<Namespace>k__BackingField");
                            if (st.Field == IntPtr.Zero)
                                st.Field = FindField(klass, "_Namespace_k__BackingField");
                            if (st.Field != IntPtr.Zero)
                                break;
                            klass = IL2CPP.il2cpp_class_get_parent(klass);
                        }

                        if (st.Field == IntPtr.Zero)
                        {
                            st.FieldMissing = true;
                            Warn(st, "[ModNamespacePin] 找不到 <Namespace>k__BackingField（已停用该项保护）");
                            return false;
                        }
                        st.Offset = (int)IL2CPP.il2cpp_field_get_offset(st.Field);
                    }

                    var addr = IntPtr.Add(instance.Pointer, st.Offset);
                    var current = Marshal.ReadIntPtr(addr);
                    if (current == st.Str)
                        return true;

                    IL2CPP.FieldWriteWbarrierStub(instance.Pointer, addr, st.Str);
                    Plugin.Log?.LogInfo("[ModNamespacePin] " + spaceName + " 的 Namespace 字段已重写（"
                        + (current == IntPtr.Zero ? "空" : "失效") + " → " + spaceName + "）");
                    return true;
                }
                catch (Exception e)
                {
                    Warn(st, "[ModNamespacePin] 钉住 " + spaceName + " 失败：" + e.GetType().Name
                        + ": " + e.Message);
                    return false;
                }
            }
        }

        private static void Warn(PinState st, string msg)
        {
            if (st.Warned)
                return;
            st.Warned = true;
            Plugin.Log?.LogWarning(msg);
        }

        /// <summary>按名找字段（找不到返回 0，不抛）。</summary>
        private static IntPtr FindField(IntPtr klass, string name)
        {
            try
            {
                return IL2CPP.GetIl2CppField(klass, name);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }
    }
}
