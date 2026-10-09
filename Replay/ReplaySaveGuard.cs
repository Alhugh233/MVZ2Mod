using System;
using MVZ2Logic.Games;
using MVZ2Logic.Saves;

namespace Replay
{
    /// <summary>
    /// 回放期间的「存档只读」护栏：**重演一局不许改动玩家的存档**。
    ///
    /// ── 为什么需要它 ─────────────────────────────────────────────────
    /// <para>MVZ2 的「金币」不是关卡内货币，而是**全局存档里的钱**：
    /// <c>MoneyComponent.GetMoney()</c> 直接读 <c>Global.Saves</c>、<c>SetMoney()</c> 直接写，
    /// 退出关卡时 <c>LevelController.ExitScene()</c> 还会 <c>Saves.SaveToFile()</c> 落盘。
    /// 于是「放一个回放」会真的把钱花了 / 挣了。统计数据（击杀数、放置数、通关次数…）
    /// 同理：它们由 <c>StatsGlobalCallbacks</c> 挂在 <c>POST_ENEMY_SPAWNED</c> /
    /// <c>POST_ENTITY_DEATH</c> / <c>POST_LEVEL_CLEAR</c> 等回调上，回放重演时会一条条累加。</para>
    ///
    /// <para>通关那一条我们已经拦了（<c>ReplayPatches.EngineClearPrefix</c>），但那只是
    /// <c>LevelController.OnEngineClearCallback</c> 这一个入口；引擎层的
    /// <c>Triggers.RunCallback(POST_LEVEL_CLEAR, …)</c> 与整局里的每一次击杀**照样在跑**。</para>
    ///
    /// ── 两种处理方式 ─────────────────────────────────────────────────
    /// <list type="bullet">
    /// <item><b>金币：影子值。</b>不能只拦写入 —— 关卡要**读**钱来决定「买不买得起」，
    /// 读到的必须还能随消费/拾取变化，只是这些变化不许落到存档上。
    /// 所以读改都走 mod 自己的一份 <see cref="shadowMoney"/>：
    /// 第一次读时用真值播种，之后一直在影子上加减，结束时整个丢掉。
    /// 玩家看到的金币照样会涨会跌（观感完全正常），真存档一个字节都不动。</item>
    /// <item><b>其它存档写入：直接拦掉。</b>统计数据 / 解锁 / 难度记录 / 关卡进度 /
    /// 上一次选卡 / 上次地图 / 地图对话 —— 这些在回放里没有任何「读回去」的需求，
    /// 拦掉最干净（写进去也只会污染存档）。</item>
    /// </list>
    ///
    /// ── 为什么挂这么多层 ─────────────────────────────────────────────
    /// <para>同一个金币有两层入口：<c>LogicSaveExt.*</c>（<c>Global.Saves.*</c> 的公开面，
    /// 游戏绝大多数地方走这条）和 <c>LogicSaveData.*</c>（直接摸到数据对象那条）。
    /// 两者都是**很短的方法**，IL2CPP 有可能把里层内联进外层 —— 只挂一层的话，
    /// 真被内联了那一层就永远不生效（而且不报任何错）。所以两层都挂，
    /// 每条都记「拦了几次」，小结里报出来：全是 0 就说明这个补丁没生效。</para>
    ///
    /// <para>多个前缀同时命中是**安全**的：金币每次都是「整体赋值给同一个影子」，
    /// 外层拦下后里层根本不会跑，不存在重复累加。</para>
    /// </summary>
    internal static class ReplaySaveGuard
    {
        /// <summary>金币的合法区间（与 <c>LogicSaveData.SetMoney</c> 里的 Clamp 一致）。</summary>
        private const int MoneyMin = 0;
        private const int MoneyMax = 999990;

        private static bool active;
        private static bool shadowValid;
        private static int shadowMoney;

        // 命中计数（写进播放小结，用来判断补丁到底有没有生效）
        private static int moneyReads;
        private static int moneyWrites;
        private static int blockedWrites;

        public static bool Active => active;
        public static int MoneyReads => moneyReads;
        public static int MoneyWrites => moneyWrites;
        public static int BlockedWrites => blockedWrites;

        /// <summary>影子里当前的钱（护栏没开时是真值）。自测 / 报告用。</summary>
        public static int ShadowMoney => shadowMoney;

        #region 开关
        /// <summary>开始一次播放：打开护栏。之后所有存档写入都由本类接管。</summary>
        public static void Begin()
        {
            // 先关掉自己再重置，避免万一有代码在这两行之间调存档 API 时读到半初始化状态。
            active = false;
            shadowValid = false;
            shadowMoney = 0;
            moneyReads = 0;
            moneyWrites = 0;
            blockedWrites = 0;
            active = true;
        }

        /// <summary>结束播放：交还存档控制权（影子值直接丢掉，真存档从头到尾没被动过）。</summary>
        public static void End()
        {
            if (!active)
                return;
            active = false;
            shadowValid = false;
            shadowMoney = 0;
            Plugin.Log?.LogInfo("[Replay] 存档护栏已关闭（影子金币改动 " + moneyWrites
                + " 次，拦下的其它存档写入 " + blockedWrites + " 次）");
        }

        public static string Describe()
        {
            if (!active)
                return "关（不在播放中）";
            return "开（金币读 " + moneyReads + " 次 / 写 " + moneyWrites
                + " 次走影子，拦下其它存档写入 " + blockedWrites + " 次）";
        }

        /// <summary>
        /// 自测用：临时把护栏关掉执行一段取值的代码。
        ///
        /// <para>「读**真存档**里的金币」只能这么做：护栏开着的时候所有公开 API 返回的都是影子，
        /// 而影子本来就该和真值不同（这正是要验证的东西）。</para>
        /// </summary>
        public static T WithoutGuard<T>(Func<T> f)
        {
            if (f == null)
                return default(T);
            var was = active;
            active = false;
            try { return f(); }
            finally { active = was; }
        }
        #endregion

        #region 影子金币：读
        /// <summary>
        /// 影子当前值。**没播种过就用真值播种** —— 这样「玩家进来这一局有多少钱」
        /// 仍然是真实的，只是之后的变化不再落到存档里。
        /// </summary>
        private static int Current(IGlobalSaveData save)
        {
            if (!shadowValid)
            {
                shadowMoney = RealOf(save);
                shadowValid = true;
            }
            return shadowMoney;
        }

        private static int Current(LogicSaveData data)
        {
            if (!shadowValid)
            {
                shadowMoney = RealOf(data);
                shadowValid = true;
            }
            return shadowMoney;
        }

        /// <summary>
        /// 读**真值**：临时把护栏关掉再走游戏自己的 API。
        ///
        /// <para>为什么不用「读字段」：interop 不一定把 <c>LogicSaveData.money</c> 这种
        /// 私有字段暴露成可访问的属性，读不到时退化成 0 会让回放里的钱凭空归零（更难查）。
        /// 把护栏关掉再调公开 API，读的一定是原值，而且不依赖任何 interop 细节。</para>
        /// </summary>
        private static int RealOf(IGlobalSaveData save)
        {
            var was = active;
            active = false;
            try { return LogicSaveExt.GetMoney(save); }
            catch (Exception) { return 0; }
            finally { active = was; }
        }

        private static int RealOf(LogicSaveData data)
        {
            var was = active;
            active = false;
            try { return data.GetMoney(); }
            catch (Exception) { return 0; }
            finally { active = was; }
        }

        private static int Clamp(int value)
        {
            if (value < MoneyMin)
                return MoneyMin;
            if (value > MoneyMax)
                return MoneyMax;
            return value;
        }
        #endregion

        #region 补丁体：金币（LogicSaveExt = Global.Saves.* 的公开面）
        public static bool ExtGetMoneyPrefix(IGlobalSaveData save, ref int __result)
        {
            if (!active)
                return true;
            moneyReads++;
            __result = Current(save);
            return false;
        }

        // ⚠️ 参数名必须和**原方法**一致（`save` / `money`）：Harmony 是按**名字**把前缀形参
        // 绑到原方法参数上的，名字对不上就直接报「补丁失败」而且不会抛给调用方。
        // 实测：写成 `int value` 时 SetMoney / AddMoney 两个补丁根本没挂上（GetMoney 因为
        // 第一个参数叫 save 所以过了）—— 而「少挂一个」从游戏表现上完全看不出来。
        public static bool ExtSetMoneyPrefix(IGlobalSaveData save, int money)
        {
            if (!active)
                return true;
            Current(save);                    // 先播种一次基准
            shadowMoney = Clamp(money);
            shadowValid = true;
            moneyWrites++;
            return false;
        }

        public static bool ExtAddMoneyPrefix(IGlobalSaveData save, int money)
        {
            if (!active)
                return true;
            shadowMoney = Clamp(Current(save) + money);
            shadowValid = true;
            moneyWrites++;
            return false;
        }
        #endregion

        #region 补丁体：金币（LogicSaveData = 直接摸到数据对象那条）
        public static bool DataGetMoneyPrefix(LogicSaveData __instance, ref int __result)
        {
            if (!active)
                return true;
            moneyReads++;
            __result = Current(__instance);
            return false;
        }

        public static bool DataSetMoneyPrefix(LogicSaveData __instance, int value)
        {
            if (!active)
                return true;
            Current(__instance);
            shadowMoney = Clamp(value);
            shadowValid = true;
            moneyWrites++;
            return false;
        }
        #endregion

        #region 补丁体：其它存档写入（统一拦掉）
        /// <summary>
        /// 统计数据 / 解锁 / 难度记录 / 关卡进度 / 上次选卡 / 上次地图 / 地图对话 —— 一律不写。
        ///
        /// <para>不需要任何参数，所以只声明一个无参前缀即可（Harmony 允许前缀只取名下需要的参数）。</para>
        /// </summary>
        public static bool BlockPrefix()
        {
            if (!active)
                return true;
            blockedWrites++;
            return false;
        }
        #endregion
    }
}
