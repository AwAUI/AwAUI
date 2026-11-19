using System;
using System.Collections.Generic;

namespace AwAUI.Markup.Compiler;

/// <summary>一份 <c>.awaui</c> 源文本及其行表。换行符 \n、\r\n、\r 均被识别。</summary>
public sealed class SourceText
{
    readonly int[] _lineStarts;

    public SourceText(string text, string? filePath = null)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        FilePath = filePath;
        _lineStarts = ComputeLineStarts(text);
    }

    public string Text { get; }
    public string? FilePath { get; }
    public int Length => Text.Length;
    public int LineCount => _lineStarts.Length;

    public char this[int index] => Text[index];

    public string ToString(TextSpan span) => Text.Substring(span.Start, span.Length);
    public override string ToString() => Text;

    /// <summary>把偏移换算为从 1 开始的行列。超出范围的偏移被钳制到文本末尾。</summary>
    public LinePosition GetLinePosition(int offset)
    {
        if (offset < 0) offset = 0;
        if (offset > Text.Length) offset = Text.Length;
        int lo = 0, hi = _lineStarts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (_lineStarts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return new LinePosition(lo + 1, offset - _lineStarts[lo] + 1);
    }

    /// <summary>第 <paramref name="line"/> 行（从 1 开始）的起始偏移。</summary>
    public int GetLineStart(int line) => _lineStarts[Math.Max(0, Math.Min(line - 1, _lineStarts.Length - 1))];

    static int[] ComputeLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (c == '\n')
            {
                starts.Add(i + 1);
            }
        }
        return starts.ToArray();
    }
}
