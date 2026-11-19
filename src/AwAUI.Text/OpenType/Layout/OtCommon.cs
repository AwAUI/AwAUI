using System;

namespace AwAUI.Text.OpenType.Layout;

/// <summary>OpenType 布局公共结构：Coverage、ClassDef。全部读取经 <see cref="Be"/> 检查边界，越界抛出 <see cref="InvalidFontException"/>。</summary>
internal static class Ot
{
    public const int NotCovered = -1;

    /// <summary>字形在 Coverage 表中的序号；不在其中时返回 <see cref="NotCovered"/>。</summary>
    public static int Coverage(ReadOnlySpan<byte> s, int offset, int glyph)
    {
        int format = Be.U16(s, offset);
        int count = Be.U16(s, offset + 2);
        if (format == 1)
        {
            int lo = 0, hi = count - 1, p = offset + 4;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int g = Be.U16(s, p + mid * 2);
                if (g < glyph) lo = mid + 1;
                else if (g > glyph) hi = mid - 1;
                else return mid;
            }
            return NotCovered;
        }
        if (format == 2)
        {
            int lo = 0, hi = count - 1, p = offset + 4;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int r = p + mid * 6;
                int start = Be.U16(s, r);
                if (glyph < start)
                {
                    hi = mid - 1;
                    continue;
                }
                int end = Be.U16(s, r + 2);
                if (glyph > end)
                {
                    lo = mid + 1;
                    continue;
                }
                return Be.U16(s, r + 4) + glyph - start;
            }
            return NotCovered;
        }
        return NotCovered;
    }

    /// <summary>字形在 ClassDef 表中的类别；偏移为 0（表不存在）或未列出时为 0。</summary>
    public static int ClassOf(ReadOnlySpan<byte> s, int offset, int glyph)
    {
        if (offset <= 0) return 0;
        int format = Be.U16(s, offset);
        if (format == 1)
        {
            int start = Be.U16(s, offset + 2);
            int count = Be.U16(s, offset + 4);
            int i = glyph - start;
            return (uint)i < (uint)count ? Be.U16(s, offset + 6 + i * 2) : 0;
        }
        if (format == 2)
        {
            int count = Be.U16(s, offset + 2);
            int lo = 0, hi = count - 1, p = offset + 4;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int r = p + mid * 6;
                int start = Be.U16(s, r);
                if (glyph < start)
                {
                    hi = mid - 1;
                    continue;
                }
                int end = Be.U16(s, r + 2);
                if (glyph > end)
                {
                    lo = mid + 1;
                    continue;
                }
                return Be.U16(s, r + 4);
            }
        }
        return 0;
    }

    /// <summary>可选的 16 位偏移：0 表示不存在，返回 −1。</summary>
    public static int Offset16(ReadOnlySpan<byte> s, int baseOffset, int at)
    {
        int o = Be.U16(s, at);
        return o == 0 ? -1 : baseOffset + o;
    }

    /// <summary>把 Coverage 表覆盖的字形加入摘要。</summary>
    public static void AddCoverage(ref GlyphDigest digest, ReadOnlySpan<byte> s, int offset)
    {
        int format = Be.U16(s, offset);
        int count = Be.U16(s, offset + 2);
        if (format == 1)
            for (int i = 0; i < count; i++) digest.Add(Be.U16(s, offset + 4 + i * 2));
        else if (format == 2)
            for (int i = 0; i < count; i++)
            {
                int r = offset + 4 + i * 6;
                digest.AddRange(Be.U16(s, r), Be.U16(s, r + 2));
            }
        else digest.AddAll();
    }
}

/// <summary>
/// 字形集合的近似摘要（与 HarfBuzz 的 set digest 相同的思路：三个不同移位的 64 位掩码）。
/// <see cref="MayHave"/> 为 false 时字形一定不在集合中，用于在应用查找前快速排除。
/// </summary>
internal struct GlyphDigest
{
    ulong _m0, _m1, _m2;

    public void AddAll() => _m0 = _m1 = _m2 = ulong.MaxValue;

    public void Add(int g)
    {
        _m0 |= 1UL << (g & 63);
        _m1 |= 1UL << ((g >> 4) & 63);
        _m2 |= 1UL << ((g >> 9) & 63);
    }

    public void AddRange(int a, int b)
    {
        if (b < a) return;
        _m0 |= RangeMask(a, b, 0);
        _m1 |= RangeMask(a, b, 4);
        _m2 |= RangeMask(a, b, 9);
    }

    static ulong RangeMask(int a, int b, int shift)
    {
        int lo = a >> shift, hi = b >> shift;
        if (hi - lo >= 63) return ulong.MaxValue;
        ulong m = 0;
        for (int v = lo; v <= hi; v++) m |= 1UL << (v & 63);
        return m;
    }

    public void Union(in GlyphDigest o)
    {
        _m0 |= o._m0;
        _m1 |= o._m1;
        _m2 |= o._m2;
    }

    /// <summary>两个集合可能相交（为 false 时一定不相交）。</summary>
    public readonly bool MayIntersect(in GlyphDigest o) => (_m0 & o._m0) != 0 && (_m1 & o._m1) != 0 && (_m2 & o._m2) != 0;

    public readonly bool MayHave(int g) =>
        (_m0 & (1UL << (g & 63))) != 0 && (_m1 & (1UL << ((g >> 4) & 63))) != 0 && (_m2 & (1UL << ((g >> 9) & 63))) != 0;
}

/// <summary>'GDEF' 表：字形类别、标记附着类、标记过滤集。</summary>
internal sealed class GdefTable
{
    readonly ReadOnlyMemory<byte> _data;
    readonly int _glyphClassDef, _markAttachClassDef, _markGlyphSets;

    GdefTable(ReadOnlyMemory<byte> data)
    {
        _data = data;
        var s = data.Span;
        int major = Be.U16(s, 0), minor = Be.U16(s, 2);
        if (major != 1) throw new InvalidFontException("Unsupported GDEF version.");
        _glyphClassDef = Be.U16(s, 4);
        _markAttachClassDef = Be.U16(s, 10);
        _markGlyphSets = minor >= 2 && s.Length >= 14 ? Be.U16(s, 12) : 0;
        if (_glyphClassDef >= s.Length) _glyphClassDef = 0;
        if (_markAttachClassDef >= s.Length) _markAttachClassDef = 0;
        if (_markGlyphSets >= s.Length) _markGlyphSets = 0;
    }

    public static GdefTable Parse(ReadOnlyMemory<byte> data) => new(data);

    public bool HasGlyphClasses => _glyphClassDef != 0;

    /// <summary>HarfBuzz 的 glyph_props：Base → 0x02，Ligature → 0x04，Mark → 0x08 | 附着类 &lt;&lt; 8，其他 0。</summary>
    public ushort GetGlyphProps(int glyph)
    {
        var s = _data.Span;
        try
        {
            switch (Ot.ClassOf(s, _glyphClassDef, glyph))
            {
                case 1: return GlyphPropsBase;
                case 2: return GlyphPropsLigature;
                case 3:
                    int k = Ot.ClassOf(s, _markAttachClassDef, glyph);
                    return (ushort)(GlyphPropsMark | ((k & 0xFF) << 8));
                default: return 0;
            }
        }
        catch (InvalidFontException)
        {
            return 0;   // 损坏的类别表：视为未分类
        }
    }

    const ushort GlyphPropsBase = 0x02, GlyphPropsLigature = 0x04, GlyphPropsMark = 0x08;

    /// <summary>标记过滤集 <paramref name="set"/> 是否包含字形。</summary>
    public bool MarkSetCovers(int set, int glyph)
    {
        if (_markGlyphSets == 0) return false;
        try
        {
            return MarkSetCoversCore(set, glyph);
        }
        catch (InvalidFontException)
        {
            return false;
        }
    }

    bool MarkSetCoversCore(int set, int glyph)
    {
        var s = _data.Span;
        int count = Be.U16(s, _markGlyphSets + 2);
        if ((uint)set >= (uint)count) return false;
        long cov = _markGlyphSets + (long)Be.U32(s, _markGlyphSets + 4 + set * 4);
        if (cov >= s.Length) return false;
        return Ot.Coverage(s, (int)cov, glyph) != Ot.NotCovered;
    }
}
