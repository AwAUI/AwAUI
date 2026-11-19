using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace AwAUI.Text.OpenType;

/// <summary>带边界检查的大端读取。越界时抛出 <see cref="InvalidFontException"/>。</summary>
internal static class Be
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Check(ReadOnlySpan<byte> s, int offset, int size)
    {
        if (offset < 0 || offset > s.Length - size) Throw(offset, size, s.Length);
    }

    static void Throw(int offset, int size, int length) =>
        throw new InvalidFontException($"Font data out of range: offset {offset}, size {size}, available {length}.");

    public static byte U8(ReadOnlySpan<byte> s, int offset)
    {
        Check(s, offset, 1);
        return s[offset];
    }

    public static sbyte I8(ReadOnlySpan<byte> s, int offset) => (sbyte)U8(s, offset);

    public static ushort U16(ReadOnlySpan<byte> s, int offset)
    {
        Check(s, offset, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(s.Slice(offset));
    }

    public static short I16(ReadOnlySpan<byte> s, int offset)
    {
        Check(s, offset, 2);
        return BinaryPrimitives.ReadInt16BigEndian(s.Slice(offset));
    }

    public static uint U32(ReadOnlySpan<byte> s, int offset)
    {
        Check(s, offset, 4);
        return BinaryPrimitives.ReadUInt32BigEndian(s.Slice(offset));
    }

    public static int I32(ReadOnlySpan<byte> s, int offset)
    {
        Check(s, offset, 4);
        return BinaryPrimitives.ReadInt32BigEndian(s.Slice(offset));
    }

    /// <summary>16.16 定点数。</summary>
    public static float Fixed(ReadOnlySpan<byte> s, int offset) => I32(s, offset) / 65536f;

    /// <summary>2.14 定点数。</summary>
    public static float F2Dot14(ReadOnlySpan<byte> s, int offset) => I16(s, offset) / 16384f;

    /// <summary>把 32 位无符号偏移转换为 int，超出 int 范围时视为损坏。</summary>
    public static int Offset32(ReadOnlySpan<byte> s, int offset)
    {
        uint v = U32(s, offset);
        if (v > int.MaxValue) throw new InvalidFontException($"Offset {v} is too large.");
        return (int)v;
    }

    public static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> s, int offset, int length)
    {
        if (length < 0) Throw(offset, length, s.Length);
        Check(s, offset, length);
        return s.Slice(offset, length);
    }

    public static ReadOnlyMemory<byte> Slice(ReadOnlyMemory<byte> m, int offset, int length)
    {
        if (length < 0 || offset < 0 || offset > m.Length - length) Throw(offset, length, m.Length);
        return m.Slice(offset, length);
    }
}
