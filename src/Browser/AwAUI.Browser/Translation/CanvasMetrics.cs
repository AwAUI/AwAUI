using System;

namespace AwAUI.Browser;

/// <summary>
/// canvas 的尺寸换算（design/04「DPI」）：逻辑尺寸 = CSS 内容盒，缩放 = devicePixelRatio，
/// 帧缓冲（canvas.width/height）= 设备像素内容盒。
/// <para>
/// 浏览器支持 ResizeObserver 的 device-pixel-content-box 时直接使用它（与合成器的像素网格精确对齐，
/// 避免 1px 误差导致整幅重采样变糊）；否则（Safari）按 CSS 尺寸 × devicePixelRatio 四舍五入。
/// 帧缓冲受 <see cref="MaxPixelDimension"/> 与 <see cref="MaxPixelArea"/> 限制：超出时等比降低有效缩放，
/// 画面变糊但不会耗尽 WASM 堆。
/// </para>
/// </summary>
internal readonly struct CanvasMetrics : IEquatable<CanvasMetrics>
{
    /// <summary>单边最大设备像素（桌面浏览器的 canvas 单边上限为 16384–32767）。</summary>
    public const int MaxPixelDimension = 16384;

    /// <summary>
    /// 最大设备像素面积：3200 万像素 ≈ 128 MB RGBA，足够 6K 屏全屏。iOS Safari 另有约 1677 万像素的 canvas 面积上限，
    /// 但手机屏幕远小于它。
    /// </summary>
    public const long MaxPixelArea = 32L * 1024 * 1024;

    public const double MinScale = 0.25, MaxScale = 8;

    /// <summary>逻辑尺寸上限（CSS 像素）。</summary>
    public const double MaxLogicalDimension = 1e6;

    public CanvasMetrics(Size clientSize, double scale, PixelSize pixelSize)
    {
        ClientSize = clientSize;
        Scale = scale;
        PixelSize = pixelSize;
    }

    /// <summary>逻辑（CSS）尺寸。</summary>
    public Size ClientSize { get; }

    /// <summary>逻辑 → 设备像素的缩放（通常等于 devicePixelRatio，帧缓冲受限时更小）。</summary>
    public double Scale { get; }

    /// <summary>帧缓冲尺寸；逻辑尺寸为 0 时为空。</summary>
    public PixelSize PixelSize { get; }

    /// <param name="cssWidth">ResizeObserver contentBoxSize（或 getBoundingClientRect）的宽度。</param>
    /// <param name="cssHeight">同上的高度。</param>
    /// <param name="devicePixelRatio">window.devicePixelRatio。</param>
    /// <param name="deviceWidth">devicePixelContentBoxSize 的宽度；浏览器不支持时传 NaN 或 0。</param>
    /// <param name="deviceHeight">devicePixelContentBoxSize 的高度。</param>
    public static CanvasMetrics Compute(double cssWidth, double cssHeight, double devicePixelRatio,
        double deviceWidth = double.NaN, double deviceHeight = double.NaN)
    {
        double scale = DomProtocol.Sanitize(devicePixelRatio, MinScale, MaxScale, 1);
        double w = DomProtocol.Sanitize(cssWidth, 0, MaxLogicalDimension, 0);
        double h = DomProtocol.Sanitize(cssHeight, 0, MaxLogicalDimension, 0);
        // 逻辑尺寸以 float 保存：小到转换后为 0 的值按 0 处理，保证「逻辑尺寸为空 ⇔ 帧缓冲为空」
        if ((float)w <= 0) w = 0;
        if ((float)h <= 0) h = 0;

        double pw, ph;
        // 设备像素盒只用于修正舍入误差：与 CSS × DPR 相差超过 2 像素（或 1%）的值视为不可信（例如脚本伪造）
        if (DomProtocol.IsFinite(deviceWidth) && DomProtocol.IsFinite(deviceHeight) && w > 0 && h > 0
            && Agrees(deviceWidth, w * scale) && Agrees(deviceHeight, h * scale))
        {
            pw = Math.Round(deviceWidth);
            ph = Math.Round(deviceHeight);
        }
        else
        {
            pw = Math.Round(w * scale);
            ph = Math.Round(h * scale);
        }
        if (w > 0 && pw < 1) pw = 1;
        if (h > 0 && ph < 1) ph = 1;
        if (w <= 0 || h <= 0) return new CanvasMetrics(new Size((float)w, (float)h), scale, new PixelSize(0, 0));

        // 超出上限：按同一比例缩小帧缓冲与有效缩放，保持逻辑 → 设备映射一致
        double factor = 1;
        if (pw > MaxPixelDimension) factor = Math.Min(factor, MaxPixelDimension / pw);
        if (ph > MaxPixelDimension) factor = Math.Min(factor, MaxPixelDimension / ph);
        if (pw * ph * factor * factor > MaxPixelArea) factor = Math.Min(factor, Math.Sqrt(MaxPixelArea / (pw * ph)));
        if (factor < 1)
        {
            pw = Math.Max(1, Math.Floor(pw * factor));
            ph = Math.Max(1, Math.Floor(ph * factor));
            scale *= factor;
        }
        return new CanvasMetrics(new Size((float)w, (float)h), scale, new PixelSize((int)pw, (int)ph));
    }

    static bool Agrees(double device, double expected) => device >= 1 && Math.Abs(device - expected) <= Math.Max(2, expected * 0.01);

    public bool Equals(CanvasMetrics other) =>
        ClientSize == other.ClientSize && Scale.Equals(other.Scale) && PixelSize == other.PixelSize;

    public override bool Equals(object? obj) => obj is CanvasMetrics m && Equals(m);
    public override int GetHashCode() => HashCodeCompat.Combine(ClientSize.GetHashCode(), Scale.GetHashCode(), PixelSize.GetHashCode());
    public static bool operator ==(CanvasMetrics a, CanvasMetrics b) => a.Equals(b);
    public static bool operator !=(CanvasMetrics a, CanvasMetrics b) => !a.Equals(b);
    public override string ToString() => FormattableString.Invariant($"{ClientSize} @{Scale} → {PixelSize}");
}
