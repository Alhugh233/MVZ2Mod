using System;
using System.Collections.Generic;
using DSHCore.Ui;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Loadout
{
    /// <summary>
    /// 一个**浮层选择窗口**（目前只有制品页在用）。
    ///
    /// <para>挂在与补全浮层同一个位置（<c>LoadoutPanel.DialogRoot</c> 下的 <c>ignoreLayout</c>
    /// 子节点），所以它不参与面板排版、也不会把面板顶高；位置用锚点居中，尺寸按面板比例算。</para>
    ///
    /// <para>背景是一块半透明色块（自绘 Image）—— 原版对话框素材只有「标题 + 正文 + 按钮」那一套，
    /// 直接克隆整个对话框会有两份按钮区，反而更乱。</para>
    ///
    /// <para>⚠️ 蓝图那一套**不用**这个窗口了：蓝图选卡直接克隆原版选卡面板，
    /// 见 <see cref="BlueprintPickWindow"/>。</para>
    /// </summary>
    internal sealed class UiPickWindow
    {
        private GameObject _box;
        private GameObject _content;
        private TextMeshProUGUI _title;
        private UiArtifactGrid _artifacts;
        private Action _onOk;
        private bool _built;

        public bool IsOpen { get { return _built && UiFinder.IsAlive(_box) && _box.activeSelf; } }

        /// <summary>窗口节点还在不在（面板重建后会被销毁）。</summary>
        public bool IsValid { get { return _built && UiFinder.IsAlive(_box); } }

        private static readonly List<UiPickWindow> All = new List<UiPickWindow>();

        public static void TickAll()
        {
            for (var i = 0; i < All.Count; i++)
            {
                try { All[i].Tick(); }
                catch (Exception) { }
            }
        }

        public static void Forget()
        {
            for (var i = 0; i < All.Count; i++)
                All[i]._built = false;
            All.Clear();
        }

        /// <summary>关上所有选择窗口（面板关闭时调）。</summary>
        public static void HideAll()
        {
            for (var i = 0; i < All.Count; i++)
            {
                try { All[i].Close(); }
                catch (Exception) { }
            }
        }

        public static UiPickWindow Create(string name)
        {
            var w = new UiPickWindow();
            All.Add(w);
            w.Build(name);
            return w;
        }

        private void Build(string name)
        {
            var root = LoadoutPanel.DialogRoot;
            if (root == null)
                return;
            _box = UiHost.CreateNode(name, root);
            if (!UiFinder.IsAlive(_box))
                return;
            try
            {
                var rt = _box.transform.TryCast<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = new Vector2(0.5f, 0.5f);
                    rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = Vector2.zero;
                    rt.sizeDelta = new Vector2(root.rect.width * 0.9f, root.rect.height * 0.82f);
                }
                var le = _box.AddComponent<LayoutElement>();
                if (le != null)
                    le.ignoreLayout = true;
                var img = _box.AddComponent<Image>();
                if (img != null)
                    img.color = new Color(0.06f, 0.05f, 0.09f, 0.97f);

                _content = UiContainer.Instantiate(_box.transform, "Content", true);
                if (UiFinder.IsAlive(_content))
                    UiContainer.FillParent(_content, _box.transform);

                _title = UiKit.Label(_content.transform, "Title", "选择");
                if (_title != null)
                {
                    var tle = _title.gameObject.GetComponent<LayoutElement>();
                    if (tle == null)
                        tle = _title.gameObject.AddComponent<LayoutElement>();
                    if (tle != null)
                        tle.preferredHeight = UiKit.RowHeight;
                }

                _artifacts = UiArtifactGrid.Create(_content.transform, "Artifacts", UiList.SuggestHeight(0.42f));

                var buttons = UiGridRow.Create("PickButtons", equalize: false);
                if (buttons != null)
                {
                    var ok = buttons.AddOptionRow("B_Ok", UiOptionKind.Button);
                    if (ok != null)
                        ok.SetButton("确定", Confirm);
                    var cancel = buttons.AddOptionRow("B_Cancel", UiOptionKind.Button);
                    if (cancel != null)
                        cancel.SetButton("取消", Close);
                    var node = buttons.Finish();
                    if (UiFinder.IsAlive(node))
                        node.transform.SetParent(_content.transform, false);
                }
                _built = true;
                _box.SetActive(false);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("pickwin", "[Pick] 造选择窗口失败：" + e.GetType().Name + ": " + e.Message);
            }
        }

        public void OpenArtifacts(string title, int slotCount, Action onOk)
        {
            if (!_built)
                return;
            _onOk = onOk;
            if (_title != null)
            {
                try { _title.text = title; } catch (Exception) { }
            }
            if (_artifacts != null)
                _artifacts.SetSlots(slotCount);
            try
            {
                _box.SetActive(true);
                _box.transform.SetAsLastSibling();
                LoadoutPanel.RebuildLayout();
            }
            catch (Exception) { }
        }

        public void Close()
        {
            try
            {
                if (UiFinder.IsAlive(_box))
                    _box.SetActive(false);
            }
            catch (Exception) { }
        }

        private void Confirm()
        {
            try
            {
                _onOk?.Invoke();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("[Pick] 确认失败：" + e.GetType().Name);
            }
        }

        /// <summary>选中的制品槽位（-1 = 没选）。</summary>
        public int ArtifactSlot
        {
            get { return _artifacts == null ? -1 : _artifacts.SelectedSlot; }
        }

        public void Tick()
        {
            if (!IsOpen)
                return;
            if (_artifacts != null && _artifacts.Root != null)
                _artifacts.Tick();
        }
    }
}
