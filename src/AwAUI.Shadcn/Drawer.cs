using System;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Platform;
using AwAUI.UI;
using DialogPrimitive = AwAUI.Controls.Primitives.Dialog;
using DrawerSide = AwAUI.Controls.Primitives.Side;

namespace AwAUI.Shadcn;

/// <summary>
/// A dialog that slides in from an edge and can be dragged away. It is built on the modal dialog primitive, so it
/// inherits the focus trap, Escape handling, overlay clicks, scroll lock and focus restoration. The default side is
/// the bottom edge, where a drag handle and drag-to-dismiss are available.
/// </summary>
public sealed partial class Drawer : Component
{
    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public bool Modal { get; set; } = true;
    public Action<bool>? OnOpenChange { get; set; }
    public Action<bool>? OpenChanged { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => new DialogPrimitive.Root
    {
        Open = Open,
        DefaultOpen = DefaultOpen,
        Modal = Modal,
        OnOpenChange = OpenChanged + OnOpenChange,
        Children = Children,
    };
}

public sealed partial class DrawerTrigger : Component
{
    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new DialogPrimitive.Trigger
    {
        AsChild = AsChild,
        Children = Children,
        Attributes = DialogAttributes.ForPart("drawer-trigger", Rest, Class),
    };
}

public sealed partial class DrawerClose : Component
{
    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new DialogPrimitive.Close
    {
        AsChild = AsChild,
        Children = Children,
        Attributes = DialogAttributes.ForPart("drawer-close", Rest, Class),
    };
}

/// <summary>Places a custom drawer overlay and panel in the same portal.</summary>
public sealed partial class DrawerPortal : Component
{
    public int ZOrder { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => new DialogPortal { ZOrder = ZOrder, Children = Children };
}

public sealed partial class DrawerOverlay : Component
{
    internal const string OverlayClass =
        "fixed inset-0 z-50 bg-black/50 data-[state=closed]:animate-aw-dialog-backdrop-out " +
        "data-[state=open]:animate-aw-dialog-backdrop-in motion-reduce:animate-none!";

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new DialogPrimitive.Overlay
    {
        Children = Children,
        Attributes = DialogAttributes.ForPart("drawer-overlay", Rest, Class, OverlayClass),
    };
}

/// <summary>
/// The sliding panel. Pointer presses on non-interactive content (including the drag handle) drag the panel along
/// the closing axis; a release past a quarter of the panel or faster than 0.5 px/ms dismisses it, otherwise it snaps
/// back. Reduced-motion preferences remove the slide and snap animations.
/// </summary>
public sealed partial class DrawerContent : Component
{
    const string ContentClass =
        "group/drawer-content fixed z-50 flex h-auto flex-col bg-background shadow-lg " +
        "data-[state=closed]:animate-aw-sheet-out data-[state=open]:animate-aw-sheet-in motion-reduce:animate-none!";
    const string DirectionClasses =
        "data-[vaul-drawer-direction=top]:inset-x-0 data-[vaul-drawer-direction=top]:top-0 data-[vaul-drawer-direction=top]:mb-24 data-[vaul-drawer-direction=top]:max-h-[80vh] data-[vaul-drawer-direction=top]:rounded-b-lg data-[vaul-drawer-direction=top]:border-b " +
        "data-[vaul-drawer-direction=bottom]:inset-x-0 data-[vaul-drawer-direction=bottom]:bottom-0 data-[vaul-drawer-direction=bottom]:mt-24 data-[vaul-drawer-direction=bottom]:max-h-[80vh] data-[vaul-drawer-direction=bottom]:rounded-t-lg data-[vaul-drawer-direction=bottom]:border-t " +
        "data-[vaul-drawer-direction=right]:inset-y-0 data-[vaul-drawer-direction=right]:right-0 data-[vaul-drawer-direction=right]:w-3/4 data-[vaul-drawer-direction=right]:border-l data-[vaul-drawer-direction=right]:sm:max-w-sm " +
        "data-[vaul-drawer-direction=left]:inset-y-0 data-[vaul-drawer-direction=left]:left-0 data-[vaul-drawer-direction=left]:w-3/4 data-[vaul-drawer-direction=left]:border-r data-[vaul-drawer-direction=left]:sm:max-w-sm";
    const string CloseClass =
        "absolute top-4 right-4 rounded-xs opacity-70 ring-offset-background transition-opacity hover:opacity-100 " +
        "focus:ring-2 focus:ring-ring focus:ring-offset-2 focus:outline-hidden disabled:pointer-events-none";
    const string MotionClass = "transition-transform duration-200 ease-out motion-reduce:transition-none";

    readonly State<bool> _dragging = new(false);
    readonly State<float> _offset = new(0f);

    OpenableShared? _shared;
    Element? _captureTarget;
    long _pointerId = -1;
    float _startX;
    float _startY;
    TimeSpan _startTime;

    public DrawerSide Side { get; set; } = DrawerSide.Bottom;
    /// <summary>Allows a pointer drag along the closing axis to move and dismiss the drawer.</summary>
    public bool DragToDismiss { get; set; } = true;
    public bool ShowCloseButton { get; set; }
    public string CloseLabel { get; set; } = "Close";
    public Action<FocusScopeAutoFocusEvent>? OnOpenAutoFocus { get; set; }
    public Action<FocusScopeAutoFocusEvent>? OnCloseAutoFocus { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnEscapeKeyDown { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnPointerDownOutside { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnFocusOutside { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnInteractOutside { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var context = Use<DialogPrimitive.Context>();
        _shared = context.Shared;
        bool dragging = _dragging.Value;
        float offset = _offset.Value;

        var children = new NodeList();
        // shadcn renders one handle for every direction and hides it unless the drawer opens from the bottom.
        var handle = new Div
        {
            Class = "mx-auto mt-4 hidden h-2 w-[100px] shrink-0 rounded-full bg-muted group-data-[vaul-drawer-direction=bottom]/drawer-content:block",
        };
        handle.Attributes["data-slot"] = "drawer-handle";
        handle.Attributes["aria-hidden"] = "true";
        children.Add(handle);
        children.AddRange(Children);

        if (ShowCloseButton)
        {
            var glyph = SelectParts.Glyph(LucideIcons.X, "drawer-close-icon", "pointer-events-none size-4");
            children.Add(new DrawerClose
            {
                Class = CloseClass,
                Rest = new AttributeDictionary { ["aria-label"] = CloseLabel },
                Children = [glyph],
            });
        }

        var attributes = DialogAttributes.ForPart("drawer-content", Rest, Class,
            ContentClass + " " + DirectionClasses + (dragging ? " transition-none" : " " + MotionClass));
        attributes["data-side"] = SideName(Side);
        attributes["data-vaul-drawer-direction"] = SideName(Side);
        if (dragging) attributes["data-dragging"] = "true";
        string sheetMotion = Side switch
        {
            DrawerSide.Top => "--aw-sheet-y: -100%; --aw-sheet-x: 0px",
            DrawerSide.Left or DrawerSide.InlineStart => "--aw-sheet-x: -100%; --aw-sheet-y: 0px",
            DrawerSide.Right or DrawerSide.InlineEnd => "--aw-sheet-x: 100%; --aw-sheet-y: 0px",
            _ => "--aw-sheet-y: 100%; --aw-sheet-x: 0px",
        };
        string style = sheetMotion;
        if (offset > 0f)
        {
            string transform = Side switch
            {
                DrawerSide.Top => "transform: translate3d(0px, -" + Format(offset) + "px, 0px)",
                DrawerSide.Bottom => "transform: translate3d(0px, " + Format(offset) + "px, 0px)",
                DrawerSide.Left or DrawerSide.InlineStart => "transform: translate3d(-" + Format(offset) + "px, 0px, 0px)",
                _ => "transform: translate3d(" + Format(offset) + "px, 0px, 0px)",
            };
            style += "; " + transform;
        }
        attributes["style"] = MergeStyle(attributes["style"], style);
        attributes["onpointerdown"] = EventComposition.Compose(
            PointerHandler(attributes["onpointerdown"]), HandlePointerDown);
        attributes["onpointermove"] = EventComposition.Compose(
            PointerHandler(attributes["onpointermove"]), HandlePointerMove);
        attributes["onpointerup"] = EventComposition.Compose(
            PointerHandler(attributes["onpointerup"]), HandlePointerUp);
        attributes["onpointercancel"] = EventComposition.Compose(
            PointerHandler(attributes["onpointercancel"]), HandlePointerCancel);

        var panel = new DialogPrimitive.Content
        {
            Attributes = attributes,
            Children = children,
            OnOpenAutoFocus = OnOpenAutoFocus,
            OnCloseAutoFocus = OnCloseAutoFocus,
            OnEscapeKeyDown = OnEscapeKeyDown,
            OnPointerDownOutside = OnPointerDownOutside,
            OnFocusOutside = OnFocusOutside,
            OnInteractOutside = OnInteractOutside,
        };

        if (TryUse<DialogPortal.Placement>(out var placement) && ReferenceEquals(placement.Owner, context.Shared))
            return panel;
        return new DialogPrimitive.Portal { Children = [new DrawerOverlay(), panel] };
    }

    protected override void OnMount()
    {
        _pointerId = -1;
        _captureTarget = null;
        if (_dragging.Peek()) _dragging.Value = false;
        if (_offset.Peek() != 0f) _offset.Value = 0f;
    }

    protected override void OnUnmount()
    {
        if (_pointerId >= 0 && _captureTarget is { } target) target.ReleasePointerCapture(_pointerId);
        _pointerId = -1;
        _captureTarget = null;
    }

    void HandlePointerDown(PointerEvent e)
    {
        if (e.DefaultPrevented || !DragToDismiss || e.Button != PointerButtons.Left) return;
        if (e.CurrentTarget is not Element host) return;
        // Never start a drag from an interactive descendant (button, input, link ...).
        for (var target = e.Target; target is not null && !ReferenceEquals(target, host); target = target.Parent)
            if (target.IsFocusable) return;

        _pointerId = e.PointerId;
        _captureTarget = host;
        _startX = e.Position.X;
        _startY = e.Position.Y;
        _startTime = e.Timestamp;
        _offset.Value = 0f;
        _dragging.Value = true;
        host.SetPointerCapture(e.PointerId);
    }

    void HandlePointerMove(PointerEvent e)
    {
        if (e.PointerId != _pointerId || !_dragging.Peek()) return;
        float along = Along(e.Position);
        _offset.Value = Math.Max(0f, along);
    }

    void HandlePointerUp(PointerEvent e)
    {
        if (e.PointerId != _pointerId) return;
        _pointerId = -1;
        if (_captureTarget is { } capture) capture.ReleasePointerCapture(e.PointerId);
        _captureTarget = null;
        bool wasDragging = _dragging.Peek();
        float along = _offset.Peek();
        _dragging.Value = false;
        _offset.Value = 0f;
        if (!wasDragging || along <= 0f || _shared is null) return;

        double elapsedMs = Math.Max(1d, (e.Timestamp - _startTime).TotalMilliseconds);
        float size = Side switch
        {
            DrawerSide.Top or DrawerSide.Bottom => (e.CurrentTarget as Element)?.GetBoundingClientRect().Height ?? 0f,
            _ => (e.CurrentTarget as Element)?.GetBoundingClientRect().Width ?? 0f,
        };
        float threshold = Math.Max(64f, size * 0.25f);
        bool dismiss = along > threshold || along / elapsedMs > 0.5f;
        if (dismiss) _shared.SetOpen(false);
    }

    void HandlePointerCancel(PointerEvent e)
    {
        if (e.PointerId != _pointerId) return;
        _pointerId = -1;
        if (_captureTarget is { } capture) capture.ReleasePointerCapture(e.PointerId);
        _captureTarget = null;
        _dragging.Value = false;
        _offset.Value = 0f;
    }

    float Along(Point position) => Side switch
    {
        DrawerSide.Top => _startY - position.Y,
        DrawerSide.Bottom => position.Y - _startY,
        DrawerSide.Left or DrawerSide.InlineStart => _startX - position.X,
        _ => position.X - _startX,
    };

    static string SideName(DrawerSide side) => side switch
    {
        DrawerSide.Top => "top",
        DrawerSide.Left => "left",
        DrawerSide.Right => "right",
        DrawerSide.InlineStart => "left",
        DrawerSide.InlineEnd => "right",
        _ => "bottom",
    };

    static string Format(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    static Action<PointerEvent>? PointerHandler(object? value) => value switch
    {
        Action<PointerEvent> handler => handler,
        Action handler => _ => handler(),
        null => null,
        _ => throw new ArgumentException("Pointer handlers must be Action<PointerEvent> or Action."),
    };

    static string MergeStyle(object? existing, string declaration)
    {
        string text = existing switch
        {
            StyleValue style => style.Text ?? "",
            null => "",
            _ => Convert.ToString(existing, CultureInfo.InvariantCulture) ?? "",
        };
        text = text.Trim().TrimEnd(';').Trim();
        return text.Length == 0 ? declaration : text + "; " + declaration;
    }
}

public sealed partial class DrawerHeader : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var node = new Div { Children = Children ?? new NodeList() };
        node.MergeAttributes(DialogAttributes.ForPart("drawer-header", Rest, Class,
            "flex flex-col gap-0.5 p-4 group-data-[vaul-drawer-direction=bottom]/drawer-content:text-center " +
            "group-data-[vaul-drawer-direction=top]/drawer-content:text-center md:gap-1.5 md:text-left"));
        return node;
    }
}

public sealed partial class DrawerFooter : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var node = new Div { Children = Children ?? new NodeList() };
        node.MergeAttributes(DialogAttributes.ForPart("drawer-footer", Rest, Class, "mt-auto flex flex-col gap-2 p-4"));
        return node;
    }
}

public sealed partial class DrawerTitle : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new DialogPrimitive.Title
    {
        Children = Children,
        Attributes = DialogAttributes.ForPart("drawer-title", Rest, Class, "font-semibold text-foreground"),
    };
}

public sealed partial class DrawerDescription : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new DialogPrimitive.Description
    {
        Children = Children,
        Attributes = DialogAttributes.ForPart("drawer-description", Rest, Class, "text-sm text-muted-foreground"),
    };
}
