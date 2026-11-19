using AwAUI.Css.Values;

namespace AwAUI.UI.UnitTests;

/// <summary>样式阶段：选择器匹配适配、默认样式表、内联样式、继承、精确失效与差异 → 脏标记。</summary>
public partial class PipelineStyleTests
{
    static Color C(string hex) => Color.Parse(hex);

    static Color ColorOf(Element e) => e.ComputedStyle!.ResolveColor(e.ComputedStyle.Color);

    static Color BackgroundOf(Element e) => e.ComputedStyle!.ResolveColor(e.ComputedStyle.BackgroundColor);

    [Fact]
    public void Default_stylesheet_sets_display_and_box_sizing()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "d", Children = [new Text("t") { Id = "t" }, new Span("s") { Id = "s" }, new Button { Id = "b", Children = ["x"] }, new Input { Id = "i" }],
        });
        Assert.Equal(CssDisplay.Block, h.El("d").ComputedStyle!.Display);
        Assert.Equal(CssBoxSizing.BorderBox, h.El("d").ComputedStyle!.BoxSizing);
        Assert.Equal(CssDisplay.Block, h.El("t").ComputedStyle!.Display);
        Assert.Equal(CssDisplay.Inline, h.El("s").ComputedStyle!.Display);
        Assert.Equal(CssDisplay.InlineFlex, h.El("b").ComputedStyle!.Display);
        Assert.Equal(CssDisplay.InlineBlock, h.El("i").ComputedStyle!.Display);
        Assert.Equal(14f, h.El("t").ComputedStyle!.FontSize);
        // 根元素匹配 :root 与类型选择器 root
        Assert.Equal(CssDisplay.Block, h.Root.RootElement.ComputedStyle!.Display);
    }

    [Fact]
    public void Class_change_restyles_and_color_inherits_into_text_and_spans()
    {
        var on = new State<bool>(false);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Id = "card", Class = ClassValue.Cx("card", ("hot", on.Value)),
                Children = [new Text { Id = "t", Children = ["a ", new Span("b") { Id = "s" }] }],
            },
        }, ".card { color: #112233; } .card.hot { color: #ff0000; } span { font-weight: 700; }");
        Assert.Equal(C("#112233"), ColorOf(h.El("t")));
        Assert.Equal(C("#112233"), ColorOf(h.El("s")));
        // 文本节点与父元素共享计算样式
        Assert.Same(h.El("t").ComputedStyle, h.El("t").Children[0].ComputedStyle);

        on.Value = true;
        h.Frame();
        Assert.Equal(C("#ff0000"), ColorOf(h.El("card")));
        Assert.Equal(C("#ff0000"), ColorOf(h.El("t")));
        Assert.Equal(C("#ff0000"), ColorOf(h.El("s")));
        Assert.Equal(700f, h.El("s").ComputedStyle!.FontWeight);
    }

    [Fact]
    public void Inline_style_overrides_sheet_and_is_cached_per_text()
    {
        var diags = new List<UIDiagnostic>();
        var h = new PipelineHarness(new Div
        {
            Children =
            [
                new Div { Id = "a", Class = "x", Style = "color: #00ff00; bogus-prop: 1" },
                new Div { Id = "b", Class = "x", Style = "color: #00ff00; bogus-prop: 1" },
            ],
        }, ".x { color: #0000ff; }");
        Assert.Equal(C("#00ff00"), ColorOf(h.El("a")));
        Assert.Equal(C("#00ff00"), ColorOf(h.El("b")));
        // 同一文本只解析一次：两个元素拿到同一个 CssInlineStyle
        var pipeline = h.Pipeline;
        Assert.Same(pipeline.GetInlineStyle("color: #00ff00; bogus-prop: 1", h.El("a")), pipeline.GetInlineStyle("color: #00ff00; bogus-prop: 1", h.El("b")));
    }

    [Fact]
    public void Invalid_inline_style_reports_css_diagnostic()
    {
        var diags = new List<UIDiagnostic>();
        var pipeline = new RenderPipeline(new AwAUI.Text.Testing.TestTextEngine());
        var root = new UIRoot { Pipeline = pipeline };
        root.Diagnostic += diags.Add;
        root.Mount(new Lambda { Render = () => new Div { Style = "colr: red" } });
        Assert.Contains(diags, d => d.Code == "AW2002" && d.Message.Contains("colr"));
    }

    [Fact]
    public void Hover_restyles_only_the_element_when_no_descendant_selectors_use_it()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "outer", Style = "padding: 10px", Children = [new Button { Id = "b", Class = "b", Children = ["Go"] }],
        }, ".b { width: 100px; height: 30px; background-color: #ffffff; } .b:hover { background-color: #00ff00; }");
        Assert.Equal(C("#ffffff"), BackgroundOf(h.El("b")));
        long version = h.Pipeline.FrameVersion;
        h.Move(20, 20);
        Assert.True(h.Root.NeedsFrame);
        h.Frame();
        Assert.Equal(C("#00ff00"), BackgroundOf(h.El("b")));
        Assert.True(h.Pipeline.FrameVersion > version);
        // :hover 落在按钮、外层 div 与根上，每个只重算自身（没有用到 :hover 的后代选择器）
        Assert.Equal(3, h.Pipeline.LastTimings.StyledElements);
        Assert.False(h.Pipeline.LastTimings.LayoutRan);

        h.Move(390, 290);
        h.Frame();
        Assert.Equal(C("#ffffff"), BackgroundOf(h.El("b")));
    }

    [Fact]
    public void Hover_without_matching_rules_does_not_repaint()
    {
        var h = new PipelineHarness(new Div { Id = "d", Style = "height: 50px" }, defaultSheet: false);
        long version = h.Pipeline.FrameVersion;
        h.Move(10, 10);
        h.Frame();
        Assert.Equal(version, h.Pipeline.FrameVersion);
        Assert.False(h.Pipeline.LastTimings.Repainted);
    }

    [Fact]
    public void Ancestor_state_selector_restyles_descendants()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "card", Class = "card",
            Children = [new Div { Children = [new Text("title") { Id = "title", Class = "title" }] }],
        }, ".card { height: 100px; } .title { color: #000000; } .card:hover .title { color: #ff0000; }");
        h.Move(10, 10);
        h.Frame();
        Assert.Equal(C("#ff0000"), ColorOf(h.El("title")));
        h.Move(390, 290);
        h.Frame();
        Assert.Equal(C("#000000"), ColorOf(h.El("title")));
    }

    [Fact]
    public void Ancestor_class_selector_restyles_descendants()
    {
        var on = new State<bool>(false);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Class = ClassValue.Cx("list", ("dark", on.Value)),
                Children = [new Div { Children = [new Text("x") { Id = "x" }] }],
            },
        }, ".x-unused {} .dark text { color: #ffffff; } text { color: #000000; }");
        Assert.Equal(C("#000000"), ColorOf(h.El("x")));
        on.Value = true;
        h.Frame();
        Assert.Equal(C("#ffffff"), ColorOf(h.El("x")));
    }

    [Fact]
    public void Sibling_combinator_and_structural_pseudo_classes_follow_changes()
    {
        var items = new State<string[]>(new[] { "b", "c" });
        var mark = new State<bool>(false);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Id = "list",
                Children =
                [
                    new Div { Id = "head", Class = ClassValue.Cx("head", ("on", mark.Value)) },
                    new Div { Id = "next", Class = "next" },
                    .. items.Value.Select(i => (Node)new Div { Key = i, Id = i, Class = "item" }),
                ],
            },
        }, ".head.on + .next { color: #ff0000; } .item:first-of-type { color: #00ff00; } .item:last-child { color: #0000ff; }");
        Assert.Equal(C("#0000ff"), ColorOf(h.El("c")));
        mark.Value = true;
        h.Frame();
        Assert.Equal(C("#ff0000"), ColorOf(h.El("next")));

        items.Value = new[] { "b", "c", "d" };
        h.Frame();
        Assert.Equal(C("#0000ff"), ColorOf(h.El("d")));
        Assert.NotEqual(C("#0000ff"), ColorOf(h.El("c")));
    }

    [Fact]
    public void Custom_property_change_reaches_descendants()
    {
        var accent = new State<string>("#ff0000");
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Style = "--accent: " + accent.Value,
                Children = [new Div { Children = [new Text("x") { Id = "x", Class = "x" }] }],
            },
        }, ".x { color: var(--accent); }");
        Assert.Equal(C("#ff0000"), ColorOf(h.El("x")));
        accent.Value = "#00ff00";
        h.Frame();
        Assert.Equal(C("#00ff00"), ColorOf(h.El("x")));
    }

    [Fact]
    public void Viewport_units_and_media_queries_follow_viewport()
    {
        var h = new PipelineHarness(new Div { Id = "d", Class = "d" },
            ".d { width: 50vw; height: 10px; } @media (max-width: 300px) { .d { height: 20px; } }", width: 400);
        Assert.Equal(new Rect(0, 0, 200, 10), h.Box("d"));
        h.Pipeline.SetViewport(new Size(600, 300), 1);
        h.Frame();
        Assert.Equal(new Rect(0, 0, 300, 10), h.Box("d"));
        h.Pipeline.SetViewport(new Size(200, 300), 1);
        h.Frame();
        Assert.Equal(new Rect(0, 0, 100, 20), h.Box("d"));
    }

    [Fact]
    public void Adding_a_stylesheet_restyles_everything()
    {
        var h = new PipelineHarness(new Text("x") { Id = "x" });
        Assert.Equal(C("#1f2328"), ColorOf(h.El("x")));
        h.Pipeline.AddStyleSheet("text { color: #123456; }");
        h.Frame();
        Assert.Equal(C("#123456"), ColorOf(h.El("x")));
    }

    [Fact]
    public void Part_selector_matches_through_component_host()
    {
        var h = new PipelineHarness(new Div
        {
            Class = "toolbar", Children = [new Labelled()],
        }, ".toolbar::part(label) { color: #ff00ff; }");
        Assert.Equal(C("#ff00ff"), ColorOf(h.El("label")));
    }

    partial class Labelled : Component
    {
        protected override Node Build()
        {
            var t = new Text("x") { Id = "label" };
            t.Attributes["part"] = "label";
            return new Div { Children = [t] };
        }
    }
}
