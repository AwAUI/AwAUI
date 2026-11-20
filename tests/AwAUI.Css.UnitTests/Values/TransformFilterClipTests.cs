using System.Numerics;
using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Values.ValueTestHelper;

namespace AwAUI.Css.UnitTests.Values;

/// <summary>变换、滤镜与裁剪属性（第九批）：解析、计算值与矩阵组合。</summary>
public class TransformFilterClipTests
{
    [Theory]
    [InlineData("none", "none")]
    [InlineData("rotate(45deg)", "rotate(45deg)")]
    [InlineData("rotate(0.5turn) scale(2)", "rotate(180deg) scale(2, 2)")]
    [InlineData("translate(10px, 50%)", "translate(10px, 50%)")]
    [InlineData("translate(1em)", "translate(16px, 0px)")]
    [InlineData("translateX(calc(1rem + 10%))", "translateX(calc(10% + 16px))")]
    [InlineData("translate3d(1px, 2px, 3px)", "translate3d(1px, 2px, 3px)")]
    [InlineData("scale(50%, 2)", "scale(0.5, 2)")]
    [InlineData("scaleX(-1)", "scaleX(-1)")]
    [InlineData("skew(10deg, 20deg)", "skew(10deg, 20deg)")]
    [InlineData("matrix(1, 0, 0, 1, 10, 20)", "matrix(1, 0, 0, 1, 10, 20)")]
    [InlineData("rotate3d(1, 0, 0, 90deg)", "rotate3d(1, 0, 0, 90deg)")]
    [InlineData("perspective(100px) rotateY(30deg)", "perspective(100px) rotateY(30deg)")]
    [InlineData("perspective(none)", "perspective(none)")]
    [InlineData("rotate(0)", "rotate(0deg)")]
    public void Transform_computed_values(string value, string expected) => Assert.Equal(expected, Text("transform", value));

    [Theory]
    [InlineData("rotate(45)")]
    [InlineData("translate(1px 2px)")]
    [InlineData("matrix(1, 0, 0, 1, 10)")]
    [InlineData("scale(1px)")]
    [InlineData("foo(1)")]
    [InlineData("rotate(45deg),")]
    [InlineData("translateZ(10%)")]
    public void Invalid_transforms(string value) => Assert.False(IsValid("transform", value), value);

    [Theory]
    [InlineData("translate", "10px", "10px")]
    [InlineData("translate", "10px 20%", "10px 20%")]
    [InlineData("translate", "1em 2em 3px", "16px 32px 3px")]
    [InlineData("translate", "none", "none")]
    [InlineData("rotate", "45deg", "45deg")]
    [InlineData("rotate", "x 90deg", "x 90deg")]
    [InlineData("rotate", "90deg z", "90deg")]
    [InlineData("rotate", "1 1 0 0.25turn", "1 1 0 90deg")]
    [InlineData("scale", "2", "2")]
    [InlineData("scale", "50% 2", "0.5 2")]
    [InlineData("scale", "1 2 3", "1 2 3")]
    [InlineData("transform-origin", "left top", "0% 0%")]
    [InlineData("transform-origin", "top left", "0% 0%")]
    [InlineData("transform-origin", "10px 2em 5px", "10px 32px 5px")]
    [InlineData("transform-origin", "bottom", "50% 100%")]
    [InlineData("perspective", "none", "none")]
    [InlineData("perspective", "2em", "32px")]
    [InlineData("perspective-origin", "right", "100% 50%")]
    [InlineData("backface-visibility", "hidden", "hidden")]
    [InlineData("transform-style", "preserve-3d", "preserve-3d")]
    [InlineData("transform-box", "fill-box", "fill-box")]
    public void Individual_transform_properties(string property, string value, string expected) => Assert.Equal(expected, Text(property, value));

    [Fact]
    public void Transform_matrix_composes_origin_individual_properties_and_list()
    {
        // translate 10px → rotate 90deg → scale 2 → transform translateX(5px)，围绕中心 (50,25)
        var s = Compute("transform", "translateX(5px)", extra: "translate: 10px; rotate: 90deg; scale: 2;");
        Assert.True(CssTransforms.HasTransform(s));
        var m = CssTransforms.GetTransform(s, new Rect(0, 0, 100, 50));
        // 列向量意义下：T(o)·T(10)·R(90)·S(2)·T(5)·T(-o)，对点 (50,25)（原点）：→ (5,0)→(10,0)→R→(0,10)→+10→(10,10)→+o→(60,35)
        var p = Vector2.Transform(new Vector2(50, 25), CssTransforms.To2D(m));
        Assert.Equal(60, p.X, 3);
        Assert.Equal(35, p.Y, 3);
        Assert.True(CssTransforms.Is2D(m));
        Assert.False(CssTransforms.HasTransform(ComputedStyle.Initial));
    }

    [Fact]
    public void Percent_translation_resolves_against_reference_box()
    {
        var s = Compute("transform", "translate(50%, -50%)");
        var m = CssTransforms.GetTransform(s, new Rect(0, 0, 200, 100));
        var p = Vector2.Transform(Vector2.Zero, CssTransforms.To2D(m));
        Assert.Equal(100, p.X, 3);
        Assert.Equal(-50, p.Y, 3);
    }

    [Fact]
    public void Rotate_matches_css_direction_and_3d_is_detected()
    {
        var m = CssTransforms.GetTransform(Compute("transform", "rotate(90deg)", extra: "transform-origin: 0 0;"), new Rect(0, 0, 10, 10));
        var p = Vector2.Transform(new Vector2(1, 0), CssTransforms.To2D(m));
        // CSS 的 rotate 在 y 轴向下的坐标中顺时针：(1,0) → (0,1)
        Assert.Equal(0, p.X, 4);
        Assert.Equal(1, p.Y, 4);
        var m3 = CssTransforms.GetTransform(Compute("transform", "rotateY(45deg)"), new Rect(0, 0, 10, 10));
        Assert.False(CssTransforms.Is2D(m3));
        var persp = CssTransforms.GetChildPerspective(Compute("perspective", "500px"), new Rect(0, 0, 100, 100));
        Assert.NotNull(persp);
        Assert.Null(CssTransforms.GetChildPerspective(ComputedStyle.Initial, new Rect(0, 0, 100, 100)));
    }

    [Theory]
    [InlineData("none", "none")]
    [InlineData("blur(4px)", "blur(4px)")]
    [InlineData("blur(0.5em)", "blur(8px)")]
    [InlineData("blur()", "blur(0px)")]
    [InlineData("brightness(150%) contrast(2)", "brightness(1.5) contrast(2)")]
    [InlineData("grayscale(200%)", "grayscale(1)")]
    [InlineData("hue-rotate(0.5turn)", "hue-rotate(180deg)")]
    [InlineData("invert() opacity(50%) saturate(0) sepia(1)", "invert(1) opacity(0.5) saturate(0) sepia(1)")]
    [InlineData("drop-shadow(1px 2px 3px red)", "drop-shadow(rgb(255, 0, 0) 1px 2px 3px)")]
    [InlineData("drop-shadow(red 1px 2px)", "drop-shadow(rgb(255, 0, 0) 1px 2px 0px)")]
    [InlineData("url(#f) blur(1px)", "url(\"#f\") blur(1px)")]
    public void Filter_computed_values(string value, string expected) => Assert.Equal(expected, Text("filter", value));

    [Theory]
    [InlineData("blur(-1px)")]
    [InlineData("blur(10%)")]
    [InlineData("brightness(-1)")]
    [InlineData("drop-shadow(1px)")]
    [InlineData("drop-shadow(1px red 2px)")]
    [InlineData("hue-rotate(10)")]
    [InlineData("blur(1px),")]
    public void Invalid_filters(string value) => Assert.False(IsValid("filter", value), value);

    [Fact]
    public void Backdrop_filter_and_webkit_alias()
    {
        Assert.Equal("blur(8px)", Text("backdrop-filter", "blur(8px)"));
        Assert.Equal("blur(4px)", Text("-webkit-backdrop-filter", "blur(4px)") ?? Compute("-webkit-backdrop-filter", "blur(4px)").GetPropertyValueText("backdrop-filter"));
        var s = Compute("-webkit-backdrop-filter", "blur(4px)");
        Assert.Equal("blur(4px)", s.GetPropertyValueText("backdrop-filter"));
        Assert.Equal(CssFilterKind.Blur, s.BackdropFilter.Functions[0].Kind);
        Assert.Equal(4f, s.BackdropFilter.Functions[0].Amount);
    }

    [Fact]
    public void Tailwind_filter_with_empty_fallbacks()
    {
        // Tailwind v4：filter: var(--tw-blur,) var(--tw-brightness,) …；全部为空时计算值时无效 → none
        const string tw = "filter: var(--tw-blur,) var(--tw-brightness,) var(--tw-contrast,) var(--tw-grayscale,);";
        Assert.Equal("none", Compute("color", "red", extra: tw).GetPropertyValueText("filter"));
        Assert.Equal("blur(8px)", Compute("color", "red", extra: "--tw-blur: blur(8px); " + tw).GetPropertyValueText("filter"));
    }

    [Theory]
    [InlineData("none", "none")]
    [InlineData("inset(50%)", "inset(50% 50% 50% 50%)")]
    [InlineData("inset(1px 2px round 4px)", "inset(1px 2px 1px 2px round 4px 4px 4px 4px / 4px 4px 4px 4px)")]
    [InlineData("circle(50% at 0 0)", "circle(50% at 0px 0px)")]
    [InlineData("circle()", "circle(closest-side at 50% 50%)")]
    [InlineData("ellipse(10px 20px at right)", "ellipse(10px 20px at 100% 50%)")]
    [InlineData("polygon(0 0, 100% 0, 50% 100%)", "polygon(0px 0px, 100% 0px, 50% 100%)")]
    [InlineData("polygon(evenodd, 0 0, 1px 1px, 2px 0)", "polygon(evenodd, 0px 0px, 1px 1px, 2px 0px)")]
    [InlineData("path('M0 0 L10 10')", "path(\"M0 0 L10 10\")")]
    [InlineData("xywh(10px 20px 30px 40px)", "inset(20px calc(100% - 40px) calc(100% - 60px) 10px)")]
    [InlineData("rect(auto 10px 20px auto)", "inset(0% calc(100% - 10px) calc(100% - 20px) 0%)")]
    [InlineData("padding-box", "padding-box")]
    [InlineData("circle(10px) content-box", "circle(10px at 50% 50%) content-box")]
    [InlineData("url(#clip)", "url(\"#clip\")")]
    public void Clip_path_computed_values(string value, string expected) => Assert.Equal(expected, Text("clip-path", value));

    [Theory]
    [InlineData("inset()")]
    [InlineData("circle(1px 2px)")]
    [InlineData("ellipse(1px)")]
    [InlineData("polygon(0 0, 1px)")]
    [InlineData("circle(-1px)")]
    [InlineData("border-box border-box")]
    [InlineData("xywh(0 0 -1px 1px)")]
    public void Invalid_clip_paths(string value) => Assert.False(IsValid("clip-path", value), value);

    [Fact]
    public void Clip_path_typed_access()
    {
        var c = Compute("clip-path", "inset(50%)").ClipPath;
        Assert.Equal(CssClipPathKind.Shape, c.Kind);
        Assert.Equal(CssBasicShapeKind.Inset, c.ShapeKind);
        Assert.Equal(50f, c.InsetTop.Percentage);
        Assert.Equal(CssGeometryBox.BorderBox, c.GeometryBox);
        Assert.Equal(4, c.InsetRadii.Count);
        Assert.Same(CssClipPath.None, ComputedStyle.Initial.ClipPath);
    }

    [Theory]
    [InlineData("mix-blend-mode", "multiply", "multiply")]
    [InlineData("isolation", "isolate", "isolate")]
    [InlineData("mask-image", "linear-gradient(black, transparent)", "linear-gradient(180deg, rgb(0, 0, 0), rgba(0, 0, 0, 0))")]
    [InlineData("-webkit-mask-image", "none", null)]
    [InlineData("mask-mode", "alpha, luminance", "alpha, luminance")]
    [InlineData("mask-repeat", "no-repeat, repeat-x", "no-repeat, repeat-x")]
    [InlineData("mask-position", "center top", "center top")]
    [InlineData("mask-size", "contain, 10px auto", "contain, 10px auto")]
    [InlineData("mask-composite", "intersect", "intersect")]
    [InlineData("mask-clip", "no-clip", "no-clip")]
    [InlineData("mask-type", "alpha", "alpha")]
    [InlineData("background-blend-mode", "multiply, screen", "multiply, screen")]
    [InlineData("will-change", "transform, opacity", "transform, opacity")]
    public void Effects_parse_only_properties(string property, string value, string? expected)
    {
        Assert.True(IsValid(property, value), property + ": " + value);
        if (expected is not null) Assert.Equal(expected, Text(property, value));
    }

    [Fact]
    public void Conic_gradient_and_interpolation_methods_parse()
    {
        Assert.True(IsValid("background-image", "conic-gradient(from 90deg at 10% 20%, red, blue 50%, green 270deg)"));
        Assert.True(IsValid("background-image", "conic-gradient(from calc(1deg * 90), #000 0%, transparent 100%)"));
        Assert.True(IsValid("background-image", "linear-gradient(to right in oklab, red, blue)"));
        Assert.True(IsValid("background-image", "linear-gradient(in oklch longer hue, red, blue)"));
        Assert.True(IsValid("background-image", "radial-gradient(circle at center in srgb-linear, red, blue)"));
        Assert.False(IsValid("background-image", "linear-gradient(in srgb longer hue, red, blue)"));
        var img = Compute("background-image", "conic-gradient(from 90deg, red, blue 90deg)").BackgroundImage.Layers[0];
        var conic = Assert.IsType<ConicGradient>(img);
        Assert.Equal(90, conic.FromDegrees);
        Assert.Equal(25f, conic.Stops[1].Position!.Value.Percentage, 3);
        var lin = Assert.IsType<LinearGradient>(Compute("background-image", "linear-gradient(to right in oklab, red, blue)").BackgroundImage.Layers[0]);
        Assert.Equal(CssColorSpace.Oklab, lin.Interpolation);
        Assert.Equal(90, lin.AngleDegrees);
    }
}
