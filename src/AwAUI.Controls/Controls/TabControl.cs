using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.Controls.Primitives;
using AwAUI.Layout;
using AwAUI.UI;
using UIButton = AwAUI.UI.Button;

namespace AwAUI.Controls;

/// <summary>A tab header and its panel content. Supply a stable key for tabs that can be reordered.</summary>
public sealed record TabItem(string Header, Node? Content, bool IsEnabled = true, object? Key = null);

/// <summary>Displays one panel at a time. Arrow keys move focus and activate the next available tab.</summary>
public sealed partial class TabControl : Control
{
    readonly string _id = PrimitiveIds.Next("tab-control");
    readonly ControllableState<int> _selection = new();

    public IReadOnlyList<TabItem>? Items { get; set; }
    public int? SelectedIndex { get; set; }
    public int DefaultSelectedIndex { get; set; }
    public Action<int>? SelectedIndexChanged { get; set; }
    public Orientation Orientation { get; set; }
    public string? TabListLabel { get; set; }

    protected override Node Build()
    {
        var items = Items ?? Array.Empty<TabItem>();
        int selection = _selection.Sync(SelectedIndex, DefaultSelectedIndex, SelectedIndexChanged);
        var headers = TabHeaderList.Build(items.Count, selection, Orientation, TabListLabel ?? AriaLabel,
            i => items[i].IsEnabled,
            i => new(items[i].Header, items[i].Key, PartId("tab", i), PartId("panel", i)),
            _selection.Set, out selection);
        var root = new Div { Children = [headers] };
        root.Attributes["data-orientation"] = TabHeaderList.Axis(Orientation);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            bool selected = i == selection;
            var panel = new Div
            {
                Id = PartId("panel", i), Key = item.Key ?? i, Role = Role.TabPanel,
                TabIndex = selected ? 0 : null, Style = selected ? default : new StyleValue("display:none"),
                Children = selected ? [item.Content] : [],
            };
            panel.Attributes["part"] = "tab-panel";
            panel.Attributes["aria-labelledby"] = PartId("tab", i);
            root.Children.Add(panel);
        }
        return Apply(root, "aw-tab-control");
    }

    string PartId(string part, int index) => _id + "-" + part + "-" + index.ToString(CultureInfo.InvariantCulture);
}

internal static class TabHeaderList
{
    internal readonly record struct Header(string Label, object? Key, string? Id, string? PanelId);

    internal static string Axis(Orientation orientation) => orientation == Orientation.Vertical ? "vertical" : "horizontal";

    internal static Div Build(int count, int requested, Orientation orientation, string? label,
        Func<int, bool> isEnabled, Func<int, Header> headerAt, Action<int> select, out int selectedIndex)
    {
        selectedIndex = (uint)requested < (uint)count && isEnabled(requested)
            ? requested : RovingFocus.Navigate(FocusIntent.First, -1, count, isEnabled, loop: false);
        var list = new Div { Role = Role.TabList, AriaLabel = label };
        list.Attributes["part"] = "tab-list";
        list.Attributes["aria-orientation"] = Axis(orientation);
        for (int i = 0; i < count; i++)
        {
            int index = i;
            var item = headerAt(i);
            bool enabled = isEnabled(i), selected = i == selectedIndex;
            var tab = new UIButton(item.Label)
            {
                Class = "aw-button", Role = Role.Tab, Id = item.Id, Key = item.Key ?? i,
                Disabled = !enabled, TabIndex = selected ? 0 : -1,
                OnKeyDown = e => Move(e, index, count, orientation, isEnabled, select),
                OnClick = e => { if (!e.DefaultPrevented && enabled) select(index); },
            };
            tab.Attributes["part"] = "tab";
            tab.Attributes["aria-selected"] = PartProps.Bool(selected);
            tab.Attributes["aria-controls"] = item.PanelId;
            list.Children.Add(tab);
        }
        return list;
    }

    static void Move(KeyEvent e, int index, int count, Orientation orientation, Func<int, bool> isEnabled, Action<int> select)
    {
        if (e.DefaultPrevented || !ReferenceEquals(e.Target, e.CurrentTarget) || e.CurrentTarget?.Parent is not { } list) return;
        var direction = Direction.Ltr;
        for (Element? current = list; current is not null; current = current.Parent)
        {
            var dir = current.GetAttribute("dir");
            if (string.Equals(dir, "rtl", StringComparison.OrdinalIgnoreCase)) { direction = Direction.Rtl; break; }
            if (string.Equals(dir, "ltr", StringComparison.OrdinalIgnoreCase)) break;
        }
        var options = new RovingFocusOptions { Orientation = orientation, Direction = direction, Loop = true };
        int next = RovingFocus.HandleKeyDown(e.Key, e.Modifiers, index, count, isEnabled, options);
        if (next < 0) return;
        e.PreventDefault();
        if (next != index && next < list.Children.Count && list.Children[next].Focus(new FocusOptions { Origin = FocusOrigin.Keyboard }))
            select(next);
    }
}
