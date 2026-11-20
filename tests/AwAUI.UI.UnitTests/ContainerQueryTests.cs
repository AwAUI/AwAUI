using AwAUI.Graphics;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 容器查询与 <c>:has()</c> 的 UI 接入（第九批）：布局之后重算依赖查询容器的元素（同一帧内生效、轮数有上限）、容器单位、
/// <c>:has()</c> 的失效（子元素插入 / 移除、class、属性、伪状态变化后重算锚点），以及表单状态伪类。
/// </summary>
public class ContainerQueryTests
{
    static Color C(string hex) => Color.Parse(hex);

    static Color ColorOf(Element e) => e.ComputedStyle!.ResolveColor(e.ComputedStyle.Color);

    [Fact]
    public void Container_query_applies_in_the_same_frame_and_follows_resizes()
    {
        var width = new State<int>(400);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Id = "card", Class = "card", Style = "width: " + width.Value + "px",
                Children = [new Div { Id = "title", Class = "title" }],
            },
        }, ".card { container-type: inline-size; container-name: card }"
           + ".title { height: 10px; color: #000000 }"
           + "@container card (min-width: 300px) { .title { color: #ff0000; height: 20px } }"
           + "@container (max-width: 200px) { .title { width: 50cqw } }", width: 500);
        Assert.Equal(C("#ff0000"), ColorOf(h.El("title")));
        Assert.Equal(20, h.Box("title").Height);

        width.Value = 150;
        h.Frame();
        Assert.Equal(C("#000000"), ColorOf(h.El("title")));
        Assert.Equal(10, h.Box("title").Height);
        // 容器单位：50cqw = 容器内容盒宽度的一半
        Assert.Equal(75, h.Box("title").Width);
    }

    [Fact]
    public void Shadcn_card_header_container_and_has_data_slot_grid()
    {
        // shadcn CardHeader：@container/card-header 与 has-data-[slot=card-action]:grid-cols-[1fr_auto]
        var withAction = new State<bool>(false);
        var h = new PipelineHarness(new Lambda
        {
            Render = () =>
            {
                var header = new Div { Id = "header", Class = "card-header" };
                header.Attributes["data-slot"] = "card-header";
                var title = new Div { Id = "title", Style = "height: 10px" };
                title.Attributes["data-slot"] = "card-title";
                if (!withAction.Value) header.Children = [title];
                else
                {
                    var action = new Div { Id = "action", Style = "width: 40px; height: 10px" };
                    action.Attributes["data-slot"] = "card-action";
                    header.Children = [title, action];
                }
                return new Div { Style = "width: 300px", Children = [header] };
            },
        }, ".card-header { container-type: inline-size; container-name: card-header; display: grid; grid-auto-rows: min-content; gap: 6px }"
           + ".card-header:has([data-slot=card-action]) { grid-template-columns: 1fr auto }"
           + "@container card-header (min-width: 200px) { [data-slot=card-title] { background-color: #00ff00 } }");
        Assert.Equal(300, h.Box("title").Width);
        Assert.Equal(C("#00ff00"), h.El("title").ComputedStyle!.ResolveColor(h.El("title").ComputedStyle!.BackgroundColor));

        withAction.Value = true;
        h.Frame();
        // :has() 在子元素插入后重算：两列，标题 1fr、动作 auto
        Assert.Equal(new Rect(0, 0, 254, 10), h.Box("title"));
        Assert.Equal(new Rect(260, 0, 40, 10), h.Box("action"));

        withAction.Value = false;
        h.Frame();
        Assert.Equal(300, h.Box("title").Width);
    }

    [Fact]
    public void Container_query_iterations_are_bounded_for_self_referencing_sizes()
    {
        // 收缩适应的容器：查询成立时内容变宽 → 容器变宽 → 条件不成立……每帧最多 MaxContainerQueryPasses 轮，不死循环
        var h = new PipelineHarness(new Div
        {
            Style = "display: flex; align-items: flex-start",
            Children = [new Div { Id = "c", Class = "c", Children = [new Div { Id = "x", Class = "x" }] }],
        }, ".c { container-type: inline-size } .x { width: 100px; height: 5px }"
           + "@container (min-width: 50px) { .x { width: 10px } }"
           + "@container (max-width: 49px) { .x { width: 100px } }");
        for (int i = 0; i < 5; i++)
        {
            h.Root.RunFrame();
            Assert.True(h.Pipeline.LastTimings.StyledElements < 50);
        }
    }

    [Fact]
    public void Has_relative_selectors_follow_class_attribute_and_state_changes()
    {
        var cls = new State<string>("a");
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Id = "outer", Class = "outer",
                Children = [new Div { Id = "mid", Children = [new Div { Id = "leaf", Class = cls.Value, Style = "width: 10px; height: 10px" }] }],
            },
        }, ".outer { color: #000000 } .outer:has(.b) { color: #0000ff } .outer:has(#leaf:hover) { color: #ff0000 }");
        Assert.Equal(C("#000000"), ColorOf(h.El("outer")));
        cls.Value = "b";
        h.Frame();
        Assert.Equal(C("#0000ff"), ColorOf(h.El("outer")));
        h.Move(5, 5);
        h.Frame();
        Assert.Equal(C("#ff0000"), ColorOf(h.El("outer")));
        h.Move(200, 200);
        h.Frame();
        Assert.Equal(C("#0000ff"), ColorOf(h.El("outer")));
        cls.Value = "a";
        h.Frame();
        Assert.Equal(C("#000000"), ColorOf(h.El("outer")));
    }

    [Fact]
    public void Has_sibling_relative_and_tailwind_has_svg_padding()
    {
        var icon = new State<bool>(false);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Children =
                [
                    new Div { Id = "first", Class = "item" },
                    icon.Value
                        ? new Button { Id = "btn", Class = "btn", Children = [new Svg { Id = "svg", Width = 8, Height = 8 }, "Go"] }
                        : new Button { Id = "btn", Class = "btn", Children = ["Go"] },
                ],
            },
        }, ".item { height: 4px } .item:has(+ .btn > svg) { height: 9px } .btn { padding: 0 16px } .btn:has(>svg) { padding: 0 12px }");
        Assert.Equal(4, h.Box("first").Height);
        Assert.Equal(16f, h.El("btn").ComputedStyle!.PaddingLeft.Resolve(0));
        icon.Value = true;
        h.Frame();
        Assert.Equal(9, h.Box("first").Height);
        Assert.Equal(12f, h.El("btn").ComputedStyle!.PaddingLeft.Resolve(0));
    }

    [Fact]
    public void Form_state_pseudo_classes_and_dir()
    {
        var value = new State<string>("");
        var h = new PipelineHarness(new Lambda
        {
            Render = () =>
            {
                var input = new Input { Id = "i", Value = value.Value };
                input.Attributes["required"] = "";
                var rtl = new Div { Id = "rtl", Children = [new Div { Id = "inner" }] };
                rtl.Attributes["dir"] = "rtl";
                return new Div { Id = "form", Class = "form", Children = [input, rtl] };
            },
        }, "input:required:invalid { color: #ff0000 } input:valid { color: #00ff00 } input:read-write { font-weight: 700 }"
           + ".form:has(:invalid) { background-color: #ff0000 } #inner:dir(rtl) { color: #0000ff }");
        var input = h.El("i");
        Assert.Equal(C("#ff0000"), ColorOf(input));
        Assert.Equal(700f, input.ComputedStyle!.FontWeight);
        Assert.Equal(C("#ff0000"), h.El("form").ComputedStyle!.ResolveColor(h.El("form").ComputedStyle!.BackgroundColor));
        Assert.Equal(C("#0000ff"), ColorOf(h.El("inner")));
        value.Value = "x";
        h.Frame();
        Assert.Equal(C("#00ff00"), ColorOf(h.El("i")));
        Assert.True(h.El("form").ComputedStyle!.ResolveColor(h.El("form").ComputedStyle!.BackgroundColor).IsTransparent);
    }

    [Fact]
    public void Presentational_hints_map_width_height_and_ratio()
    {
        var doc = AwAUI.Svg.SvgDocument.Parse("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 40 10'><rect width='40' height='10' fill='#ff0000'/></svg>");
        var h = new PipelineHarness(new Div
        {
            Style = "width: 120px",
            Children =
            [
                new Image { Id = "a", Source = ImageSource.FromSvg(doc), Width = 100, Height = 50, Style = "display: block; width: 200px" },
                new Image { Id = "b", Source = ImageSource.FromSvg(doc), Width = 300, Height = 150, Class = "fluid" },
                new Image { Id = "c", Width = 60, Height = 30, Class = "fluid" },
                new Svg { Id = "s", Document = doc, Width = 20, Style = "display: block" },
            ],
        }, ".fluid { display: block; max-width: 100%; height: auto }");
        // 作者的 width 覆盖提示，高度仍是提示的 50
        Assert.Equal(new Rect(0, 0, 200, 50), h.Box("a"));
        // max-width: 100% + height: auto：宽 120，按固有比例 4:1 推出高 30
        Assert.Equal(120, h.Box("b").Width);
        var bm = ((ReplacedElement)h.El("b")).Measure;
        Assert.True(h.Box("b").Height == 30, $"{h.Box("b")} {h.El("b").ComputedStyle!.Height} {h.El("b").ComputedStyle!.AspectRatio} {bm.NaturalSize} {bm.AspectRatio} {bm.NaturalRatio}");
        // 没有内容（未加载）：aspect-ratio: auto 60 / 30 由提示给出
        Assert.Equal(new Rect(0, 80, 60, 30), h.Box("c"));
        // 只给宽度：高度按 SVG 的固有比例
        Assert.Equal(new Rect(0, 110, 20, 5), h.Box("s"));
        // 提示以表现提示层级参与级联（DevTools 可见）
        Assert.Equal("width:100px;height:50px;aspect-ratio:auto 100 / 50;", ((ReplacedElement)h.El("a")).PresentationalHintText);
    }
}
