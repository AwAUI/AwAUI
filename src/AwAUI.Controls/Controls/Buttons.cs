using System;
using System.Windows.Input;
using AwAUI.Controls.Primitives;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls;

/// <summary>A button with optional command routing. Raise <see cref="ICommand.CanExecuteChanged"/> on the UI thread.</summary>
public sealed partial class Button : Control
{
    public bool IsEnabled { get; set; } = true;
    public bool IsAccent { get; set; }
    public ICommand? Command { get; set; }
    public object? CommandParameter { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        var command = Command;
        UseEffect(() =>
        {
            if (command is null) return null;
            EventHandler refresh = (_, _) => Invalidate();
            command.CanExecuteChanged += refresh;
            return () => command.CanExecuteChanged -= refresh;
        }, command);

        var button = new UIButton
        {
            Disabled = !IsEnabled || (command is not null && !command.CanExecute(CommandParameter)),
            OnClick = OnClick, Children = Children ?? [],
        };
        Apply(button, IsAccent ? "aw-button accent" : "aw-button");
        var click = button.OnClick;
        button.OnClick = e =>
        {
            if (!IsEnabled || (Command is { } bound && !bound.CanExecute(CommandParameter))) return;
            click?.Invoke(e);
            if (!e.DefaultPrevented && Command is { } current && current.CanExecute(CommandParameter))
                current.Execute(CommandParameter);
        };
        return button;
    }
}

/// <summary>A button that retains its pressed state.</summary>
public sealed partial class ToggleButton : Control
{
    readonly ControllableState<bool> _checked = new();

    public bool? IsChecked { get; set; }
    public bool DefaultIsChecked { get; set; }
    public Action<bool>? IsCheckedChanged { get; set; }
    public bool IsEnabled { get; set; } = true;
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        bool pressed = _checked.Sync(IsChecked, DefaultIsChecked, IsCheckedChanged);
        var button = new UIButton { Disabled = !IsEnabled, Children = Children ?? [] };
        button.Attributes["aria-pressed"] = PartProps.Bool(pressed);
        button.Attributes["data-state"] = pressed ? "on" : "off";
        Apply(button, "aw-button aw-toggle-button");
        ControlParts.OnActivate(button, () => _checked.Set(!_checked.Value));
        return button;
    }
}

/// <summary>A checkbox that can also represent a mixed state. Set <see cref="IsChecked"/> for a controlled checkbox, or <see cref="DefaultIsChecked"/> for an initial value.</summary>
public sealed partial class CheckBox : Control
{
    readonly ControllableState<bool?> _checked = new();

    bool? _checkedProp = false;
    bool _hasCheckedProp;

    public bool? IsChecked
    {
        get => _checkedProp;
        set { _checkedProp = value; _hasCheckedProp = true; }
    }

    public bool? DefaultIsChecked { get; set; } = false;
    public Action<bool?>? IsCheckedChanged { get; set; }
    public bool IsThreeState { get; set; }
    public bool IsEnabled { get; set; } = true;
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        bool? value = _checked.Sync(_hasCheckedProp, _checkedProp, DefaultIsChecked, IsCheckedChanged);
        var indicator = new Div();
        indicator.Attributes["part"] = "indicator";
        indicator.Attributes["aria-hidden"] = "true";
        if (value != false)
        {
            var mark = new Div();
            mark.Attributes["part"] = "mark";
            indicator.Children.Add(mark);
        }
        var button = ControlParts.BinaryButton(Role.CheckBox, value, IsEnabled, indicator, Children);
        Apply(button, "aw-checkbox");
        ControlParts.OnActivate(button, () => _checked.Set(_checked.Value switch
        {
            false => true,
            true when IsThreeState => null,
            _ => false,
        }));
        return button;
    }

    // Omission and an explicit null are different, so the generated property comparison is insufficient here.
    protected override bool PropsEqual(Component other) => other is CheckBox box && base.PropsEqual(other)
        && _hasCheckedProp == box._hasCheckedProp && _checkedProp == box._checkedProp
        && DefaultIsChecked == box.DefaultIsChecked && IsCheckedChanged == box.IsCheckedChanged
        && IsThreeState == box.IsThreeState && IsEnabled == box.IsEnabled && ReferenceEquals(Children, box.Children);

    protected override void AdoptProps(Component other)
    {
        base.AdoptProps(other);
        var box = (CheckBox)other;
        _hasCheckedProp = box._hasCheckedProp;
        _checkedProp = box._checkedProp;
        DefaultIsChecked = box.DefaultIsChecked;
        IsCheckedChanged = box.IsCheckedChanged;
        IsThreeState = box.IsThreeState;
        IsEnabled = box.IsEnabled;
        Children = box.Children;
    }
}

/// <summary>An on/off switch with an optional text label.</summary>
public sealed partial class ToggleSwitch : Control
{
    readonly ControllableState<bool> _checked = new();

    public bool? IsChecked { get; set; }
    public bool DefaultIsChecked { get; set; }
    public Action<bool>? IsCheckedChanged { get; set; }
    public bool IsEnabled { get; set; } = true;
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        bool value = _checked.Sync(IsChecked, DefaultIsChecked, IsCheckedChanged);
        var thumb = new Div();
        thumb.Attributes["part"] = "thumb";
        var indicator = new Div { Children = [thumb] };
        indicator.Attributes["aria-hidden"] = "true";
        indicator.Attributes["part"] = "indicator";
        var button = ControlParts.BinaryButton(Role.Switch, value, IsEnabled, indicator, Children);
        Apply(button, "aw-toggle-switch");
        ControlParts.OnActivate(button, () => _checked.Set(!_checked.Value));
        return button;
    }
}

internal static class ControlParts
{
    internal static UIButton BinaryButton(Role role, bool? value, bool enabled, Node indicator, NodeList? children)
    {
        var button = new UIButton { Role = role, Disabled = !enabled, Children = [indicator] };
        button.Attributes["aria-checked"] = value.HasValue ? PartProps.Bool(value.Value) : "mixed";
        button.Attributes["data-state"] = value switch { true => "checked", false => "unchecked", null => "mixed" };
        if (children is { Count: > 0 })
        {
            var label = new Div { Children = children };
            label.Attributes["part"] = "label";
            button.Children.Add(label);
        }
        return button;
    }

    internal static void OnActivate(UIButton button, Action activate)
    {
        var click = button.OnClick;
        button.OnClick = e =>
        {
            click?.Invoke(e);
            if (!e.DefaultPrevented && e.CurrentTarget is { IsDisabled: false }) activate();
        };
    }
}
