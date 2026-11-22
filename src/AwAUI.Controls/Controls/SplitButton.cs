using System;
using System.Collections.Generic;
using System.Windows.Input;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls;

/// <summary>
/// A primary action with an adjacent dropdown of related actions. The two parts form one group: the
/// primary button activates on Enter or Space, and the dropdown opens on click, Down, Enter or Space.
/// </summary>
/// <remarks>
/// Put <see cref="MenuItem"/> nodes in <see cref="Items"/>; nested items open submenus. The dropdown
/// closes on Escape, an outside press, or selection, and returns focus to the dropdown button.
/// </remarks>
public sealed partial class SplitButton : Control
{
    readonly SplitButtonParts _parts = new();
    readonly ControllableState<bool> _open = new();

    protected override string GlobalStyleSheet => SplitButtonParts.StyleSheet;

    /// <summary>Text or noninteractive content for the primary button.</summary>
    public NodeList? Children { get; set; }

    /// <summary>The dropdown entries, typically <see cref="MenuItem"/> nodes.</summary>
    public NodeList? Items { get; set; }

    public string? MenuAriaLabel { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Runs before the command. Cancel the event to skip the command.</summary>
    public Action<PointerEvent>? OnClick { get; set; }

    /// <summary>Raise <see cref="ICommand.CanExecuteChanged"/> on the UI thread.</summary>
    public ICommand? Command { get; set; }

    public object? CommandParameter { get; set; }

    /// <summary>The dropdown's accessible name. Defaults to the primary content's text.</summary>
    public string? DropDownAriaLabel { get; set; }

    /// <summary>Set to control the dropdown, or leave null to let the control manage it.</summary>
    public bool? IsOpen { get; set; }

    public bool DefaultIsOpen { get; set; }

    /// <summary>Requests a change to <see cref="IsOpen"/>.</summary>
    public Action<bool>? IsOpenChanged { get; set; }

    public int ZOrder { get; set; } = OverlayZOrder.Default;

    protected override Node Build() => _parts.Build(new SplitButtonParts.Options
    {
        Host = this,
        Open = _open.Sync(IsOpen, DefaultIsOpen, IsOpenChanged),
        OpenState = _open,
        IsEnabled = IsEnabled,
        Children = Children,
        Items = Items,
        MenuAriaLabel = MenuAriaLabel,
        DropDownAriaLabel = DropDownAriaLabel,
        OnClick = OnClick,
        Command = Command,
        CommandParameter = CommandParameter,
        ZOrder = ZOrder,
    });
}

/// <summary>
/// A <see cref="SplitButton"/> whose primary part is a toggle button that keeps its checked state.
/// </summary>
public sealed partial class ToggleSplitButton : Control
{
    readonly SplitButtonParts _parts = new();
    readonly ControllableState<bool> _open = new();
    readonly ControllableState<bool> _checked = new();

    protected override string GlobalStyleSheet => SplitButtonParts.StyleSheet;

    /// <summary>Text or noninteractive content for the primary toggle button.</summary>
    public NodeList? Children { get; set; }

    /// <summary>The dropdown entries, typically <see cref="MenuItem"/> nodes.</summary>
    public NodeList? Items { get; set; }

    public string? MenuAriaLabel { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Whether the primary part is pressed. Null uses internal state.</summary>
    public bool? IsChecked { get; set; }

    public bool DefaultIsChecked { get; set; }

    public Action<bool>? IsCheckedChanged { get; set; }

    /// <summary>Runs after the checked state changes. Cancel the event to skip the command.</summary>
    public Action<PointerEvent>? OnClick { get; set; }

    /// <summary>Raise <see cref="ICommand.CanExecuteChanged"/> on the UI thread.</summary>
    public ICommand? Command { get; set; }

    public object? CommandParameter { get; set; }

    public string? DropDownAriaLabel { get; set; }

    public bool? IsOpen { get; set; }

    public bool DefaultIsOpen { get; set; }

    public Action<bool>? IsOpenChanged { get; set; }

    public int ZOrder { get; set; } = OverlayZOrder.Default;

    protected override Node Build() => _parts.Build(new SplitButtonParts.Options
    {
        Host = this,
        Open = _open.Sync(IsOpen, DefaultIsOpen, IsOpenChanged),
        OpenState = _open,
        IsEnabled = IsEnabled,
        Children = Children,
        Items = Items,
        MenuAriaLabel = MenuAriaLabel,
        DropDownAriaLabel = DropDownAriaLabel,
        OnClick = OnClick,
        Command = Command,
        CommandParameter = CommandParameter,
        ZOrder = ZOrder,
        Toggle = _checked.Sync(IsChecked, DefaultIsChecked, IsCheckedChanged),
        ToggleState = _checked,
    });
}

/// <summary>Split button rendering shared by <see cref="SplitButton"/> and <see cref="ToggleSplitButton"/>.</summary>
sealed class SplitButtonParts
{
    readonly Ref<Element> _group = new();
    readonly Ref<Element> _dropDown = new();
    readonly Ref<Element> _content = new();
    readonly string _contentId = PrimitiveIds.Next("split-button-menu");
    MenuLevel? _level;
    bool _wasOpen;

    internal sealed class Options
    {
        internal Control Host = null!;
        internal bool Open;
        internal ControllableState<bool> OpenState = null!;
        internal bool IsEnabled;
        internal NodeList? Children;
        internal NodeList? Items;
        internal string? MenuAriaLabel;
        internal string? DropDownAriaLabel;
        internal Action<PointerEvent>? OnClick;
        internal ICommand? Command;
        internal object? CommandParameter;
        internal int ZOrder;
        internal bool? Toggle;
        internal ControllableState<bool>? ToggleState;
    }

    internal Node Build(Options options)
    {
        _level ??= new MenuLevel(null, options.OpenState, _content, options.ZOrder);
        bool canActivate = options.IsEnabled
            && (options.Command is not { } command || command.CanExecute(options.CommandParameter));
        if (options.Open && !_wasOpen) _level.SkipRestore = false;
        _wasOpen = options.Open;

        var primary = new UIButton
        {
            Disabled = !canActivate,
            Children = options.Children ?? [],
            OnClick = e => OnPrimaryClick(e, options, canActivate),
        };
        primary.Class = "aw-button aw-split-button-primary";
        primary.Attributes["part"] = "primary";
        primary.Attributes["type"] = "button";
        if (options.Toggle.HasValue)
        {
            primary.Attributes["aria-pressed"] = PartProps.Bool(options.Toggle.Value);
            primary.Attributes["data-state"] = options.Toggle.Value ? "on" : "off";
        }

        var indicator = new Span();
        indicator.Attributes["part"] = "indicator";
        indicator.Attributes["aria-hidden"] = "true";
        var dropDown = new UIButton
        {
            Disabled = !canActivate,
            Children = [indicator],
            OnClick = e => OnDropDownClick(e, options),
            OnKeyDown = e => OnDropDownKeyDown(e, options, canActivate),
        };
        dropDown.Class = "aw-button aw-split-button-toggle";
        dropDown.Ref = _dropDown;
        dropDown.AriaLabel = options.DropDownAriaLabel ?? options.MenuAriaLabel;
        dropDown.Attributes["part"] = "toggle";
        dropDown.Attributes["type"] = "button";
        dropDown.Attributes["aria-haspopup"] = "menu";
        dropDown.Attributes["aria-expanded"] = PartProps.Bool(options.Open);
        dropDown.Attributes["aria-controls"] = options.Open ? _contentId : null;
        dropDown.Attributes["data-state"] = PartProps.OpenState(options.Open);

        var group = new Div { Children = [primary, dropDown], Class = "aw-split-button" };
        group.Attributes["role"] = "group";
        group.Attributes["data-state"] = PartProps.OpenState(options.Open);
        group.Id = options.Host.Id;
        group.Ref = options.Host.Ref ?? _group;
        group.AriaLabel = options.Host.AriaLabel;
        PartProps.Apply(group, options.Host.Class, options.Host.Style, options.Host.Attributes);
        group.Attributes["part"] = "group";

        var anchor = new PopperAnchor { AsChild = true, Children = [group] };
        return new Popper { Children = [anchor, BuildSurface(options)] };
    }

    Node BuildSurface(Options options)
    {
        var level = _level!;
        var attributes = new AttributeDictionary
        {
            ["part"] = "content",
            ["data-state"] = PartProps.OpenState(options.Open),
            ["aria-hidden"] = options.Open ? null : "true",
            ["aria-label"] = options.MenuAriaLabel,
            ["aria-labelledby"] = options.MenuAriaLabel is null ? _dropDown.Current?.Id : null,
            ["onkeydown"] = (Action<KeyEvent>)(e => level.HandleKeyDown(e, null)),
        };
        var content = new PopperContent
        {
            Id = _contentId,
            Role = Role.Menu,
            TabIndex = -1,
            Ref = _content,
            Class = "aw-menu-content",
            Side = Side.Bottom,
            Align = PopperAlign.Start,
            SideOffset = 4,
            CollisionPadding = 8,
            Attributes = attributes,
            Children = [Context.Provide(level, new Fragment(options.Items ?? []))],
        };
        var layer = new DismissableLayer
        {
            Active = options.Open,
            OnInteractOutside = e =>
            {
                if (_dropDown.Current?.IsInclusiveAncestorOf(e.Target) == true) e.PreventDefault();
            },
            OnDismiss = _ =>
            {
                if (!options.Open) return;
                level.SkipRestore = true;
                options.OpenState.Set(false);
            },
            Children = [content],
        };
        return new PresenceGate
        {
            Present = options.Open,
            Children = [new Portal { ZOrder = options.ZOrder, Children = [BuildScope(layer, options, level)] }],
        };
    }

    Node BuildScope(DismissableLayer layer, Options options, MenuLevel level) => new FocusScope
    {
        Active = options.Open,
        OnMountAutoFocus = e =>
        {
            e.PreventDefault();
            if (!options.Open || _level is not { } current || !ReferenceEquals(current, level)) return;
            var origin = _revealOrigin;
            if (origin == FocusOrigin.Pointer && Root?.FocusManager.LastInteractionWasKeyboard == true)
                origin = FocusOrigin.Keyboard;
            if (_openAtEnd) level.FocusLast(origin);
            else level.FocusFirst(origin);
        },
        OnUnmountAutoFocus = e =>
        {
            e.PreventDefault();
            var root = Root;
            if (root is null || level.SkipRestore) return;
            var focused = root.FocusManager.FocusedElement;
            if (focused is not null && !e.Container.IsInclusiveAncestorOf(focused)) return;
            _dropDown.Current?.Focus(new FocusOptions { Origin = FocusOrigin.Programmatic, PreventScroll = true });
        },
        Children = [layer],
    };

    UIRoot? Root => _group.Current?.Root ?? _content.Current?.Root;

    void OnPrimaryClick(PointerEvent e, Options options, bool canActivate)
    {
        if (e.DefaultPrevented || !canActivate || e.CurrentTarget is not { IsDisabled: false }) return;
        if (options.ToggleState is { } state) state.Set(!state.Value);
        options.OnClick?.Invoke(e);
        if (e.DefaultPrevented || !canActivate) return;
        if (options.Command is { } command && command.CanExecute(options.CommandParameter))
            command.Execute(options.CommandParameter);
    }

    void OnDropDownClick(PointerEvent e, Options options)
    {
        if (e.DefaultPrevented || _dropDown.Current is not { IsDisabled: false }) return;
        e.PreventDefault();
        if (options.Open) Close(options);
        else Open(options, FocusIntent.First, e.IsSynthetic ? FocusOrigin.Keyboard : FocusOrigin.Pointer);
    }

    void OnDropDownKeyDown(KeyEvent e, Options options, bool canActivate)
    {
        if (e.DefaultPrevented || !canActivate || e.Modifiers != KeyModifiers.None
            || !ReferenceEquals(e.Target, e.CurrentTarget)) return;
        if (e.Key == "ArrowDown")
        {
            e.PreventDefault();
            Open(options, FocusIntent.First, FocusOrigin.Keyboard);
        }
        else if (e.Key == "ArrowUp")
        {
            e.PreventDefault();
            Open(options, FocusIntent.Last, FocusOrigin.Keyboard);
        }
    }

    void Open(Options options, FocusIntent intent, FocusOrigin origin)
    {
        _openAtEnd = intent == FocusIntent.Last;
        _revealOrigin = origin;
        options.OpenState.Set(true);
    }

    void Close(Options options)
    {
        _dropDown.Current?.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard, PreventScroll = true });
        _level!.SkipRestore = true;
        options.OpenState.Set(false);
    }

    bool _openAtEnd;
    FocusOrigin _revealOrigin = FocusOrigin.Programmatic;

    internal const string StyleSheet = """
        @layer aw-split-button-defaults {
          :where(.aw-split-button) {
            display: inline-flex;
            flex-direction: row;
            align-items: stretch;
            box-sizing: border-box;
          }
          :where(.aw-split-button > [part="primary"]) {
            border-top-right-radius: 0;
            border-bottom-right-radius: 0;
          }
          :where(.aw-split-button > [part="toggle"]) {
            display: inline-flex;
            align-items: center;
            justify-content: center;
            min-width: 26px;
            padding: 0 6px;
            border-top-left-radius: 0;
            border-bottom-left-radius: 0;
            border-inline-start-width: 0;
          }
          :where(.aw-split-button > [part="toggle"] > [part="indicator"]) {
            display: block;
            width: 6px;
            height: 6px;
            margin-bottom: 3px;
            border-right: 1px solid currentColor;
            border-bottom: 1px solid currentColor;
            transform: rotate(45deg);
          }
        }
        """ + MenuStyles.StyleSheet;
}
