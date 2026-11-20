using System;
using AwAUI.Controls.Primitives;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>
/// A nonmodal surface positioned against an existing same-window element. The caller owns the anchor,
/// its expanded state and the popup's visibility. Use <see cref="Control.Id"/> to link the anchor's
/// <c>aria-controls</c> to the surface and label the surface with <see cref="Control.AriaLabel"/> or <c>aria-labelledby</c>.
/// The surface is <c>.aw-popup[part="content"]</c>; its final position is exposed as <c>data-side</c> and <c>data-align</c>.
/// </summary>
public sealed partial class Popup : ContentControl
{
    readonly PopperScope _popper = new();
    readonly string _contentId = PrimitiveIds.Next("popup");
    bool _showing;
    Rect _lastAnchorRect;

    /// <summary>A ref to a laid-out element in this window. While the anchor is unavailable, the popup is not shown.</summary>
    public Ref<Element>? Anchor { get; set; }

    /// <summary>Explicit visibility. Bind this value or update it in <see cref="IsOpenChanged"/> to accept a dismissal.</summary>
    public bool IsOpen { get; set; }

    /// <summary>Requests a change to <see cref="IsOpen"/>. Escape or an enabled outside dismissal requests false.</summary>
    public Action<bool>? IsOpenChanged { get; set; }

    public Side Side { get; set; } = Side.Bottom;
    public PopperAlign Align { get; set; } = PopperAlign.Center;
    public float SideOffset { get; set; } = 4;
    public float AlignOffset { get; set; }
    public bool AvoidCollisions { get; set; } = true;
    public float CollisionPadding { get; set; } = 8;
    public int ZOrder { get; set; } = OverlayZOrder.Default;

    /// <summary>Move focus into the surface after layout. Focus is never trapped.</summary>
    public bool AutoFocus { get; set; } = true;

    protected override string GlobalStyleSheet => """
        :where([data-aw-popper-content-wrapper]:has(> .aw-popup[data-state="closed"])) { pointer-events: none; }
        """;

    public bool DismissOnEscape { get; set; } = true;
    public bool DismissOnPointerDownOutside { get; set; } = true;

    /// <summary>Request a close when focus moves outside the anchor and popup. Off by default.</summary>
    public bool DismissOnFocusOutside { get; set; }

    protected override Node BuildTemplate()
    {
        // Refs may be populated or detached later in the same commit, and an anchor can
        // appear after the popup. Check after layout while open and reconcile before paint.
        UseEffect(() =>
        {
            if (!IsOpen || Root is not { } root) return null;
            root.LayoutCompleted += OnLayout;
            return () => root.LayoutCompleted -= OnLayout;
        }, IsOpen);

        var currentRoot = Root;
        _showing = IsOpen && currentRoot is not null && TryReadAnchor(currentRoot, out _);
        if (IsOpen && !_showing) return new Fragment();

        _popper.VirtualAnchor ??= MeasureAnchor;
        var attributes = new AttributeDictionary(Attributes)
        {
            ["part"] = "content",
            ["data-state"] = PartProps.OpenState(_showing),
            ["aria-hidden"] = _showing ? null : "true",
            ["aria-label"] = AriaLabel ?? Attributes?["aria-label"],
        };
        var content = new PopperContent
        {
            Id = Id ?? _contentId,
            Ref = Ref,
            Role = Role.Dialog,
            TabIndex = _showing ? -1 : null,
            Class = Cx("aw-popup", (Class.Text, !Class.IsEmpty)),
            Style = SurfaceStyle(Style, _showing),
            Attributes = attributes,
            Side = Side,
            Align = Align,
            SideOffset = SideOffset,
            AlignOffset = AlignOffset,
            AvoidCollisions = AvoidCollisions,
            CollisionPadding = CollisionPadding,
            Children = BuildContent(),
        };
        var scope = new FocusScope
        {
            Active = _showing,
            OnMountAutoFocus = e => { if (!AutoFocus) e.PreventDefault(); },
            OnUnmountAutoFocus = e =>
            {
                // Outside interaction may have put focus on another control. Leave it there.
                if (IsOpen || currentRoot?.FocusManager.FocusedElement is { } focused
                    && !e.Container.IsInclusiveAncestorOf(focused)) e.PreventDefault();
            },
            Children =
            [
                new DismissableLayer
                {
                    Active = _showing,
                    OnEscapeKeyDown = e => { if (!DismissOnEscape) e.PreventDefault(); },
                    OnPointerDownOutside = e => KeepOpenFor(e, DismissOnPointerDownOutside),
                    OnFocusOutside = e => KeepOpenFor(e, DismissOnFocusOutside),
                    OnDismiss = _ => { if (_showing) IsOpenChanged?.Invoke(false); },
                    Children = [content],
                },
            ],
        };
        return Provide(_popper, new PresenceGate
        {
            Present = _showing,
            Children = [new Portal { ZOrder = ZOrder, Children = [scope] }],
        });
    }

    void KeepOpenFor(DismissableLayerEvent<Element> e, bool dismissOutside)
    {
        if (!dismissOutside || Anchor?.Current is { } anchor && anchor.IsInclusiveAncestorOf(e.Target))
            e.PreventDefault();
    }

    void OnLayout(UIRoot root)
    {
        if (IsOpen && TryReadAnchor(root, out _) != _showing) InvalidateFromLayout();
    }

    Rect MeasureAnchor()
    {
        if (Root is { } root && TryReadAnchor(root, out var rect)) _lastAnchorRect = rect;
        return _lastAnchorRect;
    }

    bool TryReadAnchor(UIRoot root, out Rect rect)
    {
        if (Anchor?.Current is { IsVisible: true } anchor && ReferenceEquals(anchor.Root, root))
        {
            rect = anchor.GetBoundingClientRect();
            return root.Pipeline is not RenderPipeline || (rect.IsFinite && !rect.Intersect(anchor.GetClippingRect()).IsEmpty);
        }
        rect = default;
        return false;
    }

    internal static StyleValue SurfaceStyle(StyleValue style, bool open) => open ? style
        : new StyleValue((style.IsEmpty ? "" : style.Text!.Trim().TrimEnd(';') + "; ") + "pointer-events: none !important");
}
