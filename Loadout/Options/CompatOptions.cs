using System;
using System.Text;
using DSHCore;
using DSHCore.Ui;
using HarmonyLib;
using MVZ2;
using MVZ2.Managers;
using MVZ2.Modding;
using MVZ2Logic;
using MVZ2Logic.Commands;
using MVZ2Logic.Games;
using MVZ2Logic.Modding;
using MVZ2Logic.Options;
using PVZEngine;

namespace Loadout
{
    /// <summary>
    /// 把**兼容性等级**做成原版设置界面里的一行（菜单 → 更多选项 → **游戏**一栏下方）。
    ///
    /// <para>做法完全照 <c>DSHCore.Ui.UiOptions</c> 的标准三步（那是本仓库为「给原版设置界面加一项」
    /// 备好的封装，活样本见 <c>UiGallery/SampleOptions.cs</c>）：</para>
    /// <list type="number">
    /// <item><b>声明</b>（<see cref="Enable"/> ← 插件 <c>Load()</c>，纯托管）：</item>
    /// <item><b>登记</b>（<see cref="LoadStagesPrefix"/> ← 官方 <c>ModLoader.LoadStages</c> 时机）：
    /// <c>Mod.AddDefinition</c> 必须落在 <c>LoadDefinitions</c> 内、<c>game.AddMod</c> 之前；</item>
    /// <item><b>写属性</b>（<c>LoadoutMod.PostInit</c> ← <c>UiOptions.ApplyProperties</c>）：
    /// 写早了会被**静默丢弃**（属性键那时还没就绪）。</item>
    /// </list>
    ///
    /// <para><b>值存哪</b>：就是原来那个 BepInEx 配置项 <c>Compat.CompatLevel</c> ——
    /// 设置界面与配置文件是**同一份数据**，谁改都立刻生效，也不用迁移老用户的值。
    /// 所以这里只提供 <c>getter</c>/<c>setter</c> 两个回调。</para>
    ///
    /// <para><b>为什么用下拉框而不是滑条</b>：三个值是有名字的枚举（Safe / Standard / Full），
    /// 官方对同类东西用的就是下拉框（血条数值模式、分辨率）。</para>
    /// </summary>
    internal static class CompatOptions
    {
        /// <summary>设置项 id（完整 id = <c>loadout:compat_level</c>）。</summary>
        public const string DropdownId = "compat_level";

        /// <summary>排在「游戏」那一栏已有条目的**下方**。</summary>
        private const int Order = 900;

        /// <summary>由插件 <c>Load()</c> 调用：登记命名空间 + 声明设置项 + 挂登记补丁。</summary>
        public static void Enable()
        {
            try
            {
                // 登记命名空间 + 注册注入类（必须在任何实例化之前）。
                ModBootstrap.Register(
                    spaceName: LoadoutMod.spaceName,
                    logicType: typeof(LoadoutMod),
                    xmlDefaultNamespace: "mvz2");

                InjectedDefinitions.Apply();
                InjectedDefinitions.Register(LoadoutMod.spaceName);

                Declare();
                ApplyPatch();

                ModInfos.OnRegister(OnRegisterModInfos);
                Plugin.Log?.LogInfo("[CompatOptions] 已启用（兼容性等级会出现在「设置 → 更多选项 → 游戏」里）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CompatOptions] Enable error: " + e);
            }
        }

        /// <summary>纯托管：把这一行声明出来（不碰游戏）。</summary>
        private static void Declare()
        {
            try
            {
                UiOptions.DeclareDropdown(
                    LoadoutMod.spaceName, DropdownId,
                    category: "gameplay",
                    label: "兼容性等级",
                    tooltip: "本 mod 会接管多少原版行为。等级不够的功能在面板里是**置灰**的，调上去立刻生效，不用重启。\n"
                        + "Safe     = 只用原版已有的属性与接口，不挂任何会改行为的补丁。\n"
                        + "Standard = 默认。额外接管「面板打开时游戏速度=0」「选卡显示所有蓝图」「蜘蛛图标作为入口」"
                        + "，以及蓝图页的「血量 / 白天不睡觉」。\n"
                        + "Full     = 全部功能，含需要替换官方行为的危险项（传送带卡池接管 / 逆阵营放置 / 无条件放置）。",
                    order: Order,
                    getter: () => CompatConfig.LevelIndex,
                    setter: CompatConfig.SetLevelIndex,
                    fillItems: FillItems);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CompatOptions] Declare error: " + e);
            }
        }

        /// <summary>
        /// 下拉框的选项文案。
        ///
        /// <para>⚠ 形参必须是 <c>Il2CppSystem.Collections.Generic.List&lt;string&gt;</c> ——
        /// interop 里官方的 <c>OptionDropdownDefinition.FillItems</c> 就是这个类型
        /// （托管 <c>List&lt;string&gt;</c> 对不上，编译期就报）。</para>
        /// </summary>
        private static void FillItems(Il2CppSystem.Collections.Generic.List<string> items)
        {
            if (items == null)
                return;
            for (var i = 0; i < CompatConfig.Levels.Length; i++)
                items.Add(CompatConfig.LevelName(CompatConfig.Levels[i]));
        }

        /// <summary>
        /// 注册本 mod 的 <c>ModInfo</c>。
        ///
        /// <para>这一步让 Loadout **出现在 mod 列表里** —— 没有 Mod 实例就没有
        /// <c>mod.AddDefinition</c>，这是那一行设置项的必要代价。</para>
        /// </summary>
        private static Il2CppSystem.Threading.Tasks.Task OnRegisterModInfos(
            IGlobalGame game,
            Il2CppSystem.Collections.Generic.List<ModInfo> results)
        {
            try
            {
                var logic = LoadoutMod.CreateInstance();
                VTableRebind.Ensure(typeof(LoadoutMod));
                results.Add(ModInfos.Create(LoadoutMod.spaceName, logic, "MVZ2 Loadout（图形化控制台）"));
                Plugin.Log?.LogInfo("[CompatOptions] ModInfo 已注册（" + LoadoutMod.spaceName + "）");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CompatOptions] OnRegisterModInfos error: " + e);
            }
            return Il2CppSystem.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>在官方 <c>ModLoader.LoadStages</c> 时机登记（<c>Mod.AddDefinition</c> 的硬要求）。</summary>
        private static void ApplyPatch()
        {
            try
            {
                var target = AccessTools.Method(typeof(ModLoader), "LoadStages");
                if (target == null)
                {
                    Plugin.Log?.LogError("[CompatOptions] 找不到 ModLoader.LoadStages");
                    return;
                }
                new Harmony(Plugin.GUID + ".compatoptions").Patch(target,
                    prefix: new HarmonyMethod(typeof(CompatOptions), nameof(LoadStagesPrefix)));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CompatOptions] 挂钩失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void LoadStagesPrefix(Mod mod)
        {
            try
            {
                // 判定归属必须用 NamespaceOf（先比 native 指针）：注入实例的 mod.Namespace
                // 字符串存在被 GC 回收的窗口，直接比字符串会让登记静默失败。
                if (InjectedDefinitions.NamespaceOf(mod) != LoadoutMod.spaceName)
                    return;
                ModNamespacePin.Ensure(mod, LoadoutMod.spaceName);
                var n = UiOptions.Attach(mod);
                Plugin.Log?.LogInfo("[CompatOptions] LoadStages：已登记 " + n + " 条设置项");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CompatOptions] Attach error: " + e);
            }
        }

        // ------------------------------------------------------------------
        // 验收：复刻官方渲染判据（自测用）
        // ------------------------------------------------------------------

        /// <summary>
        /// 「这一行到底会不会出现在设置界面里」的可读结论。
        ///
        /// <para>判据就是**官方渲染代码自己的判据**
        /// （<c>OptionsDialogController.UpdateMoreOptionsPageWidgets</c>）：
        /// 定义在官方表里 → <c>ShouldEnable</c> → <c>GetCategoryID()</c> 有效 →
        /// 该分类的 meta 存在（否则整组**静默消失**）→ 类型是下拉框 → <c>FillItems</c>/<c>GetValue</c> 可读。
        /// 全部满足就一定会被画出来，不必人肉去点设置界面。</para>
        /// </summary>
        public static string Report()
        {
            var sb = new StringBuilder(400);
            try
            {
                var main = MainManager.Instance;
                if (main == null)
                    return "（MainManager 未就绪）";
                var game = Global.Game;
                if (game == null)
                    return "（Global.Game 未就绪）";
                // ⚠ IGlobalGame 上看不到 IGameContent 的扩展方法 → 按指针包壳 + 静态调用
                //   （与 AllBlueprintsPatch 里 GetDefinitions 的写法同源）。
                var content = new IGameContent(game.Pointer);
                var id = new NamespaceID(LoadoutMod.spaceName, DropdownId);
                var def = LogicGameDefinitionsExt.GetOptionWidgetDefinition(content, id);
                sb.Append("兼容性等级设置项 ").Append(id.SpaceName).Append(':').Append(id.Path);
                if (def == null)
                {
                    sb.Append("：**不在官方表里**（登记失败）");
                    return sb.ToString();
                }
                sb.Append(" 类型=").Append(def.GetItemType());

                var category = def.GetCategoryID();
                var valid = NamespaceID.IsValid(category);
                sb.Append(" 分类=").Append(valid ? category.SpaceName + ":" + category.Path : "<null>");
                var meta = valid ? main.ResourceManager.GetOptionCategoryMeta(category) : null;
                sb.Append(" 分类meta=").Append(meta == null ? "**没有（整组会被丢掉）**" : "有");
                sb.Append(" 标签=「").Append(def.GetLabel() ?? "").Append('」');

                var dropdown = def.TryCast<OptionDropdownDefinition>();
                if (dropdown == null)
                {
                    sb.Append("  ⚠ 不是 OptionDropdownDefinition（类型模式对不上，画不出来）");
                    return sb.ToString();
                }
                var items = new Il2CppSystem.Collections.Generic.List<string>();
                dropdown.FillItems(null, items);
                sb.Append(" 选项=").Append(items.Count).Append('[');
                for (var i = 0; i < items.Count; i++)
                {
                    if (i > 0)
                        sb.Append(" / ");
                    sb.Append(items[i]);
                }
                sb.Append("] 当前值=").Append(dropdown.GetValue(null));
                sb.Append("  判据 可渲染=").Append(valid && meta != null && items.Count > 0 ? "OK" : "NG");
                sb.Append("  当前等级=").Append(CompatConfig.Level);
            }
            catch (Exception e)
            {
                sb.Append("（报告失败 ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append("）");
            }
            return sb.ToString();
        }
    }
}
