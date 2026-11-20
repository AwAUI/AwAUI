using System.Globalization;
using System.Numerics;
using AwAUI.Css.Animation;
using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Animation.AnimationTestHelper;

namespace AwAUI.Css.UnitTests.Animation;

/// <summary>
/// 与 Chrome（chrome-headless-shell 1243）对照的插值结果：<c>element.animate([{p: from}, {p: to}], {duration: 1000, easing: 'linear'})</c>
/// 暂停在 <c>p × 1000</c> ms 处读取 getComputedStyle（200×100 的 div）。变换按矩阵比较（Chrome 的解析值总是矩阵）。
/// </summary>
public class ChromeInterpolationParityTests
{
    [Theory]
    [InlineData("translateX(10px)", "translateY(20px)", 0.5, "matrix(1, 0, 0, 1, 5, 10)")]
    [InlineData("rotate(0deg)", "rotate(360deg)", 0.5, "matrix(-1, 0, 0, -1, 0, 0)")]
    [InlineData("scale(2) rotate(90deg)", "translateX(0px)", 0.5, "matrix(1.06066, 1.06066, -1.06066, 1.06066, 0, 0)")]
    [InlineData("rotate(-170deg) translateX(0px)", "translateX(0px) rotate(170deg)", 0.5, "matrix(-1, 0, 0, -1, 0, 0)")]
    [InlineData("none", "scale(2)", 0.5, "matrix(1.5, 0, 0, 1.5, 0, 0)")]
    [InlineData("translateX(50%) rotate(0deg)", "rotate(90deg) translateX(10px)", 0.5, "matrix(0.707107, 0.707107, -0.707107, 0.707107, 50, 5)")]
    [InlineData("rotateX(0deg)", "rotateY(90deg)", 0.5, "matrix3d(0.707107, 0, -0.707107, 0, 0, 1, 0, 0, 0.707107, 0, 0.707107, 0, 0, 0, 0, 1)")]
    [InlineData("matrix(1, 0, 0, 1, 0, 0)", "matrix(0, 1, -1, 0, 100, 50)", 0.25, "matrix(0.92388, 0.382683, -0.382683, 0.92388, 25, 12.5)")]
    [InlineData("skewX(0deg) scale(1)", "rotate(45deg) skewX(30deg)", 0.5, "matrix(0.92388, 0.382683, -0.115982, 1.03435, 0, 0)")]
    [InlineData("perspective(none)", "perspective(100px)", 0.5, "matrix3d(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, -0.005, 0, 0, 0, 1)")]
    [InlineData("translate3d(0px, 0px, 0px) scale3d(0.95, 0.95, 0.95) rotate(0deg)", "none", 0.5, "matrix3d(0.975, 0, 0, 0, 0, 0.975, 0, 0, 0, 0, 0.975, 0, 0, 0, 0, 1)")]
    [InlineData("scale(-1, 1)", "scale(1, -1)", 0.25, "matrix(-0.5, 0, 0, 0.5, 0, 0)")]
    public void Transforms_match_chrome(string from, string to, double p, string chrome)
    {
        Assert.True(CssProperties.TryGet("transform", out var t));
        CssInterpolation.Interpolate(t.Id, Computed("transform", from), Computed("transform", to), p, out var v);
        var m = ((CssTransformList)v).ToMatrix(200, 100);
        var expected = ParseMatrix(chrome);
        float[] actual = { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 };
        for (int i = 0; i < 16; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) < 2e-5 * Math.Max(1, Math.Abs(expected[i])), $"[{i}] chrome {expected[i]} ours {actual[i]} ({v})");
    }

    [Theory]
    [InlineData("oklch(0.6 0.2 30)", "oklch(0.8 0.1 200)", 0.5, "oklab(0.7 0.0396179 0.032899)")]
    [InlineData("oklch(0.205 0 0)", "oklch(0.985 0 0)", 0.3, "oklab(0.439 0 0)")]
    [InlineData("transparent", "red", 0.5, "rgba(255, 0, 0, 0.5)")]
    [InlineData("rgba(0,0,255,0)", "rgb(0,0,255)", 0.25, "rgba(0, 0, 255, 0.25)")]
    public void Non_legacy_colors_match_chrome(string from, string to, double p, string chrome)
    {
        Assert.True(CssColorParser.TryParse(chrome, out var c));
        var ours = CssInterpolation.InterpolateColor((CssColor)Computed("color", from), (CssColor)Computed("color", to), p).Value;
        Assert.True(Math.Abs(c.Value.R - ours.R) * 255 <= 1 && Math.Abs(c.Value.G - ours.G) * 255 <= 1 && Math.Abs(c.Value.B - ours.B) * 255 <= 1
                    && Math.Abs(c.Value.A - ours.A) * 255 <= 1, $"chrome {c} ours {new CssColor(ours)}");
    }

    [Fact]
    public void Legacy_srgb_colors_differ_from_chrome_by_design()
    {
        // Chrome 对两端都是传统 sRGB 写法（命名色、hex、rgb()）的颜色在 sRGB 中插值：red → blue 一半为 rgb(128, 0, 128)。
        // 这里按 CSS Color 4 的默认插值空间 Oklab（计算值不保留「传统写法」的信息）
        Assert.Equal("rgb(140, 83, 162)", Lerp("color", "red", "blue", 0.5));
    }

    [Theory]
    [InlineData("box-shadow", "none", "10px 10px 10px red", 0.5, "rgba(255, 0, 0, 0.5) 5px 5px 5px 0px")]
    [InlineData("box-shadow", "1px 1px red, 2px 2px blue", "3px 3px red", 0.5, "rgb(255, 0, 0) 2px 2px 0px 0px, rgba(0, 0, 255, 0.5) 1px 1px 0px 0px")]
    [InlineData("box-shadow", "inset 0 0 2px red", "0 0 2px red", 0.4, "rgb(255, 0, 0) 0px 0px 2px 0px inset")]
    [InlineData("text-shadow", "0 0 0 black", "4px 4px 2px black", 0.5, "rgb(0, 0, 0) 2px 2px 1px")]
    [InlineData("filter", "none", "blur(10px) opacity(0)", 0.5, "blur(5px) opacity(0.5)")]
    [InlineData("filter", "grayscale(0)", "grayscale(1) brightness(2)", 0.5, "grayscale(0.5) brightness(1.5)")]
    [InlineData("filter", "blur(2px)", "grayscale(1)", 0.4, "blur(2px)")]
    [InlineData("filter", "drop-shadow(0 0 0 transparent)", "drop-shadow(4px 4px 2px red)", 0.5, "drop-shadow(rgba(255, 0, 0, 0.5) 2px 2px 1px)")]
    [InlineData("z-index", "0", "5", 0.5, "3")]
    [InlineData("z-index", "0", "5", 0.45, "2")]
    [InlineData("font-weight", "100", "900", 0.5, "500")]
    [InlineData("rotate", "none", "90deg", 0.5, "45deg")]
    [InlineData("rotate", "x 0deg", "y 90deg", 0.5, "y 45deg")]
    [InlineData("scale", "none", "2", 0.5, "1.5")]
    [InlineData("translate", "none", "100px 20px", 0.5, "50px 10px")]
    [InlineData("visibility", "hidden", "visible", 0.1, "visible")]
    [InlineData("visibility", "visible", "hidden", 0.9, "visible")]
    [InlineData("aspect-ratio", "1 / 1", "4 / 1", 0.5, "2 / 1")]
    [InlineData("object-position", "0% 0%", "100% 50%", 0.5, "50% 25%")]
    [InlineData("border-top-left-radius", "0px", "20px 10%", 0.5, "10px 5%")]
    [InlineData("perspective", "100px", "300px", 0.5, "200px")]
    [InlineData("perspective", "none", "300px", 0.4, "none")]
    [InlineData("font-variation-settings", "\"wght\" 400", "\"wght\" 500", 0.5, "\"wght\" 450")]
    [InlineData("display", "none", "block", 0.1, "block")]
    [InlineData("position", "static", "absolute", 0.49, "static")]
    [InlineData("height", "0px", "auto", 0.4, "0px")]
    public void Values_match_chrome(string property, string from, string to, double p, string chrome)
        => Assert.Equal(chrome, Lerp(property, from, to, p));

    static float[] ParseMatrix(string s)
    {
        int open = s.IndexOf('(');
        var parts = s.Substring(open + 1, s.Length - open - 2).Split(',').Select(x => float.Parse(x.Trim(), CultureInfo.InvariantCulture)).ToArray();
        if (parts.Length == 16) return parts;
        return new[] { parts[0], parts[1], 0, 0, parts[2], parts[3], 0, 0, 0, 0, 1, 0, parts[4], parts[5], 0, 1 };
    }
}
