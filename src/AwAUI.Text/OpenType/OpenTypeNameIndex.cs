using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;

namespace AwAUI.Text.OpenType;

/// <summary>
/// 只读取 sfnt 头部、表目录与 'name' 表的轻量读取器。
/// <para>
/// 系统字体索引（族名 → 文件）只需要族名，不需要字型：Windows 的 <c>%WINDIR%\Fonts</c> 约 470 MB，
/// 逐个面加载会把进程私有内存推到同一量级。这里每次只读一个面的头部与 name 表（通常是几 KB），
/// 字型在真正用到某个族时才由 <see cref="FontCollection"/> 按需加载。
/// </para>
/// 表偏移相对文件开头（与 <see cref="SfntDirectory"/> 一致，.ttc 的各面共享同一份文件）。
/// </summary>
internal static class OpenTypeNameIndex
{
    const uint TrueTypeVersion = 0x00010000;
    const uint TrueTag = 0x74727565;   // 'true'（旧版 Apple TrueType）
    const uint OttoTag = 0x4F54544F;   // 'OTTO'（CFF 轮廓）
    const uint TtcfTag = 0x74746366;   // 'ttcf'
    const uint WoffTag = 0x774F4646;   // 'wOFF'
    const uint Woff2Tag = 0x774F4632;  // 'wOF2'
    const uint NameTag = 0x6E616D65;   // 'name'

    /// <summary>与 <see cref="SfntDirectory"/> 相同的上限，防止恶意数据声明海量表或字型。</summary>
    const int MaxTables = 1024;
    const int MaxFaces = 4096;

    /// <summary>name 表本身通常小于 1 MB；超过上限的文件视为损坏，避免一次分配巨量内存。</summary>
    const int MaxNameTableBytes = 16 * 1024 * 1024;

    /// <summary>
    /// 读取文件中每个面的族名（与 <see cref="OpenTypeFace"/> 的 <c>FamilyNames</c> 一致：排版族名优先，其次是旧式族名，去重）。
    /// 文件无法解析时返回空数组，不抛异常。
    /// </summary>
    public static IReadOnlyList<string[]> ReadFamilyNames(string path)
    {
        // 建索引时要读几百个文件：缓冲区从池里租（同一线程反复拿到同一块），文件流不带自己的缓冲（bufferSize: 1），
        // 否则每个文件多分配一个 4 KB 的流缓冲和一个与 name 表等长的数组
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
            ReadExactly(stream, buffer, 0, 12);
            uint tag = Be.U32(buffer, 0);
            if (tag == WoffTag || tag == Woff2Tag) return Array.Empty<string[]>();   // 尚不支持的容器
            if (tag != TtcfTag) return new[] { ReadFaceFamilyNames(stream, 0, ref buffer) };

            uint count = Be.U32(buffer, 8);
            if (count == 0 || count > MaxFaces) return Array.Empty<string[]>();
            Ensure(ref buffer, (int)count * 4);
            ReadExactly(stream, buffer, 0, (int)count * 4);
            var offsets = new uint[count];
            for (int i = 0; i < offsets.Length; i++) offsets[i] = Be.U32(buffer, i * 4);
            var names = new string[offsets.Length][];
            for (int i = 0; i < offsets.Length; i++) names[i] = ReadFaceFamilyNames(stream, offsets[i], ref buffer);
            return names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidFontException
            or NotSupportedException or ArgumentOutOfRangeException)
        {
            return Array.Empty<string[]>();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static string[] ReadFaceFamilyNames(FileStream stream, long offset, ref byte[] buffer)
    {
        stream.Position = offset;
        ReadExactly(stream, buffer, 0, 12);
        uint version = Be.U32(buffer, 0);
        if (version != TrueTypeVersion && version != TrueTag && version != OttoTag)
            throw new InvalidFontException($"Unknown sfnt version 0x{version:X8}.");
        int numTables = Be.U16(buffer, 4);
        if (numTables <= 0 || numTables > MaxTables) throw new InvalidFontException($"Too many tables ({numTables}).");

        Ensure(ref buffer, numTables * 16);
        ReadExactly(stream, buffer, 0, numTables * 16);
        for (int i = 0; i < numTables; i++)
        {
            int rec = i * 16;
            if (Be.U32(buffer, rec) != NameTag) continue;
            long tableOffset = Be.U32(buffer, rec + 8);
            long length = Be.U32(buffer, rec + 12);
            if (length == 0 || length > MaxNameTableBytes || tableOffset + length > stream.Length) return Array.Empty<string>();
            stream.Position = tableOffset;
            Ensure(ref buffer, (int)length);
            ReadExactly(stream, buffer, 0, (int)length);
            return FamilyNames(buffer.AsSpan(0, (int)length));
        }
        return Array.Empty<string>();
    }

    /// <summary>保证缓冲区至少有 <paramref name="size"/> 字节（内容不保留）。</summary>
    static void Ensure(ref byte[] buffer, int size)
    {
        if (buffer.Length >= size) return;
        ArrayPool<byte>.Shared.Return(buffer);
        buffer = ArrayPool<byte>.Shared.Rent(size);
    }

    static string[] FamilyNames(ReadOnlySpan<byte> table)
    {
        var legacy = NameTable.Get(table, NameTable.Family);
        var primary = NameTable.Get(table, NameTable.TypographicFamily) ?? legacy ?? NameTable.Get(table, NameTable.FullName);
        if (primary is null || primary.Length == 0)
            return legacy is null || legacy.Length == 0 ? Array.Empty<string>() : new[] { legacy };
        return legacy is null || legacy.Length == 0 || string.Equals(primary, legacy, StringComparison.OrdinalIgnoreCase)
            ? new[] { primary }
            : new[] { primary, legacy };
    }

    static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int read = stream.Read(buffer, offset, count);
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
            count -= read;
        }
    }
}
