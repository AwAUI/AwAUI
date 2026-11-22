using AwAUI.UI.Accessibility;

namespace AwAUI.UI.UnitTests.Accessibility;

/// <summary>
/// design/10：语义树重建时产生结构化事件（焦点、值、名称、结构、实时区域播报），并保留最近事件与订阅。
/// </summary>
public class AccessibilityEventsTests
{
    static UIRoot Mount(Node node)
    {
        var root = new UIRoot();
        root.Mount(node is Component ? node : new Lambda { Render = () => node });
        return root;
    }

    [Fact]
    public void Name_value_and_structure_changes_produce_structured_events()
    {
        var root = Mount(new Div
        {
            Children =
            {
                new Button { Id = "btn", Children = { "A" } },
                new Input { Id = "field", Value = "one" },
                new Div { Id = "extra", Role = Role.Group, Children = { "附加" } },
            },
        });
        var tree = root.Accessibility;
        _ = tree.Root; // 建立基线
        tree.ClearEvents();
        var seen = new List<AccessibilityEvent>();
        tree.EventRaised += e => seen.Add(e);

        root.RootElement.ById("btn").SetAttributeOverride("aria-label", "B");
        ((InputElement)root.RootElement.ById("field")).SetValue("two");
        root.RootElement.ById("extra").SetAttributeOverride("aria-hidden", "true");

        var events = tree.RecentEvents;

        Assert.Equal(events.Count, seen.Count); // 订阅者收到同一批事件

        var name = Assert.Single(events, e => e.Kind == AccessibilityEventKind.NameChanged);
        Assert.Equal("btn", name.NodeId);
        Assert.Equal("B", name.Name);

        var value = Assert.Single(events, e => e.Kind == AccessibilityEventKind.ValueChanged);
        Assert.Equal("field", value.NodeId);
        Assert.Equal("two", value.Value);

        var structure = Assert.Single(events, e => e.Kind == AccessibilityEventKind.StructureChanged);
        Assert.Contains("extra", structure.RemovedNodeIds);

        Assert.All(events, e => Assert.Equal(tree.Version, e.Version));
    }

    [Fact]
    public void Focus_changes_are_reported_with_previous_node()
    {
        var root = Mount(new Div
        {
            Children =
            {
                new Button { Id = "first", Children = { "一" } },
                new Button { Id = "second", Children = { "二" } },
            },
        });
        var tree = root.Accessibility;
        _ = tree.Root;
        tree.ClearEvents();

        root.RootElement.ById("first").Focus();
        var first = Assert.Single(tree.RecentEvents);
        Assert.Equal(AccessibilityEventKind.FocusChanged, first.Kind);
        Assert.Equal("first", first.NodeId);
        Assert.Null(first.PreviousNodeId);

        tree.ClearEvents();
        root.RootElement.ById("second").Focus();
        var second = Assert.Single(tree.RecentEvents);
        Assert.Equal(AccessibilityEventKind.FocusChanged, second.Kind);
        Assert.Equal("second", second.NodeId);
        Assert.Equal("first", second.PreviousNodeId);

        tree.ClearEvents();
        root.RootElement.ById("second").Blur();
        var blurred = Assert.Single(tree.RecentEvents);
        Assert.Equal(AccessibilityEventKind.FocusChanged, blurred.Kind);
        Assert.Null(blurred.NodeId);
        Assert.Equal("second", blurred.PreviousNodeId);
    }

    [Fact]
    public void Live_region_text_changes_produce_polite_announcements_without_duplicates()
    {
        Node Build(string text) => new Div
        {
            Children =
            {
                new Div
                {
                    Id = "status",
                    Attributes = { ["aria-live"] = "polite" },
                    Children = { new Text(text) { Id = "statusText" } },
                },
            },
        };

        var root = new UIRoot();
        root.Mount(new Lambda { Render = () => Build("已保存") });
        var tree = root.Accessibility;
        _ = tree.Root;
        tree.ClearEvents();

        root.Mount(new Lambda { Render = () => Build("保存失败") });
        var events = tree.RecentEvents;

        var announcement = Assert.Single(events, e => e.Kind == AccessibilityEventKind.Announcement);
        Assert.Equal("status", announcement.NodeId);
        Assert.Equal(AccessibilityLiveRegion.Polite, announcement.Politeness);
        Assert.Equal("保存失败", announcement.Text);

        // 没有新的文本变化时不重复播报。
        int count = events.Count;
        _ = tree.RecentEvents;
        Assert.Equal(count, events.Count);
        Assert.DoesNotContain(tree.RecentEvents, e => e.Kind == AccessibilityEventKind.Announcement && e.Text == "已保存");
    }

    [Fact]
    public void Recent_event_limit_keeps_only_the_newest_events()
    {
        var root = Mount(new Div { Children = { new Button { Id = "btn", Children = { "A" } } } });
        var tree = root.Accessibility;
        _ = tree.Root;
        tree.ClearEvents();
        tree.RecentEventLimit = 2;

        for (int i = 1; i <= 4; i++)
        {
            root.RootElement.ById("btn").SetAttributeOverride("aria-label", "N" + i);
            _ = tree.RecentEvents;
        }

        Assert.Equal(2, tree.RecentEvents.Count);
    }
}
