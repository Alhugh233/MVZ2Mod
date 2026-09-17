using System;
using System.IO;
using MVZ2.Level;
using MVZ2.Managers;
using MVZ2Logic.Resources;
using PVZEngine;
using UnityEngine;
using UnityEngine.UI;

namespace ModularCurse
{
    /// <summary>
    /// 诅咒池的图标。
    ///
    /// 三个池的图标来源：
    ///   - 原版池：**直接用原版诅咒入口按钮上那颗图标**（`ui/level/curse.png`）。
    ///     运行时从按钮子节点 <c>Image</c> 上把 <c>sprite</c> 取过来，
    ///     所以永远与按钮图标是同一张贴图（按钮换皮也跟着变）；
    ///   - QC / SC 池：随 mod 一起发布的 PNG，放在
    ///     <c>StreamingAssets/Mods/modular_curse/Content/sprites/</c>。
    ///
    /// 渲染方式也照抄按钮：按钮那颗图标不是白图，而是**给 Image 上一层紫色 tint**
    /// （prefab 里 <c>m_Color = (0.635, 0, 1, 1)</c>）。这里把同一个颜色读出来，
    /// 同样以 tint 的方式作用在池图标的 Image 上 —— 于是"紫色滤镜"与原版一致。
    ///
    /// 为什么 QC/SC 要自己 RegisterSprite：
    ///   0.7.0 的图片走 Addressables 的 SpriteManifest，磁盘上的 mod 资源目录
    ///   只被 DSHCore 用于 XML metas，图片不会自动进资源库。所以这里读 PNG →
    ///   <c>Texture2D</c> → <c>Sprite.Create</c> →
    ///   <c>ResourceManager.RegisterSprite(SpriteReference, Sprite)</c>
    ///   —— 写进去的正是 <c>ResourceManager.GetSprite</c> 查询的那张表
    ///   （两边都用不带 tags 的 <c>SpriteLookupKey</c>），在游戏侧等同于"内嵌进游戏"。
    ///
    /// 懒加载：需要 MainManager.Instance 与诅咒入口按钮，所以第一次进选卡界面时才准备。
    /// </summary>
    internal static class CursePoolIcons
    {
        /// <summary>mod 资源里的相对目录（相对 StreamingAssets/Mods/&lt;nsp&gt;）。</summary>
        private const string SpriteDir = "Content/sprites/";

        /// <summary>原版诅咒入口按钮里那颗图标所在的子节点名。</summary>
        private const string EntranceIconNode = "Image";

        /// <summary>取不到按钮图标时的兜底（原版诅咒列表里前两个）。</summary>
        private static readonly string[] FallbackVanillaIcons =
        {
            "curses/the_creatures_brain",
            "curses/nightmares_tentacle",
        };

        private static bool prepared;
        private static Sprite vanillaIcon;
        private static Sprite qcIcon;
        private static Sprite scIcon;
        private static Color tint = Color.white;

        /// <summary>池图标叠加的 tint（= 原版诅咒按钮图标那个紫色滤镜）。</summary>
        public static Color Tint => tint;

        /// <summary>进选卡界面时调用：把三个图标准备好（幂等）。</summary>
        public static void Prepare(LevelBlueprintChooseController ctl)
        {
            if (prepared)
                return;
            try
            {
                var main = MainManager.Instance;
                if (main == null)
                    return;
                if (!TakeFromEntranceButton(ctl))
                    vanillaIcon = LoadFallbackVanilla();
                if (vanillaIcon == null)
                    return; // 还没准备好（界面没起来），下次再试
                qcIcon = LoadPng("curse_qc.png", "curse_qc");
                scIcon = LoadPng("curse_sc.png", "curse_sc");
                prepared = true;
                Plugin.Log?.LogInfo("[CursePool] 图标已准备：原版 " + (vanillaIcon != null)
                    + " / QC " + (qcIcon != null) + " / SC " + (scIcon != null)
                    + "，紫色滤镜 " + F(tint.r) + "," + F(tint.g) + "," + F(tint.b));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CursePool] 图标准备失败: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>取某个池的图标；取不到时返回 null（调用方保留原图）。</summary>
        public static Sprite Get(CursePool pool)
        {
            switch (pool)
            {
                case CursePool.Qc: return qcIcon;
                case CursePool.Sc: return scIcon;
                default: return vanillaIcon;
            }
        }

        /// <summary>从原版诅咒入口按钮的图标节点上取 sprite 与 tint。</summary>
        private static bool TakeFromEntranceButton(LevelBlueprintChooseController ctl)
        {
            try
            {
                var chooseUI = ctl == null ? null : ctl.chooseUI;
                var entrance = chooseUI == null ? null : chooseUI.GetCursePanelEntranceButton();
                if (entrance == null)
                    return false;
                var node = entrance.transform.Find(EntranceIconNode);
                if (node == null)
                    return false;
                var image = node.GetComponent<Image>();
                if (image == null || image.sprite == null)
                    return false;
                vanillaIcon = image.sprite;
                tint = image.color;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CursePool] 读取诅咒按钮图标失败: " + e.GetType().Name);
                return false;
            }
        }

        private static Sprite LoadFallbackVanilla()
        {
            try
            {
                var main = MainManager.Instance;
                if (main == null)
                    return null;
                for (var i = 0; i < FallbackVanillaIcons.Length; i++)
                {
                    var id = new NamespaceID(main.BuiltinNamespace, FallbackVanillaIcons[i]);
                    var sprite = main.GetSprite(new SpriteReference(id, 0));
                    if (sprite != null)
                        return sprite;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static Sprite LoadPng(string file, string spriteName)
        {
            var path = Dir() + file;
            if (!File.Exists(path))
            {
                Plugin.Log?.LogWarning("[CursePool] 找不到图标文件: " + path);
                return null;
            }
            var bytes = File.ReadAllBytes(path);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, bytes))
            {
                Plugin.Log?.LogWarning("[CursePool] 解析 PNG 失败: " + file);
                return null;
            }
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            var sprite = Sprite.Create(texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f), 100f);
            sprite.name = spriteName;
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;

            // 注册进游戏的资源库：之后任何代码都能用
            // new SpriteReference(new NamespaceID("modular_curse", spriteName), 0) 取到它。
            var main = MainManager.Instance;
            if (main != null && main.ResourceManager != null)
            {
                var spriteRef = new SpriteReference(new NamespaceID(CurseIds.Nsp, spriteName), 0);
                main.ResourceManager.RegisterSprite(spriteRef, sprite);
            }
            return sprite;
        }

        private static string Dir()
        {
            var root = Application.streamingAssetsPath;
            if (string.IsNullOrEmpty(root))
                root = ".";
            return root + "/Mods/" + CurseIds.Nsp + "/" + SpriteDir;
        }

        private static string F(float v)
        {
            return v.ToString("0.00");
        }
    }
}
