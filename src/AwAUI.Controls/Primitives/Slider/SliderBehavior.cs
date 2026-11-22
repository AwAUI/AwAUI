using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls.Primitives;

/// <summary>Shared behavior and unstyled parts for a horizontal slider with one or more values.</summary>
/// <remarks>Create one instance per component. Call <see cref="Attach"/> or <see cref="AttachRange"/> during Build after applying caller attributes to the host.</remarks>
public sealed class SliderBehavior
{
    readonly ControllableState<double> _value = new();
    readonly ControllableState<double[]> _values = new(RangeValuesComparer.Instance);
    readonly State<Drag?> _drag = new((Drag?)null);
    readonly State<RangeDrag?> _rangeDrag = new((RangeDrag?)null);
    readonly Ref<Element> _track = new();
    List<Ref<Element>> _thumbs = new();
    SliderScale _scale;
    double _gap;
    int _activeIndex;

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
    /// Attaches multi-value (range) slider semantics and input handlers to a host and returns its parts for styling.
    /// One focusable thumb is rendered per value; adjacent thumbs are kept apart by <paramref name="minStepsBetweenThumbs"/>.
    /// </summary>
    public (Div Track, Div Range, IReadOnlyList<Div> Thumbs) AttachRange(Div root, double min, double max, double step,
        IReadOnlyList<double>? values, IReadOnlyList<double>? defaultValues,
        Action<IReadOnlyList<double>>? onValuesChange, int? minStepsBetweenThumbs,
        string? valueText = null, Func<double, string?>? formatValueText = null, bool disabled = false)
    {
        var scale = new SliderScale(min, max, step);
        _scale = scale;
        _gap = Math.Max(0, minStepsBetweenThumbs ?? 0) * step;

        Action<double[]>? onChange = onValuesChange is null ? null : next => onValuesChange(next);
        var raw = _values.Sync(values is not null, ToArray(values), ToArray(defaultValues, min), onChange);
        double[] current = Normalize(scale, raw, _gap);

        string state = disabled ? "disabled" : _rangeDrag.Value is null ? "idle" : "dragging";
        SetPart(root, "slider", state, disabled);
        root.Attributes["data-orientation"] = "horizontal";

        var thumbRefs = new List<Ref<Element>>(current.Length);
        var thumbs = new List<Div>(current.Length);
        for (int i = 0; i < current.Length; i++)
        {
            var thumbRef = new Ref<Element>();
            string percent = Percent(scale.Fraction(current[i]));
            var thumb = new Div
            {
                Ref = thumbRef,
                Style = "left: " + percent + "; transform: translate(-50%, -50%)",
            };
            SetPart(thumb, "slider-thumb", state, disabled);
            thumb.Role = Role.Slider;
            thumb.TabIndex = disabled ? -1 : 0;
            thumb.Attributes["aria-valuemin"] = Number(min);
            thumb.Attributes["aria-valuemax"] = Number(max);
            thumb.Attributes["aria-valuenow"] = Number(current[i]);
            thumb.Attributes["aria-orientation"] = "horizontal";
            thumb.Attributes["aria-disabled"] = disabled ? "true" : null;
            string? ariaValueText = formatValueText?.Invoke(current[i]) ?? valueText;
            if (ariaValueText is not null) thumb.Attributes["aria-valuetext"] = ariaValueText;

            thumbRefs.Add(thumbRef);
            thumbs.Add(thumb);
        }
        _thumbs = thumbRefs;

        var range = new Div();
        if (current.Length <= 1)
        {
            range.Style = "width: " + Percent(scale.Fraction(current[0]));
        }
        else
        {
            range.Style = "left: " + Percent(scale.Fraction(current[0]))
                + "; right: " + Percent(1 - scale.Fraction(current[current.Length - 1]));
        }
        SetPart(range, "slider-range", state, disabled);
        range.Attributes["aria-hidden"] = "true";

        var track = new Div { Ref = _track };
        SetPart(track, "slider-track", state, disabled);
        track.Children.Add(range);
        foreach (var thumb in thumbs) track.Children.Add(thumb);
        root.Children.Add(track);

        root.OnKeyDown = EventComposition.Compose(root.OnKeyDown, e => RangeKeyDown(e, disabled));
        root.OnPointerDown = EventComposition.Compose(root.OnPointerDown, e =>
        {
            if (!disabled) StartRangeDrag(e);
        });
        root.OnPointerMove = EventComposition.Compose(root.OnPointerMove, e =>
        {
            if (!disabled && _rangeDrag.Peek() is { } drag && drag.PointerId == e.PointerId) MoveRangeDrag(e, drag);
        });
        var up = root.OnPointerUp;
        var cancel = root.OnPointerCancel;
        root.OnPointerUp = e => EndRangeDrag(e, up, update: !disabled);
        root.OnPointerCancel = e => EndRangeDrag(e, cancel, update: false);

        return (track, range, thumbs);
    }

    /// <summary>
    /// Register as a post-commit effect with the range host's disabled state as its dependency.
    /// The returned cleanup releases pointer capture on unmount or a change in availability.
    /// </summary>
    public Action CommitRangeAvailability(bool disabled)
    {
        if (disabled) StopRangeDrag();
        return StopRangeDrag;
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

    // netstandard2.0 没有 Math.Clamp。
    static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    static string Percent(double fraction) =>
        (fraction * 100).ToString("0.##########", CultureInfo.InvariantCulture) + "%";

    static double[] ToArray(IReadOnlyList<double>? values, double start = 0)
    {
        if (values is null || values.Count == 0) return [start];
        var result = new double[values.Count];
        for (int i = 0; i < result.Length; i++) result[i] = values[i];
        return result;
    }

    /// <summary>Sorts, clamps to <paramref name="scale"/> and keeps neighbouring values at least <paramref name="gap"/> apart.</summary>
    static double[] Normalize(SliderScale scale, IReadOnlyList<double> values, double gap)
    {
        if (values.Count == 0) return [scale.Min];
        var result = new double[values.Count];
        for (int i = 0; i < result.Length; i++) result[i] = scale.Clamp(values[i]);
        Array.Sort(result);

        for (int i = 1; i < result.Length; i++)
        {
            double minimum = result[i - 1] + gap;
            if (result[i] < minimum) result[i] = minimum;
        }
        if (result[result.Length - 1] > scale.Max)
        {
            result[result.Length - 1] = scale.Max;
            for (int i = result.Length - 2; i >= 0; i--)
            {
                double maximum = result[i + 1] - gap;
                if (result[i] > maximum) result[i] = maximum;
            }
        }
        for (int i = 0; i < result.Length; i++) result[i] = scale.Clamp(result[i]);
        return result;
    }

    /// <summary>Clamps a value at <paramref name="index"/> between its neighbours minus the minimum step gap.</summary>
    static double ClampAt(SliderScale scale, double value, int index, IReadOnlyList<double> values, double gap)
    {
        double minimum = index > 0 ? values[index - 1] + gap : scale.Min;
        double maximum = index < values.Count - 1 ? values[index + 1] - gap : scale.Max;
        minimum = Math.Max(minimum, scale.Min);
        maximum = Math.Min(maximum, scale.Max);
        if (minimum > maximum) minimum = maximum;
        return Math.Max(minimum, Math.Min(maximum, scale.Clamp(value)));
    }

    double[] CurrentValues()
    {
        var raw = _values.Value;
        return raw is null ? [_scale.Min] : Normalize(_scale, raw, _gap);
    }

    void SetValues(double[] next)
    {
        var normalized = Normalize(_scale, next, _gap);
        _values.Set(normalized);
    }

    int ThumbIndex(Element? target)
    {
        if (target is null) return -1;
        for (int i = 0; i < _thumbs.Count; i++)
        {
            if (_thumbs[i].Current is { } thumb && thumb.IsInclusiveAncestorOf(target)) return i;
        }
        return -1;
    }

    static int NearestThumb(SliderScale scale, double fraction, IReadOnlyList<double> values)
    {
        int best = 0;
        double distance = double.PositiveInfinity;
        for (int i = 0; i < values.Count; i++)
        {
            double current = Math.Abs(scale.Fraction(values[i]) - fraction);
            if (current < distance)
            {
                distance = current;
                best = i;
            }
        }
        return best;
    }

    void RangeKeyDown(KeyEvent e, bool disabled)
    {
        // A slider has no button activation action.
        if (e.Key is "Enter" or " ")
        {
            e.PreventDefault();
            return;
        }
        if (disabled || (e.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta | KeyModifiers.AltGraph)) != 0) return;

        int index = ThumbIndex(e.Target);
        if (index < 0) index = _thumbs.Count == 1 ? 0 : _activeIndex;
        if (index < 0 || index >= _thumbs.Count) return;

        var values = CurrentValues();
        if (index >= values.Length) return;

        double value = values[index];
        bool shift = (e.Modifiers & KeyModifiers.Shift) != 0;
        double page = (_scale.Max - _scale.Min) / 10;
        double? next = e.Key switch
        {
            "Home" => _scale.Min,
            "End" => _scale.Max,
            "ArrowLeft" or "ArrowDown" => _scale.StepFrom(value, shift ? -10 : -1),
            "ArrowRight" or "ArrowUp" => _scale.StepFrom(value, shift ? 10 : 1),
            "PageUp" => value + page,
            "PageDown" => value - page,
            _ => null,
        };
        if (next is null) return;

        e.PreventDefault();
        _activeIndex = index;
        values[index] = ClampAt(_scale, next.Value, index, values, _gap);
        SetValues(values);
    }

    void StartRangeDrag(PointerEvent e)
    {
        if (e.Button != PointerButtons.Left && (e.PointerType == PointerType.Mouse || e.Button != PointerButtons.None)) return;
        if (_rangeDrag.Peek() is { } active && active.Host.HasPointerCapture(active.PointerId)) return;
        if (e.CurrentTarget is not { } host || !TryPointerFraction(e, out double fraction)) return;

        var values = CurrentValues();
        if (values.Length == 0) return;

        int index = ThumbIndex(e.Target);
        bool thumbPressed = index >= 0;
        if (!thumbPressed) index = NearestThumb(_scale, fraction, values);
        index = Clamp(index, 0, values.Length - 1);

        // Grabbing the thumb keeps it under the same point of the pointer; pressing the track jumps to it.
        double grab = thumbPressed ? fraction - _scale.Fraction(values[index]) : 0;
        var drag = new RangeDrag(host, e.PointerId, index, grab);
        host.SetPointerCapture(e.PointerId);
        _rangeDrag.Value = drag;
        _activeIndex = index;

        // Keep the focus on the thumb that will move instead of letting pointer-down focus the host.
        e.PreventDefault();
        _thumbs[index].Current?.Focus();

        if (thumbPressed) drag.LastFraction = fraction;
        else ApplyRangeFraction(drag, fraction);
    }

    void MoveRangeDrag(PointerEvent e, RangeDrag drag)
    {
        if (!drag.Host.HasPointerCapture(drag.PointerId))
        {
            StopRangeDrag();
            return;
        }
        if (TryPointerFraction(e, out double fraction)) ApplyRangeFraction(drag, fraction);
    }

    void ApplyRangeFraction(RangeDrag drag, double fraction)
    {
        if (fraction == drag.LastFraction) return;
        drag.LastFraction = fraction;

        var values = CurrentValues();
        if (values.Length == 0) return;
        int index = Clamp(drag.Index, 0, values.Length - 1);
        double next = _scale.AtFraction(fraction - drag.GrabOffset);
        values[index] = ClampAt(_scale, next, index, values, _gap);
        SetValues(values);
    }

    void EndRangeDrag(PointerEvent e, Action<PointerEvent>? caller, bool update)
    {
        var drag = _rangeDrag.Peek();
        bool ownsPointer = drag is not null && drag.PointerId == e.PointerId;
        try
        {
            caller?.Invoke(e);
            if (ownsPointer && update && !e.DefaultPrevented) MoveRangeDrag(e, drag!);
        }
        finally
        {
            if (ownsPointer && ReferenceEquals(_rangeDrag.Peek(), drag)) StopRangeDrag();
        }
    }

    void StopRangeDrag()
    {
        if (_rangeDrag.Peek() is not { } drag) return;
        drag.Host.ReleasePointerCapture(drag.PointerId);
        _rangeDrag.Value = null;
    }

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

    sealed class RangeDrag(Element host, long pointerId, int index, double grabOffset)
    {
        public Element Host { get; } = host;
        public long PointerId { get; } = pointerId;
        public int Index { get; } = index;
        public double GrabOffset { get; } = grabOffset;
        public double? LastFraction { get; set; }
    }

    sealed class RangeValuesComparer : IEqualityComparer<double[]>
    {
        public static readonly RangeValuesComparer Instance = new();

        public bool Equals(double[]? x, double[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null || x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++)
            {
                if (x[i] != y[i]) return false;
            }
            return true;
        }

        public int GetHashCode(double[] values)
        {
            unchecked
            {
                int hash = 17;
                foreach (double value in values) hash = hash * 31 + value.GetHashCode();
                return hash;
            }
        }
    }
}
