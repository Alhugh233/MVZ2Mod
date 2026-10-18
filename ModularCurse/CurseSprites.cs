using System;
using System.Collections.Generic;
using System.IO;
using MVZ2.Managers;
using MVZ2Logic.Resources;
using PVZEngine;
using UnityEngine;

namespace ModularCurse
{
    /// <summary>
    /// 把本 mod 自带的 PNG 载入游戏资源库。
    ///
    /// <para><b>为什么需要它</b>：0.7.0 的图片走 Addressables 的 SpriteManifest，
    /// 磁盘上的 mod 资源目录（<c>StreamingAssets/Mods/&lt;nsp&gt;/</c>）只被 DSHCore
    /// 用于 XML metas，PNG 不会自动进资源库。所以这里读 PNG →
    /// <c>Texture2D</c> → <c>Sprite.Create</c> → <c>ResourceManager.RegisterSprite</c>
    /// —— 写进去的正是 <c>ResourceManager.GetSprite</c> 查询的那张表
    /// （两边都用不带 tags 的 <c>SpriteLookupKey</c>），在游戏侧等同于"内嵌进游戏"。</para>
    ///
    /// <para><b>命名约定（唯一的规则）</b>：<c>Content/sprites/</c> 下的
    /// <b>相对路径就是 sprite id 的路径</b>，索引恒为 0。例如</para>
    /// <code>
    /// Content/sprites/curse/QC/INJURY.png
    ///   → new SpriteReference(new NamespaceID("modular_curse", "curse/QC/INJURY"), 0)
    /// </code>
    /// <para>于是 curses.xml 里直接写
    /// <c>&lt;sprite id="modular_curse:curse/QC/INJURY" /&gt;</c> 就能用。
    /// <see cref="RegisterAll"/> 递归扫描整个目录并**按实际路径**注册，
    /// 所以以后加图只要把 PNG 丢进 <c>Content/sprites/</c> 的任意子目录即可，不用改代码
    /// —— 只要 curses.xml 里的 id 与磁盘路径逐字符一致（大小写也算）。</para>
    ///
    /// <para>为什么 PPU 取 100：诅咒条目与池条目都是 UI 的 <c>Image</c>，
    /// 尺寸由 RectTransform 决定（100×100 那张本来就已经在用这个值），保持一致即可。</para>
    /// </summary>
    internal static class CurseSprites
    {
        /// <summary>mod 资源里的贴图根目录（相对 StreamingAssets/Mods/&lt;nsp&gt;）。</summary>
        public const string SpriteRoot = "Content/sprites/";

        /// <summary>已扫描并注册过（幂等标记）。</summary>
        private static bool registered;

        /// <summary>已经警告过"文件不存在"的 id，避免每帧刷屏。</summary>
        private static readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// 递归扫描 <c>Content/sprites/**/*.png</c> 并全部注册进资源库（幂等）。
        ///
        /// 调用时机：<c>ModularCurseMod.PostInit</c> 一次即可 ——
        /// <c>ResourceManager.Init()</c> 只在启动时跑一次，中途不会 <c>ClearResources</c>，
        /// 所以注册一次就能撑到关游戏。若那次 MainManager 还没准备好（返回 null），
        /// 标记不置位，下次调用会重试。
        /// </summary>
        public static void RegisterAll()
        {
            if (registered)
                return;
            try
            {
                var main = MainManager.Instance;
                if (main == null || main.ResourceManager == null)
                    return; // 还没准备好，留待下次

                var fullRoot = FullRoot();
                if (!Directory.Exists(fullRoot))
                {
                    Plugin.Log?.LogWarning("[CurseSprites] 找不到贴图目录: " + fullRoot);
                    return;
                }

                var ok = 0;
                var failed = 0;
                foreach (var file in Directory.GetFiles(fullRoot, "*.png", SearchOption.AllDirectories))
                {
                    var id = ToRelativeId(fullRoot, file);
                    if (string.IsNullOrEmpty(id))
                        continue;
                    if (LoadAndRegister(id, null) != null)
                        ok++;
                    else
                        failed++;
                }
                registered = true;
                Plugin.Log?.LogInfo("[CurseSprites] 已注册 " + ok + " 张贴图"
                    + (failed > 0 ? "（失败 " + failed + " 张）" : "")
                    + "，根目录 " + fullRoot);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CurseSprites] RegisterAll 失败: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 载入**一张**贴图（相对 <c>Content/sprites/</c> 的路径，含扩展名）并注册。
        /// 供 <see cref="CursePoolIcons"/> 这类"直接要 Sprite 对象"的地方使用。
        /// </summary>
        public static Sprite TryLoad(string relativePath, string spriteName = null)
        {
            if (string.IsNullOrEmpty(relativePath))
                return null;
            var id = relativePath.Replace('\\', '/');
            var dot = id.LastIndexOf('.');
            if (dot >= 0)
                id = id.Substring(0, dot);
            return LoadAndRegister(id, spriteName);
        }

        /// <summary>
        /// 按 sprite id 路径（相对 <c>Content/sprites/</c>，**不带**扩展名）载入并注册。
        /// </summary>
        private static Sprite LoadAndRegister(string id, string spriteName)
        {
            try
            {
                var path = Dir() + id + ".png";
                if (!File.Exists(path))
                {
                    if (warned.Add(id))
                        Plugin.Log?.LogWarning("[CurseSprites] 找不到贴图: " + path);
                    return null;
                }
                var bytes = File.ReadAllBytes(path);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(texture, bytes))
                {
                    if (warned.Add(id))
                        Plugin.Log?.LogWarning("[CurseSprites] 解析 PNG 失败: " + path);
                    return null;
                }
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                texture.hideFlags = HideFlags.DontUnloadUnusedAsset;

                var sprite = Sprite.Create(texture,
                    new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f), 100f);
                sprite.name = string.IsNullOrEmpty(spriteName) ? id : spriteName;
                sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;

                var main = MainManager.Instance;
                if (main != null && main.ResourceManager != null)
                {
                    var spriteRef = new SpriteReference(new NamespaceID(CurseIds.Nsp, id), 0);
                    main.ResourceManager.RegisterSprite(spriteRef, sprite);
                }
                return sprite;
            }
            catch (Exception e)
            {
                if (warned.Add(id))
                    Plugin.Log?.LogWarning("[CurseSprites] 载入 " + id + " 失败: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>贴图根目录的绝对路径（结尾带分隔符）。</summary>
        private static string FullRoot()
        {
            var full = Path.GetFullPath(Dir());
            var sep = Path.DirectorySeparatorChar;
            if (!full.EndsWith(sep.ToString(), StringComparison.Ordinal))
                full += sep;
            return full;
        }

        /// <summary>把绝对文件路径换算成 sprite id（相对路径、正斜杠、去扩展名）。</summary>
        private static string ToRelativeId(string fullRoot, string file)
        {
            var full = Path.GetFullPath(file);
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                return null;
            var id = full.Substring(fullRoot.Length).Replace('\\', '/');
            var dot = id.LastIndexOf('.');
            if (dot >= 0)
                id = id.Substring(0, dot);
            return id;
        }

        /// <summary>StreamingAssets 下的贴图目录（正斜杠）。</summary>
        private static string Dir()
        {
            var root = Application.streamingAssetsPath;
            if (string.IsNullOrEmpty(root))
                root = ".";
            return root + "/Mods/" + CurseIds.Nsp + "/" + SpriteRoot;
        }
    }
}
