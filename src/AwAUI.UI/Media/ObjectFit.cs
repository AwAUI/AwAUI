using System;
using AwAUI.Css.Values;

namespace AwAUI.UI;

/// <summary>
/// 替换内容在内容盒中的放置（CSS Images 3 的 <c>object-fit</c> / <c>object-position</c>）。
/// 取值来自计算样式（<see cref="Css.ComputedStyle.ObjectFit"/>、<see cref="Css.ComputedStyle.ObjectPosition"/>），
/// 节点上没有对应的 C# 属性：与 HTML 一样，这两个属性只能经 CSS（样式表、<c>Style</c>、类）设置。
/// </summary>
internal static class ObjectFitting
{
    /// <summary>
    /// 求「具体对象尺寸」并按位置放进内容盒 <paramref name="box"/>，返回目标矩形（可超出内容盒，由调用方裁剪）。
    /// </summary>
    /// <param name="natural">固有尺寸；任一边不为正表示没有固有尺寸。</param>
    /// <param name="naturalRatio">只有固有宽高比（如只有 viewBox 的 SVG）时的宽高比；有固有尺寸时忽略。</param>
    /// <remarks>
    /// 有固有尺寸时按规范缩放；只有宽高比时，<c>contain / none / scale-down</c> 都得到按宽高比放入内容盒的最大矩形
    /// （默认对象尺寸即内容盒），<c>cover</c> 得到铺满的最小矩形；两者都没有时与 <c>fill</c> 相同。
    /// </remarks>
    public static Rect Place(CssObjectFit fit, PositionValue position, Size natural, float? naturalRatio, Rect box)
    {
        float bw = Math.Max(0, box.Width), bh = Math.Max(0, box.Height);
        float nw = natural.Width, nh = natural.Height;
        float w = bw, h = bh;
        if (fit != CssObjectFit.Fill)
        {
            if (nw > 0 && nh > 0 && !float.IsInfinity(nw) && !float.IsInfinity(nh))
            {
                float contain = Math.Min(bw / nw, bh / nh);
                float s = fit switch
                {
                    CssObjectFit.Contain => contain,
                    CssObjectFit.Cover => Math.Max(bw / nw, bh / nh),
                    CssObjectFit.None => 1,
                    _ => Math.Min(1, contain),
                };
                if (!(s > 0) || float.IsInfinity(s)) s = 0;
                w = nw * s;
                h = nh * s;
            }
            else if (naturalRatio is float r && r > 0 && !float.IsInfinity(r) && bw > 0 && bh > 0)
            {
                bool cover = fit == CssObjectFit.Cover;
                // 按宽高比放进（或铺满）内容盒：比较内容盒自身的宽高比
                bool widthLimited = bw / bh < r;
                if (widthLimited != cover)
                {
                    w = bw;
                    h = bw / r;
                }
                else
                {
                    h = bh;
                    w = bh * r;
                }
            }
        }
        // 极端的固有尺寸（1e30 × 1e-30 的 cover）不产生无穷大的目标矩形
        w = Finite(w);
        h = Finite(h);
        float x = box.X + Finite(position.ResolveX(bw - w));
        float y = box.Y + Finite(position.ResolveY(bh - h));
        return new Rect(x, y, w, h);
    }

    /// <summary>
    /// 位图放在目标矩形 <paramref name="dst"/> 时、落在内容盒 <paramref name="box"/> 内的部分：<paramref name="visible"/> 为可见的目标矩形，
    /// <paramref name="source"/> 为它对应的源矩形（纹素坐标，可为小数）。<c>cover</c>、<c>none</c> 与偏移的位置因此不需要裁剪命令
    /// （GPU 后端也就不需要裁剪蒙版）。没有可见部分时返回 false。
    /// </summary>
    public static bool Crop(Rect dst, Rect box, int bitmapWidth, int bitmapHeight, out Rect source, out Rect visible)
    {
        source = visible = default;
        if (!(dst.Width > 0) || !(dst.Height > 0) || bitmapWidth <= 0 || bitmapHeight <= 0) return false;
        var v = dst.Intersect(box);
        if (!(v.Width > 0) || !(v.Height > 0)) return false;
        visible = v;
        if (v == dst)
        {
            source = new Rect(0, 0, bitmapWidth, bitmapHeight);
            return true;
        }
        // double：dst 可达 ±1e7，乘以纹素比例后仍需要足够的有效位
        double sx = bitmapWidth / (double)dst.Width, sy = bitmapHeight / (double)dst.Height;
        double l = (v.Left - (double)dst.Left) * sx, t = (v.Top - (double)dst.Top) * sy;
        double r = (v.Right - (double)dst.Left) * sx, b = (v.Bottom - (double)dst.Top) * sy;
        l = Math.Max(0, Math.Min(bitmapWidth, l));
        r = Math.Max(l, Math.Min(bitmapWidth, r));
        t = Math.Max(0, Math.Min(bitmapHeight, t));
        b = Math.Max(t, Math.Min(bitmapHeight, b));
        source = Rect.FromLTRB((float)l, (float)t, (float)r, (float)b);
        return source.Width > 0 && source.Height > 0;
    }

    /// <summary>
    /// 位图的目标矩形四边对齐设备像素（与浏览器一致：图片按对齐后的矩形绘制），使 <c>contain</c> / <c>none</c> 等放置在小数位置时
    /// 边缘不与背景混合、最近邻采样的像素块大小一致。<paramref name="dst"/> 是逻辑像素（绝对坐标，与对齐过的内容盒同一坐标系）；
    /// 对齐后某一边为空（不足半个设备像素）时保持原样。
    /// </summary>
    public static Rect SnapToDevice(Rect dst, float scale)
    {
        if (!(scale > 0) || float.IsInfinity(scale)) return dst;
        double s = scale;
        float S(float v) => (float)(Math.Floor(v * s + 0.5) / s);
        float l = S(dst.Left), t = S(dst.Top), r = S(dst.Right), b = S(dst.Bottom);
        return r > l && b > t ? Rect.FromLTRB(l, t, r, b) : dst;
    }

    /// <summary>限制在 ±1e7 逻辑像素内（NaN 为 0）。</summary>
    static float Finite(float v) => float.IsNaN(v) ? 0 : Math.Max(-1e7f, Math.Min(1e7f, v));
}
