using AwAUI.UI.Accessibility;

namespace AwAUI.UI.UnitTests.Accessibility;

/// <summary>
/// design/10：静态文本、名称计算（含 label 关联与访问键）以及复合控件属性的语义映射。
/// </summary>
public class AccessibilitySemanticsTests
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
        _ = tree.Root;
        return tree;
    }

    [Fact]
    public void Text_block_and_paragraph_produce_read_only_static_text()
    {
        var root = Mount(new Div
        {
            Children =
            {
                new Text("第一段") { Id = "p1" },
                new TextBlock("第二段") { Id = "p2" },
                new Paragraph("第三段") { Id = "p3" },
                new Text("小标题") { Id = "heading", Role = Role.Heading, Attributes = { ["aria-level"] = "3" } },
                // 纯布局盒子：只有裸文本，不应被当成静态文本。
                new Div { Id = "layout", Children = { "布局文字" } },
            },
        });
        var tree = Tree(root);

        foreach (var id in new[] { "p1", "p2", "p3" })
        {
            var node = tree.FindById(id);
            Assert.NotNull(node);
            Assert.Equal("statictext", node!.Role);
            Assert.True(node.IsStaticText);
            Assert.True(node.State.HasFlag(AccessibilityState.ReadOnly));
            Assert.NotNull(node.TextPattern);
            Assert.True(node.TextPattern!.IsReadOnly);
        }

        Assert.Equal("第一段", tree.FindById("p1")!.Name);
        Assert.Equal("第二段", tree.FindById("p2")!.Name);
        Assert.Equal("第三段", tree.FindById("p3")!.Name);
        Assert.Equal("第一段", tree.FindById("p1")!.TextPattern!.Value);

        var heading = tree.FindById("heading")!;
        Assert.Equal("heading", heading.Role);
        Assert.Equal(3, heading.HeadingLevel);
        Assert.True(heading.State.HasFlag(AccessibilityState.ReadOnly));

        Assert.Null(tree.FindById("layout"));
    }

    [Fact]
    public void Accessible_name_prefers_labelledby_then_label_for_then_label_wrapper_then_aria_label()
    {
        var root = Mount(new Div
        {
            Children =
            {
                new Div { Id = "lbl", Children = { "来自 labelledby" } },
                new Label("来自 for 标签") { Id = "forUser", For = "user" },
                new Label("来自包裹标签") { Id = "wrap", Children = { new Input { Id = "wrapped" } } },
                new Span
                {
                    Id = "precedence",
                    Role = Role.Button,
                    AriaLabel = "来自 aria-label",
                    Attributes = { ["aria-labelledby"] = "lbl" },
                },
                new Label("标签优先于 aria-label") { Id = "forBtn", For = "btn" },
                new Span { Id = "btn", Role = Role.Button, AriaLabel = "来自 aria-label", Children = { "内容" } },
                new Input { Id = "user" },
                new Input { Id = "fallback", Attributes = { ["aria-labelledby"] = "missing" }, AriaLabel = "回退名称" },
            },
        });
        var tree = Tree(root);

        Assert.Equal("来自 labelledby", tree.FindById("precedence")!.Name);
        Assert.Equal("标签优先于 aria-label", tree.FindById("btn")!.Name);
        Assert.Equal("来自 for 标签", tree.FindById("user")!.Name);
        Assert.Equal("来自包裹标签", tree.FindById("wrapped")!.Name);
        // labelledby 断链时继续按 aria-label 回退。
        Assert.Equal("回退名称", tree.FindById("fallback")!.Name);
    }

    [Fact]
    public void Broken_labelledby_falls_through_to_content_and_title()
    {
        var root = Mount(new Div
        {
            Children =
            {
                new Span
                {
                    Id = "content",
                    Role = Role.Link,
                    Attributes = { ["aria-labelledby"] = "nope" },
                    Children = { "链接文字" },
                },
                new Div
                {
                    Id = "title",
                    Role = Role.Group,
                    Attributes = { ["aria-labelledby"] = "nope", ["title"] = "标题回退" },
                },
            },
        });
        var tree = Tree(root);

        Assert.Equal("链接文字", tree.FindById("content")!.Name);
        Assert.Equal("标题回退", tree.FindById("title")!.Name);
    }

    [Fact]
    public void Windows_mnemonics_and_accesskey_produce_deduplicated_access_keys()
    {
        var root = Mount(new Div
        {
            Children =
            {
                new Button { Id = "save1", Children = { "&Save" } },
                new Button { Id = "save2", Children = { "&Save" } },
                new Span
                {
                    Id = "explicit",
                    Role = Role.Button,
                    TabIndex = 0,
                    Attributes = { ["accesskey"] = "k" },
                },
                new Span
                {
                    Id = "shifted",
                    Role = Role.Button,
                    TabIndex = 0,
                    Attributes = { ["accesskey"] = "J" },
                },
                new Button { Id = "literal", Children = { "&&File" } },
            },
        });
        var tree = Tree(root);

        var save1 = tree.FindById("save1")!;
        Assert.Equal("Save", save1.Name);
        Assert.Equal("S", save1.AccessKey);
        Assert.Equal(AccessKeyModifiers.Alt, save1.AccessKeyModifiers);

        // 重复的访问键只在第一个节点暴露。
        var save2 = tree.FindById("save2")!;
        Assert.Equal("Save", save2.Name);
        Assert.Null(save2.AccessKey);
        Assert.Equal(AccessKeyModifiers.None, save2.AccessKeyModifiers);

        var explicitKey = tree.FindById("explicit")!;
        Assert.Equal("K", explicitKey.AccessKey);
        Assert.Equal(AccessKeyModifiers.Ctrl | AccessKeyModifiers.Alt, explicitKey.AccessKeyModifiers);

        // 原文大写时再加 Shift（Web accesskey 的平台约定）。
        var shifted = tree.FindById("shifted")!;
        Assert.Equal("J", shifted.AccessKey);
        Assert.Equal(AccessKeyModifiers.Ctrl | AccessKeyModifiers.Alt | AccessKeyModifiers.Shift, shifted.AccessKeyModifiers);

        // && 是字面量 &，不产生访问键。
        var literal = tree.FindById("literal")!;
        Assert.Equal("&File", literal.Name);
        Assert.Null(literal.AccessKey);
    }

    [Fact]
    public void Composite_widget_attributes_map_to_semantic_node()
    {
        var root = Mount(new Div
        {
            Children =
            {
                new Div
                {
                    Id = "widget",
                    Role = Role.ComboBox,
                    TabIndex = 0,
                    Attributes =
                    {
                        ["aria-activedescendant"] = "opt1",
                        ["aria-haspopup"] = "listbox",
                        ["aria-current"] = "page",
                        ["aria-orientation"] = "vertical",
                        ["aria-level"] = "2",
                        ["aria-valuemin"] = "0",
                        ["aria-valuemax"] = "10",
                        ["aria-keyshortcuts"] = "Control+K",
                        ["aria-modal"] = "true",
                        ["aria-expanded"] = "false",
                        ["aria-pressed"] = "true",
                        ["aria-selected"] = "false",
                        ["aria-checked"] = "mixed",
                        ["aria-busy"] = "true",
                        ["aria-invalid"] = "grammar",
                        ["aria-live"] = "polite",
                        ["aria-atomic"] = "true",
                        ["aria-relevant"] = "additions text",
                    },
                },
                new Input
                {
                    Id = "range",
                    InputType = "range",
                    Attributes = { ["min"] = "1", ["max"] = "5", ["step"] = "0.5" },
                },
            },
        });
        var tree = Tree(root);

        var node = tree.FindById("widget")!;
        Assert.Equal("opt1", node.ActiveDescendant);
        Assert.Equal("listbox", node.HasPopup);
        Assert.Equal("page", node.Current);
        Assert.Equal("vertical", node.Orientation);
        Assert.Equal(2, node.Level);
        Assert.Equal(0, node.ValueMin);
        Assert.Equal(10, node.ValueMax);
        Assert.Equal("Control+K", node.KeyShortcuts);
        Assert.True(node.IsModal);
        Assert.False(node.Expanded);
        Assert.True(node.Pressed);
        Assert.False(node.Selected);
        Assert.Null(node.Checked); // mixed 由 State 表达
        Assert.True(node.State.HasFlag(AccessibilityState.Mixed));
        Assert.True(node.Busy);
        Assert.Equal("grammar", node.Invalid);
        Assert.Equal(AccessibilityLiveRegion.Polite, node.LiveRegion);
        Assert.True(node.Atomic);
        Assert.Equal("additions text", node.Relevant);

        var range = tree.FindById("range")!;
        Assert.Equal(1, range.ValueMin);
        Assert.Equal(5, range.ValueMax);
        Assert.Equal(0.5, range.ValueStep);
    }

    [Fact]
    public void Selectable_static_text_exposes_text_pattern_selection()
    {
        var harness = new PipelineHarness(new Div
        {
            Children = { new SelectableTextBlock("hello") { Id = "text" } },
        });
        harness.Frame();
        harness.Root.Selection.SelectAll();

        var tree = harness.Root.Accessibility;
        var node = tree.FindById("text")!;
        Assert.True(node.IsStaticText);
        var pattern = node.TextPattern;
        Assert.NotNull(pattern);
        Assert.Equal("hello", pattern!.Value);
        Assert.True(pattern.IsReadOnly);
        Assert.True(pattern.HasSelection);
        Assert.Equal(0, pattern.SelectionStart);
        Assert.Equal(5, pattern.SelectionEnd);
        Assert.Equal("hello", pattern.SelectedText);

        // 选区变化后，语义树在下次读取时重建并反映新的选区。
        harness.Root.Selection.Clear();
        var after = tree.FindById("text")!;
        Assert.False(after.TextPattern!.HasSelection);
        Assert.Equal(0, after.TextPattern.SelectionStart);
        Assert.Equal(0, after.TextPattern.SelectionEnd);
    }
}
