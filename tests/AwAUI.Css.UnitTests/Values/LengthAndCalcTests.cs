using AwAUI.Css.Media;
using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Values.ValueTestHelper;

namespace AwAUI.Css.UnitTests.Values;

public class LengthAndCalcTests
{
    static CssEnvironment Env(double w = 1000, double h = 500)
        => new(new MediaEnvironment { Width = w, Height = h });

    [Theory]
    [InlineData("10px", "10px")]
    [InlineData("0", "0px")]
    [InlineData("1in", "96px")]
    [InlineData("2.54cm", "96px")]
    [InlineData("25.4mm", "96px")]
    [InlineData("101.6q", "96px")]
    [InlineData("72pt", "96px")]
    [InlineData("1pc", "16px")]
    [InlineData("2em", "40px")]
    [InlineData("2rem", "40px")]          // 根元素上 rem 即自身字体大小
    [InlineData("1ex", "10px")]
    [InlineData("1ch", "10px")]
    [InlineData("10vw", "100px")]
    [InlineData("10vh", "50px")]
    [InlineData("10vmin", "50px")]
    [InlineData("10vmax", "100px")]
    [InlineData("10dvh", "50px")]
    [InlineData("10dvw", "100px")]
    [InlineData("10svh", "40px")]
    [InlineData("10lvh", "60px")]
    [InlineData("10vi", "100px")]
    [InlineData("10vb", "50px")]
    [InlineData("10PX", "10px")]
    [InlineData("25%", "25%")]
    [InlineData("-5px", "-5px")]
    public void Length_units_compute_to_px(string value, string expected)
    {
        var env = Env();
        env.SmallViewportHeightOverride = 400;
        env.LargeViewportHeightOverride = 600;
        Assert.Equal(expected, Text("margin-left", value, env, "font-size: 20px;"));
    }

    [Fact]
    public void Viewport_units_mark_style_as_viewport_dependent()
    {
        Assert.True(Compute("width", "10vw").Flags.HasFlag(ComputedStyleFlags.DependsOnViewport));
        Assert.False(Compute("width", "10px").Flags.HasFlag(ComputedStyleFlags.DependsOnViewport));
    }

    [Theory]
    [InlineData("calc(10px + 5px)", "15px")]
    [InlineData("calc(10px - 2em)", "-30px")]
    [InlineData("calc(2 * 3px)", "6px")]
    [InlineData("calc(3px * 2 / 4)", "1.5px")]
    [InlineData("calc((1px + 2px) * 3)", "9px")]
    [InlineData("calc(50% + 10px)", "calc(50% + 10px)")]
    [InlineData("calc(50% - 10px)", "calc(50% - 10px)")]
    [InlineData("calc(100%)", "100%")]
    [InlineData("min(10px, 20px)", "10px")]
    [InlineData("max(10px, 2em)", "40px")]
    [InlineData("clamp(10px, 50px, 30px)", "30px")]
    [InlineData("clamp(10px, 5px, 30px)", "10px")]
    [InlineData("min(50%, 100px)", "min(50%, 100px)")]
    [InlineData("calc(min(1px, 2px) + max(3px, 4px))", "5px")]
    [InlineData("CALC(1px + 1px)", "2px")]
    [InlineData("calc(1px * pi)", "3.1416px")]
    public void Calc_expressions(string value, string expected)
        => Assert.Equal(expected, Text("margin-left", value, null, "font-size: 20px;"));

    [Fact]
    public void Non_linear_calc_is_kept_until_layout()
    {
        var s = Compute("width", "min(50%, 100px)");
        var lp = s.Width.Value;
        Assert.NotNull(lp.Calc);
        Assert.Equal(50f, lp.Resolve(100));
        Assert.Equal(100f, lp.Resolve(1000));
        var c = Compute("width", "clamp(10px, 10%, 50px)").Width.Value;
        Assert.Equal(10f, c.Resolve(50));
        Assert.Equal(30f, c.Resolve(300));
        Assert.Equal(50f, c.Resolve(3000));
        var linear = Compute("width", "calc(50% + 2em)").Width.Value;
        Assert.Null(linear.Calc);
        Assert.Equal(82f, linear.Resolve(100));
    }

    [Theory]
    [InlineData("calc(1px+2px)")]          // + 两侧需要空白
    [InlineData("calc(1px + )")]
    [InlineData("calc(1px * 2px)")]       // 两个维度相乘
    [InlineData("calc(1px / 2px)")]       // 除以维度
    [InlineData("calc(1px + 2)")]         // 类型不匹配
    [InlineData("calc(1px + 1s)")]
    [InlineData("calc()")]
    [InlineData("min()")]
    [InlineData("clamp(1px, 2px)")]
    [InlineData("calc(foo)")]
    [InlineData("10")]                    // 非零的无单位数
    [InlineData("10foo")]
    [InlineData("1deg")]
    [InlineData("auto auto")]
    [InlineData("calc(5)")]
    public void Invalid_lengths_are_rejected(string value) => Assert.False(IsValid("margin-left", value), value);

    [Theory]
    [InlineData("width", "-1px")]
    [InlineData("padding-top", "-1px")]
    [InlineData("border-top-width", "-1px")]
    [InlineData("border-top-width", "10%")]
    [InlineData("flex-grow", "-1")]
    [InlineData("width", "none")]
    [InlineData("max-width", "auto")]
    [InlineData("margin-top", "none")]
    [InlineData("row-gap", "auto")]
    public void Range_and_keyword_restrictions(string property, string value) => Assert.False(IsValid(property, value), property + ": " + value);

    [Fact]
    public void Negative_calc_result_is_clamped_for_non_negative_properties()
    {
        Assert.Equal("0px", Text("padding-left", "calc(10px - 20px)"));
        Assert.Equal("0px", Text("width", "calc(10px - 20px)"));
        Assert.Equal("-10px", Text("margin-left", "calc(10px - 20px)"));
        Assert.Equal("calc(50% - 20px)", Text("width", "calc(50% - 20px)"));
    }

    [Fact]
    public void Deeply_nested_calc_is_rejected_without_stack_overflow()
    {
        var deep = new string('(', 5000) + "1px" + new string(')', 5000);
        Assert.False(IsValid("width", "calc" + deep));
        var nested = string.Concat(Enumerable.Repeat("calc(", 200)) + "1px" + new string(')', 200);
        Assert.False(IsValid("width", nested));
        var ok = string.Concat(Enumerable.Repeat("calc(", 10)) + "1px" + new string(')', 10);
        Assert.True(IsValid("width", ok));
    }

    [Fact]
    public void Huge_calc_sum_is_bounded()
    {
        var sum = "calc(" + string.Join(" + ", Enumerable.Repeat("1px", 5000)) + ")";
        Assert.False(IsValid("width", sum));
        var small = "calc(" + string.Join(" + ", Enumerable.Repeat("1px", 100)) + ")";
        Assert.Equal("100px", Text("width", small));
    }

    [Fact]
    public void Infinity_and_nan_are_clamped()
    {
        Assert.Equal("0px", Text("margin-left", "calc(1px * NaN)"));
        var inf = Compute("width", "calc(1px * infinity)").Width.Value.Length;
        Assert.True(inf > 1e29f && !float.IsInfinity(inf));
    }

    [Fact]
    public void Size_keywords()
    {
        Assert.Equal("auto", Text("width", "auto"));
        Assert.Equal("min-content", Text("width", "min-content"));
        Assert.Equal("max-content", Text("height", "MAX-CONTENT"));
        Assert.Equal("fit-content", Text("min-width", "fit-content"));
        Assert.Equal("none", Text("max-height", "none"));
        Assert.Equal("content", Text("flex-basis", "content"));
        Assert.Equal("normal", Text("row-gap", "normal"));
        Assert.Equal("auto", Text("margin-top", "auto"));
    }

    [Fact]
    public void CssLength_api()
    {
        Assert.True(CssLength.TryParseUnit("DVMAX", out var u));
        Assert.Equal(CssLengthUnit.Dvmax, u);
        Assert.False(CssLength.TryParseUnit("pxx", out _));
        var ctx = new LengthContext(10, 20, Env(200, 100));
        Assert.Equal(15, new CssLength(1.5, CssLengthUnit.Em).ToPx(ctx));
        Assert.Equal(40, new CssLength(2, CssLengthUnit.Rem).ToPx(ctx));
        Assert.Equal(1, new CssLength(1, CssLengthUnit.Vmin).ToPx(ctx));
        Assert.Throws<InvalidOperationException>(() => new CssLength(1, CssLengthUnit.Percent).ToPx(ctx));
        Assert.Equal("1.5em", new CssLength(1.5, CssLengthUnit.Em).ToString());
        Assert.True(new CssLength(1, CssLengthUnit.Pt).IsAbsolute);
        Assert.True(new CssLength(1, CssLengthUnit.Svh).IsViewportRelative);
    }

    [Theory]
    [InlineData(90, "deg", 90)]
    [InlineData(100, "grad", 90)]
    [InlineData(0.25, "turn", 90)]
    [InlineData(Math.PI / 2, "rad", 90)]
    public void Angle_units(double value, string unit, double degrees)
    {
        Assert.True(CssUnits.TryAngleToDegrees(value, unit, out var d));
        Assert.Equal(degrees, d, 6);
    }

    [Fact]
    public void Time_units()
    {
        Assert.True(CssUnits.TryTimeToMilliseconds(1.5, "s", out var ms));
        Assert.Equal(1500, ms);
        Assert.True(CssUnits.TryTimeToMilliseconds(20, "MS", out ms));
        Assert.Equal(20, ms);
        Assert.False(CssUnits.TryTimeToMilliseconds(1, "px", out _));
        var values = Syntax.CssComponentValue.ParseList("calc(1s + 500ms)");
        Assert.True(ValueParsers.TryTime(values[0], out ms));
        Assert.Equal(1500, ms);
        Assert.False(ValueParsers.TryTime(Syntax.CssComponentValue.ParseList("1px")[0], out _));
    }

    [Fact]
    public void Numbers_and_integers()
    {
        Assert.Equal("2.5", Text("flex-grow", "2.5"));
        Assert.Equal("3", Text("flex-grow", "calc(1 + 2)"));
        Assert.Equal("5", Text("z-index", "5"));
        Assert.Equal("-3", Text("z-index", "-3"));
        Assert.Equal("4", Text("z-index", "calc(3.5)"));
        Assert.Equal("auto", Text("z-index", "auto"));
        Assert.False(IsValid("z-index", "1.5"));
        Assert.False(IsValid("z-index", "1px"));
        Assert.False(IsValid("flex-grow", "1px"));
        Assert.Equal("0.5", Text("opacity", "50%"));
        Assert.Equal("1", Text("opacity", "3"));
        Assert.Equal("0", Text("opacity", "-1"));
        Assert.Equal("0.25", Text("opacity", "calc(0.5 / 2)"));
    }
}
