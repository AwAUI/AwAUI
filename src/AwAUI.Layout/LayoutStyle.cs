namespace AwAUI.Layout;

/// <summary>
/// 布局引擎的强类型输入样式（类似 Yoga / Taffy 的样式结构），与 CSS 值的表示方式无关。
/// UI 层在布局相关的计算值变化时把 ComputedStyle 映射为它。
/// <para>
/// 不可变：用 <c>with</c> 表达式派生新样式，同一实例可被任意多个节点共享（例如所有使用默认样式的节点共享
/// <see cref="Default"/>）。各属性的默认值即 CSS 初始值（<c>display</c> 例外，默认 <see cref="Display.Block"/>）。
/// </para>
/// </summary>
public sealed record LayoutStyle
{
    /// <summary>全部为初始值的共享实例。</summary>
    public static LayoutStyle Default { get; } = new();

    public Display Display { get; init; } = Display.Block;
    public Position Position { get; init; } = Position.Static;
    public BoxSizing BoxSizing { get; init; } = BoxSizing.ContentBox;
    public Direction Direction { get; init; } = Direction.Ltr;
    public Overflow OverflowX { get; init; } = Overflow.Visible;
    public Overflow OverflowY { get; init; } = Overflow.Visible;

    public Dimension Width { get; init; }
    public Dimension Height { get; init; }
    /// <summary><see cref="Dimension.Auto"/> 表示自动最小尺寸（flex 项目为基于内容的最小尺寸，其他情况为 0）。</summary>
    public Dimension MinWidth { get; init; }
    public Dimension MinHeight { get; init; }
    /// <summary><see cref="Dimension.None"/>（即 Auto）表示不限制。</summary>
    public Dimension MaxWidth { get; init; }
    public Dimension MaxHeight { get; init; }

    /// <summary><c>aspect-ratio</c>（宽 / 高）；null 表示 auto。作用于 <see cref="BoxSizing"/> 指定的盒。</summary>
    public float? AspectRatio { get; init; }

    /// <summary>外边距；百分比相对包含块的宽度（两个方向都是）。</summary>
    public Edges<LengthPercentageAuto> Margin { get; init; }
    /// <summary>内边距；百分比相对包含块的宽度（两个方向都是）。</summary>
    public Edges<LengthPercentage> Padding { get; init; }
    /// <summary>边框宽度（px）。</summary>
    public Edges<float> Border { get; init; }
    /// <summary><c>top/right/bottom/left</c>，默认全部 auto。</summary>
    public Edges<LengthPercentageAuto> Inset { get; init; } = new(LengthPercentageAuto.Auto);

    public FlexDirection FlexDirection { get; init; } = FlexDirection.Row;
    public FlexWrap FlexWrap { get; init; } = FlexWrap.NoWrap;
    public float FlexGrow { get; init; }
    public float FlexShrink { get; init; } = 1;
    /// <summary><c>flex-basis</c>：auto、content 或长度/百分比（相对 flex 容器内容盒的主轴尺寸）。</summary>
    public Dimension FlexBasis { get; init; }

    public JustifyContent JustifyContent { get; init; } = JustifyContent.Normal;
    public AlignItems AlignItems { get; init; } = AlignItems.Normal;
    public AlignSelf AlignSelf { get; init; } = AlignSelf.Auto;
    public AlignContent AlignContent { get; init; } = AlignContent.Normal;

    /// <summary>行间距；百分比相对容器内容盒高度（不确定时为 0）。</summary>
    public LengthPercentage RowGap { get; init; }
    /// <summary>列间距；百分比相对容器内容盒宽度。</summary>
    public LengthPercentage ColumnGap { get; init; }

    /// <summary>
    /// <c>order</c>：flex 与网格项目按 (order, 文档顺序) 排列（布局顺序；绘制顺序也应按它，见 design/08）。
    /// 对块布局与绝对定位的子节点没有作用。
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// 是否为替换元素（图片、SVG 等，CSS 2.1 §10.3.2）：<c>width: auto</c> 时取固有宽度，块布局与绝对定位（left、right 都给出）
    /// 都不拉伸它；高度同理。aspect-ratio 的自动最小尺寸（CSS Sizing 4）只用于非替换元素；网格中 <c>normal</c> 对齐按 start。
    /// </summary>
    public bool IsReplaced { get; init; }

    // ---------------- 网格（CSS Grid Layout 1） ----------------

    /// <summary><c>grid-template-columns</c>；null 表示 none（没有显式列，除非 <see cref="GridTemplateAreas"/> 定义了列）。</summary>
    public GridTrackList? GridTemplateColumns { get; init; }
    /// <summary><c>grid-template-rows</c>；null 表示 none。</summary>
    public GridTrackList? GridTemplateRows { get; init; }
    /// <summary><c>grid-template-areas</c>；null 表示 none。</summary>
    public GridAreas? GridTemplateAreas { get; init; }
    /// <summary><c>grid-auto-columns</c>：隐式列的尺寸（按顺序循环使用，只取各段的轨道）；null 表示 auto。</summary>
    public GridTrackList? GridAutoColumns { get; init; }
    /// <summary><c>grid-auto-rows</c>；null 表示 auto。</summary>
    public GridTrackList? GridAutoRows { get; init; }
    /// <summary><c>grid-auto-flow</c>。</summary>
    public GridAutoFlow GridAutoFlow { get; init; } = GridAutoFlow.Row;
    /// <summary><c>grid-row-start</c>（网格项目的放置；绝对定位的子节点用它确定作为包含块的网格区域）。</summary>
    public GridLine GridRowStart { get; init; }
    public GridLine GridRowEnd { get; init; }
    public GridLine GridColumnStart { get; init; }
    public GridLine GridColumnEnd { get; init; }
    /// <summary><c>justify-items</c>（网格容器）。</summary>
    public JustifyItems JustifyItems { get; init; } = JustifyItems.Normal;
    /// <summary><c>justify-self</c>（网格项目；flex 项目忽略）。</summary>
    public JustifySelf JustifySelf { get; init; } = JustifySelf.Auto;

    internal bool IsOutOfFlow => Position is Position.Absolute or Position.Fixed;

    /// <summary><c>width: auto</c> 时不撑满包含块（行内级容器与替换元素），而取 fit-content / 固有宽度。</summary>
    internal bool IsShrinkToFit => Display is Display.InlineFlex or Display.InlineGrid || IsReplaced;

    /// <summary>是否为滚动容器（任一方向 overflow 为 hidden/scroll/auto；clip 不算）。</summary>
    internal bool IsScrollContainer =>
        OverflowX is Overflow.Hidden or Overflow.Scroll or Overflow.Auto ||
        OverflowY is Overflow.Hidden or Overflow.Scroll or Overflow.Auto;
}
