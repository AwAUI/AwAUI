using System.Diagnostics;
using AwAUI.Graphics;
using Xunit.Abstractions;

namespace AwAUI.Imaging.UnitTests;

/// <summary>ICO / CUR 解码与 Pillow 逐项比较。</summary>
public class IcoTests(ITestOutputHelper output)
{
    [Fact]
    public void BmpEntriesMatchPillow()
    {
        var data = FormatTestImages.Read("ico", "bmp-entries.ico");
        Assert.True(IcoCodec.CanDecode(data));
        Assert.False(IcoCodec.IsCursor(data));
        var entries = IcoCodec.ReadEntries(data);
        Assert.Equal(new[] { (16, 32), (32, 8), (48, 4), (24, 1), (20, 24) }, entries.Select(e => (e.Width, e.BitsPerPixel)));
        foreach (var e in entries)
        {
            var expected = FormatTestImages.ReadPngRgba(FormatTestImages.PathOf("ico", $"bmp-entries.{e.Width}x{e.Height}-{e.BitsPerPixel}.ref.png"), out int w, out _);
            var actual = IcoCodec.DecodeRgba(data, e, out int aw, out int ah);
            Assert.Equal((e.Width, e.Height), (aw, ah));
            FormatTestImages.AssertVisiblyEqual(expected, actual, w, $"entry {e.Width} / {e.BitsPerPixel} bpp");
            Assert.Contains(actual.Where((_, i) => i % 4 == 3), a => a == 0);
        }
        using (var best = IcoCodec.Decode(data)) Assert.Equal(48, best.Width);
        using (var s20 = IcoCodec.Decode(data, 20)) Assert.Equal(20, s20.Width);
        using (var s25 = IcoCodec.Decode(data, 25)) Assert.Equal(32, s25.Width);
        using (var s100 = IcoCodec.Decode(data, 100)) Assert.Equal(48, s100.Width);
        using (var s1 = IcoCodec.Decode(data, 1)) Assert.Equal(16, s1.Width);
    }

    [Fact]
    public void PngEntriesMatchPillow()
    {
        var data = FormatTestImages.Read("ico", "png-entries.ico");
        var entries = IcoCodec.ReadEntries(data);
        Assert.All(entries, e => Assert.True(e.IsPng));
        Assert.Equal(new[] { 16, 32, 256 }, entries.Select(e => e.Width).OrderBy(x => x));
        foreach (var e in entries)
        {
            var expected = FormatTestImages.ReadPngRgba(FormatTestImages.PathOf("ico", $"png-entries.{e.Width}x{e.Height}.ref.png"), out int w, out _);
            using var bmp = IcoCodec.Decode(data, e);
            Assert.Equal(FormatTestImages.Premultiply(expected), FormatTestImages.PremultipliedBytes(bmp));
        }
        using var best = IcoCodec.Decode(data);
        Assert.Equal(256, best.Width);
    }

    [Fact]
    public void CursorHotspots()
    {
        var data = FormatTestImages.Read("ico", "pointer.cur");
        Assert.True(IcoCodec.IsCursor(data));
        var entries = IcoCodec.ReadEntries(data);
        Assert.Equal(new[] { (32, 3, 4), (16, 1, 2) }, entries.Select(e => (e.Width, e.HotspotX, e.HotspotY)));
        var expected = FormatTestImages.ReadPngRgba(FormatTestImages.PathOf("ico", "pointer.32x32.ref.png"), out int w, out _);
        FormatTestImages.AssertRgbaEqual(expected, IcoCodec.DecodeRgba(data, entries[0], out _, out _), w, "cursor");
    }

    [Theory]
    [InlineData("bmp-entries.ico")]
    [InlineData("png-entries.ico")]
    [InlineData("pointer.cur")]
    public void CorruptedFilesOnlyThrowDocumentedExceptions(string name)
    {
        var original = FormatTestImages.Read("ico", name);
        var sw = Stopwatch.StartNew();
        var (ok, failed) = FormatTestImages.Fuzz(original, 2000, name.GetHashCode(), data =>
        {
            foreach (var e in IcoCodec.ReadEntries(data)) IcoCodec.Decode(data, e).Dispose();
        }, 128, allowWrapped: name.StartsWith("png", StringComparison.Ordinal));
        output.WriteLine($"{name}: {ok} decoded, {failed} rejected in {sw.ElapsedMilliseconds} ms");
    }
}
