using System;
using System.Globalization;
using System.Threading;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Platform;
using AwAUI.Svg;
using AwAUI.UI;
using PopoverPrimitive = AwAUI.Controls.Primitives.Popover;

namespace AwAUI.Shadcn;

/// <summary>A single selection. Compose one <see cref="SelectTrigger"/> and one <see cref="SelectContent"/>.</summary>
public sealed partial class Select : Component
{
    readonly SelectScope _scope = new();
    readonly ControllableState<bool> _open = new();
    bool _wasOpen;

    /// <summary>Null leaves selection uncontrolled; an empty string is a controlled empty selection.</summary>
    public string? Value { get; set; }
    public string DefaultValue { get; set; } = "";
    public Action<string>? ValueChanged { get; set; }
    public Action<string>? OnValueChange { get; set; }
    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public Action<bool>? OpenChanged { get; set; }
    public Action<bool>? OnOpenChange { get; set; }
    public bool Disabled { get; set; }
    /// <summary>Marks the combobox as required for accessibility.</summary>
    public bool Required { get; set; }
    public NodeList? Children { get; set; }

    protected override Node Build()
    {
        string value = _scope.Selection.Sync(Value is not null, Value ?? "", DefaultValue, ValueChanged + OnValueChange);
        bool requestedOpen = _open.Sync(Open, DefaultOpen && !Disabled, OpenChanged + OnOpenChange);
        bool open = !Disabled && requestedOpen;
        if (open != _wasOpen) _scope.Search.Reset();
        _wasOpen = open;

        // A disabled uncontrolled select stays closed when it becomes enabled again.
        UseEffect(() =>
        {
            if (Disabled && !_open.IsControlled) _open.Set(false);
            return null;
        }, Disabled, Open.HasValue);

        return Provide(new SelectContext(_scope, value, Disabled, Required), new PopoverPrimitive.Root
        {
            Open = open,
            OnOpenChange = SetOpen,
            Children = Children,
        });
    }

    void SetOpen(bool open)
    {
        if (!open || !Disabled) _open.Set(open);
    }
}

internal sealed class SelectScope
{
    public readonly ControllableState<string> Selection = new(StringComparer.Ordinal);
    public readonly State<(string? Value, string? Label)> SelectedLabel = new((null, null));
    public readonly TypeAhead Search = new();
    public string TriggerId { get; set; } = SelectParts.NextId("trigger");
    public bool OpenAtEnd { get; set; }
    public SelectContent? Content;
}

internal readonly record struct SelectContext(SelectScope Scope, string Value, bool Disabled, bool Required);

internal static class SelectParts
{
    static long s_nextId;

    public const KeyModifiers CommandModifiers = KeyModifiers.Alt | KeyModifiers.Control | KeyModifiers.Meta;

    internal static string NextId(string part) =>
        "aw-select-" + part + "-" + Interlocked.Increment(ref s_nextId).ToString(CultureInfo.InvariantCulture);

    internal static T Style<T>(T node, string slot, string defaults, string? classes, AttributeDictionary? rest)
        where T : ElementNode
    {
        node.MergeAttributes(DialogAttributes.ForPart(slot, rest, classes, defaults));
        return node;
    }

    internal static string Bool(bool value) => value ? "true" : "false";

    internal static SvgDocument ChevronDown => LucideIcons.ChevronDown;
    internal static SvgDocument ChevronUp => LucideIcons.ChevronUp;
    internal static SvgDocument Check => LucideIcons.Check;

    internal static UI.Svg Glyph(SvgDocument document, string slot, string classes)
    {
        var glyph = new UI.Svg(document) { Class = classes };
        glyph.Attributes["aria-hidden"] = "true";
        glyph.Attributes["data-slot"] = slot;
        return glyph;
    }
}
