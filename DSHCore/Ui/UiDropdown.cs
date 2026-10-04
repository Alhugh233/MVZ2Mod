using System;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DSHCore.Ui
{
    /// <summary>弹窗画布要怎么配。</summary>
    public enum UiPopupCanvasMode
    {
        /// <summary>与宿主根画布一致（renderMode / worldCamera / planeDistance / sortingLayer）。</summary>
        MatchHost,

        /// <summary>强制当成一个 Overlay 根画布（renderMode=ScreenSpaceOverlay、无相机）。</summary>
        Overlay,

        /// <summary>直接不要画布：把列表当成普通节点插在宿主画布层里。</summary>
        Strip,
    }

    /// <summary>
    /// <c>TMP_Dropdown</c> 的**展开列表**：**只用于诊断**。
    ///
    /// <para><b>⚠️ 碍于技术问题，下拉框组件无法正常工作，请自行寻找替代方案，如果你对修复该组件有任何建议，
    /// 请联系 QoZnoS[https://github.com/QoZnoS]。</b></para>
    ///
    /// <para>这一整类存在的唯一目的是**把运行时事实打出来**，好让接手的人不必从零复现：
    /// 展开列表不是 prefab 里的固定节点，而是 <c>Show()</c> 时由 <c>TMP_Dropdown</c> 从
    /// <c>template</c>（prefab 里那个 <c>SetActive(false)</c> 的 <c>Template</c> 子节点）
    /// **实例化**出来的，叫 <c>Dropdown List</c>；关掉时又会被 <c>Destroy</c> 掉。
    /// 所以"列表为什么不显示"在 prefab 里根本看不到 —— 只能在运行时问。</para>
    ///
    /// <para><b>已排除的猜想（都不成立，别再试一遍）：</b>列表节点确实被创建、<c>active=1</c>、
    /// <c>CanvasGroup.alpha=1</c>、<c>Canvas overrideSorting=1 order=30000</c>、
    /// 选项行的 <c>Graphic</c> 全是 <c>enabled=1</c>、屏幕矩形正常 —— 但像素差分恒为 0，
    /// 而射线照常生效。把弹窗画布对齐宿主 / 强制 Overlay / 把列表挪出面板 / 搬到根画布 /
    /// 删掉列表画布，**五种都测不到任何像素变化**。
    /// 完整数字见 <c>UI_RESEARCH.md</c> §6.4-17 与 §7.6。</para>
    ///
    /// <para>本类**不提供替代实现**。要显示"当前选项"可以用
    /// <see cref="UiOptionRow.SetDropdown"/> 的外观（标题文字照常显示）；要真正的选择交互
    /// 请自行设计（例如一排复选框行）。</para>
    /// </summary>
    public static class UiDropdown
    {
        /// <summary>TMP 给展开列表起的名字（<c>TMP_Dropdown.Show</c> 里写死的）。</summary>
        public const string ListObjectName = "Dropdown List";

        /// <summary>弹窗画布的最低排序值（原版 TMP 用的就是 30000）。</summary>
        public const int PopupSortingBaseOrder = 30000;

        /// <summary>
        /// 预先给下拉框的**弹窗模板**配好画布 —— 这是本游戏里“下拉框点开不显示”的根因修法。
        ///
        /// <para><b>病因：</b><c>TMP_Dropdown</c> 展开时会在 <c>template</c> 上加一个
        /// <c>Canvas</c>（运行时新建的 Canvas 是 <c>ScreenSpaceOverlay</c>、<c>worldCamera = null</c>），
        /// 自己只改 <c>overrideSorting = 1</c> 与 <c>sortingOrder = 30000</c>。
        /// 而本游戏的主 UI 画布 <c>GlobalUICanvas</c> 是 <b>Screen Space - Camera</b>
        /// （<c>cam = UICamera</c>、<c>planeDistance = 100</c>）。
    /// 开了 <c>overrideSorting</c> 的嵌套画布会被当成独立画布按自己的 renderMode/worldCamera 处理，
        /// 于是那棵弹窗一个像素都画不出来 —— 而它的 <c>GraphicRaycaster</c> 照常生效，
        /// 所以表现为“点开没反应、乱点却能改值”。</para>
        ///
        /// <para><b>为什么在“展开之前”就能修：</b>TMP 是用 <c>GetOrAddComponent&lt;Canvas&gt;</c>
        /// 拿画布的（<c>Canvas</c> 是 <c>DisallowMultipleComponent</c>），所以先把 Canvas 配好，
        /// TMP 就会直接用它。不必等到展开之后再追着改。</para>
        ///
        /// <para><b>为什么原版设置界面看起来“正常”：</b>那边挂在 <c>Mainmenu/Canvas</c> 这类
        /// <b>Overlay</b> 画布下，运行时新建的 Overlay 弹窗画布恰好能画出来；
        /// 一旦把同一个下拉框搬进 <c>GlobalUICanvas</c>（相机画布）就会现形。</para>
        /// </summary>
        public static bool PreparePopup(TMP_Dropdown dd, UiPopupCanvasMode mode = UiPopupCanvasMode.MatchHost)
        {
            if (!UiFinder.IsAlive(dd))
                return false;
            try
            {
                var tpl = dd.template;
                if (!UiFinder.IsAlive(tpl))
                {
                    UiLog.WarnOnce("dd-no-template", "下拉框没有 template 子节点，弹窗无法准备。");
                    return false;
                }

                var popup = GetOrAddComponent<Canvas>(tpl.gameObject);
                if (popup == null)
                    return false;

                ApplyCanvasMode(popup, HostCanvas(dd), mode);
                GetOrAddComponent<CanvasGroup>(tpl.gameObject);
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("准备下拉弹窗画布失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 换一种弹窗画布配法（用于 A/B 排查）。已展开的列表下次展开才生效。
        ///
        /// <para>返回 false 表示没有模板可改。</para>
        /// </summary>
        public static bool SetPopupCanvasMode(TMP_Dropdown dd, UiPopupCanvasMode mode)
        {
            return PreparePopup(dd, mode);
        }

        private static void ApplyCanvasMode(Canvas popup, Canvas host, UiPopupCanvasMode mode)
        {
            popup.overrideSorting = true;
            switch (mode)
            {
                case UiPopupCanvasMode.Overlay:
                    popup.renderMode = RenderMode.ScreenSpaceOverlay;
                    popup.worldCamera = null;
                    popup.planeDistance = 100f;
                    break;

                case UiPopupCanvasMode.MatchHost:
                    if (host != null)
                    {
                        popup.renderMode = host.renderMode;
                        popup.worldCamera = host.worldCamera;
                        popup.planeDistance = host.planeDistance;
                        popup.sortingLayerID = host.sortingLayerID;
                        popup.sortingOrder = Mathf.Max(PopupSortingBaseOrder, host.sortingOrder + 1);
                    }
                    break;

                case UiPopupCanvasMode.Strip:
                    // 什么都不配 —— 由 StripListCanvas 直接删掉画布。
                    break;
            }
        }

        /// <summary>
        /// 把已展开的列表从下拉框底下**搬到宿主画布的顶层**。
        ///
        /// <para>假设的问题是"列表被面板的背景盖住了"（若如此，它的像素会与未展开时完全一致）。</para>
        /// </summary>
        public static bool MoveListToRoot(TMP_Dropdown dd)
        {
            var list = FindList(dd);
            var host = HostCanvas(dd);
            if (!UiFinder.IsAlive(list) || host == null)
                return false;
            try
            {
                list.transform.SetParent(host.transform, true);
                list.transform.SetAsLastSibling();
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("搬移下拉列表失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 把已展开列表自己的 <c>Canvas</c> 删掉，让它当普通节点参与宿主画布的绘制顺序。
        ///
        /// <para>这一手同时解决"排序无效"与"相机模式画不出"两种可能 ——
        /// 但代价是它不再是一个独立排序层，可能被同画布内更晚绘制的节点盖住。</para>
        /// </summary>
        public static bool StripListCanvas(TMP_Dropdown dd)
        {
            var list = FindList(dd);
            if (!UiFinder.IsAlive(list))
                return false;
            try
            {
                var canvas = list.GetComponent<Canvas>();
                if (canvas != null)
                    UnityEngine.Object.Destroy(canvas);
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("删下拉列表画布失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 把已展开列表挪到**屏幕上的指定点**（列将把它的 pivot 对齐到该点）。
        ///
        /// <para>用途：验证“列表是不是被面板不透明背景盖住了”。若挪到面板外的空白处
        /// 就能看见，那病因就是排序而不是渲染。</para>
        /// </summary>
        public static bool MoveListTo(TMP_Dropdown dd, Vector2 screenPoint)
        {
            var list = FindList(dd);
            if (!UiFinder.IsAlive(list))
                return false;
            try
            {
                var host = HostCanvas(dd);
                var hostRect = host == null ? null : host.transform.TryCast<RectTransform>();
                var cam = host == null || host.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null
                    : host.worldCamera;

                if (hostRect == null)
                    return false;

                Vector3 world;
                if (!RectTransformUtility.ScreenPointToWorldPointInRectangle(hostRect, screenPoint, cam, out world))
                    return false;

                list.transform.position = world;
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("挪移下拉列表失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 删掉**模板**上事先加的 <c>Canvas</c> / <c>CanvasGroup</c>，回到“TMP 完全自理”的原始状态。
        ///
        /// <para>这是我事先改画布的对照实验：如果删掉之后反而能画出来，
        /// 那说明 <see cref="PreparePopup"/> 是在帮倒忙（TMP 自己建的那个 Overlay 画布才是对的）。</para>
        /// </summary>
        public static bool StripTemplateCanvas(TMP_Dropdown dd)
        {
            if (!UiFinder.IsAlive(dd))
                return false;
            try
            {
                var tpl = dd.template;
                if (!UiFinder.IsAlive(tpl))
                    return false;

                var canvas = tpl.GetComponent<Canvas>();
                if (canvas != null)
                    UnityEngine.Object.Destroy(canvas);
                var group = tpl.GetComponent<CanvasGroup>();
                if (group != null)
                    UnityEngine.Object.Destroy(group);
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("清理模板画布失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>下拉框所属于的根画布（对齐弹窗画布时要参照它）。</summary>
        public static Canvas HostCanvas(TMP_Dropdown dd)
        {
            if (!UiFinder.IsAlive(dd))
                return null;
            try
            {
                var any = dd.GetComponentInParent<Canvas>();
                return any == null ? null : any.rootCanvas;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static T GetOrAddComponent<T>(GameObject go) where T : Component
        {
            try
            {
                var c = go.GetComponent<T>();
                return c != null ? c : go.AddComponent<T>();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>在一个节点（或其子树）上找下拉框。找不到返回 null。</summary>
        public static TMP_Dropdown Of(GameObject node)
        {
            if (!UiFinder.IsAlive(node))
                return null;
            try
            {
                var dd = node.GetComponent<TMP_Dropdown>();
                if (dd == null)
                    dd = node.GetComponentInChildren<TMP_Dropdown>(true);
                return dd;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>展开列表（等同用户点一下）。</summary>
        public static bool Show(TMP_Dropdown dd)
        {
            if (!UiFinder.IsAlive(dd))
                return false;
            try
            {
                dd.Show();
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("展开下拉列表失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>收起列表。</summary>
        public static bool Hide(TMP_Dropdown dd)
        {
            if (!UiFinder.IsAlive(dd))
                return false;
            try
            {
                dd.Hide();
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("收起下拉列表失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// 找当前的展开列表。
        ///
        /// <para>它被实例化在**模板的父节点**下（也就是下拉框自己），名字固定是
        /// <c>Dropdown List</c>。不要按"模板的克隆"去找 —— 模板本身还在，两者长得一样。</para>
        /// </summary>
        public static GameObject FindList(TMP_Dropdown dd)
        {
            if (!UiFinder.IsAlive(dd))
                return null;
            try
            {
                var t = dd.transform.Find(ListObjectName);
                return t == null ? null : t.gameObject;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 把下拉框与它的展开列表的运行时状态打出来。
        ///
        /// <para>建议在 <c>Show()</c> 的当帧、以及之后若干帧（等 <c>CanvasGroup</c> 淡入完成）
        /// 各打一次 —— 两次的数据不一样，合起来才够判断。</para>
        /// </summary>
        public static string Describe(TMP_Dropdown dd)
        {
            if (!UiFinder.IsAlive(dd))
                return "（无下拉框）";

            var sb = new StringBuilder(700);
            try
            {
                var go = dd.gameObject;

                sb.Append("本身：").Append(UiFinder.PathOf(dd.transform)).Append('\n');
                sb.Append("  互动=").Append(dd.IsInteractable() ? 1 : 0);
                sb.Append(" 展开中=").Append(dd.IsExpanded ? 1 : 0);

                var count = 0;
                try
                {
                    var options = dd.options;
                    count = options == null ? 0 : options.Count;
                }
                catch (Exception) { }
                sb.Append(" 值=").Append(dd.value).Append('/').Append(count);

                var caption = dd.captionText;
                if (UiFinder.IsAlive(caption))
                    sb.Append(" 标题=「").Append(caption.text ?? "").Append('」');
                sb.Append('\n');

                AppendNode(sb, "  自身矩形", go);

                // 活动链：Show() 在 !IsActive() 时会**直接返回且不报错**，
                // 而"哪个祖先被关了"光看这条矩形是看不出来的（矩形照样是对的）。
                sb.Append("  活动链：");
                var up = dd.transform;
                for (var i = 0; i < 5 && up != null; i++)
                {
                    sb.Append(up.name).Append(up.gameObject.activeSelf ? "[on] " : "[OFF] ");
                    up = up.parent;
                }
                sb.Append('\n');
                // 宿主画布 vs 弹窗画布：两者 renderMode/worldCamera 不一致就是“画不出来”的根因。
                sb.Append("  宿主画布：").Append(DescribeCanvas(HostCanvas(dd))).Append('\n');

                // 模板：正常状态应该是 inactive 的；Show() 之后它会被临时激活。
                var tpl = dd.template;
                if (UiFinder.IsAlive(tpl))
                {
                    sb.Append("  模板：").Append(UiFinder.PathOf(tpl)).Append('\n');
                    AppendNode(sb, "    模板矩形", tpl.gameObject);
                }
                else
                {
                    sb.Append("  模板：（空）—— 这种下拉框永远展不开。\n");
                }

                var list = FindList(dd);
                if (!UiFinder.IsAlive(list))
                {
                    sb.Append("  列表：**没有**（Show() 没有创建出来，或已经被 Hide() 销毁）\n");
                }
                else
                {
                    sb.Append("  列表：").Append(UiFinder.PathOf(list.transform)).Append('\n');
                    AppendNode(sb, "    列表矩形", list);
                    sb.Append("    Canvas：");
                    var canvas = list.GetComponent<Canvas>();
                    if (canvas == null)
                        sb.Append("**没有 Canvas** —— 会被父层的排序吃掉。\n");
                    else
                        sb.Append(DescribeCanvas(canvas)).Append('\n');

                    sb.Append("    CanvasGroup：");
                    var group = list.GetComponent<CanvasGroup>();
                    if (group == null)
                        sb.Append("**没有**（TMP 的淡入需要它）\n");
                    else
                        sb.Append("alpha=").Append(Number(group.alpha))
                          .Append(" ignoreParentGroups=").Append(group.ignoreParentGroups ? 1 : 0)
                          .Append(" blocksRaycasts=").Append(group.blocksRaycasts ? 1 : 0).Append('\n');

                    sb.Append("    Graphic：").Append(DescribeGraphics(list)).Append('\n');
                    sb.Append(DescribeGraphicsDetail(list, "      "));
                }

                sb.Append("  子节点：").Append(DescribeChildren(dd.transform)).Append('\n');
            }
            catch (Exception e)
            {
                sb.Append("读下拉框状态失败：").Append(e.GetType().Name).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 把场景里**所有活着**的 <c>ScrollRect</c> 打出来。
        ///
        /// <para>展开列表本身就是个 <c>ScrollRect</c>，所以这条清单能回答"它到底有没有被创建、
        /// 是不是被建到别处去了"。原版设置界面的 <c>Scroll View</c> 也在这份清单里，可以当对照。</para>
        /// </summary>
        public static string DumpScrollViews(string prefix = "  ")
        {
            var sb = new StringBuilder(800);
            try
            {
                var all = UiFinder.FindAll<ScrollRect>();
                sb.Append(prefix).Append("场景里的 ScrollRect：").Append(all.Count).Append('\n');
                for (var i = 0; i < all.Count; i++)
                {
                    var sr = all[i];
                    if (!UiFinder.IsAlive(sr))
                        continue;
                    var go = sr.gameObject;
                    if (!go.activeInHierarchy)
                        continue;   // 原版那份 Scroll View 平时是关着的，不用看

                    sb.Append(prefix).Append("  ").Append(UiFinder.PathOf(sr.transform)).Append('\n');
                    AppendNode(sb, prefix + "    矩形", go);
                }
            }
            catch (Exception e)
            {
                sb.Append(prefix).Append("dump ScrollRect 失败：").Append(e.GetType().Name).Append('\n');
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // 内部
        // ------------------------------------------------------------------

        private static void AppendNode(StringBuilder sb, string label, GameObject go)
        {
            if (!UiFinder.IsAlive(go))
            {
                sb.Append(label).Append("：（无）\n");
                return;
            }

            try
            {
                sb.Append(label).Append("：");

                var rt = go.GetComponent<RectTransform>();
                if (rt == null)
                {
                    sb.Append("（无 RectTransform）\n");
                    return;
                }

                var local = rt.rect;
                sb.Append("局部=").Append(Round(local.width)).Append('x').Append(Round(local.height));

                var canvas = go.GetComponentInParent<Canvas>();
                var cam = canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null
                    : canvas.worldCamera;

                var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
                rt.GetWorldCorners(corners);
                var min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
                var max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);

                sb.Append("  屏幕=x").Append(Round(Mathf.Min(min.x, max.x)))
                  .Append(" y").Append(Round(Mathf.Min(min.y, max.y)))
                  .Append(" ").Append(Round(Mathf.Abs(max.x - min.x)))
                  .Append('x').Append(Round(Mathf.Abs(max.y - min.y)));

                sb.Append("  self=").Append(go.activeSelf ? 1 : 0)
                  .Append(" inHierarchy=").Append(go.activeInHierarchy ? 1 : 0);
                sb.Append('\n');
            }
            catch (Exception e)
            {
                sb.Append(label).Append("：读矩形失败（").Append(e.GetType().Name).Append("）\n");
            }
        }

        /// <summary>
        /// 画布的渲染参数一行式。
        ///
        /// <para><c>mode=ScreenSpaceCamera</c> 时 <c>cam</c> 不能是 <c>null</c> ——
        /// 这是“节点全对但一个像素都不画”的判据（见 <see cref="PreparePopup"/>）。</para>
        /// </summary>
        private static string DescribeCanvas(Canvas c)
        {
            if (c == null)
                return "（无）";
            try
            {
                var cam = c.worldCamera;
                return "mode=" + c.renderMode
                     + " cam=" + (cam == null ? "null" : cam.name)
                     + " plane=" + Number(c.planeDistance)
                     + " override=" + (c.overrideSorting ? 1 : 0)
                     + " order=" + c.sortingOrder;
            }
            catch (Exception e)
            {
                return "读画布失败（" + e.GetType().Name + "）";
            }
        }

        /// <summary>
        /// 把场景里**所有** <c>Canvas</c> 打出来（含未激活的）。
        ///
        /// <para>为什么需要它："弹窗画不出来"这类问题最后往往取决于**排序层**与**相机模式**，
        /// 而这两件事都是画布层级的属性 —— 只盯弹窗自己那一层是看不出来的。
        /// 本仓库实测：宿主是 <c>GlobalUICanvas</c>（Screen Space - Camera）时，
        /// 即使把弹窗画布对齐成完全一样，也仍然一个像素都不画（截图差分 = 0）。</para>
        /// </summary>
        public static string DumpCanvases(string prefix = "  ")
        {
            var sb = new StringBuilder(1200);
            try
            {
                var all = UiFinder.FindAll<Canvas>();
                sb.Append(prefix).Append("场景里的 Canvas：").Append(all.Count).Append('\n');
                for (var i = 0; i < all.Count; i++)
                {
                    var c = all[i];
                    if (!UiFinder.IsAlive(c))
                        continue;

                    var rt = c.GetComponent<RectTransform>();
                    sb.Append(prefix).Append("  ")
                      .Append(c.isRootCanvas ? "[根] " : "     ")
                      .Append(UiFinder.PathOf(c.transform)).Append('\n');
                    sb.Append(prefix).Append("      ")
                      .Append(DescribeCanvas(c))
                      .Append(" active=")
                      .Append(c.gameObject.activeInHierarchy ? 1 : 0)
                      .Append(" canvasScaler=")
                      .Append(c.GetComponent<CanvasScaler>() != null ? 1 : 0);
                    if (rt != null)
                        sb.Append(" rect=").Append(Round(rt.rect.width)).Append('x').Append(Round(rt.rect.height));
                    sb.Append('\n');
                }
            }
            catch (Exception e)
            {
                sb.Append(prefix).Append("dump Canvas 失败：").Append(e.GetType().Name).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 列表子树里**逐个 Graphic** 的明细。
        ///
        /// <para>比只数"开着几个"有用：<c>sprite</c> 为空、或 <c>Mask</c> 的遮罩图被关掉，
        /// 都会造成"节点全对但一个像素不画"。名字给短路径（只留两级），否则日志读不了。</para>
        /// </summary>
        private static string DescribeGraphicsDetail(GameObject go, string prefix)
        {
            var sb = new StringBuilder(1200);
            try
            {
                var graphics = go.GetComponentsInChildren<Graphic>(true);
                if (graphics == null)
                    return prefix + "（无 Graphic）\n";

                for (var i = 0; i < graphics.Length; i++)
                {
                    var g = graphics[i];
                    if (g == null)
                        continue;

                    sb.Append(prefix).Append(g.gameObject.name);
                    sb.Append("  on=").Append(g.enabled ? 1 : 0);
                    sb.Append(" hier=").Append(g.gameObject.activeInHierarchy ? 1 : 0);

                    var img = g.TryCast<Image>();
                    if (img != null)
                    {
                        var sp = img.sprite;
                        sb.Append(" sprite=").Append(sp == null ? "null" : sp.name)
                          .Append(" a=").Append(Number(img.color.a))
                          .Append(" raycast=").Append(img.raycastTarget ? 1 : 0);
                    }

                    if (g.GetComponent<Mask>() != null)
                        sb.Append(" [Mask]");
                    if (g.GetComponent<RectMask2D>() != null)
                        sb.Append(" [RectMask2D]");

                    sb.Append('\n');
                }
            }
            catch (Exception e)
            {
                sb.Append(prefix).Append("读 Graphic 明细失败：").Append(e.GetType().Name).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 数一数这块子树里有多少 <c>Graphic</c> 是"真的会画"的。
        ///
        /// <para>"关着"的 <c>Graphic</c> 是"节点矩形完全正确却一个像素都没有"的常见原因
        /// （原版对话框的 <c>Blocker</c>/<c>Background</c> 在隐藏状态下就是 <c>enabled=0</c>），
        /// 所以这里连**名字**一起打出来，好定位到具体是哪一个。</para>
        /// </summary>
        private static string DescribeGraphics(GameObject go)
        {
            try
            {
                var graphics = go.GetComponentsInChildren<Graphic>(true);
                if (graphics == null)
                    return "（无）";

                var on = 0;
                var off = new StringBuilder(80);
                var offCount = 0;
                var noSprite = 0;

                for (var i = 0; i < graphics.Length; i++)
                {
                    var g = graphics[i];
                    if (g == null)
                        continue;

                    if (g.enabled && g.gameObject.activeInHierarchy)
                    {
                        on++;
                        var img = g.TryCast<Image>();
                        if (img != null && img.sprite == null)
                            noSprite++;
                    }
                    else if (offCount < 6)
                    {
                        off.Append(offCount == 0 ? "" : ",").Append(g.gameObject.name);
                        offCount++;
                    }
                }

                var sb = new StringBuilder(120);
                sb.Append("开着 ").Append(on).Append('/').Append(graphics.Length);
                if (noSprite > 0)
                    sb.Append("（其中无 sprite ").Append(noSprite).Append(" 个）");
                if (offCount > 0)
                    sb.Append("  关着=").Append(off);
                return sb.ToString();
            }
            catch (Exception e)
            {
                return "读 Graphic 失败（" + e.GetType().Name + "）";
            }
        }

        private static string DescribeChildren(Transform parent)
        {
            if (parent == null)
                return "（无）";
            try
            {
                var sb = new StringBuilder(120);
                var t = parent.childCount;
                for (var i = 0; i < t; i++)
                {
                    var child = parent.GetChild(i);
                    if (child == null)
                        continue;
                    if (i > 0)
                        sb.Append(", ");
                    sb.Append(child.name).Append(child.gameObject.activeSelf ? "(on)" : "(off)");
                }
                return sb.Length == 0 ? "（无）" : sb.ToString();
            }
            catch (Exception e)
            {
                return "读子节点失败（" + e.GetType().Name + "）";
            }
        }

        private static string Round(float v)
        {
            return ((int)Mathf.Round(v)).ToString();
        }

        private static string Number(float v)
        {
            return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
