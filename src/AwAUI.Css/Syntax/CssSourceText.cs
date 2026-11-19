using System;
using System.Collections.Generic;
using System.Text;

namespace AwAUI.Css.Syntax;

/// <summary>
/// 按 CSS Syntax 3 §3.3 预处理后的源文本：去掉开头 BOM，CRLF/CR/FF 统一为 LF，NUL 替换为 U+FFFD。
/// 分词器在预处理后的文本上工作，并通过本类把位置映射回原始文本的偏移、行与列。
/// </summary>
internal sealed class CssSourceText
{
    readonly int _bom;
    readonly int[]? _removedCr;      // 预处理文本中「其前被删掉一个 CR」的 LF 下标，升序
    readonly int[] _lineStarts;      // 预处理文本中每行起始下标
    int _lineHint;

    public CssSourceText(string original)
    {
        Original = original ?? throw new ArgumentNullException(nameof(original));
        _bom = original.Length > 0 && original[0] == '﻿' ? 1 : 0;
        var lineStarts = new List<int> { 0 };

        bool needsCopy = false;
        for (int i = _bom; i < original.Length; i++)
        {
            char c = original[i];
            if (c == '\r' || c == '\f' || c == '\0') { needsCopy = true; break; }
        }

        if (!needsCopy)
        {
            Text = _bom == 0 ? original : original.Substring(1);
            int idx = 0;
            while ((idx = Text.IndexOf('\n', idx)) >= 0)
                lineStarts.Add(++idx);
            _lineStarts = lineStarts.ToArray();
            return;
        }

        var sb = new StringBuilder(original.Length);
        List<int>? removed = null;
        for (int i = _bom; i < original.Length; i++)
        {
            char c = original[i];
            switch (c)
            {
                case '\r':
                    if (i + 1 < original.Length && original[i + 1] == '\n')
                    {
                        (removed ??= new List<int>()).Add(sb.Length);
                        continue; // 由随后的 LF 记一次换行
                    }
                    sb.Append('\n');
                    lineStarts.Add(sb.Length);
                    break;
                case '\f':
                    sb.Append('\n');
                    lineStarts.Add(sb.Length);
                    break;
                case '\n':
                    sb.Append('\n');
                    lineStarts.Add(sb.Length);
                    break;
                case '\0':
                    sb.Append('�');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        Text = sb.ToString();
        _removedCr = removed?.ToArray();
        _lineStarts = lineStarts.ToArray();
    }

    /// <summary>调用方提供的原始文本。</summary>
    public string Original { get; }

    /// <summary>预处理后的文本。</summary>
    public string Text { get; }

    /// <summary>预处理文本中的下标映射到原始文本偏移。</summary>
    public int ToOriginalOffset(int p)
    {
        if (_removedCr is null) return p + _bom;
        int idx = Array.BinarySearch(_removedCr, p);
        int before = idx >= 0 ? idx : ~idx; // 严格小于 p 的被删 CR 个数
        return p + _bom + before;
    }

    /// <summary>预处理文本中的下标对应的源位置。对单调递增的查询是摊还 O(1)。</summary>
    public SourcePosition GetPosition(int p)
    {
        int line = FindLine(p);
        return new SourcePosition(ToOriginalOffset(p), line + 1, p - _lineStarts[line] + 1);
    }

    public SourceRange GetRange(int start, int end)
    {
        int line = FindLine(start);
        var a = new SourcePosition(ToOriginalOffset(start), line + 1, start - _lineStarts[line] + 1);
        // 绝大多数记号不跨行：终点与起点同一行时免去第二次查找
        if (line + 1 >= _lineStarts.Length || end < _lineStarts[line + 1])
            return new SourceRange(a, new SourcePosition(ToOriginalOffset(end), line + 1, end - _lineStarts[line] + 1));
        return new SourceRange(a, GetPosition(end));
    }

    int FindLine(int p)
    {
        var starts = _lineStarts;
        int h = _lineHint;
        if (h < starts.Length && starts[h] <= p)
        {
            // 绝大多数查询落在当前行或之后的少数几行
            for (int k = 0; k < 4; k++)
            {
                if (h + 1 >= starts.Length || starts[h + 1] > p) { _lineHint = h; return h; }
                h++;
            }
        }
        int lo = 0, hi = starts.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (starts[mid] <= p) lo = mid; else hi = mid - 1;
        }
        _lineHint = lo;
        return lo;
    }

    /// <summary>取原始文本中由两个源位置界定的子串。</summary>
    public string Slice(SourcePosition start, SourcePosition end)
        => end.Offset <= start.Offset ? string.Empty : Original.Substring(start.Offset, end.Offset - start.Offset);
}
