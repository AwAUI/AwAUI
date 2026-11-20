using System;
using AwAUI.Graphics;
using AwAUI.MacOS;
using AwAUI.Render;
using AwAUI.Rhi.Metal;

namespace AwAUI.Samples;

/// <summary>
/// 窗口呈现的自检（示例共用，Counter 以链接方式引用），两种比较都以软件后端离屏渲染同一个 DisplayList 的结果为参照：
/// <list type="bullet">
/// <item><see cref="CompareWithSoftware"/>：读回最近一次呈现到 CAMetalLayer 的可绘制纹理（<see cref="MetalRenderBackend.CapturePresentedFrames"/>）。</item>
/// <item><see cref="CompareWindow"/>：截取窗口服务器合成后的窗口内容（<see cref="MacWindow.CaptureContent"/>），即屏幕上实际显示的像素；
/// 经过显示器色彩空间往返，只比较两张图中都完全不透明的像素（参照图透明处显示的是窗口背景，截图透明处是窗口圆角）。</item>
/// </list>
/// </summary>
static class FrameCheck
{
    /// <summary>比较结果：通道差的最大值，以及差值大于 2 / 8 / 24 的像素占比（%）。</summary>
    public readonly struct Result
    {
        public Result(PixelSize size, int maxDiff, double over2, double over8, double over24)
        {
            Size = size;
            MaxDiff = maxDiff;
            Over2Percent = over2;
            Over8Percent = over8;
            Over24Percent = over24;
        }

        public PixelSize Size { get; }
        public int MaxDiff { get; }
        public double Over2Percent { get; }
        public double Over8Percent { get; }
        public double Over24Percent { get; }

        /// <summary>与 GPU 一致性测试的默认容差相同：&gt;8 的像素 ≤ 0.3%，&gt;24 的 ≤ 0.02%，&gt;2 的 ≤ 3%，最大差 ≤ 64。</summary>
        public bool WithinParityTolerance => MaxDiff <= 64 && Over8Percent <= 0.3 && Over24Percent <= 0.02 && Over2Percent <= 3;

        public override string ToString() => FormattableString.Invariant(
            $"{Size.Width}x{Size.Height} px: max diff {MaxDiff}/255, >2: {Over2Percent:F3}%, >8: {Over8Percent:F3}%, >24: {Over24Percent:F4}% — {(WithinParityTolerance ? "within" : "OUTSIDE")} GPU parity tolerance");
    }

    /// <summary>
    /// 读回 <paramref name="backend"/> 最近呈现到窗口的帧，与软件后端渲染 <paramref name="frame"/> 的结果比较。
    /// 没有捕获到帧（未开启捕获、已回退到软件）时返回 null。
    /// </summary>
    public static Result? CompareWithSoftware(MetalRenderBackend backend, DisplayList frame, double scale, Color clear)
    {
        using var presented = backend.ReadPresentedFrame();
        if (presented is null) return null;
        using var software = new SoftwareRenderBackend();
        using var target = (BitmapRenderTarget)software.CreateOffscreenTarget(presented.Size, scale);
        target.Render(frame, new RenderOptions(clear));
        return Compare(target.Bitmap, presented);
    }

    /// <summary>截取窗口内容并与软件渲染 <paramref name="frame"/> 比较；截取失败时返回 null。</summary>
    public static Result? CompareWindow(MacWindow window, DisplayList frame, double scale, Color clear)
    {
        using var captured = window.CaptureContent();
        if (captured is null) return null;
        using var software = new SoftwareRenderBackend();
        using var target = (BitmapRenderTarget)software.CreateOffscreenTarget(captured.Size, scale);
        target.Render(frame, new RenderOptions(clear));
        Save(captured, "window-" + window.PresentationMode);
        Save(target.Bitmap, "software");
        // 窗口装饰盖住的像素不比较：内容区第一行（标题栏的分隔线，1 个逻辑像素）与底部圆角（截图中透明）
        return Compare(target.Bitmap, captured, opaqueOnly: true, skipTopRows: (int)Math.Ceiling(scale));
    }

    /// <summary>设置环境变量 AWAUI_SELF_CHECK_DIR 时把参与比较的图像写成 PNG（人工检查用）。</summary>
    static void Save(Bitmap bitmap, string name)
    {
        var dir = Environment.GetEnvironmentVariable("AWAUI_SELF_CHECK_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        System.IO.Directory.CreateDirectory(dir);
        AwAUI.Imaging.PngCodec.Save(bitmap, System.IO.Path.Combine(dir, name + ".png"));
    }

    /// <param name="opaqueOnly">只比较两张图中 alpha 都是 255 的像素（占比按这些像素计）。</param>
    /// <param name="skipTopRows">跳过顶部的行数。</param>
    public static Result Compare(Bitmap expected, Bitmap actual, bool opaqueOnly = false, int skipTopRows = 0)
    {
        if (expected.Size != actual.Size) throw new ArgumentException($"Size mismatch: {expected.Size} vs {actual.Size}.");
        int w = expected.Width, h = expected.Height;
        int max = 0;
        long over2 = 0, over8 = 0, over24 = 0, counted = 0;
        for (int y = skipTopRows; y < h; y++)
        {
            var e = expected.GetRow(y);
            var a = actual.GetRow(y);
            for (int i = 0; i < w * 4; i += 4)
            {
                if (opaqueOnly && (e[i + 3] != 255 || a[i + 3] != 255)) continue;
                counted++;
                int d = 0;
                for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(e[i + c] - a[i + c]));
                if (d > max) max = d;
                if (d > 2) over2++;
                if (d > 8) over8++;
                if (d > 24) over24++;
            }
        }
        double n = Math.Max(1, (double)counted) / 100;
        return new Result(expected.Size, max, over2 / n, over8 / n, over24 / n);
    }
}
