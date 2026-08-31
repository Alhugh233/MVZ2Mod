using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace MVZ2LabMod
{
    /// <summary>
    /// 绕过 ClassInjector 的两处已知限制：
    /// 1. 基类 vtable 含泛型虚方法时（Definition.TryGetProperty&lt;T&gt; 等），
    ///    SystemTypeFromIl2CppType 无法把泛型参数 T 解析为 System.Type → 返回 object 占位，
    ///    使 GetMethod 找不到重写而保留基类实现（我们不重写泛型虚方法，符合预期）。
    /// 2. RewriteType 会把 System.String 重写为 Il2CppSystem.String，
    ///    但托管 override 的签名是 System.String（与代理基类一致）→ GetMethod 匹配失败，
    ///    抽象方法槽（如 Mod.LoadSaveData）保留抽象 stub，调用时抛 EntryPointNotFoundException。
    ///    这里让 string 保持 System.String，使 vtable 槽正确替换为托管实现。
    /// </summary>
    internal static class ClassInjectorFix
    {
        private static readonly MethodInfo GetFullNameMethod = typeof(ClassInjector).GetMethod(
            "GetIl2CppTypeFullName", BindingFlags.Static | BindingFlags.NonPublic)!;

        public static void Apply()
        {
            var target = typeof(ClassInjector).GetMethod(
                "SystemTypeFromIl2CppType", BindingFlags.Static | BindingFlags.NonPublic)!;
            var harmony = new Harmony("qoznos.mvz2.labmod.classinjectorfix");
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(ClassInjectorFix), nameof(SystemTypeFromIl2CppTypePrefix)));
            Plugin.Log?.LogInfo("ClassInjector generic vtable patch applied");
        }

        [HarmonyPrefix]
        private static bool SystemTypeFromIl2CppTypePrefix(IntPtr typePointer, ref Type __result)
        {
            // 复刻原方法前半段：GetIl2CppTypeFullName + Type.GetType
            var fullName = (string)GetFullNameMethod.Invoke(null, new object[] { typePointer });
            var type = Type.GetType(fullName);
            if (type == typeof(string))
            {
                // 关键：避免原方法 RewriteType 把 System.String 改成 Il2CppSystem.String。
                // vtable 遍历用该类型调 GetMethod 匹配托管 override，代理基类签名是 System.String。
                Plugin.Log?.LogInfo("[ClassInjectorFix] string 保持 System.String (" + fullName + ")");
                __result = typeof(string);
                return false;
            }
            if (type != null)
                return true; // 正常路径交给原方法（内部还会做 RewriteType）

            // 泛型参数（如 "T, PVZEngine.Base"）或其它无法解析的类型 → object 占位
            Plugin.Log?.LogInfo("[ClassInjectorFix] 无法解析 " + fullName + " → object 占位");
            __result = typeof(object);
            return false;
        }
    }
}
