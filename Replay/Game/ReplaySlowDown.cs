using System;
using System.Collections.Generic;
using System.Globalization;
using DSHCore.Ui;
using MVZ2.Managers;
using MVZ2Logic.Options;
using UnityEngine;
using UnityEngine.UI;

// DSHCore 的 UiLog 是 internal，跨程序集用不了 —— 别名到本 mod 自己的那份。
using UiLog = Replay.ReplayLog;

namespace Replay
{
    /// <summary>
    /// 关卡内的**减速按钮**：贴在加速按钮正下方，图标是加速按钮的镜像，
    /// 倍率恒为加速倍率的**倒数**（<c>1 / GetFastForwardMultiplier()</c>），与加速互斥。
    ///
    /// <para><b>① 倍率怎么生效 —— 不要动 <c>slowMotions</c> 列表。</b>
    /// 原版有一套 <c>LevelController.AddSlowMotion(timeout, multiplier)</c>，
    /// 但它**带超时**、而且没有移除接口（<c>slowMotions</c> 是私有列表，只在 timeout 到点时
    /// <c>RemoveAll</c>），用它做「开关式减速」会让列表无限增长。
    /// 所以改成给 <c>LevelController.GetGameSpeed()</c> 挂一个 postfix 直接乘上去：
    /// <code>
    /// __result *= 1f / Main.OptionsManager.GetFastForwardMultiplier();
    /// </code>
    /// 乘在最后意味着：限定在 <c>(0,1]</c>、与游戏自己的慢动作（Kraken 之类）天然叠加、
    /// 而且倍率**跟随设置实时变化**（玩家在选项里拖动快进倍率，这里立刻跟着变）。</para>
    ///
    /// <para><b>② 互斥怎么保证 —— 不碰原按钮的回调，改听 <c>LevelUIPreset.SetSpeedUp</c>。</b>
    /// 加速有三条入口（按钮、热键、别的 mod），逐个拦必然漏。而它们最终都会走到
    /// <c>LevelController.SwitchSpeedUp()</c> → <c>GetUIPreset().SetSpeedUp(speedUp)</c>，
    /// 所以给那个**公开方法**挂 postfix：一旦 <c>speedUp == true</c>，就把我们的减速关掉。
    /// 反过来，开启减速时我们直接 <c>speedUpButton.onClick.Invoke()</c> ——
    /// 走的正是游戏自己那条路，不需要反射私有字段。</para>
    ///
    /// <para><b>③ 按钮怎么造 —— 克隆原按钮，然后下移一格。</b>
    /// 加速按钮上有两个图标节点（开/关）与一个热键文字节点。克隆下来后：
    /// 两个图标节点做 <c>localScale.x = -1</c>（镜像），热键文字清空 ——
    /// <b>本按钮不注册热键</b>（需求）。位置用锚点算：读原节点的
    /// <c>anchoredPosition.y</c> 减去「自身高度 + 行距」。</para>
    ///
    /// <para><b>④ 为什么不进回放状态：</b>与暂停/加速一致 ——
    /// <c>isPaused</c> / <c>speedUp</c> 都不属于 <c>SerializableLevel</c>，
    /// 回放里也不该被录进去（这是设计要求，不是缺陷）。</para>
    /// </summary>
    internal static class ReplaySlowDown
    {
        private const string ButtonName = "DSH_ReplaySlowDown";

        /// <summary>减速是否开启。</summary>
        public static bool Enabled { get; private set; }

        /// <summary>最后一次操作的结果描述（自测/日志）。</summary>
        public static string LastInfo { get; private set; } = "-";

        /// <summary>按钮建没建出来（自测断言用）。</summary>
        public static bool ButtonReady { get; private set; }

        /// <summary>
        /// 有没有成功把克隆体里的开/关图标找出来并镜像。
        ///
        /// <para>找不到不影响按钮能用（只是图标没镜像、看起来和加速按钮一样），
        /// 但**应当**能找到 —— 所以这条会进自测报告，方便发现游戏改了 prefab 结构。</para>
        /// </summary>
        public static bool IconMirrored { get { return onIcon != null || offIcon != null; } }

        /// <summary>克隆来的热键文字有没有被清空（清零表示已清）。</summary>
        public static bool HotkeyCleared { get; private set; }

        /// <summary>
        /// 按钮是不是真的贴在加速按钮**正下方**：x 对齐、y 更低、两块矩形不重叠。
        ///
        /// <para>这是「位置对不对」唯一能被自动检查的判据 —— 截图只能证明画出来了。</para>
        /// </summary>
        public static bool PlacedCorrectly
        {
            get
            {
                try
                {
                    var preset = Preset();
                    var src = preset != null ? preset.speedUpButton : null;
                    if (src == null || button == null || !UiFinder.IsAlive(button))
                        return false;
                    var a = src.transform.TryCast<RectTransform>();
                    var b = button.transform.TryCast<RectTransform>();
                    if (a == null || b == null || a.parent != b.parent)
                        return false;

                    var pa = a.anchoredPosition;
                    var pb = b.anchoredPosition;
                    var ha = a.rect.height > 1f ? a.rect.height : a.sizeDelta.y;
                    if (Mathf.Abs(pa.x - pb.x) > 1f)
                        return false;             // 没对齐
                    if (pb.y >= pa.y)
                        return false;             // 没在下面
                    return (pa.y - pb.y) >= ha - 1f;  // 不重叠（至少错开一个自身高度）
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>位置的文字说明（进自测报告）。</summary>
        public static string DescribeLayout()
        {
            try
            {
                var preset = Preset();
                var src = preset != null ? preset.speedUpButton : null;
                if (src == null || button == null || !UiFinder.IsAlive(button))
                    return "（拿不到加速按钮或减速按钮）";
                var a = src.transform.TryCast<RectTransform>();
                var b = button.transform.TryCast<RectTransform>();
                if (a == null || b == null)
                    return "（不是 RectTransform）";
                var ha = a.rect.height > 1f ? a.rect.height : a.sizeDelta.y;
                return "加速 anchored=(" + a.anchoredPosition.x.ToString("0.#") + ", "
                    + a.anchoredPosition.y.ToString("0.#") + ") 高=" + ha.ToString("0.#")
                    + "；减速 anchored=(" + b.anchoredPosition.x.ToString("0.#") + ", "
                    + b.anchoredPosition.y.ToString("0.#") + ") 高="
                    + (b.rect.height > 1f ? b.rect.height : b.sizeDelta.y).ToString("0.#")
                    + "；同父=" + (a.parent == b.parent)
                    + "；判定=" + (PlacedCorrectly ? "OK" : "不对");
            }
            catch (Exception e)
            {
                return "（读位置失败：" + e.GetType().Name + "）";
            }
        }

        private static GameObject button;
        private static GameObject onIcon;      // 克隆来的「加速开」图标（已镜像）
        private static GameObject offIcon;     // 克隆来的「加速关」图标（已镜像）
        private static int builtForController; // 这个按钮是给哪个控制器造的
        private static bool warnedOnce;

        /// <summary>
        /// 当前设置里的**加速倍率**（没在做减速，就是把它取出来给自测/日志对照）。
        /// 拿不到时返回 1。
        /// </summary>
        public static float FastForwardMultiplier
        {
            get
            {
                try
                {
                    var main = MainManager.Instance;
                    var opts = main != null ? main.OptionsManager : null;
                    if (opts == null)
                        return 1f;
                    var ff = opts.GetOptionFloat(LogicOptionItemID.fastForwardMultiplier);
                    return ff > 0.0001f ? ff : 1f;
                }
                catch (Exception)
                {
                    return 1f;
                }
            }
        }

        /// <summary>当前减速倍率（加速倍率的倒数）。拿不到设置时返回 1（= 不减速）。</summary>
        public static float Multiplier
        {
            get
            {
                var ff = FastForwardMultiplier;
                return ff > 0.0001f ? 1f / ff : 1f;
            }
        }

        /// <summary>开关（点按钮走这里）。</summary>
        public static void Toggle()
        {
            Set(!Enabled);
        }

        /// <summary>设置开关。开启时顺手把游戏的加速关掉（互斥）。</summary>
        public static void Set(bool on)
        {
            if (on == Enabled)
            {
                SyncVisual();
                return;
            }

            Enabled = on;
            if (on)
            {
                // 互斥：开启减速时先把加速关掉。invoke 原按钮的 onClick
                // = 走游戏自己的 SwitchSpeedUp()，无需反射私有字段。
                try
                {
                    var preset = Preset();
                    var btn = preset != null ? preset.speedUpButton : null;
                    if (btn != null && btn.onClick != null && IsSpeedUpOn(preset))
                        btn.onClick.Invoke();
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning("[Replay] 关闭加速失败：" + e.GetType().Name);
                }
            }

            LastInfo = (on ? "减速开" : "减速关")
                + "（倍率 " + Multiplier.ToString("0.###", CultureInfo.InvariantCulture) + "）";
            Plugin.Log?.LogInfo("[Replay] 关卡减速 " + LastInfo);
            SyncVisual();
        }

        /// <summary>离开关卡 / 播放结束时复位。</summary>
        public static void Reset()
        {
            Enabled = false;
            ButtonReady = false;
            HotkeyCleared = false;
            LastInfo = "-";
            if (button != null)
            {
                try { UnityEngine.Object.Destroy(button); } catch (Exception) { }
            }
            button = null;
            onIcon = null;
            offIcon = null;
            builtForController = 0;
        }

        // ------------------------------------------------------------------
        // 每帧
        // ------------------------------------------------------------------

        /// <summary>
        /// 每帧调一次：把关卡控制器拿进来，需要时把按钮造/重建出来。
        ///
        /// <para><b>只有回放播放期间才有这个按钮。</b>它是「看回放时的辅助工具」，
        /// 不是通用 HUD 功能 —— 正常游玩不应该凭空多出一个按钮来
        /// （而且正常游玩时把速度减到 1/3 也没有任何好处）。</para>
        /// </summary>
        public static void Tick()
        {
            try
            {
                // 配置关掉 = 完全不碰关卡 UI（排障用）。
                if (!ReplayConfig.SlowDownButtonOn)
                {
                    if (button != null || Enabled)
                        Reset();
                    return;
                }

                // 不在回放中：拆掉（包括玩家正常游玩时——这是它唯一该存在的场景）。
                if (!ReplayPlayer.IsActive)
                {
                    if (button != null || Enabled)
                        Reset();
                    return;
                }

                var ctrl = Controller();
                if (ctrl == null)
                {
                    if (button != null || Enabled)
                        Reset();
                    return;
                }

                var preset = Preset();
                if (preset == null)
                    return;

                var ptr = ctrl.Pointer.ToInt32();
                if (button == null || !UiFinder.IsAlive(button) || ptr != builtForController
                    || !IsUnderCurrentPreset(preset))
                {
                    Build(preset, ptr);
                }
                SyncVisual();
            }
            catch (Exception e)
            {
                if (!warnedOnce)
                {
                    warnedOnce = true;
                    Plugin.Log?.LogWarning("[Replay] 减速按钮心跳失败：" + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        // ------------------------------------------------------------------
        // 建按钮
        // ------------------------------------------------------------------

        private static void Build(MVZ2.Level.UI.LevelUIPreset preset, int controllerPtr)
        {
            if (button != null)
            {
                try { UnityEngine.Object.Destroy(button); } catch (Exception) { }
            }
            button = null;
            onIcon = null;
            offIcon = null;
            ButtonReady = false;
            HotkeyCleared = false;

            var src = preset.speedUpButton;
            if (src == null || src.gameObject == null)
            {
                UiLog.WarnOnce("rp-slow-src", "找不到加速按钮素材（LevelUIPreset.speedUpButton）—— 不建减速按钮。");
                return;
            }

            var srcRt = src.transform.TryCast<RectTransform>();
            var parent = src.transform.parent;
            if (srcRt == null)
            {
                UiLog.WarnOnce("rp-slow-rt", "加速按钮不是 RectTransform —— 不建减速按钮。");
                return;
            }
            if (parent == null)
            {
                UiLog.WarnOnce("rp-slow-parent", "加速按钮没有父节点 —— 不建减速按钮。");
                return;
            }

            var go = UiClone.Instantiate(src.gameObject, parent, ButtonName);
            if (!UiFinder.IsAlive(go))
            {
                UiLog.WarnOnce("rp-slow-clone", "克隆加速按钮失败 —— 不建减速按钮。");
                return;
            }
            button = go;

            // 位置：原按钮正下方
            var rt = go.transform.TryCast<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = srcRt.anchorMin;
                rt.anchorMax = srcRt.anchorMax;
                rt.pivot = srcRt.pivot;
                var height = srcRt.rect.height > 1f ? srcRt.rect.height : srcRt.sizeDelta.y;
                var gap = Mathf.Max(4f, height * 0.15f);
                rt.anchoredPosition = new Vector2(srcRt.anchoredPosition.x,
                    srcRt.anchoredPosition.y - (height + gap));
                rt.localRotation = srcRt.localRotation;
                rt.localScale = srcRt.localScale;
            }

            // 父节点若在用布局组，锚点摆位会被它盖掉 —— 声明「不参与排版」。
            try
            {
                var layout = parent.GetComponent<LayoutGroup>();
                if (layout != null)
                {
                    var le = go.GetComponent<LayoutElement>();
                    if (le == null)
                        le = go.AddComponent<LayoutElement>();
                    if (le != null)
                        le.ignoreLayout = true;
                }
            }
            catch (Exception) { }

            // 图标镜像（开/关各自一份），热键文字清空。
            MirrorIcons(preset, go);
            // 点击：切换减速。
            UiClone.ClearButtonListeners(go);
            UiClone.OnClick(go, Toggle, false);
            // 需求：**不加悬停提示**（它就在加速按钮下面、图标又是镜像的，看一眼就知道）。

            builtForController = controllerPtr;
            ButtonReady = true;
            Plugin.Log?.LogInfo("[Replay] 减速按钮已建立于加速按钮正下方（控制器 = 0x"
                + controllerPtr.ToString("X") + "）");
            SyncVisual();
        }

        /// <summary>
        /// 把克隆体里与「加速开/关图标」「热键文字」对应的节点找出来：
        /// 两个图标镜像（<c>localScale.x = -1</c>），热键文字清空。
        /// </summary>
        private static void MirrorIcons(MVZ2.Level.UI.LevelUIPreset preset, GameObject clone)
        {
            try
            {
                var onName = preset.speedUpEnabledObject != null ? preset.speedUpEnabledObject.name : null;
                var offName = preset.speedUpDisabledObject != null ? preset.speedUpDisabledObject.name : null;
                var hotkeyName = preset.speedUpHotkeyText != null
                    ? preset.speedUpHotkeyText.gameObject.name : null;

                if (string.IsNullOrEmpty(onName) && string.IsNullOrEmpty(offName))
                {
                    UiLog.WarnOnce("rp-slow-icons",
                        "读不到加速按钮的开/关图标节点名 —— 减速按钮不镜像（仍然可用）。");
                    return;
                }

                var hits = new List<GameObject>(4);
                CollectByName(clone.transform, onName, offName, hotkeyName, hits);

                for (int i = 0; i < hits.Count; i++)
                {
                    var node = hits[i];
                    var n = node.name;

                    if (!string.IsNullOrEmpty(hotkeyName) && n == hotkeyName)
                    {
                        // 不注册热键：把克隆来的热键文字清掉。
                        var t = UiClone.FindText(node);
                        if (t != null)
                        {
                            try
                            {
                                t.text = string.Empty;
                                HotkeyCleared = true;
                            }
                            catch (Exception) { }
                        }
                        continue;
                    }

                    bool isOn = !string.IsNullOrEmpty(onName) && n == onName;
                    bool isOff = !string.IsNullOrEmpty(offName) && n == offName;
                    if (!isOn && !isOff)
                        continue;

                    var t2 = node.transform;
                    if (t2 == null)
                        continue;
                    var sc = t2.localScale;
                    t2.localScale = new Vector3(-Mathf.Abs(sc.x), sc.y, sc.z);
                    if (isOn)
                        onIcon = node;
                    else
                        offIcon = node;
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("rp-slow-mirror", "镜像减速图标失败：" + e.GetType().Name);
            }
        }

        private static void CollectByName(Transform root, string a, string b, string c, List<GameObject> into)
        {
            if (root == null)
                return;
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child == null)
                    continue;
                var n = child.name;
                if ((!string.IsNullOrEmpty(a) && n == a)
                    || (!string.IsNullOrEmpty(b) && n == b)
                    || (!string.IsNullOrEmpty(c) && n == c))
                {
                    into.Add(child.gameObject);
                }
                CollectByName(child, a, b, c, into);
            }
        }

        /// <summary>
        /// 图标显示状态跟着 <see cref="Enabled"/> 走（沿用原版约定：
        /// 开 = 高亮图标、关 = 灰图标）。
        /// </summary>
        private static void SyncVisual()
        {
            try
            {
                if (onIcon != null && UiFinder.IsAlive(onIcon) && onIcon.activeSelf != Enabled)
                    onIcon.SetActive(Enabled);
                if (offIcon != null && UiFinder.IsAlive(offIcon) && offIcon.activeSelf == Enabled)
                    offIcon.SetActive(!Enabled);
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // 补丁入口
        // ------------------------------------------------------------------

        /// <summary><c>LevelController.GetGameSpeed()</c> 的 postfix：把减速乘上去。</summary>
        public static void GetGameSpeedPostfix(ref float __result)
        {
            if (!Enabled || __result <= 0f)
                return; // 0 = 暂停 / 控制台打开 / 已结束，乘什么都没意义
            __result *= Multiplier;
        }

        /// <summary>
        /// <c>LevelUIPreset.SetSpeedUp(bool)</c> 的 postfix：加速一开就把减速关掉（互斥）。
        ///
        /// <para>只对 <c>true</c> 生效：游戏在初始化 UI 时会用当前值调它一次（通常 false），
        /// 那时不该把玩家刚开的减速偷偷关掉。</para>
        /// </summary>
        public static void SetSpeedUpPostfix(bool speedUp)
        {
            if (!speedUp || !Enabled)
                return;
            Set(false);
        }

        // ------------------------------------------------------------------
        // 取场景对象
        // ------------------------------------------------------------------

        private static MVZ2.Level.LevelController Controller()
        {
            try
            {
                var main = MainManager.Instance;
                if (main == null || main.LevelManager == null)
                    return null;
                return main.LevelManager.GetLevelController();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static MVZ2.Level.UI.LevelUIPreset Preset()
        {
            try
            {
                var ctrl = Controller();
                if (ctrl == null)
                    return null;
                return ctrl.GetUIPreset();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>游戏当前是不是在加速状态（原版就是拿这个对象的显隐表示开/关）。</summary>
        private static bool IsSpeedUpOn(MVZ2.Level.UI.LevelUIPreset preset)
        {
            try
            {
                var go = preset.speedUpEnabledObject;
                return go != null && go.activeSelf;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsUnderCurrentPreset(MVZ2.Level.UI.LevelUIPreset preset)
        {
            try
            {
                var src = preset.speedUpButton;
                if (src == null || src.transform == null || src.transform.parent == null)
                    return false;
                return button != null && button.transform != null
                    && button.transform.parent == src.transform.parent;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
