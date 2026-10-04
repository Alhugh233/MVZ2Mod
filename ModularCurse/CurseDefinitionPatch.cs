using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2.Modding;
using MVZ2.Saves;
using MVZ2Logic.Modding;
using PVZEngine;

namespace ModularCurse
{
    /// <summary>
    /// 让本 mod 的定义在 metadata v31 的注入环境下能被官方链路正常看到。
    ///
    /// 背景（LaboratoryMod 实测结论，本 mod 同样适用）：
    ///   - <c>DefinitionGroup.GetDefinitions()</c> 内部是 <c>SelectMany + ToArray</c>
    ///     原生 LINQ，<c>DefinitionList.GetAllDefinitions&lt;T&gt;()</c> 内部是
    ///     <c>OfType&lt;T&gt;()</c>；在注入定义参与枚举时 v31 环境非确定性抛
    ///     MethodAccessException 或返回空数组。
    ///   - <c>GlobalGame.AddMod(mod)</c> 正是走 <c>mod.GetDefinitions()</c> ——
    ///     一旦返回空，本 mod 的 24 个诅咒 buff 和 24 条诅咒定义都不会进入全局内容表，
    ///     诅咒面板将是空的。
    ///
    /// 对策（与 LaboratoryMod 相同）：
    ///   - 用托管 List 手工跟踪本 nsp 的全部定义（<c>Mod.AddDefinition</c> 后缀）；
    ///   - 拦截 <c>Mod.GetDefinitions()</c>，对本 nsp 直接返回该托管 List；
    ///   - 跳过 <c>Mod.PostReloadMods</c>（官方实现遍历 definitionGroup.GetDefinitions()）。
    ///
    /// 另：在官方 <c>ModLoader.LoadStages</c> 时机（位于 LoadDefinitions 内，
    /// 早于 LoadBuffMetas / LoadCurseMetas）创建 24 个诅咒 buff 定义。
    /// </summary>
    internal static class CurseDefinitionPatch
    {
        private static bool applied;
        private static Harmony harmony;

        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            Plugin.Log?.LogInfo("[CurseDefinitionPatch] Apply begin");
            harmony = new Harmony(Plugin.GUID + ".defs");

            // 「跟踪 Mod.AddDefinition / 拦 Mod.GetDefinitions / 跳 Mod.PostReloadMods」
            // 三个补丁已收敛到 DSHCore.InjectedDefinitions（公共设施，按命名空间各管一份）——
            // 本 mod 在 Plugin.Load 里 Register("modular_curse") 即可，不要在这里再装一遍。
            // 剩下的是**诅咒特有**的时机：定义创建、属性重算、面板顺序。
            Patch("ModLoader.LoadStages",
                AccessTools.Method(typeof(ModLoader), "LoadStages"),
                prefix: new HarmonyMethod(typeof(CurseDefinitionPatch), nameof(LoadStagesPrefix)));

            // 属性键可能在我们跑 LoadCurseMetas 时还未注册（PropertyMapper 按 mod
            // 顺序扫描程序集）—— 存档重算解锁时先把词条属性补上，保证时序上必然生效。
            Patch("SaveManager.EvaluateUnlocks",
                AccessTools.Method(typeof(SaveManager), "EvaluateUnlocks", new[] { typeof(bool) }),
                prefix: new HarmonyMethod(typeof(CurseDefinitionPatch), nameof(EvaluateUnlocksPrefix)));

            // 诅咒面板顺序：官方的缓存顺序 = 定义注册顺序，而本 mod 的 LoadStages
            // 早于原版 mod（PropertyMapper 按 mod 顺序扫描），于是模组诅咒会排在
            // 原版诅咒上方。这里把内置命名空间（mvz2）的诅咒稳定地挤到最前。
            Patch("SaveManager.GetUnlockedCurses",
                AccessTools.Method(typeof(SaveManager), "GetUnlockedCurses"),
                postfix: new HarmonyMethod(typeof(CurseDefinitionPatch), nameof(GetUnlockedCursesPostfix)));

            Plugin.Log?.LogInfo("[CurseDefinitionPatch] all patches processed");
        }

        private static void Patch(string label, System.Reflection.MethodInfo target,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null)
        {
            if (target == null)
            {
                Plugin.Log?.LogError("[CurseDefinitionPatch] patch target not found: " + label);
                return;
            }
            try
            {
                harmony.Patch(target, prefix: prefix, postfix: postfix);
                Plugin.Log?.LogInfo("[CurseDefinitionPatch] patched " + label);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CurseDefinitionPatch] patch failed: " + label + " -> "
                    + e.GetType().Name + ": " + e.Message);
            }
        }

        private static IntPtr loggedOtherMod;

        /// <summary>
        /// 是否属于本 mod。
        ///
        /// **双重判据**：先比 native 指针（最可靠），再比 <c>mod.Namespace</c> 字符串。
        /// 只靠字符串曾经出现过间歇性失配（注入实例的 Namespace 字段读到空），
        /// 于是整套补丁对本 mod 静默失效 —— 表现为诅咒一个都没注册、列表为空。
        /// </summary>
        private static bool IsOurs(Mod mod)
        {
            if (mod == null)
                return false;
            try
            {
                // 每次判定前先把 Namespace 字段刷成有效值并钉住
                // （注入实例的引用字段不被 il2cpp GC 扫描，字符串会被回收）。
                ModularCurseMod.EnsureNamespace();
                var self = ModularCurseMod.self;
                if (self != null && mod.Pointer == self.Pointer)
                    return true;
                string nsp = null;
                try
                {
                    nsp = mod.Namespace;
                }
                catch (Exception e)
                {
                    nsp = "<throw " + e.GetType().Name + ">";
                }
                if (nsp == ModularCurseMod.spaceName)
                    return true;
                if (loggedOtherMod != mod.Pointer)
                {
                    loggedOtherMod = mod.Pointer;
                    Plugin.Log?.LogInfo("[CurseDefinitionPatch] 观察到非本 mod: ptr=" + mod.Pointer
                        + " selfPtr=" + (self == null ? 0 : self.Pointer)
                        + " namespace='" + (nsp ?? "<null>") + "'");
                }
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CurseDefinitionPatch] IsOurs error: " + e);
                return false;
            }
        }

        /// <summary>
        /// 官方 LoadStages 时机（LoadDefinitions 内、LoadBuffMetas 之前）注册 24 个诅咒 buff。
        /// LoadStages 本来对没有 stages.xml 的本 mod 是空操作，因此直接放行原逻辑。
        /// </summary>
        [HarmonyPrefix]
        private static void LoadStagesPrefix(ModLoader __instance, Mod mod)
        {
            try
            {
                var mine = IsOurs(mod);
                Plugin.Log?.LogInfo("[CurseDefinitionPatch] LoadStages: isOurs=" + mine
                    + " selfPtr=" + (ModularCurseMod.self == null ? 0 : ModularCurseMod.self.Pointer));
                if (!mine)
                    return;
                CurseBuffs.RegisterDefinitions(mod);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CurseDefinitionPatch] LoadStagesPrefix error: " + e);
            }
        }

        /// <summary>存档重算解锁前，先把 curses.xml 的词条属性重新套一遍。</summary>
        [HarmonyPrefix]
        private static void EvaluateUnlocksPrefix()
        {
            try
            {
                CurseMetaApplier.ApplyAll();
                // 兼顾：修饰符也必须在属性键注册之后重建（幂等）。
                CurseBuffs.ApplyModifiers();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CurseDefinitionPatch] EvaluateUnlocksPrefix error: " + e);
            }
        }

        /// <summary>
        /// 把原版（内置命名空间）的诅咒稳定地排到模组诅咒之前：
        /// 保留两侧原有相对顺序，只做一次稳定分组。
        ///
        /// 注意：原生返回类型是 <c>Il2CppReferenceArray&lt;NamespaceID&gt;</c>
        /// 而不是 <c>NamespaceID[]</c>（后者会让 Harmony 报
        /// "Cannot assign method return type ... to __result type ..."）。
        /// </summary>
        [HarmonyPostfix]
        private static void GetUnlockedCursesPostfix(ref Il2CppReferenceArray<NamespaceID> __result)
        {
            try
            {
                var list = __result;
                if (list == null || list.Length < 2)
                    return;
                var nsp = BuiltinNamespace;
                var builtin = new System.Collections.Generic.List<NamespaceID>(list.Length);
                var others = new System.Collections.Generic.List<NamespaceID>(list.Length);
                for (var i = 0; i < list.Length; i++)
                {
                    var id = list[i];
                    if (id != null && id.SpaceName == nsp)
                        builtin.Add(id);
                    else
                        others.Add(id);
                }
                if (builtin.Count == 0 || others.Count == 0)
                    return;
                builtin.AddRange(others);

                var arr = new Il2CppReferenceArray<NamespaceID>(builtin.Count);
                for (var i = 0; i < builtin.Count; i++)
                    arr[i] = builtin[i];
                __result = arr;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CurseDefinitionPatch] GetUnlockedCursesPostfix error: " + e);
            }
        }

        /// <summary>内置命名空间（mvz2）。</summary>
        private static string BuiltinNamespace
        {
            get
            {
                try
                {
                    return MVZ2Logic.Global.BuiltinNamespace;
                }
                catch (Exception)
                {
                    return "mvz2";
                }
            }
        }
    }
}
