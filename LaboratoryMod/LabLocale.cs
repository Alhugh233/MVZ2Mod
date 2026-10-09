using DSHCore.Ui;
using MVZ2Logic.Localization;

namespace LaboratoryMod
{
    /// <summary>
    /// 本 mod 的全部用户可见文案（<b>多语言的唯一真源</b>）。
    ///
    /// <para>这里只有三样东西，但正好覆盖了三种典型情况：</para>
    /// <list type="number">
    /// <item><b>游戏内容名</b>：关卡名（来自 <c>assets/Content/metas/stages.xml</c> 的
    /// <c>name</c> 属性）。游戏显示关卡名时走 <c>LevelManager.GetStageName</c> →
    /// <c>_p(LogicStrings.CONTEXT_LEVEL_NAME, 名字)</c>，所以登记时**必须带这个上下文**；
    /// 带上了，关卡列表、关卡内标题、统计页会一起变（DSHCore 挂了那条查找链）。</item>
    /// <item><b>本 mod 自述</b>：进 DSHCore「模组设置」页左栏的名字与右栏「说明」。</item>
    /// </list>
    ///
    /// <para>玩家不改 mod 也能加语言：<c>BepInEx/config/DSHCore/lang/&lt;语言码&gt;.txt</c>，
    /// 带上下文的行写成 <c>[levelname] 真正的序章 = Real Prologue</c>。</para>
    /// </summary>
    internal static class LabLocale
    {
        private static readonly UiLocaleTable T = UiLocale.Table("mvz2_lab");

        /// <summary>在 <c>Plugin.Load()</c> 里调一次（早于任何界面/关卡取词）。</summary>
        public static void Register()
        {
            // ---- mod 自述（说明那一段是中文源串 → 补英文）----
            T.En("实验室小游戏（real_prologue / zero_k_space / unknow_palace）",
                "Laboratory minigames (real_prologue / zero_k_space / unknow_palace)");

            // ---- 关卡名（游戏内容名，必须带 CONTEXT_LEVEL_NAME）----
            T.EnAllWith(LogicStrings.CONTEXT_LEVEL_NAME,
                "真正的序章", "Real Prologue",
                "真理伪证殿堂", "Hall of Perjured Truth",
                "零度空间", "Zero-K Space");
        }
    }
}
