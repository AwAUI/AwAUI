using System;

namespace AwAUI.Markup.Compiler;

/// <summary>源文本中的一段区间：起始偏移（UTF-16 码元）与长度。</summary>
public readonly struct TextSpan : IEquatable<TextSpan>
{
    public TextSpan(int start, int length)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        Start = start;
        Length = length;
    }

    public int Start { get; }
    public int Length { get; }
    public int End => Start + Length;
    public bool IsEmpty => Length == 0;

    public static TextSpan FromBounds(int start, int end) => new(start, end - start);

    public bool Contains(TextSpan other) => other.Start >= Start && other.End <= End;
    public bool Contains(int position) => position >= Start && position < End;

    public bool Equals(TextSpan other) => Start == other.Start && Length == other.Length;
    public override bool Equals(object? obj) => obj is TextSpan s && Equals(s);
    public override int GetHashCode() => HashCodeCompat.Combine(Start, Length);
    public static bool operator ==(TextSpan a, TextSpan b) => a.Equals(b);
    public static bool operator !=(TextSpan a, TextSpan b) => !a.Equals(b);
    public override string ToString() => $"[{Start}..{End})";
}

/// <summary>行列位置，均从 1 开始（与编辑器和 #line 指令一致）。</summary>
public readonly struct LinePosition : IEquatable<LinePosition>
{
    public LinePosition(int line, int column)
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }
    public int Column { get; }

    public bool Equals(LinePosition other) => Line == other.Line && Column == other.Column;
    public override bool Equals(object? obj) => obj is LinePosition p && Equals(p);
    public override int GetHashCode() => HashCodeCompat.Combine(Line, Column);
    public override string ToString() => $"({Line},{Column})";
}
