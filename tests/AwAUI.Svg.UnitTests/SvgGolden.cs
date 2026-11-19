using AwAUI.Graphics;
using AwAUI.Imaging;
using AwAUI.Render;

namespace AwAUI.Svg.UnitTests;

/// <summary>
/// SVG 黄金图像：源文件与期望图片都在 tests/TestFiles/Svg；失败时实际输出与差异图写入 tests/TestFiles/Output/Svg。
/// 设置环境变量 AWAUI_UPDATE_GOLDEN=1 重新生成期望图片（须人工审核后再提交）。
/// </summary>
public static class SvgGolden
{
    public static string RepoRoot { get; } = FindRoot();

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public static string SvgDir => System.IO.Path.Combine(RepoRoot, "tests", "TestFiles", "Svg");
    public static string OutputDir => System.IO.Path.Combine(RepoRoot, "tests", "TestFiles", "Output", "Svg");

    public static Bitmap Render(DisplayList list, int width, int height, Color? clear = null)
    {
        using var backend = new SoftwareRenderBackend();
        var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(new PixelSize(width, height), 1);
        target.Render(list, new RenderOptions(clear ?? Color.White));
        var copy = new Bitmap(target.PixelSize);
        target.Bitmap.CopyTo(copy);
        target.Dispose();
        return copy;
    }

    /// <summary>把文档绘制到 (0,0,width,height) 并渲染。</summary>
    public static Bitmap Render(SvgDocument doc, int width, int height, SvgRenderOptions? options = null, Color? clear = null) =>
        Render(doc.ToDisplayList(new Rect(0, 0, width, height), options), width, height, clear);

    /// <param name="tolerance">单个通道允许的最大差值。</param>
    public static void AssertMatches(string name, Bitmap actual, int tolerance = 2)
    {
        var goldenPath = System.IO.Path.Combine(SvgDir, name + ".png");
        if (Environment.GetEnvironmentVariable("AWAUI_UPDATE_GOLDEN") == "1")
        {
            PngCodec.Save(actual, goldenPath);
            return;
        }
        Directory.CreateDirectory(OutputDir);
        if (!File.Exists(goldenPath))
        {
            PngCodec.Save(actual, System.IO.Path.Combine(OutputDir, name + ".actual.png"));
            Assert.Fail($"Golden image '{name}.png' is missing. Review the output and run with AWAUI_UPDATE_GOLDEN=1.");
        }

        using var expected = PngCodec.Load(goldenPath);
        Assert.Equal(expected.Size, actual.Size);
        var e = expected.ToRgbaUnpremultiplied();
        var a = actual.ToRgbaUnpremultiplied();
        int bad = 0, maxDiff = 0;
        var diff = new byte[e.Length];
        for (int i = 0; i < e.Length; i += 4)
        {
            int d = 0;
            for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(e[i + c] - a[i + c]));
            maxDiff = Math.Max(maxDiff, d);
            if (d > tolerance) bad++;
            diff[i] = (byte)Math.Min(255, d * 8);
            diff[i + 3] = 255;
        }
        if (bad > 0)
        {
            PngCodec.Save(actual, System.IO.Path.Combine(OutputDir, name + ".actual.png"));
            using var diffBmp = Bitmap.FromRgbaUnpremultiplied(diff, actual.Size);
            PngCodec.Save(diffBmp, System.IO.Path.Combine(OutputDir, name + ".diff.png"));
            Assert.Fail($"'{name}': {bad} pixels differ (max channel diff {maxDiff}, tolerance {tolerance}). See {OutputDir}.");
        }
    }

    /// <summary>读一个像素（非预乘 RGBA 字节）。</summary>
    public static (byte R, byte G, byte B, byte A) Pixel(Bitmap b, int x, int y)
    {
        var c = b.GetPixel(x, y);
        static byte B8(float v) => (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
        return (B8(c.R), B8(c.G), B8(c.B), B8(c.A));
    }
}
