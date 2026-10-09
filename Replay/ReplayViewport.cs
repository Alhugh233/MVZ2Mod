using System.Text;
using UnityEngine;

namespace Replay
{
    /// <summary>
    /// 把「录制那一刻的屏幕坐标」换算成「现在这个窗口里的屏幕坐标」。
    ///
    /// ── 为什么必须换 ─────────────────────────────────────────────────
    /// <para><c>.mrep</c> 里记的指针坐标是**当时的屏幕像素**（<c>Input.mousePosition</c>）。
    /// 游戏把像素变成世界坐标的公式（<c>LevelCamera</c>，正交相机）是：</para>
    /// <code>
    /// 世界偏移 = (像素 - 屏幕中心) * 2 * orthographicSize / 屏幕高
    /// </code>
    /// <para>注意分子里**只有屏幕高**：纵向可视范围恒为 <c>2*orthographicSize</c>，
    /// 横向才是「纵向 × 宽高比」。所以同一个像素在不同窗口尺寸下会指向**不同的格子**
    /// —— 这正是「回放里假鼠标和实际操作对不上，而且完全跟着窗口分辨率走」的原因：
    /// 操作本身是**语义化重演**的（记录的是 lane/column、实体 ID），跟像素无关；
    /// 而假鼠标（以及手持物位置）走的是像素，两者只在录制时那个分辨率下才重合。</para>
    ///
    /// <para>反解上式，要在新窗口里指向**同一个世界点**：</para>
    /// <code>
    /// k  = 新屏幕高 / 录制时屏幕高
    /// 新 = 新屏幕中心 + (旧 - 录制时屏幕中心) * k
    /// </code>
    /// <para>两个分量用同一个 <c>k</c>（横向可视范围随宽高比一起变，所以横向不能用
    /// 新宽/旧宽）。窗口尺寸没变时 <c>k = 1</c>、中心相同 → 原样返回，
    /// 所以「不换窗口」这条路上这个函数是**恒等**的，不会影响任何既有行为。</para>
    ///
    /// <para>旧文件没有 <c>sw</c>/<c>sh</c> 字段（版本 6 之前）→ <see cref="Active"/> 为假
    /// → 恒等返回，还是老行为。</para>
    /// </summary>
    internal static class ReplayViewport
    {
        private static float recW;
        private static float recH;

        public static float RecordWidth => recW;
        public static float RecordHeight => recH;

        /// <summary>有没有可用的录制时窗口尺寸（没有就一切照旧）。</summary>
        public static bool Active => recW > 1f && recH > 1f;

        /// <summary>开始一次播放：从文件头取录制时的窗口尺寸。</summary>
        public static void Capture(ReplayFile f)
        {
            recW = f != null ? f.ScreenW : 0f;
            recH = f != null ? f.ScreenH : 0f;
        }

        public static void Clear()
        {
            recW = 0f;
            recH = 0f;
        }

        /// <summary>把一个「录制时的屏幕坐标」换算成当前窗口里的屏幕坐标。</summary>
        public static Vector2 ToCurrent(float x, float y)
        {
            return Remap(x, y, recW, recH, Screen.width, Screen.height);
        }

        public static Vector2 ToCurrent(Vector2 p)
        {
            return Remap(p.x, p.y, recW, recH, Screen.width, Screen.height);
        }

        /// <summary>
        /// 同一个换算，但**假装**当前窗口是 <paramref name="nowW"/>×<paramref name="nowH"/>。
        ///
        /// <para>自测拿它验「播放中途换窗口尺寸」那条路：不必真的去改分辨率
        /// （那会打乱后续的截图与布局检定）。</para>
        /// </summary>
        public static Vector2 ToSize(Vector2 recordPos, float nowW, float nowH)
        {
            return Remap(recordPos.x, recordPos.y, recW, recH, nowW, nowH);
        }

        /// <summary>
        /// 纯函数版（自测要拿别的尺寸验算，所以单独拆出来，不依赖 <c>Screen.*</c>）。
        /// </summary>
        public static Vector2 Remap(float x, float y, float fromW, float fromH, float toW, float toH)
        {
            if (fromW <= 1f || fromH <= 1f || toW <= 1f || toH <= 1f)
                return new Vector2(x, y);
            float k = toH / fromH;
            return new Vector2(
                (x - fromW * 0.5f) * k + toW * 0.5f,
                (y - fromH * 0.5f) * k + toH * 0.5f);
        }

        public static string Describe()
        {
            var sb = new StringBuilder(96);
            if (!Active)
            {
                sb.Append("无（文件没记窗口尺寸，按原样使用）");
                return sb.ToString();
            }
            float k = Screen.height / recH;
            sb.Append("录制时 ").Append(recW.ToString("0")).Append('x').Append(recH.ToString("0"))
                .Append(" → 现在 ").Append(Screen.width).Append('x').Append(Screen.height)
                .Append("，缩放 k=").Append(k.ToString("0.####"));
            if (Mathf.Abs(k - 1f) < 0.0001f && Mathf.Abs(Screen.width - recW) < 0.5f)
                sb.Append("（恒等）");
            return sb.ToString();
        }

        /// <summary>
        /// 自测用：验证换算公式的两条不变量。
        ///
        /// <list type="number">
        /// <item>尺寸相同时是恒等映射；</item>
        /// <item>尺寸不同时，换算后的点相对**屏幕中心**的偏移（除以屏幕高）与换算前完全一致
        /// —— 这正是「指向同一个世界点」的充要条件。</item>
        /// </list>
        /// </summary>
        public static bool SelfCheck(out string detail)
        {
            var sb = new StringBuilder(256);
            bool ok = true;

            // ① 恒等
            var same = Remap(300f, 200f, 1024f, 766f, 1024f, 766f);
            bool sameOk = Mathf.Abs(same.x - 300f) < 0.01f && Mathf.Abs(same.y - 200f) < 0.01f;
            ok &= sameOk;
            sb.Append("同尺寸恒等=").Append(sameOk ? "OK" : ("失败(" + same.x.ToString("0.##") + "," + same.y.ToString("0.##") + ")"));

            // ② 世界偏移不变：1920x1080 下算一遍
            const float fw = 1024f, fh = 766f, tw = 1920f, th = 1080f;
            var p = Remap(900f, 600f, fw, fh, tw, th);
            float before = (600f - fh * 0.5f) / fh;
            float after = (p.y - th * 0.5f) / th;
            float bx = (900f - fw * 0.5f) / fh;
            float ax = (p.x - tw * 0.5f) / th;
            bool invOk = Mathf.Abs(after - before) < 0.0005f && Mathf.Abs(ax - bx) < 0.0005f;
            ok &= invOk;
            sb.Append("；1024x766→1920x1080 (900,600)→(")
                .Append(p.x.ToString("0.#")).Append(',').Append(p.y.ToString("0.#"))
                .Append(") 世界偏移不变=").Append(invOk ? "OK" : "失败");

            // ③ 屏幕中心始终映射到屏幕中心
            var c = Remap(fw * 0.5f, fh * 0.5f, fw, fh, tw, th);
            bool cOk = Mathf.Abs(c.x - tw * 0.5f) < 0.01f && Mathf.Abs(c.y - th * 0.5f) < 0.01f;
            ok &= cOk;
            sb.Append("；中心不动=").Append(cOk ? "OK" : "失败");

            detail = sb.ToString();
            return ok;
        }
    }
}
