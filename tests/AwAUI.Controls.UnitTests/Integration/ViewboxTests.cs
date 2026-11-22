using AwAUI.UI;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>Uniform fitting and the desired-size feedback into parent layout.</summary>
public class ViewboxTests
{
    static Element Part(UIHarness h, string part) => h.FindAll(e => e.GetAttribute("part") == part).Single();

    [Fact]
    public void Uniform_viewbox_reports_the_scaled_height_to_the_parent()
    {
        var h = new UIHarness(() => new Div
        {
            Style = "width:200px;",
            Children =
            [
                new Viewbox
                {
                    Style = "width:100px;",
                    Children = [new Div { Id = "content", Style = "width:200px;height:200px;" }],
                },
                new Div { Id = "after", Style = "height:10px;" },
            ],
        });

        // 自然尺寸 200×200、视口宽 100 → 缩放 0.5，Viewbox 高 100，后续兄弟紧接其后
        var viewport = Part(h, "viewport");
        Assert.Equal(100, viewport.GetBoundingRect().Width, 2);
        Assert.Equal(100, viewport.GetBoundingRect().Height, 2);
        Assert.Equal(100, h.ById("content").GetBoundingClientRect().Width, 2);
        Assert.Equal(100, h.ById("after").GetBoundingClientRect().Top, 2);
    }

    [Fact]
    public void Uniform_viewbox_derives_height_from_the_natural_aspect_ratio()
    {
        var h = new UIHarness(() => new Div
        {
            Style = "width:200px;",
            Children =
            [
                new Viewbox
                {
                    Style = "width:100px;",
                    Children = [new Div { Id = "content", Style = "width:200px;height:100px;" }],
                },
                new Div { Id = "after", Style = "height:10px;" },
            ],
        });

        // 2:1 的内容在 100 宽的视口中是 100×50
        Assert.Equal(50, Part(h, "viewport").GetBoundingRect().Height, 2);
        Assert.Equal(0.5f, h.ById("content").GetBoundingClientRect().Width / 200, 3);
        Assert.Equal(50, h.ById("after").GetBoundingClientRect().Top, 2);
    }

    [Fact]
    public void Explicit_height_keeps_the_box_size_and_centers_the_scaled_content()
    {
        var h = new UIHarness(() => new Viewbox
        {
            Style = "width:200px;height:50px;",
            Children = [new Div { Id = "content", Style = "width:200px;height:100px;" }],
        });

        var viewport = Part(h, "viewport");
        Assert.Equal(200, viewport.GetBoundingRect().Width, 2);
        Assert.Equal(50, viewport.GetBoundingRect().Height, 2);
        // 缩放 0.5，水平居中：内容绘制宽 100，左侧留白 50
        var rect = h.ById("content").GetBoundingClientRect();
        Assert.Equal(100, rect.Width, 2);
        Assert.Equal(50, rect.Height, 2);
        Assert.Equal(50, rect.X, 2);
    }

    [Fact]
    public void None_stretch_keeps_natural_size_without_a_scaled_height()
    {
        var h = new UIHarness(() => new Div
        {
            Style = "width:200px;",
            Children =
            [
                new Viewbox
                {
                    Stretch = ViewboxStretch.None,
                    Children = [new Div { Id = "content", Style = "width:200px;height:100px;" }],
                },
                new Div { Id = "after", Style = "height:10px;" },
            ],
        });

        Assert.Equal(200, h.ById("content").GetBoundingClientRect().Width, 2);
        Assert.Equal(100, h.ById("after").GetBoundingClientRect().Top, 2);
    }

    [Fact]
    public void Switching_to_none_removes_the_reported_aspect_ratio_and_scale()
    {
        var stretch = new State<ViewboxStretch>(ViewboxStretch.Uniform);
        var h = new UIHarness(() => new Div
        {
            Style = "width:200px;",
            Children =
            [
                new Viewbox
                {
                    Stretch = stretch.Value,
                    Style = "width:100px;",
                    Children = [new Div { Id = "content", Style = "width:200px;height:100px;" }],
                },
                new Div { Id = "after", Style = "height:10px;" },
            ],
        });
        Assert.Equal(50, h.ById("after").GetBoundingClientRect().Top, 2);

        stretch.Value = ViewboxStretch.None;
        h.Frame();

        Assert.Equal(100, h.ById("content").GetBoundingClientRect().Height, 2);
        Assert.Equal(200, h.ById("content").GetBoundingClientRect().Width, 2);
        Assert.Equal(100, h.ById("after").GetBoundingClientRect().Top, 2);
    }
}
