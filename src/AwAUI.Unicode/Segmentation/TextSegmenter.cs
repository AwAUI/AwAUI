using System;

namespace AwAUI.Unicode;

/// <summary>
/// 文本分段入口：字素簇、单词、句子（UAX #29）与断行机会（UAX #14）。所有偏移都是 UTF-16 代码单元偏移，枚举过程不分配。
/// </summary>
public static class TextSegmenter
{
    /// <summary>按扩展字素簇枚举。</summary>
    public static GraphemeEnumerator EnumerateGraphemes(ReadOnlySpan<char> text) => new(text);

    /// <summary>按 UAX #29 单词边界枚举（空白与标点也各自成段）。</summary>
    public static WordEnumerator EnumerateWords(ReadOnlySpan<char> text) => new(text);

    /// <summary>按 UAX #29 默认句子边界枚举。</summary>
    public static SentenceEnumerator EnumerateSentences(ReadOnlySpan<char> text) => new(text);

    /// <summary>枚举 UAX #14 断行机会（不含位置 0，总以文本末尾的强制断点结束；空文本没有断点）。</summary>
    public static LineBreakEnumerator EnumerateLineBreaks(ReadOnlySpan<char> text) => new(text);

    /// <summary>按指定的严格程度（CJ 小假名等的解析方式）枚举 UAX #14 断行机会。</summary>
    public static LineBreakEnumerator EnumerateLineBreaks(ReadOnlySpan<char> text, LineBreakStrictness strictness) => new(text, strictness);

    /// <summary>
    /// <paramref name="index"/> 之后的下一个字素簇边界（大于 index；已在末尾时返回文本长度）。
    /// 从 index 往前找到与上下文无关的安全边界再向后扫描，因此 index 可以落在簇中间。
    /// </summary>
    public static int GetNextGraphemeBoundary(ReadOnlySpan<char> text, int index)
    {
        if ((uint)index > (uint)text.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (index >= text.Length) return text.Length;
        int start = FindSafeGraphemeStart(text, index);
        var e = new GraphemeEnumerator(text.Slice(start));
        while (e.MoveNext())
        {
            int end = start + e.Current.End;
            if (end > index) return end;
        }
        return text.Length;
    }

    /// <summary><paramref name="index"/> 之前的上一个字素簇边界（小于 index；index 为 0 时返回 0）。</summary>
    public static int GetPreviousGraphemeBoundary(ReadOnlySpan<char> text, int index)
    {
        if ((uint)index > (uint)text.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == 0) return 0;
        int start = FindSafeGraphemeStart(text, index - 1);
        int result = start;
        var e = new GraphemeEnumerator(text.Slice(start));
        while (e.MoveNext())
        {
            int s = start + e.Current.Start;
            if (s >= index) break;
            result = s;
        }
        return result;
    }

    /// <summary><paramref name="index"/> 是否是字素簇边界（0 与文本长度总是边界）。</summary>
    public static bool IsGraphemeBoundary(ReadOnlySpan<char> text, int index)
    {
        if ((uint)index > (uint)text.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == 0 || index == text.Length) return true;
        return GetPreviousGraphemeBoundary(text, NextCodePointStart(text, index)) == index;
    }

    /// <summary>包含 <paramref name="index"/> 处代码单元的字素簇。</summary>
    public static TextSegment GetGraphemeAt(ReadOnlySpan<char> text, int index)
    {
        if ((uint)index >= (uint)text.Length) throw new ArgumentOutOfRangeException(nameof(index));
        int end = GetNextGraphemeBoundary(text, index);
        int start = GetPreviousGraphemeBoundary(text, end);
        return new TextSegment(start, end - start);
    }

    static int NextCodePointStart(ReadOnlySpan<char> text, int index)
    {
        index = CodePoint.AlignToStart(text, index);
        CodePoint.Decode(text, index, out int len);
        return index + len;
    }

    /// <summary>不晚于 <paramref name="index"/> 的一个与上下文无关的簇边界（最坏情况为 0）。</summary>
    internal static int FindSafeGraphemeStart(ReadOnlySpan<char> text, int index)
    {
        int p = CodePoint.AlignToStart(text, index);
        if (p == 0) return 0;
        int nextSeg = UnicodeTables.Segment(CodePoint.Decode(text, p));
        while (p > 0)
        {
            int cp = CodePoint.DecodeBefore(text, p, out int len);
            int prevSeg = UnicodeTables.Segment(cp);
            if (GraphemeBreaker.IsSafeBoundary(prevSeg, nextSeg)) return p;
            p -= len;
            nextSeg = prevSeg;
        }
        return 0;
    }
}
