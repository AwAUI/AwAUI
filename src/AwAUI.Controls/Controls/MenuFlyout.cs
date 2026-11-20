using System;
using System.Collections.Generic;
using System.Windows.Input;
using AwAUI.Controls.Primitives;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls;

/// <summary>A flat action menu with its own trigger button. Supply <see cref="MenuFlyoutItem"/> children.</summary>
public sealed partial class MenuFlyout : ContentControl
{
    protected override string GlobalStyleSheet => """
        @layer aw-menu-flyout-defaults {
          :where(.aw-menu-flyout) { display: inline-block; }
          :where(.aw-menu-flyout-content) {
            display: flex;
            flex-direction: column;
            box-sizing: border-box;
            width: max-content;
            min-width: min(128px, var(--aw-popper-available-width, calc(100vw - 16px)));
            max-width: var(--aw-popper-available-width, calc(100vw - 16px));
            max-height: var(--aw-popper-available-height, calc(100vh - 16px));
            overflow: auto;
            overscroll-behavior: contain;
            padding: 4px;
            border: 1px solid var(--aw-border, #c8ced6);
            border-radius: var(--aw-radius, 6px);
            background-color: var(--aw-surface, #ffffff);
            color: var(--aw-text, #1f2328);
            z-index: var(--aw-z-popup, 50);
          }
          :where(.aw-menu-flyout-content[data-state="closed"],
                 [data-aw-popper-content-wrapper]:has(> .aw-menu-flyout-content[data-state="closed"])) {
            pointer-events: none;
          }
          :where(.aw-menu-flyout-item) {
            display: flex;
            flex: 0 0 auto;
            align-items: center;
            box-sizing: border-box;
            min-height: 28px;
            padding: 4px 8px;
            border: 0;
            border-radius: var(--aw-radius, 6px);
            background-color: transparent;
            color: inherit;
          }
          :where(.aw-menu-flyout-item:enabled:hover, .aw-menu-flyout-item:enabled:focus) {
            background-color: var(--aw-control-hover, #edf1f5);
          }
          :where(.aw-menu-flyout-item:disabled) { opacity: 0.55; }
          :where(.aw-drop-down-button-trigger) { display: inline-flex; align-items: center; gap: 8px; }
          :where(.aw-drop-down-button-trigger > [part="indicator"]) {
            width: 6px;
            height: 6px;
            flex: none;
            margin-bottom: 3px;
            border-right: 1px solid currentColor;
            border-bottom: 1px solid currentColor;
            transform: rotate(45deg);
          }
        }
        """;

    readonly ControllableState<bool> _open = new();
    readonly Ref<Element> _trigger = new();
    readonly Ref<Element> _panel = new();
    readonly string _triggerId = PrimitiveIds.Next("menu-trigger");
    readonly string _menuId = PrimitiveIds.Next("menu-content");
    MenuSession? _session;
    bool _wasOpen;
    bool _openAtEnd;
    bool _movingTab;

    sealed class MenuSession
    {
        internal MenuSession(bool openAtEnd) => OpenAtEnd = openAtEnd;
        internal readonly bool OpenAtEnd;
        internal readonly TypeAhead Search = new();
        internal bool SkipRestore;
    }

    /// <summary>Text or noninteractive content for the trigger button.</summary>
    public object? Trigger { get; set; }
    public ClassValue TriggerClass { get; set; }
    public string? TriggerAriaLabel { get; set; }
    /// <summary>Defaults to the accessible name of the trigger button.</summary>
    public string? MenuAriaLabel { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Set to control visibility, or leave null to let the menu manage it.</summary>
    public bool? IsOpen { get; set; }
    public bool DefaultIsOpen { get; set; }
    public Action<bool>? IsOpenChanged { get; set; }
    public Placement Placement { get; set; } = Placement.BottomStart;
    public float SideOffset { get; set; } = 4;

    internal bool CanInvokeItem => IsMounted && IsEnabled && _open.Value;

    protected override Node BuildTemplate()
    {
        bool requestedOpen = _open.Sync(IsOpen, DefaultIsOpen, IsOpenChanged);
        bool open = requestedOpen && IsEnabled;
        if (open && !_wasOpen) _session = new MenuSession(_openAtEnd);
        _openAtEnd = false;
        _wasOpen = open;
        UseEffect(() =>
        {
            if (!IsEnabled && _open.Value) _open.Set(false);
            return null;
        }, IsEnabled, requestedOpen);

        var state = PartProps.OpenState(open);
        var trigger = PartProps.Apply(new UIButton
        {
            Class = "aw-button aw-menu-flyout-trigger",
            Id = _triggerId,
            Ref = _trigger,
            Disabled = !IsEnabled,
            AriaLabel = TriggerAriaLabel ?? AriaLabel,
            OnClick = OnTriggerClick,
            OnKeyDown = OnTriggerKeyDown,
            Children = [Present(Trigger ?? "Open menu", null)],
        }, TriggerClass, default, null);
        trigger.Attributes["part"] = "trigger";
        trigger.Attributes["aria-haspopup"] = "menu";
        trigger.Attributes["aria-expanded"] = PartProps.Bool(open);
        trigger.Attributes["aria-disabled"] = IsEnabled ? null : "true";
        trigger.Attributes["aria-controls"] = open ? _menuId : null;
        trigger.Attributes["data-state"] = state;

        var root = Apply(new Div(), "aw-menu-flyout");
        // AriaLabel is the fallback name of the interactive trigger rather than the layout wrapper.
        root.AriaLabel = null;
        root.Attributes["data-state"] = state;
        root.Children.Add(new Popper
        {
            Children = [
                new PopperAnchor { AsChild = true, Children = [trigger] },
                new PresenceGate
                {
                    Present = open,
                    Children = _session is { } session
                        ? [new Portal { Children = [BuildSurface(open, session)] }]
                        : [],
                },
            ],
        });
        return root;
    }

    Node BuildSurface(bool open, MenuSession session)
    {
        var content = new PopperContent
        {
            Id = _menuId,
            Role = Role.Menu,
            TabIndex = -1,
            Ref = _panel,
            Class = "aw-menu-flyout-content",
            Side = Placement.Side,
            Align = Placement.Align,
            SideOffset = SideOffset,
            CollisionPadding = 8,
            Attributes = new AttributeDictionary
            {
                ["part"] = "content",
                ["data-state"] = PartProps.OpenState(open),
                ["aria-hidden"] = open ? null : "true",
                ["aria-label"] = MenuAriaLabel,
                ["aria-labelledby"] = MenuAriaLabel is null ? _triggerId : null,
                ["onkeydown"] = (Action<KeyEvent>)(e => OnMenuKeyDown(e, session)),
            },
            Children = [Provide(this, new Fragment(BuildContent()))],
        };
        return new FocusScope
        {
            Active = open,
            OnMountAutoFocus = e =>
            {
                e.PreventDefault();
                if (CanInvokeItem && ReferenceEquals(_session, session))
                {
                    var origin = Root?.FocusManager.LastInteractionWasKeyboard == true
                        ? FocusOrigin.Keyboard : FocusOrigin.Programmatic;
                    FocusEdge(e.Container, session.OpenAtEnd, origin);
                }
            },
            OnUnmountAutoFocus = e =>
            {
                e.PreventDefault();
                if (!IsMounted || CanInvokeItem || !IsEnabled || session.SkipRestore
                    || !ReferenceEquals(_session, session) || Root is not { } root) return;
                var focused = root.FocusManager.FocusedElement;
                if (focused is null || e.Container.IsInclusiveAncestorOf(focused))
                    TryFocus(_trigger.Current, FocusOrigin.Programmatic, preventScroll: true);
            },
            Children = [new DismissableLayer
            {
                Active = open,
                OnInteractOutside = e =>
                {
                    // The trigger toggles on click, after the pointer-down and focus events.
                    if (!CanInvokeItem || _movingTab || _trigger.Current?.IsInclusiveAncestorOf(e.Target) == true
                        || e.OriginalEvent is FocusChangedEventArgs { Origin: FocusOrigin.Pointer })
                        e.PreventDefault();
                },
                OnDismiss = reason => Close(skipRestore: reason != DismissReason.EscapeKey),
                Children = [content],
            }],
        };
    }

    void OnTriggerClick(PointerEvent e)
    {
        if (e.DefaultPrevented || !IsEnabled || e.CurrentTarget is not { IsDisabled: false }) return;
        if (_open.Value) Close();
        else Open(atEnd: false);
    }

    void OnTriggerKeyDown(KeyEvent e)
    {
        if (e.DefaultPrevented || e.Modifiers != KeyModifiers.None || !IsEnabled
            || e.Key is not ("ArrowDown" or "ArrowUp")) return;
        e.PreventDefault();
        bool last = e.Key == "ArrowUp";
        if (!_open.Value) Open(last);
        else if (_panel.Current is { IsVisible: true } panel) FocusEdge(panel, last, FocusOrigin.Keyboard);
    }

    void Open(bool atEnd)
    {
        _openAtEnd = atEnd;
        _open.Set(true);
    }

    internal void Close(bool skipRestore = false)
    {
        if (!CanInvokeItem) return;
        if (_session is { } session) session.SkipRestore = skipRestore;
        _open.Set(false);
    }

    void OnMenuKeyDown(KeyEvent e, MenuSession session)
    {
        if (e.DefaultPrevented || !CanInvokeItem || !ReferenceEquals(session, _session)
            || _panel.Current is not { } panel) return;

        const KeyModifiers command = KeyModifiers.Alt | KeyModifiers.Control | KeyModifiers.Meta;
        if (e.Key == "Tab" && (e.Modifiers & command) == 0)
        {
            e.PreventDefault();
            MoveTabFromTrigger(forward: (e.Modifiers & KeyModifiers.Shift) == 0);
            Close(skipRestore: true);
            return;
        }

        var intent = RovingFocus.GetFocusIntent(e.Key, Orientation.Vertical, Direction.Ltr);
        bool navigate = intent != FocusIntent.None && e.Modifiers == KeyModifiers.None;
        var key = e.Key;
        bool search = key is not null && TypeAhead.IsTypeAheadKey(key, e.Modifiers)
            && (key != " " || session.Search.IsTypingAhead(e.Timestamp));
        if (!navigate && !search) return;

        var items = VisibleItems(panel);
        int current = Root?.FocusManager.FocusedElement is { } focused ? items.IndexOf(focused) : -1;
        int target;
        if (navigate)
        {
            e.PreventDefault();
            session.Search.Reset();
            target = RovingFocus.Navigate(intent, current, items.Count, i => items[i].IsFocusable, loop: true);
        }
        else
        {
            target = session.Search.HandleKey(key!, e.Timestamp, items.ConvertAll<string?>(item =>
                (item.GetAttribute("data-menu-text-value") ?? item.GetAttribute("aria-label") ?? item.TextContent).Trim()),
                current, i => items[i].IsFocusable);
            if (target >= 0 || key == " ") e.PreventDefault();
        }
        if (target >= 0) TryFocus(items[target], FocusOrigin.Keyboard);
    }

    void MoveTabFromTrigger(bool forward)
    {
        if (Root is not { } root || _trigger.Current is not { } trigger) return;
        var order = root.FocusManager.GetTabOrder();
        int start = -1;
        for (int i = 0; i < order.Count; i++)
            if (ReferenceEquals(order[i], trigger)) { start = i; break; }

        _movingTab = true;
        try
        {
            if (start >= 0)
            {
                for (int i = 1; i < order.Count; i++)
                {
                    var candidate = order[(start + (forward ? i : -i) + order.Count) % order.Count];
                    if (_panel.Current?.IsInclusiveAncestorOf(candidate) != true && !root.IsPointerBlocked(candidate)
                        && TryFocus(candidate, FocusOrigin.Keyboard)) return;
                }
            }
            TryFocus(trigger, FocusOrigin.Keyboard, preventScroll: true);
        }
        finally { _movingTab = false; }
    }

    static void FocusEdge(Element panel, bool last, FocusOrigin origin)
    {
        var items = VisibleItems(panel);
        for (int i = 0; i < items.Count; i++)
            if (TryFocus(items[last ? items.Count - 1 - i : i], origin)) return;
        TryFocus(panel, origin, preventScroll: true);
    }

    static bool TryFocus(Element? element, FocusOrigin origin, bool preventScroll = false) =>
        element is { IsVisible: true, IsFocusable: true }
        && element.Focus(new FocusOptions { Origin = origin, PreventScroll = preventScroll });

    static List<Element> VisibleItems(Element panel)
    {
        var items = new List<Element>();
        Visit(panel);
        return items;

        void Visit(Element parent)
        {
            foreach (var child in parent.ElementChildren)
            {
                if (child.Role == Role.Menu) continue;
                if (child.Role == Role.MenuItem)
                {
                    if (child.IsVisible) items.Add(child);
                    continue;
                }
                Visit(child);
            }
        }
    }
}

/// <summary>An action in a <see cref="MenuFlyout"/> or <see cref="DropDownButton"/>.</summary>
public sealed partial class MenuFlyoutItem : ContentControl
{
    public bool IsEnabled { get; set; } = true;
    /// <summary>Raise <see cref="ICommand.CanExecuteChanged"/> on the UI thread.</summary>
    public ICommand? Command { get; set; }
    public object? CommandParameter { get; set; }
    /// <summary>Cancel the event to keep the menu open and skip the command.</summary>
    public Action<PointerEvent>? OnClick { get; set; }
    /// <summary>Optional label for typeahead. Defaults to the accessible label or rendered text.</summary>
    public string? TextValue { get; set; }

    protected override Node BuildTemplate()
    {
        var command = Command;
        UseEffect(() =>
        {
            if (command is null) return null;
            EventHandler refresh = (_, _) => Invalidate();
            command.CanExecuteChanged += refresh;
            return () => command.CanExecuteChanged -= refresh;
        }, command);

        TryUse<MenuFlyout>(out var menu);
        var button = Apply(new UIButton
        {
            Role = Role.MenuItem,
            TabIndex = -1,
            Disabled = !CanActivate,
            OnClick = OnClick,
            Children = BuildContent(),
        }, "aw-menu-flyout-item");
        button.Attributes["part"] = "item";
        button.Attributes["aria-disabled"] = button.Disabled ? "true" : null;
        button.Attributes["data-menu-text-value"] = TextValue;
        var click = button.OnClick;
        button.OnClick = e =>
        {
            if (menu is null || !menu.CanInvokeItem || !CanActivate || e.CurrentTarget is not { IsDisabled: false }) return;
            click?.Invoke(e);
            if (e.DefaultPrevented || !menu.CanInvokeItem || !CanActivate) return;
            Command?.Execute(CommandParameter);
            menu.Close();
        };
        return button;
    }

    bool CanActivate => IsEnabled && (Command is not { } command || command.CanExecute(CommandParameter));
}
