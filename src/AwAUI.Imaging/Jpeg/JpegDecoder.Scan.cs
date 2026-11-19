using System;
using System.IO;

namespace AwAUI.Imaging.Jpeg;

// 扫描（SOS）的熵解码：位读取器、Huffman 解码、顺序与渐进四种扫描、重启间隔。
internal sealed unsafe partial class JpegDecoder
{
    int _scanCount;

    // 位读取器：高位对齐的 64 位缓冲。遇到标记或数据末尾后补 0；
    // 若解码真正消耗了补出来的位，说明数据被截断或损坏。
    ulong _bitBuf;
    int _bitCount;
    int _padBits;
    bool _hitMarker;

    // 当前扫描
    readonly Component[] _scanComps = new Component[4];
    int _scanCompCount, _ss, _se, _ah, _al;
    int _eobrun;

    void ProcessScan()
    {
        var (s, e) = ReadSegment();
        if (e - s < 1) throw new InvalidDataException("JPEG scan header is truncated.");
        int n = _data[s];
        if (n < 1 || n > 4 || e - s < 1 + 2 * n + 3) throw new InvalidDataException("Bad JPEG scan header.");
        int blocksPerMcu = 0;
        for (int i = 0; i < n; i++)
        {
            int id = _data[s + 1 + 2 * i], tables = _data[s + 2 + 2 * i];
            Component? comp = null;
            foreach (var c in _components)
                if (c.Id == id) comp = c;
            if (comp == null) throw new InvalidDataException("JPEG scan references an unknown component.");
            for (int j = 0; j < i; j++)
                if (_scanComps[j] == comp) throw new InvalidDataException("JPEG scan lists a component twice.");
            int td = tables >> 4, ta = tables & 15;
            if (td > 3 || ta > 3) throw new InvalidDataException("Bad JPEG Huffman table selector.");
            // 缺少 DHT 时（Motion-JPEG 帧常省略）与 libjpeg-turbo 一样退回 Annex K 的标准表（仅 0、1 号）
            comp.DcTable = _dcTables[td] ?? JpegHuffmanTable.Standard(dc: true, td);
            comp.AcTable = _acTables[ta] ?? JpegHuffmanTable.Standard(dc: false, ta);
            _scanComps[i] = comp;
            blocksPerMcu += comp.H * comp.V;
        }
        _scanCompCount = n;
        int p = s + 1 + 2 * n;
        _ss = _data[p];
        _se = _data[p + 1];
        _ah = _data[p + 2] >> 4;
        _al = _data[p + 2] & 15;
        if (n > 1 && blocksPerMcu > 10) throw new InvalidDataException("JPEG scan has too many blocks per MCU.");

        if (_progressive)
        {
            bool dc = _ss == 0;
            // 与 libjpeg start_pass_phuff_decoder 相同的参数检查；细化扫描每次只能推进 1 位
            if (_se > 63 || _ss > _se || (dc && _se != 0) || (!dc && n != 1) || _al > 13 || (_ah != 0 && _al != _ah - 1))
                throw new InvalidDataException("Bad JPEG progressive scan parameters.");
        }
        else
        {
            _ss = 0; _se = 63; _ah = _al = 0;
        }

        // 校验所需的表并锁定量化表
        for (int i = 0; i < n; i++)
        {
            var c = _scanComps[i];
            if (c.Quant == null)
            {
                // 锁定量化表（复制一份，后续 DQT 不影响已开始解码的分量），并分配、固定该分量的缓冲
                var q = _quant[c.QuantIndex] ?? throw new InvalidDataException("JPEG quantization table is missing.");
                c.Quant = (int[])q.Clone();
                c.QuantPtr = (int*)Pin(c.Quant);
                AllocateComponent(c);
            }
            if (_progressive)
            {
                // 逐次逼近进度检查（libjpeg 的 coef_bits）：首扫描（Ah = 0）只能针对尚未出现过的系数，
                // 细化扫描的 Ah 必须等于该系数上一次扫描的 Al。libjpeg 对此只给警告；这里直接拒绝，
                // 使每个系数最多参与 1 + 13 次扫描，解码耗时与文件大小保持线性关系。
                var bits = c.CoefBits!;
                for (int k = _ss; k <= _se; k++)
                {
                    int expected = bits[k] < 0 ? 0 : bits[k];
                    if (_ah != expected || (_ah == 0 && bits[k] >= 0))
                        throw new InvalidDataException("JPEG progressive scan does not follow the previous scans.");
                    bits[k] = _al;
                }
            }
            bool needDc = !_progressive || (_ss == 0 && _ah == 0);
            bool needAc = !_progressive || _ss > 0;
            if (needDc && c.DcTable == null) throw new InvalidDataException("JPEG DC Huffman table is missing.");
            if (needAc && c.AcTable == null) throw new InvalidDataException("JPEG AC Huffman table is missing.");
            c.DcPred = 0;
        }

        _scanCount++;
        _eobrun = 0;
        ResetBits();
        DecodeScan();
    }

    void DecodeScan()
    {
        int mcuCount;
        Component single = _scanComps[0];
        if (_scanCompCount == 1) mcuCount = single.BlocksPerLine * single.BlocksPerColumn;
        else mcuCount = _mcusPerLine * _mcusPerColumn;

        short* block = stackalloc short[64];
        int restartsLeft = _restartInterval;
        int expectedRst = 0;
        for (int mcu = 0; mcu < mcuCount; mcu++)
        {
            if (_restartInterval > 0)
            {
                if (restartsLeft == 0)
                {
                    ProcessRestart(expectedRst);
                    expectedRst = (expectedRst + 1) & 7;
                    restartsLeft = _restartInterval;
                }
                restartsLeft--;
            }

            if (_scanCompCount == 1)
            {
                int row = mcu / single.BlocksPerLine, col = mcu - row * single.BlocksPerLine;
                DecodeBlock(single, row, col, block);
            }
            else
            {
                int mcuRow = mcu / _mcusPerLine, mcuCol = mcu - mcuRow * _mcusPerLine;
                for (int i = 0; i < _scanCompCount; i++)
                {
                    var c = _scanComps[i];
                    for (int v = 0; v < c.V; v++)
                        for (int h = 0; h < c.H; h++)
                            DecodeBlock(c, mcuRow * c.V + v, mcuCol * c.H + h, block);
                }
            }
        }
        // 扫描结束：缓冲中剩余的位是字节对齐的填充，直接丢弃；_pos 停在下一个标记（或其前的杂散字节）处
    }

    void DecodeBlock(Component c, int row, int col, short* block)
    {
        if (!_progressive)
        {
            DecodeBlockSequential(c, block);
            JpegIdct.Transform(block, c.QuantPtr, c.PixelsPtr + (long)row * 8 * c.Stride + col * 8, c.Stride);
            return;
        }

        short* b = c.CoefsPtr + ((long)row * c.BlocksPerLineAlloc + col) * 64;
        if (_ss == 0)
        {
            if (_ah == 0) DecodeDcFirst(c, b);
            else DecodeDcRefine(b);
        }
        else
        {
            if (_ah == 0) DecodeAcFirst(c, b);
            else DecodeAcRefine(c, b);
        }
    }

    // ---------------------------------------------------------------- 四种块解码

    void DecodeBlockSequential(Component c, short* block)
    {
        new Span<short>(block, 64).Clear();
        int t = DecodeDcCategory(c);
        int diff = t == 0 ? 0 : Extend(ReceiveBits(t), t);
        c.DcPred += diff;
        block[0] = (short)c.DcPred;

        var ac = c.AcTable!;
        fixed (int* fastAc = ac.FastAc)
        fixed (byte* zz = ZigZag)
        {
            for (int k = 1; k < 64; k++)
            {
                if (_bitCount < 16) FillBits();
                int fast = fastAc[(int)(_bitBuf >> (64 - JpegHuffmanTable.LookupBits))];
                if (fast != 0)
                {
                    // 短码：游程、码长与幅值一次查出
                    k += (fast >> 4) & 15;
                    Consume(fast & 15);
                    if (k > 63) throw new InvalidDataException("JPEG AC coefficient index out of range.");
                    block[zz[k]] = (short)(fast >> 8);
                    continue;
                }
                int rs = DecodeHuffman(ac);
                int r = rs >> 4, s = rs & 15;
                if (s == 0)
                {
                    if (r != 15) break;     // EOB
                    k += 15;                // ZRL
                    continue;
                }
                k += r;
                if (k > 63) throw new InvalidDataException("JPEG AC coefficient index out of range.");
                block[zz[k]] = (short)Extend(ReceiveBits(s), s);
            }
        }
    }

    int DecodeDcCategory(Component c)
    {
        int t = DecodeHuffman(c.DcTable!);
        if (t > 16) throw new InvalidDataException("Bad JPEG DC difference category.");
        return t;
    }

    void DecodeDcFirst(Component c, short* block)
    {
        int t = DecodeDcCategory(c);
        int diff = t == 0 ? 0 : Extend(ReceiveBits(t), t);
        c.DcPred += diff;
        block[0] = (short)(c.DcPred << _al);
    }

    void DecodeDcRefine(short* block)
    {
        if (ReceiveBits(1) != 0) block[0] |= (short)(1 << _al);
    }

    void DecodeAcFirst(Component c, short* block)
    {
        if (_eobrun > 0)
        {
            _eobrun--;
            return;
        }
        var ac = c.AcTable!;
        int al = _al, se = _se;
        fixed (int* fastAc = ac.FastAc)
        {
            for (int k = _ss; k <= se; k++)
            {
                if (_bitCount < 16) FillBits();
                int fast = fastAc[(int)(_bitBuf >> (64 - JpegHuffmanTable.LookupBits))];
                if (fast != 0)
                {
                    k += (fast >> 4) & 15;
                    Consume(fast & 15);
                    if (k > se) throw new InvalidDataException("JPEG AC coefficient index out of range.");
                    block[ZigZag[k]] = (short)((fast >> 8) * (1 << al));
                    continue;
                }
                int rs = DecodeHuffman(ac);
                int r = rs >> 4, s = rs & 15;
                if (s == 0)
                {
                    if (r < 15)
                    {
                        _eobrun = (1 << r) - 1;
                        if (r != 0) _eobrun += ReceiveBits(r);
                        break;
                    }
                    k += 15;
                    continue;
                }
                k += r;
                if (k > se) throw new InvalidDataException("JPEG AC coefficient index out of range.");
                block[ZigZag[k]] = (short)(Extend(ReceiveBits(s), s) * (1 << al));
            }
        }
    }

    /// <summary>AC 逐次逼近细化扫描（libjpeg decode_mcu_AC_refine）。</summary>
    void DecodeAcRefine(Component c, short* block)
    {
        int p1 = 1 << _al, m1 = -1 << _al;
        int k = _ss, se = _se;
        if (_eobrun == 0)
        {
            var ac = c.AcTable!;
            for (; k <= se; k++)
            {
                int rs = DecodeHuffman(ac);
                int r = rs >> 4, s = rs & 15;
                if (s != 0)
                {
                    // 细化扫描中新出现的系数幅值只能是 ±1
                    if (s != 1) throw new InvalidDataException("Bad JPEG AC refinement data.");
                    s = ReceiveBits(1) != 0 ? p1 : m1;
                }
                else if (r != 15)
                {
                    _eobrun = 1 << r;
                    if (r != 0) _eobrun += ReceiveBits(r);
                    break;
                }
                // 跳过 r 个零系数（途中的非零系数各读一位修正位）；s != 0 时停在目标零系数上
                do
                {
                    short* coef = block + ZigZag[k];
                    if (*coef != 0)
                    {
                        RefineNonZero(coef, p1, m1);
                    }
                    else
                    {
                        if (--r < 0) break;
                    }
                    k++;
                } while (k <= se);
                if (s != 0)
                {
                    if (k > se) throw new InvalidDataException("JPEG AC coefficient index out of range.");
                    block[ZigZag[k]] = (short)s;
                }
            }
        }
        if (_eobrun > 0)
        {
            // 处于 EOB 区间内：只修正已有的非零系数
            for (; k <= se; k++)
            {
                short* coef = block + ZigZag[k];
                if (*coef != 0) RefineNonZero(coef, p1, m1);
            }
            _eobrun--;
        }
    }

    void RefineNonZero(short* coef, int p1, int m1)
    {
        if (ReceiveBits(1) != 0 && (*coef & p1) == 0)
        {
            *coef = (short)(*coef >= 0 ? *coef + p1 : *coef + m1);
        }
    }

    void FinishProgressive(Component c)
    {
        if (c.CoefsPtr == null) return;   // 分量从未出现在任何扫描中：由 DecodeCore 填充
        AllocatePixels(c);
        for (int row = 0; row < c.BlocksPerColumn; row++)
        {
            for (int col = 0; col < c.BlocksPerLine; col++)
            {
                JpegIdct.Transform(c.CoefsPtr + ((long)row * c.BlocksPerLineAlloc + col) * 64, c.QuantPtr,
                    c.PixelsPtr + (long)row * 8 * c.Stride + col * 8, c.Stride);
            }
        }
    }

    // ---------------------------------------------------------------- 重启间隔

    void ProcessRestart(int expected)
    {
        // 丢弃缓冲中的填充位；位读取器不会越过标记，因此 _pos 之后应紧跟 RSTn
        ResetBits();
        while (_pos + 1 < _data.Length && _data[_pos] == 0xFF && _data[_pos + 1] == 0xFF) _pos++;
        if (_pos + 1 >= _data.Length) throw new InvalidDataException("JPEG data is truncated (missing restart marker).");
        if (_data[_pos] != 0xFF || _data[_pos + 1] != 0xD0 + expected)
            throw new InvalidDataException("JPEG restart marker is missing or out of sequence.");
        _pos += 2;
        for (int i = 0; i < _scanCompCount; i++) _scanComps[i].DcPred = 0;
        _eobrun = 0;
    }

    // ---------------------------------------------------------------- 位读取

    void ResetBits()
    {
        _bitBuf = 0;
        _bitCount = 0;
        _padBits = 0;
        _hitMarker = false;
    }

    /// <summary>把缓冲补到至少 57 位。遇到标记（FF 后跟非 00）或数据末尾后改为补 0 并计入 _padBits。</summary>
    void FillBits()
    {
        ulong buf = _bitBuf;
        int count = _bitCount;
        int pos = _pos;
        byte[] data = _data;
        while (count <= 56)
        {
            if (!_hitMarker)
            {
                if ((uint)pos < (uint)data.Length)
                {
                    int b = data[pos];
                    if (b != 0xFF)
                    {
                        pos++;
                        buf |= (ulong)(uint)b << (56 - count);
                        count += 8;
                        continue;
                    }
                    if (pos + 1 < data.Length && data[pos + 1] == 0x00)
                    {
                        pos += 2;   // 填充字节 FF 00
                        buf |= 0xFFUL << (56 - count);
                        count += 8;
                        continue;
                    }
                }
                _hitMarker = true;   // 停在标记的 FF 处
            }
            _padBits += 8;
            count += 8;
        }
        _bitBuf = buf;
        _bitCount = count;
        _pos = pos;
    }

    void Consume(int n)
    {
        _bitBuf <<= n;
        _bitCount -= n;
        if (_bitCount < _padBits) throw new InvalidDataException("JPEG entropy-coded data is truncated or corrupt.");
    }

    int ReceiveBits(int n)
    {
        if (_bitCount < n) FillBits();
        int v = (int)(_bitBuf >> (64 - n));
        Consume(n);
        return v;
    }

    static int Extend(int v, int t) => v < 1 << (t - 1) ? v - (1 << t) + 1 : v;

    int DecodeHuffman(JpegHuffmanTable table)
    {
        if (_bitCount < 16) FillBits();
        int entry = table.Lookup[(int)(_bitBuf >> (64 - JpegHuffmanTable.LookupBits))];
        if (entry != 0)
        {
            Consume(entry >> 8);
            return entry & 0xFF;
        }
        int code = (int)(_bitBuf >> (64 - 16));
        for (int len = JpegHuffmanTable.LookupBits + 1; len <= 16; len++)
        {
            int c = code >> (16 - len);
            if (c <= table.MaxCode[len])
            {
                Consume(len);
                return table.Values[table.ValOffset[len] + c];
            }
        }
        throw new InvalidDataException("Bad JPEG Huffman code.");
    }
}
