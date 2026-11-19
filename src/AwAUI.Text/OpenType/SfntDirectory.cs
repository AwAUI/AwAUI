using System;
using System.Collections.Generic;

namespace AwAUI.Text.OpenType;

/// <summary>sfnt 容器（单个 .ttf/.otf，或 .ttc 集合中的一个字型）的表目录。</summary>
internal sealed class SfntDirectory
{
    const uint TrueTypeVersion = 0x00010000;
    const uint TrueTag = 0x74727565;   // 'true'（旧版 Apple TrueType）
    const uint OttoTag = 0x4F54544F;   // 'OTTO'（CFF 轮廓）
    const uint TtcfTag = 0x74746366;   // 'ttcf'
    const uint WoffTag = 0x774F4646;   // 'wOFF'
    const uint Woff2Tag = 0x774F4632;  // 'wOF2'

    /// <summary>防止恶意数据声明海量表或字型。</summary>
    const int MaxTables = 1024;
    const int MaxFaces = 4096;

    readonly Dictionary<uint, (int Offset, int Length)> _tables;

    SfntDirectory(ReadOnlyMemory<byte> file, uint version, Dictionary<uint, (int, int)> tables)
    {
        File = file;
        SfntVersion = version;
        _tables = tables;
    }

    /// <summary>整个文件（表偏移相对于文件开头，集合中的字型共享表数据）。</summary>
    public ReadOnlyMemory<byte> File { get; }
    public uint SfntVersion { get; }
    public IEnumerable<uint> Tags => _tables.Keys;

    public bool Contains(uint tag) => _tables.ContainsKey(tag);

    public bool TryGetTable(uint tag, out ReadOnlyMemory<byte> table)
    {
        if (_tables.TryGetValue(tag, out var r))
        {
            table = File.Slice(r.Offset, r.Length);
            return true;
        }
        table = default;
        return false;
    }

    public ReadOnlyMemory<byte> GetTable(uint tag) => TryGetTable(tag, out var t) ? t : ReadOnlyMemory<byte>.Empty;

    static void CheckSupportedContainer(uint tag)
    {
        if (tag == WoffTag || tag == Woff2Tag)
            throw new NotSupportedException("WOFF/WOFF2 font containers are not supported yet; decompress to an sfnt (.ttf/.otf) first.");
    }

    public static bool IsCollection(ReadOnlySpan<byte> data) => data.Length >= 4 && Be.U32(data, 0) == TtcfTag;

    /// <summary>文件中的字型数量：.ttc 为集合中的数量，单字体文件为 1。</summary>
    public static int GetFaceCount(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) throw new InvalidFontException("Font data is too short.");
        uint tag = Be.U32(data, 0);
        CheckSupportedContainer(tag);
        if (tag != TtcfTag) return 1;
        uint n = Be.U32(data, 8);
        long available = (data.Length - 12L) / 4;
        if (n == 0 || n > MaxFaces || n > available) throw new InvalidFontException($"Invalid font collection face count {n}.");
        return (int)n;
    }

    public static SfntDirectory Read(ReadOnlyMemory<byte> file, int faceIndex)
    {
        var data = file.Span;
        if (data.Length < 12) throw new InvalidFontException("Font data is too short.");
        uint tag = Be.U32(data, 0);
        CheckSupportedContainer(tag);
        int offset = 0;
        if (tag == TtcfTag)
        {
            int count = GetFaceCount(data);
            if ((uint)faceIndex >= (uint)count) throw new ArgumentOutOfRangeException(nameof(faceIndex), $"The collection has {count} faces.");
            offset = Be.Offset32(data, 12 + faceIndex * 4);
        }
        else if (faceIndex != 0)
            throw new ArgumentOutOfRangeException(nameof(faceIndex), "A single-font file only has face 0.");

        uint version = Be.U32(data, offset);
        if (version != TrueTypeVersion && version != TrueTag && version != OttoTag)
            throw new InvalidFontException($"Unknown sfnt version 0x{version:X8}.");
        int numTables = Be.U16(data, offset + 4);
        if (numTables > MaxTables) throw new InvalidFontException($"Too many tables ({numTables}).");

        var tables = new Dictionary<uint, (int, int)>(numTables);
        for (int i = 0; i < numTables; i++)
        {
            int rec = offset + 12 + i * 16;
            uint t = Be.U32(data, rec);
            uint off = Be.U32(data, rec + 8);
            uint len = Be.U32(data, rec + 12);
            // 越界的表视为不存在（宽容处理，交给上层判断是否缺少必需表）
            if (off > (uint)data.Length || len > (uint)data.Length - off) continue;
            if (!tables.ContainsKey(t)) tables.Add(t, ((int)off, (int)len));
        }
        return new SfntDirectory(file, version, tables);
    }
}
