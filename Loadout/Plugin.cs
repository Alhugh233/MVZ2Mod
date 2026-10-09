using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace Loadout
{
    /// <summary>
    /// MVZ2 Loadout —— 用可视化面板替代原版调试控制台的输入窗口。
    ///
    /// <para>设计取向（详见 README.md）：**测试工具，最大兼容性优先**。</para>
    /// <list type="bullet">
    /// <item>不注册任何 Definition、不注册自己的 ModInfo
    /// → 完全避开 metadata v31 下注入类的那一堆坑；
    /// （「兼容性等级」那一行设置项也走零注入路径 —— 它声明进 DSHCore 的「模组设置」页，
    /// 见 <see cref="CompatOptions"/>，不再需要 Mod 实例）；</item>
    /// <item>全部功能落在「读写原版已有属性」「调用原版已有接口」「克隆原版已有 UI」上；</item>
    /// <item>UI 建立在 <c>DSHCore.Ui</c>（原版控件克隆基础设施）之上：不算坐标、不定尺寸、不自绘控件；</item>
    /// <item>Harmony 补丁都是**只读 / 只追加**性质，且可以被「兼容性等级」在运行时关掉；</item>
    /// <item>关卡内面板只写 level 级属性，绝不改全局 options。</item>
    /// </list>
    ///
    /// <para><b>DSHCore 是硬依赖</b>：整个 UI 层都用它的 <c>UiSkin</c> / <c>UiOptionRow</c> /
    /// <c>UiList</c> / <c>UiTooltip</c> / <c>UiClone</c> 这些基础设施。</para>
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency(DSHCoreGuid, BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BasePlugin
    {
        public const string GUID = "qoznos.mvz2.loadout";
        public const string NAME = "MVZ2 Loadout";
        public const string VERSION = "1.0.0";

        /// <summary>DSHCore 的 GUID（写常量而不引用 <c>DSHCore.Plugin.GUID</c>：属性参数必须是常量）。</summary>
        private const string DSHCoreGuid = "qoznos.mvz2.core";

        internal static new ManualLogSource Log;

        public override void Load()
        {
            Log = base.Log;
            Log.LogInfo("[Plugin] " + NAME + " v" + VERSION + " 加载中…");

            // 配置先初始化：兼容性等级决定后面几个补丁在运行时是否真的生效。
            CompatConfig.Init(Config);

            // 全部用户可见文案（页签 / 按钮 / 字段标签 / 说明 / 自述）——
            // 必须早于任何界面取词。面板自己画的文字都从几个统一入口过取词，
            // 所以这里登记完就生效。
            LoadoutLocale.Register();

            // ① 面板暂停：面板打开时把游戏速度压到 0（冻结关卡逻辑但保留游戏内操作）
            GameSpeedPatch.Apply();
            // ② 选卡界面显示所有实体蓝图（只追加到官方排序结果之后）
            AllBlueprintsPatch.Apply();
            // ③ 面板心跳（纯追加，始终生效）
            PanelTickPatch.Apply();
            // ④ 左上角图标 → 面板入口（Standard 档）
            DebugIconEntry.Apply();
            // ⑤ 逆阵营放置（生成漏斗 postfix，**Full 档**才生效）
            ReverseFactionPatch.Apply();
            // ⑥ 蓝图血量 / 白天不睡觉（同一生成漏斗，只对标记过的卡生效，Standard 档）
            BlueprintSpawnPatch.Apply();
            // ⑦ 无条件放置（短路官方放得下判定，**Full 档**才生效）
            BlueprintPlacementPatch.Apply();
            // ⑧ 放置作用域：标记只对「从这张卡放出来」的实体生效（血量/白天不睡觉/逆阵营的前提）
            PlacementScope.Apply();
            // ⑨ 补满能量 / 星之碎片（面板暂停时原版 buff 的每帧更新不跑）
            CheatRefill.Apply();
            // ⑩ 面板开着时不触发原版失焦暂停
            FocusPausePatch.Apply();
            // ⑪ 诊断用 /loadout（含 Tab 补全）
            LoadoutCommand.Apply();
            // ⑫ 自测（配置里开启时才干活）
            SelfTest.Init();
            // ⑬ 「兼容性等级」做成原版设置项（要注入一个 Mod 实例，所以放在最后）
            CompatOptions.Enable();

            Log.LogInfo("[Plugin] " + NAME + " v" + VERSION + " 已就绪。"
                + "入口：左上角图标 或 /loadout open"
                + (string.IsNullOrEmpty(CompatConfig.PanelHotkeyText)
                    ? "（未注册热键）"
                    : "（热键 " + CompatConfig.PanelHotkeyText + "）")
                + "；原版控制台仍是 '/'，诊断指令 /loadout。");
        }

        // ------------------------------------------------------------------
        // 日志工具
        // ------------------------------------------------------------------

        private static readonly HashSet<string> LoggedOnce = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object LogLock = new object();

        /// <summary>同一个 key 只打一次日志（排障用，避免每帧刷屏）。</summary>
        internal static void LogOnce(string key, string message)
        {
            if (!string.IsNullOrEmpty(key))
            {
                lock (LogLock)
                {
                    if (LoggedOnce.Contains(key))
                        return;
                    LoggedOnce.Add(key);
                }
            }
            try
            {
                if (Log != null)
                    Log.LogWarning(message);
            }
            catch (Exception) { }
        }
    }
}
