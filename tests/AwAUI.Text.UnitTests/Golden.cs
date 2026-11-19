using AwAUI.Graphics;
using AwAUI.Imaging;
using AwAUI.Render;

namespace AwAUI.Text.UnitTests;

/// <summary>
/// 黄金图像比对（复制自 tests/AwAUI.RenderTests/Golden.cs）。期望图片在 tests/TestFiles/Golden/Text；
/// 失败时实际输出与差异图写入 tests/TestFiles/Output/Text。设置 AWAUI_UPDATE_GOLDEN=1 重新生成（须人工审核后再提交）。
/// </summary>
public static class Golden
{
    public static string GoldenDir => System.IO.Path.Combine(TestFonts.RepoRoot, "tests", "TestFiles", "Golden", "Text");
    public static string OutputDir => System.IO.Path.Combine(TestFonts.RepoRoot, "tests", "TestFiles", "Output", "Text");

    public static Bitmap Render(DisplayList list, int width, int height, double scale = 1, Color? clear = null)
    {
        using var backend = new SoftwareRenderBackend();
        var target = (BitmapRenderTarget)backend.CreateOffscreenTarget(
            new PixelSize((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale)), scale);
        target.Render(list, new RenderOptions(clear ?? Color.White));
        var copy = new Bitmap(target.PixelSize);
        target.Bitmap.CopyTo(copy);
        target.Dispose();
        return copy;
    }

    /// <param name="tolerance">单个通道允许的最大差值。</param>
    public static void AssertMatches(string name, Bitmap actual, int tolerance = 2)
    {
        var goldenPath = System.IO.Path.Combine(GoldenDir, name + ".png");
        if (Environment.GetEnvironmentVariable("AWAUI_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(GoldenDir);
            PngCodec.Save(actual, goldenPath);
            return;
        }
        if (!File.Exists(goldenPath))
        {
            Directory.CreateDirectory(OutputDir);
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
            Directory.CreateDirectory(OutputDir);
            PngCodec.Save(actual, System.IO.Path.Combine(OutputDir, name + ".actual.png"));
            using var diffBmp = Bitmap.FromRgbaUnpremultiplied(diff, actual.Size);
            PngCodec.Save(diffBmp, System.IO.Path.Combine(OutputDir, name + ".diff.png"));
            Assert.Fail($"'{name}': {bad} pixels differ (max channel diff {maxDiff}, tolerance {tolerance}). See {OutputDir}.");
        }
    }
}
