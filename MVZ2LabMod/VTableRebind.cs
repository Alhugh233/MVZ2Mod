using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;

namespace MVZ2LabMod
{
    /// <summary>
    /// 修复注入类 vtable 抽象槽被 IL2CPP Class::Init 重置的问题。
    ///
    /// 现象链：
    /// 1. ClassInjector 注册 LabMod 时，vtable 槽 21 (CreateSaveData) / 22 (LoadSaveData)
    ///    被正确绑定到托管 trampoline（VTableDiag 已实锤）。
    /// 2. 游戏首次实例化 new LabMod() 时，IL2CPP 对注入类执行 Class::Init，
    ///    用基类 vtable 重建子类 vtable —— 抽象槽被重置为抽象 thunk
    ///    （一个共享的、调用即抛 EntryPointNotFoundException 的原生 stub）。
    /// 3. 游戏侧 SaveManager.LoadModData:244 走 IL2CPP vtable 虚调用
    ///    modInfo.Logic.LoadSaveData(...) → 命中抽象 thunk → EntryPointNotFoundException。
    ///
    /// 说明：SelfTest 通过是因为托管侧经代理类的调用走托管分派，不经 IL2CPP vtable。
    ///
    /// 修复：在游戏的关键调用点（LoadModData / WriteSaveData 前缀）执行前，
    /// 把注入类 vtable 槽 21/22 重新写回我们 Methods 数组里的 trampoline 指针。
    /// 槽号来自 dump.cs（Mod Slot: 21 CreateSaveData / Slot: 22 LoadSaveData）并
    /// 与 VTableDiag 实测一致。
    /// </summary>
    internal static unsafe class VTableRebind
    {
        // dump.cs: public abstract ModSaveData CreateSaveData();  // Slot: 21
        // dump.cs: public abstract ModSaveData LoadSaveData(string json);  // Slot: 22
        private const int CreateSaveDataSlot = 21;
        private const int LoadSaveDataSlot = 22;

        /// <summary>
        /// 确保 LabMod 注入类的 vtable 槽 21/22 指向托管 trampoline。
        /// 幂等：槽已正确时不做任何修改。
        /// </summary>
        public static void Ensure()
        {
            try
            {
                var labClassPtr = Il2CppClassPointerStore.GetNativeClassPointer(typeof(LabMod));
                if (labClassPtr == IntPtr.Zero)
                    return;
                var labKlass = UnityVersionHandler.Wrap((Il2CppClass*)labClassPtr);
                var vtable = (VirtualInvokeData*)labKlass.VTable;

                var bound = BindSlot(labKlass, vtable, "CreateSaveData", CreateSaveDataSlot);
                var bound2 = BindSlot(labKlass, vtable, "LoadSaveData", LoadSaveDataSlot);

                if (bound || bound2)
                    Plugin.Log?.LogInfo("[VTableRebind] vtable slots repaired");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[VTableRebind] error: " + e);
            }
        }

        private static bool BindSlot(INativeClassStruct labKlass, VirtualInvokeData* vtable,
            string methodName, int slot)
        {
            for (var i = 0; i < labKlass.MethodCount; i++)
            {
                var m = UnityVersionHandler.Wrap(labKlass.Methods[i]);
                var name = Marshal.PtrToStringAnsi(m.Name);
                if (name != methodName)
                    continue;

                if (vtable[slot].methodPtr != m.MethodPointer)
                {
                    vtable[slot].method = labKlass.Methods[i];
                    vtable[slot].methodPtr = m.MethodPointer;
                    Plugin.Log?.LogInfo("[VTableRebind] slot " + slot + " " + methodName
                        + " -> 0x" + m.MethodPointer.ToInt64().ToString("X"));
                    return true;
                }
                return false;
            }
            Plugin.Log?.LogWarning("[VTableRebind] method not found in LabMod methods: " + methodName);
            return false;
        }
    }
}
