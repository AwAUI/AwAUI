using AwAUI.Css.Cascade;
using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Values.ValueTestHelper;

namespace AwAUI.Css.UnitTests.Values;

/// <summary>
/// CSS Color 4/5：各颜色空间、color-mix()、none 分量与相对颜色语法。期望值是 headless Chromium（chrome-headless-shell 1243）
/// 在 sRGB canvas 上的读数（8 位）；alpha 很低时 canvas 预乘后的读数有量化误差，这类颜色只比较 alpha 与未预乘的期望。
/// </summary>
public class ColorSpaceTests
{
    [Theory]
    [InlineData("oklch(0.205 0 0)", 23, 23, 23, 255)]
    [InlineData("oklch(0.577 0.245 27.325)", 231, 0, 11, 255)]
    [InlineData("color-mix(in oklab, oklch(0.205 0 0) 90%, transparent)", 23, 23, 23, 230)]
    [InlineData("oklch(0.985 0 0)", 250, 250, 250, 255)]
    [InlineData("oklch(0.7 0.4 140)", 0, 205, 0, 255)]
    [InlineData("color(display-p3 1 0 0)", 255, 0, 0, 255)]
    [InlineData("lab(50 100 -100)", 201, 0, 255, 255)]
    [InlineData("oklch(0.6 0.3 300)", 162, 38, 255, 255)]
    [InlineData("color(rec2020 0 1 0)", 0, 255, 0, 255)]
    [InlineData("oklch(1.2 0 0)", 255, 255, 255, 255)]
    [InlineData("oklch(0.95 0.3 120)", 216, 255, 0, 255)]
    [InlineData("oklch(0.145 0 0)", 10, 10, 10, 255)]
    [InlineData("oklch(0.269 0 0)", 38, 38, 38, 255)]
    [InlineData("oklch(0.398 0.07 227.392)", 16, 78, 100, 255)]
    [InlineData("oklch(0.488 0.243 264.376)", 20, 71, 230, 255)]
    [InlineData("oklch(0.556 0 0)", 115, 115, 115, 255)]
    [InlineData("oklch(0.6 0.118 184.704)", 0, 150, 137, 255)]
    [InlineData("oklch(0.627 0.265 303.9)", 173, 70, 255, 255)]
    [InlineData("oklch(0.645 0.246 16.439)", 255, 32, 86, 255)]
    [InlineData("oklch(0.646 0.222 41.116)", 245, 74, 0, 255)]
    [InlineData("oklch(0.696 0.17 162.48)", 0, 188, 125, 255)]
    [InlineData("oklch(0.704 0.191 22.216)", 255, 100, 103, 255)]
    [InlineData("oklch(0.708 0 0)", 161, 161, 161, 255)]
    [InlineData("oklch(0.769 0.188 70.08)", 254, 154, 0, 255)]
    [InlineData("oklch(0.828 0.189 84.429)", 255, 185, 0, 255)]
    [InlineData("oklch(0.922 0 0)", 229, 229, 229, 255)]
    [InlineData("oklch(0.97 0 0)", 245, 245, 245, 255)]
    [InlineData("oklch(1 0 0 / 10%)", 245, 255, 255, 26)]
    [InlineData("oklch(1 0 0 / 15%)", 255, 255, 255, 38)]
    [InlineData("color-mix(in oklab, oklch(0.577 0.245 27.325) 20%, transparent)", 230, 0, 10, 51)]
    [InlineData("color-mix(in oklab, oklch(0.704 0.191 22.216) 40%, transparent)", 255, 100, 102, 102)]
    [InlineData("color-mix(in oklab, oklch(0.708 0 0) 50%, transparent)", 161, 161, 161, 128)]
    [InlineData("color-mix(in oklab, oklch(1 0 0 / 15%) 50%, transparent)", 255, 255, 255, 19)]
    [InlineData("color-mix(in oklab, #000 50%, transparent)", 0, 0, 0, 128)]
    [InlineData("color-mix(in srgb, red, blue)", 128, 0, 128, 255)]
    [InlineData("color-mix(in srgb-linear, red, blue)", 188, 0, 188, 255)]
    [InlineData("color-mix(in hsl, red, blue)", 255, 0, 255, 255)]
    [InlineData("color-mix(in hsl longer hue, red, blue)", 0, 255, 0, 255)]
    [InlineData("color-mix(in hsl increasing hue, red, blue)", 0, 255, 0, 255)]
    [InlineData("color-mix(in hsl decreasing hue, red, blue)", 255, 0, 255, 255)]
    [InlineData("color-mix(in oklch, red 30%, blue)", 136, 0, 236, 255)]
    [InlineData("color-mix(in oklch longer hue, red, blue)", 0, 147, 0, 255)]
    [InlineData("color-mix(in lch, red, white)", 255, 159, 128, 255)]
    [InlineData("color-mix(in lab, red 25%, blue 25%)", 191, 0, 135, 128)]
    [InlineData("color-mix(in hwb, red, lime)", 255, 255, 0, 255)]
    [InlineData("color-mix(in xyz, red, blue)", 188, 0, 188, 255)]
    [InlineData("color-mix(in xyz-d50, red 70%, lime)", 218, 149, 0, 255)]
    [InlineData("color-mix(in display-p3, red, blue)", 128, 10, 145, 255)]
    [InlineData("color-mix(in a98-rgb, red, blue)", 128, 0, 128, 255)]
    [InlineData("color-mix(in prophoto-rgb, red, blue)", 186, 3, 157, 255)]
    [InlineData("color-mix(in rec2020, red, blue)", 162, 19, 147, 255)]
    [InlineData("color-mix(in oklch, oklch(0.5 none 120), oklch(0.7 0.1 none))", 121, 137, 64, 255)]
    [InlineData("color-mix(in oklab, rgb(255 0 0 / 50%), blue)", 101, 77, 194, 191)]
    [InlineData("color-mix(in srgb, red 60%, blue 60%)", 128, 0, 128, 255)]
    [InlineData("lch(50 50 50)", 178, 95, 55, 255)]
    [InlineData("lab(60 -40 30)", 74, 162, 88, 255)]
    [InlineData("hwb(120 20% 30%)", 51, 179, 51, 255)]
    [InlineData("color(a98-rgb 1 0.5 0)", 255, 128, 0, 255)]
    [InlineData("color(prophoto-rgb 0.5 0.5 0.2)", 161, 146, 36, 255)]
    [InlineData("color(xyz-d50 0.3 0.2 0.1)", 198, 86, 96, 255)]
    [InlineData("color(xyz 0.3 0.2 0.1)", 206, 84, 81, 255)]
    [InlineData("color(srgb-linear 0.5 0.2 0.1)", 188, 124, 89, 255)]
    [InlineData("rgb(from red r g b / 50%)", 255, 0, 0, 128)]
    [InlineData("oklch(from #3366cc calc(l + 0.1) c h)", 80, 133, 238, 255)]
    [InlineData("hsl(from blue h s calc(l - 20))", 0, 0, 153, 255)]
    [InlineData("oklab(0.5 0.1 -0.1 / 0.7)", 130, 68, 154, 179)]
    [InlineData("oklch(50% 50% 90)", 143, 86, 0, 255)]
    // 已在插值空间中的颜色：无效色相（色度/饱和度为 0）不转为缺失，照常参与插值（只有换算时才转为缺失，与 Chrome 一致）
    [InlineData("color-mix(in oklch, oklch(0.5 0 30), oklch(0.7 0.2 120))", 163, 119, 52, 255)]
    [InlineData("color-mix(in hsl, hsl(120 0% 50%), hsl(240 100% 50%))", 64, 191, 191, 255)]
    [InlineData("color-mix(in hwb, hwb(30 60% 60%), blue)", 179, 77, 153, 255)]
    // sRGB → HSL 直接换算：白色的饱和度恰为 0，色相无效 → 取蓝色的色相
    [InlineData("color-mix(in hsl, white, blue)", 159, 159, 223, 255)]
    [InlineData("color-mix(in oklch, white, blue)", 115, 163, 255, 255)]
    [InlineData("color-mix(in oklch, red 10%, blue 30%)", 122, 0, 242, 102)]
    [InlineData("color-mix(in lch, transparent, red)", 253, 0, 0, 128)]
    [InlineData("color-mix(in oklch, oklch(0.6 0.1 180), oklch(0.6 0.1 0))", 151, 125, 48, 255)]
    [InlineData("color-mix(in oklch decreasing hue, oklch(0.6 0.1 90), oklch(0.6 0.1 91))", 105, 125, 188, 255)]
    [InlineData("color-mix(in srgb, color-mix(in oklch, red, blue), lime 25%)", 140, 30, 145, 255)]
    [InlineData("oklch(from red l c calc(h + 180))", 0, 169, 219, 255)]
    [InlineData("lch(50 -10 30)", 119, 119, 119, 255)]
    // hsl()/hwb() 的范围（Chrome 1243）：现代语法只把 S/L、W/B 的下界钳到 0，上界不钳；旧（逗号）语法钳到 [0, 100]；
    // 相对颜色语法 rgb/hsl/hwb(from …) 完全不钳（广色域来源可往返）。只在 color-mix 或相对颜色中可观察到
    [InlineData("color-mix(in hsl, hsl(120 150% 50%), hsl(120 0% 50%))", 32, 223, 32, 255)]
    [InlineData("color-mix(in hsl, hsl(120 50% 150%), hsl(120 50% 0%))", 159, 223, 159, 255)]
    [InlineData("color-mix(in hsl, hsl(120 -50% 50%), hsl(120 100% 50%))", 64, 191, 64, 255)]
    [InlineData("color-mix(in hsl, hsla(120, 150%, 50%, 1), hsl(120 0% 50%))", 64, 191, 64, 255)]
    [InlineData("color-mix(in hwb, hwb(120 150% 0%), hwb(120 0% 0%))", 191, 255, 191, 255)]
    [InlineData("color-mix(in hwb, hwb(120 -50% 0%), hwb(120 50% 0%))", 64, 255, 64, 255)]
    [InlineData("hwb(0 30% 130%)", 48, 48, 48, 255)]
    [InlineData("hwb(0 120% 30%)", 204, 204, 204, 255)]
    [InlineData("hsl(from oklch(0.7 0.2 30) h s l)", 255, 97, 77, 255)]
    [InlineData("hsl(from color(display-p3 1 0 0) h s l)", 255, 0, 0, 255)]
    [InlineData("hwb(from color(display-p3 0 1 0) h w b)", 0, 255, 0, 255)]
    [InlineData("hsl(from color(srgb 0.5 1.2 0.3) h s l)", 127, 255, 76, 255)]
    [InlineData("hsl(from red h calc(s * -1) l)", 0, 255, 255, 255)]
    [InlineData("color-mix(in srgb, rgb(from red calc(r * 2) g b), black)", 255, 0, 0, 255)]
    [InlineData("color-mix(in srgb, rgb(from red r calc(g - 255) b), rgb(0 255 0))", 128, 0, 0, 255)]
    [InlineData("color-mix(in lab, lab(from white calc(l + 50) a b), lab(0 0 0))", 119, 119, 119, 255)]
    [InlineData("color-mix(in oklch, oklch(from red l calc(c * -1) h), oklch(0.5 0.2 30))", 168, 94, 82, 255)]
    public void Matches_chrome(string text, int r, int g, int b, int a)
    {
        Assert.True(CssColorParser.TryParse(text, out var c), text);
        var v = c.Value;
        int A = (int)Math.Round(v.A * 255);
        Assert.True(Math.Abs(A - a) <= 1, $"{text}: alpha {A} vs {a}");
        if (a < 64) return; // canvas 的预乘读数在低 alpha 下不可靠
        int R = (int)Math.Round(v.R * 255), G = (int)Math.Round(v.G * 255), B = (int)Math.Round(v.B * 255);
        int tol = a >= 250 ? 1 : 3; // 半透明时 canvas 读数经过 8 位预乘
        Assert.True(Math.Abs(R - r) <= tol && Math.Abs(G - g) <= tol && Math.Abs(B - b) <= tol, $"{text}: ({R},{G},{B}) vs chrome ({r},{g},{b})");
    }

    [Fact]
    public void Shadcn_tokens_compute_exactly_like_chrome()
    {
        // shadcn v4 的令牌（:root 与 .dark）经 var() 进入 background-color；结果与 Chrome 的 8 位值逐一相同
        var engine = new StyleEngine();
        engine.AddStyleSheet(StyleSheet.Parse(":root { --primary: oklch(0.205 0 0); --destructive: oklch(0.577 0.245 27.325); --ring: oklch(0.708 0 0) }"
            + " .a { background-color: var(--primary) } .b { background-color: var(--destructive) }"
            + " .c { background-color: color-mix(in oklab, var(--primary) 90%, transparent) } .d { outline-color: color-mix(in oklab, var(--ring) 50%, transparent) }"));
        var root = new El("html").Add(new El("div", null, "a"), new El("div", null, "b"), new El("div", null, "c"), new El("div", null, "d"));
        var rs = engine.Resolve(root, null);
        string Rgba(Color c) => $"{(int)Math.Round(c.R * 255)},{(int)Math.Round(c.G * 255)},{(int)Math.Round(c.B * 255)},{(int)Math.Round(c.A * 255)}";
        Assert.Equal("23,23,23,255", Rgba(engine.Resolve(root[0], rs).BackgroundColor.Value));
        Assert.Equal("231,0,11,255", Rgba(engine.Resolve(root[1], rs).BackgroundColor.Value));
        Assert.Equal("23,23,23,230", Rgba(engine.Resolve(root[2], rs).BackgroundColor.Value));
        Assert.Equal("161,161,161,128", Rgba(engine.Resolve(root[3], rs).OutlineColor.Value));
    }

    [Theory]
    [InlineData("lab(50 0 0)")]
    [InlineData("lch(50% 30% 90deg)")]
    [InlineData("oklab(50% -40% 40% / 0.5)")]
    [InlineData("oklch(none none none)")]
    [InlineData("hwb(90deg none 10)")]
    [InlineData("color(display-p3 1 0 0 / 50%)")]
    [InlineData("color(xyz-d65 none 0.5 0.5)")]
    [InlineData("color-mix(in srgb, red, blue)")]
    [InlineData("color-mix(red, blue)")]
    [InlineData("color-mix(in oklch shorter hue, red 10%, blue)")]
    [InlineData("color-mix(in oklab, currentcolor 50%, transparent)")]
    [InlineData("color-mix(in oklab, color-mix(in srgb, red, blue), green)")]
    [InlineData("rgb(from oklch(0.5 0.1 120) r g b)")]
    [InlineData("lab(from red l a b / calc(alpha / 2))")]
    [InlineData("color(from red srgb r g b)")]
    public void Valid_modern_colors(string text) => Assert.True(CssColorParser.TryParse(text, out _), text);

    [Theory]
    [InlineData("lab(50, 0, 0)")]
    [InlineData("oklch(0.5 0.1)")]
    [InlineData("oklch(0.5 0.1 10%)")]
    [InlineData("hwb(10, 20%, 30%)")]
    [InlineData("color(unknown 1 0 0)")]
    [InlineData("color(srgb 1 0)")]
    [InlineData("color(lab 1 0 0)")]
    [InlineData("color-mix(in srgb red, blue)")]
    [InlineData("color-mix(in srgb, red)")]
    [InlineData("color-mix(in srgb, red 0%, blue 0%)")]
    [InlineData("color-mix(in srgb, red 150%, blue)")]
    [InlineData("color-mix(in srgb longer hue, red, blue)")]
    [InlineData("color-mix(in foo, red, blue)")]
    [InlineData("color-mix(in srgb, red, blue, green)")]
    [InlineData("rgb(from currentcolor r g b)")]
    [InlineData("rgb(from red r g q)")]
    public void Invalid_modern_colors(string text) => Assert.False(CssColorParser.TryParse(text, out _), text);

    [Fact]
    public void Color_mix_with_currentcolor_is_resolved_at_used_value_time()
    {
        var engine = new StyleEngine();
        engine.AddStyleSheet(StyleSheet.Parse("p { color: rgb(255 0 0) } span { color: blue; border-top-color: color-mix(in srgb, currentcolor 50%, white); }"
            + " em { color: color-mix(in srgb, currentcolor 50%, rgb(0 0 255)) }"));
        var root = new El("p").Add(new El("span"), new El("em"));
        var ps = engine.Resolve(root, null);
        var span = engine.Resolve(root[0], ps);
        Assert.True(span.BorderTopColor.DependsOnCurrentColor);
        Assert.False(span.BorderTopColor.IsCurrentColor);
        var used = span.ResolveColor(span.BorderTopColor);
        Assert.Equal(0.5f, used.R, 3);
        Assert.Equal(0.5f, used.G, 3);
        Assert.Equal(1f, used.B, 3);
        // color 属性中的 currentcolor 指父元素的颜色，计算值是具体颜色
        var em = engine.Resolve(root[1], ps);
        Assert.False(em.Color.DependsOnCurrentColor);
        Assert.Equal(0.5f, em.Color.Value.R, 3);
        Assert.Equal(0.5f, em.Color.Value.B, 3);
        Assert.Contains("color-mix", span.GetPropertyValueText("border-top-color"));
    }

    [Fact]
    public void Deferred_mixes_compare_structurally()
    {
        Assert.True(CssColorParser.TryParse("color-mix(in oklab, currentcolor 40%, red)", out var a));
        Assert.True(CssColorParser.TryParse("color-mix(in oklab, currentColor 40%, red)", out var b));
        Assert.True(CssColorParser.TryParse("color-mix(in oklab, currentcolor 41%, red)", out var c));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Gamut_mapping_api_follows_css_color_4()
    {
        // shadcn 的 destructive 红只稍出 sRGB 色域：裁剪结果与原色的 ΔEOK < 0.02，CSS 色域映射直接返回裁剪结果，两种方式一致
        var clippedRed = CssColorMath.ToSrgbClipped(CssColorSpace.Oklch, 0.577, 0.245, 27.325, 1);
        Assert.Equal(clippedRed, CssColorMath.GamutMapToSrgb(CssColorSpace.Oklch, 0.577, 0.245, 27.325, 1));
        // 远超色域时不同：CSS 色域映射保持亮度与色相、降低色度
        var clipped = CssColorMath.ToSrgbClipped(CssColorSpace.Oklch, 0.7, 0.4, 140, 1);
        var mapped = CssColorMath.GamutMapToSrgb(CssColorSpace.Oklch, 0.7, 0.4, 140, 1);
        Assert.Equal(0f, clipped.R);
        Assert.NotEqual(clipped, mapped);
        CssColorMath.Convert(CssColorSpace.Srgb, mapped.R, mapped.G, mapped.B, CssColorSpace.Oklch, out var l, out var ch, out var h);
        Assert.Equal(0.7, l, 1);
        Assert.InRange(h, 137, 143); // 最后一步仍会裁剪（ΔEOK < 0.02），色相略有偏移
        Assert.True(ch < 0.25, ch.ToString());
        // 在色域内的颜色不变；亮度超出范围的得到白/黑
        var inGamut = CssColorMath.GamutMapToSrgb(CssColorSpace.Srgb, 0.2, 0.4, 0.6, 1);
        Assert.Equal(0.4f, inGamut.G, 5);
        Assert.Equal(new Color(1, 1, 1), CssColorMath.GamutMapToSrgb(CssColorSpace.Oklch, 1.2, 0.3, 0, 1));
    }

    [Fact]
    public void Round_trip_conversions_are_stable()
    {
        foreach (CssColorSpace s in new[] { CssColorSpace.DisplayP3, CssColorSpace.A98Rgb, CssColorSpace.ProphotoRgb, CssColorSpace.Rec2020,
                     CssColorSpace.XyzD50, CssColorSpace.XyzD65, CssColorSpace.Lab, CssColorSpace.Lch, CssColorSpace.Oklab, CssColorSpace.Oklch,
                     CssColorSpace.Hsl, CssColorSpace.Hwb, CssColorSpace.SrgbLinear })
        {
            CssColorMath.Convert(CssColorSpace.Srgb, 0.2, 0.5, 0.8, s, out var a, out var b, out var c);
            CssColorMath.Convert(s, a, b, c, CssColorSpace.Srgb, out var r, out var g, out var bl);
            Assert.Equal(0.2, r, 6);
            Assert.Equal(0.5, g, 6);
            Assert.Equal(0.8, bl, 6);
        }
    }

    [Fact]
    public void Hostile_nesting_is_bounded()
    {
        var text = "red";
        for (int i = 0; i < 200; i++) text = $"color-mix(in srgb, {text}, blue)";
        Assert.False(CssColorParser.TryParse(text, out _));
        var rel = "red";
        for (int i = 0; i < 200; i++) rel = $"rgb(from {rel} r g b)";
        Assert.False(CssColorParser.TryParse(rel, out _));
        Assert.True(IsValid("color", "color-mix(in srgb, color-mix(in srgb, red, blue), green)"));
    }
}
