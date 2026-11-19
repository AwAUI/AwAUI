using AwAUI.Graphics;
using AwAUI.Imaging;
using AwAUI.Render;
using AwAUI.Render.Gpu;
using AwAUI.RenderTests;
using AwAUI.Rhi.Metal;

namespace AwAUI.Rhi.Metal.Tests;

/// <summary>需要 Metal 的测试：在非 macOS 或没有 Metal 设备的机器上以明确的原因跳过。</summary>
public sealed class MetalFactAttribute : FactAttribute
{
    public MetalFactAttribute()
    {
        if (MetalTestSupport.SkipReason is { } reason) Skip = reason;
    }
}

/// <inheritdoc cref="MetalFactAttribute"/>
public sealed class MetalTheoryAttribute : TheoryAttribute
{
    public MetalTheoryAttribute()
    {
        if (MetalTestSupport.SkipReason is { } reason) Skip = reason;
    }
}

public static class MetalTestSupport
{
    /// <summary>为 null 表示可以运行 Metal 测试。</summary>
    public static string? SkipReason { get; } =
        !OperatingSystem.IsMacOS() ? "Metal tests run only on macOS."
        : !MetalDevice.IsSupported ? "No Metal device is available on this Mac."
        : null;

    static readonly Lazy<GpuRenderBackend> s_backend = new(() => new GpuRenderBackend(MetalDevice.Create()));

    /// <summary>测试进程共享的 GPU 后端（xunit 默认按测试类并行，后端不是线程安全的，用 <see cref="Lock"/> 串行化）。</summary>
    public static GpuRenderBackend Backend => s_backend.Value;

    public static readonly object Lock = new();

    public static string OutputDir => System.IO.Path.Combine(Golden.OutputDir, "Gpu");

    /// <summary>用 GPU 后端渲染并回读为 RGBA8（预乘）位图。</summary>
    public static Bitmap RenderGpu(DisplayList list, int width, int height, double scale = 1, Color? clear = null)
    {
        lock (Lock)
        {
            var size = new PixelSize((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale));
            using var target = Backend.CreateOffscreenTarget(size, scale);
            target.Render(list, new RenderOptions(clear ?? Color.White));
            var bmp = new Bitmap(size);
            unsafe
            {
                var span = new Span<byte>((void*)bmp.Address, bmp.RowBytes * size.Height);
                target.ReadPixels(new PixelRect(0, 0, size.Width, size.Height), span, PixelFormat.Rgba8888);
            }
            return bmp;
        }
    }

    public static Bitmap RenderSoftware(DisplayList list, int width, int height, double scale = 1, Color? clear = null) =>
        Golden.Render(list, width, height, scale, clear);

    public static void Save(Bitmap bmp, string name)
    {
        Directory.CreateDirectory(OutputDir);
        PngCodec.Save(bmp, System.IO.Path.Combine(OutputDir, name + ".png"));
    }
}

/// <summary>
/// GPU 与软件输出的比对结果。比较的是预乘的 RGBA8 字节，逐像素取四个通道差的最大值。
/// </summary>
public sealed class ImageDiff
{
    public int Width { get; private init; }
    public int Height { get; private init; }
    public int MaxDiff { get; private init; }
    /// <summary>通道差大于 2 的像素数（「有差别」，排除 GPU 与 CPU 的舍入差）。</summary>
    public int Over2 { get; private init; }
    /// <summary>通道差大于 8 的像素数（超出抗锯齿边缘的容差）。</summary>
    public int Over8 { get; private init; }
    /// <summary>通道差大于 24 的像素数（明显不同）。</summary>
    public int Over24 { get; private init; }
    public double MeanDiff { get; private init; }
    public byte[] DiffImage { get; private init; } = Array.Empty<byte>();

    public int Pixels => Width * Height;
    public double Over2Percent => 100.0 * Over2 / Pixels;
    public double Over8Percent => 100.0 * Over8 / Pixels;
    public double Over24Percent => 100.0 * Over24 / Pixels;

    public static ImageDiff Compare(Bitmap expected, Bitmap actual)
    {
        Assert.Equal(expected.Size, actual.Size);
        int w = expected.Width, h = expected.Height;
        var diff = new byte[w * h * 4];
        int max = 0, over2 = 0, over8 = 0, over24 = 0;
        long sum = 0;
        for (int y = 0; y < h; y++)
        {
            var e = expected.GetRow(y);
            var a = actual.GetRow(y);
            for (int x = 0; x < w; x++)
            {
                int d = 0;
                for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(e[x * 4 + c] - a[x * 4 + c]));
                max = Math.Max(max, d);
                sum += d;
                if (d > 2) over2++;
                if (d > 8) over8++;
                if (d > 24) over24++;
                int o = (y * w + x) * 4;
                // 差异图：红色强度 = 差值 × 8；无差别处显示淡化的期望图像，便于定位
                if (d > 0) { diff[o] = (byte)Math.Min(255, 64 + d * 8); diff[o + 1] = 0; diff[o + 2] = 0; }
                else { diff[o] = (byte)(e[x * 4] / 4 + 160); diff[o + 1] = (byte)(e[x * 4 + 1] / 4 + 160); diff[o + 2] = (byte)(e[x * 4 + 2] / 4 + 160); }
                diff[o + 3] = 255;
            }
        }
        return new ImageDiff
        {
            Width = w, Height = h, MaxDiff = max, Over2 = over2, Over8 = over8, Over24 = over24,
            MeanDiff = (double)sum / (w * h), DiffImage = diff,
        };
    }

    public void SaveDiff(string name)
    {
        Directory.CreateDirectory(MetalTestSupport.OutputDir);
        using var bmp = Bitmap.FromRgbaUnpremultiplied(DiffImage, new PixelSize(Width, Height));
        PngCodec.Save(bmp, System.IO.Path.Combine(MetalTestSupport.OutputDir, name + ".diff.png"));
    }

    public override string ToString() =>
        $"max {MaxDiff}, mean {MeanDiff:F3}, >2: {Over2} ({Over2Percent:F3}%), >8: {Over8} ({Over8Percent:F3}%), >24: {Over24}";
}
