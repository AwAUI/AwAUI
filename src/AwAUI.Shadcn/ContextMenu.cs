using System;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using PopoverPrimitive = AwAUI.Controls.Primitives.Popover;

namespace AwAUI.Shadcn;

/// <summary>A menu opened at the pointer on right-click or long press.</summary>
public sealed partial class ContextMenu : Component
{
    readonly ContextMenuState _state = new();

    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public bool Modal { get; set; } = true;
    public Action<bool>? OpenChanged { get; set; }
    public Action<bool>? OnOpenChange { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => Provide(_state, new PopoverPrimitive.Root
    {
        Open = Open,
        DefaultOpen = DefaultOpen,
        Modal = Modal,
        OnOpenChange = OpenChanged + OnOpenChange,
        Children = Children,
    });
}

internal sealed class ContextMenuState
{
    internal readonly string TriggerId = TabsParts.NextId("context-menu-trigger");
    internal Rect Anchor;
    internal Element? PreviousFocus;
    internal readonly Ref<Element> Trigger = new();
}

/// <summary>Explicit portal wrapper kept for API parity; context-menu content already portals itself.</summary>
public sealed partial class ContextMenuPortal : Component
{
    public NodeList? Children { get; set; }

    protected override Node Build() => new Fragment(Children ?? []);
}

/// <summary>The element that receives the right-click or long press. It is the positioning anchor.</summary>
public sealed partial class ContextMenuTrigger : Component
{
    IDisposable? _longPress;
    int _longPressVersion;
    Point _pressPosition;
    ContextMenuState? _state;
    OpenableShared? _popup;

    public bool AsChild { get; set; }
    /// <summary>Long press duration in milliseconds. Values below 1 are treated as 1.</summary>
    public int LongPressDelay { get; set; } = 700;
    /// <summary>How far a touch may move before a long press is cancelled, in logical pixels.</summary>
    public float LongPressTolerance { get; set; } = 8;
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var popup = Use<PopoverPrimitive.Context>();
        var state = _state = Use<ContextMenuState>();
        _popup = popup.Shared;
        var target = new Div
        {
            Ref = Ref ?? state.Trigger,
            TabIndex = AsChild ? null : -1,
            Children = Children ?? new NodeList(),
            OnPointerDown = OnPointerDown,
            OnPointerUp = OnPointerUp,
            OnPointerMove = OnPointerMove,
            OnPointerCancel = _ => CancelLongPress(),
            OnKeyDown = OnKeyDown,
        };
        TabsParts.Style(target, "context-menu-trigger", "select-none", Class, Rest);
        target.Id ??= state.TriggerId;
        target.Attributes["aria-haspopup"] = "menu";
        target.Attributes["aria-expanded"] = TabsParts.Bool(popup.Open);
        if (popup.Open) target.Attributes["aria-controls"] = popup.Shared.ContentId;
        target.Attributes["data-state"] = TabsParts.OpenState(popup.Open);
        Node child = AsChild ? Slot.Merge(target, Slot.SingleChild(Children)) : target;
        return new PopperAnchor { AsChild = true, VirtualRect = () => state.Anchor, Children = [child] };
    }

    void OnPointerDown(PointerEvent e)
    {
        if (e.DefaultPrevented || e.CurrentTarget is not { IsDisabled: false } target) return;
        bool rightClick = e.Button == PointerButtons.Right
            || (e.Button == PointerButtons.Left && (e.Modifiers & KeyModifiers.Control) != 0);
        if (rightClick)
        {
            e.PreventDefault();
            OpenAt(e.Position);
            return;
        }
        if (e.PointerType == PointerType.Touch && e.Button == PointerButtons.Left)
        {
            _pressPosition = e.Position;
            if (Root?.Clock is { } clock)
            {
                int version = ++_longPressVersion;
                _longPress = clock.Schedule(TimeSpan.FromMilliseconds(Math.Max(1, LongPressDelay)), () =>
                {
                    if (version != _longPressVersion) return;
                    _longPress = null;
                    if (_state is not null && _popup?.IsOpen != true) OpenAt(_pressPosition);
                });
            }
            else
            {
                OpenAt(_pressPosition);
            }
        }
    }

    void OnPointerUp(PointerEvent e)
    {
        if (e.PointerType == PointerType.Touch) CancelLongPress();
    }

    void OnPointerMove(PointerEvent e)
    {
        if (e.PointerType != PointerType.Touch) return;
        float dx = e.Position.X - _pressPosition.X;
        float dy = e.Position.Y - _pressPosition.Y;
        if (dx * dx + dy * dy > LongPressTolerance * LongPressTolerance) CancelLongPress();
    }

    void OnKeyDown(KeyEvent e)
    {
        if (e.DefaultPrevented) return;
        if (e.Key != "ContextMenu" && !(e.Key == "F10" && (e.Modifiers & KeyModifiers.Shift) != 0)) return;
        if (_state is null) return;
        e.PreventDefault();
        if (e.CurrentTarget is not { } element) return;
        var rect = element.GetBoundingClientRect();
        OpenAt(new Point(rect.X, rect.Y + rect.Height));
    }

    void OpenAt(Point position)
    {
        var state = _state;
        var popup = _popup;
        if (state is null || popup is null) return;
        CancelLongPress();
        state.Anchor = new Rect(position.X, position.Y, 0, 0);
        state.PreviousFocus = Root?.FocusManager.FocusedElement;
        if (popup.IsOpen) Invalidate();
        else popup.SetOpen(true);
    }

    void CancelLongPress()
    {
        _longPressVersion++;
        _longPress?.Dispose();
        _longPress = null;
    }

    protected override void OnUnmount()
    {
        CancelLongPress();
        _state = null;
        _popup = null;
    }
}

/// <summary>
/// The menu panel opened at the trigger anchor. It closes on Escape and an outside press, restores focus to the
/// element that was focused when it opened, and uses menu semantics with roving item focus and typeahead.
/// </summary>
public sealed partial class ContextMenuContent : Component
{
    const string ContentClass =
        "z-50 max-h-[var(--aw-popper-available-height,20rem)] min-w-[8rem] overflow-x-hidden overflow-y-auto rounded-md " +
        "border bg-popover p-1 text-popover-foreground shadow-md outline-hidden " + PopupMotion.Classes;

    public Side Side { get; set; } = Side.Bottom;
    public PopperAlign Align { get; set; } = PopperAlign.Start;
    public float SideOffset { get; set; }
    public float AlignOffset { get; set; }
    public float CollisionPadding { get; set; } = 8;
    public bool AvoidCollisions { get; set; } = true;
    public int ZOrder { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var popup = Use<PopoverPrimitive.Context>();
        var state = Use<ContextMenuState>();
        return new S2MenuSurface
        {
            Slot = "context-menu-content",
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
            LabelId = state.TriggerId,
            FocusTarget = () => RestoreTarget(state),
            Class = Class,
            Rest = Rest,
            Children = Children,
        };
    }

    static Element? RestoreTarget(ContextMenuState state)
    {
        var trigger = state.Trigger.Current;
        var previous = state.PreviousFocus;
        if (previous is { IsAttached: true, IsVisible: true, IsFocusable: true }
            && (trigger is null || ReferenceEquals(previous, trigger) || trigger.IsInclusiveAncestorOf(previous)))
            return previous;
        return trigger;
    }
}

public sealed partial class ContextMenuItem : Component
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

    protected override Node Build() => S2MenuParts.Item(Use<DropdownMenuParts.Scope>(), "context-menu-item",
        Disabled, Inset, Destructive, ItemClass, Rest, Children, OnSelect);
}

public sealed partial class ContextMenuCheckboxItem : Component
{
    const string ItemClass =
        "flex cursor-default items-center rounded-sm py-1.5 pr-2 pl-8 text-sm outline-hidden select-none " +
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
        return S2MenuParts.Item(Use<DropdownMenuParts.Scope>(), "context-menu-checkbox-item", Disabled, false, false,
            ItemClass, Rest, Children, EventComposition.Compose(OnSelect, _ => _checked.Set(!_checked.Value)),
            "menuitemcheckbox", selected);
    }
}

public sealed partial class ContextMenuRadioGroup : Component
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
        var group = S2MenuParts.Group("context-menu-radio-group", Children, Class, Rest);
        return Provide(new Selection(_value.Sync(Value, DefaultValue, ValueChanged), _value.Set), group);
    }

    internal sealed record Selection(string Value, Action<string> Set);
}

public sealed partial class ContextMenuRadioItem : Component
{
    const string ItemClass =
        "flex cursor-default items-center rounded-sm py-1.5 pr-2 pl-8 text-sm outline-hidden select-none " +
        "data-[disabled]:pointer-events-none data-[disabled]:opacity-50";

    public string Value { get; set; } = "";
    public bool Disabled { get; set; }
    public Action<PointerEvent>? OnSelect { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var radio = Use<ContextMenuRadioGroup.Selection>();
        return S2MenuParts.Item(Use<DropdownMenuParts.Scope>(), "context-menu-radio-item", Disabled, false, false,
            ItemClass, Rest, Children, EventComposition.Compose(OnSelect, _ => radio.Set(Value)),
            "menuitemradio", Value == radio.Value, radio: true);
    }
}

public sealed partial class ContextMenuGroup : Component
{
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => S2MenuParts.Group("context-menu-group", Children, Class, Rest);
}

public sealed partial class ContextMenuLabel : Component
{
    const string LabelClass = "px-2 py-1.5 text-sm font-medium text-foreground data-[inset]:pl-8";

    public bool Inset { get; set; }
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() =>
        S2MenuParts.Label("context-menu-label", Inset, Children, Class, Rest, LabelClass);
}

public sealed partial class ContextMenuSeparator : Component
{
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => S2MenuParts.Separator("context-menu-separator", Class, Rest);
}

public sealed partial class ContextMenuShortcut : Component
{
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => S2MenuParts.Shortcut("context-menu-shortcut", Children, Class, Rest);
}

public sealed partial class ContextMenuSub : Component
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

public sealed partial class ContextMenuSubTrigger : Component
{
    const string TriggerClass =
        "flex cursor-default items-center rounded-sm px-2 py-1.5 text-sm outline-hidden select-none " +
        "focus:bg-accent focus:text-accent-foreground data-[inset]:pl-8 data-[state=open]:bg-accent " +
        "data-[state=open]:text-accent-foreground [&_svg:not([class*='text-'])]:text-muted-foreground";

    public bool Disabled { get; set; }
    public bool Inset { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new S2SubmenuTrigger
    {
        Slot = "context-menu-sub-trigger",
        Classes = TriggerClass,
        Disabled = Disabled,
        Inset = Inset,
        Class = Class,
        Rest = Rest,
        Children = Children,
    };
}

public sealed partial class ContextMenuSubContent : Component
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
        Slot = "context-menu-sub-content",
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
