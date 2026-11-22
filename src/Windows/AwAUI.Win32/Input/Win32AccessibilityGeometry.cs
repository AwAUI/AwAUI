using System;

namespace AwAUI.Win32;

// 纯逻辑：客户区 DIP 边界 → 屏幕物理像素矩形。UIA 的 RECT 使用屏幕物理像素，
// 而语义树快照的 Bounds 是窗口客户区逻辑坐标（DIP）。测试直接验证换算。

/// <summary>屏幕物理像素矩形（与 Win32 RECT 同布局）。</summary>
internal readonly struct Win32AccessibilityRect
{
    public Win32AccessibilityRect(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public int Left { get; }
    public int Top { get; }
    public int Right { get; }
    public int Bottom { get; }
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(double x, double y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
}

/// <summary>窗口客户区在屏幕物理像素中的原点与缩放。</summary>
internal readonly struct Win32AccessibilityClientGeometry
{
    public Win32AccessibilityClientGeometry(float scale, int originX, int originY)
    {
        Scale = scale > 0 ? scale : 1f;
        OriginX = originX;
        OriginY = originY;
    }

    /// <summary>DIP → 物理像素的比例（DPI / 96）。</summary>
    public float Scale { get; }

    /// <summary>客户区左上角的屏幕物理像素 X。</summary>
    public int OriginX { get; }

    /// <summary>客户区左上角的屏幕物理像素 Y。</summary>
    public int OriginY { get; }
}

/// <summary>边界换算与命中测试。</summary>
internal static class Win32AccessibilityGeometry
{
    /// <summary>把客户区 DIP 矩形换算为屏幕物理像素矩形。</summary>
    public static Win32AccessibilityRect ToScreenRect(Rect clientBounds, Win32AccessibilityClientGeometry geometry)
    {
        if (!clientBounds.IsFinite) return default;
        double scale = geometry.Scale;
        int left = ToInt(geometry.OriginX + clientBounds.Left * scale);
        int top = ToInt(geometry.OriginY + clientBounds.Top * scale);
        int right = ToInt(geometry.OriginX + clientBounds.Right * scale);
        int bottom = ToInt(geometry.OriginY + clientBounds.Bottom * scale);
        if (right < left) right = left;
        if (bottom < top) bottom = top;
        return new Win32AccessibilityRect(left, top, right, bottom);
    }

    /// <summary>元素边界是否完全落在客户区之外（或不可见）。</summary>
    public static bool IsOffscreen(Rect bounds, Rect clientBounds)
    {
        if (!bounds.IsFinite || bounds.IsEmpty) return true;
        if (!clientBounds.IsFinite || clientBounds.IsEmpty) return true;
        return !bounds.Intersects(clientBounds);
    }

    /// <summary>UIA OrientationType：只对有方向的角色（滑块、进度条、分隔线、滚动条、工具栏）有意义。</summary>
    public static int OrientationFor(string telemetryRole, Rect bounds)
    {
        switch (telemetryRole)
        {
            case "slider":
            case "spinbutton":
            case "progressbar":
            case "separator":
            case "scrollbar":
            case "toolbar":
                if (!bounds.IsFinite || bounds.IsEmpty) return Win32UiaConstants.OrientationNone;
                return bounds.Width >= bounds.Height
                    ? Win32UiaConstants.OrientationHorizontal
                    : Win32UiaConstants.OrientationVertical;
            default:
                return Win32UiaConstants.OrientationNone;
        }
    }

    /// <summary>屏幕物理像素坐标 → 客户区 DIP 坐标。</summary>
    public static Point ScreenToClient(double x, double y, Win32AccessibilityClientGeometry geometry)
    {
        float scale = geometry.Scale;
        return new Point((float)((x - geometry.OriginX) / scale), (float)((y - geometry.OriginY) / scale));
    }

    static int ToInt(double value)
    {
        if (double.IsNaN(value)) return 0;
        if (value <= int.MinValue) return int.MinValue;
        if (value >= int.MaxValue) return int.MaxValue;
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
