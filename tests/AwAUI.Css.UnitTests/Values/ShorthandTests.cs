using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Values.ValueTestHelper;

namespace AwAUI.Css.UnitTests.Values;

public class ShorthandTests
{
    static ComputedStyle S(string declarations)
    {
        var e = new AwAUI.Css.Cascade.StyleEngine();
        e.AddStyleSheet(StyleSheet.Parse("div { " + declarations + " }"));
        return e.Resolve(new El("div"), null);
    }

    static string[] Sides(ComputedStyle s, string prefix, string suffix = "")
        => new[] { "top", "right", "bottom", "left" }.Select(side => s.GetPropertyValueText(prefix + side + suffix)!).ToArray();

    [Theory]
    [InlineData("1px", "1px,1px,1px,1px")]
    [InlineData("1px 2px", "1px,2px,1px,2px")]
    [InlineData("1px 2px 3px", "1px,2px,3px,2px")]
    [InlineData("1px 2px 3px 4px", "1px,2px,3px,4px")]
    [InlineData("auto 10%", "auto,10%,auto,10%")]
    public void Margin(string value, string expected) => Assert.Equal(expected, string.Join(",", Sides(S("margin: " + value), "margin-")));

    [Fact]
    public void Padding_inset_and_border_box4()
    {
        Assert.Equal("1px,2px,1px,2px", string.Join(",", Sides(S("padding: 1px 2px"), "padding-")));
        Assert.Equal("0px,auto,0px,auto", string.Join(",", Sides(S("inset: 0 auto"), "")));
        Assert.Equal("1px,3px,5px,3px", string.Join(",", Sides(S("border-style: solid; border-width: thin medium thick"), "border-", "-width")));
        Assert.Equal("solid,dashed,solid,dashed", string.Join(",", Sides(S("border-style: solid dashed"), "border-", "-style")));
        Assert.Equal("rgb(255, 0, 0),currentcolor,rgb(255, 0, 0),currentcolor",
            string.Join(",", Sides(S("border-color: red currentcolor"), "border-", "-color")));
    }

    [Theory]
    [InlineData("margin", "1px 2px 3px 4px 5px")]
    [InlineData("margin", "")]
    [InlineData("margin", "1px red")]
    [InlineData("padding", "-1px")]
    [InlineData("border-width", "1px auto")]
    [InlineData("gap", "1px 2px 3px")]
    [InlineData("overflow", "hidden scroll auto")]
    [InlineData("flex-flow", "row column")]
    [InlineData("flex", "1 2 3")]
    [InlineData("flex", "none 1")]
    [InlineData("flex", "auto auto")]
    [InlineData("border", "1px 2px solid")]
    [InlineData("border", "solid solid")]
    [InlineData("border-top", "red red")]
    [InlineData("border-radius", "1px / ")]
    [InlineData("border-radius", "1px 2px 3px 4px 5px")]
    [InlineData("font", "bold")]
    [InlineData("font", "12px")]
    [InlineData("font", "12px/ serif")]
    [InlineData("font", "caption")]
    [InlineData("background", "red, blue")]
    [InlineData("background", "url(a) url(b)")]
    [InlineData("all", "red")]
    public void Invalid_shorthands(string property, string value) => Assert.False(IsValid(property, value), property + ": " + value);

    [Fact]
    public void Border_shorthands()
    {
        var s = S("border: 2px solid red");
        Assert.Equal(new[] { "2px", "2px", "2px", "2px" }, Sides(s, "border-", "-width"));
        Assert.Equal(new[] { "solid", "solid", "solid", "solid" }, Sides(s, "border-", "-style"));
        Assert.All(Sides(s, "border-", "-color"), c => Assert.Equal("rgb(255, 0, 0)", c));

        // 省略的部分重置为初始值
        var t = S("border-top-color: blue; border: solid");
        Assert.True(t.BorderTopColor.IsCurrentColor);
        Assert.Equal(3f, t.BorderTopWidth);

        var side = S("border-left: dotted 4px");
        Assert.Equal("4px", side.GetPropertyValueText("border-left-width"));
        Assert.Equal("dotted", side.GetPropertyValueText("border-left-style"));
        Assert.Equal("0px", side.GetPropertyValueText("border-top-width"));
    }

    [Fact]
    public void Border_radius()
    {
        var s = S("border-radius: 1px 2px 3px 4px / 5px 6px");
        Assert.Equal("1px 5px", s.GetPropertyValueText("border-top-left-radius"));
        Assert.Equal("2px 6px", s.GetPropertyValueText("border-top-right-radius"));
        Assert.Equal("3px 5px", s.GetPropertyValueText("border-bottom-right-radius"));
        Assert.Equal("4px 6px", s.GetPropertyValueText("border-bottom-left-radius"));
        var t = S("border-radius: 50%");
        Assert.Equal("50%", t.GetPropertyValueText("border-bottom-left-radius"));
        var u = S("border-radius: 1px 2px");
        Assert.Equal("1px", u.GetPropertyValueText("border-bottom-right-radius"));
        Assert.Equal("2px", u.GetPropertyValueText("border-bottom-left-radius"));
    }

    [Theory]
    [InlineData("none", "0", "0", "auto")]
    [InlineData("auto", "1", "1", "auto")]
    [InlineData("initial", "0", "1", "auto")]
    [InlineData("2", "2", "1", "0px")]
    [InlineData("2 3", "2", "3", "0px")]
    [InlineData("10px", "1", "1", "10px")]
    [InlineData("2 3 10%", "2", "3", "10%")]
    [InlineData("content 2", "2", "1", "content")]
    [InlineData("0 0 auto", "0", "0", "auto")]
    public void Flex(string value, string grow, string shrink, string basis)
    {
        var s = S("flex: " + value);
        Assert.Equal(grow, s.GetPropertyValueText("flex-grow"));
        Assert.Equal(shrink, s.GetPropertyValueText("flex-shrink"));
        Assert.Equal(basis, s.GetPropertyValueText("flex-basis"));
    }

    [Fact]
    public void Flex_flow_gap_overflow()
    {
        var s = S("flex-flow: wrap column-reverse; gap: 4px 10%; overflow: hidden");
        Assert.Equal(CssFlexDirection.ColumnReverse, s.FlexDirection);
        Assert.Equal(CssFlexWrap.Wrap, s.FlexWrap);
        Assert.Equal("4px", s.GetPropertyValueText("row-gap"));
        Assert.Equal("10%", s.GetPropertyValueText("column-gap"));
        Assert.Equal(CssOverflow.Hidden, s.OverflowX);
        Assert.Equal(CssOverflow.Hidden, s.OverflowY);
        var t = S("flex-direction: column; flex-flow: wrap; gap: 3px; overflow: clip auto");
        Assert.Equal(CssFlexDirection.Row, t.FlexDirection);
        Assert.Equal("3px", t.GetPropertyValueText("column-gap"));
        Assert.Equal(CssOverflow.Hidden, t.OverflowX);
        Assert.Equal(CssOverflow.Auto, t.OverflowY);
    }

    [Fact]
    public void Background_shorthand()
    {
        var s = S("background: red");
        Assert.Equal("rgb(255, 0, 0)", s.GetPropertyValueText("background-color"));
        Assert.True(s.BackgroundImage.IsNone);

        var t = S("background-color: blue; background: url(a.png) no-repeat center / cover");
        Assert.Equal("rgba(0, 0, 0, 0)", t.GetPropertyValueText("background-color"));
        Assert.Equal("url(\"a.png\")", t.GetPropertyValueText("background-image"));

        var u = S("background: linear-gradient(red, blue) left top, url(b.png) 10px 20px repeat-x #00f");
        Assert.Equal(2, u.BackgroundImage.Layers.Count);
        Assert.Equal("rgb(0, 0, 255)", u.GetPropertyValueText("background-color"));
    }

    [Fact]
    public void Font_shorthand()
    {
        var s = S("font-weight: 900; line-height: 3; font: italic bold condensed 12px/1.5 \"Fira Sans\", serif");
        Assert.Equal(CssFontStyle.Italic, s.FontStyle);
        Assert.Equal(700f, s.FontWeight);
        Assert.Equal(75f, s.FontStretch);
        Assert.Equal(12f, s.FontSize);
        Assert.Equal("1.5", s.GetPropertyValueText("line-height"));
        Assert.Equal("\"Fira Sans\", serif", s.GetPropertyValueText("font-family"));

        // 省略的子属性重置为初始值
        var t = S("font-weight: 900; line-height: 3; font: 2em monospace");
        Assert.Equal(400f, t.FontWeight);
        Assert.Equal("normal", t.GetPropertyValueText("line-height"));
        Assert.Equal(32f, t.FontSize);

        var u = S("font: normal normal 600 small-caps 10px a");
        Assert.Equal(600f, u.FontWeight);
    }

    [Fact]
    public void All_shorthand_resets_everything()
    {
        var e = new AwAUI.Css.Cascade.StyleEngine();
        e.AddStyleSheet(StyleSheet.Parse("p { color: red; width: 5px } span { width: 3px; all: initial } b { all: inherit }"));
        var p = new El("p").Add(new El("span"), new El("b"));
        var ps = e.Resolve(p, null);
        var ss = e.Resolve(p[0], ps);
        Assert.Equal("rgb(0, 0, 0)", ss.GetPropertyValueText("color"));
        Assert.Equal("auto", ss.GetPropertyValueText("width"));
        var bs = e.Resolve(p[1], ps);
        Assert.Equal("5px", bs.GetPropertyValueText("width"));
        Assert.Equal("rgb(255, 0, 0)", bs.GetPropertyValueText("color"));
    }

    [Fact]
    public void Shorthand_with_var_is_substituted_at_computed_value_time()
    {
        var s = S("--m: 1px 2px; margin: var(--m); --b: 3px solid; border-top: var(--b) green");
        Assert.Equal("1px,2px,1px,2px", string.Join(",", Sides(s, "margin-")));
        Assert.Equal("3px", s.GetPropertyValueText("border-top-width"));
        Assert.Equal("rgb(0, 128, 0)", s.GetPropertyValueText("border-top-color"));

        // 替换后无效：全部长属性按 unset 处理
        var t = S("--m: red; margin-top: 7px; margin: var(--m)");
        Assert.Equal("0px", t.GetPropertyValueText("margin-top"));
    }
}
