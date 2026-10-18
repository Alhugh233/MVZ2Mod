using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Replay
{
    /// <summary>
    /// 直接读 IL2CPP 对象内存的小工具。
    ///
    /// <para><b>为什么不用 <c>ToString()</c>：</b>Il2CppInterop 的
    /// <c>Il2CppObjectBase.ToString()</c> 被改写成返回**代理类名**
    /// （所以探针只会打出 <c>Il2CppSystem.Object</c> / <c>Il2CppSystem.Type</c>），
    /// 而通过属性拿值（<c>type.Name</c>）要走一次原生调用 —— 在排查
    /// 「Type 坏掉了」时，那次调用本身就可能再炸。</para>
    ///
    /// <para>这里全部用 <see cref="Marshal"/> 读内存，**一次原生调用都不发**，
    /// 因此绝不会把进程搞崩，最多读出一串乱码。</para>
    ///
    /// <para>结构偏移（x86 / IL2CPP v31）：</para>
    /// <code>
    /// Il2CppObject  { Il2CppClass* klass; void* monitor; }              // 头 8 字节
    /// Il2CppClass   { Il2CppImage* image; void* gc_desc; const char* name; const char* namespaze; … }
    ///                                                                            ^name=+8, namespaze=+12
    /// Il2CppString  { Il2CppObject obj; int32 length; Il2CppChar chars[]; }     ^length=+8, chars=+12
    /// Il2CppReflectionType { Il2CppObject obj; const Il2CppType* type; }        ^type=+8
    /// Il2CppType    { union data; unsigned attrs:16; Il2CppTypeEnum type:8; … }  ^data=+0, type=(+4)&gt;&gt;16
    /// Il2CppGenericClass { const Il2CppType* type; Il2CppGenericContext context; }  ^type=+0
    /// Il2CppArrayType { const Il2CppType* etype; uint8 rank; … }               ^etype=+0
    /// </code>
    /// </summary>
    internal static class NativePeek
    {
        /// <summary>读一个以 0 结尾的 ANSI 字符串（最多 200 字节，超长就截断）。</summary>
        public static string CStr(IntPtr p)
        {
            if (p == IntPtr.Zero)
                return "<null>";
            var sb = new StringBuilder();
            for (int i = 0; i < 200; i++)
            {
                byte b;
                try { b = Marshal.ReadByte(p, i); }
                catch (Exception) { break; }
                if (b == 0)
                    break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        /// <summary>对象的 il2cpp 类名（<c>String</c> / <c>NamespaceID</c> / <c>RuntimeType</c> …）。</summary>
        public static string KlassName(IntPtr obj)
        {
            if (obj == IntPtr.Zero)
                return "<null obj>";
            try
            {
                var klass = Marshal.ReadIntPtr(obj);
                if (klass == IntPtr.Zero)
                    return "<klass=0>";
                return CStr(Marshal.ReadIntPtr(klass, 8));
            }
            catch (Exception e)
            {
                return "<读类名失败 " + e.GetType().Name + ">";
            }
        }

        /// <summary>
        /// 把对象描述成「类名 + 值」。
        /// <list type="bullet">
        /// <item><c>String</c> → 直接读出字符内容（Addressables 的 key 通常就是它）；</item>
        /// <item>其它 → 只给类名（再去读属性就得发原生调用，风险大）。</item>
        /// </list>
        /// </summary>
        public static string Describe(IntPtr obj)
        {
            if (obj == IntPtr.Zero)
                return "null";
            var cls = KlassName(obj);
            if (cls == "String")
            {
                try
                {
                    int len = Marshal.ReadInt32(obj, 8);
                    if (len < 0 || len > 4096)
                        return "String(len=" + len + " ??)";
                    return "\"" + Marshal.PtrToStringUni(Marshal.ReadIntPtr(obj) == IntPtr.Zero
                        ? IntPtr.Zero : obj + 12, len) + "\"";
                }
                catch (Exception e)
                {
                    return "String<读取失败 " + e.GetType().Name + ">";
                }
            }
            return cls + "(0x" + obj.ToInt64().ToString("X") + ")";
        }

        // IL2CPP_TYPE_* 里我们关心的几个
        private const int T_END = 0x00, T_VOID = 0x01, T_BOOLEAN = 0x02, T_CHAR = 0x03, T_I1 = 0x04,
            T_U1 = 0x05, T_I2 = 0x06, T_U2 = 0x07, T_I4 = 0x08, T_U4 = 0x09, T_I8 = 0x0A, T_U8 = 0x0B,
            T_R4 = 0x0C, T_R8 = 0x0D, T_STRING = 0x0E, T_PTR = 0x0F, T_BYREF = 0x10,
            T_VALUETYPE = 0x11, T_CLASS = 0x12, T_VAR = 0x13, T_ARRAY = 0x14,
            T_GENERICINST = 0x15, T_TYPEDBYREF = 0x16, T_I = 0x18, T_U = 0x19,
            T_FNPTR = 0x1B, T_OBJECT = 0x1C, T_SZARRAY = 0x1D, T_MVAR = 0x1E;

        private static readonly string[] Prims =
        {
            "System.Void", "System.Void", "System.Boolean", "System.Char", "System.SByte",
            "System.Byte", "System.Int16", "System.UInt16", "System.Int32", "System.UInt32",
            "System.Int64", "System.UInt64", "System.Single", "System.Double", "System.String",
            "System.IntPtr", "System.Object", "System.Object",
        };

        /// <summary>
        /// 把 <c>System.Type</c> 对象翻译成类型名字符串。
        /// <para>走 <c>Il2CppReflectionType → Il2CppType</c>，全程只读内存。
        /// 拿不到时返回 <c>"&lt;无法解析 …&gt;"</c>，并把中间指针一起带上 ——
        /// 一个指向垃圾的 <c>Type</c> 本身就是最关键的证据。</para>
        /// </summary>
        public static string DescribeType(IntPtr typeObj)
        {
            if (typeObj == IntPtr.Zero)
                return "null Type";
            var cls = KlassName(typeObj);
            try
            {
                var t = Marshal.ReadIntPtr(typeObj, 8); // Il2CppReflectionType.type
                if (t == IntPtr.Zero)
                    return "Type[" + cls + "] 的 Il2CppType = 0";
                var name = TypeNameOf(t, 0);
                return (name ?? "<未知>") + "  (Type[" + cls + "]@0x" + typeObj.ToInt64().ToString("X")
                    + " → Il2CppType@0x" + t.ToInt64().ToString("X") + ")";
            }
            catch (Exception e)
            {
                return "<读 Type 失败 " + e.GetType().Name + ">";
            }
        }

        /// <summary>
        /// <c>System.Type</c> → 它描述的 <c>Il2CppClass*</c>（只读；不是 class/valueType 就返回 0）。
        /// </summary>
        public static IntPtr TypeKlassPtr(IntPtr typeObj)
        {
            if (typeObj == IntPtr.Zero)
                return IntPtr.Zero;
            try
            {
                var t = Marshal.ReadIntPtr(typeObj, 8); // Il2CppReflectionType.type
                if (t == IntPtr.Zero)
                    return IntPtr.Zero;
                int bits = Marshal.ReadInt32(t, 4);
                int e = (bits >> 16) & 0xFF;
                if (e == T_CLASS || e == T_VALUETYPE)
                    return Marshal.ReadIntPtr(t);
                return IntPtr.Zero;
            }
            catch (Exception) { return IntPtr.Zero; }
        }

        /// <summary><c>System.Type</c> 的完整类型名（拿不到返回 null）。</summary>
        public static string TypeFullName(IntPtr typeObj)
        {
            if (typeObj == IntPtr.Zero)
                return null;
            try
            {
                var t = Marshal.ReadIntPtr(typeObj, 8);
                if (t == IntPtr.Zero)
                    return null;
                return TypeNameOf(t, 0);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 这个指针像不像一个「还活着」的 il2cpp 对象：类指针非空、类名可读且长度合理。
        ///
        /// <para>用于在**把参数交给原生代码之前**做一次体检 ——
        /// 一个指向垃圾的指针交给原生 <c>IsAssignableFrom</c> 就是一次访问违例。</para>
        /// </summary>
        public static bool LooksValid(IntPtr obj)
        {
            if (obj == IntPtr.Zero)
                return false;
            try
            {
                var klass = Marshal.ReadIntPtr(obj);
                if (klass == IntPtr.Zero)
                    return false;
                var namePtr = Marshal.ReadIntPtr(klass, 8);
                if (namePtr == IntPtr.Zero)
                    return false;
                var s = CStr(namePtr);
                return s.Length > 0 && s.Length < 200;
            }
            catch (Exception) { return false; }
        }

        private static string TypeNameOf(IntPtr il2CppType, int depth)
        {
            if (il2CppType == IntPtr.Zero || depth > 6)
                return null;
            int bits;
            IntPtr data;
            try
            {
                data = Marshal.ReadIntPtr(il2CppType);
                bits = Marshal.ReadInt32(il2CppType, 4);
            }
            catch (Exception)
            {
                return null;
            }
            int enumType = (bits >> 16) & 0xFF;

            if (enumType >= 0 && enumType <= 0x12 && enumType != T_VOID)
            {
                if (enumType < Prims.Length && enumType != T_PTR && enumType != T_BYREF)
                    return Prims[enumType];
            }

            switch (enumType)
            {
                case T_VOID:
                    return "System.Void";
                case T_STRING:
                    return "System.String";
                case T_OBJECT:
                    return "System.Object";
                case T_I:
                    return "System.IntPtr";
                case T_U:
                    return "System.UIntPtr";
                case T_PTR:
                    return TypeNameOf(data, depth + 1) + "*";
                case T_BYREF:
                    return TypeNameOf(data, depth + 1) + "&";
                case T_SZARRAY:
                case T_ARRAY:
                    {
                        var et = Marshal.ReadIntPtr(data); // Il2CppArrayType.etype
                        return TypeNameOf(et, depth + 1) + "[]";
                    }
                case T_GENERICINST:
                    {
                        var inner = Marshal.ReadIntPtr(data); // Il2CppGenericClass.type
                        return TypeNameOf(inner, depth + 1) + "<…>";
                    }
                case T_CLASS:
                case T_VALUETYPE:
                    {
                        if (data == IntPtr.Zero)
                            return null;
                        var name = CStr(Marshal.ReadIntPtr(data, 8));
                        var ns = CStr(Marshal.ReadIntPtr(data, 12));
                        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
                    }
                default:
                    return "<typeenum=" + enumType + ">";
            }
        }
    }
}
