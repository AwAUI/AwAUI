using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// offset：沿主轴（离开锚点的方向）与交叉轴平移浮层。<see cref="AlignmentAxis"/> 只对带对齐的方位生效，
/// 并按逻辑方向解释（<c>end</c> 对齐时取反），设置时覆盖 <see cref="CrossAxis"/>。
/// </summary>
public sealed class OffsetMiddleware : PopperMiddleware
{
    public OffsetMiddleware(float mainAxis = 0) : base("offset") => MainAxis = mainAxis;

    public float MainAxis { get; init; }

    public float CrossAxis { get; init; }

    public float? AlignmentAxis { get; init; }

    public override PopperMiddlewareResult Compute(PopperState state)
    {
        var placement = state.Placement;
        // 箭头中间件为了对准锚点已经修正过位置时，同一方位下不再重复施加偏移
        if (state.Data.Offset is { } prev && prev.Placement == placement && state.Data.Arrow?.AlignmentOffset is float ao && ao != 0)
            return PopperMiddlewareResult.None;
        bool isVertical = placement.IsVertical;
        float mainMulti = placement.Side is Side.Left or Side.Top ? -1 : 1;
        float crossMulti = state.Rtl && isVertical ? -1 : 1;
        float main = MainAxis;
        float cross = CrossAxis;
        if (!placement.IsBase && AlignmentAxis is float aa)
            cross = placement.Align == PopperAlign.End ? -aa : aa;
        float dx = isVertical ? cross * crossMulti : main * mainMulti;
        float dy = isVertical ? main * mainMulti : cross * crossMulti;
        state.Data.Offset = new OffsetData(dx, dy, placement);
        return new PopperMiddlewareResult { X = state.X + dx, Y = state.Y + dy };
    }
}

/// <summary>翻转的候选都放不下时的回退策略。</summary>
public enum FlipFallbackStrategy : byte
{
    /// <summary>选总溢出最少的方位。</summary>
    BestFit,

    /// <summary>回到最初请求的方位。</summary>
    InitialPlacement,
}

/// <summary>是否在另一轴上增加候选方位，以及从哪一侧开始。</summary>
public enum FallbackAxisSideDirection : byte
{
    None,
    Start,
    End,
}

/// <summary>flip 的交叉轴检查方式。</summary>
public enum FlipCrossAxis : byte
{
    /// <summary>不检查交叉轴。</summary>
    None,

    /// <summary>检查交叉轴（默认）。</summary>
    Check,

    /// <summary>只在候选与初始方位同轴时才因交叉轴溢出而翻转（Floating UI 的 <c>crossAxis: 'alignment'</c>）。</summary>
    Alignment,
}

/// <summary>flip：主轴（或交叉轴）溢出时依次尝试候选方位，都不合适时按回退策略选择。</summary>
public sealed class FlipMiddleware : PopperMiddleware
{
    public FlipMiddleware() : base("flip") { }

    public bool MainAxis { get; init; } = true;

    public FlipCrossAxis CrossAxis { get; init; } = FlipCrossAxis.Check;

    /// <summary>显式的候选方位（按顺序尝试）；null 时自动生成对边（以及对齐取反的变体）。</summary>
    public IReadOnlyList<Placement>? FallbackPlacements { get; init; }

    public FlipFallbackStrategy FallbackStrategy { get; init; } = FlipFallbackStrategy.BestFit;

    public FallbackAxisSideDirection FallbackAxisSideDirection { get; init; } = FallbackAxisSideDirection.None;

    /// <summary>自动候选是否包含对齐取反的方位。</summary>
    public bool FlipAlignment { get; init; } = true;

    public DetectOverflowOptions? Overflow { get; init; }

    public override PopperMiddlewareResult Compute(PopperState state)
    {
        var data = state.Data;
        if (data.Arrow?.AlignmentOffset is float ao && ao != 0) return PopperMiddlewareResult.None;
        var placement = state.Placement;
        var initial = state.InitialPlacement;
        var initialSideAxis = initial.SideAxis;
        bool rtl = state.Rtl;

        List<Placement> fallbacks = FallbackPlacements is not null
            ? new List<Placement>(FallbackPlacements)
            : (initial.IsBase || !FlipAlignment ? new List<Placement> { initial.Opposite } : PopperEngine.GetExpandedPlacements(initial));
        bool hasAxisDirection = FallbackAxisSideDirection != FallbackAxisSideDirection.None;
        if (FallbackPlacements is null && hasAxisDirection)
            fallbacks.AddRange(PopperEngine.GetOppositeAxisPlacements(initial, FlipAlignment, FallbackAxisSideDirection, rtl));
        var placements = new List<Placement>(fallbacks.Count + 1) { initial };
        placements.AddRange(fallbacks);

        var overflow = state.DetectOverflow(Overflow);
        var overflows = new List<float>(3);
        if (MainAxis) overflows.Add(overflow[placement.Side]);
        if (CrossAxis != FlipCrossAxis.None)
        {
            var (a, b) = PopperEngine.GetAlignmentSides(placement, state.Rects, rtl);
            overflows.Add(overflow[a]);
            overflows.Add(overflow[b]);
        }
        var history = new List<PlacementOverflow>(data.Flip?.Overflows ?? Array.Empty<PlacementOverflow>())
        {
            new(placement, overflows.ToArray()),
        };

        if (overflows.All(o => o <= 0)) return PopperMiddlewareResult.None;

        int nextIndex = (data.Flip?.Index ?? 0) + 1;
        if (nextIndex < placements.Count)
        {
            var next = placements[nextIndex];
            bool ignoreCrossAxisOverflow = CrossAxis == FlipCrossAxis.Alignment && initialSideAxis != next.SideAxis;
            if (!ignoreCrossAxisOverflow ||
                history.All(d => d.Placement.SideAxis != initialSideAxis || (d.Overflows.Count > 0 && d.Overflows[0] > 0)))
            {
                data.Flip = new FlipData(nextIndex, history);
                return new PopperMiddlewareResult { ResetPlacement = next };
            }
        }

        // 候选用尽：优先选主轴放得下、交叉轴溢出最少的
        // （JS 的比较器遇到缺失的第二项得 NaN，视为相等；稳定排序于是保持原顺序，这里取第一个）
        Placement? reset = null;
        float best = float.PositiveInfinity;
        bool keyed = true;
        foreach (var d in history)
        {
            if (d.Overflows.Count == 0 || d.Overflows[0] > 0) continue;
            if (reset is null)
            {
                reset = d.Placement;
                keyed = d.Overflows.Count > 1;
                best = keyed ? d.Overflows[1] : 0;
                continue;
            }
            if (!keyed || d.Overflows.Count < 2) continue;
            if (d.Overflows[1] < best)
            {
                best = d.Overflows[1];
                reset = d.Placement;
            }
        }
        if (reset is null)
        {
            switch (FallbackStrategy)
            {
                case FlipFallbackStrategy.BestFit:
                {
                    float bestSum = float.PositiveInfinity;
                    foreach (var d in history)
                    {
                        if (hasAxisDirection)
                        {
                            var axis = d.Placement.SideAxis;
                            if (!(axis == initialSideAxis || axis == Axis.Y)) continue;
                        }
                        float sum = 0;
                        foreach (var o in d.Overflows) if (o > 0) sum += o;
                        if (sum < bestSum)
                        {
                            bestSum = sum;
                            reset = d.Placement;
                        }
                    }
                    break;
                }
                case FlipFallbackStrategy.InitialPlacement:
                    reset = initial;
                    break;
            }
        }
        // 与 Floating UI 相同：回退时不更新 flip 数据（下一轮仍基于之前的尝试记录）
        if (reset.HasValue && reset.Value != placement) return new PopperMiddlewareResult { ResetPlacement = reset.Value };
        return PopperMiddlewareResult.None;
    }
}

/// <summary>
/// shift 的限制器（Floating UI 的 <c>limiter</c>）：在 shift 夹紧之后再修正坐标。
/// </summary>
public abstract class ShiftLimiter
{
    /// <summary>给出最终坐标。<paramref name="x"/> / <paramref name="y"/> 是 shift 夹紧后的坐标。</summary>
    public abstract Point Limit(PopperState state, float x, float y);
}

/// <summary>
/// limitShift：平移不超过「浮层与锚点刚好不再相邻」的位置，使浮层始终贴着锚点（Radix 的 <c>sticky="partial"</c>）。
/// </summary>
public sealed class LimitShift : ShiftLimiter
{
    /// <summary>主轴上提前停止的距离（正值让浮层与锚点保留更多重叠）。</summary>
    public float Offset { get; init; }

    /// <summary>交叉轴偏移（对应 Floating UI 的 <c>offset: { crossAxis }</c>）。</summary>
    public float CrossAxisOffset { get; init; }

    public bool MainAxis { get; init; } = true;

    public bool CrossAxis { get; init; } = true;

    public override Point Limit(PopperState state, float x, float y)
    {
        var placement = state.Placement;
        var crossAxis = placement.SideAxis;
        var mainAxis = crossAxis == Axis.X ? Axis.Y : Axis.X;
        float mainCoord = mainAxis == Axis.X ? x : y;
        float crossCoord = crossAxis == Axis.X ? x : y;
        var reference = state.Rects.Reference;
        var floating = state.Rects.Floating;
        if (MainAxis)
        {
            float len = PopperEngine.Length(floating, mainAxis);
            float refLen = PopperEngine.Length(reference, mainAxis);
            float refCoord = PopperEngine.Coord(reference, mainAxis);
            float limitMin = refCoord - len + Offset;
            float limitMax = refCoord + refLen - Offset;
            if (mainCoord < limitMin) mainCoord = limitMin;
            else if (mainCoord > limitMax) mainCoord = limitMax;
        }
        if (CrossAxis)
        {
            float len = PopperEngine.Length(floating, crossAxis);
            float refLen = PopperEngine.Length(reference, crossAxis);
            float refCoord = PopperEngine.Coord(reference, crossAxis);
            bool isOriginSide = placement.Side is Side.Top or Side.Left;
            float offsetData = state.Data.Offset is { } od ? (crossAxis == Axis.X ? od.X : od.Y) : 0;
            float limitMin = refCoord - len + (isOriginSide ? offsetData : 0) + (isOriginSide ? 0 : CrossAxisOffset);
            float limitMax = refCoord + refLen + (isOriginSide ? 0 : offsetData) - (isOriginSide ? CrossAxisOffset : 0);
            if (crossCoord < limitMin) crossCoord = limitMin;
            else if (crossCoord > limitMax) crossCoord = limitMax;
        }
        return mainAxis == Axis.X ? new Point(mainCoord, crossCoord) : new Point(crossCoord, mainCoord);
    }
}

/// <summary>shift：沿对齐轴（可选也沿主轴）把浮层推回边界内。</summary>
public sealed class ShiftMiddleware : PopperMiddleware
{
    public ShiftMiddleware() : base("shift") { }

    /// <summary>沿对齐轴（上 / 下边时为 x）平移。</summary>
    public bool MainAxis { get; init; } = true;

    /// <summary>沿边的方向（上 / 下边时为 y）平移，允许浮层盖住锚点。</summary>
    public bool CrossAxis { get; init; }

    public ShiftLimiter? Limiter { get; init; }

    public DetectOverflowOptions? Overflow { get; init; }

    public override PopperMiddlewareResult Compute(PopperState state)
    {
        float x = state.X, y = state.Y;
        var overflow = state.DetectOverflow(Overflow);
        var crossAxis = state.Placement.SideAxis;
        var mainAxis = crossAxis == Axis.X ? Axis.Y : Axis.X;
        float mainCoord = mainAxis == Axis.X ? x : y;
        float crossCoord = crossAxis == Axis.X ? x : y;
        if (MainAxis) mainCoord = ClampAxis(mainAxis, mainCoord, overflow);
        if (CrossAxis) crossCoord = ClampAxis(crossAxis, crossCoord, overflow);
        float nx = mainAxis == Axis.X ? mainCoord : crossCoord;
        float ny = mainAxis == Axis.X ? crossCoord : mainCoord;
        if (Limiter is not null)
        {
            var p = Limiter.Limit(state, nx, ny);
            nx = p.X;
            ny = p.Y;
        }
        bool enabledX = mainAxis == Axis.X ? MainAxis : CrossAxis;
        bool enabledY = mainAxis == Axis.Y ? MainAxis : CrossAxis;
        state.Data.Shift = new ShiftData(nx - x, ny - y, enabledX, enabledY);
        return new PopperMiddlewareResult { X = nx, Y = ny };
    }

    static float ClampAxis(Axis axis, float coord, in SideValues overflow)
    {
        float min = coord + (axis == Axis.Y ? overflow.Top : overflow.Left);
        float max = coord - (axis == Axis.Y ? overflow.Bottom : overflow.Right);
        return PopperEngine.Clamp(min, coord, max);
    }
}

/// <summary>size 中间件交给 <see cref="SizeMiddleware.Apply"/> 的参数。</summary>
public readonly struct SizeApplyArgs
{
    internal SizeApplyArgs(PopperState state, float availableWidth, float availableHeight)
    {
        State = state;
        AvailableWidth = availableWidth;
        AvailableHeight = availableHeight;
    }

    public PopperState State { get; }

    public float AvailableWidth { get; }

    public float AvailableHeight { get; }
}

/// <summary>
/// size：算出浮层在当前方位下的可用宽高（写入 <see cref="PopperMiddlewareData.Size"/>）。
/// <see cref="Apply"/> 可返回浮层的新尺寸（例如把 max-height 限制到可用高度后重新测量的结果）；尺寸变化时管线以新矩形重置。
/// </summary>
public sealed class SizeMiddleware : PopperMiddleware
{
    public SizeMiddleware() : base("size") { }

    /// <summary>应用可用尺寸并返回浮层的新尺寸；返回 null 表示尺寸不变。</summary>
    public Func<SizeApplyArgs, Size?>? Apply { get; init; }

    public DetectOverflowOptions? Overflow { get; init; }

    public override PopperMiddlewareResult Compute(PopperState state)
    {
        var overflow = state.DetectOverflow(Overflow);
        var placement = state.Placement;
        var side = placement.Side;
        bool isYAxis = placement.IsVertical;
        float width = state.Rects.Floating.Width;
        float height = state.Rects.Floating.Height;
        Side heightSide, widthSide;
        if (side is Side.Top or Side.Bottom)
        {
            heightSide = side;
            widthSide = placement.Align == (state.Rtl ? PopperAlign.Start : PopperAlign.End) ? Side.Left : Side.Right;
        }
        else
        {
            widthSide = side;
            heightSide = placement.Align == PopperAlign.End ? Side.Top : Side.Bottom;
        }
        float maxClipHeight = height - overflow.Top - overflow.Bottom;
        float maxClipWidth = width - overflow.Left - overflow.Right;
        float availableHeight = Math.Min(height - overflow[heightSide], maxClipHeight);
        float availableWidth = Math.Min(width - overflow[widthSide], maxClipWidth);
        var shift = state.Data.Shift;
        if (shift is not null && shift.EnabledX) availableWidth = maxClipWidth;
        if (shift is not null && shift.EnabledY) availableHeight = maxClipHeight;
        if (shift is null && placement.IsBase)
        {
            float xMin = Math.Max(overflow.Left, 0), xMax = Math.Max(overflow.Right, 0);
            float yMin = Math.Max(overflow.Top, 0), yMax = Math.Max(overflow.Bottom, 0);
            if (isYAxis)
                availableWidth = width - 2 * (xMin != 0 || xMax != 0 ? xMin + xMax : Math.Max(overflow.Left, overflow.Right));
            else
                availableHeight = height - 2 * (yMin != 0 || yMax != 0 ? yMin + yMax : Math.Max(overflow.Top, overflow.Bottom));
        }
        state.Data.Size = new SizeData(availableWidth, availableHeight);
        var next = Apply?.Invoke(new SizeApplyArgs(state, availableWidth, availableHeight));
        if (next is Size s && (s.Width != width || s.Height != height))
            return new PopperMiddlewareResult { ResetRects = new ElementRects(state.Rects.Reference, s) };
        return PopperMiddlewareResult.None;
    }
}

/// <summary>arrow：箭头在浮层内的位置，使其指向锚点中心；浮层太偏时修正浮层位置（<see cref="ArrowData.AlignmentOffset"/>）。</summary>
public sealed class ArrowMiddleware : PopperMiddleware
{
    public ArrowMiddleware(Size arrowSize) : base("arrow") => ArrowSize = arrowSize;

    public Size ArrowSize { get; }

    /// <summary>箭头与浮层两端（圆角）保持的距离。</summary>
    public SideValues Padding { get; init; }

    public override PopperMiddlewareResult Compute(PopperState state)
    {
        var placement = state.Placement;
        var axis = placement.AlignmentAxis;
        bool isY = axis == Axis.Y;
        var reference = state.Rects.Reference;
        var floating = state.Rects.Floating;
        float arrowLen = isY ? ArrowSize.Height : ArrowSize.Width;
        float coord = state.Get(axis);
        float refLen = PopperEngine.Length(reference, axis);
        float refCoord = PopperEngine.Coord(reference, axis);
        float floatLen = PopperEngine.Length(floating, axis);
        float endDiff = refLen + refCoord - coord - floatLen;
        float startDiff = coord - refCoord;
        float clientSize = floatLen;
        float centerToReference = endDiff / 2 - startDiff / 2;
        float largestPossiblePadding = clientSize / 2 - arrowLen / 2 - 1;
        float minPadding = Math.Min(isY ? Padding.Top : Padding.Left, largestPossiblePadding);
        float maxPadding = Math.Min(isY ? Padding.Bottom : Padding.Right, largestPossiblePadding);
        float min = minPadding;
        float max = clientSize - arrowLen - maxPadding;
        float center = clientSize / 2 - arrowLen / 2 + centerToReference;
        float offset = PopperEngine.Clamp(min, center, max);
        bool shouldAddOffset = state.Data.Arrow is null && !placement.IsBase && center != offset &&
            refLen / 2 - (center < min ? minPadding : maxPadding) - arrowLen / 2 < 0;
        float alignmentOffset = shouldAddOffset ? (center < min ? center - min : center - max) : 0;
        // 与 Floating UI 的数据合并语义一致：本次没有 alignmentOffset 时保留上一次的
        float? keptAlignment = shouldAddOffset ? alignmentOffset : state.Data.Arrow?.AlignmentOffset;
        state.Data.Arrow = new ArrowData(isY ? null : offset, isY ? offset : null, center - offset - alignmentOffset, keptAlignment);
        float newCoord = coord + alignmentOffset;
        return new PopperMiddlewareResult
        {
            X = isY ? null : newCoord,
            Y = isY ? newCoord : null,
            Reset = shouldAddOffset,
        };
    }
}

/// <summary>hide 的检测策略。</summary>
public enum HideStrategy : byte
{
    /// <summary>锚点被其裁剪祖先完全遮住（滚出了容器）。</summary>
    ReferenceHidden,

    /// <summary>浮层完全离开了锚点所在的裁剪边界。</summary>
    Escaped,
}

/// <summary>hide：提供隐藏浮层的依据（Radix 的 <c>hideWhenDetached</c>）。</summary>
public sealed class HideMiddleware : PopperMiddleware
{
    public HideMiddleware(HideStrategy strategy = HideStrategy.ReferenceHidden) : base("hide") => Strategy = strategy;

    public HideStrategy Strategy { get; }

    public SideValues Padding { get; init; }

    public Rect? Boundary { get; init; }

    public override PopperMiddlewareResult Compute(PopperState state)
    {
        var prev = state.Data.Hide;
        if (Strategy == HideStrategy.ReferenceHidden)
        {
            var overflow = state.DetectOverflow(new DetectOverflowOptions { Padding = Padding, Boundary = Boundary, ElementContext = PopperElementContext.Reference });
            var offsets = SideOffsets(overflow, state.Rects.Reference);
            state.Data.Hide = new HideData(AnyFullyClipped(offsets), offsets, prev?.Escaped, prev?.EscapedOffsets);
        }
        else
        {
            var overflow = state.DetectOverflow(new DetectOverflowOptions { Padding = Padding, Boundary = Boundary, AltBoundary = true });
            var offsets = SideOffsets(overflow, state.Rects.Floating);
            state.Data.Hide = new HideData(prev?.ReferenceHidden, prev?.ReferenceHiddenOffsets, AnyFullyClipped(offsets), offsets);
        }
        return PopperMiddlewareResult.None;
    }

    static SideValues SideOffsets(in SideValues o, Rect r) =>
        new(o.Top - r.Height, o.Right - r.Width, o.Bottom - r.Height, o.Left - r.Width);

    static bool AnyFullyClipped(in SideValues o) => o.Top >= 0 || o.Right >= 0 || o.Bottom >= 0 || o.Left >= 0;
}

/// <summary>autoPlacement：逐个尝试候选方位，选可用空间最多的（替代 flip，不能与 flip 同用）。</summary>
public sealed class AutoPlacementMiddleware : PopperMiddleware
{
    public AutoPlacementMiddleware() : base("autoPlacement") { }

    /// <summary>比较时把交叉轴溢出也计入（只对带对齐的方位）。</summary>
    public bool CrossAxis { get; init; }

    /// <summary>只考虑这种对齐（以及 <see cref="AutoAlignment"/> 时的反向对齐）；null 表示只考虑基本方位。</summary>
    public PopperAlign? Alignment { get; init; }

    /// <summary>允许的方位；null 为全部 12 个。</summary>
    public IReadOnlyList<Placement>? AllowedPlacements { get; init; }

    public bool AutoAlignment { get; init; } = true;

    public DetectOverflowOptions? Overflow { get; init; }

    internal List<Placement> GetPlacementList()
    {
        var allowed = AllowedPlacements ?? Placement.All;
        var align = Alignment is PopperAlign a && a != PopperAlign.Center ? a : (PopperAlign?)null;
        if (AllowedPlacements is not null && Alignment is null) return new List<Placement>(allowed);
        List<Placement> sorted;
        if (align.HasValue)
        {
            sorted = allowed.Where(p => p.Align == align.Value).ToList();
            sorted.AddRange(allowed.Where(p => p.Align != align.Value));
        }
        else
        {
            sorted = allowed.Where(p => p.IsBase).ToList();
        }
        return sorted.Where(p => !align.HasValue || p.Align == align.Value || (AutoAlignment && !p.IsBase)).ToList();
    }

    public override PopperMiddlewareResult Compute(PopperState state)
    {
        var placements = GetPlacementList();
        var data = state.Data;
        var overflow = state.DetectOverflow(Overflow);
        int currentIndex = data.AutoPlacement?.Index ?? 0;
        if (currentIndex >= placements.Count) return PopperMiddlewareResult.None;
        var current = placements[currentIndex];
        if (state.Placement != current) return new PopperMiddlewareResult { ResetPlacement = placements[0] };
        var (a, b) = PopperEngine.GetAlignmentSides(current, state.Rects, state.Rtl);
        var all = new List<PlacementOverflow>(data.AutoPlacement?.Overflows ?? Array.Empty<PlacementOverflow>())
        {
            new(current, new[] { overflow[current.Side], overflow[a], overflow[b] }),
        };
        if (currentIndex + 1 < placements.Count)
        {
            data.AutoPlacement = new AutoPlacementData(currentIndex + 1, all);
            return new PopperMiddlewareResult { ResetPlacement = placements[currentIndex + 1] };
        }
        var byMostSpace = all
            .Select((d, order) => (d.Placement, Key: !d.Placement.IsBase && CrossAxis ? d.Overflows[0] + d.Overflows[1] : d.Overflows[0], d.Overflows, order))
            .OrderBy(t => t.Key).ThenBy(t => t.order)
            .ToList();
        var fits = byMostSpace.Where(t => t.Overflows.Take(t.Placement.IsBase ? 3 : 2).All(v => v <= 0)).ToList();
        var reset = fits.Count > 0 ? fits[0].Placement : byMostSpace[0].Placement;
        if (reset != state.Placement)
        {
            data.AutoPlacement = new AutoPlacementData(currentIndex + 1, all);
            return new PopperMiddlewareResult { ResetPlacement = reset };
        }
        return PopperMiddlewareResult.None;
    }
}

/// <summary>Radix 的 transformOrigin 中间件：让缩放进出场动画以箭头尖（或锚点方向的边）为原点。</summary>
public sealed class TransformOriginMiddleware : PopperMiddleware
{
    public TransformOriginMiddleware(float arrowWidth, float arrowHeight) : base("transformOrigin")
    {
        ArrowWidth = arrowWidth;
        ArrowHeight = arrowHeight;
    }

    public float ArrowWidth { get; }

    public float ArrowHeight { get; }

    public override PopperMiddlewareResult Compute(PopperState state)
    {
        var arrow = state.Data.Arrow;
        bool arrowHidden = arrow is null || arrow.CenterOffset != 0;
        float aw = arrowHidden ? 0 : ArrowWidth;
        float ah = arrowHidden ? 0 : ArrowHeight;
        var placement = state.Placement;
        string noArrowAlign = placement.Align switch { PopperAlign.Start => "0%", PopperAlign.End => "100%", _ => "50%" };
        float arrowXCenter = (arrow?.X ?? 0) + aw / 2;
        float arrowYCenter = (arrow?.Y ?? 0) + ah / 2;
        string x, y;
        switch (placement.Side)
        {
            case Side.Bottom:
                x = arrowHidden ? noArrowAlign : Px(arrowXCenter);
                y = Px(-ah);
                break;
            case Side.Top:
                x = arrowHidden ? noArrowAlign : Px(arrowXCenter);
                y = Px(state.Rects.Floating.Height + ah);
                break;
            case Side.Right:
                x = Px(-ah);
                y = arrowHidden ? noArrowAlign : Px(arrowYCenter);
                break;
            default:
                x = Px(state.Rects.Floating.Width + ah);
                y = arrowHidden ? noArrowAlign : Px(arrowYCenter);
                break;
        }
        state.Data.TransformOrigin = new TransformOriginData(x, y);
        return PopperMiddlewareResult.None;
    }

    internal static string Px(float v) => FormatNumber(v) + "px";

    internal static string FormatNumber(float v)
    {
        if (v == 0) return "0";   // 包括 -0
        return v.ToString("R", CultureInfo.InvariantCulture);
    }
}
