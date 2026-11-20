using System;
using System.Collections.Generic;
using AwAUI.Controls.Primitives;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.UI;
using PopoverPrimitive = AwAUI.Controls.Primitives.Popover;

namespace AwAUI.Shadcn;

/// <summary>The popup listbox. Its children can include options, groups, labels, and separators.</summary>
public sealed partial class SelectContent : Component
{
    const string ContentClass =
        "relative z-50 flex min-w-[max(8rem,var(--aw-popper-anchor-width,0px))] max-w-[var(--aw-popper-available-width,100vw)] " +
        "max-h-[var(--aw-popper-available-height,20rem)] flex-col overflow-hidden rounded-md border bg-popover text-popover-foreground shadow-md outline-hidden " + PopupMotion.Classes;
    const string ViewportClass = "min-h-0 overflow-x-hidden overflow-y-auto overscroll-contain p-1";

    readonly Ref<Element> _viewport = new();
    readonly Ref<Element> _content = new();
    readonly SelectScrollContext _scroll = new();
    SelectContext _context;
    OpenableShared? _popup;
    bool _hasCustomLabeling;
    bool _interactedOutside;
    Element? _outsidePress;

    public Side Side { get; set; } = Side.Bottom;
    public float SideOffset { get; set; } = 4;
    public PopperAlign Align { get; set; } = PopperAlign.Start;
    public float AlignOffset { get; set; }
    public bool AvoidCollisions { get; set; } = true;
    public float CollisionPadding { get; set; } = 8;
    public bool ShowScrollButtons { get; set; } = true;
    public SelectScrollUpButton? ScrollUpButton { get; set; }
    public SelectScrollDownButton? ScrollDownButton { get; set; }
    public Action<FocusScopeAutoFocusEvent>? OnOpenAutoFocus { get; set; }
    public Action<FocusScopeAutoFocusEvent>? OnCloseAutoFocus { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnEscapeKeyDown { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnPointerDownOutside { get; set; }
    public Action<DismissableLayerEvent<Element>>? OnFocusOutside { get; set; }
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        _context = Use<SelectContext>();
        var popover = Use<PopoverPrimitive.Context>();
        var popup = popover.Shared;
        _popup = popup;
        var viewport = new Div { Role = Role.Presentation, Ref = _viewport, Children = Children ?? [] };
        viewport.Attributes["data-slot"] = "select-viewport";

        // The inactive inline copy keeps labels discoverable without keeping an overlay mounted.
        // PresenceGate mounts only one branch, so composed item instances are never attached twice.
        var hidden = new Div
        {
            Id = popup.ContentId,
            Style = "display: none",
            Children = [new Div { Ref = _viewport, Children = Children ?? [] }],
        };
        hidden.Attributes["aria-hidden"] = "true";

        viewport.Class = ShowScrollButtons ? ViewportClass + " [scrollbar-width:none]" : ViewportClass;
        viewport.ScrollController = _scroll.Controller;
        var parts = ShowScrollButtons
            ? new NodeList { ScrollUpButton ?? new SelectScrollUpButton(), viewport, ScrollDownButton ?? new SelectScrollDownButton() }
            : new NodeList { viewport };
        var attrs = DialogAttributes.ForPart("select-content", Rest, Class, ContentClass);
        attrs["data-state"] = TabsParts.OpenState(popover.Open);
        if (!popover.Open) attrs["aria-hidden"] = "true";
        attrs["aria-orientation"] = "vertical";
        _hasCustomLabeling = attrs["aria-label"] is not null || attrs["aria-labelledby"] is not null;
        if (!_hasCustomLabeling) attrs["aria-labelledby"] = _context.Scope.TriggerId;
        var onKey = new Div();
        onKey.MergeAttributes(attrs);
        attrs["onkeydown"] = EventComposition.Compose(onKey.OnKeyDown, HandleKey);

        // Popover.Content has a dialog role. Use its constituent primitives so the popup itself is a listbox.
        return new PresenceGate
        {
            Present = popover.Open,
            Fallback = hidden,
            Children = [new Portal { Children = [new FocusScope
            {
                Active = popover.Open,
                Trapped = false,
                OnMountAutoFocus = OpenAutoFocus,
                OnUnmountAutoFocus = e => CloseAutoFocus(e, popup),
                Children = [new DismissableLayer
                {
                    Active = popover.Open,
                    OnEscapeKeyDown = OnEscapeKeyDown,
                    OnPointerDownOutside = e => PointerDownOutside(e, popup),
                    OnFocusOutside = FocusOutside,
                    OnDismiss = _ => { if (popup.IsOpen) popup.SetOpen(false); },
                    Children = [new PopperContent
                    {
                        Id = popup.ContentId,
                        Ref = _content,
                        Role = Role.ListBox,
                        TabIndex = -1,
                        Side = Side,
                        SideOffset = SideOffset,
                        Align = Align,
                        AlignOffset = AlignOffset,
                        AvoidCollisions = AvoidCollisions,
                        CollisionPadding = CollisionPadding,
                        Attributes = attrs,
                        Children = [Provide(_scroll, new Fragment(parts))],
                    }],
                }],
            }] }],
        };
    }

    protected override void OnMount()
    {
        _context.Scope.Content = this;
        Root!.LayoutCompleted += UpdateFromLayout;
    }

    protected override void OnUnmount()
    {
        Root!.LayoutCompleted -= UpdateFromLayout;
        if (!ReferenceEquals(_context.Scope.Content, this)) return;
        var scope = _context.Scope;
        scope.Content = null;
        Root!.RequestAfterLayout(() =>
        {
            if (scope.Content is null) scope.SelectedLabel.Value = (null, null);
        });
    }

    void UpdateFromLayout(UIRoot root)
    {
        var selected = _context.Value;
        if (_content.Current is { } content)
            content.SetAttributeOverride("aria-labelledby", _hasCustomLabeling ? null : _popup?.TriggerElement?.Id ?? _context.Scope.TriggerId);
        string? text = null;
        if (selected.Length > 0 && _viewport.Current is { } viewport)
        {
            foreach (var (item, element) in Options(viewport, _context.Scope))
            {
                if (item.Value != selected) continue;
                text = item.LabelOf(element);
                break;
            }
        }
        _context.Scope.SelectedLabel.Value = (selected, text);

        bool open = _popup?.IsOpen == true;
        _outsidePress = null;
        if (open && root.FocusManager.FocusedElement is { IsDisabled: true } focused && _viewport.Current is { } list
            && list.IsInclusiveAncestorOf(focused))
            FocusInitial();
        _scroll.Refresh(open && ShowScrollButtons);
    }

    void OpenAutoFocus(FocusScopeAutoFocusEvent e)
    {
        _interactedOutside = false;
        OnOpenAutoFocus?.Invoke(e);
        if (e.DefaultPrevented || _popup?.IsOpen != true) return;
        e.PreventDefault();
        FocusInitial();
    }

    void CloseAutoFocus(FocusScopeAutoFocusEvent e, OpenableShared popup)
    {
        OnCloseAutoFocus?.Invoke(e);
        if (e.DefaultPrevented) return;
        e.PreventDefault();
        if (!IsMounted || !popup.IsOpen)
        {
            if (!_interactedOutside && popup.TriggerElement is { Root: { } root } trigger)
            {
                var current = root.FocusManager.FocusedElement;
                if (current is null || e.Container.IsInclusiveAncestorOf(current))
                    trigger.Focus(new FocusOptions { PreventScroll = true });
            }
            _interactedOutside = false;
        }
    }

    void FocusInitial()
    {
        bool openAtEnd = _context.Scope.OpenAtEnd;
        _context.Scope.OpenAtEnd = false;
        if (_viewport.Current is { } viewport)
        {
            Element? first = null;
            Element? last = null;
            foreach (var (item, element) in Options(viewport, _context.Scope))
            {
                if (!CanNavigate(element)) continue;
                first ??= element;
                last = element;
                if (item.Value == _context.Value && element.Focus()) return;
            }
            if ((openAtEnd ? last : first)?.Focus() == true) return;
        }
        _content.Current?.Focus(new FocusOptions { PreventScroll = true });
    }

    void PointerDownOutside(DismissableLayerEvent<Element> e, OpenableShared popup)
    {
        _outsidePress = e.Target;
        OnPointerDownOutside?.Invoke(e);
        if (e.DefaultPrevented) return;
        if (e.Target is { } target && popup.TriggerElement is { } trigger && trigger.IsInclusiveAncestorOf(target))
        {
            e.PreventDefault();
            return;
        }
        _interactedOutside = true;
    }

    void FocusOutside(DismissableLayerEvent<Element> e)
    {
        OnFocusOutside?.Invoke(e);
        if (e.DefaultPrevented) return;
        if (_outsidePress is { } pressed && e.Target is { } target && target.IsInclusiveAncestorOf(pressed)
            && e.OriginalEvent is FocusChangedEventArgs { Origin: FocusOrigin.Pointer })
        {
            e.PreventDefault();
            return;
        }
        _interactedOutside = true;
    }

    void HandleKey(KeyEvent e)
    {
        if (e.DefaultPrevented || _popup?.IsOpen != true) return;
        if (e.Key == "Tab" && (e.Modifiers & SelectParts.CommandModifiers) == 0 && MoveFocusFromTrigger(e)) return;
        if (e.Key == "Enter" || (e.Key == " " && !_context.Scope.Search.IsTypingAhead(e.Timestamp)))
        {
            // Options are native buttons. Let the platform synthesize the click.
            if (e.IsRepeat || (e.Modifiers & SelectParts.CommandModifiers) != 0) e.PreventDefault();
            return;
        }
        var intent = RovingFocus.GetFocusIntent(e.Key, Orientation.Vertical, Direction.Ltr);
        if (intent != FocusIntent.None && (e.Modifiers & (SelectParts.CommandModifiers | KeyModifiers.Shift)) == 0)
        {
            e.PreventDefault();
            var options = OpenOptions();
            int current = options.FindIndex(o => ReferenceEquals(o.Element, Root?.FocusManager.FocusedElement));
            int next = RovingFocus.Navigate(intent, current, options.Count, null, loop: false);
            if (next >= 0) FocusOption(options[next].Element);
            return;
        }
        if (!TypeAhead.IsTypeAheadKey(e.Key, e.Modifiers)) return;
        e.PreventDefault();
        FindTypedOption(e.Key, e.Timestamp, open: true);
    }

    bool MoveFocusFromTrigger(KeyEvent e)
    {
        if (Root is not { } root || _popup?.TriggerElement is not { } trigger) return false;
        var order = root.FocusManager.GetTabOrder();
        int step = (e.Modifiers & KeyModifiers.Shift) == 0 ? 1 : -1;
        for (int i = 0; i < order.Count; i++)
        {
            if (!ReferenceEquals(order[i], trigger)) continue;
            var next = order[(i + step + order.Count) % order.Count];
            if (!next.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard })) return false;
            e.PreventDefault();
            return true;
        }
        return false;
    }

    internal void FindTypedOption(string key, TimeSpan timestamp, bool open)
    {
        if (_viewport.Current is not { } viewport || _context.Disabled) return;
        var options = open ? OpenOptions() : new List<(SelectItem Item, Element Element)>(Options(viewport, _context.Scope));
        int current = -1;
        var labels = new string?[options.Count];
        var focused = Root?.FocusManager.FocusedElement;
        for (int i = 0; i < options.Count; i++)
        {
            var (item, element) = options[i];
            if ((open && ReferenceEquals(focused, element)) || (!open && item.Value == _context.Scope.Selection.Value))
                current = i;
            if (!element.IsDisabled) labels[i] = item.TextValue ?? item.LabelOf(element);
        }
        int next = _context.Scope.Search.HandleKey(key, timestamp, labels, current);
        if (next < 0) return;
        if (open) FocusOption(options[next].Element);
        else _context.Scope.Selection.Set(options[next].Item.Value);
    }

    List<(SelectItem Item, Element Element)> OpenOptions()
    {
        var options = new List<(SelectItem, Element)>();
        if (_viewport.Current is { } viewport)
            foreach (var option in Options(viewport, _context.Scope))
                if (CanNavigate(option.Element)) options.Add(option);
        return options;
    }

    static bool CanNavigate(Element element) => element.IsFocusable && element.IsVisible;

    static void FocusOption(Element element) => element.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });

    // Retained tree order also covers options rendered by a child component and keyed reordering.
    static IEnumerable<(SelectItem Item, Element Element)> Options(Element parent, SelectScope scope)
    {
        foreach (var element in parent.ElementChildren)
        {
            if (element is ButtonElement && element.Owner is SelectItem item)
            {
                if (ReferenceEquals(item.Scope, scope)) yield return (item, element);
                continue;
            }
            foreach (var option in Options(element, scope)) yield return option;
        }
    }
}

internal sealed class SelectScrollContext
{
    internal readonly ScrollController Controller = new();
    internal readonly State<(bool Up, bool Down)> Directions = new((false, false));

    internal void Refresh(bool enabled)
    {
        var offset = Controller.Offset.Y;
        Directions.Value = (enabled && offset > 0, enabled && offset < Controller.MaxOffset.Y);
    }

    internal void Move(bool up)
    {
        var offset = Controller.Offset;
        float step = Math.Max(24, Controller.ViewportSize.Height * 0.8f);
        Controller.JumpTo(new Point(offset.X, offset.Y + (up ? -step : step)));
    }
}
