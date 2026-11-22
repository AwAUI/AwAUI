using System;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using PopoverPrimitive = AwAUI.Controls.Primitives.Popover;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>
/// A non-modal card that opens on hover or focus after a delay and stays open while either the trigger or
/// the card is hovered. Timers use <see cref="UIRoot.Clock"/> so the delays are deterministic in tests.
/// </summary>
public sealed partial class HoverCard : Component
{
    readonly Scope _scope = new();

    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public int OpenDelay { get; set; } = 700;
    public int CloseDelay { get; set; } = 300;
    public Action<bool>? OpenChanged { get; set; }
    public Action<bool>? OnOpenChange { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        _scope.OpenDelay = OpenDelay;
        _scope.CloseDelay = CloseDelay;
        return Provide(_scope, new PopoverPrimitive.Root
        {
            Open = Open,
            DefaultOpen = DefaultOpen,
            OnOpenChange = OpenChanged + OnOpenChange,
            Children = Children,
        });
    }

    internal sealed class Scope
    {
        internal readonly TooltipInteraction Interaction = new();
        internal int OpenDelay = 700;
        internal int CloseDelay = 300;
    }
}

public sealed partial class HoverCardTrigger : Component
{
    TooltipInteraction? _interaction;

    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<HoverCard.Scope>();
        var context = Use<PopoverPrimitive.Context>();
        var interaction = _interaction = scope.Interaction;
        var shared = context.Shared;
        var button = new UIButton { Children = Children ?? new NodeList() };
        button.MergeAttributes(DialogAttributes.ForPart("hover-card-trigger", Rest, Class));
        button.Attributes["aria-haspopup"] = "dialog";
        button.Attributes["aria-expanded"] = TabsParts.Bool(context.Open);
        button.Attributes["aria-controls"] = shared.ContentId;
        button.Attributes["data-state"] = TabsParts.OpenState(context.Open);

        button.OnPointerEnter = EventComposition.Chain(button.OnPointerEnter, e =>
        {
            if (!e.DefaultPrevented && e.PointerType != PointerType.Touch && e.Buttons == PointerButtons.None
                && e.CurrentTarget is { IsDisabled: false })
                interaction.SetHover(shared, Root?.Clock, true, false, scope.OpenDelay, scope.CloseDelay);
        });
        button.OnPointerLeave = EventComposition.Chain(button.OnPointerLeave, e =>
        {
            if (e.PointerType != PointerType.Touch)
                interaction.SetHover(shared, Root?.Clock, false, false, scope.OpenDelay, scope.CloseDelay);
        });
        button.OnFocusIn = EventComposition.Chain(button.OnFocusIn, e =>
        {
            if (!e.DefaultPrevented && e.Origin != FocusOrigin.Pointer) interaction.SetFocus(shared, true);
        });
        button.OnFocusOut = EventComposition.Chain(button.OnFocusOut, e =>
        {
            if (e.CurrentTarget?.IsInclusiveAncestorOf(e.RelatedTarget) != true) interaction.SetFocus(shared, false);
        });

        Node anchor = AsChild ? Slot.Merge(button, Slot.SingleChild(Children)) : button;
        return new PopoverPrimitive.Anchor { AsChild = true, Children = [anchor] };
    }

    protected override void OnUnmount() => _interaction?.Reset();
}

public sealed partial class HoverCardContent : Component
{
    const string ContentClass =
        "z-50 w-64 rounded-md border bg-popover p-4 text-popover-foreground shadow-md outline-hidden " + PopupMotion.Classes;

    public Side Side { get; set; } = Side.Bottom;
    public float SideOffset { get; set; } = 4;
    public PopperAlign Align { get; set; } = PopperAlign.Center;
    public float AlignOffset { get; set; }
    public bool AvoidCollisions { get; set; } = true;
    public float CollisionPadding { get; set; }
    public float ArrowPadding { get; set; }
    public PopperSticky Sticky { get; set; } = PopperSticky.Partial;
    public bool HideWhenDetached { get; set; }
    public int ZOrder { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnEscapeKeyDown { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnPointerDownOutside { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnFocusOutside { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnInteractOutside { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<HoverCard.Scope>();
        var context = Use<PopoverPrimitive.Context>();
        var interaction = scope.Interaction;
        var shared = context.Shared;
        bool open = context.Open;
        var attrs = DialogAttributes.ForPart("hover-card-content", Rest, Class, ContentClass);
        attrs["data-state"] = TabsParts.OpenState(open);
        if (!open) attrs["aria-hidden"] = "true";
        attrs["onpointerenter"] = ComposePointer(attrs["onpointerenter"], e =>
        {
            if (!e.DefaultPrevented && e.PointerType != PointerType.Touch)
                interaction.SetHover(shared, Root?.Clock, true, true, scope.OpenDelay, scope.CloseDelay);
        });
        attrs["onpointerleave"] = ComposePointer(attrs["onpointerleave"], e =>
        {
            if (e.PointerType != PointerType.Touch)
                interaction.SetHover(shared, Root?.Clock, false, true, scope.OpenDelay, scope.CloseDelay);
        });
        attrs["onfocusin"] = ComposeFocus(attrs["onfocusin"], _ => interaction.SetFocus(shared, true));
        attrs["onfocusout"] = ComposeFocus(attrs["onfocusout"], e =>
        {
            if (e.CurrentTarget?.IsInclusiveAncestorOf(e.RelatedTarget) != true) interaction.SetFocus(shared, false);
        });

        var content = new PopperContent
        {
            Id = shared.ContentId,
            Side = Side,
            SideOffset = SideOffset,
            Align = Align,
            AlignOffset = AlignOffset,
            AvoidCollisions = AvoidCollisions,
            CollisionPadding = CollisionPadding,
            ArrowPadding = ArrowPadding,
            Sticky = Sticky,
            HideWhenDetached = HideWhenDetached,
            Attributes = attrs,
            Children = Children,
        };
        var layer = new DismissableLayer
        {
            Active = open,
            OnEscapeKeyDown = open ? OnEscapeKeyDown : null,
            OnPointerDownOutside = open ? OnPointerDownOutside : null,
            OnFocusOutside = open ? OnFocusOutside : null,
            OnInteractOutside = e =>
            {
                OnInteractOutside?.Invoke(e);
                if (shared.TriggerElement?.IsInclusiveAncestorOf(e.Target) == true) e.PreventDefault();
            },
            OnDismiss = _ =>
            {
                if (shared.IsOpen) interaction.Dismiss(shared);
            },
            Children = [content],
        };
        var focus = new FocusScope
        {
            Active = open,
            OnMountAutoFocus = e => e.PreventDefault(),
            OnUnmountAutoFocus = e =>
            {
                var focused = Root?.FocusManager.FocusedElement;
                // Restore focus only when it was inside the card; outside interaction keeps its own focus.
                if (focused is null || !e.Container.IsInclusiveAncestorOf(focused)) e.PreventDefault();
            },
            Children = [layer],
        };
        return new PresenceGate
        {
            Present = open,
            Children = [new Portal { ZOrder = ZOrder, Children = [focus] }],
        };
    }

    static object? ComposePointer(object? handler, Action<PointerEvent> own) => handler switch
    {
        Action<PointerEvent> typed => e => { typed(e); own(e); },
        Action plain => e => { plain(); own(e); },
        null => own,
        _ => own,
    };

    static object? ComposeFocus(object? handler, Action<FocusEvent> own) => handler switch
    {
        Action<FocusEvent> typed => e => { typed(e); own(e); },
        Action plain => e => { plain(); own(e); },
        null => own,
        _ => own,
    };
}
