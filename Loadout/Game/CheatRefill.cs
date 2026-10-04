using System;
using HarmonyLib;
using MVZ2Logic.Level;
using PVZEngine.Level;

namespace Loadout
{
    /// <summary>
    /// 「无限能量 / 无限星之碎片」在**原版实现之外**额外做的补满。
    ///
    /// <para>原版那两条是靠关卡 buff 每帧 <c>AddEnergy(9990)</c> / <c>AddStarshardCount(5)</c>
    /// （见 <c>DebugEnergyBuff</c> / <c>DebugStarshardBuff</c>）。问题是面板打开时游戏速度被压成 0，
    /// 关卡逻辑不再推进 → <b>buff 的每帧更新也不跑了</b>，于是「面板暂停中放卡 / 丢星之碎片」
    /// 会真的把能量扣下去、还补不回来。</para>
    ///
    /// <para>所以按需求在这些**动作点**上直接补到上限：</para>
    /// <list type="bullet">
    /// <item><b>消耗能量</b>（放置蓝图就是 <c>AddEnergy(-费用)</c>）→ 补满能量；</item>
    /// <item><b>消耗星之碎片</b>（<c>AddStarshardCount(-1)</c>）→ 补满星之碎片；</item>
    /// <item><b>点开关</b>与<b>改上限</b> → 由 <see cref="RefillNow"/> 显式补一次。</item>
    /// </list>
    ///
    /// <para>两个补丁都只在**扣减**（参数 &lt; 0）时动手，且只在对应的 buff 存在时生效 ——
    /// 平时（buff 没开）它们什么都不做，所以对原版行为没有任何影响。</para>
    ///
    /// <para><b>⚠ 为什么星之碎片那一条要「晚点挂」</b>（<see cref="Tick"/> 而不是
    /// <see cref="Apply"/>）：<c>LogicLevelProps</c> 是个带 <c>[PropertyRegistryRegion]</c> 的静态类，
    /// 它的**类型初始化必须在游戏内容注册完成之后**才能跑。在 <c>Plugin.Load()</c> 里去反射它
    /// （哪怕只是 <c>typeof</c>）会**提前**触发 <c>.cctor</c>，结果是
    /// <c>TypeInitializationException</c> —— 而这个异常会被运行时**永久缓存**，
    /// 于是连原版自己的 <c>GetStarshardCount</c>、连别的 mod 全炸。
    /// 实测日志：<c>[Refill] 挂补丁失败：… LogicLevelProps..cctor() …</c> +
    /// <c>Trying to create a buff with missing definition mvz2:debug_no_recharge</c>。
    /// 所以改成**进关卡之后**再挂（那时游戏自己早就把它初始化过了）。</para>
    /// </summary>
    internal static class CheatRefill
    {
        private static bool applied;
        private static bool starApplied;
        private static bool starFailed;
        private static bool logged;

        /// <summary>
        /// 只挂**核心引擎类型**上的那一条（<c>LevelEngine</c> 不涉及内容注册顺序，提前挂安全）。
        /// </summary>
        public static void Apply()
        {
            if (applied)
                return;
            applied = true;
            try
            {
                var harmony = new Harmony(Plugin.GUID + ".cheatrefill");
                var addEnergy = AccessTools.Method(typeof(LevelEngine), "AddEnergy", new[] { typeof(float) });
                if (addEnergy != null)
                {
                    harmony.Patch(addEnergy, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CheatRefill), nameof(EnergyPostfix))));
                    Plugin.Log?.LogInfo("[Refill] 已挂补满补丁（无限能量：扣减时补到上限）");
                }
                else
                {
                    Plugin.Log?.LogError("[Refill] 找不到 LevelEngine.AddEnergy，无限能量不会自动补满");
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Refill] 挂补丁失败：" + e);
            }
        }

        /// <summary>
        /// 进关卡之后补挂星之碎片那一条（由面板心跳每帧调，只做一次）。
        /// 见类注释里那段「提前初始化 <c>LogicLevelProps</c> 会把游戏打坏」的说明。
        /// </summary>
        public static void Tick()
        {
            if (starApplied || starFailed)
                return;
            var level = LevelFacade.Current;
            if (level == null)
                return;

            // 先确认这个类型此刻能正常初始化：这一步走通了，才说明时机对了。
            try
            {
                var probe = level.GetStarshardCount();
            }
            catch (Exception e)
            {
                starFailed = true;
                Plugin.Log?.LogError("[Refill] 读星之碎片失败，放弃补挂（无限星之碎片不会自动补满）："
                    + e.GetType().Name + ": " + e.Message);
                return;
            }

            try
            {
                var harmony = new Harmony(Plugin.GUID + ".cheatrefill");
                var addStarshard = AccessTools.Method(typeof(LogicLevelProps), "AddStarshardCount",
                    new[] { typeof(LevelEngine), typeof(int) });
                if (addStarshard != null)
                {
                    harmony.Patch(addStarshard, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CheatRefill), nameof(StarshardPostfix))));
                    starApplied = true;
                    Plugin.Log?.LogInfo("[Refill] 已补挂补丁（无限星之碎片：扣减时补到上限）");
                }
                else
                {
                    starFailed = true;
                    Plugin.Log?.LogError("[Refill] 找不到 AddStarshardCount，无限星之碎片不会自动补满");
                }
            }
            catch (Exception e)
            {
                starFailed = true;
                Plugin.Log?.LogError("[Refill] 补挂星之碎片补丁失败：" + e);
            }
        }

        /// <summary>扣能量之后补满（只在开了无限能量时）。</summary>
        private static void EnergyPostfix(LevelEngine __instance, float value)
        {
            try
            {
                if (value >= 0f || __instance == null)
                    return;
                if (!LevelFacade.HasBuff(__instance, LevelFacade.BuffEnergy))
                    return;
                Energy(__instance);
            }
            catch (Exception) { }
        }

        /// <summary>扣星之碎片之后补满（只在开了无限星之碎片时）。</summary>
        private static void StarshardPostfix(LevelEngine game, int value)
        {
            try
            {
                if (value >= 0 || game == null)
                    return;
                if (!LevelFacade.HasBuff(game, LevelFacade.BuffStarshard))
                    return;
                Starshard(game);
            }
            catch (Exception) { }
        }

        /// <summary>把能量补到上限（只在开了无限能量时）。</summary>
        public static bool Energy(LevelEngine level)
        {
            if (level == null || !LevelFacade.HasBuff(level, LevelFacade.BuffEnergy))
                return false;
            try
            {
                var max = LevelFacade.GetMaxEnergy(level);
                if (LevelFacade.GetEnergy(level) >= max)
                    return false;
                LevelFacade.SetEnergy(level, max);
                LogOnce("能量补满（上限 " + max + "）");
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>把星之碎片补到上限（只在开了无限星之碎片时）。</summary>
        public static bool Starshard(LevelEngine level)
        {
            if (level == null || !LevelFacade.HasBuff(level, LevelFacade.BuffStarshard))
                return false;
            try
            {
                var max = LevelFacade.GetStarshardSlots(level);
                if (LevelFacade.GetStarshardCount(level) >= max)
                    return false;
                LevelFacade.SetStarshardCount(level, max);
                LogOnce("星之碎片补满（上限 " + max + "）");
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>两个都补一次（点开关 / 改上限时调）。</summary>
        public static void RefillNow(LevelEngine level)
        {
            Energy(level);
            Starshard(level);
        }

        private static void LogOnce(string what)
        {
            if (logged)
                return;
            logged = true;
            Plugin.Log?.LogInfo("[Refill] 面板暂停时也能生效：已" + what + "（之后同类操作只补不记日志）");
        }
    }
}
