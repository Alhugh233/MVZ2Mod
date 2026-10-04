using System;
using System.Collections.Generic;
using PVZEngine;

namespace ModularCurse
{
    /// <summary>
    /// 全部诅咒的命名空间 ID 缓存。
    ///
    /// 约定：诅咒 ID、buff ID、curses.xml / buffs.xml 里的 id 三者同名，
    /// 形如 <c>modular_curse:qc_c1</c> —— 与原版诅咒（<c>mvz2:the_creatures_brain</c>）
    /// 的结构一致。
    /// </summary>
    internal static class CurseIds
    {
        public const string Nsp = "modular_curse";

        /// <summary>QC 池 12 条。</summary>
        public static readonly string[] Qc =
        {
            "qc_c1", "qc_c2", "qc_c3", "qc_c4", "qc_c5", "qc_c6",
            "qc_c7", "qc_c8", "qc_c9", "qc_c10", "qc_c11", "qc_c12",
        };

        /// <summary>SC 池 16 条。</summary>
        public static readonly string[] Sc =
        {
            "sc_c1", "sc_c2", "sc_c3", "sc_c4", "sc_c5", "sc_c6", "sc_c7", "sc_c8",
            "sc_c9", "sc_c10", "sc_c11", "sc_c12", "sc_c13", "sc_c14", "sc_c15", "sc_c16",
        };

        /// <summary>ZY 池 16 条（咒鵺兼程 / 3q4a 的诅咒）。</summary>
        public static readonly string[] Zy =
        {
            "zy_c1", "zy_c2", "zy_c3", "zy_c4", "zy_c5", "zy_c6", "zy_c7", "zy_c8",
            "zy_c9", "zy_c10", "zy_c11", "zy_c12", "zy_c13", "zy_c14", "zy_c15", "zy_c16",
        };

        private static readonly Dictionary<string, NamespaceID> cache
            = new Dictionary<string, NamespaceID>(StringComparer.Ordinal);

        /// <summary>取（并缓存）诅咒 ID。热路径请自行缓存到静态字段。</summary>
        public static NamespaceID Get(string path)
        {
            if (cache.TryGetValue(path, out var id))
                return id;
            id = new NamespaceID(Nsp, path);
            cache[path] = id;
            return id;
        }
    }
}
