using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace CustomCore
{
    /// <summary>
    /// MVZ2 Custom Core —— 实验室内容 mod 前置插件（通用基建宿主）。
    /// 本身不向游戏注册任何 ModInfo / 游戏内容；只承载：
    ///   - ClassInjector 泛型 vtable 修复（必须先于任何注入类注册）
    ///   - VTableRebind / SaveManagerPatch 存档兜底
    ///   - 磁盘 XML metas 注入（MetaInjection）
    /// 由 LaboratoryMod 等依赖本插件（BepInDependency）的 mod 使用 CoreRegistry。
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BasePlugin
    {
        public const string GUID = "qoznos.mvz2.core";
        public const string NAME = "MVZ2 Custom Core";
        public const string VERSION = "0.1.0";

        internal static new ManualLogSource Log;

        public override void Load()
        {
            Log = base.Log;

            // 修复 ClassInjector 无法处理基类泛型虚方法 vtable 的限制。
            // 必须在任何注入类 RegisterTypeInIl2Cpp 之前应用（依赖本插件的 mod 都在其后加载）。
            ClassInjectorFix.Apply();

            // 注册磁盘 XML metas 注入补丁（幂等）。
            MetaInjection.Apply();

            // 存档读写兜底：SaveManager 对注入 Mod.Logic 做虚调用前确保 vtable 抽象槽已绑定。
            SaveManagerPatch.Apply();

            // 游戏 LoadMods 前对所有注册注入 Mod 类型统一修复 vtable。
            LoadModsPatch.Apply();

            // arcade 条目 Index 平移（LoadMods 后修正外部 mod 与 vanilla 的 Index 撞车）。
            ArcadeIndexFix.Apply();

            Log.LogInfo(NAME + " v" + VERSION + " loaded (infrastructure ready)");
        }
    }

    /// <summary>
    /// 跨插件注册表：依赖 CustomCore 的 mod 在此登记其注入 Mod 类型与 metas 目录，
    /// CustomCore 的通用补丁据此工作。
    /// </summary>
    public static class CoreRegistry
    {
        private static readonly List<RegisteredMod> mods = new List<RegisteredMod>();

        public class RegisteredMod
        {
            /// <summary>游戏的 ModInfo 命名空间（如 mvz2_lab）。</summary>
            public string Namespace;
            /// <summary>注入的 Mod 子类（继承 MVZ2Logic.Modding.Mod，需已 RegisterTypeInIl2Cpp）。</summary>
            public Type LogicType;
            /// <summary>磁盘 XML metas 所在目录（相对游戏根 StreamingAssets/Mods/&lt;nsp&gt;/Content/metas）。</summary>
            public string MetasDir;
            /// <summary>该 mod 是否把 XML 中无前缀 NamespaceID 解析为自身命名空间（默认 mvz2 内置）。</summary>
            public string XmlDefaultNamespace = "mvz2";
        }

        /// <summary>登记一个依赖 CustomCore 的内容 mod。可在任意时机调用（补丁作用域按 nsp 过滤）。</summary>
        public static void Register(RegisteredMod mod)
        {
            mods.RemoveAll(m => m.Namespace == mod.Namespace);
            mods.Add(mod);
            Plugin.Log?.LogInfo("[CoreRegistry] registered nsp=" + mod.Namespace
                + " logic=" + mod.LogicType?.FullName + " metas=" + mod.MetasDir);
        }

        public static RegisteredMod[] GetAll()
        {
            return mods.ToArray();
        }

        public static RegisteredMod Get(string nsp)
        {
            foreach (var m in mods)
            {
                if (m.Namespace == nsp)
                    return m;
            }
            return null;
        }
    }
}
