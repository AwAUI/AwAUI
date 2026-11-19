using System;
using System.IO;
using AwAUI.Graphics;
using AwAUI.Imaging.Gif;

namespace AwAUI.Imaging;

/// <summary>
/// 纯 C# 的 GIF 解码（GIF87a / GIF89a）：全局与局部颜色表、透明色、隔行扫描、动画（处置方式、延迟、
/// NETSCAPE 循环次数）。合成语义与浏览器一致：画布初始全透明，处置方式 2 清为透明。
/// 损坏的输入只抛出 <see cref="InvalidDataException"/> 或 <see cref="NotSupportedException"/>；
/// 帧数据被截断时保留已解出的像素（与浏览器一致），其余部分保持透明或上一帧的内容。
/// </summary>
public static class GifCodec
{
    /// <summary>以 "GIF87a" / "GIF89a" 识别（需要 6 字节）。</summary>
    public static bool CanDecode(ReadOnlySpan<byte> header) => GifParser.HasSignature(header);

    /// <summary>解码第一帧（整张画布，预乘 RGBA）。</summary>
    public static Bitmap Decode(byte[] data) => DecodeAnimation(data).RenderFrame(0);

    /// <summary>解析为动画。容器结构在此完整校验，帧像素在合成时解码（结果保留对 <paramref name="data"/> 的引用，之后不要修改它）。</summary>
    public static AnimatedImage DecodeAnimation(byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        try
        {
            return GifParser.Parse(data);
        }
        catch (Exception e) when (DecodeGuard.IsInternalFailure(e))
        {
            throw DecodeGuard.Wrap("GIF", e);
        }
    }

    public static Bitmap Load(string path) => Decode(File.ReadAllBytes(path));
}
