using System;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using PopoverPrimitive = AwAUI.Controls.Primitives.Popover;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>Sets default hover delays for descendant tooltips, in milliseconds.</summary>
public sealed partial class TooltipProvider : Component
{
    public int DelayDuration { get; set; }
    public int CloseDelayDuration { get; set; } = 150;
    public NodeList? Children { get; set; }

    internal readonly struct Delays(int open, int close)
    {
        internal int Open { get; } = open;
        internal int Close { get; } = close;
    }

    protected override Node Build() => Provide(new Delays(DelayDuration, CloseDelayDuration),
        new Fragment(Children ?? new NodeList()));
}

/// <summary>Shows a description on pointer hover or keyboard focus without moving focus.</summary>
public sealed partial class Tooltip : Component
{
    readonly TooltipInteraction _interaction = new();

    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public Action<bool>? OnOpenChange { get; set; }
    public Action<bool>? OpenChanged { get; set; }
    public int? DelayDuration { get; set; }
    public int? CloseDelayDuration { get; set; }
    public NodeList? Children { get; set; }

    internal readonly struct Scope(TooltipInteraction interaction, int openDelay, int closeDelay)
    {
        internal TooltipInteraction Interaction { get; } = interaction;
        internal int OpenDelay { get; } = openDelay;
        internal int CloseDelay { get; } = closeDelay;
    }

    protected override Node Build()
    {
        if (!TryUse<TooltipProvider.Delays>(out var delays)) delays = new TooltipProvider.Delays(0, 150);
        return Provide(new Scope(_interaction, DelayDuration ?? delays.Open, CloseDelayDuration ?? delays.Close),
            new PopoverPrimitive.Root
            {
                Open = Open,
                DefaultOpen = DefaultOpen,
                OnOpenChange = OpenChanged + OnOpenChange,
                Children = Children,
            });
    }

    protected override void OnUnmount() => _interaction.Reset();
}

public sealed partial class TooltipTrigger : Component
{
    TooltipInteraction? _interaction;

    public bool AsChild { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<Tooltip.Scope>();
        var context = Use<PopoverPrimitive.Context>();
        var interaction = _interaction = scope.Interaction;
        var shared = context.Shared;
        var button = new UIButton { Children = Children ?? new NodeList() };
        button.MergeAttributes(DialogAttributes.ForPart("tooltip-trigger", Rest, Class));
        button.Attributes["data-state"] = interaction.StateName(context.Open);
        if (context.Open)
        {
            var description = button.Attributes["aria-describedby"];
            button.Attributes["aria-describedby"] = string.IsNullOrWhiteSpace(description)
                ? shared.ContentId : description + " " + shared.ContentId;
        }

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
            if (!e.DefaultPrevented && e.Origin != FocusOrigin.Pointer)
                interaction.SetFocus(shared, true);
        });
        button.OnFocusOut = EventComposition.Chain(button.OnFocusOut, e =>
        {
            if (e.CurrentTarget?.IsInclusiveAncestorOf(e.RelatedTarget) != true)
                interaction.SetFocus(shared, false);
        });
        button.OnPointerDown = EventComposition.Chain(button.OnPointerDown, e =>
        {
            // The mounted dismissable layer handles pointer presses while open.
            if (!e.DefaultPrevented && !shared.IsOpen) interaction.Reset();
        });
        button.OnClick = EventComposition.Chain(button.OnClick, e =>
        {
            if (!e.DefaultPrevented && e.PointerId == -1) interaction.Dismiss(shared);
        });

        Node anchor = AsChild ? Slot.Merge(button, Slot.SingleChild(Children)) : button;
        return new PopoverPrimitive.Anchor { AsChild = true, Children = [anchor] };
    }

    protected override void OnUnmount() => _interaction?.Reset();
}

public sealed partial class TooltipContent : Component
{
    const string ContentClass = "relative z-50 w-fit rounded-md bg-foreground px-3 py-1.5 text-xs text-balance text-background " + PopupMotion.Classes;
    const string ArrowClass = "pointer-events-none z-50 size-2.5 rotate-45 rounded-[2px] bg-foreground fill-foreground";

    public Side Side { get; set; } = Side.Top;
    public float SideOffset { get; set; }
    public PopperAlign Align { get; set; } = PopperAlign.Center;
    public float AlignOffset { get; set; }
    public bool AvoidCollisions { get; set; } = true;
    public float CollisionPadding { get; set; }
    public bool HideWhenDetached { get; set; }
    public bool ShowArrow { get; set; } = true;
    public int ZOrder { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnEscapeKeyDown { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<Tooltip.Scope>();
        var context = Use<PopoverPrimitive.Context>();
        var interaction = scope.Interaction;
        var shared = context.Shared;
        var attributes = DialogAttributes.ForPart("tooltip-content", Rest, Class, ContentClass);
        attributes["data-state"] = interaction.StateName(context.Open);
        if (!context.Open) attributes["aria-hidden"] = "true";
        attributes["onpointerenter"] = ComposePointer(attributes["onpointerenter"], e =>
        {
            if (!e.DefaultPrevented && shared.IsOpen && e.PointerType != PointerType.Touch)
                interaction.SetHover(shared, Root?.Clock, true, true, scope.OpenDelay, scope.CloseDelay);
        });
        attributes["onpointerleave"] = ComposePointer(attributes["onpointerleave"], e =>
        {
            if (e.PointerType != PointerType.Touch)
                interaction.SetHover(shared, Root?.Clock, false, true, scope.OpenDelay, scope.CloseDelay);
        });

        var children = Children;
        if (ShowArrow)
        {
            // A square position box keeps the diamond centered after Popper flips sides.
            children = new NodeList(Children)
            {
                new PopperArrow
                {
                    Width = 10,
                    Height = 10,
                    Class = "pointer-events-none group/tooltip-arrow",
                    Children =
                    [
                        new Div
                        {
                            Class = ArrowClass + " absolute group-data-[side=bottom]/tooltip-arrow:top-[3px] group-data-[side=top]/tooltip-arrow:bottom-[3px] group-data-[side=right]/tooltip-arrow:left-[3px] group-data-[side=left]/tooltip-arrow:right-[3px]",
                        },
                    ],
                },
            };
        }

        // A tooltip shares Popover's overlay and positioning, without the dialog autofocus scope.
        return new PopoverPrimitive.Portal
        {
            ZOrder = ZOrder,
            Children =
            [
                new DismissableLayer
                {
                    Active = context.Open,
                    OnEscapeKeyDown = context.Open ? OnEscapeKeyDown : null,
                    OnFocusOutside = static e => e.PreventDefault(),
                    OnDismiss = context.Open ? _ => interaction.Dismiss(shared) : null,
                    Children =
                    [
                        new PopperContent
                        {
                            Id = shared.ContentId,
                            Role = Role.Tooltip,
                            Side = Side,
                            SideOffset = SideOffset,
                            Align = Align,
                            AlignOffset = AlignOffset,
                            AvoidCollisions = AvoidCollisions,
                            CollisionPadding = CollisionPadding,
                            HideWhenDetached = HideWhenDetached,
                            Attributes = attributes,
                            Children = children,
                        },
                    ],
                },
            ],
        };
    }

    static Action<PointerEvent> ComposePointer(object? handler, Action<PointerEvent> own) => handler switch
    {
        Action<PointerEvent> typed => e => { typed(e); own(e); },
        Action plain => e => { plain(); own(e); },
        null => own,
        _ => throw new ArgumentException("Pointer handlers must be Action<PointerEvent> or Action."),
    };
}

// Manages only tooltip input and timing. The Popover root owns the open state and its callbacks.
internal sealed class TooltipInteraction
{
    bool _triggerHovered;
    bool _contentHovered;
    bool _focused;
    bool _delayedOpen;
    IDisposable? _timer;
    int _timerVersion;

    bool IsHovered => _triggerHovered || _contentHovered;

    internal string StateName(bool open) => !open ? "closed" : _delayedOpen ? "delayed-open" : "instant-open";

    internal void SetHover(OpenableShared shared, UIClock? clock, bool hovered, bool content, int openDelay, int closeDelay)
    {
        bool wasHovered = IsHovered;
        if (content) _contentHovered = hovered;
        else _triggerHovered = hovered;
        if (wasHovered == IsHovered || _focused) return;
        Schedule(shared, clock, IsHovered, IsHovered ? openDelay : closeDelay);
    }

    internal void SetFocus(OpenableShared shared, bool focused)
    {
        if (_focused == focused) return;
        _focused = focused;
        if (focused || !IsHovered) Schedule(shared, null, focused, 0);
    }

    internal void Dismiss(OpenableShared shared)
    {
        Reset();
        shared.SetOpen(false);
    }

    internal void Reset()
    {
        CancelTimer();
        _triggerHovered = false;
        _contentHovered = false;
        _focused = false;
    }

    void Schedule(OpenableShared shared, UIClock? clock, bool open, int delay)
    {
        CancelTimer();
        if (shared.IsOpen == open) return;
        if (clock is null || delay <= 0)
        {
            SetOpen(shared, open, false);
            return;
        }

        int version = _timerVersion;
        _timer = clock.Schedule(TimeSpan.FromMilliseconds(delay), () =>
        {
            if (version != _timerVersion) return;
            _timer = null;
            SetOpen(shared, open, open);
        });
    }

    void SetOpen(OpenableShared shared, bool open, bool delayed)
    {
        if (open) _delayedOpen = delayed;
        shared.SetOpen(open);
    }

    void CancelTimer()
    {
        _timerVersion++;
        _timer?.Dispose();
        _timer = null;
    }
}
