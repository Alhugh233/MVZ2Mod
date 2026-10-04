using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using DSHCore.Diagnostics;

namespace DSHCore
{
    /// <summary>
    /// DSHCore—— MVZ2 模组开发工具 + 前置库。
    ///
    /// 定位：为「AI 辅助开发」准备的模组基建。它本身不向游戏注册任何 ModInfo
    /// 或游戏内容，只把 IL2CPP 注入环境里那些**必须踩过一遍才知道**的坑
    /// 一次性修好，并把可复用的注册样板收敛成公共 API，供后续 mod 直接调用。
    ///
    /// 加载顺序：本插件必须先于任何依赖它的内容 mod（后者通过
    /// &lt;BepInDependency&gt; 声明）。所有修复都必须在注入类
    /// RegisterTypeInIl2Cpp 之前生效 —— 这是把修复放在前置插件里的根本原因。
    ///
    /// 公共 API 一览（详细用法见 AI_GUIDE.md）：
    ///   - Il2CppPin.Pin              注入对象字段的 GC 根驻留
    ///   - InjectedMod.Create         创建注入类 Mod 实例（补做原生 ctor 初始化）
    ///   - ModBootstrap.Register      一步完成登记 + 注入类注册
    ///   - ModInfos.Create / OnRegister   ModInfo 构造与官方注册点订阅
    ///   - CoreRegistry               跨插件注册表（各补丁按命名空间生效）
    ///   - VTableRebind.Ensure        注入类 vtable 槽重绑
    ///   - LogViewer                  本地网页日志查看器（游戏内 /dshlog 开启）
    ///   - DSHCore.Ui.*               原版 UI 基建：UiGame / UiFinder / UiSkin / UiHost /
    ///                                UiClone / UiTooltip（论证见 UI_RESEARCH.md，
    ///                                可运行范例见 UiGallery）
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BasePlugin
    {
        /// <summary>
        /// 插件标识。：依赖本库的 mod 用
        /// &lt;BepInDependency&gt; 声明它，改这个字符串会让既有声明全部失效。
        /// （目录名 / 程序集名 / 命名空间 / 显示名都换了，只有它是稳定契约。）
        /// </summary>
        public const string GUID = "qoznos.mvz2.core";

        public const string NAME = "DSHCore";

        public const string VERSION = "0.3.0";

        /// <summary>本库自己的日志通道。各 mod 建议保留自己的 Log，便于按来源过滤。</summary>
        public static new ManualLogSource Log;

        public override void Load()
        {
            Log = base.Log;

            // 0) 日志**捕获**要尽可能早启动：它挂的是 BepInEx 的全局日志监听表，
            //    越早装上，越能捕获后续插件（包括本库自己那几个补丁的安装过程）的
            //    启动日志。注意这里只捕获、不监听端口也不开浏览器 —— 网页服务由
            //    游戏内指令 /dshlog 按需开启，所以启动阶段完全无感。
            //    失败也绝不能影响游戏，所以整块包在 try 里。
            StartLogViewerCapture();

            // 0.5) 把 /dshlog 接进游戏的调试控制台。
            DebugCommands.Apply();

            // 1) 修 ClassInjector 无法处理「基类泛型虚方法」与「System.String 被重写成
            //    Il2CppSystem.String」这两处限制。必须在任何注入类 RegisterTypeInIl2Cpp
            //    之前应用（依赖本库的 mod 都在其后加载）。
            ClassInjectorFix.Apply();

            // 2) 注册磁盘 XML metas 注入补丁（幂等）。在 ResourceManager.LoadMetaLists
            //    里直接用游戏的 MetaXMLParser 解析磁盘 XML，绕开 Addressables provider 链路。
            MetaInjection.Apply();

            // 3) 存档读写兜底：SaveManager 对注入 Mod.Logic 做虚调用前确保 vtable 抽象槽已绑定。
            SaveManagerPatch.Apply();

            // 4) 游戏 ModManager.LoadMods 之前，对所有已注册注入类型统一修复 vtable。
            LoadModsPatch.Apply();

            // 5) arcade 条目 Index 平移：LoadMods 之后修正外部 mod 与 vanilla 的 Index 撞车。
            ArcadeIndexFix.Apply();

            Log.LogInfo(NAME + " v" + VERSION + " loaded (infrastructure ready)");
        }

        /// <summary>
        /// 插件卸载时收好摊子（关掉 HTTP 监听、摘掉日志监听器）。
        /// 正常玩游戏的退出路径不一定走到这里，所以 <see cref="LogPipeline"/> 里
        /// 另外挂了 <c>AppDomain.ProcessExit</c> 兜底。
        /// </summary>
        public override bool Unload()
        {
            try { LogPipeline.Stop(); }
            catch { }
            return true;
        }

        private void StartLogViewerCapture()
        {
            try
            {
                var options = LogViewerConfig.Bind(Config);

                // 只有一件事用得到 config 目录：允许用 <config>\DSHCore\logviewer.html
                // 覆盖内嵌的查看器页面。
                string configDir = null;
                if (Config != null && !string.IsNullOrEmpty(Config.ConfigFilePath))
                    configDir = Path.GetDirectoryName(Config.ConfigFilePath);

                LogPipeline.Start(options, configDir, Log);
            }
            catch (Exception ex)
            {
                Log.LogWarning("日志查看器初始化失败（不影响游戏）：" + ex.Message);
            }
        }
    }
}
