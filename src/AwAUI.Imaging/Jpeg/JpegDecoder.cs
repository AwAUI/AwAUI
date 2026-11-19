using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using AwAUI.Graphics;

namespace AwAUI.Imaging.Jpeg;

/// <summary>
/// 一次性的 JPEG 解码器：解析标记段、熵解码各扫描，最后上采样并做颜色转换。
/// 顺序（基线 / 扩展）模式下每个块解码后立即 IDCT 到分量平面；渐进模式先累积系数，所有扫描结束后统一 IDCT。
/// </summary>
#if NET5_0_OR_GREATER
// stackalloc 缓冲在读取前都会被完整写入，无需清零
[System.Runtime.CompilerServices.SkipLocalsInit]
#endif
internal sealed unsafe partial class JpegDecoder
{
    /// <summary>之字形序号 → 自然顺序下标。</summary>
    internal static readonly byte[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    };

    sealed class Component
    {
        public int Id, H, V, QuantIndex;
        /// <summary>下采样后的实际尺寸（libjpeg 的 downsampled_width / height）。</summary>
        public int Width, Height;
        /// <summary>覆盖实际尺寸所需的块数（非交织扫描按此遍历）。</summary>
        public int BlocksPerLine, BlocksPerColumn;
        /// <summary>按 MCU 补齐后的块数（交织扫描按此遍历，平面按此分配）。</summary>
        public int BlocksPerLineAlloc, BlocksPerColumnAlloc;
        /// <summary>
        /// 分量采样平面，行距 = BlocksPerLineAlloc * 8。延迟分配：顺序模式在分量首次参与扫描时，
        /// 渐进模式在全部扫描结束后（截断的文件因此不会白白分配整幅平面）。
        /// </summary>
        public byte[]? Pixels;
        public int Stride;
        /// <summary>渐进模式下的系数（自然顺序，每块 64 项）；分量首次参与扫描时分配。</summary>
        public short[]? Coefs;
        /// <summary>
        /// 渐进模式下每个系数当前的逐次逼近位（libjpeg 的 coef_bits）：-1 表示尚未出现在任何扫描中。
        /// 用于拒绝不合进度的扫描，从而限制有效扫描的总数（防止构造的文件无限重复细化扫描）。
        /// </summary>
        public int[]? CoefBits;
        /// <summary>首次出现在扫描中时锁定的量化表（自然顺序）。</summary>
        public int[]? Quant;
        public JpegHuffmanTable? DcTable, AcTable;
        public int DcPred;
        /// <summary>解码期间固定（pin）住的缓冲指针；首次参与扫描时设置。</summary>
        public byte* PixelsPtr;
        public short* CoefsPtr;
        public int* QuantPtr;
    }

    readonly byte[] _data;
    int _pos;

    // 帧信息
    bool _frameSeen, _progressive, _arithmetic, _lossless;
    /// <summary>仅凭头部即可判断的不支持原因（null 表示头部层面可解码）。</summary>
    string? _unsupported;
    int _precision, _width, _height;
    Component[] _components = Array.Empty<Component>();
    int _hMax, _vMax, _mcusPerLine, _mcusPerColumn;

    // 表与参数
    readonly int[]?[] _quant = new int[]?[4];
    readonly JpegHuffmanTable?[] _dcTables = new JpegHuffmanTable?[4];
    readonly JpegHuffmanTable?[] _acTables = new JpegHuffmanTable?[4];
    int _restartInterval;
    bool _sawJfif, _sawAdobe;
    int _adobeTransform;
    int _orientation = 1;
    bool _sawEoi;
    readonly List<GCHandle> _pins = new();

    public JpegInfo? Info { get; private set; }

    public JpegDecoder(byte[] data) => _data = data;

    // ---------------------------------------------------------------- 入口

    public JpegInfo ReadInfo()
    {
        ExpectSoi();
        while (true)
        {
            int marker = NextMarker();
            if (marker == 0xDA || marker == 0xD9) break;
            ProcessHeaderMarker(marker, infoOnly: true);
        }
        if (!_frameSeen) throw new InvalidDataException("JPEG has no frame header (SOF).");
        return Info = BuildInfo();
    }

    public Bitmap Decode()
    {
        try
        {
            return DecodeCore();
        }
        finally
        {
            foreach (var h in _pins) h.Free();
            _pins.Clear();
        }
    }

    Bitmap DecodeCore()
    {
        ExpectSoi();
        while (!_sawEoi)
        {
            int marker = NextMarker();
            if (marker == 0xD9)
            {
                _sawEoi = true;
            }
            else if (marker == 0xDA)
            {
                if (!_frameSeen) throw new InvalidDataException("JPEG scan (SOS) before frame header (SOF).");
                ProcessScan();
            }
            else
            {
                ProcessHeaderMarker(marker, infoOnly: false);
            }
        }
        if (!_frameSeen) throw new InvalidDataException("JPEG has no frame header (SOF).");
        if (_scanCount == 0) throw new InvalidDataException("JPEG has no image data (SOS).");
        Info = BuildInfo();

        if (_progressive)
        {
            foreach (var c in _components) FinishProgressive(c);
        }
        foreach (var c in _components)
        {
            if (c.Pixels != null) continue;
            // 从未出现在任何扫描中的分量：等同全 0 系数块的 IDCT 结果（128），与 libjpeg 一致
            AllocatePixels(c);
            c.Pixels!.AsSpan().Fill(128);
        }
        return Output();
    }

    void* Pin(Array array)
    {
        var h = GCHandle.Alloc(array, GCHandleType.Pinned);
        _pins.Add(h);
        return (void*)h.AddrOfPinnedObject();
    }

    JpegInfo BuildInfo() => new(_width, _height, _components.Length, _precision, _progressive, _arithmetic, _lossless,
        ResolveColorSpace(), _orientation, _unsupported == null);

    // ---------------------------------------------------------------- 标记段

    void ExpectSoi()
    {
        if (_data.Length < 4 || _data[0] != 0xFF || _data[1] != 0xD8) throw new InvalidDataException("Not a JPEG file.");
        _pos = 2;
    }

    /// <summary>
    /// 跳到下一个标记并返回其代码。允许标记前的 0xFF 填充；段之间的杂散字节（部分编码器在扫描后留下）被跳过。
    /// 到达数据末尾而未遇到 EOI 视为截断。
    /// </summary>
    int NextMarker()
    {
        while (true)
        {
            while (_pos < _data.Length && _data[_pos] != 0xFF) _pos++;
            while (_pos < _data.Length && _data[_pos] == 0xFF) _pos++;
            if (_pos >= _data.Length) throw new InvalidDataException("JPEG data is truncated (missing EOI).");
            int m = _data[_pos++];
            if (m != 0x00) return m;
        }
    }

    /// <summary>读取段长度并返回段体的 [start, end)；_pos 移到段尾。</summary>
    (int Start, int End) ReadSegment()
    {
        if (_pos + 2 > _data.Length) throw new InvalidDataException("JPEG segment is truncated.");
        int len = _data[_pos] << 8 | _data[_pos + 1];
        if (len < 2 || _pos + len > _data.Length) throw new InvalidDataException("JPEG segment is truncated.");
        int start = _pos + 2, end = _pos + len;
        _pos = end;
        return (start, end);
    }

    void ProcessHeaderMarker(int marker, bool infoOnly)
    {
        switch (marker)
        {
            case 0xC0: case 0xC1: case 0xC2: case 0xC3:
            case 0xC5: case 0xC6: case 0xC7:
            case 0xC9: case 0xCA: case 0xCB:
            case 0xCD: case 0xCE: case 0xCF:
                ReadFrame(marker, infoOnly);
                break;
            case 0xC4:
                if (infoOnly) ReadSegment(); else ReadHuffmanTables();
                break;
            case 0xDB:
                if (infoOnly) ReadSegment(); else ReadQuantTables();
                break;
            case 0xDD:
                ReadRestartInterval();
                break;
            case 0xE0:
            case 0xE1:
            case 0xEE:
                ReadApp(marker);
                break;
            case 0xDC:
                throw new NotSupportedException("JPEG DNL marker (height defined after the scan) is not supported.");
            case 0xD8:
                throw new InvalidDataException("Unexpected SOI marker inside JPEG.");
            case 0x01:
            case >= 0xD0 and <= 0xD7:
                // 无参数的独立标记：TEM、游离的 RSTn
                break;
            default:
                // APPn、COM、DAC、DHP、EXP 等：跳过
                ReadSegment();
                break;
        }
    }

    void ReadFrame(int marker, bool infoOnly)
    {
        var (s, e) = ReadSegment();
        if (_frameSeen) throw new InvalidDataException("JPEG has more than one frame header.");
        if (e - s < 6) throw new InvalidDataException("JPEG frame header is truncated.");
        _frameSeen = true;
        _progressive = marker is 0xC2 or 0xC6 or 0xCA or 0xCE;
        _lossless = marker is 0xC3 or 0xC7 or 0xCB or 0xCF;
        _arithmetic = marker >= 0xC9;
        bool hierarchical = marker is 0xC5 or 0xC6 or 0xC7 or 0xCD or 0xCE or 0xCF;
        _precision = _data[s];
        _height = _data[s + 1] << 8 | _data[s + 2];
        _width = _data[s + 3] << 8 | _data[s + 4];
        int n = _data[s + 5];
        if (n == 0 || e - s < 6 + n * 3) throw new InvalidDataException("JPEG frame header is truncated.");

        var comps = new Component[n];
        for (int i = 0; i < n; i++)
        {
            int o = s + 6 + i * 3;
            var c = new Component { Id = _data[o], H = _data[o + 1] >> 4, V = _data[o + 1] & 15, QuantIndex = _data[o + 2] };
            if (c.H is < 1 or > 4 || c.V is < 1 or > 4) throw new InvalidDataException("Bad JPEG sampling factor.");
            if (c.QuantIndex > 3) throw new InvalidDataException("Bad JPEG quantization table index.");
            for (int j = 0; j < i; j++)
                if (comps[j].Id == c.Id) throw new InvalidDataException("Duplicate JPEG component id.");
            comps[i] = c;
        }
        _components = comps;
        if (_width == 0) throw new InvalidDataException("JPEG width is zero.");

        _hMax = _vMax = 1;
        foreach (var c in comps)
        {
            _hMax = Math.Max(_hMax, c.H);
            _vMax = Math.Max(_vMax, c.V);
        }
        bool fractional = false;
        foreach (var c in comps)
            if (_hMax % c.H != 0 || _vMax % c.V != 0) fractional = true;

        // 头部即可判断的不支持特性，ReadInfo 也据此给出 IsDecodable
        _unsupported =
            _arithmetic ? "Arithmetic-coded JPEG is not supported." :
            _lossless ? "Lossless JPEG is not supported." :
            hierarchical ? "Hierarchical JPEG is not supported." :
            _precision != 8 ? $"{_precision}-bit JPEG is not supported." :
            _height == 0 ? "JPEG DNL marker (height defined after the scan) is not supported." :
            n is 2 or > 4 ? $"JPEG with {n} components is not supported." :
            (long)_width * _height > int.MaxValue / 4 ? "JPEG image is too large." :
            fractional ? "JPEG with fractional sampling factors is not supported." :
            null;

        if (infoOnly) return;
        if (_unsupported != null) throw new NotSupportedException(_unsupported);
        DecodeLimits.CheckDimensions(_width, _height, "JPEG");
        SetupComponents();
    }

    void SetupComponents()
    {
        _mcusPerLine = (_width + 8 * _hMax - 1) / (8 * _hMax);
        _mcusPerColumn = (_height + 8 * _vMax - 1) / (8 * _vMax);

        long codedBlocks = 0;
        foreach (var c in _components)
        {
            c.Width = (int)(((long)_width * c.H + _hMax - 1) / _hMax);
            c.Height = (int)(((long)_height * c.V + _vMax - 1) / _vMax);
            c.BlocksPerLine = (c.Width + 7) / 8;
            c.BlocksPerColumn = (c.Height + 7) / 8;
            // 单分量帧不交织，按实际块数即可；多分量按 MCU 补齐
            c.BlocksPerLineAlloc = _components.Length == 1 ? c.BlocksPerLine : _mcusPerLine * c.H;
            c.BlocksPerColumnAlloc = _components.Length == 1 ? c.BlocksPerColumn : _mcusPerColumn * c.V;
            c.Stride = c.BlocksPerLineAlloc * 8;
            codedBlocks += (long)c.BlocksPerLine * c.BlocksPerColumn;
        }

        // 合法文件中每个分量的每个块都要在某个扫描里编码：顺序模式至少 2 位（DC 码 + EOB），
        // 渐进模式的 DC 首扫描至少 1 位。以 SOF 之后剩余的字节数为上限，拒绝声称巨大尺寸的损坏头部，
        // 使分量平面与系数缓冲的总分配量与文件大小成正比（交织扫描按 MCU 补齐的块最多再多出一个 MCU 行 / 列）。
        long availableBits = (long)(_data.Length - _pos) * 8;
        if (codedBlocks * (_progressive ? 1 : 2) > availableBits)
            throw new InvalidDataException("JPEG dimensions are inconsistent with the file size.");
    }

    /// <summary>分量首次参与扫描时分配并固定其缓冲（顺序模式：采样平面；渐进模式：系数与进度状态）。</summary>
    void AllocateComponent(Component c)
    {
        long blocks = (long)c.BlocksPerLineAlloc * c.BlocksPerColumnAlloc;
        if (_progressive)
        {
            c.Coefs = new short[blocks * 64];
            c.CoefsPtr = (short*)Pin(c.Coefs);
            c.CoefBits = new int[64];
            for (int i = 0; i < 64; i++) c.CoefBits[i] = -1;
        }
        else
        {
            AllocatePixels(c);
        }
    }

    void AllocatePixels(Component c)
    {
        c.Pixels = new byte[(long)c.Stride * c.BlocksPerColumnAlloc * 8];
        c.PixelsPtr = (byte*)Pin(c.Pixels);
    }

    void ReadHuffmanTables()
    {
        var (s, e) = ReadSegment();
        int p = s;
        while (p < e)
        {
            if (p + 17 > e) throw new InvalidDataException("JPEG Huffman table is truncated.");
            int tc = _data[p] >> 4, th = _data[p] & 15;
            if (tc > 1 || th > 3) throw new InvalidDataException("Bad JPEG Huffman table class or index.");
            var counts = new byte[17];
            int total = 0;
            for (int i = 1; i <= 16; i++)
            {
                counts[i] = _data[p + i];
                total += counts[i];
            }
            p += 17;
            if (total > 256 || p + total > e) throw new InvalidDataException("JPEG Huffman table is truncated.");
            var values = new byte[total];
            Array.Copy(_data, p, values, 0, total);
            p += total;
            var table = new JpegHuffmanTable(counts, values);
            if (tc == 0) _dcTables[th] = table; else _acTables[th] = table;
        }
    }

    void ReadQuantTables()
    {
        var (s, e) = ReadSegment();
        int p = s;
        while (p < e)
        {
            int pq = _data[p] >> 4, tq = _data[p] & 15;
            if (pq > 1 || tq > 3) throw new InvalidDataException("Bad JPEG quantization table.");
            p++;
            int size = pq == 0 ? 64 : 128;
            if (p + size > e) throw new InvalidDataException("JPEG quantization table is truncated.");
            var table = new int[64];
            for (int i = 0; i < 64; i++)
            {
                table[ZigZag[i]] = pq == 0 ? _data[p + i] : _data[p + 2 * i] << 8 | _data[p + 2 * i + 1];
            }
            p += size;
            _quant[tq] = table;
        }
    }

    void ReadRestartInterval()
    {
        var (s, e) = ReadSegment();
        if (e - s < 2) throw new InvalidDataException("JPEG DRI segment is truncated.");
        _restartInterval = _data[s] << 8 | _data[s + 1];
    }

    void ReadApp(int marker)
    {
        var (s, e) = ReadSegment();
        int len = e - s;
        if (marker == 0xE0)
        {
            if (len >= 5 && _data[s] == 'J' && _data[s + 1] == 'F' && _data[s + 2] == 'I' && _data[s + 3] == 'F' && _data[s + 4] == 0)
                _sawJfif = true;
        }
        else if (marker == 0xEE)
        {
            if (len >= 12 && _data[s] == 'A' && _data[s + 1] == 'd' && _data[s + 2] == 'o' && _data[s + 3] == 'b' && _data[s + 4] == 'e')
            {
                _sawAdobe = true;
                _adobeTransform = _data[s + 11];
            }
        }
        else if (len >= 6 && _data[s] == 'E' && _data[s + 1] == 'x' && _data[s + 2] == 'i' && _data[s + 3] == 'f' &&
                 _data[s + 4] == 0 && _data[s + 5] == 0)
        {
            _orientation = ExifReader.ReadOrientation(new ReadOnlySpan<byte>(_data, s + 6, len - 6));
        }
    }

    /// <summary>与 libjpeg 的 default_decompress_parms 相同的颜色空间推断规则。</summary>
    JpegColorSpace ResolveColorSpace()
    {
        switch (_components.Length)
        {
            case 1:
                return JpegColorSpace.Grayscale;
            case 3:
                if (_sawJfif) return JpegColorSpace.YCbCr;
                if (_sawAdobe) return _adobeTransform == 0 ? JpegColorSpace.Rgb : JpegColorSpace.YCbCr;
                if (_components[0].Id == 'R' && _components[1].Id == 'G' && _components[2].Id == 'B') return JpegColorSpace.Rgb;
                return JpegColorSpace.YCbCr;
            case 4:
                if (_sawAdobe) return _adobeTransform == 0 ? JpegColorSpace.Cmyk : JpegColorSpace.Ycck;
                return JpegColorSpace.Cmyk;
            default:
                return JpegColorSpace.Unknown;
        }
    }
}
