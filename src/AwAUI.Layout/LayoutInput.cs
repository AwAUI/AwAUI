namespace AwAUI.Layout;

internal enum RunMode : byte
{
    /// <summary>只求边框盒尺寸（以及按需的基线），不写入子节点位置。</summary>
    ComputeSize,
    /// <summary>完整布局：确定尺寸并写入所有子孙的位置与尺寸。</summary>
    PerformLayout,
}

internal enum SizingMode : byte
{
    /// <summary>应用节点自身的 width/height/min/max/aspect-ratio。</summary>
    Inherent,
    /// <summary>忽略节点自身的尺寸属性，只求内容决定的尺寸（自动最小尺寸、固有尺寸关键字的求值）。</summary>
    Content,
}

/// <summary>
/// 一次节点计算的输入（约束下传）。尺寸均为边框盒尺度，NaN 表示不确定。
/// 可用空间是给节点外边距盒的。整个结构即缓存键。
/// </summary>
internal struct LayoutInput
{
    public float KnownWidth, KnownHeight;
    /// <summary>包含块尺寸（百分比基准）。</summary>
    public float ParentWidth, ParentHeight;
    public AvailableSpace AvailableWidth, AvailableHeight;
    public RunMode Mode;
    public SizingMode Sizing;
    public bool RequestBaseline;
    /// <summary>
    /// 调用方只需要宽度（固有宽度查询：min/max-content 贡献、flex 基准尺寸等）。算法在宽度确定后立即返回，
    /// 输出的高度无意义（NaN）。这样固有宽度查询不会触发子孙的「按某个宽度求高度」布局，
    /// 否则每一层的内容尺寸上下文都会派生出一批新的已知宽度，缓存键随深度增长而失效，计算量呈指数增长。
    /// </summary>
    public bool WidthOnly;

    public bool IsPerform => Mode == RunMode.PerformLayout;

    static bool Same(float a, float b) => a == b || (float.IsNaN(a) && float.IsNaN(b));

    /// <summary>除运行模式外完全相同（PerformLayout 的结果可以回答同键的 ComputeSize）。</summary>
    public bool SameKey(in LayoutInput o) =>
        Same(KnownWidth, o.KnownWidth) && Same(KnownHeight, o.KnownHeight) &&
        Same(ParentWidth, o.ParentWidth) && Same(ParentHeight, o.ParentHeight) &&
        AvailableWidth.Equals(o.AvailableWidth) && AvailableHeight.Equals(o.AvailableHeight) &&
        Sizing == o.Sizing && (RequestBaseline == o.RequestBaseline || !o.RequestBaseline) &&
        (!WidthOnly || o.WidthOnly); // 完整结果可以回答只求宽度的查询，反之不行

    public bool SameAs(in LayoutInput o) => Mode == o.Mode && SameKey(o) && RequestBaseline == o.RequestBaseline;
}

/// <summary>一次节点计算的输出（尺寸上报）。</summary>
internal struct LayoutOutput
{
    public float Width, Height;
    /// <summary>第一条基线到边框盒顶部的距离，没有时为 NaN。</summary>
    public float Baseline;

    public LayoutOutput(float width, float height, float baseline)
    {
        Width = width;
        Height = height;
        Baseline = baseline;
    }
}

internal struct CacheEntry
{
    public LayoutInput Input;
    public LayoutOutput Output;
}

/// <summary>节点样式尺寸解析结果（边框盒尺度）。</summary>
internal struct StyleSizes
{
    /// <summary>未经 min/max 钳制的 width/height，auto 或不确定时为 NaN。</summary>
    public float Width, Height;
    /// <summary>不小于 padding + border；auto 为 padding + border。</summary>
    public float MinWidth, MinHeight;
    /// <summary>none 为正无穷。</summary>
    public float MaxWidth, MaxHeight;
    /// <summary><see cref="Height"/> 是由确定的宽度经 aspect-ratio 推出的（用于非替换元素的自动最小高度）。</summary>
    public bool HeightFromRatio;

    public readonly float ClampedWidth => float.IsNaN(Width) ? float.NaN : LayoutMath.Clamp(Width, MinWidth, MaxWidth);
    public readonly float ClampedHeight => float.IsNaN(Height) ? float.NaN : LayoutMath.Clamp(Height, MinHeight, MaxHeight);

    public static StyleSizes Unconstrained(float pbW, float pbH) => new()
    {
        Width = float.NaN,
        Height = float.NaN,
        MinWidth = pbW,
        MinHeight = pbH,
        MaxWidth = float.PositiveInfinity,
        MaxHeight = float.PositiveInfinity,
    };
}

internal static class LayoutMath
{
    /// <summary>CSS 的钳制顺序：先 max 后 min（min 胜出）。</summary>
    public static float Clamp(float v, float min, float max) => System.Math.Max(min, System.Math.Min(v, max));

    public static float Or(float a, float b) => float.IsNaN(a) ? b : a;

    public static bool IsDef(float v) => !float.IsNaN(v);

    /// <summary>
    /// 长度的绝对值上限（逻辑像素）。样式与测量给出的有限长度都被钳制到这个范围，
    /// 使「大量子节点的尺寸求和」「伸缩因子 × 基准尺寸」等中间量不会溢出为无穷并进一步产生 NaN。
    /// </summary>
    public const float MaxLength = 1e9f;

    /// <summary>伸缩因子（flex-grow / flex-shrink）的上限，理由同 <see cref="MaxLength"/>。</summary>
    public const float MaxFactor = 1e9f;

    /// <summary>把有限值钳制到 ±<see cref="MaxLength"/>；NaN 与无穷原样返回（由调用方按 auto / 0 处理）。</summary>
    public static float Lim(float v) =>
        float.IsInfinity(v) ? v : v > MaxLength ? MaxLength : v < -MaxLength ? -MaxLength : v;
}
