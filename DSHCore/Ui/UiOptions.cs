using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime.Injection;
using MVZ2.Managers;
using MVZ2Logic.Commands;
using MVZ2Logic.Modding;
using MVZ2Logic.Options;
using PVZEngine;

namespace DSHCore.Ui
{
    /// <summary>
    /// 一条设置项**注册到哪里**。
    ///
    /// <para><b>为什么默认是 <see cref="ModPage"/>：</b>一个 mod 的设置项是它**自己的**东西，
    /// 丢进原版「设置 → 更多选项」的通用分类里会就地稀释原版项，
    /// 而且要求每个 mod 都得注入一个 <c>ModInfo</c>（连带一串注入类坑）。
    /// 现在默认进本库自己的**「模组设置」页**（插件页 → 模组设置），
    /// 零注入、不需要 XML、不需要 Mod 实例。</para>
    ///
    /// <para><see cref="VanillaMenu"/> 是给「就想让这一项出现在原版设置界面里」的人留的
    /// —— 它会走一整套注入链（<c>ModInfo</c> + 注入定义 + <c>options.xml</c> + 三时机），
    /// 代价见 <see cref="UiOptions.Attach"/> 的注释。</para>
    /// </summary>
    public enum UiOptionScope
    {
        /// <summary>只进本库的「模组设置」页（**默认**）。</summary>
        ModPage = 0,

        /// <summary>只进原版设置界面（需要注入 <c>ModInfo</c> 与 <c>options.xml</c>）。</summary>
        VanillaMenu = 1,

        /// <summary>两边都出现。</summary>
        Both = 2,
    }

    /// <summary>
    /// **设置项注册：默认进本库的「模组设置」页，而不是占用原版设置菜单。**
    ///
    /// <para><b>默认路径（零注入，推荐）：</b>只要在插件 <c>Load()</c> 里声明一句就够了：</para>
    /// <code>
    /// UiOptions.DeclareToggle("my_mod", "my_toggle", label: "我的开关", tooltip: "说明文字",
    ///     getter: () => _value, setter: v => _value = v);
    /// </code>
    /// <para>它会在「插件页 → 模组设置（DSHCore）」里变成一个原生观感的设置行
    /// （复选框 / 滑条 / 选择），按命名空间归到对应 mod 名下。
    /// 不需要 <c>ModInfo</c>、不需要注入定义类、不需要 <c>options.xml</c>、不需要三时机 ——
    /// 值的存取完全在你自己手里（BepInEx 配置、全局选项、静态字段都行）。</para>
    ///
    /// <para><b>另一种路径（<see cref="UiOptionScope.VanillaMenu"/>）：</b>让这一项出现在**原版设置界面**。
    /// 那条路要完整走一遍官方链路：</para>
    /// <list type="number">
    /// <item><b>声明</b>可以在插件 <c>Load()</c> 做，纯托管（并指定 <c>scope</c>）；</item>
    /// <item><b>创建 + <c>Mod.AddDefinition</c></b> 必须落在官方 <c>ModLoader.LoadStages</c> 时机
    /// （<c>LoadDefinitions</c> 之内、<c>game.AddMod</c> 之前），否则定义不进全局表；</item>
    /// <item><b>写属性（<c>CATEGORY_ID</c>/<c>LABEL</c>/<c>ORDER</c>…）必须等到 <c>PostInit</c>。</b>
    /// 在 <c>LoadStages</c> 就写会**被静默丢弃**（属性键那时还没就绪）——
    /// 症状极具迷惑性：<c>SetProperty</c> 一声不吭、"属性已应用"日志照打，
    /// 但官方渲染时 <c>GetCategoryID()</c> 读回 null，于是被
    /// <c>!NamespaceID.IsValid(categoryID)</c> 静默过滤掉，界面上什么都不多。</item>
    /// <item>还得有一份 <c>Content/metas/options.xml</c>（文案/分类/顺序在那里），
    /// 以及一个真的注册到游戏里的 <c>Mod</c> 实例（没有 Mod 就没有 <c>Mod.AddDefinition</c>）。</item>
    /// </list>
    ///
    /// <para>两种路径可以共用一条声明（<c>scope: Both</c>）。</para>
    ///
    /// <para><b>值持久化</b>：本封装把值交给你的 setter，怎么存由你决定。
    /// 最省事的是游戏自己的全局选项：<c>Global.Options.SetOptionBool(id, v)</c> +
    /// <c>SaveOptions()</c>（键用**自己命名空间**下的 NamespaceID）。</para>
    /// </summary>
    public static class UiOptions
    {
        /// <summary>一条设置项的声明。</summary>
        public sealed class Declaration
        {
            internal string Space;
            internal string Id;
            internal string Category;
            internal string Label;
            internal string Tooltip;
            internal int Order;

            /// <summary>这一条要注册到哪里（默认只进「模组设置」页）。</summary>
            internal UiOptionScope ScopeValue = UiOptionScope.ModPage;

            internal bool IsSlider;
            internal bool IsDropdown;
            internal Func<bool> GetBool;
            internal Action<bool> SetBool;
            internal Func<float> GetFloat;
            internal Action<float> SetFloat;
            internal Func<float, string> Format;
            internal float Min;
            internal float Max;
            internal bool Whole;
            internal Func<int> GetIndex;
            internal Action<int> SetIndex;

            /// <summary>选项文案（托管数组版，优先用）。</summary>
            internal Func<string[]> GetItems;

            /// <summary>选项文案（原生委托版，兼容官方 <c>FillItems</c> 形态的老声明）。</summary>
            internal Action<Il2CppSystem.Collections.Generic.List<string>> FillItems;

            /// <summary>被创建出来的注入定义（<see cref="Attach"/> 之后才有值）。</summary>
            internal OptionWidgetDefinition Definition;

            /// <summary>完整命名空间 ID（<c>space:id</c>）。</summary>
            public string FullID
            {
                get { return Space + ":" + Id; }
            }

            /// <summary>命名空间（哪个 mod 的）。</summary>
            public string SpaceName
            {
                get { return Space; }
            }

            /// <summary>裸 id（同 mod 内唯一）。</summary>
            public string Name
            {
                get { return Id; }
            }

            /// <summary>显示文案。</summary>
            public string Text
            {
                get { return Label ?? string.Empty; }
            }

            /// <summary>悬停说明。</summary>
            public string Description
            {
                get { return Tooltip ?? string.Empty; }
            }

            /// <summary>排序权重（小的在前）。</summary>
            public int SortOrder
            {
                get { return Order; }
            }

            /// <summary>注册目标。</summary>
            public UiOptionScope Scope
            {
                get { return ScopeValue; }
            }

            /// <summary>是否要在「模组设置」页里出现。</summary>
            public bool OnModPage
            {
                get { return ScopeValue != UiOptionScope.VanillaMenu; }
            }

            /// <summary>是否要在原版设置界面里出现。</summary>
            public bool OnVanillaMenu
            {
                get { return ScopeValue != UiOptionScope.ModPage; }
            }

            /// <summary>用哪种控件渲染（模组设置页用）。</summary>
            public UiOptionKind PageKind
            {
                get
                {
                    if (IsSlider)
                        return UiOptionKind.Slider;
                    // 多值项在模组设置页渲染成「点一下切到下一个值」的按钮行 ——
                    // 原因见 ModSettingsPage 的注释（原版下拉框的展开列表在本环境画不出来）。
                    if (IsDropdown)
                        return UiOptionKind.Button;
                    return UiOptionKind.Toggle;
                }
            }

            /// <summary>能否读写值（只有声明、没给回调时为 false，页面会渲染成只读行）。</summary>
            public bool CanEdit
            {
                get
                {
                    if (IsSlider)
                        return GetFloat != null;
                    if (IsDropdown)
                        return GetIndex != null && SetIndex != null;
                    return GetBool != null && SetBool != null;
                }
            }

            /// <summary>被创建出来的注入定义（<see cref="Attach"/> 之后才有值）。</summary>
            public bool IsAttached
            {
                get { return Definition != null; }
            }

            /// <summary>滑条最小值（非滑条时为 0）。</summary>
            public float SliderMin
            {
                get { return Min; }
            }

            /// <summary>滑条最大值（非滑条时为 0）。</summary>
            public float SliderMax
            {
                get { return Max; }
            }

            /// <summary>滑条是否只取整数。</summary>
            public bool SliderWholeNumbers
            {
                get { return Whole; }
            }

            /// <summary>当前值（复选框）。</summary>
            public bool CurrentBool
            {
                get
                {
                    try { return GetBool != null && GetBool(); }
                    catch (Exception) { return false; }
                }
            }

            /// <summary>当前值（滑条）。</summary>
            public float CurrentFloat
            {
                get
                {
                    try { return GetFloat != null ? GetFloat() : 0f; }
                    catch (Exception) { return 0f; }
                }
            }

            /// <summary>当前选项下标（多值项）。</summary>
            public int CurrentIndex
            {
                get
                {
                    try { return GetIndex != null ? GetIndex() : 0; }
                    catch (Exception) { return 0; }
                }
            }

            /// <summary>
            /// 选项文案。优先用托管数组；只有老式的 <c>FillItems</c> 时才去建一个原生 list 现填。
            /// 两者都拿不到时返回空数组。
            /// </summary>
            public string[] Items
            {
                get
                {
                    try
                    {
                        if (GetItems != null)
                        {
                            var arr = GetItems();
                            return arr ?? new string[0];
                        }
                        if (FillItems != null)
                        {
                            var list = new Il2CppSystem.Collections.Generic.List<string>();
                            FillItems(list);
                            var n = list.Count;
                            var result = new string[n];
                            for (var i = 0; i < n; i++)
                                result[i] = list[i];
                            return result;
                        }
                    }
                    catch (Exception e)
                    {
                        LogWarn("读取 " + FullID + " 的选项文案失败：" + e.GetType().Name);
                    }
                    return new string[0];
                }
            }

            /// <summary>当前选项的显示文案（拿不到就返回空串）。</summary>
            public string CurrentItemText
            {
                get
                {
                    var items = Items;
                    if (items.Length == 0)
                        return string.Empty;
                    var i = CurrentIndex;
                    if (i < 0 || i >= items.Length)
                        i = 0;
                    return items[i] ?? string.Empty;
                }
            }

            /// <summary>当前值 → 文本（滑条用声明里的 format，没给就用默认）。</summary>
            public string CurrentValueText
            {
                get
                {
                    var v = CurrentFloat;
                    try
                    {
                        if (Format != null)
                        {
                            var s = Format(v);
                            if (s != null)
                                return s;
                        }
                    }
                    catch (Exception) { }
                    return Whole ? Math.Round(v).ToString() : v.ToString("0.###");
                }
            }

            /// <summary>把值改成 <paramref name="value"/>（只对有 setter 的项有效）。</summary>
            public void SetValue(bool value)
            {
                try { if (SetBool != null) SetBool(value); }
                catch (Exception e) { LogWarn("写入 " + FullID + " 失败：" + e.GetType().Name); }
            }

            /// <summary>把值改成 <paramref name="value"/>（只对有 setter 的项有效）。</summary>
            public void SetValue(float value)
            {
                try { if (SetFloat != null) SetFloat(value); }
                catch (Exception e) { LogWarn("写入 " + FullID + " 失败：" + e.GetType().Name); }
            }

            /// <summary>切到下一个选项（循环）。返回是否真的改了。</summary>
            public bool Cycle()
            {
                var items = Items;
                if (items.Length == 0 || SetIndex == null)
                    return false;
                var next = (CurrentIndex + 1) % items.Length;
                try
                {
                    SetIndex(next);
                    return true;
                }
                catch (Exception e)
                {
                    LogWarn("切换 " + FullID + " 失败：" + e.GetType().Name);
                    return false;
                }
            }
        }

        private static readonly List<Declaration> Declarations = new List<Declaration>();
        private static readonly Dictionary<string, Declaration> ById = new Dictionary<string, Declaration>();

        // ------------------------------------------------------------------
        // ① 声明
        // ------------------------------------------------------------------

        /// <summary>
        /// 声明一个复选框设置项（默认进「模组设置」页）。
        /// </summary>
        /// <param name="category">
        /// 原版设置界面里的分类（<c>general</c> / <c>graphics</c> / …）。
        /// **只有在 <paramref name="scope"/> 含原版菜单时才用得到**（见 <see cref="UiOptionScope"/>）。
        /// </param>
        public static Declaration DeclareToggle(string spaceName, string id, string category,
            string label, string tooltip = null, int order = 0,
            Func<bool> getter = null, Action<bool> setter = null,
            UiOptionScope scope = UiOptionScope.ModPage)
        {
            return Declare(new Declaration
            {
                Space = spaceName,
                Id = id,
                Category = category,
                Label = label,
                Tooltip = tooltip,
                Order = order,
                ScopeValue = scope,
                GetBool = getter,
                SetBool = setter,
            });
        }

        /// <summary>声明一个滑条设置项（默认进「模组设置」页）。</summary>
        public static Declaration DeclareSlider(string spaceName, string id, string category,
            string label, float min, float max, bool wholeNumbers,
            string tooltip = null, int order = 0,
            Func<float> getter = null, Action<float> setter = null,
            Func<float, string> format = null,
            UiOptionScope scope = UiOptionScope.ModPage)
        {
            return Declare(new Declaration
            {
                Space = spaceName,
                Id = id,
                Category = category,
                Label = label,
                Tooltip = tooltip,
                Order = order,
                ScopeValue = scope,
                IsSlider = true,
                GetFloat = getter,
                SetFloat = setter,
                Format = format,
                Min = min,
                Max = max,
                Whole = wholeNumbers,
            });
        }

        /// <summary>
        /// 声明一个**多值选项**（默认进「模组设置」页）。
        ///
        /// <para>选项文案直接给一个托管数组（或一个现算的函数）—— 这是推荐写法：
        /// 不需要构造 il2cpp 的 <c>List&lt;string&gt;</c>，也不需要写进 XML。</para>
        ///
        /// <para>在「模组设置」页里它渲染成**按钮行**（「标签：当前值」，点一下切到下一个值）；
        /// 在原版设置界面里它是官方的下拉框。前者是无奈之举，原因见
        /// <see cref="ModSettingsPage"/> 的类注释（原版下拉框的展开列表在本环境里画不出来）。</para>
        /// </summary>
        public static Declaration DeclareChoice(string spaceName, string id, string category,
            string label, string[] items, string tooltip = null, int order = 0,
            Func<int> getter = null, Action<int> setter = null,
            UiOptionScope scope = UiOptionScope.ModPage)
        {
            return Declare(new Declaration
            {
                Space = spaceName,
                Id = id,
                Category = category,
                Label = label,
                Tooltip = tooltip,
                Order = order,
                ScopeValue = scope,
                IsDropdown = true,
                GetIndex = getter,
                SetIndex = setter,
                GetItems = () => items,
            });
        }

        /// <summary>
        /// 声明一个**下拉框**设置项。
        ///
        /// <para>选项文案在 <paramref name="fillItems"/> 里现填 —— 与官方
        /// <c>OptionDropdownDefinition.FillItems</c> 一致。
        /// ⚠ 形参类型必须是 <c>Il2CppSystem.Collections.Generic.List&lt;string&gt;</c>；
        /// 托管 <c>List&lt;string&gt;</c> 对不上（编译期就报）。
        /// 项目文案固定时请用 <see cref="DeclareChoice"/>（不用建原生 list）。</para>
        ///
        /// <para>在「模组设置」页里它读不到文案时会渲染成只读行（不会报错）。</para>
        /// </summary>
        public static Declaration DeclareDropdown(string spaceName, string id, string category,
            string label, string tooltip = null, int order = 0,
            Func<int> getter = null, Action<int> setter = null,
            Action<Il2CppSystem.Collections.Generic.List<string>> fillItems = null,
            UiOptionScope scope = UiOptionScope.ModPage)
        {
            return Declare(new Declaration
            {
                Space = spaceName,
                Id = id,
                Category = category,
                Label = label,
                Tooltip = tooltip,
                Order = order,
                ScopeValue = scope,
                IsDropdown = true,
                GetIndex = getter,
                SetIndex = setter,
                FillItems = fillItems,
            });
        }

        private static Declaration Declare(Declaration d)
        {
            if (d == null || string.IsNullOrEmpty(d.Space) || string.IsNullOrEmpty(d.Id))
            {
                LogWarn("声明无效（space/id 不能为空）");
                return null;
            }
            var key = d.FullID;
            if (ById.ContainsKey(key))
            {
                LogWarn("重复声明 " + key + "，忽略后一个");
                return ById[key];
            }

            // 只有要走原版设置界面的声明才需要定义设施（那里才有 Definition）。
            if (d.OnVanillaMenu)
            {
                InjectedDefinitions.Apply();
                InjectedDefinitions.Register(d.Space);
            }

            Declarations.Add(d);
            ById[key] = d;
            return d;
        }

        /// <summary>已经声明、且属于这个命名空间的设置项（含全部 scope），按声明顺序。</summary>
        public static Declaration[] For(string spaceName)
        {
            var list = new List<Declaration>();
            for (var i = 0; i < Declarations.Count; i++)
            {
                if (Declarations[i].Space == spaceName)
                    list.Add(Declarations[i]);
            }
            return list.ToArray();
        }

        /// <summary>
        /// 声明本 mod 在**「模组设置」页**里显示的名字（可选）。
        ///
        /// <para>不声明时会退到官方 <c>ModInfo.DisplayName</c>，再退到命名空间。
        /// 走默认（零注入）路径的 mod 没有 <c>ModInfo</c>，所以最好声明一句 ——
        /// 否则列表里显示的是 <c>loadout</c> 这种裸命名空间。</para>
        ///
        /// <para><b>名字是「可翻译的源串」</b>：列表里显示的其实是
        /// <c>UiLocale.Get(displayName)</c>，所以中文名有两种给法 ——</para>
        /// <list type="bullet">
        /// <item>省事：<c>SetModDisplayName("loadout", "MVZ2 Loadout", "图形化控制台")</c>
        /// （第三个参数 = 中文译文，等价于再写一句 <see cref="UiLocale.Add(string, string, string)"/>）；</item>
        /// <item>正规：只声明源串，译文交给语言包（<c>assets/&lt;nsp&gt;/&lt;lang&gt;/*.mo</c>）
        /// 或 <see cref="UiLocale.Add"/>。见 <see cref="UiLocale"/> 的注释。</item>
        /// </list>
        ///
        /// <para><b>建议写短一点</b>：它在左栏里是一行的文案，太长会被原版的自动缩字号压得
        /// 比邻居小一号。版本号、书名号里的中文全名这类信息留给右栏的「显示名」那一行。</para>
        /// </summary>
        public static void SetModDisplayName(string spaceName, string displayName, string chinese = null)
        {
            DeclareMod(spaceName, displayName, null, null, chinese, true);
        }

        /// <summary>
        /// **完整地介绍一个 mod**：显示名 + 一句说明 + 作者（都可翻译）。
        ///
        /// <para>与 <see cref="SetModDisplayName"/> 的区别只有「多两个字段」，但**效果上一个样**：
        /// 声明过的命名空间会**无条件出现在「模组设置」页的左栏**里 ——
        /// 哪怕它既没注册 <c>ModInfo</c>、也没声明任何设置项
        /// （`Replay` 就是这种：纯补丁 mod，没有任何游戏内容，但玩家应该能在列表里看到它）。</para>
        ///
        /// <para><b>三个文本都是「可翻译的源串」</b>（见 <see cref="UiLocale"/>），
        /// <paramref name="chinese"/> 只给显示名补中文；说明与作者要另配译文的话写
        /// <c>UiLocale.Add</c> 或放进语言包。</para>
        /// </summary>
        /// <param name="spaceName">命名空间（没有游戏内容的 mod 可以自定一个稳定标识，例如 <c>replay</c>）。</param>
        /// <param name="displayName">显示名（源串；列表里显示 <c>UiLocale.Get(它)</c>）。</param>
        /// <param name="description">一句话说明（选填）。</param>
        /// <param name="author">作者（选填）。</param>
        /// <param name="chinese"><paramref name="displayName"/> 的中文译文（选填）。</param>
        public static void DeclareMod(string spaceName, string displayName, string description = null,
            string author = null, string chinese = null)
        {
            DeclareMod(spaceName, displayName, description, author, chinese, false);
        }

        private static void DeclareMod(string spaceName, string displayName, string description,
            string author, string chinese, bool keepExistingInfo)
        {
            if (string.IsNullOrEmpty(spaceName) || string.IsNullOrEmpty(displayName))
                return;

            ModEntry entry;
            if (!ModEntries.TryGetValue(spaceName, out entry))
            {
                entry = new ModEntry { Space = spaceName };
                ModEntries[spaceName] = entry;
            }

            entry.DisplayName = displayName;
            if (!keepExistingInfo)
            {
                if (!string.IsNullOrEmpty(description))
                    entry.Description = description;
                if (!string.IsNullOrEmpty(author))
                    entry.Author = author;
            }
            if (!string.IsNullOrEmpty(chinese))
                UiLocale.Add(displayName, chinese, UiLocale.Chinese);
        }

        /// <summary>一个 mod 的介绍（都是**源串**，取词请用 <see cref="GetModDisplayName"/> 等）。</summary>
        public sealed class ModEntry
        {
            /// <summary>命名空间。</summary>
            public string Space;

            /// <summary>显示名（源串）。</summary>
            public string DisplayName;

            /// <summary>一句话说明（源串，可能为 null）。</summary>
            public string Description;

            /// <summary>作者（源串，可能为 null）。</summary>
            public string Author;
        }

        /// <summary>取声明过的 mod 介绍（没声明返回 null）。</summary>
        public static ModEntry GetModEntry(string spaceName)
        {
            ModEntry entry;
            return (spaceName != null && ModEntries.TryGetValue(spaceName, out entry)) ? entry : null;
        }

        /// <summary>
        /// 取声明过的显示名（没声明返回 null）。
        ///
        /// <para>返回值已经过 <see cref="UiLocale"/>（当前语言）—— 也就是说这个页面
        /// 每次打开都会按**当时**的语言重新取一遍词，玩家切语言后重进本页即可看到新语言。</para>
        /// </summary>
        public static string GetModDisplayName(string spaceName)
        {
            var entry = GetModEntry(spaceName);
            return entry == null ? null : UiLocale.Get(entry.DisplayName);
        }

        /// <summary>取声明过的显示名**原文**（不翻译；排障用）。</summary>
        public static string GetModDisplayNameSource(string spaceName)
        {
            var entry = GetModEntry(spaceName);
            return entry == null ? null : entry.DisplayName;
        }

        /// <summary>取说明（当前语言）。没声明过「说明」时返回 null。</summary>
        public static string GetModDescription(string spaceName)
        {
            var entry = GetModEntry(spaceName);
            return (entry == null || string.IsNullOrEmpty(entry.Description))
                ? null : UiLocale.Get(entry.Description);
        }

        /// <summary>取作者（当前语言）。没声明过「作者」时返回 null。</summary>
        public static string GetModAuthor(string spaceName)
        {
            var entry = GetModEntry(spaceName);
            return (entry == null || string.IsNullOrEmpty(entry.Author))
                ? null : UiLocale.Get(entry.Author);
        }

        /// <summary>
        /// 已经**介绍过自己**的全部命名空间（见 <see cref="DeclareMod"/>）。
        /// 模组设置页用它把「没注册 <c>ModInfo</c>、也没有设置项」的 mod 也列出来。
        /// </summary>
        public static string[] DeclaredSpaces()
        {
            var list = new List<string>();
            foreach (var pair in ModEntries)
            {
                if (!string.IsNullOrEmpty(pair.Key))
                    list.Add(pair.Key);
            }
            return list.ToArray();
        }

        private static readonly Dictionary<string, ModEntry> ModEntries
            = new Dictionary<string, ModEntry>(StringComparer.Ordinal);

        /// <summary>
        /// 已经声明、且在**「模组设置」页**里出现的设置项（按 <c>Order</c> 升序）。
        /// 这是模组设置页的取数入口。
        /// </summary>
        public static Declaration[] ForPage(string spaceName)        {
            var list = new List<Declaration>();
            for (var i = 0; i < Declarations.Count; i++)
            {
                var d = Declarations[i];
                if (d.Space == spaceName && d.OnModPage)
                    list.Add(d);
            }
            list.Sort((a, b) => a.Order.CompareTo(b.Order));
            return list.ToArray();
        }

        /// <summary>
        /// 已经声明过设置项的**全部命名空间**（含没有注册 <c>ModInfo</c> 的那些）。
        /// 模组设置页用它把「只有设置项、没注册 ModInfo」的 mod 也列出来。
        /// </summary>
        public static string[] SpacesWithSettings()
        {
            var list = new List<string>();
            for (var i = 0; i < Declarations.Count; i++)
            {
                var d = Declarations[i];
                if (!d.OnModPage || string.IsNullOrEmpty(d.Space))
                    continue;
                if (!list.Contains(d.Space))
                    list.Add(d.Space);
            }
            return list.ToArray();
        }

        /// <summary>已声明的全部设置项（诊断用）。</summary>
        public static Declaration[] All()
        {
            return Declarations.ToArray();
        }

        /// <summary>按 <c>space:id</c> 或裸 id 查一条声明（渲染时回调按它查）。</summary>
        internal static Declaration Lookup(string idOrFullId)
        {
            if (string.IsNullOrEmpty(idOrFullId))
                return null;
            Declaration d;
            if (ById.TryGetValue(idOrFullId, out d))
                return d;
            // 来自 GetID().Path 时是裸 id：按后缀找（同 mod 内 id 唯一）
            for (var i = 0; i < Declarations.Count; i++)
            {
                if (Declarations[i].Id == idOrFullId)
                    return Declarations[i];
            }
            return null;
        }

        // ------------------------------------------------------------------
        // ② 登记（LoadStages 时机）
        // ------------------------------------------------------------------

        /// <summary>
        /// 为 <paramref name="mod"/> 的命名空间创建**声明里指定了原版设置界面的**设置项并
        /// <c>AddDefinition</c>。
        ///
        /// <para>一般在 mod 自己的 <c>ModLoader.LoadStages</c> prefix 里调用（幂等）。
        /// scope 为默认值 <see cref="UiOptionScope.ModPage"/> 的声明**不会**经过这里
        /// —— 它们只进「模组设置」页，不需要 Mod 实例与 XML。</para>
        /// </summary>
        public static int Attach(Mod mod)
        {
            if (mod == null)
                return 0;

            EnsureTypesRegistered();

            // 注入实例的 Namespace 字段可能读到空/悬垂值（原因见 ModNamespacePin），
            // 所以先用指针判据（InjectedDefinitions.NamespaceOf），再退回字符串。
            string nsp = null;
            try { nsp = InjectedDefinitions.NamespaceOf(mod); }
            catch (Exception) { }
            if (string.IsNullOrEmpty(nsp))
            {
                try { nsp = mod.Namespace; }
                catch (Exception) { }
            }
            if (string.IsNullOrEmpty(nsp))
            {
                LogWarn("Attach 时读不到 mod.Namespace，跳过");
                return 0;
            }
            // 顺手把该字段钉住：登记之后游戏自己还要读 mod.Namespace 找内容表。
            ModNamespacePin.Ensure(mod, nsp);

            InjectedDefinitions.Apply();
            InjectedDefinitions.Register(nsp);
            InjectedDefinitions.Bind(nsp, mod);

            var mine = For(nsp);
            var created = 0;
            for (var i = 0; i < mine.Length; i++)
            {
                var d = mine[i];
                if (!d.OnVanillaMenu)          // 只进模组设置页的声明不走注入链
                    continue;
                if (d.IsAttached)
                    continue;
                try
                {
                    OptionWidgetDefinition def;
                    if (d.IsSlider)
                        def = (OptionWidgetDefinition)InjectedDefinitions.Create<UiSliderDefinition>(d.Space, d.Id);
                    else if (d.IsDropdown)
                        def = (OptionWidgetDefinition)InjectedDefinitions.Create<UiDropdownDefinition>(d.Space, d.Id);
                    else
                        def = (OptionWidgetDefinition)InjectedDefinitions.Create<UiToggleDefinition>(d.Space, d.Id);
                    if (def == null)
                        continue;
                    d.Definition = def;
                    mod.AddDefinition(def);
                    created++;
                }
                catch (Exception e)
                {
                    LogWarn("创建设置项 " + d.FullID + " 失败：" + e.GetType().Name + ": " + e.Message);
                }
            }

            LogInfo("Attach " + nsp + "：" + created + " 条设置项已登记"
                + "（属性等 PostInit 的 ApplyProperties 再写）");
            return created;
        }

        // ------------------------------------------------------------------
        // ③ 写属性（PostInit 时机）
        // ------------------------------------------------------------------

        /// <summary>
        /// 把 <c>options.xml</c> 里的文案/分类/顺序写进已登记的定义。
        /// **必须在属性键就绪之后调用**（一般在 <c>Mod.PostInit</c> 里）。
        /// </summary>
        public static int ApplyProperties()
        {
            var res = MainManager.Instance?.ResourceManager;
            var applied = 0;

            for (var i = 0; i < Declarations.Count; i++)
            {
                var d = Declarations[i];
                if (!d.IsAttached)
                    continue;
                try
                {
                    if (ApplyOne(d, res))
                        applied++;
                }
                catch (Exception e)
                {
                    LogWarn("写 " + d.FullID + " 的属性失败：" + e.GetType().Name + ": " + e.Message);
                }
            }

            LogInfo("ApplyProperties：" + applied + " 条设置项属性已写入");
            return applied;
        }

        private static bool ApplyOne(Declaration d, MVZ2.Managers.ResourceManager res)
        {
            var id = new NamespaceID(d.Space, d.Id);
            var meta = res == null ? null : res.GetOptionWidgetMeta(id);

            // 元数据是"锦上添花"：XML 不在也能用声明里的文案兜底，
            // 否则一条 XML 打错字就会让设置项整个消失、而症状只是一行 Warning。
            var category = meta != null ? meta.Category : new NamespaceID(d.Space, d.Category);
            var label = meta != null && !string.IsNullOrEmpty(meta.Label) ? meta.Label : (d.Label ?? string.Empty);
            var tooltip = meta != null && !string.IsNullOrEmpty(meta.Tooltip) ? meta.Tooltip : (d.Tooltip ?? string.Empty);
            var order = meta != null ? meta.Order : d.Order;

            if (meta == null)
            {
                LogWarn("找不到 " + d.FullID + " 的 options.xml 条目，改用代码里的文案兜底"
                    + "（分类=" + d.Category + "）");
            }

            var def = d.Definition;
            var ok = true;
            ok &= Try(() => def.SetProperty(LogicOptionWidgetProps.CATEGORY_ID, category), d, "CATEGORY_ID");
            ok &= Try(() => def.SetProperty(LogicOptionWidgetProps.LABEL, label), d, "LABEL");
            ok &= Try(() => def.SetProperty(LogicOptionWidgetProps.TOOLTIP, tooltip), d, "TOOLTIP");
            ok &= Try(() => def.SetProperty(LogicOptionWidgetProps.ORDER, order), d, "ORDER");
            if (d.IsSlider)
            {
                ok &= Try(() => def.SetProperty(LogicOptionWidgetProps.SLIDER_MIN_VALUE,
                    meta != null ? meta.SliderMinValue : d.Min), d, "SLIDER_MIN_VALUE");
                ok &= Try(() => def.SetProperty(LogicOptionWidgetProps.SLIDER_MAX_VALUE,
                    meta != null ? meta.SliderMaxValue : d.Max), d, "SLIDER_MAX_VALUE");
                ok &= Try(() => def.SetProperty(LogicOptionWidgetProps.SLIDER_WHOLE_NUMBERS,
                    meta != null ? meta.SliderWholeNumber : d.Whole), d, "SLIDER_WHOLE_NUMBERS");
            }
            return ok;
        }

        private static bool Try(Action action, Declaration d, string label)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception e)
            {
                LogWarn("属性 " + label + " 写入 " + d.FullID + " 失败："
                    + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 由注入定义反查自己的声明。放在这里（而不是注入类的实例方法上）是因为
        /// Il2CppInterop 会为注入类的**每个**方法生成代理，一旦某个方法暴露纯托管类型
        /// （比如本类的 <see cref="Declaration"/>）就会报
        /// "has unsupported return type" 警告 —— 静态 helper 不在注入类上，没有这个问题。
        /// </summary>
        internal static Declaration LookupOf(Definition def)
        {
            if (def == null)
                return null;
            try { return Lookup(def.GetID().Path); }
            catch (Exception) { return null; }
        }

        private static bool _typesRegistered;

        /// <summary>
        /// 把两个注入类注册进 il2cpp。**必须在任何实例化之前**（<c>ClassInjector</c> 的硬要求），
        /// 所以放在 <see cref="Attach"/> 的第一行（Attach 就在 LoadStages 时机、早于创建）。
        /// </summary>
        private static void EnsureTypesRegistered()
        {
            if (_typesRegistered)
                return;
            _typesRegistered = true;
            try
            {
                ClassInjector.RegisterTypeInIl2Cpp(typeof(UiToggleDefinition));
                ClassInjector.RegisterTypeInIl2Cpp(typeof(UiSliderDefinition));
                ClassInjector.RegisterTypeInIl2Cpp(typeof(UiDropdownDefinition));
            }
            catch (Exception e)
            {
                LogWarn("注册注入类失败（重复注册可忽略）：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>状态报告（排障用）。</summary>
        public static string Report()
        {
            var sb = new StringBuilder(400);
            sb.Append("UiOptions：").Append(Declarations.Count).Append(" 条声明，")
              .Append(ModEntries.Count).Append(" 个 mod 介绍过自己\n");
            foreach (var pair in ModEntries)
            {
                var m = pair.Value;
                sb.Append("  ").Append(m.Space).Append("  名字=\"").Append(UiLocale.Get(m.DisplayName))
                  .Append("\"  说明=\"").Append(m.Description == null ? "（未声明）" : UiLocale.Get(m.Description))
                  .Append("\"  作者=\"").Append(m.Author == null ? "（未声明）" : UiLocale.Get(m.Author))
                  .Append("\"\n");
            }
            for (var i = 0; i < Declarations.Count; i++)
            {
                var d = Declarations[i];
                string cat = "?";
                try
                {
                    var c = d.IsAttached ? d.Definition.GetCategoryID() : null;
                    cat = c == null ? "<null>" : c.SpaceName + ":" + c.Path;
                }
                catch (Exception e) { cat = "<抛 " + e.GetType().Name + ">"; }
                sb.Append("  ").Append(d.FullID)
                  .Append(d.IsSlider ? " [Slider]" : d.IsDropdown ? " [Choice]" : " [Toggle]")
                  .Append("  ")
                  .Append(d.Scope == UiOptionScope.Both ? "两处"
                        : d.Scope == UiOptionScope.VanillaMenu ? "仅原版" : "仅模组页")
                  .Append(d.OnVanillaMenu ? (d.IsAttached ? " 已登记" : " **未登记**") : "")
                  .Append("  category=").Append(cat).Append('\n');
            }
            return sb.ToString();
        }

        private static void LogInfo(string msg)
        {
            try { Plugin.Log?.LogInfo("[UiOptions] " + msg); } catch (Exception) { }
        }

        private static void LogWarn(string msg)
        {
            try { Plugin.Log?.LogWarning("[UiOptions] " + msg); } catch (Exception) { }
        }
    }

    /// <summary>
    /// 注入的复选框设置项。回调按 id 从 <see cref="UiOptions"/> 的**静态**声明表里取 —— 静态表
    /// 是托管 GC 根，所以托管委托不会被回收（这跟 <c>Il2CppPin</c> 解决的是同类问题：
    /// 注入类实例自己的字段不被 il2cpp GC 追踪，别把委托存在实例字段里）。
    /// </summary>
    public class UiToggleDefinition : OptionToggleDefinition
    {
        public UiToggleDefinition(IntPtr ptr) : base(ptr)
        {
        }

        public UiToggleDefinition(string nsp, string name) : base(nsp, name)
        {
        }

        public override bool GetValue(IOptionContext context)
        {
            var d = UiOptions.LookupOf(this);
            return d != null && d.GetBool != null && d.GetBool();
        }

        public override void OnValueChanged(IOptionContext context, bool value)
        {
            var d = UiOptions.LookupOf(this);
            if (d == null || d.SetBool == null)
                return;
            d.SetBool(value);
        }
    }

    /// <summary>注入的滑条设置项。回调来自 <see cref="UiOptions"/> 的静态声明表（理由同上）。</summary>
    public class UiSliderDefinition : OptionSliderDefinition
    {
        public UiSliderDefinition(IntPtr ptr) : base(ptr)
        {
        }

        public UiSliderDefinition(string nsp, string name) : base(nsp, name)
        {
        }

        public override float GetValue(IOptionContext context)
        {
            var d = UiOptions.LookupOf(this);
            return (d == null || d.GetFloat == null) ? 0f : d.GetFloat();
        }

        public override string GetLabelValue(IOptionContext context, float value)
        {
            var d = UiOptions.LookupOf(this);
            if (d != null && d.Format != null)
            {
                try { return d.Format(value); }
                catch (Exception) { }
            }
            return d != null && d.Whole
                ? Math.Round(value).ToString()
                : value.ToString("0.###");
        }

        public override void OnValueChanged(IOptionContext context, float value)
        {
            var d = UiOptions.LookupOf(this);
            if (d == null || d.SetFloat == null)
                return;
            d.SetFloat(value);
        }

        public override void OnEndEdit(IOptionContext context, float value)
        {
            // 与 OnValueChanged 相同即可：值的持久化由调用方在自己的 setter 里决定
            // （一般是 SetOptionXxx + SaveOptions）。原版也是在这个时机落盘。
            OnValueChanged(context, value);
        }
    }

    /// <summary>
    /// 注入的下拉框设置项。回调来自 <see cref="UiOptions"/> 的静态声明表（理由同上）。
    ///
    /// <para>⚠ <c>FillItems</c> 的 <c>items</c> 形参在 interop 里是
    /// <c>Il2CppSystem.Collections.Generic.List&lt;string&gt;</c>，**不是**托管 List ——
    /// 覆写签名必须与它逐字一致，否则编译期就报「没有找到合适的方法来重写」。</para>
    /// </summary>
    public class UiDropdownDefinition : OptionDropdownDefinition
    {
        public UiDropdownDefinition(IntPtr ptr) : base(ptr)
        {
        }

        public UiDropdownDefinition(string nsp, string name) : base(nsp, name)
        {
        }

        public override int GetValue(IOptionContext context)
        {
            var d = UiOptions.LookupOf(this);
            return d == null || d.GetIndex == null ? 0 : d.GetIndex();
        }

        public override void FillItems(IOptionContext context, Il2CppSystem.Collections.Generic.List<string> items)
        {
            var d = UiOptions.LookupOf(this);
            if (d == null || d.FillItems == null || items == null)
                return;
            try { d.FillItems(items); }
            catch (Exception) { }
        }

        public override void OnValueChanged(IOptionContext context, int index)
        {
            var d = UiOptions.LookupOf(this);
            if (d == null || d.SetIndex == null)
                return;
            d.SetIndex(index);
        }
    }
}
