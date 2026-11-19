using System;
using System.IO;
using AwAUI.Graphics;
using AwAUI.Imaging;
using AwAUI.Svg;

namespace AwAUI.UI;

/// <summary>
/// 用 <c>AwAUI.Imaging</c> 解码 SVG <c>&lt;image&gt;</c> 的 <c>data:</c> URI（PNG、JPEG、GIF / WebP 的第一帧、BMP、ICO）。
/// 解码的耗时与内存受 Imaging 的解码上限约束（与载荷大小成比例），单张位图不超过 <see cref="MaxPixelsPerImage"/>，解出的像素总数另受 <see cref="SvgParseOptions.MaxImagePixels"/> 约束；
/// 损坏或不支持的数据返回 null（文档记录诊断，图片不画）。不加载外部引用（与浏览器把 SVG 当作图片时相同）。无状态，可在任意线程上使用。
/// <para>
/// <see cref="ImageCache"/> 在 <see cref="ImageCache.SvgOptions"/> 为 null 时用 <see cref="DefaultOptions"/> 解析作为图片的 SVG；
/// 自行解析 SVG 的代码可以把 <see cref="Instance"/> 设为 <see cref="SvgParseOptions.ImageResolver"/>。
/// </para>
/// </summary>
public sealed class ImagingSvgImageResolver : SvgImageResolver
{
    ImagingSvgImageResolver() { }

    /// <summary>共享实例。</summary>
    public static ImagingSvgImageResolver Instance { get; } = new();

    /// <summary>
    /// 单张嵌入位图的像素上限（与 <see cref="SvgParseOptions.MaxImagePixels"/> 的默认值相同，64M 像素）：头部声明更大的图片
    /// 在分配像素之前被拒绝（返回 null）。
    /// </summary>
    public const long MaxPixelsPerImage = 64L * 1024 * 1024;

    /// <summary>默认上限 + 本解析器。</summary>
    public static SvgParseOptions DefaultOptions { get; } = new() { ImageResolver = Instance };

    public override Bitmap? Decode(ReadOnlySpan<byte> data, string mediaType)
    {
        if (ImageDecoder.DetectFormat(data) == ImageFormat.Unknown) return null;
        try
        {
            // 头部声明的尺寸超过单张上限时在分配之前拒绝（文档的 MaxImagePixels 只能在解码之后统计）
            return ImageDecoder.Decode(data.ToArray(), MaxPixelsPerImage);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OutOfMemoryException)
        {
            return null;
        }
    }
}
