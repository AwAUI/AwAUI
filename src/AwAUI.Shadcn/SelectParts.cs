using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>Groups related options. Put a <see cref="SelectLabel"/> in the group to name it.</summary>
public sealed partial class SelectGroup : Component
{
    readonly Ref<Element> _element = new();

    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() => SelectParts.Style(
        new Div { Role = Role.Group, Ref = _element, Children = Children ?? [] }, "select-group", "", Class, Rest);

    protected override void OnMount() => Root!.LayoutCompleted += LinkLabel;

    protected override void OnUnmount() => Root!.LayoutCompleted -= LinkLabel;

    void LinkLabel(UIRoot root)
    {
        if (_element.Current is not { } group) return;
        string? label = Rest?["aria-label"] is null && Rest?["aria-labelledby"] is null ? FindLabel(group) : null;
        group.SetAttributeOverride("aria-labelledby", label);
    }

    static string? FindLabel(Element parent)
    {
        foreach (var child in parent.ElementChildren)
        {
            if (child.Owner is SelectLabel) return child.Id;
            if (child.Role is Role.Group or Role.Option or Role.ComboBox) continue;
            if (FindLabel(child) is { } label) return label;
        }
        return null;
    }
}

/// <summary>A visual section label and the accessible name for its containing group.</summary>
public sealed partial class SelectLabel : Component
{
    readonly string _id = SelectParts.NextId("label");

    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var label = SelectParts.Style(new Div { Children = Children ?? [] },
            "select-label", "px-2 py-1.5 text-xs text-muted-foreground", Class, Rest);
        label.Id ??= _id;
        return label;
    }
}

/// <summary>A horizontal divider between options or groups.</summary>
public sealed partial class SelectSeparator : Component
{
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var divider = SelectParts.Style(new Div { Role = Role.Separator }, "select-separator",
            "pointer-events-none -mx-1 my-1 h-px bg-border", Class, Rest);
        divider.Attributes["aria-orientation"] = "horizontal";
        return divider;
    }
}
