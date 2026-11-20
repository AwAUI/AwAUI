using System;
using AwAUI.Controls.Primitives;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>Common element properties for the standard controls.</summary>
public abstract partial class Control : Component
{
    public string? Id { get; set; }
    public ClassValue Class { get; set; }
    public StyleValue Style { get; set; }
    public Ref<Element>? Ref { get; set; }
    public string? AriaLabel { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Attributes { get; set; }

    protected T Apply<T>(T element, string className) where T : ElementNode
    {
        element.Class = className;
        element.Id = Id;
        element.Ref = Ref;
        element.AriaLabel = AriaLabel;
        return PartProps.Apply(element, Class, Style, Attributes);
    }
}

/// <summary>A structural control whose default visual tree can be replaced by a delegate.</summary>
public abstract partial class TemplatedControl : Control
{
    public Func<TemplatedControl, Node>? Template { get; set; }
    protected sealed override Node Build() => Template?.Invoke(this) ?? BuildTemplate();
    protected abstract Node BuildTemplate();
}

/// <summary>Hosts a node, text, or a data object rendered by <see cref="ContentTemplate"/>.</summary>
public partial class ContentControl : TemplatedControl
{
    public object? Content { get; set; }
    public Func<object, Node>? ContentTemplate { get; set; }
    public NodeList? Children { get; set; }

    protected NodeList BuildContent() => Content is { } content
        ? [Present(content, ContentTemplate)]
        : Children ?? [];

    internal static Node Present(object content, Func<object, Node>? template) =>
        content is Node node ? node : template?.Invoke(content) ?? new UIText(content.ToString());

    protected override Node BuildTemplate() => Apply(new Div { Children = BuildContent() }, "aw-content-control");
}

/// <summary>A control with a header and a body, each accepting content or a template.</summary>
public sealed partial class HeaderedContentControl : ContentControl
{
    public object? Header { get; set; }
    public Func<object, Node>? HeaderTemplate { get; set; }

    protected override Node BuildTemplate()
    {
        var body = new Div { Children = BuildContent() };
        body.Attributes["part"] = "content";
        var box = new Div();
        if (Header is { } header)
        {
            var heading = new Div { Children = [Present(header, HeaderTemplate)] };
            heading.Attributes["part"] = "header";
            box.Children.Add(heading);
        }
        box.Children.Add(body);
        return Apply(box, "aw-headered-content-control");
    }
}

/// <summary>A content host with a themeable border and background.</summary>
public sealed partial class Border : ContentControl
{
    protected override Node BuildTemplate() => Apply(new Div { Children = BuildContent() }, "aw-border");
}

/// <summary>A styled paragraph which can contain inline spans.</summary>
public sealed partial class TextBlock : Control
{
    public string? Text { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build() => Apply(new UIText(Text) { Children = Children ?? [] }, "aw-textblock");
}
