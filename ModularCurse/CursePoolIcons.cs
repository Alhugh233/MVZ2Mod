using System;
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
    /// 四个池的图标来源：
    ///   - 原版池：**直接用原版诅咒入口按钮上那颗图标**（`ui/level/curse.png`）。
    ///     运行时从按钮子节点 <c>Image</c> 上把 <c>sprite</c> 取过来，
    ///     所以永远与按钮图标是同一张贴图（按钮换皮也跟着变）；
    ///   - QC / SC / ZY 池：随 mod 一起发布的 PNG，放在
    ///     <c>StreamingAssets/Mods/modular_curse/Content/sprites/</c>。
    ///     载入与注册统一交给 <see cref="CurseSprites"/>（见那里的命名约定）。
    ///
    /// 渲染方式也照抄按钮：按钮那颗图标不是白图，而是**给 Image 上一层紫色 tint**
    /// （prefab 里 <c>m_Color = (0.635, 0, 1, 1)</c>）。这里把同一个颜色读出来，
    /// 同样以 tint 的方式作用在池图标的 Image 上 —— 于是"紫色滤镜"与原版一致。
    ///
    /// 懒加载：需要 MainManager.Instance 与诅咒入口按钮，所以第一次进选卡界面时才准备。
    /// </summary>
    internal static class CursePoolIcons
    {
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
        private static Sprite zyIcon;
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
                // 逐条诅咒的图标也要一起注册（幂等；正常情况下 PostInit 已经做过）。
                CurseSprites.RegisterAll();
                // 此时资源已全部加载完，可以真正查一次"图标会不会显示"。
                CurseMetaApplier.LogIconDiag();
                if (!TakeFromEntranceButton(ctl))
                    vanillaIcon = LoadFallbackVanilla();
                if (vanillaIcon == null)
                    return; // 还没准备好（界面没起来），下次再试
                qcIcon = LoadPng("curse_qc.png", "curse_qc");
                scIcon = LoadPng("curse_sc.png", "curse_sc");
                zyIcon = LoadPng("curse_3q4a.png", "curse_3q4a");
                prepared = true;
                Plugin.Log?.LogInfo("[CursePool] 图标已准备：原版 " + (vanillaIcon != null)
                    + " / QC " + (qcIcon != null) + " / SC " + (scIcon != null)
                    + " / ZY " + (zyIcon != null)
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
                case CursePool.Zy: return zyIcon;
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

        /// <summary>池图标（文件名相对 Content/sprites/）。</summary>
        private static Sprite LoadPng(string file, string spriteName)
        {
            var sprite = CurseSprites.TryLoad(file, spriteName);
            if (sprite == null)
                Plugin.Log?.LogWarning("[CursePool] 找不到图标文件: " + file);
            return sprite;
        }

        private static string F(float v)
        {
            return v.ToString("0.00");
        }
    }
}
