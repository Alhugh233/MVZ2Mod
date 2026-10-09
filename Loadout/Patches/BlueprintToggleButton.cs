using System;
using DSHCore.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 选卡界面上的「全部蓝图 / 原版蓝图」切换按钮。
    ///
    /// <para>需求：在「X（取消全选）」按钮**右侧**加一个**同尺寸**的按钮，用来切换是否显示全部蓝图；
    /// 图标在显示全部时是 <c>-</c>、不显示时是 <c>+</c>；而且**只在原版 debug 存档里出现**
    /// （非 debug 存档本来也不显示全部蓝图，见 <see cref="AllBlueprintsPatch.IsAllowed"/>）。</para>
    ///
    /// <para><b>做法：</b>克隆选卡面板自己的「取消」按钮，插到它右边一格 ——
    /// 用同一个 prefab 的按钮，尺寸/观感/音效/悬停天然一致。</para>
    ///
    /// <para>两个必须做的修补：</para>
    /// <list type="number">
    /// <item><c>Instantiate</c> 会把**持久化监听器**一起复制过来（那个监听器会让选卡被取消），
    /// 用 <see cref="UiClone.ClearButtonListeners"/> 清掉；</item>
    /// <item>它所在的那一层**没有布局组**（原版是按坐标摆的），所以位置只能算：
    /// 取消按钮的右边一格 —— 这是从原版按钮的位置与宽度**推**出来的，不是写死的魔术数字。</item>
    /// </list>
    /// </summary>
    internal static class BlueprintToggleButton
    {
        private static GameObject node;
        private static Button button;
        private static TMPro.TextMeshProUGUI label;
        private static int tick;
        private static MVZ2.Level.LevelBlueprintChooseController part;

        /// <summary>由面板心跳调用（内部节流）。</summary>
        public static void Tick()
        {
            if (++tick % 30 != 0)
                return;
            Update();
        }

        /// <summary>
        /// 每帧（**不节流**）的点击处理。
        ///
        /// <para><b>为什么不走 <c>Button.onClick</c></b>：实测这个克隆按钮上的
        /// <c>onClick</c> 收不到点击（射线被别的东西吃了 —— 见 <see cref="DebugRaycast"/> 的探针），
        /// 而本仓库对"自己造的按钮"一贯的做法就是**矩形命中 + 自己处理**
        /// （<c>UiHitTest</c> 的注释里写了原因）。所以：按钮只留观感与悬停提示，
        /// 点击一律走这里。鼠标按下的那一帧 <c>GetMouseButtonDown</c> 只在一帧为真，
        /// 所以这个函数**不能节流**。</para>
        /// </summary>
        public static void Frame()
        {
            if (!UiFinder.IsAlive(node))
                return;
            try
            {
                if (!Input.GetMouseButtonDown(0))
                    return;
                var pos = (Vector2)Input.mousePosition;
                // 面板盖在上面的时候点的是面板 —— 别顺手把按钮也切了。
                var panel = LoadoutPanel.DialogRoot;
                if (LoadoutPanel.IsOpen && panel != null && UiHitTest.Contains(panel.gameObject, pos))
                    return;
                TryClick(pos);
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 点击命中的**本体**：点落在按钮矩形里就切换蓝图范围并返回 <c>true</c>。
        ///
        /// <para>抽出来是为了自测能**真的走一遍这条路**（只不过点是我们喂进去的）——
        /// 只剩「读鼠标」那一步没法自动验，其余（矩形命中 + 切换 + 改图标）全都验到。</para>
        /// </summary>
        public static bool TryClick(Vector2 screenPoint)
        {
            if (!UiFinder.IsAlive(node))
                return false;
            if (!UiHitTest.Contains(node, screenPoint))
                return false;
            ClickOnce();
            return true;
        }

        /// <summary>
        /// 切一次蓝图范围，**同一帧只算一次**。
        ///
        /// <para>为什么需要去重：这个按钮有意装了两条点击通路 ——
        /// ① 每帧的矩形命中（<see cref="Frame"/>，不依赖 EventSystem，谁挡着都能点）；
        /// ② 原版 <c>Button.onClick</c>（EventSystem 那条，位置对、没人挡时也有效）。
        /// 两条都命中就会**切两次 = 等于没切**，所以用帧号去重。</para>
        /// </summary>
        private static void ClickOnce()
        {
            var f = Time.frameCount;
            if (f == _lastClickFrame)
                return;
            _lastClickFrame = f;
            OnClick();
        }

        private static int _lastClickFrame = -1;

        /// <summary>真正干活的那一步（节流在外面）。</summary>
        public static void Update()
        {
            try
            {
                // 场景遍历不便宜 —— 缓存控制器引用，只在它失效（换关卡）时才重新找。
                if (!UiFinder.IsAlive(part))
                    part = ResolvePart();
                if (part == null)
                {
                    Discard();
                    return;
                }
                var ui = part.chooseUI;
                if (ui == null)
                {
                    Discard();
                    return;
                }
                if (!ui.blueprintChooseDisplaying)
                {
                    Discard();
                    return;
                }
                // ★ 非 debug 存档：这个按钮根本不该出现（需求）。
                if (!AllBlueprintsPatch.IsAllowed())
                {
                    Discard();
                    return;
                }
                var panel = ui.blueprintChoosePanel;
                if (panel == null)
                    return;

                if (!UiFinder.IsAlive(node))
                    Create(panel);
                UpdateLabel();
            }
            catch (Exception e)
            {
                Plugin.LogOnce("bptoggle", "[Blueprints] 切换按钮心跳异常：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void Create(Component panel)
        {
            try
            {
                var cancel = panel.GetComponent<MVZ2.Level.Blueprints.BlueprintChoosePanel>();
                Button source = cancel == null ? null : cancel.cancelButton;
                if (source == null)
                {
                    Plugin.LogOnce("bptoggle-src", "[Blueprints] 找不到选卡面板的取消按钮，无法创建切换按钮");
                    return;
                }

                node = UiClone.Instantiate(source.gameObject, source.transform.parent, "DSH_AllBlueprints");
                if (!UiFinder.IsAlive(node))
                {
                    Plugin.LogOnce("bptoggle-clone", "[Blueprints] 切换按钮克隆失败");
                    return;
                }

                // ⚠️ 克隆体会带着原按钮的持久化监听器（= 取消选卡），必须清掉。
                UiClone.ClearButtonListeners(node);
                button = node.GetComponent<Button>();
                if (button == null)
                {
                    Plugin.LogOnce("bptoggle-btn", "[Blueprints] 切换按钮克隆体上没有 Button 组件");
                    return;
                }
                // 两条点击通路都装上（同一帧只生效一次，见 ClickOnce）：
                //   ① onClick = EventSystem 那条（位置对、没人挡时有效）；
                //   ② Frame() 的矩形命中 = 不依赖 EventSystem（有东西挡着也能点）。
                // 只装一条都会在某种情形下点不动 —— 这个按钮已经因为这个被投诉过一次。
                button.onClick.AddListener((UnityEngine.Events.UnityAction)ClickOnce);

                // 位置与尺寸：与取消按钮**完全一致**（同锚点/同锚点位置偏移）。
                try
                {
                    var src = source.transform.TryCast<RectTransform>();
                    var dst = node.transform.TryCast<RectTransform>();
                    if (src != null && dst != null)
                    {
                        var gap = src.rect.width > 1f ? Mathf.Max(8f, src.rect.width * 0.04f) : 8f;
                        dst.anchoredPosition = src.anchoredPosition + new Vector2(src.rect.width + gap, 0f);
                    }
                    node.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
                }
                catch (Exception) { }

                label = BuildIcon(node);
                UiTooltip.Attach(node, "蓝图范围",
                    "「全部蓝图」= 内容库里凡注册了 seed 定义的蓝图都列出来（含未解锁与图鉴外的条目）；\n"
                    + "「原版蓝图」= 只看原版按存档解锁算出来的那批。\n"
                    + "图标 =「-」说明现在显示的是全部蓝图，「+」说明是原版那一批。\n"
                    + "只在原版 debug 存档里出现。");
                UpdateLabel();

                Plugin.Log?.LogInfo("[Blueprints] 已在选卡界面「取消」按钮右侧加了一个切换按钮"
                    + "（当前 " + AllBlueprintsPatch.LastOrderedCount + " 项，官方 "
                    + AllBlueprintsPatch.LastOfficialCount + " 项）\n" + Dump(source));
            }
            catch (Exception e)
            {
                Plugin.LogOnce("bptoggle-create", "[Blueprints] 创建切换按钮失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        private static void OnClick()
        {
            try
            {
                AllBlueprintsPatch.ToggleShowAll();
                UpdateLabel();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Blueprints] 切换蓝图范围失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// 准备按钮上的**图标**（需求：显示全部时 <c>-</c>、不显示时 <c>+</c>）。
        ///
        /// <para>「X（取消全选）」那个按钮本身只有图标、**没有文字节点**（实测），
        /// 所以克隆过来之后要自己造一个：把它自带的那张图标**关掉**（那是「X」，
        /// 与「- / +」语义冲突），再叠一个占满按钮的文字。</para>
        ///
        /// <para>为什么要关图标而不是换 sprite：没有现成的「-」「+」sprite，
        /// 而自绘 sprite 违背本 mod「不新造美术资源」的取向；文字最省事也最清楚。</para>
        /// </summary>
        private static TextMeshProUGUI BuildIcon(GameObject clone)
        {
            try
            {
                // 本来有文字节点就直接用（将来原版换成文字按钮也能用）。
                var existing = UiClone.FindText(clone);
                if (existing != null)
                    return existing;

                // 把克隆体自带的图标（子节点上的 Image）关掉 —— 只关子节点，
                // 按钮自己那层 Image 留着（它是射线目标，关了就点不动了）。
                var children = clone.transform.childCount;
                for (var i = 0; i < children; i++)
                {
                    var child = clone.transform.GetChild(i);
                    if (child == null)
                        continue;
                    var go = child.gameObject;
                    if (go.GetComponent<Image>() == null)
                        continue;
                    go.SetActive(false);
                }

                var label = UiKit.Label(clone.transform, "DSH_Icon", string.Empty);
                if (label == null)
                {
                    Plugin.LogOnce("bptoggle-nolabel", "[Blueprints] 造不出图标文字节点，切换按钮只有悬停提示可用");
                    return null;
                }
                var le = label.gameObject.GetComponent<LayoutElement>();
                if (le != null)
                    le.ignoreLayout = true;     // 别参与按钮上的任何布局
                var rt = label.rectTransform;
                if (rt != null)
                {
                    // 占满按钮（与按钮同矩形）→ 文字自然居中。
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;
                }
                label.alignment = TextAlignmentOptions.Center;
                try
                {
                    label.enableAutoSizing = false;
                    label.fontSize = IconFontSize;
                    label.enableWordWrapping = false;
                    // ★ 文字必须**不吃射线**：它盖满了整个按钮，是射线目标的话
                    //   点上去命中不到下面的 Button —— 实测症状就是「按钮看得见、点不了」。
                    label.raycastTarget = false;
                    label.color = Color.white;
                }
                catch (Exception) { }
                // 视觉居中：`-` 与 `+` 在 TMP 里上下留白不对称（字形的 ink 中心偏上），
                // 所以整个文字块往右下偏一点，看起来才在按钮正中。
                if (rt != null)
                {
                    rt.offsetMin = new Vector2(IconOffset.x, IconOffset.y);
                    rt.offsetMax = new Vector2(IconOffset.x, IconOffset.y);
                }
                return label;
            }
            catch (Exception e)
            {
                Plugin.LogOnce("bptoggle-icon", "[Blueprints] 造图标失败：" + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>图标字号（按钮只有 40x40，16pt 偏小）。</summary>
        private const float IconFontSize = 30f;

        /// <summary>图标的视觉居中偏移（`-` / `+` 的字形 ink 中心偏上，往右下偏一点才看着居中）。</summary>
        private static readonly Vector2 IconOffset = new Vector2(1.5f, -1.5f);

        private static void UpdateLabel()
        {
            if (label == null)
                return;
            try
            {
                // 需求：图标在**显示时**为「-」、不显示时为「+」。
                label.text = AllBlueprintsPatch.ShowAll ? "-" : "+";
            }
            catch (Exception) { }
        }

        /// <summary>
        /// 自测/日志用：把一个按钮里面有什么打出来（有没有 TMP、图片用的是什么 sprite、多大）。
        /// 需求要的「- / +」图标得先知道按钮上原本画的是什么，才能决定改哪里。
        /// </summary>
        public static string Dump(Button source)
        {
            var sb = new System.Text.StringBuilder(500);
            try
            {
                if (source == null)
                    return "（没有按钮）";
                sb.Append("按钮 ").Append(SafeName(source.gameObject));
                AppendSelf(sb, source.gameObject, 0);
                AppendTree(sb, source.gameObject, 1);
                if (UiFinder.IsAlive(node))
                {
                    sb.Append("\n  克隆体（我们的按钮）：").Append(SafeName(node));
                    AppendSelf(sb, node, 0);
                    AppendTree(sb, node, 1);
                }
            }
            catch (Exception e)
            {
                sb.Append("\n（dump 失败 ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append("）");
            }
            return sb.ToString();
        }

        /// <summary>节点自己的名字/矩形/图形/文字（不看子节点）。</summary>
        private static void AppendSelf(System.Text.StringBuilder sb, GameObject go, int depth)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                var rt = go.transform.TryCast<RectTransform>();
                sb.Append("  ").Append(go.activeSelf ? "" : "（已隐藏）")
                  .Append(" 尺寸=").Append(rt == null ? "?"
                        : (Mathf.RoundToInt(rt.rect.width) + "x" + Mathf.RoundToInt(rt.rect.height)));
                if (rt != null)
                    sb.Append(" 屏").Append(ScreenRect(rt));
                var tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
                if (tmp != null)
                    sb.Append(" 文字「").Append(tmp.text).Append("」");
                var img = go.GetComponent<Image>();
                if (img != null)
                {
                    sb.Append(" 图=").Append(img.sprite == null ? "（无 sprite）" : img.sprite.name);
                    try { sb.Append(" 色=").Append(img.color.r.ToString("0.##")).Append(',').Append(img.color.a.ToString("0.##")); }
                    catch (Exception) { }
                    sb.Append(" 射线=").Append(img.raycastTarget ? "开" : "关");
                }
                else
                {
                    sb.Append(" 图=（没有 Image）");
                }
            }
            catch (Exception e)
            {
                sb.Append("（自己读不出来 ").Append(e.GetType().Name).Append("）");
            }
        }

        private static void AppendTree(System.Text.StringBuilder sb, GameObject root, int depth)
        {
            if (!UiFinder.IsAlive(root) || depth > 4)
                return;
            try
            {
                var count = root.transform.childCount;
                for (var i = 0; i < count; i++)
                {
                    var child = root.transform.GetChild(i);
                    if (child == null)
                        continue;
                    var go = child.gameObject;
                    sb.Append("\n  ");
                    for (var k = 0; k < depth; k++)
                        sb.Append("  ");
                    sb.Append(SafeName(go));
                    AppendSelf(sb, go, depth);
                    AppendTree(sb, go, depth + 1);
                }
            }
            catch (Exception e)
            {
                sb.Append("\n  （子树读不出来 ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append("）");
            }
        }

        private static string SafeName(GameObject go)
        {
            try { return go == null ? "（null）" : go.name; }
            catch (Exception) { return "（名字读不出来）"; }
        }

        private static void Discard()
        {
            label = null;
            button = null;
            if (!UiFinder.IsAlive(node))
            {
                node = null;
                return;
            }
            try { UnityEngine.Object.Destroy(node); }
            catch (Exception) { }
            node = null;
        }

        /// <summary>
        /// 自测用：把选卡界面**强制显示出来**再走一次心跳。
        ///
        /// <para>正常流程里它只在「开局前选卡」那会儿出现，自测是直接起关卡的（跳过了选卡），
        /// 所以按钮平时根本不会被创建 —— 必须能主动把它拉起来才验得到
        /// （<c>LevelUIBlueprintChoose.SetBlueprintChooseDisplaying</c> 是公开的）。</para>
        /// </summary>
        public static string DebugForceShow()
        {
            try
            {
                var p = ResolvePart();
                if (p == null)
                    return "（找不到选卡控制器）";
                part = p;
                var ui = p.chooseUI;
                if (ui == null)
                    return "（控制器上没有 chooseUI）";
                ui.SetBlueprintChooseDisplaying(true);
                Update();
                return (AllBlueprintsPatch.IsAllowed() ? "debug 存档" : "非 debug 存档")
                    + "（IsAllowed=" + AllBlueprintsPatch.IsAllowed() + "）  显示状态="
                    + ui.blueprintChooseDisplaying + "\n  " + Describe();
            }
            catch (Exception e)
            {
                return "（强制显示失败 " + e.GetType().Name + ": " + e.Message + "）";
            }
        }

        /// <summary>
        /// 找选卡控制器。
        ///
        /// <para>★ 优先走**控制器自己的部件表**（<c>LevelController.BlueprintChoosePart</c>）：
        /// 场景遍历 <c>FindOne</c> 走的是 <c>FindObjectsOfType</c>，**不含未激活的对象**，
        /// 而「直接起关卡」时选卡那套 UI 是关着的 —— 实测就找不到（自测里报「找不到选卡控制器」）。</para>
        /// </summary>
        private static MVZ2.Level.LevelBlueprintChooseController ResolvePart()
        {
            try
            {
                var controller = LevelFacade.Controller;
                var part = controller == null ? null : controller.BlueprintChoosePart;
                if (part != null)
                    return part;
            }
            catch (Exception) { }
            return UiFinder.FindOne<MVZ2.Level.LevelBlueprintChooseController>();
        }

        /// <summary>自测用：把「X（取消全选）」按钮的层级 dump 出来。</summary>
        public static string DebugDumpX()
        {
            try
            {
                var p = UiFinder.IsAlive(part) ? part : ResolvePart();
                var ui = p == null ? null : p.chooseUI;
                var panel = ui == null ? null : ui.blueprintChoosePanel;
                var choose = panel == null ? null : panel.GetComponent<MVZ2.Level.Blueprints.BlueprintChoosePanel>();
                if (choose == null)
                    return "（拿不到选卡面板组件）";
                var sb = new System.Text.StringBuilder(500);
                sb.Append("面板上的按钮：start=" + Name(choose.startButton)
                    + " viewLawn=" + Name(choose.viewLawnButton)
                    + " cancel=" + Name(choose.cancelButton)
                    + " repick=" + Name(choose.repickButton));
                // ★ 四个原版按钮 + 我们的克隆体各自的**屏幕矩形**：
                //   这样就能一眼看出「是我们的按钮位置算错了」还是「整个面板这会儿就不在屏上」
                //   （自测里的强制显示属于后者，是测试假象，不代表真·选卡界面的位置）。
                sb.Append("\n  屏矩形 start=").Append(ScreenRectOf(choose.startButton))
                  .Append("\n  屏矩形 viewLawn=").Append(ScreenRectOf(choose.viewLawnButton))
                  .Append("\n  屏矩形 cancel=").Append(ScreenRectOf(choose.cancelButton))
                  .Append("\n  屏矩形 repick=").Append(ScreenRectOf(choose.repickButton))
                  .Append("\n  屏矩形 我们的=").Append(ScreenRectOf(node));
                sb.Append('\n').Append(Dump(choose.cancelButton));
                return sb.ToString();
            }
            catch (Exception e)
            {
                return "（dump X 按钮失败 " + e.GetType().Name + ": " + e.Message + "）";
            }
        }

        private static string Name(Button b)
        {
            if (b == null)
                return "（无）";
            try { return b.gameObject.name; }
            catch (Exception) { return "（读不到）"; }
        }

        /// <summary>自测用：把所有蓝图模式切一下并回报（验「切换 + 图标」）。</summary>
        public static string DebugToggle()
        {
            var before = AllBlueprintsPatch.ShowAll;
            var now = AllBlueprintsPatch.ToggleShowAll();
            Update();
            return "切换前=" + (before ? "全部" : "原版") + " → 切换后=" + (now ? "全部" : "原版")
                + "  图标=" + (label == null ? "（没有文字节点！）" : label.text)
                + "  列表=" + AllBlueprintsPatch.LastOrderedCount + " 项（官方 "
                + AllBlueprintsPatch.LastOfficialCount + " 项）";
        }

        /// <summary>
        /// 自测用：图标文字节点的实际可点参数。
        ///
        /// <para>这一批的主要 bug 是「按钮看得见、点不了」：图标文字是**铺满按钮**的，
        /// 它自己又是射线目标 → 射线全被它吃了，下面的 <c>Button</c> 永远收不到点击。
        /// 所以这里把 <c>raycastTarget</c> 打出来当判据。</para>
        /// </summary>
        public static string DebugIcon()
        {
            if (label == null)
                return "图标文字节点=无";
            try
            {
                var rt = label.rectTransform;
                return "图标「" + label.text + "」字号=" + label.fontSize
                    + "  颜色=" + label.color.r.ToString("0.##") + "/" + label.color.g.ToString("0.##")
                    + "/" + label.color.b.ToString("0.##") + "/" + label.color.a.ToString("0.##")
                    + "  吃射线=" + (label.raycastTarget ? "是（会挡住点击！）" : "否")
                    + "  自动缩放=" + (label.enableAutoSizing ? "开" : "关")
                    + "  偏移=" + (rt == null ? "?" : rt.offsetMin.x.ToString("0.##") + "," + rt.offsetMin.y.ToString("0.##"))
                    + "  判据 不挡点击=" + (label.raycastTarget ? "NG" : "OK")
                    + "  判据 白字=" + (label.color.r > 0.99f && label.color.g > 0.99f && label.color.b > 0.99f ? "OK" : "NG");
            }
            catch (Exception e)
            {
                return "读图标参数失败：" + e.GetType().Name;
            }
        }

        /// <summary>
        /// 自测用：在按钮中心做一次**真正的 UI 射线**，看是谁在吃点击。
        ///
        /// <para>这个按钮的 <c>onClick</c> 收不到点击，而「看不见的挡板」靠肉眼是查不出来的 ——
        /// 只能问 EventSystem：把命中的前几个节点打出来，谁挡在上面一目了然。
        /// （这也是本轮把点击改成自己矩形命中的依据。）</para>
        /// </summary>
        public static string DebugRaycast()
        {
            if (!UiFinder.IsAlive(node))
                return "（按钮不存在）";
            try
            {
                var rt = node.transform.TryCast<RectTransform>();
                if (rt == null)
                    return "（按钮没有 RectTransform）";
                var center = RectTransformUtility.WorldToScreenPoint(
                    UiHitTest.CameraFor(node), rt.TransformPoint(rt.rect.center));
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es == null)
                    return "（场景里没有 EventSystem）";

                var data = new UnityEngine.EventSystems.PointerEventData(es);
                data.position = center;
                var list = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                es.RaycastAll(data, list);

                var sb = new System.Text.StringBuilder(400);
                sb.Append("按钮中心屏坐标=").Append(Mathf.RoundToInt(center.x)).Append(',')
                  .Append(Mathf.RoundToInt(center.y))
                  .Append("  命中 ").Append(list.Count).Append(" 个\n");                var self = false;
                for (var i = 0; i < list.Count && i < 6; i++)
                {
                    var go = list[i].gameObject;
                    if (go == null)
                        continue;
                    if (go == node || UiHost.IsOwn(go.transform))
                        self = true;
                    sb.Append("  ").Append(i).Append(": ").Append(UiFinder.PathOf(go.transform)).Append('\n');
                }
                sb.Append("  判据 射线能命中自己=").Append(self ? "能" : "不能（就是它在挡住点击）");

                // ★ 真正要紧的是这一条：我们**不看 EventSystem**，点击走的是
                //   「Input.GetMouseButtonDown → UiHitTest.Contains(矩形命中)」。
                //   所以只要这一条为「能」，点按钮中心就一定会走到 OnClick。
                var hit = UiHitTest.Contains(node, center);
                sb.Append("\n  判据 矩形命中能打到自己=").Append(hit ? "能（点击通路成立）" : "不能（点击通路断了）");
                sb.Append("\n  按钮屏矩形=").Append(ScreenRect(rt));
                return sb.ToString();
            }
            catch (Exception e)
            {
                return "射线探测失败：" + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>
        /// 自测用：把「点在按钮中心」这条路**真的走一遍**（矩形命中 → 切换 → 改图标 → 再点还原）。
        ///
        /// <para>这个按钮的点击**不看 EventSystem**，所以「射线命中 0 个」不代表点不了；
        /// 真正要证的是「点落在矩形里就会切换」。这里把点喂给 <see cref="TryClick"/>，
        /// 与 <see cref="Frame"/> 走的是同一个函数 —— 只剩「读鼠标按键」那一步没法自动验。</para>
        /// </summary>
        public static string DebugClick()
        {
            if (!UiFinder.IsAlive(node))
                return "（按钮不存在）";
            try
            {
                var rt = node.transform.TryCast<RectTransform>();
                if (rt == null)
                    return "（按钮没有 RectTransform）";
                var center = RectTransformUtility.WorldToScreenPoint(
                    UiHitTest.CameraFor(node), rt.TransformPoint(rt.rect.center));

                var before = AllBlueprintsPatch.ShowAll;
                var hit = TryClick(center);
                var after = AllBlueprintsPatch.ShowAll;
                // 再点一次还原成原样（探针不能把状态留在原地）。
                // ⚠️ 先清掉「同帧只切一次」的闸门 —— 探针是在**同一帧**里点两下的。
                _lastClickFrame = -1;
                var back = TryClick(center);
                var restored = AllBlueprintsPatch.ShowAll == before;
                return "点中心(" + Mathf.RoundToInt(center.x) + ',' + Mathf.RoundToInt(center.y) + ")"
                    + "  矩形命中=" + (hit ? "是" : "否")
                    + "  链上激活=" + (SafeActiveInHierarchy() ? "是" : "否（自测强制显示时它整个面板都还没激活 → 这是测试假象）")
                    + "  画布=" + UiHitTest.CanvasOf(node)
                    + "  模式 " + (before ? "全部" : "原版") + " → " + (after ? "全部" : "原版")
                    + "\n  判据 点中即切换=" + (hit && before != after ? "OK" : "NG")
                    + "  判据 再点一次能还原=" + (back && restored ? "OK" : "NG");
            }
            catch (Exception e)
            {
                return "点击探针失败：" + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>自测用：节点的屏幕矩形（在本机像素空间里，和 <c>Input.mousePosition</c> 同一套）。</summary>
        private static string ScreenRect(RectTransform rt)
        {
            if (rt == null)
                return "（无矩形）";
            try
            {
                var cam = UiHitTest.CameraFor(rt.gameObject);
                var bl = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.min));
                var tr = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.max));
                return "x[" + Mathf.RoundToInt(bl.x) + "," + Mathf.RoundToInt(tr.x)
                    + "] y[" + Mathf.RoundToInt(bl.y) + "," + Mathf.RoundToInt(tr.y)
                    + "]  屏=" + Screen.width + "x" + Screen.height;
            }
            catch (Exception e)
            {
                return "（读不出来 " + e.GetType().Name + "）";
            }
        }

        private static string ScreenRectOf(Button b)
        {
            if (b == null)
                return "（无）";
            try { return ScreenRect(b.transform.TryCast<RectTransform>()); }
            catch (Exception) { return "（读不出来）"; }
        }

        private static bool SafeActiveInHierarchy()
        {
            try { return UiFinder.IsAlive(node) && node.activeInHierarchy; }
            catch (Exception) { return false; }
        }

        private static string ScreenRectOf(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return "（无）";
            try { return ScreenRect(go.transform.TryCast<RectTransform>()); }
            catch (Exception) { return "（读不出来）"; }
        }

        public static string Describe()
        {
            if (!UiFinder.IsAlive(node))
                return "选卡界面切换按钮：未创建";
            return "选卡界面切换按钮：" + UiFinder.PathOf(node.transform)
                + "  文案=" + (label == null ? "?" : label.text)
                + "  模式=" + (AllBlueprintsPatch.ShowAll ? "全部" : "原版");
        }
    }
}
