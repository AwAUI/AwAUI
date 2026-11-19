namespace AwAUI.Imaging.UnitTests;

/// <summary>参考图之外的边界用例：头部可解码性判断、缺省 Huffman 表、边界文件的结构特征。</summary>
public class JpegEdgeCaseTests
{
    [Fact]
    public void EdgeCaseFilesHaveTheIntendedStructure()
    {
        // SOF1 + 16 位量化表
        var sof1 = TestImages.Read("sof1-16bit-q3.jpg");
        Assert.True(JpegFile.Find(sof1, 0xC1) > 0);
        Assert.Equal(1, sof1[JpegFile.Find(sof1, 0xDB) + 4] >> 4);
        // 顺序模式、每个分量一个扫描
        var ni = TestImages.Read("sequential-noninterleaved.jpg");
        Assert.True(JpegFile.Find(ni, 0xC0) > 0);
        Assert.Equal(3, JpegFile.Segments(ni).Count(s => s.Marker == 0xDA && ni[s.Offset + 4] == 1));
        // 缺 DHT
        Assert.Equal(-1, JpegFile.Find(TestImages.Read("missing-dht.jpg"), 0xC4));
        // 窄色度平面
        Assert.Equal(4, JpegCodec.ReadInfo(TestImages.Read("tiny-4x4-420.jpg")).Width);
    }

    [Fact]
    public void MissingHuffmanTablesFallBackToStandardTables()
    {
        // baseline-420-q90.jpg 未 -optimize，用的就是标准表：去掉 DHT 后结果必须完全相同
        var data = TestImages.Read("baseline-420-q90.jpg");
        var stripped = JpegFile.RemoveSegments(data, 0xC4);
        Assert.Equal(-1, JpegFile.Find(stripped, 0xC4));
        using var a = JpegCodec.Decode(data);
        using var b = JpegCodec.Decode(stripped);
        Assert.Equal(a.ToRgbaUnpremultiplied(), b.ToRgbaUnpremultiplied());

        // 只有 0、1 号有标准表：引用 2 号且缺表时报错
        int sos = JpegFile.Find(stripped, 0xDA);
        stripped[sos + 6] = 0x22;
        Assert.Throws<InvalidDataException>(() => JpegCodec.Decode(stripped));
    }

    [Fact]
    public void HierarchicalFrameIsReportedAsNotDecodable()
    {
        var data = TestImages.Read("gray.jpg");
        data[JpegFile.Find(data, 0xC0) + 1] = 0xC5;
        var info = JpegCodec.ReadInfo(data);
        Assert.False(info.IsDecodable);
        Assert.Throws<NotSupportedException>(() => JpegCodec.Decode(data));
    }

    [Fact]
    public void FractionalSamplingIsReportedAsNotDecodable()
    {
        var data = TestImages.Read("baseline-420-q90.jpg");
        int sof = JpegFile.Find(data, 0xC0);
        Assert.Equal(0x22, data[sof + 11]);   // Y: 2x2
        data[sof + 14] = 0x31;                // Cb: 3x1，与 Y 的 2 不成整数倍
        var info = JpegCodec.ReadInfo(data);
        Assert.False(info.IsDecodable);
        Assert.Throws<NotSupportedException>(() => JpegCodec.Decode(data));
    }

    [Fact]
    public void DnlHeightIsReportedAsNotDecodable()
    {
        var data = TestImages.Read("gray.jpg");
        int sof = JpegFile.Find(data, 0xC0);
        data[sof + 5] = 0;
        data[sof + 6] = 0;
        Assert.False(JpegCodec.ReadInfo(data).IsDecodable);
    }
}

/// <summary>测试用的 JPEG 段操作。</summary>
internal static class JpegFile
{
    public readonly record struct Segment(int Marker, int Offset, int Length);

    /// <summary>按顺序列出 SOI 之后的标记段（含 SOS，Length 覆盖段头与其后的熵编码数据），直到 EOI。</summary>
    public static List<Segment> Segments(byte[] data)
    {
        var list = new List<Segment>();
        int i = 2;
        while (i + 1 < data.Length)
        {
            Assert.Equal(0xFF, data[i]);
            int marker = data[i + 1];
            if (marker == 0xD9)
            {
                list.Add(new Segment(marker, i, 2));
                break;
            }
            int end = i + 2 + (data[i + 2] << 8 | data[i + 3]);
            if (marker == 0xDA)
            {
                while (!(data[end] == 0xFF && data[end + 1] != 0 && data[end + 1] is < 0xD0 or > 0xD7)) end++;
            }
            list.Add(new Segment(marker, i, end - i));
            i = end;
        }
        return list;
    }

    public static int Find(byte[] data, int marker) =>
        Segments(data).Where(s => s.Marker == marker).Select(s => s.Offset).DefaultIfEmpty(-1).First();

    public static byte[] RemoveSegments(byte[] data, int marker)
    {
        var result = new List<byte>(data.Take(2));
        foreach (var s in Segments(data))
            if (s.Marker != marker) result.AddRange(data.Skip(s.Offset).Take(s.Length));
        return result.ToArray();
    }

    /// <summary>按段重新拼接（SOI + 给定段）。</summary>
    public static byte[] Build(byte[] data, IEnumerable<Segment> segments)
    {
        var result = new List<byte>(data.Take(2));
        foreach (var s in segments) result.AddRange(data.Skip(s.Offset).Take(s.Length));
        return result.ToArray();
    }
}
