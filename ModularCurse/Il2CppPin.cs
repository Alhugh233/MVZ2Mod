using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace ModularCurse
{
    /// <summary>
    /// il2cpp 对象根驻留（GC 句柄）。
    ///
    /// 背景（LaboratoryMod 实测结论，本 mod 同样适用）：
    ///   把"托管代码 new 出来的 il2cpp 对象"存进**注入类实例的字段**后，该引用
    ///   不会被 il2cpp GC 正确追踪（写入不经 write barrier，且注入类的 GC 描述符
    ///   不含继承来的引用字段）。经若干次 GC 后对象被回收，字段变成悬垂指针。
    ///   症状：定义对象 GetID() 值比较恒为 false（游戏查不到我们的 Definition）。
    ///
    /// 对策：对写入注入对象字段的每个 il2cpp 对象调用 il2cpp_gchandle_new 使其
    ///   成为 GC 根并永不释放。这些都是定义级对象（与进程同生命周期），数量固定
    ///   且极少，不构成实际泄漏。
    /// </summary>
    internal static class Il2CppPin
    {
        private static readonly List<IntPtr> handles = new List<IntPtr>();

        /// <summary>把 il2cpp 对象钉住为 GC 根（幂等，可重复调用），返回原对象便于链式使用。</summary>
        public static T Pin<T>(T obj) where T : Il2CppObjectBase
        {
            if (obj == null)
                return obj;
            try
            {
                var h = IL2CPP.il2cpp_gchandle_new(obj.Pointer, false);
                handles.Add(h);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Il2CppPin] pin failed: " + e.Message);
            }
            return obj;
        }
    }
}
