using System;
using AwAUI.Controls.Primitives;
using AwAUI.Icons.Lucide;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Shadcn;

public sealed partial class CommandInput : Component
{
    const string WrapperClass = "flex h-9 items-center gap-2 border-b px-3";
    const string InputClass =
        "flex h-10 w-full rounded-md bg-transparent py-3 text-sm outline-hidden placeholder:text-muted-foreground " +
        "disabled:cursor-not-allowed disabled:opacity-50";

    public string? Placeholder { get; set; }
    public bool Disabled { get; set; }
    public string? Class { get; set; }
    public Ref<Element>? Ref { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<CommandScope>();
        var wrapper = TabsParts.Style(new Div(), "command-input-wrapper", WrapperClass, null, null);
        wrapper.Children.Add(SelectParts.Glyph(LucideIcons.Search, "command-input-icon", "size-4 shrink-0 opacity-50"));

        var input = TabsParts.Style(new UI.Input
        {
            Value = scope.Query.Value,
            Placeholder = Placeholder,
            Disabled = Disabled,
            Ref = Ref,
        }, "command-input", InputClass, Class, Rest);

        // The listbox and active option live on the input so focus never leaves the search field.
        input.Role = Role.ComboBox;
        input.Attributes["aria-autocomplete"] = "list";
        input.Attributes["aria-expanded"] = "true";
        input.Attributes["aria-controls"] = scope.ListId;
        input.Attributes["aria-activedescendant"] = scope.ActiveId;
        input.Attributes["autocomplete"] = "off";
        input.Attributes["spellcheck"] = "false";

        input.OnInput = EventComposition.Chain(input.OnInput, e =>
        {
            if (!e.DefaultPrevented && !Disabled) scope.SetQuery(e.Value);
        });
        input.OnKeyDown = EventComposition.Chain(input.OnKeyDown, e => HandleKey(scope, e));

        wrapper.Children.Add(input);
        return wrapper;
    }

    void HandleKey(CommandScope scope, KeyEvent e)
    {
        if (Disabled || e.DefaultPrevented) return;
        switch (e.Key)
        {
            case "ArrowDown":
                e.PreventDefault();
                scope.Move(1);
                break;
            case "ArrowUp":
                e.PreventDefault();
                scope.Move(-1);
                break;
            case "Home":
                e.PreventDefault();
                scope.MoveToEdge(last: false);
                break;
            case "End":
                e.PreventDefault();
                scope.MoveToEdge(last: true);
                break;
            case "Enter":
                if (scope.ActivateActive()) e.PreventDefault();
                break;
        }
    }
}

public sealed partial class CommandList : Component
{
    const string ListClass = "max-h-[300px] scroll-py-1 overflow-x-hidden overflow-y-auto";

    public string? Id { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<CommandScope>();
        var node = TabsParts.Style(new Div { Children = Children ?? new NodeList() },
            "command-list", ListClass, Class, Rest);
        node.Id = Id;
        if (!scope.SuppressListRole)
        {
            node.Role = Role.ListBox;
            node.Id ??= scope.ListId;
        }
        return node;
    }
}

/// <summary>Shown only while the current query matches no command item.</summary>
public sealed partial class CommandEmpty : Component
{
    const string EmptyClass = "py-6 text-center text-sm";

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<CommandScope>();
        if (scope.Visible.Count != 0) return new Fragment();
        return TabsParts.Style(new Div { Role = Role.Presentation, Children = Children ?? new NodeList() },
            "command-empty", EmptyClass, Class, Rest);
    }
}

public sealed partial class CommandGroup : Component
{
    const string GroupClass =
        "overflow-hidden p-1 text-foreground [&_[data-slot=command-group-heading]]:px-2 " +
        "[&_[data-slot=command-group-heading]]:py-1.5 [&_[data-slot=command-group-heading]]:text-xs " +
        "[&_[data-slot=command-group-heading]]:font-medium [&_[data-slot=command-group-heading]]:text-muted-foreground";
    const string HeadingClass = "px-2 py-1.5 text-xs font-medium text-muted-foreground";

    readonly string _headingId = TabsParts.NextId("command-group-heading");

    /// <summary>Optional heading rendered above the items.</summary>
    public string? Heading { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<CommandScope>();
        string query = scope.Query.Value;
        var items = CommandTree.CollectItems(Children);
        if (items.Count > 0)
        {
            bool anyVisible = false;
            foreach (var item in items)
            {
                if (!CommandTree.Matches(item, query)) continue;
                anyVisible = true;
                break;
            }
            if (!anyVisible) return new Fragment();
        }

        var children = new NodeList();
        if (Heading is not null)
        {
            var heading = TabsParts.Style(new Div { Children = [Heading] },
                "command-group-heading", HeadingClass, null, null);
            heading.Id = _headingId;
            children.Add(heading);
        }
        children.AddRange(Children);

        var node = TabsParts.Style(new Div { Role = Role.Group, Children = children },
            "command-group", GroupClass, Class, Rest);
        if (Heading is not null) node.Attributes["aria-labelledby"] = _headingId;
        return node;
    }
}

public sealed partial class CommandSeparator : Component
{
    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() =>
        TabsParts.Style(new Div { Role = Role.Separator }, "command-separator", "-mx-1 h-px bg-border", Class, Rest);
}

public sealed partial class CommandItem : Component
{
    const string ItemClass =
        "relative flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm outline-hidden select-none " +
        "data-[disabled=true]:pointer-events-none data-[disabled=true]:opacity-50 " +
        "data-[selected=true]:bg-accent data-[selected=true]:text-accent-foreground " +
        "[&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4 " +
        "[&_svg:not([class*='text-'])]:text-muted-foreground";

    readonly string _generatedId = TabsParts.NextId("command-item");

    /// <summary>Filter value used instead of the visible text. Also the value committed by a combobox.</summary>
    public string? Value { get; set; }

    /// <summary>Extra words that also match the query.</summary>
    public string? Keywords { get; set; }

    public bool Disabled { get; set; }
    public string? Id { get; set; }
    public string? Class { get; set; }
    public Action<PointerEvent>? OnSelect { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    internal int Order { get; set; }

    internal string ElementId => Id ?? _generatedId;

    internal string SelectionValue => string.IsNullOrEmpty(Value) ? CommandTree.Text(this) : Value!;

    protected override Node Build()
    {
        var scope = Use<CommandScope>();
        string query = scope.Query.Value;
        bool matches = CommandTree.Matches(this, query);
        bool active = string.Equals(scope.ActiveId, ElementId, StringComparison.Ordinal);

        if (!matches) return new Fragment();

        var button = TabsParts.Style(new Div
        {
            Id = ElementId,
            Role = Role.Option,
            Children = Children ?? new NodeList(),
        }, "command-item", ItemClass, Class, Rest);

        button.Attributes["data-selected"] = TabsParts.Bool(active);
        button.Attributes["data-disabled"] = Disabled ? "true" : null;
        button.Attributes["data-value"] = CommandTree.FilterText(this);
        button.Attributes["aria-selected"] = TabsParts.Bool(active);
        button.Attributes["aria-disabled"] = Disabled ? "true" : null;

        button.OnPointerDown = EventComposition.Chain(button.OnPointerDown, e =>
        {
            // Keep focus in the search input: a non-focusable press target would otherwise clear it.
            if (!e.DefaultPrevented && !Disabled) e.PreventDefault();
        });
        button.OnPointerMove = EventComposition.Chain(button.OnPointerMove, e =>
        {
            if (!e.DefaultPrevented && !Disabled && e.PointerType == PointerType.Mouse)
                scope.SetActive(ElementId);
        });
        button.OnClick = EventComposition.Chain(button.OnClick, e =>
        {
            if (e.DefaultPrevented || Disabled) return;
            Activate(e);
        });
        return button;
    }

    internal bool Activate(PointerEvent? pointer = null)
    {
        if (Disabled) return false;
        var e = pointer ?? new PointerEvent("click");
        OnSelect?.Invoke(e);
        if (!e.DefaultPrevented && TryUse<ComboboxScope>(out var combobox))
            combobox.Commit(SelectionValue, CommandTree.Text(this));
        return true;
    }
}

public sealed partial class CommandShortcut : Component
{
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() =>
        TabsParts.Style(new Span { Children = Children ?? new NodeList() },
            "command-shortcut", "ml-auto text-xs tracking-widest text-muted-foreground", Class, Rest);
}
