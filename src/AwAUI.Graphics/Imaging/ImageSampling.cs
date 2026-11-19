namespace AwAUI.Graphics;

/// <summary>
/// <see cref="DrawingContext.DrawImage(Bitmap, Rect, Rect, ImageSampling, float)"/> 的采样方式（design/13，操作码 34）。
/// 数值是 DisplayList 二进制格式的一部分，发布后不可改变。
/// </summary>
public enum ImageSampling : byte
{
    /// <summary>
    /// 最近纹素：放大时保持像素块（CSS <c>image-rendering: pixelated / crisp-edges</c>）。缩小时不做预滤波
    /// （每个设备像素取一个纹素，细节会闪烁 / 走样，这是像素风格内容期望的行为）。
    /// </summary>
    Nearest = 0,

    /// <summary>
    /// 双线性插值（预乘颜色）。缩小超过约 √2 倍时先选用盒式预滤波的 mip 级别：级别 L = round(log2 ρ)，ρ 为每个设备像素对应的纹素数
    /// （取两个方向中较大的），第 L 级的每个纹素是原图 2^L × 2^L 块的平均值，再在该级别上做双线性。所以任意倍数的缩小都不会明显走样
    /// （没有级间插值：缩放连续变化时在 √2 的倍数处会有轻微的锐度跳变）。
    /// </summary>
    Linear = 1,
}
