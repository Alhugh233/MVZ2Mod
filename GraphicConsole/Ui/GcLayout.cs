namespace GraphicConsole
{
    /// <summary>
    /// 面板的全部尺寸常量 —— **改这里就能整体调大小 / 单独调某一项**。
    ///
    /// 为什么集中到一个文件：面板的视觉是"设计尺寸 × 统一缩放"两层结构。
    /// 里面的所有数字都按**设计单位**写（见下方 Design* 注释），最后给对话框的
    /// RectTransform 设一个 <see cref="Scale"/>，于是所有组件**等比**变化，
    /// 不需要一个个改。
    ///
    /// ⚠️ 布局组之间的**减法关系**（例如"内容区高度 = 面板高度 - 头部 - 底部"）在
    /// <see cref="PanelShell"/> 里按这些常量算，所以只要改这里，整体就一致。
    /// </summary>
    internal static class GcLayout
    {
        // ===================== 整体 =====================
        /// <summary>
        /// 全局等比缩放。想整体放大/缩小面板只改这一个数。
        /// 0.55 时面板约 550×363（在 1920×1080 下约占屏宽 29%）。
        /// </summary>
        public const float Scale = 0.55f;

        /// <summary>面板设计宽度（缩放前）。</summary>
        public const float Width = 1000f;
        /// <summary>面板设计高度（缩放前）。</summary>
        public const float Height = 660f;

        /// <summary>面板四周距屏幕边缘的最小可见距离（缩放后，屏幕像素）。拖出画面时保留这么多。</summary>
        public const float MinVisibleOnScreen = 10f;

        // ===================== 头部（页签行）=====================
        public const float HeaderHeight = 46f;
        public const float TabWidth = 110f;
        public const float TabSpacing = 4f;
        public const float TabFontSize = 20f;

        // ===================== 底部按钮行 =====================
        public const float FooterHeight = 46f;
        public const float FooterButtonWidth = 150f;

        // ===================== 内容区 =====================
        public const float ContentPadding = 8f;
        /// <summary>内容区可用宽 = Width - 2*ContentPadding。</summary>
        public const float ContentWidth = Width - ContentPadding * 2f;
        /// <summary>
        /// 内容区高度 = 面板高 - 上下内边距 - 页签行 - 底部行 - 两条间距。
        /// （= 660 - 16 - 46 - 46 - 8 = 544）放这里是为了让页面能直接算"还剩下多少高度给列表"。
        /// </summary>
        public const float ContentHeight = Height
            - DialogPadY * 2f
            - HeaderHeight
            - FooterHeight
            - DialogSpacing * 2f;

        // ===================== 行 / 格子 =====================
        /// <summary>一行的高度。</summary>
        public const float RowHeight = 44f;
        /// <summary>行间距。</summary>
        public const float RowSpacing = 4f;
        /// <summary>左侧标签的**最大**宽度（超出会截断）。实际宽度按文字自适应。</summary>
        public const float LabelMaxWidth = 240f;
        /// <summary>标签与右侧控件之间的间隙。</summary>
        public const float LabelGap = 6f;
        /// <summary>统一字号 —— 所有控件与标签都用它，保证"文本大小固定"。</summary>
        public const float FontSize = 18f;
        /// <summary>小一号字号（提示、列表条目）。</summary>
        public const float SmallFontSize = 15f;
        /// <summary>标签字号。</summary>
        public const float LabelFontSize = 18f;

        /// <summary>下拉框宽度。</summary>
        public const float DropdownWidth = 260f;
        /// <summary>输入框宽度。</summary>
        public const float InputWidth = 130f;
        /// <summary>按钮宽度。</summary>
        public const float ButtonWidth = 150f;
        /// <summary>复选框方框宽度（含文字）。</summary>
        public const float ToggleWidth = 220f;

        // ===================== 网格页（基础页 5×4）=====================
        public const int GridColumns = 4;
        public const int GridRows = 5;
        /// <summary>网格里一个格子的宽度 = 内容宽 / 列数。</summary>
        public const float GridCellWidth = ContentWidth / GridColumns;
        /// <summary>网格里一个格子的高度。</summary>
        public const float GridCellHeight = 46f;
        public const float GridCellSpacing = 4f;

        // ===================== 内容列表（Treeview 风格）=====================
        /// <summary>列表可见高度。</summary>
        public const float ListHeight = 420f;        /// <summary>列表条目行高。</summary>
        public const float ListRowHeight = 26f;
        /// <summary>列表条目缩进（留出选中标记的宽度）。</summary>
        public const float ListRowIndent = 6f;
        /// <summary>列表背景/选中色。</summary>
        public static readonly UnityEngine.Color ListRowNormal = new UnityEngine.Color(1f, 1f, 1f, 0f);
        public static readonly UnityEngine.Color ListRowSelected = new UnityEngine.Color(0.6f, 0.2f, 1f, 0.45f);
        public static readonly UnityEngine.Color ListRowHover = new UnityEngine.Color(1f, 1f, 1f, 0.12f);

        // ===================== 内边距（对话框 VerticalLayoutGroup）=====================
        public const int DialogPadX = 10;
        public const int DialogPadY = 8;
        public const float DialogSpacing = 4f;

        // ===================== 悬停说明浮层 =====================
        /// <summary>浮层文本的最大宽度（超出自动换行）。</summary>
        public const float TooltipMaxWidth = 360f;
        /// <summary>浮层相对鼠标的偏移（设计单位；y 正 = 往下）。</summary>
        public static readonly UnityEngine.Vector2 TooltipOffset = new UnityEngine.Vector2(14f, -18f);

        // ===================== 内容列表两列 =====================
        /// <summary>两列布局里的列间距。</summary>
        public const float ColumnGap = 12f;
        /// <summary>右侧控件列的宽度。</summary>
        public const float ControlColumnWidth = 330f;
    }
}
