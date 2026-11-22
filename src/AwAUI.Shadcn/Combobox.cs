using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using PopoverPrimitive = AwAUI.Controls.Primitives.Popover;

namespace AwAUI.Shadcn;

/// <summary>
/// An autocomplete built from a popover, a command list and a trigger input. The input stays focused while
/// the list is open; typing filters it, the arrow keys move the active option and Enter commits it.
/// <para>
/// Compose the list declaratively with <see cref="ComboboxGroup"/>, <see cref="ComboboxItem"/>,
/// <see cref="ComboboxSeparator"/> and <see cref="ComboboxEmpty"/>. Plain <see cref="CommandItem"/>,
/// <see cref="CommandGroup"/>, <see cref="CommandSeparator"/> and <see cref="CommandEmpty"/> children keep
/// working and are treated as the corresponding combobox parts.
/// </para>
/// </summary>
public sealed partial class Combobox : Component
{
    const string InputClass =
        "h-9 w-full min-w-0 rounded-md border border-input bg-transparent px-3 py-1 text-base shadow-xs " +
        "transition-[color,box-shadow] outline-none selection:bg-primary selection:text-primary-foreground " +
        "file:inline-flex file:h-7 file:border-0 file:bg-transparent file:text-sm file:font-medium file:text-foreground " +
        "placeholder:text-muted-foreground " +
        "disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-50 md:text-sm dark:bg-input/30 " +
        "focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 " +
        "aria-invalid:border-destructive aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40";

    readonly ComboboxScope _scope = new();
    readonly ControllableState<bool> _open = new();

    public string? Value { get; set; }
    public string? DefaultValue { get; set; }
    public Action<string?>? ValueChanged { get; set; }
    public Action<string?>? OnValueChange { get; set; }

    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public Action<bool>? OpenChanged { get; set; }
    public Action<bool>? OnOpenChange { get; set; }

    public string? Placeholder { get; set; } = "Select...";
    public bool Disabled { get; set; }

    /// <summary>Whether <c>ArrowDown</c> / <c>ArrowUp</c> wrap around at the ends. Defaults to true.</summary>
    public bool Loop { get; set; } = true;

    /// <summary>
    /// Custom match function: <c>(query, searchText) =&gt; bool</c>. <c>searchText</c> is the item label joined
    /// with its <see cref="ComboboxItem.Keywords"/>. When null, a case-insensitive <c>Contains</c> over
    /// <c>searchText</c> is used.
    /// </summary>
    public Func<string, string, bool>? Filter { get; set; }

    public string? Id { get; set; }
    public string? Class { get; set; }

    /// <summary>Classes for the popup list, not the input.</summary>
    public string? PopupClass { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        _scope.Filter = Filter;
        _scope.Loop = Loop;
        _scope.SetChildren(Children);

        bool open = _open.Sync(Open, DefaultOpen, OpenChanged + OnOpenChange);
        string value = _scope.Value.Sync(Value, DefaultValue ?? "",
            ValueChanged is null && OnValueChange is null
                ? null
                : v => { ValueChanged?.Invoke(v); OnValueChange?.Invoke(v); });
        var (selectedValue, selectedLabel) = _scope.Selection.Value;
        string? label = value is null
            ? null
            : string.Equals(selectedValue, value, StringComparison.Ordinal) && selectedLabel is not null
                ? selectedLabel
                : _scope.FindLabel(value);
        string display = open ? _scope.Query.Value : label ?? value ?? "";
        string closedText = open ? "" : display;
        _scope.RequestClose = () => _open.Set(false);

        // A controlled open (or DefaultOpen) must highlight the first enabled option even before any key press.
        UseEffect(() =>
        {
            if (open) _scope.SelectFirst();
            return null;
        }, open);

        var input = TabsParts.Style(new UI.Input
        {
            Id = Id ?? _scope.InputId,
            Value = display,
            Placeholder = Placeholder,
            Disabled = Disabled,
            Ref = _scope.InputRef,
        }, "combobox-input", InputClass, Class, Rest);

        // Popover.Trigger supplies aria-expanded, aria-controls and the anchor; the overrides below are
        // combobox semantics rather than the dialog semantics of a generic popover trigger.
        input.Role = Role.ComboBox;
        input.Attributes["aria-expanded"] = TabsParts.Bool(open);
        input.Attributes["aria-haspopup"] = "listbox";
        input.Attributes["aria-autocomplete"] = "list";
        input.Attributes["aria-activedescendant"] = _scope.ActiveId;
        input.Attributes["autocomplete"] = "off";
        input.Attributes["spellcheck"] = "false";

        input.OnClick = EventComposition.Chain(input.OnClick, e =>
        {
            if (e.DefaultPrevented) return;
            e.PreventDefault();
            if (!Disabled && !open) OpenList();
        });
        input.OnFocusIn = EventComposition.Chain(input.OnFocusIn, e =>
        {
            if (e.DefaultPrevented || Disabled || open) return;
            OpenList();
        });
        input.OnBlur = EventComposition.Chain(input.OnBlur, e =>
        {
            if (!e.DefaultPrevented && e.RelatedTarget is null) _open.Set(false);
        });
        input.OnInput = EventComposition.Chain(input.OnInput, e =>
        {
            if (e.DefaultPrevented || Disabled) return;
            string typed = e.Value;
            if (!open && closedText.Length > 0 && typed.StartsWith(closedText, StringComparison.Ordinal))
                typed = typed.Substring(closedText.Length);
            _scope.SetQuery(typed);
            if (!open) _open.Set(true);
        });
        input.OnKeyDown = EventComposition.Chain(input.OnKeyDown, e => HandleKey(e, open));

        var trigger = new PopoverPrimitive.Trigger { AsChild = true, Children = [input] };
        var popup = new ComboboxPopup
        {
            Scope = _scope,
            Children = Children,
            Class = PopupClass,
            LabelId = Id ?? _scope.InputId,
        };

        return new PopoverPrimitive.Root
        {
            Open = open,
            OnOpenChange = next => _open.Set(next),
            Children = [Context.Provide(_scope, new Fragment { Children = [trigger, popup] })],
        };
    }

    void OpenList()
    {
        _scope.SetQuery("");
        _scope.SelectFirst();
        _open.Set(true);
    }

    void HandleKey(KeyEvent e, bool open)
    {
        if (Disabled || e.DefaultPrevented) return;
        switch (e.Key)
        {
            case "ArrowDown":
                e.PreventDefault();
                if (open) _scope.Move(1);
                else OpenList();
                break;
            case "ArrowUp":
                e.PreventDefault();
                if (open) _scope.Move(-1);
                else
                {
                    OpenList();
                    _scope.Move(-1);
                }
                break;
            case "Home" when open:
                e.PreventDefault();
                _scope.MoveToEdge(last: false);
                break;
            case "End" when open:
                e.PreventDefault();
                _scope.MoveToEdge(last: true);
                break;
            case "Enter":
                if (!open) _open.Set(true);
                else if (_scope.ActivateActive()) e.PreventDefault();
                break;
            case "Escape" when open:
                e.PreventDefault();
                _scope.Close();
                break;
            case "Tab" when open:
                _open.Set(false);
                break;
        }
    }
}

/// <summary>Selection, filtering and navigation state shared by the trigger input, the popup and the items.</summary>
internal sealed class ComboboxScope
{
    /// <summary>Shadcn command surface classes, used by the popup body.</summary>
    internal const string RootClass =
        "flex h-full w-full flex-col overflow-hidden rounded-md bg-popover text-popover-foreground";

    internal readonly State<string> Query = new(string.Empty);
    internal readonly State<string?> Active = new((string?)null);
    internal readonly ControllableState<string> Value = new(StringComparer.Ordinal);
    internal readonly State<(string? Value, string? Label)> Selection = new((null, null));
    internal readonly Ref<Element> InputRef = new();
    internal readonly string InputId = TabsParts.NextId("combobox-input");

    internal Func<string, string, bool>? Filter;
    internal bool Loop = true;
    internal Action? RequestClose;

    readonly object _rootGroup = new();
    List<ComboboxRecord> _records = new();
    Dictionary<string, ComboboxRecord> _byId = new(StringComparer.Ordinal);

    internal string? ActiveId => Active.Value;

    /// <summary>Re-collects the composition into ordered item and separator records. Does not touch selection.</summary>
    internal void SetChildren(NodeList? children)
    {
        var records = new List<ComboboxRecord>();
        Collect(children, _rootGroup, records);
        _records = records;
        var byId = new Dictionary<string, ComboboxRecord>(StringComparer.Ordinal);
        foreach (var record in records)
            if (!record.IsSeparator && record.Id.Length > 0 && !byId.ContainsKey(record.Id))
                byId[record.Id] = record;
        _byId = byId;
    }

    internal ComboboxRecord? GetRecord(string id) => _byId.TryGetValue(id, out var record) ? record : null;

    internal bool Matches(ComboboxRecord record, string query)
    {
        string search = query.Trim();
        if (search.Length == 0) return true;
        return Filter is { } filter
            ? filter(search, record.Search)
            : record.Search.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    internal int VisibleCount
    {
        get
        {
            string query = Query.Value;
            int count = 0;
            foreach (var record in _records)
                if (!record.IsSeparator && Matches(record, query)) count++;
            return count;
        }
    }

    /// <summary>Matching items that can receive the active highlight, in document order.</summary>
    internal List<ComboboxRecord> Navigable
    {
        get
        {
            string query = Query.Value;
            var list = new List<ComboboxRecord>();
            foreach (var record in _records)
                if (!record.IsSeparator && !record.Disabled && Matches(record, query))
                    list.Add(record);
            return list;
        }
    }

    internal ComboboxRecord? ActiveRecord
    {
        get
        {
            string? id = Active.Value;
            if (id is null) return null;
            foreach (var record in Navigable)
                if (string.Equals(record.Id, id, StringComparison.Ordinal))
                    return record;
            return null;
        }
    }

    internal string? FirstNavigableId()
    {
        var navigable = Navigable;
        return navigable.Count == 0 ? null : navigable[0].Id;
    }

    /// <summary>Replaces the query and highlights the first remaining enabled item.</summary>
    internal void SetQuery(string? value)
    {
        string next = value ?? string.Empty;
        if (!string.Equals(Query.Value, next, StringComparison.Ordinal)) Query.Value = next;
        Active.Value = FirstNavigableId();
    }

    internal void SelectFirst() => Active.Value = FirstNavigableId();

    internal void Move(int delta)
    {
        var navigable = Navigable;
        if (navigable.Count == 0)
        {
            Active.Value = null;
            return;
        }

        int current = IndexOf(navigable);
        int next;
        if (current < 0)
        {
            next = delta >= 0 ? 0 : navigable.Count - 1;
        }
        else
        {
            int raw = current + delta;
            next = Loop
                ? ((raw % navigable.Count) + navigable.Count) % navigable.Count
                : raw < 0 ? 0 : raw > navigable.Count - 1 ? navigable.Count - 1 : raw;
        }
        Active.Value = navigable[next].Id;
    }

    internal void MoveToEdge(bool last)
    {
        var navigable = Navigable;
        if (navigable.Count == 0)
        {
            Active.Value = null;
            return;
        }
        Active.Value = navigable[last ? navigable.Count - 1 : 0].Id;
    }

    internal void SetActive(string? id) => Active.Value = id;

    /// <summary>Invokes the active item. Returns false when nothing is active.</summary>
    internal bool ActivateActive()
    {
        var record = ActiveRecord;
        if (record is null) return false;
        Activate(record);
        return true;
    }

    internal void Activate(ComboboxRecord record, PointerEvent? pointer = null)
    {
        if (record.Disabled) return;
        var e = pointer ?? new PointerEvent("click");
        record.OnSelect?.Invoke(e);
        if (e.DefaultPrevented) return;
        Commit(record.SelectionValue, record.Label);
    }

    internal void Commit(string? value, string? label)
    {
        Selection.Value = (value, label);
        Value.Set(value ?? "");
        RequestClose?.Invoke();
        // Focus returns to the input so typing can continue immediately.
        InputRef.Current?.Focus();
    }

    internal void Close()
    {
        if (!string.Equals(Query.Value, string.Empty, StringComparison.Ordinal)) Query.Value = string.Empty;
        RequestClose?.Invoke();
        InputRef.Current?.Focus();
    }

    internal string? FindLabel(string value)
    {
        foreach (var record in _records)
            if (!record.IsSeparator && string.Equals(record.SelectionValue, value, StringComparison.Ordinal))
                return record.Label.Length == 0 ? null : record.Label;
        return null;
    }

    /// <summary>
    /// A separator renders only when visible items exist on both sides of it. Adjacent separators that collapse
    /// onto the same pair of neighbours render once, so hiding a group never leaves a stray rule.
    /// </summary>
    internal bool SeparatorVisible(object source)
    {
        ComboboxRecord? separator = null;
        foreach (var record in _records)
        {
            if (!record.IsSeparator || !ReferenceEquals(record.Source, source)) continue;
            separator = record;
            break;
        }
        if (separator is null) return false;

        string query = Query.Value;
        ComboboxRecord? previous = null;
        foreach (var record in _records)
            if (!record.IsSeparator && record.Order < separator.Order && Matches(record, query))
                previous = record;

        ComboboxRecord? next = null;
        foreach (var record in _records)
            if (!record.IsSeparator && record.Order > separator.Order && Matches(record, query))
            {
                next = record;
                break;
            }

        if (previous is null || next is null) return false;

        // Collapse duplicate separators that end up between the same two visible neighbours.
        foreach (var record in _records)
            if (record.IsSeparator && record.Order > previous.Order && record.Order < next.Order && record.Order < separator.Order)
                return false;

        return true;
    }

    /// <summary>Collects a subtree to decide whether a group still has matching items.</summary>
    internal (bool HasItems, bool AnyVisible) Inspect(NodeList? children, string query)
    {
        var records = new List<ComboboxRecord>();
        Collect(children, new object(), records);
        bool hasItems = false;
        foreach (var record in records)
        {
            if (record.IsSeparator) continue;
            hasItems = true;
            if (Matches(record, query)) return (true, true);
        }
        return (hasItems, false);
    }

    int IndexOf(IReadOnlyList<ComboboxRecord> records)
    {
        string? id = Active.Value;
        if (id is null) return -1;
        for (int i = 0; i < records.Count; i++)
            if (string.Equals(records[i].Id, id, StringComparison.Ordinal))
                return i;
        return -1;
    }

    void Collect(NodeList? children, object group, List<ComboboxRecord> list)
    {
        if (children is null) return;
        foreach (var node in children) CollectNode(node, group, list);
    }

    void CollectNode(Node? node, object group, List<ComboboxRecord> list)
    {
        switch (node)
        {
            case null:
                return;
            case ComboboxGroup comboboxGroup:
                Collect(comboboxGroup.Children, new object(), list);
                return;
            case CommandGroup commandGroup:
                Collect(commandGroup.Children, new object(), list);
                return;
            case ComboboxItem item:
                AddItem(list, item.ElementId, ComboboxTree.Text(item.Children), item.Value, item.Keywords,
                    item.Disabled, group, item.OnSelect);
                return;
            case CommandItem commandItem:
                AddItem(list, commandItem.ElementId, CommandTree.Text(commandItem), commandItem.Value,
                    commandItem.Keywords, commandItem.Disabled, group, commandItem.OnSelect);
                return;
            case ComboboxSeparator separator:
                list.Add(new ComboboxRecord { IsSeparator = true, Source = separator, Group = group, Order = list.Count });
                return;
            case CommandSeparator commandSeparator:
                list.Add(new ComboboxRecord { IsSeparator = true, Source = commandSeparator, Group = group, Order = list.Count });
                return;
            case CommandList commandList:
                Collect(commandList.Children, group, list);
                return;
            case Fragment fragment:
                Collect(fragment.Children, group, list);
                return;
            case ElementNode element:
                Collect(element.Children, group, list);
                return;
        }
    }

    static void AddItem(List<ComboboxRecord> list, string id, string label, string? value, string? keywords,
        bool disabled, object group, Action<PointerEvent>? onSelect)
    {
        string search = string.IsNullOrWhiteSpace(keywords) ? label : label + " " + keywords;
        list.Add(new ComboboxRecord
        {
            Id = id,
            Label = label,
            Search = search,
            SelectionValue = string.IsNullOrEmpty(value) ? label : value!,
            Disabled = disabled,
            Group = group,
            OnSelect = onSelect,
            Order = list.Count,
        });
    }
}

/// <summary>One item or separator in document order, used for filtering, navigation and separator collapsing.</summary>
internal sealed class ComboboxRecord
{
    internal bool IsSeparator;
    internal string Id = "";
    internal string Label = "";
    internal string Search = "";
    internal string SelectionValue = "";
    internal bool Disabled;
    internal object Group = null!;
    internal object Source = null!;
    internal Action<PointerEvent>? OnSelect;
    internal int Order;
}

/// <summary>The popover listbox. Its own element owns the listbox role so focus can stay in the trigger input.</summary>
internal sealed partial class ComboboxPopup : Component
{
    const string ContentClass =
        "z-50 w-[var(--aw-popper-anchor-width,14rem)] overflow-hidden rounded-md border bg-popover p-0 " +
        "text-popover-foreground shadow-md outline-hidden " + PopupMotion.Classes;

    public ComboboxScope? Scope { get; set; }
    public NodeList? Children { get; set; }
    public string? Class { get; set; }
    public string? LabelId { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var popup = Use<PopoverPrimitive.Context>();
        var shared = popup.Shared;
        var scope = Scope!;
        var attributes = DialogAttributes.ForPart("combobox-content", Rest, Class, ContentClass);
        attributes["data-state"] = TabsParts.OpenState(popup.Open);
        if (attributes["aria-label"] is null && attributes["aria-labelledby"] is null)
            attributes["aria-labelledby"] = LabelId;
        if (!popup.Open) attributes["aria-hidden"] = "true";

        var shell = TabsParts.Style(new Div { Children = ComboboxTree.NormalizeList(Children) },
            "combobox", ComboboxScope.RootClass, null, null);
        var content = new PopperContent
        {
            Id = shared.ContentId,
            Role = Role.ListBox,
            TabIndex = -1,
            Side = Side.Bottom,
            SideOffset = 4,
            Align = PopperAlign.Start,
            AvoidCollisions = true,
            CollisionPadding = 8,
            Attributes = attributes,
            Children = [Context.Provide(scope, shell)],
        };

        var layer = new DismissableLayer
        {
            Active = popup.Open,
            // Interacting with the trigger must not be treated as an outside dismissal.
            OnPointerDownOutside = e =>
            {
                if (shared.TriggerElement is { } trigger && e.Target is { } target
                    && trigger.IsInclusiveAncestorOf(target))
                    e.PreventDefault();
            },
            OnFocusOutside = e =>
            {
                if (shared.TriggerElement is { } trigger && e.Target is { } target
                    && trigger.IsInclusiveAncestorOf(target))
                    e.PreventDefault();
            },
            OnDismiss = _ => shared.SetOpen(false),
            Children = [content],
        };

        return new PopoverPrimitive.Portal { Children = [layer] };
    }
}

/// <summary>A labeled section of combobox items. The whole group (label included) disappears when no item matches.</summary>
public sealed partial class ComboboxGroup : Component
{
    const string GroupClass = "overflow-hidden p-1 text-foreground";
    const string LabelClass = "px-2 py-1.5 text-xs font-medium text-muted-foreground";

    readonly string _labelId = TabsParts.NextId("combobox-label");

    /// <summary>Heading rendered above the group's items.</summary>
    public string? Label { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<ComboboxScope>();
        string query = scope.Query.Value;
        var (hasItems, anyVisible) = scope.Inspect(Children, query);
        if (hasItems && !anyVisible) return new Fragment();

        var children = new NodeList();
        if (!string.IsNullOrEmpty(Label))
        {
            var label = TabsParts.Style(new Div { Children = [Label] }, "combobox-label", LabelClass, null, null);
            label.Id = _labelId;
            children.Add(label);
        }
        children.AddRange(ComboboxTree.NormalizeList(Children));

        var group = TabsParts.Style(new Div { Role = Role.Group, Children = children },
            "combobox-group", GroupClass, Class, Rest);
        if (!string.IsNullOrEmpty(Label)) group.Attributes["aria-labelledby"] = _labelId;
        return group;
    }
}

/// <summary>A selectable option. <see cref="Value"/> is committed; the visible text is shown in the input when closed.</summary>
public sealed partial class ComboboxItem : Component
{
    const string ItemClass =
        "relative flex cursor-default items-center gap-2 rounded-sm px-2 py-1.5 text-sm outline-hidden select-none " +
        "data-[disabled=true]:pointer-events-none data-[disabled=true]:opacity-50 " +
        "data-[selected=true]:bg-accent data-[selected=true]:text-accent-foreground " +
        "[&_svg]:pointer-events-none [&_svg]:shrink-0 [&_svg:not([class*='size-'])]:size-4 " +
        "[&_svg:not([class*='text-'])]:text-muted-foreground";

    readonly string _generatedId = TabsParts.NextId("combobox-item");

    /// <summary>Value committed to the combobox. Defaults to the item's visible text.</summary>
    public string? Value { get; set; }

    /// <summary>Extra words that also match the query.</summary>
    public string? Keywords { get; set; }

    public bool Disabled { get; set; }
    public string? Id { get; set; }
    public string? Class { get; set; }
    public Action<PointerEvent>? OnSelect { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    internal string ElementId => Id ?? _generatedId;

    protected override Node Build()
    {
        var scope = Use<ComboboxScope>();
        var record = scope.GetRecord(ElementId);
        if (record is null) return new Fragment();
        string query = scope.Query.Value;
        if (!scope.Matches(record, query)) return new Fragment();

        bool active = string.Equals(scope.ActiveId, ElementId, StringComparison.Ordinal);

        var item = TabsParts.Style(new Div
        {
            Id = ElementId,
            Role = Role.Option,
            Children = Children ?? new NodeList(),
        }, "combobox-item", ItemClass, Class, Rest);

        item.Attributes["data-selected"] = TabsParts.Bool(active);
        item.Attributes["data-disabled"] = Disabled ? "true" : null;
        item.Attributes["data-value"] = record.SelectionValue;
        item.Attributes["aria-selected"] = TabsParts.Bool(active);
        item.Attributes["aria-disabled"] = Disabled ? "true" : null;

        item.OnPointerDown = EventComposition.Chain(item.OnPointerDown, e =>
        {
            // Keep focus in the search input: a non-focusable press target would otherwise clear it.
            if (!e.DefaultPrevented && !Disabled) e.PreventDefault();
        });
        item.OnPointerMove = EventComposition.Chain(item.OnPointerMove, e =>
        {
            if (!e.DefaultPrevented && !Disabled && e.PointerType == PointerType.Mouse)
                scope.SetActive(ElementId);
        });
        item.OnClick = EventComposition.Chain(item.OnClick, e =>
        {
            if (e.DefaultPrevented) return;
            scope.Activate(record, e);
        });
        return item;
    }
}

/// <summary>A rule between groups. It renders only when visible groups exist on both sides of it.</summary>
public sealed partial class ComboboxSeparator : Component
{
    /// <summary>The part this separator was translated from (legacy <see cref="CommandSeparator"/>).</summary>
    internal object? Source { get; set; }

    public string? Class { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<ComboboxScope>();
        if (!scope.SeparatorVisible(Source ?? this)) return new Fragment();
        return TabsParts.Style(new Div { Role = Role.Separator },
            "combobox-separator", "-mx-1 h-px bg-border", Class, Rest);
    }
}

/// <summary>Shown only while the current query matches no item.</summary>
public sealed partial class ComboboxEmpty : Component
{
    const string EmptyClass = "py-6 text-center text-sm";

    /// <summary>Shortcut for a text-only empty state; the content is also composed from <see cref="Children"/>.</summary>
    public string? Text { get; set; }
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var scope = Use<ComboboxScope>();
        if (scope.VisibleCount != 0) return new Fragment();

        var children = new NodeList();
        if (Text is not null) children.Add(Text);
        children.AddRange(Children);

        return TabsParts.Style(new Div { Role = Role.Presentation, Children = children },
            "combobox-empty", EmptyClass, Class, Rest);
    }
}

/// <summary>Composition helpers shared by the combobox parts.</summary>
internal static class ComboboxTree
{
    /// <summary>
    /// Copies a composition into combobox parts. Legacy command parts are translated so a plain
    /// <see cref="CommandItem"/> list keeps working; native combobox parts pass through unchanged.
    /// </summary>
    internal static NodeList NormalizeList(NodeList? children)
    {
        var list = new NodeList();
        if (children is null) return list;
        foreach (var node in children)
            if (node is not null)
                list.Add(Normalize(node));
        return list;
    }

    internal static Node Normalize(Node node) => node switch
    {
        CommandItem item => new ComboboxItem
        {
            Id = item.ElementId,
            Value = item.Value,
            Keywords = item.Keywords,
            Disabled = item.Disabled,
            Class = item.Class,
            OnSelect = item.OnSelect,
            Children = item.Children,
            Rest = item.Rest,
        },
        CommandGroup group => new ComboboxGroup
        {
            Label = group.Heading,
            Class = group.Class,
            Children = NormalizeList(group.Children),
            Rest = group.Rest,
        },
        CommandSeparator separator => new ComboboxSeparator
        {
            Source = separator,
            Class = separator.Class,
            Rest = separator.Rest,
        },
        CommandEmpty empty => new ComboboxEmpty
        {
            Children = empty.Children,
            Class = empty.Class,
            Rest = empty.Rest,
        },
        CommandList list => new Fragment { Children = NormalizeList(list.Children) },
        Fragment fragment => new Fragment { Children = NormalizeList(fragment.Children) },
        _ => node,
    };

    /// <summary>Visible text of an item, excluding any shortcut.</summary>
    internal static string Text(NodeList? children)
    {
        var text = new StringBuilder();
        Append(children, text);
        return text.ToString();
    }

    static void Append(NodeList? children, StringBuilder text)
    {
        if (children is null) return;
        foreach (var child in children) Append(child, text);
    }

    static void Append(Node? node, StringBuilder text)
    {
        switch (node)
        {
            case null:
                return;
            case TextNode textNode:
                text.Append(textNode.Text);
                return;
            case UI.Text element:
                if (element.Content is { } content) text.Append(content);
                Append(element.Children, text);
                return;
            case CommandShortcut:
                return;
            case Fragment fragment:
                Append(fragment.Children, text);
                return;
            case ElementNode element:
                string slot = Convert.ToString(element.Attributes["data-slot"], CultureInfo.InvariantCulture) ?? "";
                if (string.Equals(slot, "command-shortcut", StringComparison.Ordinal)
                    || string.Equals(slot, "combobox-shortcut", StringComparison.Ordinal))
                    return;
                Append(element.Children, text);
                return;
        }
    }
}
