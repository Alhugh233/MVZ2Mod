using System;
using DSHCore.Ui;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;

namespace Loadout
{
    /// <summary>
    /// 一个**原版观感的搜索框** —— 克隆自对话档案索引页的那个
    /// （<c>IndexArchivePage.searchInputField</c>，带自己的底图与占位文本）。
    ///
    /// <para>与 <see cref="UiField"/> 的区别：这个没有标签、不是「标签 + 输入框」的格子，
    /// 而是**整格一个输入框**（搜索框就该占满一行）；尺寸契约仍然用面板那一套
    /// （<see cref="UiContainer"/> 的单元格 + <see cref="UiKit.RowHeight"/>），
    /// 所以它和面板里其它行天然对齐。</para>
    ///
    /// <para>取值用 <c>onValueChanged</c>（**实时**）而不是只靠 <c>onEndEdit</c>：
    /// 需求是"敲 <c>[x]</c> 就直接过滤"，要边打边筛。两个事件都接，行为不会互相打架
    /// （<c>OnTextChanged</c> 是幂等的）。</para>
    /// </summary>
    internal sealed class UiSearchBox
    {
        private readonly GameObject _root;
        private readonly TMP_InputField _input;

        private System.Action<string> _handlerManaged;
        private UnityEngine.Events.UnityAction<string> _handlerIl2Cpp;

        private UiSearchBox(GameObject root, TMP_InputField input)
        {
            _root = root;
            _input = input;
        }

        public GameObject Root { get { return UiFinder.IsAlive(_root) ? _root : null; } }

        public bool IsValid { get { return UiFinder.IsAlive(_root) && UiFinder.IsAlive(_input); } }

        public bool IsFocused
        {
            get
            {
                try { return UiFinder.IsAlive(_input) && _input.isFocused; }
                catch (Exception) { return false; }
            }
        }

        public string Text
        {
            get
            {
                try { return UiFinder.IsAlive(_input) ? (_input.text ?? string.Empty) : string.Empty; }
                catch (Exception) { return string.Empty; }
            }
            set
            {
                if (!UiFinder.IsAlive(_input))
                    return;
                try { _input.SetTextWithoutNotify(value ?? string.Empty); }
                catch (Exception) { }
            }
        }

        /// <summary>拿不到档案页的输入框时返回 null（页面应当降级）。</summary>
        public static UiSearchBox Create(Transform parent, string name, Action<string> onChanged)
        {
            var template = ArchiveProbe.SearchFieldNode();
            if (!UiFinder.IsAlive(template))
            {
                Plugin.LogOnce("search-tpl", "[Search] 拿不到档案页的搜索框，进度页的搜索不可用。");
                return null;
            }

            var root = UiClone.Instantiate(template, parent, name, false);
            if (!UiFinder.IsAlive(root))
            {
                Plugin.LogOnce("search-clone", "[Search] 搜索框克隆失败。");
                return null;
            }

            var input = root.GetComponent<TMP_InputField>();
            if (input == null)
            {
                UnityEngine.Object.Destroy(root);
                Plugin.LogOnce("search-comp", "[Search] 搜索框克隆体上没有 TMP_InputField。");
                return null;
            }

            // 尺寸契约：整格宽 + 与其它行同一高度（克隆体原先的契约是"宽度弹性、高度不管"，
            // 脱离档案页的布局链之后高度会算成 0）。
            UiContainer.FitCell(root, 0f, UiKit.RowHeight);
            UiClone.Activate(root);

            var box = new UiSearchBox(root, input);
            box.Bind(onChanged);
            UiField.RegisterExternal(input);
            return box;
        }

        private void Bind(Action<string> onChanged)
        {
            if (onChanged == null || !UiFinder.IsAlive(_input))
                return;
            try
            {
                _handlerManaged = new System.Action<string>(value => onChanged(value ?? string.Empty));
                _handlerIl2Cpp = DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<string>>(_handlerManaged);
                UiClone.KeepAlive(_root, _handlerManaged, _handlerIl2Cpp);
                _input.onValueChanged.AddListener(_handlerIl2Cpp);
                _input.onEndEdit.AddListener(_handlerIl2Cpp);
            }
            catch (Exception e)
            {
                Plugin.LogOnce("search-bind", "[Search] 挂搜索回调失败：" + e.GetType().Name);
            }
        }

        /// <summary>归位（面板重建时调用；输入框的文本会一起没掉）。</summary>
        public void Clear()
        {
            Text = string.Empty;
        }

        public string Describe()
        {
            return "搜索框=" + (UiFinder.IsAlive(_root) ? "OK" : "无")
                + "  文本「" + Text + "」"
                + "  聚焦=" + (IsFocused ? 1 : 0);
        }
    }
}
