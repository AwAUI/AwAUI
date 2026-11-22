using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.UI;

namespace AwAUI.Shadcn;

/// <summary>
/// A cmdk-style command palette: a search field with filtered, ranked command items.
/// Filtering is a case-insensitive subsequence match; results are ranked by a deterministic score.
/// </summary>
public sealed partial class Command : Component
{
    readonly CommandScope _scope = new();

    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build() =>
        CommandParts.Shell(_scope, Children, CommandScope.RootClass, Class, Rest, suppressListRole: false);
}

/// <summary>A command palette shown inside a dialog. The dialog supplies the title and description for assistive technology.</summary>
public sealed partial class CommandDialog : Component
{
    const string CommandClass =
        "**:data-[slot=command-input-wrapper]:h-12 [&_[data-slot=command-group-heading]]:px-2 " +
        "[&_[data-slot=command-group-heading]]:font-medium [&_[data-slot=command-group-heading]]:text-muted-foreground " +
        "[&_[data-slot=command-group]]:px-2 [&_[data-slot=command-group]:not([hidden])_~[data-slot=command-group]]:pt-0 " +
        "[&_[data-slot=command-input-wrapper]_svg]:h-5 [&_[data-slot=command-input-wrapper]_svg]:w-5 " +
        "[&_[data-slot=command-input]]:h-12 [&_[data-slot=command-item]]:px-2 [&_[data-slot=command-item]]:py-3 " +
        "[&_[data-slot=command-item]_svg]:h-5 [&_[data-slot=command-item]_svg]:w-5";

    public bool? Open { get; set; }
    public bool DefaultOpen { get; set; }
    public bool Modal { get; set; } = true;
    public Action<bool>? OpenChanged { get; set; }
    public Action<bool>? OnOpenChange { get; set; }
    public string Title { get; set; } = "Command Palette";
    public string Description { get; set; } = "Search for a command to run...";
    public bool ShowCloseButton { get; set; } = true;
    public string? Class { get; set; }
    public NodeList? Children { get; set; }
    [CaptureUnmatchedAttributes] public AttributeDictionary? Rest { get; set; }

    protected override Node Build()
    {
        var header = new DialogHeader
        {
            Class = "sr-only",
            Children =
            [
                new DialogTitle { Children = [Title] },
                new DialogDescription { Children = [Description] },
            ],
        };
        var content = new DialogContent
        {
            ShowCloseButton = ShowCloseButton,
            Class = Tailwind.TailwindMerge.Cn("overflow-hidden p-0", Class),
            Rest = Rest,
            Children =
            [
                header,
                new Command { Class = CommandClass, Children = Children },
            ],
        };
        return new Dialog
        {
            Open = Open,
            DefaultOpen = DefaultOpen,
            Modal = Modal,
            OpenChanged = OpenChanged,
            OnOpenChange = OnOpenChange,
            Children = [content],
        };
    }
}

/// <summary>Shared state for all command parts. The same instance survives rebuilds so parts can subscribe to query and selection.</summary>
internal sealed class CommandScope
{
    /// <summary>Shadcn command surface classes, shared by the standalone root and the combobox popup.</summary>
    internal const string RootClass =
        "flex h-full w-full flex-col overflow-hidden rounded-md bg-popover text-popover-foreground";

    internal readonly State<string> Query = new(string.Empty);
    internal readonly State<string?> Active = new((string?)null);
    internal readonly string ListId = TabsParts.NextId("command-list");

    List<CommandItem> _items = new();
    int _itemsVersion;
    string? _visibleQuery;
    int _visibleVersion = -1;
    List<CommandItem>? _visible;

    /// <summary>Items in source order, refreshed by the command root before its children build.</summary>
    internal IReadOnlyList<CommandItem> Items => _items;

    /// <summary>When true a list part does not claim the listbox role (the popup itself owns it).</summary>
    internal bool SuppressListRole { get; set; }

    internal void SetItems(IReadOnlyList<CommandItem> items)
    {
        _items = new List<CommandItem>(items);
        for (int i = 0; i < _items.Count; i++) _items[i].Order = i;
        _itemsVersion++;
    }

    /// <summary>Matching items, including disabled ones, sorted by score then source order.</summary>
    internal IReadOnlyList<CommandItem> Visible
    {
        get
        {
            string query = Query.Value;
            if (_visible is null || _visibleVersion != _itemsVersion || !string.Equals(_visibleQuery, query, StringComparison.Ordinal))
            {
                _visible = CommandTree.Rank(_items, query);
                _visibleQuery = query;
                _visibleVersion = _itemsVersion;
            }
            return _visible;
        }
    }

    /// <summary>Matching items that can receive the active highlight.</summary>
    internal IReadOnlyList<CommandItem> Navigable
    {
        get
        {
            var visible = Visible;
            var list = new List<CommandItem>(visible.Count);
            foreach (var item in visible)
                if (!item.Disabled) list.Add(item);
            return list;
        }
    }

    internal string? ActiveId => Active.Value;

    internal CommandItem? ActiveItem
    {
        get
        {
            string? id = Active.Value;
            if (id is null) return null;
            foreach (var item in Navigable)
                if (string.Equals(item.ElementId, id, StringComparison.Ordinal)) return item;
            return null;
        }
    }

    internal string? FirstNavigableId()
    {
        var navigable = Navigable;
        return navigable.Count == 0 ? null : navigable[0].ElementId;
    }

    /// <summary>Replaces the query and highlights the best-ranked remaining item.</summary>
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
        int next = current < 0
            ? (delta >= 0 ? 0 : navigable.Count - 1)
            : ((current + delta) % navigable.Count + navigable.Count) % navigable.Count;
        Active.Value = navigable[next].ElementId;
    }

    internal void MoveToEdge(bool last)
    {
        var navigable = Navigable;
        if (navigable.Count == 0)
        {
            Active.Value = null;
            return;
        }
        Active.Value = navigable[last ? navigable.Count - 1 : 0].ElementId;
    }

    internal void SetActive(string? id) => Active.Value = id;

    /// <summary>Invokes the active item. Returns false when nothing is active.</summary>
    internal bool ActivateActive() => ActiveItem?.Activate() == true;

    int IndexOf(IReadOnlyList<CommandItem> items)
    {
        string? id = Active.Value;
        if (id is null) return -1;
        for (int i = 0; i < items.Count; i++)
            if (string.Equals(items[i].ElementId, id, StringComparison.Ordinal)) return i;
        return -1;
    }
}

internal static class CommandParts
{
    internal static Node Shell(CommandScope scope, NodeList? children, string rootClass, string? classes,
        AttributeDictionary? rest, bool suppressListRole)
    {
        scope.SuppressListRole = suppressListRole;
        scope.SetItems(CommandTree.CollectItems(children));
        var node = TabsParts.Style(new Div { Children = children ?? new NodeList() }, "command", rootClass, classes, rest);
        return Context.Provide(scope, node);
    }
}

/// <summary>Collection, text extraction and deterministic scoring for command items.</summary>
internal static class CommandTree
{
    internal static List<CommandItem> CollectItems(NodeList? children)
    {
        var list = new List<CommandItem>();
        Visit(children, list);
        return list;
    }

    static void Visit(NodeList? children, List<CommandItem> list)
    {
        if (children is null) return;
        foreach (var child in children) Visit(child, list);
    }

    static void Visit(Node? node, List<CommandItem> list)
    {
        switch (node)
        {
            case null:
                return;
            case CommandItem item:
                list.Add(item);
                return;
            case CommandGroup group:
                Visit(group.Children, list);
                return;
            case CommandList commandList:
                Visit(commandList.Children, list);
                return;
            case Fragment fragment:
                Visit(fragment.Children, list);
                return;
            case ElementNode element:
                Visit(element.Children, list);
                return;
        }
    }

    /// <summary>Visible text of an item, excluding its shortcut.</summary>
    internal static string Text(CommandItem item) => TextOf(item.Children);

    /// <summary>Text used for matching: an explicit value, the visible text, and optional keywords.</summary>
    internal static string FilterText(CommandItem item)
    {
        string text = string.IsNullOrEmpty(item.Value) ? Text(item) : item.Value!;
        if (string.IsNullOrWhiteSpace(item.Keywords)) return text;
        return text + " " + item.Keywords;
    }

    internal static string TextOf(NodeList? children)
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
                if (string.Equals(Convert.ToString(element.Attributes["data-slot"], System.Globalization.CultureInfo.InvariantCulture),
                        "command-shortcut", StringComparison.Ordinal))
                    return;
                Append(element.Children, text);
                return;
        }
    }

    internal static bool Matches(CommandItem item, string? query) => Score(FilterText(item), query ?? "") >= 0;

    internal static List<CommandItem> Rank(IReadOnlyList<CommandItem> items, string? query)
    {
        string search = (query ?? string.Empty).Trim();
        var scored = new List<(CommandItem Item, double Score, int Order)>(items.Count);
        foreach (var item in items)
        {
            double score = search.Length == 0 ? 0 : Score(FilterText(item), search);
            if (score >= 0) scored.Add((item, score, item.Order));
        }
        scored.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.Order.CompareTo(b.Order);
        });
        var result = new List<CommandItem>(scored.Count);
        foreach (var entry in scored) result.Add(entry.Item);
        return result;
    }

    /// <summary>
    /// Case-insensitive subsequence score. Returns -1 when the query is not a subsequence of the text.
    /// Word-boundary matches, consecutive runs, an exact prefix and exact case matches score higher;
    /// long gaps are penalised. The formula is deterministic and culture independent.
    /// </summary>
    internal static double Score(string? text, string query)
    {
        if (string.IsNullOrEmpty(query)) return 0;
        if (text is null || text.Length == 0) return -1;

        int textIndex = 0;
        int queryIndex = 0;
        int lastMatch = -2;
        int firstMatch = -1;
        double score = 0;

        while (textIndex < text.Length && queryIndex < query.Length)
        {
            char actual = text[textIndex];
            char wanted = query[queryIndex];
            if (char.ToLowerInvariant(actual) == char.ToLowerInvariant(wanted))
            {
                if (firstMatch < 0) firstMatch = textIndex;
                double bonus = 1;
                bool boundary = textIndex == 0 || !char.IsLetterOrDigit(text[textIndex - 1]);
                if (boundary) bonus += 1.5;
                if (textIndex == lastMatch + 1) bonus += 1;
                if (actual == wanted) bonus += 0.1;
                score += bonus;
                lastMatch = textIndex;
                queryIndex++;
            }
            textIndex++;
        }

        if (queryIndex < query.Length) return -1;
        if (firstMatch == 0) score += 2;
        score -= Math.Min(3, Math.Max(0, lastMatch - (query.Length - 1)) * 0.05);
        return score;
    }
}
