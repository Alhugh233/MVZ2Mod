using System;
using MVZ2.Vanilla.Level;
using PVZEngine.Level;
using PVZEngine.Modifiers;

namespace ModularCurse
{
    // =====================================================================
    // 癫狂之触（12 条）
    //
    // 本文件只放"定义级"内容：ID、以及能用原版属性修饰符表达的常量效果。
    // 需要回调/跨帧状态的行为在 CurseRuntime（由 CurseHooks 驱动）。
    //
    // 为什么不用 buff 的原生触发器：见 CurseHooks 的类注释。
    //
    // 注意：显示名（游戏里看到的那 12 个名字）在
    // assets/Content/metas/curses.xml + CurseLocale.cs，
    // 下面的注释里写的 "QC-Cn" 一律只是诅咒 ID qc_cn 的简写，与显示名无关。
    // =====================================================================

    /// <summary>QC-C1：器械受到伤害时漏电 3s。</summary>
    public class QcC1Buff : CurseBuffBase
    {
        public QcC1Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "qc_c1";
    }

    /// <summary>QC-C2：镐子和星之碎片各自具有 3s 冷却时间。</summary>
    public class QcC2Buff : CurseBuffBase
    {
        public QcC2Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "qc_c2";
    }

    /// <summary>QC-C3：器械被星之碎片激发后凋零 27s。</summary>
    public class QcC3Buff : CurseBuffBase
    {
        public QcC3Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "qc_c3";
    }

    /// <summary>QC-C4：敌怪有 25% 概率死亡时复活（BOSS 除外）。</summary>
    public class QcC4Buff : CurseBuffBase
    {
        public QcC4Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "qc_c4";
    }

    /// <summary>QC-C5：怪物和器械死亡时留下火焰。</summary>
    public class QcC5Buff : CurseBuffBase
    {
        public QcC5Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "qc_c5";
    }

    /// <summary>QC-C6：怪物有 10% 概率隐形。</summary>
    public class QcC6Buff : CurseBuffBase
    {
        public QcC6Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "qc_c6";
    }

    /// <summary>QC-C7：很长的冷却时间变为 75s（≥50s 视为很长）。</summary>
    public class QcC7Buff : CurseBuffBase
    {
        public QcC7Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "qc_c7";

        /// <summary>≥50s 视为"很长"。</summary>
        public const int LONG_RECHARGE_THRESHOLD = 1500;
        /// <summary>统一改为 75s。</summary>
        public const int LONG_RECHARGE_REPLACED = 2250;
    }

    /// <summary>QC-C8：怪物的出场时间提前 5s。</summary>
    public class QcC8Buff : CurseBuffBase
    {
        public QcC8Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "qc_c8";

        internal override void Setup()
        {
            // 关卡属性修饰符：first_wave_time - 5s。
            // 修饰符没有下限截断，但原版首波时间普遍 ≥15s，实际不会出现负值。
            AddModifier(new FloatModifier(EngineStageProps.FIRST_WAVE_TIME,
                NumberOperator.Add, -CurseRuntime.FIRST_WAVE_ADVANCE_SECONDS));
        }
    }

    /// <summary>QC-C9：器械子弹的速度变为 1/3。</summary>
    public class QcC9Buff : CurseBuffBase
    {
        public QcC9Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "qc_c9";
    }

    /// <summary>QC-C10：重质量怪物可以阻挡穿透伤害。</summary>
    public class QcC10Buff : CurseBuffBase
    {
        public QcC10Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "qc_c10";
    }

    /// <summary>QC-C11：每一大波所有器械价格上涨 50。</summary>
    public class QcC11Buff : CurseBuffBase
    {
        public QcC11Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "qc_c11";
    }

    /// <summary>QC-C12：关卡开始时在第三列随机一路生成一个不给予星碎的借贷告示牌。</summary>
    public class QcC12Buff : CurseBuffBase
    {
        public QcC12Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "qc_c12";
    }
}
