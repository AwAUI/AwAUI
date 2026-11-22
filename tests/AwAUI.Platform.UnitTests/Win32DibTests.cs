using AwAUI.Win32;

namespace AwAUI.Platform.UnitTests;

/// <summary>CF_DIB 的构造与解析（纯逻辑；可在任何系统上验证）。</summary>
public class Win32DibTests
{
    [Fact]
    public void Build_parse_round_trips_pixels()
    {
        const int w = 3, h = 2;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4] = (byte)(i * 10 + 1);     // R
            rgba[i * 4 + 1] = (byte)(i * 10 + 2); // G
            rgba[i * 4 + 2] = (byte)(i * 10 + 3); // B
            rgba[i * 4 + 3] = (byte)(i == 0 ? 0 : 255); // 第一个像素完全透明
        }

        byte[] dib = Win32Dib.Build(w, h, rgba);
        Assert.True(Win32Dib.TryParse(dib, out int width, out int height, out byte[] parsed));
        Assert.Equal(w, width);
        Assert.Equal(h, height);
        Assert.Equal(rgba, parsed);
    }

    [Fact]
    public void Build_writes_bottom_up_bgra_and_transparent_mask_bit()
    {
        const int w = 2, h = 2;
        // 顶行红、绿；底行蓝、白（alpha 全 255，第一个像素透明）
        var rgba = new byte[]
        {
            255, 0, 0, 255,   0, 255, 0, 255,
            0, 0, 255, 255,   255, 255, 255, 255,
        };
        rgba[3] = 0; // 顶行第一个像素透明

        byte[] dib = Win32Dib.Build(w, h, rgba);
        Assert.Equal(40, BitConverter.ToInt32(dib, 0));
        Assert.Equal(w, BitConverter.ToInt32(dib, 4));
        Assert.Equal(h, BitConverter.ToInt32(dib, 8)); // 正数 = 自下而上
        Assert.Equal(32, BitConverter.ToInt16(dib, 14));

        // 第一行（内存中）是源的最底行：蓝、白 -> BGRA
        Assert.Equal(new byte[] { 255, 0, 0, 255, 255, 255, 255, 255 }, dib.Skip(40).Take(8).ToArray());
        // 第二行是顶行：红（alpha 0）、绿
        Assert.Equal(new byte[] { 0, 0, 255, 0, 0, 255, 0, 255 }, dib.Skip(48).Take(8).ToArray());

        // 掩码紧随像素：透明像素（顶行第一个，位于第二行）置位
        int maskBase = 40 + w * h * 4;
        int maskStride = 2;
        Assert.Equal(0x80, dib[maskBase + maskStride]); // 第二行第一像素
    }

    [Fact]
    public void Parse_accepts_24_bit_dib()
    {
        const int w = 2, h = 1;
        int stride = 8;
        byte[] dib = new byte[40 + stride];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)24).CopyTo(dib, 14);
        // BGR: 红 (0,0,255) 与 绿 (0,255,0)
        dib[40] = 0; dib[41] = 0; dib[42] = 255;
        dib[43] = 0; dib[44] = 255; dib[45] = 0;

        Assert.True(Win32Dib.TryParse(dib, out int width, out int height, out byte[] rgba));
        Assert.Equal(2, width);
        Assert.Equal(1, height);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 }, rgba);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public void Parse_rejects_truncated_data(int length)
    {
        Assert.False(Win32Dib.TryParse(new byte[length], out _, out _, out _));
    }

    [Fact]
    public void Parse_rejects_absurd_dimensions()
    {
        byte[] dib = new byte[40];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(int.MaxValue).CopyTo(dib, 4);
        BitConverter.GetBytes(int.MaxValue).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);
        Assert.False(Win32Dib.TryParse(dib, out _, out _, out _));
    }

    [Fact]
    public void Parse_rejects_rows_beyond_the_buffer()
    {
        // 声明 100x100 却没有像素数据
        byte[] dib = new byte[40];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(100).CopyTo(dib, 4);
        BitConverter.GetBytes(100).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);
        Assert.False(Win32Dib.TryParse(dib, out _, out _, out _));
    }

    [Fact]
    public void Build_rejects_invalid_input()
    {
        Assert.Throws<ArgumentException>(() => Win32Dib.Build(2, 2, new byte[4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Win32Dib.Build(0, 2, Array.Empty<byte>()));
    }
}
