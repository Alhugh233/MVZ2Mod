using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;

namespace MVZ2LabMod
{
    /// <summary>
    /// 诊断：转储注入类的 IL2CPP vtable 槽状态。
    /// 目的：确认 LabMod 的 Slot 21 (CreateSaveData) / Slot 22 (LoadSaveData)
    /// 是否被 ClassInjector 正确绑定到托管 trampoline。
    /// 判定方法：把槽的 methodPtr 与 LabMod Methods 数组里同名方法的
    /// MethodPointer 比较——相等即绑定成功，否则槽里是基类抽象 thunk。
    /// </summary>
    internal static unsafe class VTableDiag
    {
        public static void Dump(string label, Type type, int from, int to)
        {
            var classPtr = Il2CppClassPointerStore.GetNativeClassPointer(type);
            if (classPtr == IntPtr.Zero)
            {
                Plugin.Log?.LogInfo("[VTableDiag] " + label + ": no class pointer");
                return;
            }
            var klass = UnityVersionHandler.Wrap((Il2CppClass*)classPtr);
            var vtable = (VirtualInvokeData*)klass.VTable;
            Plugin.Log?.LogInfo("[VTableDiag] " + label + " class=0x" + classPtr.ToInt64().ToString("X")
                + " vtableCount=" + klass.VtableCount + " methodCount=" + klass.MethodCount);

            for (var i = 0; i < klass.MethodCount; i++)
            {
                var m = UnityVersionHandler.Wrap(klass.Methods[i]);
                var name = Marshal.PtrToStringAnsi(m.Name) ?? "<unnamed>";
                Plugin.Log?.LogInfo("[VTableDiag] " + label + " method[" + i + "] " + name
                    + " methodPtr=0x" + m.MethodPointer.ToInt64().ToString("X"));
            }

            for (var i = from; i <= to && i < klass.VtableCount; i++)
            {
                var slot = vtable[i];
                string name = "<null>";
                if (slot.method != null)
                {
                    var m = UnityVersionHandler.Wrap(slot.method);
                    name = Marshal.PtrToStringAnsi(m.Name) ?? "<unnamed>";
                }
                Plugin.Log?.LogInfo("[VTableDiag] " + label + " slot[" + i + "] " + name
                    + " methodPtr=0x" + slot.methodPtr.ToInt64().ToString("X"));
            }
        }
    }
}
