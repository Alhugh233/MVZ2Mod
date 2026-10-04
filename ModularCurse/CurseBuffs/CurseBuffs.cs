using System;
using System.Collections.Generic;
using DSHCore;
using Il2CppInterop.Runtime.Injection;
using MVZ2Logic.Modding;
using PVZEngine;
using PVZEngine.Auras;
using PVZEngine.Buffs;
using PVZEngine.Callbacks;
using PVZEngine.Modifiers;

namespace ModularCurse
{
    /// <summary>
    /// 诅咒 buff 定义的注册与创建。
    ///
    /// 为什么不走官方程序集扫描：本 mod 的 ModInfo.Assemblies 为空
    /// （官方 LoadAssemblies 对注入程序集做属性扫描易触发 metadata v31 的
    /// 原生泛型问题），改为在官方 LoadStages 时机手工创建定义并 AddDefinition
    /// —— 与 LaboratoryMod 的已验证做法一致。
    ///
    /// 创建注入类实例时不跑原生 ctor（Il2CppInterop 下不可靠），改为
    /// ClassInjector.DerivedConstructorPointer/Body + 手工补字段初值。
    /// 字段清单对应各基类的字段初始化器：
    ///   Definition:          id, propertyDict, triggers
    ///   BuffDefinition:      modifiers, auraDefinitions, modelInsertions
    ///   CurseBuffDefinition: modifiers += CURSE_COUNT +1
    ///
    /// 注意：修饰符必须**推迟**到 <see cref="ApplyModifiers"/>（本 mod 的 PostInit，
    /// 即所有 mod 的 PropertyMapper.InitPropertyMaps 跑完之后）才创建，
    /// 否则 PropertyMeta 的隐式转换会把尚未注册的 Key==0 占位键拷进修饰符，
    /// 导致 CURSE_COUNT 等修饰符静默失效。
    /// </summary>
    internal static class CurseBuffs
    {
        private static readonly Type[] types =
        {
            typeof(QcC1Buff), typeof(QcC2Buff), typeof(QcC3Buff), typeof(QcC4Buff),
            typeof(QcC5Buff), typeof(QcC6Buff), typeof(QcC7Buff), typeof(QcC8Buff),
            typeof(QcC9Buff), typeof(QcC10Buff), typeof(QcC11Buff), typeof(QcC12Buff),
            typeof(ScC1Buff), typeof(ScC2Buff), typeof(ScC3Buff), typeof(ScC4Buff),
            typeof(ScC5Buff), typeof(ScC6Buff), typeof(ScC7Buff), typeof(ScC8Buff),
            typeof(ScC9Buff), typeof(ScC10Buff), typeof(ScC11Buff), typeof(ScC12Buff),
            typeof(ScC13Buff), typeof(ScC14Buff), typeof(ScC15Buff), typeof(ScC16Buff),
            typeof(ZyC1Buff), typeof(ZyC2Buff), typeof(ZyC3Buff), typeof(ZyC4Buff),
            typeof(ZyC5Buff), typeof(ZyC6Buff), typeof(ZyC7Buff), typeof(ZyC8Buff),
            typeof(ZyC9Buff), typeof(ZyC10Buff), typeof(ZyC11Buff), typeof(ZyC12Buff),
            typeof(ZyC13Buff), typeof(ZyC14Buff), typeof(ZyC15Buff), typeof(ZyC16Buff),
            typeof(ZyC15DimBuff),
        };

        /// <summary>全体诅咒 buff 定义（按注册顺序）。</summary>
        private static readonly List<CurseBuffBase> definitions = new List<CurseBuffBase>();
        private static bool typesRegistered;
        private static bool defsRegistered;
        private static bool modifiersApplied;

        /// <summary>注册注入类型（必须在任何实例化之前调用）。</summary>
        public static void RegisterAll()
        {
            if (typesRegistered)
                return;
            typesRegistered = true;
            foreach (var type in types)
            {
                try
                {
                    ClassInjector.RegisterTypeInIl2Cpp(type);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogError("[CurseBuffs] RegisterTypeInIl2Cpp(" + type.Name + ") failed: " + e);
                }
            }
            Plugin.Log?.LogInfo("[CurseBuffs] " + types.Length + " 个注入类型已注册");
        }

        /// <summary>创建并注册全部诅咒 buff 定义（官方 LoadStages 时机调用）。</summary>
        public static void RegisterDefinitions(Mod mod)
        {
            if (mod == null || defsRegistered)
                return;
            defsRegistered = true;

            Create(mod, CurseBuffBase.Build<QcC1Buff>);
            Create(mod, CurseBuffBase.Build<QcC2Buff>);
            Create(mod, CurseBuffBase.Build<QcC3Buff>);
            Create(mod, CurseBuffBase.Build<QcC4Buff>);
            Create(mod, CurseBuffBase.Build<QcC5Buff>);
            Create(mod, CurseBuffBase.Build<QcC6Buff>);
            Create(mod, CurseBuffBase.Build<QcC7Buff>);
            Create(mod, CurseBuffBase.Build<QcC8Buff>);
            Create(mod, CurseBuffBase.Build<QcC9Buff>);
            Create(mod, CurseBuffBase.Build<QcC10Buff>);
            Create(mod, CurseBuffBase.Build<QcC11Buff>);
            Create(mod, CurseBuffBase.Build<QcC12Buff>);

            Create(mod, CurseBuffBase.Build<ScC1Buff>);
            Create(mod, CurseBuffBase.Build<ScC2Buff>);
            Create(mod, CurseBuffBase.Build<ScC3Buff>);
            Create(mod, CurseBuffBase.Build<ScC4Buff>);
            Create(mod, CurseBuffBase.Build<ScC5Buff>);
            Create(mod, CurseBuffBase.Build<ScC6Buff>);
            Create(mod, CurseBuffBase.Build<ScC7Buff>);
            Create(mod, CurseBuffBase.Build<ScC8Buff>);
            Create(mod, CurseBuffBase.Build<ScC9Buff>);
            Create(mod, CurseBuffBase.Build<ScC10Buff>);
            Create(mod, CurseBuffBase.Build<ScC11Buff>);
            Create(mod, CurseBuffBase.Build<ScC12Buff>);
            Create(mod, CurseBuffBase.Build<ScC13Buff>);
            Create(mod, CurseBuffBase.Build<ScC14Buff>);
            Create(mod, CurseBuffBase.Build<ScC15Buff>);
            Create(mod, CurseBuffBase.Build<ScC16Buff>);

            Create(mod, CurseBuffBase.Build<ZyC1Buff>);
            Create(mod, CurseBuffBase.Build<ZyC2Buff>);
            Create(mod, CurseBuffBase.Build<ZyC3Buff>);
            Create(mod, CurseBuffBase.Build<ZyC4Buff>);
            Create(mod, CurseBuffBase.Build<ZyC5Buff>);
            Create(mod, CurseBuffBase.Build<ZyC6Buff>);
            Create(mod, CurseBuffBase.Build<ZyC7Buff>);
            Create(mod, CurseBuffBase.Build<ZyC8Buff>);
            Create(mod, CurseBuffBase.Build<ZyC9Buff>);
            Create(mod, CurseBuffBase.Build<ZyC10Buff>);
            Create(mod, CurseBuffBase.Build<ZyC11Buff>);
            Create(mod, CurseBuffBase.Build<ZyC12Buff>);
            Create(mod, CurseBuffBase.Build<ZyC13Buff>);
            Create(mod, CurseBuffBase.Build<ZyC14Buff>);
            Create(mod, CurseBuffBase.Build<ZyC15Buff>);
            Create(mod, CurseBuffBase.Build<ZyC16Buff>);

            // ZY-C15 给发光器械挂的光照减益子 buff。
            Create(mod, CurseBuffBase.Build<ZyC15DimBuff>);

            Plugin.Log?.LogInfo("[CurseBuffs] " + definitions.Count + " 个诅咒定义已注册");
        }

        private static void Create(Mod mod, Func<CurseBuffBase> factory)
        {
            try
            {
                var def = factory();
                if (def == null)
                    return;
                mod.AddDefinition(def);
                Plugin.TrackDefinition(def);
                definitions.Add(def);
                Plugin.Log?.LogInfo("[CurseBuffs] registered " + CurseIds.Nsp + ":" + def.CurseId);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CurseBuffs] create failed: " + e);
            }
        }

        /// <summary>取已注册的诅咒定义（按池内 ID）。</summary>
        public static CurseBuffBase Get(string curseId)
        {
            for (var i = 0; i < definitions.Count; i++)
            {
                if (definitions[i].CurseId == curseId)
                    return definitions[i];
            }
            return null;
        }

        /// <summary>自检：修饰符数量为 0 的定义数（应恒为 0）。</summary>
        public static int CountWithoutModifiers()
        {
            var n = 0;
            for (var i = 0; i < definitions.Count; i++)
            {
                try
                {
                    if (definitions[i].GetModifierCount() <= 0)
                        n++;
                }
                catch (Exception)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>取已注册的定义总数（自检用）。</summary>
        public static int Count => definitions.Count;

        /// <summary>
        /// 自检：每条诅咒的 CURSE_COUNT 修饰符的键是否就是当前已注册的键。
        /// 返回"键失效"的诅咒数（应恒为 0）。
        ///
        /// 这是"关卡诅咒计数不含模组诅咒"的在线复现：若修饰符创建得太早，
        /// 它持有的键会是 Key==0 的占位对象，与这里的 expected 不符。
        /// </summary>
        public static int CountWithStaleCurseCountKey()
        {
            var n = 0;
            try
            {
                var expected = ((PVZEngine.PropertyKey<int>)MVZ2Logic.Level.LogicLevelProps.CURSE_COUNT).Key;
                for (var i = 0; i < definitions.Count; i++)
                {
                    var def = definitions[i];
                    if (!def.CountsAsCurse)
                        continue;
                    try
                    {
                        // 第 0 个修饰符就是 CURSE_COUNT（ApplyModifiers 先加它）。
                        if (def.GetModifierCount() <= 0 || def.GetModifierAt(0).PropertyName.Key != expected)
                            n++;
                    }
                    catch (Exception)
                    {
                        n++;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CurseBuffs] CURSE_COUNT 键自检失败: " + e.GetType().Name);
            }
            return n;
        }

        /// <summary>
        /// 创建"绑定注入类 + 手工补字段 + Setup"的 buff 实例。
        /// 由 <see cref="CurseBuffBase.Build{T}"/> 调用。
        /// </summary>
        internal static T Construct<T>() where T : CurseBuffBase
        {
            var ptr = ClassInjector.DerivedConstructorPointer<T>();
            var def = (T)Activator.CreateInstance(typeof(T), new object[] { ptr });
            ClassInjector.DerivedConstructorBody(def);
            InitDefinition(def, def.CurseId);
            return def;
        }

        /// <summary>补上 Definition / BuffDefinition 的字段初值（不碰修饰符）。</summary>
        private static void InitDefinition(BuffDefinition def, string name)
        {
            def.id = Il2CppPin.Pin(new NamespaceID(CurseIds.Nsp, name));
            def.propertyDict = Il2CppPin.Pin(new PropertyDictionary());
            def.triggers = Il2CppPin.Pin(new Il2CppSystem.Collections.Generic.List<ITrigger>());
            def.auraDefinitions = Il2CppPin.Pin(new Il2CppSystem.Collections.Generic.List<AuraEffectDefinition>());
            def.modelInsertions = Il2CppPin.Pin(new Il2CppSystem.Collections.Generic.List<ModelInsertion>());
            try
            {
                if (def.modifiers == null)
                    def.modifiers = Il2CppPin.Pin(new Il2CppSystem.Collections.Generic.List<PropertyModifier>());
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CurseBuffs] modifiers 字段初值失败: " + e.GetType().Name);
            }
        }

        /// <summary>
        /// 展开全部定义级修饰符（CURSE_COUNT 计数 + <see cref="CurseBuffBase.Setup"/> 里的属性修饰符）。
        ///
        /// 为什么必须延后：<c>PropertyMeta&lt;T&gt;</c> 到 <c>PropertyKey&lt;T&gt;</c> 的隐式转换
        /// 会在**创建修饰符的那一刻**把 meta.key 指向的对象拷进 PropertyModifier；
        /// 而属性键的注册（<c>PropertyMapper.SetRegisteredKey</c>）会**替换** meta.key 所指向的
        /// 新对象。若修饰符先于注册创建，它持有的永远是 Key==0 的占位键 → 修饰符静默失效
        /// （这就是关卡诅咒计数不含模组诅咒的根因）。
        ///
        /// 本 mod 的 LoadStages 早于原版 mod 的 Load（PropertyMapper 按 mod 顺序扫描程序集），
        /// 而 PostInit 在所有 mod 的 Load 之后，此时键已全部注册。
        /// </summary>
        public static void ApplyModifiers()
        {
            if (modifiersApplied)
                return;
            modifiersApplied = true;
            var applied = 0;
            for (var i = 0; i < definitions.Count; i++)
            {
                var def = definitions[i];
                try
                {
                    // 基类 ctor 可能塞过一个同款但失效的修饰符，先清掉再重建。
                    var list = def.modifiers;
                    if (list == null)
                        def.modifiers = Il2CppPin.Pin(new Il2CppSystem.Collections.Generic.List<PropertyModifier>());
                    else
                        list.Clear();

                    if (def.CountsAsCurse)
                    {
                        // 等价于 CurseBuffDefinition 的 ctor 体：每挂一条诅咒，关卡 CURSE_COUNT +1。
                        def.modifiers.Add(new IntModifier(
                            MVZ2Logic.Level.LogicLevelProps.CURSE_COUNT, IntegerOperator.Add, 1));
                    }
                    def.Setup();
                    applied++;
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogError("[CurseBuffs] 修饰符展开失败 " + def.CurseId + ": " + e);
                }
            }
            Plugin.Log?.LogInfo("[CurseBuffs] " + applied + "/" + definitions.Count
                + " 个定义的修饰符已展开（属性键已注册）");
        }
    }
}
