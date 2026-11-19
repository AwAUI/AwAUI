using System;

namespace AwAUI.Graphics.Text;

/// <summary>字形串的合成样式（字体缺少对应字重或风格时由渲染器模拟）。</summary>
[Flags]
public enum GlyphRunFlags : byte
{
    None = 0,
    /// <summary>合成加粗：渲染器把轮廓加粗约 <see cref="GlyphRun.GetSyntheticBoldExtent(float)"/>。</summary>
    SyntheticBold = 1,
    /// <summary>合成倾斜：轮廓按 <see cref="GlyphRun.SyntheticObliqueSkew"/> 向右错切（约 14°，与 CSS <c>oblique</c> 默认角度一致）。</summary>
    SyntheticOblique = 2,
}

/// <summary>
/// 已整形、已定位的一串字形（design/02、design/13 的 <c>GlyphRun</c> 资源）。不可变，可跨线程读取。
/// 坐标为逻辑像素、y 向下；<see cref="Positions"/> 是每个字形在基线上的原点，相对于绘制时传入的原点。
/// 由文本栈（<c>AwAUI.Text</c> 的 <c>ShapedRun.ToGlyphRun</c>）产生，渲染后端经 <see cref="Face"/> 读取轮廓。
/// </summary>
public sealed class GlyphRun
{
    /// <summary>合成倾斜的错切系数（tan 14° ≈ 0.25）：x' = x − y·Skew（y 向下，基线以上的点右移）。</summary>
    public const float SyntheticObliqueSkew = 0.25f;

    readonly ushort[] _glyphs;
    readonly Point[] _positions;
    readonly Rect _bounds;

    /// <summary>创建字形串。数组会被复制。</summary>
    /// <param name="face">字型。</param>
    /// <param name="size">字号（逻辑像素，即 1 em 的长度），必须是非负有限值。</param>
    /// <param name="glyphs">字形 ID。</param>
    /// <param name="positions">每个字形的原点（相对绘制原点），长度必须与 <paramref name="glyphs"/> 相同。</param>
    /// <param name="flags">合成加粗 / 倾斜。</param>
    /// <param name="variation">可变字体的轴取值。</param>
    public GlyphRun(FontFace face, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Point> positions,
        GlyphRunFlags flags = GlyphRunFlags.None, FontVariation variation = default)
    {
        Face = face ?? throw new ArgumentNullException(nameof(face));
        if (!(size >= 0) || float.IsInfinity(size)) throw new ArgumentOutOfRangeException(nameof(size), "Size must be a finite, non-negative number.");
        if (positions.Length != glyphs.Length) throw new ArgumentException("Positions must match glyphs.", nameof(positions));
        Size = size;
        Flags = flags;
        Variation = variation;
        _glyphs = glyphs.ToArray();
        _positions = positions.ToArray();
        // 在构造时算好：惰性缓存一个多字段的 Rect? 在并发读取时可能被撕裂（HasValue 先于数值可见）
        _bounds = ComputeBounds();
    }

    public FontFace Face { get; }

    /// <summary>字号（逻辑像素）。</summary>
    public float Size { get; }

    public GlyphRunFlags Flags { get; }

    public FontVariation Variation { get; }

    public int Count => _glyphs.Length;

    public bool IsEmpty => _glyphs.Length == 0 || Size == 0;

    public ReadOnlySpan<ushort> Glyphs => _glyphs;

    public ReadOnlySpan<Point> Positions => _positions;

    public bool IsSyntheticBold => (Flags & GlyphRunFlags.SyntheticBold) != 0;

    public bool IsSyntheticOblique => (Flags & GlyphRunFlags.SyntheticOblique) != 0;

    /// <summary>
    /// 合成加粗时轮廓的加宽量（与字号同单位）。与 Skia 的做法一致：9px 时为 1/24 em，36px 及以上为 1/32 em，中间线性插值。
    /// </summary>
    public static float GetSyntheticBoldExtent(float size)
    {
        float ratio;
        if (size <= 9) ratio = 1f / 24;
        else if (size >= 36) ratio = 1f / 32;
        else ratio = 1f / 24 + (1f / 32 - 1f / 24) * (size - 9) / (36 - 9);
        return size * ratio;
    }

    /// <summary>
    /// 保守的墨迹包围盒（相对绘制原点，逻辑像素）：按字型的全局边界框（'head'）估算，含合成加粗与倾斜的外扩。
    /// 字型没有边界框信息时按每个字形占 [−1, 2] em × [−1.5, 0.5] em 估算。不保证包含声明了错误边界框的字体的全部墨迹。
    /// 任一位置不是有限值时返回非有限的矩形。
    /// </summary>
    public Rect Bounds => _bounds;

    Rect ComputeBounds()
    {
        if (IsEmpty) return Rect.Empty;
        var m = Face.Metrics;
        float scale = m.GetScale(Size);
        float l, t, r, b;
        if (m.XMax > m.XMin && m.YMax > m.YMin && scale > 0)
        {
            l = m.XMin * scale; r = m.XMax * scale;
            t = -m.YMax * scale; b = -m.YMin * scale;
        }
        else
        {
            l = -Size; r = 2 * Size;
            t = -1.5f * Size; b = 0.5f * Size;
        }
        if (IsSyntheticOblique)
        {
            // x' = x − y·k：y ∈ [t, b] 时 x 的偏移在 [−b·k, −t·k] 之间
            float k = SyntheticObliqueSkew;
            float s0 = -b * k, s1 = -t * k;
            l += Math.Min(s0, s1);
            r += Math.Max(s0, s1);
        }
        if (IsSyntheticBold)
        {
            float e = GetSyntheticBoldExtent(Size);
            l -= e; t -= e; r += e; b += e;
        }

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in _positions)
        {
            if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsInfinity(p.X) || float.IsInfinity(p.Y))
                return new Rect(float.NaN, float.NaN, 0, 0);   // 非有限的位置：DrawingContext 不录制
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        return Rect.FromLTRB(minX + l, minY + t, maxX + r, maxY + b);
    }

    public override string ToString() => $"GlyphRun({Face}, {Size}px, {Count} glyphs)";
}
