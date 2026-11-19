using System;
using System.Collections.Generic;
using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.Text.OpenType;

/// <summary>解码后的 TrueType 字形轮廓：二次贝塞尔控制点（设计单位，y 向上）与各轮廓的末点索引。</summary>
internal sealed class GlyphOutline
{
    public readonly List<Vector2> Points = new();
    public readonly List<bool> OnCurve = new();
    /// <summary>每个轮廓最后一个点的索引（含）。</summary>
    public readonly List<int> ContourEnds = new();

    public int PointCount => Points.Count;
    public int ContourCount => ContourEnds.Count;

    public void Clear()
    {
        Points.Clear();
        OnCurve.Clear();
        ContourEnds.Clear();
    }

    /// <summary>
    /// 把轮廓写入路径。<paramref name="transform"/> 作用于「y 向下」的设计单位坐标（即 (x, -y)），
    /// 这样字形原点在基线上，与 UI 坐标系一致。
    /// </summary>
    public void Emit(PathBuilder sink, in Matrix3x2 transform)
    {
        int start = 0;
        foreach (int end in ContourEnds)
        {
            EmitContour(sink, start, end, transform);
            start = end + 1;
        }
    }

    static Point Map(Vector2 p, in Matrix3x2 m)
    {
        var v = Vector2.Transform(new Vector2(p.X, -p.Y), m);
        return new Point(v.X, v.Y);
    }

    void EmitContour(PathBuilder sink, int first, int last, in Matrix3x2 m)
    {
        int n = last - first + 1;
        if (n <= 0) return;
        // 选择起点：首个落在曲线上的点；全是控制点时取首末两个控制点的中点
        Vector2 startPoint;
        int startIndex;
        if (OnCurve[first])
        {
            startPoint = Points[first];
            startIndex = 0;
        }
        else if (OnCurve[last])
        {
            startPoint = Points[last];
            startIndex = n - 1;
        }
        else
        {
            startPoint = (Points[first] + Points[last]) * 0.5f;
            startIndex = n - 1;   // 从最后一个（控制）点之后开始，最后再回到它
        }
        sink.MoveTo(Map(startPoint, m));

        Vector2? control = null;
        // 从起点之后的第一个点开始，绕一圈回到起点
        for (int k = 1; k <= n; k++)
        {
            int i = first + (startIndex + k) % n;
            var p = Points[i];
            bool on = OnCurve[i];
            bool isStart = k == n;
            if (isStart && !OnCurve[first + startIndex])
            {
                // 起点是隐含的中点：此时 p 是那个末尾控制点
                if (control is { } c0)
                {
                    var mid = (c0 + p) * 0.5f;
                    sink.QuadTo(Map(c0, m), Map(mid, m));
                }
                sink.QuadTo(Map(p, m), Map(startPoint, m));
                control = null;
                break;
            }
            if (on)
            {
                if (control is { } c) sink.QuadTo(Map(c, m), Map(p, m));
                else sink.LineTo(Map(p, m));
                control = null;
            }
            else
            {
                if (control is { } c)
                {
                    var mid = (c + p) * 0.5f;
                    sink.QuadTo(Map(c, m), Map(mid, m));
                }
                control = p;
            }
        }
        if (control is { } last2) sink.QuadTo(Map(last2, m), Map(startPoint, m));
        sink.Close();
    }
}

/// <summary>'loca' + 'glyf' 表：简单字形与复合字形的解码。</summary>
internal sealed class GlyfTable
{
    /// <summary>复合字形的最大嵌套深度。</summary>
    public const int MaxCompositeDepth = 16;
    /// <summary>单个字形（含展开后的全部组件）的最大点数，防止恶意的指数级展开。</summary>
    public const int MaxPoints = 1 << 16;
    const int MaxComponents = 4096;

    readonly ReadOnlyMemory<byte> _glyf;
    readonly ReadOnlyMemory<byte> _loca;
    readonly bool _longLoca;
    readonly int _numGlyphs;

    public GlyfTable(ReadOnlyMemory<byte> glyf, ReadOnlyMemory<byte> loca, bool longLoca, int numGlyphs)
    {
        _glyf = glyf;
        _loca = loca;
        _longLoca = longLoca;
        // loca 需要 numGlyphs + 1 个条目，表不够长时只信任实际存在的部分
        int entries = loca.Length / (longLoca ? 4 : 2);
        _numGlyphs = Math.Max(0, Math.Min(numGlyphs, entries - 1));
    }

    /// <summary>字形在 glyf 中的数据；空字形（如空格）返回空。</summary>
    public ReadOnlySpan<byte> GetGlyphData(int glyph)
    {
        if ((uint)glyph >= (uint)_numGlyphs) return default;
        var l = _loca.Span;
        long start, end;
        if (_longLoca)
        {
            start = Be.U32(l, glyph * 4);
            end = Be.U32(l, glyph * 4 + 4);
        }
        else
        {
            start = Be.U16(l, glyph * 2) * 2L;
            end = Be.U16(l, glyph * 2 + 2) * 2L;
        }
        if (end <= start || end > _glyf.Length) return default;
        return _glyf.Span.Slice((int)start, (int)(end - start));
    }

    /// <summary>字形头部的边界框（设计单位，y 向上）；空字形返回 false。</summary>
    public bool TryGetBounds(int glyph, out short xMin, out short yMin, out short xMax, out short yMax)
    {
        var g = GetGlyphData(glyph);
        if (g.Length < 10)
        {
            xMin = yMin = xMax = yMax = 0;
            return false;
        }
        xMin = Be.I16(g, 2);
        yMin = Be.I16(g, 4);
        xMax = Be.I16(g, 6);
        yMax = Be.I16(g, 8);
        return true;
    }

    /// <summary>解码字形轮廓到 <paramref name="outline"/>。数据损坏时抛出 <see cref="InvalidFontException"/>。</summary>
    public void Decode(int glyph, GlyphOutline outline)
    {
        outline.Clear();
        int components = 0;
        DecodeInto(glyph, outline, 0, ref components);
    }

    void DecodeInto(int glyph, GlyphOutline outline, int depth, ref int components)
    {
        if ((uint)glyph >= (uint)_numGlyphs) throw new InvalidFontException($"Glyph {glyph} is out of range.");
        var g = GetGlyphData(glyph);
        if (g.Length == 0) return;
        int contours = Be.I16(g, 0);
        if (contours >= 0) DecodeSimple(g, contours, outline);
        else DecodeComposite(g, outline, depth, ref components);
    }

    static void DecodeSimple(ReadOnlySpan<byte> g, int contours, GlyphOutline outline)
    {
        if (contours == 0) return;
        int pos = 10;
        int baseIndex = outline.PointCount;
        int prevEnd = -1;
        for (int i = 0; i < contours; i++)
        {
            int end = Be.U16(g, pos + i * 2);
            if (end < prevEnd) throw new InvalidFontException("Simple glyph contour end points are not increasing.");
            // 允许 end == prevEnd（空轮廓），emit 时跳过
            prevEnd = end;
        }
        int numPoints = prevEnd + 1;
        if (baseIndex + numPoints > MaxPoints) throw new InvalidFontException("Glyph has too many points.");
        pos += contours * 2;
        int instructionLength = Be.U16(g, pos);
        pos += 2 + instructionLength;

        // 标志（含重复）
        var flags = new byte[numPoints];
        for (int i = 0; i < numPoints;)
        {
            byte f = Be.U8(g, pos++);
            flags[i++] = f;
            if ((f & 0x08) != 0)
            {
                int repeat = Be.U8(g, pos++);
                if (repeat > numPoints - i) throw new InvalidFontException("Glyph flag repeat overflows the point count.");
                for (int r = 0; r < repeat; r++) flags[i++] = f;
            }
        }

        var xs = new int[numPoints];
        int v = 0;
        for (int i = 0; i < numPoints; i++)
        {
            byte f = flags[i];
            if ((f & 0x02) != 0)
            {
                int d = Be.U8(g, pos++);
                v += (f & 0x10) != 0 ? d : -d;
            }
            else if ((f & 0x10) == 0)
            {
                v += Be.I16(g, pos);
                pos += 2;
            }
            xs[i] = v;
        }
        v = 0;
        for (int i = 0; i < numPoints; i++)
        {
            byte f = flags[i];
            if ((f & 0x04) != 0)
            {
                int d = Be.U8(g, pos++);
                v += (f & 0x20) != 0 ? d : -d;
            }
            else if ((f & 0x20) == 0)
            {
                v += Be.I16(g, pos);
                pos += 2;
            }
            outline.Points.Add(new Vector2(xs[i], v));
            outline.OnCurve.Add((flags[i] & 0x01) != 0);
        }

        prevEnd = -1;
        for (int i = 0; i < contours; i++)
        {
            int end = Be.U16(g, 10 + i * 2);
            if (end != prevEnd) outline.ContourEnds.Add(baseIndex + end);
            prevEnd = end;
        }
    }

    // 复合字形标志
    const int Arg1And2AreWords = 0x0001;
    const int ArgsAreXYValues = 0x0002;
    const int WeHaveAScale = 0x0008;
    const int MoreComponents = 0x0020;
    const int WeHaveAnXAndYScale = 0x0040;
    const int WeHaveATwoByTwo = 0x0080;
    const int ScaledComponentOffset = 0x0800;
    const int UnscaledComponentOffset = 0x1000;

    void DecodeComposite(ReadOnlySpan<byte> g, GlyphOutline outline, int depth, ref int components)
    {
        if (depth >= MaxCompositeDepth) throw new InvalidFontException("Composite glyph nesting is too deep.");
        int pos = 10;
        int compositeStart = outline.PointCount;
        int flags;
        do
        {
            if (++components > MaxComponents) throw new InvalidFontException("Composite glyph has too many components.");
            flags = Be.U16(g, pos);
            int child = Be.U16(g, pos + 2);
            pos += 4;
            int arg1, arg2;
            bool xy = (flags & ArgsAreXYValues) != 0;
            if ((flags & Arg1And2AreWords) != 0)
            {
                arg1 = xy ? Be.I16(g, pos) : Be.U16(g, pos);
                arg2 = xy ? Be.I16(g, pos + 2) : Be.U16(g, pos + 2);
                pos += 4;
            }
            else
            {
                arg1 = xy ? Be.I8(g, pos) : Be.U8(g, pos);
                arg2 = xy ? Be.I8(g, pos + 1) : Be.U8(g, pos + 1);
                pos += 2;
            }

            float a = 1, b = 0, c = 0, d = 1;
            if ((flags & WeHaveAScale) != 0)
            {
                a = d = Be.F2Dot14(g, pos);
                pos += 2;
            }
            else if ((flags & WeHaveAnXAndYScale) != 0)
            {
                a = Be.F2Dot14(g, pos);
                d = Be.F2Dot14(g, pos + 2);
                pos += 4;
            }
            else if ((flags & WeHaveATwoByTwo) != 0)
            {
                a = Be.F2Dot14(g, pos);
                b = Be.F2Dot14(g, pos + 2);
                c = Be.F2Dot14(g, pos + 4);
                d = Be.F2Dot14(g, pos + 6);
                pos += 8;
            }

            int first = outline.PointCount;
            DecodeInto(child, outline, depth + 1, ref components);
            int count = outline.PointCount - first;

            // 先做线性变换：x' = a·x + c·y，y' = b·x + d·y
            bool linear = a != 1 || b != 0 || c != 0 || d != 1;
            if (linear)
                for (int i = first; i < first + count; i++)
                {
                    var p = outline.Points[i];
                    outline.Points[i] = new Vector2(a * p.X + c * p.Y, b * p.X + d * p.Y);
                }

            Vector2 offset;
            if (xy)
            {
                offset = new Vector2(arg1, arg2);
                // 默认按 Microsoft 的约定不缩放偏移；显式要求时才变换偏移
                if (linear && (flags & ScaledComponentOffset) != 0 && (flags & UnscaledComponentOffset) == 0)
                    offset = new Vector2(a * offset.X + c * offset.Y, b * offset.X + d * offset.Y);
            }
            else
            {
                // 点对齐：父字形已有的第 arg1 个点与本组件的第 arg2 个点重合
                if (compositeStart + arg1 >= first || arg2 >= count) throw new InvalidFontException("Composite glyph anchor point is out of range.");
                offset = outline.Points[compositeStart + arg1] - outline.Points[first + arg2];
            }
            if (offset != Vector2.Zero)
                for (int i = first; i < first + count; i++) outline.Points[i] += offset;
        } while ((flags & MoreComponents) != 0);
    }
}
