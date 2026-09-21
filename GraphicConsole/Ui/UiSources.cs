using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GraphicConsole
{
    /// <summary>
    /// 面板要用的**克隆源**，全部取自当前场景里已有的活对象。
    ///
    /// 为什么加载不了 prefab：游戏 UI prefab 在 Addressables bundle 里，mod 侧没有可靠的
    /// 按名加载入口。而「克隆现场已有节点」是已验证可行、且能连序列化引用
    /// （字体 / sprite / 点击音效 / 事件订阅）一起继承的做法。
    ///
    /// ⚠️ 关键设计：**把找到的节点先复制到我们自己的「模板架」上，之后一律从模板架克隆。**
    /// 之前直接从活的原版节点克隆有两个真问题：
    ///   1. 面板自己的克隆体也带 <c>InputNameDialog</c> 组件 → 下次查找会**把我们自己当成模板**，
    ///      于是「第二次打开面板时文字变回原版的『请输入用户名』/『确定』」（顶部文字与页签
    ///      是克隆出来的节点，而那次克隆的来源正是上一次的面板，它已经被我们改过/清过子节点）；
    ///   2. 模板跟着原对象一起受场景与对话框生命周期影响，切场景或原对象被销毁就悬垂。
    /// 现在模板架是**我们自己的对象**，且查找时显式跳过它。
    ///
    /// 模板架挂在原对话框的同一个父节点下（保证同画布、同缩放），整体 <c>SetActive(false)</c>：
    /// 不渲染、不参与父级布局（LayoutGroup 会跳过未激活子节点）。
    /// </summary>
    internal static class UiSources
    {
        public const string HolderName = "GraphicConsoleTemplates";

        /// <summary>
        /// 面板应该挂到哪个父节点下。
        ///
        /// ⚠️⚠️ 必须是**模板架的父节点**，不能是模板架本身 —— 模板架是
        /// <c>SetActive(false)</c> 的（不渲染、不参与布局），把面板挂进去会让面板
        /// <b>整支处于 inactive</b>：LayoutGroup 一次都不会跑，所有子节点停在初始
        /// 100×100 / 位置 0,0，而且屏幕上什么都看不到。
        /// 这是真踩过的坑：布局 dump 里 Header/Content/Footer 全是 <c>100x100 @ 0,0</c>。
        /// </summary>
        public static Transform HostParent;

        // ---- 模板架上的东西（都是我们自己的克隆体）----
        public static GameObject Holder;
        /// <summary>整个对话框（自带 Blocker + Dialog 框体 + 背景图 + 布局组）。</summary>
        public static GameObject DialogTemplate;
        /// <summary>原版文本按钮（TextButton）。</summary>
        public static GameObject ButtonTemplate;
        /// <summary>原版输入框（TMP_InputField）。</summary>
        public static GameObject InputTemplate;
        /// <summary>原版独立文本（TMP_Text，带字体资产）。</summary>
        public static GameObject TextTemplate;
        /// <summary>原版复选框行（LabeledToggle）。</summary>
        public static GameObject ToggleTemplate;
        /// <summary>原版下拉框节点（TMP_Dropdown，含它自己的 Template 子节点）。</summary>
        public static GameObject DropdownTemplate;

        public static string LastReport = "（尚未解析）";

        public static bool Ready
        {
            get
            {
                return UiFinder.IsAlive(Holder)
                    && UiFinder.IsAlive(DialogTemplate)
                    && UiFinder.IsAlive(ButtonTemplate)
                    && UiFinder.IsAlive(InputTemplate)
                    && UiFinder.IsAlive(TextTemplate)
                    && UiFinder.IsAlive(ToggleTemplate)
                    && UiFinder.IsAlive(DropdownTemplate);
            }
        }

        private static int cachedSceneHandle = -1;
        private static bool resolved;

        public static bool Prepare(bool force = false)
        {
            var handle = SceneHandle();
            if (!force && resolved && handle == cachedSceneHandle && Ready)
                return true;

            resolved = true;
            cachedSceneHandle = handle;
            DestroyHolder();

            var notes = new List<string>();

            // ---- ① 找原对话框（UiFinder 会跳过我们自己的克隆体与模板架）----
            MVZ2.UI.InputNameDialog source = null;
            try
            {
                source = UiFinder.FindOne<MVZ2.UI.InputNameDialog>();
            }
            catch (Exception e)
            {
                notes.Add("找对话框异常 " + e.GetType().Name);
            }
            if (source == null)
            {
                LastReport = "找不到 MVZ2.UI.InputNameDialog（场景里没有可用的弹窗模板）";
                Plugin.Log?.LogError("[Ui] " + LastReport);
                return false;
            }

            var sourceGo = source.gameObject;
            var parent = FindTemplateParent(sourceGo.transform);
            if (parent == null)
            {
                LastReport = "对话框没有可用的父节点，无法建立模板架";
                Plugin.Log?.LogError("[Ui] " + LastReport);
                return false;
            }

            // ---- ② 建模板架 ----
            try
            {
                Holder = new GameObject(HolderName);
                Holder.AddComponent<RectTransform>();
                Holder.transform.SetParent(parent, false);
                UiFactory.RectOf(Holder).sizeDelta = new Vector2(1f, 1f);
                Holder.SetActive(false);
                // 面板本体挂到模板架的**父节点**（链可见）上，不能挂进模板架。
                HostParent = parent;
            }
            catch (Exception e)
            {
                LastReport = "建立模板架失败：" + e.GetType().Name;
                Plugin.Log?.LogError("[Ui] " + LastReport);
                return false;
            }

            // ---- ③ 逐个复制模板 ----
            DialogTemplate = UiFactory.Clone(sourceGo, Holder.transform, "T_Dialog");

            try
            {
                var confirm = source.confirmButton;
                if (confirm != null)
                    ButtonTemplate = UiFactory.Clone(confirm.gameObject, Holder.transform, "T_Button");
            }
            catch (Exception)
            {
            }
            if (ButtonTemplate == null)
            {
                var node = UiFinder.FindChild(sourceGo.transform, "ConfirmButton");
                if (node != null)
                    ButtonTemplate = UiFactory.Clone(node.gameObject, Holder.transform, "T_Button");
            }

            try
            {
                var input = source.inputField;
                if (input != null)
                    InputTemplate = UiFactory.Clone(input.gameObject, Holder.transform, "T_Input");
            }
            catch (Exception)
            {
            }
            if (InputTemplate == null)
            {
                var node = UiFinder.FindChild(sourceGo.transform, "InputField");
                if (node != null)
                    InputTemplate = UiFactory.Clone(node.gameObject, Holder.transform, "T_Input");
            }

            var titleNode = UiFinder.FindByPath(sourceGo.transform, "Dialog/TitleArea/Title");
            if (titleNode == null)
                titleNode = UiFinder.FindChild(sourceGo.transform, "Title");
            if (titleNode != null)
                TextTemplate = UiFactory.Clone(titleNode.gameObject, Holder.transform, "T_Text");

            BuildControlTemplates(notes);

            // ---- ④ 汇总 ----
            notes.Add("对话框=OK");
            notes.Add("按钮=" + (ButtonTemplate != null ? "OK" : "缺失"));
            notes.Add("输入框=" + (InputTemplate != null ? "OK" : "缺失"));
            notes.Add("文本=" + (TextTemplate != null ? "OK" : "缺失"));
            notes.Add("复选框=" + (ToggleTemplate != null ? "OK" : "缺失"));
            notes.Add("下拉框=" + (DropdownTemplate != null ? "OK" : "缺失"));
            LastReport = string.Join("  |  ", notes.ToArray());

            if (Ready)
                Plugin.Log?.LogInfo("[Ui] 模板架已就绪：" + LastReport);
            else
                Plugin.Log?.LogError("[Ui] 模板不完整：" + LastReport);
            return Ready;
        }

        /// <summary>
        /// 从场景里的 <c>OptionWidgets</c> 拆出复选框与下拉框模板。
        ///
        /// 为什么拆出来单独用、而不是整个 OptionWidgets 一起用：
        ///   OptionWidgets 是"四合一"容器（同时含复选框/滑条/下拉框/按钮），靠 <c>UpdateWidgets</c>
        ///   只激活其中一个。它的子节点被父级 <c>HorizontalLayoutGroup</c> + <c>LayoutElement</c>
        ///   约束宽度，直接拿来用时下拉框的 RectTransform 会被压得很窄 —— 而 TMP_Dropdown 展开
        ///   是**按自己的 RectTransform 算弹出位置**的，尺寸异常会让弹窗跑到看不见的地方，
        ///   表现为「下拉框点不开」。
        ///   所以这里只取里面的控件节点，由我们自己的行布局（标签自适应宽 + 控件固定宽）摆放。
        /// </summary>
        private static void BuildControlTemplates(List<string> notes)
        {
            try
            {
                var widgets = UiFinder.FindOne<MVZ2.UI.OptionWidgets>();
                if (widgets == null)
                {
                    notes.Add("场景里没有 OptionWidgets（复选框/下拉框模板缺失）");
                    return;
                }

                try
                {
                    var toggle = widgets.Toggle;
                    if (toggle != null && toggle.Toggle != null)
                    {
                        // ⚠️ 取 **LabeledToggle 节点本身**，不能取它的 parent。
                        // parent 是 OptionWidgets —— 那个"四合一"容器同时挂着
                        // LabeledToggle / TextSlider / LabeledDropdown / TextButton 四个子控件，
                        // 它靠代码里的 OptionWidgets.UpdateWidgets 只激活其中一个。
                        // 我们克隆来的副本里没有那个控制器，于是四个子控件各自的 activeSelf 保持
                        // 模板当时的状态 —— 实测结果是每个复选框格子里都冒出原版设置界面的
                        // 「语言」下拉框，而真正的复选框是关掉的。
                        ToggleTemplate = UiFactory.Clone(toggle.gameObject, Holder.transform, "T_Toggle");
                    }
                }
                catch (Exception)
                {
                }

                try
                {
                    var labeled = widgets.Dropdown;
                    if (labeled != null && labeled.Dropdown != null)
                        DropdownTemplate = UiFactory.Clone(labeled.Dropdown.gameObject, Holder.transform, "T_Dropdown");
                }
                catch (Exception)
                {
                }
            }
            catch (Exception e)
            {
                notes.Add("拆解 OptionWidgets 失败 " + e.GetType().Name);
            }
        }

        /// <summary>
        /// 找模板架的父节点：沿父链往上找第一个「整条链可见」的节点
        /// （原版有弹窗被"显示时才 SetActive(true)"的包装对象包着，那时克隆体永远不可见
        /// —— 本仓库踩过这个坑）。
        /// </summary>
        private static Transform FindTemplateParent(Transform source)
        {
            var node = source.parent;
            var guard = 0;
            while (node != null && guard++ < 32)
            {
                if (UiFinder.IsChainActive(node))
                    return node;
                node = node.parent;
            }
            // 都不行就退到最顶层根节点（宁可层级偏高，也不要克隆出永远看不见的面板）
            var root = source;
            guard = 0;
            while (root.parent != null && guard++ < 32)
                root = root.parent;
            return root;
        }

        private static void DestroyHolder()
        {
            try
            {
                if (Holder != null)
                    UnityEngine.Object.Destroy(Holder);
            }
            catch (Exception)
            {
            }
            Holder = null;
            DialogTemplate = null;
            ButtonTemplate = null;
            InputTemplate = null;
            TextTemplate = null;
            ToggleTemplate = null;
            DropdownTemplate = null;
            HostParent = null;
        }
        private static int SceneHandle()
        {
            try
            {
                return SceneManager.GetActiveScene().handle;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
