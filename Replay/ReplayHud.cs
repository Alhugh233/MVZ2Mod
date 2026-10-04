using System;
using DSHCore.Ui;
using MVZ2.Level;
using TMPro;
using UnityEngine;

namespace Replay
{
    /// <summary>
    /// 回放时的 <c>REC 当前帧/总帧数</c> 标签。
    ///
    /// <para><b>为什么克隆难度文字：</b>
    /// 原版每个文本节点的字号、字体、材质、缩放都是跟它所在容器一起调好的；从零
    /// <c>new GameObject + TextMeshProUGUI</c> 十有八九字号/字体不对。克隆难度文字
    /// （<c>LevelUIPreset.difficultyText</c>）能白拿这一整套。</para>
    ///
    /// <para><b>为什么挂在难度文字**自己**下面（而不是当兄弟节点）：</b>
    /// 它上面那层是带 <c>LayoutGroup</c> 的（实测：当兄弟节点时会被排到难度文字的
    /// **右侧**，因为 anchoredPosition 会被布局组接管）。挂在难度文字内部就不受布局组
    /// 影响：父节点的矩形由布局决定，我们锚在它下边缘正中，就稳定是「正下方」。</para>
    ///
    /// <para>失败一律吞掉只记日志 —— 一个装饰性标签绝不能把播放本身搞崩。</para>
    /// </summary>
    internal static class ReplayHud
    {
        private const string NodeName = "ReplayRecLabel";

        private static TextMeshProUGUI label;
        private static RectTransform self;
        private static string lastText;
        private static bool gaveUp;

        /// <summary>确保标签存在（幂等）。关卡 UI 就绪之后才能调用。</summary>
        public static void Ensure(LevelController ctrl)
        {
            if (label != null && label.Pointer != IntPtr.Zero)
                return;
            if (gaveUp)
                return;

            try
            {
                if (ctrl == null)
                    return;

                var preset = ctrl.GetUIPreset();
                if (preset == null)
                    return;

                var template = preset.difficultyText;
                if (template == null)
                {
                    Log("找不到难度文字节点，REC 标签不可用");
                    gaveUp = true;
                    return;
                }
                if (!UiFinder.IsAlive(template.gameObject))
                {
                    Log("难度文字节点已销毁，REC 标签不可用");
                    gaveUp = true;
                    return;
                }

                var src = template.rectTransform;
                if (src == null)
                {
                    Log("难度文字的 RectTransform 取不到，REC 标签不可用");
                    gaveUp = true;
                    return;
                }

                // 父节点就用难度文字本身（见类注释：挂上面那层会被 LayoutGroup 排到右边）。
                var go = UiClone.Instantiate(template.gameObject, template.transform, NodeName);
                if (go == null)
                {
                    Log("克隆 REC 标签失败");
                    gaveUp = true;
                    return;
                }

                label = go.GetComponent<TextMeshProUGUI>();
                self = go.GetComponent<RectTransform>();
                if (label == null || self == null)
                {
                    Log("克隆出来的 REC 标签缺组件");
                    label = null;
                    self = null;
                    gaveUp = true;
                    return;
                }

                PlaceBelow(src);
                label.text = "REC 0/0";
                try { label.raycastTarget = false; } catch (Exception) { }
                lastText = label.text;
                Log("REC 标签已创建于难度文字下方");
            }
            catch (Exception e)
            {
                Log("创建 REC 标签出错：" + e.GetType().Name + ": " + e.Message);
                gaveUp = true;
                label = null;
                self = null;
            }
        }

        /// <summary>
        /// 把标签锚到难度文字的下边缘正中。
        ///
        /// <para>锚点用「底部中心」+ 枢轴「顶部中心」，所以 <c>anchoredPosition=(0,-2)</c>
        /// 就是「紧贴父节点下边缘、水平居中」——无论父节点是被布局组拉宽的还是本身固定宽。</para>
        /// </summary>
        private static void PlaceBelow(RectTransform src)
        {
            if (self == null || src == null)
                return;
            try
            {
                self.anchorMin = new Vector2(0.5f, 0f);
                self.anchorMax = new Vector2(0.5f, 0f);
                self.pivot = new Vector2(0.5f, 1f);
                self.anchoredPosition = new Vector2(0f, -2f);

                // 换锚点会让 sizeDelta 的含义从「相对锚点的偏移」变成「自身尺寸」，
                // 所以把当前实际渲染尺寸显式写回去（否则可能变成巨大/极小的矩形）。
                float w = src.rect.width;
                float h = src.rect.height;
                if (w <= 1f)
                    w = src.sizeDelta.x;
                if (h <= 1f)
                    h = src.sizeDelta.y;
                if (w < 60f)
                    w = 60f;
                if (h < 16f)
                    h = 16f;
                self.sizeDelta = new Vector2(w, h);

                // 父节点自己的缩放/旋转会被子节点继承，这里不能重复应用。
                self.localScale = Vector3.one;
                self.localRotation = Quaternion.identity;
            }
            catch (Exception e)
            {
                Log("定位 REC 标签失败：" + e.GetType().Name);
            }
        }

        /// <summary>更新文字；内容没变就不写（避免每 tick 触发一次文本重排）。</summary>
        public static void SetProgress(int tick, int total)
        {
            if (label == null || label.Pointer == IntPtr.Zero)
                return;
            var text = total > 0 ? "REC " + tick + "/" + total : "REC " + tick;
            if (text == lastText)
                return;
            try
            {
                label.text = text;
                lastText = text;
            }
            catch (Exception) { }
        }

        /// <summary>播放结束/停止时把标签抹掉。</summary>
        public static void Hide()
        {
            if (label == null)
                return;
            try
            {
                label.text = string.Empty;
                lastText = string.Empty;
            }
            catch (Exception) { }
        }

        /// <summary>离开关卡时清引用（节点已随场景销毁）。</summary>
        public static void Reset()
        {
            label = null;
            self = null;
            lastText = null;
            gaveUp = false;
        }

        private static void Log(string msg)
        {
            Plugin.Log?.LogInfo("[Replay] REC 标签：" + msg);
        }
    }
}
