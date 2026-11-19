using System;

namespace AwAUI.Css;

/// <summary>
/// 源文本中的一个位置。<see cref="Offset"/> 是相对原始文本（未经预处理，含 BOM 与 CRLF）的 UTF-16 偏移；
/// <see cref="Line"/> 与 <see cref="Column"/> 从 1 开始，列按 UTF-16 码元计数。
/// </summary>
public readonly struct SourcePosition : IEquatable<SourcePosition>, IComparable<SourcePosition>
{
    public SourcePosition(int offset, int line, int column)
    {
        Offset = offset;
        Line = line;
        Column = column;
    }

    /// <summary>相对原始文本的 0 基偏移。</summary>
    public int Offset { get; }

    /// <summary>1 基行号。CR、LF、CRLF、FF 都算一次换行。</summary>
    public int Line { get; }

    /// <summary>1 基列号（UTF-16 码元）。</summary>
    public int Column { get; }

    public bool Equals(SourcePosition other) => Offset == other.Offset && Line == other.Line && Column == other.Column;
    public override bool Equals(object? obj) => obj is SourcePosition p && Equals(p);
    public override int GetHashCode() => HashCodeCompat.Combine(Offset, Line, Column);
    public int CompareTo(SourcePosition other) => Offset.CompareTo(other.Offset);
    public static bool operator ==(SourcePosition a, SourcePosition b) => a.Equals(b);
    public static bool operator !=(SourcePosition a, SourcePosition b) => !a.Equals(b);
    public override string ToString() => FormattableString.Invariant($"{Line}:{Column}");
}

/// <summary>源文本中的半开区间 [Start, End)。</summary>
public readonly struct SourceRange : IEquatable<SourceRange>
{
    public SourceRange(SourcePosition start, SourcePosition end)
    {
        Start = start;
        End = end;
    }

    public SourcePosition Start { get; }
    public SourcePosition End { get; }

    /// <summary>区间覆盖的原始文本长度（UTF-16 码元）。</summary>
    public int Length => End.Offset - Start.Offset;

    public bool IsEmpty => End.Offset == Start.Offset;

    /// <summary>覆盖两个区间的最小区间。</summary>
    public static SourceRange Union(SourceRange a, SourceRange b)
        => new(a.Start.Offset <= b.Start.Offset ? a.Start : b.Start, a.End.Offset >= b.End.Offset ? a.End : b.End);

    public bool Contains(int offset) => offset >= Start.Offset && offset < End.Offset;

    public bool Equals(SourceRange other) => Start.Equals(other.Start) && End.Equals(other.End);
    public override bool Equals(object? obj) => obj is SourceRange r && Equals(r);
    public override int GetHashCode() => HashCodeCompat.Combine(Start.GetHashCode(), End.GetHashCode());
    public static bool operator ==(SourceRange a, SourceRange b) => a.Equals(b);
    public static bool operator !=(SourceRange a, SourceRange b) => !a.Equals(b);
    public override string ToString() => FormattableString.Invariant($"{Start}-{End}");
}
