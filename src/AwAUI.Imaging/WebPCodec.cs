using System;
using System.IO;
using AwAUI.Graphics;
using AwAUI.Imaging.WebP;

namespace AwAUI.Imaging;

/// <summary>不解码像素即可得到的 WebP 信息。</summary>
public sealed class WebPInfo
{
    internal WebPInfo(int width, int height, bool hasAlpha, bool isAnimated, bool isLossless, int frameCount, int loopCount)
    {
        Width = width;
        Height = height;
        HasAlpha = hasAlpha;
        IsAnimated = isAnimated;
        IsLossless = isLossless;
        FrameCount = frameCount;
        LoopCount = loopCount;
    }

    /// <summary>画布宽度。</summary>
    public int Width { get; }
    public int Height { get; }
    /// <summary>静态图：是否有 ALPH 块或 VP8L 的 alpha 提示位；动画：任意一帧有 alpha。</summary>
    public bool HasAlpha { get; }
    public bool IsAnimated { get; }
    /// <summary>静态图是否为 VP8L；动画为所有帧都是 VP8L。</summary>
    public bool IsLossless { get; }
    public int FrameCount { get; }
    /// <summary>ANIM 块的循环次数（0 = 无限）；静态图为 1。</summary>
    public int LoopCount { get; }
}

/// <summary>
/// 纯 C# 的 WebP 解码：无损（VP8L）、有损（VP8 关键帧，含 ALPH 透明通道的原始 / 无损压缩与三种预测滤波）、
/// 扩展容器（VP8X，跳过 ICC / EXIF / XMP）与动画（ANIM / ANMF）。输出与 libwebp 逐位一致：有损图使用 libwebp
/// 默认的 fancy 色度上采样，动画合成复刻 WebPAnimDecoder 的关键帧与非预乘混合规则。
/// 损坏的输入只抛出 <see cref="InvalidDataException"/> 或 <see cref="NotSupportedException"/>；
/// 分配量与耗时受输入大小约束（见 <see cref="DecodeLimits"/>）。
/// </summary>
public static class WebPCodec
{
    /// <summary>以 "RIFF" ???? "WEBP" 识别（需要 12 字节）。</summary>
    public static bool CanDecode(ReadOnlySpan<byte> header) => WebPContainer.HasSignature(header);

    static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception e) when (DecodeGuard.IsInternalFailure(e))
        {
            throw DecodeGuard.Wrap("WebP", e);
        }
    }

    /// <summary>只解析容器与位流头部。</summary>
    public static WebPInfo ReadInfo(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        var file = Guard(() => WebPContainer.Parse(data));
        if (file.Still is { } s)
            return new WebPInfo(file.CanvasWidth, file.CanvasHeight, s.HasAlpha, false, s.IsLossless, 1, 1);
        bool alpha = false, lossless = true;
        foreach (var f in file.Frames)
        {
            alpha |= f.Image.HasAlpha;
            lossless &= f.Image.IsLossless;
        }
        return new WebPInfo(file.CanvasWidth, file.CanvasHeight, alpha, true, lossless, file.Frames.Count, file.LoopCount);
    }

    /// <summary>解码为预乘 RGBA 位图；动画返回合成后的第一帧（整张画布）。</summary>
    public static Bitmap Decode(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        var file = Guard(() => WebPContainer.Parse(data));
        if (file.IsAnimated) return CreateAnimation(data, file).RenderFrame(0);
        var rgba = Guard(() => DecodeStill(data, file));
        return Bitmap.FromRgbaUnpremultiplied(rgba, new PixelSize(file.CanvasWidth, file.CanvasHeight));
    }

    /// <summary>解析为动画（静态图为单帧）。容器结构在此校验，帧像素在合成时解码（动画保留对 <paramref name="data"/> 的引用，之后不要修改它）。</summary>
    public static AnimatedImage DecodeAnimation(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        var file = Guard(() => WebPContainer.Parse(data));
        if (!file.IsAnimated) return AnimatedImage.FromRgba(Guard(() => DecodeStill(data, file)), new PixelSize(file.CanvasWidth, file.CanvasHeight));
        return CreateAnimation(data, file);
    }

    /// <summary>解码为非预乘 RGBA（动画为第一帧）。</summary>
    internal static byte[] DecodeRgba(byte[] data, out int width, out int height)
    {
        var file = Guard(() => WebPContainer.Parse(data));
        width = file.CanvasWidth;
        height = file.CanvasHeight;
        return file.IsAnimated ? CreateAnimation(data, file).RenderFrameRgba(0) : Guard(() => DecodeStill(data, file));
    }

    static byte[] DecodeStill(byte[] data, WebPFile file)
    {
        var decoded = WebPContainer.DecodeImage(data, file.Still!);
        var rgba = new byte[checked(file.CanvasWidth * file.CanvasHeight * 4)];
        WebPContainer.WriteRgba(decoded, file.CanvasWidth, file.CanvasHeight, rgba, 0, file.CanvasWidth * 4);
        return rgba;
    }

    static AnimatedImage CreateAnimation(byte[] data, WebPFile file)
    {
        long work = (long)file.CanvasWidth * file.CanvasHeight;
        var frames = new AnimationFrame[file.Frames.Count];
        for (int i = 0; i < frames.Length; i++)
        {
            var f = file.Frames[i];
            work += 2L * f.Image.Width * f.Image.Height;
            frames[i] = new AnimationFrame(i, f.Rect, TimeSpan.FromMilliseconds(f.Duration),
                f.DisposeToBackground ? FrameDisposal.Background : FrameDisposal.None,
                f.Blend ? FrameBlending.Over : FrameBlending.Source);
        }
        DecodeLimits.CheckBudget(work, data.Length, "WebP");
        uint bg = file.BackgroundBgra;
        var color = Color.FromRgba((byte)(bg >> 16), (byte)(bg >> 8), (byte)bg, (byte)(bg >> 24));
        return new AnimatedImage(new PixelSize(file.CanvasWidth, file.CanvasHeight), file.LoopCount, frames, new WebPCompositor(data, file), color);
    }

    public static Bitmap Load(string path) => Decode(File.ReadAllBytes(path));
}
