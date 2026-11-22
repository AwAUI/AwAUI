using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Shadcn;

// Presence retains the panel during its exit. Only the open panel participates in the layer stack.
internal sealed partial class DropdownMenuSurface : Component
{
    const string ContentClass = "z-50 min-w-32 max-h-[var(--aw-popper-available-height,20rem)] overflow-x-hidden overflow-y-auto rounded-md border bg-popover p-1 text-popover-foreground shadow-md outline-none " + PopupMotion.Classes;

    readonly Ref<Element> _content = new();
    DropdownMenuParts.Scope? _scope;
    bool _wasOpen;

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
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var submenu = Submenu;
        var reference = submenu?.Content ?? _content;
        var scope = _scope ??= new DropdownMenuParts.Scope(Popup, ParentMenu, reference, ZOrder);
        bool openAtEnd = TryUse<DropdownMenuParts.OpenIntent>(out var openIntent) && openIntent.Last;
        var attrs = DialogAttributes.ForPart(submenu is null ? "dropdown-menu-content" : "dropdown-menu-sub-content", Rest, Class, ContentClass);
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
        var events = new Div();
        events.MergeAttributes(attrs);
        attrs["onkeydown"] = EventComposition.Compose(events.OnKeyDown, e => scope.KeyDown(e, submenu is null ? null : CloseToTrigger));
        if (submenu is not null)
        {
            attrs["onpointerenter"] = EventComposition.Chain(events.OnPointerEnter, e =>
            {
                if (e.PointerType == PointerType.Mouse) scope.Parent?.ClearGrace(scope.Popup);
            });
        }
        var content = new PopperContent
        {
            Id = Popup.ContentId, Role = Role.Menu, TabIndex = -1, Ref = reference,
            Side = Side, Align = Align, SideOffset = SideOffset, AlignOffset = AlignOffset,
            CollisionPadding = CollisionPadding, AvoidCollisions = AvoidCollisions,
            Attributes = attrs,
            Children = [Provide(scope, Provide(new DropdownMenuParts.MotionState(Open), new Fragment(Children ?? [])))],
        };
        var layer = new DismissableLayer
        {
            Active = Open,
            DisableOutsidePointerEvents = submenu is null && Modal,
            OnInteractOutside = e =>
            {
                // The trigger handles its own press, and focus can return to it while this layer is still mounted.
                if (!scope.IsOpen || (submenu?.Trigger.Current ?? Popup.TriggerElement)?.IsInclusiveAncestorOf(e.Target) == true)
                    e.PreventDefault();
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
        if (submenu is not null) return layer;

        // Only the root owns autofocus. Opening a submenu on hover leaves focus on its trigger.
        var root = Root!;
        return new FocusScope
        {
            Active = Open,
            OnMountAutoFocus = e =>
            {
                e.PreventDefault();
                if (!scope.IsOpen) return;
                bool last = openAtEnd;
                if (TryUse<DropdownMenuParts.OpenIntent>(out var intent)) intent.Last = false;
                scope.FocusFirst(root.FocusManager.LastInteractionWasKeyboard ? FocusOrigin.Keyboard : FocusOrigin.Programmatic, last);
            },
            OnUnmountAutoFocus = e =>
            {
                e.PreventDefault();
                var focused = root.FocusManager.FocusedElement;
                if (!scope.SkipRestore && (focused is null || OwnsFocus(focused))) scope.Popup.TriggerElement?.Focus();
            },
            Children = [layer],
        };
    }

    void CloseToTrigger()
    {
        if (_scope is not { IsOpen: true } scope) return;
        // Focus before unmount so a keyboard step back is usable without waiting for another layout.
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
