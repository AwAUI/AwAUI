using AwAUI.X11;

namespace AwAUI.Platform.UnitTests;

/// <summary>XIM 预编辑文本的解码：多字节按 <c>LC_CTYPE</c> 区域设置（不假定 UTF-8），wchar_t 按 UTF-32。</summary>
public unsafe class X11TextEncodingTests
{
    /// <summary>模拟 GB18030 双字节：C4 E3 → U+4F60（你），ASCII 原样，其余字节无效。</summary>
    static int Gbk(byte* state, byte* source, int remaining, uint* codePoint)
    {
        byte b = source[0];
        if (b == 0) return 0;
        if (b < 0x80)
        {
            *codePoint = b;
            return 1;
        }
        if (b == 0xC4 && remaining >= 2 && source[1] == 0xE3)
        {
            *codePoint = 0x4F60;
            return 2;
        }
        return -1;
    }

    [Fact]
    public void Utf32_decodes_scalars()
    {
        uint* cps = stackalloc uint[] { 0x4F60, 0x597D, 'A', 0, 'x' };
        Assert.Equal("你好A", X11TextEncoding.FromUtf32(cps, 5));
    }

    [Fact]
    public void Utf32_replaces_surrogates_and_out_of_range()
    {
        uint* cps = stackalloc uint[] { 0xD800, 0x110000, 0x1F600 };
        Assert.Equal("��\U0001F600", X11TextEncoding.FromUtf32(cps, 3));
    }

    [Fact]
    public void Multibyte_decodes_with_the_locale_decoder()
    {
        byte* bytes = stackalloc byte[] { 0xC4, 0xE3, (byte)'A', 0, (byte)'B' };
        Assert.Equal("你A", X11TextEncoding.FromMultibyte(bytes, 5, Gbk));
    }

    [Fact]
    public void Multibyte_replaces_invalid_bytes_and_keeps_going()
    {
        // 0xFF 不是合法字符：替换为一个 U+FFFD 后继续解码后面的 ASCII，而不是整段变成乱码
        byte* bytes = stackalloc byte[] { 0xFF, (byte)'B', (byte)'C' };
        Assert.Equal("�BC", X11TextEncoding.FromMultibyte(bytes, 3, Gbk));
    }

    [Fact]
    public void Multibyte_replaces_incomplete_trailing_sequence()
    {
        byte* bytes = stackalloc byte[] { (byte)'A', 0xC4, 0x00 };
        Assert.Equal("A�", X11TextEncoding.FromMultibyte(bytes, 2, Gbk));
    }

    [Fact]
    public void Multibyte_empty_input_is_empty()
    {
        Assert.Equal("", X11TextEncoding.FromMultibyte(null, 0, Gbk));
    }
}
