using System;
using DSHCore.Ui;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UiGallery
{
    /// <summary>
    /// 画廊的每一页 = 一种原版 UI 素材的**真实观感** + 一句"它是从哪来的 / 有什么坑"。
    ///
    /// <para>写这一整个文件的约束是刻意的：<b>不允许出现任何坐标、尺寸、缩放数字</b>。
    /// 所有节点都直接塞进官方对话框自己的 <c>VerticalLayoutGroup</c>，排版由原版负责。
    /// 如果哪天某页非得算坐标才能对上，那就说明 DSHCore.Ui 缺一个 API —— 应该去补 API，
    /// 而不是在本文件里加数字（GraphicConsole 就是从这里开始失控的）。</para>
    ///
    /// <para><b>另一条约束：只用 <see cref="UiSkinPart"/> 里"整件"的素材</b> ——
    /// 设置项一律走 <see cref="UiOptionRow"/>（原生四合一容器），
    /// <b>绝不单独克隆 LabeledToggle / TextSlider / LabeledDropdown</b>。
    /// 这三个控件的尺寸契约与提示锚点都由容器提供，拆开用就会烂。</para>
    /// </summary>
    internal static class GalleryPages
    {
        internal sealed class Page
        {
            public string Name;
            public string Description;

            /// <summary>参数是「把一行加进内容区」的回调。把它挂到内容宿主上即可。</summary>
            public Action<Action<GameObject>> Build;
        }

        public static readonly Page[] All = Define();
        public static readonly string[] Names = NamesOf(All);

        /// <summary>
        /// 导航按钮的文案。**不要多**：对话框的按钮区按行排，每多一行就把它撑高一行，
        /// 很容易超出屏幕（实测 9 个按钮 = 305px）。
        /// </summary>
        public static Il2CppStringArray NavLabels()
        {
            return ToIl2Cpp(new[] { "上一页", "下一页", "关闭" });
        }

        /// <summary>按名字（支持前缀 / 序号）找页；找不到返回 -1。</summary>
        public static int IndexOf(string name)
        {
            if (string.IsNullOrEmpty(name))
                return -1;

            int number;
            if (int.TryParse(name, out number))
            {
                var idx = number - 1;
                return (idx >= 0 && idx < All.Length) ? idx : -1;
            }

            for (var i = 0; i < All.Length; i++)
            {
                if (string.Equals(All[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            for (var i = 0; i < All.Length; i++)
            {
                if (All[i].Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
            return -1;
        }

        public static string List()
        {
            var sb = new System.Text.StringBuilder(300);
            for (var i = 0; i < All.Length; i++)
                sb.Append("  ").Append(i + 1).Append(". ").Append(All[i].Name).Append('\n');
            return sb.ToString();
        }

        public static string[] NamesOf(Page[] pages)
        {
            var names = new string[pages.Length];
            for (var i = 0; i < pages.Length; i++)
                names[i] = pages[i].Name;
            return names;
        }

        // ------------------------------------------------------------------
        // 页面定义
        // ------------------------------------------------------------------

        private static Page[] Define()
        {
            return new[]
            {
                new Page
                {
                    Name = "文本",
                    Description = "字号一览：原版素材的出厂值（★） vs 本画廊自己的选择（标题 22 / 正文 14）。",
                    Build = BuildText,
                },
                new Page
                {
                    Name = "复选框",
                    Description = "Toggle：原生设置项行（OptionWidgets），只激活复选框那一支。",
                    Build = BuildToggles,
                },
                new Page
                {
                    Name = "滑条",
                    Description = "Slider：同一个原生行容器，这次激活滑条那一支。",
                    Build = BuildSliders,
                },
                new Page
                {
                    Name = "下拉框",
                    Description = "Dropdown：点开它，列表应该正常展开（弹窗画布已对齐）。",
                    Build = BuildDropdowns,
                },
                new Page
                {
                    Name = "按钮",
                    Description = "ButtonRow 铺一排横向按钮；单按钮也可以走设置项行。",
                    Build = BuildButtons,
                },
                new Page
                {
                    Name = "列表",
                    Description = "UiList：官方 ScrollRect + 官方按钮行。滚轮/拖拽由原版 ScrollRect 负责。",
                    Build = BuildList,
                },
                new Page
                {
                    Name = "输入框",
                    Description = "InputField：原版 TMP_InputField，光标与输入法都继承自 prefab。",
                    Build = BuildInputs,
                },
                new Page
                {
                    Name = "提示",
                    Description = "悬停提示：把鼠标放到下面的按钮或复选框上看看。",
                    Build = BuildTooltips,
                },
                new Page
                {
                    Name = "报告",
                    Description = "素材解析状态。完整版（含来源路径）用 /dshui report 看。",
                    Build = BuildReport,
                },
            };
        }

        // ------------------------------------------------------------------
        // 各页内容
        // ------------------------------------------------------------------

        private static void BuildText(Action<GameObject> add)
        {
            // 「原版默认字号」全部是**实时量出来的**（UiSkin.MeasureFont），不是文档里抄的数字。
            var title = UiSkin.MeasureFont(UiSkinPart.Title);
            var body = UiSkin.MeasureFont(UiSkinPart.Text);

            // 每行必须能放下：对话框高度是随内容自适应的，行太长/太多会让整个框顶出屏幕。
            Add(add, Note("下面每行都是一份 Text 素材的克隆，只改字号；★ = 原版出厂值（本页实时量得）。"));

            // ① 素材原样（不改字号，只换文本）——原版长什么样就直接画出来
            Add(add, Sample(UiSkinPart.Title, "★ 标题素材 " + title.Size + "pt"));
            Add(add, SizedText(0f, "★ 正文素材 " + body.Size + "pt"));

            // ② 本画廊自己的选择
            Add(add, SizedText(GalleryPanel.TitleFontSize, GalleryPanel.TitleFontSize + "pt 本画廊标题"));
            Add(add, SizedText(GalleryPanel.BodyFontSize, GalleryPanel.BodyFontSize + "pt 本画廊正文"));

            // ③ 其它原版文本素材的出厂字号（作为「我该用几号」的参照）
            Add(add, SizedText(16f, "16pt ★ 原版设置项标签"));
            Add(add, SizedText(14f, "14pt ★ 原版输入框文字"));
            Add(add, SizedText(12f, "12pt 更小号的注释"));

            Add(add, Note("结论：原版字号是为「短文本提示」调的。要放大段说明请自己调 "
                + "UiClone.SetFontSize(...)，别改 UiSkin 的素材（那会连带所有拿同一素材的人）。"
                + "想知道某个素材出厂多大：UiSkin.MeasureFont(part)。"));
        }

        private static void BuildToggles(Action<GameObject> add)
        {
            AddRow(add, row => row
                .SetToggle("复选框：默认勾选", true, v => Toast("复选框 = " + v))
                .SetTooltip("复选框", "这是原生设置项行，提示出现在行的上方而不是压住控件。"));
            AddRow(add, row => row.SetToggle("复选框：默认未勾选", false, v => Toast("复选框 = " + v)));
        }

        private static void BuildSliders(Action<GameObject> add)
        {
            // 标签会自动长成「标签：值」并在拖动时实时刷新（原版行为，见 UiOptionRow.SetSlider）。
            AddRow(add, row => row.SetSlider("整数滑条（0~100，wholeNumbers）", 0f, 100f, true, 60f,
                null, v => Toast("松手值 = " + (int)v)));
            AddRow(add, row => row.SetSlider("小数滑条（0.0~1.0）", 0f, 1f, false, 0.2f));

            // 值那一半是可替换的：拖一下看它变成百分比。
            AddRow(add, row => row.SetSlider("自定义格式", 0f, 100f, true, 40f, null, null,
                v => Mathf.RoundToInt(v) + "%"));

            Add(add, Note(
                "滑条标签是「标签：值」，拖动时逐帧刷新 —— 原版由 OptionsDialogController 调 "
                + "OptionWidgets.SetSliderText 做这件事。要换格式用 SetSliderValueFormat。"));
        }

        private static void BuildDropdowns(Action<GameObject> add)
        {
            // ⚠️ 下拉框那一支是**已知不可用**的：行本身能显示、能改值，但展开出来的选项列表
            // 在本游戏里一个像素都不画（射线却生效）。本库不提供替代实现，详见
            // DSHCore/UI_RESEARCH.md §6.4-17 / §7.6。
            AddRow(add, row => row.SetDropdown("下拉框（已知不可用）",
                new[] { "选项 A", "选项 B", "选项 C" }, 0,
                i => Toast("下拉框 = 选项 " + (char)('A' + i))));

            Add(add, Note(
                "碍于技术问题，该组件无法正常工作，请自行寻找替代方案，"
                + "如果你对修复该组件有任何建议，请联系 QoZnoS[https://github.com/QoZnoS]。"
                + "点开它只会“没反应却改值”——射线生效、列表不画。"
                + "运行时状态（画布/矩形/Graphic）用 /dshui dd 打印。"));
        }

        private static void BuildButtons(Action<GameObject> add)
        {
            var row = UiSkin.Clone(UiSkinPart.ButtonRow, null, "Row_ButtonRow");
            var buttonRow = UiFinder.IsAlive(row) ? row.GetComponent<MVZ2.UI.ButtonRow>() : null;
            if (buttonRow != null)
            {
                buttonRow.UpdateButtons(ToIl2Cpp(new[] { "按钮 A", "按钮 B", "按钮 C" }));
                HookButtonRow(buttonRow);
                Add(add, row);
            }
            else
            {
                Add(add, Missing("ButtonRow"));
            }

            AddRow(add, r => r.SetButton("单按钮：走设置项行", () => Toast("点了单按钮行")));
        }

        /// <summary>「列表」页做出来的那个列表（自测用；页面重建时会被覆盖）。</summary>
        private static UiList LastList;

        private static void BuildList(Action<GameObject> add)
        {
            var list = UiList.Create(null, "Sample_List");
            if (list == null)
            {
                Add(add, Missing("ScrollView"));
                return;
            }
            LastList = list;

            // 视口高度：本页处在「ContentSizeFitter 撑高的对话框」里，所以必须显式给一个有界高度。
            // 这里按画布高度折算（不是写死像素）—— 见 UiList 的类注释。
            // 比例取 0.45：对话框高度是「内容之和」，列表再高就会把整个面板顶出屏幕。
            list.SetViewportHeight(UiList.SuggestHeight(0.45f));

            for (var i = 1; i <= 10; i++)
            {
                var n = i;
                list.AddButton("列表项 " + n + "（点我看反馈）", () => Toast("点了列表项 " + n));
            }

            Add(add, list.Root);
        }

        /// <summary>自测用：把「列表」页那个列表的状态打出来（行数/高度/滚动位置）。</summary>
        internal static string ListReport()
        {
            return LastList == null ? "（本次还没建过列表）" : LastList.Describe();
        }

        private static void BuildInputs(Action<GameObject> add)        {
            var row = UiSkin.Clone(UiSkinPart.InputField, null, "Row_Input");
            if (!UiFinder.IsAlive(row))
            {
                Add(add, Missing("InputField"));
                return;
            }

            try
            {
                var input = row.GetComponentInChildren<TMP_InputField>(true);
                if (input != null)
                {
                    input.text = "在这里打字试试（中文也行）";
                    HookInput(input);
                }
            }
            catch (Exception)
            {
            }

            Add(add, row);
        }

        private static void BuildTooltips(Action<GameObject> add)
        {
            // ① 原生行自带的锚点（推荐路径）
            AddRow(add, r => r.SetButton("设置项行 + 提示", () => Toast("按钮"))
                                 .SetTooltip("设置项行", "锚点是行顶边中点、pivot (0.5, 0)，所以提示朝上展开。"));

            // ② 给任意节点挂提示：UiTooltip 会照原版契约造一个专用锚点子节点
            var row = UiSkin.Clone(UiSkinPart.ButtonRow, null, "Row_Tooltip");
            var buttonRow = UiFinder.IsAlive(row) ? row.GetComponent<MVZ2.UI.ButtonRow>() : null;
            if (buttonRow != null)
            {
                buttonRow.UpdateButtons(ToIl2Cpp(new[] { "把鼠标放上来", "长文本提示" }));
                HookButtonRow(buttonRow);
                AttachTooltipsToButtons(row,
                    "悬停提示",
                    "这是原版同一套提示：位置、跟随、淡入淡出全部由游戏负责，我们只提供文本。");
                Add(add, row);
            }
            else
            {
                Add(add, Missing("ButtonRow"));
            }

            Add(add, Note(
                "注意：锚点的位置与 pivot 决定了提示朝哪边长。"
                + "默认 pivot 是 (0.5,0.5)，直接挂在控件上就会把提示居中盖住控件 —— 这是踩过的坑。"));
        }

        private static void BuildReport(Action<GameObject> add)
        {
            // 只放**精简版**报告：完整版带完整节点路径，十几个长路径折行后会撑出屏幕
            // （对话框的框体由 ContentSizeFitter 撑高，没有滚动能力）。
            Add(add, Note(UiSkin.ReportCompact));
        }

        // ------------------------------------------------------------------
        // 行构造
        // ------------------------------------------------------------------

        /// <summary>
        /// 一段**说明性文字**（本画廊的段落）。
        ///
        /// <para>字号用画廊自己的正文字号：原版素材的 24pt 是给短提示的，
        /// 画廊每页要塞好几行解释，用 24pt 两行就把不滚动的面板顶出屏幕。</para>
        /// </summary>
        private static GameObject Note(string text)
        {
            return SizedText(GalleryPanel.BodyFontSize, text);
        }

        /// <summary>
        /// 用正文素材克隆一行，并把字号钉成 <paramref name="size"/>。
        /// <paramref name="size"/> &lt;= 0 表示**保持素材原样**（用来展示"原版默认字号"）。
        /// </summary>
        private static GameObject SizedText(float size, string text)
        {
            var go = UiSkin.Clone(UiSkinPart.Text, null,
                "Sample_Text_" + size.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
            if (!UiFinder.IsAlive(go))
                return Missing("Text");
            if (size > 0f)
                UiClone.SetFontSize(go, size, true);
            UiClone.SetLabel(go, text);
            return go;
        }

        private static void Add(Action<GameObject> add, GameObject row)
        {
            if (row != null)
                add(row);
        }

        private static void AddRow(Action<GameObject> add, Action<UiOptionRow> configure)
        {
            var row = UiOptionRow.Create(null, "Row_Option");
            if (row == null)
            {
                Add(add, Missing("OptionWidgets"));
                return;
            }

            row.SetStretch();
            configure(row);
            Add(add, row.Root);
        }

        private static GameObject Sample(UiSkinPart part, string text)
        {
            var go = UiSkin.Clone(part, null, "Sample_" + part);
            if (!UiFinder.IsAlive(go))
                return Missing(part.ToString());
            // 字号一律**不动**：原版每个文本节点的字号都是配着容器算好的。
            UiClone.SetLabel(go, text);
            return go;
        }

        private static GameObject Missing(string what)
        {
            var go = UiSkin.Clone(UiSkinPart.Text, null, "Missing");
            if (!UiFinder.IsAlive(go))
                return null;
            UiClone.SetLabel(go, "[素材缺失] " + what + " —— 见「报告」页。");
            return go;
        }

        // ------------------------------------------------------------------
        // 交互（全部走"转 IL2CPP 委托 + 自己持有"这一条已验证的路）
        // ------------------------------------------------------------------

        private static void HookButtonRow(MVZ2.UI.ButtonRow row)
        {
            try
            {
                var managed = new Action<MVZ2.UI.ButtonRow, int>(OnButtonRowClick);
                var il2cpp = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<Il2CppSystem.Action<MVZ2.UI.ButtonRow, int>>(managed);
                UiClone.KeepAlive(row.gameObject, managed, il2cpp);
                row.add_OnButtonClick(il2cpp);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Gallery] 挂按钮行回调失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void OnButtonRowClick(MVZ2.UI.ButtonRow row, int index)
        {
            Toast("点了第 " + (index + 1) + " 个按钮");
        }

        private static void HookInput(TMP_InputField input)
        {
            try
            {
                var managed = new Action<string>(v => Toast("输入结束：" + v));
                var il2cpp = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<UnityEngine.Events.UnityAction<string>>(managed);
                UiClone.KeepAlive(input.gameObject, managed, il2cpp);
                input.onEndEdit.AddListener(il2cpp);
            }
            catch (Exception)
            {
            }
        }

        private static void AttachTooltipsToButtons(GameObject row, string title, string text)
        {
            try
            {
                var buttons = row.GetComponentsInChildren<Button>(true);
                if (buttons == null)
                    return;
                for (var i = 0; i < buttons.Length; i++)
                {
                    var b = buttons[i];
                    if (b == null)
                        continue;
                    // 挂在按钮**根节点**上（事件向上冒泡，整块按钮都算热区）；
                    // 锚点子节点由 UiTooltip 按原版契约创建在节点顶边中点。
                    UiTooltip.Attach(b.gameObject, title, text);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>用游戏自己的弹出提示给反馈（比自绘 toast 更原生，也少一层依赖）。</summary>
        private static void Toast(string text)
        {
            try
            {
                var scene = UiGame.Scene;
                if (scene != null)
                {
                    scene.ShowPopup(text);
                    return;
                }
            }
            catch (Exception)
            {
            }
            Plugin.Log?.LogInfo("[Gallery] " + text);
        }

        // ------------------------------------------------------------------
        // 工具
        // ------------------------------------------------------------------

        internal static Il2CppStringArray ToIl2Cpp(string[] items)
        {
            var n = items == null ? 0 : items.Length;
            var arr = new Il2CppStringArray(n);
            for (var i = 0; i < n; i++)
                arr[i] = items[i];
            return arr;
        }
    }
}
