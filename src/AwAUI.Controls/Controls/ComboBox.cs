using System;
using System.Runtime.CompilerServices;
using System.Text;
using AwAUI.Controls.Primitives;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;
using UIPortal = AwAUI.UI.Portal;
using UIText = AwAUI.UI.Text;

namespace AwAUI.Controls;

/// <summary>A select-only dropdown with a single selection. Item templates should contain non-interactive content.</summary>
public sealed partial class ComboBox : ItemsControl
{
    const KeyModifiers CommandModifiers = KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta;
    const int PageSize = 10;
    readonly string _triggerId = PrimitiveIds.Next("combobox");
    readonly string _popupId = PrimitiveIds.Next("combobox-popup");
    readonly PopperScope _popper = new();
    readonly Ref<Element> _popup = new();
    readonly ConditionalWeakTable<ItemRow, string> _optionIds = new();
    readonly SelectionModel<object?> _selection = new();
    readonly ControllableState<bool> _open = new();
    readonly TypeAhead _typeAhead = new();
    DismissableLayer<Element>? _layer;
    ItemRow? _highlight;
    FocusIntent _openingIntent;
    bool _initialized;
    bool _enabled;
    bool _renderedOpen;
    bool _closeRequested;
    bool _revealHighlight;

    /// <summary>Controls the selected index; -1 clears it and null uses internal state.</summary>
    public int? SelectedIndex { get; set; }

    /// <summary>Initial selection when <see cref="SelectedIndex"/> is null.</summary>
    public int DefaultSelectedIndex { get; set; } = -1;

    /// <summary>Reports an index change, or requests one when selection is controlled.</summary>
    public Action<int>? SelectedIndexChanged { get; set; }

    /// <summary>Controls whether the dropdown is open. Null uses internal state.</summary>
    public bool? IsDropDownOpen { get; set; }

    /// <summary>Initial open state when <see cref="IsDropDownOpen"/> is null.</summary>
    public bool DefaultDropDownOpen { get; set; }

    /// <summary>Reports an open state change, or requests one when open state is controlled.</summary>
    public Action<bool>? DropDownOpenChanged { get; set; }

    public bool IsEnabled { get; set; } = true;
    public string? Placeholder { get; set; }

    /// <summary>Provides the trigger label and typeahead text for an item. Set this for custom item nodes without plain text.</summary>
    public Func<object?, string?>? ItemText { get; set; }

    /// <summary>Returns false for an option that cannot be reached or selected by the user.</summary>
    public Func<object?, bool>? IsItemEnabled { get; set; }

    public int CurrentSelectedIndex => _selection.SelectedIndex;
    public object? SelectedItem => _selection.SelectedItem;
    public bool IsOpen => _enabled && _open.Value;

    // Popper's positioning wrapper must also pass through pointer hits during the exit animation.
    protected override string? GlobalStyleSheet =>
        "[data-aw-popper-content-wrapper]:has(> .aw-combobox-popup[data-state=closed]) { pointer-events: none; }";

    protected override Node Build()
    {
        var items = ReadItems();
        _selection.Source = items;
        if (SelectedIndex is int controlled) SyncSelection(items, controlled);
        else if (!_initialized) SyncSelection(items, DefaultSelectedIndex);
        _initialized = true;

        var trigger = Apply(new UIButton
        {
            Role = Role.ComboBox,
            Disabled = !IsEnabled,
            OnClick = e =>
            {
                if (e.DefaultPrevented || !_enabled) return;
                if (IsOpen) Close();
                else Open();
            },
            OnKeyDown = e => HandleKey(items, e),
            // The dismissal stack handles focus moving to another element, but not focus being cleared.
            OnBlur = e => { if (e.RelatedTarget is null) Close(); },
        }, "aw-combobox");
        trigger.Id ??= _triggerId;
        _enabled = !trigger.Disabled;
        bool open = _open.Sync(IsDropDownOpen, _enabled && DefaultDropDownOpen, DropDownOpenChanged) && _enabled;
        _closeRequested = false;

        int selected = CurrentSelectedIndex;
        int active = HighlightedIndex(items);
        if (open && !CanSelect(items, active))
        {
            active = _openingIntent switch
            {
                FocusIntent.First or FocusIntent.Last => Navigate(items, _openingIntent, -1),
                FocusIntent.PageBackward or FocusIntent.PageForward => Navigate(items, _openingIntent, selected),
                _ when CanSelect(items, selected) => selected,
                _ => Navigate(items, _openingIntent == FocusIntent.Previous ? FocusIntent.Last : FocusIntent.First, -1),
            };
            _highlight = active >= 0 ? items.RowAt(active) : null;
            _revealHighlight = true;
        }
        else if (!open)
        {
            if (_renderedOpen) _typeAhead.Reset();
            _highlight = null;
        }
        _openingIntent = FocusIntent.None;
        _renderedOpen = open;

        var value = new UIText(selected < 0 ? Placeholder : TextOf(items[selected])) { Class = "aw-combobox-value" };
        value.Attributes["part"] = "value";
        var indicator = new Div { Class = "aw-combobox-indicator" };
        indicator.Attributes["part"] = "indicator";
        indicator.Attributes["aria-hidden"] = "true";
        trigger.Children = [value, indicator];
        trigger.Attributes["part"] = "trigger";
        trigger.Attributes["type"] = "button";
        trigger.Attributes["data-state"] = PartProps.OpenState(open);
        trigger.Attributes["data-disabled"] = _enabled ? null : "";
        trigger.Attributes["data-placeholder"] = selected < 0 ? "" : null;
        trigger.Attributes["aria-haspopup"] = "listbox";
        trigger.Attributes["aria-expanded"] = PartProps.Bool(open);
        trigger.Attributes["aria-disabled"] = _enabled ? null : "true";
        trigger.Attributes["aria-controls"] = open ? _popupId : null;
        trigger.Attributes["aria-activedescendant"] = open && _highlight is { } row ? OptionId(row) : null;

        _popper.DefaultAnchor = this;
        UseDismissal(open);
        UseHighlightReveal(open);

        return Provide(_popper, new Fragment([trigger, new PresenceGate
        {
            Present = open,
            Children = [new UIPortal { Children = [BuildPopup(items, trigger, open)] }],
        }]));
    }

    PopperContent BuildPopup(ItemView items, UIButton trigger, bool open)
    {
        var options = new NodeList();
        // Keep the option tree during exit, but only the open popup can receive input.
        bool keepOptions = open || _popup.Current is not null;
        if (keepOptions)
            for (int i = 0; i < items.Count; i++) options.Add(BuildOption(items, i, open));

        var attrs = new AttributeDictionary
        {
            ["part"] = "popup",
            ["data-state"] = PartProps.OpenState(open),
            ["aria-hidden"] = open ? null : "true",
            ["onpointerdown"] = (Action<PointerEvent>)(e => e.PreventDefault()),
        };
        if (trigger.Attributes["aria-labelledby"] is { } labelledBy) attrs["aria-labelledby"] = labelledBy;
        else if (trigger.AriaLabel is { } label) attrs["aria-label"] = label;
        else attrs["aria-labelledby"] = trigger.Id;

        return new PopperContent
        {
            Id = _popupId,
            Ref = _popup,
            Role = Role.ListBox,
            Align = PopperAlign.Start,
            SideOffset = 4,
            CollisionPadding = 8,
            Class = "aw-combobox-popup",
            Style = "overflow: auto; scroll-behavior: auto; overscroll-behavior: contain; " +
                "min-width: var(--aw-popper-anchor-width, 0px); max-width: var(--aw-popper-available-width, 100vw); " +
                "max-height: var(--aw-combobox-max-height, min(20rem, var(--aw-popper-available-height, 20rem)))" +
                (open ? "" : "; pointer-events: none"),
            Attributes = attrs,
            Children = keepOptions ? PresentItems(options) : options,
        };
    }

    Div BuildOption(ItemView items, int index, bool open)
    {
        var item = items[index];
        var row = items.RowAt(index);
        bool enabled = CanSelect(items, index);
        bool selected = index == CurrentSelectedIndex;
        var option = new Div
        {
            Key = row,
            Id = OptionId(row),
            Ref = row.ElementRef ??= new Ref<Element>(),
            Role = Role.Option,
            Class = "aw-combobox-item",
            Children = [ItemText is not null && ItemTemplate is null && item is not Node
                ? new UIText(TextOf(item)) : PresentItem(item)],
            OnClick = e =>
            {
                if (e.DefaultPrevented || !IsOpen || _closeRequested) return;
                int target = items.IndexOf(row);
                if (!ReferenceEquals(items, _selection.Source) || !CanSelect(items, target)) return;
                FocusTrigger();
                Close(items, target);
            },
            OnPointerMove = e =>
            {
                if (e.DefaultPrevented || e.PointerType == PointerType.Touch || !IsOpen || _closeRequested) return;
                if (ReferenceEquals(items, _selection.Source) && CanSelect(items, items.IndexOf(row)))
                    SetHighlight(row, reveal: false);
            },
        };
        option.Attributes["part"] = "item";
        option.Attributes["aria-selected"] = PartProps.Bool(selected);
        option.Attributes["aria-disabled"] = enabled ? null : "true";
        option.Attributes["data-state"] = selected ? "selected" : "unselected";
        option.Attributes["data-highlighted"] = open && ReferenceEquals(row, _highlight) ? "" : null;
        option.Attributes["data-disabled"] = enabled ? null : "";
        return option;
    }

    void UseDismissal(bool open)
    {
        _layer ??= new DismissableLayer<Element>(target =>
            _popper.AnchorElement?.IsInclusiveAncestorOf(target) == true || _popup.Current?.IsInclusiveAncestorOf(target) == true)
        {
            OnDismiss = reason =>
            {
                if (!IsOpen || _closeRequested) return;
                if (reason == DismissReason.EscapeKey) FocusTrigger(FocusOrigin.Keyboard);
                Close();
            },
        };
        _layer.Parent = TryUse<DismissableLayerScope>(out var scope) ? scope.Layer : null;
        var layer = _layer;
        UseEffect(() =>
        {
            if (!open || Root is not { } root) return null;
            var services = PrimitiveRoot.For(root);
            services.AddLayer(layer);
            root.RequestAfterLayout(() =>
            {
                if (ReferenceEquals(Root, root) && IsOpen && !_closeRequested) FocusTrigger();
            });
            return () => services.RemoveLayer(layer);
        }, open);

        UseEffect(() =>
        {
            if (!_enabled && !_open.IsControlled && _open.Value) _open.Set(false);
            return null;
        }, _enabled);
    }

    void FocusTrigger(FocusOrigin origin = FocusOrigin.Programmatic) =>
        _popper.AnchorElement?.Focus(new FocusOptions { Origin = origin, PreventScroll = true });

    void UseHighlightReveal(bool open)
    {
        bool reveal = _revealHighlight;
        var row = _highlight;
        _revealHighlight = false;
        UseEffect(() =>
        {
            if (!open || !reveal || row is null || Root is not { } root) return null;
            root.RequestAfterLayout(() =>
            {
                if (ReferenceEquals(Root, root) && IsOpen && !_closeRequested && ReferenceEquals(_highlight, row)
                    && _popup.Current is { } popup && row.ElementRef?.Current is { } option)
                    _ = popup.ScrollController.ScrollIntoView(option, ScrollAlignment.Nearest);
            });
            return null;
        }, open, row, reveal);
    }

    void HandleKey(ItemView items, KeyEvent e)
    {
        if (e.DefaultPrevented || !_enabled || !ReferenceEquals(items, _selection.Source)) return;
        var modifiers = e.Modifiers;
        int active = HighlightedIndex(items);
        bool open = IsOpen;

        if (e.Key == "Tab" && open && (modifiers & CommandModifiers) == 0)
        {
            Close(items, active);
            return; // Focus is still on the trigger, so normal Tab order applies.
        }
        if (modifiers == KeyModifiers.Alt && e.Key is ("ArrowDown" or "ArrowUp"))
        {
            if (open == (e.Key == "ArrowUp"))
            {
                e.PreventDefault();
                if (!e.IsRepeat)
                {
                    if (open) Close(items, active);
                    else Open();
                }
            }
            return;
        }
        if (e.Key == "Enter" || (e.Key == " " && !_typeAhead.IsTypingAhead(e.Timestamp)))
        {
            if (e.IsRepeat || (modifiers & CommandModifiers) != 0) e.PreventDefault();
            else if (open)
            {
                e.PreventDefault();
                Close(items, active);
            }
            // When closed, native button activation opens the popup.
            return;
        }

        var intent = RovingFocus.GetFocusIntent(e.Key, Orientation.Vertical, Direction.Ltr, PageSize);
        if (intent != FocusIntent.None && (modifiers & (CommandModifiers | KeyModifiers.Shift)) == 0)
        {
            e.PreventDefault();
            _typeAhead.Reset();
            if (open)
            {
                int next = Navigate(items, intent, active);
                if (next >= 0) SetHighlight(items.RowAt(next));
            }
            else if (!e.IsRepeat) Open(intent);
            return;
        }
        if (!TypeAhead.IsTypeAheadKey(e.Key, modifiers)) return;
        e.PreventDefault();
        var labels = new string?[items.Count];
        for (int i = 0; i < items.Count; i++)
            if (CanSelect(items, i)) labels[i] = TextOf(items[i])?.Trim();
        int match = _typeAhead.HandleKey(e.Key, e.Timestamp, labels, open ? active : -1);
        if (open)
        {
            if (match >= 0) SetHighlight(items.RowAt(match));
        }
        else Open(highlight: match >= 0 ? items.RowAt(match) : null, keepSearch: true);
    }

    void Open(FocusIntent intent = FocusIntent.None, ItemRow? highlight = null, bool keepSearch = false)
    {
        if (!_enabled || IsOpen) return;
        if (!keepSearch) _typeAhead.Reset();
        _openingIntent = intent;
        _highlight = highlight;
        _revealHighlight = true;
        _open.Set(true);
        // Also consume the initial navigation intent if a controlled owner leaves the popup closed.
        Invalidate();
    }

    void Close(ItemView? items = null, int index = -1)
    {
        if (!IsOpen || _closeRequested) return;
        // One interaction can request dismissal twice: first on pointer down or Tab, then on focus change.
        _closeRequested = true;
        _typeAhead.Reset();
        try
        {
            if (items is not null && CanSelect(items, index) && index != CurrentSelectedIndex)
            {
                if (!SelectedIndex.HasValue) _selection.SelectedIndex = index;
                SelectedIndexChanged?.Invoke(index);
            }
        }
        finally
        {
            _open.Set(false);
            Invalidate();
        }
    }

    void SetHighlight(ItemRow row, bool reveal = true)
    {
        if (_closeRequested || ReferenceEquals(_highlight, row)) return;
        _highlight = row;
        _revealHighlight = reveal;
        Invalidate();
    }

    int HighlightedIndex(ItemView items) => _highlight is { } row ? items.IndexOf(row) : -1;
    string OptionId(ItemRow row) => _optionIds.GetValue(row, static _ => PrimitiveIds.Next("combobox-option"));
    bool CanSelect(ItemView items, int index) => (uint)index < (uint)items.Count && (IsItemEnabled?.Invoke(items[index]) ?? true);
    int Navigate(ItemView items, FocusIntent intent, int index) =>
        RovingFocus.Navigate(intent, index, items.Count, i => CanSelect(items, i), loop: false, pageSize: PageSize);

    void SyncSelection(ItemView items, int index)
    {
        int next = (uint)index < (uint)items.Count ? index : -1;
        if (CurrentSelectedIndex != next) _selection.SelectedIndex = next;
    }

    private protected override void OnItemsChanged(Action apply)
    {
        int before = CurrentSelectedIndex;
        apply();
        if (SelectedIndex is int controlled && _selection.Source is ItemView items) SyncSelection(items, controlled);
        Invalidate();
        if (!SelectedIndex.HasValue && before != CurrentSelectedIndex)
            SelectedIndexChanged?.Invoke(CurrentSelectedIndex);
    }

    string? TextOf(object? item)
    {
        if (ItemText is { } text) return text(item);
        if (item is not Node node) return item?.ToString();
        var result = new StringBuilder();
        AppendText(node, result);
        return result.ToString();
    }

    static void AppendText(Node node, StringBuilder result)
    {
        switch (node)
        {
            case TextNode text:
                result.Append(text.Text);
                return;
            case UIText text:
                result.Append(text.Content);
                break;
            case TextBlock text:
                result.Append(text.Text);
                break;
        }
        var children = node switch
        {
            ElementNode element => element.Children,
            Fragment fragment => fragment.Children,
            TextBlock text => text.Children,
            _ => null,
        };
        if (children is not null)
            foreach (var child in children) AppendText(child, result);
    }
}
