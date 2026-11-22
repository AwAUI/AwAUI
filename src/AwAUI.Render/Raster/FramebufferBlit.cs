using System;
using AwAUI.Graphics;

namespace AwAUI.Render.Raster;

/// <summary>
/// 在目标位图内搬移一块像素（design/18「滚动：位块搬移已有像素 + 重画新露出的条带」）。
/// 源与目的可以重叠：行内用 <see cref="Span{T}.CopyTo(Span{T})"/>（memmove 语义），行间按位移方向选择自顶向下 / 自底向上，
/// 所以任意方向、任意重叠都正确，不需要临时缓冲。
/// </summary>
internal static class FramebufferBlit
{
    /// <summary>执行一次搬移，返回实际复制的像素数（0 表示没有可搬移的内容）。</summary>
    public static long Shift(Bitmap target, in FramebufferShift shift)
    {
        if (target is null) throw new ArgumentNullException(nameof(target));
        if (shift.IsEmpty) return 0;
        var full = new PixelRect(0, 0, target.Width, target.Height);
        var source = shift.Source.Intersect(full);
        if (source.IsEmpty) return 0;
        int dx = shift.Dx, dy = shift.Dy;
        // 目的区域裁剪到目标；源区域随之调整，保证读写都在位图内
        var dest = new PixelRect(source.X + dx, source.Y + dy, source.Width, source.Height).Intersect(full);
        if (dest.IsEmpty) return 0;
        source = new PixelRect(dest.X - dx, dest.Y - dy, dest.Width, dest.Height).Intersect(source);
        if (source.IsEmpty) return 0;
        int width = source.Width * 4;
        // 1 行高、任意水平位移：逐行 memmove，彼此独立；垂直重叠时按方向决定行的顺序
        if (dy < 0)
        {
            for (int y = source.Y; y < source.Bottom; y++)
                target.GetRow(y).Slice(source.X * 4, width).CopyTo(target.GetRow(y + dy).Slice((source.X + dx) * 4, width));
        }
        else
        {
            for (int y = source.Bottom - 1; y >= source.Y; y--)
                target.GetRow(y).Slice(source.X * 4, width).CopyTo(target.GetRow(y + dy).Slice((source.X + dx) * 4, width));
        }
        target.Invalidate();
        return (long)source.Width * source.Height;
    }
}
