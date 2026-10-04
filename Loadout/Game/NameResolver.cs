using System;
using System.Collections.Generic;
using MVZ2.Managers;
using MVZ2.Vanilla;
using MVZ2Logic;
using MVZ2Logic.Games;
using MVZ2Logic.Level;
using PVZEngine;
using PVZEngine.Entities;
using PVZEngine.Level;

namespace Loadout
{
    /// <summary>
    /// 把 <see cref="NamespaceID"/> 换成**玩家看得懂的名字**。
    ///
    /// 一律优先走游戏自己的访问器（它们内部已经处理了"XML 里的 name 是翻译键/字面量"
    /// 与多语言包查找），拿不到才退化成 id：
    /// <list type="bullet">
    /// <item>实体/器械：<c>ResourceManager.GetEntityName</c></item>
    /// <item>蓝图：<c>ResourceManager.GetBlueprintName</c></item>
    /// <item>制品：<c>ResourceManager.GetArtifactName</c></item>
    /// <item>音乐：<c>ResourceManager.GetMusicName</c></item>
    /// <item>关卡：<c>LevelManager.GetStageName</c>（会按 <c>LogicStrings.CONTEXT_LEVEL_NAME</c> 翻译）</item>
    /// <item>难度：<c>IGlobalGame.GetDifficultyName</c></item>
    /// </list>
    ///
    /// ⚠️ **区域（背景）在 XML 里没有 name 属性**（<c>AreaMeta</c> 也没有 Name 字段，
    /// 只有 ModelID / MusicID / Tags…），所以背景只能显示 id —— 这不是漏做，
    /// 是原版数据里就没有可显示的名字。
    ///
    /// 结果做了缓存：下拉框每帧都在刷新，不缓存会一直做字符串分配。
    /// </summary>
    internal static class NameResolver
    {
        private static readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>上一次解析时的语言，语言一变就清缓存（多语言切换后要重新取）。</summary>
        private static string lastLanguage;

        public static void ClearCache()
        {
            cache.Clear();
        }

        private static void CheckLanguage()
        {
            try
            {
                var main = MainManager.Instance;
                if (main == null || main.LanguageManager == null)
                    return;
                var lang = main.LanguageManager.GetCurrentLanguage();
                if (lang == lastLanguage)
                    return;
                lastLanguage = lang;
                cache.Clear();
            }
            catch (Exception)
            {
            }
        }

        private static string Key(string kind, NamespaceID id)
        {
            if (id == null)
                return null;
            return kind + "|" + id.SpaceName + ":" + id.Path;
        }

        private static string Cached(string kind, NamespaceID id, Func<string> resolve)
        {
            var key = Key(kind, id);
            if (key == null)
                return "（无）";
            if (cache.TryGetValue(key, out var hit))
                return hit;
            string text;
            try
            {
                text = resolve();
            }
            catch (Exception)
            {
                text = null;
            }
            if (string.IsNullOrEmpty(text))
                text = id.ToString();
            cache[key] = text;
            return text;
        }

        public static string Entity(NamespaceID id)
        {
            CheckLanguage();
            return Cached("entity", id, () =>
            {
                var main = MainManager.Instance;
                if (main == null || main.ResourceManager == null)
                    return null;
                return main.ResourceManager.GetEntityName(id);
            });
        }

        public static string Blueprint(NamespaceID id, bool commandBlock)
        {
            CheckLanguage();
            return Cached(commandBlock ? "bp_cmd" : "blueprint", id, () =>
            {
                var main = MainManager.Instance;
                if (main == null || main.ResourceManager == null)
                    return null;
                return main.ResourceManager.GetBlueprintName(id, commandBlock);
            });
        }

        public static string Artifact(NamespaceID id)
        {
            CheckLanguage();
            return Cached("artifact", id, () =>
            {
                var main = MainManager.Instance;
                if (main == null || main.ResourceManager == null)
                    return null;
                return main.ResourceManager.GetArtifactName(id);
            });
        }

        public static string Music(NamespaceID id)
        {
            CheckLanguage();
            return Cached("music", id, () =>
            {
                var main = MainManager.Instance;
                if (main == null || main.ResourceManager == null)
                    return null;
                return main.ResourceManager.GetMusicName(id);
            });
        }

        public static string Stage(NamespaceID id)
        {
            CheckLanguage();
            return Cached("stage", id, () =>
            {
                var main = MainManager.Instance;
                if (main == null || main.LevelManager == null)
                    return null;
                return main.LevelManager.GetStageName(id);
            });
        }

        public static string Difficulty(NamespaceID id)
        {
            CheckLanguage();
            return Cached("difficulty", id, () =>
            {
                var game = Global.Game;
                if (game == null)
                    return null;
                return game.GetDifficultyName(id);
            });
        }

        /// <summary>
        /// 区域 / 章节名。
        ///
        /// ⚠️ 原版数据里**根本没有名字**：<c>AreaMeta</c> 只读 model/music/cart/tags/lighting/grids，
        /// <c>MapMeta</c> 只读 area/size/presets/stages —— 两者都没有 <c>name</c> 属性，
        /// 也没有任何 <c>GetAreaName</c> 之类的本地化访问器。所以这里内置一张原版章节名表。
        ///
        /// 非原版 namespace 的项（mod 新增背景）退化成显示 id，不会看起来像漏做。
        /// </summary>
        public static string Area(NamespaceID id)
        {
            if (id == null)
                return "（无）";
            try
            {
                if (id.SpaceName == VanillaMod.spaceName)
                {
                    string name;
                    if (VanillaChapterNames.TryGetValue(id.Path, out name))
                        return name;
                }
            }
            catch (Exception)
            {
            }
            return id.ToString();
        }

        /// <summary>
        /// 原版背景 id → 章节名。这张表只能手写（见 <see cref="Area"/> 的说明）。
        /// 顺序与游戏主线一致：序章 → 万圣夜 → 梦境世界 → 奎针城 → 梦殿大祀庙 → 圣辇船 → 地灵殿 → 妖怪之山，
        /// 另有陵墓小游戏（解谜模式）。
        /// </summary>
        private static readonly Dictionary<string, string> VanillaChapterNames =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "day", "序章" },
                { "halloween", "万圣夜" },
                { "dream", "梦境世界" },
                { "castle", "辉针城" },
                { "mausoleum", "梦殿大祀庙" },
                { "ship", "圣辇船" },
                { "palace", "地灵殿" },
                { "mountain", "妖怪之山" },
                { "mausoleum_minigame", "解谜模式" },
            };

        /// <summary>通用：用于实体定义（枚举 EntityDefinition 时用）。</summary>
        public static string EntityDefinition(EntityDefinition def)
        {
            if (def == null)
                return "（无）";
            return Entity(def.GetID());
        }

        /// <summary>下拉框标签统一格式：<c>名称</c>（名字与 id 不同时才附带 id，方便排障）。</summary>
        public static string ForDropdown(string name, NamespaceID id)
        {
            if (id == null)
                return name ?? "（无）";
            var idText = id.ToString();
            if (string.IsNullOrEmpty(name) || name == idText)
                return idText;
            return name + "  (" + idText + ")";
        }
    }
}
