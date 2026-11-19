using System;
using System.Collections.Generic;

namespace AwAUI.Text;

/// <summary>
/// 段落构建器：按样式栈追加文本与行内占位盒，最后 <see cref="Build"/> 得到 <see cref="TextLayout"/>（design/19「段落布局」）。
/// 不是线程安全的。<see cref="Build"/> 之后仍可继续追加并再次构建。
/// </summary>
public sealed class ParagraphBuilder
{
    /// <summary>占位盒在文本中占用的字符（U+FFFC OBJECT REPLACEMENT CHARACTER）。</summary>
    public const char PlaceholderChar = '\uFFFC';

    readonly ParagraphStyle _style;
    readonly TextEngine _engine;
    readonly List<TextStyle> _stack = new();
    readonly List<StyleSpan> _spans = new();
    readonly List<PlaceholderRun> _placeholders = new();
    char[] _buffer = new char[64];
    int _length;

    public ParagraphBuilder(ParagraphStyle style, TextEngine engine)
    {
        _style = style ?? throw new ArgumentNullException(nameof(style));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>当前样式（栈顶；栈为空时为段落的默认文本样式）。</summary>
    public TextStyle CurrentStyle => _stack.Count > 0 ? _stack[_stack.Count - 1] : _style.TextStyle;

    /// <summary>已追加的 UTF-16 长度。</summary>
    public int Length => _length;

    /// <summary>已追加的占位盒数量。</summary>
    public int PlaceholderCount => _placeholders.Count;

    /// <summary>压入样式：之后追加的文本使用它，直到对应的 <see cref="Pop"/>。</summary>
    public void PushStyle(TextStyle style) => _stack.Add(style ?? throw new ArgumentNullException(nameof(style)));

    /// <summary>弹出最近压入的样式。栈为空时不做任何事。</summary>
    public void Pop()
    {
        if (_stack.Count > 0) _stack.RemoveAt(_stack.Count - 1);
    }

    public void AddText(string text) => AddText((text ?? throw new ArgumentNullException(nameof(text))).AsSpan());

    public void AddText(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return;
        Append(text);
    }

    /// <summary>追加一个行内占位盒（图片、行内组件）。它在文本中占一个 U+FFFC 字符，序号即 <see cref="TextLayout.GetPlaceholderRect"/> 的参数。</summary>
    public void AddPlaceholder(in PlaceholderStyle placeholder)
    {
        _placeholders.Add(new PlaceholderRun(_length, placeholder));
        Span<char> c = stackalloc char[1];
        c[0] = PlaceholderChar;
        Append(c);
    }

    void Append(ReadOnlySpan<char> text)
    {
        if ((long)_length + text.Length > int.MaxValue - 64) throw new InvalidOperationException("Paragraph text is too long.");
        int start = _length;
        if (_length + text.Length > _buffer.Length)
        {
            int cap = (int)Math.Min(int.MaxValue - 64, Math.Max((long)_buffer.Length * 2, (long)_length + text.Length));
            Array.Resize(ref _buffer, cap);
        }
        text.CopyTo(_buffer.AsSpan(_length));
        _length += text.Length;

        var style = CurrentStyle;
        if (_spans.Count > 0 && ReferenceEquals(_spans[_spans.Count - 1].Style, style) && _spans[_spans.Count - 1].End == start)
            _spans[_spans.Count - 1] = new StyleSpan(_spans[_spans.Count - 1].Start, _length, style);
        else
            _spans.Add(new StyleSpan(start, _length, style));
    }

    /// <summary>完成构建：分段、双向分析、字体回退与整形在此完成；断行在 <see cref="TextLayout.Layout"/> 中进行。</summary>
    public TextLayout Build()
    {
        var text = new string(_buffer, 0, _length);
        return new TextLayout(_engine, _style, text, _spans.ToArray(), _placeholders.ToArray());
    }
}

readonly struct StyleSpan
{
    public StyleSpan(int start, int end, TextStyle style)
    {
        Start = start;
        End = end;
        Style = style;
    }

    public int Start { get; }
    public int End { get; }
    public TextStyle Style { get; }
}

readonly struct PlaceholderRun
{
    public PlaceholderRun(int offset, PlaceholderStyle style)
    {
        Offset = offset;
        Style = style;
    }

    public int Offset { get; }
    public PlaceholderStyle Style { get; }
}
