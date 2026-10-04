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
                ReplayCommand.Apply();
                Log.LogInfo(NAME + " v" + VERSION + " 已加载。 " + ReplayConfig.Describe());
            }
            catch (Exception e)
            {
                Log.LogError("[Plugin] Load 出错: " + e);
            }
        }
    }
}
