using System;
using System.IO;

namespace AwAUI.Imaging.WebP;

/// <summary>VP8L 的位读取器：低位在前。读过数据末尾时返回 0 并置 <see cref="Eos"/>（之后的结果均视为损坏）。</summary>
internal sealed class Vp8LBitReader
{
    readonly byte[] _data;
    readonly int _end;
    int _pos;
    ulong _buf;
    int _nbits;

    public Vp8LBitReader(byte[] data, int offset, int length)
    {
        _data = data;
        _pos = offset;
        _end = offset + length;
    }

    /// <summary>是否已经消耗了比数据更多的位。</summary>
    public bool Eos { get; private set; }

    void Fill()
    {
        while (_nbits <= 56 && _pos < _end)
        {
            _buf |= (ulong)_data[_pos++] << _nbits;
            _nbits += 8;
        }
    }

    public uint ReadBits(int n)
    {
        if (n == 0) return 0;
        if (_nbits < n)
        {
            Fill();
            if (_nbits < n)
            {
                Eos = true;
                uint partial = (uint)_buf & (uint)((1UL << n) - 1);
                _buf = 0;
                _nbits = 0;
                return partial;
            }
        }
        uint v = (uint)(_buf & ((1UL << n) - 1));
        _buf >>= n;
        _nbits -= n;
        return v;
    }

    /// <summary>预取至少 15 位（不足时高位为 0）。</summary>
    public uint Peek()
    {
        if (_nbits < 32) Fill();
        return (uint)_buf;
    }

    public void Skip(int n)
    {
        if (n > _nbits)
        {
            Eos = true;
            _buf = 0;
            _nbits = 0;
            return;
        }
        _buf >>= n;
        _nbits -= n;
    }
}

/// <summary>
/// 规范 Huffman 码的两级查找表（与 libwebp 相同的构造：根表 min(8, 最长码长) 位，更长的码进入二级表）。
/// 表项 = 码长 &lt;&lt; 16 | 符号；根表中指向二级表的项为 (根位数 + 二级位数) &lt;&lt; 16 | 二级表相对偏移。
/// 只接受完整的前缀码（或只有一个符号的码，读取时不消耗位），否则视为损坏。
/// </summary>
internal sealed class HuffmanTables
{
    const int MaxCodeLength = 15;
    int[] _table = new int[1024];
    int _count;
    int[] _sorted = new int[64];

    public int[] Table => _table;
    public int Count => _count;

    public void Clear() => _count = 0;

    void Ensure(int size)
    {
        if (size <= _table.Length) return;
        int n = _table.Length;
        while (n < size) n *= 2;
        Array.Resize(ref _table, n);
    }

    /// <summary>按码长构造一棵树。返回树在表中的位置；码不合法时返回 false。</summary>
    public bool Build(ReadOnlySpan<byte> codeLengths, out int offset, out int rootBits)
    {
        offset = _count;
        rootBits = 0;
        Span<int> count = stackalloc int[MaxCodeLength + 1];
        Span<int> start = stackalloc int[MaxCodeLength + 2];
        int n = 0, maxLen = 0;
        for (int s = 0; s < codeLengths.Length; s++)
        {
            int len = codeLengths[s];
            if (len > MaxCodeLength) return false;
            count[len]++;
            if (len > 0)
            {
                n++;
                if (len > maxLen) maxLen = len;
            }
        }
        if (n == 0) return false;
        for (int len = 1; len <= MaxCodeLength; len++)
        {
            if (count[len] > (1 << len)) return false;
            start[len + 1] = start[len] + count[len];
        }
        if (_sorted.Length < n) _sorted = new int[Math.Max(n, _sorted.Length * 2)];
        for (int s = 0; s < codeLengths.Length; s++)
            if (codeLengths[s] > 0) _sorted[start[codeLengths[s]]++] = s;

        if (n == 1)
        {
            Ensure(_count + 1);
            _table[_count++] = _sorted[0];   // 0 位
            return true;
        }

        rootBits = Math.Min(8, maxLen);
        int tableSize = 1 << rootBits, totalSize = tableSize;
        Ensure(offset + totalSize);
        int key = 0, numNodes = 1, numOpen = 1, symbol = 0;
        for (int len = 1, step = 2; len <= rootBits; len++, step <<= 1)
        {
            numOpen <<= 1;
            numNodes += numOpen;
            numOpen -= count[len];
            if (numOpen < 0) return false;
            for (; count[len] > 0; count[len]--)
            {
                Replicate(offset + key, step, tableSize, len << 16 | _sorted[symbol++]);
                key = NextKey(key, len);
            }
        }
        int mask = tableSize - 1, low = -1, cur = offset, curSize = tableSize;
        for (int len = rootBits + 1, step = 2; len <= MaxCodeLength; len++, step <<= 1)
        {
            numOpen <<= 1;
            numNodes += numOpen;
            numOpen -= count[len];
            if (numOpen < 0) return false;
            for (; count[len] > 0; count[len]--)
            {
                if ((key & mask) != low)
                {
                    cur += curSize;
                    int bits = NextTableBits(count, len, rootBits);
                    curSize = 1 << bits;
                    totalSize += curSize;
                    Ensure(offset + totalSize);
                    low = key & mask;
                    _table[offset + low] = (bits + rootBits) << 16 | (cur - offset);
                }
                Replicate(cur + (key >> rootBits), step, curSize, (len - rootBits) << 16 | _sorted[symbol++]);
                key = NextKey(key, len);
            }
        }
        if (numNodes != 2 * n - 1) return false;
        _count = offset + totalSize;
        return true;
    }

    void Replicate(int at, int step, int end, int code)
    {
        do
        {
            end -= step;
            _table[at + end] = code;
        } while (end > 0);
    }

    static int NextKey(int key, int len)
    {
        int step = 1 << (len - 1);
        while ((key & step) != 0) step >>= 1;
        return step != 0 ? (key & (step - 1)) + step : key;
    }

    static int NextTableBits(Span<int> count, int len, int rootBits)
    {
        int left = 1 << (len - rootBits);
        while (len < MaxCodeLength)
        {
            left -= count[len];
            if (left <= 0) break;
            len++;
            left <<= 1;
        }
        return len - rootBits;
    }

    public static int ReadSymbol(int[] table, int offset, int rootBits, Vp8LBitReader br)
    {
        uint bits = br.Peek();
        int e = table[offset + (int)(bits & ((1u << rootBits) - 1))];
        int len = e >> 16;
        if (len > rootBits)
        {
            int sub = len - rootBits;
            e = table[offset + (e & 0xFFFF) + (int)((bits >> rootBits) & ((1u << sub) - 1))];
            br.Skip(rootBits + (e >> 16));
            return e & 0xFFFF;
        }
        br.Skip(len);
        return e & 0xFFFF;
    }
}

/// <summary>一组 5 棵树：绿色 + 长度前缀 + 颜色缓存、红、蓝、alpha、距离前缀。</summary>
internal struct HTreeGroup
{
    public int GreenOff, GreenBits, RedOff, RedBits, BlueOff, BlueBits, AlphaOff, AlphaBits, DistOff, DistBits;
}

/// <summary>
/// VP8L（WebP 无损）解码：四种变换（预测、交叉颜色、减绿、调色板含像素打包）、颜色缓存、元 Huffman 码、
/// LZ77 后向引用。解码结果为 ARGB（uint，a 在最高字节）。
/// <para>
/// 加固：所有长度、距离、索引都做边界检查；Huffman 码必须完整；表项总数与像素数都受输入大小约束
/// （见 <see cref="DecodeLimits"/>）；耗时与像素数和输入位数成线性关系。
/// </para>
/// </summary>
internal sealed class Vp8LDecoder
{
    const int NumLiteralCodes = 256, NumLengthCodes = 24, NumDistanceCodes = 40, MaxCacheBits = 11;
    static readonly byte[] CodeLengthOrder = { 17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

    static readonly byte[] CodeToPlane =
    {
        0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a, 0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a,
        0x25, 0x2b, 0x48, 0x04, 0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45, 0x4b, 0x34, 0x3c, 0x03,
        0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d, 0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
        0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e, 0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f,
        0x64, 0x6c, 0x42, 0x4e, 0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e, 0x00, 0x74, 0x7c, 0x41,
        0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d, 0x51, 0x5f, 0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70,
    };

    readonly Vp8LBitReader _br;
    readonly int _inputLength;
    readonly HuffmanTables _scratch = new();
    readonly byte[] _codeLengths = new byte[NumLiteralCodes + NumLengthCodes + (1 << MaxCacheBits)];
    long _tableEntries;

    Vp8LDecoder(byte[] data, int offset, int length, int inputLength)
    {
        _br = new Vp8LBitReader(data, offset, length);
        _inputLength = inputLength;
    }

    /// <summary>读取 VP8L 头：签名、宽高、alpha 提示位、版本。</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> data, out int width, out int height, out bool hasAlpha)
    {
        width = height = 0;
        hasAlpha = false;
        if (data.Length < 5 || data[0] != 0x2F) return false;
        uint bits = (uint)(data[1] | data[2] << 8 | data[3] << 16 | data[4] << 24);
        width = (int)(bits & 0x3FFF) + 1;
        height = (int)((bits >> 14) & 0x3FFF) + 1;
        hasAlpha = ((bits >> 28) & 1) != 0;
        return (bits >> 29) == 0;
    }

    /// <summary>解码一个完整的 VP8L 位流（含 5 字节头）。</summary>
    public static uint[] Decode(byte[] data, int offset, int length, int inputLength, out int width, out int height)
    {
        if (!TryReadHeader(data.AsSpan(offset, length), out width, out height, out _))
            throw new InvalidDataException("Invalid VP8L header.");
        DecodeLimits.CheckBudget((long)width * height, inputLength, "WebP");
        var dec = new Vp8LDecoder(data, offset + 5, length - 5, inputLength);
        return dec.DecodeMain(width, height);
    }

    /// <summary>解码 ALPH 块中无头的 VP8L 位流，返回绿色通道。</summary>
    public static byte[] DecodeAlpha(byte[] data, int offset, int length, int width, int height, int inputLength)
    {
        var dec = new Vp8LDecoder(data, offset, length, inputLength);
        var argb = dec.DecodeMain(width, height);
        var alpha = new byte[argb.Length];
        for (int i = 0; i < argb.Length; i++) alpha[i] = (byte)(argb[i] >> 8);
        return alpha;
    }

    sealed class Transform
    {
        public int Type, Bits, XSize, YSize;
        public uint[] Data = Array.Empty<uint>();
    }

    uint[] DecodeMain(int width, int height)
    {
        var transforms = new Transform[4];
        int count = 0, seen = 0;
        int xsize = width;
        while (_br.ReadBits(1) != 0)
        {
            int type = (int)_br.ReadBits(2);
            if ((seen & (1 << type)) != 0) throw new InvalidDataException("Repeated VP8L transform.");
            seen |= 1 << type;
            var t = new Transform { Type = type, XSize = xsize, YSize = height };
            switch (type)
            {
                case 0:   // 预测
                case 1:   // 交叉颜色
                    t.Bits = (int)_br.ReadBits(3) + 2;
                    t.Data = DecodeStream(SubSample(xsize, t.Bits), SubSample(height, t.Bits), false);
                    break;
                case 3:   // 调色板
                {
                    int colors = (int)_br.ReadBits(8) + 1;
                    t.Bits = colors > 16 ? 0 : colors > 4 ? 1 : colors > 2 ? 2 : 3;
                    var palette = DecodeStream(colors, 1, false);
                    // 调色板逐项差分编码；扩展到 1 << (8 >> bits) 项，表外索引为透明黑
                    var expanded = new uint[1 << (8 >> t.Bits)];
                    expanded[0] = palette[0];
                    for (int i = 1; i < colors; i++) expanded[i] = AddPixels(palette[i], expanded[i - 1]);
                    t.Data = expanded;
                    xsize = SubSample(xsize, t.Bits);
                    break;
                }
            }
            if (_br.Eos) throw new InvalidDataException("Truncated VP8L data.");
            transforms[count++] = t;
        }

        var pixels = DecodeStream(xsize, height, true);
        for (int i = count - 1; i >= 0; i--) pixels = Inverse(transforms[i], pixels);
        return pixels;
    }

    static int SubSample(int size, int bits) => (size + (1 << bits) - 1) >> bits;

    /// <summary>解码一个图像流（主图像或实体图像：变换数据、调色板、元 Huffman 图）。</summary>
    uint[] DecodeStream(int xsize, int ysize, bool isLevel0)
    {
        int cacheBits = 0;
        if (_br.ReadBits(1) != 0)
        {
            cacheBits = (int)_br.ReadBits(4);
            if (cacheBits < 1 || cacheBits > MaxCacheBits) throw new InvalidDataException("Invalid VP8L color cache size.");
        }

        int huffBits = 0, huffXSize = 0;
        int[]? groupMap = null;
        int groupCount = 1;
        if (isLevel0 && _br.ReadBits(1) != 0)
        {
            huffBits = (int)_br.ReadBits(3) + 2;
            huffXSize = SubSample(xsize, huffBits);
            var image = DecodeStream(huffXSize, SubSample(ysize, huffBits), false);
            groupMap = new int[image.Length];
            int max = 0;
            for (int i = 0; i < image.Length; i++)
            {
                int g = (int)(image[i] >> 8) & 0xFFFF;
                groupMap[i] = g;
                if (g > max) max = g;
            }
            groupCount = max + 1;
        }
        if (_br.Eos) throw new InvalidDataException("Truncated VP8L data.");

        // 只为实体图像中用到的组建表；未用到的组仍读取并校验
        int[] remap;
        int used;
        if (groupMap != null)
        {
            remap = new int[groupCount];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            used = 0;
            for (int i = 0; i < groupMap.Length; i++)
            {
                int g = groupMap[i];
                if (remap[g] < 0) remap[g] = used++;
                groupMap[i] = remap[g];
            }
        }
        else
        {
            remap = new[] { 0 };
            used = 1;
        }

        var tables = new HuffmanTables();
        var groups = new HTreeGroup[used];
        int greenSize = NumLiteralCodes + NumLengthCodes + (cacheBits > 0 ? 1 << cacheBits : 0);
        for (int g = 0; g < groupCount; g++)
        {
            var target = remap[g] >= 0 ? tables : null;
            ref var grp = ref (remap[g] >= 0 ? ref groups[remap[g]] : ref _dummy);
            ReadCode(greenSize, target, out grp.GreenOff, out grp.GreenBits);
            ReadCode(NumLiteralCodes, target, out grp.RedOff, out grp.RedBits);
            ReadCode(NumLiteralCodes, target, out grp.BlueOff, out grp.BlueBits);
            ReadCode(NumLiteralCodes, target, out grp.AlphaOff, out grp.AlphaBits);
            ReadCode(NumDistanceCodes, target, out grp.DistOff, out grp.DistBits);
            if (_br.Eos) throw new InvalidDataException("Truncated VP8L Huffman codes.");
        }

        var pixels = new uint[checked(xsize * ysize)];
        DecodePixels(pixels, xsize, ysize, tables.Table, groups, groupMap, huffBits, huffXSize, cacheBits);
        return pixels;
    }

    HTreeGroup _dummy;

    /// <summary>读取一棵 Huffman 树。<paramref name="target"/> 为 null 时只校验、不保留。</summary>
    void ReadCode(int alphabetSize, HuffmanTables? target, out int offset, out int rootBits)
    {
        var lengths = _codeLengths.AsSpan(0, alphabetSize);
        lengths.Clear();
        if (_br.ReadBits(1) != 0)
        {
            // 简单码：1 或 2 个符号
            int numSymbols = (int)_br.ReadBits(1) + 1;
            int firstBits = _br.ReadBits(1) == 0 ? 1 : 8;
            int s0 = (int)_br.ReadBits(firstBits);
            if (s0 < alphabetSize) lengths[s0] = 1;
            if (numSymbols == 2)
            {
                int s1 = (int)_br.ReadBits(8);
                if (s1 < alphabetSize) lengths[s1] = 1;
            }
        }
        else
        {
            Span<byte> clLengths = stackalloc byte[19];
            int numCodes = (int)_br.ReadBits(4) + 4;
            for (int i = 0; i < numCodes; i++) clLengths[CodeLengthOrder[i]] = (byte)_br.ReadBits(3);
            ReadCodeLengths(clLengths, lengths);
        }
        if (_br.Eos) throw new InvalidDataException("Truncated VP8L Huffman code.");
        var tables = target ?? _scratch;
        if (target == null) _scratch.Clear();
        if (!tables.Build(lengths, out offset, out rootBits)) throw new InvalidDataException("Invalid VP8L Huffman code.");
        if (target != null)
        {
            _tableEntries = target.Count;
            if (_tableEntries > (1L << 20) + 128L * _inputLength)
                throw new NotSupportedException("VP8L Huffman tables are too large for the input size.");
        }
    }

    void ReadCodeLengths(ReadOnlySpan<byte> clLengths, Span<byte> lengths)
    {
        _scratch.Clear();
        if (!_scratch.Build(clLengths, out int clOff, out int clBits)) throw new InvalidDataException("Invalid VP8L code length code.");
        var clTable = _scratch.Table;
        int numSymbols = lengths.Length;
        int maxSymbol;
        if (_br.ReadBits(1) != 0)
        {
            int nbits = 2 + 2 * (int)_br.ReadBits(3);
            maxSymbol = 2 + (int)_br.ReadBits(nbits);
            if (maxSymbol > numSymbols) throw new InvalidDataException("Invalid VP8L code length count.");
        }
        else maxSymbol = numSymbols;

        int symbol = 0, prev = 8;
        while (symbol < numSymbols)
        {
            if (maxSymbol-- == 0) break;
            if (_br.Eos) throw new InvalidDataException("Truncated VP8L code lengths.");
            int len = HuffmanTables.ReadSymbol(clTable, clOff, clBits, _br);
            if (len < 16)
            {
                lengths[symbol++] = (byte)len;
                if (len != 0) prev = len;
            }
            else
            {
                int slot = len - 16;
                int extra = slot == 0 ? 2 : slot == 1 ? 3 : 7;
                int offset = slot == 2 ? 11 : 3;
                int repeat = (int)_br.ReadBits(extra) + offset;
                if (symbol + repeat > numSymbols) throw new InvalidDataException("Invalid VP8L code length repeat.");
                byte value = (byte)(slot == 0 ? prev : 0);
                lengths.Slice(symbol, repeat).Fill(value);
                symbol += repeat;
            }
        }
    }

    int CopyValue(int symbol)
    {
        if (symbol < 4) return symbol + 1;
        int extra = (symbol - 2) >> 1;
        int offset = (2 + (symbol & 1)) << extra;
        return offset + (int)_br.ReadBits(extra) + 1;
    }

    static int PlaneCodeToDistance(int xsize, int planeCode)
    {
        if (planeCode > 120) return planeCode - 120;
        int code = CodeToPlane[planeCode - 1];
        int yoffset = code >> 4, xoffset = 8 - (code & 0xF);
        long dist = (long)yoffset * xsize + xoffset;
        return dist >= 1 ? (int)Math.Min(dist, int.MaxValue) : 1;
    }

    void DecodePixels(uint[] pixels, int width, int height, int[] table, HTreeGroup[] groups, int[]? groupMap, int huffBits, int huffXSize, int cacheBits)
    {
        uint[]? cache = cacheBits > 0 ? new uint[1 << cacheBits] : null;
        int cacheShift = 32 - cacheBits;
        int lengthLimit = NumLiteralCodes + NumLengthCodes;
        int mask = groupMap == null ? -1 : (1 << huffBits) - 1;
        int total = pixels.Length;
        int pos = 0, col = 0, row = 0, lastCached = 0;
        var g = groups[0];
        while (pos < total)
        {
            if ((col & mask) == 0 && groupMap != null) g = groups[groupMap[(row >> huffBits) * huffXSize + (col >> huffBits)]];
            int code = HuffmanTables.ReadSymbol(table, g.GreenOff, g.GreenBits, _br);
            if (code < NumLiteralCodes)
            {
                int red = HuffmanTables.ReadSymbol(table, g.RedOff, g.RedBits, _br);
                int blue = HuffmanTables.ReadSymbol(table, g.BlueOff, g.BlueBits, _br);
                int alpha = HuffmanTables.ReadSymbol(table, g.AlphaOff, g.AlphaBits, _br);
                pixels[pos++] = (uint)(alpha << 24 | red << 16 | code << 8 | blue);
                if (++col >= width)
                {
                    col = 0;
                    row++;
                    if (_br.Eos) throw new InvalidDataException("Truncated VP8L image data.");
                }
            }
            else if (code < lengthLimit)
            {
                int length = CopyValue(code - NumLiteralCodes);
                int distSymbol = HuffmanTables.ReadSymbol(table, g.DistOff, g.DistBits, _br);
                int dist = PlaneCodeToDistance(width, CopyValue(distSymbol));
                if (_br.Eos) throw new InvalidDataException("Truncated VP8L image data.");
                if (dist > pos || length > total - pos) throw new InvalidDataException("Invalid VP8L backward reference.");
                for (int i = 0; i < length; i++, pos++) pixels[pos] = pixels[pos - dist];
                col += length;
                while (col >= width)
                {
                    col -= width;
                    row++;
                }
                if (pos < total && groupMap != null && (col & mask) != 0)
                    g = groups[groupMap[(row >> huffBits) * huffXSize + (col >> huffBits)]];
            }
            else
            {
                int key = code - lengthLimit;
                if (cache == null || key >= cache.Length) throw new InvalidDataException("Invalid VP8L color cache code.");
                while (lastCached < pos)
                {
                    uint p = pixels[lastCached++];
                    cache[(0x1E35A7BDu * p) >> cacheShift] = p;
                }
                pixels[pos++] = cache[key];
                if (++col >= width)
                {
                    col = 0;
                    row++;
                }
            }
            if (cache != null)
            {
                while (lastCached < pos)
                {
                    uint p = pixels[lastCached++];
                    cache[(0x1E35A7BDu * p) >> cacheShift] = p;
                }
            }
        }
        if (_br.Eos) throw new InvalidDataException("Truncated VP8L image data.");
    }

    // ------------------------------------------------------------------ 逆变换

    static uint[] Inverse(Transform t, uint[] pixels)
    {
        switch (t.Type)
        {
            case 0:
                InversePredictor(t, pixels);
                return pixels;
            case 1:
                InverseCrossColor(t, pixels);
                return pixels;
            case 2:
                for (int i = 0; i < pixels.Length; i++)
                {
                    uint p = pixels[i];
                    uint green = (p >> 8) & 0xFF;
                    uint rb = (p & 0x00FF00FF) + (green << 16 | green);
                    pixels[i] = (p & 0xFF00FF00) | (rb & 0x00FF00FF);
                }
                return pixels;
            default:
                return InverseColorIndexing(t, pixels);
        }
    }

    static uint AddPixels(uint a, uint b)
    {
        uint ag = (a & 0xFF00FF00) + (b & 0xFF00FF00);
        uint rb = (a & 0x00FF00FF) + (b & 0x00FF00FF);
        return (ag & 0xFF00FF00) | (rb & 0x00FF00FF);
    }

    static uint Average2(uint a, uint b) => (((a ^ b) & 0xFEFEFEFE) >> 1) + (a & b);

    static int Clip255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    static uint ClampedAddSubtractFull(uint c0, uint c1, uint c2)
    {
        int a = Clip255((int)(c0 >> 24) + (int)(c1 >> 24) - (int)(c2 >> 24));
        int r = Clip255((int)((c0 >> 16) & 0xFF) + (int)((c1 >> 16) & 0xFF) - (int)((c2 >> 16) & 0xFF));
        int g = Clip255((int)((c0 >> 8) & 0xFF) + (int)((c1 >> 8) & 0xFF) - (int)((c2 >> 8) & 0xFF));
        int b = Clip255((int)(c0 & 0xFF) + (int)(c1 & 0xFF) - (int)(c2 & 0xFF));
        return (uint)(a << 24 | r << 16 | g << 8 | b);
    }

    static int HalfComponent(int a, int b) => Clip255(a + (a - b) / 2);

    static uint Select(uint t, uint l, uint tl)
    {
        int paMinusPb =
            Sub3((int)(t >> 24), (int)(l >> 24), (int)(tl >> 24)) +
            Sub3((int)((t >> 16) & 0xFF), (int)((l >> 16) & 0xFF), (int)((tl >> 16) & 0xFF)) +
            Sub3((int)((t >> 8) & 0xFF), (int)((l >> 8) & 0xFF), (int)((tl >> 8) & 0xFF)) +
            Sub3((int)(t & 0xFF), (int)(l & 0xFF), (int)(tl & 0xFF));
        return paMinusPb <= 0 ? t : l;
    }

    static int Sub3(int a, int b, int c) => Math.Abs(b - c) - Math.Abs(a - c);

    static uint Predict(int mode, uint l, uint t, uint tl, uint tr)
    {
        switch (mode)
        {
            case 0: return 0xFF000000;
            case 1: return l;
            case 2: return t;
            case 3: return tr;
            case 4: return tl;
            case 5: return Average2(Average2(l, tr), t);
            case 6: return Average2(l, tl);
            case 7: return Average2(l, t);
            case 8: return Average2(tl, t);
            case 9: return Average2(t, tr);
            case 10: return Average2(Average2(l, tl), Average2(t, tr));
            case 11: return Select(t, l, tl);
            case 12: return ClampedAddSubtractFull(l, t, tl);
            case 13:
            {
                uint ave = Average2(l, t);
                int a = HalfComponent((int)(ave >> 24), (int)(tl >> 24));
                int r = HalfComponent((int)((ave >> 16) & 0xFF), (int)((tl >> 16) & 0xFF));
                int g = HalfComponent((int)((ave >> 8) & 0xFF), (int)((tl >> 8) & 0xFF));
                int b = HalfComponent((int)(ave & 0xFF), (int)(tl & 0xFF));
                return (uint)(a << 24 | r << 16 | g << 8 | b);
            }
            default: return 0xFF000000;   // 14、15：与 libwebp 一样按黑色处理
        }
    }

    static void InversePredictor(Transform t, uint[] p)
    {
        int width = t.XSize, height = t.YSize;
        int tilesPerRow = SubSample(width, t.Bits);
        // 第 0 行：首像素用黑色预测，其余用左侧
        p[0] = AddPixels(p[0], 0xFF000000);
        for (int x = 1; x < width; x++) p[x] = AddPixels(p[x], p[x - 1]);
        for (int y = 1; y < height; y++)
        {
            int row = y * width;
            int modeRow = (y >> t.Bits) * tilesPerRow;
            p[row] = AddPixels(p[row], p[row - width]);
            for (int x = 1; x < width; x++)
            {
                int i = row + x;
                int mode = (int)(t.Data[modeRow + (x >> t.Bits)] >> 8) & 0xF;
                // 右上角：最后一列取当前行的首像素（与内存中的下一个像素相同）
                uint pred = Predict(mode, p[i - 1], p[i - width], p[i - width - 1], p[i - width + 1]);
                p[i] = AddPixels(p[i], pred);
            }
        }
    }

    static int ColorDelta(sbyte pred, sbyte color) => (pred * color) >> 5;

    static void InverseCrossColor(Transform t, uint[] p)
    {
        int width = t.XSize, height = t.YSize;
        int tilesPerRow = SubSample(width, t.Bits);
        for (int y = 0; y < height; y++)
        {
            int modeRow = (y >> t.Bits) * tilesPerRow;
            for (int x = 0; x < width; x++)
            {
                uint m = t.Data[modeRow + (x >> t.Bits)];
                sbyte greenToRed = (sbyte)(m & 0xFF), greenToBlue = (sbyte)((m >> 8) & 0xFF), redToBlue = (sbyte)((m >> 16) & 0xFF);
                int i = y * width + x;
                uint argb = p[i];
                sbyte green = (sbyte)(argb >> 8);
                int red = (int)(argb >> 16) & 0xFF;
                int blue = (int)argb & 0xFF;
                red = (red + ColorDelta(greenToRed, green)) & 0xFF;
                blue += ColorDelta(greenToBlue, green);
                blue += ColorDelta(redToBlue, (sbyte)red);
                blue &= 0xFF;
                p[i] = (argb & 0xFF00FF00) | (uint)(red << 16) | (uint)blue;
            }
        }
    }

    static uint[] InverseColorIndexing(Transform t, uint[] packed)
    {
        int width = t.XSize, height = t.YSize;
        var palette = t.Data;
        if (t.Bits == 0)
        {
            for (int i = 0; i < packed.Length; i++) packed[i] = palette[(packed[i] >> 8) & 0xFF];
            return packed;
        }
        int bitsPerPixel = 8 >> t.Bits, perByte = 1 << t.Bits, pixelMask = (1 << bitsPerPixel) - 1;
        int packedWidth = SubSample(width, t.Bits);
        var result = new uint[checked(width * height)];
        for (int y = 0; y < height; y++)
        {
            int src = y * packedWidth, dst = y * width;
            for (int x = 0; x < width; x++)
            {
                int g = (int)(packed[src + (x >> t.Bits)] >> 8) & 0xFF;
                int idx = (g >> (bitsPerPixel * (x & (perByte - 1)))) & pixelMask;
                result[dst + x] = palette[idx];
            }
        }
        return result;
    }
}
