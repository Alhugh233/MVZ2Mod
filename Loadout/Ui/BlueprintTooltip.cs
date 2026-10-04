using System;
using System.Collections.Generic;
using DSHCore.Ui;
using MVZ2.Blueprints;
using MVZ2.UI;
using PVZEngine;
using UnityEngine;

namespace Loadout
{
    /// <summary>
    /// 给一张原版蓝图卡挂**原版那一套**悬停提示（不是自绘浮层）：标题 = 蓝图名，
    /// 正文 = 蓝图的 tooltip；**蓝图没有定义 tooltip 时正文退化成它的 id**。
    ///
    /// <para>为什么要提示：面板里的卡是我们克隆出来的，脱离了官方选卡控制器
    /// （<c>LevelBlueprintChooseController_TooltipSources</c> 是按**下标**给官方那批卡注册来源的），
    /// 所以克隆出来的卡自己**不会有**任何提示 —— 悬停上去毫无反应。</para>
    ///
    /// <para>实现走 <see cref="UiTooltip"/>（它做了「挂 <c>TooltipHandler</c> + 订阅 +
    /// 调 <c>MainSceneController.ShowTooltip</c>」这一整条宿主链路）。原版卡上没有
    /// <c>TooltipHandler</c>（卡自己是 <c>ITooltipTarget</c>，由关卡控制器按目标匹配来源），
    /// 所以我们直接挂一个不会与官方打架。</para>
    /// </summary>
    internal static class BlueprintTooltip
    {
        private static int _count;
        private static string _lastSample;

        /// <summary>
        /// 一张卡上已经铺了什么（按**卡的实例 id** 缓存）。
        ///
        /// <para>为什么需要它：选卡浮层的网格卡是官方 <c>ElementList</c> **复用**的 ——
        /// 翻页时同一个 <c>Blueprint</c> 实例被换上另一份数据。所以缓存必须连
        /// 「铺的是哪个蓝图」一起记：实例相同但蓝图换了 → 重铺；两者都相同 → 直接跳过
        /// （这样每帧调也不会反复做本地化查询与写字段）。</para>
        /// </summary>
        private sealed class Entry
        {
            public string IdText;
            public bool CommandBlock;
            public string Title;
            public string Text;
            public bool Applied;
        }

        private static readonly Dictionary<int, Entry> _cache = new Dictionary<int, Entry>();

        /// <summary>给 <paramref name="bp"/> 这张卡铺上提示（同一张卡重复调用会自动跳过）。</summary>
        public static void Apply(Blueprint bp, NamespaceID id, bool commandBlock)
        {
            if (!UiFinder.IsAlive(bp) || !NamespaceID.IsValid(id))
                return;
            try
            {
                var idText = IdText(id);
                int inst;
                try { inst = bp.gameObject.GetInstanceID(); }
                catch (Exception) { inst = 0; }

                Entry entry = null;
                if (inst != 0 && _cache.TryGetValue(inst, out entry))
                {
                    if (entry.IdText == idText && entry.CommandBlock == commandBlock)
                    {
                        if (entry.Applied)
                            return;      // 已经铺好了，什么也不用做
                    }
                    else
                    {
                        entry = null;    // 同一张卡换了蓝图 → 重铺
                    }
                }
                if (entry == null)
                {
                    entry = new Entry { IdText = idText, CommandBlock = commandBlock };
                    if (inst != 0)
                    {
                        if (_cache.Count > 512)
                            _cache.Clear();
                        _cache[inst] = entry;
                    }
                }

                entry.Title = TitleOf(id, commandBlock);
                entry.Text = TextOf(id);

                var go = bp.gameObject;
                var ok = go.GetComponent<TooltipHandler>() != null
                    ? UiTooltip.SetText(go, entry.Title, entry.Text)      // 已经挂过 → 只改文案
                    : UiTooltip.Attach(go, entry.Title, entry.Text);      // 首次 → 挂组件 + 订阅
                entry.Applied = ok;
                if (ok)
                {
                    _count++;
                    _lastSample = entry.Title + " / " + entry.Text;
                }
            }
            catch (Exception) { }
        }

        /// <summary>自测用：把缓存清掉（下次铺的时候会重新查一遍文案）。</summary>
        public static void ClearCache()
        {
            _cache.Clear();
        }

        /// <summary>标题：蓝图名（拿不到就用 id）。模仿者会带「（模仿者）」后缀。</summary>
        public static string TitleOf(NamespaceID id, bool commandBlock)
        {
            var name = BlueprintOps.BlueprintName(id, commandBlock);
            return string.IsNullOrWhiteSpace(name) ? IdText(id) : name;
        }

        /// <summary>
        /// 正文：蓝图的 tooltip；**没有定义就显示 id**。
        ///
        /// <para>「没有定义」在实现上的表现是 <c>GetBlueprintTooltip</c> 返回空串
        /// （种子定义拿不到 / 实体拿不到 tooltip 词条都是这一条路）。</para>
        /// </summary>
        public static string TextOf(NamespaceID id)
        {
            string tip = null;
            try
            {
                var main = LevelFacade.Main;
                var res = main == null ? null : main.ResourceManager;
                if (res != null)
                    tip = res.GetBlueprintTooltip(id);
            }
            catch (Exception) { }
            if (string.IsNullOrWhiteSpace(tip))
                tip = IdText(id);
            return tip;
        }

        /// <summary>`命名空间:路径` —— 蓝图的原样 id。</summary>
        public static string IdText(NamespaceID id)
        {
            if (!NamespaceID.IsValid(id))
                return "（空 id）";
            try { return id.SpaceName + ":" + id.Path; }
            catch (Exception) { return "（读不出 id）"; }
        }

        /// <summary>自测用：一共铺了几次、最近一次的内容。</summary>
        public static string Report()
        {
            return "蓝图提示：铺了 " + _count + " 次  最近=" + (_lastSample ?? "（无）");
        }

        /// <summary>自测用：某张卡上的提示状态（有没有挂上、文案是什么）。</summary>
        public static string Describe(Blueprint bp)
        {
            if (!UiFinder.IsAlive(bp))
                return "（无效的卡）";
            try
            {
                var handler = bp.gameObject.GetComponent<TooltipHandler>();
                if (handler == null)
                    return "无 handler";
                return UiTooltip.Describe(bp.gameObject);
            }
            catch (Exception e)
            {
                return "（" + e.GetType().Name + "）";
            }
        }
    }
}
