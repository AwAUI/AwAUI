using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Values.ValueTestHelper;

namespace AwAUI.Css.UnitTests.Values;

public class ComplexValueTests
{
    // ================= 背景图像与渐变 =================

    [Theory]
    [InlineData("none", "none")]
    [InlineData("url(a.png)", "url(\"a.png\")")]
    [InlineData("url(\"b c.png\")", "url(\"b c.png\")")]
    [InlineData("linear-gradient(red, blue)", "linear-gradient(180deg, rgb(255, 0, 0), rgb(0, 0, 255))")]
    [InlineData("linear-gradient(45deg, red 10%, blue)", "linear-gradient(45deg, rgb(255, 0, 0) 10%, rgb(0, 0, 255))")]
    [InlineData("linear-gradient(to right, red, blue)", "linear-gradient(90deg, rgb(255, 0, 0), rgb(0, 0, 255))")]
    [InlineData("linear-gradient(to top, red, blue)", "linear-gradient(0deg, rgb(255, 0, 0), rgb(0, 0, 255))")]
    [InlineData("linear-gradient(to left top, red, blue)", "linear-gradient(to top left, rgb(255, 0, 0), rgb(0, 0, 255))")]
    [InlineData("linear-gradient(0.5turn, red, blue)", "linear-gradient(180deg, rgb(255, 0, 0), rgb(0, 0, 255))")]
    [InlineData("linear-gradient(red 0 50%, blue)", "linear-gradient(180deg, rgb(255, 0, 0) 0px, rgb(255, 0, 0) 50%, rgb(0, 0, 255))")]
    [InlineData("linear-gradient(red, 30%, blue)", "linear-gradient(180deg, rgb(255, 0, 0), 30%, rgb(0, 0, 255))")]
    [InlineData("repeating-linear-gradient(red, blue 2em)", "repeating-linear-gradient(180deg, rgb(255, 0, 0), rgb(0, 0, 255) 32px)")]
    [InlineData("radial-gradient(red, blue)", "radial-gradient(ellipse at 50% 50%, rgb(255, 0, 0), rgb(0, 0, 255))")]
    [InlineData("radial-gradient(circle, red, blue)", "radial-gradient(circle at 50% 50%, rgb(255, 0, 0), rgb(0, 0, 255))")]
    [InlineData("radial-gradient(circle 10px at left top, red, blue)", "radial-gradient(circle 10px at 0% 0%, rgb(255, 0, 0), rgb(0, 0, 255))")]
    [InlineData("radial-gradient(10px 20% at right 5px bottom 10%, red, blue)", "radial-gradient(ellipse 10px 20% at calc(100% - 5px) 90%, rgb(255, 0, 0), rgb(0, 0, 255))")]
    [InlineData("radial-gradient(closest-side circle at 30% 40%, red, blue)", "radial-gradient(circle closest-side at 30% 40%, rgb(255, 0, 0), rgb(0, 0, 255))")]
    [InlineData("url(a.png), linear-gradient(red, blue), none", "url(\"a.png\"), linear-gradient(180deg, rgb(255, 0, 0), rgb(0, 0, 255)), none")]
    public void Background_images(string value, string expected) => Assert.Equal(expected, Text("background-image", value));

    [Theory]
    [InlineData("linear-gradient(red)")]
    [InlineData("linear-gradient(10px, red, blue)")]
    [InlineData("linear-gradient(to middle, red, blue)")]
    [InlineData("linear-gradient(to left right, red, blue)")]
    [InlineData("linear-gradient(45deg red, blue)")]
    [InlineData("linear-gradient(red, 10%, 20%, blue)")]
    [InlineData("linear-gradient(10%, red, blue)")]
    [InlineData("linear-gradient(red, blue, 10%)")]
    [InlineData("radial-gradient(circle 10%, red, blue)")]
    [InlineData("radial-gradient(circle 1px 2px, red, blue)")]
    [InlineData("radial-gradient(ellipse 10px, red, blue)")]
    [InlineData("radial-gradient(at, red, blue)")]
    [InlineData("conic-gradient(red, blue)")]
    [InlineData("url(a.png) url(b.png)")]
    [InlineData("url(a.png),")]
    public void Invalid_background_images(string value) => Assert.False(IsValid("background-image", value), value);

    [Fact]
    public void Gradient_object_model()
    {
        var img = Compute("background-image", "linear-gradient(30deg, red, 40%, rgb(0 0 255 / 0.5) 2em)", extra: "font-size: 10px;").BackgroundImage;
        var g = Assert.IsType<LinearGradient>(Assert.Single(img.Layers));
        Assert.Equal(30, g.AngleDegrees);
        Assert.Equal(3, g.Stops.Count);
        Assert.True(g.Stops[1].IsHint);
        Assert.Equal(LengthPercentage.FromPercentage(40), g.Stops[1].Position);
        Assert.Equal(20f, g.Stops[2].Position!.Value.Length);
        Assert.Equal(0.5f, g.Stops[2].Color.Value.A, 3);
        Assert.False(img.IsNone);
        Assert.True(ComputedStyle.Initial.BackgroundImage.IsNone);
    }

    // ================= 阴影 =================

    [Theory]
    [InlineData("none", "none")]
    [InlineData("1px 2px", "currentcolor 1px 2px 0px 0px")]
    [InlineData("1px 2px 3px 4px red", "rgb(255, 0, 0) 1px 2px 3px 4px")]
    [InlineData("red 1px 2px inset", "rgb(255, 0, 0) 1px 2px 0px 0px inset")]
    [InlineData("inset 0 0 1em blue", "rgb(0, 0, 255) 0px 0px 16px 0px inset")]
    [InlineData("1px 1px red, -2px -2px 0 3px blue", "rgb(255, 0, 0) 1px 1px 0px 0px, rgb(0, 0, 255) -2px -2px 0px 3px")]
    public void Box_shadows(string value, string expected) => Assert.Equal(expected, Text("box-shadow", value));

    [Theory]
    [InlineData("1px")]
    [InlineData("1px 2px -3px")]
    [InlineData("1px 2px 3px 4px 5px")]
    [InlineData("1px red 2px")]
    [InlineData("inset inset 1px 2px")]
    [InlineData("red blue 1px 2px")]
    [InlineData("1px 2px,")]
    [InlineData("none, 1px 2px")]
    [InlineData("10% 2px")]
    public void Invalid_box_shadows(string value) => Assert.False(IsValid("box-shadow", value), value);

    // ================= 字体 =================

    [Theory]
    [InlineData("Arial", "\"Arial\"")]
    [InlineData("\"Helvetica Neue\", sans-serif", "\"Helvetica Neue\", sans-serif")]
    [InlineData("Helvetica   Neue, SERIF", "\"Helvetica Neue\", serif")]
    [InlineData("system-ui, ui-monospace, emoji", "system-ui, ui-monospace, emoji")]
    public void Font_family(string value, string expected) => Assert.Equal(expected, Text("font-family", value));

    [Theory]
    [InlineData("inherit, serif")]
    [InlineData("serif,")]
    [InlineData("10px")]
    [InlineData("default")]
    public void Invalid_font_family(string value) => Assert.False(IsValid("font-family", value), value);

    [Fact]
    public void Generic_family_flag()
    {
        var f = Compute("font-family", "serif, \"serif\"").FontFamily.Families;
        Assert.Equal(GenericFontFamily.Serif, f[0].Generic);
        Assert.Equal(GenericFontFamily.None, f[1].Generic);
    }

    [Theory]
    [InlineData("medium", "16px")]
    [InlineData("xx-small", "9.6px")]
    [InlineData("x-small", "12px")]
    [InlineData("small", "14.2222px")]
    [InlineData("large", "19.2px")]
    [InlineData("x-large", "24px")]
    [InlineData("xx-large", "32px")]
    [InlineData("xxx-large", "48px")]
    [InlineData("larger", "19.2px")]
    [InlineData("smaller", "13.3333px")]
    [InlineData("150%", "24px")]
    [InlineData("2em", "32px")]
    [InlineData("calc(1em + 2px)", "18px")]
    [InlineData("12pt", "16px")]
    public void Font_size(string value, string expected) => Assert.Equal(expected, Text("font-size", value));

    [Theory]
    [InlineData("-1px")]
    [InlineData("big")]
    [InlineData("1")]
    public void Invalid_font_size(string value) => Assert.False(IsValid("font-size", value), value);

    [Theory]
    [InlineData("normal", "400")]
    [InlineData("bold", "700")]
    [InlineData("1", "1")]
    [InlineData("1000", "1000")]
    [InlineData("550", "550")]
    [InlineData("bolder", "700")]
    [InlineData("lighter", "100")]
    public void Font_weight(string value, string expected) => Assert.Equal(expected, Text("font-weight", value));

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("bold 700")]
    [InlineData("400px")]
    public void Invalid_font_weight(string value) => Assert.False(IsValid("font-weight", value), value);

    [Theory]
    [InlineData("normal", "100%")]
    [InlineData("condensed", "75%")]
    [InlineData("ultra-expanded", "200%")]
    [InlineData("80%", "80%")]
    public void Font_stretch(string value, string expected) => Assert.Equal(expected, Text("font-stretch", value));

    [Theory]
    [InlineData("normal", "normal")]
    [InlineData("1.5", "1.5")]
    [InlineData("0", "0")]
    [InlineData("20px", "20px")]
    [InlineData("150%", "24px")]
    [InlineData("2em", "32px")]
    [InlineData("calc(1em + 4px)", "20px")]
    public void Line_height(string value, string expected) => Assert.Equal(expected, Text("line-height", value));

    [Theory]
    [InlineData("-1")]
    [InlineData("-1px")]
    [InlineData("auto")]
    public void Invalid_line_height(string value) => Assert.False(IsValid("line-height", value), value);

    [Fact]
    public void Line_height_number_is_kept_as_number_and_inherited_as_number()
    {
        var engine = new AwAUI.Css.Cascade.StyleEngine();
        engine.AddStyleSheet(StyleSheet.Parse("div { font-size: 10px; line-height: 1.5 } span { font-size: 20px } p { font-size: 10px; line-height: 150% }"));
        var div = new El("div").Add(new El("span"));
        var p = new El("p").Add(new El("span"));
        var ds = engine.Resolve(div, null);
        var ss = engine.Resolve(div[0], ds);
        Assert.Equal(LineHeightKind.Number, ss.LineHeight.Kind);
        Assert.Equal(30f, ss.LineHeight.Resolve(ss.FontSize));
        var ps = engine.Resolve(p, null);
        var ps2 = engine.Resolve(p[0], ps);
        // 百分比在父元素上已算成长度，子元素继承长度
        Assert.Equal(LineHeightKind.Length, ps2.LineHeight.Kind);
        Assert.Equal(15f, ps2.LineHeight.Resolve(ps2.FontSize));
    }

    // ================= 关键字与其他 =================

    [Theory]
    [InlineData("display", "flex", "flex")]
    [InlineData("display", "NONE", "none")]
    [InlineData("position", "absolute", "absolute")]
    [InlineData("justify-content", "space-between", "space-between")]
    [InlineData("align-items", "baseline", "baseline")]
    [InlineData("border-top-style", "dashed", "dashed")]
    [InlineData("white-space", "pre-wrap", "pre-wrap")]
    [InlineData("cursor", "pointer", "pointer")]
    [InlineData("cursor", "url(a.cur), url(b.png) 2 3, not-allowed", "not-allowed")]
    [InlineData("pointer-events", "none", "none")]
    [InlineData("visibility", "hidden", "hidden")]
    [InlineData("text-overflow", "ellipsis", "ellipsis")]
    [InlineData("box-sizing", "border-box", "border-box")]
    public void Keywords(string property, string value, string expected) => Assert.Equal(expected, Text(property, value));

    [Theory]
    [InlineData("display", "table")]
    [InlineData("display", "flex block")]
    [InlineData("position", "1px")]
    [InlineData("cursor", "url(a.cur)")]
    [InlineData("cursor", "url(a.cur) pointer")]
    [InlineData("text-align", "\"left\"")]
    public void Invalid_keywords(string property, string value) => Assert.False(IsValid(property, value), value);

    [Theory]
    [InlineData("thin", "1px")]
    [InlineData("medium", "3px")]
    [InlineData("thick", "5px")]
    [InlineData("2px", "2px")]
    public void Border_width_keywords(string value, string expected)
        => Assert.Equal(expected, Text("border-top-width", value, extra: "border-top-style: solid;"));

    [Fact]
    public void Border_width_is_zero_when_style_is_none()
    {
        Assert.Equal("0px", Text("border-top-width", "10px"));
        Assert.Equal("0px", Text("border-top-width", "10px", extra: "border-top-style: hidden;"));
        Assert.Equal(0f, ComputedStyle.Initial.BorderTopWidth);
        // 有样式但没有显式宽度：medium
        Assert.Equal("3px", Text("border-top-style", "solid", extra: "") == "solid" ? Text("border-top-width", "initial", extra: "border-top-style: solid;") : "");
        Assert.Equal("3px", Text("border-left-width", "unset", extra: "border-left-style: dotted;"));
        Assert.Equal("3px", Compute("border", "solid").GetPropertyValueText("border-bottom-width"));
        Assert.Equal("0px", Compute("border", "1px none").GetPropertyValueText("border-bottom-width"));
        Assert.Equal("3px", Compute("border-right-style", "double").GetPropertyValueText("border-right-width"));
    }

    [Theory]
    [InlineData("10px", "10px")]
    [InlineData("10px 20%", "10px 20%")]
    [InlineData("5% 5%", "5%")]
    public void Corner_radius(string value, string expected) => Assert.Equal(expected, Text("border-top-left-radius", value));

    [Fact]
    public void Overflow_visible_becomes_auto_when_other_axis_scrolls()
    {
        var s = Compute("overflow-x", "visible", extra: "overflow-y: scroll;");
        Assert.Equal(CssOverflow.Auto, s.OverflowX);
        var c = Compute("overflow-x", "clip", extra: "overflow-y: hidden;");
        Assert.Equal(CssOverflow.Hidden, c.OverflowX);
        var both = Compute("overflow-x", "clip", extra: "overflow-y: visible;");
        Assert.Equal(CssOverflow.Clip, both.OverflowX);
    }

    // ================= 诊断 =================

    [Fact]
    public void Unknown_property_reports_AW2002_with_suggestion()
    {
        var d = Assert.Single(Diagnostics("colr: red"));
        Assert.Equal(CssDiagnosticCodes.UnsupportedProperty, d.Code);
        Assert.Equal(CssDiagnosticSeverity.Warning, d.Severity);
        Assert.Contains("color", d.Message);
        Assert.Equal(1, d.Range.Start.Column);
    }

    [Fact]
    public void Planned_property_reports_phase()
    {
        var d = Assert.Single(Diagnostics("transform: rotate(1deg)"));
        Assert.Equal(CssDiagnosticCodes.UnsupportedProperty, d.Code);
        Assert.Contains("第 2 期", d.Message);
        var f = Assert.Single(Diagnostics("float: left"));
        Assert.Contains("flex", f.Message);
    }

    [Fact]
    public void Invalid_value_reports_AW2003_with_value_range()
    {
        var d = Assert.Single(Diagnostics("width: 10 ; color: red"));
        Assert.Equal(CssDiagnosticCodes.InvalidValue, d.Code);
        Assert.Equal(8, d.Range.Start.Column);
    }

    [Fact]
    public void Css_wide_keywords_are_valid_for_every_property()
    {
        foreach (var p in CssProperties.All)
            foreach (var k in new[] { "initial", "inherit", "unset", "revert", "revert-layer", "INHERIT" })
                Assert.True(IsValid(p.Name, k), p.Name + ": " + k);
        Assert.False(IsValid("width", "inherit 1px"));
    }

    [Fact]
    public void Var_references_are_validated_syntactically()
    {
        Assert.True(IsValid("width", "var(--a)"));
        Assert.True(IsValid("width", "calc(var(--a) + 1px)"));
        Assert.True(IsValid("width", "var(--a, )"));
        Assert.True(IsValid("width", "env(safe-area-inset-top, 0px)"));
        Assert.False(IsValid("width", "var(a)"));
        Assert.False(IsValid("width", "var(--a 1px)"));
        Assert.False(IsValid("width", "var()"));
        // CSS Variables 1 §3：含语法正确的 var() 的声明在解析期一律视为有效（all 也不例外）
        Assert.True(IsValid("all", "var(--a)"));
    }

    [Fact]
    public void Rejected_declarations_are_kept_for_devtools()
    {
        var block = PropertyDeclarationBlock.Parse(StyleSheet.ParseDeclarations("colr: red; width: x; color: blue !important"));
        Assert.Equal(2, block.RejectedDeclarations.Count);
        Assert.Equal(DeclarationStatus.Unsupported, block.RejectedDeclarations[0].Status);
        Assert.Equal(DeclarationStatus.Invalid, block.RejectedDeclarations[1].Status);
        Assert.Empty(block.NormalDeclarations);
        Assert.Single(block.ImportantDeclarations);
    }
}
