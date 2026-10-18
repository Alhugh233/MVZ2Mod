#if !DIAGNOSTICS

namespace Replay
{
    /// <summary>
    /// 发布构建里的「传送带守卫」空实现。
    ///
    /// <para>真正的 <c>ReplayConveyorGuard</c> 住在 <c>Diagnostics\</c>（发布构建整份不编译，见
    /// <c>Replay.csproj</c>）。它接管的是 <c>ConveyorBlueprintController.UpdateFixed</c>，
    /// 属于「重写游戏逻辑」而不是「只是看看」；而 0.7.0 正式版的传送带闪退**尚未修好**
    /// （已明确推迟），所以它不该跟着发布包出去。</para>
    ///
    /// <para>但它的 <see cref="Beat"/> 调用点留在了 <c>ReplayPatches</c> / <c>ReplayPlayer</c> 的
    /// 每帧/每 tick 路径上（在诊断构建里是「崩溃前的最后一条标记」，是定位原生 AV 的唯一手段）。
    /// 这个空实现就是为了让那些调用点在发布构建里原样保留、且编译成一次空调用 ——
    /// 这样「诊断构建」与「发布构建」的调用位置完全一致，不会因为要排查而改变代码结构。</para>
    /// </summary>
    internal static class ReplayConveyorGuard
    {
        /// <summary>发布构建里什么也不做（JIT 会把整个调用抹掉）。</summary>
        public static void Beat(int tick, string note)
        {
        }
    }
}

#endif
