using AwAUI.UI;

namespace AwAUI.Controls.UnitTests.Integration;

/// <summary>Relative positioning plus the desired-size feedback into parent layout.</summary>
public class RelativePanelTests
{
    [Fact]
    public void Auto_height_panel_reports_the_child_extent_to_its_parent()
    {
        var h = new UIHarness(() => new Div
        {
            Style = "width:300px;",
            Children =
            [
                new RelativePanel
                {
                    Children =
                    [
                        new Div { Id = "a", Style = "width:80px;height:40px;" },
                        new Div
                        {
                            Id = "b",
                            Style = "width:50px;height:30px;",
                            Attributes = { ["data-relative-below"] = "a" },
                        },
                    ],
                },
                new Div { Id = "after", Style = "height:10px;" },
            ],
        });

        // 面板高 = b 的下边缘 40 + 30；后续兄弟从面板下方开始
        Assert.Equal(40, h.ById("b").GetBoundingRect().Top, 2);
        Assert.Equal(70, h.ById("after").GetBoundingClientRect().Top, 2);
    }

    [Fact]
    public void Panel_relative_alignment_does_not_feed_its_own_size_back_into_the_extent()
    {
        var h = new UIHarness(() => new Div
        {
            Style = "width:300px;",
            Children =
            [
                new RelativePanel
                {
                    Children =
                    [
                        new Div
                        {
                            Id = "bottom",
                            Style = "width:80px;height:40px;",
                            Attributes = { ["data-relative-align-bottom-with-panel"] = "true" },
                        },
                    ],
                },
                new Div { Id = "after", Style = "height:10px;" },
            ],
        });

        // 与面板底对齐的子项从面板起点参与范围计算：面板恰好容纳它，不会随面板高度反复增长
        Assert.Equal(40, h.ById("bottom").GetBoundingRect().Height, 2);
        Assert.Equal(40, h.ById("after").GetBoundingClientRect().Top, 2);
        Assert.Equal(0, h.ById("bottom").GetBoundingRect().Top, 2);
    }

    [Fact]
    public void Explicit_panel_height_is_preserved()
    {
        var h = new UIHarness(() => new RelativePanel
        {
            Style = "width:200px;height:120px;",
            Children =
            [
                new Div { Id = "a", Style = "width:80px;height:40px;" },
                new Div { Id = "b", Style = "width:50px;height:30px;", Attributes = { ["data-relative-below"] = "a" } },
            ],
        });

        var panel = h.FindAll(e => e.GetAttribute("part") == "panel").Single();
        Assert.Equal(120, panel.GetBoundingRect().Height, 2);
    }

    [Fact]
    public void Auto_width_panel_reports_the_horizontal_extent_in_a_row()
    {
        var h = new UIHarness(() => new Div
        {
            Style = "display:flex;height:100px;",
            Children =
            [
                new RelativePanel
                {
                    Children =
                    [
                        new Div { Id = "a", Style = "width:80px;height:40px;" },
                        new Div { Id = "b", Style = "width:50px;height:30px;", Attributes = { ["data-relative-right-of"] = "a" } },
                    ],
                },
            ],
        });

        // 行中的 auto 宽按子项范围收缩：b 的右边缘 80 + 50
        var panel = h.FindAll(e => e.GetAttribute("part") == "panel").Single();
        Assert.Equal(130, panel.GetBoundingRect().Width, 2);
        Assert.Equal(80, h.ById("b").GetBoundingRect().Left, 2);
    }
}
