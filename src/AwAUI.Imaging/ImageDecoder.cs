using System;
using System.IO;
using AwAUI.Graphics;

namespace AwAUI.Imaging;

/// <summary>按文件签名识别出的图片格式。</summary>
public enum ImageFormat
{
    Unknown,
    Png,
    Jpeg,
    Gif,
    WebP,
    Bmp,
    /// <summary>ICO 或 CUR。</summary>
    Ico,
}

/// <summary>
/// 按文件签名选择解码器的统一入口。结果均为预乘 RGBA 位图；动画格式（GIF、WebP）由 <see cref="Decode"/>
/// 返回合成后的第一帧，完整动画用 <see cref="DecodeAnimation"/>。
/// </summary>
public static class ImageDecoder
{
    /// <summary>
    /// 根据文件开头的字节判断格式。所需字节数：PNG 8、JPEG 3、GIF 6、WebP 12、ICO/CUR 6、BMP 2
    /// （BMP 只看 "BM"，给出至少 18 字节时还检查信息头大小）。
    /// </summary>
    public static ImageFormat DetectFormat(ReadOnlySpan<byte> header)
    {
        if (PngCodec.CanDecode(header)) return ImageFormat.Png;
        if (JpegCodec.CanDecode(header)) return ImageFormat.Jpeg;
        if (GifCodec.CanDecode(header)) return ImageFormat.Gif;
        if (WebPCodec.CanDecode(header)) return ImageFormat.WebP;
        if (IcoCodec.CanDecode(header)) return ImageFormat.Ico;
        if (BmpCodec.CanDecode(header)) return ImageFormat.Bmp;
        return ImageFormat.Unknown;
    }

    public static bool CanDecode(ReadOnlySpan<byte> header) => DetectFormat(header) != ImageFormat.Unknown;

    /// <summary>
    /// 按签名选择解码器。PngCodec 在部分损坏数据上会抛出 <see cref="OverflowException"/>、
    /// <see cref="IndexOutOfRangeException"/> 或 <see cref="ArgumentException"/>，这里统一转换为
    /// <see cref="InvalidDataException"/>（原异常保留在 InnerException）。其余解码器只抛出下列两种异常。
    /// </summary>
    /// <remarks>
    /// 头部声明的尺寸在分配之前检查：整张 RGBA 不超过 2 GB，且相对于文件大小不过大（每个输入字节至多 4096 像素，另加 400 万像素保底；
    /// PNG 还受 DEFLATE 压缩比约束）。合法但很大的图片（例如 1 MB 的 PNG 解压为上亿像素）仍会按实际尺寸分配；
    /// 不可信来源请用 <see cref="Decode(byte[], long)"/> 给出像素上限。
    /// </remarks>
    /// <exception cref="NotSupportedException">无法识别的格式，格式中不支持的特性，或相对于文件大小声明的尺寸过大。</exception>
    /// <exception cref="InvalidDataException">数据截断或损坏。</exception>
    public static Bitmap Decode(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        return DecodeCore(data);
    }

    /// <summary>
    /// 同 <see cref="Decode(byte[])"/>，但头部声明的像素数（宽 × 高；GIF / WebP 为画布）超过 <paramref name="maxPixels"/> 时
    /// 在分配任何像素缓冲之前抛出 <see cref="NotSupportedException"/>。ICO 按所选条目的尺寸检查。
    /// </summary>
    /// <param name="maxPixels">像素上限（大于 0）。</param>
    public static Bitmap Decode(byte[] data, long maxPixels)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (maxPixels <= 0) throw new ArgumentOutOfRangeException(nameof(maxPixels));
        long previous = DecodeLimits.SetCallerMaxPixels(maxPixels);
        try
        {
            return DecodeCore(data);
        }
        finally
        {
            DecodeLimits.RestoreCallerMaxPixels(previous);
        }
    }

    static Bitmap DecodeCore(byte[] data)
    {
        switch (DetectFormat(data))
        {
            case ImageFormat.Png:
                try
                {
                    return PngCodec.Decode(data);
                }
                catch (Exception ex) when (ex is OverflowException or IndexOutOfRangeException or ArgumentException)
                {
                    throw new InvalidDataException("PNG data is corrupt.", ex);
                }
            case ImageFormat.Jpeg:
                return JpegCodec.Decode(data);
            case ImageFormat.Gif:
                return GifCodec.Decode(data);
            case ImageFormat.WebP:
                return WebPCodec.Decode(data);
            case ImageFormat.Bmp:
                return BmpCodec.Decode(data);
            case ImageFormat.Ico:
                return IcoCodec.Decode(data);
            default:
                throw new NotSupportedException("Unrecognized image format.");
        }
    }

    /// <summary>
    /// 解码为动画：GIF 与 WebP 返回全部帧（按需合成），其余格式返回单帧动画。
    /// 异常与 <see cref="Decode"/> 相同；动画帧的像素在 <see cref="AnimatedImage.RenderFrame(int)"/> 时才解码，
    /// 因此 GIF / WebP 动画保留对 <paramref name="data"/> 的引用，之后不要修改该数组。
    /// </summary>
    public static AnimatedImage DecodeAnimation(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        switch (DetectFormat(data))
        {
            case ImageFormat.Gif:
                return GifCodec.DecodeAnimation(data);
            case ImageFormat.WebP:
                return WebPCodec.DecodeAnimation(data);
            default:
                using (var bitmap = Decode(data)) return AnimatedImage.FromStill(bitmap);
        }
    }

    public static Bitmap Load(string path) => Decode(File.ReadAllBytes(path));

    public static AnimatedImage LoadAnimation(string path) => DecodeAnimation(File.ReadAllBytes(path));
}
