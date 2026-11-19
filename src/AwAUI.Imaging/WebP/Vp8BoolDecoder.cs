using System.Runtime.CompilerServices;

namespace AwAUI.Imaging.WebP;

/// <summary>
/// VP8 布尔（算术）解码器，逐位复刻 libwebp 的 VP8BitReader（含读过末尾时补 8 个 0 位并置 <see cref="Eof"/> 的行为）。
/// </summary>
internal sealed class Vp8BoolDecoder
{
    static readonly byte[] Log2Floor = BuildLog2();

    readonly byte[] _data;
    readonly int _end;
    int _pos;
    ulong _value;
    int _bits;
    uint _range;

    public Vp8BoolDecoder(byte[] data, int offset, int length)
    {
        _data = data;
        _pos = offset;
        _end = offset + length;
        _range = 255 - 1;
        _value = 0;
        _bits = -8;
        LoadNewBytes();
    }

    /// <summary>是否读过了数据末尾（libwebp 中此时解码失败）。</summary>
    public bool Eof { get; private set; }

    static byte[] BuildLog2()
    {
        var t = new byte[256];
        for (int i = 2; i < 256; i++) t[i] = (byte)(t[i >> 1] + 1);
        return t;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void LoadNewBytes()
    {
        if (_pos + 8 <= _end)
        {
            // 一次装入 7 字节（56 位），与 libwebp 的 BITS = 56 相同
            ulong bits = 0;
            for (int i = 0; i < 7; i++) bits = bits << 8 | _data[_pos + i];
            _pos += 7;
            _value = bits | (_value << 56);
            _bits += 56;
        }
        else if (_pos < _end)
        {
            _bits += 8;
            _value = _data[_pos++] | (_value << 8);
        }
        else if (!Eof)
        {
            _value <<= 8;
            _bits += 8;
            Eof = true;
        }
        else
        {
            _bits = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetBit(int prob)
    {
        uint range = _range;
        if (_bits < 0) LoadNewBytes();
        int pos = _bits;
        uint split = (range * (uint)prob) >> 8;
        uint value = (uint)(_value >> pos);
        int bit;
        if (value > split)
        {
            range -= split;
            _value -= (ulong)(split + 1) << pos;
            bit = 1;
        }
        else
        {
            range = split + 1;
            bit = 0;
        }
        int shift = 7 ^ Log2Floor[range];
        range <<= shift;
        _bits -= shift;
        _range = range - 1;
        return bit;
    }

    /// <summary>以概率 1/2 读取 <paramref name="bits"/> 位无符号数（高位在前）。</summary>
    public int GetValue(int bits)
    {
        int v = 0;
        while (bits-- > 0) v |= GetBit(0x80) << bits;
        return v;
    }

    /// <summary>读取数值后再读符号位（1 为负）。</summary>
    public int GetSignedValue(int bits)
    {
        int v = GetValue(bits);
        return GetBit(0x80) != 0 ? -v : v;
    }

    /// <summary>读取一个标志位，为 1 时再读带符号数值，否则为 0。</summary>
    public int GetOptionalSigned(int bits) => GetBit(0x80) != 0 ? GetSignedValue(bits) : 0;
}
