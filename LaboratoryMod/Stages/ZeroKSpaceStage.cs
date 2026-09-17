using System;
using DSHCore;
using Il2CppInterop.Runtime.Injection;
using Il2CppSystem.Collections.Generic;
using MVZ2.GameContent.Pickups;
using MVZ2.GameContent.Stages;
using PVZEngine;
using PVZEngine.Buffs;
using PVZEngine.Callbacks;
using PVZEngine.Entities;
using PVZEngine.Level;
using PVZEngine.SeedPacks;
using UnityEngine;

namespace LaboratoryMod.Stages
{
    /// <summary>
    /// 零度空间（Zero K Space），0.7 移植版。
    ///
    /// 原版（0.6.4）机制：
    ///   1. 全关卡蓝图瞬间充能（RECHARGE_SPEED → 极大值）
    ///   2. 火药（gunpowder / furious_gunpowder）落地后 5 帧爆炸
    ///   3. 种子包成本 × recharge quality（ZeroKSpaceCostBuff）
    ///
    /// 0.7 注入版实现（规避 metadata v31 泛型/接口分派限制）：
    ///   - 关卡定义 = 注入 StageDefinition 子类，组合 vanilla behaviour 与 0.6.4 相同；
    ///   - 自定义逻辑放在 override OnSetup/OnUpdate 虚方法（不走 behaviour + AddTrigger 泛型链，
    ///     也避免自定义 PropertyMeta 无法注册的问题）；
    ///   - RECHARGE_SPEED 用 level 属性直接设置（level 属性 fallback 链：level → stageDef）；
    ///   - 火药超时用 OnUpdate 轮询（pickup 数量少，开销可接受）；
    ///   - 费用倍率 = 给每个 seedpack 挂 ZeroKSpaceCostBuff（常量乘数 modifier），
    ///     multiplier = max(1, recharge quality)；modifier 层内 Set(文文新闻) 先算、
    ///     Multiply(本 buff) 后乘 → 与文文新闻天然叠加。
    /// </summary>
    public class ZeroKSpaceStage : StageDefinition
    {
        public const string StageID = "zero_k_space";
        public const float INSTANT_RECHARGE_SPEED = 99999999f;
        public const int GUNPOWDER_EXPLODE_TIMEOUT = 5;

        private int frameCounter;

        public ZeroKSpaceStage(IntPtr ptr) : base(ptr)
        {
        }

        internal static ZeroKSpaceStage CreateInstance()
        {
            var stage = new ZeroKSpaceStage(ClassInjector.DerivedConstructorPointer<ZeroKSpaceStage>());
            ClassInjector.DerivedConstructorBody(stage);

            stage.id = Il2CppPin.Pin(new NamespaceID(LabMod.spaceName, StageID));
            stage.propertyDict = Il2CppPin.Pin(new PropertyDictionary());
            stage.triggers = Il2CppPin.Pin(new List<ITrigger>());
            stage.behaviours = Il2CppPin.Pin(new List<StageBehaviour>());

            stage.SetProperty(EngineStageProps.WAVES_PER_FLAG, 10);
            stage.SetProperty(EngineLevelProps.RECHARGE_SPEED, 1f);

            // 行为组合与 0.6.4 ZeroKSpaceStage 一致。
            stage.AddBehaviour(Il2CppPin.Pin(new WaveStageBehaviour(stage)));
            stage.AddBehaviour(Il2CppPin.Pin(new FinalWaveClearBehaviour(stage)));
            stage.AddBehaviour(Il2CppPin.Pin(new GemStageBehaviour(stage)));
            stage.AddBehaviour(Il2CppPin.Pin(new StarshardStageBehaviour(stage)));
            return stage;
        }

        /// <summary>
        /// 属性查询 override（v31 规避，同 RealPrologueStage）。
        ///
        /// 原生 LevelEngine 属性 fallback 虚调用 Definition.TryGetPropertyObject；
        /// 注入类未 override 的基类虚方法槽错位 → 原生侧抛 MethodAccessException
        /// （金币拾取物 PickupAutoCollect 每帧触发，栈显示 comparer 崩到 SortingGroup）。
        /// override 后槽指向托管 trampoline，托管侧直访原生 propertyDict。
        /// </summary>
        public override bool TryGetPropertyObject(IPropertyKey name, out Il2CppSystem.Object value)
        {
            try
            {
                return propertyDict.TryGetPropertyObject(name, out value);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ZeroKSpaceStage] TryGetPropertyObject error: " + e);
                value = null;
                return false;
            }
        }

        public override void OnSetup(LevelEngine level)
        {
            // 不能调 base.OnSetup(level)（原生栈递归）。
            Plugin.Log?.LogInfo("[ZeroKSpaceStage] OnSetup: 设置瞬间充能");
            try
            {
                // 直接写 level 属性（level 属性读取 fallback：level → stageDef → area）。
                level.SetProperty(EngineLevelProps.RECHARGE_SPEED, INSTANT_RECHARGE_SPEED);
                Plugin.Log?.LogInfo("[ZeroKSpaceStage] RECHARGE_SPEED set to " + INSTANT_RECHARGE_SPEED);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ZeroKSpaceStage] OnSetup error: " + e);
            }
        }

        public override void OnUpdate(LevelEngine level)
        {
            // 不能调 base.OnUpdate(level)（原生栈递归）。
            frameCounter++;
            // 每 120 帧（约 2 秒）打一次日志，确认 OnUpdate 被关卡持续驱动。
            if (frameCounter % 120 == 1)
            {
                Plugin.Log?.LogInfo("[ZeroKSpaceStage] OnUpdate alive, frame=" + frameCounter);
            }
            // 火药速爆：拾取物落地后设 timeout（Time>5 才覆盖，避免每帧重置）。
            // 0.6.4 用 POST_ENTITY_INIT 触发；此处改用 OnUpdate 轮询
            // （pickup 数量少，开销可接受；规避泛型 AddTrigger 的 v31 限制）。
            try
            {
                foreach (var gunpowderID in new[] { VanillaPickupID.gunpowder, VanillaPickupID.furiousGunpowder })
                {
                    var pickups = level.FindEntities(gunpowderID);
                    for (var i = 0; i < pickups.Length; i++)
                    {
                        var pickup = pickups[i];
                        if (pickup == null)
                            continue;
                        if (pickup.Timeout > GUNPOWDER_EXPLODE_TIMEOUT)
                        {
                            pickup.Timeout = GUNPOWDER_EXPLODE_TIMEOUT;
                            if (frameCounter % 60 == 1)
                                Plugin.Log?.LogInfo("[ZeroKSpaceStage] gunpowder timeout set to "
                                    + GUNPOWDER_EXPLODE_TIMEOUT);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ZeroKSpaceStage] OnUpdate gunpowder error: " + e);
            }
            // 费用倍率：给每个 seedpack 挂 ZeroKSpaceCostBuff（每帧轮询，幂等）。
            try
            {
                ApplyCostMultiplier(level, frameCounter % 600 == 1);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ZeroKSpaceStage] cost multiplier error: " + e);
            }
        }

        /// <summary>
        /// 0.6.4 ZeroKSpaceStageBehaviour.Update 的移植：
        /// 遍历 seedpack，若未挂 ZeroKSpaceCostBuff 则按 recharge quality
        /// 计算乘数并挂载（乘数&gt;1 才需要；=1 无需处理）。
        /// </summary>
        private static void ApplyCostMultiplier(LevelEngine level, bool verbose)
        {
            int slotCount;
            try
            {
                slotCount = level.GetSeedSlotCount();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[ZeroKSpaceStage] GetSeedSlotCount NRE? " + e);
                return;
            }
            if (verbose)
                Plugin.Log?.LogInfo("[ZeroKSpaceStage] ApplyCostMultiplier slots=" + slotCount);
            for (var i = 0; i < slotCount; i++)
            {
                SeedPack seedPack;
                try
                {
                    seedPack = level.GetSeedPackAt(i);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogError("[ZeroKSpaceStage] GetSeedPackAt(" + i + ") error: " + e);
                    continue;
                }
                if (seedPack == null)
                    continue;
                RechargeDefinition rechargeDef;
                try
                {
                    rechargeDef = seedPack.GetRechargeDefinition();
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogError("[ZeroKSpaceStage] slot " + i + " GetRechargeDefinition error: " + e);
                    continue;
                }
                if (rechargeDef == null)
                    continue;
                float multiplier;
                try
                {
                    multiplier = Mathf.Max(1f, EngineRechargeProps.GetQuality(rechargeDef));
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogError("[ZeroKSpaceStage] slot " + i + " GetQuality error: " + e);
                    continue;
                }
                if (verbose)
                    Plugin.Log?.LogInfo("[ZeroKSpaceStage] slot " + i + " quality mult=" + multiplier);
                if (multiplier <= 1f)
                    continue;
                var costBuff = GetCostBuff(multiplier);
                if (costBuff == null)
                    continue;
                try
                {
                    // 包装 IBuffTarget（SeedPack 显式实现接口），避免泛型/接口代理分派问题。
                    var target = new IBuffTarget(seedPack.Pointer);
                    if (BuffTargetExt.HasBuff(target, costBuff))
                        continue;
                    BuffTargetExt.AddBuff(target, costBuff);
                    Plugin.Log?.LogInfo("[ZeroKSpaceStage] cost multiplier applied: seed="
                        + seedPack.GetDefinitionID() + " mult=" + multiplier);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogError("[ZeroKSpaceStage] slot " + i + " addbuff error: " + e);
                }
            }
        }

        private static Buffs.ZeroKSpaceCostBuff GetCostBuff(float multiplier)
        {
            // 优先返回 LoadStages 时注册的正式定义（乘数 3/5）；
            // 其余罕见乘数动态创建兜底。
            return Buffs.ZeroKSpaceCostBuff.GetDefinition(multiplier);
        }
    }
}
