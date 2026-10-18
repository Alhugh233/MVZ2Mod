using System;
using Il2CppInterop.Runtime.InteropTypes;
using MVZ2.Managers;
using MVZ2.Metas;
using MVZ2Logic.Curses;
using MVZ2Logic.Definitions;
using PVZEngine;

namespace ModularCurse
{
    /// <summary>
    /// 把 curses.xml 的词条属性重新写到诅咒定义上。
    ///
    /// 背景（与 LaboratoryMod 的 StageMetaApplier 同源）：
    ///   官方 LoadCurseMetas 创建 DefaultCurseDefinition 后会立刻
    ///   SetCurseName / SetBuffID / SetUnlockConditions …，
    ///   但那一刻 **MVZ2.Logic 的属性键可能还没注册**（PropertyMapper 按
    ///   mod 顺序扫描程序集）。未注册的键 Key==0，写入会落到无效键上，
    ///   之后用正确的键读取就全部落空 —— 表现为诅咒名称为空、BUFF_ID 为 null、
    ///   unlock 条件为 null（于是永远不会出现在诅咒面板里）。
    ///
    /// 对策：在属性键确定已注册的时机（PostInit / SaveManager.EvaluateUnlocks）
    ///   用 XML 词条再套一遍。写入的是同一个 Definition 对象，
    ///   而它已经在 game.AddMod 时进入全局内容表，因此立刻生效。
    /// </summary>
    internal static class CurseMetaApplier
    {
        private static bool applied;

        /// <summary>
        /// 重新应用全部诅咒词条属性。幂等，可重复调用
        /// （SaveManager 每次重算解锁都会先跑一遍，确保时序上必然生效）。
        /// </summary>
        public static void ApplyAll()
        {
            try
            {
                var main = MainManager.Instance;
                if (main == null)
                    return;
                var res = main.ResourceManager;
                if (res == null)
                    return;
                var metas = res.GetModCurseMetas(ModularCurseMod.spaceName);
                if (metas == null || metas.Length == 0)
                {
                    Plugin.Log?.LogWarning("[CurseMetaApplier] curses.xml 词条为空");
                    return;
                }

                var count = 0;
                for (var i = 0; i < metas.Length; i++)
                {
                    var meta = metas[i];
                    if (meta == null)
                        continue;
                    var def = FindDefinition(meta.ID);
                    if (def == null)
                        continue;
                    Apply(def, meta);
                    count++;
                }
                if (!applied)
                {
                    applied = true;
                    Plugin.Log?.LogInfo("[CurseMetaApplier] 已套用 " + count + " 条诅咒词条属性");
                    LogSpriteDiag(metas, false);
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[CurseMetaApplier] ApplyAll failed: " + e);
            }
        }

        /// <summary>
        /// 在"资源已经全部加载完"之后（= 选卡界面起来时）再查一次图标。
        /// 和 PostInit 那次不同：此时连原版 Addressables 贴图也已就绪，
        /// 所以这个 N/M 就是玩家真正会看到的图标数。
        /// </summary>
        public static void LogIconDiag()
        {
            try
            {
                var main = MainManager.Instance;
                if (main == null || main.ResourceManager == null)
                    return;
                var metas = main.ResourceManager.GetModCurseMetas(ModularCurseMod.spaceName);
                if (metas == null || metas.Length == 0)
                    return;
                LogSpriteDiag(metas, true);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CurseMetaApplier] 图标复查失败: " + e.GetType().Name);
            }
        }

        /// <summary>
        /// 图标自检（与诅咒面板**完全相同**的那条链：
        /// <c>Main.GetSprite(def.GetSpriteReference())</c>，见
        /// <c>LevelBlueprintChooseController.GetCurseIcon</c>）。
        ///
        /// <paramref name="warnAll"/> 为 false 时只在**本 mod 命名空间**的贴图上告警：
        /// 原版贴图（SC/ZY 前 7 条复用的 `mvz2:curses/*`）来自 Addressables 的
        /// SpriteManifest，而那份是 <c>LoadAllModResourcesMain</c> 加载的、**晚于** PostInit，
        /// 此刻查不到很正常。本 mod 自己的贴图是 <see cref="CurseSprites"/> 刚注册进去的，
        /// 查不到就是真缺图。
        /// </summary>
        private static void LogSpriteDiag(CurseMeta[] metas, bool warnAll)
        {
            try
            {
                var main = MainManager.Instance;
                if (main == null)
                    return;
                var declared = 0;
                var resolved = 0;
                for (var i = 0; i < metas.Length; i++)
                {
                    var meta = metas[i];
                    if (meta == null || meta.Sprite == null)
                        continue;
                    declared++;
                    if (main.GetSprite(meta.Sprite) != null)
                    {
                        resolved++;
                        continue;
                    }
                    var text = meta.Sprite.ToString() ?? "<null>";
                    var ours = text.StartsWith(CurseIds.Nsp + ":", StringComparison.Ordinal);
                    if (warnAll || ours)
                    {
                        Plugin.Log?.LogWarning("[CurseMetaApplier] 图标缺失: " + meta.ID
                            + " -> " + text + "（本 mod 的图请检查 Content/sprites/ 下是否已部署）");
                    }
                }
                Plugin.Log?.LogInfo("[CurseMetaApplier] 图标解析 " + resolved + "/" + declared
                    + (warnAll ? "（资源已全部加载）" : "（PostInit，原版贴图尚未加载）"));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CurseMetaApplier] 图标自检失败: " + e.GetType().Name);
            }
        }

        /// <summary>把一个词条写到定义上（等价于官方 LoadCurseMetas 的赋值段落）。</summary>
        private static void Apply(CurseDefinition def, CurseMeta meta)
        {
            // 代理不复制接口声明：XMLConditionList 在托管侧看不到 IConditionList，
            // 需要显式包一层（官方代码两侧都是原生，因此没这个问题）。
            var unlock = meta.UnlockConditions;
            def.SetUnlockConditions(unlock == null
                ? null
                : new MVZ2Logic.Conditions.IConditionList(unlock.Pointer));
            def.SetBuffID(meta.BuffID);
            def.SetCurseName(meta.Name);
            def.SetCurseTooltip(meta.Tooltip);
            if (meta.Sprite != null)
                def.SetSpriteReference(meta.Sprite);
        }

        /// <summary>
        /// 在本 mod 已登记的诅咒定义里按 path 找（绕开原生 LINQ）。
        ///
        /// 注意：不能写 <c>def is CurseDefinition</c> —— Harmony 传入 AddDefinition
        /// 的参数按**声明类型** Definition 包装，托管侧看不到 DefaultCurseDefinition
        /// 这个具体类型，因此 is 恒为 false。改按 GetDefinitionType() 判别 + TryCast。
        /// </summary>
        private static CurseDefinition FindDefinition(string path)
        {
            lock (Plugin.AllDefinitions)
            {
                for (var i = 0; i < Plugin.AllDefinitions.Count; i++)
                {
                    var def = Plugin.AllDefinitions[i];
                    if (def == null)
                        continue;
                    string type;
                    try
                    {
                        type = def.GetDefinitionType();
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (type != LogicDefinitionTypes.CURSE)
                        continue;
                    var id = def.GetID();
                    if (id == null || id.Path != path)
                        continue;
                    return def.TryCast<CurseDefinition>();
                }
            }
            return null;
        }
    }
}
