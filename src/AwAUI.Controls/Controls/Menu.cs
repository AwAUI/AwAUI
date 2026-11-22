using System;
using System.Collections.Generic;
using System.Windows.Input;
using AwAUI.Controls.Primitives;
using AwAUI.Css.Values;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIPortal = AwAUI.UI.Portal;

namespace AwAUI.Controls;

/// <summary>
/// A horizontal menu bar with roving keyboard focus. Put <see cref="MenuItem"/> children in
/// <see cref="Children"/>; an item with children opens a submenu.
/// </summary>
/// <remarks>
/// Left and Right move between top-level items and open the neighbor's submenu while one is open.
/// Down and Up open the focused item's submenu; Escape closes it and returns focus to the item.
/// The bar is a single Tab stop. Only one top-level submenu is open at a time.
/// </remarks>
public sealed partial class Menu : Control
{
    readonly MenuBarContext _bar = new();
    readonly Ref<Element> _barElement = new();
    readonly MenuLevel _level;

    public Menu() => _level = new MenuLevel(null, null, _barElement, OverlayZOrder.Default);

    protected override string GlobalStyleSheet => MenuStyles.StyleSheet;

    /// <summary>The top-level <see cref="MenuItem"/> nodes.</summary>
    public NodeList? Children { get; set; }

    internal Element? BarElement => Ref?.Current ?? _barElement.Current;

    protected override Node Build()
    {
        var children = Children ?? [];
        int index = 0;
        foreach (var node in children)
            if (node is MenuItem item) item.Index = index++;

        var bar = new Div
        {
            Role = Role.MenuBar,
            TabIndex = -1,
            Children = new NodeList(children),
            OnKeyDown = OnBarKeyDown,
        };
        bar.Attributes["part"] = "bar";
        Apply(bar, "aw-menu");
        bar.Ref = Ref ?? _barElement;
        return Provide(_bar, Provide(_level, bar));
    }

    void OnBarKeyDown(KeyEvent e)
    {
        if (e.DefaultPrevented || e.Modifiers != KeyModifiers.None || BarElement is not { } bar) return;
        var direction = bar.ComputedStyle is { Direction: CssDirection.Rtl } ? Direction.Rtl : Direction.Ltr;
        var intent = RovingFocus.GetFocusIntent(e.Key, Orientation.Horizontal, direction);
        if (intent == FocusIntent.None) return;

        var items = new List<MenuItem>();
        foreach (var child in bar.ElementChildren)
            if (child.Owner is MenuItem item && item.CanActivate) items.Add(item);
        if (items.Count == 0) return;

        var focused = Root?.FocusManager.FocusedElement;
        int current = focused is null ? -1 : items.FindIndex(item => item.OwnsTrigger(focused));
        int next = RovingFocus.Navigate(intent, current, items.Count, _ => true, loop: true);
        e.PreventDefault();
        if (next < 0 || next == current) return;

        bool wasOpen = _bar.OpenItem is not null;
        var target = items[next];
        _bar.ActiveIndex.Value = target.Index;
        if (!target.FocusTrigger(FocusOrigin.Keyboard)) return;
        if (wasOpen && target.HasSubMenu) target.OpenSubMenu(FocusIntent.First, FocusOrigin.Keyboard);
    }
}

/// <summary>
/// An entry in a <see cref="Menu"/> or <see cref="ContextMenu"/>. An item with <see cref="Children"/>
/// opens a submenu; an item without them runs its command.
/// </summary>
/// <remarks>
/// Submenus open on hover after a short delay, on click, and with Right, Down or Enter. Left closes a
/// submenu and returns focus to its trigger; Escape closes the topmost menu. Inside an open submenu,
/// Up, Down, Home, End and typeahead move the highlight.
/// </remarks>
public sealed partial class MenuItem : Control
{
    static readonly TimeSpan s_hoverDelay = TimeSpan.FromMilliseconds(100);

    readonly ControllableState<bool> _open = new();
    readonly Ref<Element> _trigger = new();
    readonly Ref<Element> _content = new();
    readonly string _triggerId = PrimitiveIds.Next("menu-item-trigger");
    readonly string _contentId = PrimitiveIds.Next("menu-item-content");
    MenuBarContext? _bar;
    MenuLevel? _level;
    SubMenuSession? _session;
    IDisposable? _hoverTimer;
    int _hoverVersion;
    bool _hoverStarted;
    bool _wasOpen;
    bool _openAtEnd;
    bool _focusOnOpen;
    FocusOrigin _revealOrigin = FocusOrigin.Programmatic;
    Side? _lastSide;

    /// <summary>Text or noninteractive content for the item label.</summary>
    public object? Header { get; set; }

    public Func<object, Node>? HeaderTemplate { get; set; }

    /// <summary>Submenu entries. Empty for an action item.</summary>
    public NodeList? Children { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Whether the item is checked. Null renders an ordinary menu item.</summary>
    public bool? IsChecked { get; set; }

    public Action<bool>? IsCheckedChanged { get; set; }

    /// <summary>Use radio semantics for the checked state.</summary>
    public bool IsRadio { get; set; }

    /// <summary>Runs before the command. Cancel the event to keep the menu open and skip the command.</summary>
    public Action<PointerEvent>? OnClick { get; set; }

    /// <summary>Raise <see cref="ICommand.CanExecuteChanged"/> on the UI thread.</summary>
    public ICommand? Command { get; set; }

    public object? CommandParameter { get; set; }

    /// <summary>Shortcut text shown at the trailing edge. Purely presentational.</summary>
    public string? InputGesture { get; set; }

    /// <summary>Controls whether the submenu is open. Null uses internal state.</summary>
    public bool? IsSubMenuOpen { get; set; }

    /// <summary>Initial open state when <see cref="IsSubMenuOpen"/> is null.</summary>
    public bool DefaultIsSubMenuOpen { get; set; }

    /// <summary>Reports an open state change, or requests one when the open state is controlled.</summary>
    public Action<bool>? IsSubMenuOpenChanged { get; set; }

    /// <summary>Optional label for typeahead. Defaults to the accessible label or rendered text.</summary>
    public string? TextValue { get; set; }

    /// <summary>Position in the menu bar (set by <see cref="Menu"/>).</summary>
    internal int Index { get; set; }

    internal bool HasSubMenu => Children is { Count: > 0 };

    internal bool CanActivate => IsEnabled && (Command is not { } command || command.CanExecute(CommandParameter));

    internal Element? TriggerElement => Ref?.Current ?? _trigger.Current;

    internal bool OwnsTrigger(Element element) => TriggerElement is { } trigger && trigger.IsInclusiveAncestorOf(element);

    internal bool FocusTrigger(FocusOrigin origin) =>
        TriggerElement is { IsVisible: true, IsFocusable: true } trigger
        && trigger.Focus(new FocusOptions { Origin = origin, PreventScroll = true });

    /// <summary>Opens this item's submenu and records it on the menu bar.</summary>
    internal void OpenSubMenu(FocusIntent intent, FocusOrigin origin)
    {
        if (!HasSubMenu || !CanActivate) return;
        _openAtEnd = intent == FocusIntent.Last;
        _focusOnOpen = true;
        _revealOrigin = origin;
        _bar?.Open(this);
        _open.Set(true);
    }

    /// <summary>Closes this item's submenu without moving focus.</summary>
    internal void CloseSubMenu()
    {
        if (!_open.Value) return;
        _open.Set(false);
        _bar?.Closed(this);
    }

    sealed class SubMenuSession(bool openAtEnd)
    {
        internal readonly bool OpenAtEnd = openAtEnd;
        internal bool SkipRestore;
    }

    protected override Node Build()
    {
        var level = Use<MenuLevel>();
        _level = level;
        // Only top-level items take part in the menu bar's roving focus.
        _bar = level.Parent is null && TryUse<MenuBarContext>(out var bar) ? bar : null;

        var command = Command;
        UseEffect(() =>
        {
            if (command is null) return null;
            EventHandler refresh = (_, _) => Invalidate();
            command.CanExecuteChanged += refresh;
            return () => command.CanExecuteChanged -= refresh;
        }, command);

        bool requested = _open.Sync(IsSubMenuOpen, DefaultIsSubMenuOpen, IsSubMenuOpenChanged);
        bool hasSubMenu = HasSubMenu;
        bool open = hasSubMenu && requested && CanActivate;
        UseEffect(() =>
        {
            if ((!hasSubMenu || !CanActivate) && _open.Value) _open.Set(false);
            return null;
        }, hasSubMenu, CanActivate);
        if (open && !_wasOpen) _session = new SubMenuSession(_openAtEnd);
        _openAtEnd = false;
        _wasOpen = open;
        if (!hasSubMenu) _session = null;

        var trigger = PartProps.Apply(new UIButton
        {
            Role = Role.MenuItem,
            TabIndex = _bar is null ? -1 : (Index == Math.Max(0, _bar.ActiveIndex.Value) ? 0 : -1),
            Disabled = !CanActivate,
            OnClick = OnTriggerClick,
            OnKeyDown = OnTriggerKeyDown,
            OnPointerMove = OnTriggerPointerMove,
            OnPointerLeave = OnTriggerPointerLeave,
            OnFocus = OnTriggerFocus,
            OnBlur = _ => CancelHover(),
            Children = BuildTriggerContent(hasSubMenu),
        }, Class, Style, Attributes);
        trigger.Id ??= _triggerId;
        trigger.Class = trigger.Class.IsEmpty ? "aw-menu-item" : new ClassValue(trigger.Class.Text + " aw-menu-item");
        trigger.Ref = Ref ?? _trigger;
        trigger.Attributes["part"] = "item";
        trigger.Attributes["data-menu-text-value"] = TextValue;
        trigger.Attributes["data-state"] = IsChecked switch
        {
            true => "checked",
            false => "unchecked",
            null => open ? "open" : "closed",
        };
        if (IsChecked.HasValue)
        {
            trigger.Attributes["role"] = IsRadio ? "menuitemradio" : "menuitemcheckbox";
            trigger.Attributes["aria-checked"] = PartProps.Bool(IsChecked.Value);
        }
        if (hasSubMenu)
        {
            trigger.Attributes["aria-haspopup"] = "menu";
            trigger.Attributes["aria-expanded"] = PartProps.Bool(open);
            trigger.Attributes["aria-controls"] = _contentId;
        }
        trigger.Attributes["aria-disabled"] = CanActivate ? null : "true";

        var anchor = new PopperAnchor { AsChild = true, Children = [trigger] };
        var surface = _session ?? (open ? new SubMenuSession(_openAtEnd) : null);
        return new Popper
        {
            Children = surface is not null && level.IsOpen
                ? [anchor, BuildSurface(open, level, surface)]
                : [anchor],
        };
    }

    NodeList BuildTriggerContent(bool hasSubMenu)
    {
        var children = new NodeList();
        if (IsChecked.HasValue)
        {
            var check = new Span();
            check.Attributes["part"] = "indicator";
            check.Attributes["data-indicator"] = "check";
            check.Attributes["aria-hidden"] = "true";
            children.Add(check);
        }
        if (Header is not null || HeaderTemplate is not null)
        {
            var label = new Span { Children = [ContentControl.Present(Header ?? "", HeaderTemplate)] };
            label.Attributes["part"] = "label";
            children.Add(label);
        }
        if (InputGesture is { } gesture)
        {
            var shortcut = new Span(gesture);
            shortcut.Attributes["part"] = "gesture";
            shortcut.Attributes["aria-hidden"] = "true";
            children.Add(shortcut);
        }
        if (hasSubMenu)
        {
            var chevron = new Span();
            chevron.Attributes["part"] = "indicator";
            chevron.Attributes["data-indicator"] = "chevron";
            chevron.Attributes["aria-hidden"] = "true";
            children.Add(chevron);
        }
        return children;
    }

    Node BuildSurface(bool open, MenuLevel level, SubMenuSession session)
    {
        var childLevel = new MenuLevel(level, _open, _content, level.ZOrder + 1);
        var attributes = new AttributeDictionary
        {
            ["part"] = "content",
            ["data-state"] = PartProps.OpenState(open),
            ["aria-hidden"] = open ? null : "true",
            ["aria-labelledby"] = _triggerId,
            ["onkeydown"] = (Action<KeyEvent>)(e => childLevel.HandleKeyDown(
                e, level.Surface is null ? null : () => CloseToTrigger(session))),
        };
        var content = new PopperContent
        {
            Id = _contentId,
            Role = Role.Menu,
            TabIndex = -1,
            Ref = _content,
            Class = "aw-menu-content",
            // 子菜单在行内末尾：LTR 在右、RTL 在左；Popper 按内容 / 锚点继承的方向解析
            Side = level.Parent is null ? Side.Bottom : Side.InlineEnd,
            Align = PopperAlign.Start,
            SideOffset = level.Parent is null ? 4 : 2,
            CollisionPadding = 8,
            OnPlaced = result => _lastSide = result.PlacedSide,
            Attributes = attributes,
            Children = [Provide(childLevel, new Fragment(Children ?? []))],
        };
        var layer = new DismissableLayer
        {
            Active = open,
            OnInteractOutside = e =>
            {
                // The trigger handles its own press; focus can return to it while the layer is still mounted.
                if (!childLevel.IsOpen || TriggerElement?.IsInclusiveAncestorOf(e.Target) == true) e.PreventDefault();
            },
            OnDismiss = reason =>
            {
                if (!childLevel.IsOpen) return;
                if (level.Parent is not null && reason == DismissReason.EscapeKey)
                {
                    CloseToTrigger(session);
                    return;
                }
                if (reason != DismissReason.EscapeKey) childLevel.SkipRestore = true;
                _open.Set(false);
                _bar?.Closed(this);
            },
            Children = [content],
        };
        return new PresenceGate
        {
            Present = open,
            Children = [new UIPortal { ZOrder = childLevel.ZOrder, Children = [WrapLayer(layer, open, childLevel, session)] }],
        };
    }

    Node WrapLayer(DismissableLayer layer, bool open, MenuLevel childLevel, SubMenuSession session)
    {
        // Only a top-level submenu owns the focus scope. A nested submenu moves focus explicitly so
        // hover-opening it does not steal focus from its trigger.
        if (_level is { Parent: not null }) return layer;
        return new FocusScope
        {
            Active = open,
            OnMountAutoFocus = e =>
            {
                e.PreventDefault();
                if (!_focusOnOpen || !CanActivate || !ReferenceEquals(_session, session)) return;
                var origin = _revealOrigin == FocusOrigin.Pointer && Root?.FocusManager.LastInteractionWasKeyboard == true
                    ? FocusOrigin.Keyboard : _revealOrigin;
                if (session.OpenAtEnd) childLevel.FocusLast(origin);
                else childLevel.FocusFirst(origin);
            },
            OnUnmountAutoFocus = e =>
            {
                e.PreventDefault();
                if (!IsMounted || _open.Value || session.SkipRestore || !ReferenceEquals(_session, session)
                    || Root is not { } root) return;
                var focused = root.FocusManager.FocusedElement;
                if (focused is null || e.Container.IsInclusiveAncestorOf(focused))
                    FocusTrigger(FocusOrigin.Programmatic);
            },
            Children = [layer],
        };
    }

    void CloseToTrigger(SubMenuSession session)
    {
        session.SkipRestore = true;
        FocusTrigger(FocusOrigin.Keyboard);
        _open.Set(false);
        _bar?.Closed(this);
    }

    void OnTriggerClick(PointerEvent e)
    {
        if (e.DefaultPrevented || !CanActivate || e.CurrentTarget is not { IsDisabled: false }) return;
        if (HasSubMenu)
        {
            e.PreventDefault();
            CancelHover();
            if (_open.Value) CloseSubMenu();
            else OpenSubMenu(FocusIntent.First, e.IsSynthetic ? FocusOrigin.Keyboard : FocusOrigin.Pointer);
            return;
        }
        OnClick?.Invoke(e);
        var menu = _level;
        if (e.DefaultPrevented || !CanActivate || menu is null) return;
        Command?.Execute(CommandParameter);
        menu.CloseChain();
    }

    void OnTriggerKeyDown(KeyEvent e)
    {
        if (e.DefaultPrevented || !HasSubMenu || !CanActivate || e.Modifiers != KeyModifiers.None
            || !ReferenceEquals(e.Target, e.CurrentTarget)) return;
        bool rtl = TriggerElement?.ComputedStyle is { Direction: CssDirection.Rtl };
        if (e.Key == (rtl ? "ArrowLeft" : "ArrowRight"))
        {
            e.PreventDefault();
            OpenSubMenu(FocusIntent.First, FocusOrigin.Keyboard);
            return;
        }
        if (e.Key == (rtl ? "ArrowRight" : "ArrowLeft"))
        {
            if (!_open.Value) return;
            e.PreventDefault();
            CloseSubMenu();
            return;
        }
        if (e.Key == "ArrowDown")
        {
            e.PreventDefault();
            OpenSubMenu(FocusIntent.First, FocusOrigin.Keyboard);
        }
        else if (e.Key == "ArrowUp")
        {
            e.PreventDefault();
            OpenSubMenu(FocusIntent.Last, FocusOrigin.Keyboard);
        }
    }

    void OnTriggerFocus(FocusEvent e)
    {
        if (_bar is null || _bar.ActiveIndex.Value == Index) return;
        _bar.ActiveIndex.Value = Index;
    }

    void OnTriggerPointerMove(PointerEvent e)
    {
        if (e.DefaultPrevented || e.PointerType != PointerType.Mouse || _level is not { } level || !level.IsOpen
            || !CanActivate || e.CurrentTarget is not { IsFocusable: true } trigger) return;
        level.ClearGrace(_open);
        if (level.InPointerGrace(e) || e.Buttons != PointerButtons.None) return;
        if (!trigger.IsFocused) trigger.Focus(new FocusOptions { Origin = FocusOrigin.Pointer, PreventScroll = true });
        if (!_open.Value && trigger.IsFocused && !_hoverStarted) StartHover(level);
    }

    void OnTriggerPointerLeave(PointerEvent e)
    {
        if (e.PointerType != PointerType.Mouse) return;
        CancelHover();
        var side = _lastSide;
        if (_level is not { } level || !_open.Value || _content.Current is not { IsVisible: true } content
            || side is not (Side.Left or Side.Right)) return;
        level.Grace.TrackPointerMove(e.Position);
        level.Grace.Begin(e.Position, content.GetBoundingClientRect(), side.Value, e.Timestamp);
        level.GraceOwner = _open;
    }

    void StartHover(MenuLevel level)
    {
        _hoverStarted = true;
        int version = _hoverVersion;
        void Open()
        {
            if (version != _hoverVersion) return;
            _hoverTimer = null;
            if (TriggerElement is not { IsFocused: true, IsVisible: true } || !level.IsOpen || !HasSubMenu || !CanActivate)
                return;
            _openAtEnd = false;
            _focusOnOpen = false;
            _revealOrigin = FocusOrigin.Pointer;
            _bar?.Open(this);
            _open.Set(true);
        }

        if (Root?.Clock is { } clock) _hoverTimer = clock.Schedule(s_hoverDelay, Open);
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
        _bar?.Closed(this);
        _bar = null;
        _level = null;
        _lastSide = null;
    }
}

internal static class MenuStyles
{
    internal const string StyleSheet = """
        @layer aw-menu-defaults {
          :where(.aw-menu) {
            display: flex;
            flex-direction: row;
            align-items: center;
            gap: 2px;
            min-width: 0;
          }
          :where(.aw-menu-item) {
            display: flex;
            flex: 0 0 auto;
            align-items: center;
            gap: 8px;
            box-sizing: border-box;
            min-height: 28px;
            padding: 4px 8px;
            border: 0;
            border-radius: var(--aw-radius, 6px);
            background-color: transparent;
            color: inherit;
            white-space: nowrap;
          }
          :where(.aw-menu-item:enabled:hover,
                 .aw-menu-item:enabled:focus,
                 .aw-menu-item[data-state="open"]) {
            background-color: var(--aw-control-hover, #edf1f5);
          }
          :where(.aw-menu-item:disabled) { opacity: 0.55; }
          :where(.aw-menu-item > [part="indicator"][data-indicator="check"]) {
            display: block;
            width: 9px;
            height: 5px;
            flex: none;
            margin-bottom: 3px;
            border-left: 1.5px solid currentColor;
            border-bottom: 1.5px solid currentColor;
            transform: rotate(-45deg);
          }
          :where(.aw-menu-item > [part="indicator"][data-indicator="chevron"]) {
            display: block;
            width: 5px;
            height: 5px;
            flex: none;
            margin-left: auto;
            border-top: 1px solid currentColor;
            border-right: 1px solid currentColor;
            transform: rotate(45deg);
          }
          :where(.aw-menu-item > [part="gesture"]) {
            margin-left: auto;
            padding-left: 16px;
            color: var(--aw-text-muted, #6b7280);
            font-size: 12px;
          }
          :where(.aw-menu-content) {
            display: flex;
            flex-direction: column;
            box-sizing: border-box;
            width: max-content;
            min-width: min(160px, var(--aw-popper-available-width, calc(100vw - 16px)));
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
          :where(.aw-menu-content[data-state="closed"],
                 [data-aw-popper-content-wrapper]:has(> .aw-menu-content[data-state="closed"])) {
            pointer-events: none;
          }
          :where(.aw-menu-content .aw-menu-item) {
            display: flex;
            flex: 0 0 auto;
            width: 100%;
            box-sizing: border-box;
            justify-content: flex-start;
          }
          :where(.aw-menu-content [part="indicator"][data-indicator="chevron"]) { margin-left: auto; }
        }
        """;
}
