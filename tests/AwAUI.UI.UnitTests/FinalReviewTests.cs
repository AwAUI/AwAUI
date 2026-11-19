using AwAUI.Css.Values;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;
using AwAUI.Diagnostics.Json;
using AwAUI.Platform;

namespace AwAUI.UI.UnitTests;

/// <summary>M1 验收终审发现的问题的回归测试。</summary>
public class FinalReviewTests
{
    static Color C(string hex) => Color.Parse(hex);

    static Color BackgroundOf(Element e) => e.ComputedStyle!.ResolveColor(e.ComputedStyle.BackgroundColor);

    [Fact]
    public void Explicit_inherit_of_non_inherited_property_follows_parent()
    {
        var on = new State<bool>(false);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Class = ClassValue.Cx("p", ("on", on.Value)),
                Children = [new Div { Id = "c", Class = "c" }],
            },
        }, ".p { background-color: #ff0000; } .p.on { background-color: #00ff00; } .c { background-color: inherit; }");
        Assert.Equal(C("#ff0000"), BackgroundOf(h.El("c")));
        on.Value = true;
        h.Frame();
        Assert.Equal(C("#00ff00"), BackgroundOf(h.El("c")));
    }

    [Fact]
    public void Rem_follows_root_font_size_through_px_override()
    {
        var h = new PipelineHarness(new Div
        {
            Class = "a", Children = [new Div { Id = "b", Class = "b" }],
        }, "root:hover { font-size: 20px; } .a { font-size: 14px; } .b { width: 2rem; height: 1px; }");
        Assert.Equal(28f, h.Box("b").Width);
        h.Move(300, 200);
        h.Frame();
        Assert.Equal(40f, h.Box("b").Width);
    }

    [Fact]
    public void Hover_on_sibling_restyles_subsequent_sibling_descendants()
    {
        var h = new PipelineHarness(new Div
        {
            Children =
            [
                new Div { Id = "a", Class = "a", Style = "height: 20px" },
                new Div { Class = "b", Children = [new Div { Id = "c", Class = "c", Style = "height: 20px" }] },
            ],
        }, ".c { background-color: #ff0000; } .a:hover ~ .b .c { background-color: #00ff00; }");
        Assert.Equal(C("#ff0000"), BackgroundOf(h.El("c")));
        h.Move(5, 5);
        h.Frame();
        Assert.Equal(C("#00ff00"), BackgroundOf(h.El("c")));
        h.Move(5, 100);
        h.Frame();
        Assert.Equal(C("#ff0000"), BackgroundOf(h.El("c")));
    }

    [Fact]
    public void Focus_within_restyles_ancestor()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "form", Class = "form",
            Children = [new Input { Id = "i", Style = "width: 100px" }],
        }, ".form { background-color: #ff0000; } .form:focus-within { background-color: #00ff00; }");
        h.El("i").Focus();
        h.Frame();
        Assert.Equal(C("#00ff00"), BackgroundOf(h.El("form")));
    }

    static int CountFillRects(DisplayList list) => FrameWalker.Ops(list).Count(o => o == DrawOp.FillRect);

    [Fact]
    public void Caret_appears_when_input_is_focused_even_without_focus_styles()
    {
        var h = new PipelineHarness(new Div { Children = [new Input { Id = "i", Style = "width: 100px" }] }, defaultSheet: false);
        int before = CountFillRects(h.Pipeline.Frame);
        h.Click(5, 5);
        h.Frame();
        Assert.True(h.El("i").IsFocused);
        Assert.Equal(before + 1, CountFillRects(h.Pipeline.Frame));
    }

    [Fact]
    public void Removed_child_disappears_from_layout()
    {
        var show = new State<bool>(true);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Id = "p", Style = "display: flex",
                Children = show.Value
                    ? [new Div { Key = "a", Style = "width: 50px; height: 10px" }, new Div { Key = "b", Id = "b", Style = "width: 30px; height: 10px" }]
                    : [new Div { Key = "b", Id = "b", Style = "width: 30px; height: 10px" }],
            },
        });
        Assert.Equal(50f, h.Box("b").X);
        show.Value = false;
        h.Frame();
        Assert.Equal(0f, h.Box("b").X);
    }

    [Fact]
    public void Border_style_none_has_no_layout_width()
    {
        var h = new PipelineHarness(new Div { Id = "d", Style = "border-width: 10px; width: 50px; height: 50px; box-sizing: content-box", Children = [new Div { Id = "c", Style = "height: 5px" }] });
        Assert.Equal(new Rect(0, 0, 50, 50), h.Box("d"));
        Assert.Equal(0f, h.Box("c").X);
    }

    [Fact]
    public void Layout_uses_the_same_device_pixel_border_widths_as_paint()
    {
        var node = new Div
        {
            Id = "d", Style = "border: 1.5px solid #000000; border-left-width: 0.3px; width: 50px; height: 50px",
            Children = [new Div { Id = "c", Style = "height: 5px" }],
        };
        var h = new PipelineHarness(node);
        // 1x：1.5px → 1px，0.3px → 1px（非零至少 1 个设备像素）
        Assert.Equal(new Rect(1, 1, 48, 5), h.Box("c"));
        // 2x：1.5px 恰好是 3 个设备像素；0.3px → 0.5px（1 个设备像素）
        h.Pipeline.SetViewport(new Size(400, 300), 2);
        h.Frame();
        Assert.Equal(new Rect(0.5f, 1.5f, 48, 5), h.Box("c"));
    }

    [Fact]
    public void Percentage_vertical_padding_resolves_against_width()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "width: 200px; height: 100px",
            Children = [new Div { Id = "d", Style = "padding-top: 10%; height: auto; box-sizing: content-box" }],
        });
        Assert.Equal(20f, h.Box("d").Height);
    }
}

public partial class DevToolsTests
{
    [Fact]
    public void Rule_edits_that_would_break_out_of_the_declaration_block_are_rejected()
    {
        // 两条规则之间有注释：未闭合的注释 / 末尾转义可以让后面的规则嵌进被编辑的规则，而规则数不变
        var f = new Fixture(css: ".item { width: 20px; height: 20px; background-color: #0000ff; } /* c */ .x { color: #ff0000; }");
        var rule = f.Client.Call("Css.getMatchedRules", DevToolsClient.Node(f.Id("i2")))["matched"]!.AsArray()
            .Single(m => m["rule"]!["selectorText"]!.GetString() == ".item")["rule"]!;
        string ruleId = rule["id"]!.GetString();
        string sheetId = rule["sheetId"]!.GetString();
        string before = f.Client.Call("Css.getStyleSheetText", P("sheetId", sheetId))["text"]!.GetString();
        foreach (var hostile in new[] { "color: red /*", "color: red\\", "content: \"abc", "width: calc(1px", "color: red [", "color: red )", "color: red /" })
        {
            Assert.Equal(-32005, f.Client.Fail("Css.setStyleText", new JsonObject
            {
                ["target"] = new JsonObject { ["ruleId"] = ruleId },
                ["text"] = hostile,
            }));
            Assert.Equal(-32005, f.Client.Fail("Css.addRule", new JsonObject { ["selector"] = "#i3", ["text"] = hostile }));
        }
        Assert.Equal(before, f.Client.Call("Css.getStyleSheetText", P("sheetId", sheetId))["text"]!.GetString());
        // 合法的编辑（含闭合的注释、字符串与函数）仍然可以
        f.Client.Call("Css.setStyleText", new JsonObject
        {
            ["target"] = new JsonObject { ["ruleId"] = ruleId },
            ["text"] = "width: calc(10px + 20px); height: 20px; /* x */ font-family: \"a\"",
        });
        f.Client.Call("Css.addRule", new JsonObject { ["selector"] = "#i3", ["text"] = "background-color: #ff00ff" });
        f.Client.Call("Css.addRule", new JsonObject { ["selector"] = "#i1", ["text"] = "background-color: #00ffff" });
        f.Frame();
        Assert.Equal(30, f.El("i2").BoundingRect.Width);
        var r = f.El("i1").BoundingRect;
        ColorAssert.Near(Color.Parse("#00ffff"), f.Pixel(r.Center.X, r.Center.Y), 0.05f);
    }

    [Fact]
    public void Pointer_release_after_a_pick_does_not_reach_the_app()
    {
        int downs = 0, ups = 0, clicks = 0;
        var f = new Fixture(new Div
        {
            Children = [new Button { Id = "b", Style = "width: 80px; height: 30px", OnPointerDown = _ => downs++, OnPointerUp = _ => ups++, OnClick = _ => clicks++ }],
        });
        f.Window.SendKey(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.F12, KeyModifiers.None, false, 0, TimeSpan.Zero));
        var p = f.El("b").BoundingRect.Center;
        f.Window.SendPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, p, PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero));
        Assert.Equal(Diagnostics.Inspection.InspectMode.None, f.DevTools.InspectMode);
        f.Window.SendPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, p, PointerButtons.Left, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
        Assert.Equal((0, 0, 0), (downs, ups, clicks));
        // 之后的点击照常进入应用
        f.Window.SendPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, p, PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, TimeSpan.Zero));
        f.Window.SendPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, p, PointerButtons.Left, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero));
        Assert.Equal((1, 1, 1), (downs, ups, clicks));
    }
}
