using System;
using System.IO;
using UnityEngine;

namespace Loadout
{
    /// <summary>
    /// 自测用的截图 —— 把游戏的**真实画面**落成 PNG，供人肉确认。
    ///
    /// 为什么需要它：面板的两个「看不见」类 bug（框体美术不显示、下拉框弹窗展开后看不到 UI）
    /// 光看布局 dump 是**证不出来**的 —— dump 只能证明节点存在、尺寸/位置/Image.enabled 都对。
    /// 而"看不到"的成因恰恰在 dump 之外：被上层挡住、sprite 是空心的、字色和底色一样……
    /// 这些必须看画面。所以自测在把弹窗展开后会**保持若干帧**（<see cref="SelfTest"/>），
    /// 这段时间里用 <see cref="ScreenCapture.CaptureScreenshot(string)"/> 抓一张图。
    ///
    /// 落盘位置：<c>&lt;游戏目录&gt;/BepInEx/_gc_shot{序号}_{tag}.png</c>
    /// （<c>Application.dataPath</c> 在 Windows 播放器里是 <c>&lt;游戏目录&gt;/MVZ2_Data</c>，
    /// 所以取它的上级目录即为游戏根目录。）
    /// </summary>
    internal static class DiagShots
    {
        private static int index;
        public static string LastPath = "（尚未截图）";

        public static void Capture(string tag)
        {
            try
            {
                var root = GameRoot();
                if (string.IsNullOrEmpty(root))
                {
                    LastPath = "找不到游戏目录";
                    Plugin.Log?.LogWarning("[Shot] 找不到游戏目录（Application.dataPath 为空），跳过截图");
                    return;
                }
                var dir = Path.Combine(root, "BepInEx");
                var path = Path.Combine(dir, "_ld_shot" + index + "_" + tag + ".png");
                index++;
                // ⚠️ 这是**异步**的：Unity 在本帧末才把画面写盘，所以调用完不能立刻读文件。
                ScreenCapture.CaptureScreenshot(path);
                LastPath = path;
                Plugin.Log?.LogInfo("[Shot] 已请求截图：" + path);
            }
            catch (Exception e)
            {
                LastPath = "截图异常 " + e.GetType().Name;
                Plugin.Log?.LogWarning("[Shot] 截图失败：" + e.GetType().Name + ":" + e.Message);
            }
        }

        private static string GameRoot()
        {
            var data = Application.dataPath;
            if (string.IsNullOrEmpty(data))
                return null;
            try
            {
                return Path.GetDirectoryName(data);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
