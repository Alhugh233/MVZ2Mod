using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace Replay
{
    /// <summary>
    /// MVZ2 Replay —— 关卡回放录制（依赖 DSHCore 前置库）。
    ///
    /// 本 mod 不注册任何游戏定义（没有 <c>Mod</c> 实例）：它只挂 Harmony 补丁，
    /// 把玩家的操作汇聚点记录下来，写成 <c>.mrep</c>。
    /// 设计依据见同目录 DESIGN.md。
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency(DSHCore.Plugin.GUID)]
    public class Plugin : BasePlugin
    {
        public const string GUID = "qoznos.mvz2.replay";
        public const string NAME = "MVZ2 Replay";
        public const string VERSION = "0.1.0";

        internal static new ManualLogSource Log;

        public override void Load()
        {
            Log = base.Log;
            Log.LogInfo("[Plugin] Load begin");

            try
            {
                ReplayConfig.Init(Config);
                ReplayHooks.Apply();

                // 把「回放」子页面登记进 DSHCore 的插件页设施（纯托管的登记，不碰游戏）。
                // 心跳由 DSHCore 驱动（它挂在 MainSceneController.Update 上），这里只是初始化一次。
                ReplayPage.EnsureRegistered();

                // 在「插件 → 模组设置（DSHCore）」的左栏里也露个面。
                // 本 mod 既没有游戏内容（没注册 ModInfo）、也没有设置项，
                // 不主动介绍自己的话玩家在列表里根本看不到它。
                DSHCore.Ui.UiOptions.DeclareMod("replay", NAME,
                    "关卡回放录制 / 播放（.mrep）", "QoZnoS", "回放");

                // 控制台指令默认**不注册**（[General] Commands）：它是排障入口，
                // 普通玩家走「主菜单 → 更多选项 → 插件 → 回放」就够了。
                if (ReplayConfig.GetCommands())
                    ReplayCommand.Apply();
                else
                    Log.LogInfo("[Replay] /replay 控制台指令未注册（[General] Commands = false）");

                Log.LogInfo(NAME + " v" + VERSION + " 已加载。 " + ReplayConfig.Describe());
            }
            catch (Exception e)
            {
                Log.LogError("[Plugin] Load 出错: " + e);
            }
        }
    }
}
