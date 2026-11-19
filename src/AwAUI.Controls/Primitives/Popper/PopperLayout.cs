using System;
using System.Collections.Generic;

namespace AwAUI.Controls.Primitives;

/// <summary>Radix 的 <c>sticky</c>：平移避让时是否允许浮层离开锚点。</summary>
public enum PopperSticky : byte
{
    /// <summary>平移不超过与锚点相邻的范围（limitShift，默认）。</summary>
    Partial,

    /// <summary>始终保持在边界内，即使因此与锚点分离。</summary>
    Always,
}

/// <summary>
/// <c>PopperContent</c> 的定位选项，与 Radix Popper 的属性一一对应：<c>side / sideOffset / align / alignOffset / avoidCollisions /
/// collisionBoundary / collisionPadding / sticky / hideWhenDetached / arrowPadding</c>，外加箭头尺寸。
/// </summary>
public sealed class PopperOptions
{
    public static readonly PopperOptions Default = new();

    public Side Side { get; init; } = Side.Bottom;

    public float SideOffset { get; init; }

    public PopperAlign Align { get; init; } = PopperAlign.Center;

    public float AlignOffset { get; init; }

    public bool AvoidCollisions { get; init; } = true;

    /// <summary>显式的碰撞边界（Radix 的 <c>collisionBoundary</c>）；设置后同时启用 altBoundary。</summary>
    public Rect? CollisionBoundary { get; init; }

    public SideValues CollisionPadding { get; init; }

    public PopperSticky Sticky { get; init; } = PopperSticky.Partial;

    public bool HideWhenDetached { get; init; }

    public float ArrowPadding { get; init; }

    /// <summary>箭头尺寸（<c>PopperArrow</c> 测得）；null 表示没有箭头。</summary>
    public Size? ArrowSize { get; init; }

    /// <summary>大于 0 时把输出坐标对齐到设备像素（Radix 的 <c>roundByDPR</c>）。</summary>
    public float DevicePixelRatio { get; init; }
}

/// <summary>
/// <see cref="PopperLayout.Compute"/> 的结果：浮层位置、实际方位（<c>data-side / data-align</c>）、箭头位置与可见性、
/// 以及 Radix 写到浮层包装元素上的 CSS 变量（这里命名为 <c>--aw-popper-*</c>）。
/// </summary>
public sealed class PopperLayoutResult
{
    internal PopperLayoutResult(PopperComputation computation, float x, float y, SizeData? size)
    {
        Computation = computation;
        X = x;
        Y = y;
        var rects = computation.Rects;
        AnchorWidth = rects.Reference.Width;
        AnchorHeight = rects.Reference.Height;
        AvailableWidth = size?.AvailableWidth ?? 0;
        AvailableHeight = size?.AvailableHeight ?? 0;
        var arrow = computation.Data.Arrow;
        ArrowX = arrow?.X;
        ArrowY = arrow?.Y;
        CannotCenterArrow = arrow is null || arrow.CenterOffset != 0;
        ReferenceHidden = computation.Data.Hide?.ReferenceHidden ?? false;
        TransformOrigin = computation.Data.TransformOrigin?.ToString() ?? "";
    }

    public PopperComputation Computation { get; }

    /// <summary>浮层左上角（已按 <see cref="PopperOptions.DevicePixelRatio"/> 取整）。</summary>
    public float X { get; }

    public float Y { get; }

    public Placement Placement => Computation.Placement;

    public Side PlacedSide => Computation.Placement.Side;

    public PopperAlign PlacedAlign => Computation.Placement.Align;

    /// <summary><c>data-side</c> 属性值。</summary>
    public string DataSide => Primitives.Placement.SideName(PlacedSide);

    /// <summary><c>data-align</c> 属性值。</summary>
    public string DataAlign => Primitives.Placement.AlignName(PlacedAlign);

    public float AvailableWidth { get; }

    public float AvailableHeight { get; }

    public float AnchorWidth { get; }

    public float AnchorHeight { get; }

    /// <summary>箭头在浮层内的 x（上 / 下边时有值）。</summary>
    public float? ArrowX { get; }

    /// <summary>箭头在浮层内的 y（左 / 右边时有值）。</summary>
    public float? ArrowY { get; }

    /// <summary>箭头无法对准锚点中心（Radix 此时让箭头 <c>visibility: hidden</c>）。</summary>
    public bool CannotCenterArrow { get; }

    /// <summary>箭头贴在浮层的哪条边上（锚点方向的边）。</summary>
    public Side ArrowBaseSide => Primitives.Placement.OppositeSide(PlacedSide);

    /// <summary>锚点已被裁剪祖先完全遮住（<see cref="PopperOptions.HideWhenDetached"/> 时浮层应隐藏且不接收指针）。</summary>
    public bool ReferenceHidden { get; }

    /// <summary><c>transform-origin</c> 取值。</summary>
    public string TransformOrigin { get; }

    public const string AvailableWidthVariable = "--aw-popper-available-width";
    public const string AvailableHeightVariable = "--aw-popper-available-height";
    public const string AnchorWidthVariable = "--aw-popper-anchor-width";
    public const string AnchorHeightVariable = "--aw-popper-anchor-height";
    public const string TransformOriginVariable = "--aw-popper-transform-origin";

    /// <summary>写到浮层包装元素上的 CSS 变量（名称见上方常量；长度以 px 表示，数值用不变区域格式）。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> CssVariables => new[]
    {
        new KeyValuePair<string, string>(AvailableWidthVariable, TransformOriginMiddleware.Px(AvailableWidth)),
        new KeyValuePair<string, string>(AvailableHeightVariable, TransformOriginMiddleware.Px(AvailableHeight)),
        new KeyValuePair<string, string>(AnchorWidthVariable, TransformOriginMiddleware.Px(AnchorWidth)),
        new KeyValuePair<string, string>(AnchorHeightVariable, TransformOriginMiddleware.Px(AnchorHeight)),
        new KeyValuePair<string, string>(TransformOriginVariable, TransformOrigin),
    };
}

/// <summary>
/// Radix Popper 的定位组合：按 Radix 的顺序组装中间件 <c>offset → shift → flip → size → arrow → transformOrigin → hide</c> 并运行引擎。
/// 纯函数：锚点矩形、浮层尺寸与环境由宿主测量后给出（UI 接入见 design/23「第 1 层」）。
/// </summary>
public static class PopperLayout
{
    /// <summary>按 Radix 的规则组装中间件。</summary>
    public static List<PopperMiddleware> CreateMiddleware(PopperOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        float arrowWidth = options.ArrowSize?.Width ?? 0;
        float arrowHeight = options.ArrowSize?.Height ?? 0;
        bool explicitBoundary = options.CollisionBoundary.HasValue;
        var overflow = new DetectOverflowOptions
        {
            Padding = options.CollisionPadding,
            Boundary = options.CollisionBoundary,
            AltBoundary = explicitBoundary,
        };
        var list = new List<PopperMiddleware>(7)
        {
            new OffsetMiddleware(Finite(options.SideOffset) + Finite(arrowHeight)) { AlignmentAxis = Finite(options.AlignOffset) },
        };
        if (options.AvoidCollisions)
        {
            list.Add(new ShiftMiddleware
            {
                MainAxis = true,
                CrossAxis = false,
                Limiter = options.Sticky == PopperSticky.Partial ? new LimitShift() : null,
                Overflow = overflow,
            });
            list.Add(new FlipMiddleware { Overflow = overflow });
        }
        list.Add(new SizeMiddleware { Overflow = overflow });
        if (options.ArrowSize is Size arrow) list.Add(new ArrowMiddleware(arrow) { Padding = options.ArrowPadding });
        list.Add(new TransformOriginMiddleware(arrowWidth, arrowHeight));
        if (options.HideWhenDetached)
            list.Add(new HideMiddleware(HideStrategy.ReferenceHidden) { Padding = options.CollisionPadding, Boundary = options.CollisionBoundary });
        return list;
    }

    /// <summary>计算 <c>PopperContent</c> 的位置。</summary>
    public static PopperLayoutResult Compute(Rect anchor, Size content, PopperEnvironment environment, PopperOptions? options = null)
    {
        options ??= PopperOptions.Default;
        anchor = Sanitize(anchor);
        content = new Size(Math.Max(0, Finite(content.Width)), Math.Max(0, Finite(content.Height)));
        var placement = new Placement(options.Side, options.Align);
        var computation = PopperEngine.ComputePosition(anchor, content, environment, placement, CreateMiddleware(options));
        float x = computation.X, y = computation.Y;
        float dpr = options.DevicePixelRatio;
        if (dpr > 0 && !float.IsInfinity(dpr))
        {
            // 与 JS 的 Math.round 相同：.5 向正无穷取整
            x = (float)(Math.Floor(x * (double)dpr + 0.5) / dpr);
            y = (float)(Math.Floor(y * (double)dpr + 0.5) / dpr);
        }
        return new PopperLayoutResult(computation, x, y, computation.Data.Size);
    }

    static float Finite(float v) => float.IsNaN(v) || float.IsInfinity(v) ? 0 : v;

    static Rect Sanitize(Rect r) => new(Finite(r.X), Finite(r.Y), Math.Max(0, Finite(r.Width)), Math.Max(0, Finite(r.Height)));
}
