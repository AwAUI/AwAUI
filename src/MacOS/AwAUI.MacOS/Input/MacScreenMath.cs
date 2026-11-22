namespace AwAUI.MacOS;

/// <summary>
/// Cocoa 屏幕坐标（原点在主显示器左下、y 向上）与框架坐标（原点在主显示器左上、y 向下）之间的换算。
/// 与 AppKit 无关，放在 Input/ 目录里以便 <c>tests/AwAUI.Platform.UnitTests</c> 以源码链接方式测试。
/// </summary>
internal static class MacScreenMath
{
    /// <summary>
    /// 全局 Cocoa 矩形 → 框架矩形。<paramref name="primaryTop"/> 是主显示器在 Cocoa 坐标中的上边（origin.y + height，
    /// 主显示器原点为 (0, 0)，因此通常就是它的高度）。
    /// </summary>
    public static Rect FromCocoa(double x, double y, double width, double height, double primaryTop) =>
        new((float)x, (float)(primaryTop - (y + height)), (float)width, (float)height);

    /// <summary>窗口 frame（Cocoa 坐标）的左上角 → 框架坐标的 <see cref="Point"/>。</summary>
    public static Point PositionFromCocoa(double x, double y, double height, double primaryTop) =>
        new((float)x, (float)(primaryTop - (y + height)));

    /// <summary>框架坐标的窗口左上角 → <c>setFrameOrigin:</c> 使用的 Cocoa 原点。</summary>
    public static void ToCocoaOrigin(Point position, double height, double primaryTop, out double x, out double y)
    {
        x = position.X;
        y = primaryTop - position.Y - height;
    }
}
