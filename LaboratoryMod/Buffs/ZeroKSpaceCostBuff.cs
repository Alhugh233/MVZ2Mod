using System;
using System.Globalization;
using DSHCore;
using Il2CppInterop.Runtime.Injection;
using MVZ2Logic.Modding;
using PVZEngine;
using PVZEngine.Auras;
using PVZEngine.Buffs;
using PVZEngine.Callbacks;
using PVZEngine.Modifiers;
using PVZEngine.SeedPacks;

namespace LaboratoryMod.Buffs
{
    /// <summary>
    /// 零度空间费用倍率 Buff（0.7 规范化注册版）。
    ///
    /// 0.6.4 原版（ZeroKSpaceCostBuff）：
    ///   AddModifier(new FloatModifier(EngineSeedProps.COST, Multiply, PROP_MULTIPLIER))
    /// 其中 PROP_MULTIPLIER 是 buff 实例属性，每个 seedpack 的 buff 通过
    /// SetProperty(PROP_MULTIPLIER, max(1, rechargeQuality)) 写入各自的乘数。
    ///
    /// 0.7 规范化版：
    ///   - 乘数作为**定义级常量**写进 FloatModifier（mod 无法把自定义 PropertyMeta
    ///     注册进 PropertyMapper —— 未注册属性 key==0，modifier 求值退回 ConstValue(0)，
    ///     费用会被乘成 0；因此不引用 buff 实例属性，直接以常量构造）。
    ///   - 关卡 recharge quality 只有 vanilla 档位（Short=1 / Long=3 / VeryLong=5），
    ///     因此注册乘数 {3, 5} 两个**唯一 ID** 的正式定义（zero_k_space_cost_x3 / _x5），
    ///     在 LoadStages 时机随关卡定义一起 mod.AddDefinition —— 使：
    ///       (1) LoadBuffMetas 遍历到定义时能在 buffs.xml 找到词条（不警告）；
    ///       (2) 读档 Buff.CreateFromSerializable 用 Content.GetBuffDefinition(id) 能恢复定义；
    ///       (3) 存档只记 definitionID，恢复出的定义常量即正确乘数。
    ///   - 其余罕见乘数（自定义 recharge）运行时动态创建未注册定义兜底（不参与存档恢复，
    ///     进关后由 OnUpdate 幂等重挂）。
    ///
    /// modifier 求值（NumberModifierCalculator）按 Priority 分层，层内顺序为
    /// Set → Add → AddMultiple → Multiply（Multiply 最后乘）。文文新闻
    /// （BunbunmaruNewspaper）对 COST 是 Set modifier，与本 buff 的 Multiply
    /// modifier 同层（priority 0）时先 Set 后乘 —— 与文文新闻天然叠加。
    /// </summary>
    public class ZeroKSpaceCostBuff : BuffDefinition
    {
        /// <summary>本 mod 预注册的乘数档位（与 vanilla recharge quality 对应）。</summary>
        public static readonly float[] REGISTERED_MULTIPLIERS = { 3f, 5f };

        /// <summary>乘数 → 已注册定义的缓存（LoadStages 时填充）。</summary>
        private static readonly System.Collections.Generic.Dictionary<float, ZeroKSpaceCostBuff>
            registeredByMultiplier = new System.Collections.Generic.Dictionary<float, ZeroKSpaceCostBuff>();

        /// <summary>乘数 → 动态创建的未注册定义（罕见乘数兜底）。</summary>
        private static readonly System.Collections.Generic.Dictionary<float, ZeroKSpaceCostBuff>
            dynamicByMultiplier = new System.Collections.Generic.Dictionary<float, ZeroKSpaceCostBuff>();

        public ZeroKSpaceCostBuff(IntPtr ptr) : base(ptr)
        {
        }

        /// <summary>
        /// 创建乘数为 multiplier 的正式定义并注册到 mod（LoadStages 时机调用）。
        /// 同一乘数只注册一次。
        /// </summary>
        internal static void RegisterDefinitions(Mod mod)
        {
            foreach (var mult in REGISTERED_MULTIPLIERS)
            {
                if (registeredByMultiplier.ContainsKey(mult))
                    continue;
                var def = CreateInstance(mult);
                if (def == null)
                    continue;
                mod.AddDefinition(def);
                Plugin.TrackDefinition(def);
                registeredByMultiplier[mult] = def;
                Plugin.Log?.LogInfo("[ZeroKSpaceCostBuff] registered def " + def.GetID()
                    + " (multiplier=" + mult + ")");
            }
        }

        /// <summary>获取乘数对应的定义：优先已注册，其次动态创建兜底。</summary>
        internal static ZeroKSpaceCostBuff GetDefinition(float multiplier)
        {
            if (registeredByMultiplier.TryGetValue(multiplier, out var registered))
                return registered;
            if (dynamicByMultiplier.TryGetValue(multiplier, out var existing))
                return existing;
            var created = CreateInstance(multiplier);
            if (created != null)
            {
                dynamicByMultiplier[multiplier] = created;
                Plugin.Log?.LogWarning("[ZeroKSpaceCostBuff] dynamic unregistered def for multiplier="
                    + multiplier + " (存档恢复不受支持，本局内可用)");
            }
            return created;
        }

        private static ZeroKSpaceCostBuff CreateInstance(float multiplier)
        {
            var buff = new ZeroKSpaceCostBuff(ClassInjector.DerivedConstructorPointer<ZeroKSpaceCostBuff>());
            ClassInjector.DerivedConstructorBody(buff);

            // 补做原生 BuffDefinition(string,string) ctor 的初始化。
            // ID 内嵌乘数（unique per multiplier），存档按 definitionID 即可恢复正确乘数。
            var name = "zero_k_space_cost_x" + multiplier.ToString("0.#", CultureInfo.InvariantCulture);
            buff.id = Il2CppPin.Pin(new NamespaceID(LabMod.spaceName, name));
            buff.propertyDict = Il2CppPin.Pin(new PropertyDictionary());
            buff.triggers = Il2CppPin.Pin(new Il2CppSystem.Collections.Generic.List<ITrigger>());
            buff.auraDefinitions = Il2CppPin.Pin(new Il2CppSystem.Collections.Generic.List<AuraEffectDefinition>());
            buff.modelInsertions = Il2CppPin.Pin(new Il2CppSystem.Collections.Generic.List<ModelInsertion>());

            // 把常量乘数 modifier 直接放入定义级 modifiers 列表
            // （Buff.GetModifierAt 委托到 Definition；列表由代理字段暴露，可写）。
            buff.modifiers = Il2CppPin.Pin(new Il2CppSystem.Collections.Generic.List<PropertyModifier>());
            buff.modifiers.Add(new FloatModifier(
                EngineSeedProps.COST,
                NumberOperator.Multiply,
                multiplier,
                0));
            return buff;
        }
    }
}
