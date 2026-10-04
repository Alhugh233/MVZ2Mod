using System;
using System.Collections.Generic;
using PVZEngine;
using PVZEngine.Level;

namespace ModularCurse
{
    /// <summary>
    /// 全部诅咒共享的托管状态。
    ///
    /// 为什么用托管静态字段而不是 buff 属性：
    ///   本 mod 的程序集不参与官方的 PropertyMapper 扫描（ModInfo.Assemblies 为空），
    ///   自己 new 出来的 PropertyMeta 的 Key 恒为 0 —— 所有未注册键会互相碰撞
    ///   （PropertyDictionary 用 Key 做字典键），读写都不可靠。
    ///
    /// 生命周期：由 <see cref="EnsureLevel"/> 在关卡变化时整体清空。
    /// 每条诅咒 buff 在 PostAdd / PostUpdate 里调用它，天然跟随关卡切换。
    /// </summary>
    internal static class CurseState
    {
        private static IntPtr level = IntPtr.Zero;

        #region QC-C2 工具冷却（各算各的 3s）
        public static int PickaxeCooldown;
        public static int StarshardCooldown;
        /// <summary>当前两件工具的禁用是否是本 mod（QC-C2 / SC-C14）写进去的。</summary>
        public static bool ToolsDisabledByUs;
        #endregion

        #region QC-C4 / SC-C8 复活只判定一次
        /// <summary>QC-C4：已经判过复活的尸体。</summary>
        public static readonly HashSet<IntPtr> QcRevived = new HashSet<IntPtr>();
        /// <summary>SC-C8：已经用掉「首次死亡复活」的敌怪。</summary>
        public static readonly HashSet<IntPtr> ScRevived = new HashSet<IntPtr>();
        #endregion

        #region SC-C14 每 10 波封禁（命运之瓶）
        /// <summary>剩余的封禁 tick（>0 = 器械放置 / 镐子 / 星之碎片不可用）。</summary>
        public static int WaveGuardTicks;
        /// <summary>已经触发过封禁的波数（避免同一波重复触发）。</summary>
        public static int WaveGuardWave;
        #endregion

        #region SC-C15 每秒受伤上限（强固补丁）
        /// <summary>本秒内本体已受到的伤害（实体指针 → 累计值）。</summary>
        public static readonly Dictionary<IntPtr, float> BodyDamagePerSecond
            = new Dictionary<IntPtr, float>();
        /// <summary>本秒内防具已受到的伤害。</summary>
        public static readonly Dictionary<IntPtr, float> ArmorDamagePerSecond
            = new Dictionary<IntPtr, float>();
        /// <summary>距离下一次清空计数还剩多少 tick。</summary>
        public static int DamageWindowTimer;
        #endregion

        #region QC-C6 / SC-C2 / SC-C10 首次见到实体时处理一次
        public static readonly HashSet<IntPtr> InvisibilityRolled = new HashSet<IntPtr>();
        public static readonly HashSet<IntPtr> CharmProcessed = new HashSet<IntPtr>();
        /// <summary>SC-C10：已经进入过"出场 10s 不死"窗口的敌怪。</summary>
        public static readonly HashSet<IntPtr> SpawnInvincibleApplied = new HashSet<IntPtr>();
        #endregion

        #region SC-C5 防具耐久 +30%（按防具指针，只加一次）
        public static readonly HashSet<IntPtr> ScArmorScaled = new HashSet<IntPtr>();
        #endregion

        #region SC-C9 古代历石（状态累积 → 12s 状态抗性）
        /// <summary>累积量 = Σ 持有 debuff 种类数 / 秒（实体指针 → 累积秒数）。</summary>
        public static readonly Dictionary<IntPtr, float> ScStatusAccum = new Dictionary<IntPtr, float>();
        /// <summary>状态抗性剩余 tick（实体指针 → 剩余 tick）。</summary>
        public static readonly Dictionary<IntPtr, int> ScStatusImmune = new Dictionary<IntPtr, int>();
        #endregion

        #region SC-C3 眩晕叠加（实体指针 → 剩余 tick；自己记账，不走 StunBuff.MaxStunTime）
        public static readonly Dictionary<IntPtr, int> ScStun = new Dictionary<IntPtr, int>();
        #endregion

        #region SC-C10 灾厄链甲（出场 10s 内不死，血量保底 1）
        /// <summary>剩余保护 tick（实体指针 → 剩余 tick）。</summary>
        public static readonly Dictionary<IntPtr, int> ScGrace = new Dictionary<IntPtr, int>();
        #endregion

        #region SC-C13 已经触发过不屈的敌怪（不能触发两次）
        public static readonly HashSet<IntPtr> ScSurvivorTriggered = new HashSet<IntPtr>();
        #endregion

        #region SC-C2 叛变计时（实体指针 → 剩余 tick）
        public static readonly Dictionary<IntPtr, int> Charmed = new Dictionary<IntPtr, int>();
        #endregion

        #region SC-C13 不屈计时（实体指针 → 剩余 tick）
        public static readonly Dictionary<IntPtr, int> Survivors = new Dictionary<IntPtr, int>();
        #endregion

        #region SC-C1 每种器械在场的数量（每帧刷新）
        public static readonly Dictionary<NamespaceID, int> ContraptionCounts
            = new Dictionary<NamespaceID, int>();
        #endregion

        #region SC-C11 每行最靠前敌人的列号（每帧刷新）
        public static readonly Dictionary<int, int> LaneFrontColumn = new Dictionary<int, int>();
        #endregion

        #region SC-C6 星之碎片使用计数（按尝试计，含被跳过的）
        public static int StarshardUses;
        #endregion

        #region QC-C12 借贷告示牌
        public static bool LoanSignPlaced;
        public static int LoanSignDelay;
        /// <summary>为 true 时，接下来生成的借贷告示牌会被设为"无借贷次数"（诅咒送的那块）。</summary>
        public static bool SuppressLoanSignLend;
        #endregion

        #region SC-C3 回血计时
        public static int HealTimer;
        #endregion

        #region QC-C3 待施加凋零（实体指针 → 剩余等待 tick）
        /// <summary>已激发、但等大招结束再上凋零的器械。</summary>
        public static readonly Dictionary<IntPtr, int> PendingWither = new Dictionary<IntPtr, int>();
        #endregion

        #region 自检
        /// <summary>已做过"诅咒计数"自检的关卡指针（每关仅记录一次）。</summary>
        public static IntPtr CountDiagLevel = IntPtr.Zero;
        #endregion

        #region ZY 池（咒鵺兼程 / 3q4a 的诅咒）
        /// <summary>最后一个已处理过"大波开始"的标记（= CurrentFlag*1000 + 波数）。</summary>
        public static long ZyBigWaveMark;

        /// <summary>ZY-C1：离开光照已经累计了多少 tick（实体指针 → tick）。</summary>
        public static readonly Dictionary<IntPtr, int> ZySleepTicks = new Dictionary<IntPtr, int>();
        /// <summary>ZY-C1：由本诅咒弄睡的器械（只叫醒自己弄睡的那些）。</summary>
        public static readonly HashSet<IntPtr> ZySleeping = new HashSet<IntPtr>();

        /// <summary>ZY-C4（排异反应）：拾取星之碎片累积的禁用时长（可叠加、无上限）。</summary>
        public static int ZyStarshardBan;

        /// <summary>ZY-C12（久经沙场）：已经受过一次 &gt;80 伤害而存活的敌人。</summary>
        public static readonly HashSet<IntPtr> ZyVeteran = new HashSet<IntPtr>();

        /// <summary>ZY-C10（病毒式传播）：正在生成"复制"时的重入保护。</summary>
        public static bool ZyViralBusy;
        #endregion

        /// <summary>关卡变化时清空全部缓存；返回是否发生了清空。</summary>
        public static bool EnsureLevel(LevelEngine current)
        {
            if (current == null)
                return false;
            if (current.Pointer == level)
                return false;
            level = current.Pointer;
            PickaxeCooldown = 0;
            StarshardCooldown = 0;
            ToolsDisabledByUs = false;
            WaveGuardTicks = 0;
            WaveGuardWave = 0;
            BodyDamagePerSecond.Clear();
            ArmorDamagePerSecond.Clear();
            DamageWindowTimer = 0;
            QcRevived.Clear();
            ScRevived.Clear();
            InvisibilityRolled.Clear();
            CharmProcessed.Clear();
            SpawnInvincibleApplied.Clear();
            ScArmorScaled.Clear();
            ScStatusAccum.Clear();
            ScStatusImmune.Clear();
            ScStun.Clear();
            ScGrace.Clear();
            ScSurvivorTriggered.Clear();
            Charmed.Clear();
            Survivors.Clear();
            ContraptionCounts.Clear();
            LaneFrontColumn.Clear();
            StarshardUses = 0;
            LoanSignPlaced = false;
            LoanSignDelay = 0;
            SuppressLoanSignLend = false;
            HealTimer = 0;
            PendingWither.Clear();
            CountDiagLevel = IntPtr.Zero;
            ResetZy();
            return true;
        }

        /// <summary>清空 ZY 池的托管状态（ZY-C6 / ZY-C12 的永久性数据也跟着关卡走）。</summary>
        private static void ResetZy()
        {
            ZyBigWaveMark = 0;
            ZySleepTicks.Clear();
            ZySleeping.Clear();
            ZyStarshardBan = 0;
            ZyVeteran.Clear();
            ZyViralBusy = false;
        }

        /// <summary>当前跟踪的关卡指针（0 = 未初始化）。</summary>
        public static IntPtr LevelPointer => level;
    }
}
