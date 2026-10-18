using System;
using BepInEx.Configuration;
using HarmonyLib;
using MVZ2.Managers;
using MVZ2.Scenes;
using MVZ2Logic.Audios;
using MVZ2Logic.Scenes;

namespace DSHCore
{
    /// <summary>
    /// <b>快速启动</b> —— 跳过开场动画，配置在 <c>BepInEx/config/qoznos.mvz2.core.cfg</c>
    /// 的 <c>[QuickStart]</c> 段（<c>SkipIntro</c>，**默认关闭**，只给 mod 作者迭代用）。
    ///
    /// <para><b>开场动画是什么</b>（0.7.0 rc-1，实测）：</para>
    /// <list type="number">
    ///   <item><c>HealthWarning</c> 健康警告页 —— 进场动画 1 s（t=1 发 <c>SetDisplayed</c>）
    ///     + <c>maxDisplaySeconds = 10</c> + 退场动画 1 s（t=1 发 <c>EnterSplash</c>），
    ///     合计 <b>约 11.5 s</b>。点屏幕任意处能提前结束，但最小也要 2 s。</item>
    ///   <item><c>Splash</c> 报幕页 —— 2.5 s 的「Cuerzor制作」卡片，t=2.5 发 <c>EnterTitleScreen</c>。</item>
    /// </list>
    ///
    /// <para><b>为什么不直接用官方的 <c>MainManager.IsFastMode()</c></b>：那个开关在发行版里被
    /// <c>#if UNITY_EDITOR</c> 写死成 <c>false</c>，本来是最合适的入口。但实测（rc-1，本机）
    /// 把它的返回值改成 <c>true</c> 之后，游戏会走 <c>GameEntrance.StartGame</c> 的
    /// <c>await GetInitTask(); DisplayMainmenu();</c> 分支并**当场卡死** —— 窗口
    /// <c>Responding = false</c>、不再有任何 <c>MainSceneController.Update</c> 帧、
    /// 也到不了主菜单（一次运行里还弹出了「初始化失败」错误框）。
    /// 也就是说游戏自己的 fast 路径在当前版本不可用，所以这里不动那个开关。</para>
    ///
    /// <para><b>这里的做法</b>：在 <c>MainSceneController.DisplayPage</c> 前置里把
    /// <c>HealthWarning</c> / <c>Splash</c> 两个页面的目标**改写成 <c>Titlescreen</c>**，
    /// 让原方法照常执行。补丁只改参数、不跳过原方法，所以除了「少这两个页面」以外
    /// 官方流程一点没变。</para>
    ///
    /// <para><b>收益的真相</b>：开场动画与资源加载**本来就是并行的**，所以省下的不是 14 s，
    /// 而是「开场动画时长 − 剩余加载时长」。本机实测资源加载 7.8 s，因此净省 <b>约 6.3 s</b>
    /// （38.0 s → 31.7 s），而且不用再点一下屏幕。机器越好、加载越快，省得越多。</para>
    /// </summary>
    internal static class QuickStart
    {
        /// <summary>当前是否跳过开场动画（启动时从配置读一次，改完要重启游戏）。</summary>
        public static bool SkipIntro { get; private set; }

        private static bool loggedRedirect;
        private static bool musicPlayed;
        private static bool musicFailed;

        public static void Apply(ConfigFile config)
        {
            SkipIntro = BindConfig(config);
            if (!SkipIntro)
            {
                // 关着的时候连补丁都不挂：行为与官方一个字节都不差。
                return;
            }

            var harmony = new Harmony("qoznos.mvz2.core.quickstart");
            var target = AccessTools.Method(typeof(MainSceneController), "DisplayPage");
            if (target == null)
            {
                Plugin.Log?.LogWarning(
                    "[QuickStart] 找不到 MainSceneController.DisplayPage，跳过开场动画不可用（不影响其它功能）。");
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(typeof(QuickStart), nameof(DisplayPagePrefix)));
            Plugin.Log?.LogWarning(
                "[QuickStart] 已开启跳过开场动画：健康警告与「Cuerzor制作」报幕会直接换成标题页。" +
                "（仅供 mod 作者迭代；正常游玩请把 qoznos.mvz2.core.cfg 里的 [QuickStart] SkipIntro 关掉。）");
        }

        /// <summary>
        /// 只改参数、不跳过原方法：把两个开场页面的目标替换成标题页，然后让
        /// <c>DisplayPage</c> 照常执行（顺带把页面切换、<c>currentPage</c> 等状态全部交给官方）。
        /// </summary>
        [HarmonyPrefix]
        private static void DisplayPagePrefix(ref MainScenePageType __0)
        {
            if (__0 != MainScenePageType.HealthWarning && __0 != MainScenePageType.Splash)
                return;

            if (!loggedRedirect)
            {
                loggedRedirect = true;
                Plugin.Log?.LogInfo(
                    "[QuickStart] 跳过开场页（健康警告 + 报幕）-> 直接进标题页。" +
                    "注意开场动画与资源加载本来是并行的，所以实际省下的是两者之差。");
            }

            // 原版会在「警告页退场 -> Splash」那一步播主菜单音乐
            // （HealthWarningController.EnterSplash），这两个页面都不要了就得自己补上，
            // 否则标题页是静音的。
            PlayMenuMusic();

            __0 = MainScenePageType.Titlescreen;
        }

        private static void PlayMenuMusic()
        {
            if (musicPlayed || musicFailed)
                return;
            musicPlayed = true;
            try
            {
                var main = MainManager.Instance;
                if (main == null || main.MusicManager == null)
                {
                    musicPlayed = false;
                    return;
                }
                main.MusicManager.Play(LogicMusicID.mainmenu);
            }
            catch (Exception e)
            {
                // 静音是可接受的降级，但不要再反复重试。
                musicFailed = true;
                musicPlayed = false;
                Plugin.Log?.LogWarning("[QuickStart] 补播主菜单音乐失败（标题页会静音）：" + e.Message);
            }
        }

        private static bool BindConfig(ConfigFile config)
        {
            if (config == null)
                return false;

            const string Section = "QuickStart";

            return config.Bind(
                Section, "SkipIntro", false,
                "【仅供 mod 作者快速迭代，默认关闭】跳过开场动画：健康警告页（约 11.5 秒）与" +
                "「Cuerzor 制作」报幕页（约 2.5 秒）都会直接换成标题页，启动后立刻可点，不用再点一下屏幕。\n" +
                "做法是把 MainSceneController.DisplayPage 的目标参数从这两个页面改写成标题页，" +
                "其余官方流程（含标题页上的资源加载进度条）一点没变。\n" +
                "收益说明：开场动画和资源加载**本来就是并行的**，所以真正省下的只有两者之差" +
                "（本机实测约 6.3 秒，38.0 s → 31.7 s），机器越好、加载越快省得越多。\n" +
                "注意：这不是官方 MainManager.IsFastMode()（那个开关实测在 rc-1 会让游戏卡死，本库没有启用它）。\n" +
                "改完需要重启游戏。").Value;
        }
    }
}
