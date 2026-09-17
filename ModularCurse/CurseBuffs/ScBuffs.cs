using System;
using MVZ2.Vanilla.Entities;
using MVZ2Logic.Level;
using PVZEngine.Modifiers;

namespace ModularCurse
{
    // =====================================================================
    // SC 池（13 条）
    //
    // 同 QcBuffs：本文件只放"定义级"内容，行为在 CurseRuntime。
    // =====================================================================

    /// <summary>SC-C1：超出 10 个后，每个器械的存在使对应器械的花费 +5%。</summary>
    public class ScC1Buff : CurseBuffBase
    {
        public ScC1Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c1";
    }

    /// <summary>SC-C2：无可激活或忠诚标签的器械放下时叛变 2s。</summary>
    public class ScC2Buff : CurseBuffBase
    {
        public ScC2Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c2";
    }

    /// <summary>SC-C3：敌怪本体（不包括防具与 BOSS）每 1s 恢复 1% 生命（每秒最多 20 点）。</summary>
    public class ScC3Buff : CurseBuffBase
    {
        public ScC3Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c3";
    }

    /// <summary>SC-C4：有尸体的敌怪在第 10 波后第一次死亡时有 20% 概率完全复活（包括防具）。</summary>
    public class ScC4Buff : CurseBuffBase
    {
        public ScC4Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c4";
    }

    /// <summary>SC-C5：敌怪的防具获得 25% 免伤（在 SC-C8 之后计算）。</summary>
    public class ScC5Buff : CurseBuffBase
    {
        public ScC5Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c5";
    }

    /// <summary>SC-C6：每使用 3 个碎片使下 1 个碎片无效。</summary>
    public class ScC6Buff : CurseBuffBase
    {
        public ScC6Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c6";
    }

    /// <summary>SC-C7：所有敌怪重量 +1（给敌怪挂 <see cref="ScC7ForEnemyBuff"/>）。</summary>
    public class ScC7Buff : CurseBuffBase
    {
        public ScC7Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c7";
    }

    /// <summary>SC-C7 挂到敌怪身上的质量子 buff（不占诅咒位）。</summary>
    public class ScC7ForEnemyBuff : CurseBuffBase
    {
        public ScC7ForEnemyBuff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c7_for_enemy";
        public override bool CountsAsCurse => false;

        internal override void Setup()
        {
            AddModifier(new FloatModifier(VanillaEntityProps.MASS, NumberOperator.Add, 1f));
        }
    }

    /// <summary>SC-C8：敌怪获得 5 的护甲（每次受伤减少 5 点伤害）。</summary>
    public class ScC8Buff : CurseBuffBase
    {
        public ScC8Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c8";
    }

    /// <summary>SC-C9：敌怪受到的 debuff 时间变为 1/3。</summary>
    public class ScC9Buff : CurseBuffBase
    {
        public ScC9Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c9";
    }

    /// <summary>
    /// SC-C10：敌怪出场时无敌 8s（用原版铁幕 buff 做提示）。
    /// 只对**自然生成**的敌怪生效：波次出怪没有生成来源（SpawnerReference == null），
    /// 而泡泡枪的泡泡 / 炮筒的炮弹僵尸 / 死灵法师的骨墙 / 恐怖之母的寄生虫等
    /// 衍生物都是被实体 Spawn 出来的，一律豁免（见 CurseRuntime.IsNaturallySpawned）。
    /// </summary>
    public class ScC10Buff : CurseBuffBase
    {
        public ScC10Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c10";
    }

    /// <summary>SC-C11：每行不在最前面那一格的敌怪受伤 ×33%。</summary>
    public class ScC11Buff : CurseBuffBase
    {
        public ScC11Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c11";
    }

    /// <summary>SC-C12：敌怪生成间隔 ×93%（不影响第一波出怪）。</summary>
    public class ScC12Buff : CurseBuffBase
    {
        public ScC12Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c12";

        internal override void Setup()
        {
            // 波内最长等待时间 → 出怪间隔；第一波时间（FIRST_WAVE_TIME）不动。
            AddModifier(new FloatModifier(LogicStageProps.WAVE_MAX_TIME,
                NumberOperator.Multiply, CurseRuntime.SC_C12_INTERVAL_MULTIPLIER));
        }
    }

    /// <summary>SC-C13：敌怪受到单次超出本体血量上限的伤害时不会立刻死亡，改为无敌并额外行动 2s。</summary>
    public class ScC13Buff : CurseBuffBase
    {
        public ScC13Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c13";
    }
}
