using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>Lays out resizable panels along one axis and reports the resulting size percentages.</summary>
public sealed partial class ResizablePanelGroup : Component
{

    const string GroupClass = "flex h-full w-full aria-[orientation=vertical]:flex-col";

    readonly ResizableScope _scope = new();
    readonly Ref<Element> _element = new();

    public Orientation Orientation { get; set; }
    public Direction? Dir { get; set; }

    /// <summary>Controlled panel percentages. When null the group keeps its own sizes.</summary>
    public IReadOnlyList<double>? Sizes { get; set; }

    /// <summary>Receives every size change, whether from a drag, a key press or a reset.</summary>
    public Action<IReadOnlyList<double>>? SizesChanged { get; set; }
    public Action<IReadOnlyList<double>>? OnLayoutChange { get; set; }

    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        bool horizontal = Orientation == Orientation.Horizontal;
        var panels = new List<ResizablePanel>();
        var handles = new List<ResizableHandle>();
        ResizableTree.Collect(Children, panels, handles);

        var defaults = ComputeDefaults(panels);
        _scope.Panels = panels;
        _scope.Handles = handles;
        _scope.Orientation = Orientation;
        _scope.Dir = Dir;
        _scope.GroupRef = Ref ?? _element;
        _scope.Defaults = defaults;

        bool controlled = Sizes is not null;
        if (controlled)
        {
            _scope.SizesState = null;
            _scope.ControlledSizes = Normalize(Sizes!, panels.Count, defaults);
        }
        else
        {
            _scope.ControlledSizes = null;
            if (_scope.SizesState is null || _scope.SizesState.Value.Length != panels.Count)
                _scope.SizesState = new State<double[]>(defaults);
        }
        _scope.Changed = next => (SizesChanged + OnLayoutChange)?.Invoke(next);

        // Controlled sizes live in props; bumping the revision rebuilds panels after the new value arrives.
        UseEffect(() =>
        {
            if (controlled) _scope.Version.Value = _scope.Version.Value + 1;
            return null;
        }, controlled, Sizes);

        var node = TabsParts.Style(new Div
        {
            Ref = Ref ?? _element,
            Children = Children ?? new NodeList(),
        }, "resizable-panel-group", GroupClass, Class, Rest);
        node.Attributes["data-orientation"] = TabsParts.Axis(Orientation);
        node.Attributes["aria-orientation"] = TabsParts.Axis(Orientation);
        if (Dir is { } dir) node.Attributes["dir"] = dir == Direction.Rtl ? "rtl" : "ltr";
        AppendStyle(node, "display:flex;min-width:0;min-height:0;box-sizing:border-box;"
            + (horizontal ? "flex-direction:row;" : "flex-direction:column;"));
        return Provide(_scope, node);
    }

    static double[] ComputeDefaults(List<ResizablePanel> panels)
    {
        int count = panels.Count;
        var sizes = new double[count];
        double specified = 0;
        int unspecified = 0;
        for (int i = 0; i < count; i++)
        {
            double size = panels[i].DefaultSize ?? 0;
            if (size > 0)
            {
                sizes[i] = size;
                specified += size;
            }
            else
            {
                unspecified++;
            }
        }
        if (unspecified > 0)
        {
            double share = Math.Max(0, 100 - specified) / unspecified;
            for (int i = 0; i < count; i++)
                if (!(sizes[i] > 0)) sizes[i] = share;
        }
        double sum = 0;
        foreach (double size in sizes) sum += size;
        if (sum <= 0 && count > 0)
            for (int i = 0; i < count; i++) sizes[i] = 100.0 / count;
        return Normalize(sizes, count, sizes);
    }

    static double[] Normalize(IReadOnlyList<double> values, int count, double[] fallback)
    {
        var sizes = new double[count];
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            double value = i < values.Count ? values[i] : fallback[i];
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) value = 0;
            sizes[i] = value;
            sum += value;
        }
        if (sum <= 0)
        {
            for (int i = 0; i < count; i++) sizes[i] = count == 0 ? 0 : 100.0 / count;
            return sizes;
        }
        double scale = 100 / sum;
        for (int i = 0; i < count; i++) sizes[i] *= scale;
        return sizes;
    }

    internal static void AppendStyle(ElementNode node, string declarations)
    {
        string existing = node.Style.Text?.Trim().TrimEnd(';') ?? "";
        node.Style = new StyleValue(existing.Length == 0 ? declarations : existing + "; " + declarations);
    }
}

/// <summary>One pane of a <see cref="ResizablePanelGroup"/>.</summary>
public sealed partial class ResizablePanel : Component
{
    public double? DefaultSize { get; set; }
    public double MinSize { get; set; }
    public double MaxSize { get; set; } = 100;
    public bool Collapsible { get; set; }
    public double CollapsedSize { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    internal int PanelIndex { get; set; } = -1;

    protected override Node Build()
    {
        var scope = Use<ResizableScope>();
        _ = scope.Version.Value;
        double[] sizes = scope.Sizes;
        int index = PanelIndex;
        double size = index >= 0 && index < sizes.Length ? sizes[index] : 0;

        var node = TabsParts.Style(new Div { Ref = Ref, Children = Children ?? new NodeList() },
            "resizable-panel", null, Class, Rest);
        ResizablePanelGroup.AppendStyle(node,
            "flex:0 0 " + F(size) + "%;min-width:0;min-height:0;box-sizing:border-box;" +
            (size <= 0.001 ? "overflow:hidden;" : "overflow:auto;"));
        node.Attributes["data-size"] = F(size);
        return node;
    }

    internal static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>The draggable divider between two panes. Arrow keys resize; a double click restores the defaults.</summary>
public sealed partial class ResizableHandle : Component
{
    const string HandleClass =
        "relative flex w-px items-center justify-center bg-border after:absolute after:inset-y-0 after:left-1/2 " +
        "after:w-1 after:-translate-x-1/2 focus-visible:ring-1 focus-visible:ring-ring focus-visible:ring-offset-1 " +
        "focus-visible:outline-hidden aria-[orientation=horizontal]:h-px aria-[orientation=horizontal]:w-full " +
        "aria-[orientation=horizontal]:after:left-0 aria-[orientation=horizontal]:after:h-1 " +
        "aria-[orientation=horizontal]:after:w-full aria-[orientation=horizontal]:after:translate-x-0 " +
        "aria-[orientation=horizontal]:after:-translate-y-1/2";

    readonly Ref<Element> _element = new();
    Element? _captureElement;
    bool _dragging;
    bool _hasLastDown;
    long _pointerId;
    double _lastDownMs;
    Point _lastDownPosition;
    Point _startPosition;
    double[] _startSizes = [];

    public bool WithHandle { get; set; }
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    internal int BoundaryIndex { get; set; } = -1;

    protected override Node Build()
    {
        var scope = Use<ResizableScope>();
        _ = scope.Version.Value;
        double[] sizes = scope.Sizes;
        bool horizontal = scope.Orientation == Orientation.Horizontal;
        int boundary = BoundaryIndex;
        bool valid = boundary >= 0 && boundary + 1 < sizes.Length;
        bool disabled = Disabled || !valid;

        var node = TabsParts.Style(new Div
        {
            Role = Role.Separator,
            TabIndex = disabled ? null : 0,
            Ref = Ref ?? _element,
            Children = new NodeList(),
        }, "resizable-handle", HandleClass, Class, Rest);

        node.Attributes["aria-orientation"] = horizontal ? "vertical" : "horizontal";
        node.Attributes["aria-valuemin"] = "0";
        node.Attributes["aria-valuemax"] = "100";
        node.Attributes["aria-valuenow"] = valid
            ? Math.Round(sizes[boundary]).ToString(CultureInfo.InvariantCulture)
            : "0";
        node.Attributes["data-disabled"] = disabled ? "true" : null;
        if (disabled) node.Attributes["aria-disabled"] = "true";
        node.AriaLabel ??= "Resize panels";
        ResizablePanelGroup.AppendStyle(node,
            "flex:0 0 1px;position:relative;touch-action:none;box-sizing:border-box;"
            + (disabled ? "cursor:default;" : horizontal ? "cursor:col-resize;" : "cursor:row-resize;"));

        if (WithHandle)
        {
            // The official grip box; Lucide's GripVerticalIcon is not in the bundled icon subset, so the
            // nearest available glyph (MoreHorizontal) is rotated for a vertical group.
            var grip = new Div { Class = "z-10 flex h-4 w-3 items-center justify-center rounded-xs border bg-border" };
            grip.Children.Add(SelectParts.Glyph(LucideIcons.MoreHorizontal, "resizable-handle-icon",
                horizontal ? "size-2.5" : "size-2.5 rotate-90"));
            node.Children.Add(grip);
        }
        node.Children.AddRange(Children);

        if (!disabled)
        {
            node.OnPointerDown = EventComposition.Chain(node.OnPointerDown, e => PointerDown(scope, e, horizontal));
            node.OnPointerMove = EventComposition.Chain(node.OnPointerMove, e => PointerMove(scope, e, horizontal));
            node.OnPointerUp = EventComposition.Chain(node.OnPointerUp, PointerUp);
            node.OnPointerCancel = EventComposition.Chain(node.OnPointerCancel, _ => EndDrag());
            node.OnKeyDown = EventComposition.Chain(node.OnKeyDown, e => KeyDown(scope, e, horizontal));
        }
        return node;
    }

    void PointerDown(ResizableScope scope, PointerEvent e, bool horizontal)
    {
        if (e.DefaultPrevented || e.Button != PointerButtons.Left) return;
        double now = e.Timestamp.TotalMilliseconds;
        bool doubleClick = _hasLastDown && now - _lastDownMs <= 400
            && Math.Abs(e.Position.X - _lastDownPosition.X) <= 4
            && Math.Abs(e.Position.Y - _lastDownPosition.Y) <= 4;
        _hasLastDown = true;
        _lastDownMs = now;
        _lastDownPosition = e.Position;
        e.PreventDefault();
        if (doubleClick)
        {
            scope.Reset();
            return;
        }

        if (e.CurrentTarget is not { IsDisabled: false } handle) return;
        handle.SetPointerCapture(e.PointerId);
        _captureElement = handle;
        _dragging = true;
        _pointerId = e.PointerId;
        _startPosition = e.Position;
        _startSizes = (double[])scope.Sizes.Clone();
        _ = horizontal;
    }

    void PointerMove(ResizableScope scope, PointerEvent e, bool horizontal)
    {
        if (!_dragging || e.PointerId != _pointerId || scope.GroupElement is not { } group) return;
        if (_startSizes.Length < 2 || BoundaryIndex < 0 || BoundaryIndex + 1 >= _startSizes.Length) return;
        var rect = group.BoundingRect;
        double extent = horizontal ? rect.Width : rect.Height;
        if (!(extent > 0)) return;
        double delta = horizontal ? e.Position.X - _startPosition.X : e.Position.Y - _startPosition.Y;
        if (horizontal && scope.IsRtl(group)) delta = -delta;
        double desired = _startSizes[BoundaryIndex] + delta / extent * 100;
        ApplyBoundary(scope, BoundaryIndex, desired);
    }

    void PointerUp(PointerEvent e)
    {
        if (e.PointerId == _pointerId) EndDrag();
    }

    void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _captureElement?.ReleasePointerCapture(_pointerId);
        _captureElement = null;
    }

    void KeyDown(ResizableScope scope, KeyEvent e, bool horizontal)
    {
        if (e.DefaultPrevented || e.Modifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
        int boundary = BoundaryIndex;
        double[] sizes = scope.Sizes;
        if (boundary < 0 || boundary + 1 >= sizes.Length) return;
        var (lower, upper, total) = BoundaryBounds(scope, boundary, sizes);

        double? desired = e.Key switch
        {
            "Home" => 0,
            "End" => total,
            _ => DirectionStep(scope, e.Key, horizontal) switch
            {
                0 => null,
                int sign => ShadcnMath.Clamp(sizes[boundary] + sign * (e.Modifiers == KeyModifiers.Shift ? 10 : 5), 0, total),
            },
        };
        if (desired is not { } value) return;
        e.PreventDefault();
        ApplyBoundary(scope, boundary, ShadcnMath.Clamp(value, lower, upper));
    }

    static int DirectionStep(ResizableScope scope, string key, bool horizontal)
    {
        if (horizontal)
        {
            bool rtl = scope.IsRtl(scope.GroupElement);
            return key switch
            {
                "ArrowRight" => rtl ? -1 : 1,
                "ArrowLeft" => rtl ? 1 : -1,
                _ => 0,
            };
        }
        return key switch
        {
            "ArrowDown" => 1,
            "ArrowUp" => -1,
            _ => 0,
        };
    }

    static void ApplyBoundary(ResizableScope scope, int boundary, double desiredLeft)
    {
        double[] sizes = scope.Sizes;
        if (boundary < 0 || boundary + 1 >= sizes.Length) return;
        var (lower, upper, total) = BoundaryBounds(scope, boundary, sizes);
        double left = ShadcnMath.Clamp(desiredLeft, Math.Max(0, lower), Math.Min(total, upper));
        var next = (double[])sizes.Clone();
        next[boundary] = left;
        next[boundary + 1] = total - left;
        scope.Apply(next);
    }

    static (double Lower, double Upper, double Total) BoundaryBounds(ResizableScope scope, int boundary, double[] sizes)
    {
        double total = sizes[boundary] + sizes[boundary + 1];
        double minLeft = MinSizeOf(scope, boundary);
        double maxLeft = MaxSizeOf(scope, boundary);
        double minRight = MinSizeOf(scope, boundary + 1);
        double maxRight = MaxSizeOf(scope, boundary + 1);
        double lower = Math.Max(minLeft, total - maxRight);
        double upper = Math.Min(maxLeft, total - minRight);
        if (upper < lower)
        {
            double middle = (lower + upper) / 2;
            lower = upper = middle;
        }
        return (lower, upper, total);
    }

    static double MinSizeOf(ResizableScope scope, int index)
    {
        if (index < 0 || index >= scope.Panels.Count) return 0;
        var panel = scope.Panels[index];
        return panel.Collapsible ? Math.Max(0, panel.CollapsedSize) : Math.Max(0, panel.MinSize);
    }

    static double MaxSizeOf(ResizableScope scope, int index)
    {
        if (index < 0 || index >= scope.Panels.Count) return 100;
        var panel = scope.Panels[index];
        return panel.MaxSize <= 0 ? 100 : Math.Max(MinSizeOf(scope, index), panel.MaxSize);
    }
}

internal sealed class ResizableScope
{
    internal Orientation Orientation;
    internal Direction? Dir;
    internal Ref<Element>? GroupRef;
    internal List<ResizablePanel> Panels = new();
    internal List<ResizableHandle> Handles = new();
    internal double[] Defaults = [];
    internal State<double[]>? SizesState;
    internal double[]? ControlledSizes;
    internal readonly State<long> Version = new(0);
    internal Action<double[]>? Changed;

    internal Element? GroupElement => GroupRef?.Current;

    internal double[] Sizes => ControlledSizes ?? SizesState?.Value ?? Defaults;

    internal bool IsRtl(Element? element) =>
        Dir == Direction.Rtl || (Dir is null && TabsParts.ResolveDirection(element, null) == Direction.Rtl);

    internal void Apply(double[] sizes)
    {
        if (ControlledSizes is null && SizesState is not null) SizesState.Value = sizes;
        Changed?.Invoke(sizes);
    }

    internal void Reset() => Apply((double[])Defaults.Clone());
}

internal static class ResizableTree
{
    internal static void Collect(NodeList? children, List<ResizablePanel> panels, List<ResizableHandle> handles)
    {
        if (children is null) return;
        foreach (var child in children) Visit(child, panels, handles);
    }

    static void Visit(Node? node, List<ResizablePanel> panels, List<ResizableHandle> handles)
    {
        switch (node)
        {
            case null:
                return;
            case ResizablePanel panel:
                panel.PanelIndex = panels.Count;
                panels.Add(panel);
                return;
            case ResizableHandle handle:
                handle.BoundaryIndex = panels.Count - 1;
                handles.Add(handle);
                return;
            case Fragment fragment:
                Collect(fragment.Children, panels, handles);
                return;
            case ElementNode element:
                Collect(element.Children, panels, handles);
                return;
        }
    }
}
