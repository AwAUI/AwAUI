using AwAUI.UI.Accessibility;

namespace AwAUI.UI.UnitTests.Accessibility;

/// <summary>design/10：语义树的构建规则（名称、过滤、角色、状态、关系、文本信息、惰性构建）。</summary>
public class AccessibilityTreeTests
{
    static UIRoot Mount(Node node)
    {
        var root = new UIRoot();
        root.Mount(node is Component ? node : new Lambda { Render = () => node });
        return root;
    }

    static AccessibilityTree Tree(UIRoot root)
    {
        var tree = root.Accessibility;
        _ = tree.Root; // 构建
        return tree;
    }

    [Fact]
    public void Name_precedence_is_labelledby_then_aria_label_then_alt_then_text_then_title()
    {
        var root = Mount(new Div
        {
            Children =
            {
                new Div { Id = "lbl", Children = { "标签文字" } },
                new Span
                {
                    Id = "all",
                    Role = Role.Button,
                    AriaLabel = "标签",
                    Attributes = { ["aria-labelledby"] = "lbl", ["alt"] = "替代文本", ["title"] = "标题" },
                    Children = { "可见文字" },
                },
                new Span
                {
                    Id = "byLabel",
                    Role = Role.Button,
                    AriaLabel = "标签",
                    Attributes = { ["aria-labelledby"] = "lbl", ["alt"] = "替代文本", ["title"] = "标题" },
                    Children = { "可见文字" },
                },
                new Image { Id = "byAlt", Alt = "图片说明" },
                new Span { Id = "byText", Role = Role.Button, Children = { "可见 文字" } },
                new Div { Id = "byTitle", Role = Role.Group, Attributes = { ["title"] = "标题文字" } },
                new Text("段落标题") { Id = "textHeading", Role = Role.Heading },
            },
        });
        var tree = Tree(root);

        // ARIA 名称计算顺序：aria-labelledby 优先于 aria-label。
        Assert.Equal("标签文字", tree.FindById("all")!.Name);
        Assert.Equal("标签文字", tree.FindById("byLabel")!.Name);
        Assert.Equal("图片说明", tree.FindById("byAlt")!.Name);
        Assert.Equal("可见 文字", tree.FindById("byText")!.Name);
        Assert.Equal("标题文字", tree.FindById("byTitle")!.Name);
        Assert.Equal("段落标题", tree.FindById("textHeading")!.Name);
    }

    [Fact]
    public void Aria_hidden_subtrees_are_removed_and_decorative_boxes_are_flattened()
    {
        var root = Mount(new Div
        {
            Id = "wrapper",
            Children =
            {
                "装饰性文字",
                new Button { Id = "btn", Children = { "确定" } },
                new Div
                {
                    Id = "hidden",
                    Role = Role.Button,
                    Attributes = { ["aria-hidden"] = "true" },
                    Children = { new Button { Id = "inside", Children = { "不应出现" } } },
                },
            },
        });
        var tree = Tree(root);

        Assert.Null(tree.FindById("wrapper"));
        Assert.Null(tree.FindById("hidden"));
        Assert.Null(tree.FindById("inside"));
        Assert.DoesNotContain(tree.Nodes, n => n.Name is not null && n.Name.Contains("装饰性文字"));

        var button = tree.FindById("btn");
        Assert.NotNull(button);
        Assert.Equal("button", button!.Role);
        Assert.Equal("确定", button.Name);
        Assert.Same(tree.Root, button.Parent); // 装饰性 wrapper 不产生节点
        // 文本本身不产生匿名节点
        Assert.DoesNotContain(tree.Nodes, n => n.Role is "text" or "statictext");
    }

    [Fact]
    public void Roles_are_inferred_from_element_and_control_types()
    {
        var root = Mount(new Div
        {
            Children =
            {
                new Button { Id = "btn", Children = { "按钮" } },
                new Input { Id = "text" },
                new Input { Id = "check", InputType = "checkbox" },
                new Input { Id = "range", InputType = "range" },
                new Image { Id = "img", Alt = "图" },
                new RawTagNode("table") { Id = "tbl" },
                new RawTagNode("h2") { Id = "h2", Children = { "标题" } },
                new Div { Id = "explicit", Role = Role.ComboBox },
            },
        });
        var tree = Tree(root);

        Assert.Equal("button", tree.FindById("btn")!.Role);
        Assert.Equal("textbox", tree.FindById("text")!.Role);
        Assert.Equal("checkbox", tree.FindById("check")!.Role);
        Assert.Equal("slider", tree.FindById("range")!.Role);
        Assert.Equal("img", tree.FindById("img")!.Role);
        Assert.Equal("table", tree.FindById("tbl")!.Role);
        Assert.Equal("heading", tree.FindById("h2")!.Role);
        Assert.Equal(2, tree.FindById("h2")!.HeadingLevel);
        Assert.Equal("combobox", tree.FindById("explicit")!.Role);
        Assert.Equal("标题", tree.FindById("h2")!.Name);
    }

    [Fact]
    public void State_flags_cover_aria_states_and_focus()
    {
        var root = Mount(new Div
        {
            Children =
            {
                new Div
                {
                    Id = "states",
                    Role = Role.CheckBox,
                    TabIndex = 0,
                    Attributes =
                    {
                        ["aria-checked"] = "mixed",
                        ["aria-disabled"] = "true",
                        ["aria-selected"] = "true",
                        ["aria-expanded"] = "true",
                        ["aria-pressed"] = "true",
                        ["aria-invalid"] = "true",
                        ["aria-busy"] = "true",
                        ["aria-required"] = "true",
                        ["aria-readonly"] = "true",
                        ["aria-multiselectable"] = "true",
                        ["aria-live"] = "polite",
                    },
                },
                new Div { Id = "focus", Role = Role.Button, TabIndex = 0 },
            },
        });
        root.RootElement.ById("focus").Focus();
        var tree = Tree(root);

        var node = tree.FindById("states")!;
        Assert.True(node.State.HasFlag(AccessibilityState.Disabled));
        Assert.True(node.State.HasFlag(AccessibilityState.Mixed));
        Assert.True(node.State.HasFlag(AccessibilityState.Selected));
        Assert.True(node.State.HasFlag(AccessibilityState.Expanded));
        Assert.True(node.State.HasFlag(AccessibilityState.Pressed));
        Assert.True(node.State.HasFlag(AccessibilityState.Invalid));
        Assert.True(node.State.HasFlag(AccessibilityState.Busy));
        Assert.True(node.State.HasFlag(AccessibilityState.Required));
        Assert.True(node.State.HasFlag(AccessibilityState.ReadOnly));
        Assert.True(node.State.HasFlag(AccessibilityState.MultiSelectable));
        Assert.True(node.State.HasFlag(AccessibilityState.LiveRegion));
        Assert.Equal(AccessibilityLiveRegion.Polite, node.LiveRegion);
        Assert.False(node.State.HasFlag(AccessibilityState.Focused));

        Assert.True(tree.FindById("focus")!.State.HasFlag(AccessibilityState.Focused));
    }

    [Fact]
    public void Relations_live_regions_and_virtualized_positions_are_exposed()
    {
        var root = Mount(new Div
        {
            Children =
            {
                new Div
                {
                    Id = "dialog",
                    Role = Role.Dialog,
                    TabIndex = -1,
                    Attributes = { ["aria-labelledby"] = "title", ["aria-describedby"] = "desc", ["aria-controls"] = "body" },
                    Children =
                    {
                        new Div { Id = "title", Role = Role.Heading, Children = { "确认删除" } },
                        new Div { Id = "desc", Children = { "此操作不可撤销" } },
                        new Div { Id = "body", Role = Role.Group },
                    },
                },
                new Div
                {
                    Id = "row",
                    Role = Role.ListItem,
                    Attributes = { ["aria-posinset"] = "3", ["aria-setsize"] = "10" },
                    Children = { "第三项" },
                },
                new Div { Id = "alert", Role = Role.Alert, Children = { "发生错误" } },
            },
        });
        var tree = Tree(root);

        var dialog = tree.FindById("dialog")!;
        Assert.Equal("确认删除", dialog.Name);
        Assert.Equal("此操作不可撤销", dialog.Description);
        Assert.Equal(new[] { "title" }, dialog.LabelledBy);
        Assert.Equal(new[] { "desc" }, dialog.DescribedBy);
        Assert.Equal(new[] { "body" }, dialog.Controls);

        var alert = tree.FindById("alert")!;
        Assert.Equal(AccessibilityLiveRegion.Assertive, alert.LiveRegion);
        Assert.True(alert.State.HasFlag(AccessibilityState.LiveRegion));
        Assert.Equal("发生错误", alert.Name);

        var row = tree.FindById("row")!;
        Assert.Equal(3, row.PosInSet);
        Assert.Equal(10, row.SetSize);
        Assert.Equal("第三项", row.Name);
    }

    [Fact]
    public void Input_selection_and_composition_map_to_text_info()
    {
        var root = Mount(new Input { Id = "field", Value = "hello" });
        var input = (InputElement)root.RootElement.ById("field");
        input.SetSelectionRange(1, 3);
        input.SetCompositionState("いう", 2, 0, 2);

        var text = Tree(root).FindById("field")!.Text;
        Assert.NotNull(text);
        Assert.Equal("hello", text!.Value);
        Assert.Equal(1, text.SelectionStart);
        Assert.Equal(3, text.SelectionEnd);
        Assert.Equal(1, text.SelectionAnchor);
        Assert.Equal(3, text.SelectionFocus);
        Assert.Equal("el", text.SelectedText);
        Assert.True(text.IsComposing);
        Assert.Equal("いう", text.CompositionText);
        Assert.Equal(2, text.CompositionStart);
        Assert.Equal(0, text.CompositionSelectionStart);
        Assert.Equal(2, text.CompositionSelectionEnd);
    }

    [Fact]
    public void Tree_is_built_on_demand_and_invalidated_by_element_changes()
    {
        var root = Mount(new Div { Id = "x", Role = Role.Button, AriaLabel = "A" });
        root.RunFrame();
        root.RunFrame();

        var tree = root.Accessibility;
        Assert.Equal(0, tree.BuildCount); // 只创建了对象
        Assert.False(tree.IsBuilt);

        Assert.Equal("A", tree.Root.Children[0].Name);
        Assert.Equal(1, tree.BuildCount);
        Assert.True(tree.IsBuilt);

        long version = tree.Version;
        root.RootElement.ById("x").SetAttributeOverride("aria-label", "B");
        Assert.True(tree.Version > version);
        Assert.False(tree.IsBuilt);

        Assert.Equal("B", tree.FindById("x")!.Name);
        Assert.Equal(2, tree.BuildCount);

        // 没有桥、没有读取：帧照常执行，不构建语义树
        root.RunFrame();
        root.RunFrame();
        Assert.Equal(2, tree.BuildCount);
    }

    [Fact]
    public void Snapshot_uses_platform_neutral_dtos()
    {
        var root = Mount(new Div
        {
            Children =
            {
                new Button { Id = "save", Children = { "保存" } },
            },
        });
        var tree = Tree(root);
        var snapshot = tree.CreateSnapshot();

        Assert.Equal(tree.Version, snapshot.Version);
        Assert.Equal(tree.Root.Id, snapshot.RootId);
        var node = snapshot.Find("save")!;
        Assert.Equal("button", node.Role);
        Assert.Equal("保存", node.Name);
        Assert.Contains(AwAUI.Platform.AccessibilityActionKind.Invoke, node.Actions);
        Assert.Equal(tree.FindById("save")!.Bounds, node.Bounds);
    }

    sealed class RawTagNode : ElementNode
    {
        public RawTagNode(string tag) => TagName = tag;

        public string TagName { get; set; }

        protected internal override Element CreateElement() => new Element(TagName);
    }
}
