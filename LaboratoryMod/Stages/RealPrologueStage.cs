using System;
using Il2CppInterop.Runtime.Injection;
using Il2CppSystem.Collections.Generic;
using MVZ2.GameContent.Stages;
using MVZ2Logic.Level;
using PVZEngine;
using PVZEngine.Callbacks;
using PVZEngine.Level;

namespace LaboratoryMod.Stages
{
    /// <summary>
    /// 真正的序章（Real Prologue），0.7 官方链注入版。
    /// 从 0.6.4 laboratory 分支移植：行为组合与 0.6.4 完全一致
    /// （Wave/FinalWaveClear/Gem/Starshard），OnStart 沿用其特殊设定
    /// （波次/旗直接推到最后）。
    ///
    /// 0.7 中不再手动 AddDefinition + StageMetaApplier：关卡定义由
    /// LabStageProvider（官方 LoadStages）创建后 AddDefinition，
    /// meta 属性（ENEMY_POOL/出怪点/名字/音乐/解锁）由官方 LoadStageProperties 自动应用。
    ///
    /// 注入类实例化要点（同 LabMod）：
    /// - 绝不能走 (string,string) ctor 链：代理基类 ctor 内部会
    ///   il2cpp_object_new(原生 StageDefinition)，产出的对象 klass 不是注入类，
    ///   我们的 OnStart 重写永远不会被分派。
    /// - CreateInstance = DerivedConstructorPointer + DerivedConstructorBody
    ///   + 手动补原生 ctor 的字段初始化（id/propertyDict/triggers/behaviours）
    ///   + StageDefinition ctor 的默认属性（WAVES_PER_FLAG=10 / RECHARGE_SPEED=1f）。
    /// </summary>
    public class RealPrologueStage : StageDefinition
    {
        public const string StageID = "real_prologue";

        /// <summary>注入类绑定 ctor（ClassInjector 注册与实例化统一走 (IntPtr) 路径）。</summary>
        public RealPrologueStage(IntPtr ptr) : base(ptr)
        {
        }

        internal static RealPrologueStage CreateInstance()
        {
            var stage = new RealPrologueStage(ClassInjector.DerivedConstructorPointer<RealPrologueStage>());
            ClassInjector.DerivedConstructorBody(stage);

            // 补做原生 Definition(string,string) 与 StageDefinition ctor 的初始化。
            // 注意：ID 落在本 mod 命名空间 mvz2_lab（全隔离方案）。
            // 每个 new 出来的 il2cpp 对象都要 Pin：注入类实例的字段不被 il2cpp GC
            // 追踪，不钉住会被回收 → GetID() 值比较失败（详见 Il2CppPin 注释）。
            stage.id = Il2CppPin.Pin(new NamespaceID(LabMod.spaceName, StageID));
            stage.propertyDict = Il2CppPin.Pin(new PropertyDictionary());
            stage.triggers = Il2CppPin.Pin(new List<ITrigger>());
            stage.behaviours = Il2CppPin.Pin(new List<StageBehaviour>());

            // 原生 StageDefinition ctor 还会设置两个默认属性（绕过了 ctor，必须手动补）：
            //   WAVES_PER_FLAG = 10 —— 缺失会导致 wave % 0 整数除零闪退（0xC0000094）；
            //   RECHARGE_SPEED = 1f —— 缺失会导致种子包永不充能。
            stage.SetProperty(EngineStageProps.WAVES_PER_FLAG, 10);
            stage.SetProperty(EngineLevelProps.RECHARGE_SPEED, 1f);

            // 行为组合与 0.6.4 一致（必须在字段初始化之后）。
            stage.AddBehaviour(Il2CppPin.Pin(new WaveStageBehaviour(stage)));
            stage.AddBehaviour(Il2CppPin.Pin(new FinalWaveClearBehaviour(stage)));
            stage.AddBehaviour(Il2CppPin.Pin(new GemStageBehaviour(stage)));
            stage.AddBehaviour(Il2CppPin.Pin(new StarshardStageBehaviour(stage)));
            return stage;
        }

        /// <summary>
        /// 属性查询 override（v31 规避）。
        ///
        /// 原生 LevelEngine 做属性 fallback（level 属性读不到时查 StageDefinition）
        /// 会虚调用 Definition.TryGetPropertyObject。注入类未 override 的基类虚方法
        /// 在 vtable 重建后槽位错位，原生侧调用会抛
        /// "MethodAccessException: Attempt to access method 'IEqualityComparer...GetHashCode'
        /// on type 'SortingGroup' failed"（金币拾取物 PickupAutoCollect 每帧触发）。
        /// override 后 IL2CPP 把该槽绑定到本托管 trampoline，逻辑改为托管侧直访
        /// 原生 propertyDict（托管→原生对象方法调用不受虚分派影响）。
        /// </summary>
        public override bool TryGetPropertyObject(IPropertyKey name, out Il2CppSystem.Object value)
        {
            try
            {
                return propertyDict.TryGetPropertyObject(name, out value);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[RealPrologueStage] TryGetPropertyObject error: " + e);
                value = null;
                return false;
            }
        }

        public override void OnStart(LevelEngine level)
        {
            // 注意：不能调 base.OnStart(level)！代理虚调用会无限递归直到 native 栈溢出。
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
                    var stageID = GetID();
                    if (stageID != null)
                    {
                        var res = MVZ2.Managers.MainManager.Instance?.ResourceManager;
                        var metas = res?.GetModStageMetas(stageID.SpaceName);
                        if (metas != null)
                        {
                            for (var i = 0; i < metas.Length; i++)
                            {
                                var meta = metas[i];
                                if (meta != null && meta.ID == stageID.Path && meta.Spawns != null)
                                {
                                    MVZ2Logic.Level.LogicStageProps.SetEnemyPool(level, meta.Spawns);
                                    Plugin.Log?.LogInfo("[RealPrologueStage] OnStart: enemy pool fallback set ("
                                        + meta.Spawns.Length + " spawns)");
                                    break;
                                }
                            }
                        }
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
