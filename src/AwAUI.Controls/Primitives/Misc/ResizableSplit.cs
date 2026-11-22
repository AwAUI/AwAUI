using System;
using System.Globalization;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// Two panes separated by a draggable separator. The split position is a fraction of the container between 0 and 1,
/// and can be controlled (<see cref="Position"/> + <see cref="PositionChanged"/>) or managed by the primitive
/// (<see cref="DefaultPosition"/>). Shared by the Controls <c>GridSplitter</c> and the Shadcn Resizable family.
/// </summary>
/// <remarks>
/// The separator is a keyboard-focusable <c>separator</c>: arrow keys move it by 2% (Shift: 10%), Home and End jump to
/// the minimum and maximum. Both panes keep at least <see cref="MinSize"/> logical pixels when the container is large
/// enough. Dragging captures the pointer, so a pan that scrolls does not lose the drag.
/// </remarks>
public sealed partial class ResizableSplit : Control
{
    readonly ControllableState<double> _position = new();
    readonly Ref<Element> _container = new();
    readonly Ref<Element> _bar = new();
    bool _dragging;
    long _pointerId;
    Point _startPointer;
    double _startFraction;

    /// <summary>Class name applied to the container in addition to the caller's classes.</summary>
    public string BaseClass { get; set; } = "aw-resizable-split";

    public Orientation Orientation { get; set; } = Orientation.Horizontal;

    /// <summary>Set to control the split fraction (0 to 1), or leave null to let the primitive manage it.</summary>
    public double? Position { get; set; }

    public double DefaultPosition { get; set; } = 0.5;

    /// <summary>Requests a change to <see cref="Position"/>. <c>@bind</c>-able.</summary>
    public Action<double>? PositionChanged { get; set; }

    /// <summary>Minimum size of each pane, in logical pixels.</summary>
    public double MinSize { get; set; } = 64;

    /// <summary>Thickness of the separator, in logical pixels.</summary>
    public double SplitterSize { get; set; } = 6;

    public bool IsEnabled { get; set; } = true;

    public string? SplitterAriaLabel { get; set; }

    public Node? First { get; set; }

    public Node? Second { get; set; }

    protected override Node Build()
    {
        bool horizontal = Orientation == Orientation.Horizontal;
        double fraction = Clamp(_position.Sync(Position, DefaultPosition, PositionChanged), horizontal);

        var first = new Div { Children = First is null ? [] : [First] };
        first.Attributes["part"] = "pane";
        first.Style = "overflow:auto;min-width:0;min-height:0;flex:0 0 " + Percent(fraction) + "%;";

        var bar = new Div
        {
            Role = Role.Separator,
            TabIndex = IsEnabled ? 0 : -1,
            Ref = _bar,
            OnPointerDown = e => OnPointerDown(e, horizontal),
            OnPointerMove = e => OnPointerMove(e, horizontal),
            OnPointerUp = OnPointerUp,
            OnPointerCancel = _ => EndDrag(),
            OnKeyDown = e => OnKeyDown(e, horizontal),
        };
        bar.Attributes["part"] = "splitter";
        bar.AriaLabel = SplitterAriaLabel;
        bar.Attributes["aria-orientation"] = horizontal ? "vertical" : "horizontal";
        bar.Attributes["aria-valuemin"] = "0";
        bar.Attributes["aria-valuemax"] = "100";
        bar.Attributes["aria-valuenow"] = Math.Round(fraction * 100).ToString(CultureInfo.InvariantCulture);
        bar.Attributes["aria-disabled"] = IsEnabled ? null : "true";
        bar.Style = "flex:0 0 " + Length(SplitterSize) + "px;touch-action:none;"
            + (IsEnabled ? (horizontal ? "cursor:col-resize;" : "cursor:row-resize;") : "cursor:default;");

        var second = new Div { Children = Second is null ? [] : [Second] };
        second.Attributes["part"] = "pane";
        second.Style = "overflow:auto;min-width:0;min-height:0;flex:1 1 0;";

        var container = new Div { Ref = _container, Children = [first, bar, second] };
        container.Attributes["part"] = "container";
        container.Style = "display:flex;box-sizing:border-box;min-width:0;min-height:0;"
            + (horizontal ? "flex-direction:row;" : "flex-direction:column;");
        Apply(container, BaseClass);
        container.Ref = Ref ?? _container;
        return container;
    }

    void OnPointerDown(PointerEvent e, bool horizontal)
    {
        if (!IsEnabled || e.DefaultPrevented || e.Button != PointerButtons.Left
            || e.CurrentTarget is not { IsDisabled: false } bar) return;
        e.PreventDefault();
        bar.SetPointerCapture(e.PointerId);
        _dragging = true;
        _pointerId = e.PointerId;
        _startPointer = e.Position;
        _startFraction = _position.Value;
    }

    void OnPointerMove(PointerEvent e, bool horizontal)
    {
        if (!_dragging || e.PointerId != _pointerId || _container.Current is not { } container) return;
        var rect = container.BoundingRect;
        float extent = horizontal ? rect.Width : rect.Height;
        if (!(extent > 0)) return;
        float delta = (horizontal ? e.Position.X - _startPointer.X : e.Position.Y - _startPointer.Y) / extent;
        _position.Set(Clamp(_startFraction + delta, horizontal));
    }

    void OnPointerUp(PointerEvent e)
    {
        if (e.PointerId == _pointerId) EndDrag();
    }

    void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _bar.Current?.ReleasePointerCapture(_pointerId);
    }

    void OnKeyDown(KeyEvent e, bool horizontal)
    {
        if (!IsEnabled || e.DefaultPrevented || e.Modifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
        double step = (e.Modifiers & KeyModifiers.Shift) != 0 ? 0.1 : 0.02;
        double? next = e.Key switch
        {
            "ArrowLeft" when horizontal => _position.Value - step,
            "ArrowRight" when horizontal => _position.Value + step,
            "ArrowUp" when !horizontal => _position.Value - step,
            "ArrowDown" when !horizontal => _position.Value + step,
            "Home" => 0,
            "End" => 1,
            _ => null,
        };
        if (next is not { } value) return;
        e.PreventDefault();
        _position.Set(Clamp(value, horizontal));
    }

    double Clamp(double fraction, bool horizontal)
    {
        if (double.IsNaN(fraction)) return 0.5;
        var rect = _container.Current?.BoundingRect;
        float extent = horizontal ? rect?.Width ?? 0 : rect?.Height ?? 0;
        double minimum = 0, maximum = 1;
        if (extent > 0 && MinSize > 0)
        {
            minimum = Math.Min(0.5, MinSize / extent);
            maximum = Math.Max(0.5, 1 - (MinSize + SplitterSize) / extent);
        }
        return Math.Min(maximum, Math.Max(minimum, fraction));
    }

    static string Percent(double value) => Length(value * 100);

    static string Length(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
