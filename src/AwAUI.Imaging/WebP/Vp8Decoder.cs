using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace AwAUI.Imaging.WebP;

/// <summary>VP8 解码得到的 YUV 4:2:0 平面（按宏块对齐，已做环路滤波）。</summary>
internal sealed class Vp8Image
{
    public int Width, Height, YStride, UVStride;
    public byte[] Y = Array.Empty<byte>(), U = Array.Empty<byte>(), V = Array.Empty<byte>();
    /// <summary>ALPH 块解出的 alpha 平面（宽 × 高），没有时为 null。</summary>
    public byte[]? Alpha;
}

/// <summary>
/// VP8 关键帧解码（WebP 有损），输出与 libwebp 逐位一致：布尔解码、帧头 / 分段 / 环路滤波头、DCT 系数
/// 与量化、帧内预测（16×16、4×4、色度 8×8，含边界上的 127 / 129 约定）、逆 DCT 与 WHT、简单与普通环路滤波。
/// 先以未滤波的像素完成整帧重建（帧内预测使用未滤波的邻居），再按宏块光栅顺序做环路滤波，与 libwebp 的结果相同。
/// <para>
/// 加固：分区长度逐一检查；每个宏块在第一分区中至少占约 1.7 位，宏块数超出第一分区所能表达的数量时直接判为截断，
/// 因此分配量受输入大小约束；读过分区末尾（libwebp 的 eof）即判为截断。
/// </para>
/// </summary>
internal sealed class Vp8Decoder
{
    const int BPS = 32;
    // 工作区：第 0 行是 Y 的上邻，1–16 行是 Y；第 17 行是 U/V 的上邻，18–25 行是 U/V；第 7 / 23 列是左邻
    const int YOff = BPS * 1 + 8, UOff = BPS * 18 + 8, VOff = BPS * 18 + 24;

    static readonly ushort[] DcTable =
    {
        4, 5, 6, 7, 8, 9, 10, 10, 11, 12, 13, 14, 15, 16, 17, 17, 18, 19, 20, 20, 21, 21, 22, 22, 23, 23, 24, 25, 25, 26, 27, 28,
        29, 30, 31, 32, 33, 34, 35, 36, 37, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58,
        59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89,
        91, 93, 95, 96, 98, 100, 101, 102, 104, 106, 108, 110, 112, 114, 116, 118, 122, 124, 126, 128, 130, 132, 134, 136, 138, 140, 143, 145, 148, 151, 154, 157,
    };

    static readonly ushort[] AcTable =
    {
        4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35,
        36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 60, 62, 64, 66, 68, 70, 72, 74, 76,
        78, 80, 82, 84, 86, 88, 90, 92, 94, 96, 98, 100, 102, 104, 106, 108, 110, 112, 114, 116, 119, 122, 125, 128, 131, 134, 137, 140, 143, 146, 149, 152,
        155, 158, 161, 164, 167, 170, 173, 177, 181, 185, 189, 193, 197, 201, 205, 209, 213, 217, 221, 225, 229, 234, 239, 245, 249, 254, 259, 264, 269, 274, 279, 284,
    };

    static readonly byte[] Bands = { 0, 1, 2, 3, 6, 4, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 0 };
    static readonly byte[] Zigzag = { 0, 1, 4, 8, 5, 2, 3, 6, 9, 12, 13, 10, 7, 11, 14, 15 };
    static readonly byte[][] Cat3456 =
    {
        new byte[] { 173, 148, 140, 0 },
        new byte[] { 176, 155, 140, 135, 0 },
        new byte[] { 180, 157, 141, 134, 130, 0 },
        new byte[] { 254, 254, 243, 230, 196, 177, 153, 140, 133, 130, 129, 0 },
    };

    // 帧内预测模式（与 libwebp 的枚举一致）
    const int DC = 0, TM = 1, VE = 2, HE = 3, RD = 4, VR = 5, LD = 6, VL = 7, HD = 8, HU = 9;

    readonly byte[] _data;
    readonly int _inputLength;
    Vp8BoolDecoder _br = null!;
    Vp8BoolDecoder[] _parts = Array.Empty<Vp8BoolDecoder>();
    int _mbw, _mbh;

    // 头部
    bool _useSegment, _updateMap, _absoluteDelta;
    readonly int[] _segQuant = new int[4], _segFilter = new int[4];
    readonly int[] _segProbs = { 255, 255, 255 };
    bool _simple, _useLfDelta;
    int _level, _sharpness, _filterType;
    readonly int[] _refLfDelta = new int[4], _modeLfDelta = new int[4];
    readonly int[,] _dq = new int[4, 6];   // 每段：y1 dc/ac、y2 dc/ac、uv dc/ac
    readonly byte[] _probs = new byte[4 * 8 * 3 * 11];
    bool _useSkipProba;
    int _skipProba;
    // 每段、是否 4×4 的滤波参数
    readonly int[,] _fLimit = new int[4, 2], _fILevel = new int[4, 2], _fHev = new int[4, 2];

    // 逐宏块状态
    byte[] _intraTop = Array.Empty<byte>();
    readonly byte[] _intraLeft = new byte[4];
    byte[] _nzTop = Array.Empty<byte>(), _nzDcTop = Array.Empty<byte>();
    int _nzLeft, _nzDcLeft;
    // 当前行各宏块的模式
    byte[] _rowIsI4 = Array.Empty<byte>(), _rowSegment = Array.Empty<byte>(), _rowSkip = Array.Empty<byte>(), _rowUvMode = Array.Empty<byte>();
    byte[] _rowModes = Array.Empty<byte>();   // 每宏块 16 个
    // 滤波信息（整帧）
    byte[] _mbLimit = Array.Empty<byte>(), _mbILevel = Array.Empty<byte>(), _mbHev = Array.Empty<byte>(), _mbInner = Array.Empty<byte>();

    readonly short[] _coeffs = new short[384];
    readonly byte[] _ws = new byte[BPS * 26];
    readonly int[] _tmp = new int[16];
    readonly short[] _dc = new short[16];

    Vp8Image _img = null!;

    Vp8Decoder(byte[] data, int inputLength)
    {
        _data = data;
        _inputLength = inputLength;
    }

    /// <summary>读取 VP8 帧头中的尺寸并做基本校验（libwebp 的 VP8GetInfo）。</summary>
    public static bool TryReadInfo(ReadOnlySpan<byte> d, out int width, out int height)
    {
        width = height = 0;
        if (d.Length < 10) return false;
        int bits = d[0] | d[1] << 8 | d[2] << 16;
        bool keyFrame = (bits & 1) == 0;
        if (!keyFrame || ((bits >> 1) & 7) > 3 || ((bits >> 4) & 1) == 0) return false;
        if ((bits >> 5) >= d.Length) return false;
        if (d[3] != 0x9D || d[4] != 0x01 || d[5] != 0x2A) return false;
        width = (d[6] | d[7] << 8) & 0x3FFF;
        height = (d[8] | d[9] << 8) & 0x3FFF;
        return width > 0 && height > 0;
    }

    /// <summary>解码 VP8 块的负载。</summary>
    public static Vp8Image Decode(byte[] data, int offset, int length, int inputLength)
    {
        var dec = new Vp8Decoder(data, inputLength);
        return dec.DecodeFrame(offset, length);
    }

    Vp8Image DecodeFrame(int offset, int length)
    {
        var d = _data.AsSpan(offset, length);
        if (!TryReadInfo(d, out int width, out int height)) throw new InvalidDataException("Invalid VP8 frame header.");
        int partLength = (d[0] | d[1] << 8 | d[2] << 16) >> 5;
        int pos = offset + 10, end = offset + length;
        if (partLength > end - pos) throw new InvalidDataException("Invalid VP8 partition length.");
        _mbw = (width + 15) >> 4;
        _mbh = (height + 15) >> 4;
        // 每个宏块在第一分区中至少消耗约 1.7 位（模式树的固定概率），加上 libwebp 允许的 8 位补齐
        if ((long)_mbw * _mbh > 8L * (partLength + 2)) throw new InvalidDataException("VP8 first partition is too short for the frame size.");
        DecodeLimits.CheckBudget((long)_mbw * _mbh * 256, _inputLength, "WebP");

        _br = new Vp8BoolDecoder(_data, pos, partLength);
        ParseHeader(pos + partLength, end);

        _img = new Vp8Image
        {
            Width = width, Height = height, YStride = _mbw * 16, UVStride = _mbw * 8,
        };
        _img.Y = new byte[checked(_mbw * 16 * _mbh * 16)];
        _img.U = new byte[_mbw * 8 * _mbh * 8];
        _img.V = new byte[_mbw * 8 * _mbh * 8];

        _intraTop = new byte[_mbw * 4];
        _nzTop = new byte[_mbw];
        _nzDcTop = new byte[_mbw];
        _rowIsI4 = new byte[_mbw];
        _rowSegment = new byte[_mbw];
        _rowSkip = new byte[_mbw];
        _rowUvMode = new byte[_mbw];
        _rowModes = new byte[_mbw * 16];
        int mbCount = _mbw * _mbh;
        if (_filterType > 0)
        {
            _mbLimit = new byte[mbCount];
            _mbILevel = new byte[mbCount];
            _mbHev = new byte[mbCount];
            _mbInner = new byte[mbCount];
        }

        for (int mby = 0; mby < _mbh; mby++)
        {
            Array.Clear(_intraLeft, 0, 4);
            _nzLeft = 0;
            _nzDcLeft = 0;
            for (int mbx = 0; mbx < _mbw; mbx++) ParseIntraMode(mbx);
            if (_br.Eof) throw new InvalidDataException("Premature end of VP8 partition 0.");
            var tokens = _parts[mby & (_parts.Length - 1)];
            for (int mbx = 0; mbx < _mbw; mbx++)
            {
                DecodeMacroblock(mbx, mby, tokens);
                if (tokens.Eof) throw new InvalidDataException("Premature end of VP8 token partition.");
            }
        }
        if (_filterType > 0) FilterFrame();
        return _img;
    }

    // ------------------------------------------------------------------ 头部

    void ParseHeader(int partitionsStart, int end)
    {
        var br = _br;
        br.GetBit(0x80);   // 颜色空间
        br.GetBit(0x80);   // 钳位类型（libwebp 总是钳位）

        _useSegment = br.GetBit(0x80) != 0;
        if (_useSegment)
        {
            _updateMap = br.GetBit(0x80) != 0;
            if (br.GetBit(0x80) != 0)
            {
                _absoluteDelta = br.GetBit(0x80) != 0;
                for (int s = 0; s < 4; s++) _segQuant[s] = br.GetOptionalSigned(7);
                for (int s = 0; s < 4; s++) _segFilter[s] = br.GetOptionalSigned(6);
            }
            if (_updateMap)
                for (int s = 0; s < 3; s++) _segProbs[s] = br.GetBit(0x80) != 0 ? br.GetValue(8) : 255;
        }

        _simple = br.GetBit(0x80) != 0;
        _level = br.GetValue(6);
        _sharpness = br.GetValue(3);
        _useLfDelta = br.GetBit(0x80) != 0;
        if (_useLfDelta && br.GetBit(0x80) != 0)
        {
            for (int i = 0; i < 4; i++)
                if (br.GetBit(0x80) != 0) _refLfDelta[i] = br.GetSignedValue(6);
            for (int i = 0; i < 4; i++)
                if (br.GetBit(0x80) != 0) _modeLfDelta[i] = br.GetSignedValue(6);
        }
        _filterType = _level == 0 ? 0 : _simple ? 1 : 2;
        if (br.Eof) throw new InvalidDataException("Truncated VP8 header.");

        // 分区
        int numParts = 1 << br.GetValue(2);
        int sizesBytes = 3 * (numParts - 1);
        if (end - partitionsStart < sizesBytes) throw new InvalidDataException("Truncated VP8 partition sizes.");
        int partStart = partitionsStart + sizesBytes;
        int left = end - partStart;
        _parts = new Vp8BoolDecoder[numParts];
        for (int p = 0; p < numParts - 1; p++)
        {
            int o = partitionsStart + 3 * p;
            int size = _data[o] | _data[o + 1] << 8 | _data[o + 2] << 16;
            if (size > left) size = left;
            _parts[p] = new Vp8BoolDecoder(_data, partStart, size);
            partStart += size;
            left -= size;
        }
        _parts[numParts - 1] = new Vp8BoolDecoder(_data, partStart, left);
        if (partStart >= end) throw new InvalidDataException("Truncated VP8 token partitions.");

        // 量化
        int baseQ = br.GetValue(7);
        int dqy1Dc = br.GetOptionalSigned(4), dqy2Dc = br.GetOptionalSigned(4), dqy2Ac = br.GetOptionalSigned(4);
        int dquvDc = br.GetOptionalSigned(4), dquvAc = br.GetOptionalSigned(4);
        for (int s = 0; s < 4; s++)
        {
            int q;
            if (_useSegment)
            {
                q = _segQuant[s];
                if (!_absoluteDelta) q += baseQ;
            }
            else q = baseQ;
            _dq[s, 0] = DcTable[Clip(q + dqy1Dc, 127)];
            _dq[s, 1] = AcTable[Clip(q, 127)];
            _dq[s, 2] = DcTable[Clip(q + dqy2Dc, 127)] * 2;
            int y2ac = AcTable[Clip(q + dqy2Ac, 127)] * 101581 >> 16;
            _dq[s, 3] = y2ac < 8 ? 8 : y2ac;
            _dq[s, 4] = DcTable[Clip(q + dquvDc, 117)];
            _dq[s, 5] = AcTable[Clip(q + dquvAc, 127)];
        }

        br.GetBit(0x80);   // update_proba，关键帧忽略
        var update = Vp8Tables.CoeffUpdateProbs;
        var defaults = Vp8Tables.DefaultCoeffProbs;
        for (int i = 0; i < _probs.Length; i++)
            _probs[i] = br.GetBit(update[i]) != 0 ? (byte)br.GetValue(8) : defaults[i];
        _useSkipProba = br.GetBit(0x80) != 0;
        if (_useSkipProba) _skipProba = br.GetValue(8);
        if (br.Eof) throw new InvalidDataException("Truncated VP8 header.");

        PrecomputeFilterStrengths();
    }

    static int Clip(int v, int max) => v < 0 ? 0 : v > max ? max : v;

    void PrecomputeFilterStrengths()
    {
        if (_filterType == 0) return;
        for (int s = 0; s < 4; s++)
        {
            int baseLevel;
            if (_useSegment)
            {
                baseLevel = _segFilter[s];
                if (!_absoluteDelta) baseLevel += _level;
            }
            else baseLevel = _level;
            for (int i4 = 0; i4 <= 1; i4++)
            {
                int level = baseLevel;
                if (_useLfDelta)
                {
                    level += _refLfDelta[0];
                    if (i4 != 0) level += _modeLfDelta[0];
                }
                level = level < 0 ? 0 : level > 63 ? 63 : level;
                if (level > 0)
                {
                    int ilevel = level;
                    if (_sharpness > 0)
                    {
                        ilevel >>= _sharpness > 4 ? 2 : 1;
                        if (ilevel > 9 - _sharpness) ilevel = 9 - _sharpness;
                    }
                    if (ilevel < 1) ilevel = 1;
                    _fILevel[s, i4] = ilevel;
                    _fLimit[s, i4] = 2 * level + ilevel;
                    _fHev[s, i4] = level >= 40 ? 2 : level >= 15 ? 1 : 0;
                }
                else _fLimit[s, i4] = 0;
            }
        }
    }

    // ------------------------------------------------------------------ 模式

    void ParseIntraMode(int mbx)
    {
        var br = _br;
        int segment = 0;
        if (_updateMap)
            segment = br.GetBit(_segProbs[0]) == 0 ? br.GetBit(_segProbs[1]) : br.GetBit(_segProbs[2]) + 2;
        _rowSegment[mbx] = (byte)segment;
        _rowSkip[mbx] = (byte)(_useSkipProba ? br.GetBit(_skipProba) : 0);
        bool i4 = br.GetBit(145) == 0;
        _rowIsI4[mbx] = (byte)(i4 ? 1 : 0);
        int top = mbx * 4;
        int modes = mbx * 16;
        if (!i4)
        {
            int ymode = br.GetBit(156) != 0 ? (br.GetBit(128) != 0 ? TM : HE) : (br.GetBit(163) != 0 ? VE : DC);
            _rowModes[modes] = (byte)ymode;
            for (int i = 0; i < 4; i++)
            {
                _intraTop[top + i] = (byte)ymode;
                _intraLeft[i] = (byte)ymode;
            }
        }
        else
        {
            var probs = Vp8Tables.BModeProbs;
            for (int y = 0; y < 4; y++)
            {
                int ymode = _intraLeft[y];
                for (int x = 0; x < 4; x++)
                {
                    int p = (_intraTop[top + x] * 10 + ymode) * 9;
                    // 树：DC | TM | VE | (HE | RD | VR) | (LD | VL | HD | HU)
                    if (br.GetBit(probs[p]) == 0) ymode = DC;
                    else if (br.GetBit(probs[p + 1]) == 0) ymode = TM;
                    else if (br.GetBit(probs[p + 2]) == 0) ymode = VE;
                    else if (br.GetBit(probs[p + 3]) == 0)
                    {
                        if (br.GetBit(probs[p + 4]) == 0) ymode = HE;
                        else ymode = br.GetBit(probs[p + 5]) == 0 ? RD : VR;
                    }
                    else if (br.GetBit(probs[p + 6]) == 0) ymode = LD;
                    else if (br.GetBit(probs[p + 7]) == 0) ymode = VL;
                    else ymode = br.GetBit(probs[p + 8]) == 0 ? HD : HU;
                    _intraTop[top + x] = (byte)ymode;
                    _rowModes[modes + y * 4 + x] = (byte)ymode;
                }
                _intraLeft[y] = (byte)ymode;
            }
        }
        _rowUvMode[mbx] = (byte)(br.GetBit(142) == 0 ? DC : br.GetBit(114) == 0 ? VE : br.GetBit(183) != 0 ? TM : HE);
    }

    // ------------------------------------------------------------------ 系数

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    int ProbIndex(int type, int n, int ctx) => ((type * 8 + Bands[n]) * 3 + ctx) * 11;

    /// <summary>解一个 4×4 块的系数（已反量化），返回最后一个非零系数之后的位置（libwebp 的 GetCoeffs）。</summary>
    int GetCoeffs(Vp8BoolDecoder br, int type, int ctx, int dq0, int dq1, int n, short[] output, int outOff)
    {
        var probs = _probs;
        int p = ProbIndex(type, n, ctx);
        for (; n < 16; n++)
        {
            if (br.GetBit(probs[p]) == 0) return n;
            while (br.GetBit(probs[p + 1]) == 0)
            {
                p = ProbIndex(type, ++n, 0);
                if (n == 16) return 16;
            }
            int v;
            if (br.GetBit(probs[p + 2]) == 0)
            {
                v = 1;
                p = ProbIndex(type, n + 1, 1);
            }
            else
            {
                v = GetLargeValue(br, p);
                p = ProbIndex(type, n + 1, 2);
            }
            if (br.GetBit(0x80) != 0) v = -v;
            output[outOff + Zigzag[n]] = (short)(v * (n > 0 ? dq1 : dq0));
        }
        return 16;
    }

    int GetLargeValue(Vp8BoolDecoder br, int p)
    {
        var probs = _probs;
        int v;
        if (br.GetBit(probs[p + 3]) == 0)
        {
            v = br.GetBit(probs[p + 4]) == 0 ? 2 : 3 + br.GetBit(probs[p + 5]);
        }
        else if (br.GetBit(probs[p + 6]) == 0)
        {
            if (br.GetBit(probs[p + 7]) == 0) v = 5 + br.GetBit(159);
            else
            {
                v = 7 + 2 * br.GetBit(165);
                v += br.GetBit(145);
            }
        }
        else
        {
            int bit1 = br.GetBit(probs[p + 8]);
            int bit0 = br.GetBit(probs[p + 9 + bit1]);
            int cat = 2 * bit1 + bit0;
            v = 0;
            foreach (var t in Cat3456[cat])
            {
                if (t == 0) break;
                v += v + br.GetBit(t);
            }
            v += 3 + (8 << cat);
        }
        return v;
    }

    /// <summary>
    /// 解析一个宏块的残差并重建。返回值：各 4×4 块的变换类型（0 无、1 仅 DC、2 完整），
    /// 按 Y 0–15、U 16–19、V 20–23 存放在 <paramref name="kinds"/> 中；整块无非零系数时返回 true。
    /// </summary>
    bool ParseResiduals(int mbx, int segment, bool i4, Vp8BoolDecoder br, Span<byte> kinds)
    {
        var dst = _coeffs;
        Array.Clear(dst, 0, dst.Length);
        int first, acType;
        bool any = false;
        if (!i4)
        {
            Array.Clear(_dc, 0, 16);
            int ctx = _nzDcTop[mbx] + _nzDcLeft;
            int nz = GetCoeffs(br, 1, ctx, _dq[segment, 2], _dq[segment, 3], 0, _dc, 0);
            int flag = nz > 0 ? 1 : 0;
            _nzDcTop[mbx] = (byte)flag;
            _nzDcLeft = flag;
            if (nz > 1) InverseWht(_dc, dst);
            else
            {
                int dc0 = (_dc[0] + 3) >> 3;
                for (int i = 0; i < 256; i += 16) dst[i] = (short)dc0;
            }
            first = 1;
            acType = 0;
        }
        else
        {
            first = 0;
            acType = 3;
        }

        int tnz = _nzTop[mbx] & 0x0F, lnz = _nzLeft & 0x0F;
        int dq0 = _dq[segment, 0], dq1 = _dq[segment, 1];
        for (int y = 0; y < 4; y++)
        {
            int l = lnz & 1;
            for (int x = 0; x < 4; x++)
            {
                int b = y * 4 + x;
                int ctx = l + (tnz & 1);
                int nz = GetCoeffs(br, acType, ctx, dq0, dq1, first, dst, b * 16);
                l = nz > first ? 1 : 0;
                tnz = (tnz >> 1) | (l << 7);
                kinds[b] = (byte)(nz > 1 ? 2 : dst[b * 16] != 0 ? 1 : 0);
                any |= kinds[b] != 0;
            }
            tnz >>= 4;
            lnz = (lnz >> 1) | (l << 7);
        }
        int outTnz = tnz, outLnz = lnz >> 4;

        int uvDq0 = _dq[segment, 4], uvDq1 = _dq[segment, 5];
        for (int ch = 0; ch < 4; ch += 2)
        {
            tnz = _nzTop[mbx] >> (4 + ch);
            lnz = _nzLeft >> (4 + ch);
            for (int y = 0; y < 2; y++)
            {
                int l = lnz & 1;
                for (int x = 0; x < 2; x++)
                {
                    int b = 16 + ch * 2 + y * 2 + x;
                    int ctx = l + (tnz & 1);
                    int nz = GetCoeffs(br, 2, ctx, uvDq0, uvDq1, 0, dst, b * 16);
                    l = nz > 0 ? 1 : 0;
                    tnz = (tnz >> 1) | (l << 3);
                    kinds[b] = (byte)(nz > 1 ? 2 : dst[b * 16] != 0 ? 1 : 0);
                    any |= kinds[b] != 0;
                }
                tnz >>= 2;
                lnz = (lnz >> 1) | (l << 5);
            }
            outTnz |= (tnz << 4) << ch;
            outLnz |= (lnz & 0xF0) << ch;
        }
        _nzTop[mbx] = (byte)outTnz;
        _nzLeft = outLnz;
        return !any;
    }

    static void InverseWht(short[] input, short[] output)
    {
        Span<int> tmp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a0 = input[i] + input[12 + i];
            int a1 = input[4 + i] + input[8 + i];
            int a2 = input[4 + i] - input[8 + i];
            int a3 = input[i] - input[12 + i];
            tmp[i] = a0 + a1;
            tmp[8 + i] = a0 - a1;
            tmp[4 + i] = a3 + a2;
            tmp[12 + i] = a3 - a2;
        }
        int o = 0;
        for (int i = 0; i < 4; i++)
        {
            int dc = tmp[i * 4] + 3;
            int a0 = dc + tmp[3 + i * 4];
            int a1 = tmp[1 + i * 4] + tmp[2 + i * 4];
            int a2 = tmp[1 + i * 4] - tmp[2 + i * 4];
            int a3 = dc - tmp[3 + i * 4];
            output[o] = (short)((a0 + a1) >> 3);
            output[o + 16] = (short)((a3 + a2) >> 3);
            output[o + 32] = (short)((a0 - a1) >> 3);
            output[o + 48] = (short)((a3 - a2) >> 3);
            o += 64;
        }
    }

    // ------------------------------------------------------------------ 重建

    void DecodeMacroblock(int mbx, int mby, Vp8BoolDecoder tokens)
    {
        int segment = _rowSegment[mbx];
        bool i4 = _rowIsI4[mbx] != 0;
        bool skip = _useSkipProba && _rowSkip[mbx] != 0;
        Span<byte> kinds = stackalloc byte[24];
        if (!skip)
        {
            skip = ParseResiduals(mbx, segment, i4, tokens, kinds);
        }
        else
        {
            _nzLeft = 0;
            _nzTop[mbx] = 0;
            if (!i4)
            {
                _nzDcLeft = 0;
                _nzDcTop[mbx] = 0;
            }
            kinds.Clear();
        }
        if (_filterType > 0)
        {
            int idx = mby * _mbw + mbx, k = i4 ? 1 : 0;
            _mbLimit[idx] = (byte)_fLimit[segment, k];
            _mbILevel[idx] = (byte)_fILevel[segment, k];
            _mbHev[idx] = (byte)_fHev[segment, k];
            _mbInner[idx] = (byte)(i4 || !skip ? 1 : 0);
        }
        Reconstruct(mbx, mby, i4, kinds);
    }

    void Reconstruct(int mbx, int mby, bool i4, ReadOnlySpan<byte> kinds)
    {
        var ws = _ws;
        var img = _img;
        int ys = img.YStride, uvs = img.UVStride;
        int x0 = mbx * 16, y0 = mby * 16, cx0 = mbx * 8, cy0 = mby * 8;

        // 左邻
        if (mbx == 0)
        {
            for (int j = 0; j < 16; j++) ws[YOff + j * BPS - 1] = 129;
            for (int j = 0; j < 8; j++)
            {
                ws[UOff + j * BPS - 1] = 129;
                ws[VOff + j * BPS - 1] = 129;
            }
        }
        else
        {
            for (int j = 0; j < 16; j++) ws[YOff + j * BPS - 1] = img.Y[(y0 + j) * ys + x0 - 1];
            for (int j = 0; j < 8; j++)
            {
                ws[UOff + j * BPS - 1] = img.U[(cy0 + j) * uvs + cx0 - 1];
                ws[VOff + j * BPS - 1] = img.V[(cy0 + j) * uvs + cx0 - 1];
            }
        }
        // 上邻、左上角、右上角
        if (mby == 0)
        {
            ws.AsSpan(YOff - BPS - 1, 21).Fill(127);
            ws.AsSpan(UOff - BPS - 1, 9).Fill(127);
            ws.AsSpan(VOff - BPS - 1, 9).Fill(127);
        }
        else
        {
            int row = (y0 - 1) * ys;
            Buffer.BlockCopy(img.Y, row + x0, ws, YOff - BPS, 16);
            ws[YOff - BPS - 1] = mbx == 0 ? (byte)129 : img.Y[row + x0 - 1];
            if (mbx == _mbw - 1) ws.AsSpan(YOff - BPS + 16, 4).Fill(img.Y[row + x0 + 15]);
            else Buffer.BlockCopy(img.Y, row + x0 + 16, ws, YOff - BPS + 16, 4);
            int crow = (cy0 - 1) * uvs;
            Buffer.BlockCopy(img.U, crow + cx0, ws, UOff - BPS, 8);
            Buffer.BlockCopy(img.V, crow + cx0, ws, VOff - BPS, 8);
            ws[UOff - BPS - 1] = mbx == 0 ? (byte)129 : img.U[crow + cx0 - 1];
            ws[VOff - BPS - 1] = mbx == 0 ? (byte)129 : img.V[crow + cx0 - 1];
        }

        var coeffs = _coeffs;
        if (i4)
        {
            // 右上角的 4 个像素复制到第 3、7、11 行之后，供每行最右侧的子块使用
            for (int r = 3; r < 12; r += 4) Buffer.BlockCopy(ws, YOff - BPS + 16, ws, YOff + r * BPS + 16, 4);
            int modes = mbx * 16;
            for (int n = 0; n < 16; n++)
            {
                int at = YOff + (n >> 2) * 4 * BPS + (n & 3) * 4;
                Predict4(_rowModes[modes + n], at);
                AddTransform(kinds[n], coeffs, n * 16, at);
            }
        }
        else
        {
            Predict16(CheckMode(mbx, mby, _rowModes[mbx * 16]), YOff, 16);
            for (int n = 0; n < 16; n++)
                AddTransform(kinds[n], coeffs, n * 16, YOff + (n >> 2) * 4 * BPS + (n & 3) * 4);
        }
        int uvMode = CheckMode(mbx, mby, _rowUvMode[mbx]);
        Predict16(uvMode, UOff, 8);
        Predict16(uvMode, VOff, 8);
        for (int n = 0; n < 4; n++)
        {
            int rel = (n >> 1) * 4 * BPS + (n & 1) * 4;
            AddTransform(kinds[16 + n], coeffs, (16 + n) * 16, UOff + rel);
            AddTransform(kinds[20 + n], coeffs, (20 + n) * 16, VOff + rel);
        }

        for (int j = 0; j < 16; j++) Buffer.BlockCopy(ws, YOff + j * BPS, img.Y, (y0 + j) * ys + x0, 16);
        for (int j = 0; j < 8; j++)
        {
            Buffer.BlockCopy(ws, UOff + j * BPS, img.U, (cy0 + j) * uvs + cx0, 8);
            Buffer.BlockCopy(ws, VOff + j * BPS, img.V, (cy0 + j) * uvs + cx0, 8);
        }
    }

    // DC 预测在边界上的变体
    const int DcNoTop = 10, DcNoLeft = 11, DcNoTopLeft = 12;

    static int CheckMode(int mbx, int mby, int mode)
    {
        if (mode != DC) return mode;
        if (mbx == 0) return mby == 0 ? DcNoTopLeft : DcNoLeft;
        return mby == 0 ? DcNoTop : DC;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static byte Clip8(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>16×16 亮度或 8×8 色度预测（DC / TM / VE / HE 及 DC 的边界变体）。</summary>
    void Predict16(int mode, int at, int size)
    {
        var ws = _ws;
        int shift = size == 16 ? 4 : 3;
        switch (mode)
        {
            case DC:
            {
                int sum = size;
                for (int i = 0; i < size; i++) sum += ws[at - BPS + i] + ws[at + i * BPS - 1];
                Fill(at, size, (byte)(sum >> (shift + 1)));
                break;
            }
            case DcNoTop:
            {
                int sum = size >> 1;
                for (int i = 0; i < size; i++) sum += ws[at + i * BPS - 1];
                Fill(at, size, (byte)(sum >> shift));
                break;
            }
            case DcNoLeft:
            {
                int sum = size >> 1;
                for (int i = 0; i < size; i++) sum += ws[at - BPS + i];
                Fill(at, size, (byte)(sum >> shift));
                break;
            }
            case DcNoTopLeft:
                Fill(at, size, 0x80);
                break;
            case TM:
            {
                int tl = ws[at - BPS - 1];
                for (int y = 0; y < size; y++)
                {
                    int l = ws[at + y * BPS - 1] - tl;
                    for (int x = 0; x < size; x++) ws[at + y * BPS + x] = Clip8(ws[at - BPS + x] + l);
                }
                break;
            }
            case VE:
                for (int y = 0; y < size; y++) Buffer.BlockCopy(ws, at - BPS, ws, at + y * BPS, size);
                break;
            default: // HE
                for (int y = 0; y < size; y++) ws.AsSpan(at + y * BPS, size).Fill(ws[at + y * BPS - 1]);
                break;
        }
    }

    void Fill(int at, int size, byte v)
    {
        for (int y = 0; y < size; y++) _ws.AsSpan(at + y * BPS, size).Fill(v);
    }

    static byte Avg3(int a, int b, int c) => (byte)((a + 2 * b + c + 2) >> 2);

    static byte Avg2(int a, int b) => (byte)((a + b + 1) >> 1);

    void Predict4(int mode, int at)
    {
        var w = _ws;
        int top = at - BPS;
        switch (mode)
        {
            case DC:
            {
                int sum = 4;
                for (int i = 0; i < 4; i++) sum += w[top + i] + w[at + i * BPS - 1];
                byte v = (byte)(sum >> 3);
                for (int y = 0; y < 4; y++) w.AsSpan(at + y * BPS, 4).Fill(v);
                break;
            }
            case TM:
            {
                int tl = w[top - 1];
                for (int y = 0; y < 4; y++)
                {
                    int l = w[at + y * BPS - 1] - tl;
                    for (int x = 0; x < 4; x++) w[at + y * BPS + x] = Clip8(w[top + x] + l);
                }
                break;
            }
            case VE:
            {
                byte a = Avg3(w[top - 1], w[top], w[top + 1]), b = Avg3(w[top], w[top + 1], w[top + 2]);
                byte c = Avg3(w[top + 1], w[top + 2], w[top + 3]), d = Avg3(w[top + 2], w[top + 3], w[top + 4]);
                for (int y = 0; y < 4; y++)
                {
                    int o = at + y * BPS;
                    w[o] = a; w[o + 1] = b; w[o + 2] = c; w[o + 3] = d;
                }
                break;
            }
            case HE:
            {
                int A = w[top - 1], B = w[at - 1], C = w[at + BPS - 1], D = w[at + 2 * BPS - 1], E = w[at + 3 * BPS - 1];
                w.AsSpan(at, 4).Fill(Avg3(A, B, C));
                w.AsSpan(at + BPS, 4).Fill(Avg3(B, C, D));
                w.AsSpan(at + 2 * BPS, 4).Fill(Avg3(C, D, E));
                w.AsSpan(at + 3 * BPS, 4).Fill(Avg3(D, E, E));
                break;
            }
            case RD:
            {
                int I = w[at - 1], J = w[at + BPS - 1], K = w[at + 2 * BPS - 1], L = w[at + 3 * BPS - 1];
                int X = w[top - 1], A = w[top], B = w[top + 1], C = w[top + 2], D = w[top + 3];
                Set(at, 0, 3, Avg3(J, K, L));
                byte v = Avg3(I, J, K); Set(at, 0, 2, v); Set(at, 1, 3, v);
                v = Avg3(X, I, J); Set(at, 0, 1, v); Set(at, 1, 2, v); Set(at, 2, 3, v);
                v = Avg3(A, X, I); Set(at, 0, 0, v); Set(at, 1, 1, v); Set(at, 2, 2, v); Set(at, 3, 3, v);
                v = Avg3(B, A, X); Set(at, 1, 0, v); Set(at, 2, 1, v); Set(at, 3, 2, v);
                v = Avg3(C, B, A); Set(at, 2, 0, v); Set(at, 3, 1, v);
                Set(at, 3, 0, Avg3(D, C, B));
                break;
            }
            case LD:
            {
                int A = w[top], B = w[top + 1], C = w[top + 2], D = w[top + 3], E = w[top + 4], F = w[top + 5], G = w[top + 6], H = w[top + 7];
                Set(at, 0, 0, Avg3(A, B, C));
                byte v = Avg3(B, C, D); Set(at, 1, 0, v); Set(at, 0, 1, v);
                v = Avg3(C, D, E); Set(at, 2, 0, v); Set(at, 1, 1, v); Set(at, 0, 2, v);
                v = Avg3(D, E, F); Set(at, 3, 0, v); Set(at, 2, 1, v); Set(at, 1, 2, v); Set(at, 0, 3, v);
                v = Avg3(E, F, G); Set(at, 3, 1, v); Set(at, 2, 2, v); Set(at, 1, 3, v);
                v = Avg3(F, G, H); Set(at, 3, 2, v); Set(at, 2, 3, v);
                Set(at, 3, 3, Avg3(G, H, H));
                break;
            }
            case VR:
            {
                int I = w[at - 1], J = w[at + BPS - 1], K = w[at + 2 * BPS - 1];
                int X = w[top - 1], A = w[top], B = w[top + 1], C = w[top + 2], D = w[top + 3];
                byte v = Avg2(X, A); Set(at, 0, 0, v); Set(at, 1, 2, v);
                v = Avg2(A, B); Set(at, 1, 0, v); Set(at, 2, 2, v);
                v = Avg2(B, C); Set(at, 2, 0, v); Set(at, 3, 2, v);
                Set(at, 3, 0, Avg2(C, D));
                Set(at, 0, 3, Avg3(K, J, I));
                Set(at, 0, 2, Avg3(J, I, X));
                v = Avg3(I, X, A); Set(at, 0, 1, v); Set(at, 1, 3, v);
                v = Avg3(X, A, B); Set(at, 1, 1, v); Set(at, 2, 3, v);
                v = Avg3(A, B, C); Set(at, 2, 1, v); Set(at, 3, 3, v);
                Set(at, 3, 1, Avg3(B, C, D));
                break;
            }
            case VL:
            {
                int A = w[top], B = w[top + 1], C = w[top + 2], D = w[top + 3], E = w[top + 4], F = w[top + 5], G = w[top + 6], H = w[top + 7];
                Set(at, 0, 0, Avg2(A, B));
                byte v = Avg2(B, C); Set(at, 1, 0, v); Set(at, 0, 2, v);
                v = Avg2(C, D); Set(at, 2, 0, v); Set(at, 1, 2, v);
                v = Avg2(D, E); Set(at, 3, 0, v); Set(at, 2, 2, v);
                Set(at, 0, 1, Avg3(A, B, C));
                v = Avg3(B, C, D); Set(at, 1, 1, v); Set(at, 0, 3, v);
                v = Avg3(C, D, E); Set(at, 2, 1, v); Set(at, 1, 3, v);
                v = Avg3(D, E, F); Set(at, 3, 1, v); Set(at, 2, 3, v);
                Set(at, 3, 2, Avg3(E, F, G));
                Set(at, 3, 3, Avg3(F, G, H));
                break;
            }
            case HD:
            {
                int I = w[at - 1], J = w[at + BPS - 1], K = w[at + 2 * BPS - 1], L = w[at + 3 * BPS - 1];
                int X = w[top - 1], A = w[top], B = w[top + 1], C = w[top + 2];
                byte v = Avg2(I, X); Set(at, 0, 0, v); Set(at, 2, 1, v);
                v = Avg2(J, I); Set(at, 0, 1, v); Set(at, 2, 2, v);
                v = Avg2(K, J); Set(at, 0, 2, v); Set(at, 2, 3, v);
                Set(at, 0, 3, Avg2(L, K));
                Set(at, 3, 0, Avg3(A, B, C));
                Set(at, 2, 0, Avg3(X, A, B));
                v = Avg3(I, X, A); Set(at, 1, 0, v); Set(at, 3, 1, v);
                v = Avg3(J, I, X); Set(at, 1, 1, v); Set(at, 3, 2, v);
                v = Avg3(K, J, I); Set(at, 1, 2, v); Set(at, 3, 3, v);
                Set(at, 1, 3, Avg3(L, K, J));
                break;
            }
            default: // HU
            {
                int I = w[at - 1], J = w[at + BPS - 1], K = w[at + 2 * BPS - 1], L = w[at + 3 * BPS - 1];
                Set(at, 0, 0, Avg2(I, J));
                byte v = Avg2(J, K); Set(at, 2, 0, v); Set(at, 0, 1, v);
                v = Avg2(K, L); Set(at, 2, 1, v); Set(at, 0, 2, v);
                Set(at, 1, 0, Avg3(I, J, K));
                v = Avg3(J, K, L); Set(at, 3, 0, v); Set(at, 1, 1, v);
                v = Avg3(K, L, L); Set(at, 3, 1, v); Set(at, 1, 2, v);
                v = (byte)L; Set(at, 3, 2, v); Set(at, 2, 2, v); Set(at, 0, 3, v); Set(at, 1, 3, v); Set(at, 2, 3, v); Set(at, 3, 3, v);
                break;
            }
        }
    }

    void Set(int at, int x, int y, byte v) => _ws[at + y * BPS + x] = v;

    void AddTransform(int kind, short[] c, int o, int at)
    {
        if (kind == 0) return;
        var ws = _ws;
        if (kind == 1)
        {
            int dc = (c[o] + 4) >> 3;
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                {
                    int i = at + y * BPS + x;
                    ws[i] = Clip8(ws[i] + dc);
                }
            return;
        }
        var tmp = _tmp;
        for (int i = 0; i < 4; i++)
        {
            int a = c[o + i] + c[o + 8 + i];
            int b = c[o + i] - c[o + 8 + i];
            int cc = Mul2(c[o + 4 + i]) - Mul1(c[o + 12 + i]);
            int d = Mul1(c[o + 4 + i]) + Mul2(c[o + 12 + i]);
            tmp[i * 4] = a + d;
            tmp[i * 4 + 1] = b + cc;
            tmp[i * 4 + 2] = b - cc;
            tmp[i * 4 + 3] = a - d;
        }
        for (int i = 0; i < 4; i++)
        {
            int dc = tmp[i] + 4;
            int a = dc + tmp[8 + i];
            int b = dc - tmp[8 + i];
            int cc = Mul2(tmp[4 + i]) - Mul1(tmp[12 + i]);
            int d = Mul1(tmp[4 + i]) + Mul2(tmp[12 + i]);
            int row = at + i * BPS;
            ws[row] = Clip8(ws[row] + ((a + d) >> 3));
            ws[row + 1] = Clip8(ws[row + 1] + ((b + cc) >> 3));
            ws[row + 2] = Clip8(ws[row + 2] + ((b - cc) >> 3));
            ws[row + 3] = Clip8(ws[row + 3] + ((a - d) >> 3));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Mul1(int a) => ((a * 20091) >> 16) + a;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Mul2(int a) => (a * 35468) >> 16;

    // ------------------------------------------------------------------ 环路滤波

    unsafe void FilterFrame()
    {
        var img = _img;
        int ys = img.YStride, uvs = img.UVStride;
        fixed (byte* Y = img.Y, U = img.U, V = img.V)
        {
            for (int mby = 0; mby < _mbh; mby++)
            {
                for (int mbx = 0; mbx < _mbw; mbx++)
                {
                    int idx = mby * _mbw + mbx;
                    int limit = _mbLimit[idx];
                    if (limit == 0) continue;
                    bool inner = _mbInner[idx] != 0;
                    byte* y = Y + mby * 16 * ys + mbx * 16;
                    if (_filterType == 1)
                    {
                        if (mbx > 0) SimpleFilter(y, 1, ys, limit + 4);
                        if (inner) for (int k = 4; k < 16; k += 4) SimpleFilter(y + k, 1, ys, limit);
                        if (mby > 0) SimpleFilter(y, ys, 1, limit + 4);
                        if (inner) for (int k = 4; k < 16; k += 4) SimpleFilter(y + k * ys, ys, 1, limit);
                        continue;
                    }
                    int ilevel = _mbILevel[idx], hev = _mbHev[idx];
                    int uvOff = mby * 8 * uvs + mbx * 8;
                    byte* u = U + uvOff, v = V + uvOff;
                    if (mbx > 0)
                    {
                        FilterLoop(y, 1, ys, 16, limit + 4, ilevel, hev, true);
                        FilterLoop(u, 1, uvs, 8, limit + 4, ilevel, hev, true);
                        FilterLoop(v, 1, uvs, 8, limit + 4, ilevel, hev, true);
                    }
                    if (inner)
                    {
                        for (int k = 4; k < 16; k += 4) FilterLoop(y + k, 1, ys, 16, limit, ilevel, hev, false);
                        FilterLoop(u + 4, 1, uvs, 8, limit, ilevel, hev, false);
                        FilterLoop(v + 4, 1, uvs, 8, limit, ilevel, hev, false);
                    }
                    if (mby > 0)
                    {
                        FilterLoop(y, ys, 1, 16, limit + 4, ilevel, hev, true);
                        FilterLoop(u, uvs, 1, 8, limit + 4, ilevel, hev, true);
                        FilterLoop(v, uvs, 1, 8, limit + 4, ilevel, hev, true);
                    }
                    if (inner)
                    {
                        for (int k = 4; k < 16; k += 4) FilterLoop(y + k * ys, ys, 1, 16, limit, ilevel, hev, false);
                        FilterLoop(u + 4 * uvs, uvs, 1, 8, limit, ilevel, hev, false);
                        FilterLoop(v + 4 * uvs, uvs, 1, 8, limit, ilevel, hev, false);
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Abs(int v)
    {
        int m = v >> 31;
        return (v ^ m) - m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int SClip1(int v) => v < -128 ? -128 : v > 127 ? 127 : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int SClip2(int v) => v < -16 ? -16 : v > 15 ? 15 : v;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static byte Clip1(int v) => (uint)v <= 255 ? (byte)v : v < 0 ? (byte)0 : (byte)255;

    /// <summary>
    /// 简单滤波器：沿边界的 16 个位置，<paramref name="step"/> 为跨边界方向的步长，<paramref name="along"/> 为沿边界方向的步长。
    /// </summary>
    static unsafe void SimpleFilter(byte* p, int step, int along, int thresh)
    {
        int t2 = 2 * thresh + 1;
        for (int i = 0; i < 16; i++, p += along)
        {
            int p1 = p[-2 * step], p0 = p[-step], q0 = p[0], q1 = p[step];
            if (4 * Abs(p0 - q0) + Abs(p1 - q1) > t2) continue;
            int a = 3 * (q0 - p0) + SClip1(p1 - q1);
            p[-step] = Clip1(p0 + SClip2((a + 3) >> 3));
            p[0] = Clip1(q0 - SClip2((a + 4) >> 3));
        }
    }

    /// <summary>
    /// 普通滤波器（libwebp 的 FilterLoop26 / FilterLoop24）：<paramref name="macroblockEdge"/> 为 true 时
    /// 非高边缘方差处改 6 个像素，否则（子块内部边）改 4 个像素。
    /// </summary>
    static unsafe void FilterLoop(byte* p, int step, int along, int size, int thresh, int ithresh, int hevThresh, bool macroblockEdge)
    {
        int t2 = 2 * thresh + 1;
        for (int k = 0; k < size; k++, p += along)
        {
            int p3 = p[-4 * step], p2 = p[-3 * step], p1 = p[-2 * step], p0 = p[-step];
            int q0 = p[0], q1 = p[step], q2 = p[2 * step], q3 = p[3 * step];
            if (4 * Abs(p0 - q0) + Abs(p1 - q1) > t2) continue;
            if (Abs(p3 - p2) > ithresh || Abs(p2 - p1) > ithresh || Abs(p1 - p0) > ithresh ||
                Abs(q3 - q2) > ithresh || Abs(q2 - q1) > ithresh || Abs(q1 - q0) > ithresh) continue;
            if (Abs(p1 - p0) > hevThresh || Abs(q1 - q0) > hevThresh)
            {
                // 高边缘方差：只调整 p0 / q0（DoFilter2）
                int a = 3 * (q0 - p0) + SClip1(p1 - q1);
                p[-step] = Clip1(p0 + SClip2((a + 3) >> 3));
                p[0] = Clip1(q0 - SClip2((a + 4) >> 3));
            }
            else if (macroblockEdge)
            {
                // DoFilter6
                int a = SClip1(3 * (q0 - p0) + SClip1(p1 - q1));
                int a1 = (27 * a + 63) >> 7, a2 = (18 * a + 63) >> 7, a3 = (9 * a + 63) >> 7;
                p[-3 * step] = Clip1(p2 + a3);
                p[-2 * step] = Clip1(p1 + a2);
                p[-step] = Clip1(p0 + a1);
                p[0] = Clip1(q0 - a1);
                p[step] = Clip1(q1 - a2);
                p[2 * step] = Clip1(q2 - a3);
            }
            else
            {
                // DoFilter4
                int a = 3 * (q0 - p0);
                int a1 = SClip2((a + 4) >> 3), a2 = SClip2((a + 3) >> 3), a3 = (a1 + 1) >> 1;
                p[-2 * step] = Clip1(p1 + a3);
                p[-step] = Clip1(p0 + a2);
                p[0] = Clip1(q0 - a1);
                p[step] = Clip1(q1 - a3);
            }
        }
    }
}
