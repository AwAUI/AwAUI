using System;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using PopoverPrimitive = AwAUI.Controls.Primitives.Popover;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

/// <summary>Height of a <see cref="NativeSelect"/> trigger.</summary>
public enum NativeSelectSize
{
    Default,
    Sm,
}

/// <summary>
/// A select that looks and behaves like a native HTML <c>&lt;select&gt;</c>. The visual trigger and popup are the
/// regular <see cref="Select"/> primitives; the options are described with <see cref="NativeSelectOption"/> and
/// <see cref="NativeSelectOptGroup"/>, which this component flattens into <see cref="SelectItem"/>,
/// <see cref="SelectGroup"/> and <see cref="SelectLabel"/> nodes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Platform limitation:</b> AwAUI has no native platform <c>&lt;select&gt;</c> element, so this is not an OS-native
/// dropdown. It emulates the native HTML select as closely as the framework allows: the closed control is a
/// <c>role="combobox"</c> button and the open list is a <c>role="listbox"</c> popup built from the regular
/// <see cref="Select"/> primitives. Typeahead, Home/End, Arrow keys, Enter/Space, Escape and Alt+ArrowDown all follow
/// native HTML select behavior, and disabled options and optgroups are skipped.
/// </para>
/// <para>
/// Because there is no native form control, the browser's own form submission cannot see this value. <see cref="Name"/>
/// is still exposed on the trigger as <c>name</c> (and mirrored as <c>data-name</c>) for accessible name computation and
/// for form/validation integrations, and <see cref="Required"/> / <see cref="Invalid"/> are exposed as
/// <c>aria-required</c> / <c>aria-invalid</c>.
/// </para>
/// </remarks>
public sealed partial class NativeSelect : Component
{
    const string WrapperClass = "group/native-select relative w-fit has-[button:disabled]:opacity-50";

    /// <summary>Controlled selection. Null leaves selection uncontrolled.</summary>
    public string? Value { get; set; }

    /// <summary>Selection used before the user picks an option (uncontrolled).</summary>
    public string DefaultValue { get; set; } = "";

    /// <summary>Receives each selection and supports <c>@bind-Value</c>.</summary>
    public Action<string>? ValueChanged { get; set; }

    /// <summary>Alias for <see cref="ValueChanged"/>.</summary>
    public Action<string>? OnValueChange { get; set; }

    /// <summary>Disables the trigger and prevents the popup from opening.</summary>
    public bool Disabled { get; set; }

    /// <summary>Marks the control as required for accessibility (<c>aria-required="true"</c>).</summary>
    public bool Required { get; set; }

    /// <summary>
    /// Form field name. AwAUI has no native platform select or form submission, so this is exposed on the trigger as
    /// the <c>name</c> attribute and mirrored as <c>data-name</c> for accessible name computation and form integrations.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>Marks the control invalid (<c>aria-invalid="true"</c> and the destructive border styles).</summary>
    public bool Invalid { get; set; }

    /// <summary>Trigger height.</summary>
    public NativeSelectSize Size { get; set; }

    /// <summary>Extra classes for the trigger button.</summary>
    public string? Class { get; set; }

    /// <summary>Option descriptions; non-description children are ignored.</summary>
    public NodeList? Children { get; set; }

    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        // A fresh carrier each build: the reconciler adopts these props into the live Select, whose private
        // scope keeps the selection while NativeSelect's own props change.
        var select = new Select
        {
            Value = Value,
            DefaultValue = DefaultValue,
            ValueChanged = ValueChanged,
            OnValueChange = OnValueChange,
            Disabled = Disabled,
            Required = Required,
            Children =
            [
                new NativeSelectTrigger
                {
                    Size = Size,
                    Class = Class,
                    Rest = Rest,
                    Name = Name,
                    Invalid = Invalid,
                    Placeholder = Placeholder(Children),
                },
                new SelectContent { Children = Flatten(Children) },
            ],
        };

        var wrapper = StaticParts.Style(new Div(), "native-select-wrapper", WrapperClass, null, null);
        wrapper.Children.Add(select);
        wrapper.Children.Add(SelectParts.Glyph(SelectParts.ChevronDown, "native-select-icon",
            "pointer-events-none absolute top-1/2 right-3.5 size-4 -translate-y-1/2 text-muted-foreground opacity-50 select-none"));
        return wrapper;
    }

    static NodeList Flatten(NodeList? children)
    {
        var result = new NodeList();
        if (children is null) return result;
        foreach (var child in children)
        {
            switch (child)
            {
                case NativeSelectOption option:
                    result.Add(Option(option, disabled: false));
                    break;
                case NativeSelectOptGroup group:
                    result.Add(Group(group, disabled: false));
                    break;
            }
        }
        return result;
    }

    /// <summary>The label of the option with an empty value, shown while nothing is selected (like a native select).</summary>
    static string? Placeholder(NodeList? children)
    {
        if (children is null) return null;
        foreach (var child in children)
        {
            if (child is NativeSelectOption { Value.Length: 0 } option)
                return Label(option.Children);
            if (child is NativeSelectOptGroup group && group.Children is not null)
                if (Placeholder(group.Children) is { } nested) return nested;
        }
        return null;
    }

    static string? Label(NodeList? children)
    {
        if (children is null) return null;
        foreach (var child in children)
            if (child is AwAUI.UI.Text text) return text.Content;
        return null;
    }

    static Div Option(NativeSelectOption option, bool disabled)
    {
        var item = new SelectItem
        {
            Value = option.Value,
            TextValue = option.TextValue,
            Disabled = disabled || option.Disabled,
            Children = option.Children,
            Class = option.Class,
            Rest = option.Rest,
            AllowEmptyValue = option.Value.Length == 0,
        };
        return Slot("native-select-option", item);
    }

    static Div Group(NativeSelectOptGroup group, bool disabled)
    {
        var items = new NodeList();
        if (!string.IsNullOrEmpty(group.Label))
        {
            // The optgroup label names its group but is not an option: role="presentation" keeps it out of the
            // listbox's option set while SelectGroup links it through aria-labelledby.
            var label = new SelectLabel
            {
                Children = new NodeList { group.Label },
                Rest = new AttributeDictionary { ["role"] = "presentation" },
            };
            items.Add(Slot("native-select-label", label));
        }
        bool groupDisabled = disabled || group.Disabled;
        if (group.Children is not null)
            foreach (var child in group.Children)
                if (child is NativeSelectOption option) items.Add(Option(option, groupDisabled));

        return Slot("native-select-optgroup", new SelectGroup
        {
            Children = items,
            Class = group.Class,
            Rest = group.Rest,
        });
    }

    static Div Slot(string slot, Node child)
    {
        var wrapper = new Div { Role = Role.Presentation, Children = [child] };
        wrapper.Attributes["data-slot"] = slot;
        return wrapper;
    }
}

/// <summary>An option of a <see cref="NativeSelect"/>. Consumed by the parent; it never renders on its own.</summary>
public sealed partial class NativeSelectOption : Component
{
    public string Value { get; set; } = "";

    /// <summary>Overrides typeahead text. Also supplies the label if the option has no text.</summary>
    public string? TextValue { get; set; }

    public bool Disabled { get; set; }

    public NodeList? Children { get; set; }

    public string? Class { get; set; }

    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new Fragment();
}

/// <summary>A labelled group of <see cref="NativeSelectOption"/>s inside a <see cref="NativeSelect"/>.</summary>
public sealed partial class NativeSelectOptGroup : Component
{
    public string? Label { get; set; }

    public bool Disabled { get; set; }

    public NodeList? Children { get; set; }

    public string? Class { get; set; }

    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => new Fragment();
}

/// <summary>The native-looking trigger button. Builds inside the inner <see cref="Select"/>'s context.</summary>
internal sealed partial class NativeSelectTrigger : Component
{
    const string TriggerClass =
        "h-9 w-full min-w-0 appearance-none rounded-md border border-input bg-transparent px-3 py-2 pr-9 text-sm shadow-xs " +
        "transition-[color,box-shadow] outline-none selection:bg-primary selection:text-primary-foreground " +
        "placeholder:text-muted-foreground disabled:pointer-events-none disabled:cursor-not-allowed " +
        "data-[size=sm]:h-8 data-[size=sm]:py-1 dark:bg-input/30 dark:hover:bg-input/50 " +
        "focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 " +
        "aria-invalid:border-destructive aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40";

    public NativeSelectSize Size { get; set; }

    public string? Class { get; set; }

    public AttributeDictionary? Rest { get; set; }

    /// <summary>Form field name exposed as <c>name</c> / <c>data-name</c> on the trigger.</summary>
    public string? Name { get; set; }

    /// <summary>Marks the trigger invalid (<c>aria-invalid</c>).</summary>
    public bool Invalid { get; set; }

    /// <summary>Label of the empty option, shown in the trigger while nothing is selected.</summary>
    public string? Placeholder { get; set; }

    protected override Node Build()
    {
        var ctx = Use<SelectContext>();
        var popover = Use<PopoverPrimitive.Context>();
        // Native HTML selects reset their typeahead buffer after about 500 ms; the shared Select default is 1 s.
        ctx.Scope.Search.ResetDelay = NativeSelectTypeAheadDelay;
        var button = SelectParts.Style(new UIButton { Role = Role.ComboBox, Disabled = ctx.Disabled },
            "native-select", TriggerClass, Class, Rest);
        button.Id ??= ctx.Scope.TriggerId;
        button.Attributes["type"] = "button";
        if (!string.IsNullOrEmpty(Name))
        {
            button.Attributes["name"] = Name;
            button.Attributes["data-name"] = Name;
        }
        button.Attributes["aria-haspopup"] = "listbox";
        button.Attributes["aria-expanded"] = SelectParts.Bool(popover.Open);
        button.Attributes["aria-controls"] = popover.Shared.ContentId;
        button.Attributes["aria-required"] = SelectParts.Bool(ctx.Required);
        button.Attributes["aria-invalid"] = Invalid ? "true" : null;
        button.Attributes["aria-disabled"] = SelectParts.Bool(button.Disabled);
        button.Attributes["data-state"] = popover.Open ? "open" : "closed";
        button.Attributes["data-disabled"] = button.Disabled ? "" : null;
        button.Attributes["data-invalid"] = Invalid ? "" : null;
        button.Attributes["data-placeholder"] = ctx.Value.Length == 0 ? "" : null;
        button.Attributes["data-size"] = Size == NativeSelectSize.Sm ? "sm" : "default";
        ctx.Scope.TriggerId = button.Id!;
        button.OnPointerDown = EventComposition.Compose(button.OnPointerDown, _ => ctx.Scope.OpenAtEnd = false);
        button.OnKeyDown = EventComposition.Compose(button.OnKeyDown, e =>
        {
            if (!button.Disabled) HandleKey(e, ctx.Scope, popover.Shared);
        });
        button.Children = [new SelectValue { Placeholder = Placeholder }];
        return new PopoverPrimitive.Trigger { AsChild = true, Children = [button] };
    }

    /// <summary>Native HTML selects reset their typeahead buffer after about 500 ms.</summary>
    internal static readonly TimeSpan NativeSelectTypeAheadDelay = TimeSpan.FromMilliseconds(500);

    static void HandleKey(KeyEvent e, SelectScope scope, OpenableShared popup)
    {
        if (e.DefaultPrevented || popup.IsOpen) return;
        bool noCommand = (e.Modifiers & SelectParts.CommandModifiers) == 0;
        // Native HTML select: Alt+ArrowDown opens the popup (without moving focus to the end).
        if (e.Key == "ArrowDown"
            && (e.Modifiers & KeyModifiers.Alt) != 0
            && (e.Modifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift)) == 0)
        {
            e.PreventDefault();
            scope.OpenAtEnd = false;
            if (!e.IsRepeat) popup.SetOpen(true);
            return;
        }
        switch (e.Key)
        {
            case "Home" or "End" when noCommand:
                // Closed native select: Home/End move the selection to the first/last enabled option.
                e.PreventDefault();
                scope.Content?.MoveSelectionEdge(end: e.Key == "End");
                return;
            case "ArrowDown" or "ArrowUp" when
                (e.Modifiers & (SelectParts.CommandModifiers | KeyModifiers.Shift)) == 0:
                e.PreventDefault();
                scope.OpenAtEnd = e.Key == "ArrowUp";
                if (!e.IsRepeat) popup.SetOpen(true);
                return;
            case "Enter":
            case " " when !scope.Search.IsTypingAhead(e.Timestamp):
                // The native button dispatches click for these keys.
                if (e.IsRepeat || (e.Modifiers & SelectParts.CommandModifiers) != 0) e.PreventDefault();
                return;
        }
        if (!TypeAhead.IsTypeAheadKey(e.Key, e.Modifiers)) return;
        e.PreventDefault();
        scope.Content?.FindTypedOption(e.Key, e.Timestamp, open: false);
    }
}
