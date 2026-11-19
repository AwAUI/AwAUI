namespace AwAUI.UI.UnitTests;

public class ElementTests
{
    [Fact]
    public void Ui_assembly_references_only_allowed_layers()
    {
        // design/01：UI 可以看到 Css、Layout、Text、Graphics（抽象）、Platform、Diagnostics（DevTools 接缝），以及纯 C# 的图片解码与 SVG（Image / Svg 元素），
        // 绝不能引用渲染器 AwAUI.Render
        var refs = typeof(Component).Assembly.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(n => n.StartsWith("AwAUI", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var allowed = new[] { "AwAUI.Css", "AwAUI.Diagnostics", "AwAUI.Foundation", "AwAUI.Graphics", "AwAUI.Imaging", "AwAUI.Layout", "AwAUI.Platform", "AwAUI.Svg", "AwAUI.Text" };
        Assert.Empty(refs.Except(allowed));
        Assert.DoesNotContain("AwAUI.Render", refs);
        Assert.DoesNotContain(typeof(Component).Assembly.GetExportedTypes(), t => t.IsInterface);
    }

    [Fact]
    public void Image_diff_marks_layout_and_paint_for_source_only()
    {
        var src = new State<string>("a.png");
        var alt = new State<string>("logo");
        var root = new UIRoot();
        root.Mount(new Lambda { Render = () => new Image { Source = src.Value, Alt = alt.Value } });
        var img = (ImageElement)root.RootElement.ByTag("img");
        Assert.Equal("a.png", img.GetAttribute("src"));
        img.ClearDirty(ElementDirtyFlags.All);
        alt.Value = "brand";
        root.RunFrame();
        Assert.Equal(ElementDirtyFlags.Style | ElementDirtyFlags.Semantics, img.DirtyFlags);
        img.ClearDirty(ElementDirtyFlags.All);
        src.Value = "b.png";
        root.RunFrame();
        Assert.True(img.DirtyFlags.HasFlag(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint));
        Assert.Equal("b.png", img.Source?.ToString());
    }

    [Fact]
    public void Class_value_normalizes_and_dedupes()
    {
        ClassValue c = "  a\tb  a \n c ";
        Assert.Equal("a b a c", c.Text);
        Assert.Equal(new[] { "a", "b", "c" }, c.Split());
        Assert.Equal("tab active", ClassValue.Cx("tab", ("active", true), ("disabled", false)).Text);
        Assert.Equal("x", ClassValue.Cx(("x", true)).Text);
        Assert.True(new ClassValue("").IsEmpty);

        var many = string.Join(" ", Enumerable.Range(0, 5000).Select(i => "c" + (i % 1000)));
        Assert.Equal(1000, new ClassValue(many).Split().Length);
    }

    [Fact]
    public void Large_attribute_maps_diff_in_linear_time()
    {
        var gen = new State<int>(0);
        var root = new UIRoot();
        root.Mount(new Lambda
        {
            Render = () =>
            {
                var d = new Div { Id = "x" };
                for (int i = 0; i < 2000; i++) d.Attributes["data-" + i] = (i == 7 ? gen.Value : 0).ToString();
                return d;
            },
        });
        var obs = new RecordingObserver();
        root.AddObserver(obs);
        var x = root.RootElement.ById("x");
        Assert.Equal("0", x.GetAttribute("DATA-1999"));
        gen.Value = 1;
        root.RunFrame();
        Assert.Equal(new[] { "attr data-7" }, obs.Events.ToArray());
        Assert.Equal("1", x.GetAttribute("data-7"));
    }

    [Fact]
    public void Attribute_map_and_parts()
    {
        var root = new UIRoot();
        root.Mount(new Lambda { Render = () => new Span { Attributes = { ["part"] = "label  icon", ["Data-X"] = "1" }, Role = Role.Button, AriaLabel = "关闭", TabIndex = 0 } });
        var span = root.RootElement.ByTag("span");
        Assert.Equal(new[] { "label", "icon" }, span.Parts.ToArray());
        Assert.Equal("1", span.GetAttribute("data-x"));
        Assert.Equal("button", span.GetAttribute("role"));
        Assert.Equal("关闭", span.GetAttribute("aria-label"));
        Assert.Equal("0", span.GetAttribute("tabindex"));
        Assert.Null(span.GetAttribute("missing"));

        var map = new AttributeMap { ["a"] = "1", ["A"] = "2" };
        Assert.Single(map);
        Assert.Equal("2", map["a"]);
        map["a"] = null;
        Assert.Empty(map);
    }

    [Fact]
    public void Node_from_converts_values()
    {
        Assert.Null(Node.From(null));
        Assert.Equal("42", ((TextNode)Node.From(42)!).Text);
        var div = new Div();
        Assert.Same(div, Node.From(div));
        Assert.IsType<Fragment>(Node.From(new NodeList { "a" }));
        Node n = "text";
        Assert.Equal("text", ((TextNode)n).Text);
        var list = new NodeList { "a", null, (string?)null, new Div() };
        list.AddRange(new Node?[] { null, new Span() });
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void Forced_states_merge_with_real_states()
    {
        var root = new UIRoot();
        root.Mount(new Lambda { Render = () => new Div { Id = "x" } });
        var x = root.RootElement.ById("x");
        x.ForcedStates = PseudoStates.Hover;
        Assert.Equal(PseudoStates.Hover, x.States);
        Assert.True(x.DirtyFlags.HasFlag(ElementDirtyFlags.Style));
        x.ForcedStates = PseudoStates.None;
        Assert.Equal(PseudoStates.None, x.States);
    }

    [Fact]
    public void Pipeline_runs_after_build_and_element_dirtiness_requests_frames()
    {
        var root = new UIRoot();
        var pipeline = new ClearingPipeline();
        root.Pipeline = pipeline;
        var s = new State<string>("a");
        root.Mount(new Lambda { Render = () => new Text(s.Value) });
        Assert.Equal(1, pipeline.Runs);
        Assert.False(root.NeedsFrame);
        int requests = 0;
        root.FrameRequested += _ => requests++;
        root.RootElement.ByTag("text").ForcedStates = PseudoStates.Active;
        Assert.True(root.NeedsFrame);
        Assert.Equal(1, requests);
        root.RunFrame();
        Assert.False(root.NeedsFrame);
    }

    sealed class ClearingPipeline : FramePipeline
    {
        public int Runs;

        public override void Run(UIRoot root)
        {
            Runs++;
            var stack = new Stack<Element>();
            stack.Push(root.RootElement);
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                e.ClearDirty(ElementDirtyFlags.All);
                foreach (var c in e.Children) stack.Push(c);
            }
        }
    }
}
