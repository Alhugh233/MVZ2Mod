using System;
using System.Collections.Generic;
using System.Text;

namespace DSHCore
{
    /// <summary>
    /// 跨插件注册表：依赖 DSHCore 的内容 mod 在此登记自己的命名空间与注入 Mod 类型，
    /// DSHCore 的通用补丁（XML metas 注入 / vtable 兜底 / arcade Index 平移）
    /// 据此**按命名空间**生效。
    ///
    /// 调用时机：插件 Load() 里。注册表本身不做任何注入，所以登记与
    /// ClassInjector.RegisterTypeInIl2Cpp 的先后顺序无所谓；
    /// 推荐直接用 ModBootstrap.Register 一步完成（见该类注释）。
    /// </summary>
    public static class CoreRegistry
    {
        private static readonly List<RegisteredMod> mods = new List<RegisteredMod>();
        private static readonly object gate = new object();

        /// <summary>一个内容 mod 的登记信息。</summary>
        public class RegisteredMod
        {
            /// <summary>游戏的 ModInfo 命名空间（如 mvz2_lab / modular_curse）。</summary>
            public string Namespace;

            /// <summary>
            /// 注入的 Mod 子类（继承 MVZ2Logic.Modding.Mod，且已 RegisterTypeInIl2Cpp）。
            /// DSHCore 会在游戏的关键调用点对它执行 VTableRebind.Ensure。
            /// </summary>
            public Type LogicType;

            /// <summary>XML 中不带前缀的 NamespaceID 解析成哪个命名空间（默认 mvz2 内置）。</summary>
            public string XmlDefaultNamespace = "mvz2";
        }

        /// <summary>登记（或以同命名空间覆盖）一个内容 mod。重复登记是幂等的。</summary>
        public static void Register(RegisteredMod mod)
        {
            if (mod == null || string.IsNullOrEmpty(mod.Namespace))
            {
                Plugin.Log?.LogWarning("[CoreRegistry] 忽略无效登记（Namespace 为空）");
                return;
            }
            lock (gate)
            {
                mods.RemoveAll(m => m.Namespace == mod.Namespace);
                mods.Add(mod);
            }
            Plugin.Log?.LogInfo("[CoreRegistry] registered nsp=" + mod.Namespace
                + " logic=" + mod.LogicType?.FullName
                + " xmlDefault=" + mod.XmlDefaultNamespace);
        }

        /// <summary>全部登记项的快照（补丁遍历用；返回副本，可安全并发读取）。</summary>
        public static RegisteredMod[] GetAll()
        {
            lock (gate)
            {
                return mods.ToArray();
            }
        }

        /// <summary>按命名空间取登记项；未登记返回 null。</summary>
        public static RegisteredMod Get(string nsp)
        {
            if (string.IsNullOrEmpty(nsp))
                return null;
            lock (gate)
            {
                foreach (var m in mods)
                {
                    if (m.Namespace == nsp)
                        return m;
                }
            }
            return null;
        }

        /// <summary>
        /// 诊断用：把当前登记情况拼成一行，例如
        /// <c>mvz2_lab(Logic=LaboratoryMod.LabMod, xml=mvz2), modular_curse(...)</c>。
        /// 建议各 mod 在自己的自检日志里打一次，便于确认"补丁是否认得我"。
        /// </summary>
        public static string FormatAll()
        {
            var all = GetAll();
            if (all.Length == 0)
                return "(none)";
            var sb = new StringBuilder();
            for (var i = 0; i < all.Length; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(all[i].Namespace)
                    .Append("(Logic=")
                    .Append(all[i].LogicType == null ? "<null>" : all[i].LogicType.Name)
                    .Append(", xml=")
                    .Append(all[i].XmlDefaultNamespace)
                    .Append(')');
            }
            return sb.ToString();
        }
    }
}
