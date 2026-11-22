using System;
using System.Collections.Generic;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>
/// Shared building blocks for the S2 menu families (ContextMenu, Menubar). They reuse the S1
/// <see cref="DropdownMenuParts.Scope"/> for dismissal, roving item focus, typeahead, and submenus,
/// while owning their own surface so each family can keep its registry slot and class strings.
/// </summary>
internal static class S2MenuParts
{
    internal const string ItemClass =
        "relative flex w-full cursor-default select-none items-center gap-2 rounded-sm border-0 bg-transparent px-2 py-1.5 text-left text-sm outline-hidden " +
        "focus:bg-accent focus:text-accent-foreground disabled:pointer-events-none disabled:opacity-50 " +
        "[&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4";

    internal static UIButton ItemButton(string slot, bool disabled, bool inset, bool destructive, string? classes,
        AttributeDictionary? rest, NodeList? children, bool openHighlight = false)
    {
        var button = new UIButton
        {
            Role = Role.MenuItem,
            TabIndex = -1,
            Disabled = disabled,
            Children = new NodeList(children),
        };
        TabsParts.Style(button, slot, ItemClass +
            (destructive ? " text-destructive focus:text-destructive" : "") +
            (openHighlight ? " data-[state=open]:bg-accent data-[state=open]:text-accent-foreground" : ""), classes, rest);
        button.Attributes["type"] = "button";
        button.Attributes["data-variant"] = destructive ? "destructive" : "default";
        if (inset) button.Attributes["data-inset"] = "true";
        TabsParts.Disabled(button, disabled);
        return button;
    }

    internal static Node Item(DropdownMenuParts.Scope scope, string slot, bool disabled, bool inset, bool destructive,
        string? classes, AttributeDictionary? rest, NodeList? children, Action<PointerEvent>? select,
        string? role = null, bool? selected = null, bool radio = false)
    {
        var button = ItemButton(slot, disabled, inset, destructive, classes, rest, children);
        if (role is not null) button.Attributes["role"] = role;
        if (selected is { } isSelected)
        {
            button.Attributes["aria-checked"] = TabsParts.Bool(isSelected);
            button.Attributes["data-state"] = isSelected ? "checked" : "unchecked";
            var glyph = radio
                ? SelectParts.Glyph(LucideIcons.Circle, slot + "-indicator", "size-2 fill-current")
                : SelectParts.Glyph(LucideIcons.Check, slot + "-indicator", "size-4");
            button.Children.Add(new Span
            {
                Class = "pointer-events-none absolute left-2 flex size-3.5 items-center justify-center",
                Children = isSelected ? [glyph] : [],
            });
        }

        var onClick = button.OnClick;
        button.OnClick = e =>
        {
            onClick?.Invoke(e);
            if (e.DefaultPrevented || button.Disabled || !scope.IsOpen) return;
            select?.Invoke(e);
            if (!e.DefaultPrevented) scope.Close();
        };
        var onPointerMove = button.OnPointerMove;
        button.OnPointerMove = e =>
        {
            onPointerMove?.Invoke(e);
            if (!e.DefaultPrevented && e.PointerType == PointerType.Mouse && scope.IsOpen && !scope.InPointerGrace(e)
                && e.CurrentTarget is { IsFocused: false, IsFocusable: true } element)
                element.Focus(new FocusOptions { Origin = FocusOrigin.Pointer, PreventScroll = true });
        };
        return button;
    }

    internal static Node Group(string slot, NodeList? children, string? classes, AttributeDictionary? rest, string defaults = "") =>
        TabsParts.Style(new Div { Role = Role.Group, Children = children ?? [] }, slot, defaults, classes, rest);

    internal static Node Label(string slot, bool inset, NodeList? children, string? classes, AttributeDictionary? rest,
        string defaults)
    {
        var label = TabsParts.Style(new UI.Text { Children = children ?? [] }, slot, defaults, classes, rest);
        if (inset) label.Attributes["data-inset"] = "true";
        return label;
    }

    internal static Node Separator(string slot, string? classes, AttributeDictionary? rest) =>
        TabsParts.Style(new Div { Role = Role.Separator }, slot, "-mx-1 my-1 h-px bg-border", classes, rest);

    internal static Node Shortcut(string slot, NodeList? children, string? classes, AttributeDictionary? rest) =>
        TabsParts.Style(new Span { Children = children ?? [] }, slot, "ml-auto text-xs tracking-widest text-muted-foreground",
            classes, rest);

    internal static List<Element> MenuItems(Element menu)
    {
        var items = new List<Element>();
        Visit(menu);
        return items;

        void Visit(Element parent)
        {
            foreach (var child in parent.ElementChildren)
            {
                if (child.Role == Role.Menu || child.GetAttribute("role") == "menu") continue;
                if (child.Role == Role.MenuItem || child.GetAttribute("role") is "menuitemcheckbox" or "menuitemradio")
                {
                    if (child.IsFocusable && child.IsVisible) items.Add(child);
                    continue;
                }
                Visit(child);
            }
        }
    }

    internal static void FocusItem(Element? content, FocusOrigin origin, bool last)
    {
        if (content is null) return;
        var options = new FocusOptions { Origin = origin };
        var items = MenuItems(content);
        if (last)
        {
            for (int i = items.Count - 1; i >= 0; i--)
                if (items[i].Focus(options)) return;
        }
        else
        {
            foreach (var item in items)
                if (item.Focus(options)) return;
        }
        content.Focus(options);
    }
}

/// <summary>
/// The anchored menu surface used by the S2 menu families. It mirrors the S1 dropdown surface but keeps the
/// registry slot and class strings per family and restores focus to the family trigger instead of a popover trigger.
/// </summary>
internal sealed partial class S2MenuSurface : Component
{
    readonly Ref<Element> _content = new();
    bool _openAtEnd;
    DropdownMenuParts.Scope? _scope;
    bool _wasOpen;

    public string Slot { get; set; } = "menu-content";
    public string ContentClass { get; set; } = "";
    public bool Open { get; set; }
    public OpenableShared Popup { get; set; } = null!;
    public DropdownMenuParts.Scope? ParentMenu { get; set; }
    public DropdownMenuSubmenu? Submenu { get; set; }
    public bool Modal { get; set; }
    public int ZOrder { get; set; }
    public Side Side { get; set; }
    public PopperAlign Align { get; set; }
    public float SideOffset { get; set; }
    public float AlignOffset { get; set; }
    public float CollisionPadding { get; set; }
    public bool AvoidCollisions { get; set; } = true;
    public bool OpenAtEnd { get; set; }
    public string? LabelId { get; set; }
    public Ref<Element>? ContentRef { get; set; }
    public Func<Element?>? FocusTarget { get; set; }
    public Action<KeyEvent>? OnMenuKeyDown { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        _openAtEnd = OpenAtEnd;
        var submenu = Submenu;
        var reference = submenu?.Content ?? ContentRef ?? _content;
        var scope = _scope ??= new DropdownMenuParts.Scope(Popup, ParentMenu, reference, ZOrder);
        var attrs = DialogAttributes.ForPart(Slot, Rest, Class, ContentClass);
        attrs["data-state"] = TabsParts.OpenState(Open);
        if (!Open) attrs["aria-hidden"] = "true";
        if (Open && !_wasOpen)
        {
            scope.SkipRestore = false;
            scope.Search.Reset();
        }
        else if (!Open && _wasOpen && submenu is not null)
        {
            scope.Parent?.ClearGrace(scope.Popup);
            RestoreSubmenuFocus(submenu, scope);
        }
        _wasOpen = Open;
        if (submenu is not null)
        {
            attrs["id"] = Popup.ContentId;
            if (attrs["aria-label"] is null && attrs["aria-labelledby"] is null) attrs["aria-labelledby"] = submenu.TriggerId;
        }
        else if (LabelId is not null && attrs["aria-label"] is null && attrs["aria-labelledby"] is null)
        {
            attrs["aria-labelledby"] = LabelId;
        }

        var events = new Div();
        events.MergeAttributes(attrs);
        attrs["onkeydown"] = EventComposition.Compose(events.OnKeyDown, e =>
        {
            OnMenuKeyDown?.Invoke(e);
            if (!e.DefaultPrevented) scope.KeyDown(e, submenu is null ? null : CloseToTrigger);
        });
        if (submenu is not null)
        {
            attrs["onpointerenter"] = EventComposition.Chain(events.OnPointerEnter, e =>
            {
                if (e.PointerType == PointerType.Mouse) scope.Parent?.ClearGrace(scope.Popup);
            });
        }

        var content = new PopperContent
        {
            Id = Popup.ContentId,
            Role = Role.Menu,
            TabIndex = -1,
            Ref = reference,
            Side = Side,
            Align = Align,
            SideOffset = SideOffset,
            AlignOffset = AlignOffset,
            CollisionPadding = CollisionPadding,
            AvoidCollisions = AvoidCollisions,
            Attributes = attrs,
            Children = [Provide(scope, Provide(new DropdownMenuParts.MotionState(Open), new Fragment(Children ?? [])))],
        };
        var layer = new DismissableLayer
        {
            Active = Open,
            DisableOutsidePointerEvents = submenu is null && Modal,
            OnInteractOutside = e =>
            {
                var trigger = submenu?.Trigger.Current ?? FocusTarget?.Invoke() ?? Popup.TriggerElement;
                if (!scope.IsOpen || trigger?.IsInclusiveAncestorOf(e.Target) == true) e.PreventDefault();
            },
            OnDismiss = reason =>
            {
                if (!scope.IsOpen) return;
                if (submenu is not null && reason == DismissReason.EscapeKey)
                {
                    CloseToTrigger();
                    return;
                }
                if (reason == DismissReason.FocusOutside || (reason == DismissReason.PointerDownOutside && (submenu is not null || !Modal)))
                    scope.SkipRestore = true;
                scope.Popup.SetOpen(false);
            },
            Children = [content],
        };
        if (submenu is not null)
            return new PresenceGate { Present = Open, Children = [new Portal { ZOrder = ZOrder, Children = [layer] }] };

        var root = Root!;
        return new PresenceGate
        {
            Present = Open,
            Children =
            [
                new Portal
                {
                    ZOrder = ZOrder,
                    Children =
                    [
                        new FocusScope
                        {
                            Active = Open,
                            OnMountAutoFocus = e =>
                            {
                                e.PreventDefault();
                                if (!scope.IsOpen) return;
                                bool last = _openAtEnd;
                                _openAtEnd = false;
                                S2MenuParts.FocusItem(reference.Current,
                                    root.FocusManager.LastInteractionWasKeyboard ? FocusOrigin.Keyboard : FocusOrigin.Programmatic, last);
                            },
                            OnUnmountAutoFocus = e =>
                            {
                                e.PreventDefault();
                                var focused = root.FocusManager.FocusedElement;
                                if (!scope.SkipRestore && (focused is null || OwnsFocus(focused)))
                                    FocusTarget?.Invoke()?.Focus();
                            },
                            Children = [layer],
                        },
                    ],
                },
            ],
        };
    }

    void CloseToTrigger()
    {
        if (_scope is not { IsOpen: true } scope) return;
        Submenu?.Trigger.Current?.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });
        scope.Popup.SetOpen(false);
    }

    protected override void OnUnmount()
    {
        if (Submenu is not { } submenu || _scope is not { Parent: { } parent } scope) return;
        parent.ClearGrace(scope.Popup);
        if (_wasOpen) RestoreSubmenuFocus(submenu, scope);
    }

    void RestoreSubmenuFocus(DropdownMenuSubmenu submenu, DropdownMenuParts.Scope scope)
    {
        if (scope.SkipRestore || scope.Parent is not { IsOpen: true } parent || Root is not { } root
            || root.FocusManager.FocusedElement is not { } focused || !OwnsFocus(focused)) return;
        root.RequestAfterLayout(() =>
        {
            var current = root.FocusManager.FocusedElement;
            if (parent.IsOpen && !scope.IsOpen && (current is null || OwnsFocus(current)))
                submenu.Trigger.Current?.Focus();
        });
    }

    bool OwnsFocus(Element focused)
    {
        for (var owner = focused.Owner; owner is not null; owner = owner.ParentComponent)
            if (ReferenceEquals(owner, this)) return true;
        return false;
    }
}

/// <summary>A submenu trigger shared by the S2 menu families.</summary>
internal sealed partial class S2SubmenuTrigger : Component
{
    IDisposable? _hoverTimer;
    int _hoverVersion;
    bool _hoverStarted;
    PopperScope? _popper;

    public string Slot { get; set; } = "menu-sub-trigger";
    public string Classes { get; set; } = "";
    public bool Disabled { get; set; }
    public bool Inset { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var parent = Use<DropdownMenuParts.Scope>();
        var submenu = Use<DropdownMenuSubmenu>();
        var popup = Use<AwAUI.Controls.Primitives.Popover.Context>();
        var popper = _popper = Use<PopperScope>();
        popper.DefaultAnchor = this;
        bool rtl = parent.Direction == Direction.Rtl;
        var button = S2MenuParts.ItemButton(Slot, Disabled, Inset, false,
            AwAUI.Tailwind.TailwindMerge.Cn(Classes, Class), Rest, Children, openHighlight: true);
        button.Id = submenu.TriggerId;
        button.Ref = submenu.Trigger;
        button.Attributes["aria-haspopup"] = "menu";
        button.Attributes["aria-controls"] = popup.Shared.ContentId;
        button.Attributes["aria-expanded"] = TabsParts.Bool(popup.Open);
        button.Attributes["aria-disabled"] = button.Disabled ? "true" : null;
        button.Attributes["data-state"] = TabsParts.OpenState(popup.Open);
        button.Children.Add(SelectParts.Glyph(rtl ? LucideIcons.ChevronLeft : LucideIcons.ChevronRight,
            Slot + "-icon", "pointer-events-none ml-auto size-4 shrink-0"));
        button.OnKeyDown = EventComposition.Compose(button.OnKeyDown, e =>
        {
            if (e.DefaultPrevented || button.Disabled || !parent.IsOpen || e.Modifiers != KeyModifiers.None
                || !ReferenceEquals(e.Target, submenu.Trigger.Current)) return;
            string openKey = parent.Direction == Direction.Rtl ? "ArrowLeft" : "ArrowRight";
            if (e.Key != openKey && e.Key != "Enter" && (e.Key != " " || parent.Search.IsTypingAhead(e.Timestamp))) return;
            e.PreventDefault();
            OpenAndFocus(parent, submenu, popup.Shared, FocusOrigin.Keyboard);
        });
        button.OnClick = EventComposition.Compose(button.OnClick, e =>
        {
            if (!e.DefaultPrevented && !button.Disabled && parent.IsOpen)
                OpenAndFocus(parent, submenu, popup.Shared, e.IsSynthetic ? FocusOrigin.Keyboard : FocusOrigin.Pointer);
        });
        button.OnPointerMove = EventComposition.Compose(button.OnPointerMove, e =>
        {
            if (e.DefaultPrevented || e.PointerType != PointerType.Mouse || !parent.IsOpen) return;
            parent.ClearGrace(popup.Shared);
            if (parent.InPointerGrace(e) || button.Disabled || e.Buttons != PointerButtons.None
                || submenu.Trigger.Current is not { IsFocusable: true } trigger) return;
            if (!trigger.IsFocused) trigger.Focus(new FocusOptions { Origin = FocusOrigin.Pointer, PreventScroll = true });
            if (trigger.IsFocused && !popup.Shared.IsOpen && !_hoverStarted) StartHover(parent, popup.Shared, trigger);
        });
        button.OnPointerLeave = EventComposition.Chain(button.OnPointerLeave, e =>
        {
            if (e.PointerType != PointerType.Mouse) return;
            CancelHover();
            if (!popup.Shared.IsOpen || submenu.Content.Current is not { IsVisible: true } content
                || popper.LastLayout is not { PlacedSide: Side.Left or Side.Right } placement) return;
            parent.Grace.TrackPointerMove(e.Position);
            parent.Grace.Begin(e.Position, content.GetBoundingClientRect(), placement.PlacedSide, e.Timestamp);
            parent.GraceOwner = popup.Shared;
        });
        button.OnBlur = EventComposition.Chain(button.OnBlur, _ => CancelHover());
        return button;
    }

    void OpenAndFocus(DropdownMenuParts.Scope parent, DropdownMenuSubmenu submenu, OpenableShared popup, FocusOrigin origin)
    {
        CancelHover();
        parent.ClearGrace(popup);
        if (submenu.Trigger.Current is not { Root: { } root } trigger || !trigger.Focus(new FocusOptions { Origin = origin })) return;
        popup.SetOpen(true);
        root.RequestAfterLayout(() =>
        {
            if (trigger.IsFocused && parent.IsOpen && popup.IsOpen)
                S2MenuParts.FocusItem(submenu.Content.Current, origin, false);
        });
    }

    void StartHover(DropdownMenuParts.Scope parent, OpenableShared popup, Element trigger)
    {
        _hoverStarted = true;
        int version = _hoverVersion;
        void Open()
        {
            if (version != _hoverVersion) return;
            _hoverTimer = null;
            if (trigger is { IsFocused: true, IsFocusable: true, IsVisible: true } && parent.IsOpen) popup.SetOpen(true);
        }

        if (Root?.Clock is { } clock) _hoverTimer = clock.Schedule(TimeSpan.FromMilliseconds(100), Open);
        else Open();
    }

    void CancelHover()
    {
        _hoverVersion++;
        _hoverTimer?.Dispose();
        _hoverTimer = null;
        _hoverStarted = false;
    }

    protected override void OnUnmount()
    {
        CancelHover();
        if (_popper is { } popper && ReferenceEquals(popper.DefaultAnchor, this)) popper.DefaultAnchor = null;
    }
}

/// <summary>A submenu panel shared by the S2 menu families.</summary>
internal sealed partial class S2SubmenuContent : Component
{
    public string Slot { get; set; } = "menu-sub-content";
    public string ContentClass { get; set; } = "";
    public PopperAlign Align { get; set; } = PopperAlign.Start;
    public float SideOffset { get; set; } = 2;
    public float AlignOffset { get; set; }
    public float CollisionPadding { get; set; } = 8;
    public bool AvoidCollisions { get; set; } = true;
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var popup = Use<AwAUI.Controls.Primitives.Popover.Context>();
        bool open = popup.Open && Use<DropdownMenuParts.MotionState>().Open;
        var parent = Use<DropdownMenuParts.Scope>();
        return new S2MenuSurface
        {
            Slot = Slot,
            ContentClass = ContentClass,
            Popup = popup.Shared,
            Open = open,
            ParentMenu = parent,
            Submenu = Use<DropdownMenuSubmenu>(),
            Side = Side.InlineEnd,
            Align = Align,
            AlignOffset = AlignOffset,
            SideOffset = SideOffset,
            CollisionPadding = CollisionPadding,
            AvoidCollisions = AvoidCollisions,
            Class = Class,
            Rest = Rest,
            Children = Children,
        };
    }
}
