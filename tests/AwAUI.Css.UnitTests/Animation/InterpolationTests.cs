using AwAUI.Css.Animation;
using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Animation.AnimationTestHelper;

namespace AwAUI.Css.UnitTests.Animation;

/// <summary>计算值的插值（CSS Values 4 §3、CSS Color 4 §12、Backgrounds 3、Filter Effects 1、Shapes 1）。</summary>
public class InterpolationTests
{
    [Theory]
    [InlineData("width", "0px", "100px", 0.25, "25px")]
    [InlineData("width", "10%", "30%", 0.5, "20%")]
    [InlineData("width", "0px", "50%", 0.5, "25%")]
    [InlineData("width", "100px", "50%", 0.5, "calc(25% + 50px)")]
    [InlineData("margin-left", "-10px", "10px", 0.25, "-5px")]
    [InlineData("padding-left", "0px", "10px", -0.5, "0px")] // 超调按非负钳制
    [InlineData("margin-left", "0px", "10px", -0.5, "-5px")] // 允许负值的属性不钳制
    [InlineData("border-top-left-radius", "0px", "20px", 0.5, "10px")]
    [InlineData("opacity", "0", "1", 0.3, "0.3")]
    [InlineData("opacity", "0", "1", 1.5, "1")]
    [InlineData("opacity", "0", "1", -0.5, "0")]
    [InlineData("z-index", "0", "5", 0.5, "3")] // 整数：.5 向正无穷取整
    [InlineData("z-index", "0", "5", 0.45, "2")]
    [InlineData("order", "1", "4", 0.5, "3")]
    [InlineData("font-weight", "100", "900", 0.5, "500")]
    [InlineData("font-weight", "100", "900", 2, "1000")]
    [InlineData("line-height", "1", "2", 0.5, "1.5")]
    [InlineData("line-height", "10px", "20px", 0.5, "15px")]
    [InlineData("flex-grow", "0", "2", 0.25, "0.5")]
    [InlineData("letter-spacing", "0px", "2px", 0.5, "1px")]
    [InlineData("object-position", "0% 0%", "100% 50%", 0.5, "50% 25%")]
    [InlineData("transform-origin", "0px 0px", "100% 50%", 0.5, "50% 25%")]
    [InlineData("aspect-ratio", "1 / 1", "4 / 1", 0.5, "2 / 1")] // 按比值的对数插值
    [InlineData("perspective", "100px", "300px", 0.5, "200px")]
    [InlineData("scale", "none", "2", 0.5, "1.5")]
    [InlineData("translate", "none", "100px 20px", 0.5, "50px 10px")]
    [InlineData("rotate", "none", "90deg", 0.5, "45deg")]
    [InlineData("rotate", "x 0deg", "x 90deg", 0.5, "x 45deg")]
    public void Numeric_values(string property, string from, string to, double p, string expected)
        => Assert.Equal(expected, Lerp(property, from, to, p));

    [Theory]
    [InlineData("width", "auto", "100px")]
    [InlineData("height", "auto", "50%")]
    [InlineData("z-index", "auto", "3")]
    [InlineData("line-height", "normal", "2")]
    [InlineData("line-height", "1.5", "20px")]
    [InlineData("perspective", "none", "100px")]
    [InlineData("box-shadow", "inset 0 0 2px red", "0 0 2px red")]
    [InlineData("filter", "blur(2px)", "grayscale(1)")]
    [InlineData("clip-path", "circle(10px)", "inset(10px)")]
    [InlineData("vertical-align", "middle", "10px")]
    public void Not_interpolable_pairs_are_discrete(string property, string from, string to)
    {
        Assert.False(IsSmooth(property, from, to));
        Assert.Equal(Text(property, Computed(property, from)), Lerp(property, from, to, 0.49));
        Assert.Equal(Text(property, Computed(property, to)), Lerp(property, from, to, 0.5));
    }

    [Fact]
    public void Keyword_properties_flip_at_half_way()
    {
        Assert.False(IsSmooth("position", "static", "absolute"));
        Assert.Equal("static", Lerp("position", "static", "absolute", 0.49));
        Assert.Equal("absolute", Lerp("position", "static", "absolute", 0.5));
    }

    [Theory]
    [InlineData(0.0, "hidden")]
    [InlineData(0.01, "visible")]
    [InlineData(0.99, "visible")]
    [InlineData(1.0, "visible")]
    public void Visibility_is_visible_whenever_one_end_is_visible(double p, string expected)
    {
        Assert.Equal(expected, Lerp("visibility", "hidden", "visible", p));
        // 反向：visible → hidden 在 p = 1 才变为 hidden
        Assert.Equal(p >= 1 ? "hidden" : "visible", Lerp("visibility", "visible", "hidden", p));
    }

    [Theory]
    [InlineData(0.0, "none")]
    [InlineData(0.01, "block")]
    [InlineData(0.99, "block")]
    [InlineData(1.0, "block")]
    public void Display_none_keeps_the_other_value_inside_the_interval(double p, string expected)
    {
        Assert.Equal(expected, Lerp("display", "none", "block", p));
        Assert.Equal(p >= 1 ? "none" : "block", Lerp("display", "block", "none", p));
        // 两端都不是 none：普通离散
        Assert.Equal(p < 0.5 ? "flex" : "grid", Lerp("display", "flex", "grid", p));
    }

    [Fact]
    public void Colors_interpolate_in_premultiplied_oklab()
    {
        // 与已对照 Chrome 验证的 color-mix(in oklab, …) 一致
        var mix = (CssColor)Computed("color", "color-mix(in oklab, red, blue)");
        var lerp = CssInterpolation.InterpolateColor((CssColor)Computed("color", "red"), (CssColor)Computed("color", "blue"), 0.5);
        Assert.Equal(mix.ToString(), lerp.ToString());
        Assert.Equal("rgb(140, 83, 162)", lerp.ToString());
        // 预乘：从透明过渡时颜色不偏向黑色
        Assert.Equal("rgba(255, 0, 0, 0.5)", Lerp("background-color", "transparent", "red", 0.5));
        Assert.Equal("rgba(0, 0, 255, 0.25)", Lerp("background-color", "rgba(0, 0, 255, 0)", "blue", 0.25));
        // 超调时 alpha 钳制
        Assert.Equal("rgb(0, 0, 255)", Lerp("color", "red", "blue", 1.0));
    }

    [Fact]
    public void Current_color_produces_a_deferred_mix()
    {
        var from = (CssColor)Computed("border-top-color", "currentcolor");
        var to = (CssColor)Computed("border-top-color", "red");
        var r = CssInterpolation.InterpolateColor(from, to, 0.25);
        Assert.True(r.DependsOnCurrentColor);
        var used = r.Resolve(new Color(0, 0, 1));
        var expected = CssInterpolation.InterpolateColor((CssColor)Computed("color", "blue"), to, 0.25).Value;
        Assert.Equal(expected, used);
    }

    [Theory]
    [InlineData("box-shadow", "0 0 0 0 black", "10px 20px 4px 2px black", 0.5, "rgb(0, 0, 0) 5px 10px 2px 1px")]
    [InlineData("box-shadow", "none", "10px 10px 10px red", 0.5, "rgba(255, 0, 0, 0.5) 5px 5px 5px 0px")]
    [InlineData("box-shadow", "inset 2px 2px red", "none", 0.5, "rgba(255, 0, 0, 0.5) 1px 1px 0px 0px inset")]
    [InlineData("box-shadow", "1px 1px red, 2px 2px blue", "3px 3px red", 0.5, "rgb(255, 0, 0) 2px 2px 0px 0px, rgba(0, 0, 255, 0.5) 1px 1px 0px 0px")]
    [InlineData("text-shadow", "0 0 0 black", "4px 4px 2px black", 0.5, "rgb(0, 0, 0) 2px 2px 1px")]
    public void Shadow_lists_pad_with_transparent_zero_shadows(string property, string from, string to, double p, string expected)
        => Assert.Equal(expected, Lerp(property, from, to, p));

    [Theory]
    [InlineData("blur(0px)", "blur(10px)", 0.5, "blur(5px)")]
    [InlineData("none", "blur(10px) opacity(0)", 0.5, "blur(5px) opacity(0.5)")]
    [InlineData("grayscale(0)", "grayscale(1) brightness(2)", 0.5, "grayscale(0.5) brightness(1.5)")]
    [InlineData("hue-rotate(0deg)", "hue-rotate(180deg)", 0.5, "hue-rotate(90deg)")]
    [InlineData("opacity(1)", "opacity(0)", 2, "opacity(0)")] // 钳制到 [0, 1]
    [InlineData("drop-shadow(0 0 0 transparent)", "drop-shadow(4px 4px 2px red)", 0.5, "drop-shadow(rgba(255, 0, 0, 0.5) 2px 2px 1px)")]
    public void Filters_interpolate_function_by_function(string from, string to, double p, string expected)
        => Assert.Equal(expected, Lerp("filter", from, to, p));

    [Theory]
    [InlineData("inset(0px)", "inset(20px 10px round 10px)", 0.5, "inset(10px 5px round 5px)")]
    [InlineData("circle(10px at 0% 0%)", "circle(30px at 100% 50%)", 0.5, "circle(20px at 50% 25%)")]
    [InlineData("polygon(0px 0px, 100px 0px, 0px 100px)", "polygon(50px 50px, 50% 0px, 0px 100%)", 0.5, "polygon(25px 25px, calc(25% + 50px) 0px, 0px calc(50% + 50px))")]
    public void Clip_path_shapes_interpolate(string from, string to, double p, string expected)
        => Assert.Equal(Text("clip-path", Computed("clip-path", expected)), Lerp("clip-path", from, to, p));

    [Fact]
    public void Repeated_reversal_of_nonlinear_calc_does_not_deepen_the_expression()
    {
        Assert.True(CssProperties.TryGet("width", out var w));
        var a = Computed("width", "min(50%, 100px)");
        var b = Computed("width", "max(10%, 20px)");
        object cur = a;
        string? first = null;
        for (int i = 0; i < 200; i++)
        {
            CssInterpolation.Interpolate(w.Id, cur, i % 2 == 0 ? b : a, 0.3, out cur);
            var text = Text("width", cur);
            first ??= text;
            Assert.True(text.Length < 200, text);
        }
        // 线性组合只含两个非线性项
        Assert.Contains("min(50%, 100px)", first);
        Assert.Contains("max(10%, 20px)", first);
    }

    [Fact]
    public void Font_variation_settings_interpolate_matching_axes()
    {
        Assert.Equal("\"wght\" 450", Lerp("font-variation-settings", "\"wght\" 400", "\"wght\" 500", 0.5));
        Assert.False(IsSmooth("font-variation-settings", "\"wght\" 400", "\"wdth\" 500"));
    }

    [Fact]
    public void Additive_composition()
    {
        Assert.True(CssProperties.TryGet("margin-left", out var ml));
        Assert.True(AnimationDispatch.TryAdd(ml.Id, Computed("margin-left", "10px"), Computed("margin-left", "5%"), false, out var sum));
        Assert.Equal("calc(5% + 10px)", Text("margin-left", sum));
        Assert.True(CssProperties.TryGet("transform", out var tr));
        Assert.True(AnimationDispatch.TryAdd(tr.Id, Computed("transform", "translateX(10px)"), Computed("transform", "rotate(45deg)"), false, out var t));
        Assert.Equal("translateX(10px) rotate(45deg)", Text("transform", t));
        Assert.True(AnimationDispatch.TryAdd(tr.Id, Computed("transform", "translateX(10px)"), Computed("transform", "translateX(5px)"), true, out var acc));
        Assert.Equal("translateX(15px)", Text("transform", acc));
        Assert.True(CssProperties.TryGet("scale", out var sc));
        Assert.True(AnimationDispatch.TryAdd(sc.Id, Computed("scale", "2"), Computed("scale", "3"), false, out var mul));
        Assert.Equal("6", Text("scale", mul));
    }

    [Theory]
    [InlineData("ease", 0.5, 0.8024033877399112)]
    [InlineData("linear", 0.3, 0.3)]
    [InlineData("steps(4)", 0.3, 0.25)]
    [InlineData("steps(4, jump-start)", 0.3, 0.5)]
    [InlineData("steps(5, jump-none)", 0.3, 0.25)]
    [InlineData("steps(3, jump-both)", 0.5, 0.5)]
    [InlineData("step-start", 0.0, 1.0)]
    [InlineData("step-end", 0.99, 0.0)]
    [InlineData("linear(0, 0.25 75%, 1)", 0.5, 0.16666666666666666)]
    [InlineData("linear(0, 0.25 75%, 1)", 0.875, 0.625)]
    [InlineData("linear(0, 0.5 50%, 0.5 50%, 1)", 0.5, 0.5)]
    [InlineData("cubic-bezier(0, 1.5, 1, 1.5)", 0.5, 1.25)]
    public void Easing_functions(string easing, double t, double expected)
    {
        var e = ((CssList<CssEasingFunction>)Computed("transition-timing-function", easing))[0];
        Assert.Equal(expected, e.Evaluate(t), 3);
    }

    [Fact]
    public void Steps_before_flag()
    {
        var e = ((CssList<CssEasingFunction>)Computed("transition-timing-function", "steps(2, jump-start)"))[0];
        Assert.Equal(0.5, e.Evaluate(0));
        Assert.Equal(0.0, e.Evaluate(0, before: true));
    }
}
