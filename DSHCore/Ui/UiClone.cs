using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DSHCore.Ui
{
    /// <summary>
    /// 克隆出来的 UI 节点必须做的"规范化"。
    ///
    /// <para>这一整类问题的共同点是：**克隆体看起来"节点都在、尺寸也对"，但就是不显示或者不对**。
    /// 每一条都是实测踩出来的（见 <c>UI_RESEARCH.md</c> §6），在这里一次性堵掉，
    /// 免得每个子 mod 再踩一遍。</para>
    /// </summary>
    public static class UiClone
    {
        // ------------------------------------------------------------------
        // 克隆
        // ------------------------------------------------------------------

        /// <summary>
        /// 从模板克隆一个节点。<paramref name="parent"/> 为 null 时只创建不挂载。
        /// 内部用 <c>worldPositionStays: false</c>，保留模板的 local 变换。
        ///
        /// <para><paramref name="activate"/> 为 <c>false</c> 时**先不激活**：给调用方一个
        /// 在“任何 <c>Awake</c>/<c>OnEnable</c> 跑之前”做结构性修补的机会（删掉不该存在的组件、
        /// 剪掉要剪的子节点）。<c>Awake</c> 只在对象在**激活的**父链下变活跃时才跑，
        /// 所以“先克隆 → 修补 → 再激活”能拦住一类“Awake 里抛空指针”的问题
        /// （典型例子：可滚动容器的 <c>Content</c> 上那个 <c>_template</c> 为空的
        /// <c>ElementList</c>）。修补完必须自己调 <see cref="Activate"/>。</para>
        /// </summary>
        public static GameObject Instantiate(GameObject template, Transform parent, string name = null,
            bool activate = true)
        {
            if (!UiFinder.IsAlive(template))
                return null;
            try
            {
                var go = UnityEngine.Object.Instantiate(template, parent, false);
                if (go == null)
                    return null;
                Normalize(go, name, activate);
                return go;
            }
            catch (Exception e)
            {
                UiLog.Warn("克隆失败：" + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// 激活一个节点（配合 <c>Instantiate(..., activate: false)</c> 用）。
        /// 已激活时什么也不做。
        /// </summary>
        public static GameObject Activate(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return null;
            try
            {
                if (!go.activeSelf)
                    go.SetActive(true);
            }
            catch (Exception e)
            {
                UiLog.Warn("激活节点失败：" + e.GetType().Name);
            }
            return go;
        }

        /// <summary>
        /// 反激活一个节点（<c>SetActive(false)</c>）。配合 <see cref="Activate"/> 用来
        /// 「建好但先别显示」—— 建页面时先摆好再一次性亮出来，避免玩家看到半成品。
        /// </summary>
        public static GameObject Deactivate(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return null;
            try
            {
                if (go.activeSelf)
                    go.SetActive(false);
            }
            catch (Exception e)
            {
                UiLog.Warn("反激活节点失败：" + e.GetType().Name);
            }
            return go;
        }

        /// <summary>
        /// 规范化一个刚克隆出来的节点（原地处理，返回同一个对象便于链式调用）：
        /// <list type="number">
        /// <item>改名并打上己方标记（否则下次查找会把自己的克隆体当成模板）；</item>
        /// <item><c>SetActive(true)</c> —— 模板平时是关着的（<c>ElementList.Awake</c> 会关掉它），
        /// 不打开的话子组件的 <c>Awake</c> 永远不会跑，事件绑定也不会发生。
        /// 传 <paramref name="activate"/> = <c>false</c> 可以推后这一步（见
        /// <see cref="Instantiate"/>），修补完再用 <see cref="Activate"/>。</item>
        /// </list>
        ///
        /// <para><b>它刻意**不**去改 TMP 的自动缩放。</b>早期版本在这里无条件
        /// <c>enableAutoSizing = false</c>，那是错的：原版很多文本就是靠自动缩放塞进固定尺寸的格子里
        /// （最典型的是对话框标题 —— 36pt、<c>fontSizeMin = 18</c>，而它的容器
        /// <c>TitleArea</c> 固定只有 40px 高）。关掉之后长标题只能换成两行，于是向上溢出到框体外面。
        /// 需要“固定字号”时请用 <see cref="SetLabel"/> 并显式传尺寸。</para>
        /// </summary>
        public static GameObject Normalize(GameObject go, string name = null, bool activate = true)
        {
            if (!UiFinder.IsAlive(go))
                return null;

            try
            {
                if (!string.IsNullOrEmpty(name))
                    go.name = name;
                UiHost.MarkOwn(go);
                if (activate && !go.activeSelf)
                    go.SetActive(true);
            }
            catch (Exception e)
            {
                UiLog.Warn("规范化节点失败：" + e.GetType().Name);
            }

            return go;
        }

        /// <summary>递归关掉子树里所有 TMP 的自动字号。</summary>
        public static void DisableAutoSizing(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                var texts = go.GetComponentsInChildren<TextMeshProUGUI>(true);
                if (texts != null)
                {
                    for (var i = 0; i < texts.Length; i++)
                    {
                        var t = texts[i];
                        if (t == null)
                            continue;
                        try { t.enableAutoSizing = false; }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------
        // 图形
        // ------------------------------------------------------------------

        /// <summary>
        /// 强制把子树里所有 <see cref="Graphic"/> 打开。
        ///
        /// <para><b>为什么要它：</b>从活的原版对话框克隆出来的框体美术，其 <c>Image.enabled</c>
        /// 实测可能是 <c>0</c>（原对话框在隐藏状态下也是这样）—— 节点在、<c>sprite</c> 有、
        /// 颜色也正常，就是不画，表现为"面板没有背景"。这是 GraphicConsole 排查了很久才定案的一条。</para>
        ///
        /// <para>因为它会覆盖运行时的显隐状态，所以**不放在 <see cref="Normalize"/> 里无条件执行** ——
        /// 只在明确需要"让这块美术显形"的地方（例如对话框壳）调用，或由调用方自己决定。</para>
        /// </summary>
        public static void ForceGraphicsVisible(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                var graphics = go.GetComponentsInChildren<Graphic>(true);
                if (graphics == null)
                    return;
                for (var i = 0; i < graphics.Length; i++)
                {
                    var g = graphics[i];
                    if (g == null)
                        continue;
                    try
                    {
                        var img = g.TryCast<Image>();
                        if (img != null && img.sprite == null)
                            continue;          // 没图集的 Image 开了也画不出东西，别动它
                        g.enabled = true;
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>把 RectTransform 撑满父节点（新建容器时必做，见 <see cref="UiHost.CreateStretched"/>）。</summary>
        public static void Stretch(RectTransform rt)
        {
            if (rt == null)
                return;
            try
            {
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.offsetMin = new Vector2(0f, 0f);
                rt.offsetMax = new Vector2(0f, 0f);
            }
            catch (Exception)
            {
            }
        }

        // ------------------------------------------------------------------
        // 文本
        // ------------------------------------------------------------------

        /// <summary>在节点（含子节点）里找第一个 TMP 文本。</summary>
        public static TextMeshProUGUI FindText(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return null;
            try
            {
                var own = go.GetComponent<TextMeshProUGUI>();
                if (own != null)
                    return own;
                return go.GetComponentInChildren<TextMeshProUGUI>(true);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 设置文本内容。
        ///
        /// <para><paramref name="fontSize"/> <b>== 0 表示“只改文字、一切样式照旧”</b>（推荐）——
        /// 原版每个文本节点的字号都是搭配它所在容器算好的，乱改一定难看。
        /// 只有确实要固定字号时才传一个正数，**那时才会关掉自动缩放**
        /// （不关的话 TMP 下一次布局会把字号重新算回去，你设的值等于没设）。</para>
        /// </summary>
        public static bool SetLabel(GameObject node, string text, float fontSize = 0f, bool? wrap = null)
        {
            var t = FindText(node);
            if (t == null)
                return false;
            try
            {
                t.text = text ?? string.Empty;
                if (fontSize > 0f)
                {
                    t.enableAutoSizing = false;
                    t.fontSize = fontSize;
                }
                if (wrap.HasValue)
                    t.enableWordWrapping = wrap.Value;
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("设置文本失败：" + e.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// **只改字号**，不动文案（相当于 <see cref="SetLabel"/> 的"无文本"变体）。
        ///
        /// <para>什么时候用：原版素材的字号是为「短文本提示」调的（对话框正文 24pt、
        /// 设置项标签 16pt…）。你自己的界面要放**说明性大段文字**时，
        /// 在克隆出来之后显式调成合适的字号 —— 而不是去改 <c>UiSkin</c> 的素材
        /// （那会让所有用同一素材的人一起变）。</para>
        ///
        /// <para>会顺手关掉自动缩放：不关的话 TMP 下一次布局会把字号算回去，你设的值等于没设。
        /// 因此**不要**拿它去改标题这类靠自动缩放塞进固定格子的文本。</para>
        /// </summary>
        public static bool SetFontSize(GameObject node, float fontSize, bool? wrap = null)
        {
            var t = FindText(node);
            if (t == null || fontSize <= 0f)
                return false;
            try
            {
                t.enableAutoSizing = false;
                t.fontSize = fontSize;
                if (wrap.HasValue)
                    t.enableWordWrapping = wrap.Value;
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("设置字号失败：" + e.GetType().Name);
                return false;
            }
        }

        /// <summary>
        /// 读一个文本节点的字号信息（诊断/展示用）。返回 <c>null</c> 表示这个节点上没有文本。
        /// </summary>
        public static FontInfo DescribeFont(GameObject node)
        {
            var t = FindText(node);
            if (t == null)
                return null;
            var info = new FontInfo();
            try
            {
                info.Size = t.fontSize;
                info.AutoSizing = t.enableAutoSizing;
                info.Min = t.fontSizeMin;
                info.Max = t.fontSizeMax;
                info.Wrapping = t.enableWordWrapping;
                info.Text = t.text;
            }
            catch (Exception) { }
            return info;
        }

        /// <summary>一个文本节点的字号快照。</summary>
        public sealed class FontInfo
        {
            public float Size;
            public bool AutoSizing;
            public float Min;
            public float Max;
            public bool Wrapping;
            public string Text;

            public override string ToString()
            {
                return "fs=" + Size + (AutoSizing ? " auto=1(" + Min + "~" + Max + ")" : " auto=0")
                    + " wrap=" + (Wrapping ? 1 : 0);
            }
        }

        // ------------------------------------------------------------------
        // 交互
        // ------------------------------------------------------------------

        /// <summary>在节点（含子节点）里找第一个 <see cref="Button"/>。</summary>
        public static Button FindButton(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return null;
            try
            {
                var own = go.GetComponent<Button>();
                if (own != null)
                    return own;
                return go.GetComponentInChildren<Button>(true);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 清掉子树里所有按钮的**持久化监听器**。
        ///
        /// <para>必须做这一步的场景：<c>Instantiate</c> 会连序列化的 <c>m_OnClick</c> 一起复制。
        /// 对于从"运行时被绑过回调的模板"克隆来的按钮，那个监听器闭包指向的是**原对象**
        /// （例如原对话框的 <c>ButtonRow</c>），点下去会去操作原对象，症状古怪且难查。</para>
        ///
        /// <para>反过来说：**想继承原版手感（点击音效、<c>Deselector.Deselect</c>）时不要清**。</para>
        /// </summary>
        public static void ClearButtonListeners(GameObject go)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                var buttons = go.GetComponentsInChildren<Button>(true);
                if (buttons == null)
                    return;
                for (var i = 0; i < buttons.Length; i++)
                {
                    var b = buttons[i];
                    if (b == null)
                        continue;
                    try { b.onClick.RemoveAllListeners(); }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 给按钮挂点击回调。
        ///
        /// <para>两个必须这么写的理由：</para>
        /// <list type="bullet">
        /// <item>interop 里 <c>AddListener</c> 收的是 <c>UnityEngine.Events.UnityAction</c>（IL2CPP 委托），
        /// 托管方法组传不进去，必须 <c>DelegateSupport.ConvertDelegate</c> 造一个；</item>
        /// <item>IL2CPP 侧只存裸函数指针，**托管委托必须自己持有**，否则 GC 一收就崩。这里登记到
        /// <see cref="KeepAlive"/>，属主销毁时由 <see cref="PruneKeepAlive"/> 回收。</item>
        /// </list>
        /// </summary>
        public static bool OnClick(GameObject buttonNode, Action handler, bool clearExisting = true)
        {
            if (!UiFinder.IsAlive(buttonNode) || handler == null)
                return false;
            var button = FindButton(buttonNode);
            if (button == null)
            {
                UiLog.WarnOnce("nobutton:" + SafeName(buttonNode),
                    "节点 \"" + SafeName(buttonNode) + "\" 上没有 Button 组件，点击回调没挂上。");
                return false;
            }

            try
            {
                if (clearExisting)
                    button.onClick.RemoveAllListeners();

                var managed = new System.Action(handler);
                var il2cpp = DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(managed);
                KeepAlive(buttonNode, managed, il2cpp);
                button.onClick.AddListener(il2cpp);
                return true;
            }
            catch (Exception e)
            {
                UiLog.Warn("挂点击回调失败：" + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private static string SafeName(GameObject go)
        {
            try { return go == null ? "?" : go.name; }
            catch (Exception) { return "?"; }
        }

        // ------------------------------------------------------------------
        // 委托保活
        // ------------------------------------------------------------------
        //
        // IL2CPP 侧存的是裸函数指针，不持有托管委托 —— 不自己留住的话，GC 一收，
        // 再点击就是访问已释放的跳板（表现是随机崩溃，且没有托管栈）。
        // 只登记按钮/输入框这类少量长命委托，不做无限制增长。

        private sealed class KeepAliveEntry
        {
            public GameObject Owner;
            public object[] Items;
        }

        private static readonly List<KeepAliveEntry> Alive = new List<KeepAliveEntry>();

        /// <summary>登记一批依赖 <paramref name="owner"/> 存活的托管对象（属主销毁后自动回收）。</summary>
        public static void KeepAlive(GameObject owner, params object[] items)
        {
            if (items == null || items.Length == 0)
                return;
            Alive.Add(new KeepAliveEntry { Owner = owner, Items = items });
        }

        /// <summary>回收已销毁属主的登记项。节点树重建后调一次即可。</summary>
        public static void PruneKeepAlive()
        {
            for (var i = Alive.Count - 1; i >= 0; i--)
            {
                if (!UiFinder.IsAlive(Alive[i].Owner))
                    Alive.RemoveAt(i);
            }
        }

        /// <summary>当前保活的登记条数（排障用）。</summary>
        public static int KeepAliveCount
        {
            get { return Alive.Count; }
        }
    }
}
