using System;
using System.IO;

namespace AwAUI.Imaging;

/// <summary>
/// 解码器共用的尺寸与工作量上限，防止不可信输入（很小的文件声明巨大尺寸）引起过量分配或耗时。
/// <para>
/// 规则：一次解码中「需要逐像素处理的像素总数」（画布、各帧区域、中间缓冲）不超过
/// <see cref="MinPixelBudget"/> + 输入字节数 × <see cref="PixelsPerInputByte"/>。GIF 的 LZW 理论上限约为
/// 每字节 2730 像素，VP8 关键帧每个宏块至少消耗约 1.7 位，均在此上限之内；VP8L（Huffman 码长可以为 0 位）
/// 与 BMP 的 RLE 跳转可以超出，超出时按「不支持」处理。
/// </para>
/// <para>
/// 头部声明的画布尺寸在分配任何与之成正比的缓冲之前检查（<see cref="CheckDimensions"/>，各格式都在读完头部后立即调用）：
/// 宽高为正、整张 RGBA 不超过 2 GB（约 5.4 亿像素），调用方经 <see cref="ImageDecoder.Decode(byte[], long)"/> 给出更小的像素上限时
/// 也在这里拒绝。与输入大小的关系：PNG 另受 DEFLATE 最大压缩比约束（解压数据 ≤ 1032 × IDAT 字节，每像素至少 1 字节，
/// 即每个输入字节至多约 1032 像素，低于本预算的 4096），JPEG 每个 8×8 块至少消耗 1–2 位（每字节至多 256–512 像素），
/// GIF、WebP、BMP 按 <see cref="CheckBudget"/> 检查。因此 1 KB 的文件最多声明约 800 万像素（32 MB），1 MB 的文件约 43 亿 ——
/// 后者再受 2 GB 的位图上限与调用方的像素上限约束。
/// </para>
/// </summary>
internal static class DecodeLimits
{
    /// <summary>每个输入字节允许的像素数。</summary>
    public const long PixelsPerInputByte = 4096;

    /// <summary>与输入大小无关的保底像素数（4M 像素，RGBA 约 16 MB）。</summary>
    public const long MinPixelBudget = 1L << 22;

    /// <summary>给定输入长度时的像素预算。</summary>
    public static long PixelBudget(int inputLength) => MinPixelBudget + inputLength * PixelsPerInputByte;

    /// <summary>整张 RGBA 位图不超过 2 GB（<see cref="AwAUI.Graphics.Bitmap"/> 的限制）时的最大像素数。</summary>
    public const long MaxPixels = int.MaxValue / 4;

    /// <summary>当前线程上调用方给出的像素上限（<see cref="ImageDecoder.Decode(byte[], long)"/>）；0 表示只用 <see cref="MaxPixels"/>。</summary>
    [ThreadStatic]
    static long t_callerMaxPixels;

    /// <summary>在当前线程上临时设置调用方的像素上限，返回之前的值（由 <see cref="RestoreCallerMaxPixels"/> 恢复）。</summary>
    public static long SetCallerMaxPixels(long maxPixels)
    {
        long previous = t_callerMaxPixels;
        t_callerMaxPixels = maxPixels;
        return previous;
    }

    public static void RestoreCallerMaxPixels(long previous) => t_callerMaxPixels = previous;

    /// <summary>
    /// 检查头部声明的尺寸：宽高为正，整张 RGBA 位图不超过 2 GB（<see cref="MaxPixels"/>），且不超过调用方给出的像素上限。
    /// 各解码器在读完头部、分配任何与尺寸成正比的缓冲之前调用。
    /// </summary>
    /// <exception cref="InvalidDataException">宽或高不为正。</exception>
    /// <exception cref="NotSupportedException">RGBA 数据超过 2 GB，或超过调用方的像素上限。</exception>
    public static void CheckDimensions(long width, long height, string format)
    {
        if (width <= 0 || height <= 0) throw new InvalidDataException($"{format} image has invalid dimensions {width}x{height}.");
        if (width * height > MaxPixels) throw new NotSupportedException($"{format} image of {width}x{height} pixels is too large.");
        long cap = t_callerMaxPixels;
        if (cap > 0 && width * height > cap)
            throw new NotSupportedException($"{format} image of {width}x{height} pixels exceeds the limit of {cap} pixels.");
    }

    /// <summary>检查像素数是否在预算之内。</summary>
    /// <exception cref="NotSupportedException">超出预算：相对于文件大小，声明的像素过多。</exception>
    public static void CheckBudget(long pixels, int inputLength, string format)
    {
        if (pixels > PixelBudget(inputLength))
            throw new NotSupportedException(
                $"{format} image needs {pixels} pixels of work, more than allowed for {inputLength} bytes of input.");
    }
}

/// <summary>
/// 最后一道防线：解码器内部若因未预料的损坏数据触发越界、溢出等异常，统一转换为
/// <see cref="InvalidDataException"/>（原异常保留在 InnerException，测试据此发现内部缺陷）。
/// </summary>
internal static class DecodeGuard
{
    public static bool IsInternalFailure(Exception e) => e is IndexOutOfRangeException or ArgumentException or OverflowException;

    public static InvalidDataException Wrap(string format, Exception e) => new($"{format} data is corrupt.", e);
}

