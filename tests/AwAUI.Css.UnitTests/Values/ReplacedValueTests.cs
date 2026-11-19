using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Values.ValueTestHelper;

namespace AwAUI.Css.UnitTests.Values;

/// <summary>替换元素相关的属性：<c>object-fit</c>、<c>object-position</c>、<c>image-rendering</c>、<c>aspect-ratio</c>。</summary>
public class ReplacedValueTests
{
    // ================= object-fit =================

    [Theory]
    [InlineData("fill", CssObjectFit.Fill)]
    [InlineData("contain", CssObjectFit.Contain)]
    [InlineData("COVER", CssObjectFit.Cover)]
    [InlineData("none", CssObjectFit.None)]
    [InlineData("scale-down", CssObjectFit.ScaleDown)]
    public void Object_fit_keywords(string value, CssObjectFit expected)
    {
        Assert.Equal(expected, Compute("object-fit", value).ObjectFit);
        Assert.Equal(value.ToLowerInvariant(), Text("object-fit", value));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("contain cover")]
    [InlineData("scaledown")]
    [InlineData("0")]
    public void Invalid_object_fit(string value) => Assert.False(IsValid("object-fit", value), value);

    // ================= object-position =================

    [Theory]
    [InlineData("50% 50%", "50% 50%")]
    [InlineData("center", "50% 50%")]
    [InlineData("left", "0% 50%")]
    [InlineData("top", "50% 0%")]
    [InlineData("right bottom", "100% 100%")]
    [InlineData("bottom right", "100% 100%")]
    [InlineData("top center", "50% 0%")]
    [InlineData("center left", "0% 50%")]
    [InlineData("10px", "10px 50%")]
    [InlineData("10px top", "10px 0%")]
    [InlineData("left 25%", "0% 25%")]
    [InlineData("2em 1rem", "32px 16px")]
    [InlineData("calc(10% + 5px) 0", "calc(10% + 5px) 0px")]
    [InlineData("right 10px bottom 20%", "calc(100% - 10px) 80%")]
    [InlineData("bottom 5px left 3px", "3px calc(100% - 5px)")]
    [InlineData("-10px -20%", "-10px -20%")]
    public void Object_position_values(string value, string expected) => Assert.Equal(expected, Text("object-position", value));

    [Theory]
    [InlineData("left right")]
    [InlineData("top bottom")]
    [InlineData("10px left")]
    [InlineData("top 10px")]                 // 第二个值是长度时，第一个必须是水平关键字
    [InlineData("left 10px top")]            // 3 值形式只属于 background-position
    [InlineData("center 10px center 10px")]
    [InlineData("1px 2px 3px 4px")]
    [InlineData("left top left")]
    [InlineData("middle")]
    [InlineData("10")]
    [InlineData("")]
    public void Invalid_object_position(string value) => Assert.False(IsValid("object-position", value), value);

    [Fact]
    public void Object_position_object_model()
    {
        var p = Compute("object-position", "right 10px top 25%").ObjectPosition;
        // 百分比相对「容器 − 对象」的剩余空间（可为负）
        Assert.Equal(90f, p.ResolveX(100));
        Assert.Equal(25f, p.ResolveY(100));
        Assert.Equal(-60f, p.ResolveX(-50));
        Assert.Equal(PositionValue.Center, ComputedStyle.Initial.ObjectPosition);
        Assert.Equal(50f, PositionValue.Center.ResolveX(100));
    }

    [Fact]
    public void Object_position_with_viewport_units_marks_the_style()
    {
        var env = new CssEnvironment(new AwAUI.Css.Media.MediaEnvironment { Width = 200, Height = 100 });
        var s = Compute("object-position", "10vw 50vh", env);
        Assert.Equal("20px 50px", s.GetPropertyValueText("object-position"));
        Assert.True((s.Flags & ComputedStyleFlags.DependsOnViewport) != 0);
    }

    // ================= image-rendering =================

    [Theory]
    [InlineData("auto", CssImageRendering.Auto)]
    [InlineData("smooth", CssImageRendering.Smooth)]
    [InlineData("high-quality", CssImageRendering.HighQuality)]
    [InlineData("pixelated", CssImageRendering.Pixelated)]
    [InlineData("crisp-edges", CssImageRendering.CrispEdges)]
    public void Image_rendering_keywords(string value, CssImageRendering expected)
    {
        Assert.Equal(expected, Compute("image-rendering", value).ImageRendering);
        Assert.Equal(value, Text("image-rendering", value));
    }

    [Theory]
    [InlineData("nearest")]
    [InlineData("pixelated smooth")]
    [InlineData("-webkit-optimize-contrast")]
    public void Invalid_image_rendering(string value) => Assert.False(IsValid("image-rendering", value), value);

    // ================= aspect-ratio =================

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("16 / 9", "16 / 9")]
    [InlineData("16/9", "16 / 9")]
    [InlineData("2", "2 / 1")]
    [InlineData("0.5", "0.5 / 1")]
    [InlineData("auto 16 / 9", "auto 16 / 9")]
    [InlineData("16 / 9 auto", "auto 16 / 9")]
    [InlineData("auto 1", "auto 1 / 1")]
    [InlineData("0 / 1", "0 / 1")]
    [InlineData("calc(1 + 1) / calc(4 / 2)", "2 / 2")]
    [InlineData("calc(1 - 3) / 1", "0 / 1")]
    [InlineData("1.5e2 / 1E2", "150 / 100")]
    public void Aspect_ratio_values(string value, string expected) => Assert.Equal(expected, Text("aspect-ratio", value));

    [Theory]
    [InlineData("auto auto")]
    [InlineData("16 / 9 / 3")]
    [InlineData("16 9")]
    [InlineData("-1 / 2")]
    [InlineData("1 / -2")]
    [InlineData("16 /")]
    [InlineData("/ 9")]
    [InlineData("16px / 9px")]
    [InlineData("50%")]
    [InlineData("auto 16 / 9 auto")]
    [InlineData("1 / 2 3 / 4")]
    [InlineData("none")]
    public void Invalid_aspect_ratio(string value) => Assert.False(IsValid("aspect-ratio", value), value);

    [Fact]
    public void Aspect_ratio_object_model()
    {
        var auto = ComputedStyle.Initial.AspectRatio;
        Assert.Equal(AspectRatioValue.Auto, auto);
        Assert.True(auto.IsAuto);
        Assert.False(auto.HasRatio);
        Assert.Null(auto.Ratio);
        Assert.Equal(1.5f, auto.Resolve(1.5f));
        Assert.Null(auto.Resolve(null));

        var ratio = Compute("aspect-ratio", "16 / 8").AspectRatio;
        Assert.False(ratio.IsAuto);
        Assert.Equal(2f, ratio.Ratio);
        Assert.Equal(2f, ratio.Resolve(0.5f));   // <ratio>：忽略固有宽高比
        Assert.Equal(2f, ratio.Resolve(null));

        var both = Compute("aspect-ratio", "auto 16 / 8").AspectRatio;
        Assert.True(both.IsAuto);
        Assert.True(both.HasRatio);
        Assert.Equal(0.5f, both.Resolve(0.5f)); // auto <ratio>：有固有宽高比时用固有的
        Assert.Equal(2f, both.Resolve(null));

        var degenerate = Compute("aspect-ratio", "0 / 1").AspectRatio;
        Assert.True(degenerate.HasRatio);
        Assert.Null(degenerate.Ratio);           // 退化比值按 auto 处理
        Assert.Null(degenerate.Resolve(null));
        Assert.Equal(1.5f, degenerate.Resolve(1.5f)); // 替换元素回到固有宽高比
        Assert.Null(Compute("aspect-ratio", "1 / 0").AspectRatio.Ratio);
    }

    [Fact]
    public void Aspect_ratio_with_huge_numbers_stays_finite()
    {
        var a = Compute("aspect-ratio", "1e300 / 1e-300").AspectRatio;
        Assert.True(Finite(a.Width) && Finite(a.Height));
        var r = a.Ratio;
        Assert.True(r is null || (r > 0 && Finite(r.Value)));
        var b = Compute("aspect-ratio", "calc(1 / 0) / 1").AspectRatio;
        Assert.True(Finite(b.Width));

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }

    [Fact]
    public void New_properties_are_phase_1_with_metadata()
    {
        foreach (var name in new[] { "object-fit", "object-position", "image-rendering", "aspect-ratio" })
        {
            Assert.True(CssProperties.TryGet(name, out var p), name);
            Assert.Equal(1, p.Phase);
            Assert.False(CssProperties.TryGetPlannedPhase(name, out _), name);
        }
        Assert.Equal(StyleDifference.Layout, CssProperties.Get(CssPropertyId.AspectRatio).Affects);
        Assert.Equal(StyleDifference.Paint, CssProperties.Get(CssPropertyId.ObjectFit).Affects);
        Assert.Equal(StyleDifference.Paint, CssProperties.Get(CssPropertyId.ObjectPosition).Affects);
        Assert.Equal(StyleDifference.Paint, CssProperties.Get(CssPropertyId.ImageRendering).Affects);
        Assert.True(CssProperties.Get(CssPropertyId.ImageRendering).Inherited);
        Assert.False(CssProperties.Get(CssPropertyId.ObjectFit).Inherited);
        Assert.False(CssProperties.Get(CssPropertyId.ObjectPosition).Inherited);
        Assert.False(CssProperties.Get(CssPropertyId.AspectRatio).Inherited);
        Assert.Equal("fill", ComputedStyle.Initial.GetPropertyValueText("object-fit"));
        Assert.Equal("50% 50%", ComputedStyle.Initial.GetPropertyValueText("object-position"));
        Assert.Equal("auto", ComputedStyle.Initial.GetPropertyValueText("image-rendering"));
        Assert.Equal("auto", ComputedStyle.Initial.GetPropertyValueText("aspect-ratio"));
        // 不再报「计划中的属性」诊断
        Assert.Empty(Diagnostics("object-fit: cover; object-position: left; aspect-ratio: 1; image-rendering: pixelated"));
    }
}
