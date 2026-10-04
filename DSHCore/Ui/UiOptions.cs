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
    /// **L2 封装：用零 UI 代码给游戏加一个"原生设置项"。**
    ///
    /// <para>原版设置界面（主菜单/地图/关卡三处共用）会把游戏认得的
    /// <c>OptionWidgetDefinition</c> 全部列出来 —— 分类页、滚动、值持久化、悬停提示
    /// 全是白送的。本类把「怎么把一条塞进去」收敛成三个一行调用。</para>
    ///
    /// <code>
    /// // ① Load()：声明（纯托管，不碰游戏）
    /// UiOptions.DeclareToggle("my_mod", "my_toggle", "general", "我的开关", "说明文字", 0,
    ///     () => _value, v => _value = v);
    ///
    /// // ② LoadStages 时机（一般的做法是在自己已有的 LoadStages prefix 里）：
    /// UiOptions.Attach(mod);
    ///
    /// // ③ PostInit：写属性
    /// UiOptions.ApplyProperties();
    /// </code>
    ///
    /// <para>三种控件：<see cref="DeclareToggle"/> / <see cref="DeclareSlider"/> /
    /// <see cref="DeclareDropdown"/>。前两种直接对应官方的对应定义；
    /// 下拉框是**多值枚举的推荐形态**（与官方的「血条数值模式」「分辨率」同一形态）。</para>
    ///
    /// <para><b>为什么必须是三次调用（本次实测结论）：</b></para>
    /// <list type="number">
    /// <item><b>声明</b>可以在插件 <c>Load()</c> 做，纯托管。</item>
    /// <item><b>创建 + <c>Mod.AddDefinition</c></b> 必须落在官方 <c>ModLoader.LoadStages</c> 时机
    /// （<c>LoadDefinitions</c> 之内、<c>game.AddMod</c> 之前），否则定义不进全局表。</item>
    /// <item><b>写属性（<c>CATEGORY_ID</c>/<c>LABEL</c>/<c>ORDER</c>…）必须等到 <c>PostInit</c>。</b>
    /// 在 <c>LoadStages</c> 就写会**被静默丢弃**（属性键那时还没就绪）——
    /// 症状极具迷惑性：<c>SetProperty</c> 一声不吭、"属性已应用"日志照打，
    /// 但官方渲染时 <c>GetCategoryID()</c> 读回 null，于是被
    /// <c>!NamespaceID.IsValid(categoryID)</c> 静默过滤掉，界面上什么都不多。
    /// 这跟关卡 meta 是同一个坑（见 <c>LaboratoryMod.StageMetaApplier</c> 的注释）。</item>
    /// </list>
    ///
    /// <para><b>XML 是必须的：</b>即使是本封装，也仍然要一份
    /// <c>Content/metas/options.xml</c>（文案/分类/顺序都在那里）。
    /// 完整写法与合法分类 ID 见 <c>DSHCore/UI_GUIDE.md</c> §3。</para>
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
            internal Action<Il2CppSystem.Collections.Generic.List<string>> FillItems;

            /// <summary>被创建出来的注入定义（<see cref="Attach"/> 之后才有值）。</summary>
            internal OptionWidgetDefinition Definition;

            /// <summary>完整命名空间 ID（<c>space:id</c>）。</summary>
            public string FullID
            {
                get { return Space + ":" + Id; }
            }

            public bool IsAttached
            {
                get { return Definition != null; }
            }
        }

        private static readonly List<Declaration> Declarations = new List<Declaration>();
        private static readonly Dictionary<string, Declaration> ById = new Dictionary<string, Declaration>();

        // ------------------------------------------------------------------
        // ① 声明
        // ------------------------------------------------------------------

        /// <summary>声明一个复选框设置项。</summary>
        public static Declaration DeclareToggle(string spaceName, string id, string category,
            string label, string tooltip = null, int order = 0,
            Func<bool> getter = null, Action<bool> setter = null)
        {
            return Declare(new Declaration
            {
                Space = spaceName,
                Id = id,
                Category = category,
                Label = label,
                Tooltip = tooltip,
                Order = order,
                GetBool = getter,
                SetBool = setter,
            });
        }

        /// <summary>声明一个滑条设置项。</summary>
        public static Declaration DeclareSlider(string spaceName, string id, string category,
            string label, float min, float max, bool wholeNumbers,
            string tooltip = null, int order = 0,
            Func<float> getter = null, Action<float> setter = null,
            Func<float, string> format = null)
        {
            return Declare(new Declaration
            {
                Space = spaceName,
                Id = id,
                Category = category,
                Label = label,
                Tooltip = tooltip,
                Order = order,
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
        /// 声明一个**下拉框**设置项（多值枚举的推荐形态）。
        ///
        /// <para>选项文案在 <paramref name="fillItems"/> 里现填 —— 与官方
        /// <c>OptionDropdownDefinition.FillItems</c> 一致，不需要写进 <c>options.xml</c>
        /// （XML 只承载 标签 / 分类 / 顺序 / 悬停说明，节点名写 <c>dropdown</c>）。</para>
        ///
        /// <para>⚠ <paramref name="fillItems"/> 的形参类型必须是
        /// <c>Il2CppSystem.Collections.Generic.List&lt;string&gt;</c> —— interop 里的
        /// <c>FillItems</c> 就是这个类型，托管 <c>List&lt;string&gt;</c> 对不上（编译期就报）。</para>
        /// </summary>
        public static Declaration DeclareDropdown(string spaceName, string id, string category,
            string label, string tooltip = null, int order = 0,
            Func<int> getter = null, Action<int> setter = null,
            Action<Il2CppSystem.Collections.Generic.List<string>> fillItems = null)
        {
            return Declare(new Declaration
            {
                Space = spaceName,
                Id = id,
                Category = category,
                Label = label,
                Tooltip = tooltip,
                Order = order,
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

            // 先确保定义设施在跑：声明本身不创建 Definition，但登记命名空间没有副作用。
            InjectedDefinitions.Apply();
            InjectedDefinitions.Register(d.Space);

            Declarations.Add(d);
            ById[key] = d;
            return d;
        }

        /// <summary>已经声明、且属于这个命名空间的设置项。</summary>
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
        /// 为 <paramref name="mod"/> 的命名空间创建全部已声明的设置项并 <c>AddDefinition</c>。
        /// 一般在 mod 自己的 <c>ModLoader.LoadStages</c> prefix 里调用（幂等）。
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
            sb.Append("UiOptions：").Append(Declarations.Count).Append(" 条声明\n");
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
                  .Append(d.IsSlider ? " [Slider]" : d.IsDropdown ? " [Dropdown]" : " [Toggle]")
                  .Append(d.IsAttached ? " 已登记" : " **未登记**")
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
