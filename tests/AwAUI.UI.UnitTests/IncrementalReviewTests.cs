using AwAUI.Graphics;

namespace AwAUI.UI.UnitTests;

/// <summary>增量绘制评审发现的问题的回归测试。</summary>
public class IncrementalReviewTests
{
    static bool IsColor(Color c, Color expected) =>
        Math.Abs(c.R - expected.R) < 0.05f && Math.Abs(c.G - expected.G) < 0.05f && Math.Abs(c.B - expected.B) < 0.05f;

    /// <summary>元素在不录制它的那几帧里（opacity:0 的祖先之下）变化了，之后重新显示：不能沿用变化之前录制的缓存。</summary>
    [Fact]
    public void Change_under_opacity_zero_ancestor_is_not_lost_when_shown_again()
    {
        var hidden = new State<bool>(false);
        var color = new State<string>("#ff0000");
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Style = "opacity: " + (hidden.Value ? "0" : "1"),
                Children = [new Div { Id = "box", Style = "width: 40px; height: 40px; background-color: " + color.Value }],
            },
        }, defaultSheet: false);
        hidden.Value = true;
        h.Frame();
        color.Value = "#0000ff";
        h.Frame();
        hidden.Value = false;
        h.Frame();
        using var bmp = h.Render();
        Assert.True(IsColor(bmp.GetPixel(20, 20), Color.Parse("#0000ff")), bmp.GetPixel(20, 20).ToString());
    }

    /// <summary>元素被剔除（在视口外）时变化了，之后移回视口：不能沿用移出之前录制的缓存。</summary>
    [Fact]
    public void Change_while_culled_is_not_lost_when_scrolled_back_into_view()
    {
        var offset = new State<int>(0);
        var color = new State<string>("#ff0000");
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Children =
                [
                    new Div { Style = "height: " + offset.Value + "px" },
                    new Div
                    {
                        Id = "wrap",
                        Children = [new Div { Id = "box", Style = "width: 40px; height: 40px; background-color: " + color.Value }],
                    },
                ],
            },
        }, width: 100, height: 100, defaultSheet: false);
        offset.Value = 500;
        h.Frame();
        color.Value = "#0000ff";
        h.Frame();
        offset.Value = 0;
        h.Frame();
        using var bmp = h.Render();
        Assert.True(IsColor(bmp.GetPixel(20, 20), Color.Parse("#0000ff")), bmp.GetPixel(20, 20).ToString());
    }

    /// <summary>层叠上下文在逃逸期间（不作为录制单元）内部变化，之后不再逃逸：不能沿用逃逸之前录制的缓存。</summary>
    [Fact]
    public void Change_while_context_escapes_is_not_lost_after_it_stops_escaping()
    {
        var clip = new State<bool>(false);
        var color = new State<string>("#ff0000");
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Style = "position: relative; width: 200px; height: 200px",
                Children =
                [
                    new Div
                    {
                        Style = "width: 100px; height: 100px" + (clip.Value ? "; overflow: hidden" : ""),
                        Children =
                        [
                            new Div
                            {
                                Style = "opacity: 0.99",
                                Children =
                                [
                                    new Div { Style = "position: absolute; left: 150px; top: 150px; width: 20px; height: 20px; background-color: #00ff00" },
                                    new Div { Id = "box", Style = "width: 40px; height: 40px; background-color: " + color.Value },
                                ],
                            },
                        ],
                    },
                ],
            },
        }, defaultSheet: false);
        clip.Value = true;
        h.Frame();
        color.Value = "#0000ff";
        h.Frame();
        clip.Value = false;
        h.Frame();
        using var bmp = h.Render();
        var c = bmp.GetPixel(20, 20);
        Assert.True(Math.Abs(c.B - 0.99f) < 0.05f && c.R < 0.05f, c.ToString());
    }
}
