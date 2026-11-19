namespace AwAUI.Text.UnitTests;

/// <summary>大端字节写入器。</summary>
public sealed class BeWriter
{
    readonly List<byte> _bytes = new();
    public int Length => _bytes.Count;
    public BeWriter U8(int v) { _bytes.Add((byte)v); return this; }
    public BeWriter U16(int v) { _bytes.Add((byte)(v >> 8)); _bytes.Add((byte)v); return this; }
    public BeWriter I16(int v) => U16((ushort)(short)v);
    public BeWriter U32(uint v) { U16((int)(v >> 16)); return U16((int)(v & 0xFFFF)); }
    public BeWriter F2Dot14(float v) => I16((int)Math.Round(v * 16384));
    public BeWriter Bytes(byte[] b) { _bytes.AddRange(b); return this; }
    public BeWriter Tag(string t) { foreach (var c in t) _bytes.Add((byte)c); return this; }
    public BeWriter Pad4() { while (_bytes.Count % 4 != 0) _bytes.Add(0); return this; }
    public byte[] ToArray() => _bytes.ToArray();
}

/// <summary>组件描述：引用字形、标志与参数（不含 MORE_COMPONENTS，由构建器设置）。</summary>
public sealed record Component(int Glyph, int Arg1, int Arg2, bool XY = true, bool Words = true,
    float[]? Transform = null, int ExtraFlags = 0);

/// <summary>在测试中构造最小的 TrueType 字体（head、hhea、maxp、hmtx、cmap、loca、glyf，可选 kern 等），用于覆盖真实字体中少见的结构与损坏数据。</summary>
public sealed class SyntheticFont
{
    public readonly List<byte[]> Glyphs = new();
    public readonly List<(int Advance, int Lsb)> Metrics = new();
    public readonly Dictionary<string, byte[]> ExtraTables = new();
    public byte[]? Cmap;
    public int UnitsPerEm = 1000;
    public bool LongLoca = true;

    public int AddGlyph(byte[] data, int advance = 500)
    {
        Glyphs.Add(data);
        Metrics.Add((advance, 0));
        return Glyphs.Count - 1;
    }

    /// <summary>简单字形：每个轮廓是 (x, y, onCurve) 点列表。坐标全部用 16 位写入，不压缩。</summary>
    public static byte[] Simple(params (int X, int Y, bool On)[][] contours)
    {
        var all = contours.SelectMany(c => c).ToArray();
        var w = new BeWriter();
        w.I16(contours.Length);
        w.I16(all.Length == 0 ? 0 : all.Min(p => p.X)).I16(all.Length == 0 ? 0 : all.Min(p => p.Y));
        w.I16(all.Length == 0 ? 0 : all.Max(p => p.X)).I16(all.Length == 0 ? 0 : all.Max(p => p.Y));
        int end = -1;
        foreach (var c in contours) w.U16(end += c.Length);
        w.U16(0);   // 指令长度
        foreach (var p in all) w.U8(p.On ? 1 : 0);
        int prev = 0;
        foreach (var p in all) { w.I16(p.X - prev); prev = p.X; }
        prev = 0;
        foreach (var p in all) { w.I16(p.Y - prev); prev = p.Y; }
        return w.ToArray();
    }

    public static (int, int, bool)[] Square(int x, int y, int size) => new[]
    {
        (x, y, true), (x, y + size, true), (x + size, y + size, true), (x + size, y, true),
    };

    public static byte[] Composite(params Component[] components)
    {
        var w = new BeWriter();
        w.I16(-1).I16(0).I16(0).I16(0).I16(0);
        for (int i = 0; i < components.Length; i++)
        {
            var c = components[i];
            int flags = c.ExtraFlags;
            if (c.Words) flags |= 0x0001;
            if (c.XY) flags |= 0x0002;
            if (i < components.Length - 1) flags |= 0x0020;
            if (c.Transform is { Length: 1 }) flags |= 0x0008;
            else if (c.Transform is { Length: 2 }) flags |= 0x0040;
            else if (c.Transform is { Length: 4 }) flags |= 0x0080;
            w.U16(flags).U16(c.Glyph);
            if (c.Words) { if (c.XY) w.I16(c.Arg1).I16(c.Arg2); else w.U16(c.Arg1).U16(c.Arg2); }
            else { w.U8((byte)(sbyte)c.Arg1).U8((byte)(sbyte)c.Arg2); }
            if (c.Transform is { } t) foreach (var v in t) w.F2Dot14(v);
        }
        return w.ToArray();
    }

    /// <summary>格式 4 的 cmap：把 (码点, 字形) 逐个写成独立的段（idDelta 映射）。</summary>
    public static byte[] CmapFormat4(params (int Cp, int Glyph)[] map)
    {
        var segs = map.OrderBy(m => m.Cp).Select(m => (Start: m.Cp, End: m.Cp, Delta: m.Glyph - m.Cp)).ToList();
        segs.Add((0xFFFF, 0xFFFF, 1));
        var sub = new BeWriter();
        int n = segs.Count;
        sub.U16(4).U16(16 + n * 8).U16(0).U16(n * 2).U16(0).U16(0).U16(0);
        foreach (var s in segs) sub.U16(s.End);
        sub.U16(0);
        foreach (var s in segs) sub.U16(s.Start);
        foreach (var s in segs) sub.U16(s.Delta & 0xFFFF);
        foreach (var _ in segs) sub.U16(0);
        return CmapWith((3, 1, sub.ToArray()));
    }

    /// <summary>由若干 (平台, 编码, 子表数据) 组装 cmap 表。</summary>
    public static byte[] CmapWith(params (int Platform, int Encoding, byte[] Subtable)[] subtables)
    {
        var w = new BeWriter();
        w.U16(0).U16(subtables.Length);
        int offset = 4 + subtables.Length * 8;
        foreach (var s in subtables)
        {
            w.U16(s.Platform).U16(s.Encoding).U32((uint)offset);
            offset += s.Subtable.Length;
        }
        foreach (var s in subtables) w.Bytes(s.Subtable);
        return w.ToArray();
    }

    public byte[] Build()
    {
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        int numGlyphs = Glyphs.Count;

        var glyf = new BeWriter();
        var loca = new BeWriter();
        foreach (var g in Glyphs)
        {
            if (LongLoca) loca.U32((uint)glyf.Length); else loca.U16(glyf.Length / 2);
            glyf.Bytes(g);
            while (glyf.Length % (LongLoca ? 4 : 2) != 0) glyf.U8(0);
        }
        if (LongLoca) loca.U32((uint)glyf.Length); else loca.U16(glyf.Length / 2);

        var head = new BeWriter();
        head.U32(0x00010000).U32(0x00010000).U32(0).U32(0x5F0F3CF5).U16(0).U16(UnitsPerEm);
        head.U32(0).U32(0).U32(0).U32(0);   // created / modified
        head.I16(0).I16(-200).I16(1000).I16(800).U16(0).U16(8).I16(2).I16(LongLoca ? 1 : 0).I16(0);

        var hhea = new BeWriter();
        hhea.U32(0x00010000).I16(800).I16(-200).I16(90).U16(1000).I16(0).I16(0).I16(1000).I16(1).I16(0).I16(0);
        hhea.I16(0).I16(0).I16(0).I16(0).I16(0).U16(numGlyphs);

        var maxp = new BeWriter();
        maxp.U32(0x00005000).U16(numGlyphs);

        var hmtx = new BeWriter();
        foreach (var (adv, lsb) in Metrics) hmtx.U16(adv).I16(lsb);

        tables["head"] = head.ToArray();
        tables["hhea"] = hhea.ToArray();
        tables["maxp"] = maxp.ToArray();
        tables["hmtx"] = hmtx.ToArray();
        tables["loca"] = loca.ToArray();
        tables["glyf"] = glyf.ToArray();
        if (Cmap is not null) tables["cmap"] = Cmap;
        foreach (var kv in ExtraTables) tables[kv.Key] = kv.Value;
        return Assemble(tables);
    }

    public static byte[] Assemble(IDictionary<string, byte[]> tables, uint version = 0x00010000)
    {
        var w = new BeWriter();
        int n = tables.Count;
        w.U32(version).U16(n).U16(0).U16(0).U16(0);
        int offset = 12 + n * 16;
        foreach (var kv in tables)
        {
            w.Tag(kv.Key.PadRight(4)).U32(0).U32((uint)offset).U32((uint)kv.Value.Length);
            offset += (kv.Value.Length + 3) & ~3;
        }
        foreach (var kv in tables) w.Bytes(kv.Value).Pad4();
        return w.ToArray();
    }

    /// <summary>把若干单字体文件合并成 TrueType 集合（.ttc），重写各自表目录中的偏移。</summary>
    public static byte[] MakeCollection(params byte[][] fonts)
    {
        int header = 12 + fonts.Length * 4;
        var w = new BeWriter();
        w.Tag("ttcf").U32(0x00010000).U32((uint)fonts.Length);
        int pos = header;
        var starts = new List<int>();
        foreach (var f in fonts)
        {
            starts.Add(pos);
            w.U32((uint)pos);
            pos += (f.Length + 3) & ~3;
        }
        var result = new List<byte>(w.ToArray());
        for (int i = 0; i < fonts.Length; i++)
        {
            var f = (byte[])fonts[i].Clone();
            int numTables = (f[4] << 8) | f[5];
            for (int t = 0; t < numTables; t++)
            {
                int rec = 12 + t * 16 + 8;
                uint off = (uint)((f[rec] << 24) | (f[rec + 1] << 16) | (f[rec + 2] << 8) | f[rec + 3]);
                off += (uint)starts[i];
                f[rec] = (byte)(off >> 24); f[rec + 1] = (byte)(off >> 16); f[rec + 2] = (byte)(off >> 8); f[rec + 3] = (byte)off;
            }
            result.AddRange(f);
            while (result.Count % 4 != 0) result.Add(0);
        }
        return result.ToArray();
    }
}
