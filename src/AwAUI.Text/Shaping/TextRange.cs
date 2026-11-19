using System;

namespace AwAUI.Text;

/// <summary>文本范围，以 UTF-16 码元索引表示 [Start, End)。End 小于 Start 时表示反向（如选区的锚点在焦点之后）。</summary>
public readonly struct TextRange : IEquatable<TextRange>
{
    public TextRange(int start, int end)
    {
        Start = start;
        End = end;
    }

    public int Start { get; }
    public int End { get; }

    public int Min => Math.Min(Start, End);
    public int Max => Math.Max(Start, End);
    public int Length => Max - Min;
    public bool IsEmpty => Start == End;
    public bool IsReversed => End < Start;

    /// <summary>按 Start ≤ End 规范化。</summary>
    public TextRange Normalize() => new(Min, Max);

    public bool Contains(int offset) => offset >= Min && offset < Max;

    public static TextRange FromLength(int start, int length) => new(start, start + length);

    public bool Equals(TextRange other) => Start == other.Start && End == other.End;
    public override bool Equals(object? obj) => obj is TextRange r && Equals(r);
    public override int GetHashCode() => HashCodeCompat.Combine(Start, End);
    public static bool operator ==(TextRange a, TextRange b) => a.Equals(b);
    public static bool operator !=(TextRange a, TextRange b) => !a.Equals(b);
    public override string ToString() => $"[{Start}, {End})";
}
