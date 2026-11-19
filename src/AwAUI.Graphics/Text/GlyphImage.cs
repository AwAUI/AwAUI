using System;

namespace AwAUI.Graphics.Text;

/// <summary>彩色字形的位图（sbix / CBDT 或光栅化后的 COLR）。彩色字形在 P2 实现。</summary>
public sealed class GlyphImage
{
    public GlyphImage(Bitmap bitmap, Point origin, float scale)
    {
        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        Origin = origin;
        Scale = scale;
    }

    public Bitmap Bitmap { get; }

    /// <summary>位图左上角相对于字形原点（基线上）的偏移，逻辑像素，y 向下。</summary>
    public Point Origin { get; }

    /// <summary>位图像素到逻辑像素的比例。</summary>
    public float Scale { get; }
}
