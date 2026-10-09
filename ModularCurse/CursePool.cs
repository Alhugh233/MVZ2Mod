using System;
using PVZEngine;

namespace ModularCurse
{
    /// <summary>
    /// 诅咒池。玩家在选卡界面里点开诅咒弹窗、在滚动区顶部点池条目选择，
    /// 选中后诅咒面板只列出该池的诅咒。
    ///
    /// 池是**单选且必选**的：没有「全部」这一档，面板里永远只会有某一个池的诅咒。
    /// </summary>
    internal enum CursePool
    {
        /// <summary>原版池：只有 mvz2 命名空间的原生诅咒。</summary>
        Vanilla = 0,
        /// <summary>QC 池：modular_curse 的 qc_c1..qc_c12（QoZnoS 的诅咒）。</summary>
        Qc = 1,
        /// <summary>SC 池：modular_curse 的 sc_c1..sc_c16（奇硅箱的诅咒）。</summary>
        Sc = 2,
        /// <summary>ZY 池：modular_curse 的 zy_c1..zy_c16（3q4a 的诅咒）。</summary>
        Zy = 3,
    }

    /// <summary>诅咒池的规则：顺序、名称、作者描述、成员判定。</summary>
    internal static class CursePoolRules
    {
        /// <summary>选择框里的展示顺序（同时也是索引顺序）。</summary>
        public static readonly CursePool[] Pools =
        {
            CursePool.Vanilla, CursePool.Qc, CursePool.Sc, CursePool.Zy,
        };

        /// <summary>没有记录时的默认池：原版池（等价于未装本 mod 时面板里的内容）。</summary>
        public const CursePool Default = CursePool.Vanilla;

        public static bool IsValid(CursePool pool)
        {
            return pool >= CursePool.Vanilla && pool <= CursePool.Zy;
        }

        public static CursePool FromIndex(int index)
        {
            if (index < 0 || index >= Pools.Length)
                return Default;
            return Pools[index];
        }

        public static int IndexOf(CursePool pool)
        {
            for (var i = 0; i < Pools.Length; i++)
            {
                if (Pools[i] == pool)
                    return i;
            }
            return 0;
        }

        /// <summary>短标签（日志与调试用）。</summary>
        public static string Label(CursePool pool)
        {
            switch (pool)
            {
                case CursePool.Qc: return "QC";
                case CursePool.Sc: return "SC";
                case CursePool.Zy: return "ZY";
                default: return "原";
            }
        }

        /// <summary>完整名称。</summary>
        public static string Name(CursePool pool)
        {
            switch (pool)
            {
                case CursePool.Qc: return "QC 池";
                case CursePool.Sc: return "SC 池";
                case CursePool.Zy: return "咒鵺兼程";
                default: return "原版池";
            }
        }

        /// <summary>池的作者描述（选择框里的提示文本）。</summary>
        public static string Description(CursePool pool)
        {
            switch (pool)
            {
                case CursePool.Qc: return "QoZnoS的诅咒";
                case CursePool.Sc: return "奇硅箱的诅咒";
                case CursePool.Zy: return "3q4a的诅咒";
                default: return "原版诅咒";
            }
        }

        /// <summary>
        /// 池的**本地化**名称/描述（给界面用；日志请继续用 <see cref="Name"/> / <see cref="Description"/>，
        /// 免得排查问题时要先猜当时是哪种语言）。
        /// </summary>
        public static string NameText(CursePool pool)
        {
            return DSHCore.Ui.UiLocale.Get(Name(pool));
        }

        public static string DescriptionText(CursePool pool)
        {
            return DSHCore.Ui.UiLocale.Get(Description(pool));
        }

        /// <summary>该诅咒是否属于这个池。</summary>
        public static bool Matches(CursePool pool, NamespaceID id)
        {
            if (id == null)
                return false;
            if (pool == CursePool.Vanilla)
                return id.SpaceName == BuiltinNamespace;
            if (id.SpaceName != CurseIds.Nsp)
                return false;
            if (pool == CursePool.Qc)
                return HasPoolPrefix(id.Path, "qc_c");
            if (pool == CursePool.Sc)
                return HasPoolPrefix(id.Path, "sc_c");
            return HasPoolPrefix(id.Path, "zy_c");
        }

        /// <summary>路径形如 qc_c1 / sc_c12（前缀之后必须还有内容）。</summary>
        private static bool HasPoolPrefix(string path, string prefix)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            return path.Length > prefix.Length;
        }

        /// <summary>内置命名空间（mvz2）。</summary>
        public static string BuiltinNamespace
        {
            get
            {
                try
                {
                    return MVZ2Logic.Global.BuiltinNamespace;
                }
                catch (Exception)
                {
                    return "mvz2";
                }
            }
        }
    }
}
