using System;
using System.Collections.Generic;
using AwAUI.Controls.Primitives;
using AwAUI.Tailwind;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIInput = AwAUI.UI.Input;
using UITextArea = AwAUI.UI.TextArea;

namespace AwAUI.Shadcn;

/// <summary>Placement of an <see cref="InputGroupAddon"/> inside an <see cref="InputGroup"/>.</summary>
public enum InputGroupAlign { InlineStart, InlineEnd, BlockStart, BlockEnd }

/// <summary>Height of an <see cref="InputGroupButton"/>.</summary>
public enum InputGroupButtonSize { Xs, Sm, IconXs, IconSm }

/// <summary>Group context shared with addons and controls (the disabled state reaches the inner editor).</summary>
internal readonly struct InputGroupContext
{
    internal InputGroupContext(bool disabled) => Disabled = disabled;

    internal bool Disabled { get; }
}

/// <summary>A control with leading or trailing addons. Compose one <see cref="InputGroupInput"/> or <see cref="InputGroupTextarea"/>.</summary>
public sealed partial class InputGroup : Component
{
    const string GroupClass =
        "group/input-group relative flex w-full items-center rounded-md border border-input shadow-xs transition-[color,box-shadow] outline-none " +
        "dark:bg-input/30 h-9 min-w-0 has-[>textarea]:h-auto " +
        "has-[>[data-align=inline-start]]:[&>input]:pl-2 " +
        "has-[>[data-align=inline-end]]:[&>input]:pr-2 " +
        "has-[>[data-align=block-start]]:h-auto has-[>[data-align=block-start]]:flex-col has-[>[data-align=block-start]]:[&>input]:pb-3 " +
        "has-[>[data-align=block-end]]:h-auto has-[>[data-align=block-end]]:flex-col has-[>[data-align=block-end]]:[&>input]:pt-3 " +
        "has-[[data-slot=input-group-control]:focus-visible]:border-ring " +
        "has-[[data-slot=input-group-control]:focus-visible]:ring-[3px] has-[[data-slot=input-group-control]:focus-visible]:ring-ring/50 " +
        "has-[[data-slot][aria-invalid=true]]:border-destructive " +
        "has-[[data-slot][aria-invalid=true]]:ring-destructive/20 dark:has-[[data-slot][aria-invalid=true]]:ring-destructive/40";

    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var group = StaticParts.Style(new Div { Ref = Ref, Children = Children ?? new NodeList() }, "input-group",
            GroupClass, Class, Rest);
        if (group.Role == Role.None) group.Role = Role.Group;
        group.Attributes["data-disabled"] ??= Disabled ? "true" : null;
        return Provide(new InputGroupContext(Disabled), group);
    }
}

/// <summary>Icon, text or button at the start or end of an input group. Clicking it focuses the editor.</summary>
public sealed partial class InputGroupAddon : Component
{
    static readonly Cva<InputGroupAlign> Variants = new(
        "flex h-auto cursor-text items-center justify-center gap-2 py-1.5 text-sm font-medium text-muted-foreground select-none " +
        "group-data-[disabled=true]/input-group:opacity-50 [&>kbd]:rounded-[calc(var(--radius)-5px)] [&>svg:not([class*='size-'])]:size-4",
        new Dictionary<InputGroupAlign, string>
        {
            [InputGroupAlign.InlineStart] =
                "order-first pl-3 has-[>button]:ml-[-0.45rem] has-[>kbd]:ml-[-0.35rem]",
            [InputGroupAlign.InlineEnd] =
                "order-last pr-3 has-[>button]:mr-[-0.45rem] has-[>kbd]:mr-[-0.35rem]",
            [InputGroupAlign.BlockStart] =
                "order-first w-full justify-start px-3 pt-3 group-has-[>input]/input-group:pt-2.5 [.border-b]:pb-3",
            [InputGroupAlign.BlockEnd] =
                "order-last w-full justify-start px-3 pb-3 group-has-[>input]/input-group:pb-2.5 [.border-t]:pt-3",
        });

    public InputGroupAlign Align { get; set; } = InputGroupAlign.InlineStart;
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var addon = StaticParts.Style(new Div { Ref = Ref, OnClick = OnClick, Children = Children ?? new NodeList() },
            "input-group-addon", Variants.Of(Align), Class, Rest);
        if (addon.Role == Role.None) addon.Role = Role.Group;
        addon.Attributes["data-align"] ??= (Align switch
        {
            InputGroupAlign.InlineEnd => "inline-end",
            InputGroupAlign.BlockStart => "block-start",
            InputGroupAlign.BlockEnd => "block-end",
            _ => "inline-start",
        });
        addon.OnClick = EventComposition.Compose(addon.OnClick, e =>
        {
            if (!e.DefaultPrevented) FocusControl(e);
        });
        return addon;
    }

    static void FocusControl(PointerEvent e)
    {
        if (e.CurrentTarget is not { } addon) return;
        // A nested button handles its own activation.
        for (var target = e.Target; target is not null; target = target.Parent)
        {
            if (ReferenceEquals(target, addon)) break;
            if (target is ButtonElement || target.Role == Role.Button) return;
        }
        Element? group = addon;
        while (group is not null && !string.Equals(group.GetAttribute("data-slot"), "input-group", StringComparison.Ordinal))
            group = group.Parent;
        group ??= addon.Parent;
        if (group is null) return;
        var pending = new Stack<Element>();
        pending.Push(group);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (element is InputElement && element.IsFocusable && !element.IsDisabled)
            {
                element.Focus(new FocusOptions { Origin = FocusOrigin.Pointer });
                return;
            }
            var children = element.Children;
            for (int index = children.Count - 1; index >= 0; index--) pending.Push(children[index]);
        }
    }
}

/// <summary>A compact button inside an input group.</summary>
public sealed partial class InputGroupButton : Component
{
    const string ButtonBase =
        "inline-flex shrink-0 items-center justify-center gap-2 rounded-md text-sm font-medium whitespace-nowrap transition-all outline-none select-none " +
        "focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 active:not-aria-[haspopup]:translate-y-px " +
        "disabled:pointer-events-none disabled:opacity-50 " +
        "aria-invalid:border-destructive aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 " +
        "[&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4 " +
        "flex items-center gap-2 text-sm shadow-none";

    static readonly Cva<ButtonVariant, InputGroupButtonSize> Variants = new(
        ButtonBase,
        new Dictionary<ButtonVariant, string>
        {
            [ButtonVariant.Default] = "bg-primary text-primary-foreground hover:bg-primary/90",
            [ButtonVariant.Destructive] = "bg-destructive text-white hover:bg-destructive/90 focus-visible:ring-destructive/20 dark:bg-destructive/60 dark:focus-visible:ring-destructive/40",
            [ButtonVariant.Outline] = "border bg-background shadow-xs hover:bg-accent hover:text-accent-foreground dark:border-input dark:bg-input/30 dark:hover:bg-input/50",
            [ButtonVariant.Secondary] = "bg-secondary text-secondary-foreground hover:bg-secondary/80",
            [ButtonVariant.Ghost] = "hover:bg-accent hover:text-accent-foreground dark:hover:bg-accent/50",
            [ButtonVariant.Link] = "text-primary underline-offset-4 hover:underline",
        },
        new Dictionary<InputGroupButtonSize, string>
        {
            [InputGroupButtonSize.Xs] = "h-6 gap-1 rounded-[calc(var(--radius)-5px)] px-2 has-[>svg]:px-2 [&>svg:not([class*='size-'])]:size-3.5",
            [InputGroupButtonSize.Sm] = "h-8 gap-1.5 rounded-md px-2.5 has-[>svg]:px-2.5",
            [InputGroupButtonSize.IconXs] = "size-6 rounded-[calc(var(--radius)-5px)] p-0 has-[>svg]:p-0",
            [InputGroupButtonSize.IconSm] = "size-8 p-0 has-[>svg]:p-0",
        });

    public ButtonVariant Variant { get; set; } = ButtonVariant.Ghost;
    public InputGroupButtonSize Size { get; set; } = InputGroupButtonSize.Xs;
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        bool disabled = Disabled || (TryUse<InputGroupContext>(out var group) && group.Disabled);
        var button = StaticParts.Style(new UIButton
        {
            Disabled = disabled,
            Ref = Ref,
            OnClick = OnClick,
            Children = Children ?? new NodeList(),
        }, "input-group-button", Variants.Of(Variant, Size), Class, Rest);
        button.Attributes["type"] ??= "button";
        button.Attributes["data-variant"] ??= VariantName(Variant);
        button.Attributes["data-size"] ??= SizeName(Size);
        if (disabled) button.Attributes["aria-disabled"] = "true";
        return button;
    }

    static string VariantName(ButtonVariant variant) => variant.ToString().ToLowerInvariant();

    static string SizeName(InputGroupButtonSize size) => size switch
    {
        InputGroupButtonSize.Sm => "sm",
        InputGroupButtonSize.IconXs => "icon-xs",
        InputGroupButtonSize.IconSm => "icon-sm",
        _ => "xs",
    };
}

/// <summary>Non-interactive text inside an input group.</summary>
public sealed partial class InputGroupText : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Span { Children = Children ?? new NodeList() }, "input-group-text",
        "flex items-center gap-2 text-sm text-muted-foreground [&_svg]:pointer-events-none [&_svg:not([class*='size-'])]:size-4",
        Class, Rest);
}

/// <summary>Single-line editor that fills an input group.</summary>
public sealed partial class InputGroupInput : Component
{
    public string? Value { get; set; }
    public Action<string>? ValueChanged { get; set; }
    public string? Id { get; set; }
    public string? InputType { get; set; }
    public string? Placeholder { get; set; }
    public bool Disabled { get; set; }
    public bool ReadOnly { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<InputEvent>? OnInput { get; set; }
    public Action<InputEvent>? OnChange { get; set; }
    public Action<SubmitEvent>? OnSubmit { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        bool groupDisabled = TryUse<InputGroupContext>(out var group) && group.Disabled;
        var input = new UIInput
        {
            Id = Id,
            InputType = InputType,
            Value = Value,
            Placeholder = Placeholder,
            Disabled = Disabled || groupDisabled,
            ReadOnly = ReadOnly,
            Ref = Ref,
            OnInput = OnInput,
            OnChange = OnChange,
            OnSubmit = OnSubmit,
        };
        input.Attributes["data-slot"] = "input-group-control";
        input.MergeAttributes(Rest);
        input.Class = TailwindMerge.Cn("flex-1 rounded-none border-0 bg-transparent shadow-none focus-visible:ring-0 dark:bg-transparent",
            input.Class.Text, Class);
        if (ValueChanged is { } changed)
        {
            var handler = input.OnInput;
            input.OnInput = e =>
            {
                changed(e.Value);
                handler?.Invoke(e);
            };
        }
        return input;
    }
}

/// <summary>Multiline editor that fills an input group.</summary>
public sealed partial class InputGroupTextarea : Component
{
    public string? Value { get; set; }
    public Action<string>? ValueChanged { get; set; }
    public string? Id { get; set; }
    public string? Placeholder { get; set; }
    public bool Disabled { get; set; }
    public bool ReadOnly { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<InputEvent>? OnInput { get; set; }
    public Action<InputEvent>? OnChange { get; set; }
    public Action<SubmitEvent>? OnSubmit { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        bool groupDisabled = TryUse<InputGroupContext>(out var group) && group.Disabled;
        var input = new UITextArea
        {
            Id = Id,
            Value = Value,
            Placeholder = Placeholder,
            Disabled = Disabled || groupDisabled,
            ReadOnly = ReadOnly,
            Ref = Ref,
            OnInput = OnInput,
            OnChange = OnChange,
            OnSubmit = OnSubmit,
        };
        input.Attributes["data-slot"] = "input-group-control";
        input.MergeAttributes(Rest);
        input.Class = TailwindMerge.Cn(
            "flex-1 resize-none rounded-none border-0 bg-transparent py-3 shadow-none focus-visible:ring-0 dark:bg-transparent",
            input.Class.Text, Class);
        if (ValueChanged is { } changed)
        {
            var handler = input.OnInput;
            input.OnInput = e =>
            {
                changed(e.Value);
                handler?.Invoke(e);
            };
        }
        return input;
    }
}
