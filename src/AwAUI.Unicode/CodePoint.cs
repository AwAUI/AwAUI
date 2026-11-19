using System;
using System.Runtime.CompilerServices;

namespace AwAUI.Unicode;

/// <summary>
/// UTF-16 码位辅助（netstandard2.0 没有 System.Text.Rune）。
/// 孤立代理按其自身的代理码位返回（General_Category = Cs），不替换为 U+FFFD，保证偏移与长度和原文一一对应。
/// </summary>
public static class CodePoint
{
    /// <summary>替换字符 U+FFFD。</summary>
    public const int ReplacementCharacter = 0xFFFD;

    /// <summary>是否为合法码位（0..0x10FFFF）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsValid(int value) => (uint)value <= 0x10FFFF;

    /// <summary>是否为 Unicode 标量值（合法码位且不是代理）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsScalarValue(int value) => (((uint)value - 0x110000u) ^ 0xD800u) >= 0xFFEF0800u;

    /// <summary>是否需要两个 UTF-16 代码单元（增补平面）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSupplementary(int value) => (uint)(value - 0x10000) <= 0xFFFFF;

    /// <summary>编码为 UTF-16 所需的代码单元数（与 <see cref="Encode"/> 一致：非法值按 U+FFFD 计为 1）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Utf16Length(int value) => IsSupplementary(value) ? 2 : 1;

    /// <summary>解码 <paramref name="index"/> 处的码位，<paramref name="length"/> 为其占用的代码单元数（1 或 2）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Decode(ReadOnlySpan<char> text, int index, out int length)
    {
        int c = text[index];
        if ((uint)(c - 0xD800) <= 0x3FF && index + 1 < text.Length)
        {
            int d = text[index + 1];
            if ((uint)(d - 0xDC00) <= 0x3FF)
            {
                length = 2;
                return ((c - 0xD800) << 10) + (d - 0xDC00) + 0x10000;
            }
        }
        length = 1;
        return c;
    }

    /// <summary>解码 <paramref name="index"/> 处的码位。</summary>
    public static int Decode(ReadOnlySpan<char> text, int index) => Decode(text, index, out _);

    /// <summary>解码结束于 <paramref name="end"/>（不含）的码位，<paramref name="length"/> 为其代码单元数。</summary>
    public static int DecodeBefore(ReadOnlySpan<char> text, int end, out int length)
    {
        int c = text[end - 1];
        if ((uint)(c - 0xDC00) <= 0x3FF && end >= 2)
        {
            int h = text[end - 2];
            if ((uint)(h - 0xD800) <= 0x3FF)
            {
                length = 2;
                return ((h - 0xD800) << 10) + (c - 0xDC00) + 0x10000;
            }
        }
        length = 1;
        return c;
    }

    /// <summary>编码为 UTF-16 写入 <paramref name="destination"/>，返回写入的代码单元数；空间不足返回 0。</summary>
    public static int Encode(int value, Span<char> destination)
    {
        if (!IsValid(value)) value = ReplacementCharacter;
        if (value < 0x10000)
        {
            if (destination.Length < 1) return 0;
            destination[0] = (char)value;
            return 1;
        }
        if (destination.Length < 2) return 0;
        value -= 0x10000;
        destination[0] = (char)(0xD800 + (value >> 10));
        destination[1] = (char)(0xDC00 + (value & 0x3FF));
        return 2;
    }

    /// <summary>把索引调整到码位边界：若 <paramref name="index"/> 落在代理对中间，返回该对的起点。</summary>
    public static int AlignToStart(ReadOnlySpan<char> text, int index)
    {
        if (index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1]))
            return index - 1;
        return index;
    }

    /// <summary>逐码位枚举（不分配）。</summary>
    public static CodePointEnumerator Enumerate(ReadOnlySpan<char> text) => new(text);
}

/// <summary>按码位枚举 UTF-16 文本；<see cref="Current"/> 是码位，<see cref="Index"/> 与 <see cref="Length"/> 是其代码单元范围。</summary>
public ref struct CodePointEnumerator
{
    readonly ReadOnlySpan<char> _text;
    int _next;

    internal CodePointEnumerator(ReadOnlySpan<char> text)
    {
        _text = text;
        _next = 0;
        Index = 0;
        Length = 0;
        Current = 0;
    }

    /// <summary>当前码位。</summary>
    public int Current { get; private set; }

    /// <summary>当前码位的起始 UTF-16 偏移。</summary>
    public int Index { get; private set; }

    /// <summary>当前码位的代码单元数。</summary>
    public int Length { get; private set; }

    /// <summary>供 foreach 使用。</summary>
    public CodePointEnumerator GetEnumerator() => this;

    /// <summary>前进到下一个码位。</summary>
    public bool MoveNext()
    {
        if (_next >= _text.Length) return false;
        Index = _next;
        Current = CodePoint.Decode(_text, _next, out var len);
        Length = len;
        _next += len;
        return true;
    }
}
