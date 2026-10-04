using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace UiGallery
{
    /// <summary>
    /// MVZ2 UI 组件画廊 —— 开发者的 UI 参考实现。
    ///
    /// <para><b>这是给开发者用的，不是给玩家装的功能。</b>它把 <c>DSHCore.Ui</c> 提供的每个素材
    /// 以真实观感展示出来，同时充当「用原版零件做 UI」的最小可编译范例。</para>
    ///
    /// <para>与 <c>GraphicConsole</c> 的路线刻意相反：不做任何尺寸体系、不算坐标、不自绘。
    /// 全部视觉都来自原版 prefab 的克隆体，布局交给原版已有的 <c>LayoutGroup</c>。
    /// 如果这个面板看起来还不够原生，问题一定在 DSHCore.Ui 的 API 上，而不是这里 ——
    /// 这正是把它做成独立 mod 的目的：**让 UI 基础设施的质量有一个可量化的验收样本**。</para>
    ///
    /// <para>对 DSHCore 是**硬依赖**：UiSkin / UiHost / UiClone 都是它的 API。</para>
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    [BepInDependency(DSHCoreGuid, BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BasePlugin
    {
        public const string GUID = "qoznos.mvz2.uigallery";
        public const string NAME = "MVZ2 UI Gallery";
        public const string VERSION = "0.1.0";

        /// <summary>DSHCore 的 GUID（写常量而不是引用 DSHCore.Plugin.GUID：属性参数必须是常量）。</summary>
        private const string DSHCoreGuid = "qoznos.mvz2.core";

        internal static new ManualLogSource Log;

        public override void Load()
        {
            Log = base.Log;

            GalleryConfig.Init(Config);

            // ① /dshui（含 Tab 补全）
            UiGalleryCommand.Apply();

            // ② 心跳：自测模式与"切换场景后素材失效"的自动重建都要它。
            //    挂在官方本来就每帧跑的 MainSceneController.Update 上，不注入自己的 MonoBehaviour
            //    （那需要 RegisterTypeInIl2Cpp，本 mod 刻意零注入）。
            GalleryTick.Apply();

            // ③ 诊断哨兵：只在自测模式下挂。它盯着 ElementList.Awake 的模板，
            //    模板不可用就把节点路径打进日志 —— 否则那种空指针在日志里只是一行
            //    "ElementList.Awake ← Instantiate"，没有调用者、完全无法定位。
            if (GalleryConfig.SelfTest)
                ElementListWatchdog.Apply();

            // ④ 示例设置项（General.SampleOptions，默认关闭）。
            //    这是本 mod **唯一**会动到游戏内容的地方：注册一个 ModInfo + 两条设置定义，
            //    用来示范 / 验证 DSHCore.Ui.UiOptions 那条路。其余部分保持零注入。
            if (GalleryConfig.SampleOptions)
                SampleOptions.Enable();

            Log.LogInfo("[Plugin] " + NAME + " v" + VERSION + " 已就绪。"
                + "游戏内输入 /dshui 打开组件画廊"
                + (GalleryConfig.SelfTest ? "（自测模式已开启，进主菜单后会自动打开并 dump）" : "")
                + (GalleryConfig.SampleOptions ? "（示例设置项已启用）" : "")
                + "。");
        }
    }
}
