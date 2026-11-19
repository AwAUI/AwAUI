using System;
using System.Collections.Generic;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 定位中间件（Floating UI 的 middleware）：读取当前坐标与方位，返回修正后的坐标、写入自己的数据，或要求从头重跑管线（reset）。
/// 中间件必须是纯计算：同样的 <see cref="PopperState"/> 给出同样的结果。内建实现见 <see cref="OffsetMiddleware"/> 等。
/// </summary>
public abstract class PopperMiddleware
{
    protected PopperMiddleware(string name) => Name = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>中间件名（自定义数据在 <see cref="PopperMiddlewareData"/> 中以它为键）。</summary>
    public string Name { get; }

    /// <summary>计算一步。</summary>
    public abstract PopperMiddlewareResult Compute(PopperState state);

    public override string ToString() => Name;
}

/// <summary>中间件的返回值：新坐标（null 表示不变）与重置请求。</summary>
public readonly struct PopperMiddlewareResult
{
    public float? X { get; init; }

    public float? Y { get; init; }

    /// <summary>请求从第一个中间件重新开始（Floating UI 的 <c>reset: true</c>）。</summary>
    public bool Reset { get; init; }

    /// <summary>重置并改用此方位（<c>reset: { placement }</c>）。</summary>
    public Placement? ResetPlacement { get; init; }

    /// <summary>重置并改用新的矩形（<c>reset: { rects }</c>，例如 size 中间件改变了浮层尺寸）。</summary>
    public ElementRects? ResetRects { get; init; }

    /// <summary>不改变任何东西。</summary>
    public static PopperMiddlewareResult None => default;

    internal bool WantsReset => Reset || ResetPlacement.HasValue || ResetRects.HasValue;
}

/// <summary>
/// 管线当前状态。中间件只读；引擎在每步之间更新。<see cref="Data"/> 在重置之间保留（与 Floating UI 的 <c>middlewareData</c> 相同），
/// flip / autoPlacement 依靠它记住已尝试过的方位。
/// </summary>
public sealed class PopperState
{
    internal PopperState(Placement initial, ElementRects rects, PopperEnvironment env)
    {
        InitialPlacement = initial;
        Placement = initial;
        Rects = rects;
        Environment = env;
        Data = new PopperMiddlewareData();
    }

    public float X { get; internal set; }

    public float Y { get; internal set; }

    /// <summary>调用方请求的方位。</summary>
    public Placement InitialPlacement { get; }

    /// <summary>当前方位（flip / autoPlacement 重置后会变化）。</summary>
    public Placement Placement { get; internal set; }

    public ElementRects Rects { get; internal set; }

    public PopperEnvironment Environment { get; }

    public bool Rtl => Environment.Rtl;

    /// <summary>各中间件的输出数据。</summary>
    public PopperMiddlewareData Data { get; }

    /// <summary>当前坐标下的溢出量（正值 = 超出边界的像素数）。</summary>
    public SideValues DetectOverflow(DetectOverflowOptions? options = null) => PopperEngine.DetectOverflow(this, options);

    internal float Get(Axis axis) => axis == Axis.X ? X : Y;
}

/// <summary>内建中间件的输出数据，以及自定义中间件按名存放的数据。</summary>
public sealed class PopperMiddlewareData
{
    Dictionary<string, object?>? _custom;

    public OffsetData? Offset { get; internal set; }

    public ShiftData? Shift { get; internal set; }

    public FlipData? Flip { get; internal set; }

    public SizeData? Size { get; internal set; }

    public ArrowData? Arrow { get; internal set; }

    public HideData? Hide { get; internal set; }

    public AutoPlacementData? AutoPlacement { get; internal set; }

    public TransformOriginData? TransformOrigin { get; internal set; }

    /// <summary>读取自定义中间件的数据。</summary>
    public bool TryGetCustom(string name, out object? value)
    {
        value = null;
        return _custom is not null && _custom.TryGetValue(name, out value);
    }

    /// <summary>写入自定义中间件的数据（覆盖同名旧值）。</summary>
    public void SetCustom(string name, object? value)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        (_custom ??= new Dictionary<string, object?>(StringComparer.Ordinal))[name] = value;
    }
}

/// <summary>offset 中间件施加的位移与当时的方位。</summary>
public sealed class OffsetData
{
    internal OffsetData(float x, float y, Placement placement)
    {
        X = x;
        Y = y;
        Placement = placement;
    }

    public float X { get; }
    public float Y { get; }
    public Placement Placement { get; }
}

/// <summary>shift 中间件施加的位移，以及两个轴是否启用了平移（size 中间件据此放宽可用尺寸）。</summary>
public sealed class ShiftData
{
    internal ShiftData(float x, float y, bool enabledX, bool enabledY)
    {
        X = x;
        Y = y;
        EnabledX = enabledX;
        EnabledY = enabledY;
    }

    public float X { get; }
    public float Y { get; }
    public bool EnabledX { get; }
    public bool EnabledY { get; }
}

/// <summary>某个尝试过的方位的溢出记录（flip / autoPlacement）。</summary>
public readonly struct PlacementOverflow
{
    internal PlacementOverflow(Placement placement, float[] overflows)
    {
        Placement = placement;
        _overflows = overflows;
    }

    readonly float[] _overflows;

    public Placement Placement { get; }

    /// <summary>检查的各边溢出量：主边在前，随后是两条对齐边（按检查选项可能缺省）。</summary>
    public IReadOnlyList<float> Overflows => _overflows ?? Array.Empty<float>();
}

/// <summary>flip 中间件的进度：下一个候选的序号与已尝试方位的溢出记录。</summary>
public sealed class FlipData
{
    internal FlipData(int index, IReadOnlyList<PlacementOverflow> overflows)
    {
        Index = index;
        Overflows = overflows;
    }

    public int Index { get; }
    public IReadOnlyList<PlacementOverflow> Overflows { get; }
}

/// <summary>autoPlacement 中间件的进度。</summary>
public sealed class AutoPlacementData
{
    internal AutoPlacementData(int index, IReadOnlyList<PlacementOverflow> overflows)
    {
        Index = index;
        Overflows = overflows;
    }

    public int Index { get; }
    public IReadOnlyList<PlacementOverflow> Overflows { get; }
}

/// <summary>size 中间件算出的可用尺寸（Radix 写入 <c>--radix-popper-available-width/height</c>）。</summary>
public sealed class SizeData
{
    internal SizeData(float availableWidth, float availableHeight)
    {
        AvailableWidth = availableWidth;
        AvailableHeight = availableHeight;
    }

    public float AvailableWidth { get; }
    public float AvailableHeight { get; }
}

/// <summary>
/// arrow 中间件的输出：箭头在浮层内沿对齐轴的位置（另一轴为 null），以及箭头无法对准锚点中心的偏差 <see cref="CenterOffset"/>
/// （Radix 在它不为 0 时隐藏箭头）。<see cref="AlignmentOffset"/> 是为了让箭头仍落在锚点上而对浮层施加的额外位移。
/// </summary>
public sealed class ArrowData
{
    internal ArrowData(float? x, float? y, float centerOffset, float? alignmentOffset)
    {
        X = x;
        Y = y;
        CenterOffset = centerOffset;
        AlignmentOffset = alignmentOffset;
    }

    public float? X { get; }
    public float? Y { get; }
    public float CenterOffset { get; }
    public float? AlignmentOffset { get; }
}

/// <summary>hide 中间件的输出：锚点是否被裁剪祖先完全遮住、浮层是否完全逃出了锚点的裁剪边界。</summary>
public sealed class HideData
{
    internal HideData(bool? referenceHidden, SideValues? referenceHiddenOffsets, bool? escaped, SideValues? escapedOffsets)
    {
        ReferenceHidden = referenceHidden;
        ReferenceHiddenOffsets = referenceHiddenOffsets;
        Escaped = escaped;
        EscapedOffsets = escapedOffsets;
    }

    public bool? ReferenceHidden { get; }
    public SideValues? ReferenceHiddenOffsets { get; }
    public bool? Escaped { get; }
    public SideValues? EscapedOffsets { get; }
}

/// <summary>Radix 的 transformOrigin 中间件输出（CSS 长度文本，如 <c>12px</c> 或 <c>50%</c>）。</summary>
public sealed class TransformOriginData
{
    internal TransformOriginData(string x, string y)
    {
        X = x;
        Y = y;
    }

    public string X { get; }
    public string Y { get; }

    /// <summary><c>transform-origin</c> 的完整取值。</summary>
    public override string ToString() => X + " " + Y;
}
