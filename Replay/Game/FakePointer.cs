using System;
using System.Collections.Generic;
using System.Text;
using DSHCore.Ui;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MVZ2.Level;
using MVZ2.Managers;
using MVZ2Logic.Level;
using PVZEngine.Level;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Replay
{
    /// <summary>
    /// 回放时的「假鼠标」：把离散的动作路点合成一条连续轨迹，并把它画出来。
    ///
    /// ── 为什么需要合成而不是重放 ─────────────────────────────────────
    /// <para>录下来的只有**动作本身**（点格子 / 点实体 / 点卡槽 / 工具按钮），
    /// 一条一条都带 tick 戳，中间没有任何鼠标轨迹。而且：</para>
    /// <list type="bullet">
    /// <item><c>bpkey</c>（数字键选卡）与 <c>tool</c>（镐子/星之碎片/触发器）**根本没有坐标**
    /// （<c>ReplayRecorder</c> 那两条不调 <c>AppendPos</c>）；</item>
    /// <item>悬浮类事件（Hover/Enter/Stay/Exit）被当作噪音整个丢掉了。</item>
    /// </list>
    /// <para>于是 <c>ReplayPlayer.pointerScreen</c> 只在「带坐标的事件」被派发的那一瞬间才变，
    /// 手持物就停在原地、下一次点击时瞬移过去 —— 观感很差。</para>
    ///
    /// ── 只走「视觉通道」这条硬约束 ───────────────────────────────────
    /// <para>指针位置有好几个消费者，其中三个**会改仿真**：</para>
    /// <list type="bullet">
    /// <item><c>MinecartRideable.Update</c> —— 矿车整车位置每 tick 完全由指针决定；</item>
    /// <item><c>Snipenser.UpdateAI</c> —— 朝向 → <c>ShootProjectile</c> 的速度向量；</item>
    /// <item><c>BreakoutBoard</c> —— 走 <c>POST_POINTER_ACTION</c> 的 <c>screenPos</c>。</item>
    /// </list>
    /// <para>所以本类**绝不改写** <c>ReplayPlayer.pointerScreen</c> 本身，
    /// 而是在 <c>LevelController.UpdateHeldItemPosition</c> 这一层临时换上假鼠标的位置
    /// （见 <c>ReplayPatches.HeldItemPosPrefix</c>）—— 那里只是纯表现，
    /// 把屏幕坐标换掉以后，游戏自己会做 <c>ScreenToWorldPoint</c> + <c>SetHeldItemPosition</c>。
    /// 这个方法之外的一切代码看到的仍是事件的原值，指纹因此不可能受影响。</para>
    ///
    /// ── 为什么还要「画」一个鼠标 ─────────────────────────────────────
    /// <para>手持物品时，物品模型**就是**指针（<c>HeldItemCursorSource</c> 把系统光标设成
    /// <c>CursorType.Empty</c>）；可**什么都没拿的时候游戏不画任何指针**，
    /// 那时光标是系统光标 —— 它停在玩家自己的鼠标位置上，跟回放毫无关系。
    /// 所以这里额外画一张「游戏光标样式」的贴图，全程显示在假鼠标的位置上。</para>
    ///
    /// <para>失败一律吞掉只记日志 —— 一个装饰性光标绝不能把播放本身搞崩。</para>
    /// </summary>
    internal static class FakePointer
    {
        private const string NodeName = "DSH_ReplayCursor";

        // ------------------------------------------------------------------
        // 轨迹状态
        // ------------------------------------------------------------------
        /// <summary>
        /// 路点表。
        ///
        /// <para>⚠️ 存的是**录制时**的屏幕像素（和文件里的 <c>sx/sy</c> 同制），
        /// 不是当前窗口的像素。换算放到**每次取值**时做（见 <see cref="ScreenPos"/>）——
        /// 如果在这里就把换算结果烘进去，玩家在**播放中途**改窗口尺寸时就又对不上了。</para>
        /// </summary>
        private static Vector2[] pts;
        private static int[] ptTicks;
        private static int ptCount;
        private static bool running;
        private static int nextIdx;            // 下一个「还没成为 prev」的路点
        private static int prevIdx;
        private static bool hasPrev;
        private static Vector2 current;
        /// <summary>
        /// 没有任何可用路点（纯热键回放 / 坐标全是哨兵值）时为真。
        ///
        /// <para>此时 <see cref="current"/> 不是「录制坐标」而是「屏幕正中」这种几何量，
        /// 不能再拿去换算（换算会把它当成录制坐标而偏掉），所以单独标记。</para>
        /// </summary>
        private static bool stationary;

        // 统计（写进播放小结）
        private static int movedTicks;
        private static int stillTicks;
        private static int droppedPoints;      // 被过滤掉的哨兵坐标数

        // ------------------------------------------------------------------
        // 视觉状态
        // ------------------------------------------------------------------
        private static Canvas canvas;
        private static RectTransform canvasRect;
        private static GameObject node;
        private static RectTransform rect;
        private static Image image;
        private static Sprite cursorSprite;
        private static Texture2D arrowTex;
        /// <summary>
        /// 光标热点，单位 = 贴图像素，**原点在贴图左上角**。
        ///
        /// <para>为什么是左上角：游戏自己把它交给 <c>Cursor.SetCursor(texture, hotspot, mode)</c>，
        /// 而 Unity 那个重载的热点就是**从左上角量**的（实测游戏的箭头就是 <c>(0,0)</c>）。
        /// 而 <c>RectTransform.pivot</c> / <c>Sprite.Create</c> 的枢轴用的是**左下角原点** ——
        /// 两者差一个上下翻转，混用就会把光标画到指针的另一侧
        /// （实测：看起来像「锚点在贴图正中」，因为它整整偏了一个贴图高度）。</para>
        /// </summary>
        private static Vector2 arrowHotspot;
        private static string spriteSource = "-";
        private static string texInfo = "-";
        private static string uvInfo = "-";
        private static string liveInfo = "-";
        private static float usedSf = 1f;
        private static float usedScale = 1f;
        private static int spriteResets;     // 精灵被别人改掉、我们又按回去的次数
        private static int fadeResets;        // 颜色乘数被改写、我们又按回白色的次数
        private static string nodeInfo = "-";
        private static string placeInfo = "-";
        private static bool gaveUp;
        private static int shownState = -1;   // -1 未定 / 0 隐 / 1 显
        private static string diag = "-";

        // ---- 手持物同步的观测（自测报告用）----
        // 手持蓝图/工具时我们不画光标（物品模型就是指针），所以「缓动有没有生效」
        // 只能从「SetHeldItemPosition 被我们改写过多少次」看。
        private static int heldSyncHits;      // 被假鼠标改写世界坐标的次数
        private static int holdingTicks;      // 检测到「手上有东西」的 tick 数

        /// <summary>手持物世界坐标被假鼠标改写的次数（0 = 那条通道没生效）。</summary>
        public static int HeldSyncHits { get { return heldSyncHits; } }

        /// <summary>手上有东西的 tick 数（0 = 这条回放里没拿过蓝图/工具）。</summary>
        public static int HoldingTicks { get { return holdingTicks; } }

        /// <summary>由 <c>ReplayPatches.HeldItemWorldPrefix</c> 调。</summary>
        internal static void NoteHeldSync()
        {
            heldSyncHits++;
        }

        // 上一次真正摆放时的屏幕点与世界坐标（自测要验它，见 SelfCheckPlacement）。
        // 为什么不在需要时现算：手上有物品时我们不摆放（游戏用物品模型当指针），
        // 那时「当前该在哪」和「上一次摆在哪」是两回事。
        private static Vector2 lastPlaceScreen;
        private static Vector3 lastPlaceWorld;
        private static bool hasPlaced;

        // 贴图原尺寸 + 上一次算尺寸时的窗口大小。
        // 假鼠标的 sizeDelta 要按「画布 UI 单位 ↔ 屏幕像素」换算，
        // 而这个比值会跟着窗口尺寸变 —— 只算一次的话，改过窗口后光标大小就不对了。
        private static float texW = 32f;
        private static float texH = 32f;
        private static int sizedScreenW;
        private static int sizedScreenH;

        // ------------------------------------------------------------------
        // 对外
        // ------------------------------------------------------------------

        /// <summary>
        /// 假鼠标当前所在的**屏幕坐标**（左下角为原点，与 <c>Input.mousePosition</c> 同制）。
        ///
        /// <para>已经换算到**当前**窗口：玩家在播放中途改窗口尺寸也一样跟着走。
        /// 换算公式见 <see cref="ReplayViewport"/>（这同时也是游戏自己把像素变成
        /// 世界坐标那一步的逆运算 —— 换窗口后指针仍指向同一个格子，而不是同一个像素）。</para>
        /// </summary>
        public static Vector2 ScreenPos
        {
            get
            {
                if (stationary)
                    return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                return ReplayViewport.ToCurrent(current);
            }
        }

        /// <summary>
        /// **未换算**的假鼠标位置（录制坐标系）。
        ///
        /// <para>只给「临时换上假鼠标位置」那条通道用（<c>HeldItemPosPrefix</c>）：
        /// 那边两边都得是同一个坐标系，混着用会在重复换算中越偏越多。</para>
        /// </summary>
        public static Vector2 RawScreenPos => current;

        /// <summary>本类是否应该接管指针位置（回放中 + 开关开着 + 有轨迹可用）。</summary>
        public static bool Active => running;

        /// <summary>画出来的那个节点是不是已经建好了（自测要用它验锚点/尺寸）。</summary>
        public static bool NodeReady => rect != null && rect.Pointer != IntPtr.Zero;

        /// <summary>路点表里有没有东西（自测据此决定要不要塞假路点）。</summary>
        public static bool HasTrack => ptCount > 0;

        /// <summary>是不是「没有任何可用坐标」那个静止模式（自测的报告里会标出来）。</summary>
        public static bool StationaryOnly => stationary;

        /// <summary>
        /// 自测专用：塞一条**假路点表**（坐标必须是录制坐标系的）。
        ///
        /// <para>为什么需要：自测注入的那几个动作（数字键选卡 / 工具按钮）**一个坐标都没有**，
        /// 所以「假鼠标位置随窗口尺寸换算」这条路径在自测里根本不会被执行 ——
        /// 而它恰恰是用户报的那个 bug 所在。塞一条假表就能把
        /// <see cref="ScreenPos"/> / <see cref="RawScreenPos"/> / <see cref="PlaceAt"/> / 
        /// <see cref="ApplySize"/> 这条链完整跑一遍（用的是与 <see cref="Begin"/> 完全相同的取值方式）。</para>
        ///
        /// <para>用完必须调 <see cref="SelfTestClearTrack"/> 还原成「无路点」状态。</para>
        /// </summary>
        public static void SelfTestSetTrack(Vector2[] recordPoints)
        {
            if (recordPoints == null || recordPoints.Length == 0)
            {
                SelfTestClearTrack();
                return;
            }
            pts = (Vector2[])recordPoints.Clone();
            int n = pts.Length;
            // tick 取 {0,1,...}：第一个 tick 之后就会稳定停在最后一个路点上，
            // 不会因为插值而让「原始坐标」变成别的值。
            ptTicks = new int[n];
            for (int i = 0; i < n; i++)
                ptTicks[i] = i;
            ptCount = n;
            prevIdx = n - 1;
            nextIdx = n;
            hasPrev = true;
            stationary = false;
            current = pts[n - 1];
        }

        /// <summary>自测专用：把假路点表去掉，回到「无路点（静止）」状态。</summary>
        public static void SelfTestClearTrack()
        {
            pts = null;
            ptTicks = null;
            ptCount = 0;
            prevIdx = 0;
            nextIdx = 0;
            hasPrev = false;
            stationary = true;
            current = Vector2.zero;
        }

        /// <summary>
        /// 自测用：验证**上一次真正摆放**的落点。
        ///
        /// <para>为什么不能用「当前位置」当判据：手上有物品时游戏用**物品模型当指针**，
        /// <c>UpdateVisual</c> 会直接跳过摆放（假鼠标节点是隐藏的），
        /// 那时 <c>rect.position</c> 还停在上一次摆放的位置上。
        /// 所以只能验「上一次摆放时算出来的世界坐标对不对」。</para>
        ///
        /// <para>两段判据：</para>
        /// <list type="number">
        /// <item>那次摆放的世界坐标 == 那个屏幕点经相机反投影的结果（验 <see cref="PlaceAt"/> 的数学）；</item>
        /// <item>若节点当前可见，则那次摆放用的屏幕点 == <see cref="ScreenPos"/>（验节点真的在跟当前换算后的位置）。</item>
        /// </list>
        /// </summary>
        public static bool SelfCheckPlacement(out string detail)
        {
            if (!hasPlaced)
            {
                detail = "还没有摆放过";
                return false;
            }
            try
            {
                var have = lastPlaceWorld;
                var want = ProjectToWorld(lastPlaceScreen, have.z);
                float dProj = (have - want).magnitude;
                bool projOk = dProj < 0.05f;

                bool visible = shownState == 1;
                float dTrack = (lastPlaceScreen - ScreenPos).magnitude;
                bool trackOk = !visible || dTrack < 0.5f;

                detail = "上次摆放屏=" + lastPlaceScreen.x.ToString("0.#") + "," + lastPlaceScreen.y.ToString("0.#")
                    + " 世界=(" + have.x.ToString("0.###") + "," + have.y.ToString("0.###") + ")"
                    + " 反投影应为=(" + want.x.ToString("0.###") + "," + want.y.ToString("0.###")
                    + ") 偏差=" + dProj.ToString("0.####") + "=" + (projOk ? "OK" : "失败")
                    + "；节点" + (visible ? "可见" : "隐藏（手持物品时游戏用物品模型当指针）")
                    + (visible ? "，与当前 ScreenPos 差=" + dTrack.ToString("0.###") + "=" + (trackOk ? "OK" : "失败") : "");
                return projOk && trackOk;
            }
            catch (Exception e)
            {
                detail = "回读出错：" + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>把一个屏幕点投到画布平面上（只用一套实现，保证「摆放」与「校验」同源）。</summary>
        private static Vector3 ProjectToWorld(Vector2 screen, float fallbackZ)
        {
            if (canvas == null || canvasRect == null)
                return new Vector3(screen.x, screen.y, fallbackZ);
            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                float sf = canvas.scaleFactor <= 0.0001f ? 1f : canvas.scaleFactor;
                return new Vector3(screen.x / sf, screen.y / sf, fallbackZ);
            }
            var cam = canvas.worldCamera;
            if (cam == null)
                return new Vector3(screen.x, screen.y, fallbackZ);
            var w = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, canvas.planeDistance));
            w.z = canvasRect.position.z;
            return w;
        }

        public static string Diag => diag;

        /// <summary>
        /// 开始一次播放：把回放里的「带坐标事件」整理成路点表，并把假鼠标吸附到第一点上。
        ///
        /// <para><b>为什么要先过滤：</b>录制时有一小部分事件（实测 <c>mountain_10</c> 那条
        /// 4564 个事件里有 <b>222</b> 个：198 个 entity + 24 个 grid）拿到的
        /// <c>sx/sy</c> 与 <c>wx/wy/wz</c> **全是 0** —— 那是**屏幕左下角**，明显是
        /// 「游戏构造这个事件时没填位置」的哨兵值（详见 <see cref="IsUsable"/>）。
        /// 不滤掉的话，假鼠标会隔三差五往左下角跑一趟。</para>
        /// </summary>
        public static void Begin(ReplayFile f)
        {
            End();
            diag = "-";
            // 这两个是**本次播放**的统计，播放中途不清（收尾报告要读它们）。
            heldSyncHits = 0;
            holdingTicks = 0;
            if (f == null)
                return;
            if (!ReplayConfig.GetFakeMouse())
            {
                diag = "关（配置）";
                return;
            }

            var events = f.Events;
            if (events == null || events.Count == 0)
            {
                diag = "无事件";
                return;
            }

            spriteResets = 0;
            fadeResets = 0;
            stationary = false;

            // ---- 建路点表 ----
            var pos = new List<Vector2>(events.Count / 4 + 8);
            var tks = new List<int>(events.Count / 4 + 8);
            droppedPoints = 0;
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (e == null || !e.HasPos)
                    continue;
                var raw = new Vector2(e.Sx, e.Sy);
                if (!IsUsable(raw))
                {
                    droppedPoints++;
                    continue;
                }
                // ⚠️ 先过滤再**存原值**：过滤看的是**原始**像素（哨兵值就是 (0,0)），
                // 而且换算必须留到取值时做 —— 播放中途改窗口尺寸也得跟着变
                //（见 ScreenPos 与 ReplayViewport）。
                var p = raw;
                // 同一 tick 的多个路点只留最后一个（否则 span = 0，那一跳会瞬间完成）。
                if (tks.Count > 0 && tks[tks.Count - 1] == e.Tick)
                {
                    pos[pos.Count - 1] = p;
                    continue;
                }
                pos.Add(p);
                tks.Add(e.Tick);
            }

            if (pos.Count == 0)
            {
                // 整条回放一条可用坐标都没有（纯热键 / 坐标全是哨兵）。
                // 仍然画一个静止的鼠标，位置用**当前**屏幕正中 —— 总比让它停在左下角好。
                diag = "无可用坐标（静止）";
                stationary = true;
                current = Vector2.zero;
                pts = null;
                ptTicks = null;
                ptCount = 0;
                hasPrev = false;
                nextIdx = 0;
                movedTicks = 0;
                stillTicks = 0;
                running = true;
                return;
            }

            pts = pos.ToArray();
            ptTicks = tks.ToArray();
            ptCount = pts.Length;

            // 起点：取 startTick 之前最后一个路点；没有（绝大多数情况）就吸附到第一个，
            // 免得鼠标从屏幕左下角飞过来。
            int start = 0;
            while (start < ptCount && ptTicks[start] < f.StartTick)
                start++;
            if (start >= ptCount)
                start = ptCount - 1;
            prevIdx = start;
            nextIdx = start + 1;
            hasPrev = true;
            current = pts[prevIdx];
            movedTicks = 0;
            stillTicks = 0;
            running = true;
        }

        /// <summary>
        /// 这个屏幕坐标能不能当路点用。
        ///
        /// <para>判据就是「会不会是 (0,0) 那个哨兵」。两个分量都 ≤ 0 才算哨兵 ——
        /// 单独一个 0 是合法的（贴着屏幕左边缘或下边缘），只有同时为 0
        /// 才不可能是一次真实点击（那得点中窗口最左下角那一个像素）。</para>
        /// </summary>
        private static bool IsUsable(Vector2 p)
        {
            if (float.IsNaN(p.x) || float.IsNaN(p.y))
                return false;
            return !(p.x <= 0f && p.y <= 0f);
        }

        /// <summary>结束一次播放：停掉轨迹、把画出来的光标藏起来。</summary>
        public static void End()
        {
            running = false;
            pts = null;
            ptTicks = null;
            ptCount = 0;
            nextIdx = 0;
            prevIdx = 0;
            hasPrev = false;
            stationary = false;
            SetShown(false);
        }

        /// <summary>
        /// 每个逻辑 tick 调一次（在事件派发**之前**）。推进假鼠标。
        /// </summary>
        public static void Tick(LevelController ctrl, LevelEngine level, int tick)
        {
            if (!running)
                return;
            try
            {
                Advance(tick);
                UpdateVisual(level);
            }
            catch (Exception e)
            {
                diag = "出错：" + e.GetType().Name;
                running = false;
            }
        }

        // ------------------------------------------------------------------
        // 轨迹
        // ------------------------------------------------------------------

        /// <summary>
        /// 把假鼠标推到 <paramref name="tick"/> 对应的位置。
        ///
        /// <para>插值在 **tick 空间**做，而不是按真实时间：暂停时没有 tick → 指针自动冻住；
        /// 游戏内加速时 tick 变密 → 指针自动跟着变快。这两件事全都免费。</para>
        ///
        /// <para><b>默认（<c>FakeMouseTicks = 0</c>）缓动覆盖「上一个路点 → 下一个路点」
        /// 这**整个区间**</b> —— 即上一个动作刚做完就开始往下一个位置走，全程不停。
        /// 这正是「鼠标不静止」的关键：旧写法只在区间**最后 N 个 tick** 里走完，
        /// 前面一大段时间鼠标是钉住不动的（实测一段几百 tick 的间隔里九成时间都静止）。</para>
        ///
        /// <para>设成正数则恢复旧行为：先停着，最后 N 个 tick 一口气走过去。</para>
        /// </summary>
        private static void Advance(int tick)
        {
            if (!hasPrev || ptCount == 0)
                return;

            // 已经到点的路点全部吸收为 prev。
            while (nextIdx < ptCount && ptTicks[nextIdx] <= tick)
            {
                prevIdx = nextIdx;
                nextIdx++;
            }

            if (nextIdx >= ptCount)
            {
                // 没有下一个路点了：停在最后一个位置。
                current = pts[ptCount - 1];
                return;
            }

            var target = pts[nextIdx];
            int span = ptTicks[nextIdx] - ptTicks[prevIdx];
            if (span <= 0)
            {
                current = target;
                return;
            }

            int travel = ReplayConfig.GetFakeMouseTicks();
            if (travel <= 0 || travel > span)
                travel = span;                 // 0（默认）= 整个区间

            int begin = ptTicks[nextIdx] - travel;
            float t = tick <= begin ? 0f : (float)(tick - begin) / travel;
            if (t < 0f) t = 0f;
            else if (t > 1f) t = 1f;

            current = Vector2.Lerp(pts[prevIdx], target, Ease(t));
            movedTicks++;
            if (t <= 0f)
                stillTicks++;                  // 真正钉住不动的 tick 数（越少越好）
        }

        private static float Ease(float t)
        {
            switch (ReplayConfig.GetFakeMouseEase())
            {
                case "linear":
                    return t;
                case "quadinout":
                    return t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t);
                case "cubicout":
                    {
                        float u = 1f - t;
                        return 1f - u * u * u;
                    }
                case "quadout":
                default:
                    {
                        float u = 1f - t;
                        return 1f - u * u;
                    }
            }
        }

        // ------------------------------------------------------------------
        // 视觉
        // ------------------------------------------------------------------

        private static void UpdateVisual(LevelEngine level)
        {
            // 手上有东西时，物品模型**就是**指针 —— 再画一个就是双光标了。
            bool holding = false;
            try { holding = level != null && LogicLevelExt.IsHoldingItem(level); }
            catch (Exception) { }

            if (holding)
                holdingTicks++;

            if (holding || !EnsureNode())
            {
                SetShown(false);
                return;
            }

            ApplySize();
            // 每次都用**当前**窗口换算 —— 播放中途改尺寸也能立即跟上。
            PlaceAt(ScreenPos);
            SetShown(true);
            ForceWhite();
        }

        /// <summary>
        /// 每帧把光标精灵 / 颜色 / 绘制类型**重新按回去**。
        ///
        /// <para><b>为什么需要每帧按：</b>这个节点是从原版「文本按钮」克隆的，
        /// 它身上的 <c>Selectable</c> 用 SpriteSwap 过渡，会把按钮自己的 sprite 写到
        /// <c>targetGraphic</c>（我们的 <c>Image</c>）上 —— 实测光标精灵被换成了
        /// 按钮那张 400x40 的 DXT1 贴图，渲染出来就是一块**灰方块**。
        /// <c>Destroy</c> 是帧末生效的，拦不住 `OnEnable`；就算现在靠
        /// <c>transition = None</c> 治了根，只要以后原版改了模板实现，这里还能兜住。</para>
        ///
        /// <para>同时把 <c>CanvasRenderer</c> 的颜色乘数按回白色：
        /// <c>Selectable</c> 残留的 <c>CrossFadeColor</c> 会一直乘在顶点色上。
        /// （<c>Graphic.color</c> 管的是顶点色，覆盖不了它。）</para>
        ///
        /// <para>都比较过再写，所以稳态下零开销。</para>
        /// </summary>
        private static void ForceWhite()
        {
            try
            {
                if (image == null || image.Pointer == IntPtr.Zero)
                    return;
                if (cursorSprite != null && cursorSprite.Pointer != IntPtr.Zero)
                {
                    var sp = image.sprite;
                    if (sp == null || sp.Pointer != cursorSprite.Pointer)
                    {
                        try { image.overrideSprite = null; } catch (Exception) { }
                        image.sprite = cursorSprite;
                        image.type = Image.Type.Simple;
                        spriteResets++;
                    }
                }
                var cr = image.canvasRenderer;
                if (cr != null)
                {
                    var c = cr.GetColor();
                    if (c.r < 0.999f || c.g < 0.999f || c.b < 0.999f || c.a < 0.999f)
                    {
                        cr.SetColor(Color.white);
                        fadeResets++;
                    }
                }
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 按**当前**窗口尺寸重新算一次假鼠标的显示大小。
        ///
        /// <para>画布是「屏幕空间-相机」+ <c>CanvasScaler.ScaleWithScreenSize</c>：
        /// 画布矩形（UI 单位）与屏幕像素的比值跟着窗口尺寸 / 宽高比变。
        /// 只在建节点时算一次的话，之后改过窗口大小光标就会明显偏大或偏小
        /// ——「假鼠标位置与窗口分辨率强相关」的另一半就是它。</para>
        ///
        /// <para>比较过才写，所以稳态下零开销（每 tick 调用也没关系）。</para>
        /// </summary>
        private static void ApplySize()
        {
            if (rect == null || rect.Pointer == IntPtr.Zero)
                return;
            if (Screen.width == sizedScreenW && Screen.height == sizedScreenH)
                return;
            try
            {
                if (canvasRect == null || canvasRect.Pointer == IntPtr.Zero)
                    return;
                // 画布矩形 v.s. 屏幕像素：要画 N 像素就得写 N * rect.width/Screen.width（是乘不是除）。
                float sf = canvasRect.rect.width / Mathf.Max(1f, Screen.width);
                if (sf <= 0.0001f)
                    sf = 1f;
                float scale = ReplayConfig.GetFakeMouseScale();
                rect.sizeDelta = new Vector2(texW, texH) * (sf * scale);
                usedSf = sf;
                usedScale = scale;
                sizedScreenW = Screen.width;
                sizedScreenH = Screen.height;
            }
            catch (Exception) { }
        }

        private static void SetShown(bool show)
        {
            int want = show ? 1 : 0;
            if (want == shownState)
                return;
            shownState = want;
            try
            {
                if (node != null && node.Pointer != IntPtr.Zero)
                    node.SetActive(show);
            }
            catch (Exception) { }
        }

        private static bool EnsureNode()
        {
            if (node != null && node.Pointer != IntPtr.Zero)
                return true;
            if (gaveUp)
                return false;

            try
            {
                canvas = UiGame.MainCanvas;
                if (canvas == null)
                {
                    diag = "找不到主画布";
                    gaveUp = true;
                    return false;
                }
                canvasRect = canvas.GetComponent<RectTransform>();
                if (canvasRect == null)
                {
                    diag = "主画布没有 RectTransform";
                    gaveUp = true;
                    return false;
                }

                var s = GetCursorSprite();
                if (s == null)
                {
                    gaveUp = true;
                    return false;
                }

                // 借「文本按钮」当模板：本库的第一原则是**绝不 AddComponent 造控件**
                // （原版控件全靠私有 [SerializeField] 引用，自己造的必崩或必无效），
                // 这里只要它身上那张 Image，其余部分全部拆掉。
                var go = UiSkin.Clone(UiSkinPart.Button, canvas.transform, NodeName);
                if (go == null)
                {
                    diag = "克隆按钮模板失败";
                    gaveUp = true;
                    return false;
                }

                var btn = go.GetComponent<Button>();
                if (btn != null)
                {
                    // ★ 关键：**先把过渡关掉再销毁**。
                    //
                    // 按钮模板的 Selectable 用的是 **SpriteSwap** 过渡，它在 `OnEnable` /
                    // 状态变化时会把**自己的 sprite 写到 targetGraphic 上**（实测：我们设好的
                    // 光标精灵被换成了按钮那张 400x40 的 DXT1 贴图，于是画出来就是一块灰方块）。
                    // 而 `Destroy` 是**帧末**生效的：我们后面 `SetActive(true)` 时 Button 还活着，
                    // `OnEnable` 照样跑 —— 光靠 Destroy 拦不住。
                    //
                    // （`transition = None` 会立即生效，且 `DoStateTransition` 会直接 return，
                    // 所以这是根治；下面 `ForceWhite` 里的每帧重按只是兼底。）
                    try { btn.transition = Selectable.Transition.None; } catch (Exception) { }
                    try { btn.targetGraphic = null; } catch (Exception) { }
                    UnityEngine.Object.Destroy(btn);
                }
                var txt = go.GetComponentInChildren<TextMeshProUGUI>(true);
                if (txt != null)
                    UnityEngine.Object.Destroy(txt.gameObject);

                image = go.GetComponent<Image>();
                rect = go.GetComponent<RectTransform>();
                if (image == null || rect == null)
                {
                    diag = "克隆体上没有 Image/RectTransform";
                    gaveUp = true;
                    UnityEngine.Object.Destroy(go);
                    return false;
                }

                // 克隆体里除了我们自己那张图，其它 Graphic 一律关掉 ——
                // 否则按钮模板自带的美术会跟我们抢同一个矩形（实测看到的就是一团方块）。
                int extra = 0;
                foreach (var g in go.GetComponentsInChildren<Image>(true))
                {
                    if (g == null || g.Pointer == IntPtr.Zero)
                        continue;
                    if (g.Pointer == image.Pointer)
                        continue;
                    g.enabled = false;
                    extra++;
                }

                image.sprite = s;
                try { image.overrideSprite = null; } catch (Exception) { }
                image.color = Color.white;
                image.type = Image.Type.Simple;
                image.preserveAspect = false;
                image.raycastTarget = false;
                image.enabled = true;

                // ⚠️ 两个「克隆带来的残留」，都会让光标渲染成一团纯色：
                //   ① `Image.material` 可能不是默认的 UI 材质 → 显式清成 null（回到默认）；
                //   ② `Selectable`（按钮）在 OnEnable 时用 `CrossFadeColor` 写过
                //      **CanvasRenderer 的颜色乘数**（正常/禁用态的灰），
                //      而 `Destroy(Button)` **不会**把它清掉 —— 于是整张图被乘成灰的。
                //      只能自己覆盖回白。（`Graphic.color` 管的是顶点色，覆盖不了它。）
                try { image.material = null; } catch (Exception) { }
                ForceWhite();

                // 锚点统一走「中心」；枢轴设成光标的**热点比例**，这样假鼠标的
                // 「点击点」正好落在热点上（和系统光标的语义一致）。
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.localScale = Vector3.one;
                rect.localRotation = Quaternion.identity;

                var tex = s.texture;
                float w = tex != null ? tex.width : 32f;
                float h = tex != null ? tex.height : 32f;
                if (w < 1f) w = 32f;
                if (h < 1f) h = 32f;
                texW = w;
                texH = h;
                rect.pivot = HotspotToPivot(arrowHotspot, w, h);

                // 画布是「屏幕空间-相机」模式，UI 单位 ≠ 屏幕像素。
                // 画布矩形 800x598 对应屏幕 1024x766，即 1 UI 单位 = Screen/rect 个像素，
                // 所以「要画 N 像素」就得写 N * rect.width/Screen.width —— 是**乘**不是除
                // （实测画布矩形恰好是可视区域，枢轴（0.5,0.5））。
                // 再乘一个玩家可配的倍率（FakeMouseScale）给「觉得光标偏小/偏大」时用。
                sizedScreenW = 0;      // 强制下面那次 ApplySize 真算一遍
                sizedScreenH = 0;
                ApplySize();

                try { rect.SetAsLastSibling(); } catch (Exception) { }

                // 再抬一层：本节点挂在主画布根下（与官方对话框同级），加一个
                // overrideSorting 画布保证它盖在所有 UI 之上。
                try
                {
                    var cv = go.GetComponent<Canvas>();
                    if (cv == null)
                        cv = go.AddComponent<Canvas>();
                    cv.overrideSorting = true;
                    cv.sortingOrder = 30000;
                }
                catch (Exception) { }

                node = go;
                diag = "就绪";
                nodeInfo = DescribeNode(s, tex, w, h, usedSf, extra);
                return true;
            }
            catch (Exception e)
            {
                diag = "创建失败：" + e.GetType().Name + ": " + e.Message;
                gaveUp = true;
                node = null;
                rect = null;
                image = null;
                return false;
            }
        }

        /// <summary>
        /// 克隆完立刻回读一遍真实状态。
        ///
        /// <para>为什么要回读：**「设置了」不等于「生效了」** ——
        /// 克隆体上原有的组件、精灵的边界、Image 的类型都可能把我们的设定吃掉，
        /// 而这些从代码上看不出来，只能把真实值打出来。</para>
        /// </summary>
        private static string DescribeNode(Sprite s, Texture2D tex, float w, float h, float sf, int extraGraphics)
        {
            var sb = new StringBuilder(256);
            try
            {
                sb.Append("贴图=").Append(spriteSource).Append(' ').Append(texInfo)
                    .Append(" 精灵rect=").Append(s != null ? s.rect.width.ToString("0") + "x" + s.rect.height.ToString("0") : "null")
                    // 枢轴是「左下角为原点」的归一化值：(0,1) 才是贴图左上角。
                    .Append(" 枢轴=").Append(rect != null ? rect.pivot.x.ToString("0.##") + "," + rect.pivot.y.ToString("0.##") : "?")
                    .Append("（左下角为原点；0,1 = 贴图左上角）")
                    .Append(" 热点=").Append(arrowHotspot.x.ToString("0.#")).Append(',').Append(arrowHotspot.y.ToString("0.#"))
                    .Append("（左上角为原点，与 Cursor.SetCursor 同制）")
                    .Append(" 尺寸=").Append((w * sf).ToString("0.#") + "x" + (h * sf).ToString("0.#"))
                    .Append(" 画布比=").Append(sf.ToString("0.###"));
                sb.Append("\n  节点 : sprite=");
                if (image == null)
                    sb.Append("**没有 Image**");
                else
                {
                    sb.Append(image.sprite != null ? ("对象#=" + image.sprite.GetInstanceID()) : "**null**");
                    sb.Append(" type=").Append(image.type)
                        .Append(" color=").Append(image.color.r.ToString("0.##") + "," + image.color.g.ToString("0.##") + "," + image.color.b.ToString("0.##") + "," + image.color.a.ToString("0.##"))
                        .Append(" enabled=").Append(image.enabled ? 1 : 0)
                        .Append(" 材质=").Append(image.material != null ? image.material.name : "默认")
                        .Append(" 与来源精灵同一个=").Append(image.sprite != null && image.sprite.Pointer == s.Pointer ? 1 : 0);
                }
                sb.Append(" 关掉的其它图=").Append(extraGraphics);
            }
            catch (Exception e)
            {
                sb.Append("(回读出错 ").Append(e.GetType().Name).Append(')');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 把画出来的光标摆到屏幕坐标 <paramref name="screen"/> 上。
        ///
        /// <para><b>为什么直接用世界坐标而不是 <c>ScreenPointToLocalPointInRectangle</c>：</b>
        /// 后者返回的是画布矩形**自己的局部坐标**，还得再按 <c>size*(0.5-pivot)</c>
        /// 换算成 <c>anchoredPosition</c>；而根画布的 <c>pivot</c> 未必是正中，
        /// 一旦算错整个光标就跑到画布角落去（实测：一直钉在左下角）。
        /// 直接把 <c>rect.position</c>（世界坐标）设过去就完全绕开了这一层换算，
        /// 画布无论怎么对齐、缩放、有什么枢轴都不影响。</para>
        /// </summary>
        private static void PlaceAt(Vector2 screen)
        {
            if (rect == null || canvasRect == null || canvas == null)
                return;
            try
            {
                var here = rect.position;
                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    rect.position = ProjectToWorld(screen, here.z);
                }
                else
                {
                    var cam = canvas.worldCamera;
                    if (cam == null)
                    {
                        placeInfo = "画布没有 worldCamera（renderMode=" + canvas.renderMode + "）";
                        return;
                    }
                    // planeDistance 就是画布平面到相机的距离，用它反投影得到的
                    // 正好是画布平面上的那一点。
                    rect.position = ProjectToWorld(screen, here.z);
                }

                lastPlaceScreen = screen;
                lastPlaceWorld = rect.position;
                hasPlaced = true;

                var ap = rect.anchoredPosition;
                var lp = rect.localPosition;
                placeInfo = "屏=" + screen.x.ToString("0") + "," + screen.y.ToString("0")
                    + " 画布=" + DescribeCanvasKind()
                    + " 本地=" + lp.x.ToString("0.#") + "," + lp.y.ToString("0.#")
                    + " 锚定=" + ap.x.ToString("0.#") + "," + ap.y.ToString("0.#");
                liveInfo = DescribeLive();
            }
            catch (Exception e)
            {
                placeInfo = "定位出错：" + e.GetType().Name + ": " + e.Message;
            }
        }

        private static string DescribeCanvasKind()
        {
            try
            {
                var r = canvasRect.rect;
                return canvas.renderMode + " rect=" + r.width.ToString("0") + "x" + r.height.ToString("0")
                    + " 枢轴=" + canvasRect.pivot.x.ToString("0.##") + "," + canvasRect.pivot.y.ToString("0.##")
                    + " 缩放=" + canvasRect.localScale.x.ToString("0.###")
                    + " 屏=" + Screen.width + "x" + Screen.height;
            }
            catch (Exception)
            {
                return "?";
            }
        }

        /// <summary>
        /// 当前这一帧真的会被渲染成什么样子 —— 每帧回读，写进播放小结。
        ///
        /// <para><b>为什么不能只在建节点时回读一次：</b>节点建成时一切正常、
        /// 渲染却是一块纯色，说明状态是**后来**被改掉的（或被回收掉的）。
        /// 只有每帧回读才能看到当时的值。</para>
        /// </summary>
        private static string DescribeLive()
        {
            var sb = new StringBuilder(120);
            try
            {
                if (image == null || image.Pointer == IntPtr.Zero)
                    return "无 Image";
                var sp = image.sprite;
                sb.Append("sprite=").Append(sp == null ? "**null**" : "有");
                if (sp != null)
                {
                    var t = sp.texture;
                    sb.Append(" 其贴图=").Append(t == null ? "**null**" : (t.width + "x" + t.height + t.format));
                    sb.Append(" 是我们的=").Append(t != null && arrowTex != null && t.Pointer == arrowTex.Pointer ? 1 : 0);
                }
                var cr = image.canvasRenderer;
                sb.Append(" CR色=").Append(cr != null ? Fmt(cr.GetColor()) : "?");
            }
            catch (Exception e)
            {
                sb.Append("回读出错 ").Append(e.GetType().Name);
            }
            return sb.ToString();
        }

        /// <summary>取一张「游戏光标样式」的贴图。取不到时退回自绘的箭头，保证功能不静默失效。</summary>
        private static Sprite GetCursorSprite()
        {
            if (cursorSprite != null && cursorSprite.Pointer != IntPtr.Zero)
                return cursorSprite;

            if (ReplayConfig.GetFakeMouseGameCursor() && TryGetGameCursor(out var src, out arrowHotspot))
            {
                spriteSource = "游戏光标";
                arrowTex = MakeUsableTexture(src);
                if (arrowTex == null)
                {
                    spriteSource = "内置箭头（游戏贴图不可用）";
                    arrowTex = BuildFallbackArrow();
                    arrowHotspot = new Vector2(0f, 0f);
                }
            }
            else
            {
                spriteSource = ReplayConfig.GetFakeMouseGameCursor() ? "内置箭头" : "内置箭头（配置指定）";
                arrowTex = BuildFallbackArrow();
                // 自绘箭头的尖端就在左上角第一个像素 ↦ 热点 (0,0)（**左上角为原点**）。
                arrowHotspot = new Vector2(0f, 0f);
            }

            if (arrowTex == null)
            {
                diag = "连内置箭头都没造出来";
                return null;
            }

            try
            {
                // 用 FullRect：Tight 网格需要读像素，而光标贴图未必可读。
                cursorSprite = Sprite.Create(arrowTex,
                    new Rect(0f, 0f, arrowTex.width, arrowTex.height),
                    HotspotToPivot(arrowHotspot, arrowTex.width, arrowTex.height),
                    100f, 0u, SpriteMeshType.FullRect);
            }
            catch (Exception e)
            {
                diag = "Sprite.Create 失败：" + e.GetType().Name + ": " + e.Message;
                return null;
            }
            uvInfo = DescribeSprite(cursorSprite, arrowTex);
            return cursorSprite;
        }

        /// <summary>
        /// 回读精灵与贴图的**真实**几何/内容。
        ///
        /// <para>为什么必须回读：渲染成一块纯色时，可能是
        /// ①贴图本身没内容、②精灵的 UV 退化成一个点、③UV 正确但采样区域不对。
        /// 这三者从代码上都看不出来 —— 只有把 <c>uv</c> / <c>textureRect</c> / 实际像素
        /// 打出来才能区分。</para>
        /// </summary>
        private static string DescribeSprite(Sprite s, Texture2D tex)
        {
            var sb = new StringBuilder(200);
            try
            {
                if (s == null)
                    return "精灵=null";
                var tr = s.textureRect;
                sb.Append("textureRect=").Append(tr.width.ToString("0")).Append('x').Append(tr.height.ToString("0"));
                sb.Append(" 精灵贴图=").Append(s.texture != null ? (s.texture.width + "x" + s.texture.height + s.texture.format) : "**null**");
                sb.Append(" 与传入同一张=").Append(s.texture != null && tex != null && s.texture.Pointer == tex.Pointer ? 1 : 0);
                // 直接读回像素：证明贴图**内容**真的写进去了。
                if (tex != null && tex.isReadable)
                {
                    sb.Append(" 像素读回=").Append(Fmt(tex.GetPixel(0, 0)))
                      .Append('/').Append(Fmt(tex.GetPixel(1, tex.height - 2)));
                }
            }
            catch (Exception e)
            {
                sb.Append(" 回读出错 ").Append(e.GetType().Name);
            }
            return sb.ToString();
        }

        private static string Fmt(Color c)
        {
            return "(" + (c.r * 255f).ToString("0") + "," + (c.g * 255f).ToString("0") + ","
                + (c.b * 255f).ToString("0") + "," + (c.a * 255f).ToString("0") + ")";
        }

        /// <summary>
        /// 把从游戏里拿到的贴图**拷一份成普通 RGBA32**。
        ///
        /// <para>为什么要拷：硬件光标贴图是按 <c>Texture Type = Cursor</c> 导入的，
        /// 格式常常是 <c>Alpha8</c>，而且可能被 Unity 当成特殊资源 ——
        /// 直接拿去给 UI 采样时**未必能得到 RGB</b>。拷成 RGBA32 之后就完全是一张普通贴图了。</para>
        ///
        /// <para>贴图不可读（<c>isReadable = false</c>）时原样返回，让后面的渲染自己决定。</para>
        /// </summary>
        private static Texture2D MakeUsableTexture(Texture2D src)
        {
            if (src == null)
                return null;
            try
            {
                texInfo = src.name + " " + src.width + "x" + src.height + " " + src.format
                    + " 可读=" + (src.isReadable ? 1 : 0);
                if (!src.isReadable)
                    return src;

                var px = src.GetPixels32();
                if (px == null || px.Length < 4)
                    return src;
                texInfo = texInfo + " 像素=" + DescribePixels(px, src.width, src.height);
                var copy = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                copy.SetPixels32(px);
                copy.Apply();
                texInfo = texInfo + " → 拷贝为RGBA32";
                return copy;
            }
            catch (Exception e)
            {
                texInfo = texInfo + " 拷贝失败(" + e.GetType().Name + ")";
                return src;
            }
        }

        /// <summary>
        /// 把贴图像素统计出来。
        ///
        /// <para><b>为什么要打出来：</b>「贴图拿到了、精灵也设上了、Image 也对」
        /// 却渲染成一团纯色时，剩下的唯一变量就是**像素本身**。
        /// 这里几个数字就能区分三种完全不同的毛病：
        /// ① <c>不同RGB数=1</c> → 图形只存在 alpha 里，RGB 是纯色；
        /// ② <c>alpha范围=255..255</c> → 根本没有透明区域（形状不在这里）；
        /// ③ <c>不透明=0</c> → 整张全透明，什么都看不见。</para>
        /// </summary>
        private static string DescribePixels(Il2CppStructArray<Color32> px, int w, int h)
        {
            var sb = new StringBuilder(160);
            int opaque = 0, clear = 0;
            int minA = 255, maxA = 0;
            var rgbSet = new HashSet<int>();
            int opaqueRgb = -1;
            for (int i = 0; i < px.Length; i++)
            {
                int a = px[i].a;
                if (a > 128) opaque++;
                else if (a < 8) clear++;
                if (a < minA) minA = a;
                if (a > maxA) maxA = a;
                rgbSet.Add((px[i].r << 16) | (px[i].g << 8) | px[i].b);
                if (a > 200 && opaqueRgb < 0)
                    opaqueRgb = (px[i].r << 16) | (px[i].g << 8) | px[i].b;
            }
            sb.Append("不透明=").Append(opaque).Append('/').Append(px.Length)
                .Append(" alpha=").Append(minA).Append("..").Append(maxA)
                .Append(" 不同RGB=").Append(rgbSet.Count)
                .Append(" 不透明处RGB=#").Append(opaqueRgb >= 0 ? opaqueRgb.ToString("X6") : "?");
            return sb.ToString();
        }

        /// <summary>
        /// 热点（**贴图左上角为原点**的像素）→ <c>RectTransform.pivot</c> / <c>Sprite</c> 枢轴
        ///（**左下角为原点**的归一化坐标）。
        ///
        /// <para>只差一个上下翻转，但这一个翻转就是「光标画在指针上方一整个贴图高度」
        /// 那个 bug（看起来像锚点跑到贴图中间）的原因。</para>
        /// </summary>
        private static Vector2 HotspotToPivot(Vector2 hotspot, float w, float h)
        {
            if (w < 1f) w = 1f;
            if (h < 1f) h = 1f;
            float px = hotspot.x / w;
            float py = 1f - hotspot.y / h;
            if (px < 0f) px = 0f;
            else if (px > 1f) px = 1f;
            if (py < 0f) py = 0f;
            else if (py > 1f) py = 1f;
            return new Vector2(px, py);
        }

        /// <summary>
        /// 自测用：验证画出来的光标**锚点在贴图左上角**（不是正中、也不是左下角）。
        ///
        /// <para>判据有两条，缺一不可：</para>
        /// <list type="number">
        /// <item>枢轴等于「热点（左上角为原点）换算成枢轴（左下角为原点）」的那个点
        /// —— 说明我们没算错；</item>
        /// <item>当热点是 (0,0)（游戏箭头就是这样，<c>Cursor.SetCursor</c> 的左上角原点）
        /// 时，枢轴必须落在 <c>(0,1)</c> 附近 —— 说明「左上角」真的是左上角，
        /// 而不是因为公式里漏了 <c>1 - y/h</c> 而变成左下角。</item>
        /// </list>
        /// </summary>
        public static bool SelfCheckAnchor(out string detail)
        {
            var sb = new StringBuilder(160);
            if (rect == null || rect.Pointer == IntPtr.Zero)
            {
                detail = "假鼠标节点不存在（" + diag + "）";
                return false;
            }
            var p = rect.pivot;
            var want = HotspotToPivot(arrowHotspot, texW, texH);
            bool formulaOk = Math.Abs(p.x - want.x) < 0.001f && Math.Abs(p.y - want.y) < 0.001f;
            bool cornerOk = !(Math.Abs(arrowHotspot.x) < 0.001f && Math.Abs(arrowHotspot.y) < 0.001f)
                || (p.y > 0.99f && p.x < 0.01f);
            sb.Append("枢轴=(").Append(p.x.ToString("0.###")).Append(',').Append(p.y.ToString("0.###"))
              .Append(") 期望=(").Append(want.x.ToString("0.###")).Append(',').Append(want.y.ToString("0.###"))
              .Append(") 与公式一致=").Append(formulaOk ? "OK" : "失败")
              .Append("；热点=").Append(arrowHotspot.x.ToString("0.#")).Append(',').Append(arrowHotspot.y.ToString("0.#"))
              .Append("（左上角为原点）⇒ 左上角锚定=").Append(cornerOk ? "OK" : "失败")
              .Append("；贴图=").Append(texW.ToString("0")).Append('x').Append(texH.ToString("0"))
              .Append(' ').Append(spriteSource);
            detail = sb.ToString();
            return formulaOk && cornerOk;
        }

        /// <summary>
        /// 从 <c>CursorManager</c> 的 <c>cursorDatas</c> 里取「箭头」那张贴图。
        ///
        /// <para>这是**硬件光标**用的贴图（游戏用 <c>Cursor.SetCursor</c> 把系统光标换成它），
        /// 所以不能用 UI 素材替代 —— 只有它能做到「和游戏原生光标一模一样的样式」。</para>
        /// </summary>
        private static bool TryGetGameCursor(out Texture2D tex, out Vector2 hotspot)
        {
            tex = null;
            hotspot = Vector2.zero;
            try
            {
                var main = MainManager.Instance;
                if (main == null)
                    return false;
                var mgr = main.CursorManager;
                if (mgr == null)
                    return false;
                var list = mgr.cursorDatas;
                if (list == null)
                    return false;
                int n = list.Count;
                for (int i = 0; i < n; i++)
                {
                    var d = list[i];
                    // CursorType.Arrow 是枚举第 0 项。
                    if ((int)d.type != 0)
                        continue;
                    if (d.texture == null)
                        continue;
                    tex = d.texture;
                    hotspot = d.hotspot;
                    break;
                }
            }
            catch (Exception e)
            {
                diag = "读游戏光标失败：" + e.GetType().Name + "（退回内置箭头）";
                return false;
            }
            return tex != null;
        }

        /// <summary>自绘的备用箭头：左上角为尖端的一枚实心楔形。</summary>
        private static Texture2D BuildFallbackArrow()
        {
            const int W = 18;
            const int H = 32;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var white = new Color32(255, 255, 255, 255);
            var black = new Color32(0, 0, 0, 255);
            var clear = new Color32(0, 0, 0, 0);
            // Texture2D 的第 0 行在**下方**，所以 y = H-1 是顶端。
            for (int y = 0; y < H; y++)
            {
                int rowsFromTop = H - 1 - y;
                int len = rowsFromTop + 1;
                if (len > W)
                    len = W;
                for (int x = 0; x < W; x++)
                {
                    t.SetPixel(x, y, x < len ? (x == 0 || x == len - 1 || rowsFromTop == 0 ? black : white) : clear);
                }
            }
            t.Apply();
            return t;
        }

        // ------------------------------------------------------------------
        // 报告
        // ------------------------------------------------------------------

        public static void AppendReport(StringBuilder sb)
        {
            if (sb == null)
                return;
            sb.Append("  假鼠标  : ").Append(diag)
                .Append("  启用=").Append(running ? 1 : 0)
                .Append(" 路点=").Append(ptCount)
                .Append("（滤掉哨兵坐标 ").Append(droppedPoints).Append("）")
                .Append(" tick 数=").Append(movedTicks)
                .Append(" 其中静止=").Append(stillTicks)
                .Append(" 缓动=").Append(ReplayConfig.GetFakeMouseEase())
                .Append("/").Append(ReplayConfig.GetFakeMouseTicks() == 0 ? "全程" : ReplayConfig.GetFakeMouseTicks() + "tick")
                .Append(" 精灵重按=").Append(spriteResets).Append(" 颜色重按=").Append(fadeResets)
                // 手持物跟随：拿过东西的 tick 数 vs 真正被改写的次数。
                // 前者 > 0 而后者 = 0 ⇒ 那条通道没生效（手持蓝图时指针会瞬移）。
                .Append(" 手持tick=").Append(holdingTicks)
                .Append(" 手持跟随=").Append(heldSyncHits)
                .Append('\n');
            sb.Append("  假鼠标节点: ").Append(nodeInfo).Append('\n');
            sb.Append("  假鼠标精灵: ").Append(uvInfo).Append('\n');
            sb.Append("  假鼠标实时: ").Append(liveInfo).Append('\n');
            sb.Append("  假鼠标定位: ").Append(placeInfo).Append('\n');
            sb.Append("  画布换算  : 1 UI 单位 = ").Append((usedSf > 0.0001f ? 1f / usedSf : 1f).ToString("0.###")).Append(" 像素")
                .Append("，贴图倍率=").Append(usedScale.ToString("0.##")).Append('\n');
            // 屏幕坐标换算是「假鼠标与操作对不上」的关键一环，必须写进小结：
            // 没有这一段的话，出问题时根本看不出「文件里记的是别的窗口尺寸」。
            sb.Append("  窗口换算  : ").Append(ReplayViewport.Describe()).Append('\n');
        }
    }
}
