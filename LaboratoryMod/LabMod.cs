using System;
using CustomCore;
using Il2CppInterop.Runtime.Injection;
using Il2CppSystem.Collections.Generic;
using MVZ2Logic.Games;
using MVZ2Logic.Modding;
using MVZ2Logic.Saves;
using PVZEngine;
using PVZEngine.Callbacks;

namespace LaboratoryMod
{
    /// <summary>
    /// 实验室内容 Mod 的 Mod 逻辑主体（0.7 官方链版）。
    ///
    /// 0.7 中 LoadMods 不再重建 Logic：ModInfo.Logic 直接使用本注入实例，
    /// 官方 ModLoader.LoadAll 会对它依次调用 Preload/PreInit/Load/OnInit/AddMod/PostInit。
    /// - PreInit 注册 IStageDefinitionProvider（minigame 关卡由官方 LoadStages 创建）。
    /// - 关卡定义 ID 落在本 mod 命名空间（mvz2_lab:xxx），XML meta 的属性由
    ///   官方 LoadStageProperties 自动应用（无需再手动补 ENEMY_POOL 等）。
    /// </summary>
    public class LabMod : Mod
    {
        public const string spaceName = "mvz2_lab";

        /// <summary>注入类标准绑定 ctor（Il2CppInterop 官方模式）。</summary>
        public LabMod(IntPtr ptr) : base(ptr)
        {
        }

        /// <summary>
        /// 创建绑定注入类（klass=LabMod）的实例，并补做原生 Mod(string) ctor 的初始化。
        /// </summary>
        internal static LabMod CreateInstance()
        {
            var instance = new LabMod(ClassInjector.DerivedConstructorPointer<LabMod>());
            ClassInjector.DerivedConstructorBody(instance);
            instance._Namespace_k__BackingField = spaceName;
            instance.definitionGroup = new DefinitionGroup();
            instance.triggers = new List<ITrigger>();
            return instance;
        }

        public override void PreInit(IModLoader modLoader)
        {
            // 注意：绝不能调 base.PreInit(modLoader)！代理虚调用会回到本方法 trampoline
            // 无限递归直到 native 栈溢出。原生 Mod.PreInit 是空实现，跳过无影响。
            //
            // 0.7 中 ModLoader.LoadAll 会对 stageProviders.OrderBy(Priority) 做原生 LINQ
            // 接口分派 —— 注入类实现 IStageDefinitionProvider 时在 metadata v31 会抛
            // MethodAccessException（与 SetPropertyObject 同源）。因此不注册 provider，
            // 改由 LoadStagesPatch 在官方 LoadStages 时机直接创建关卡定义
            // （LoadStageProperties 依然在 LoadDefinitionProperties 中自动应用 meta 属性）。
            Plugin.Log?.LogInfo("[LabMod] PreInit (no provider registration; LoadStages patch handles defs)");
        }

        public override void OnInit(IGlobalGame game)
        {
            // 不能调 base.OnInit(game)（递归）。
            // 关卡定义由官方 LoadStages/LoadStageProperties 处理，此处仅日志。
            Plugin.Log?.LogInfo("[LabMod] OnInit(game) called");
        }

        public override void PostInit(IGlobalGame game)
        {
            // 不能调 base.PostInit(game)（递归）。
            Plugin.Log?.LogInfo("[LabMod] PostInit(game) called, applying stage meta properties");
            // 此时 LoadAll 已完成所有 mod 的 LoadAssemblies（含 vanilla），
            // LogicStageProps 等 PropertyMeta 的 key 已注册 —— SetProperty 不再 invalid key。
            foreach (var stage in registeredStages)
            {
                if (stage != null)
                    StageMetaApplier.ApplyMetaTo(stage);
            }
        }

        /// <summary>LoadStagesPatch 创建的关卡定义（PostInit 时统一套 meta 属性）。</summary>
        internal static readonly System.Collections.Generic.List<PVZEngine.Level.StageDefinition> registeredStages
            = new System.Collections.Generic.List<PVZEngine.Level.StageDefinition>();

        internal static void RegisterStage(PVZEngine.Level.StageDefinition stage)
        {
            registeredStages.Add(stage);
        }

        public override ModSaveData CreateSaveData()
        {
            Plugin.Log?.LogInfo("[LabMod] CreateSaveData called");
            return new LogicSaveData(spaceName);
        }

        public override ModSaveData LoadSaveData(string json)
        {
            Plugin.Log?.LogInfo("[LabMod] LoadSaveData called");
            // 本 Mod 不存储自定义数据，读档返回空存档即可。
            return new LogicSaveData(spaceName);
        }
    }
}
