using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MVZ2.Modding;
using MVZ2.Saves;

namespace MVZ2LabMod
{
    /// <summary>
    /// 兜底补丁：SaveManager.LoadModData 收到的 ModInfo 可能是缓存副本，
    /// 其 Logic 是游戏重建的原生 Mod 实例（抽象槽无实现），存档读写会抛
    /// EntryPointNotFoundException。此处用字段直写兜底替换。
    /// LoadModData 是 private 方法，必须用 AccessTools.Method 获取。
    /// </summary>
    internal static class SaveManagerPatch
    {
        public static void Apply()
        {
            var harmony = new Harmony("qoznos.mvz2.labmod.savemanagerpatch");
            var target = AccessTools.Method(typeof(SaveManager), "LoadModData");
            if (target == null)
            {
                Plugin.Log?.LogError("[SaveManagerPatch] LoadModData not found! Patch NOT applied.");
                return;
            }
            harmony.Patch(
                target,
                prefix: new HarmonyMethod(typeof(SaveManagerPatch), nameof(Prefix)));
            Plugin.Log?.LogInfo("SaveManager.LoadModData patch applied");

            // WriteSaveData 内部也有 modInfo.Logic.LoadSaveData(validateString) 虚调用，
            // 同样需要在执行前修复 vtable（否则存档写入的验证会失败）。
            var writeTarget = AccessTools.Method(typeof(SaveManager), "WriteSaveData");
            if (writeTarget != null)
            {
                harmony.Patch(
                    writeTarget,
                    prefix: new HarmonyMethod(typeof(SaveManagerPatch), nameof(WriteSaveDataPrefix)));
                Plugin.Log?.LogInfo("SaveManager.WriteSaveData patch applied");
            }
            else
            {
                Plugin.Log?.LogWarning("[SaveManagerPatch] WriteSaveData not found!");
            }
        }

        [HarmonyPrefix]
        private static void WriteSaveDataPrefix()
        {
            VTableRebind.Ensure();
        }

        [HarmonyPrefix]
        private static void Prefix(int userIndex, ModInfo modInfo)
        {
            try
            {
                // 关键：游戏侧 LoadModData 会对 modInfo.Logic 做 IL2CPP 虚调用，
                // 注入类 vtable 的抽象槽可能已被 Class::Init 重置为抽象 thunk，
                // 这里在游戏代码执行前重新绑定（幂等，开销极小）。
                VTableRebind.Ensure();

                if (modInfo == null)
                {
                    Plugin.Log?.LogInfo("[SM] LoadModData userIndex=" + userIndex + " modInfo=null");
                    return;
                }
                Plugin.Log?.LogInfo("[SM] LoadModData userIndex=" + userIndex + " nsp=" + modInfo.Namespace
                    + " modInfo.Pointer=" + modInfo.Pointer.ToString("X"));
                if (modInfo.Namespace != LabMod.spaceName)
                    return;
                var current = modInfo._Logic_k__BackingField;
                Plugin.Log?.LogInfo("[SM] mvz2_lab Logic=" + (current == null ? "null" : current.GetType().FullName)
                    + " ptr=" + (current == null ? "null" : current.Pointer.ToString("X"))
                    + " sharedPtr=" + (ModLogicFix.Instance == null ? "null" : ModLogicFix.Instance.Pointer.ToString("X")));

                // 内存级诊断：确认对象的真实 IL2CPP klass 与 ModInfo 字段布局。
                if (current != null)
                {
                    var logicKlass = IL2CPP.il2cpp_object_get_class(current.Pointer);
                    var logicKlassName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(logicKlass));
                    Plugin.Log?.LogInfo("[SM] Logic 真实 klass=0x" + logicKlass.ToInt64().ToString("X")
                        + " name=" + logicKlassName);
                    var labKlass = Il2CppClassPointerStore.GetNativeClassPointer(typeof(LabMod));
                    Plugin.Log?.LogInfo("[SM] LabMod 注入 klass=0x" + labKlass.ToInt64().ToString("X"));
                    // 原生读 ModInfo.Logic backing field（dump.cs 偏移 0x20），
                    // 与代理字段读取结果对比，检测代理偏移是否错误。
                    var rawFieldPtr = Marshal.ReadIntPtr(modInfo.Pointer, 0x20);
                    Plugin.Log?.LogInfo("[SM] ModInfo+0x20 原始字段=0x" + rawFieldPtr.ToInt64().ToString("X")
                        + " 与代理读取" + (rawFieldPtr == current.Pointer ? "一致" : "不一致!"));
                }
                // 注意：不要在此处通过代理调用 LoadSaveData —— 代理直连 IL2CPP 方法指针，
                // 若 vtable 槽未绑定会触发原生异常直接杀死进程（无托管 catch 机会）。
                // 改用 VTableDiag 在注册后静态检查槽状态。
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[SM] Prefix error: " + e);
            }
        }
    }
}
