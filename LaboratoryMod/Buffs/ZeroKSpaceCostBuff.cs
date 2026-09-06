using System;
using Il2CppInterop.Runtime.Injection;
using PVZEngine;
using PVZEngine.Auras;
using PVZEngine.Buffs;
using PVZEngine.Callbacks;
using PVZEngine.Modifiers;
using PVZEngine.SeedPacks;

namespace LaboratoryMod.Buffs
{
    /// <summary>
    /// 零度空间费用倍率 Buff（0.7 注入版）。
    ///
    /// 0.6.4 原版（ZeroKSpaceCostBuff）：
    ///   AddModifier(new FloatModifier(EngineSeedProps.COST, Multiply, PROP_MULTIPLIER))
    /// 其中 PROP_MULTIPLIER 是 buff 实例属性，每个 seedpack 的 buff 通过
    /// SetProperty(PROP_MULTIPLIER, max(1, rechargeQuality)) 写入各自的乘数。
    ///
    /// 0.7 mod 版差异：mod 程序集无法把自定义 PropertyMeta 注册进 PropertyMapper
    /// （只有游戏加载的 assembly 才会被扫描），未注册属性的 key==0，
    /// PropertyModifier 求值时 buffPropertyName 无效会退回 ConstValue（0），
    /// 导致费用被乘成 0。因此这里不引用 buff 属性，而是直接以**常量乘数**
    /// 构造 FloatModifier —— 同一乘数值共享一个 BuffDefinition 实例，
    /// 不同乘数值各自一个实例（recharge quality 档位有限，缓存开销可忽略）。
    ///
    /// modifier 求值（NumberModifierCalculator）按 Priority 分层，层内顺序为
    /// Set → Add → AddMultiple → Multiply（Multiply 最后乘）。文文新闻
    /// （BunbunmaruNewspaper）对 COST 是 Set modifier，与本 buff 的 Multiply
    /// modifier 同层（priority 0）时先 Set 后乘 —— 与文文新闻天然叠加。
    /// </summary>
    public class ZeroKSpaceCostBuff : BuffDefinition
    {
        public ZeroKSpaceCostBuff(IntPtr ptr) : base(ptr)
        {
        }

        internal static ZeroKSpaceCostBuff CreateInstance(float multiplier)
        {
            var buff = new ZeroKSpaceCostBuff(ClassInjector.DerivedConstructorPointer<ZeroKSpaceCostBuff>());
            ClassInjector.DerivedConstructorBody(buff);

            // 补做原生 BuffDefinition(string,string) ctor 的初始化。
            buff.id = new NamespaceID(LabMod.spaceName, "zero_k_space_cost");
            buff.propertyDict = new PropertyDictionary();
            buff.triggers = new Il2CppSystem.Collections.Generic.List<ITrigger>();
            buff.auraDefinitions = new Il2CppSystem.Collections.Generic.List<AuraEffectDefinition>();
            buff.modelInsertions = new Il2CppSystem.Collections.Generic.List<ModelInsertion>();

            // 把常量乘数 modifier 直接放入定义级 modifiers 列表
            // （Buff.GetModifierAt 委托到 Definition；列表由代理字段暴露，可写）。
            buff.modifiers = new Il2CppSystem.Collections.Generic.List<PropertyModifier>();
            buff.modifiers.Add(new FloatModifier(
                EngineSeedProps.COST,
                NumberOperator.Multiply,
                multiplier,
                0));
            return buff;
        }
    }
}
