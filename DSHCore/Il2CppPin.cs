using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace DSHCore
{
    /// <summary>
    /// il2cpp 对象 GC 根驻留。
    ///
    /// ── 为什么必须有这个东西 ────────────────────────────────────────────
    /// 把「托管代码 new 出来的 il2cpp 对象」存进**注入类实例的字段**后，该引用
    /// 不会被 il2cpp GC 正确追踪：写入不经 write barrier，且注入类的 GC 描述符
    /// 不含继承来的引用字段。几次 GC 之后对象被回收，字段变成悬垂指针。
    ///
    /// 症状（非常反直觉，容易误判成"逻辑写错了"）：
    ///   - definition.GetID() 的值比较恒为 false
    ///     → 游戏查不到我们的 Definition（街机列表为空、gotolevel 找不到关卡）；
    ///   - 读该字段的 string 直接抛
    ///     "Fatal error. Internal CLR error. (0x80131506)"（Il2CppStringToManaged）。
    ///
    /// 对策：对每个写入注入对象字段的 il2cpp 对象调用
    /// <c>il2cpp_gchandle_new(obj, false)</c> 使其成为 GC 根并**永不释放**。
    /// 被钉的都是定义级对象（与进程同生命周期），数量固定且极少，
    /// 不构成实际泄漏（可用 <see cref="Count"/> 核对数量）。
    ///
    /// ── 什么时候要 Pin ────────────────────────────────────────────────
    /// 只要满足「托管 new + 存进注入类字段」两个条件就要 Pin。典型清单：
    ///   NamespaceID / PropertyDictionary / List&lt;ITrigger&gt; / List&lt;Behaviour&gt; /
    ///   DefinitionGroup / 各种 *Meta / 自己 new 出来的 Behaviour、BuffDefinition…
    /// （字段是**原生对象**、或对象由游戏创建时不需要。）
    /// </summary>
    public static class Il2CppPin
    {
        private static readonly List<IntPtr> handles = new List<IntPtr>();
        private static readonly object gate = new object();

        /// <summary>
        /// 把 il2cpp 对象钉住为 GC 根（幂等，可重复调用）。
        /// 返回原对象，便于写成 <c>field = Il2CppPin.Pin(new Xxx());</c> 这样的链式赋值。
        /// </summary>
        public static T Pin<T>(T obj) where T : Il2CppObjectBase
        {
            if (obj == null)
                return obj;
            try
            {
                var handle = IL2CPP.il2cpp_gchandle_new(obj.Pointer, false);
                lock (gate)
                {
                    handles.Add(handle);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Il2CppPin] pin failed: " + e.GetType().Name + ": " + e.Message);
            }
            return obj;
        }

        /// <summary>已钉住的对象数（只增不减）。自检时可核对它是否与预期条数一致。</summary>
        public static int Count
        {
            get
            {
                lock (gate)
                {
                    return handles.Count;
                }
            }
        }
    }
}
