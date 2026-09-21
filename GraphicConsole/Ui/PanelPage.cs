using System;
using System.Collections.Generic;
using PVZEngine;
using TMPro;
using UnityEngine;

namespace GraphicConsole
{
    /// <summary>面板里的一页。页面自己负责在 <paramref name="content"/> 里搭出内容。</summary>
    internal interface IPanelPage
    {
        /// <summary>页签上显示的名字。</summary>
        string Title { get; }
        /// <summary>搭页面。每次切到该页都会重新调用（先前的子节点已被清空）。</summary>
        void Build(PanelShell shell, Transform content);
        /// <summary>把游戏当前状态同步到控件上（打开时 / 切页时 / 每帧节流调用）。</summary>
        void Refresh();
        /// <summary>离开该页（切走或关闭面板）时调用。</summary>
        void OnLeave();
    }

    /// <summary>
    /// 页面基类：提供「造格子 / 造列表」和「UI ↔ 游戏状态双向同步」两件样板活。
    ///
    /// 同步为什么用**轮询**而不是订阅控件事件：
    ///   ① 克隆出来的控件事件要用 <c>DelegateSupport.ConvertDelegate</c> 造委托并自己持有
    ///      防 GC，每页每次重建都要重新挂一遍；
    ///   ② 轮询能自然处理「游戏状态被别的途径改了」（例如游戏自己推进波数），事件不能；
    ///   ③ 60fps 轮询体感就是即时的。
    ///
    /// 同步的核心难点是**区分「UI 变了」和「游戏状态变了」**：用 <c>lastWritten</c> 记住我们
    /// 上次写进 UI 的值 ——
    ///   - UI 值 == lastWritten → UI 没被动过，是游戏状态变了 → 把新值写进 UI；
    ///   - UI 值 != lastWritten → 用户刚改了 UI → 把 UI 值应用到游戏。
    /// </summary>
    internal abstract class PanelPageBase : IPanelPage
    {
        public abstract string Title { get; }

        protected PanelShell Shell;
        protected Transform Content;

        private readonly List<WidgetCell> cells = new List<WidgetCell>();
        private readonly Dictionary<string, bool> toggleWritten = new Dictionary<string, bool>();
        private readonly Dictionary<string, int> dropdownWritten = new Dictionary<string, int>();
        private readonly Dictionary<string, string> numberWritten = new Dictionary<string, string>();
        private readonly HashSet<string> warned = new HashSet<string>();

        public void Build(PanelShell shell, Transform content)
        {
            Shell = shell;
            Content = content;
            cells.Clear();
            toggleWritten.Clear();
            dropdownWritten.Clear();
            numberWritten.Clear();
            warned.Clear();
            try
            {
                OnBuild();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError("[Page] 搭建「" + Title + "」失败：" + e);
            }
        }

        public void Refresh()
        {
            try
            {
                OnRefresh();
            }
            catch (Exception e)
            {
                if (warned.Add("refresh"))
                    Plugin.Log?.LogError("[Page] 刷新「" + Title + "」失败：" + e);
            }
        }

        public void OnLeave()
        {
            try
            {
                OnLeaveCore();
            }
            catch (Exception)
            {
            }
        }

        protected abstract void OnBuild();
        protected abstract void OnRefresh();
        protected virtual void OnLeaveCore() { }

        #region 造控件
        /// <summary>造一个格子（宽度默认按类型给）。</summary>
        protected WidgetCell NewCell(Transform parent, string id, CellKind kind, string label, float width = -1f)
        {
            return NewCell(parent, id, kind, label, width, -1f, false);
        }

        /// <summary>造一个格子（可指定标签最大宽度、以及控件是否吸收剩余宽度）。</summary>
        protected WidgetCell NewCell(Transform parent, string id, CellKind kind, string label, float width,
            float labelMaxWidth, bool flexibleControl)
        {
            if (width < 0f)
                width = DefaultWidth(kind);
            var cell = UiFactory.CreateCell(parent, id, kind, label, width, labelMaxWidth, flexibleControl);
            if (cell != null)
                cells.Add(cell);
            return cell;
        }

        private static float DefaultWidth(CellKind kind)
        {
            switch (kind)
            {
                case CellKind.Toggle: return GcLayout.ToggleWidth;
                case CellKind.Dropdown: return GcLayout.LabelMaxWidth + GcLayout.DropdownWidth;
                case CellKind.Number: return GcLayout.LabelMaxWidth + GcLayout.InputWidth;
                case CellKind.Button: return GcLayout.ButtonWidth;
                default: return GcLayout.ToggleWidth;
            }
        }

        /// <summary>造一整行（内部用横向布局把若干格子排开）。固定宽度、按列左对齐。</summary>
        protected Transform NewRow(Transform parent, string name, float y, float height, float spacing)
        {
            var go = UiFactory.NewRect(parent, name);
            if (go == null)
                return null;
            var rt = UiFactory.RectOf(go);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, height);
            rt.anchoredPosition = new Vector2(0f, -y);
            var group = go.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            group.spacing = spacing;
            group.childAlignment = TextAnchor.MiddleLeft;
            group.childControlWidth = true;
            group.childControlHeight = true;
            group.childForceExpandWidth = false;
            group.childForceExpandHeight = false;
            return go.transform;
        }

        /// <summary>造一个纯文本提示（可换行）。</summary>
        protected TextMeshProUGUI NewHint(Transform parent, float y, float height, string text, Color? color = null)
        {
            var label = UiFactory.MakeLabel(parent, text, GcLayout.SmallFontSize, true, false);
            if (label == null)
                return null;
            var rt = UiFactory.RectOf(label);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, height);
            rt.anchoredPosition = new Vector2(0f, -y);
            if (color.HasValue)
                label.color = color.Value;
            return label;
        }
        #endregion

        #region 轮询同步
        protected void SyncToggle(WidgetCell cell, bool current, Action<bool> apply)
        {
            if (cell == null)
                return;
            bool last;
            var has = toggleWritten.TryGetValue(cell.Id, out last);
            var shown = UiFactory.GetToggleValue(cell);
            if (!has)
            {
                toggleWritten[cell.Id] = current;
                UiFactory.SetToggleValue(cell, current);
                return;
            }
            if (shown == last)
            {
                if (current != last)
                {
                    toggleWritten[cell.Id] = current;
                    UiFactory.SetToggleValue(cell, current);
                }
                return;
            }
            toggleWritten[cell.Id] = shown;
            try
            {
                apply(shown);
            }
            catch (Exception e)
            {
                if (warned.Add("apply-toggle-" + cell.Id))
                    Plugin.Log?.LogError("[Page] 开关「" + cell.Id + "」应用失败：" + e.Message);
            }
        }

        protected void SyncDropdown(WidgetCell cell, int current, Action<int> apply)
        {
            if (cell == null)
                return;
            int last;
            var has = dropdownWritten.TryGetValue(cell.Id, out last);
            var shown = UiFactory.GetDropdownValue(cell);
            if (shown < 0)
                return;
            if (!has)
            {
                dropdownWritten[cell.Id] = current;
                UiFactory.SetDropdownValue(cell, current);
                return;
            }
            if (shown == last)
            {
                if (current != last)
                {
                    dropdownWritten[cell.Id] = current;
                    UiFactory.SetDropdownValue(cell, current);
                }
                return;
            }
            dropdownWritten[cell.Id] = shown;
            try
            {
                apply(shown);
            }
            catch (Exception e)
            {
                if (warned.Add("apply-dropdown-" + cell.Id))
                    Plugin.Log?.LogError("[Page] 下拉「" + cell.Id + "」应用失败：" + e.Message);
            }
        }

        /// <summary>
        /// 数值输入框：只在**失去焦点**时应用（否则每敲一个数字就会写一次游戏状态）。
        /// 关闭面板 / 切页时由 <see cref="ApplyAllNumbers"/> 兜底。
        /// </summary>
        protected void SyncNumber(WidgetCell cell, string current, Action<long> apply)
        {
            if (cell == null || cell.Input == null)
                return;
            string last;
            var has = numberWritten.TryGetValue(cell.Id, out last);
            var shown = UiFactory.GetInputText(cell);
            if (!has)
            {
                numberWritten[cell.Id] = current;
                UiFactory.SetInputText(cell, current);
                return;
            }
            if (shown == last)
            {
                if (current != last)
                {
                    numberWritten[cell.Id] = current;
                    UiFactory.SetInputText(cell, current);
                }
                return;
            }
            if (IsInputFocused(cell))
                return;
            numberWritten[cell.Id] = shown;
            if (!TryParseLong(shown, out var value))
                return;
            try
            {
                apply(value);
            }
            catch (Exception e)
            {
                if (warned.Add("apply-number-" + cell.Id))
                    Plugin.Log?.LogError("[Page] 输入框「" + cell.Id + "」应用失败：" + e.Message);
            }
        }

        /// <summary>
        /// 把**所有**数值输入框的当前文本提交一次。用在关闭面板 / 切页时 ——
        /// 那时输入框不会失焦，靠「失焦才应用」的规则会漏掉最后一次编辑。
        /// </summary>
        protected void ApplyAllNumbers(Action<string, long> apply)
        {
            if (apply == null)
                return;
            for (var i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                if (cell == null || cell.Kind != CellKind.Number || cell.Input == null)
                    continue;
                var shown = UiFactory.GetInputText(cell);
                if (!TryParseLong(shown, out var value))
                    continue;
                numberWritten[cell.Id] = shown;
                try
                {
                    apply(cell.Id, value);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning("[Page] 关闭前提交「" + cell.Id + "」失败：" + e.Message);
                }
            }
        }

        private static bool IsInputFocused(WidgetCell cell)
        {
            try
            {
                return cell.Input.isFocused;
            }
            catch (Exception)
            {
                return false;
            }
        }

        protected static bool TryParseLong(string text, out long value)
        {
            value = 0L;
            if (string.IsNullOrEmpty(text))
                return false;
            text = text.Trim();
            if (text.Length == 0 || text == "-")
                return false;
            return long.TryParse(text, out value);
        }

        protected static bool TryParseFloat(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(text))
                return false;
            text = text.Trim();
            if (text.Length == 0 || text == "-" || text == ".")
                return false;
            return float.TryParse(text, out value);
        }

        protected static string Fmt(float value)
        {
            return Math.Abs(value - Math.Round(value)) < 0.0001f
                ? ((long)Math.Round(value)).ToString()
                : value.ToString("0.###");
        }

        protected static NamespaceID SafeAt(List<NamespaceID> list, int index)
        {
            if (list == null || index < 0 || index >= list.Count)
                return null;
            return list[index];
        }
        #endregion
    }
}
