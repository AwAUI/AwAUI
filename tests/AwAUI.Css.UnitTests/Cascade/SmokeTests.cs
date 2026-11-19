using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Cascade.CascadeTestHelper;

namespace AwAUI.Css.UnitTests.Cascade;

public class SmokeTests
{
    [Fact]
    public void Initial_style_has_spec_initial_values()
    {
        var s = ComputedStyle.Initial;
        Assert.Equal(CssDisplay.Inline, s.Display);
        Assert.Equal(16f, s.FontSize);
        Assert.Equal(400f, s.FontWeight);
        Assert.True(s.BorderTopColor.IsCurrentColor);
        Assert.Equal(0f, s.BorderTopWidth);   // 初始值 medium，但初始样式 none 使计算值为 0
        Assert.Equal(SizeKind.Auto, s.Width.Kind);
        Assert.Equal(SizeKind.None, s.MaxWidth.Kind);
        Assert.Equal("rgb(0, 0, 0)", s.Text("color"));
        Assert.Equal(1f, s.Opacity);
        Assert.Equal(1f, s.FlexShrink);
        Assert.Equal("normal", s.Text("line-height"));
    }

    [Fact]
    public void Simple_rule_applies()
    {
        var el = new El("div", classes: "a");
        var s = One(".a { width: 10px; color: red; margin: 1px 2px; padding: 5%; font-size: 2em }", el);
        Assert.Equal("10px", s.Text("width"));
        Assert.Equal("rgb(255, 0, 0)", s.Text("color"));
        Assert.Equal("1px", s.Text("margin-top"));
        Assert.Equal("2px", s.Text("margin-left"));
        Assert.Equal("5%", s.Text("padding-bottom"));
        Assert.Equal(32f, s.FontSize);
    }
}
