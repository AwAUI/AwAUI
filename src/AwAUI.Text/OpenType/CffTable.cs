using System;
using System.Collections.Generic;

namespace AwAUI.Text.OpenType;

/// <summary>
/// 'CFF ' 与 'CFF2' 表：字形的 Type 2 字符串轮廓（Adobe TN #5176、#5177 与 OpenType 'CFF2' 规范）。
/// 支持名字键（非 CID）与 CID 键字体（FDArray + FDSelect 格式 0 / 3 / 4）、全局与局部子程序（含偏置）、
/// endchar 的 seac 式重音组合（经 charset 与标准编码查找字形），以及 CFF2 的 blend / vsindex（只取默认实例，ItemVariationStore 只解析各数据块的区域数）。
/// 字形前进量与边界不取自 CFF（使用 hmtx 与解码后的轮廓）。FontMatrix 被忽略：轮廓坐标按设计单位解释（与 head.unitsPerEm 一致的字体都如此）。
/// 解析时检查全部偏移；任何越界或不一致的数据都抛出 <see cref="InvalidFontException"/>。
/// 不可信输入：FDArray 中的许多字典（CFF2 最多 65535 个）可以指向同一个或彼此重叠的很大的 Private DICT；
/// Private DICT 按 (偏移, 长度) 只解析一次，且解析的总字节数与表长度成正比，超出时整张表视为损坏。
/// </summary>
internal sealed class CffTable
{
    /// <summary>INDEX 结构：<see cref="DataBase"/> 是「第一个数据字节之前一个字节」的位置（偏移以 1 为起点）。</summary>
    internal readonly struct Index
    {
        public Index(int count, int offSize, int offsetsPos, int dataBase, int end)
        {
            Count = count;
            OffSize = offSize;
            OffsetsPos = offsetsPos;
            DataBase = dataBase;
            End = end;
        }

        public int Count { get; }
        public int OffSize { get; }
        public int OffsetsPos { get; }
        public int DataBase { get; }
        /// <summary>INDEX 之后的第一个字节。</summary>
        public int End { get; }

        int ReadOffset(ReadOnlySpan<byte> s, int i)
        {
            int p = OffsetsPos + i * OffSize;
            return OffSize switch
            {
                1 => Be.U8(s, p),
                2 => Be.U16(s, p),
                3 => (Be.U8(s, p) << 16) | Be.U16(s, p + 1),
                _ => Be.I32(s, p),
            };
        }

        /// <summary>第 <paramref name="i"/> 个元素在表中的范围。</summary>
        public void Get(ReadOnlySpan<byte> s, int i, out int start, out int length)
        {
            if ((uint)i >= (uint)Count) throw new InvalidFontException($"CFF INDEX element {i} is out of range.");
            int a = ReadOffset(s, i), b = ReadOffset(s, i + 1);
            if (a < 1 || b < a || DataBase + (long)b > End) throw new InvalidFontException("CFF INDEX offsets are invalid.");
            start = DataBase + a;
            length = b - a;
        }

        public static Index Read(ReadOnlySpan<byte> s, int pos, bool cff2)
        {
            if (pos < 0 || pos > s.Length) throw new InvalidFontException("CFF INDEX offset is out of range.");
            int count = cff2 ? Be.I32(s, pos) : Be.U16(s, pos);
            int p = pos + (cff2 ? 4 : 2);
            if (count < 0) throw new InvalidFontException("CFF2 INDEX count is too large.");
            if (count == 0) return new Index(0, 1, p, p, p);
            int offSize = Be.U8(s, p);
            if (offSize < 1 || offSize > 4) throw new InvalidFontException("CFF INDEX offSize is invalid.");
            p++;
            long offsetsLength = ((long)count + 1) * offSize;
            if (p + offsetsLength > s.Length) throw new InvalidFontException("CFF INDEX offset array is truncated.");
            int dataBase = (int)(p + offsetsLength) - 1;
            var tmp = new Index(count, offSize, p, dataBase, s.Length);
            int last = tmp.ReadOffset(s, count);
            if (last < 1 || dataBase + (long)last > s.Length) throw new InvalidFontException("CFF INDEX data is truncated.");
            return new Index(count, offSize, p, dataBase, dataBase + last);
        }
    }

    /// <summary>一个字体字典（FD）的私有字典中轮廓解释需要的部分。</summary>
    internal sealed class FontDict
    {
        public Index LocalSubrs;
        public bool HasLocalSubrs;
        public int VsIndex;
    }

    // DICT 操作符（escape 操作符记为 1200 + b1）
    const int OpCharset = 15, OpCharStrings = 17, OpPrivate = 18, OpSubrs = 19, OpVsIndex = 22, OpBlend = 23, OpVarStore = 24;
    const int OpRos = 1230, OpCharstringType = 1206, OpFdArray = 1236, OpFdSelect = 1237;

    /// <summary>DICT 的操作数栈上限（CFF 规范为 48，CFF2 为 513）。</summary>
    internal const int MaxStackCff = 48, MaxStackCff2 = 513;

    readonly ReadOnlyMemory<byte> _data;
    readonly int _numGlyphs;
    readonly int _fdSelectOffset = -1;
    readonly int _charsetOffset;
    readonly int[]? _regionCounts;
    readonly double[] _dictStack;
    readonly Dictionary<long, FontDict> _privateDicts = new();
    long _privateBudget;

    public bool IsCff2 { get; }
    public bool IsCid { get; }
    internal Index CharStrings { get; }
    internal Index GlobalSubrs { get; }
    internal FontDict[] FontDicts { get; }

    CffTable(ReadOnlyMemory<byte> data, bool cff2, int numGlyphs)
    {
        _data = data;
        IsCff2 = cff2;
        var s = data.Span;
        int major = Be.U8(s, 0);
        int hdrSize = Be.U8(s, 2);
        if (major != (cff2 ? 2 : 1)) throw new InvalidFontException($"Unsupported CFF major version {major}.");
        if (hdrSize < 4 || hdrSize > s.Length) throw new InvalidFontException("CFF header size is invalid.");
        _dictStack = new double[cff2 ? MaxStackCff2 : MaxStackCff];
        _privateBudget = 4L * s.Length + 65536;

        var top = new Dictionary<int, double[]>();
        Index globalSubrs;
        if (cff2)
        {
            int topLength = Be.U16(s, 3);
            if (hdrSize < 5 || hdrSize + topLength > s.Length) throw new InvalidFontException("CFF2 top DICT is truncated.");
            ParseDict(s, hdrSize, topLength, top, true, _dictStack);
            globalSubrs = Index.Read(s, hdrSize + topLength, true);
        }
        else
        {
            var names = Index.Read(s, hdrSize, false);
            var topDicts = Index.Read(s, names.End, false);
            var strings = Index.Read(s, topDicts.End, false);
            globalSubrs = Index.Read(s, strings.End, false);
            if (topDicts.Count < 1) throw new InvalidFontException("CFF has no top DICT.");
            // OpenType 中的 CFF 只含一个字体；名字 INDEX 中的其余项被忽略
            topDicts.Get(s, 0, out int ts, out int tl);
            ParseDict(s, ts, tl, top, false, _dictStack);
        }
        GlobalSubrs = globalSubrs;

        if (!cff2 && top.TryGetValue(OpCharstringType, out var cst) && cst.Length > 0 && cst[0] != 2)
            throw new InvalidFontException("Only Type 2 charstrings are supported.");
        if (!top.TryGetValue(OpCharStrings, out var cs) || cs.Length < 1) throw new InvalidFontException("CFF has no CharStrings.");
        CharStrings = Index.Read(s, CheckedOffset(cs[0], s.Length), cff2);
        if (CharStrings.Count == 0) throw new InvalidFontException("CFF CharStrings INDEX is empty.");
        _numGlyphs = Math.Min(numGlyphs, CharStrings.Count);

        if (cff2 && top.TryGetValue(OpVarStore, out var vs) && vs.Length > 0)
            _regionCounts = ReadRegionCounts(s, CheckedOffset(vs[0], s.Length));
        IsCid = top.ContainsKey(OpRos) || (cff2 && top.ContainsKey(OpFdArray));
        if (top.TryGetValue(OpFdArray, out var fda) && fda.Length > 0 && (IsCid || cff2))
        {
            var fdIndex = Index.Read(s, CheckedOffset(fda[0], s.Length), cff2);
            if (fdIndex.Count == 0 || fdIndex.Count > 256 && !cff2) throw new InvalidFontException("CFF FDArray is invalid.");
            if (fdIndex.Count > 65535) throw new InvalidFontException("CFF2 FDArray is too large.");
            FontDicts = new FontDict[fdIndex.Count];
            var fd = new Dictionary<int, double[]>();
            for (int i = 0; i < fdIndex.Count; i++)
            {
                fdIndex.Get(s, i, out int fs, out int fl);
                fd.Clear();
                ParseDict(s, fs, fl, fd, cff2, _dictStack);
                FontDicts[i] = ReadPrivate(s, fd, cff2);
            }
            if (top.TryGetValue(OpFdSelect, out var fds) && fds.Length > 0) _fdSelectOffset = CheckedOffset(fds[0], s.Length);
            else if (FontDicts.Length > 1) throw new InvalidFontException("CFF FDSelect is missing.");
        }
        else
        {
            if (cff2) throw new InvalidFontException("CFF2 FDArray is missing.");
            FontDicts = new[] { ReadPrivate(s, top, false) };
        }

        if (!cff2 && !IsCid)
            _charsetOffset = top.TryGetValue(OpCharset, out var cso) && cso.Length > 0 ? CheckedOffset(cso[0], int.MaxValue) : 0;
    }

    public static CffTable Parse(ReadOnlyMemory<byte> data, bool cff2, int numGlyphs) => new(data, cff2, numGlyphs);

    public ReadOnlySpan<byte> Span => _data.Span;
    public int GlyphCount => _numGlyphs;

    static int CheckedOffset(double v, int limit)
    {
        if (!(v >= 0) || v > limit || v != Math.Floor(v)) throw new InvalidFontException("CFF offset is out of range.");
        return (int)v;
    }

    FontDict ReadPrivate(ReadOnlySpan<byte> s, Dictionary<int, double[]> dict, bool cff2)
    {
        if (!dict.TryGetValue(OpPrivate, out var p) || p.Length < 2) return new FontDict();
        int size = CheckedOffset(p[0], s.Length), offset = CheckedOffset(p[1], s.Length);
        if ((long)offset + size > s.Length) throw new InvalidFontException("CFF Private DICT is truncated.");
        long key = ((long)offset << 32) | (uint)size;
        if (_privateDicts.TryGetValue(key, out var shared)) return shared;
        _privateBudget -= size + 1;
        if (_privateBudget < 0) throw new InvalidFontException("CFF Private DICTs are too large in total.");
        var result = new FontDict();
        var priv = new Dictionary<int, double[]>();
        ParseDict(s, offset, size, priv, cff2, _dictStack, _regionCounts);
        if (priv.TryGetValue(OpSubrs, out var subrs) && subrs.Length > 0)
        {
            // Subrs 的偏移相对 Private DICT 的起点
            result.LocalSubrs = Index.Read(s, CheckedOffset(offset + subrs[0], s.Length), cff2);
            result.HasLocalSubrs = true;
        }
        if (cff2 && priv.TryGetValue(OpVsIndex, out var vsi) && vsi.Length > 0)
        {
            if (!(vsi[0] >= 0 && vsi[0] < 65536)) throw new InvalidFontException("CFF2 vsindex is invalid.");
            result.VsIndex = (int)vsi[0];
        }
        _privateDicts[key] = result;
        return result;
    }

    /// <summary>ItemVariationStore：只读出每个 ItemVariationData 的区域数（blend 需要它来确定操作数个数）。</summary>
    static int[] ReadRegionCounts(ReadOnlySpan<byte> s, int offset)
    {
        // CFF2 的 VarStore 以 2 字节长度开头，其后才是 ItemVariationStore
        int store = offset + 2;
        int count = Be.U16(s, store + 6);
        var result = new int[count];
        for (int i = 0; i < count; i++)
        {
            long ivd = store + (long)Be.U32(s, store + 8 + i * 4);
            if (ivd > s.Length) throw new InvalidFontException("CFF2 ItemVariationData offset is out of range.");
            result[i] = Be.U16(s, (int)ivd + 4);
        }
        return result;
    }

    /// <summary>vsindex 对应的区域数（blend 操作数的倍数）。</summary>
    internal int GetRegionCount(int vsIndex)
    {
        var rc = _regionCounts;
        if (rc is null || rc.Length == 0) return 0;
        if ((uint)vsIndex >= (uint)rc.Length) throw new InvalidFontException("CFF2 vsindex is out of range.");
        return rc[vsIndex];
    }

    /// <summary>字形所属的字体字典。</summary>
    internal FontDict GetFontDict(int glyph)
    {
        if (FontDicts.Length == 1 && _fdSelectOffset < 0) return FontDicts[0];
        int fd = SelectFd(Span, _fdSelectOffset, glyph, _numGlyphs);
        if ((uint)fd >= (uint)FontDicts.Length) throw new InvalidFontException("CFF FDSelect refers to a missing font DICT.");
        return FontDicts[fd];
    }

    static int SelectFd(ReadOnlySpan<byte> s, int offset, int glyph, int numGlyphs)
    {
        int format = Be.U8(s, offset);
        switch (format)
        {
            case 0:
                return Be.U8(s, offset + 1 + glyph);
            case 3:
            case 4:
            {
                bool wide = format == 4;
                int nRanges = wide ? Be.I32(s, offset + 1) : Be.U16(s, offset + 1);
                if (nRanges <= 0) throw new InvalidFontException("CFF FDSelect has no ranges.");
                int rec = wide ? 6 : 3;
                int first = offset + (wide ? 5 : 3);
                // 二分查找最后一个 first ≤ glyph 的区间
                int lo = 0, hi = nRanges - 1, found = -1;
                while (lo <= hi)
                {
                    int mid = (int)(((uint)lo + (uint)hi) >> 1);
                    int p = first + mid * rec;
                    long start = wide ? Be.U32(s, p) : Be.U16(s, p);
                    if (start <= glyph)
                    {
                        found = mid;
                        lo = mid + 1;
                    }
                    else hi = mid - 1;
                }
                if (found < 0) throw new InvalidFontException("CFF FDSelect does not cover the glyph.");
                int rp = first + found * rec;
                long sentinel = wide ? Be.U32(s, first + nRanges * rec) : Be.U16(s, first + nRanges * rec);
                if (glyph >= sentinel) throw new InvalidFontException("CFF FDSelect does not cover the glyph.");
                return wide ? Be.U16(s, rp + 4) : Be.U8(s, rp + 2);
            }
            default:
                throw new InvalidFontException($"Unsupported FDSelect format {format}.");
        }
    }

    // ------------------------------------------------------------------ seac 支持：标准编码 → SID → 字形

    /// <summary>标准编码中代码 161..255 对应的 SID（0 表示未定义）；32..126 的 SID 为代码 − 31。</summary>
    static ReadOnlySpan<byte> StandardEncodingHigh => new byte[]
    {
        96,97,98,99,100,101,102,103,104,105,106,107,108,109,110,0,111,112,113,114,0,115,116,117,118,119,120,121,122,0,123,
        0,124,125,126,127,128,129,130,131,0,132,133,0,134,135,136,137,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,138,0,139,0,0,0,0,140,141,142,143,0,0,0,0,0,144,0,0,0,145,0,0,146,147,148,149,0,0,0,0,
    };

    static int StandardEncodingSid(int code)
    {
        if (code >= 32 && code <= 126) return code - 31;
        if (code >= 161 && code <= 255) return StandardEncodingHigh[code - 161];
        return 0;
    }

    /// <summary>标准编码代码对应的字形（seac 的 bchar / achar）；找不到时返回 −1。</summary>
    internal int GlyphForStandardCode(int code)
    {
        if (IsCff2 || IsCid) return -1;
        int sid = StandardEncodingSid(code);
        if (sid == 0) return -1;
        var s = Span;
        int cs = _charsetOffset;
        if (cs == 0) return sid < _numGlyphs ? sid : -1;   // ISOAdobe：字形 ID 与 SID（≤ 228）相同
        if (cs <= 2) return -1;                            // Expert 字符集不含标准拉丁字符的完整集合
        int format = Be.U8(s, cs);
        int p = cs + 1;
        int gid = 1;
        switch (format)
        {
            case 0:
                for (; gid < _numGlyphs; gid++, p += 2)
                    if (Be.U16(s, p) == sid) return gid;
                return -1;
            case 1:
            case 2:
                while (gid < _numGlyphs)
                {
                    int first = Be.U16(s, p);
                    int left = format == 1 ? Be.U8(s, p + 2) : Be.U16(s, p + 2);
                    p += format == 1 ? 3 : 4;
                    if (sid >= first && sid <= first + left) return gid + (sid - first);
                    gid += left + 1;
                }
                return -1;
            default:
                return -1;
        }
    }

    // ------------------------------------------------------------------ DICT

    static void ParseDict(ReadOnlySpan<byte> s, int start, int length, Dictionary<int, double[]> dict, bool cff2, double[] stack,
        int[]? regionCounts = null)
    {
        int vsIndex = 0;
        if (start < 0 || length < 0 || (long)start + length > s.Length) throw new InvalidFontException("CFF DICT is out of range.");
        int maxStack = cff2 ? MaxStackCff2 : MaxStackCff;
        int sp = 0;
        int p = start, end = start + length;
        while (p < end)
        {
            int b0 = s[p];
            if (b0 <= 21 || cff2 && b0 >= 22 && b0 <= 24)
            {
                int op = b0;
                p++;
                if (b0 == 12)
                {
                    if (p >= end) throw new InvalidFontException("CFF DICT escape operator is truncated.");
                    op = 1200 + s[p++];
                }
                if (op == OpBlend && cff2)
                {
                    // blend：栈顶为 n，其下是 n 个默认值与 n × k 个区域增量；默认实例只保留默认值
                    if (sp < 1) throw new InvalidFontException("CFF2 DICT blend stack underflow.");
                    int n = (int)stack[--sp];
                    int k = regionCounts is { Length: > 0 } rc && (uint)vsIndex < (uint)rc.Length ? rc[vsIndex] : 0;
                    long consumed = (long)n * (k + 1);
                    if (n < 0 || consumed > sp) throw new InvalidFontException("CFF2 DICT blend operand count is invalid.");
                    sp = sp - (int)consumed + n;
                    continue;
                }
                if (op == OpVsIndex && sp > 0) vsIndex = (int)stack[0];
                dict[op] = stack.AsSpan(0, sp).ToArray();
                sp = 0;
                continue;
            }
            if (sp >= maxStack) throw new InvalidFontException("CFF DICT operand stack overflow.");
            if (b0 == 28)
            {
                stack[sp++] = Be.I16(s, p + 1);
                p += 3;
            }
            else if (b0 == 29)
            {
                stack[sp++] = Be.I32(s, p + 1);
                p += 5;
            }
            else if (b0 == 30)
            {
                stack[sp++] = ReadReal(s, ref p, end);
            }
            else if (b0 >= 32 && b0 <= 246)
            {
                stack[sp++] = b0 - 139;
                p++;
            }
            else if (b0 >= 247 && b0 <= 250)
            {
                stack[sp++] = (b0 - 247) * 256 + Be.U8(s, p + 1) + 108;
                p += 2;
            }
            else if (b0 >= 251 && b0 <= 254)
            {
                stack[sp++] = -(b0 - 251) * 256 - Be.U8(s, p + 1) - 108;
                p += 2;
            }
            else throw new InvalidFontException($"Invalid CFF DICT byte {b0}.");
            if (p > end) throw new InvalidFontException("CFF DICT operand is truncated.");
        }
    }

    /// <summary>BCD 实数（每字节两个半字节，0xF 结束）。</summary>
    static double ReadReal(ReadOnlySpan<byte> s, ref int p, int end)
    {
        p++;
        Span<char> buf = stackalloc char[64];
        int n = 0;
        while (true)
        {
            if (p >= end) throw new InvalidFontException("CFF DICT real number is truncated.");
            int b = s[p++];
            for (int k = 0; k < 2; k++)
            {
                int nib = k == 0 ? b >> 4 : b & 0xF;
                if (nib == 0xF) return Parse(buf.Slice(0, n));
                string piece = nib switch
                {
                    <= 9 => ((char)('0' + nib)).ToString(),
                    0xA => ".",
                    0xB => "E",
                    0xC => "E-",
                    0xE => "-",
                    _ => "",
                };
                foreach (char c in piece)
                    if (n < buf.Length) buf[n++] = c;
            }
        }

        static double Parse(ReadOnlySpan<char> text) =>
            double.TryParse(text.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
