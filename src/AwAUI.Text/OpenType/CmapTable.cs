using System;
using System.Collections.Generic;

namespace AwAUI.Text.OpenType;

/// <summary>
/// 'cmap' 表：从全部子表中挑出最合适的一张 Unicode 映射。
/// 支持格式 0、4、6、12、13；Windows Symbol (3,0) 与 Mac Roman (1,0) 作为最后的选择。
/// 子表在解析时就校验数组范围，查找过程不会越界。
/// </summary>
internal sealed class CmapTable
{
    enum Mode : byte { Unicode, Symbol, MacRoman }

    readonly Subtable _subtable;
    readonly Mode _mode;

    CmapTable(Subtable subtable, Mode mode, int platform, int encoding)
    {
        _subtable = subtable;
        _mode = mode;
        PlatformId = platform;
        EncodingId = encoding;
    }

    /// <summary>选中的子表所属的平台、编码与格式（诊断用）。</summary>
    public int PlatformId { get; }
    public int EncodingId { get; }
    public int Format => _subtable.Format;

    public ushort Map(int codePoint)
    {
        switch (_mode)
        {
            case Mode.Unicode:
                return _subtable.Map(codePoint);
            case Mode.Symbol:
            {
                ushort g = _subtable.Map(codePoint);
                // 符号字体通常把字符放在 U+F020–U+F0FF
                if (g == 0 && codePoint >= 0 && codePoint <= 0xFF) g = _subtable.Map(0xF000 + codePoint);
                return g;
            }
            default:
                return MacRoman.TryEncode(codePoint, out byte b) ? _subtable.Map(b) : (ushort)0;
        }
    }

    /// <summary>解析 cmap；没有可用的子表时返回 null。</summary>
    public static CmapTable? Parse(ReadOnlyMemory<byte> table)
    {
        var s = table.Span;
        if (s.Length < 4) return null;
        int count = Be.U16(s, 2);
        var candidates = new List<(int Score, int Platform, int Encoding, int Offset)>();
        for (int i = 0; i < count; i++)
        {
            int rec = 4 + i * 8;
            if (rec + 8 > s.Length) break;
            int platform = Be.U16(s, rec), encoding = Be.U16(s, rec + 2);
            uint offset = Be.U32(s, rec + 4);
            if (offset + 2 > (uint)s.Length) continue;
            int format = Be.U16(s, (int)offset);
            int score = Score(platform, encoding, format);
            if (score > 0) candidates.Add((score, platform, encoding, (int)offset));
        }
        // 稳定排序：分数高者优先，同分保持表中顺序
        candidates.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Offset.CompareTo(b.Offset));
        foreach (var c in candidates)
        {
            Subtable? sub;
            try
            {
                sub = Subtable.Parse(table.Slice(c.Offset));
            }
            catch (InvalidFontException)
            {
                sub = null;
            }
            if (sub is null) continue;
            var mode = c.Platform == 3 && c.Encoding == 0 ? Mode.Symbol
                : c.Platform == 1 ? Mode.MacRoman
                : Mode.Unicode;
            return new CmapTable(sub, mode, c.Platform, c.Encoding);
        }
        return null;
    }

    static int Score(int platform, int encoding, int format)
    {
        bool full = format == 12 || format == 13;
        bool supported = full || format == 0 || format == 4 || format == 6;
        if (!supported) return 0;
        if (platform == 3 && encoding == 10) return format == 12 ? 100 : full ? 60 : 0;
        if (platform == 0 && (encoding == 4 || encoding == 6)) return format == 12 ? 99 : full ? 59 : 0;
        if (platform == 3 && encoding == 1) return full ? 0 : 80;
        if (platform == 0 && encoding <= 3) return full ? 70 : 79;
        if (platform == 3 && encoding == 0) return 40;
        if (platform == 1 && encoding == 0) return format == 0 || format == 6 ? 20 : 0;
        return 0;
    }

    abstract class Subtable
    {
        protected Subtable(ReadOnlyMemory<byte> data, int format)
        {
            Data = data;
            Format = format;
        }

        protected ReadOnlyMemory<byte> Data { get; }
        public int Format { get; }

        public abstract ushort Map(int codePoint);

        public static Subtable? Parse(ReadOnlyMemory<byte> data)
        {
            var s = data.Span;
            int format = Be.U16(s, 0);
            switch (format)
            {
                case 0:
                    return s.Length >= 6 + 256 ? new Format0(data) : null;
                case 4:
                    return Format4.Create(data);
                case 6:
                    return Format6.Create(data);
                case 12:
                case 13:
                    return Format12.Create(data, format);
                default:
                    return null;
            }
        }
    }

    sealed class Format0 : Subtable
    {
        public Format0(ReadOnlyMemory<byte> data) : base(data, 0) { }

        public override ushort Map(int codePoint) =>
            (uint)codePoint < 256 ? Data.Span[6 + codePoint] : (ushort)0;
    }

    sealed class Format4 : Subtable
    {
        readonly int _segCount;

        Format4(ReadOnlyMemory<byte> data, int segCount) : base(data, 4) => _segCount = segCount;

        public static Format4? Create(ReadOnlyMemory<byte> data)
        {
            var s = data.Span;
            int segCount = Be.U16(s, 6) / 2;
            // 头部声明的 length 在大字体中常常溢出，所以以实际可用数据为准
            if (segCount == 0 || 16 + segCount * 8 > s.Length) return null;
            return new Format4(data, segCount);
        }

        public override ushort Map(int codePoint)
        {
            if ((uint)codePoint > 0xFFFF) return 0;
            var s = Data.Span;
            int endCodes = 14, startCodes = 16 + _segCount * 2, deltas = startCodes + _segCount * 2, ranges = deltas + _segCount * 2;
            int lo = 0, hi = _segCount - 1;
            // 找到第一个 endCode >= codePoint 的段
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (Be.U16(s, endCodes + mid * 2) < codePoint) lo = mid + 1;
                else hi = mid;
            }
            int seg = lo;
            if (Be.U16(s, endCodes + seg * 2) < codePoint) return 0;
            int start = Be.U16(s, startCodes + seg * 2);
            if (codePoint < start) return 0;
            int delta = Be.U16(s, deltas + seg * 2);
            int rangeOffsetPos = ranges + seg * 2;
            int rangeOffset = Be.U16(s, rangeOffsetPos);
            if (rangeOffset == 0) return (ushort)((codePoint + delta) & 0xFFFF);
            int addr = rangeOffsetPos + rangeOffset + (codePoint - start) * 2;
            if (addr + 2 > s.Length) return 0;
            int g = Be.U16(s, addr);
            return g == 0 ? (ushort)0 : (ushort)((g + delta) & 0xFFFF);
        }
    }

    sealed class Format6 : Subtable
    {
        readonly int _first, _count;

        Format6(ReadOnlyMemory<byte> data, int first, int count) : base(data, 6)
        {
            _first = first;
            _count = count;
        }

        public static Format6? Create(ReadOnlyMemory<byte> data)
        {
            var s = data.Span;
            if (s.Length < 10) return null;
            int first = Be.U16(s, 6);
            int count = Math.Min(Be.U16(s, 8), (s.Length - 10) / 2);
            return new Format6(data, first, count);
        }

        public override ushort Map(int codePoint)
        {
            int i = codePoint - _first;
            return (uint)i < (uint)_count ? Be.U16(Data.Span, 10 + i * 2) : (ushort)0;
        }
    }

    /// <summary>格式 12（分段线性）与 13（多对一）共用的分组结构。</summary>
    sealed class Format12 : Subtable
    {
        readonly int _groups;

        Format12(ReadOnlyMemory<byte> data, int format, int groups) : base(data, format) => _groups = groups;

        public static Format12? Create(ReadOnlyMemory<byte> data, int format)
        {
            var s = data.Span;
            if (s.Length < 16) return null;
            uint n = Be.U32(s, 12);
            int groups = (int)Math.Min(n, (uint)((s.Length - 16) / 12));
            return groups == 0 ? null : new Format12(data, format, groups);
        }

        public override ushort Map(int codePoint)
        {
            if ((uint)codePoint > 0x10FFFF) return 0;
            var s = Data.Span;
            uint cp = (uint)codePoint;
            int lo = 0, hi = _groups - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                int g = 16 + mid * 12;
                uint start = Be.U32(s, g), end = Be.U32(s, g + 4);
                if (cp < start) hi = mid - 1;
                else if (cp > end) lo = mid + 1;
                else
                {
                    uint glyph = Be.U32(s, g + 8);
                    if (Format == 12) glyph += cp - start;
                    return glyph <= 0xFFFF ? (ushort)glyph : (ushort)0;
                }
            }
            return 0;
        }
    }
}
