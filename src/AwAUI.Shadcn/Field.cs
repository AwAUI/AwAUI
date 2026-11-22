using System;
using System.Collections.Generic;
using AwAUI.Controls.Primitives;
using AwAUI.Tailwind;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>Layout of a <see cref="Field"/> label and control.</summary>
public enum FieldOrientation { Vertical, Horizontal, Responsive }

/// <summary>Typography of a <see cref="FieldLegend"/>.</summary>
public enum FieldLegendVariant { Legend, Label }

/// <summary>A labelled form control with optional description and error. Composes with <see cref="FieldGroup"/> and <see cref="FieldSet"/>.</summary>
public sealed partial class Field : Component
{
    static readonly Cva<FieldOrientation> Variants = new(
        "group/field flex w-full gap-3 data-[invalid=true]:text-destructive",
        new Dictionary<FieldOrientation, string>
        {
            [FieldOrientation.Vertical] = "flex-col [&>*]:w-full [&>.sr-only]:w-auto",
            [FieldOrientation.Horizontal] =
                "flex-row items-center [&>[data-slot=field-label]]:flex-auto " +
                "has-[>[data-slot=field-content]]:items-start has-[>[data-slot=field-content]]:[&>[role=checkbox],[role=radio]]:mt-px",
            [FieldOrientation.Responsive] =
                "flex-col @md/field-group:flex-row @md/field-group:items-center [&>*]:w-full @md/field-group:[&>*]:w-auto [&>.sr-only]:w-auto " +
                "@md/field-group:[&>[data-slot=field-label]]:flex-auto " +
                "@md/field-group:has-[>[data-slot=field-content]]:items-start " +
                "@md/field-group:has-[>[data-slot=field-content]]:[&>[role=checkbox],[role=radio]]:mt-px",
        });

    public FieldOrientation Orientation { get; set; }
    public bool Invalid { get; set; }
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var field = StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "field",
            Variants.Of(Orientation), Class, Rest);
        if (field.Role == Role.None) field.Role = Role.Group;
        field.Attributes["data-orientation"] ??= (Orientation switch
        {
            FieldOrientation.Horizontal => "horizontal",
            FieldOrientation.Responsive => "responsive",
            _ => "vertical",
        });
        field.Attributes["data-invalid"] ??= Invalid ? "true" : null;
        field.Attributes["data-disabled"] ??= Disabled ? "true" : null;
        return field;
    }
}

/// <summary>Groups fields with a shared <see cref="FieldLegend"/>.</summary>
public sealed partial class FieldSet : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var set = StaticParts.Style(new StaticParts.TagElement("fieldset") { Children = Children ?? new NodeList() }, "field-set",
            "flex flex-col gap-6 has-[>[data-slot=checkbox-group]]:gap-3 has-[>[data-slot=radio-group]]:gap-3", Class, Rest);
        if (set.Role == Role.None) set.Role = Role.Group;
        return set;
    }
}

/// <summary>The accessible name of a <see cref="FieldSet"/>.</summary>
public sealed partial class FieldLegend : Component
{
    public FieldLegendVariant Variant { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var legend = StaticParts.Style(new StaticParts.TagElement("legend") { Children = Children ?? new NodeList() },
            "field-legend", "mb-3 font-medium data-[variant=legend]:text-base data-[variant=label]:text-sm", Class, Rest);
        legend.Attributes["data-variant"] ??= Variant == FieldLegendVariant.Label ? "label" : "legend";
        return legend;
    }
}

/// <summary>Stacks related fields; container queries respond to its width for responsive fields.</summary>
public sealed partial class FieldGroup : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "field-group",
        "group/field-group @container/field-group flex w-full flex-col gap-7 data-[slot=checkbox-group]:gap-3 [&>[data-slot=field-group]]:gap-4",
        Class, Rest);
}

/// <summary>The control column of a field.</summary>
public sealed partial class FieldContent : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "field-content",
        "group/field-content flex flex-1 flex-col gap-1.5 leading-snug", Class, Rest);
}

/// <summary>A label that focuses its associated control when clicked. Set <see cref="For"/> to the control's ID.</summary>
public sealed partial class FieldLabel : Component
{
    public string? Id { get; set; }
    public string? For { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    public Action<PointerEvent>? OnClick { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var label = StaticParts.Style(new StaticParts.TagElement("label")
        {
            Id = Id,
            Ref = Ref,
            OnClick = OnClick,
            Children = Children ?? new NodeList(),
        }, "field-label",
            // FieldLabel renders the Label component, so it inherits the label typography as well.
            "group/field-label peer/field-label flex w-fit items-center gap-2 text-sm font-medium leading-snug select-none " +
            "group-data-[disabled=true]/field:opacity-50 peer-disabled:cursor-not-allowed peer-disabled:opacity-50 " +
            "has-[>[data-slot=field]]:w-full has-[>[data-slot=field]]:flex-col has-[>[data-slot=field]]:rounded-md " +
            "has-[>[data-slot=field]]:border [&>*]:data-[slot=field]:p-4 " +
            "has-data-[state=checked]:border-primary has-data-[state=checked]:bg-primary/5 dark:has-data-[state=checked]:bg-primary/10",
            Class, Rest);
        if (For is not null) label.Attributes["for"] = For;
        label.OnClick = EventComposition.Compose(label.OnClick, e =>
        {
            if (!e.DefaultPrevented) FocusControl(e);
        });
        return label;
    }

    static void FocusControl(PointerEvent e)
    {
        if (e.CurrentTarget is not { } label) return;
        // A nested interactive element handles its own click.
        for (var target = e.Target; target is not null; target = target.Parent)
        {
            if (ReferenceEquals(target, label)) break;
            if (target.IsFocusable || target.CanBeDisabled || target.Role == Role.Link) return;
        }
        if (label.GetAttribute("for") is not { Length: > 0 } id) return;
        if (label.Root is not { } root) return;
        var pending = new Stack<Element>();
        pending.Push(root.RootElement);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            if (string.Equals(element.Id, id, StringComparison.Ordinal))
            {
                if (!element.IsFocusable || element.IsDisabled) return;
                if (element.Focus(new FocusOptions { Origin = FocusOrigin.Pointer }) && element is ButtonElement)
                    root.Input.Activate(element);
                return;
            }
            var children = element.Children;
            for (int index = children.Count - 1; index >= 0; index--) pending.Push(children[index]);
        }
    }
}

/// <summary>A non-editable title for a group of fields.</summary>
public sealed partial class FieldTitle : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new Div { Children = Children ?? new NodeList() }, "field-label",
        "flex w-fit items-center gap-2 text-sm leading-snug font-medium group-data-[disabled=true]/field:opacity-50", Class, Rest);
}

/// <summary>Helper text below a field's control.</summary>
public sealed partial class FieldDescription : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => StaticParts.Style(new StaticParts.TagElement("p") { Children = Children ?? new NodeList() },
        "field-description",
        "text-sm leading-normal font-normal text-muted-foreground group-has-[[data-orientation=horizontal]]/field:text-balance " +
        "last:mt-0 nth-last-2:-mt-1 [[data-variant=legend]+&]:-mt-1.5 [&>a]:underline [&>a]:underline-offset-4 [&>a:hover]:text-primary",
        Class, Rest);
}

/// <summary>A divider between fields, optionally labelled.</summary>
public sealed partial class FieldSeparator : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        bool hasContent = Children is { Count: > 0 };
        var content = new NodeList { new Separator { Class = "absolute inset-0 top-1/2" } };
        if (Children is { Count: > 0 } children)
            content.Add(StaticParts.Style(new Span { Children = children }, "field-separator-content",
                "relative mx-auto block w-fit bg-background px-2 text-muted-foreground", null, null));
        var separator = StaticParts.Style(new Div { Children = content }, "field-separator",
            "relative -my-2 h-5 text-sm group-data-[variant=outline]/field-group:-mb-2", Class, Rest);
        separator.Attributes["data-content"] ??= hasContent ? "true" : "false";
        return separator;
    }
}

/// <summary>Validation message(s) for a field. Exposed as an alert; renders nothing without content.</summary>
public sealed partial class FieldError : Component
{
    public NodeList? Children { get; set; }
    public IReadOnlyList<string?>? Errors { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        Node? content;
        if (Children is { Count: > 0 } children) content = new Fragment(children);
        else content = BuildErrors();
        if (content is null) return new Fragment();
        var error = StaticParts.Style(new Div { Children = [content] }, "field-error",
            "text-sm font-normal text-destructive", Class, Rest);
        if (error.Role == Role.None) error.Role = Role.Alert;
        return error;
    }

    Node? BuildErrors()
    {
        if (Errors is not { Count: > 0 } errors) return null;
        // shadcn keeps the first occurrence order of distinct messages (a Map keyed by message).
        var unique = new List<string?>(errors.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in errors)
        {
            if (message is null) unique.Add(null);
            else if (seen.Add(message)) unique.Add(message);
        }
        if (unique.Count == 0) return null;
        if (unique.Count == 1) return string.IsNullOrEmpty(unique[0]) ? null : new TextNode(unique[0]!);

        var list = new StaticParts.TagElement("ul") { Class = "ml-4 flex list-disc flex-col gap-1" };
        foreach (var message in unique)
            if (!string.IsNullOrEmpty(message))
                list.Children.Add(new StaticParts.TagElement("li") { Children = [new TextNode(message!)] });
        return list.Children.Count == 0 ? null : list;
    }
}
