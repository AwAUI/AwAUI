using System;
using System.Runtime.InteropServices;

namespace AwAUI;

/// <summary>四角半径独立、支持椭圆角的圆角矩形。半径按 CSS 规则在构造时等比缩小以避免重叠。</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct RoundRect : IEquatable<RoundRect>
{
    public RoundRect(Rect rect, float radius) : this(rect, radius, radius, radius, radius) { }

    public RoundRect(Rect rect, float topLeft, float topRight, float bottomRight, float bottomLeft)
        : this(rect, new Size(topLeft, topLeft), new Size(topRight, topRight),
            new Size(bottomRight, bottomRight), new Size(bottomLeft, bottomLeft)) { }

    public RoundRect(Rect rect, Size topLeft, Size topRight, Size bottomRight, Size bottomLeft)
    {
        Rect = rect;
        // CSS Backgrounds 3 §5.5：所有半径乘以 f = min(Li / Si)，Si 为同一边上两个半径之和
        float f = 1f;
        f = Scale(f, rect.Width, topLeft.Width + topRight.Width);
        f = Scale(f, rect.Width, bottomLeft.Width + bottomRight.Width);
        f = Scale(f, rect.Height, topLeft.Height + bottomLeft.Height);
        f = Scale(f, rect.Height, topRight.Height + bottomRight.Height);
        TopLeft = Clamp(topLeft, f);
        TopRight = Clamp(topRight, f);
        BottomRight = Clamp(bottomRight, f);
        BottomLeft = Clamp(bottomLeft, f);
    }

    static float Scale(float f, float length, float sum) => sum > length && sum > 0 ? Math.Min(f, length / sum) : f;
    static Size Clamp(Size s, float f) => new(Math.Max(0, s.Width * f), Math.Max(0, s.Height * f));

    public Rect Rect { get; }
    public Size TopLeft { get; }
    public Size TopRight { get; }
    public Size BottomRight { get; }
    public Size BottomLeft { get; }

    public bool IsRect => TopLeft.IsEmpty && TopRight.IsEmpty && BottomRight.IsEmpty && BottomLeft.IsEmpty;

    public RoundRect Inflate(float d)
    {
        static Size Grow(Size s, float d) => s.IsEmpty ? s : new Size(Math.Max(0, s.Width + d), Math.Max(0, s.Height + d));
        return new RoundRect(Rect.Inflate(d), Grow(TopLeft, d), Grow(TopRight, d), Grow(BottomRight, d), Grow(BottomLeft, d));
    }

    public bool Equals(RoundRect o) => Rect == o.Rect && TopLeft == o.TopLeft && TopRight == o.TopRight &&
                                       BottomRight == o.BottomRight && BottomLeft == o.BottomLeft;
    public override bool Equals(object? obj) => obj is RoundRect r && Equals(r);
    public override int GetHashCode() => HashCodeCompat.Combine(Rect.GetHashCode(), TopLeft.GetHashCode(), BottomRight.GetHashCode());
}
