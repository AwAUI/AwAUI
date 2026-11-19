using System;
using System.IO;
using AwAUI.Graphics;
using AwAUI.Imaging.Jpeg;

namespace AwAUI.Imaging;

/// <summary>JPEG 文件中图像数据所用的颜色空间。</summary>
public enum JpegColorSpace
{
    /// <summary>无法识别（如 2 个分量）。</summary>
    Unknown,
    /// <summary>单通道灰度。</summary>
    Grayscale,
    /// <summary>YCbCr（JFIF 默认）。</summary>
    YCbCr,
    /// <summary>直接存储 RGB（Adobe transform=0 或分量标识为 'R','G','B'）。</summary>
    Rgb,
    /// <summary>CMYK（按 Adobe 惯例反相存储）。</summary>
    Cmyk,
    /// <summary>YCCK（Adobe transform=2）。</summary>
    Ycck,
}

/// <summary>
/// 不解码像素即可得到的 JPEG 信息。<see cref="Orientation"/> 来自 APP1 Exif 的 Orientation 标签，
/// 解码器不会据此旋转像素，由调用方决定如何显示。
/// </summary>
public sealed class JpegInfo
{
    internal JpegInfo(int width, int height, int componentCount, int bitsPerSample, bool isProgressive,
        bool isArithmeticCoded, bool isLossless, JpegColorSpace colorSpace, int orientation, bool headerSupported)
    {
        _headerSupported = headerSupported;
        Width = width;
        Height = height;
        ComponentCount = componentCount;
        BitsPerSample = bitsPerSample;
        IsProgressive = isProgressive;
        IsArithmeticCoded = isArithmeticCoded;
        IsLossless = isLossless;
        ColorSpace = colorSpace;
        Orientation = orientation;
    }

    public int Width { get; }
    public int Height { get; }
    /// <summary>帧中的分量数（1 = 灰度，3 = YCbCr/RGB，4 = CMYK/YCCK）。</summary>
    public int ComponentCount { get; }
    /// <summary>采样精度（位）。解码器只支持 8。</summary>
    public int BitsPerSample { get; }
    public bool IsProgressive { get; }
    /// <summary>是否为算术编码（解码器不支持）。</summary>
    public bool IsArithmeticCoded { get; }
    /// <summary>是否为无损 JPEG（解码器不支持）。</summary>
    public bool IsLossless { get; }
    public JpegColorSpace ColorSpace { get; }
    /// <summary>Exif 方向（1–8，与 TIFF 定义相同）；没有 Exif 或取值无效时为 1。</summary>
    public int Orientation { get; }

    /// <summary>Exif 方向是否要求交换宽高（5–8）后再显示。</summary>
    public bool OrientationSwapsDimensions => Orientation >= 5;

    readonly bool _headerSupported;

    /// <summary>
    /// 按头部判断当前解码器是否支持这张图片：排除算术编码、无损、分层、非 8 位、DNL（高度为 0）、
    /// 分量数不是 1/3/4、尺寸过大、非整数倍采样因子。为 true 时解码仍可能因数据截断或损坏而抛出
    /// <see cref="InvalidDataException"/>。
    /// </summary>
    public bool IsDecodable => _headerSupported && ColorSpace != JpegColorSpace.Unknown;
}

/// <summary>
/// 纯 C# 的 JPEG 解码：基线顺序与渐进式 DCT（频谱选择 + 逐次逼近）、8 位精度、Huffman 编码、
/// 重启间隔、任意采样因子（2 倍方向用与 libjpeg 一致的平滑上采样）、灰度 / YCbCr / RGB / CMYK / YCCK。
/// IDCT 与颜色转换逐位复刻 libjpeg 的 islow 实现。算术编码、12 位、无损、分层模式抛出
/// <see cref="NotSupportedException"/>；截断或损坏的数据抛出 <see cref="InvalidDataException"/>。
/// </summary>
public static class JpegCodec
{
    /// <summary>以 SOI 标记后紧跟另一个标记识别 JPEG。</summary>
    public static bool CanDecode(ReadOnlySpan<byte> header) =>
        header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF;

    /// <summary>只解析头部标记段，读取尺寸、分量、编码方式与 Exif 方向。</summary>
    public static JpegInfo ReadInfo(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        return new JpegDecoder(data).ReadInfo();
    }

    public static JpegInfo ReadInfo(string path) => ReadInfo(File.ReadAllBytes(path));

    /// <summary>解码为预乘 RGBA 位图（JPEG 不含透明度，alpha 恒为 255）。</summary>
    public static Bitmap Decode(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        return new JpegDecoder(data).Decode();
    }

    /// <summary>解码并同时返回头部信息。</summary>
    public static Bitmap Decode(byte[] data, out JpegInfo info)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        var decoder = new JpegDecoder(data);
        var bitmap = decoder.Decode();
        info = decoder.Info!;
        return bitmap;
    }

    public static Bitmap Load(string path) => Decode(File.ReadAllBytes(path));
}
