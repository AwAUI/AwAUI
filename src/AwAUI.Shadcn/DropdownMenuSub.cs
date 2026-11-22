using System;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.UI;
using PopoverPrimitive = AwAUI.Controls.Primitives.Popover;

namespace AwAUI.Shadcn;

/// <summary>Manages the open state of a submenu within a dropdown menu.</summary>
public sealed partial class DropdownMenuSub : Component
{
    readonly DropdownMenuSubmenu _submenu = new();

    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public Action<bool>? OpenChanged { get; set; }
    public Action<bool>? OnOpenChange { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => Provide(_submenu, new PopoverPrimitive.Root
    {
        Open = Open,
        DefaultOpen = DefaultOpen,
        OnOpenChange = OpenChanged + OnOpenChange,
        Children = Children,
    });
}

/// <summary>A menu item that opens an adjacent submenu.</summary>
public sealed partial class DropdownMenuSubTrigger : Component
{
    IDisposable? _hoverTimer;
    int _hoverVersion;
    bool _hoverStarted;
    PopperScope? _popper;

    public bool Disabled { get; set; }
    public bool Inset { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var parent = Use<DropdownMenuParts.Scope>();
        var submenu = Use<DropdownMenuSubmenu>();
        var popup = Use<PopoverPrimitive.Context>();
        var popper = _popper = Use<PopperScope>();
        popper.DefaultAnchor = this;
        bool rtl = parent.Direction == Direction.Rtl;
        var button = DropdownMenuParts.ItemButton("dropdown-menu-sub-trigger", Disabled, Inset,
            Class, Rest, Children, openHighlight: true);
        button.Id = submenu.TriggerId;
        button.Ref = submenu.Trigger;
        button.Attributes["aria-haspopup"] = "menu";
        button.Attributes["aria-controls"] = popup.Shared.ContentId;
        button.Attributes["aria-expanded"] = TabsParts.Bool(popup.Open);
        button.Attributes["aria-disabled"] = button.Disabled ? "true" : null;
        button.Attributes["data-state"] = TabsParts.OpenState(popup.Open);
        button.Children.Add(SelectParts.Glyph(rtl ? LucideIcons.ChevronLeft : LucideIcons.ChevronRight, "dropdown-menu-sub-trigger-icon",
            "pointer-events-none ml-auto size-4 shrink-0"));
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
                DropdownMenuParts.Scope.FocusFirst(submenu.Content.Current, origin);
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

/// <summary>The submenu panel, positioned by its trigger. It can also be wrapped in <see cref="DropdownMenuPortal"/>.</summary>
public sealed partial class DropdownMenuSubContent : Component
{
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
        var popup = Use<PopoverPrimitive.Context>();
        bool open = popup.Open && Use<DropdownMenuParts.MotionState>().Open;
        var parent = Use<DropdownMenuParts.Scope>();
        TryUse<DropdownMenuParts.InPortal>(out var portal);
        return DropdownMenuParts.RenderSurface(portal, parent, new DropdownMenuSurface
        {
            Popup = popup.Shared, Open = open, ParentMenu = parent, Submenu = Use<DropdownMenuSubmenu>(),
            // 行内末尾：LTR 在右、RTL 在左；由 Popper 按内容继承的文档方向解析
            Side = Side.InlineEnd,
            Align = Align, AlignOffset = AlignOffset, SideOffset = SideOffset,
            CollisionPadding = CollisionPadding, AvoidCollisions = AvoidCollisions,
            Class = Class, Rest = Rest, Children = Children,
        });
    }
}

internal sealed class DropdownMenuSubmenu
{
    internal readonly string TriggerId = TabsParts.NextId("dropdown-menu-sub-trigger");
    internal readonly Ref<Element> Trigger = new();
    internal readonly Ref<Element> Content = new();
}
