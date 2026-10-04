using System;
using MVZ2.Vanilla.Enemies;
using MVZ2Logic.Entities;
using PVZEngine.Modifiers;
using UnityEngine;

namespace ModularCurse
{
    // =====================================================================
    // ZY 池（咒鵺兼程 / 3q4a 的诅咒，16 条）
    //
    // 同 QcBuffs / ScBuffs：本文件只放"定义级"内容，行为在 ZyRuntime。
    //
    // 2026-09 按需求**重排 + 改设计**过，编号与语义的对应关系见 ZyRuntime 的类注释；
    // 名字颜色由 curses.xml 里的 <color=...> 标签决定（红/绿/蓝/白）。
    // =====================================================================

    /// <summary>zy_c1 趋光性（红）：非夜行性器械离开光照 8s 后睡着；无法被美梦丝入眠的器械不受影响。</summary>
    public class ZyC1Buff : CurseBuffBase
    {
        public ZyC1Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c1";
    }

    /// <summary>zy_c2 夜长梦多（红）：每小波开始时蓝图冷却 +2.5%（上限 50%）。</summary>
    public class ZyC2Buff : CurseBuffBase
    {
        public ZyC2Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c2";
    }

    /// <summary>zy_c3 地狱残响（红）：矿场的生产间隔改为 30s。</summary>
    public class ZyC3Buff : CurseBuffBase
    {
        public ZyC3Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c3";
    }

    /// <summary>zy_c4 排异反应（红）：拾取星之碎片后阻止使用星之碎片 12s，可叠加、无上限。</summary>
    public class ZyC4Buff : CurseBuffBase
    {
        public ZyC4Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c4";
    }

    /// <summary>zy_c5 斋禁（绿）：每大波开始时失去所有机械能，并失去 1 个星之碎片。</summary>
    public class ZyC5Buff : CurseBuffBase
    {
        public ZyC5Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c5";
    }

    /// <summary>zy_c6 磨灭（绿）：每大波开始时从右往左永久禁用 1 张器械蓝图。</summary>
    public class ZyC6Buff : CurseBuffBase
    {
        public ZyC6Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c6";
    }

    /// <summary>zy_c7 室上性心动过速（绿）：第一大波开始前波长最短为 16s。</summary>
    public class ZyC7Buff : CurseBuffBase
    {
        public ZyC7Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c7";
    }

    /// <summary>zy_c8 我们不去幻想乡（绿）：倒数第二大波开始后出怪量 +200%（1 格关不生效）。</summary>
    public class ZyC8Buff : CurseBuffBase
    {
        public ZyC8Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c8";
    }

    /// <summary>zy_c9 它回来了（蓝）：被消除亡语 / 坠空 / 落水而死的敌人重生为携带「成真」的恶魂（地雷击杀除外）。</summary>
    public class ZyC9Buff : CurseBuffBase
    {
        public ZyC9Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c9";
    }

    /// <summary>zy_c10 病毒式传播（蓝）：第 x 大波起，出怪点数低于 x 的敌人额外在其他行生成 1 个不占点数的复制。</summary>
    public class ZyC10Buff : CurseBuffBase
    {
        public ZyC10Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c10";
    }

    /// <summary>zy_c11 衰变（蓝）：所有友方爆炸不分敌我。</summary>
    public class ZyC11Buff : CurseBuffBase
    {
        public ZyC11Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c11";
    }

    /// <summary>zy_c12 久经沙场（蓝）：敌人单次受到超过 80 的伤害后若未死亡，其之后单次受到的伤害不超过 20。</summary>
    public class ZyC12Buff : CurseBuffBase
    {
        public ZyC12Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c12";
    }

    /// <summary>zy_c13 纯粹（白）：不能携带任何制品（飞碟玩具 / 智能手机 / 槐树树枝 / 贪婪吸尘器除外）。</summary>
    public class ZyC13Buff : CurseBuffBase
    {
        public ZyC13Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c13";
    }

    /// <summary>zy_c14 恶意（白）：第一大波开始前，器械的攻击间隔与生产间隔取最大值。</summary>
    public class ZyC14Buff : CurseBuffBase
    {
        public ZyC14Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c14";
    }

    /// <summary>zy_c15 晚安（白）：除荧石以外的发光器械光照范围 ×50%。</summary>
    public class ZyC15Buff : CurseBuffBase
    {
        public ZyC15Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c15";
    }

    /// <summary>zy_c15 挂到发光器械身上的光照减益子 buff（不占诅咒位）。</summary>
    public class ZyC15DimBuff : CurseBuffBase
    {
        public ZyC15DimBuff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c15_dim";
        public override bool CountsAsCurse => false;

        internal override void Setup()
        {
            // ×50% 光照范围（与"破灯笼"同一种写法：常量 Vector3 乘数）。
            AddModifier(new Vector3Modifier(LogicEntityProps.LIGHT_RANGE,
                NumberOperator.Multiply, new Vector3(0.5f, 0.5f, 0.5f)));
        }
    }

    /// <summary>zy_c16 抗拒（白）：使用鼠标点击器械时，其失去 10 点耐久。</summary>
    public class ZyC16Buff : CurseBuffBase
    {
        public ZyC16Buff(IntPtr ptr) : base(ptr) { }
        public override string CurseId => "zy_c16";
    }
}
