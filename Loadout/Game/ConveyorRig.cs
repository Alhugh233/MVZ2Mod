using System;
using MVZ2.GameContent.Stages;
using PVZEngine.Level;

namespace Loadout
{
    /// <summary>
    /// 「传送带模式」的那个**传送带行为**。
    ///
    /// <para>只把 <c>level.SetConveyorMode(true)</c> 打开是不够的：那一句只切 HUD 那一行 +
    /// 本组件的开关，真正的「隔一段时间从卡池里抽一张卡放到传送带上」是关卡定义里的
    /// <c>ConveyorStageBehaviour</c> 干的（它 <c>Start</c> 时设传送带模式 + 起计时器，
    /// <c>Update</c> 里每 <c>CONVEYOR_INTERVAL</c>(120) 帧抽一张）。普通关卡的定义里**没有**它，
    /// 所以面板切过去之后传送带是不出卡的。</para>
    ///
    /// <para>做法：**用原版那一个类本身**，自己 new 一个实例、自己驱动它
    /// （<c>Start</c> 一次 + 每帧 <c>Update</c>），而不是往关卡定义里 <c>AddBehaviour</c>。
    /// 两个理由：① <c>AddBehaviour</c> 是 <c>protected</c>，而且定义里的行为列表**没有删除接口**
    /// —— 需求要的「切回自选卡时删掉」做不到；② 自己驱动就等于把它的生命周期握在手里，
    /// 抽卡节奏 / 「蓝图越多越慢」这些逻辑依旧是原版那一份代码。</para>
    ///
    /// <para>关剧本**自己就带**传送带行为的关卡（原版的 11 关之类）不会被我们重复驱动：
    /// 检测到定义里已经有一个就什么都不做，交给它自己那套。</para>
    /// </summary>
    internal static class ConveyorRig
    {
        /// <summary>我们自己造的那个（null = 没启用）。</summary>
        private static ConveyorStageBehaviour mine;
        /// <summary>它属于哪个关卡（换关就把旧的丢掉）。用 <c>Pointer</c> 当身份 ——
        /// <c>LevelEngine</c> 不是 MonoBehaviour，没有 <c>GetInstanceID</c>。</summary>
        private static long levelId;
        private static bool selfDriven;

        /// <summary>
        /// 打开传送带：确保有东西在抽卡。切模式时调。
        /// </summary>
        public static bool Enable(LevelEngine level)
        {
            if (level == null)
                return false;
            try
            {
                var id = IdentityOf(level);
                if (levelId != id)
                    Reset();
                levelId = id;
                BlueprintOps.SetConveyorMode(level, true);

                if (mine != null)
                    return true;         // 已经在驱动了

                var stageDef = level.StageDefinition;
                if (stageDef == null)
                {
                    Plugin.LogOnce("conv-nostage", "[Conveyor] 拿不到关卡定义，无法加传送带行为。");
                    return false;
                }
                if (HasOwnBehaviour(stageDef))
                {
                    selfDriven = true;
                    Plugin.Log?.LogInfo("[Conveyor] 这一关自己就带传送带行为，交给它自己出卡。");
                    return true;
                }

                mine = new ConveyorStageBehaviour(stageDef);
                // Start 里做的正是「设传送带模式 + 起抽卡计时器」。
                // （定义里的那些行为在关卡开始时已经跑过一次 Start，中途加进来的不会再有第二次，
                //   所以这里必须自己叫一次。）
                mine.Start(level);
                selfDriven = false;
                Plugin.Log?.LogInfo("[Conveyor] 已为本关加上传送带行为（每 "
                    + ConveyorStageBehaviour.CONVEYOR_INTERVAL + " 帧从卡池抽一张）。");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Conveyor] 启用传送带行为失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>关掉传送带：把**我们加的那个**行为丢掉。切回自选卡时调。</summary>
        public static void Disable(LevelEngine level)
        {
            try
            {
                if (mine == null && !selfDriven)
                    return;
                var had = mine != null;
                mine = null;
                selfDriven = false;
                Plugin.Log?.LogInfo("[Conveyor] 已移除传送带行为"
                    + (had ? "（我们自己加的那个）" : "（本关自带的那个不动）") + "。");
                if (level != null)
                    BlueprintOps.SetConveyorMode(level, false);
            }
            catch (Exception) { }
        }

        /// <summary>关卡的「身份」（换关就认不出来了）：用 il2cpp 对象的指针。</summary>
        private static long IdentityOf(LevelEngine level)
        {
            try { return level == null ? 0L : level.Pointer.ToInt64(); }
            catch (Exception) { return 0L; }
        }

        /// <summary>换关 / 重载时丢掉状态。</summary>
        public static void Reset()
        {
            mine = null;
            selfDriven = false;
        }

        /// <summary>
        /// 每帧驱动（**与面板开没开无关** —— 面板关着的时候传送带也必须继续出卡）。
        /// </summary>
        public static void Tick()
        {
            if (mine == null)
                return;
            try
            {
                var level = LevelFacade.Current;
                if (level == null || IdentityOf(level) != levelId)
                {
                    Reset();
                    return;
                }
                mine.Update(level);
            }
            catch (Exception)
            {
                // 原生心跳上不能抛。
            }
        }

        public static string Describe()
        {
            return "传送带行为：" + (mine != null ? "我们加的（在驱动）"
                : (selfDriven ? "关卡自带的（不重复驱动）" : "无"));
        }

        /// <summary>关卡定义里是不是已经有一个传送带行为（避开 interop 的泛型 HasBehaviour）。</summary>
        private static bool HasOwnBehaviour(StageDefinition stageDef)
        {
            try
            {
                var count = stageDef.GetBehaviourCount();
                for (var i = 0; i < count; i++)
                {
                    if (stageDef.GetBehaviourAt(i) is ConveyorStageBehaviour)
                        return true;
                }
            }
            catch (Exception) { }
            return false;
        }
    }
}
