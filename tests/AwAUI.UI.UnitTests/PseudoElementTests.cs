using AwAUI.Graphics;
using AwAUI.Imaging;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 伪元素的 UI 接入（第九批）：<c>::before / ::after</c> 生成的盒（容器中作为第一个 / 最后一个布局子节点、段落中作为行内文字）、
/// 继承、命中测试报告原始元素、增删与样式变化的失效，<c>::placeholder</c> / <c>::selection</c>。TestTextEngine：每字符 1em。
/// </summary>
public class PseudoElementTests
{
    static (Element? Before, Element? After) Gen(PipelineHarness h, string id) => h.Pipeline.GetGenerated(h.El(id));

    [Fact]
    public void Before_and_after_are_first_and_last_flex_items()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "row", Class = "row",
            Children = [new Div { Id = "a", Style = "width: 20px; height: 10px" }],
        }, ".row { display: flex; align-items: flex-start; font-size: 10px; color: #0000ff }"
           + ".row::before { content: 'ab'; }"
           + ".row::after { content: ''; width: 5px; height: 6px; background-color: #ff0000 }");
        var (before, after) = Gen(h, "row");
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(new Rect(0, 0, 20, 10), before!.BoundingRect);
        Assert.Equal(new Rect(20, 0, 20, 10), h.Box("a"));
        Assert.Equal(new Rect(40, 0, 5, 6), after!.BoundingRect);
        // 继承：伪元素从原始元素继承 color 与 font-size
        Assert.Equal(10f, before.ComputedStyle!.FontSize);
        Assert.Equal(Color.Parse("#0000ff"), before.ComputedStyle.ResolveColor(before.ComputedStyle.Color));
        // 伪元素不在子节点列表中（选择器、协调器、DevTools 树看不到它）
        Assert.Single(h.El("row").Children);
        Assert.Same(h.El("row"), before.Parent);
        // 绘制：::after 的红色背景
        using var bmp = h.Render();
        Assert.Equal(Color.Parse("#ff0000"), bmp.GetPixel(42, 3));
    }

    [Fact]
    public void Hit_test_on_generated_box_reports_originating_element()
    {
        var clicks = 0;
        var h = new PipelineHarness(new Div
        {
            Id = "host", Class = "host",
            OnClick = _ => clicks++,
            Children = [new Div { Id = "inner", Style = "width: 10px; height: 10px" }],
        }, ".host { position: relative; width: 100px; height: 40px }"
           + ".host::after { content: ''; position: absolute; left: 50px; top: 0; width: 30px; height: 30px; background-color: #00ff00 }");
        var after = Gen(h, "host").After;
        Assert.NotNull(after);
        Assert.Equal(new Rect(50, 0, 30, 30), after!.BoundingRect);
        Assert.Same(h.El("host"), h.Pipeline.HitTest(new Point(60, 10)));
        Assert.Same(h.El("host"), h.Pipeline.HitTester.HitTest(new Point(60, 10)));
        h.Click(60, 10);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public void Generated_text_joins_paragraph_of_text_element_and_inline_spans()
    {
        var h = new PipelineHarness(new Text
        {
            Id = "t", Class = "t",
            Children = ["cd", new Span("ef") { Id = "s", Class = "s" }],
        }, ".t { font-size: 10px } .t::before { content: 'ab' } .t::after { content: '!' } .s::before { content: '[' } .s::after { content: ']' }");
        // ab + cd + [ef] + ! = 9 个字符
        Assert.Equal("cdef", RenderBox.Get(h.El("t")).Paragraph!.Layout.Text.Replace("ab", "").Replace("[", "").Replace("]", "").Replace("!", ""));
        Assert.Equal(90, RenderBox.Get(h.El("t")).Paragraph!.Layout.MaxIntrinsicWidth);
        // 段落中的伪元素没有自己的盒
        Assert.Equal(default, Gen(h, "t").Before!.BoundingRect);
    }

    [Fact]
    public void Attr_and_quotes_are_evaluated_and_content_none_removes_the_box()
    {
        var on = new State<bool>(true);
        var h = new PipelineHarness(new Lambda
        {
            Render = () =>
            {
                var d = new Div { Id = "d", Class = on.Value ? "tip on" : "tip", Style = "display: flex; font-size: 10px" };
                d.Attributes["data-label"] = "xyz";
                return d;
            },
        }, ".tip.on::before { content: open-quote attr(data-label) close-quote; }");
        var before = Gen(h, "d").Before;
        Assert.NotNull(before);
        Assert.Equal("“xyz”", ((GeneratedContentElement)before!).Text);
        Assert.Equal(50, before.BoundingRect.Width);
        Assert.Equal(1, h.Pipeline.GeneratedBoxCount);

        on.Value = false;
        h.Frame();
        Assert.Null(Gen(h, "d").Before);
        Assert.Equal(0, h.Pipeline.GeneratedBoxCount);
        Assert.Equal(0, h.Box("d").Height);
    }

    [Fact]
    public void Tailwind_style_content_variable_and_hover_state_on_originating_element()
    {
        // Tailwind v4 的 before: 变体：content: var(--tw-content)，@property 给出初始值 ""
        var h = new PipelineHarness(new Div { Id = "b", Class = "btn" },
            "@property --tw-content { syntax: '*'; inherits: false; initial-value: ''; }"
            + ".btn { position: relative; display: block; width: 50px; height: 20px }"
            + ".btn::before { content: var(--tw-content); position: absolute; inset: 0; background-color: #000000; opacity: 0 }"
            + ".btn:hover::before { opacity: 0.5 }");
        var before = Gen(h, "b").Before;
        Assert.NotNull(before);
        Assert.Equal(new Rect(0, 0, 50, 20), before!.BoundingRect);
        Assert.Equal(0f, before.ComputedStyle!.Opacity);
        h.Move(10, 10);
        h.Frame();
        Assert.Equal(0.5f, Gen(h, "b").Before!.ComputedStyle!.Opacity);
        using var bmp = h.Render();
        var px = bmp.GetPixel(10, 10);
        Assert.InRange(px.R, 0.45f, 0.55f);
    }

    [Fact]
    public void No_generated_boxes_without_content_rules()
    {
        // 只有不带 content 的 ::before 规则（preflight 的 *, ::before 盒模型重置）：不解析伪元素、不生成盒
        var h = new PipelineHarness(new Div { Id = "d", Children = [new Div { Id = "c" }] }, "*, ::before, ::after { box-sizing: border-box; margin: 0 }");
        Assert.Equal(0, h.Pipeline.GeneratedBoxCount);
        Assert.Null(Gen(h, "d").Before);
    }

    [Fact]
    public void Generated_content_change_repaints_incrementally_like_full_repaint()
    {
        var label = new State<string>("a");
        Node Build() => new Lambda
        {
            Render = () =>
            {
                var d = new Div { Id = "d", Class = "d" };
                d.Attributes["data-x"] = label.Value;
                return new Div { Children = [d, new Div { Id = "other", Style = "height: 10px; background-color: #336699" }] };
            },
        };
        const string css = ".d { display: flex; font-size: 10px; height: 12px } .d::before { content: attr(data-x); color: #cc0000 }"
                           + ".d::after { content: ''; width: 4px; height: 4px; background-color: #00aa00 }";
        var inc = new PipelineHarness(Build(), css);
        label.Value = "abcd";
        inc.Frame();
        var full = new PipelineHarness(Build(), css);
        full.Pipeline.IncrementalPaint = false;
        full.Frame();
        using var a = inc.Render();
        using var b = full.Render();
        Assert.Equal(b.ToRgbaUnpremultiplied(), a.ToRgbaUnpremultiplied());
        Assert.Equal(new Rect(40, 0, 4, 4), Gen(inc, "d").After!.BoundingRect);
    }

    [Fact]
    public void Generated_overflow_keeps_a_transformed_host_visible_and_is_removed_from_damage()
    {
        var visible = new State<bool>(true);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div { Id = "host", Class = visible.Value ? "host" : "host hidden" },
        }, ".host { position: absolute; left: -30px; width: 10px; height: 10px; transform: translateX(1px) }"
           + ".host::after { content: ''; position: absolute; left: 40px; width: 8px; height: 8px; background: red }"
           + ".hidden { display: none }", width: 60, height: 40);
        var pixel = new Point(13, 3);
        using (var initial = h.Render()) Assert.Equal(Color.Parse("#ff0000"), initial.GetPixel(13, 3));
        Assert.Same(h.El("host"), h.Pipeline.HitTest(pixel));
        visible.Value = false;
        h.Frame();
        Assert.True(h.Pipeline.LastDamage is { } damage && damage.Contains(pixel));
        using var result = h.Render();
        Assert.Equal(Color.White, result.GetPixel(13, 3));
    }

    [Fact]
    public void Generated_box_moves_with_its_own_scrolling_container_without_layout()
    {
        var h = new PipelineHarness(new Div
        {
            Id = "scroll", Style = "height: 40px; width: 40px; overflow: auto",
            Children = [new Div { Style = "height: 120px" }],
        }, "#scroll::before { content: ''; display: block; height: 50px; background: red }");
        var before = Gen(h, "scroll").Before!;
        Assert.Equal(0, before.BoundingRect.Y);
        h.El("scroll").ScrollController.JumpTo(new Point(0, 30));
        h.Root.RunFrame();
        Assert.False(h.Pipeline.LastTimings.LayoutRan);
        Assert.Equal(-30, before.BoundingRect.Y);
    }

    [Fact]
    public void Placeholder_pseudo_element_styles_placeholder_text()
    {
        var h = new PipelineHarness(new Input { Id = "i", Placeholder = "ab" },
            "input { font-size: 10px; width: 100px; color: #000000 } input::placeholder { color: #ff0000; letter-spacing: 2px }");
        var view = RenderBox.Get(h.El("i")).GetView(h.Pipeline);
        Assert.NotNull(view.PlaceholderStyle);
        // letter-spacing 2px：两个字符 2 × (10 + 2)
        var para = RenderBox.Get(h.El("i")).Paragraph!;
        para.Layout.Layout(float.PositiveInfinity);
        Assert.Equal(24, para.Layout.Width);
        using var bmp = h.Render();
        bool red = false;
        for (int x = 0; x < 100 && !red; x++)
            for (int y = 0; y < 30 && !red; y++)
            {
                var c = bmp.GetPixel(x, y);
                red = c.R > 0.9f && c.G < 0.1f && c.B < 0.1f;
            }
        Assert.True(red);
    }

    [Fact]
    public void Generated_content_is_bounded_for_hostile_input()
    {
        var css = ".d::before { content: " + string.Join(" ", Enumerable.Repeat("'xxxxxxxxxx'", 500)) + " }";
        var h = new PipelineHarness(new Div { Id = "d", Class = "d", Style = "display: flex; font-size: 1px" }, css);
        var before = (GeneratedContentElement)Gen(h, "d").Before!;
        Assert.Equal(RenderPipeline.MaxGeneratedText, before.Text.Length);
    }

    [Fact]
    public void Golden_pseudo_elements_badge_and_required_marker()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "display: flex; flex-direction: column; gap: 8px; padding: 10px",
            Children =
            [
                new Div { Class = "label", Children = ["Name"] },
                new Div { Class = "badge", Children = ["New"] },
            ],
        }, ".label { display: flex; font-size: 12px; color: #222222 }"
           + ".label::after { content: '*'; color: #dd0000; margin-left: 2px }"
           + ".badge { position: relative; display: flex; width: 60px; height: 24px; padding-left: 16px; align-items: center;"
           + " font-size: 10px; background-color: #e8eefc; border-radius: 6px }"
           + ".badge::before { content: ''; position: absolute; left: 5px; top: 9px; width: 6px; height: 6px; border-radius: 3px; background-color: #2266ee }"
           + ".badge::after { content: ''; position: absolute; inset: -3px; border: 1px dashed #2266ee; border-radius: 8px }",
            width: 100, height: 70);
        using var bmp = h.Render();
        GoldenImage.AssertMatches("ui-pseudo-elements", bmp);
    }
}

/// <summary>本批新测试共用的黄金图比较（与 <see cref="PipelineGoldenTests"/> 相同的规则：AWAUI_UPDATE_GOLDEN=1 时重写）。</summary>
static class GoldenImage
{
    static string Dir([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.Combine(Path.GetDirectoryName(file)!, "Golden");

    public static void AssertMatches(string name, Bitmap actual, int tolerance = 2)
    {
        var dir = Dir();
        var path = Path.Combine(dir, name + ".png");
        var outDir = Path.Combine(AppContext.BaseDirectory, "GoldenOutput");
        Directory.CreateDirectory(outDir);
        PngCodec.Save(actual, Path.Combine(outDir, name + ".actual.png"));
        if (Environment.GetEnvironmentVariable("AWAUI_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(dir);
            PngCodec.Save(actual, path);
            return;
        }
        if (!File.Exists(path)) Assert.Fail($"Golden image '{name}.png' is missing. Review {outDir} and run with AWAUI_UPDATE_GOLDEN=1.");
        using var expected = PngCodec.Load(path);
        Assert.Equal(expected.Size, actual.Size);
        var e = expected.ToRgbaUnpremultiplied();
        var a = actual.ToRgbaUnpremultiplied();
        int bad = 0;
        for (int i = 0; i < e.Length; i++)
            if (Math.Abs(e[i] - a[i]) > tolerance) bad++;
        Assert.True(bad == 0, $"{name}: {bad} channel values differ by more than {tolerance}");
    }
}
