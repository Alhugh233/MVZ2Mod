using System;
using System.Text;
using Il2CppInterop.Runtime;
using MVZ2.Archives;
using PVZEngine.Tools.Unity;
using UnityEngine;
using UnityEngine.UI;

namespace DSHCore.Ui
{
    /// <summary>
    /// **对话档案样式的条目行**（<c>Toggle</c> + 文字）—— 想在自己的列表里排出
    /// 「和档案页一模一样的一行」，就用它。
    ///
    /// <para><b>为什么要从档案页克隆而不是自己搭：</b>行高、勾选框、悬停/按下视觉、文字字号
    /// 全在 prefab 里，自己 <c>AddComponent</c> 拿不到那些 sprite（这是本库的第一原则）。
    /// 档案索引页左栏（<c>Center/Filter/Contents/Tags</c>）里的 <c>ElementList</c>
    /// 正好就是这个部件的出处。</para>
    ///
    /// <para><b>行从哪来</b>：<c>ElementList._template</c>（公开属性）。它指向的那份节点
    /// 是 prefab 里的**序列化子节点**，所以克隆体上已经重映射到自己的那份，拿到即可用
    /// —— 不需要让那个列表自己 <c>updateList</c> 去生成行（本库只借它的素材，
    /// 行的排布交给我们自己的 <see cref="UiList"/>）。</para>
    ///
    /// <para><b>勾选 = 选中</b>：<c>ArchiveTagItem</c> 是复选框行，所以
    /// 「列表里当前选中哪一条」天然就是勾选状态（单选列表自己把别的行勾掉）。
    /// 点击是**行自己的 Toggle 在处理**（原生视觉 + 原生行为），
    /// 我们只订阅 <c>OnValueChanged</c> 做单选归一。</para>
    /// </summary>
    public static class UiTagRow
    {
        /// <summary>条目模板的相对路径（档案索引页左栏里那个列表）。</summary>
        private const string TagsPath = "Center/Filter/Contents/Tags";

        private static GameObject _template;

        // ------------------------------------------------------------------
        // 模板
        // ------------------------------------------------------------------

        /// <summary>
        /// 取（并缓存）档案条目模板。**必须在清空左栏之前调**（模板就长在那棵树里）。
        ///
        /// <para><paramref name="searchRoot"/> 是优先搜索的根（一般传**已克隆的页面壳**：
        /// 它此时是激活的，里面的 <c>ElementList</c> 已经把自己的模板引用补齐了）。
        /// 传 null 或找不到时会退回场景里的档案页。</para>
        ///
        /// <para>返回 null 表示这台机器/这个版本里拿不到该素材 —— 调用方应当降级
        /// （<see cref="UiList.AddButton"/> 永远可用），而不是崩。</para>
        /// </summary>
        public static GameObject Template(Transform searchRoot = null)
        {
            if (UiFinder.IsAlive(_template))
                return _template;

            var source = FindSource(searchRoot);
            if (!UiFinder.IsAlive(source))
            {
                UiLog.WarnOnce("tagrow-tpl",
                    "拿不到「对话档案」的条目素材（要先进过一次档案页/主菜单）——"
                    + " 列表会退回原生按钮行。");
                return null;
            }

            // 不挂父节点、不激活：它只是个「原件」，每次造行时 Instantiate 它。
            _template = UiClone.Instantiate(source, null, "DSH_TagRowTemplate", false);
            if (!UiFinder.IsAlive(_template))
            {
                UiLog.WarnOnce("tagrow-tpl2", "条目素材克隆失败。");
                return null;
            }
            UiLog.Info("档案条目素材已缓存：" + UiFinder.PathOf(source.transform));
            return _template;
        }

        /// <summary>丢掉缓存的模板（自测收尾 / 场景切换后用）。</summary>
        public static void Forget()
        {
            UiKit.Kill(_template, true);
            _template = null;
        }

        private static GameObject FindSource(Transform searchRoot)
        {
            // ① 调用方给的根（已克隆的页面壳）里的那个列表
            var fromClone = FindRowTemplate(searchRoot);
            if (UiFinder.IsAlive(fromClone))
                return fromClone;

            // ② 场景里的档案页
            var archive = AddonPages.FindShellSource(AddonShellSource.ArchiveIndex);
            if (!UiFinder.IsAlive(archive))
                return null;

            var tags = UiKit.Path(archive, TagsPath);
            var fromArchive = FindRowTemplate(UiFinder.IsAlive(tags) ? tags.transform : archive.transform);
            if (UiFinder.IsAlive(fromArchive))
                return fromArchive;

            return UiFinder.IsAlive(tags) ? FindTagItem(tags) : null;
        }

        private static GameObject FindRowTemplate(Transform root)
        {
            if (root == null)
                return null;
            try
            {
                var list = UiFinder.FindChildComponent<ElementList>(root);
                if (list == null)
                    return null;
                var tpl = list._template;
                if (UiFinder.IsAlive(tpl) && tpl.GetComponent<ArchiveTagItem>() != null)
                    return tpl;
                // 模板字段没就绪（列表还没 Awake）→ 退到「找模板的子节点」。
                var host = list.ListRoot;
                if (host != null && host != root)
                {
                    var byList = FindTagItem(host.gameObject, root);
                    if (UiFinder.IsAlive(byList))
                        return byList;
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("tagrow-find", "找档案条目模板失败：" + e.GetType().Name);
            }
            return null;
        }

        private static GameObject FindTagItem(GameObject root, Transform exclude = null)
        {
            if (!UiFinder.IsAlive(root))
                return null;
            try
            {
                var items = root.GetComponentsInChildren<ArchiveTagItem>(true);
                if (items == null)
                    return null;
                for (var i = 0; i < items.Length; i++)
                {
                    var item = items[i];
                    if (item == null)
                        continue;
                    var go = item.gameObject;
                    if (!UiFinder.IsAlive(go))
                        continue;
                    if (exclude != null && go.transform.IsChildOf(exclude))
                        continue;
                    return go;
                }
            }
            catch (Exception) { }
            return null;
        }

        // ------------------------------------------------------------------
        // 造行 / 改行
        // ------------------------------------------------------------------

        /// <summary>
        /// 造一行档案样式条目。<paramref name="parent"/> 一般传
        /// <c>UiList.Content</c>（列表自己会负责挂载与排版，也可以先造好再
        /// <see cref="UiList.Add"/>）。
        /// </summary>
        public static GameObject Create(Transform parent, string name, string label, bool isOn,
            Action<bool> onChanged = null)
        {
            var tpl = Template(parent);
            if (!UiFinder.IsAlive(tpl))
                return null;

            var row = UiClone.Instantiate(tpl, parent, name);
            if (!UiFinder.IsAlive(row))
                return null;

            // 档案页在**另一个 Canvas** 下：世界缩放会跟着克隆过来，
            // 不归 1 的话行高跟我们自己的布局组对不上（DSHCore 的既有教训）。
            try { row.transform.localScale = Vector3.one; }
            catch (Exception) { }

            // 行素材自己没有 LayoutElement（原版靠固定的父容器给它尺寸），
            // 塞进我们的纵向布局组前必须给个高度契约，否则会被算成 0 高。
            if (row.GetComponent<LayoutElement>() == null && OwnHeightOf(row) <= 1f)
                UiKit.Fit(row, 0f, UiKit.RowHeight, 1f);

            RestoreVisuals(row);
            Set(row, label, isOn);

            if (onChanged != null)
            {
                try
                {
                    var item = row.GetComponent<ArchiveTagItem>();
                    if (item != null)
                    {
                        var managed = new Action<ArchiveTagItem, bool>((it, value) =>
                        {
                            try { onChanged(value); }
                            catch (Exception) { }
                        });
                        var il2cpp = DelegateSupport
                            .ConvertDelegate<Il2CppSystem.Action<ArchiveTagItem, bool>>(managed);
                        UiClone.KeepAlive(row, managed, il2cpp);
                        item.OnValueChanged = il2cpp;
                    }
                }
                catch (Exception e)
                {
                    UiLog.Warn("挂档案条目回调失败：" + e.GetType().Name + ": " + e.Message);
                }
            }

            return row;
        }

        /// <summary>
        /// 还原行里的「整行色块」。
        ///
        /// <para><b>为什么必须做</b>：档案条目在 prefab 里是这样画的 ——
        /// <c>Checkmark</c> 是**无 sprite 的纯色 Image**（绿，铺满整行 = 勾选状态），
        /// <c>Hover</c> 也是无 sprite 的纯色 Image（白，铺满整行 = 悬停），
        /// 两者的可见性靠 <c>Toggle</c> / <c>Selectable</c> 写 <c>CanvasRenderer</c> 的 alpha，
        /// 而 <c>Image.color.a</c> 是**总开关**。</para>
        ///
        /// <para>本库整备页面壳时会调 <see cref="UiKit.ClearEmptySpriteAlpha"/>
        /// （把所有无 sprite 的 Image 清成透明，堵住「克隆出来一块白」）——
        /// 条目模板正好在那棵子树里，于是克隆出来的行**永远是透明的**：
        /// 文字还在、勾选状态对，但画面上看不出选了哪一条（实测踩过）。</para>
        ///
        /// <para>判据用「rgb 还在、alpha 被清」的指纹还原（清 alpha 是保留 rgb 的），
        /// 纯黑+透明的不动。</para>
        /// </summary>
        private static int RestoreVisuals(GameObject row)
        {
            if (!UiFinder.IsAlive(row))
                return 0;
            var n = 0;
            try
            {
                var images = row.GetComponentsInChildren<Image>(true);
                if (images == null)
                    return 0;
                for (var i = 0; i < images.Length; i++)
                {
                    var img = images[i];
                    if (img == null || img.sprite != null)
                        continue;
                    var c = img.color;
                    if (c.a > 0.001f)
                        continue;
                    if (c.r + c.g + c.b <= 0.001f)
                        continue;
                    img.color = new Color(c.r, c.g, c.b, 1f);
                    n++;
                }
            }
            catch (Exception e)
            {
                UiLog.WarnOnce("tagrow-visual", "还原条目行色块失败：" + e.GetType().Name);
            }
            return n;
        }

        /// <summary>
        /// 写一行的文案与勾选状态。勾选用 <c>SetIsOnWithoutNotify</c> ——
        /// 不会反过来触发 <c>OnValueChanged</c>（否则单选归一那一步会自己递归）。
        /// </summary>
        public static bool Set(GameObject row, string label, bool isOn)
        {
            var item = ItemOf(row);
            if (item == null)
                return false;
            try
            {
                var text = item.tagText;
                if (text != null)
                    text.text = label ?? string.Empty;
            }
            catch (Exception) { }
            try
            {
                var toggle = item.toggle;
                if (toggle != null)
                    toggle.SetIsOnWithoutNotify(isOn);
            }
            catch (Exception) { }
            return true;
        }

        /// <summary>当前勾选状态（拿不到行时返回 false）。</summary>
        public static bool IsOn(GameObject row)
        {
            var item = ItemOf(row);
            if (item == null)
                return false;
            try
            {
                var toggle = item.toggle;
                return toggle != null && toggle.isOn;
            }
            catch (Exception) { return false; }
        }

        /// <summary>行上的文字（排障用）。</summary>
        public static string TextOf(GameObject row)
        {
            var item = ItemOf(row);
            if (item == null)
                return null;
            try
            {
                var text = item.tagText;
                return text == null ? null : text.text;
            }
            catch (Exception) { return null; }
        }

        /// <summary>行自己的 RectTransform 高度（0 = 没有自己的尺寸，得由布局组给）。</summary>
        private static float OwnHeightOf(GameObject go)
        {
            try
            {
                var rt = go.transform.TryCast<RectTransform>();
                return rt == null ? 0f : rt.rect.height;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        private static ArchiveTagItem ItemOf(GameObject row)
        {
            if (!UiFinder.IsAlive(row))
                return null;
            try
            {
                var item = row.GetComponent<ArchiveTagItem>();
                if (item != null)
                    return item;
                return row.GetComponentInChildren<ArchiveTagItem>(true);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>一行是什么样（自测报告用）。</summary>
        public static string Describe(GameObject row)
        {
            var sb = new StringBuilder(120);
            sb.Append(UiKit.Describe(row));
            sb.Append("  勾选=").Append(IsOn(row) ? 1 : 0);
            sb.Append("  文字=\"").Append(UiKit.Trim(TextOf(row), 24)).Append('"');
            return sb.ToString();
        }
    }
}
