using System;
using MVZ2Logic;
using MVZ2Logic.Options;

namespace GraphicConsole
{
    /// <summary>
    /// 面板的「暂停」= 把游戏速度压到 0（见 <see cref="GcFeature.PanelSpeedZeroPause"/>）。
    ///
    /// ## 为什么不用原版控制台那套暂停
    /// 原版 <c>DebugConsoleController.IsActive()</c> 为真时，<c>LevelController.IsGameRunning()</c>
    /// 变 false → <c>UpdateLogic()</c> 整段被跳过、键盘与草坪射线被输入层挡掉，
    /// 于是"面板一开就没法操作游戏"。需求明确要求改掉这个行为。
    ///
    /// ## 为什么"速度 = 0"是等效而更好的一条路
    /// <c>LevelController.GetGameSpeed()</c> 的返回值只用于**推进倍率**：
    ///   · <c>UpdateLogicRunning()</c>：<c>times = (int)gameSpeed</c> → 0 就不再推进关卡逻辑；
    ///   · <c>UpdateFrame()</c> / <c>UpdateFrameAnimators()</c>：实体与 UI 的帧推进乘以它 → 0 即冻结；
    /// 而 <c>IsGameRunning()</c>（看 isGameStarted/isPaused/isGameOver/控制台）与
    /// <c>IsInputDisabled()</c>（看 level/cleared/相册/商店/控制台）**都不看速度**，
    /// 所以关卡冻结、蓝图 / 镐子 / 星之碎片 / 草坪点击全部照常工作。
    /// 这就是需求里的「将游戏速度调至 0，等效速度倍率为 0 的加速」。
    ///
    /// 原版自己也有同一机制（私有字段 <c>advancedPause</c> + F7 键），
    /// 但那个字段是私有的、F7 也只是切换，所以这里用 postfix
    /// <c>LevelController.GetGameSpeed</c> 实现，零反射、零注入，且两套机制可以并存。
    ///
    /// ## 开关的归属
    /// 单一开关就是**原版自己的全局选项「后台暂停」**（<c>pause_on_focus_lost</c>）：
    ///   · 面板页面1 第二行的复选框勾上/取消，直接读写该全局选项（与原版设置界面同一个开关）；
    ///   · 勾上时，面板打开 → 速度 0，面板关闭 → 恢复；
    ///   · 因此底部那个额外的"暂停"按钮不再需要（需求要求移除）。
    /// 这样"面板暂停"与"后台暂停"共用一个开关，语义一致、也不会出现两处状态打架。
    /// </summary>
    internal static class PanelPause
    {
        /// <summary>面板是否正在把游戏速度压成 0。</summary>
        public static bool Active
        {
            get { return CompatConfig.IsEnabled(GcFeature.PanelSpeedZeroPause) && Enabled && ConsolePanel.IsOpen; }
        }

        /// <summary>「后台暂停」开关（= 原版全局选项 <c>pause_on_focus_lost</c>）。</summary>
        public static bool Enabled
        {
            get
            {
                try
                {
                    var options = Global.Options;
                    return options != null && options.GetPauseOnFocusLost();
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static void SetEnabled(bool value)
        {
            try
            {
                var options = Global.Options;
                if (options == null)
                    return;
                options.SetPauseOnFocusLost(value);
                Plugin.Log?.LogInfo("[Pause] 后台暂停（pause_on_focus_lost）= " + value);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Pause] 写全局选项失败：" + e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
