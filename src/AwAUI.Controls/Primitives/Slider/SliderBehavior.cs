using System;
using System.Globalization;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls.Primitives;

/// <summary>Shared behavior and unstyled parts for a horizontal, single-value slider.</summary>
/// <remarks>Create one instance per component. Call <see cref="Attach"/> during Build after applying caller attributes to the host.</remarks>
public sealed class SliderBehavior
{
    readonly ControllableState<double> _value = new();
    readonly State<Drag?> _drag = new((Drag?)null);
    readonly Ref<Element> _track = new();

    /// <summary>Attaches slider semantics and input handlers to the host and returns its parts for styling.</summary>
    public (Div Track, Div Range, Div Thumb) Attach(UIButton root, double min, double max, double step,
        double? value, double? defaultValue, Action<double>? onValueChange,
        string? valueText = null, Func<double, string?>? formatValueText = null)
    {
        var scale = new SliderScale(min, max, step);
        double current = scale.Clamp(_value.Sync(value, scale.Clamp(defaultValue ?? min), onValueChange));
        string percent = (scale.Fraction(current) * 100).ToString("0.##########", CultureInfo.InvariantCulture) + "%";

        root.Role = Role.Slider;
        bool disabled = root.Disabled;
        string state = disabled ? "disabled" : _drag.Value is null ? "idle" : "dragging";
        SetPart(root, "slider", state, disabled);
        root.Attributes["type"] = "button";
        root.Attributes["data-orientation"] = "horizontal";
        root.Attributes["aria-orientation"] = "horizontal";
        root.Attributes["aria-valuemin"] = Number(min);
        root.Attributes["aria-valuemax"] = Number(max);
        root.Attributes["aria-valuenow"] = Number(current);
        root.Attributes["aria-valuetext"] = formatValueText?.Invoke(current) ?? valueText ?? root.Attributes["aria-valuetext"];
        root.Attributes["aria-disabled"] = disabled ? "true" : null;

        var range = new Div { Style = "width: " + percent };
        SetPart(range, "slider-range", state, disabled);
        var thumb = new Div { Style = "left: " + percent + "; transform: translate(-50%, -50%)" };
        SetPart(thumb, "slider-thumb", state, disabled);
        var track = new Div { Ref = _track, Children = [range, thumb] };
        SetPart(track, "slider-track", state, disabled);
        track.Attributes["aria-hidden"] = "true";
        root.Children.Add(track);

        root.OnKeyDown = EventComposition.Compose(root.OnKeyDown, e => KeyDown(e, scale, disabled));
        root.OnPointerDown = EventComposition.Compose(root.OnPointerDown, e =>
        {
            if (!disabled) StartDrag(e, scale);
        });
        root.OnPointerMove = EventComposition.Compose(root.OnPointerMove, e =>
        {
            if (!disabled && _drag.Peek() is { } drag && drag.PointerId == e.PointerId) MoveDrag(e, scale, drag);
        });
        var up = root.OnPointerUp;
        var cancel = root.OnPointerCancel;
        root.OnPointerUp = e => EndDrag(e, up, scale, update: !disabled);
        root.OnPointerCancel = e => EndDrag(e, cancel, scale, update: false);

        return (track, range, thumb);
    }

    /// <summary>
    /// Register as a post-commit effect with the host's disabled state as its dependency.
    /// The returned cleanup releases pointer capture on unmount or a change in availability.
    /// </summary>
    public Action CommitAvailability()
    {
        if (_track.Current?.Parent is { IsDisabled: true } host)
        {
            StopDrag();
            host.Blur();
        }
        return StopDrag;
    }

    static void SetPart(ElementNode part, string slot, string state, bool disabled)
    {
        part.Attributes["data-slot"] = slot;
        part.Attributes["data-state"] = state;
        part.Attributes["data-disabled"] = disabled ? "" : null;
    }

    static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    void KeyDown(KeyEvent e, SliderScale scale, bool disabled)
    {
        // The host supplies disabled semantics; a slider has no button activation action.
        if (e.Key is "Enter" or " ")
        {
            e.PreventDefault();
            return;
        }
        if (disabled || (e.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta | KeyModifiers.AltGraph)) != 0) return;

        double next;
        switch (e.Key)
        {
            case "Home": next = scale.Min; break;
            case "End": next = scale.Max; break;
            case "ArrowLeft":
            case "ArrowDown": next = scale.StepFrom(_value.Value, -1); break;
            case "ArrowRight":
            case "ArrowUp": next = scale.StepFrom(_value.Value, 1); break;
            default: return;
        }
        e.PreventDefault();
        SetValue(scale, next);
    }

    void StartDrag(PointerEvent e, SliderScale scale)
    {
        if (e.Button != PointerButtons.Left && (e.PointerType == PointerType.Mouse || e.Button != PointerButtons.None)) return;
        if (_drag.Peek() is { } active && active.Host.HasPointerCapture(active.PointerId)) return;
        if (e.CurrentTarget is not { } host || !TryPointerFraction(e, out double fraction)) return;

        // Grabbing the thumb keeps it under the same point of the pointer; pressing the track jumps to it.
        bool thumbPressed = ReferenceEquals(e.Target, _track.Current?.LastChild);
        var drag = new Drag(host, e.PointerId, thumbPressed ? fraction - scale.Fraction(_value.Value) : 0);
        host.SetPointerCapture(e.PointerId);
        _drag.Value = drag;
        if (thumbPressed) drag.LastFraction = fraction;
        else ApplyPointerFraction(scale, drag, fraction);
    }

    void MoveDrag(PointerEvent e, SliderScale scale, Drag drag)
    {
        if (!drag.Host.HasPointerCapture(drag.PointerId))
        {
            StopDrag();
            return;
        }
        if (TryPointerFraction(e, out double fraction)) ApplyPointerFraction(scale, drag, fraction);
    }

    void EndDrag(PointerEvent e, Action<PointerEvent>? caller, SliderScale scale, bool update)
    {
        var drag = _drag.Peek();
        bool ownsPointer = drag is not null && drag.PointerId == e.PointerId;
        try
        {
            caller?.Invoke(e);
            if (ownsPointer && update && !e.DefaultPrevented) MoveDrag(e, scale, drag!);
        }
        finally
        {
            if (ownsPointer && ReferenceEquals(_drag.Peek(), drag)) StopDrag();
        }
    }

    void StopDrag()
    {
        if (_drag.Peek() is not { } drag) return;
        drag.Host.ReleasePointerCapture(drag.PointerId);
        _drag.Value = null;
    }

    bool TryPointerFraction(PointerEvent e, out double fraction)
    {
        fraction = 0;
        if (_track.Current is not { IsAttached: true } track) return false;
        var bounds = track.BoundingRect;
        if (!bounds.IsFinite || bounds.Width <= 0) return false;
        var position = e.Position;
        if (track.Root?.Pipeline is RenderPipeline pipeline && !pipeline.TryMapToElement(track, position, out position)) return false;
        fraction = ((double)position.X - bounds.Left) / bounds.Width;
        return !double.IsNaN(fraction) && !double.IsInfinity(fraction);
    }

    void ApplyPointerFraction(SliderScale scale, Drag drag, double fraction)
    {
        if (fraction == drag.LastFraction) return;
        drag.LastFraction = fraction;
        double next = scale.AtFraction(fraction - drag.GrabOffset);
        if (next == drag.LastValue) return;
        drag.LastValue = next;
        SetValue(scale, next);
    }

    void SetValue(SliderScale scale, double next)
    {
        if (next != scale.Clamp(_value.Value)) _value.Set(next);
    }

    sealed class Drag(Element host, long pointerId, double grabOffset)
    {
        public Element Host { get; } = host;
        public long PointerId { get; } = pointerId;
        public double GrabOffset { get; } = grabOffset;
        public double? LastFraction { get; set; }
        public double? LastValue { get; set; }
    }
}
