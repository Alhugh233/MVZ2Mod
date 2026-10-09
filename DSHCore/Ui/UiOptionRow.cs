using System;
using Il2CppInterop.Runtime;
using MVZ2.UI;
using UnityEngine;

namespace DSHCore.Ui
{
    /// <summary>设置项行的类型。</summary>
    public enum UiOptionKind
    {
        Toggle,
        Slider,

        /// <summary>
        /// 下拉框。**碍于技术问题，该组件无法正常工作，请自行寻找替代方案，
        /// 如果你对修复该组件有任何建议，请联系 QoZnoS[https://github.com/QoZnoS]。**
        /// 详见 <see cref="UiOptionRow.SetDropdown"/>。
        /// </summary>
        Dropdown,

        Button,
    }

    /// <summary>
    /// 一个**原版设置项行**：复选框 / 滑条 / 下拉框 / 单按钮。
    ///
    /// <para><b>为什么不能单独克隆 <c>LabeledToggle</c> / <c>TextSlider</c> / <c>LabeledDropdown</c>：</b>
    /// 这四个控件的尺寸契约是**由 <c>OptionWidgets</c> 这个容器提供的** ——
    /// 容器自己的 <c>LayoutGroup</c> 是 <c>childControlWidth/Height = 1</c> 且
    /// <c>forceExpand = 0</c>（子节点按各自 preferred 尺寸）；而外层容器（比如对话框的 <c>Root</c>）
    /// 也是 <c>childControlHeight = 1</c>，于是会来问本行的 preferred 高度。
    /// 单独克隆某个控件时这个链路就断了：<c>LabeledToggle</c> 甚至**没有自己的
    /// <c>LayoutElement</c>**，于是尺寸完全看运气（实测表现为"复选框过大、内部方块位置偏上"，
    /// 滑条则根本不显示）。</para>
    ///
    /// <para>另外 <c>OptionWidgets</c> 上**自带一个已经接好锚点的 <c>TooltipHandler</c>**
    /// （锚点在行的顶边中点，<c>TooltipAnchor.pivot = (0.5, 0)</c>）—— 提示因此出现在行的上方
    /// 而不是压在控件上。<see cref="SetTooltip"/> 复用的就是它。</para>
    ///
    /// <para><b>滑条的文字是「标签：值」并在拖动时实时刷新</b> —— 这是原版的行为
    /// （原版由 <c>OptionsDialogController</c> 在 <c>OnSliderValueChanged</c> 里调
    /// <c>OptionWidgets.SetSliderText</c>）。在本类里这一步是自动的，
    /// 要改成别的格式用 <see cref="SetSliderValueFormat"/>。</para>
    ///
    /// <para><b>用法：</b></para>
    /// <code>
    /// var row = UiOptionRow.Create(root, "MyToggle");
    /// row.SetToggle("无限能量", true, v => { /* … */ });
    /// row.SetTooltip("无限能量", "开启后能量恒满。");
    ///
    /// var slide = UiOptionRow.Create(root, "MySlider", UiOptionKind.Slider);
    /// slide.SetSlider("音量", 0f, 1f, false, 0.5f);    // 文字自动变「音量：0.5」
    /// </code>
    /// </summary>
    public sealed class UiOptionRow
    {
        private readonly GameObject _root;
        private readonly MVZ2.UI.OptionWidgets _widgets;

        private UiOptionKind _kind = UiOptionKind.Toggle;

        /// <summary>滑条的标签文本（不含值）—— 原版每次改值都会重新拼一次整句。</summary>
        private string _sliderLabel;

        /// <summary>滑条值的格式化器（默认 <c>0.##</c>）。</summary>
        private Func<float, string> _sliderFormat;

        private UiOptionRow(GameObject root, MVZ2.UI.OptionWidgets widgets)
        {
            _root = root;
            _widgets = widgets;
        }

        /// <summary>行根节点（拿去挂到父节点上、或设 <c>LayoutElement</c>）。</summary>
        public GameObject Root
        {
            get { return UiFinder.IsAlive(_root) ? _root : null; }
        }

        /// <summary>原版的四合一容器组件。需要它别的能力时可以直接用。</summary>
        public MVZ2.UI.OptionWidgets Widgets
        {
            get { return UiFinder.IsAlive(_widgets) ? _widgets : null; }
        }

        /// <summary>当前的滑条控件（<see cref="UiOptionKind.Slider"/> 时才有意义）。</summary>
        public UnityEngine.UI.Slider Slider
        {
            get { return IsValid && _widgets.Slider != null ? _widgets.Slider.Slider : null; }
        }

        /// <summary>当前的下拉框控件（<see cref="UiOptionKind.Dropdown"/> 时才有意义）。</summary>
        public TMPro.TMP_Dropdown Dropdown
        {
            get { return IsValid && _widgets.Dropdown != null ? _widgets.Dropdown.Dropdown : null; }
        }

        public UiOptionKind Kind
        {
            get { return _kind; }
        }

        public bool IsValid
        {
            get { return UiFinder.IsAlive(_root) && UiFinder.IsAlive(_widgets); }
        }

        /// <summary>
        /// 克隆一个设置项行。<paramref name="kind"/> 默认为复选框 ——
        /// **必须给定类型**，否则模板里那四个控件会一起显形。
        /// </summary>
        public static UiOptionRow Create(Transform parent, string name = null, UiOptionKind kind = UiOptionKind.Toggle)
        {
            var go = UiSkin.Clone(UiSkinPart.OptionRow, parent, name);
            if (!UiFinder.IsAlive(go))
            {
                UiLog.WarnOnce("optionrow-miss",
                    "拿不到设置项行素材（OptionWidgets）—— 它来自主菜单的设置界面，"
                    + "进主菜单后才会就绪。");
                return null;
            }

            var widgets = go.GetComponent<MVZ2.UI.OptionWidgets>();
            if (widgets == null)
            {
                UiLog.WarnOnce("optionrow-nocomp", "设置项行克隆体上没有 OptionWidgets 组件。");
                return null;
            }

            var row = new UiOptionRow(go, widgets);
            row.SetKind(kind);
            return row;
        }

        // ------------------------------------------------------------------
        // 类型与文案
        // ------------------------------------------------------------------

        /// <summary>
        /// 激活其中一个控件、关掉另外三个。
        ///
        /// <para>这一步**不能省**：模板里四个控件全是激活的（原版只在
        /// <c>UpdateWidgets</c> 被调用时才关掉其余三个）。</para>
        /// </summary>
        public UiOptionRow SetKind(UiOptionKind kind)
        {
            if (!IsValid)
                return this;
            try
            {
                _kind = kind;
                SetChildActive(_widgets.Toggle == null ? null : _widgets.Toggle.gameObject, kind == UiOptionKind.Toggle);
                SetChildActive(_widgets.Slider == null ? null : _widgets.Slider.gameObject, kind == UiOptionKind.Slider);
                SetChildActive(_widgets.Dropdown == null ? null : _widgets.Dropdown.gameObject, kind == UiOptionKind.Dropdown);
                SetChildActive(_widgets.TextButton == null ? null : _widgets.TextButton.gameObject, kind == UiOptionKind.Button);
            }
            catch (Exception e)
            {
                UiLog.Warn("设置行类型失败：" + e.GetType().Name + ": " + e.Message);
            }
            return this;
        }

        private static void SetChildActive(GameObject go, bool active)
        {
            if (!UiFinder.IsAlive(go))
                return;
            try
            {
                if (go.activeSelf != active)
                    go.SetActive(active);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// 设置行的标签文字。
        ///
        /// <para>直接写各自的 <c>Text.text</c>，与原版 <c>UpdateToggle</c> / <c>UpdateSlider</c> /
        /// <c>UpdateDropdown</c> / <c>UpdateButton</c> 完全一致 —— 这四个控件的字号是配好自动缩放的，
        /// 所以这里**不改字号**（<see cref="UiClone.SetLabel"/> 传 0 就是"只改文字"）。</para>
        ///
        /// <para>滑条是个例外：它的标签是「标签：值」，所以这里只记下标签，整句由
        /// <see cref="UpdateSliderText"/> 拼 —— 否则下一次改值会把值那一半冲掉。</para>
        ///
        /// <para>标签会过一遍 <see cref="UiLocale.Get"/>（子 mod 的一切控件行文案
        /// 因此只需登记译文，不用逐个调用点写取词）。</para>
        /// </summary>
        public UiOptionRow SetLabel(string text)
        {
            if (!IsValid)
                return this;
            text = UiLocale.Get(text);
            try
            {
                switch (_kind)
                {
                    case UiOptionKind.Toggle:
                        if (_widgets.Toggle != null)
                            _widgets.Toggle.Text.text = text;
                        break;
                    case UiOptionKind.Slider:
                        _sliderLabel = text;
                        UpdateSliderText(CurrentSliderValue());
                        break;
                    case UiOptionKind.Dropdown:
                        if (_widgets.Dropdown != null)
                            _widgets.Dropdown.Text.text = text;
                        break;
                    case UiOptionKind.Button:
                        if (_widgets.TextButton != null)
                            _widgets.TextButton.Text.text = text;
                        break;
                }
            }
            catch (Exception e)
            {
                UiLog.Warn("设置行标签失败：" + e.GetType().Name);
            }
            return this;
        }

        /// <summary>
        /// 让这一行铺满父节点的宽度。
        ///
        /// <para>原版行自身的 <c>sizeDelta</c> 是 <c>300×40</c>，而对话框 <c>Root</c> 的
        /// <c>VerticalLayoutGroup</c> 是 <c>forceExpandWidth = 0</c> —— 不铺满的话行会保持约 300 宽
        /// 并靠左排。做法是给行加一个**只设 <c>flexibleWidth</c>** 的 <c>LayoutElement</c>：
        /// 其余字段留 <c>-1</c>（= 忽略），所以不会影响它自己的高度。</para>
        /// </summary>
        public UiOptionRow SetStretch(bool stretch = true)
        {
            if (!IsValid)
                return this;
            try
            {
                var le = _root.GetComponent<UnityEngine.UI.LayoutElement>();
                if (le == null)
                    le = _root.AddComponent<UnityEngine.UI.LayoutElement>();
                if (le != null)
                    le.flexibleWidth = stretch ? 1f : -1f;
            }
            catch (Exception e)
            {
                UiLog.Warn("设置行铺满失败：" + e.GetType().Name);
            }
            return this;
        }

        /// <summary>
        /// 挂悬停提示。
        ///
        /// <para>用的是本行**自带的** <c>TooltipHandler</c> 与它已接好的锚点
        /// （行顶边中点 + <c>pivot (0.5, 0)</c>），所以提示出现在行的上方，不会压住控件。</para>
        ///
        /// <para>标题与正文都会过一遍 <see cref="UiLocale.Get"/>（多行 tooltip 里的
        /// <c>\n</c> 不受影响）。</para>
        /// </summary>
        public UiOptionRow SetTooltip(string title, string text)
        {
            if (!IsValid || string.IsNullOrEmpty(text))
                return this;
            try
            {
                UiTooltip.AttachExisting(_root, UiLocale.Get(title), UiLocale.Get(text));
            }
            catch (Exception e)
            {
                UiLog.Warn("设置行提示失败：" + e.GetType().Name);
            }
            return this;
        }

        // ------------------------------------------------------------------
        // 各类型
        // ------------------------------------------------------------------

        public UiOptionRow SetToggle(string label, bool value, Action<bool> onChanged = null)
        {
            SetKind(UiOptionKind.Toggle);
            SetLabel(label);
            if (!IsValid)
                return this;

            try
            {
                if (_widgets.Toggle != null)
                    _widgets.Toggle.Toggle.SetIsOnWithoutNotify(value);

                if (onChanged != null)
                {
                    var managed = new System.Action<MVZ2.UI.OptionWidgets, bool>((w, v) => onChanged(v));
                    var il2cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<MVZ2.UI.OptionWidgets, bool>>(managed);
                    UiClone.KeepAlive(_root, managed, il2cpp);
                    _widgets.add_OnToggleValueChanged(il2cpp);
                }
            }
            catch (Exception e)
            {
                UiLog.Warn("配置复选框行失败：" + e.GetType().Name + ": " + e.Message);
            }
            return this;
        }

        /// <summary>
        /// 配置滑条行。
        ///
        /// <para><b>标签会自动变成「标签：值」，并且在拖动的每一帧刷新</b> —— 这是原版行为：
        /// 原版由 <c>OptionsDialogController.OnMoreOptionsSliderValueChangedCallback</c> 调
        /// <c>widgets.SetSliderText(...)</c> 重写整句，而本类把这一步做在了自己的事件里，
        /// 所以直接用就会动。不想要值或想换格式用 <see cref="SetSliderValueFormat"/>。</para>
        ///
        /// <para>默认格式是 <c>0.##</c>。原版的格式来自语言条目 <c>option.name</c>
        /// （<c>"{0}：{1}"</c>），而值那一半由各设置项自己决定（例：<c>60%</c>、<c>1.5x</c>）——
        /// 所以这里把它留成可替换的委托。</para>
        /// </summary>
        /// <param name="valueFormat">值 → 字符串；传 <c>null</c> 用默认格式。</param>
        public UiOptionRow SetSlider(string label, float min, float max, bool wholeNumbers, float value,
                                     Action<float> onChanged = null, Action<float> onEnd = null,
                                     Func<float, string> valueFormat = null)
        {
            SetKind(UiOptionKind.Slider);
            _sliderLabel = label;
            _sliderFormat = valueFormat ?? DefaultSliderValueFormat;
            if (!IsValid)
                return this;

            try
            {
                var slider = _widgets.Slider == null ? null : _widgets.Slider.Slider;
                if (slider != null)
                {
                    slider.wholeNumbers = wholeNumbers;
                    slider.minValue = min;
                    slider.maxValue = max;
                    slider.SetValueWithoutNotify(value);
                }

                UpdateSliderText(value);

                // 值一变就重写标签 —— 这就是"文字跟着滑块跑"。
                var onValue = new System.Action<MVZ2.UI.OptionWidgets, float>((w, v) =>
                {
                    UpdateSliderText(v);
                    if (onChanged != null)
                        onChanged(v);
                });
                var onValueIl2Cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<MVZ2.UI.OptionWidgets, float>>(onValue);
                UiClone.KeepAlive(_root, onValue, onValueIl2Cpp);
                _widgets.add_OnSliderValueChanged(onValueIl2Cpp);

                // 原版用 SliderEndHandler 做"松手才提交"，避免拖的过程中每帧写值。
                if (onEnd != null)
                {
                    var managed = new System.Action<MVZ2.UI.OptionWidgets, float>((w, v) => onEnd(v));
                    var il2cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<MVZ2.UI.OptionWidgets, float>>(managed);
                    UiClone.KeepAlive(_root, managed, il2cpp);
                    _widgets.add_OnSliderEnd(il2cpp);
                }
            }
            catch (Exception e)
            {
                UiLog.Warn("配置滑条行失败：" + e.GetType().Name + ": " + e.Message);
            }
            return this;
        }

        /// <summary>
        /// 换滑条值的显示格式（<c>null</c> = 恢复默认 <c>0.##</c>）。
        ///
        /// <para>标签里不想要值：传 <c>v =&gt; null</c>，标签就只剩名字。</para>
        /// </summary>
        public UiOptionRow SetSliderValueFormat(Func<float, string> format)
        {
            _sliderFormat = format ?? DefaultSliderValueFormat;
            if (IsValid && _kind == UiOptionKind.Slider)
                UpdateSliderText(CurrentSliderValue());
            return this;
        }

        /// <summary>程序化地改滑条的值（同时刷新标签，但**不**触发 <c>onChanged</c>）。</summary>
        public UiOptionRow SetSliderValue(float value)
        {
            if (!IsValid || _kind != UiOptionKind.Slider)
                return this;
            try
            {
                var slider = _widgets.Slider == null ? null : _widgets.Slider.Slider;
                if (slider != null)
                    slider.SetValueWithoutNotify(value);
                UpdateSliderText(value);
            }
            catch (Exception e)
            {
                UiLog.Warn("设置滑条值失败：" + e.GetType().Name);
            }
            return this;
        }

        /// <summary>
        /// 重写滑条标签为「标签：值」。
        ///
        /// <para>它会从 il2cpp 的事件回调里被调用，所以**自己吞掉异常**（抛回去会穿进原生代码）。</para>
        /// </summary>
        private void UpdateSliderText(float value)
        {
            if (!IsValid || _kind != UiOptionKind.Slider)
                return;
            try
            {
                if (_widgets.Slider == null)
                    return;

                var format = _sliderFormat ?? DefaultSliderValueFormat;
                string valueText = null;
                try { valueText = format(value); }
                catch (Exception) { valueText = null; }

                var label = _sliderLabel ?? "";
                _widgets.Slider.Text.text = string.IsNullOrEmpty(valueText) ? label : label + "：" + valueText;
            }
            catch (Exception)
            {
            }
        }

        private float CurrentSliderValue()
        {
            try
            {
                var slider = _widgets.Slider == null ? null : _widgets.Slider.Slider;
                return slider == null ? 0f : slider.value;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        private static string DefaultSliderValueFormat(float v)
        {
            return v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 配置下拉框行。
        ///
        /// <para><b>⚠️ 碍于技术问题，该组件无法正常工作，请自行寻找替代方案，如果你对修复该组件有任何建议，请联系
        /// QoZnoS[https://github.com/QoZnoS]</b></para>
        ///
        /// <para>行本身能正常显示、能改值，但**展开出来的选项列表在本游戏里一个像素都不画**，
        /// 而它的射线照常生效 —— 用户看到的是「点开没反应、乱点却能改值」。
        /// 这一点原版设置界面的下拉框也一样（我们复现并逐一排除过多种画布/层级配法，
        /// 记录在 <c>UI_RESEARCH.md</c> §6.4-17 与 §7.6，供后来者接手）。
        /// <b>本库不提供标准替代实现。</b></para>
        ///
        /// <para>保留本方法只为两点：① 让需要「只显示当前值」的人有一个原生观感的只读行；
        /// ② 让接手修复的人能从已知状态出发。诊断用 <see cref="DescribeDropdown"/>。</para>
        /// </summary>
        /// <param name="preparePopup">是否预先对齐弹窗画布（对显示没有帮助，留作排查用）。</param>
        public UiOptionRow SetDropdown(string label, string[] options, int value, Action<int> onChanged = null,
                                       bool preparePopup = true)
        {
            SetKind(UiOptionKind.Dropdown);
            SetLabel(label);
            if (!IsValid)
                return this;

            try
            {
                if (options != null)
                    _widgets.SetDropdownOptions(ToIl2CppList(options));

                if (_widgets.Dropdown != null)
                    _widgets.Dropdown.Dropdown.SetValueWithoutNotify(value);

                if (preparePopup && _widgets.Dropdown != null)
                    UiDropdown.PreparePopup(_widgets.Dropdown.Dropdown);

                if (onChanged != null)
                {
                    var managed = new System.Action<MVZ2.UI.OptionWidgets, int>((w, v) => onChanged(v));
                    var il2cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<MVZ2.UI.OptionWidgets, int>>(managed);
                    UiClone.KeepAlive(_root, managed, il2cpp);
                    _widgets.add_OnDropdownValueChanged(il2cpp);
                }
            }
            catch (Exception e)
            {
                UiLog.Warn("配置下拉行失败：" + e.GetType().Name + ": " + e.Message);
            }
            return this;
        }

        /// <summary>
        /// 程序化地展开下拉列表（等同用户点一下）。
        ///
        /// <para>展开出来的列表是 TMP 运行时实例化的，<c>Hide()</c> 时会被销毁 ——
        /// 所以"展开 → 看公告 → 收起"这套只能写在**每帧心跳或协程**里，不能在同一个栈里连做。</para>
        /// </summary>
        public bool ShowDropdownList()
        {
            var dd = Dropdown;
            return dd != null && UiDropdown.Show(dd);
        }

        /// <summary>收起下拉列表。</summary>
        public bool HideDropdownList()
        {
            var dd = Dropdown;
            return dd != null && UiDropdown.Hide(dd);
        }

        /// <summary>把下拉框与展开列表的运行时状态打出来（排查“列表不显示”用）。</summary>
        public string DescribeDropdown()
        {
            var dd = Dropdown;
            return dd == null ? "（本行不是下拉框）" : UiDropdown.Describe(dd);
        }

        public UiOptionRow SetButton(string label, Action onClick = null)
        {
            SetKind(UiOptionKind.Button);
            SetLabel(label);
            if (!IsValid)
                return this;

            try
            {
                if (onClick != null)
                {
                    var managed = new System.Action<MVZ2.UI.OptionWidgets>(w => onClick());
                    var il2cpp = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<MVZ2.UI.OptionWidgets>>(managed);
                    UiClone.KeepAlive(_root, managed, il2cpp);
                    _widgets.add_OnButtonClicked(il2cpp);
                }
            }
            catch (Exception e)
            {
                UiLog.Warn("配置按钮行失败：" + e.GetType().Name + ": " + e.Message);
            }
            return this;
        }

        // ------------------------------------------------------------------
        // 诊断
        // ------------------------------------------------------------------

        /// <summary>把一个行的实际状态打出来（尺寸/显隐/字号/标签文案 —— 视觉问题靠这个判，不靠猜）。</summary>
        public string Describe()
        {
            if (!IsValid)
                return "（无效行）";
            try
            {
                var sb = new System.Text.StringBuilder(260);
                sb.Append("kind=").Append(_kind);
                AppendChild(sb, "toggle", _widgets.Toggle == null ? null : _widgets.Toggle.gameObject);
                AppendChild(sb, "slider", _widgets.Slider == null ? null : _widgets.Slider.gameObject);
                AppendChild(sb, "dropdown", _widgets.Dropdown == null ? null : _widgets.Dropdown.gameObject);
                AppendChild(sb, "button", _widgets.TextButton == null ? null : _widgets.TextButton.gameObject);

                // 标签文案：滑条的值是不是真的跟着走，只能看这里。
                var label = LabelText();
                if (!string.IsNullOrEmpty(label))
                    sb.Append("  文案=「").Append(label).Append('」');

                sb.Append("  tip[").Append(UiTooltip.Describe(_root)).Append(']');
                return sb.ToString();
            }
            catch (Exception e)
            {
                return "读行状态失败：" + e.GetType().Name;
            }
        }

        /// <summary>当前类型的那个控件上显示的标签文案。</summary>
        public string LabelText()
        {
            if (!IsValid)
                return null;
            try
            {
                switch (_kind)
                {
                    case UiOptionKind.Toggle:
                        return _widgets.Toggle == null ? null : _widgets.Toggle.Text.text;
                    case UiOptionKind.Slider:
                        return _widgets.Slider == null ? null : _widgets.Slider.Text.text;
                    case UiOptionKind.Dropdown:
                        return _widgets.Dropdown == null ? null : _widgets.Dropdown.Text.text;
                    case UiOptionKind.Button:
                        return _widgets.TextButton == null ? null : _widgets.TextButton.Text.text;
                }
            }
            catch (Exception e)
            {
                return "读文案失败：" + e.GetType().Name;
            }
            return null;
        }

        private static void AppendChild(System.Text.StringBuilder sb, string name, GameObject go)
        {
            sb.Append("  ").Append(name).Append('=');
            if (!UiFinder.IsAlive(go))
            {
                sb.Append("null");
                return;
            }
            try
            {
                var rt = go.GetComponent<RectTransform>();
                if (rt == null)
                {
                    sb.Append("无RT");
                    return;
                }
                var r = rt.rect;
                sb.Append(go.activeSelf ? "on" : "off");
                sb.Append('(').Append(Mathf.RoundToInt(r.width)).Append('x').Append(Mathf.RoundToInt(r.height)).Append(')');
            }
            catch (Exception)
            {
                sb.Append('?');
            }
        }

        private static Il2CppSystem.Collections.Generic.List<string> ToIl2CppList(string[] items)
        {
            var list = new Il2CppSystem.Collections.Generic.List<string>(items.Length);
            for (var i = 0; i < items.Length; i++)
                list.Add(items[i] ?? string.Empty);
            return list;
        }
    }
}
