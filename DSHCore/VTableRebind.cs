using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;

namespace DSHCore
{
    /// <summary>
    /// 修复注入类 vtable 虚方法槽被 IL2CPP Class::Init 重置的问题。
    ///
    /// 现象（0.7.0 win32_debug / metadata v31，与 test-1 一致）：
    /// 1. ClassInjector 注册注入类时，vtable 槽被绑定到托管 trampoline（VTableDiag 已实锤）。
    /// 2. 游戏首次实例化 / Class::Init 时用基类 vtable 重建子类 vtable ——
    ///    抽象槽（CreateSaveData/LoadSaveData）被重置为抽象 thunk，调用即抛
    ///    EntryPointNotFoundException；virtual 槽若被重置则 override 静默失效。
    /// 3. 游戏侧 SaveManager/ModLoader 走 IL2CPP vtable 虚调用 → 崩溃或行为缺失。
    ///
    /// 修复：在游戏的关键调用点执行前，把注入类相关 vtable 槽重新写回
    /// 托管 trampoline 指针（幂等；只修注入类真正 override 的方法）。
    /// 槽号来自 dump.cs (win32_debug) MVZ2Logic.Modding.Mod：
    ///   16 PreInit / 17 OnInit / 18 PostInit / 19 PostGameInit / 20 PostReloadMods
    ///   21 CreateSaveData(abstract) / 22 LoadSaveData(abstract) / 23 PostAllSaveDataLoaded
    /// </summary>
    public static unsafe class VTableRebind
    {
        private static readonly (int Slot, string Name)[] TargetSlots =
        {
            (16, "PreInit"),
            (17, "OnInit"),
            (18, "PostInit"),
            (19, "PostGameInit"),
            (20, "PostReloadMods"),
            (21, "CreateSaveData"),
            (22, "LoadSaveData"),
            (23, "PostAllSaveDataLoaded"),
        };

        /// <summary>对指定的注入 Mod 类型确保相关 vtable 槽指向托管 trampoline。幂等。</summary>
        public static void Ensure(Type logicType)
        {
            try
            {
                if (logicType == null)
                    return;
                var classPtr = Il2CppClassPointerStore.GetNativeClassPointer(logicType);
                if (classPtr == IntPtr.Zero)
                    return;
                var klass = UnityVersionHandler.Wrap((Il2CppClass*)classPtr);
                var vtable = (VirtualInvokeData*)klass.VTable;

                var repaired = false;
                foreach (var (slot, name) in TargetSlots)
                {
                    if (BindSlot(klass, vtable, logicType, name, slot))
                        repaired = true;
                }
                if (repaired)
                    Plugin.Log?.LogInfo("[VTableRebind] vtable slots repaired for " + logicType.FullName);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[VTableRebind] error: " + e);
            }
        }

        private static bool BindSlot(INativeClassStruct klass, VirtualInvokeData* vtable,
            Type logicType, string methodName, int slot)
        {
            // 只处理注入类真正 override 的方法（未 override 的方法在 Methods 中找不到，静默跳过）。
            for (var i = 0; i < klass.MethodCount; i++)
            {
                var m = UnityVersionHandler.Wrap(klass.Methods[i]);
                var name = Marshal.PtrToStringAnsi(m.Name);
                if (name != methodName)
                    continue;

                if (vtable[slot].methodPtr != m.MethodPointer)
                {
                    vtable[slot].method = klass.Methods[i];
                    vtable[slot].methodPtr = m.MethodPointer;
                    Plugin.Log?.LogInfo("[VTableRebind] " + logicType.Name + " slot " + slot + " "
                        + methodName + " -> 0x" + m.MethodPointer.ToInt64().ToString("X"));
                    return true;
                }
                return false;
            }
            return false;
        }
    }
}
