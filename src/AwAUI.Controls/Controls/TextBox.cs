using System;
using System.Collections.Generic;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>Single or multiline editor with optional content before and after the text.</summary>
public sealed partial class TextBox : Control
{
    readonly ControllableState<string> _text = new(StringComparer.Ordinal);

    public string? Text { get; set; }
    public string DefaultText { get; set; } = "";
    public Action<string>? TextChanged { get; set; }
    public string? Watermark { get; set; }
    public bool AcceptsReturn { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsPassword { get; set; }
    public bool IsInvalid { get; set; }
    public Node? InnerLeftContent { get; set; }
    public Node? InnerRightContent { get; set; }
    public Action<SubmitEvent>? OnSubmit { get; set; }

    protected override Node Build()
    {
        string text = _text.Sync(Text, DefaultText, TextChanged);
        Input input = AcceptsReturn && !IsPassword ? new TextArea() : new Input();
        input.Id = Id;
        input.Ref = Ref;
        input.AriaLabel = AriaLabel;
        input.Role = Role.TextBox;
        input.Value = text;
        input.Placeholder = Watermark;
        input.ReadOnly = IsReadOnly;
        input.Disabled = !IsEnabled;
        input.InputType = IsPassword ? "password" : null;
        input.OnSubmit = OnSubmit;
        input.Attributes["part"] = "input";
        input.Attributes["aria-invalid"] = IsInvalid ? "true" : null;
        input.MergeAttributes(Attributes);
        var inputHandler = input.OnInput;
        input.OnInput = e =>
        {
            _text.Set(e.Value);
            inputHandler?.Invoke(e);
        };

        var box = PartProps.Apply(new Div { Class = "aw-textbox" }, Class, Style, null);
        box.Attributes["aria-disabled"] = input.Disabled ? "true" : null;
        box.Attributes["data-invalid"] = input.Attributes["aria-invalid"];
        box.Attributes["data-multiline"] = input is TextArea ? "true" : null;
        if (InnerLeftContent is { } before) box.Children.Add(Adornment("leading", before));
        box.Children.Add(input);
        if (InnerRightContent is { } after) box.Children.Add(Adornment("trailing", after));
        return box;
    }

    static Div Adornment(string part, Node content)
    {
        var box = new Div { Children = [content] };
        box.Attributes["part"] = part;
        return box;
    }
}

/// <summary>Labels a focusable control in the same window by its element ID.</summary>
public sealed partial class Label : Control
{
    public string? Target { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        var element = new LabelNode { Children = Children ?? [] };
        element.Attributes["for"] = Target;
        Apply(element, "aw-label");
        var down = element.OnPointerDown;
        element.OnPointerDown = e =>
        {
            down?.Invoke(e);
            if (!e.DefaultPrevented && e.Button == PointerButtons.Left && FindTarget(e) is { IsFocused: true })
                e.PreventDefault();
        };
        var click = element.OnClick;
        element.OnClick = e =>
        {
            click?.Invoke(e);
            if (e.DefaultPrevented || FindTarget(e) is not { Root: { } root } target) return;
            if (root.FocusManager.Focus(target, e.IsSynthetic ? FocusOrigin.Programmatic : FocusOrigin.Pointer)
                && target is ButtonElement { Role: Role.CheckBox or Role.Radio or Role.Switch })
                root.Input.Activate(target);
        };
        return element;
    }

    static Element? FindTarget(PointerEvent e)
    {
        if (e.CurrentTarget is not { Root: { } root } label || label.GetAttribute("for") is not { Length: > 0 } id)
            return null;
        for (var source = e.Target; source is not null && !ReferenceEquals(source, label); source = source.Parent)
            if (source.IsFocusable || source.CanBeDisabled || source.Role == Role.Link) return null;

        var pending = new Stack<Element>();
        pending.Push(root.RootElement);
        while (pending.Count != 0)
        {
            var element = pending.Pop();
            if (string.Equals(element.Id, id, StringComparison.Ordinal))
                return element.IsFocusable && element.IsVisible && !root.IsPointerBlocked(element) ? element : null;
            for (int i = element.Children.Count - 1; i >= 0; i--) pending.Push(element.Children[i]);
        }
        return null;
    }

    sealed class LabelNode : ElementNode
    {
        protected override Element CreateElement() => new Element("label");
    }
}
