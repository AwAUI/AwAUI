using System;
using System.Collections.Generic;

namespace AwAUI.Controls.Primitives;

/// <summary>一次定位计算的结果。</summary>
public sealed class PopperComputation
{
    internal PopperComputation(float x, float y, Placement placement, PopperMiddlewareData data, ElementRects rects, int resets)
    {
        X = x;
        Y = y;
        Placement = placement;
        Data = data;
        Rects = rects;
        ResetCount = resets;
    }

    /// <summary>浮层左上角（与输入矩形同一坐标系）。</summary>
    public float X { get; }

    public float Y { get; }

    /// <summary>最终方位（翻转后可能与请求的不同）。</summary>
    public Placement Placement { get; }

    public PopperMiddlewareData Data { get; }

    /// <summary>最终使用的矩形（size 中间件可能改变了浮层尺寸）。</summary>
    public ElementRects Rects { get; }

    /// <summary>管线被重置的次数（诊断用；上限 <see cref="PopperEngine.MaxResets"/>）。</summary>
    public int ResetCount { get; }
}

/// <summary>
/// 定位引擎：Floating UI <c>computePosition</c> 的纯函数移植。输入锚点矩形、浮层尺寸与环境，按顺序运行中间件，输出浮层坐标与最终方位。
/// <para>
/// 有界：中间件的重置请求最多生效 <see cref="MaxResets"/> + 1 次（与 Floating UI 相同，之后的重置请求被忽略），
/// 因此即使自定义中间件每次都请求重置，计算量也不超过 (<see cref="MaxResets"/> + 2) × 中间件数 步。
/// </para>
/// </summary>
public static class PopperEngine
{
    /// <summary>重置次数上限（Floating UI 的 <c>MAX_RESET_COUNT</c>）。</summary>
    public const int MaxResets = 50;

    /// <summary>运行中间件管线。</summary>
    public static PopperComputation ComputePosition(Rect reference, Size floating, PopperEnvironment environment,
        Placement placement, IReadOnlyList<PopperMiddleware?>? middleware = null)
    {
        if (environment is null) throw new ArgumentNullException(nameof(environment));
        // 逻辑边先在入口解析成物理边：中间件、翻转与 data-side 看到的都是物理方位
        placement = new Placement(Placement.ResolveSide(placement.Side, environment.Rtl), placement.Align);
        var rects = new ElementRects(reference, floating);
        var state = new PopperState(placement, rects, environment);
        var p = ComputeCoordsFromPlacement(rects, placement, environment.Rtl);
        state.X = p.X;
        state.Y = p.Y;
        int resets = 0;
        if (middleware is not null)
        {
            for (int i = 0; i < middleware.Count; i++)
            {
                var m = middleware[i];
                if (m is null) continue;
                var r = m.Compute(state);
                if (r.X.HasValue) state.X = r.X.Value;
                if (r.Y.HasValue) state.Y = r.Y.Value;
                if (r.WantsReset && resets < MaxResets)
                {
                    resets++;
                    if (r.ResetPlacement.HasValue || r.ResetRects.HasValue)
                    {
                        if (r.ResetPlacement.HasValue) state.Placement = r.ResetPlacement.Value;
                        if (r.ResetRects.HasValue) state.Rects = r.ResetRects.Value;
                        var c = ComputeCoordsFromPlacement(state.Rects, state.Placement, environment.Rtl);
                        state.X = c.X;
                        state.Y = c.Y;
                    }
                    i = -1;
                }
            }
        }
        return new PopperComputation(state.X, state.Y, state.Placement, state.Data, state.Rects, resets);
    }

    /// <summary>不经中间件的基本坐标（Floating UI 的 <c>computeCoordsFromPlacement</c>）。</summary>
    public static Point ComputeCoordsFromPlacement(ElementRects rects, Placement placement, bool rtl)
    {
        var reference = rects.Reference;
        var floating = rects.Floating;
        bool isVertical = placement.IsVertical;
        var side = Placement.ResolveSide(placement.Side, rtl);
        float commonX = reference.X + reference.Width / 2 - floating.Width / 2;
        float commonY = reference.Y + reference.Height / 2 - floating.Height / 2;
        var alignmentAxis = placement.AlignmentAxis;
        float commonAlign = Length(reference, alignmentAxis) / 2 - Length(floating, alignmentAxis) / 2;
        float x, y;
        switch (side)
        {
            case Side.Top: x = commonX; y = reference.Y - floating.Height; break;
            case Side.Bottom: x = commonX; y = reference.Y + reference.Height; break;
            case Side.Right: x = reference.X + reference.Width; y = commonY; break;
            default: x = reference.X - floating.Width; y = commonY; break;
        }
        float sign = rtl && isVertical ? -1 : 1;
        float delta = placement.Align switch
        {
            PopperAlign.Start => -commonAlign * sign,
            PopperAlign.End => commonAlign * sign,
            _ => 0,
        };
        if (alignmentAxis == Axis.X) x += delta;
        else y += delta;
        return new Point(x, y);
    }

    /// <summary>
    /// 溢出量（Floating UI 的 <c>detectOverflow</c>）：各边超出裁剪矩形的像素数，正值表示溢出、负值表示还有余量。
    /// 裁剪矩形 = 元素的裁剪祖先（或 <see cref="DetectOverflowOptions.Boundary"/>）与根边界的交。
    /// </summary>
    public static SideValues DetectOverflow(PopperState state, DetectOverflowOptions? options = null)
    {
        if (state is null) throw new ArgumentNullException(nameof(state));
        options ??= DetectOverflowOptions.Default;
        var env = state.Environment;
        var clipElement = options.AltBoundary
            ? (options.ElementContext == PopperElementContext.Floating ? PopperElementContext.Reference : PopperElementContext.Floating)
            : options.ElementContext;
        Rect root = options.RootBoundary ?? env.Viewport;
        Rect? ancestors = options.Boundary ?? (clipElement == PopperElementContext.Floating ? env.FloatingClippingRect : env.ReferenceClippingRect);
        float cTop = root.Top, cLeft = root.Left, cRight = root.Right, cBottom = root.Bottom;
        if (ancestors.HasValue)
        {
            var a = ancestors.Value;
            // 与 Floating UI 的 getClippingRect 相同：逐边取交，不相交时宽高可以为负
            cTop = Math.Max(cTop, a.Top);
            cLeft = Math.Max(cLeft, a.Left);
            cRight = Math.Min(cRight, a.Right);
            cBottom = Math.Min(cBottom, a.Bottom);
        }
        Rect element = options.ElementContext == PopperElementContext.Floating
            ? new Rect(state.X, state.Y, state.Rects.Floating.Width, state.Rects.Floating.Height)
            : state.Rects.Reference;
        var pad = options.Padding;
        return new SideValues(
            top: cTop - element.Top + pad.Top,
            right: element.Right - cRight + pad.Right,
            bottom: element.Bottom - cBottom + pad.Bottom,
            left: cLeft - element.Left + pad.Left);
    }

    // ------------------------------------------------------------ 共用工具（与 Floating UI 的 utils 同名）

    internal static float Length(Rect r, Axis axis) => axis == Axis.X ? r.Width : r.Height;

    internal static float Coord(Rect r, Axis axis) => axis == Axis.X ? r.X : r.Y;

    internal static float Clamp(float start, float value, float end) => Math.Max(start, Math.Min(value, end));

    /// <summary>对齐方向上的两条边（Floating UI 的 <c>getAlignmentSides</c>）：第一条是对齐端可能溢出的一侧。</summary>
    internal static (Side Main, Side Cross) GetAlignmentSides(Placement placement, ElementRects rects, bool rtl)
    {
        var alignmentAxis = placement.AlignmentAxis;
        Side main = alignmentAxis == Axis.X
            ? (placement.Align == (rtl ? PopperAlign.End : PopperAlign.Start) ? Side.Right : Side.Left)
            : (placement.Align == PopperAlign.Start ? Side.Bottom : Side.Top);
        if (Length(rects.Reference, alignmentAxis) > Length(rects.Floating, alignmentAxis)) main = Placement.OppositeSide(main);
        return (main, Placement.OppositeSide(main));
    }

    /// <summary>getExpandedPlacements：对齐取反、对边、对边且对齐取反。</summary>
    internal static List<Placement> GetExpandedPlacements(Placement placement)
    {
        var opposite = placement.Opposite;
        return new List<Placement> { placement.OppositeAlignment, opposite, opposite.OppositeAlignment };
    }

    /// <summary>getOppositeAxisPlacements：另一轴上的候选（flip 的 <c>fallbackAxisSideDirection</c>）。</summary>
    internal static List<Placement> GetOppositeAxisPlacements(Placement placement, bool flipAlignment, FallbackAxisSideDirection direction, bool rtl)
    {
        bool isStart = direction == FallbackAxisSideDirection.Start;
        Side[] sides;
        if (placement.IsVertical)
            sides = rtl ? (isStart ? new[] { Side.Right, Side.Left } : new[] { Side.Left, Side.Right })
                        : (isStart ? new[] { Side.Left, Side.Right } : new[] { Side.Right, Side.Left });
        else
            sides = isStart ? new[] { Side.Top, Side.Bottom } : new[] { Side.Bottom, Side.Top };
        var list = new List<Placement>(4);
        foreach (var s in sides) list.Add(new Placement(s, placement.Align));
        if (!placement.IsBase && flipAlignment)
        {
            int n = list.Count;
            for (int i = 0; i < n; i++) list.Add(list[i].OppositeAlignment);
        }
        return list;
    }
}
