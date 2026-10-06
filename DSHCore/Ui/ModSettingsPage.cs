using System;
using System.Collections.Generic;
using System.Text;
using MVZ2.Managers;
using MVZ2.Modding;
using UnityEngine;

namespace DSHCore.Ui
{
    /// <summary>
    /// **模组设置页**（插件页 → 「模组设置（DSHCore）」）：左边列出已加载的 mod，
    /// 右边显示选中 mod 的详细信息与它声明的设置项。
    ///
    /// <para><b>版面来源</b>：页面壳克隆**对话档案页**的索引（<c>Archive/Canvas/Index</c>），
    /// 因为那正是「左栏 + 右栏」这个版式的原版出处 ——
    /// <c>Top{Title} / Center{Filter[左栏], List[右栏]} / Bottom{ReturnButton}</c>。
    /// 左栏的搜索框与标签列表整栏清掉后换成我们自己的模组列表，
    /// 右栏换成「详细信息 + 设置项」的滚动列表。一个坐标都不用算。</para>
    ///
    /// <para><b>左栏的行样式</b>：不是原生按钮行，而是**档案页自己的条目行**
    /// （<c>Toggle</c> + 文字，见 <see cref="UiTagRow"/>）—— 视觉与档案页左栏逐像素同源，
    /// 「勾选」天然就是「当前选中」。拿不到该素材时退回按钮行（不会崩）。</para>
    ///
    /// <para><b>两栏宽度</b>：合计宽度按画布宽的比例调（<see cref="TwoColumnWidthRatio"/> = 0.94），
    /// 与「回放」页同宽；左栏保持 prefab 原宽度，**右栏吸收全部增量**。</para>
    ///
    /// <para><b>设置项从哪来</b>：<see cref="UiOptions"/> 的**声明表**
    /// （见 <see cref="UiOptions.DeclareToggle"/> 等）。也就是说 mod 只要在
    /// <c>Load()</c> 里声明一句，这里就会出现一行 —— 不需要注册 ModInfo、不需要 XML、
    /// 不需要注入定义类。</para>
    ///
    /// <para><b>文案都是可翻译的</b>：mod 名与设置项文案走 <see cref="UiLocale.Get"/>，
    /// 页面自己的中文字面串也过一遍（见 <see cref="DeclareTexts"/>），
    /// 所以切语言后重进本页就能看到对应语言。</para>
    ///
    /// <para><b>为什么多值项渲染成按钮而不是下拉框</b>：原版 <c>TMP_Dropdown</c> 的展开列表
    /// 在本环境（<c>GlobalUICanvas</c> 相机画布）下一个像素都画不出来（DSHCore 实测并记录在
    /// <c>UI_RESEARCH.md</c>）。所以这里渲染成「<b>标签：当前值</b>」的按钮行，
    /// 点一下切到下一个值 —— 它**不是**下拉框的替代实现（没有弹窗、没有候选列表），
    /// 只是一个循环按钮。想修下拉框请看 <c>UI_GUIDE.md</c> §9。</para>
    /// </summary>
    public sealed class ModSettingsPage : AddonPage
    {
        /// <summary>左栏标题（源语言 = 中文，译文见 <see cref="DeclareTexts"/>）。</summary>
        private const string ListTitleText = "已加载的模组";

        /// <summary>右栏标题的默认文案（选中 mod 后会换成 mod 名）。</summary>
        private const string DetailTitleText = "详细信息";

        /// <summary>栏目标题的字号。**比正文（14）大一档**，并居中显示。</summary>
        private const float HeaderFontSize = 18f;

        /// <summary>
        /// 栏目标题的**行高**。刻意比原版行高（<see cref="UiKit.RowHeight"/>，实测 40 单位）矮：
        /// 标题只占一行，40 单位里有一半是空白，白占了下方内容框的高度。
        /// </summary>
        private const float HeaderRowHeight = 26f;

        /// <summary>左栏行间距（原版列表素材是 10 单位，条目之间会空出一大截）。</summary>
        private const float ModListSpacing = 2f;

        /// <summary>右栏行间距（比左栏松一点：信息条目之间要能分得清）。</summary>
        private const float DetailSpacing = 6f;

        /// <summary>
        /// 内容框四周的留白（单位；6 单位 ≈ 1024 宽下 8 屏幕像素）。
        ///
        /// <para>加在**面板自己的纵向布局组**上（标题与列表一起往里缩），对齐原版菜单页里
        /// 列表与框体之间的那圈留白。列表自己的内距是 0（<see cref="UiList.SetRowFit"/> 清掉的，
        /// 为了让行铺满框体），所以留白只能从这里来。</para>
        ///
        /// <para>注意壳的框架美术（<c>Background</c>）是 <c>ignoreLayout</c> 的，**不跟着缩** ——
        /// 缩的只是里面的内容，所以看起来正好是「框体留白」。</para>
        /// </summary>
        protected override int PanelPadding
        {
            get { return 6; }
        }

        public override string Id
        {
            get { return "ModSettings"; }
        }

        public override string EntryLabel
        {
            get { return "模组设置（DSHCore）"; }
        }

        public override string Title
        {
            get { return "模组设置（DSHCore）"; }
        }

        /// <summary>左右两栏版式 —— 与「对话档案」页同款。</summary>
        protected override AddonShellSource ShellSource
        {
            get { return AddonShellSource.ArchiveIndex; }
        }

        /// <summary>单栏宽度改动不适用于两栏版式（走 <see cref="TwoColumnWidthRatio"/>）。</summary>
        protected override string WidthTargetPath
        {
            get { return null; }
        }

        /// <summary>
        /// 两栏合计宽度 = 画布宽 × 0.94（与原版「回放」页的单栏面板同宽）。
        /// 左栏保持 prefab 宽度，右栏吸收全部增量，整体水平居中。
        /// </summary>
        protected override float TwoColumnWidthRatio
        {
            get { return 0.94f; }
        }

        /// <summary>
        /// 页面标题行 80 → 64 单位：标题只占一行文字，多出来的高度全部让给两栏内容框
        /// （实测两栏各高 +20 屏幕像素）。
        /// </summary>
        protected override float TopHeight
        {
            get { return 64f; }
        }

        // ------------------------------------------------------------------
        // 状态
        // ------------------------------------------------------------------

        private sealed class ModRow
        {
            public string Nsp;

            /// <summary>显示名（**已按当前语言取过词**）。</summary>
            public string Name;

            /// <summary>显示名的源串（排障 / 报告用）。</summary>
            public string NameSource;

            /// <summary>一句说明的**源串**（没声明时为 null）。</summary>
            public string DescriptionSource;

            /// <summary>作者的**源串**（没声明时为 null）。</summary>
            public string AuthorSource;

            public bool Builtin;
            public bool Registered;
            public string Logic;
            public int Settings;
        }

        private UiList _modList;
        private UiList _detailList;
        private TMPro.TextMeshProUGUI _listTitle;
        private TMPro.TextMeshProUGUI _detailTitle;
        private List<ModRow> _mods = new List<ModRow>();
        private readonly List<GameObject> _modRows = new List<GameObject>();
        private int _selected = -1;

        /// <summary>行素材拿不到时退回按钮行（此时选中状态靠文案上的【】表示）。</summary>
        private bool _rowFallback;

        public ModSettingsPage()
        {
            Register();
        }

        // ------------------------------------------------------------------
        // 内容
        // ------------------------------------------------------------------

        /// <summary>
        /// 页面自己那几句中文的**英文译文**（源语言 = 中文，正是游戏自己的约定）。
        /// 只是示范「模组的中文文案 + 英文译文」也能走同一套；不补也不会崩（查不到返回原文）。
        /// </summary>
        private static void DeclareTexts()
        {
            if (_textsDeclared)
                return;
            _textsDeclared = true;
            UiLocale.AddAll(UiLocale.English,
                ListTitleText, "Loaded Mods",
                DetailTitleText, "Details",
                "说明", "Description",
                "作者", "Author",
                "命名空间", "Namespace",
                "来源", "Source",
                "逻辑体", "Logic",
                "设置项", "Settings",
                "游戏内置", "Built-in",
                "外部 mod", "External mod",
                "外部 mod（无 ModInfo）", "External mod (no ModInfo)",
                "（无）", "(none)",
                "（未声明）", "(not declared)",
                "没有已加载的模组。", "No mods loaded.",
                "该 mod 没有声明设置项。",
                "This mod declares no settings.");
        }

        private static bool _textsDeclared;

        protected override bool BuildContent(AddonShell shell)
        {
            DeclareTexts();

            // ---- 左栏：已加载的模组 ----
            var left = shell.Node("Center/Filter/Contents");
            if (!UiFinder.IsAlive(left))
            {
                // 版面变了（例如原版把 Filter 改名）→ 退回整块 Center 自己分栏。
                left = shell.Node("Center");
            }
            if (!UiFinder.IsAlive(left))
                return false;

            // ⚠ 必须在**清空之前**：行素材（档案页左栏那个条目的模板）就长在这棵树里。
            //   拿不到时后续 Create 会返回 null，列表退回原生按钮行。
            UiTagRow.Template(left.transform);

            shell.PreparePanel(left, clearChildren: true, spacing: 6f, padding: PanelPadding);
            _listTitle = UiKit.Label(left.transform, "ListTitle", UiLocale.Get(ListTitleText),
                HeaderFontSize, 0f, 1f, TMPro.TextAlignmentOptions.Center, HeaderRowHeight);
            UiKit.SetBold(_listTitle);

            _modList = UiList.Create(left.transform, "DSH_ModSettingsMods");
            if (_modList == null || !_modList.IsValid)
                return false;
            _modList.SetFlexible();
            _modList.SetRowFit(true, ModListSpacing);

            // ---- 右栏：详细信息 + 设置项 ----
            var right = shell.Node("Center/List/Contents");
            if (!UiFinder.IsAlive(right))
                right = shell.Node("Center/List");
            if (!UiFinder.IsAlive(right))
                return false;

            shell.PreparePanel(right, clearChildren: true, spacing: 6f, padding: PanelPadding);
            _detailTitle = UiKit.Label(right.transform, "DetailTitle", UiLocale.Get(DetailTitleText),
                HeaderFontSize, 0f, 1f, TMPro.TextAlignmentOptions.Center, HeaderRowHeight);
            UiKit.SetBold(_detailTitle);

            _detailList = UiList.Create(right.transform, "DSH_ModSettingsDetail");
            if (_detailList == null || !_detailList.IsValid)
                return false;
            _detailList.SetFlexible();
            _detailList.SetRowFit(true, DetailSpacing);

            return true;
        }

        protected override void OnOpened()
        {
            DeclareTexts();
            SetListTitle(UiLocale.Get(ListTitleText));
            RefreshMods();
        }

        protected override void OnClosed()
        {
            _modList = null;
            _detailList = null;
            _detailTitle = null;
            _listTitle = null;
            _mods.Clear();
            _modRows.Clear();
            _rowFallback = false;
            _selected = -1;
        }

        // ------------------------------------------------------------------
        // 模组列表
        // ------------------------------------------------------------------

        private void RefreshMods()
        {
            if (_modList == null || !_modList.IsValid)
                return;

            _mods = LoadMods();

            // 默认选中「第一个有设置项的 mod」，没有就选第一个。
            var pick = -1;
            for (var i = 0; i < _mods.Count; i++)
            {
                if (_mods[i].Settings > 0)
                {
                    pick = i;
                    break;
                }
            }
            if (pick < 0 && _mods.Count > 0)
                pick = 0;

            _modList.Clear();
            _modRows.Clear();
            _rowFallback = false;
            for (var i = 0; i < _mods.Count; i++)
            {
                var idx = i;
                // 行 = 「对话档案」左栏那种条目（Toggle + 文字）：勾选天然就是「当前选中」。
                var row = UiTagRow.Create(_modList.Content, "DSH_Mod" + i, RowLabel(idx),
                    idx == pick, v => OnRowToggled(idx, v));
                if (UiFinder.IsAlive(row))
                {
                    _modRows.Add(_modList.Add(row));
                    continue;
                }

                // 素材拿不到（从没进过档案页 / 版本变了）→ 退回原生按钮行：功能不丢。
                _rowFallback = true;
                _modRows.Add(_modList.AddButton(RowLabel(idx), () => Select(idx)));
            }
            UiKit.Rebuild(_modList.Root);

            _selected = pick;
            ApplySelection();
            RefreshDetails();
        }

        /// <summary>列表里一行的文案（选中标记由行自己的勾选表达，文案里不再加括号）。</summary>
        private string RowLabel(int index)
        {
            return _mods[index].Name;
        }

        /// <summary>玩家点了某一行（只有档案样式条目会走这里：值 = 点完之后它的勾选状态）。</summary>
        private void OnRowToggled(int index, bool value)
        {
            if (index != _selected)
            {
                Select(index);
                return;
            }
            // 点的是当前行、把自己的勾去掉了 → 弹回去：单选列表不允许「一个都不选」。
            if (!value)
                ApplySelection();
        }

        /// <summary>把所有行的文案与勾选状态刷成当前选中项（<c>SetIsOnWithoutNotify</c>，不会回调）。</summary>
        private void ApplySelection()
        {
            for (var i = 0; i < _modRows.Count && i < _mods.Count; i++)
            {
                var row = _modRows[i];
                if (!UiFinder.IsAlive(row))
                    continue;
                if (_rowFallback)
                    UiList.SetButtonLabel(row, i == _selected ? "【" + RowLabel(i) + "】" : RowLabel(i));
                else
                    UiTagRow.Set(row, RowLabel(i), i == _selected);
            }
        }

        private void Select(int index)
        {
            if (index < 0 || index >= _mods.Count || index == _selected)
                return;
            _selected = index;

            // 只改文案 / 勾选，**不要重建整个列表** —— 这个回调正是列表行上的控件自己发出来的，
            // 在回调里毁掉那一行会连正在执行的对象一起拆掉。
            //
            // ⚠️ 档案条目行走 UiTagRow.Set；退回的按钮行必须走 UiList.SetButtonLabel
            //    （内部会再调一次 ButtonRow.UpdateButtons）—— 直接改子节点上的 TMP 不生效，
            //    那一份往往不是真正被画出来的（实测：“日志改了、画面没变”）。
            ApplySelection();

            RefreshDetails();
        }

        /// <summary>
        /// 已加载的模组 = ① 官方 <c>ModManager</c> 注册的 <c>ModInfo</c>
        /// **并上** ② 「声明过设置项」与 ③ 「介绍过自己」（<see cref="UiOptions.DeclareMod"/>）的命名空间。
        ///
        /// <para>②③ 很重要：本库默认的设置项路径是零注入的，走那条路的 mod
        /// （例如 Loadout）根本不会在 <c>ModManager</c> 里出现；
        /// 而 <c>Replay</c> 这种**纯补丁 mod**（没有任何游戏内容、也没设置项）
        /// 只能靠 <see cref="UiOptions.DeclareMod"/> 把自己登记进来。</para>
        /// </summary>
        private static List<ModRow> LoadMods()
        {
            var result = new List<ModRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            try
            {
                var main = MainManager.Instance;
                var mm = main == null ? null : main.ModManager;
                if (mm != null)
                {
                    ModInfo[] infos = mm.GetAllModInfos();
                    if (infos != null)
                    {
                        for (var i = 0; i < infos.Length; i++)
                        {
                            var info = infos[i];
                            if (info == null)
                                continue;
                            var nsp = info.Namespace;
                            if (string.IsNullOrEmpty(nsp) || seen.Contains(nsp))
                                continue;
                            seen.Add(nsp);
                            result.Add(MakeRow(nsp, info.DisplayName, true, info.IsBuiltin, LogicName(info)));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("modset-infos", "枚举 ModInfo 失败：" + e.GetType().Name + ": " + e.Message);
            }

            var extras = UiOptions.SpacesWithSettings();
            for (var i = 0; i < extras.Length; i++)
                AddExtra(result, seen, extras[i]);

            var declared = UiOptions.DeclaredSpaces();
            for (var i = 0; i < declared.Length; i++)
                AddExtra(result, seen, declared[i]);

            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < result.Count; i++)
            {
                result[i].Settings = UiOptions.ForPage(result[i].Nsp).Length;
                order[result[i].Nsp] = i;
            }

            // 排序：① 有设置项的外部 mod 排最前（这个页面的用途就是调它们）；
            //       ② 没有设置项的外部 mod；③ 内置 mod（游戏本体）最后。
            // 组内保持原有顺序（List.Sort 不稳定，所以用登记序号兜底）。
            result.Sort((a, b) =>
            {
                if (a.Builtin != b.Builtin)
                    return a.Builtin ? 1 : -1;
                var sa = a.Settings > 0 ? 0 : 1;
                var sb2 = b.Settings > 0 ? 0 : 1;
                if (sa != sb2)
                    return sa - sb2;
                return order[a.Nsp].CompareTo(order[b.Nsp]);
            });
            return result;
        }

        private static void AddExtra(List<ModRow> result, HashSet<string> seen, string nsp)
        {
            if (string.IsNullOrEmpty(nsp) || seen.Contains(nsp))
                return;
            seen.Add(nsp);
            result.Add(MakeRow(nsp, null, false, false, null));
        }

        /// <summary>拼一行：名字/说明/作者优先取 mod 自己声明过的，再退到 <c>ModInfo</c>。</summary>
        private static ModRow MakeRow(string nsp, string infoDisplayName, bool registered, bool builtin,
            string logic)
        {
            var entry = UiOptions.GetModEntry(nsp);
            return new ModRow
            {
                Nsp = nsp,
                Name = DisplayNameOf(nsp, infoDisplayName),
                NameSource = DisplayNameSourceOf(nsp, infoDisplayName),
                DescriptionSource = entry == null ? null : entry.Description,
                AuthorSource = entry == null ? null : entry.Author,
                Builtin = builtin,
                Registered = registered,
                Logic = logic,
            };
        }

        /// <summary>
        /// 一个 mod 在列表里显示的名字。
        ///
        /// <para>顺序：① <c>UiOptions.SetModDisplayName</c> 声明过的（**已经按当前语言取过词**）
        /// → ② 官方 <c>ModInfo.DisplayName</c> 过一遍 <see cref="UiLocale"/>（mod 可以在语言包里
        /// 给它的原名配译文，也可以用 <c>UiLocale.Add</c>）→ ③ 命名空间。</para>
        /// </summary>
        private static string DisplayNameOf(string nsp, string infoDisplayName)
        {
            var declared = UiOptions.GetModDisplayName(nsp);
            if (!string.IsNullOrEmpty(declared))
                return declared;
            return UiLocale.Get(DisplayNameSourceOf(nsp, infoDisplayName));
        }

        /// <summary>显示名的**原文**（没取词的那一份；排障与「名称原文」行用）。</summary>
        private static string DisplayNameSourceOf(string nsp, string infoDisplayName)
        {
            var declared = UiOptions.GetModDisplayNameSource(nsp);
            if (!string.IsNullOrEmpty(declared))
                return declared;
            return string.IsNullOrEmpty(infoDisplayName) ? nsp : infoDisplayName;
        }

        private static string LogicName(ModInfo info)
        {
            try
            {
                var logic = info.Logic;
                return logic == null ? null : logic.GetType().Name;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // 详细信息 + 设置项
        // ------------------------------------------------------------------

        private void RefreshDetails()
        {
            if (_detailList == null || !_detailList.IsValid)
                return;
            _detailList.Clear();

            if (_selected < 0 || _selected >= _mods.Count)
            {
                SetDetailTitle(UiLocale.Get(DetailTitleText));
                AddLine("Empty", UiLocale.Get("没有已加载的模组。"));
                UiKit.Rebuild(_detailList.Root);
                return;
            }

            var m = _mods[_selected];
            SetDetailTitle(m.Name);

            // 顺序：说明 / 作者 / 来源 / 逻辑体 / 命名空间
            // （命名空间是给排查用的，放最后一行；前面四条是玩家关心的信息）
            AddInfo("说明", m.DescriptionSource ?? "（未声明）");
            AddInfo("作者", m.AuthorSource ?? "（未声明）");
            AddInfo("来源", m.Builtin ? "游戏内置"
                : m.Registered ? "外部 mod" : "外部 mod（无 ModInfo）");
            AddInfo("逻辑体", string.IsNullOrEmpty(m.Logic) ? "（无）" : m.Logic);
            // 命名空间是原始 id：**不能取词**（万一有人给同名串配了译文就会被换掉）。
            AddInfo("命名空间", m.Nsp, false);

            AddSeparator();

            if (m.Settings == 0)
            {
                AddLine("NoSettings", UiLocale.Get("该 mod 没有声明设置项。\n"
                    + "（用 UiOptions.DeclareToggle / DeclareSlider / DeclareChoice 在 Load() 里声明。）"));
                UiKit.Rebuild(_detailList.Root);
                return;
            }

            var decls = UiOptions.ForPage(m.Nsp);
            for (var i = 0; i < decls.Length; i++)
                AddSetting(decls[i]);

            UiKit.Rebuild(_detailList.Root);
        }

        private void SetListTitle(string text)
        {
            SetLabel(_listTitle, text);
        }

        private void SetDetailTitle(string text)
        {
            SetLabel(_detailTitle, text);
        }

        private static void SetLabel(TMPro.TextMeshProUGUI label, string text)
        {
            if (label == null)
                return;
            try { label.text = text; }
            catch (Exception) { }
        }

        private void AddLine(string name, string text)
        {
            var go = UiKit.Paragraph(null, "Line_" + name, text, UiKit.LabelFontSize, 1f);
            if (UiFinder.IsAlive(go))
                _detailList.Add(go.gameObject);
        }

        /// <summary>
        /// 在右栏里插一条**原版分割线**（<see cref="UiKit.Separator"/> = 原版设置界面分类标题下面那条，
        /// 2 单位高的纯色 Image）。拿不到素材时退回**一行空白**（只为留出间距）——
        /// **不要**用文字画分割线。
        /// </summary>
        private void AddSeparator()
        {
            var sep = UiKit.Separator(null, "DSH_ModSettingsSep");
            if (UiFinder.IsAlive(sep))
            {
                _detailList.Add(sep);
                return;
            }
            AddLine("Sep", " ");
        }

        /// <param name="localizeValue">
        /// 值要不要过一遍本地化。**原始 id / 类名这类本身**（例如「命名空间」「逻辑体」）要传 <c>false</c>，
        /// 否则可能被同名译文替换掉。
        /// </param>
        private void AddInfo(string key, string value, bool localizeValue = true)
        {
            // key 与 value 都过一遍本地化：key 是页面自己的词条，
            // value 里可能是 mod 自己声明的说明 / 来源说明。
            var row = UiKit.HContainer(null, "Info_" + key);
            if (!UiFinder.IsAlive(row))
            {
                AddLine(key, UiLocale.Get(key) + "：" + value);
                return;
            }

            // 宽度铺满；**高度不设**（信息行只占一行文字的高度，比原版控件行矮一大截，
            // 条目之间的间距因此自然变紧）。
            UiKit.Fit(row, -1f, -1f, 1f);
            UiKit.AutoHeight(row);

            UiKit.Label(row.transform, "Key", UiLocale.Get(key), UiKit.LabelFontSize,
                0f, 0.4f, TMPro.TextAlignmentOptions.Left, 0f);

            // 值用 Paragraph（可换行 + 高度由 TMP 自己算）：
            // 长说明 / 长类名宁可换行，也不要被省略号截成「外部 mod（无 ModInfo…」。
            var text = value ?? "（无）";
            UiKit.Paragraph(row.transform, "Value", localizeValue ? UiLocale.Get(text) : text,
                UiKit.LabelFontSize, 1f);

            _detailList.Add(row);
        }

        /// <summary>
        /// 把一个声明渲染成一行。
        ///
        /// <para>控件一律用官方的四合一行素材（<see cref="UiOptionRow"/>）——
        /// 它与原版设置界面里那一行长的一模一样（字号、间距、悬停提示锚点全是 prefab 自带的）。</para>
        /// </summary>
        private void AddSetting(UiOptions.Declaration d)
        {
            if (d == null)
                return;

            // 设置项的文案是**声明方给的源串**，这里按当前语言取词 ——
            // mod 既可以给源串配译文（UiLocale.Add / 语言包），也可以直接写中文（源语言就是中文）。
            var label = UiLocale.Get(d.Text);
            var tooltip = UiLocale.Get(d.Description);

            if (!d.CanEdit)
            {
                AddLine("RO_" + d.Name, label + "：" + d.CurrentValueText + "（只读：声明时没给回调）");
                return;
            }

            var row = _detailList.AddOptionRow("Setting_" + d.Name, d.PageKind);
            if (row == null)
            {
                AddLine("FAIL_" + d.Name, label + "（设置行素材拿不到）");
                return;
            }

            switch (d.PageKind)
            {
                case UiOptionKind.Slider:
                    row.SetSlider(label, d.Min, d.Max, d.Whole, d.CurrentFloat,
                        null,                                   // 拖动过程不写值
                        v => d.SetValue(v),                     // 松手才提交
                        v => { try { return d.Format != null ? d.Format(v) : null; } catch (Exception) { return null; } });
                    break;

                case UiOptionKind.Button:
                    // 多值项：按钮上写「标签：当前值」，点一下切到下一个值。
                    var current = d;
                    var target = row;
                    Action sync = () =>
                    {
                        try { target.SetLabel(label + "：" + UiLocale.Get(current.CurrentItemText)); }
                        catch (Exception) { }
                    };
                    sync();
                    row.SetButton(label + "：" + UiLocale.Get(current.CurrentItemText), () =>
                    {
                        if (current.Cycle())
                            sync();
                    });
                    break;

                default:
                    row.SetToggle(label, d.CurrentBool, v => d.SetValue(v));
                    break;
            }

            row.SetTooltip(label, tooltip);
        }

        // ------------------------------------------------------------------
        // 验收
        // ------------------------------------------------------------------

        /// <summary>
        /// 选中指定命名空间的 mod（验收 / 截图用）。返回是否找到。
        /// </summary>
        public bool SelectNamespace(string nsp)
        {
            if (string.IsNullOrEmpty(nsp) || _mods == null)
                return false;
            for (var i = 0; i < _mods.Count; i++)
            {
                if (_mods[i].Nsp == nsp)
                {
                    if (i == _selected)
                    {
                        RefreshDetails();
                        return true;
                    }
                    Select(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>当前选中的命名空间（验收用）。</summary>
        public string SelectedNamespace
        {
            get
            {
                return (_selected >= 0 && _mods != null && _selected < _mods.Count)
                    ? _mods[_selected].Nsp : null;
            }
        }

        /// <summary>一行栏目标题的描述（文案 / 字号 / 对齐 / 加粗）。</summary>
        private static string DescribeHeader(TMPro.TextMeshProUGUI label)
        {
            if (label == null)
                return "（无）";
            return "\"" + UiKit.Trim(label.text, 24) + "\"@" + Mathf.RoundToInt(label.fontSize)
                + " 对齐=" + label.alignment + " 加粗=" + (UiKit.IsBold(label) ? 1 : 0);
        }

        /// <summary>把本页的真实布局与内容写进报告（自测用）。</summary>
        public string DumpLayout()
        {
            var sb = new StringBuilder(600);
            sb.Append(DescribeEntry());
            if (Shell != null)
                sb.Append(Shell.Describe());

            sb.Append("两栏总宽 : ")
              .Append(UiKit.Describe(Shell == null ? null : Shell.Node("Center"))).Append('\n')
              .Append("  左栏   : ").Append(UiKit.Describe(Shell == null ? null : Shell.Node("Center/Filter"))).Append('\n')
              .Append("  右栏   : ").Append(UiKit.Describe(Shell == null ? null : Shell.Node("Center/List"))).Append('\n')
              .Append("  内边距 : 左=").Append(UiKit.GetPadding(Shell == null ? null : Shell.Node("Center/Filter/Contents")))
              .Append("  右=").Append(UiKit.GetPadding(Shell == null ? null : Shell.Node("Center/List/Contents")))
              .Append(" 单位（PanelPadding=").Append(PanelPadding).Append("）\n")
              .Append("  左栏标题 : ")
              .Append(DescribeHeader(_listTitle)).Append('\n');

            sb.Append("模组列表 : ").Append(UiKit.Describe(_modList == null ? null : _modList.Root)).Append('\n');
            if (_modList != null)
                sb.Append(_modList.DescribeStructure()).Append('\n');
            sb.Append("  行样式 : ").Append(_rowFallback ? "**原生按钮行（档案条目素材拿不到）**" : "档案条目行（Toggle + 文字）")
              .Append("  条目模板=").Append(UiTagRow.Template() == null ? "无" : "有").Append('\n');
            sb.Append("  模组数 : ").Append(_mods.Count).Append("  当前选中=").Append(_selected).Append('\n');
            for (var i = 0; i < _mods.Count; i++)
            {
                var m = _mods[i];
                sb.Append("    ").Append(i).Append(" : ").Append(m.Nsp)
                  .Append("  名字=\"").Append(UiKit.Trim(m.Name, 20)).Append('"');
                if (!string.IsNullOrEmpty(m.NameSource) && m.NameSource != m.Name)
                    sb.Append("  原文=\"").Append(UiKit.Trim(m.NameSource, 20)).Append('"');
                sb.Append("  说明=\"").Append(UiKit.Trim(m.DescriptionSource, 16)).Append('"')
                  .Append("  作者=\"").Append(UiKit.Trim(m.AuthorSource, 12)).Append('"')
                  .Append("  内置=").Append(m.Builtin ? 1 : 0)
                  .Append("  ModInfo=").Append(m.Registered ? 1 : 0)
                  .Append("  设置项=").Append(m.Settings)
                  .Append('\n');
                if (i < _modRows.Count && UiFinder.IsAlive(_modRows[i]))
                    sb.Append("      行 : ").Append(_rowFallback
                        ? UiKit.Describe(_modRows[i])
                        : UiTagRow.Describe(_modRows[i])).Append('\n');
            }

            sb.Append("详情列表 : ").Append(UiKit.Describe(_detailList == null ? null : _detailList.Root)).Append('\n');
            if (_detailList != null)
                sb.Append(_detailList.DescribeStructure()).Append('\n');
            sb.Append("  右栏标题 : ").Append(DescribeHeader(_detailTitle)).Append('\n');

            var content = _detailList == null ? null : _detailList.Content;
            if (content != null)
            {
                sb.Append("  行数 : ").Append(content.childCount).Append('\n');
                var n = Math.Min(content.childCount, 16);
                for (var i = 0; i < n; i++)
                {
                    var row = content.GetChild(i);
                    if (row == null)
                        continue;
                    sb.Append("    行").Append(i).Append(" : ").Append(UiKit.Describe(row.gameObject)).Append('\n')
                      .Append("      ").Append(UiKit.DescribeTexts(row.gameObject)).Append('\n');
                }
            }

            sb.Append(UiLocale.Report()).Append('\n');
            sb.Append("屏幕     : ").Append(Screen.width).Append('x').Append(Screen.height).Append('\n');
            return sb.ToString();
        }
    }
}
