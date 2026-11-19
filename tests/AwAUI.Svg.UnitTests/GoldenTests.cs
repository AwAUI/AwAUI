using AwAUI.Graphics;

namespace AwAUI.Svg.UnitTests;

/// <summary>
/// 手写的代表性图标与插画，经软件后端渲染后与 tests/TestFiles/Svg/*.png 比对。
/// 重新生成：AWAUI_UPDATE_GOLDEN=1 dotnet test ...（生成后须逐张目视检查）。
/// </summary>
public class GoldenTests
{
    /// <summary>名称、输出尺寸、currentColor、允许出现的诊断代码。</summary>
    public static readonly TheoryData<string, int, int, string, string> Cases = new()
    {
        { "icon-home", 96, 96, "#1f2937", "" },
        { "icon-heart", 96, 96, "#e11d48", "" },
        { "icon-search", 96, 96, "#2563eb", "" },
        { "icon-settings", 96, 96, "#475569", "" },
        { "icon-badge", 96, 96, "#000000", "" },
        { "gradients", 240, 140, "#7c3aed", "" },
        { "clip-paths", 200, 100, "#000000", "" },
        { "strokes", 240, 160, "#000000", "" },
        { "css-styles", 200, 110, "#000000", "UnsupportedCss" },
        { "landscape", 320, 200, "#000000", "" },
        { "paths-arcs", 240, 150, "#000000", "" },
        { "transforms", 240, 140, "#000000", "" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Renders_like_the_golden_image(string name, int width, int height, string currentColor, string allowedDiagnostics)
    {
        var path = System.IO.Path.Combine(SvgGolden.SvgDir, name + ".svg");
        var doc = SvgDocument.Parse(File.ReadAllBytes(path));
        var allowed = allowedDiagnostics.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var d in doc.Diagnostics)
            Assert.True(allowed.Contains(d.Code.ToString()), $"Unexpected diagnostic: {d}");

        var options = new SvgRenderOptions { CurrentColor = Color.Parse(currentColor) };
        using var bmp = SvgGolden.Render(doc, width, height, options);
        SvgGolden.AssertMatches(name, bmp);
    }

    [Fact]
    public void Icon_tinting_changes_only_current_color_pixels()
    {
        var doc = SvgDocument.Parse(File.ReadAllBytes(System.IO.Path.Combine(SvgGolden.SvgDir, "icon-heart.svg")));
        Assert.True(doc.UsesCurrentColor);
        using var red = SvgGolden.Render(doc, 48, 48, new SvgRenderOptions { CurrentColor = Color.Parse("#ff0000") }, Color.Transparent);
        using var blue = SvgGolden.Render(doc, 48, 48, new SvgRenderOptions { CurrentColor = Color.Parse("#0000ff") }, Color.Transparent);
        int painted = 0;
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 48; x++)
            {
                var r = SvgGolden.Pixel(red, x, y);
                var b = SvgGolden.Pixel(blue, x, y);
                Assert.Equal(r.A, b.A);
                if (r.A == 255)
                {
                    painted++;
                    Assert.Equal((255, 0, 0), (r.R, r.G, r.B));
                    Assert.Equal((0, 0, 255), (b.R, b.G, b.B));
                }
            }
        Assert.True(painted > 300, painted.ToString());
    }

    [Fact]
    public void Scaled_rendering_is_consistent_with_the_intrinsic_size()
    {
        var doc = SvgDocument.Parse(File.ReadAllBytes(System.IO.Path.Combine(SvgGolden.SvgDir, "icon-settings.svg")));
        Assert.Equal(new Size(24, 24), doc.IntrinsicSize);
        var at1 = doc.ToDisplayList();
        var at4 = doc.ToDisplayList(new Rect(0, 0, 96, 96));
        Assert.True(at1.Bounds.Width <= 24.01f && at4.Bounds.Width <= 96.01f);
        Assert.True(at4.Bounds.Width > at1.Bounds.Width * 3.9f);
    }
}
