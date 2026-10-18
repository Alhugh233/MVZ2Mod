using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Runtime.VersionSpecific.Class;

namespace DSHCore
{
    /// <summary>
    /// 修复注入类 vtable 虚方法槽被 IL2CPP Class::Init 重置的问题。
    ///
    /// 现象（0.7.0 的内测构建 win32_debug / metadata v31）：
    /// 1. ClassInjector 注册注入类时，vtable 槽被绑定到托管 trampoline（VTableDiag 已实锤）。
    /// 2. 游戏首次实例化 / Class::Init 时用基类 vtable 重建子类 vtable ——
    ///    抽象槽（CreateSaveData/LoadSaveData）被重置为抽象 thunk，调用即抛
    ///    EntryPointNotFoundException；virtual 槽若被重置则 override 静默失效。
    /// 3. 游戏侧 SaveManager/ModLoader 走 IL2CPP vtable 虚调用 → 崩溃或行为缺失。
    ///
    /// 修复：在游戏的关键调用点执行前，把注入类相关 vtable 槽重新写回
    /// 托管 trampoline 指针（幂等；只修注入类真正 override 的方法）。
    ///
    /// <para><b>槽号从游戏自己读，不写死。</b>把槽号写成常量是这里最容易埋雷的地方：
    /// 0.7.0 往 <c>Mod</c> 里插了一个抽象槽（<c>LoadSaveData</c> 拆成
    /// <c>SerializeSaveData</c>/<c>DeserializeSaveData</c>），后面全体后移 ——
    /// <c>PostAllSaveDataLoaded</c> 从 23 挪到 24，而 23 变成了 <c>DeserializeSaveData</c>。
    /// 若仍按写死的 <c>(23, "PostAllSaveDataLoaded")</c> 去写，就会把
    /// <c>PostAllSaveDataLoaded</c> 的指针塞进 <c>DeserializeSaveData</c> 的槽 ——
    /// 游戏一读档就调到错的方法，而且**既没有编译错误也没有任何日志**。</para>
    ///
    /// <para><b>槽号也不能从注入类自己身上读。</b>注入类里那些方法在 IL2CPP 元数据里
    /// 是新声明的方法，<c>MethodInfo.slot</c> 是 <c>0xFFFF</c>（未绑定虚槽）——
    /// 0.7.0 实测 7 个目标方法全部 slot=65535，改成读 <c>m.Slot</c> 的结果是"全都跳过、一个没修"。
    /// 权威来源是**基类 <c>Mod</c> 的元数据**：那里的虚方法才有真正的 slot，
    /// 注入类的 vtable 就是照它铺的。</para>
    ///
    /// <para>槽位对照（仅留档，代码不再依赖）：
    /// 早期内测版：16 PreInit / 17 OnInit / 18 PostInit / 19 PostGameInit / 20 PostReloadMods
    /// / 21 CreateSaveData / 22 LoadSaveData / 23 PostAllSaveDataLoaded；
    /// 0.7.0：21 CreateSaveData / 22 SerializeSaveData / 23 DeserializeSaveData / 24 PostAllSaveDataLoaded。</para>
    /// </summary>
    public static unsafe class VTableRebind
    {
        /// <summary>
        /// 要修的方法**名字**。槽号运行时从基类 <c>Mod</c> 的元数据读，不写死（见类注释）。
        ///
        /// <para>名单里同时留着新旧两代存档接口名：基类里找不到哪个名字就静默跳过，
        /// 所以同一份代码在早期内测版与 0.7.0 上都能用。</para>
        /// </summary>
        private static readonly string[] TargetMethods =
        {
            "PreInit",
            "OnInit",
            "PostInit",
            "PostGameInit",
            "PostReloadMods",
            "CreateSaveData",
            "LoadSaveData",          // 早期内测版
            "SerializeSaveData",     // 0.7.0 起
            "DeserializeSaveData",   // 0.7.0 起（LoadSaveData 的继任者）
            "PostAllSaveDataLoaded",
        };

        /// <summary>方法名 → 基类 <c>Mod</c> 元数据里的虚槽号（-1 = 该版本没有这个方法）。</summary>
        private static readonly Dictionary<string, int> BaseSlots = new Dictionary<string, int>();

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
                if (!klass.IsVtableInitialized)
                {
                    // vtable 还没建起来，槽位不可信；等游戏 Class::Init 之后再修。
                    Plugin.Log?.LogWarning("[VTableRebind] vtable 尚未初始化，跳过：" + logicType.Name);
                    return;
                }
                var vtable = (VirtualInvokeData*)klass.VTable;

                var repaired = false;
                for (var i = 0; i < TargetMethods.Length; i++)
                {
                    if (BindSlot(klass, vtable, logicType, TargetMethods[i]))
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
            Type logicType, string methodName)
        {
            // ① 槽号来自基类 Mod 的元数据（注入类自己的 slot 全是 0xFFFF）。
            var slot = ResolveBaseSlot(methodName);
            if (slot < 0)
                return false;

            // ② 只处理注入类真正 override 的方法（未 override 的找不到，静默跳过）。
            for (var i = 0; i < klass.MethodCount; i++)
            {
                var m = UnityVersionHandler.Wrap(klass.Methods[i]);
                var name = Marshal.PtrToStringAnsi(m.Name);
                if (name != methodName)
                    continue;

                if (slot >= klass.VtableCount)
                {
                    Plugin.Log?.LogWarning("[VTableRebind] " + logicType.Name + " " + methodName
                        + " slot " + slot + " 超出 vtable 长度 " + klass.VtableCount + "，跳过");
                    return false;
                }

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

        /// <summary>
        /// 在基类 <c>MVZ2Logic.Modding.Mod</c> 的 IL2CPP 元数据里按名字找虚槽号。
        /// 找不到（该游戏版本没这个方法，如早期内测版没有 SerializeSaveData）返回 -1。
        /// 结果缓存 —— 每个 class 只查一次。
        /// </summary>
        private static int ResolveBaseSlot(string methodName)
        {
            lock (BaseSlots)
            {
                int cached;
                if (BaseSlots.TryGetValue(methodName, out cached))
                    return cached;
            }

            var slot = -1;
            try
            {
                var basePtr = Il2CppClassPointerStore
                    .GetNativeClassPointer(typeof(MVZ2Logic.Modding.Mod));
                if (basePtr != IntPtr.Zero)
                {
                    var baseKlass = UnityVersionHandler.Wrap((Il2CppClass*)basePtr);
                    for (var i = 0; i < baseKlass.MethodCount; i++)
                    {
                        var m = UnityVersionHandler.Wrap(baseKlass.Methods[i]);
                        if (Marshal.PtrToStringAnsi(m.Name) != methodName)
                            continue;
                        // 非虚方法/未绑定槽位是 0xFFFF，天然被上界挡掉。
                        if (m.Slot < baseKlass.VtableCount)
                        {
                            slot = m.Slot;
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[VTableRebind] 解析基类槽位失败 " + methodName + ": " + e);
            }
            Plugin.Log?.LogInfo("[VTableRebind] 基类 Mod." + methodName + " -> slot " + slot);
            lock (BaseSlots) BaseSlots[methodName] = slot;
            return slot;
        }
    }
}
