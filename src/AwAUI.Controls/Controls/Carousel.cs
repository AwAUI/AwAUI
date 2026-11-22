using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls;

/// <summary>
/// A horizontally paged set of slides with snapping, previous / next buttons and indicator tabs.
/// </summary>
/// <remarks>
/// The carousel keeps one slide visible and translates the track between slides. Drag horizontally (or swipe on touch) to
/// page; a drag past a fifth of the viewport moves one slide and shorter drags snap back. Arrow keys move between slides,
/// Home and End jump to the first and last, and the indicator tabs use the tablist keyboard pattern. With
/// <see cref="ShowNavigation"/> the previous and next buttons are focusable; with <see cref="IsLooping"/> they wrap around.
/// </remarks>
public sealed partial class Carousel : Control
{
    readonly ControllableState<int> _selected = new();
    readonly Ref<Element> _viewport = new();
    readonly Ref<Element> _track = new();
    readonly List<Ref<Element>> _indicators = new();
    readonly string _baseId = PrimitiveIds.Next("carousel");
    bool _dragging;
    long _pointerId;
    Point _dragStart;
    float _dragDelta;
    bool _focusIndicator;

    protected override string GlobalStyleSheet => CarouselStyles.StyleSheet;

    /// <summary>The slides, in order.</summary>
    public NodeList? Children { get; set; }

    /// <summary>Index of the visible slide. Null uses internal state.</summary>
    public int? SelectedIndex { get; set; }

    public int DefaultSelectedIndex { get; set; }

    /// <summary>Requests a change to <see cref="SelectedIndex"/>. <c>@bind</c>-able.</summary>
    public Action<int>? SelectedIndexChanged { get; set; }

    public bool ShowNavigation { get; set; } = true;

    public bool ShowIndicators { get; set; } = true;

    /// <summary>Whether the previous and next buttons wrap around at the ends.</summary>
    public bool IsLooping { get; set; } = true;

    public bool IsEnabled { get; set; } = true;

    public string? PreviousLabel { get; set; } = "Previous slide";

    public string? NextLabel { get; set; } = "Next slide";

    /// <summary>Accessible label for the indicator group, for example <c>"Slides"</c>.</summary>
    public string? IndicatorsAriaLabel { get; set; }

    /// <summary>Accessible label for the carousel region.</summary>
    public string? CarouselAriaLabel { get; set; }

    protected override Node Build()
    {
        var slides = Children ?? [];
        int count = slides.Count;
        int index = count == 0 ? 0 : Math.Max(0, Math.Min(_selected.Sync(SelectedIndex, DefaultSelectedIndex, SelectedIndexChanged), count - 1));

        var track = new Div { Ref = _track };
        track.Attributes["part"] = "track";
        track.Style = FormattableString.Invariant(
            $"display:flex;width:100%;transform:translateX(-{(index * 100.0).ToString("R", CultureInfo.InvariantCulture)}%);");
        for (int i = 0; i < count; i++)
        {
            var slide = new Div { Children = [slides[i]] };
            slide.Attributes["part"] = "slide";
            slide.Attributes["role"] = "group";
            slide.Attributes["aria-roledescription"] = "slide";
            slide.Attributes["aria-label"] = FormattableString.Invariant($"{i + 1} of {count}");
            slide.Attributes["aria-hidden"] = i == index ? null : "true";
            slide.Style = "flex:0 0 100%;min-width:0;box-sizing:border-box;";
            track.Children.Add(slide);
        }

        var viewport = new Div
        {
            Ref = _viewport,
            Children = [track],
            OnPointerDown = OnPointerDown,
            OnPointerMove = OnPointerMove,
            OnPointerUp = OnPointerUp,
            OnPointerCancel = _ => EndDrag(index, cancel: true),
            OnKeyDown = e => OnKeyDown(e, index, count),
        };
        viewport.Attributes["part"] = "viewport";

        var region = new Div { Children = [viewport] };
        region.Attributes["part"] = "region";
        region.Attributes["role"] = "region";
        region.Attributes["aria-roledescription"] = "carousel";
        region.AriaLabel = CarouselAriaLabel;
        region.Attributes["data-orientation"] = "horizontal";
        region.Attributes["data-disabled"] = IsEnabled ? null : "true";

        if (ShowNavigation && count > 1)
        {
            region.Children.Add(NavigationButton("previous", PreviousLabel, index, count, -1));
            region.Children.Add(NavigationButton("next", NextLabel, index, count, +1));
        }

        if (ShowIndicators && count > 1)
        {
            _indicators.Clear();
            var indicators = new Div();
            indicators.Attributes["part"] = "indicators";
            indicators.Attributes["role"] = "tablist";
            indicators.AriaLabel = IndicatorsAriaLabel;
            for (int i = 0; i < count; i++)
            {
                int target = i;
                var tab = new UIButton
                {
                    Disabled = !IsEnabled,
                    Children = [],
                    OnClick = e =>
                    {
                        if (e.DefaultPrevented) return;
                        e.PreventDefault();
                        Select(target);
                    },
                    OnKeyDown = e => OnIndicatorKeyDown(e, index, count),
                };
                tab.Ref = new Ref<Element>();
                _indicators.Add(tab.Ref);
                tab.Class = "aw-carousel-indicator";
                tab.Attributes["part"] = "indicator";
                tab.Attributes["role"] = "tab";
                tab.Attributes["aria-selected"] = PartProps.Bool(i == index);
                tab.Attributes["aria-controls"] = FormattableString.Invariant($"{_baseId}-slide-{i + 1}");
                tab.Attributes["tabindex"] = i == index ? "0" : "-1";
                tab.AriaLabel = FormattableString.Invariant($"Go to slide {i + 1}");
                indicators.Children.Add(tab);
            }
            region.Children.Add(indicators);
        }

        return Apply(region, "aw-carousel");
    }

    UIButton NavigationButton(string part, string? label, int index, int count, int delta)
    {
        var button = new UIButton
        {
            Disabled = !IsEnabled || (!IsLooping && (delta < 0 ? index == 0 : index == count - 1)),
            Children = [],
            OnClick = e =>
            {
                if (e.DefaultPrevented) return;
                e.PreventDefault();
                MoveTo(index, count, delta);
            },
        };
        button.Class = "aw-carousel-nav";
        button.Attributes["part"] = part;
        button.AriaLabel = label;
        return button;
    }

    void MoveTo(int index, int count, int delta)
    {
        if (count == 0) return;
        int next = index + delta;
        next = IsLooping ? (next + count) % count : Math.Max(0, Math.Min(next, count - 1));
        Select(next);
    }

    void Select(int index)
    {
        _focusIndicator = false;
        _selected.Set(index);
    }

    void OnPointerDown(PointerEvent e)
    {
        if (!IsEnabled || e.DefaultPrevented || e.Button != PointerButtons.Left
            || e.CurrentTarget is not { IsDisabled: false } viewport) return;
        e.PreventDefault();
        viewport.SetPointerCapture(e.PointerId);
        _dragging = true;
        _pointerId = e.PointerId;
        _dragStart = e.Position;
        _dragDelta = 0;
    }

    void OnPointerMove(PointerEvent e)
    {
        if (!_dragging || e.PointerId != _pointerId || _viewport.Current is not { } viewport
            || _track.Current is not { } track) return;
        _dragDelta = e.Position.X - _dragStart.X;
        float width = viewport.BoundingRect.Width;
        if (!(width > 0)) return;
        int index = _selected.Value;
        track.SetStyleOverride("transition", "none");
        track.SetStyleOverride("transform", FormattableString.Invariant($"translateX({(-index * width + _dragDelta).ToString("R", CultureInfo.InvariantCulture)}px)"));
    }

    void OnPointerUp(PointerEvent e)
    {
        if (e.PointerId == _pointerId) EndDrag(_selected.Value, cancel: false);
    }

    void EndDrag(int index, bool cancel)
    {
        if (!_dragging) return;
        _dragging = false;
        _viewport.Current?.ReleasePointerCapture(_pointerId);
        _track.Current?.SetStyleOverride("transition", null);
        _track.Current?.SetStyleOverride("transform", null);
        if (cancel || _dragDelta == 0) return;
        float width = _viewport.Current?.BoundingRect.Width ?? 0;
        if (!(width > 0)) return;
        int count = Children?.Count ?? 0;
        if (count <= 1) return;
        if (Math.Abs(_dragDelta) >= width / 5)
            MoveTo(index, count, _dragDelta < 0 ? 1 : -1);
    }

    void OnIndicatorKeyDown(KeyEvent e, int index, int count)
    {
        if (!IsEnabled || e.DefaultPrevented || e.Modifiers is not (KeyModifiers.None or KeyModifiers.Shift)) return;
        int? next = e.Key switch
        {
            "ArrowLeft" => index - 1,
            "ArrowRight" => index + 1,
            "Home" => 0,
            "End" => count - 1,
            _ => null,
        };
        if (next is not { } value) return;
        e.PreventDefault();
        value = IsLooping ? (value + count) % count : Math.Max(0, Math.Min(value, count - 1));
        _focusIndicator = true;
        _selected.Set(value);
        if (Root is { } root)
            root.RequestAfterLayout(() =>
            {
                if (_focusIndicator && value < _indicators.Count) _indicators[value].Current?.Focus();
            });
    }

    void OnKeyDown(KeyEvent e, int index, int count)
    {
        if (!IsEnabled || e.DefaultPrevented || e.Modifiers != KeyModifiers.None || count <= 1) return;
        // Page while focus is on a navigation button; indicators handle their own roving arrows.
        if (e.Target?.GetAttribute("part") is not ("previous" or "next")) return;
        int? next = e.Key switch
        {
            "ArrowLeft" => index - 1,
            "ArrowRight" => index + 1,
            "Home" => 0,
            "End" => count - 1,
            _ => null,
        };
        if (next is not { } value) return;
        e.PreventDefault();
        value = IsLooping ? (value + count) % count : Math.Max(0, Math.Min(value, count - 1));
        Select(value);
    }
}

static class CarouselStyles
{
    internal const string StyleSheet = """
        @layer aw-carousel-defaults {
          :where(.aw-carousel) {
            position: relative;
            display: flex;
            flex-direction: column;
            min-width: 0;
            box-sizing: border-box;
          }
          :where(.aw-carousel > [part="viewport"]) {
            position: relative;
            overflow: clip;
            flex: 1 1 auto;
            min-width: 0;
          }
          :where(.aw-carousel [part="track"]) {
            transition: transform 300ms ease;
            touch-action: pan-y;
          }
          :where(.aw-carousel [part="slide"]) {
            position: relative;
          }
          :where(.aw-carousel [part="previous"]),
          :where(.aw-carousel [part="next"]) {
            position: absolute;
            top: 50%;
            width: 32px;
            height: 32px;
            margin-top: -16px;
            padding: 0;
            border-radius: 50%;
          }
          :where(.aw-carousel [part="previous"]) { inset-inline-start: 8px; }
          :where(.aw-carousel [part="next"]) { inset-inline-end: 8px; }
          :where(.aw-carousel [part="indicators"]) {
            display: flex;
            flex-direction: row;
            align-items: center;
            justify-content: center;
            gap: 8px;
            padding: 8px 0;
          }
          :where(.aw-carousel [part="indicator"]) {
            width: 8px;
            height: 8px;
            min-width: 0;
            padding: 0;
            border-radius: 50%;
          }
          :where(.aw-carousel [part="indicator"][aria-selected="true"]) {
            width: 20px;
            border-radius: 4px;
          }
        }
        @media (prefers-reduced-motion: reduce) {
          :where(.aw-carousel [part="track"]) { transition: none; }
        }
        """;
}
