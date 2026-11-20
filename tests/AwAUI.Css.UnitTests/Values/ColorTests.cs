using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Values.ValueTestHelper;

namespace AwAUI.Css.UnitTests.Values;

public class ColorTests
{
    static string C(string text)
    {
        Assert.True(CssColorParser.TryParse(text, out var c), text);
        return c.ToString();
    }

    [Theory]
    [InlineData("#f00", "rgb(255, 0, 0)")]
    [InlineData("#F00", "rgb(255, 0, 0)")]
    [InlineData("#f008", "rgba(255, 0, 0, 0.533)")]
    [InlineData("#00ff00", "rgb(0, 255, 0)")]
    [InlineData("#0000ff80", "rgba(0, 0, 255, 0.502)")]
    [InlineData("rgb(1, 2, 3)", "rgb(1, 2, 3)")]
    [InlineData("rgba(1, 2, 3, 0.5)", "rgba(1, 2, 3, 0.5)")]
    [InlineData("rgb(1 2 3)", "rgb(1, 2, 3)")]
    [InlineData("rgb(1 2 3 / 50%)", "rgba(1, 2, 3, 0.5)")]
    [InlineData("rgb(100% 0% 50%)", "rgb(255, 0, 128)")]
    [InlineData("rgb(100%, 0%, 50%)", "rgb(255, 0, 128)")]
    [InlineData("rgb(300 -5 0)", "rgb(255, 0, 0)")]
    [InlineData("rgb(none 255 none)", "rgb(0, 255, 0)")]
    [InlineData("rgba(1 2 3)", "rgb(1, 2, 3)")]
    [InlineData("rgb(1, 2, 3, 2)", "rgb(1, 2, 3)")]
    [InlineData("rgb(calc(100 + 55) 0 0)", "rgb(155, 0, 0)")]
    [InlineData("hsl(0, 100%, 50%)", "rgb(255, 0, 0)")]
    [InlineData("hsl(120deg 100% 25%)", "rgb(0, 128, 0)")]
    [InlineData("hsl(240 100 50)", "rgb(0, 0, 255)")]
    [InlineData("hsla(0.5turn, 100%, 50%, 0.25)", "rgba(0, 255, 255, 0.25)")]
    [InlineData("hsl(-120 100% 50%)", "rgb(0, 0, 255)")]
    [InlineData("hsl(none 0% 100%)", "rgb(255, 255, 255)")]
    [InlineData("red", "rgb(255, 0, 0)")]
    [InlineData("RebeccaPurple", "rgb(102, 51, 153)")]
    [InlineData("lightgoldenrodyellow", "rgb(250, 250, 210)")]
    [InlineData("transparent", "rgba(0, 0, 0, 0)")]
    [InlineData("currentColor", "currentcolor")]
    public void Valid_colors(string text, string expected) => Assert.Equal(expected, C(text));

    [Theory]
    [InlineData("#ff")]
    [InlineData("#fffff")]
    [InlineData("#ggg")]
    [InlineData("rgb(1, 2)")]
    [InlineData("rgb(1 2 3 4)")]
    [InlineData("rgb(1, 2 3)")]
    [InlineData("rgb(1 2, 3)")]
    [InlineData("rgb(1, 2%, 3)")]
    [InlineData("rgb(none, 2, 3)")]
    [InlineData("rgb(1 2 3 / )")]
    [InlineData("rgb(1 2 3 / 1 / 1)")]
    [InlineData("rgb(1, 2, 3,)")]
    [InlineData("rgb(1px 2 3)")]
    [InlineData("hsl(10%, 50%, 50%)")]
    [InlineData("hsl(10, 50, 50)")]
    [InlineData("hsl(10 50% 50% 0.5)")]
    [InlineData("notacolor")]
    [InlineData("rgb()")]
    [InlineData("lab(50, 0, 0)")]
    [InlineData("1px")]
    public void Invalid_colors(string text) => Assert.False(CssColorParser.TryParse(text, out _), text);

    [Fact]
    public void All_148_named_colors_are_known()
    {
        Assert.Equal(148, CssColorParser.NamedColors.Count);
        foreach (var name in CssColorParser.NamedColors.Keys) Assert.True(CssColorParser.TryParse(name, out _), name);
    }

    [Fact]
    public void CurrentColor_is_kept_as_keyword_and_resolved_at_use_time()
    {
        var s = Compute("border-top-color", "currentcolor", extra: "color: blue;");
        Assert.True(s.BorderTopColor.IsCurrentColor);
        Assert.Equal(Color.FromRgb(0, 0, 255), s.ResolveColor(s.BorderTopColor));
        Assert.Equal(Color.FromRgb(1, 2, 3), s.ResolveColor(new CssColor(Color.FromRgb(1, 2, 3))));
    }

    [Fact]
    public void Color_property_currentcolor_inherits()
    {
        var engine = new AwAUI.Css.Cascade.StyleEngine();
        engine.AddStyleSheet(StyleSheet.Parse("p { color: green } span { color: currentColor }"));
        var root = new El("p").Add(new El("span"));
        var ps = engine.Resolve(root, null);
        var ss = engine.Resolve(root[0], ps);
        Assert.Equal("rgb(0, 128, 0)", ss.GetPropertyValueText("color"));
        Assert.False(ss.Color.IsCurrentColor);
    }

    [Fact]
    public void Invalid_color_declaration_reports_AW2003()
    {
        var d = Assert.Single(Diagnostics("color: #12"));
        Assert.Equal(CssDiagnosticCodes.InvalidValue, d.Code);
        Assert.Contains("color", d.Message);
    }
}
