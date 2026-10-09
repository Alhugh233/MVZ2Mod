using System;
using System.Collections.Generic;

namespace DSHCore.Ui
{
    /// <summary>
    /// 一个 mod 的译文表（<see cref="UiLocale.Table"/> 的返回值）—— **mod 侧写多语言的入口**。
    ///
    /// <para><b>为什么要有这一层</b>：直接在 <c>Plugin.Load()</c> 里堆一长串
    /// <c>UiLocale.Add(...)</c> 有三个毛病 —— 看不出属于哪个 mod、容易漏掉某种语言、
    /// 没法一眼看出「哪些是游戏内容名（带上下文）、哪些是界面文字」。
    /// 用一张表把这两件事一次写清楚：</para>
    ///
    /// <code>
    /// // Localization.cs
    /// internal static class LabLocale
    /// {
    ///     private static readonly UiLocaleTable T = UiLocale.Table("mvz2_lab");
    ///
    ///     public static void Register()
    ///     {
    ///         // 一次给「中文源串 → 英文」，界面文字（不带上下文）
    ///         T.Both("实验室", "Laboratory");
    ///
    ///         // 游戏内容名要带上下文 —— 关卡名走 LogicStrings.CONTEXT_LEVEL_NAME
    ///         T.Both("真正的序章", "Real Prologue", LogicStrings.CONTEXT_LEVEL_NAME);
    ///
    ///         // 只补一种语言也行（源语言 = 中文时通常只需要补英文）
    ///         T.Add("零度空间", "Zero-K Space", UiLocale.English, LogicStrings.CONTEXT_LEVEL_NAME);
    ///     }
    /// }
    /// </code>
    ///
    /// <para><see cref="Owner"/> 只用于诊断（<see cref="Report"/>），不参与取词。</para>
    ///
    /// <para><b>注册时机</b>：必须在任何界面/关卡开始取词之前（就是 <c>Plugin.Load()</c> 里），
    /// 而且要在 <c>UiOptions.DeclareMod</c> 前后都行 —— 取词发生在画界面的时候。</para>
    /// </summary>
    public sealed class UiLocaleTable
    {
        /// <summary>这张表的归属者（mod 命名空间 / 类名，仅诊断用）。</summary>
        public string Owner { get; private set; }

        /// <summary>已经登记了多少条（本表内计数）。</summary>
        public int Count { get; private set; }

        internal UiLocaleTable(string owner)
        {
            Owner = owner ?? string.Empty;
        }

        // ------------------------------------------------------------------
        // 登记
        // ------------------------------------------------------------------

        /// <summary>
        /// 登记一条。<paramref name="language"/> 省略 = 源语言（中文）；
        /// <paramref name="context"/> 见 <see cref="UiLocale.Add"/>（游戏内容名才需要）。
        /// </summary>
        public UiLocaleTable Add(string source, string translated, string language = null, string context = null)
        {
            UiLocale.Add(source, translated, language, context);
            if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(translated))
                Count++;
            return this;
        }

        /// <summary>
        /// 一次登记两种语言（最常用的形式：中文源串 + 英文）。
        ///
        /// <para><paramref name="chinese"/> 为 null/空 = 源串本身就是中文，不用再登一遍源语言。</para>
        /// </summary>
        public UiLocaleTable Both(string source, string english, string chinese = null, string context = null)
        {
            if (!string.IsNullOrEmpty(chinese))
                Add(source, chinese, UiLocale.Chinese, context);
            if (!string.IsNullOrEmpty(english))
                Add(source, english, UiLocale.English, context);
            return this;
        }

        /// <summary>「中文源串 → 英文」的简写（源串即中文，最常见的一种）。</summary>
        public UiLocaleTable En(string chinese, string english, string context = null)
        {
            return Both(chinese, english, null, context);
        }

        /// <summary>「英文源串 → 中文」的简写（mod 自述名这一类：源串是英文名）。</summary>
        public UiLocaleTable Zh(string source, string chinese, string context = null)
        {
            return Both(source, null, chinese, context);
        }

        /// <summary>批量登记「中文 → 英文」：<c>EnAll("甲", "A", "乙", "B")</c>。</summary>
        public UiLocaleTable EnAll(params string[] pairs)
        {
            if (pairs == null)
                return this;
            for (var i = 0; i + 1 < pairs.Length; i += 2)
                En(pairs[i], pairs[i + 1]);
            return this;
        }

        /// <summary>批量登记「中文 → 英文」并**全部带上同一个上下文**（游戏内容名成片登记时用）。</summary>
        public UiLocaleTable EnAllWith(string context, params string[] pairs)
        {
            if (pairs == null)
                return this;
            for (var i = 0; i + 1 < pairs.Length; i += 2)
                En(pairs[i], pairs[i + 1], context);
            return this;
        }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Owner) ? ("译文表（" + Count + " 条）") : (Owner + "（" + Count + " 条）");
        }
    }
}
