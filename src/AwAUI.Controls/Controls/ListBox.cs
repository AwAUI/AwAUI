using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AwAUI.Controls.Primitives;
using AwAUI.Platform;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>
/// A list of selectable options with a single tab stop. Item templates should contain non-interactive content.
/// Set <see cref="IsVirtualized"/> to mount only the rows in and near the viewport; selection and keyboard
/// navigation keep working across the realized range and scroll the list to rows that are not mounted yet.
/// </summary>
public sealed partial class ListBox : ItemsControl
{
    static readonly RovingFocusOptions s_navigation = new() { Orientation = Orientation.Vertical };
    readonly SelectionModel<object?> _selection = new();
    ItemRow? _focusedRow;
    bool _initialized;
    int _pendingFocusIndex = -1;

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

        var list = new Div { Role = Role.ListBox, Style = "overflow:auto;" };
        if (IsVirtualized)
        {
            var host = Host;
            host.Mount(Root);
            bool fixedHeight = ItemHeight is double requested && requested > 0 && !double.IsInfinity(requested);
            double height = fixedHeight ? ItemHeight!.Value : 0;
            double estimate = EstimatedItemHeight > 0 && !double.IsInfinity(EstimatedItemHeight) ? EstimatedItemHeight : 32;
            // 定高与变高是两种行结构：SetOptions 在两者之间切换时会丢弃测量
            host.SetOptions(new VirtualizerOptions
            {
                ItemSize = fixedHeight ? height : null,
                EstimatedItemSize = estimate,
                Overscan = Math.Max(0, Overscan),
            });
            var realized = host.Begin(items.Count, tabStop);
            list.ScrollController = host.Controller;
            var extent = new Div { Style = "position:relative;height:" + Pixels(host.Virtualizer.TotalSize) };
            extent.Attributes["part"] = "extent";
            // 变高行不写固定高度：布局后按内容高度测量，再把尺寸写回虚拟化器
            string rowStyle = "position:absolute;inset-inline:0;box-sizing:border-box;overflow:clip;"
                + (fixedHeight ? "height:" + Pixels(height) + ";" : "");
            foreach (var item in realized)
            {
                extent.Children.Add(BuildOption(items, item.Index, item.Index == tabStop, rowStyle + "top:" + Pixels(item.Start) + ";"));
                host.Track(item.Index, items.RowAt(item.Index).ElementRef!);
            }
            list.Children.Add(extent);
            QueuePendingFocus(items);
        }
        else
        {
            Host.Deactivate();
            var options = new NodeList();
            for (int i = 0; i < items.Count; i++) options.Add(BuildOption(items, i, i == tabStop, null));
            list.Children = PresentItems(options);
        }
        list.Attributes["aria-multiselectable"] = _selection.SingleSelect ? null : "true";
        list.Attributes["aria-disabled"] = IsEnabled ? null : "true";
        return Apply(list, "aw-listbox");
    }

    Div BuildOption(ItemView items, int index, bool tabStop, string? rowStyle)
    {
        var row = items.RowAt(index);
        bool selected = _selection.IsSelected(index);
        var option = new Div
        {
            Key = row,
            Ref = row.ElementRef ??= new Ref<Element>(),
            Role = Role.Option,
            Class = "aw-listbox-item",
            Style = rowStyle,
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
        option.Attributes["aria-posinset"] = (index + 1).ToString(CultureInfo.InvariantCulture);
        option.Attributes["aria-setsize"] = items.Count.ToString(CultureInfo.InvariantCulture);
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
        // 虚拟化时行可能尚未实现：导航不按「已挂载的可聚焦元素」判断，所有项都可到达
        Func<int, bool>? canFocus = IsVirtualized
            ? null
            : i => items.RowAt(i).ElementRef?.Current is { IsFocusable: true, IsVisible: true };
        ChangeSelection(() =>
        {
            if (!SelectionGestures.HandleKeyDown(_selection, key, e.Modifiers, Mode, index, items.Count,
                canFocus, s_navigation, out int next)) return;
            e.PreventDefault();
            if (next != index) FocusIndex(items, next);
        });
    }

    /// <summary>把焦点移到第 <paramref name="index"/> 项；该项尚未实现（虚拟化）时先滚动到它，重建后再聚焦。</summary>
    void FocusIndex(ItemView items, int index)
    {
        if (index < 0 || index >= items.Count) return;
        if (items.RowAt(index).ElementRef?.Current is { } element && element.IsAttached && element.IsFocusable)
        {
            element.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });
            return;
        }
        if (!IsVirtualized) return;
        _pendingFocusIndex = index;
        ScrollIndexIntoView(index);
        Invalidate();
    }

    void ScrollIndexIntoView(int index)
    {
        var host = Host;
        host.SyncViewport();
        double target = host.Virtualizer.GetOffsetForIndex(index);
        var offset = host.Controller.Offset;
        if (Math.Abs(target - offset.Y) < 0.5) return;
        host.Controller.JumpTo(new Point(offset.X, (float)target));
    }

    /// <summary>本次构建实现了待聚焦的行之后，在布局完成时把焦点移过去。</summary>
    void QueuePendingFocus(ItemView items)
    {
        int index = _pendingFocusIndex;
        if (index < 0) return;
        _pendingFocusIndex = -1;
        if (index >= items.Count || Root is not { } root) return;
        var row = items.RowAt(index);
        root.RequestAfterLayout(() =>
        {
            if (!ReferenceEquals(Root, root)) return;
            if (row.ElementRef?.Current is { IsFocusable: true } element && element.IsAttached)
                element.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard });
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
