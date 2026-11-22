using System;
using System.Collections.Generic;
using AwAUI.Platform;
using AwAUI.Win32;

namespace AwAUI.Platform.UnitTests;

/// <summary>
/// Win32 UI Automation 桥的纯逻辑：角色→控件类型/模式、屏幕边界换算、快照索引与增量 diff、
/// provider 的属性读取/导航/动作派发。真实 UIA 客户端只能在 Windows 上冒烟，这里在任何系统上验证语义。
/// </summary>
public class Win32AccessibilityTests
{
    // ------------------------------------------------------------ 角色 → ControlType / Pattern

    [Theory]
    [InlineData("root", 50032)]
    [InlineData("button", 50000)]
    [InlineData("link", 50005)]
    [InlineData("checkbox", 50002)]
    [InlineData("switch", 50002)]
    [InlineData("radio", 50013)]
    [InlineData("textbox", 50004)]
    [InlineData("searchbox", 50004)]
    [InlineData("combobox", 50003)]
    [InlineData("list", 50008)]
    [InlineData("listitem", 50007)]
    [InlineData("option", 50007)]
    [InlineData("tab", 50019)]
    [InlineData("slider", 50015)]
    [InlineData("spinbutton", 50016)]
    [InlineData("progressbar", 50012)]
    [InlineData("heading", 50020)]
    [InlineData("img", 50006)]
    [InlineData("table", 50036)]
    [InlineData("grid", 50028)]
    [InlineData("cell", 50029)]
    [InlineData("dialog", 50032)]
    [InlineData("group", 50026)]
    [InlineData("secret-role", 50025)]
    public void Role_maps_to_control_type(string role, int expected)
    {
        Assert.Equal(expected, Win32AccessibilityMapping.ControlType(role));
    }

    [Fact]
    public void Patterns_reflect_role_states_and_actions()
    {
        var checkbox = Node("cb", "checkbox", states: new[] { "checked" },
            actions: new[] { AccessibilityActionKind.Toggle });
        Assert.True(Win32AccessibilityMapping.HasPattern(checkbox, Win32AccessibilityPattern.Toggle));

        var textbox = Node("tb", "textbox", text: new AccessibilityTextSnapshot { Value = "hi" },
            actions: new[] { AccessibilityActionKind.SetValue });
        Assert.True(Win32AccessibilityMapping.HasPattern(textbox, Win32AccessibilityPattern.Value));

        var slider = Node("sl", "slider", value: "42");
        Assert.True(Win32AccessibilityMapping.HasPattern(slider, Win32AccessibilityPattern.RangeValue));

        var item = Node("li", "listitem", states: new[] { "selected" });
        Assert.True(Win32AccessibilityMapping.HasPattern(item, Win32AccessibilityPattern.SelectionItem));

        var expander = Node("ex", "button", actions: new[]
        {
            AccessibilityActionKind.Expand, AccessibilityActionKind.Collapse,
        });
        Assert.True(Win32AccessibilityMapping.HasPattern(expander, Win32AccessibilityPattern.ExpandCollapse));

        var list = Node("l", "list");
        Assert.True(Win32AccessibilityMapping.HasPattern(list, Win32AccessibilityPattern.Selection));

        var table = Node("t", "table");
        Assert.True(Win32AccessibilityMapping.HasPattern(table, Win32AccessibilityPattern.Grid));

        var cell = Node("c", "cell");
        Assert.True(Win32AccessibilityMapping.HasPattern(cell, Win32AccessibilityPattern.GridItem));

        var plain = Node("p", "group");
        Assert.Equal(Win32AccessibilityPattern.None,
            Win32AccessibilityMapping.Patterns(plain) & ~Win32AccessibilityPattern.Selection);
    }

    [Fact]
    public void Toggle_and_expand_states_map_to_uia_enums()
    {
        Assert.Equal(1, Win32AccessibilityMapping.ToggleState(Node("a", "checkbox", states: new[] { "checked" })));
        Assert.Equal(2, Win32AccessibilityMapping.ToggleState(Node("b", "checkbox", states: new[] { "mixed" })));
        Assert.Equal(0, Win32AccessibilityMapping.ToggleState(Node("c", "checkbox")));

        Assert.Equal(1, Win32AccessibilityMapping.ExpandCollapseState(Node("d", "button", states: new[] { "expanded" })));
        Assert.Equal(0, Win32AccessibilityMapping.ExpandCollapseState(Node("e", "button",
            actions: new[] { AccessibilityActionKind.Expand })));
        Assert.Equal(3, Win32AccessibilityMapping.ExpandCollapseState(Node("f", "button")));
    }

    [Fact]
    public void Names_access_key_and_item_status_are_constructed_from_snapshot()
    {
        var node = Node("save", "button", name: "Save &File", states: new[] { "checked", "disabled" });
        Assert.Equal("Save &File", Win32AccessibilityMapping.Name(node));
        Assert.Equal("F", Win32AccessibilityMapping.AccessKey(node));
        Assert.Equal("", Win32AccessibilityMapping.AcceleratorKey(node));
        Assert.Equal("checked, disabled", Win32AccessibilityMapping.ItemStatus(node));

        var doubled = Node("amp", "button", name: "A && B");
        Assert.Equal("", Win32AccessibilityMapping.AccessKey(doubled));
    }

    // ------------------------------------------------------------ 几何

    [Fact]
    public void Client_dip_bounds_convert_to_screen_physical_pixels()
    {
        var geometry = new Win32AccessibilityClientGeometry(2f, 100, 200);
        var rect = Win32AccessibilityGeometry.ToScreenRect(new Rect(10, 20, 30, 40), geometry);

        Assert.Equal(120, rect.Left);
        Assert.Equal(240, rect.Top);
        Assert.Equal(180, rect.Right);
        Assert.Equal(320, rect.Bottom);
        Assert.Equal(60, rect.Width);
        Assert.Equal(80, rect.Height);
    }

    [Fact]
    public void Empty_or_non_finite_bounds_are_offscreen()
    {
        var client = new Rect(0, 0, 800, 600);
        Assert.True(Win32AccessibilityGeometry.IsOffscreen(default, client));
        Assert.True(Win32AccessibilityGeometry.IsOffscreen(new Rect(float.NaN, 0, 10, 10), client));
        Assert.True(Win32AccessibilityGeometry.IsOffscreen(new Rect(900, 0, 10, 10), client));
        Assert.False(Win32AccessibilityGeometry.IsOffscreen(new Rect(10, 10, 10, 10), client));
    }

    [Fact]
    public void Orientation_is_only_reported_for_directional_roles()
    {
        Assert.Equal(1, Win32AccessibilityGeometry.OrientationFor("slider", new Rect(0, 0, 100, 10)));
        Assert.Equal(2, Win32AccessibilityGeometry.OrientationFor("slider", new Rect(0, 0, 10, 100)));
        Assert.Equal(0, Win32AccessibilityGeometry.OrientationFor("button", new Rect(0, 0, 100, 10)));
    }

    // ------------------------------------------------------------ 索引与导航

    [Fact]
    public void Index_keeps_stable_int_ids_across_versions()
    {
        var map = new Win32AccessibilityIdMap();
        var first = Snapshot(1, "root",
            Node("root", "root", name: "App", childIds: new[] { "a", "b" }),
            Node("a", "button", parent: "root", name: "A"),
            Node("b", "button", parent: "root", name: "B"));
        var second = Snapshot(2, "root",
            Node("root", "root", name: "App", childIds: new[] { "a", "b" }),
            Node("a", "button", parent: "root", name: "A"),
            Node("b", "button", parent: "root", name: "B"));

        var firstIndex = Win32AccessibilityIndex.Build(first, map);
        var secondIndex = Win32AccessibilityIndex.Build(second, map);

        Assert.Equal(firstIndex.EntryId("root"), secondIndex.EntryId("root"));
        Assert.Equal(firstIndex.EntryId("a"), secondIndex.EntryId("a"));
        Assert.Equal(firstIndex.EntryId("b"), secondIndex.EntryId("b"));
        Assert.True(firstIndex.TryGet(firstIndex.EntryId("a"), out var a));
        Assert.Equal(firstIndex.EntryId("root"), a!.ParentId);
        Assert.Equal(2, firstIndex.Entries[firstIndex.RootId].Children.Length);
    }

    [Fact]
    public void Provider_core_navigates_parent_and_children()
    {
        var core = new Win32AccessibilityProviderCore();
        Assert.True(core.TryUpdate(Snapshot(1, "root",
            Node("root", "root", name: "App", childIds: new[] { "a", "b" }),
            Node("a", "button", parent: "root", name: "A"),
            Node("b", "button", parent: "root", name: "B"))));

        int rootId = core.RootId;
        int aId = core.Index!.EntryId("a");
        int bId = core.Index!.EntryId("b");

        Assert.True(core.TryNavigate(aId, Win32UiaConstants.NavigateParent, out int parent));
        Assert.Equal(rootId, parent);
        Assert.True(core.TryNavigate(rootId, Win32UiaConstants.NavigateFirstChild, out int first));
        Assert.Equal(aId, first);
        Assert.True(core.TryNavigate(rootId, Win32UiaConstants.NavigateLastChild, out int last));
        Assert.Equal(bId, last);
        Assert.True(core.TryNavigate(aId, Win32UiaConstants.NavigateNextSibling, out int next));
        Assert.Equal(bId, next);
        Assert.False(core.TryNavigate(bId, Win32UiaConstants.NavigateNextSibling, out _));
        Assert.False(core.TryNavigate(rootId, Win32UiaConstants.NavigateParent, out _));
    }

    [Fact]
    public void Runtime_id_is_append_runtime_id_plus_stable_node_id()
    {
        var core = new Win32AccessibilityProviderCore();
        core.TryUpdate(Snapshot(1, "root", Node("root", "root", name: "App")));
        var id = core.Index!.EntryId("root");
        Assert.Equal(new[] { 42, id }, core.GetRuntimeId(id));
    }

    // ------------------------------------------------------------ 属性读取

    [Fact]
    public void Provider_core_reads_common_properties()
    {
        var core = new Win32AccessibilityProviderCore();
        core.Geometry = new Win32AccessibilityClientGeometry(2f, 50, 60);
        core.ClientBounds = new Rect(0, 0, 800, 600);
        core.TryUpdate(Snapshot(1, "root",
            Node("root", "root", name: "App", bounds: new Rect(0, 0, 800, 600), childIds: new[] { "btn" }),
            Node("btn", "button", parent: "root", name: "Save", bounds: new Rect(10, 10, 100, 30),
                actions: new[] { AccessibilityActionKind.Invoke, AccessibilityActionKind.Focus })));

        int buttonId = core.Index!.EntryId("btn");
        var name = core.GetProperty(buttonId, Win32UiaPropertyIds.Name);
        Assert.Equal(Win32AccessibilityValueKind.String, name.Kind);
        Assert.Equal("Save", name.StringValue);

        var controlType = core.GetProperty(buttonId, Win32UiaPropertyIds.ControlType);
        Assert.Equal(Win32AccessibilityValueKind.Int32, controlType.Kind);
        Assert.Equal(50000, controlType.IntValue);

        var enabled = core.GetProperty(buttonId, Win32UiaPropertyIds.IsEnabled);
        Assert.True(enabled.BoolValue);

        var focusable = core.GetProperty(buttonId, Win32UiaPropertyIds.IsKeyboardFocusable);
        Assert.True(focusable.BoolValue);

        var bounds = core.GetProperty(buttonId, Win32UiaPropertyIds.BoundingRectangle);
        Assert.Equal(Win32AccessibilityValueKind.Rect, bounds.Kind);
        Assert.Equal(70, bounds.RectValue.Left);
        Assert.Equal(80, bounds.RectValue.Top);
        Assert.Equal(270, bounds.RectValue.Right);
        Assert.Equal(140, bounds.RectValue.Bottom);

        var invokeAvailable = core.GetProperty(buttonId, Win32UiaPropertyIds.IsInvokePatternAvailable);
        Assert.True(invokeAvailable.BoolValue);
    }

    [Fact]
    public void Range_value_properties_use_snapshot_value_and_defaults()
    {
        var core = new Win32AccessibilityProviderCore();
        core.TryUpdate(Snapshot(1, "root",
            Node("root", "root", childIds: new[] { "slider" }),
            Node("slider", "slider", parent: "root", value: "42",
                actions: new[] { AccessibilityActionKind.SetValue })));

        int id = core.Index!.EntryId("slider");
        Assert.Equal(42, core.GetProperty(id, Win32UiaPropertyIds.RangeValueValue).DoubleValue);
        Assert.Equal(0, core.GetProperty(id, Win32UiaPropertyIds.RangeValueMinimum).DoubleValue);
        Assert.Equal(100, core.GetProperty(id, Win32UiaPropertyIds.RangeValueMaximum).DoubleValue);
        Assert.False(core.GetProperty(id, Win32UiaPropertyIds.RangeValueIsReadOnly).BoolValue);
    }

    [Fact]
    public void Position_and_size_fall_back_to_sibling_layout()
    {
        var core = new Win32AccessibilityProviderCore();
        core.TryUpdate(Snapshot(1, "root",
            Node("root", "root", childIds: new[] { "list" }),
            Node("list", "list", parent: "root", childIds: new[] { "a", "b", "c" }),
            Node("a", "listitem", parent: "list"),
            Node("b", "listitem", parent: "list"),
            Node("c", "listitem", parent: "list")));

        int b = core.Index!.EntryId("b");
        Assert.Equal(2, core.GetProperty(b, Win32UiaPropertyIds.PositionInSet).IntValue);
        Assert.Equal(3, core.GetProperty(b, Win32UiaPropertyIds.SizeOfSet).IntValue);
    }

    [Fact]
    public void Tree_item_level_counts_treeitem_ancestors()
    {
        var core = new Win32AccessibilityProviderCore();
        core.TryUpdate(Snapshot(1, "root",
            Node("root", "root", childIds: new[] { "tree" }),
            Node("tree", "tree", parent: "root", childIds: new[] { "parent" }),
            Node("parent", "treeitem", parent: "tree", childIds: new[] { "child" }),
            Node("child", "treeitem", parent: "parent")));

        Assert.Equal(1, core.GetProperty(core.Index!.EntryId("parent"), Win32UiaPropertyIds.Level).IntValue);
        Assert.Equal(2, core.GetProperty(core.Index!.EntryId("child"), Win32UiaPropertyIds.Level).IntValue);
    }

    [Fact]
    public void Hidden_and_out_of_client_nodes_are_offscreen()
    {
        var core = new Win32AccessibilityProviderCore { ClientBounds = new Rect(0, 0, 100, 100) };
        core.TryUpdate(Snapshot(1, "root",
            Node("root", "root", bounds: new Rect(0, 0, 100, 100), childIds: new[] { "hidden", "outside" }),
            Node("hidden", "button", parent: "root", states: new[] { "hidden" }, bounds: new Rect(1, 1, 10, 10)),
            Node("outside", "button", parent: "root", bounds: new Rect(200, 200, 10, 10))));

        Assert.True(core.GetProperty(core.Index!.EntryId("hidden"), Win32UiaPropertyIds.IsOffscreen).BoolValue);
        Assert.True(core.GetProperty(core.Index!.EntryId("outside"), Win32UiaPropertyIds.IsOffscreen).BoolValue);
    }

    // ------------------------------------------------------------ 版本增量与 diff

    [Fact]
    public void Update_skips_rebuild_when_version_is_unchanged()
    {
        var core = new Win32AccessibilityProviderCore();
        var snapshot = Snapshot(1, "root", Node("root", "root", name: "App"));
        Assert.True(core.TryUpdate(snapshot));
        Assert.False(core.TryUpdate(snapshot));

        snapshot.Version = 2;
        Assert.True(core.TryUpdate(snapshot));
    }

    [Fact]
    public void Diff_reports_added_removed_and_reordered_children()
    {
        var map = new Win32AccessibilityIdMap();
        var before = Win32AccessibilityIndex.Build(Snapshot(1, "root",
            Node("root", "root", childIds: new[] { "a", "b" }),
            Node("a", "button", parent: "root"),
            Node("b", "button", parent: "root")), map);

        var afterAddRemove = Win32AccessibilityIndex.Build(Snapshot(2, "root",
            Node("root", "root", childIds: new[] { "a", "c" }),
            Node("a", "button", parent: "root"),
            Node("c", "button", parent: "root")), map);
        var diff = Win32AccessibilityDiff.Compute(before, afterAddRemove);

        var added = diff.Structures.Find(s => s.ChangeType == Win32UiaConstants.StructureChangeChildAdded);
        var removed = diff.Structures.Find(s => s.ChangeType == Win32UiaConstants.StructureChangeChildRemoved);
        Assert.NotNull(added);
        Assert.NotNull(removed);
        Assert.Equal(afterAddRemove.RootId, added!.ParentId);
        Assert.Equal(new[] { afterAddRemove.EntryId("c") }, added.ChildIds);
        Assert.Equal(new[] { before.EntryId("b") }, removed!.ChildIds);

        var afterReorder = Win32AccessibilityIndex.Build(Snapshot(2, "root",
            Node("root", "root", childIds: new[] { "b", "a" }),
            Node("a", "button", parent: "root"),
            Node("b", "button", parent: "root")), map);
        var reorder = Win32AccessibilityDiff.Compute(before, afterReorder)
            .Structures.Find(s => s.ChangeType == Win32UiaConstants.StructureChangeChildrenReordered);
        Assert.NotNull(reorder);
        Assert.Equal(new[] { afterReorder.EntryId("b"), afterReorder.EntryId("a") }, reorder!.ChildIds);
    }

    [Fact]
    public void Diff_reports_property_changes_focus_and_live_region()
    {
        var map = new Win32AccessibilityIdMap();
        var before = Win32AccessibilityIndex.Build(Snapshot(1, "root",
            Node("root", "root", childIds: new[] { "a" },
                states: new[] { "focused" }),
            Node("a", "checkbox", parent: "root", name: "One",
                states: new[] { "checked" },
                actions: new[] { AccessibilityActionKind.Toggle })), map);

        var after = Win32AccessibilityIndex.Build(Snapshot(2, "root",
            Node("root", "root", childIds: new[] { "a" }),
            Node("a", "checkbox", parent: "root", name: "Two",
                states: new[] { "focused", "disabled" },
                actions: new[] { AccessibilityActionKind.Toggle },
                live: AccessibilityPoliteness.Polite)), map);

        var diff = Win32AccessibilityDiff.Compute(before, after);
        var propertyIds = new HashSet<int>();
        foreach (var change in diff.Properties) propertyIds.Add(change.PropertyId);

        Assert.Contains(Win32UiaPropertyIds.Name, propertyIds);
        Assert.Contains(Win32UiaPropertyIds.IsEnabled, propertyIds);
        Assert.Contains(Win32UiaPropertyIds.HasKeyboardFocus, propertyIds);
        Assert.Contains(Win32UiaPropertyIds.ToggleToggleState, propertyIds);

        Assert.True(diff.FocusChanged);
        Assert.Equal(after.EntryId("a"), diff.FocusedNodeId);
        Assert.Single(diff.Notifications);
        Assert.Equal("Two", diff.Notifications[0].Text);
        Assert.Equal(AccessibilityPoliteness.Polite, diff.Notifications[0].Politeness);
    }

    // ------------------------------------------------------------ 动作派发

    [Fact]
    public void Provider_core_dispatches_actions_to_handler()
    {
        var core = new Win32AccessibilityProviderCore();
        core.TryUpdate(Snapshot(1, "root",
            Node("root", "root", childIds: new[] { "btn" }),
            Node("btn", "button", parent: "root", actions: new[] { AccessibilityActionKind.Invoke })));

        int id = core.Index!.EntryId("btn");
        var recorded = new List<(int NodeId, AccessibilityActionKind Action, string? Value)>();
        core.ActionHandler = (nodeId, action, value) =>
        {
            recorded.Add((nodeId, action, value));
            return true;
        };

        Assert.True(core.RequestAction(id, AccessibilityActionKind.Invoke));
        Assert.Single(recorded);
        Assert.Equal(id, recorded[0].NodeId);
        Assert.Equal(AccessibilityActionKind.Invoke, recorded[0].Action);

        core.ActionHandler = null;
        Assert.False(core.RequestAction(id, AccessibilityActionKind.Invoke));
        Assert.False(core.RequestAction(999, AccessibilityActionKind.Invoke));
    }

    // ------------------------------------------------------------ 辅助构造

    static AccessibilityNodeSnapshot Node(
        string id,
        string role,
        string? parent = null,
        string? name = null,
        string? value = null,
        Rect? bounds = null,
        string[]? states = null,
        AccessibilityActionKind[]? actions = null,
        string[]? childIds = null,
        AccessibilityTextSnapshot? text = null,
        AccessibilityPoliteness live = AccessibilityPoliteness.Off,
        int? headingLevel = null,
        int? posInSet = null,
        int? setSize = null)
    {
        return new AccessibilityNodeSnapshot
        {
            Id = id,
            Role = role,
            Name = name,
            Value = value,
            Bounds = bounds ?? new Rect(0, 0, 100, 20),
            ParentId = parent,
            ChildIds = childIds ?? Array.Empty<string>(),
            States = states ?? Array.Empty<string>(),
            Actions = actions ?? Array.Empty<AccessibilityActionKind>(),
            Text = text,
            LiveRegion = live,
            HeadingLevel = headingLevel,
            PosInSet = posInSet,
            SetSize = setSize,
        };
    }

    static AccessibilitySnapshot Snapshot(long version, string rootId, params AccessibilityNodeSnapshot[] nodes) =>
        new() { Version = version, RootId = rootId, Nodes = nodes };
}

/// <summary>测试辅助：按字符串 ID 取稳定 int ID。</summary>
internal static class Win32AccessibilityTestExtensions
{
    public static int EntryId(this Win32AccessibilityIndex index, string id)
    {
        foreach (var entry in index.Entries.Values)
            if (string.Equals(entry.StringId, id, StringComparison.Ordinal)) return entry.Id;
        throw new InvalidOperationException("node not found: " + id);
    }
}
