using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppSystem.Collections.Generic;
using MVZ2.GameContent.Stages;
using MVZ2Logic.Level;
using PVZEngine;
using PVZEngine.Callbacks;
using PVZEngine.Level;

namespace MVZ2LabMod.Stages
{
    /// <summary>
    /// 真正的序章（Real Prologue）。
    /// 从 0.6.4 laboratory 分支移植，仅组合原版 StageBehaviour，无自定义逻辑。
    ///
    /// 注入类实例化要点（与 LabMod 相同）：
    /// - 绝不能走 (string,string) ctor 链：代理基类 ctor 内部会
    ///   il2cpp_object_new(原生 StageDefinition)，产出的对象 klass 不是注入类，
    ///   我们的 OnStart 重写永远不会被分派。
    /// - CreateInstance = DerivedConstructorPointer + DerivedConstructorBody
    ///   + 手动补原生 ctor 的字段初始化（id/propertyDict/triggers/behaviours），
    ///   再添加行为（AddBehaviour 依赖 behaviours 字段已初始化）。
    /// </summary>
    public class RealPrologueStage : StageDefinition
    {
        public const string VanillaNamespace = "mvz2";
        public const string VanillaStageID = "real_prologue";

        /// <summary>注入类绑定 ctor（ClassInjector 注册与实例化统一走 (IntPtr) 路径）。</summary>
        public RealPrologueStage(IntPtr ptr) : base(ptr)
        {
        }

        internal static RealPrologueStage CreateInstance()
        {
            var stage = new RealPrologueStage(ClassInjector.DerivedConstructorPointer<RealPrologueStage>());
            ClassInjector.DerivedConstructorBody(stage);

            // 补做原生 Definition(string,string) 与 StageDefinition ctor 的初始化。
            stage.id = new NamespaceID(VanillaNamespace, VanillaStageID);
            stage.propertyDict = new PropertyDictionary();
            stage.triggers = new List<ITrigger>();
            stage.behaviours = new List<StageBehaviour>();

            // 原生 StageDefinition ctor 还会设置两个默认属性（我们绕过了 ctor，必须手动补）：
            //   WAVES_PER_FLAG = 10 —— 缺失会导致 wave % 0 整数除零，进关一段时间后
            //     进程直接闪退（0xC0000094, GameAssembly.dll，实测）；
            //   RECHARGE_SPEED = 1f —— 缺失会导致种子包充能速度为 0，永不充能。
            stage.SetProperty(EngineStageProps.WAVES_PER_FLAG, 10);
            stage.SetProperty(EngineLevelProps.RECHARGE_SPEED, 1f);

            // 行为组合与 0.6.4 一致（必须在字段初始化之后，AddBehaviour 内部会 Add 到 behaviours）。
            stage.AddBehaviours();
            return stage;
        }

        private void AddBehaviours()
        {
            AddBehaviour(new WaveStageBehaviour(this));
            AddBehaviour(new FinalWaveClearBehaviour(this));
            AddBehaviour(new GemStageBehaviour(this));
            AddBehaviour(new StarshardStageBehaviour(this));
        }

        public override void OnStart(LevelEngine level)
        {
            // 注意：不能调 base.OnStart(level)！
            // 代理虚调用经 il2cpp_object_get_virtual_method 走 vtable 槽 10，
            // 而槽 10 绑定到本方法的 trampoline → 无限递归直到 native 栈溢出。
            // 原生 StageDefinition.OnStart 是空实现，跳过无影响。
            Plugin.Log?.LogInfo("[RealPrologueStage] OnStart: CurrentWave=200, CurrentFlag=20");
            level.CurrentWave = 200;
            level.CurrentFlag = 20;

            // 兜底：确保敌怪池已设置（进关预览 CreatePreviewEnemies 依赖它）。
            // 正常路径由 StageMetaApplier 在关卡定义上设置 ENEMY_POOL；
            // 若泛型 SetProperty 在 metadata v31 上失败，这里用非泛型的
            // level.SetEnemyPool（原生静态扩展）直接写在 LevelEngine 上。
            try
            {
                if (level.GetEnemyPool() == null)
                {
                    var meta = StageMetaApplier.GetStageMeta(VanillaStageID);
                    if (meta != null && meta.Spawns != null)
                    {
                        LogicStageProps.SetEnemyPool(level, meta.Spawns);
                        Plugin.Log?.LogInfo("[RealPrologueStage] OnStart: enemy pool fallback set ("
                            + meta.Spawns.Length + " spawns)");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[RealPrologueStage] OnStart enemy pool fallback error: " + e);
            }
        }
    }
}
