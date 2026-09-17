using System;
using MVZ2.Managers;
using MVZ2.UI;
using UnityEngine;

namespace ModularCurse
{
    /// <summary>
    /// 借用原版工具提示（<c>SimpleTooltipSource</c> + <c>MainSceneController.ShowTooltip</c>）
    /// 显示一段文字，用在**我们自己克隆出来的控件**上
    /// （原版控件的提示由游戏自己接，别抢）。
    ///
    /// <paramref name="targetPointer"/> 传的是某个游戏对象（如诅咒条目）的**原生指针**：
    /// interop 生成的代理**不声明自己实现了 <c>ITooltipTarget</c>**（见实现备注 12），
    /// 直接隐式转换编译不过；所以这里按本项目一贯的做法手工包一层接口壳
    /// （与 <c>new IBuffTarget(ptr)</c>、<c>new IConditionList(ptr)</c> 完全同理）。
    /// </summary>
    internal static class CurseTooltip
    {
        public static void Show(IntPtr targetPointer, Camera camera, string name, string description)
        {
            try
            {
                if (targetPointer == IntPtr.Zero)
                    return;
                var main = MainManager.Instance;
                if (main == null)
                    return;
                var content = new TooltipContent
                {
                    name = name,
                    description = description,
                };
                var target = new ITooltipTarget(targetPointer);
                var source = new SimpleTooltipSource(camera, target, content);
                // ShowTooltip 要的也是接口，代理同样不声明 → 也包一层壳。
                main.Scene.ShowTooltip(new ITooltipSource(source.Pointer));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[CursePool] 显示提示失败: " + e.GetType().Name + ": " + e.Message);
            }
        }

        public static void Hide()
        {
            try
            {
                var main = MainManager.Instance;
                if (main == null)
                    return;
                main.Scene.HideTooltip();
            }
            catch (Exception)
            {
            }
        }
    }
}
