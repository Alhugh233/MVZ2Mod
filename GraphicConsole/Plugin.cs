using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace GraphicConsole
{
    /// <summary>
    /// MVZ2 图形化控制台 —— 用可视化面板替代原版调试控制台的输入窗口。
    ///
    /// 设计取向（详见 README.md）：**测试工具，最大兼容性优先**。
    ///   - 不注入任何 il2cpp 类型 / 不注册任何 Definition / 不注册自己的 ModInfo
    ///     → 完全避开 metadata v31 下注入类的那一大堆坑；
    ///   - 全部功能落在「读写原版已有属性」+「调用原版已有接口」+「克隆原版已有 UI」上；
    ///   - 只有三个 Harmony 补丁，且都是**只读 / 只追加**性质，并且都能被
    ///     「兼容性等级」在运行时关掉（见 <see cref="CompatConfig"/>）；
    ///   - 关卡内面板只写 level 级属性，绝不改全局 options。
    ///
    /// 对 DSHCore 是**软依赖**：本 mod 不使用 DSHCore 的任何 API（不需要它的注入机制），
    /// 声明软依赖只是为了在装了 DSHCore 时保证加载顺序、并共用它的日志查看器排查问题。
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency(DSHCoreGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BasePlugin
    {
        public const string GUID = "qoznos.mvz2.graphicconsole";
        public const string NAME = "MVZ2 Graphic Console";
        public const string VERSION = "0.0.1";

        /// <summary>DSHCore 的 GUID（写常量而不是引用 DSHCore.Plugin.GUID：属性参数必须是常量，两者等价且更松耦合）。</summary>
        private const string DSHCoreGuid = "qoznos.mvz2.core";

        internal static new ManualLogSource Log;

        public override void Load()
        {
            Log = base.Log;
            Log.LogInfo("[Plugin] " + NAME + " v" + VERSION + " 加载中…");

            // 配置先初始化：兼容性等级决定后面几个补丁在运行时是否真的生效。
            CompatConfig.Init(Config);

            // ① 面板暂停：面板打开时把游戏速度压到 0（冻结关卡逻辑但保留游戏内操作）
            GameSpeedPatch.Apply();
            // ② 选卡界面显示所有实体蓝图（只追加官方排序结果之后）
            AllBlueprintsPatch.Apply();
            // ③ 面板心跳（纯追加，始终生效）
            PanelTickPatch.Apply();
            // ④ 左上角蛛图标 -> 面板入口（Standard 档）
            GraphicConsoleIcon.Apply();
            // ⑤ 诊断用 /gc（含 Tab 补全）
            PanelCommand.Apply();

            Log.LogInfo("[Plugin] " + NAME + " v" + VERSION + " 已就绪。"
                + "入口：左上角蛛图标 或 /gc open"
                + (string.IsNullOrEmpty(CompatConfig.PanelHotkeyText) ? "（未注册热键）" : "（热键 " + CompatConfig.PanelHotkeyText + "）")
                + "，原版控制台仍是 '/'，诊断指令 /gc。");
        }
    }
}
