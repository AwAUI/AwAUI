namespace AwAUI.UI.UnitTests;

/// <summary>第九批第二次复查：getBoundingClientRect 计入变换、运行时样式覆盖不能吞掉其后的覆盖。</summary>
public class PortalOverlayReview2Tests
{
    [Fact]
    public void Bounding_client_rect_includes_css_transforms_and_scroll()
    {
        const string css = ".outer { position: absolute; left: 10px; top: 20px; width: 200px; height: 100px; transform: translate(30px, 40px); }"
                           + ".inner { width: 50px; height: 20px; transform: scale(2); transform-origin: 0 0; }";
        var h = new PipelineHarness(new Div { Id = "outer", Class = "outer", Children = { new Div { Id = "inner", Class = "inner" } } }, css, 400, 300);
        var inner = h.El("inner");
        Assert.Equal(new Rect(10, 20, 50, 20), inner.BoundingRect);
        Assert.Equal(new Rect(40, 60, 100, 40), inner.GetBoundingClientRect());
        Assert.Equal(new Rect(40, 60, 200, 100), h.El("outer").GetBoundingClientRect());
    }

    [Fact]
    public void Bounding_client_rect_composes_nested_transforms_before_taking_bounds()
    {
        var h = new PipelineHarness(new Div
        {
            Style = "width: 100px; height: 50px; rotate: 45deg; transform-origin: 0 0",
            Children = [new Div { Id = "inner", Style = "width: 40px; height: 20px; rotate: -45deg; transform-origin: 0 0" }],
        });
        var rect = h.El("inner").GetBoundingClientRect();
        Assert.Equal(40, rect.Width, 3);
        Assert.Equal(20, rect.Height, 3);
    }

    [Fact]
    public void Style_override_values_that_would_swallow_later_declarations_are_rejected()
    {
        var h = new PipelineHarness(new Div { Id = "a", Style = "position: absolute; width: 10px; height: 10px" });
        var a = h.El("a");
        foreach (var bad in new[] { "1px /* x", "calc(1px + 2px", "\"abc", "url('x)", "1px\\", "calc(1px]" })
            Assert.Throws<ArgumentException>(() => a.SetStyleOverride("left", bad));
        a.SetStyleOverride("--v", "calc(1px + (2px * 3))");
        a.SetStyleOverride("font-family", "\"A (b)\", serif");
        a.SetStyleOverride("left", "calc(1px + (2px * 3))");
        a.SetStyleOverride("top", "5px");
        h.Frame();
        Assert.Equal(new Rect(7, 5, 10, 10), a.BoundingRect);
    }
}
