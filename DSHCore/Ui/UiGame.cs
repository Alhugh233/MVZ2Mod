using System;
using UnityEngine;

namespace DSHCore.Ui
{
    /// <summary>
    /// 官方的 UI 入口。**所有 mod 侧取游戏 UI 的正路都在这儿**，不要自己去遍历场景找
    /// <c>MainSceneUI</c>。
    ///
    /// <para>链路（已用 Cecil 对 <c>runtime/BepInEx/interop</c> 核实过，勿凭印象改）：</para>
    /// <code>
    /// MVZ2.Managers.MainManager.Instance          // 静态属性
    ///   .Scene                                    // → MVZ2.Scenes.MainSceneController
    ///     .ui                                     // → MVZ2.Scenes.MainSceneUI
    ///       .dialog                               // → MVZ2.UI.CustomDialog（官方唯一的"标题+正文+按钮"对话框）
    ///       .tooltip                              // → MVZ2.UI.Tooltip
    ///     .inputNameDialog                        // → MVZ2.Scenes.InputNameDialogController
    ///       .ui                                   // → MVZ2.UI.InputNameDialog
    /// </code>
    ///
    /// <para><b>为什么优先走这条链而不是场景遍历：</b>它是游戏自己的引用，不受节点改名、
    /// Standalone/Mobile 双预设、父链 active 状态影响；而场景遍历只是"找不到时的兜底"。</para>
    ///
    /// <para><b>线程约定：</b>本类型的一切成员只允许在 Unity 主线程调用（它们会触碰 IL2CPP 对象）。
    /// 详见 <c>AI_GUIDE.md</c> §5-⑮。</para>
    /// </summary>
    public static class UiGame
    {
        /// <summary>游戏 UI 是否已经可用（主菜单之前为 false）。</summary>
        public static bool IsReady
        {
            get { return Main != null && Scene != null && SceneUi != null; }
        }

        /// <summary>官方唯一的 <c>MainManager</c> 实例。未初始化时为 null。</summary>
        public static MVZ2.Managers.MainManager Main
        {
            get
            {
                try
                {
                    var m = MVZ2.Managers.MainManager.Instance;
                    return UiFinder.IsAlive(m) ? m : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>主场景控制器（主菜单 / 图鉴 / 地图 / 商店……都挂在它下面）。</summary>
        public static MVZ2.Scenes.MainSceneController Scene
        {
            get
            {
                var main = Main;
                if (main == null)
                    return null;
                try
                {
                    var s = main.Scene;
                    return UiFinder.IsAlive(s) ? s : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>主场景 UI 容器（对话框 / Tooltip / 遮罩 / 光标图标的宿主）。</summary>
        public static MVZ2.Scenes.MainSceneUI SceneUi
        {
            get
            {
                var scene = Scene;
                if (scene == null)
                    return null;
                try
                {
                    var ui = scene.ui;
                    return UiFinder.IsAlive(ui) ? ui : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// 官方的「标题 + 正文 + 若干按钮」对话框（<c>CustomDialog</c>）。
        /// 它是**唯一一个原版会自己复用的通用对话框**，所以也是本库全部对话框素材的来源。
        /// </summary>
        public static MVZ2.UI.CustomDialog Dialog
        {
            get
            {
                var ui = SceneUi;
                if (ui == null)
                    return null;
                try
                {
                    var d = ui.dialog;
                    return UiFinder.IsAlive(d) ? d : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>官方的「输入名字」对话框（带 TMP 输入框）。</summary>
        public static MVZ2.UI.InputNameDialog InputDialog
        {
            get
            {
                var scene = Scene;
                if (scene == null)
                    return null;
                try
                {
                    var ctrl = scene.inputNameDialog;
                    if (!UiFinder.IsAlive(ctrl))
                        return null;
                    var d = ctrl.ui;
                    return UiFinder.IsAlive(d) ? d : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        /// <summary>主 UI 画布（<c>GlobalUICanvas</c>）。</summary>
        public static Canvas MainCanvas
        {
            get
            {
                var ui = SceneUi;
                if (ui == null)
                    return null;
                try
                {
                    var c = ui.GetComponentInParent<Canvas>();
                    if (c == null)
                        return null;
                    return c.rootCanvas;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
    }
}
