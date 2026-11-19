using AwAUI.Graphics;
using AwAUI.Imaging;

namespace AwAUI.Imaging.UnitTests;

/// <summary>PngCodec.Decode 直接调用时对损坏输入的行为：只抛出约定的异常，且不会按伪造的尺寸大量分配。</summary>
public class PngRobustnessTests
{
    static byte[] SamplePng()
    {
        using var bmp = new Bitmap(new PixelSize(37, 23));
        bmp.Clear(Color.Parse("#336699"));
        bmp.GetRow(5)[20] = 255;
        return PngCodec.Encode(bmp);
    }

    [Fact]
    public void Round_trips()
    {
        using var b = PngCodec.Decode(SamplePng());
        Assert.Equal(new PixelSize(37, 23), b.Size);
    }

    [Fact]
    public void Corrupted_and_truncated_inputs_only_throw_documented_exceptions()
    {
        var original = SamplePng();
        var rng = new Random(1234);
        for (int i = 0; i < 3000; i++)
        {
            var data = (byte[])original.Clone();
            if (i % 3 == 0) Array.Resize(ref data, rng.Next(9, data.Length));
            int flips = rng.Next(1, 6);
            for (int f = 0; f < flips; f++) data[rng.Next(8, data.Length)] = (byte)rng.Next(256);
            try { PngCodec.Decode(data).Dispose(); }
            catch (InvalidDataException) { }
            catch (NotSupportedException) { }
        }
    }

    [Fact]
    public void Huge_declared_size_is_rejected_without_allocating_it()
    {
        var data = SamplePng();
        // IHDR 宽高位于偏移 16 与 20（大端序）；伪造为 60000 x 60000
        foreach (int o in new[] { 16, 20 })
        {
            data[o] = 0; data[o + 1] = 0; data[o + 2] = 0xEA; data[o + 3] = 0x60;
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsAny<Exception>(() => PngCodec.Decode(data));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 * 1024 * 1024);
    }
}
