using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>An item with a stable, non-null key unique within the tree.</summary>
/// <param name="Key">The key used for reconciliation, expansion, and selection.</param>
/// <param name="Header">The accessible name and default visible label.</param>
/// <param name="Children">The child items, or null for a leaf.</param>
/// <param name="IsEnabled">Whether the item and its descendants can be focused or operated.</param>
public sealed record TreeViewItem(object Key, string Header, IReadOnlyList<TreeViewItem>? Children = null, bool IsEnabled = true);

/// <summary>A hierarchical view with single selection and one tab stop among the visible, enabled items.</summary>
/// <remarks>
/// Use <see cref="Control.AriaLabel"/> or <c>aria-labelledby</c> to name the tree. Up, Down, Home, and End move focus
/// without changing selection. Right opens a branch or enters it; Left closes it or returns to its parent.
/// Clicking a row or pressing Space selects it. Enter selects and activates it. Clicking the indicator toggles expansion.
/// Supply the full hierarchy as a snapshot and replace <see cref="Items"/> when it changes. There is no virtualization
/// or lazy loading; every item in an expanded branch is rendered, and collapsed descendants are unmounted.
/// </remarks>
public sealed partial class TreeView : Control
{
    static readonly RovingFocusOptions s_navigation = new() { Orientation = Orientation.Vertical };
    readonly ControllableState<object?> _selection = new();
    readonly ControllableState<IReadOnlyCollection<object>> _expansion = new();
    Dictionary<object, Entry> _entries = new();
    List<Entry> _visible = new();
    object? _focusedKey;

    public IReadOnlyList<TreeViewItem>? Items { get; set; }

    /// <summary>Renders noninteractive content for the label. The item's header is still used as its accessible name.</summary>
    public Func<TreeViewItem, Node>? ItemTemplate { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Controls selection when non-null. Use <see cref="IsSelectionControlled"/> to control an empty selection.</summary>
    public object? SelectedKey { get; set; }

    public bool IsSelectionControlled { get; set; }
    public object? DefaultSelectedKey { get; set; }
    public Action<object?>? SelectedKeyChanged { get; set; }

    /// <summary>The current selection key, retained even when the item is collapsed or absent from the supplied snapshot.</summary>
    public object? CurrentSelectedKey => _selection.Value;

    /// <summary>The current item, including a collapsed descendant, or null when the selection key has no matching item.</summary>
    public TreeViewItem? SelectedItem => Find(_entries, CurrentSelectedKey)?.Item;

    /// <summary>Controls expansion when supplied. Pass an empty collection to keep all branches collapsed.</summary>
    public IReadOnlyCollection<object>? ExpandedKeys { get; set; }

    public IReadOnlyCollection<object>? DefaultExpandedKeys { get; set; }
    public Action<IReadOnlyCollection<object>>? ExpandedKeysChanged { get; set; }

    /// <summary>Runs when Enter or a synthetic activation invokes an enabled item, even if it is already selected.</summary>
    public Action<TreeViewItem>? ItemActivated { get; set; }

    protected override Node Build()
    {
        var selectedKey = _selection.Sync(IsSelectionControlled || SelectedKey is not null,
            SelectedKey, DefaultSelectedKey, SelectedKeyChanged);
        var expandedKeys = _expansion.Sync(ExpandedKeys is not null, ExpandedKeys ?? Array.Empty<object>(),
            DefaultExpandedKeys ?? Array.Empty<object>(), ExpandedKeysChanged);
        var entries = new Dictionary<object, Entry>();
        var visible = new List<Entry>();
        var roots = IndexItems(Items ?? Array.Empty<TreeViewItem>(), null, true,
            new HashSet<object>(expandedKeys), entries, visible);

        var oldEntry = Find(_entries, _focusedKey);
        var oldFocus = oldEntry?.ElementRef?.Current is { IsFocused: true } focused ? focused : null;
        var tabStop = IsEnabled ? FindTabStop(entries, visible, selectedKey, oldEntry) : null;
        var tree = new Div { Role = Role.Tree, Children = RenderItems(roots, tabStop, selectedKey) };
        tree.Attributes["aria-disabled"] = IsEnabled ? null : "true";

        var root = oldFocus?.Root;
        bool focusVisible = oldFocus is not null && (oldFocus.States & PseudoStates.FocusVisible) != 0;
        UseEffect(() =>
        {
            if (root is not null && oldFocus is not null)
                RestoreFocus(root, oldFocus, tabStop, focusVisible);
            return null;
        }, oldFocus is null ? null : entries);

        _entries = entries;
        _visible = visible;
        return Apply(tree, "aw-tree-view");
    }

    IReadOnlyList<Entry> IndexItems(IReadOnlyList<TreeViewItem> items, Entry? parent, bool isVisible,
        HashSet<object> expandedKeys, Dictionary<object, Entry> entries, List<Entry> visible)
    {
        var result = new Entry[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item?.Key is not { } key || entries.ContainsKey(key))
                throw new ArgumentException("TreeView items must have non-null keys that are unique within the tree.", nameof(Items));

            bool expanded = item.Children is { Count: > 0 } && expandedKeys.Contains(key);
            var entry = new Entry(item, parent, expanded, i + 1, items.Count);
            entries.Add(key, entry);
            result[i] = entry;
            if (isVisible)
            {
                entry.VisibleIndex = visible.Count;
                entry.ElementRef = Find(_entries, key)?.ElementRef ?? new Ref<Element>();
                visible.Add(entry);
            }
            if (item.Children is { Count: > 0 } children)
                entry.Children = IndexItems(children, entry, isVisible && expanded, expandedKeys, entries, visible);
        }
        return result;
    }

    static Entry? Find(Dictionary<object, Entry> entries, object? key) =>
        key is not null && entries.TryGetValue(key, out var entry) ? entry : null;

    static Entry? FindTabStop(Dictionary<object, Entry> entries, List<Entry> visible, object? selectedKey, Entry? former)
    {
        var current = Find(entries, former?.Item.Key);
        for (var entry = current; entry is not null; entry = entry.Parent)
            if (entry.CanFocus) return entry;
        // If an item was removed or moved into a hidden subtree, its former ancestors may still be visible.
        for (var entry = former?.Parent; entry is not null; entry = entry.Parent)
            if (Find(entries, entry.Item.Key) is { CanFocus: true } ancestor) return ancestor;
        for (var entry = Find(entries, selectedKey); entry is not null; entry = entry.Parent)
            if (entry.CanFocus) return entry;
        int first = RovingFocus.Navigate(FocusIntent.First, -1, visible.Count, i => visible[i].CanFocus, loop: false);
        return first < 0 ? null : visible[first];
    }

    void RestoreFocus(UIRoot root, Element oldFocus, Entry? tabStop, bool focusVisible)
    {
        if (root.FocusManager.FocusedElement is { } active && !ReferenceEquals(active, oldFocus)) return;
        if (tabStop is null)
        {
            oldFocus.Blur();
            return;
        }
        if (ReferenceEquals(tabStop.ElementRef?.Current, oldFocus)) return;

        // Closing or replacing a branch can detach the focused element. Wait until its replacement is laid out.
        root.RequestAfterLayout(() =>
        {
            if (!ReferenceEquals(Root, root)) return;
            if (root.FocusManager.FocusedElement is { } current && !ReferenceEquals(current, oldFocus)) return;
            if (IsEnabled && Find(_entries, tabStop.Item.Key) is { CanFocus: true, ElementRef.Current: { } next }
                && next.IsVisible && !root.IsPointerBlocked(next) && next.Focus(new FocusOptions { FocusVisible = focusVisible })) return;
            if (!oldFocus.IsFocusable) oldFocus.Blur();
        });
    }

    NodeList RenderItems(IReadOnlyList<Entry> entries, Entry? tabStop, object? selectedKey)
    {
        var items = new NodeList();
        foreach (var entry in entries)
        {
            bool enabled = IsEnabled && entry.CanFocus;
            bool selected = Equals(selectedKey, entry.Item.Key);
            bool focused = enabled && entry.ElementRef?.Current is { IsFocused: true };
            string? state = entry.HasChildren ? PartProps.OpenState(entry.Expanded) : null;

            var indicator = new Div
            {
                OnClick = entry.HasChildren ? e =>
                {
                    if (e.DefaultPrevented) return;
                    e.PreventDefault();
                    if (IsEnabled && entry.CanFocus) SetExpanded(entry, expanded: null);
                } : null,
            };
            indicator.Attributes["part"] = "indicator";
            indicator.Attributes["aria-hidden"] = "true";
            indicator.Attributes["data-state"] = state;

            var label = new Div { Children = [ItemTemplate?.Invoke(entry.Item) ?? new UIText(entry.Item.Header)] };
            label.Attributes["part"] = "label";
            var row = new Div { Children = [indicator, label], OnClick = e => Click(entry, e, onItem: false) };
            SetPart(row, "row", state, selected, enabled, focused);

            var item = new Div
            {
                Key = entry.Item.Key,
                Ref = entry.ElementRef,
                Role = Role.TreeItem,
                AriaLabel = entry.Item.Header,
                TabIndex = enabled ? (ReferenceEquals(entry, tabStop) ? 0 : -1) : null,
                Children = [row],
                OnFocus = _ => { _focusedKey = entry.Item.Key; Invalidate(); },
                OnBlur = _ => Invalidate(),
                OnKeyDown = e => HandleKey(entry, e),
                OnPointerDown = enabled ? null : static e => e.PreventDefault(),
                OnClick = e => Click(entry, e, onItem: true),
            };
            SetPart(item, "item", state, selected, enabled, focused);
            item.Attributes["aria-level"] = entry.Level.ToString(CultureInfo.InvariantCulture);
            item.Attributes["aria-posinset"] = entry.Position.ToString(CultureInfo.InvariantCulture);
            item.Attributes["aria-setsize"] = entry.SetSize.ToString(CultureInfo.InvariantCulture);
            item.Attributes["aria-selected"] = PartProps.Bool(selected);
            item.Attributes["aria-disabled"] = enabled ? null : "true";
            item.Attributes["aria-expanded"] = entry.HasChildren ? PartProps.Bool(entry.Expanded) : null;
            if (entry.Expanded)
            {
                var group = new Div { Role = Role.Group, Children = RenderItems(entry.Children, tabStop, selectedKey) };
                group.Attributes["part"] = "group";
                group.Attributes["data-state"] = "open";
                item.Children.Add(group);
            }
            items.Add(item);
        }
        return items;
    }

    static void SetPart(Div element, string part, string? state, bool selected, bool enabled, bool focused)
    {
        element.Attributes["part"] = part;
        element.Attributes["data-state"] = state;
        element.Attributes["data-selected"] = PartProps.Bool(selected);
        element.Attributes["data-disabled"] = PartProps.Bool(!enabled);
        element.Attributes["data-focused"] = PartProps.Bool(focused);
    }

    void Click(Entry entry, PointerEvent e, bool onItem)
    {
        if (!IsEnabled || !entry.CanFocus || e.DefaultPrevented) return;
        if (onItem && !ReferenceEquals(e.Target, e.CurrentTarget)) return;
        for (var target = e.Target; target is not null && !ReferenceEquals(target, e.CurrentTarget); target = target.Parent)
            if (target.IsFocusable || target.CanBeDisabled || target.Role == Role.Link) return;
        Select(entry, activate: e.IsSynthetic);
    }

    void Select(Entry entry, bool activate)
    {
        _selection.Set(entry.Item.Key);
        if (activate) ItemActivated?.Invoke(entry.Item);
    }

    void SetExpanded(Entry entry, bool? expanded)
    {
        var keys = new HashSet<object>(_expansion.Value);
        var key = entry.Item.Key;
        bool open = expanded ?? !keys.Contains(key);
        if (!(open ? keys.Add(key) : keys.Remove(key))) return;
        var snapshot = new object[keys.Count];
        keys.CopyTo(snapshot);
        _expansion.Set(Array.AsReadOnly(snapshot));
    }

    void HandleKey(Entry entry, KeyEvent e)
    {
        if (!IsEnabled || !entry.CanFocus || e.DefaultPrevented || !ReferenceEquals(e.Target, e.CurrentTarget)
            || e.Modifiers != KeyModifiers.None) return;

        switch (e.Key)
        {
            case "ArrowUp": case "ArrowDown": case "Home": case "End":
                e.PreventDefault();
                int next = RovingFocus.HandleKeyDown(e.Key, e.Modifiers, entry.VisibleIndex, _visible.Count,
                    i => CanMoveTo(_visible[i]), s_navigation);
                if (next >= 0 && next != entry.VisibleIndex) MoveFocus(_visible[next]);
                break;
            case "ArrowRight":
                e.PreventDefault();
                if (!entry.HasChildren) break;
                if (!entry.Expanded) SetExpanded(entry, true);
                else
                    foreach (var child in entry.Children)
                        if (CanMoveTo(child)) { MoveFocus(child); break; }
                break;
            case "ArrowLeft":
                e.PreventDefault();
                if (entry.Expanded) SetExpanded(entry, false);
                else
                    for (var parent = entry.Parent; parent is not null; parent = parent.Parent)
                        if (CanMoveTo(parent)) { MoveFocus(parent); break; }
                break;
            case "Enter": case " ":
                e.PreventDefault();
                if (!e.IsRepeat) Select(entry, activate: e.Key == "Enter");
                break;
        }
    }

    static bool CanMoveTo(Entry entry) => entry.CanFocus && entry.ElementRef?.Current is { IsFocusable: true, IsVisible: true };

    static void MoveFocus(Entry entry) => entry.ElementRef?.Current?.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });

    sealed class Entry(TreeViewItem item, Entry? parent, bool expanded, int position, int setSize)
    {
        public TreeViewItem Item { get; } = item;
        public Entry? Parent { get; } = parent;
        public bool Expanded { get; } = expanded;
        public int Level { get; } = (parent?.Level ?? 0) + 1;
        public int Position { get; } = position;
        public int SetSize { get; } = setSize;
        public bool Enabled { get; } = item.IsEnabled && (parent?.Enabled ?? true);
        public int VisibleIndex { get; set; } = -1;
        public Ref<Element>? ElementRef { get; set; }
        public IReadOnlyList<Entry> Children { get; set; } = Array.Empty<Entry>();
        public bool HasChildren => Children.Count > 0;
        public bool CanFocus => VisibleIndex >= 0 && Enabled;
    }
}
