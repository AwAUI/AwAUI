using System;
using System.Collections.Generic;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.UI;
using PopoverPrimitive = AwAUI.Controls.Primitives.Popover;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>Anchored menu with keyboard navigation and typeahead.</summary>
public sealed partial class DropdownMenu : Component
{
    readonly DropdownMenuParts.OpenIntent _openIntent = new();

    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public bool Modal { get; set; } = true;
    public Action<bool>? OpenChanged { get; set; }
    public Action<bool>? OnOpenChange { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => Provide(_openIntent, new PopoverPrimitive.Root
    {
        Open = Open,
        DefaultOpen = DefaultOpen,
        Modal = Modal,
        OnOpenChange = OpenChanged + OnOpenChange,
        Children = Children,
    });
}

public sealed partial class DropdownMenuTrigger : Component
{
    public bool AsChild { get; set; }
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var popup = Use<PopoverPrimitive.Context>();
        var button = TabsParts.Style(new UIButton { Disabled = Disabled, Children = Children ?? [] },
            "dropdown-menu-trigger", null, Class, Rest);
        button.Attributes["aria-haspopup"] = "menu";
        DropdownMenuParts.OpenIntent? openIntent = TryUse<DropdownMenuParts.OpenIntent>(out var intent) ? intent : null;
        button.OnKeyDown = EventComposition.Compose(button.OnKeyDown, e =>
        {
            if (!button.Disabled && !popup.Open && e.Key is "ArrowDown" or "ArrowUp")
            {
                e.PreventDefault();
                if (openIntent is not null) openIntent.Last = e.Key == "ArrowUp";
                popup.Shared.SetOpen(true);
            }
        });
        Node child = AsChild ? Slot.Merge(button, Slot.SingleChild(Children)) : button;
        return new PopoverPrimitive.Trigger { AsChild = true, Children = [child] };
    }
}

/// <summary>Explicit menu portal. Content also creates one automatically when used directly.</summary>
public sealed partial class DropdownMenuPortal : Component
{
    public int ZOrder { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        var popup = Use<PopoverPrimitive.Context>();
        bool open = popup.Open && (!TryUse<DropdownMenuParts.MotionState>(out var parentMotion) || parentMotion.Open);
        int order = DropdownMenuParts.PortalOrder(TryUse<DropdownMenuParts.Scope>(out var host) ? host : null, ZOrder);
        return new PresenceGate
        {
            Present = open,
            Children = [new Portal
            {
                ZOrder = order,
                Children = [Provide(new DropdownMenuParts.InPortal(popup.Shared, order), new Fragment(Children ?? []))],
            }],
        };
    }
}

public sealed partial class DropdownMenuContent : Component
{
    public Side Side { get; set; } = Side.Bottom;
    public PopperAlign Align { get; set; } = PopperAlign.Start;
    public float SideOffset { get; set; } = 4;
    public float CollisionPadding { get; set; } = 8;
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var popup = Use<PopoverPrimitive.Context>();
        bool open = popup.Open && (!TryUse<DropdownMenuParts.MotionState>(out var parentMotion) || parentMotion.Open);
        TryUse<DropdownMenuParts.InPortal>(out var portal);
        TryUse<DropdownMenuParts.Scope>(out var host);
        return DropdownMenuParts.RenderSurface(portal, host, new DropdownMenuSurface
        {
            Popup = popup.Shared, Open = open, Modal = popup.Modal,
            Side = Side, Align = Align, SideOffset = SideOffset, CollisionPadding = CollisionPadding,
            Class = Class, Rest = Rest, Children = Children,
        });
    }
}

public sealed partial class DropdownMenuItem : Component
{
    public bool Disabled { get; set; }
    public bool Inset { get; set; }
    public bool Destructive { get; set; }
    public Action<PointerEvent>? OnSelect { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => DropdownMenuParts.Item(Use<DropdownMenuParts.Scope>(), "dropdown-menu-item",
        Disabled, Inset, Class, Rest, Children, OnSelect, destructive: Destructive);
}

public sealed partial class DropdownMenuCheckboxItem : Component
{
    readonly ControllableState<bool> _checked = new();
    public bool? Checked { get; set; }
    public bool DefaultChecked { get; set; }
    public Action<bool>? CheckedChanged { get; set; }
    public Action<PointerEvent>? OnSelect { get; set; }
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        bool selected = _checked.Sync(Checked, DefaultChecked, CheckedChanged);
        return DropdownMenuParts.Item(Use<DropdownMenuParts.Scope>(), "dropdown-menu-checkbox-item", Disabled, true,
            Class, Rest, Children, EventComposition.Compose(OnSelect, _ => _checked.Set(!_checked.Value)), "menuitemcheckbox", selected);
    }
}

public sealed partial class DropdownMenuRadioGroup : Component
{
    readonly ControllableState<string> _value = new(StringComparer.Ordinal);
    public string? Value { get; set; }
    public string DefaultValue { get; set; } = "";
    public Action<string>? ValueChanged { get; set; }
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => Provide(new Selection(_value.Sync(Value, DefaultValue, ValueChanged), _value.Set),
        TabsParts.Style(new Div { Role = Role.Group, Children = Children ?? [] }, "dropdown-menu-radio-group", null, Class, Rest));

    internal sealed record Selection(string Value, Action<string> Set);
}

public sealed partial class DropdownMenuRadioItem : Component
{
    public string Value { get; set; } = "";
    public bool Disabled { get; set; }
    public Action<PointerEvent>? OnSelect { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var radio = Use<DropdownMenuRadioGroup.Selection>();
        return DropdownMenuParts.Item(Use<DropdownMenuParts.Scope>(), "dropdown-menu-radio-item", Disabled, true,
            Class, Rest, Children, EventComposition.Compose(OnSelect, _ => radio.Set(Value)), "menuitemradio", Value == radio.Value,
            radio: true);
    }
}

public sealed partial class DropdownMenuGroup : Component
{
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }
    protected override Node Build() => TabsParts.Style(new Div { Role = Role.Group, Children = Children ?? [] },
        "dropdown-menu-group", null, Class, Rest);
}

public sealed partial class DropdownMenuLabel : Component
{
    public bool Inset { get; set; }
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }
    protected override Node Build() => TabsParts.Style(new UI.Text { Children = Children ?? [] },
            "dropdown-menu-label", "px-2 py-1.5 text-sm font-medium" + (Inset ? " pl-8" : ""), Class,
            Inset ? WithInset(Rest) : Rest);

    static AttributeDictionary WithInset(AttributeDictionary? rest)
    {
        var attributes = rest is null ? new AttributeDictionary() : new AttributeDictionary(rest);
        attributes["data-inset"] = "true";
        return attributes;
    }
}

public sealed partial class DropdownMenuSeparator : Component
{
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }
    protected override Node Build() => TabsParts.Style(new Div { Role = Role.Separator },
        "dropdown-menu-separator", "-mx-1 my-1 h-px bg-border", Class, Rest);
}

public sealed partial class DropdownMenuShortcut : Component
{
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }
    protected override Node Build() => TabsParts.Style(new Span { Children = Children ?? [] },
        "dropdown-menu-shortcut", "ml-auto text-xs tracking-widest text-muted-foreground", Class, Rest);
}

internal static class DropdownMenuParts
{
    internal sealed record InPortal(OpenableShared Popup, int ZOrder);
    internal readonly record struct MotionState(bool Open);

    /// <summary>Remembers whether the menu was opened with ArrowUp so the surface can focus the last item.</summary>
    internal sealed class OpenIntent
    {
        internal bool Last;
    }

    internal static int PortalOrder(Scope? host, int order)
    {
        if (host is null) return order;
        return order == 0 ? host.ZOrder : Math.Max(host.ZOrder, order);
    }

    internal static Node RenderSurface(InPortal? portal, Scope? host, DropdownMenuSurface surface)
    {
        if (portal is not null && ReferenceEquals(portal.Popup, surface.Popup))
        {
            surface.ZOrder = portal.ZOrder;
            return surface;
        }

        // Even within an explicit parent portal, a submenu needs its own layer above the parent.
        surface.ZOrder = PortalOrder(host, 0);
        return new PresenceGate
        {
            Present = surface.Open,
            Children = [new Portal { ZOrder = surface.ZOrder, Children = [surface] }],
        };
    }

    internal sealed class Scope(OpenableShared popup, Scope? parent, Ref<Element> content, int zOrder)
    {
        internal OpenableShared Popup { get; } = popup;
        internal Scope? Parent { get; } = parent;
        internal int ZOrder { get; } = zOrder;
        internal bool SkipRestore;
        internal readonly TypeAhead Search = new();
        internal readonly SubmenuGraceArea Grace = new();
        internal OpenableShared? GraceOwner;

        internal bool IsOpen => Popup.IsOpen && (Parent?.IsOpen ?? true);
        internal Direction Direction => Parent?.Direction ?? TabsParts.ResolveDirection(Popup.TriggerElement, null);

        internal void Close(bool restoreFocus = true)
        {
            for (var scope = this; scope is not null; scope = scope.Parent)
            {
                if (!restoreFocus) scope.SkipRestore = true;
                scope.Popup.SetOpen(false);
            }
        }

        internal void ClearGrace(OpenableShared submenu)
        {
            if (!ReferenceEquals(GraceOwner, submenu)) return;
            Grace.Clear();
            GraceOwner = null;
        }

        internal bool InPointerGrace(PointerEvent e)
        {
            Grace.TrackPointerMove(e.Position);
            if (GraceOwner is not { } submenu) return false;
            if (submenu.IsOpen && Grace.IsPointerMovingToSubmenu(e.Position, e.Timestamp)) return true;
            ClearGrace(submenu);
            return false;
        }

        internal void FocusFirst(FocusOrigin origin, bool last = false) => FocusFirst(content.Current, origin, last);

        internal static void FocusFirst(Element? content, FocusOrigin origin, bool last = false)
        {
            if (content is null) return;
            var options = new FocusOptions { Origin = origin };
            var items = Items(content);
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

        internal void KeyDown(KeyEvent e, Action? closeSubmenu)
        {
            if (e.DefaultPrevented || !IsOpen || content.Current is not { } menu) return;
            if (e.Key == "Tab" && (e.Modifiers & (KeyModifiers.Alt | KeyModifiers.Control | KeyModifiers.Meta)) == 0)
            {
                Close(restoreFocus: false);
                return;
            }
            if (closeSubmenu is not null && e.Modifiers == KeyModifiers.None && e.Key == (Direction == Direction.Rtl ? "ArrowRight" : "ArrowLeft"))
            {
                e.PreventDefault();
                closeSubmenu();
                return;
            }

            var items = Items(menu);
            if (items.Count == 0) return;
            int current = menu.Root?.FocusManager.FocusedElement is { } focused ? items.IndexOf(focused) : -1;
            var intent = RovingFocus.GetFocusIntent(e.Key, Orientation.Vertical, Direction);
            int next = -1;
            if (intent != FocusIntent.None && e.Modifiers == KeyModifiers.None)
            {
                e.PreventDefault();
                next = RovingFocus.Navigate(intent, current, items.Count, null, loop: true);
            }
            else if (TypeAhead.IsTypeAheadKey(e.Key, e.Modifiers) && (e.Key != " " || Search.IsTypingAhead(e.Timestamp)))
            {
                next = Search.HandleKey(e.Key, e.Timestamp, items.ConvertAll<string?>(x => x.TextContent.Trim()), current);
                if (next >= 0 || e.Key == " ") e.PreventDefault();
            }
            if (next >= 0) items[next].Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });
        }

        static List<Element> Items(Element menu)
        {
            var items = new List<Element>();
            Visit(menu);
            return items;

            void Visit(Element parent)
            {
                foreach (var element in parent.ElementChildren)
                {
                    if (element.Role == Role.Menu || element.GetAttribute("role") == "menu") continue;
                    if (element.Role == Role.MenuItem || element.GetAttribute("role") is "menuitemcheckbox" or "menuitemradio")
                    {
                        if (element.IsFocusable && element.IsVisible) items.Add(element);
                        continue;
                    }
                    Visit(element);
                }
            }
        }
    }

    internal static UIButton ItemButton(string slot, bool disabled, bool inset, string? classes,
        AttributeDictionary? rest, NodeList? children, bool destructive = false, bool openHighlight = false)
    {
        var button = TabsParts.Style(new UIButton { Role = Role.MenuItem, TabIndex = -1, Disabled = disabled,
            Children = new NodeList(children) }, slot,
            "relative flex w-full cursor-default items-center gap-2 rounded-sm border-0 bg-transparent px-2 py-1.5 text-left text-sm outline-hidden select-none focus:bg-accent focus:text-accent-foreground disabled:pointer-events-none disabled:opacity-50 data-[disabled]:pointer-events-none data-[disabled]:opacity-50 " +
            "[&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4 [&_svg:not([class*='text-'])]:text-muted-foreground " +
            "data-[variant=destructive]:text-destructive data-[variant=destructive]:focus:bg-destructive/10 data-[variant=destructive]:focus:text-destructive dark:data-[variant=destructive]:focus:bg-destructive/20 data-[variant=destructive]:*:[svg]:text-destructive!" +
            (inset ? " data-[inset]:pl-8" : "") +
            (openHighlight ? " data-[state=open]:bg-accent data-[state=open]:text-accent-foreground" : ""), classes, rest);
        button.Attributes["type"] = "button";
        button.Attributes["data-variant"] = destructive ? "destructive" : "default";
        if (inset) button.Attributes["data-inset"] = "true";
        TabsParts.Disabled(button, button.Disabled);
        return button;
    }

    internal static Node Item(Scope scope, string slot, bool disabled, bool inset, string? classes,
        AttributeDictionary? rest, NodeList? children, Action<PointerEvent>? select, string? role = null,
        bool? selected = null, bool destructive = false, bool radio = false)
    {
        var button = ItemButton(slot, disabled, inset, classes, rest, children, destructive);
        if (role is not null) button.Attributes["role"] = role;
        if (selected is { } isSelected)
        {
            button.Attributes["aria-checked"] = TabsParts.Bool(isSelected);
            button.Attributes["data-state"] = isSelected ? "checked" : "unchecked";
            button.Children.Add(new Span
            {
                Class = "pointer-events-none absolute left-2 flex size-3.5 items-center justify-center",
                Children = isSelected
                    ? [radio
                        ? SelectParts.Glyph(LucideIcons.Circle, slot + "-indicator", "size-2 fill-current")
                        : SelectParts.Glyph(LucideIcons.Check, slot + "-indicator", "size-4")]
                    : [],
            });
        }
        button.OnClick = EventComposition.Compose(button.OnClick, e =>
        {
            if (button.Disabled || !scope.IsOpen) return;
            select?.Invoke(e);
            if (!e.DefaultPrevented) scope.Close();
        });
        button.OnPointerMove = EventComposition.Compose(button.OnPointerMove, e =>
        {
            if (!e.DefaultPrevented && e.PointerType == PointerType.Mouse && scope.IsOpen && !scope.InPointerGrace(e)
                && e.CurrentTarget is { IsFocused: false, IsFocusable: true } element)
                element.Focus(new FocusOptions { Origin = FocusOrigin.Pointer, PreventScroll = true });
        });
        return button;
    }
}
