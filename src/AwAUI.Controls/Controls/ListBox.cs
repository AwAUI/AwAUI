using System;
using System.Collections.Generic;
using System.Linq;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>A list of selectable options with a single tab stop. Item templates should contain non-interactive content.</summary>
public sealed partial class ListBox : ItemsControl
{
    static readonly RovingFocusOptions s_navigation = new() { Orientation = Orientation.Vertical };
    readonly SelectionModel<object?> _selection = new();
    ItemRow? _focusedRow;
    bool _initialized;

    /// <summary>Controls single selection by index. Use -1 to clear it, or null for automatic state management.</summary>
    public int? SelectedIndex { get; set; }

    /// <summary>The initial selection when <see cref="SelectedIndex"/> is null.</summary>
    public int DefaultSelectedIndex { get; set; } = -1;

    /// <summary>Reports a change to the primary index or requests one when the index is controlled.</summary>
    public Action<int>? SelectedIndexChanged { get; set; }

    /// <summary>Reports option changes caused by interactions or collection notifications in uncontrolled mode.</summary>
    public Action<SelectionModelSelectionChangedEventArgs<object?>>? SelectionChanged { get; set; }

    /// <summary>Selection gestures to enable. A controlled <see cref="SelectedIndex"/> takes precedence over multiple selection.</summary>
    public SelectionMode SelectionMode { get; set; }

    public bool IsEnabled { get; set; } = true;
    public int CurrentSelectedIndex => _selection.SelectedIndex;
    public object? SelectedItem => _selection.SelectedItem;
    public IReadOnlyList<int> SelectedIndexes => _selection.SelectedIndexes;
    public IReadOnlyList<object?> SelectedItems => _selection.SelectedItems.ToArray();

    SelectionMode Mode => SelectedIndex.HasValue ? SelectionMode & ~SelectionMode.Multiple : SelectionMode;

    protected override Node Build()
    {
        var items = ReadItems();
        _selection.SingleSelect = (Mode & SelectionMode.Multiple) == 0;
        _selection.Source = items;
        if (SelectedIndex is int controlled) SetControlledIndex(controlled);
        else if (!_initialized) _selection.SelectedIndex = DefaultSelectedIndex;
        _initialized = true;
        EnsureSelection();

        int focused = _focusedRow is { } row ? items.IndexOf(row) : -1;
        if (focused < 0) _focusedRow = null;
        int tabStop = focused >= 0 && _focusedRow?.ElementRef?.Current is { IsFocused: true } ? focused
            : CurrentSelectedIndex >= 0 ? CurrentSelectedIndex : Math.Max(0, focused);

        var options = new NodeList();
        for (int i = 0; i < items.Count; i++) options.Add(BuildOption(items, i, i == tabStop));
        var list = new Div { Role = Role.ListBox, Style = "overflow:auto;", Children = PresentItems(options) };
        list.Attributes["aria-multiselectable"] = _selection.SingleSelect ? null : "true";
        list.Attributes["aria-disabled"] = IsEnabled ? null : "true";
        return Apply(list, "aw-listbox");
    }

    Div BuildOption(ItemView items, int index, bool tabStop)
    {
        var row = items.RowAt(index);
        bool selected = _selection.IsSelected(index);
        var option = new Div
        {
            Key = row,
            Ref = row.ElementRef ??= new Ref<Element>(),
            Role = Role.Option,
            Class = "aw-listbox-item",
            TabIndex = IsEnabled ? (tabStop ? 0 : -1) : null,
            Children = [PresentItem(items[index])],
            OnFocus = _ =>
            {
                _focusedRow = row;
                if (!tabStop) Invalidate();
            },
            OnClick = e => SelectFromPointer(items, row, e, rightButton: false),
            OnPointerDown = e =>
            {
                if (e.Button == PointerButtons.Right) SelectFromPointer(items, row, e, rightButton: true);
            },
            OnKeyDown = e => SelectFromKey(items, row, e),
        };
        option.Attributes["part"] = "item";
        option.Attributes["aria-selected"] = PartProps.Bool(selected);
        option.Attributes["data-state"] = selected ? "selected" : "unselected";
        option.Attributes["aria-disabled"] = IsEnabled ? null : "true";
        return option;
    }

    void SelectFromPointer(ItemView items, ItemRow row, PointerEvent e, bool rightButton)
    {
        if (!IsEnabled || e.DefaultPrevented || !ReferenceEquals(_selection.Source, items)) return;
        for (var target = e.Target; target is not null && !ReferenceEquals(target, e.CurrentTarget); target = target.Parent)
            if (target.IsFocusable || target.CanBeDisabled || target.Role == Role.Link) return;

        int index = items.IndexOf(row);
        if (index >= 0) ChangeSelection(() => SelectionGestures.PointerPressed(_selection, index, e.Modifiers, Mode, rightButton));
    }

    void SelectFromKey(ItemView items, ItemRow row, KeyEvent e)
    {
        if (!IsEnabled || e.DefaultPrevented || !ReferenceEquals(e.Target, e.CurrentTarget) || !ReferenceEquals(_selection.Source, items)) return;
        int index = items.IndexOf(row);
        if (index < 0) return;
        var key = e.Key == "Enter" ? " " : e.Key;
        if (key == " " && e.IsRepeat)
        {
            e.PreventDefault();
            return;
        }
        ChangeSelection(() =>
        {
            if (!SelectionGestures.HandleKeyDown(_selection, key, e.Modifiers, Mode, index, items.Count,
                i => items.RowAt(i).ElementRef?.Current is { IsFocusable: true, IsVisible: true }, s_navigation, out int next)) return;
            e.PreventDefault();
            if (next != index) items.RowAt(next).ElementRef?.Current?.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });
        });
    }

    private protected override void OnItemsChanged(Action apply)
    {
        if (SelectedIndex is int controlled)
        {
            apply();
            SetControlledIndex(controlled);
            Invalidate();
            return;
        }
        ApplySelection(() =>
        {
            apply();
            EnsureSelection();
        }, itemsChanged: true);
    }

    void SetControlledIndex(int index)
    {
        int normalized = index >= 0 && index < _selection.ItemCount ? index : -1;
        if (_selection.SelectedIndex != normalized) _selection.SelectedIndex = normalized;
    }

    void EnsureSelection()
    {
        if (!SelectedIndex.HasValue && (Mode & SelectionMode.AlwaysSelected) != 0 && _selection.Count == 0 && _selection.ItemCount > 0)
            _selection.Select(0);
    }

    void ChangeSelection(Action change)
    {
        if (!SelectedIndex.HasValue)
        {
            ApplySelection(change, itemsChanged: false);
            return;
        }

        // The model computes the requested index. Restore the rendered state until the owner updates the prop.
        int before = CurrentSelectedIndex;
        int anchor = _selection.AnchorIndex;
        int requested = before;
        using (_selection.BatchUpdate())
        {
            try
            {
                change();
                requested = CurrentSelectedIndex;
            }
            finally
            {
                if (CurrentSelectedIndex != before) _selection.SelectedIndex = before;
                _selection.AnchorIndex = anchor;
            }
        }
        if (requested != before) SelectedIndexChanged?.Invoke(requested);
    }

    void ApplySelection(Action change, bool itemsChanged)
    {
        var listener = SelectionChanged;
        List<SelectionModelSelectionChangedEventArgs<object?>>? changes = null;
        void Record(object? sender, SelectionModelSelectionChangedEventArgs<object?> args) =>
            (changes ??= new()).Add(listener is null ? args : new SelectionModelSelectionChangedEventArgs<object?>(
                args.SelectedIndexes, args.DeselectedIndexes, args.SelectedItems.ToArray(), args.DeselectedItems.ToArray()));

        int before = CurrentSelectedIndex;
        _selection.SelectionChanged += Record;
        try { change(); }
        finally { _selection.SelectionChanged -= Record; }

        int after = CurrentSelectedIndex;
        if (itemsChanged || before != after || changes is not null) Invalidate();
        if (before != after) SelectedIndexChanged?.Invoke(after);
        if (listener is not null && changes is not null)
            foreach (var args in changes) listener(args);
    }
}
