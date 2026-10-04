using System;
using MVZ2.Vanilla.Entities;
using MVZ2Logic.Level;
using PVZEngine.Modifiers;

namespace ModularCurse
{
    // =====================================================================
    // SC 池（16 条）
    //
    // 同 QcBuffs：本文件只放"定义级"内容，行为在 CurseRuntime。
    // C13~C16 是「额外」部分（游戏内名称用红字显示）。
    // =====================================================================

    /// <summary>SC-C1（怪人的肋骨）：第 10 个同种器械后，每多 3 个使该种器械的花费 +20%（13 个 → 120%，16 个 → 140%）。</summary>
    public class ScC1Buff : CurseBuffBase
    {
        public ScC1Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c1";
    }

    /// <summary>SC-C2（梦魇碎片）：无可激活 / 忠诚标签的器械放下时叛变 2s（御柱 / 冲击活塞 无视此诅咒）。</summary>
    public class ScC2Buff : CurseBuffBase
    {
        public ScC2Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c2";
    }

    /// <summary>
    /// SC-C3（恶咒之星）：第 10 波后，每个敌人死亡时使所有非免控器械眩晕 0.1s（可叠加）。
    /// 视为亡语 —— 不触发亡语的死因（地雷、蜡烛等）不会引发这次眩晕。
    /// </summary>
    public class ScC3Buff : CurseBuffBase
    {
        public ScC3Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c3";
    }

    /// <summary>SC-C4（血肉核心）：敌怪本体（不包括防具与 BOSS）每 4s 恢复 5% 生命（每次最多 100 点）。</summary>
    public class ScC4Buff : CurseBuffBase
    {
        public ScC4Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c4";
    }

    /// <summary>SC-C5（龙头骨）：敌怪的防具耐久 ×1.3（直接把上限与当前耐久都提上去，不做免伤计算）。</summary>
    public class ScC5Buff : CurseBuffBase
    {
        public ScC5Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c5";
    }

    /// <summary>SC-C6（折断的钥匙）：每使用 3 个碎片使下 1 个碎片无效。</summary>
    public class ScC6Buff : CurseBuffBase
    {
        public ScC6Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c6";
    }

    /// <summary>
    /// SC-C7（钢铁吸盘）：敌人受到的击退量减半；接触地面的敌人重量至少为 HEAVY。
    /// 不再禁止木风扇的吹飞 / 击退（也不给敌人加重量修饰符）——
    /// 只改原版的击退系数，见 CurseRuntime.ModifyKnockback。
    /// </summary>
    public class ScC7Buff : CurseBuffBase
    {
        public ScC7Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c7";
    }

    /// <summary>SC-C8（再生藤蔓）：第 10 波后，非无来源的敌怪首次死亡时改为复活至本体 15% 生命（不恢复防具、不触发亡语）。</summary>
    public class ScC8Buff : CurseBuffBase
    {
        public ScC8Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c8";
    }

    /// <summary>
    /// SC-C9（古代历石）：敌怪受到的 debuff 累积时长（持有种类 × 经过秒数）超过 36s 后，
    /// 12s 内无视所有 debuff（清除已有 debuff 并阻止继续获取）。
    /// </summary>
    public class ScC9Buff : CurseBuffBase
    {
        public ScC9Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c9";
    }

    /// <summary>
    /// SC-C10（灾厄链甲）：非无来源的敌怪出场后 10s 不会死亡，血量至少保留 1 点。
    /// 只对**自然生成**的敌怪生效（波次出怪没有生成来源，SpawnerReference == null），
    /// 泡泡枪的泡泡 / 炮筒的炮弹僵尸 / 死灵法师的骨墙 / 恐怖之母的寄生虫等衍生物一律豁免。
    /// </summary>
    public class ScC10Buff : CurseBuffBase
    {
        public ScC10Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c10";
    }

    /// <summary>SC-C11（权衡之杖）：每行不在最前面那一格的敌人受伤 ×40%。</summary>
    public class ScC11Buff : CurseBuffBase
    {
        public ScC11Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c11";
    }

    /// <summary>SC-C12（黄铜齿轮）：敌怪生成间隔 ×90%（不影响第一波出怪）。</summary>
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

    /// <summary>SC-C13（永恒之心）：第 10 波后，非无来源的敌怪受到单次超出（本体上限 + 当前防具耐久）的伤害时不会立刻死亡，改为无敌并额外行动 3s。</summary>
    public class ScC13Buff : CurseBuffBase
    {
        public ScC13Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c13";
    }

    /// <summary>SC-C14（命运之瓶）：每 10 波时禁用所有器械放置 / 镐子 / 星之碎片 15s（不影响驱动器）。</summary>
    public class ScC14Buff : CurseBuffBase
    {
        public ScC14Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c14";
    }

    /// <summary>SC-C15（强固补丁）：敌怪每秒受到的伤害被限制为「当前血量/耐久的 40%」或 600（取最大值）；防具吃不完的溢出伤害结算到本体。</summary>
    public class ScC15Buff : CurseBuffBase
    {
        public ScC15Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c15";
    }

    /// <summary>SC-C16（超缓冲胶）：敌怪获得 5 的护甲（本体与防具的伤害都先减 5 点），但最终结算至少保留 1 点伤害。</summary>
    public class ScC16Buff : CurseBuffBase
    {
        public ScC16Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "sc_c16";
    }
}
