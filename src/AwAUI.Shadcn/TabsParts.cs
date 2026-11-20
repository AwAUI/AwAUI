using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using AwAUI.Controls.Primitives;
using AwAUI.Layout;
using AwAUI.Tailwind;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Shadcn;

internal static class TabsParts
{
    static long s_nextId;

    internal static string NextId(string part) =>
        "aw-" + part + "-" + Interlocked.Increment(ref s_nextId).ToString(CultureInfo.InvariantCulture);

    internal static string ValueId(string prefix, string value)
    {
        // Escape each UTF-16 code unit that could be ambiguous or break an ARIA ID reference.
        var id = new StringBuilder(prefix.Length + value.Length + 1).Append(prefix).Append('-');
        foreach (char c in value)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-') id.Append(c);
            else id.Append('_').Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        }
        return id.ToString();
    }

    internal static string Bool(bool value) => value ? "true" : "false";
    internal static string OpenState(bool value) => value ? "open" : "closed";
    internal static string Axis(Orientation value) => value == Orientation.Vertical ? "vertical" : "horizontal";

    internal static T Style<T>(T node, string slot, string? defaults, string? classes, AttributeDictionary? rest)
        where T : ElementNode
    {
        node.MergeAttributes(rest);
        node.Class = TailwindMerge.Cn(defaults, node.Class.Text, classes);
        node.Attributes["data-slot"] = slot;
        return node;
    }

    internal static void DirectionAttribute(ElementNode node, Direction? direction)
    {
        if (direction is { } dir) node.Attributes["dir"] = dir == Direction.Rtl ? "rtl" : "ltr";
    }

    internal static Direction ResolveDirection(Element? element, Direction? direction)
    {
        if (direction is { } explicitDirection) return explicitDirection;
        for (var current = element; current is not null; current = current.Parent)
        {
            var dir = current.GetAttribute("dir");
            if (string.Equals(dir, "rtl", StringComparison.OrdinalIgnoreCase)) return Direction.Rtl;
            if (string.Equals(dir, "ltr", StringComparison.OrdinalIgnoreCase)) return Direction.Ltr;
        }
        return Direction.Ltr;
    }

    internal static void Activate(UIButton button, Action action)
    {
        var onClick = button.OnClick;
        button.OnClick = e =>
        {
            onClick?.Invoke(e);
            if (!e.DefaultPrevented && !button.Disabled) action();
        };
        button.Attributes["type"] = "button";
        Disabled(button, button.Disabled);
    }

    internal static void Disabled(ElementNode node, bool disabled) =>
        node.Attributes["data-disabled"] = disabled ? "" : null;

    internal static void Hide(ElementNode node)
    {
        node.Attributes["hidden"] = "";
        node.Attributes["aria-hidden"] = "true";
        node.Style = (node.Style.Text?.TrimEnd().TrimEnd(';') ?? "") + ";display:none !important";
    }
}

/// <summary>Tracks each group's live buttons and delegates key navigation to Controls.</summary>
internal sealed class TabsFocus
{
    internal sealed class Item
    {
        internal Element? Element;
        internal bool Preferred;
    }

    readonly List<Item> _items = new();
    readonly State<Item?> _tabStop = new((Item?)null);
    Item? _focused;

    internal int GetTabIndex(Item item, bool preferred) =>
        _tabStop.Value is { } current ? (ReferenceEquals(current, item) ? 0 : -1) : (preferred ? 0 : -1);

    internal Action? Register(Item item, Element? element, bool preferred)
    {
        if (element is null) return null;
        item.Element = element;
        item.Preferred = preferred;
        _items.Add(item);
        Refresh();
        return () =>
        {
            _items.Remove(item);
            if (ReferenceEquals(_focused, item)) _focused = null;
            item.Element = null;
            Refresh();
        };
    }

    internal void Refresh()
    {
        _items.Sort(static (a, b) => Element.CompareTreeOrder(a.Element!, b.Element!));
        if (_focused?.Element is { IsFocused: true, IsFocusable: true })
        {
            _tabStop.Value = _focused;
            return;
        }
        Item? first = null;
        foreach (var item in _items)
        {
            if (item.Element is not { IsFocusable: true }) continue;
            if (item.Element.IsFocused)
            {
                _focused = item;
                _tabStop.Value = item;
                return;
            }
            first ??= item;
            if (item.Preferred)
            {
                _tabStop.Value = item;
                return;
            }
        }
        _tabStop.Value = first;
    }

    internal void Wire(UIButton button, Item item, Orientation? orientation, Direction? direction, bool loop,
        bool roving = true, Action? onKeyboardMove = null)
    {
        if (roving)
        {
            var onFocus = button.OnFocus;
            button.OnFocus = e =>
            {
                _focused = item;
                _tabStop.Value = item;
                onFocus?.Invoke(e);
            };
            var onBlur = button.OnBlur;
            button.OnBlur = e =>
            {
                if (ReferenceEquals(_focused, item)) _focused = null;
                Refresh();
                onBlur?.Invoke(e);
            };
        }
        var onKeyDown = button.OnKeyDown;
        button.OnKeyDown = e =>
        {
            onKeyDown?.Invoke(e);
            if (e.DefaultPrevented || !ReferenceEquals(e.Target, item.Element)) return;
            Move(e, item, orientation, direction, loop, onKeyboardMove is not null);
        };
        itemKeyboardActions(this, item, onKeyboardMove);
    }

    // Use the destination item's action when navigation also selects it, as in a radio group.
    readonly Dictionary<Item, Action> _keyboardActions = new();

    static void itemKeyboardActions(TabsFocus group, Item item, Action? action)
    {
        if (action is null) group._keyboardActions.Remove(item);
        else group._keyboardActions[item] = action;
    }

    void Move(KeyEvent e, Item item, Orientation? orientation, Direction? direction, bool loop, bool select)
    {
        _items.Sort(static (a, b) => Element.CompareTreeOrder(a.Element!, b.Element!));
        int current = _items.IndexOf(item);
        if (current < 0) return;
        var options = new RovingFocusOptions { Orientation = orientation, Direction = TabsParts.ResolveDirection(item.Element, direction), Loop = loop };
        int next = RovingFocus.HandleKeyDown(e.Key, e.Modifiers, current, _items.Count,
            i => _items[i].Element is { IsFocusable: true, IsVisible: true }, options);
        if (next < 0) return;
        e.PreventDefault();
        if (next == current) return;
        var destination = _items[next];
        var element = destination.Element!;
        if (element.Focus(new FocusOptions { Origin = FocusOrigin.Keyboard }) && select
            && _keyboardActions.TryGetValue(destination, out var activate)) activate();
    }
}
