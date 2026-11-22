using System;
using AwAUI.Platform;

namespace AwAUI.X11;

/// <summary>
/// 与 X 服务器无关的窗口几何计算：首次显示的位置（手动 / 居中）、逻辑像素到设备像素的换算，
/// 以及 <c>_NET_WORKAREA</c> 的解析与裁剪。纯逻辑，平台测试可直接编译并覆盖。
/// </summary>
internal static class X11WindowGeometry
{
    /// <summary>
    /// 首次显示时的位置（逻辑像素）。<see cref="WindowStartupLocation.Manual"/> 有位置时原样返回；
    /// 没有位置时返回 <see cref="Point.Zero"/>（交给窗口管理器）；居中时在主显示器工作区内居中。
    /// </summary>
    public static Point ResolveStartupPosition(WindowOptions options, Screen? primary)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (options.StartupLocation == WindowStartupLocation.Manual)
            return options.Position ?? Point.Zero;

        var work = primary?.WorkingArea ?? Rect.Empty;
        if (work.IsEmpty || !work.IsFinite)
            return options.Position ?? Point.Zero;

        float x = work.X + (work.Width - options.Size.Width) * 0.5f;
        float y = work.Y + (work.Height - options.Size.Height) * 0.5f;
        // 窗口比工作区大时贴住左上角，而不是得到负坐标（部分窗口管理器会拒绝负的初始位置）
        if (x < work.X) x = work.X;
        if (y < work.Y) y = work.Y;
        return new Point(x, y);
    }

    /// <summary>
    /// 解析 <c>_NET_WORKAREA</c>（CARDINAL，每个桌面四项：x、y、宽、高）中指定桌面的工作区。
    /// 数据缺失、越界或不是正数时返回 <see cref="Rect.Empty"/>（调用方退回完整屏幕）。
    /// </summary>
    public static Rect ParseWorkArea(long[] values, int desktopIndex)
    {
        if (values is null || desktopIndex < 0) return Rect.Empty;
        long offset = (long)desktopIndex * 4;
        if (offset + 4 > values.Length) return Rect.Empty;
        long x = values[offset], y = values[offset + 1], w = values[offset + 2], h = values[offset + 3];
        if (w <= 0 || h <= 0) return Rect.Empty;
        if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue) return Rect.Empty;
        if (w > int.MaxValue || h > int.MaxValue) return Rect.Empty;
        return new Rect((float)x, (float)y, (float)w, (float)h);
    }

    /// <summary>把工作区裁剪到显示器边界内；工作区无效或不相交时退回显示器边界。</summary>
    public static Rect ClampWorkArea(Rect bounds, Rect workArea)
    {
        if (workArea.IsEmpty || !workArea.IsFinite) return bounds;
        var clipped = bounds.Intersect(workArea);
        return clipped.IsEmpty ? bounds : clipped;
    }

    /// <summary>逻辑像素坐标 → 设备像素坐标（四舍五入并钳制到 int 范围）。</summary>
    public static int ToPixelCoordinate(float logical, double scale)
    {
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1;
        double value = Math.Round(logical * scale);
        if (double.IsNaN(value)) return 0;
        if (value < int.MinValue) return int.MinValue;
        if (value > int.MaxValue) return int.MaxValue;
        return (int)value;
    }

    /// <summary>逻辑像素长度 → 设备像素长度；结果至少为 1。</summary>
    public static int ToPixelExtent(double logical, double scale)
    {
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) scale = 1;
        double value = Math.Round(logical * scale);
        if (double.IsNaN(value) || value < 1) return 1;
        if (value > int.MaxValue) return int.MaxValue;
        return (int)value;
    }
}
