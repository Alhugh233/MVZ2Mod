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
        #endregion

        #region QC-C4 / SC-C4 复活只判定一次（两者都在尸体消失时判）
        public static readonly HashSet<IntPtr> QcRevived = new HashSet<IntPtr>();
        public static readonly HashSet<IntPtr> ScRevived = new HashSet<IntPtr>();
        #endregion

        #region QC-C6 / SC-C2 / SC-C7 / SC-C10 首次见到实体时处理一次
        public static readonly HashSet<IntPtr> InvisibilityRolled = new HashSet<IntPtr>();
        public static readonly HashSet<IntPtr> CharmProcessed = new HashSet<IntPtr>();
        public static readonly HashSet<IntPtr> MassApplied = new HashSet<IntPtr>();
        public static readonly HashSet<IntPtr> SpawnInvincibleApplied = new HashSet<IntPtr>();
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
            QcRevived.Clear();
            ScRevived.Clear();
            InvisibilityRolled.Clear();
            CharmProcessed.Clear();
            MassApplied.Clear();
            SpawnInvincibleApplied.Clear();
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
            return true;
        }

        /// <summary>当前跟踪的关卡指针（0 = 未初始化）。</summary>
        public static IntPtr LevelPointer => level;
    }
}
