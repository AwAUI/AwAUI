using System;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>
/// Describes one directly wrapped built-in target: <c>button</c>, <c>input</c>, <c>div</c>, <c>span</c>,
/// <c>text</c> or <c>img</c>. The target keeps its own layout and focus behavior; a noninteractive target
/// needs an explicit <c>tabindex="0"</c> to show the tip on keyboard focus. Use descriptive, noninteractive
/// content; interactive floating content belongs in a <see cref="Popup"/> or <see cref="Flyout"/>.
/// </summary>
public sealed partial class ToolTip : Control
{
    readonly string _contentId = PrimitiveIds.Next("tooltip");
    Element? _target;
    UIRoot? _inputRoot;
    IDisposable? _timer;
    int _timerVersion;
    bool _overTarget;
    bool _overContent;
    bool _focused;
    bool _open;
    bool _skipExit;

    /// <summary>Exactly one supported target. No layout wrapper or extra Tab stop is inserted.</summary>
    public NodeList? Children { get; set; }

    /// <summary>Text or a node to display in the tip. Null disables the tip.</summary>
    public object? Content { get; set; }

    /// <summary>Hover delay in milliseconds. Keyboard and programmatic focus open immediately.</summary>
    public int ShowDelay { get; set; } = 500;

    /// <summary>Time in milliseconds to cross the gap between target and tip before the tip closes.</summary>
    public int CloseDelay { get; set; } = 150;

    public Side Side { get; set; } = Side.Top;
    public PopperAlign Align { get; set; } = PopperAlign.Center;
    public float SideOffset { get; set; } = 6;
    public float AlignOffset { get; set; }
    public bool AvoidCollisions { get; set; } = true;
    public float CollisionPadding { get; set; } = 8;
    public int ZOrder { get; set; } = OverlayZOrder.Tooltip;

    protected override string GlobalStyleSheet => """
        :where([data-aw-popper-content-wrapper]:has(> .aw-tooltip[data-state="closed"])) { pointer-events: none; }
        """;

    protected override Node Build()
    {
        if (Children is not { Count: > 0 })
        {
            Reset();
            return new Fragment();
        }
        var child = Slot.SingleChild(Children);
        if (Children.Count != 1 || (child is not (UIButton or Div or Span or UIText or Image) && child?.GetType() != typeof(Input)))
            throw new InvalidOperationException("ToolTip requires a single built-in button, input, div, span, text or img as its child.");

        var content = Content;
        if (content is null) Reset();

        string id = Id ?? _contentId;
        var target = (ElementNode)Slot.Merge(new Div
        {
            Class = "aw-tooltip-target",
            OnPointerEnter = EnterTarget,
            OnPointerLeave = LeaveTarget,
            OnPointerDown = _ => Dismiss(),
            OnFocus = FocusTarget,
            OnBlur = BlurTarget,
        }, child);
        if (_open)
        {
            var existing = target.Attributes["aria-describedby"];
            target.Attributes["aria-describedby"] = string.IsNullOrWhiteSpace(existing) ? id : existing + " " + id;
        }

        var anchor = new PopperAnchor { AsChild = true, Children = [target] };
        return new Popper { Children = _skipExit || content is null ? [anchor] : [anchor, BuildTip(id, content)] };
    }

    Node BuildTip(string id, object content)
    {
        // Let callers observe content events, but always run leave cleanup.
        var attributes = new AttributeDictionary(Attributes)
        {
            ["part"] = "content",
            ["data-state"] = PartProps.OpenState(_open),
            ["aria-hidden"] = _open ? null : "true",
            ["aria-label"] = AriaLabel ?? Attributes?["aria-label"],
        };
        attributes["onpointerenter"] = WithPointer(attributes["onpointerenter"], EnterContent);
        attributes["onpointerleave"] = WithPointer(attributes["onpointerleave"], LeaveContent);
        return new PresenceGate
        {
            Present = _open,
            Children =
            [
                new Portal
                {
                    ZOrder = ZOrder,
                    Children =
                    [
                        new DismissableLayer
                        {
                            Active = _open,
                            OnFocusOutside = static e => e.PreventDefault(),
                            OnDismiss = _ => Dismiss(),
                            Children =
                            [
                                new PopperContent
                                {
                                    Id = id,
                                    Ref = Ref,
                                    Role = Role.Tooltip,
                                    Class = Cx("aw-tooltip", (Class.Text, !Class.IsEmpty)),
                                    Style = Popup.SurfaceStyle(Style, _open),
                                    Attributes = attributes,
                                    Side = Side,
                                    Align = Align,
                                    SideOffset = SideOffset,
                                    AlignOffset = AlignOffset,
                                    AvoidCollisions = AvoidCollisions,
                                    CollisionPadding = CollisionPadding,
                                    HideWhenDetached = true,
                                    Children = [ContentControl.Present(content, null)],
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }

    static Action<PointerEvent> WithPointer(object? existing, Action<PointerEvent> handler) => existing switch
    {
        Action<PointerEvent> action => e => { action(e); handler(e); },
        Action action => e => { action(); handler(e); },
        null => handler,
        _ => throw new ArgumentException("Pointer handlers must be Action<PointerEvent> or Action."),
    };

    void EnterTarget(PointerEvent e)
    {
        if (e.DefaultPrevented || e.PointerType == PointerType.Touch || e.Buttons != PointerButtons.None
            || Content is null || e.CurrentTarget is not { IsDisabled: false } target) return;
        Track(target);
        _overTarget = true;
        Refresh();
    }

    void LeaveTarget(PointerEvent e)
    {
        if (e.PointerType == PointerType.Touch || !ReferenceEquals(e.CurrentTarget, _target)) return;
        _overTarget = false;
        Refresh();
    }

    void EnterContent(PointerEvent e)
    {
        if (!_open || e.PointerType == PointerType.Touch) return;
        _overContent = true;
        Refresh();
    }

    void LeaveContent(PointerEvent e)
    {
        if (!_open || e.PointerType == PointerType.Touch) return;
        _overContent = false;
        Refresh();
    }

    void FocusTarget(FocusEvent e)
    {
        if (e.DefaultPrevented || e.Origin == FocusOrigin.Pointer || Content is null
            || e.CurrentTarget is not { IsDisabled: false } target) return;
        Track(target);
        _focused = true;
        Refresh();
    }

    void BlurTarget(FocusEvent e)
    {
        if (!ReferenceEquals(e.CurrentTarget, _target)) return;
        _focused = false;
        Refresh(closeImmediately: true);
    }

    void Track(Element target)
    {
        if (ReferenceEquals(_target, target)) return;
        CancelTimer();
        _overTarget = _overContent = _focused = false;
        _target = target;
        if (_inputRoot is null && target.Root is { } root)
        {
            _inputRoot = root;
            root.Input.PreviewKeyDown += CancelPendingOnEscape;
            root.LayoutCompleted += CheckTarget;
        }
    }

    void Refresh(bool closeImmediately = false)
    {
        bool wantOpen = _focused || _overTarget || _overContent;
        if (_open == wantOpen)
        {
            CancelTimer();
            if (!wantOpen) StopTracking();
            return;
        }

        int delay = wantOpen ? (_focused ? 0 : ShowDelay) : (closeImmediately ? 0 : CloseDelay);
        if (delay > 0 && _inputRoot?.Clock is { } clock)
        {
            if (_timer is not null) return;
            int version = _timerVersion;
            _timer = clock.Schedule(TimeSpan.FromMilliseconds(delay), () =>
            {
                if (version != _timerVersion) return;
                _timer = null;
                if (TargetAvailable) SetOpen(wantOpen);
                else Dismiss(skipExit: true);
            });
        }
        else
        {
            CancelTimer();
            SetOpen(wantOpen);
        }
    }

    bool TargetAvailable => _target is { IsVisible: true, IsDisabled: false } target && Content is not null
        && ReferenceEquals(target.Root, _inputRoot);

    void CheckTarget(UIRoot root)
    {
        if (!TargetAvailable) Dismiss(skipExit: true, afterLayout: true);
    }

    // The layer handles Escape while open. A pending hover should be cancelled without
    // consuming Escape that might otherwise dismiss a visible ancestor surface.
    void CancelPendingOnEscape(KeyEvent e)
    {
        if (!_open && e.Key == "Escape") Dismiss();
    }

    void SetOpen(bool open)
    {
        _open = open;
        if (open) _skipExit = false;
        else StopTracking();
        Invalidate();
    }

    void Dismiss(bool skipExit = false, bool afterLayout = false)
    {
        bool wasOpen = _open;
        Reset();
        if (!wasOpen) return;
        _skipExit = skipExit;
        if (afterLayout) InvalidateFromLayout();
        else Invalidate();
    }

    void CancelTimer()
    {
        _timerVersion++;
        _timer?.Dispose();
        _timer = null;
    }

    void StopTracking()
    {
        if (_inputRoot is { } root)
        {
            root.Input.PreviewKeyDown -= CancelPendingOnEscape;
            root.LayoutCompleted -= CheckTarget;
        }
        _inputRoot = null;
        _target = null;
    }

    void Reset()
    {
        CancelTimer();
        StopTracking();
        _open = _overTarget = _overContent = _focused = false;
    }

    protected override void OnUnmount() => Reset();
}
