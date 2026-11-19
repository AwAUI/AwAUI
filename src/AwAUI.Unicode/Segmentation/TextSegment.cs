using System;

namespace AwAUI.Unicode;

/// <summary>文本中的一段（UTF-16 偏移与长度）。</summary>
public readonly struct TextSegment : IEquatable<TextSegment>
{
    /// <summary>创建一段。</summary>
    public TextSegment(int start, int length)
    {
        Start = start;
        Length = length;
    }

    /// <summary>起始 UTF-16 偏移。</summary>
    public int Start { get; }

    /// <summary>UTF-16 代码单元数。</summary>
    public int Length { get; }

    /// <summary>结束偏移（不含）。</summary>
    public int End => Start + Length;

    /// <summary>判断相等。</summary>
    public bool Equals(TextSegment other) => Start == other.Start && Length == other.Length;

    /// <summary>判断相等。</summary>
    public override bool Equals(object? obj) => obj is TextSegment s && Equals(s);

    /// <summary>哈希。</summary>
    public override int GetHashCode() => HashCodeCompat.Combine(Start, Length);

    /// <summary>调试显示。</summary>
    public override string ToString() => $"[{Start}, {End})";

    /// <summary>相等运算。</summary>
    public static bool operator ==(TextSegment a, TextSegment b) => a.Equals(b);

    /// <summary>不等运算。</summary>
    public static bool operator !=(TextSegment a, TextSegment b) => !a.Equals(b);
}

/// <summary>单词分段结果：一段文本以及它是否像“词”（含字母、数字或表意文字，而非空白与标点）。</summary>
public readonly struct WordSegment
{
    /// <summary>创建一段。</summary>
    public WordSegment(int start, int length, bool isWordLike)
    {
        Start = start;
        Length = length;
        IsWordLike = isWordLike;
    }

    /// <summary>起始 UTF-16 偏移。</summary>
    public int Start { get; }

    /// <summary>UTF-16 代码单元数。</summary>
    public int Length { get; }

    /// <summary>结束偏移（不含）。</summary>
    public int End => Start + Length;

    /// <summary>是否含字母、数字或表意文字；双击选词等操作通常只选这类段。</summary>
    public bool IsWordLike { get; }

    /// <summary>作为普通文本段。</summary>
    public TextSegment Segment => new(Start, Length);

    /// <summary>调试显示。</summary>
    public override string ToString() => $"[{Start}, {End}){(IsWordLike ? " word" : "")}";
}

/// <summary>断行机会：在 <see cref="Position"/>（UTF-16 偏移）之前可以换行。</summary>
public readonly struct LineBreakOpportunity
{
    /// <summary>创建断行机会。</summary>
    public LineBreakOpportunity(int position, bool isMandatory)
    {
        Position = position;
        IsMandatory = isMandatory;
    }

    /// <summary>断点位置：新行从此偏移开始。</summary>
    public int Position { get; }

    /// <summary>是否为强制换行（UAX #14 的“!”：硬换行符之后，以及文本末尾）。否则是允许的断点（“÷”）。</summary>
    public bool IsMandatory { get; }

    /// <summary>调试显示。</summary>
    public override string ToString() => IsMandatory ? $"{Position}!" : $"{Position}÷";
}
