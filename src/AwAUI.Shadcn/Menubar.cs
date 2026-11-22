using System;
using System.Collections.Generic;
using AwAUI.Controls.Primitives;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.UI;
using PopoverPrimitive = AwAUI.Controls.Primitives.Popover;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>A horizontal menu bar. Only one top-level menu opens at a time.</summary>
public sealed partial class Menubar : Component
{
    readonly Scope _scope = new();
    readonly Ref<Element> _bar = new();

    public bool Loop { get; set; } = true;
    public Direction? Dir { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var children = Children ?? new NodeList();
        int index = 0;
        foreach (var node in children)
            if (node is MenubarMenu menu) menu.Index = index++;

        var bar = new Div
        {
            Role = Role.MenuBar,
            TabIndex = -1,
            Ref = Ref ?? _bar,
            Children = new NodeList(children),
        };
        TabsParts.Style(bar, "menubar", "flex h-9 items-center gap-1 rounded-md border bg-background p-1 shadow-xs", Class, Rest);
        bar.Attributes["data-orientation"] = "horizontal";
        bar.Attributes["aria-orientation"] = "horizontal";
        TabsParts.DirectionAttribute(bar, Dir);
        bar.OnKeyDown = e => OnBarKeyDown(e);
        return Provide(_scope, bar);
    }

    void OnBarKeyDown(KeyEvent e)
    {
        if (e.DefaultPrevented || Root?.FocusManager.FocusedElement is not { } focused) return;
        var trigger = _scope.Find(focused);
        if (trigger is null) return;

        if (e.Modifiers == KeyModifiers.None)
        {
            var direction = TabsParts.ResolveDirection(_bar.Current, null);
            var intent = RovingFocus.GetFocusIntent(e.Key, Orientation.Horizontal, direction);
            if (intent != FocusIntent.None)
            {
                e.PreventDefault();
                var ordered = _scope.Ordered();
                int current = ordered.IndexOf(trigger);
                int next = RovingFocus.Navigate(intent, current, ordered.Count, i => ordered[i].CanFocus, loop: Loop);
                if (next < 0 || next == current) return;
                bool wasOpen = _scope.OpenMenu is not null;
                var target = ordered[next];
                _scope.ActiveIndex.Value = target.Index;
                if (!target.FocusTrigger(FocusOrigin.Keyboard)) return;
                if (wasOpen) target.OpenAtStart();
                return;
            }
        }

        if (e.Modifiers == KeyModifiers.None && TypeAhead.IsTypeAheadKey(e.Key, e.Modifiers))
        {
            var ordered = _scope.Ordered();
            int current = ordered.IndexOf(trigger);
            var labels = new string?[ordered.Count];
            for (int i = 0; i < ordered.Count; i++) labels[i] = ordered[i].TextValue;
            int next = _scope.Search.HandleKey(e.Key, e.Timestamp, labels, current, i => ordered[i].CanFocus);
            if (next >= 0)
            {
                e.PreventDefault();
                _scope.ActiveIndex.Value = ordered[next].Index;
                ordered[next].FocusTrigger(FocusOrigin.Keyboard);
            }
        }
    }

    internal sealed class Scope
    {
        readonly List<MenubarTrigger> _triggers = new();

        internal readonly State<int> ActiveIndex = new(-1);
        internal readonly TypeAhead Search = new();
        internal MenubarMenu? OpenMenu;

        internal void Register(MenubarTrigger trigger)
        {
            if (!_triggers.Contains(trigger)) _triggers.Add(trigger);
        }

        internal void Unregister(MenubarTrigger trigger) => _triggers.Remove(trigger);

        internal List<MenubarTrigger> Ordered()
        {
            var ordered = new List<MenubarTrigger>(_triggers);
            ordered.Sort(static (a, b) => a.Index.CompareTo(b.Index));
            return ordered;
        }

        internal MenubarTrigger? Find(Element focused)
        {
            foreach (var trigger in _triggers)
                if (trigger.OwnsTrigger(focused)) return trigger;
            return null;
        }

        internal void SetOpen(MenubarMenu menu, bool open)
        {
            if (open)
            {
                var previous = OpenMenu;
                OpenMenu = menu;
                if (previous is not null && !ReferenceEquals(previous, menu)) previous.CloseMenu();
            }
            else if (ReferenceEquals(OpenMenu, menu))
            {
                OpenMenu = null;
            }
        }

        internal void MoveFromContent(int menuIndex, bool forward)
        {
            var ordered = Ordered();
            int current = ordered.FindIndex(t => t.Index == menuIndex);
            if (current < 0 || ordered.Count == 0) return;
            int next = (current + (forward ? 1 : -1) + ordered.Count) % ordered.Count;
            var target = ordered[next];
            ActiveIndex.Value = target.Index;
            target.OpenAtStart();
        }
    }
}

/// <summary>Wraps one top-level menubar item: a trigger and its content panel.</summary>
public sealed partial class MenubarMenu : Component
{
    internal int Index;
    internal readonly MenubarMenuState State = new();

    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public bool Modal { get; set; } = true;
    public Action<bool>? OpenChanged { get; set; }
    public Action<bool>? OnOpenChange { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        var menubar = Use<Menubar.Scope>();
        State.Owner = this;
        return Provide(new MenuScope(State, Index), new PopoverPrimitive.Root
        {
            Open = Open,
            DefaultOpen = DefaultOpen,
            Modal = Modal,
            OnOpenChange = OpenChanged + OnOpenChange + (open => menubar.SetOpen(this, open)),
            Children = Children,
        });
    }

    internal void CloseMenu() => State.Popup?.SetOpen(false);

    internal sealed class MenuScope(MenubarMenuState state, int index)
    {
        internal MenubarMenuState State { get; } = state;
        internal int Index { get; } = index;
    }
}

/// <summary>Explicit portal wrapper kept for API parity; menubar content already portals itself.</summary>
public sealed partial class MenubarPortal : Component
{
    public NodeList? Children { get; set; }

    protected override Node Build() => new Fragment(Children ?? []);
}

internal sealed class MenubarMenuState
{
    internal readonly string TriggerId = TabsParts.NextId("menubar-trigger");
    internal Ref<Element> Trigger = new();
    internal readonly Ref<Element> Content = new();
    internal OpenableShared? Popup;
    internal MenubarMenu? Owner;
    internal bool OpenAtEnd;
}

public sealed partial class MenubarTrigger : Component
{
    const string TriggerClass =
        "flex cursor-default items-center rounded-sm px-2 py-1 text-sm font-medium outline-hidden select-none " +
        "focus:bg-accent focus:text-accent-foreground data-[state=open]:bg-accent data-[state=open]:text-accent-foreground";

    readonly Ref<Element> _trigger = new();
    Menubar.Scope? _menubar;
    MenubarMenu.MenuScope? _menu;
    OpenableShared? _popup;

    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    internal int Index => _menu?.Index ?? 0;
    internal bool CanFocus => _trigger.Current is { IsFocusable: true, IsVisible: true };
    internal string? TextValue => _trigger.Current?.TextContent.Trim();
    internal Element? TriggerElement => _trigger.Current;
    internal bool OwnsTrigger(Element element) => _trigger.Current is { } trigger && trigger.IsInclusiveAncestorOf(element);

    protected override Node Build()
    {
        var menubar = _menubar = Use<Menubar.Scope>();
        var menu = _menu = Use<MenubarMenu.MenuScope>();
        var popup = Use<PopoverPrimitive.Context>();
        _popup = popup.Shared;
        menu.State.Popup ??= popup.Shared;
        var index = menu.Index;
        menu.State.Trigger = _trigger;

        var button = new UIButton { Role = Role.MenuItem, Ref = _trigger, Disabled = Disabled, Children = Children ?? new NodeList() };
        TabsParts.Style(button, "menubar-trigger", TriggerClass, Class, Rest);
        button.Id = menu.State.TriggerId;
        button.Attributes["aria-haspopup"] = "menu";
        button.Attributes["aria-expanded"] = TabsParts.Bool(popup.Open);
        button.Attributes["aria-controls"] = popup.Shared.ContentId;
        button.Attributes["data-state"] = TabsParts.OpenState(popup.Open);
        button.TabIndex = menubar.ActiveIndex.Value == index || (menubar.ActiveIndex.Value < 0 && index == 0) ? 0 : -1;
        button.OnClick = e =>
        {
            if (e.DefaultPrevented || button.Disabled) return;
            e.PreventDefault();
            if (popup.Open) popup.Shared.SetOpen(false);
            else OpenCore(FocusIntent.First, e.IsSynthetic ? FocusOrigin.Keyboard : FocusOrigin.Pointer);
        };
        button.OnKeyDown = e =>
        {
            if (e.DefaultPrevented || button.Disabled || e.Modifiers != KeyModifiers.None
                || !ReferenceEquals(e.Target, _trigger.Current)) return;
            if (e.Key == "ArrowDown")
            {
                e.PreventDefault();
                if (popup.Open) S2MenuParts.FocusItem(menu.State.Content.Current, FocusOrigin.Keyboard, last: false);
                else OpenCore(FocusIntent.First, FocusOrigin.Keyboard);
            }
            else if (e.Key == "ArrowUp")
            {
                e.PreventDefault();
                if (popup.Open) S2MenuParts.FocusItem(menu.State.Content.Current, FocusOrigin.Keyboard, last: true);
                else OpenCore(FocusIntent.Last, FocusOrigin.Keyboard);
            }
            else if (e.Key == "Escape" && popup.Open)
            {
                e.PreventDefault();
                popup.Shared.SetOpen(false);
            }
        };
        button.OnPointerMove = e =>
        {
            if (e.DefaultPrevented || button.Disabled || e.PointerType != PointerType.Mouse || e.Buttons != PointerButtons.None) return;
            if (menubar.OpenMenu is null || ReferenceEquals(menubar.OpenMenu, menu.State.Owner) || popup.Shared.IsOpen) return;
            if (!FocusTrigger(FocusOrigin.Pointer)) return;
            menu.State.OpenAtEnd = false;
            popup.Shared.SetOpen(true);
        };
        button.OnFocus = _ => menubar.ActiveIndex.Value = index;

        UseEffect(() =>
        {
            menubar.Register(this);
            return () => menubar.Unregister(this);
        }, menubar, index);
        return new PopoverPrimitive.Anchor { AsChild = true, Children = [button] };
    }

    internal bool FocusTrigger(FocusOrigin origin) =>
        _trigger.Current is { IsFocusable: true, IsVisible: true } trigger
        && trigger.Focus(new FocusOptions { Origin = origin, PreventScroll = true });

    internal void OpenAtStart() => OpenCore(FocusIntent.First, FocusOrigin.Keyboard);

    bool OpenCore(FocusIntent intent, FocusOrigin origin)
    {
        var menu = _menu;
        var popup = _popup;
        if (menu is null || popup is null) return false;
        menu.State.OpenAtEnd = intent == FocusIntent.Last;
        if (!FocusTrigger(origin)) return false;
        popup.SetOpen(true);
        return true;
    }

    protected override void OnUnmount()
    {
        _menubar?.Unregister(this);
        _menubar = null;
        _menu = null;
        _popup = null;
    }
}

public sealed partial class MenubarContent : Component
{
    const string ContentClass =
        "z-50 min-w-[12rem] overflow-hidden rounded-md border bg-popover p-1 text-popover-foreground shadow-md " +
        PopupMotion.Classes;

    public Side Side { get; set; } = Side.Bottom;
    public PopperAlign Align { get; set; } = PopperAlign.Start;
    public float SideOffset { get; set; } = 8;
    public float AlignOffset { get; set; } = -4;
    public float CollisionPadding { get; set; } = 8;
    public bool AvoidCollisions { get; set; } = true;
    public int ZOrder { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var popup = Use<PopoverPrimitive.Context>();
        var menubar = Use<Menubar.Scope>();
        var menu = Use<MenubarMenu.MenuScope>();
        return new S2MenuSurface
        {
            Slot = "menubar-content",
            ContentClass = ContentClass,
            Open = popup.Open,
            Popup = popup.Shared,
            Modal = popup.Modal,
            ZOrder = ZOrder,
            Side = Side,
            Align = Align,
            AlignOffset = AlignOffset,
            SideOffset = SideOffset,
            CollisionPadding = CollisionPadding,
            AvoidCollisions = AvoidCollisions,
            OpenAtEnd = menu.State.OpenAtEnd,
            LabelId = menu.State.TriggerId,
            ContentRef = menu.State.Content,
            FocusTarget = () => menu.State.Trigger.Current,
            OnMenuKeyDown = e => OnContentKey(e, menubar, menu),
            Class = Class,
            Rest = Rest,
            Children = Children,
        };
    }

    static void OnContentKey(KeyEvent e, Menubar.Scope menubar, MenubarMenu.MenuScope menu)
    {
        if (e.DefaultPrevented || e.Modifiers != KeyModifiers.None) return;
        if (e.Key is not ("ArrowLeft" or "ArrowRight")) return;
        var direction = TabsParts.ResolveDirection(menu.State.Content.Current, null);
        bool forward = (e.Key == "ArrowRight") != (direction == Direction.Rtl);
        e.PreventDefault();
        menubar.MoveFromContent(menu.Index, forward);
    }
}

public sealed partial class MenubarItem : Component
{
    const string ItemClass =
        "flex cursor-default items-center rounded-sm px-2 py-1.5 text-sm outline-hidden select-none " +
        "data-[inset]:pl-8 data-[variant=destructive]:text-destructive data-[variant=destructive]:focus:bg-destructive/10 " +
        "data-[variant=destructive]:focus:text-destructive dark:data-[variant=destructive]:focus:bg-destructive/20 " +
        "[&_svg:not([class*='text-'])]:text-muted-foreground data-[variant=destructive]:*:[svg]:text-destructive!";

    public bool Disabled { get; set; }
    public bool Inset { get; set; }
    public bool Destructive { get; set; }
    public Action<PointerEvent>? OnSelect { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => S2MenuParts.Item(Use<DropdownMenuParts.Scope>(), "menubar-item",
        Disabled, Inset, Destructive, ItemClass, Rest, Children, OnSelect);
}

public sealed partial class MenubarCheckboxItem : Component
{
    const string ItemClass =
        "flex cursor-default items-center rounded-xs py-1.5 pr-2 pl-8 text-sm outline-hidden select-none " +
        "data-[disabled]:pointer-events-none data-[disabled]:opacity-50";

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
        return S2MenuParts.Item(Use<DropdownMenuParts.Scope>(), "menubar-checkbox-item", Disabled, false, false,
            ItemClass, Rest, Children, EventComposition.Compose(OnSelect, _ => _checked.Set(!_checked.Value)),
            "menuitemcheckbox", selected);
    }
}

public sealed partial class MenubarRadioGroup : Component
{
    readonly ControllableState<string> _value = new(StringComparer.Ordinal);

    public string? Value { get; set; }
    public string DefaultValue { get; set; } = "";
    public Action<string>? ValueChanged { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var group = S2MenuParts.Group("menubar-radio-group", Children, Class, Rest);
        return Provide(new Selection(_value.Sync(Value, DefaultValue, ValueChanged), _value.Set), group);
    }

    internal sealed record Selection(string Value, Action<string> Set);
}

public sealed partial class MenubarRadioItem : Component
{
    const string ItemClass =
        "flex cursor-default items-center rounded-xs py-1.5 pr-2 pl-8 text-sm outline-hidden select-none " +
        "data-[disabled]:pointer-events-none data-[disabled]:opacity-50";

    public string Value { get; set; } = "";
    public bool Disabled { get; set; }
    public Action<PointerEvent>? OnSelect { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var radio = Use<MenubarRadioGroup.Selection>();
        return S2MenuParts.Item(Use<DropdownMenuParts.Scope>(), "menubar-radio-item", Disabled, false, false,
            ItemClass, Rest, Children, EventComposition.Compose(OnSelect, _ => radio.Set(Value)),
            "menuitemradio", Value == radio.Value, radio: true);
    }
}

public sealed partial class MenubarGroup : Component
{
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => S2MenuParts.Group("menubar-group", Children, Class, Rest);
}

public sealed partial class MenubarLabel : Component
{
    const string LabelClass = "px-2 py-1.5 text-sm font-medium data-[inset]:pl-8";

    public bool Inset { get; set; }
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => S2MenuParts.Label("menubar-label", Inset, Children, Class, Rest, LabelClass);
}

public sealed partial class MenubarSeparator : Component
{
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => S2MenuParts.Separator("menubar-separator", Class, Rest);
}

public sealed partial class MenubarShortcut : Component
{
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => S2MenuParts.Shortcut("menubar-shortcut", Children, Class, Rest);
}

public sealed partial class MenubarSub : Component
{
    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public Action<bool>? OpenChanged { get; set; }
    public Action<bool>? OnOpenChange { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => Provide(new DropdownMenuSubmenu(), new PopoverPrimitive.Root
    {
        Open = Open,
        DefaultOpen = DefaultOpen,
        OnOpenChange = OpenChanged + OnOpenChange,
        Children = Children,
    });
}

public sealed partial class MenubarSubTrigger : Component
{
    const string TriggerClass =
        "flex cursor-default items-center rounded-sm px-2 py-1.5 text-sm outline-none select-none " +
        "focus:bg-accent focus:text-accent-foreground data-[inset]:pl-8 data-[state=open]:bg-accent " +
        "data-[state=open]:text-accent-foreground";

    public bool Disabled { get; set; }
    public bool Inset { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new S2SubmenuTrigger
    {
        Slot = "menubar-sub-trigger",
        Classes = TriggerClass,
        Disabled = Disabled,
        Inset = Inset,
        Class = Class,
        Rest = Rest,
        Children = Children,
    };
}

public sealed partial class MenubarSubContent : Component
{
    const string ContentClass =
        "z-50 min-w-[8rem] overflow-hidden rounded-md border bg-popover p-1 text-popover-foreground shadow-lg " +
        PopupMotion.Classes;

    public PopperAlign Align { get; set; } = PopperAlign.Start;
    public float SideOffset { get; set; } = 2;
    public float AlignOffset { get; set; }
    public float CollisionPadding { get; set; } = 8;
    public bool AvoidCollisions { get; set; } = true;
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new S2SubmenuContent
    {
        Slot = "menubar-sub-content",
        ContentClass = ContentClass,
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
