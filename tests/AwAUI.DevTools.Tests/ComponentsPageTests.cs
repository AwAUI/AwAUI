using System.Diagnostics;
using AwAUI.Diagnostics.Json;
using AwAUI.UI;
using UIText = AwAUI.UI.Text;

namespace AwAUI.DevTools.Tests;

/// <summary>
/// 组件页：从镜像里的 <c>ComponentName</c> 构建组件实例树、点击在元素树里选中代表节点、空状态，
/// 以及大树的实例上限 / 每结构版本只构建一次。
/// </summary>
public partial class ComponentsPageTests
{
    sealed partial class ComponentRoot : Component
    {
        protected override Node Build() => new Div
        {
            Id = "comp-root",
            Children =
            [
                new ChildAlpha(),
                new ChildBeta(),
            ],
        };
    }

    sealed partial class ChildAlpha : Component
    {
        protected override Node Build() => new Div
        {
            Id = "child-alpha",
            Children = [new UIText { Id = "alpha-text", Children = ["alpha"] }],
        };
    }

    sealed partial class ChildBeta : Component
    {
        protected override Node Build() => new Div { Id = "child-beta" };
    }

    static Harness ComponentsHarness() =>
        new(root: new ComponentRoot(), sheets: Array.Empty<AwAUI.Css.StyleSheet>());

    [Fact]
    public void Components_tab_lists_instances_and_clicking_reveals_the_node()
    {
        using var h = ComponentsHarness();
        h.Open();
        var model = h.Model;

        model.SetTab(SideTab.Components);
        h.Settle();
        Assert.Equal(SideTab.Components, model.Tab);
        Assert.NotNull(h.PanelById("dt-tab-components"));

        // Panel -> Alpha / Beta：三个实例，父子关系按最近的组件祖先
        Assert.True(model.ComponentCount >= 3, "instances: " + model.ComponentCount);
        Assert.False(model.ComponentsTruncated);
        var rootInstance = Assert.Single(model.Components);
        Assert.Equal("ComponentRoot", rootInstance.Name);
        Assert.Contains(rootInstance.Children, c => c.Name == "ChildAlpha");
        Assert.Contains(rootInstance.Children, c => c.Name == "ChildBeta");
        var alpha = rootInstance.Children.Find(c => c.Name == "ChildAlpha")!;
        Assert.Same(rootInstance, alpha.Parent);

        var countText = h.PanelById("dt-components-count")!.TextContent;
        Assert.Contains(model.ComponentCount.ToString(), countText);

        // 每个实例都有一行，带组件名与节点标签
        var alphaElement = h.AppById("child-alpha");
        var row = h.PanelById("dt-component-" + alphaElement.UniqueId);
        Assert.NotNull(row);
        Assert.Equal(alphaElement.UniqueId.ToString(), row!.GetAttribute("data-component-node"));
        Assert.Contains("ChildAlpha", row.TextContent);
        Assert.Contains("<div#child-alpha>", row.TextContent);

        // 点击在元素树里选中该节点，组件页显示名称 / 节点 id / 标签
        h.ClickPanel(row);
        Assert.Equal(alphaElement.UniqueId, h.P.SelectedNodeId);
        Assert.Equal(alphaElement.UniqueId, model.SelectedComponentNodeId);
        var detail = h.PanelById("dt-components-detail")!.TextContent;
        Assert.Contains("ChildAlpha", detail);
        Assert.Contains(alphaElement.UniqueId.ToString(), detail);
        Assert.Contains("<div#child-alpha>", detail);
        Assert.Contains("selected", h.PanelRow(alphaElement.UniqueId)!.ClassName);
    }

    [Fact]
    public void Components_tab_shows_an_empty_state_without_component_names()
    {
        using var h = new Harness(
            root: new Div { Id = "plain-root", Children = [new UIText { Children = ["plain"] }] },
            sheets: Array.Empty<AwAUI.Css.StyleSheet>());
        h.Open();
        h.Model.SetTab(SideTab.Components);
        h.Settle();

        Assert.Equal(0, h.Model.ComponentCount);
        Assert.Empty(h.Model.Components);
        Assert.False(h.Model.ComponentsTruncated);
        var empty = h.PanelById("dt-components-empty");
        Assert.NotNull(empty);
        Assert.Contains("No component instances", empty!.TextContent);
    }

    [Fact]
    public void Components_are_rebuilt_once_per_structure_version_not_every_read()
    {
        var transport = new StubTransport
        {
            RootJson = Node(1, null, Node(2, "Alpha", Node(3, "Beta")), Node(4, "Alpha")),
        };
        var model = FrontendTestUtil.CreateModel(transport, out var client);
        try
        {
            model.Start();
            Assert.Equal(3, model.ComponentCount);
            int builds = model.ComponentsBuildCount;
            Assert.Equal(1, builds);
            _ = model.Components;
            _ = model.Components;
            Assert.Equal(builds, model.ComponentsBuildCount);
        }
        finally
        {
            model.Dispose();
            client.Dispose();
        }
    }

    [Fact]
    public void The_builder_caps_a_hundred_thousand_node_tree()
    {
        // 10 万个兄弟节点，每个都带组件名：构建器在达到上限时提前停止，不会遍历整棵树
        var children = new JsonArray();
        for (int i = 0; i < 100_000; i++) children.Add(Node(i + 2, i % 2 == 0 ? "A" : "B"));
        var root = Node(1, null);
        root["childCount"] = 100_000;
        root["children"] = children;

        var mirror = new TreeMirror();
        Assert.True(mirror.LoadDocument(root));
        Assert.Equal(100_001, mirror.Count);

        var roots = new List<ComponentInstance>();
        var sw = Stopwatch.StartNew();
        int count = ComponentTreeBuilder.Build(mirror, ComponentTreeBuilder.MaxInstances, roots, out bool truncated);
        sw.Stop();

        Assert.True(truncated);
        Assert.Equal(ComponentTreeBuilder.MaxInstances, count);
        Assert.Equal(ComponentTreeBuilder.MaxInstances, roots.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "the builder took " + sw.Elapsed);
    }

    static JsonObject Node(long id, string? componentName, params JsonObject[] children)
    {
        var o = new JsonObject { ["id"] = id, ["kind"] = "element", ["tag"] = "div", ["childCount"] = children.Length };
        if (componentName is not null) o["componentName"] = componentName;
        if (children.Length > 0)
        {
            var arr = new JsonArray();
            foreach (var c in children) arr.Add(c);
            o["children"] = arr;
        }
        return o;
    }
}
