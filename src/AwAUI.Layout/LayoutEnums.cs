namespace AwAUI.Layout;

/// <summary>盒的生成方式（<c>display</c>）。</summary>
public enum Display : byte
{
    /// <summary>块级盒，子元素纵向堆叠（简化版块布局：无外边距折叠、无浮动，见 design/08）。</summary>
    Block,
    /// <summary>块级 Flex 容器。</summary>
    Flex,
    /// <summary>
    /// 行内级 Flex 容器。引擎不实现行内格式化上下文，它在块容器中仍纵向堆叠，
    /// 区别只在于 <c>width: auto</c> 时取 fit-content 宽度而不是撑满。
    /// </summary>
    InlineFlex,
    /// <summary>块级网格容器（CSS Grid Layout 1，见 <see cref="LayoutStyle.GridTemplateColumns"/> 等）。</summary>
    Grid,
    /// <summary>行内级网格容器：与 <see cref="InlineFlex"/> 一样在块容器中纵向堆叠，<c>width: auto</c> 时取 fit-content 宽度。</summary>
    InlineGrid,
    /// <summary>不生成盒，整个子树不参与布局，结果全为零。</summary>
    None,
    /// <summary>自身不生成盒，子节点提升到父容器中参与布局；自身的布局结果为零。</summary>
    Contents,
}

/// <summary>定位方案（<c>position</c>）。</summary>
public enum Position : byte
{
    Static,
    /// <summary>按常规流布局后再按 inset 偏移，不影响其他盒；同时成为绝对定位后代的包含块。</summary>
    Relative,
    /// <summary>脱离常规流，相对最近的已定位祖先的内边距盒定位；没有则相对初始包含块（视口）。</summary>
    Absolute,
    /// <summary>目前按「相对初始包含块（视口）的绝对定位」处理。</summary>
    Fixed,
}

/// <summary><c>box-sizing</c>：width/height 等尺寸属性度量的是哪一个盒。</summary>
public enum BoxSizing : byte
{
    ContentBox,
    BorderBox,
}

/// <summary>行内基础方向（<c>direction</c>）。影响 row 方向的主轴、column 方向的交叉轴以及块布局中的水平对齐。</summary>
public enum Direction : byte
{
    Ltr,
    Rtl,
}

/// <summary><c>overflow-x</c> / <c>overflow-y</c>。布局只用它判断是否为滚动容器（影响 flex 项目的自动最小尺寸）。</summary>
public enum Overflow : byte
{
    Visible,
    Hidden,
    Clip,
    Scroll,
    Auto,
}

/// <summary><c>flex-direction</c>。</summary>
public enum FlexDirection : byte
{
    Row,
    RowReverse,
    Column,
    ColumnReverse,
}

/// <summary><c>flex-wrap</c>。</summary>
public enum FlexWrap : byte
{
    NoWrap,
    Wrap,
    WrapReverse,
}

/// <summary><c>justify-content</c>（flex 容器中 <c>normal</c> 与 <c>stretch</c> 等同 <c>flex-start</c>）。</summary>
public enum JustifyContent : byte
{
    Normal,
    FlexStart,
    FlexEnd,
    /// <summary>书写方向的起始侧（与 flex-direction 的反转无关）。</summary>
    Start,
    /// <summary>书写方向的结束侧。</summary>
    End,
    Center,
    SpaceBetween,
    SpaceAround,
    SpaceEvenly,
    Stretch,
}

/// <summary><c>align-content</c>（flex 容器中 <c>normal</c> 等同 <c>stretch</c>；单行容器上无效果）。</summary>
public enum AlignContent : byte
{
    Normal,
    FlexStart,
    FlexEnd,
    Start,
    End,
    Center,
    SpaceBetween,
    SpaceAround,
    SpaceEvenly,
    Stretch,
}

/// <summary><c>align-items</c>（flex 容器中 <c>normal</c> 等同 <c>stretch</c>）。</summary>
public enum AlignItems : byte
{
    Normal,
    Stretch,
    FlexStart,
    FlexEnd,
    Start,
    End,
    Center,
    Baseline,
}

/// <summary><c>align-self</c>。<see cref="Auto"/> 取父容器的 <c>align-items</c>。</summary>
public enum AlignSelf : byte
{
    Auto,
    Normal,
    Stretch,
    FlexStart,
    FlexEnd,
    Start,
    End,
    Center,
    Baseline,
}
