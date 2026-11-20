using System;
using System.Collections.Generic;
using AwAUI.Controls.Primitives;
using AwAUI.UI;

namespace AwAUI.Controls;

/// <summary>A tab label with optional IDs to associate it with an application-owned panel.</summary>
public sealed record TabStripItem(string Header, bool IsEnabled = true, object? Key = null)
{
    public string? Id { get; init; }
    public string? ControlsId { get; init; }
}

/// <summary>Displays a tab list whose panels are owned by the caller. Arrow keys move and select an enabled tab.</summary>
public sealed partial class TabStrip : Control
{
    readonly ControllableState<int> _selection = new();

    public IReadOnlyList<TabStripItem>? Items { get; set; }
    public int? SelectedIndex { get; set; }
    public int DefaultSelectedIndex { get; set; }
    public Action<int>? SelectedIndexChanged { get; set; }
    public Orientation Orientation { get; set; }

    protected override Node Build()
    {
        var items = Items ?? Array.Empty<TabStripItem>();
        int selection = _selection.Sync(SelectedIndex, DefaultSelectedIndex, SelectedIndexChanged);
        var list = TabHeaderList.Build(items.Count, selection, Orientation, AriaLabel,
            i => items[i].IsEnabled,
            i => new(items[i].Header, items[i].Key, items[i].Id, items[i].ControlsId),
            _selection.Set, out _);
        list.Attributes["data-orientation"] = TabHeaderList.Axis(Orientation);
        return Apply(list, "aw-tab-strip");
    }
}
