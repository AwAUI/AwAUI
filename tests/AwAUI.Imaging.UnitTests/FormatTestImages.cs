using System.IO.Compression;
using AwAUI.Graphics;

namespace AwAUI.Imaging.UnitTests;

/// <summary>GIF / WebP / BMP / ICO 测试图片（tests/TestFiles/Images 下的子目录，由 generate_formats.py 生成）。</summary>
internal static class FormatTestImages
{
    public static string PathOf(string sub, string name) => System.IO.Path.Combine(TestImages.Dir, sub, name);

    public static byte[] Read(string sub, string name) => File.ReadAllBytes(PathOf(sub, name));

    public static IEnumerable<string> Files(string sub, string pattern) =>
        Directory.GetFiles(System.IO.Path.Combine(TestImages.Dir, sub), pattern)
            .Select(p => System.IO.Path.GetFileName(p)!)
            .OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>读取参考 PNG（8 位 RGBA，非隔行）的原始非预乘像素，不经过被测代码。</summary>
    public static byte[] ReadPngRgba(string path, out int width, out int height)
    {
        var data = File.ReadAllBytes(path);
        int pos = 8;
        width = height = 0;
        using var idat = new MemoryStream();
        while (pos + 8 <= data.Length)
        {
            int len = data[pos] << 24 | data[pos + 1] << 16 | data[pos + 2] << 8 | data[pos + 3];
            string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            if (type == "IHDR")
            {
                width = data[pos + 8] << 24 | data[pos + 9] << 16 | data[pos + 10] << 8 | data[pos + 11];
                height = data[pos + 12] << 24 | data[pos + 13] << 16 | data[pos + 14] << 8 | data[pos + 15];
                Assert.Equal(8, data[pos + 16]);
                Assert.Equal(6, data[pos + 17]);
                Assert.Equal(0, data[pos + 20]);
            }
            else if (type == "IDAT") idat.Write(data, pos + 8, len);
            pos += 12 + len;
        }
        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        using (var z = new DeflateStream(new MemoryStream(idat.ToArray(), 2, (int)idat.Length - 2), CompressionMode.Decompress))
        {
            int total = 0;
            while (total < raw.Length)
            {
                int n = z.Read(raw, total, raw.Length - total);
                Assert.True(n > 0);
                total += n;
            }
        }
        var result = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int f = raw[y * (stride + 1)];
            for (int i = 0; i < stride; i++)
            {
                int a = i >= 4 ? result[y * stride + i - 4] : 0;
                int b = y > 0 ? result[(y - 1) * stride + i] : 0;
                int c = i >= 4 && y > 0 ? result[(y - 1) * stride + i - 4] : 0;
                int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                int pred = f switch
                {
                    0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1,
                    _ => pa <= pb && pa <= pc ? a : pb <= pc ? b : c,
                };
                result[y * stride + i] = (byte)(raw[y * (stride + 1) + 1 + i] + pred);
            }
        }
        return result;
    }

    /// <summary>逐字节比较两段 RGBA，失败时报告第一个差异的位置。</summary>
    public static void AssertRgbaEqual(byte[] expected, byte[] actual, int width, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                int px = i / 4;
                Assert.Fail($"{what}: first difference at ({px % width},{px / width}) channel {i % 4}: " +
                            $"expected {Pixel(expected, px)} got {Pixel(actual, px)}");
            }
        }
    }

    /// <summary>非预乘比较，但忽略 alpha 为 0 的像素的颜色（不可见，部分编码器 / 解码器会改写）。</summary>
    public static void AssertVisiblyEqual(byte[] expected, byte[] actual, int width, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i += 4)
        {
            bool same = expected[i + 3] == actual[i + 3] &&
                        (expected[i + 3] == 0 || (expected[i] == actual[i] && expected[i + 1] == actual[i + 1] && expected[i + 2] == actual[i + 2]));
            if (!same)
            {
                int px = i / 4;
                Assert.Fail($"{what}: first difference at ({px % width},{px / width}): expected {Pixel(expected, px)} got {Pixel(actual, px)}");
            }
        }
    }

    static string Pixel(byte[] d, int px) => $"({d[px * 4]},{d[px * 4 + 1]},{d[px * 4 + 2]},{d[px * 4 + 3]})";

    /// <summary>位图转为预乘 RGBA 字节。</summary>
    public static byte[] PremultipliedBytes(Bitmap b)
    {
        var r = new byte[b.Width * b.Height * 4];
        for (int y = 0; y < b.Height; y++) b.GetRow(y).CopyTo(r.AsSpan(y * b.Width * 4));
        return r;
    }

    /// <summary>与 Bitmap.FromRgbaUnpremultiplied 相同的预乘公式。</summary>
    public static byte[] Premultiply(byte[] rgba)
    {
        var r = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            for (int c = 0; c < 3; c++) r[i + c] = (byte)(a == 255 ? rgba[i + c] : (rgba[i + c] * a + 127) / 255);
            r[i + 3] = (byte)a;
        }
        return r;
    }

    /// <summary>
    /// 对变异后的数据反复解码：只允许 InvalidDataException / NotSupportedException（且不能是包装的内部异常）。
    /// </summary>
    public static (int ok, int failed) Fuzz(byte[] original, int iterations, int seed, Action<byte[]> decode, int headerBias = 256,
        bool allowWrapped = false)
    {
        // 环境变量 AWAUI_FUZZ_SCALE 可放大迭代次数（如 20）做更长时间的模糊测试
        if (int.TryParse(Environment.GetEnvironmentVariable("AWAUI_FUZZ_SCALE"), out int scale) && scale > 1) iterations *= scale;
        var rng = new Random(seed);
        int ok = 0, failed = 0;
        for (int iter = 0; iter < iterations; iter++)
        {
            byte[] data;
            int mode = iter % 4;
            if (mode == 0)
            {
                data = original.AsSpan(0, rng.Next(original.Length)).ToArray();
            }
            else
            {
                data = (byte[])original.Clone();
                int flips = 1 + rng.Next(mode == 3 ? 16 : 4);
                for (int f = 0; f < flips; f++)
                {
                    int pos = mode == 1 ? rng.Next(Math.Min(headerBias, data.Length)) : rng.Next(data.Length);
                    switch (rng.Next(4))
                    {
                        case 0: data[pos] = (byte)rng.Next(256); break;
                        case 1: data[pos] ^= (byte)(1 << rng.Next(8)); break;
                        case 2: data[pos] = 0xFF; break;
                        default: data[pos] = 0; break;
                    }
                }
            }
            try
            {
                decode(data);
                ok++;
            }
            catch (InvalidDataException e)
            {
                // 内部越界等异常被 DecodeGuard 包装时 InnerException 非空：说明解码器漏掉了某个检查
                if (!allowWrapped && e.InnerException != null) throw new Xunit.Sdk.XunitException($"Internal failure at iteration {iter}: {e.InnerException}");
                failed++;
            }
            catch (NotSupportedException)
            {
                failed++;
            }
        }
        return (ok, failed);
    }
}
