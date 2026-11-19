using System;

namespace AwAUI.Text.OpenType;

/// <summary>'head' 表：全局信息。</summary>
internal sealed class HeadTable
{
    public ushort UnitsPerEm { get; private set; }
    public short XMin { get; private set; }
    public short YMin { get; private set; }
    public short XMax { get; private set; }
    public short YMax { get; private set; }
    public ushort MacStyle { get; private set; }
    /// <summary>0 = loca 为 16 位（偏移 / 2），1 = 32 位。</summary>
    public short IndexToLocFormat { get; private set; }

    public bool IsBold => (MacStyle & 1) != 0;
    public bool IsItalic => (MacStyle & 2) != 0;

    public static HeadTable Parse(ReadOnlySpan<byte> s)
    {
        if (s.Length < 54) throw new InvalidFontException("'head' table is too short.");
        var t = new HeadTable
        {
            UnitsPerEm = Be.U16(s, 18),
            XMin = Be.I16(s, 36),
            YMin = Be.I16(s, 38),
            XMax = Be.I16(s, 40),
            YMax = Be.I16(s, 42),
            MacStyle = Be.U16(s, 44),
            IndexToLocFormat = Be.I16(s, 50),
        };
        // 规范要求 16..16384；更宽松地只拒绝 0，否则整个缩放会失效
        if (t.UnitsPerEm == 0) throw new InvalidFontException("'head' unitsPerEm is zero.");
        return t;
    }
}

/// <summary>'hhea' 表：水平排版度量。</summary>
internal sealed class HheaTable
{
    public short Ascender { get; private set; }
    public short Descender { get; private set; }
    public short LineGap { get; private set; }
    public ushort AdvanceWidthMax { get; private set; }
    public short CaretSlopeRise { get; private set; }
    public short CaretSlopeRun { get; private set; }
    public ushort NumberOfHMetrics { get; private set; }

    public static HheaTable Parse(ReadOnlySpan<byte> s)
    {
        if (s.Length < 36) throw new InvalidFontException("'hhea' table is too short.");
        return new HheaTable
        {
            Ascender = Be.I16(s, 4),
            Descender = Be.I16(s, 6),
            LineGap = Be.I16(s, 8),
            AdvanceWidthMax = Be.U16(s, 10),
            CaretSlopeRise = Be.I16(s, 18),
            CaretSlopeRun = Be.I16(s, 20),
            NumberOfHMetrics = Be.U16(s, 34),
        };
    }
}

/// <summary>'maxp' 表：只取字形数量。</summary>
internal static class MaxpTable
{
    public static ushort ParseNumGlyphs(ReadOnlySpan<byte> s)
    {
        if (s.Length < 6) throw new InvalidFontException("'maxp' table is too short.");
        return Be.U16(s, 4);
    }
}

/// <summary>'hmtx' 表：水平前进量与左侧支撑。</summary>
internal sealed class HmtxTable
{
    readonly ReadOnlyMemory<byte> _data;
    readonly int _numberOfHMetrics;
    readonly int _numGlyphs;

    public HmtxTable(ReadOnlyMemory<byte> data, int numberOfHMetrics, int numGlyphs)
    {
        _data = data;
        // 表实际能容纳的条目数可能少于声明值：按实际长度截断，而不是在读取时越界
        _numberOfHMetrics = Math.Min(numberOfHMetrics, data.Length / 4);
        _numGlyphs = numGlyphs;
    }

    public ushort GetAdvance(int glyph)
    {
        if (_numberOfHMetrics == 0 || (uint)glyph >= (uint)_numGlyphs) return 0;
        int i = Math.Min(glyph, _numberOfHMetrics - 1);
        return Be.U16(_data.Span, i * 4);
    }

    public short GetLeftSideBearing(int glyph)
    {
        if ((uint)glyph >= (uint)_numGlyphs) return 0;
        var s = _data.Span;
        if (glyph < _numberOfHMetrics) return Be.I16(s, glyph * 4 + 2);
        int off = _numberOfHMetrics * 4 + (glyph - _numberOfHMetrics) * 2;
        return off + 2 <= s.Length ? Be.I16(s, off) : (short)0;
    }
}

/// <summary>'post' 表的头部（不解析字形名）。</summary>
internal sealed class PostTable
{
    public float ItalicAngle { get; private set; }
    public short UnderlinePosition { get; private set; }
    public short UnderlineThickness { get; private set; }
    public bool IsFixedPitch { get; private set; }

    public static PostTable Parse(ReadOnlySpan<byte> s)
    {
        if (s.Length < 16) throw new InvalidFontException("'post' table is too short.");
        return new PostTable
        {
            ItalicAngle = Be.Fixed(s, 4),
            UnderlinePosition = Be.I16(s, 8),
            UnderlineThickness = Be.I16(s, 10),
            IsFixedPitch = Be.U32(s, 12) != 0,
        };
    }
}

/// <summary>'OS/2' 表：字重、字宽、风格位与各类度量。</summary>
internal sealed class Os2Table
{
    public ushort Version { get; private set; }
    public short XAvgCharWidth { get; private set; }
    public ushort WeightClass { get; private set; }
    public ushort WidthClass { get; private set; }
    public short SubscriptYSize { get; private set; }
    public short StrikeoutSize { get; private set; }
    public short StrikeoutPosition { get; private set; }
    public ushort FsSelection { get; private set; }
    public bool HasTypoMetrics { get; private set; }
    public short TypoAscender { get; private set; }
    public short TypoDescender { get; private set; }
    public short TypoLineGap { get; private set; }
    public ushort WinAscent { get; private set; }
    public ushort WinDescent { get; private set; }
    public short XHeight { get; private set; }
    public short CapHeight { get; private set; }

    public bool IsItalic => (FsSelection & 0x0001) != 0;
    public bool IsBold => (FsSelection & 0x0020) != 0;
    public bool UseTypoMetrics => (FsSelection & 0x0080) != 0;
    public bool IsOblique => (FsSelection & 0x0200) != 0;

    public static Os2Table Parse(ReadOnlySpan<byte> s)
    {
        // 版本 0 的最短形式（Apple 早期）只有 68 字节，不含 typo 度量
        if (s.Length < 68) throw new InvalidFontException("'OS/2' table is too short.");
        var t = new Os2Table
        {
            Version = Be.U16(s, 0),
            XAvgCharWidth = Be.I16(s, 2),
            WeightClass = Be.U16(s, 4),
            WidthClass = Be.U16(s, 6),
            SubscriptYSize = Be.I16(s, 12),
            StrikeoutSize = Be.I16(s, 26),
            StrikeoutPosition = Be.I16(s, 28),
            FsSelection = Be.U16(s, 62),
        };
        if (s.Length >= 78)
        {
            t.HasTypoMetrics = true;
            t.TypoAscender = Be.I16(s, 68);
            t.TypoDescender = Be.I16(s, 70);
            t.TypoLineGap = Be.I16(s, 72);
            t.WinAscent = Be.U16(s, 74);
            t.WinDescent = Be.U16(s, 76);
        }
        if (t.Version >= 2 && s.Length >= 90)
        {
            t.XHeight = Be.I16(s, 86);
            t.CapHeight = Be.I16(s, 88);
        }
        return t;
    }
}
