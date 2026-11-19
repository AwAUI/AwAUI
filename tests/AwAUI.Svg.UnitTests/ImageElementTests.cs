using System.Diagnostics;
using System.Text;
using AwAUI.Graphics;
using AwAUI.Graphics.Commands;
using AwAUI.Imaging;

namespace AwAUI.Svg.UnitTests;

/// <summary>&lt;image&gt;：经 <see cref="SvgImageResolver"/> 解码（AwAUI.Svg 不引用 AwAUI.Imaging，测试里用 PNG 解码器实现解析器）。</summary>
public class ImageElementTests
{
    const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";

    /// <summary>用 AwAUI.Imaging 解码的解析器（UI 将提供同样的实现），并记录调用次数。</summary>
    sealed class PngResolver : SvgImageResolver
    {
        public int Decodes;
        public List<string> MediaTypes = new();
        public Func<string, Bitmap?>? External;

        public override Bitmap? Decode(ReadOnlySpan<byte> data, string mediaType)
        {
            Decodes++;
            MediaTypes.Add(mediaType);
            return PngCodec.Decode(data.ToArray());
        }

        public override Bitmap? ResolveExternal(string href) => External?.Invoke(href);
    }

    sealed class ThrowingResolver : SvgImageResolver
    {
        public override Bitmap? Decode(ReadOnlySpan<byte> data, string mediaType) => throw new InvalidDataException("corrupt");
    }

    /// <summary>2×2 的 PNG：红、绿 / 蓝、黄。</summary>
    static string PngDataUri(int scale = 1)
    {
        var colors = new[] { (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0) };
        int n = 2 * scale;
        var rgba = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var c = colors[(y / scale) * 2 + x / scale];
                int o = (y * n + x) * 4;
                rgba[o] = (byte)c.Item1; rgba[o + 1] = (byte)c.Item2; rgba[o + 2] = (byte)c.Item3; rgba[o + 3] = 255;
            }
        using var bmp = Bitmap.FromRgbaUnpremultiplied(rgba, new PixelSize(n, n));
        // 折行的 base64（SVG 编辑器常这样输出）
        var b64 = Convert.ToBase64String(PngCodec.Encode(bmp));
        var wrapped = new StringBuilder();
        for (int i = 0; i < b64.Length; i += 40) wrapped.Append(b64, i, Math.Min(40, b64.Length - i)).Append('\n');
        return "data:image/png;base64," + wrapped;
    }

    static SvgDocument Doc(string body, SvgImageResolver? resolver, string viewBox = "0 0 10 10") =>
        SvgDocument.Parse($"<svg {Ns} xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"{viewBox}\">{body}</svg>",
            new SvgParseOptions { ImageResolver = resolver });

    static (byte R, byte G, byte B, byte A) Probe(SvgDocument doc, int x, int y)
    {
        using var bmp = SvgGolden.Render(doc, 100, 100, null, Color.Transparent);
        return SvgGolden.Pixel(bmp, x, y);
    }

    static List<DrawImageCmd> ImageCommands(SvgDocument doc)
    {
        var list = doc.ToDisplayList(new Rect(0, 0, 100, 100));
        var result = new List<DrawImageCmd>();
        var r = list.GetReader();
        while (r.MoveNext())
            if (r.Op == DrawOp.DrawImage) result.Add(r.Read<DrawImageCmd>());
        return result;
    }

    [Fact]
    public void Image_without_a_resolver_is_ignored_with_a_diagnostic()
    {
        var doc = Doc($"<image href=\"{PngDataUri()}\" width=\"10\" height=\"10\"/>", null);
        Assert.Contains(doc.Diagnostics, d => d.Code == SvgDiagnosticCode.UnsupportedFeature && d.Message.Contains("ImageResolver"));
        Assert.Empty(ImageCommands(doc));
    }

    [Fact]
    public void Data_uri_png_is_decoded_and_drawn()
    {
        var resolver = new PngResolver();
        var doc = Doc($"<image href=\"{PngDataUri()}\" width=\"10\" height=\"10\" image-rendering=\"pixelated\"/>", resolver);
        Assert.Empty(doc.Diagnostics);
        Assert.Equal(new[] { "image/png" }, resolver.MediaTypes);
        Assert.Equal((255, 0, 0, 255), ((int, int, int, int))Probe(doc, 20, 20));
        Assert.Equal((0, 255, 0, 255), ((int, int, int, int))Probe(doc, 80, 20));
        Assert.Equal((0, 0, 255, 255), ((int, int, int, int))Probe(doc, 20, 80));
        Assert.Equal((255, 255, 0, 255), ((int, int, int, int))Probe(doc, 80, 80));
        Assert.Equal(ImageSampling.Nearest, ImageCommands(doc).Single().Sampling);
    }

    [Fact]
    public void Xlink_href_and_auto_size_use_the_intrinsic_size()
    {
        // 没有 width / height：取位图的固有尺寸（2×2 用户单位）；只给 width 时按宽高比推出高度
        var doc = Doc($"<image xlink:href=\"{PngDataUri()}\" x=\"1\" y=\"1\"/><image href=\"{PngDataUri()}\" x=\"4\" y=\"0\" width=\"6\"/>", new PngResolver());
        var cmds = ImageCommands(doc);
        Assert.Equal(2, cmds.Count);
        Assert.Equal(new Rect(1, 1, 2, 2), cmds[0].Dst);
        Assert.Equal(new Rect(4, 0, 6, 6), cmds[1].Dst);
    }

    [Theory]
    [InlineData("xMidYMid meet", 0, 2.5f, 10, 5, 0, 0, 2, 2)]
    [InlineData("xMinYMax meet", 0, 5, 10, 5, 0, 0, 2, 2)]
    [InlineData("none", 0, 0, 10, 10, 0, 0, 2, 2)]
    [InlineData("xMidYMid slice", 2.5f, 0, 5, 10, 0.5f, 0, 1, 2)]
    public void Preserve_aspect_ratio_maps_the_image_into_the_viewport(string par, float dx, float dy, float dw, float dh,
        float sx, float sy, float sw, float sh)
    {
        // 2×2 的图像放进 10×5（meet / none）或 5×10（slice）的视口；slice 直接裁成源矩形
        bool slice = par.Contains("slice");
        var doc = Doc($"<image href=\"{PngDataUri()}\" x=\"{(slice ? 2.5f : 0)}\" y=\"0\" width=\"{(slice ? 5 : 10)}\" height=\"{(slice ? 10 : 5)}\" preserveAspectRatio=\"{par}\"/>",
            new PngResolver());
        var c = ImageCommands(doc).Single();
        var expectedDst = par == "none" ? new Rect(0, 0, 10, 5) : new Rect(dx, dy, dw, dh);
        if (par.StartsWith("xMidYMid meet")) expectedDst = new Rect(2.5f, 0, 5, 5);
        if (par.StartsWith("xMinYMax meet")) expectedDst = new Rect(0, 0, 5, 5);
        if (slice) expectedDst = new Rect(2.5f, 0, 5, 10);
        Assert.Equal(expectedDst, c.Dst);
        Assert.Equal(new Rect(sx, sy, sw, sh), c.Src);
    }

    [Fact]
    public void Opacity_is_applied_to_the_image_without_a_layer()
    {
        var doc = Doc($"<image href=\"{PngDataUri()}\" width=\"10\" height=\"10\" opacity=\"0.5\"/>", new PngResolver());
        var list = doc.ToDisplayList(new Rect(0, 0, 100, 100));
        var r = list.GetReader();
        bool layer = false;
        float opacity = 0;
        while (r.MoveNext())
        {
            if (r.Op == DrawOp.PushLayer) layer = true;
            if (r.Op == DrawOp.DrawImage) opacity = r.Read<DrawImageCmd>().Opacity;
        }
        Assert.False(layer);
        Assert.Equal(0.5f, opacity);
    }

    [Fact]
    public void Image_referenced_by_many_uses_is_decoded_once()
    {
        var resolver = new PngResolver();
        var sb = new StringBuilder($"<defs><image id=\"i\" href=\"{PngDataUri()}\" width=\"1\" height=\"1\"/></defs>");
        for (int i = 0; i < 500; i++) sb.Append($"<use href=\"#i\" x=\"{i % 10}\" y=\"{i / 50}\"/>");
        var doc = Doc(sb.ToString(), resolver);
        Assert.Equal(1, resolver.Decodes);
        Assert.Equal(500, ImageCommands(doc).Count);
    }

    [Fact]
    public void Invalid_inputs_are_reported_not_thrown()
    {
        var resolver = new PngResolver();
        var doc = Doc("""
            <image href="data:image/png;base64,@@@@" width="5" height="5"/>
            <image href="data:image/png;base64," width="5" height="5"/>
            <image href="data:image/svg+xml,%3Csvg%2F%3E" width="5" height="5"/>
            <image href="data:image/png,not-a-png" width="5" height="5"/>
            <image href="https://example.com/a.png" width="5" height="5"/>
            <image href="" width="5" height="5"/>
            <image width="5" height="5"/>
            """, resolver);
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("Invalid data: URI"));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("SVG documents inside <image>"));
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("could not be loaded") || d.Message.Contains("could not be decoded"));
        Assert.Empty(ImageCommands(doc));

        var throwing = Doc($"<image href=\"{PngDataUri()}\" width=\"5\" height=\"5\"/>", new ThrowingResolver());
        Assert.Contains(throwing.Diagnostics, d => d.Message.Contains("could not be decoded"));
    }

    [Fact]
    public void External_references_are_only_loaded_by_the_resolver()
    {
        using var bmp = new Bitmap(new PixelSize(3, 3));
        bmp.Clear(Color.Parse("#00ff00"));
        var seen = new List<string>();
        var resolver = new PngResolver { External = href => { seen.Add(href); return href == "logo.png" ? bmp : null; } };
        var doc = Doc("<image href=\" logo.png \" width=\"10\" height=\"10\"/>", resolver);
        Assert.Equal(new[] { "logo.png" }, seen);
        Assert.Equal((0, 255, 0, 255), ((int, int, int, int))Probe(doc, 50, 50));
    }

    [Fact]
    public void Decoded_pixels_are_limited_per_document()
    {
        var resolver = new PngResolver();
        var uri = PngDataUri(4);   // 8×8 = 64 像素
        var doc = SvgDocument.Parse($"<svg {Ns} viewBox=\"0 0 10 10\"><image href=\"{uri}\" width=\"5\" height=\"5\"/><image href=\"{uri}\" x=\"5\" width=\"5\" height=\"5\"/></svg>",
            new SvgParseOptions { ImageResolver = resolver, MaxImagePixels = 100 });
        Assert.Contains(doc.Diagnostics, d => d.Code == SvgDiagnosticCode.LimitExceeded && d.Message.Contains("MaxImagePixels"));
        Assert.Single(ImageCommands(doc));
    }

    [Fact]
    public void Images_after_the_pixel_budget_is_exhausted_are_not_decoded()
    {
        // 评审发现：预算只在解码之后检查，用完之后每个 <image> 仍被解码一次再丢弃（解码的时间与内存不受 MaxImagePixels 约束）
        var resolver = new PngResolver();
        var uri = PngDataUri(4);   // 8×8 = 64 像素
        var body = new StringBuilder();
        for (int i = 0; i < 20; i++) body.Append($"<image href=\"{uri}\" x=\"{i}\" width=\"1\" height=\"1\"/>");
        var doc = SvgDocument.Parse($"<svg {Ns} viewBox=\"0 0 20 1\">{body}</svg>",
            new SvgParseOptions { ImageResolver = resolver, MaxImagePixels = 130 });
        Assert.Equal(2, ImageCommands(doc).Count);
        Assert.Equal(3, resolver.Decodes);   // 两张在预算内，第三张解码后超出；之后的 17 张不再解码
        Assert.Contains(doc.Diagnostics, d => d.Code == SvgDiagnosticCode.LimitExceeded && d.Message.Contains("MaxImagePixels"));
    }

    [Fact]
    public void Image_inside_clip_path_is_ignored_and_clip_path_on_image_applies()
    {
        var doc = Doc($"""
            <defs><clipPath id="c"><rect width="5" height="10"/><image href="{PngDataUri()}" width="10" height="10"/></clipPath></defs>
            <image href="{PngDataUri()}" width="10" height="10" clip-path="url(#c)" image-rendering="pixelated"/>
            """, new PngResolver());
        Assert.Contains(doc.Diagnostics, d => d.Message.Contains("inside <clipPath>"));
        Assert.Equal(255, Probe(doc, 20, 20).A);
        Assert.Equal(0, Probe(doc, 80, 20).A);
    }

    [Fact]
    public void Huge_base64_payload_decodes_in_linear_time()
    {
        var sb = new StringBuilder("data:image/png;base64,");
        sb.Append('A', 8 * 1024 * 1024);
        var resolver = new PngResolver();
        var sw = Stopwatch.StartNew();
        var doc = Doc($"<image href=\"{sb}\" width=\"5\" height=\"5\"/>", new ThrowingResolver());
        Assert.True(sw.Elapsed.TotalSeconds < 30, $"took {sw.Elapsed}");
        Assert.Empty(ImageCommands(doc));
    }
}
