using System;
using System.Collections.Generic;
using System.Globalization;

namespace AwAUI.Svg;

/// <summary>SVG 解析与构建过程中的非致命问题分类。</summary>
public enum SvgDiagnosticCode
{
    /// <summary>DOCTYPE（含内部子集）被整体跳过，其中声明的实体不会展开。</summary>
    DoctypeIgnored,
    /// <summary>未定义的实体引用或格式错误的字符引用，按字面文本保留。</summary>
    UndefinedEntity,
    /// <summary>不支持的元素（文本、滤镜、蒙版、图案、标记、图像等），连同子树一起忽略。</summary>
    UnsupportedElement,
    /// <summary>不支持的属性或特性（如 mask、filter、marker 引用、径向渐变焦点），被忽略或近似。</summary>
    UnsupportedFeature,
    /// <summary>属性或样式值无法解析，按规范忽略该声明。</summary>
    InvalidValue,
    /// <summary>路径数据有错误，按规范只渲染出错之前的部分。</summary>
    PathDataError,
    /// <summary>引用（href、url(#id)）指向不存在或类型不对的元素。</summary>
    MissingReference,
    /// <summary>引用成环（use、渐变 href），环上的引用被忽略。</summary>
    CircularReference,
    /// <summary>超出了 <see cref="SvgParseOptions"/> 的某项上限，超出部分被截断。</summary>
    LimitExceeded,
    /// <summary>&lt;style&gt; 中不支持的选择器或 @ 规则，整条规则被忽略。</summary>
    UnsupportedCss,
    /// <summary>诊断条数达到上限，其余诊断被省略。</summary>
    DiagnosticsTruncated,
}

/// <summary>一条诊断。行列号从 1 开始；0 表示没有位置信息。</summary>
public readonly struct SvgDiagnostic
{
    public SvgDiagnostic(SvgDiagnosticCode code, string message, int line, int column)
    {
        Code = code;
        Message = message;
        Line = line;
        Column = column;
    }

    public SvgDiagnosticCode Code { get; }
    public string Message { get; }
    public int Line { get; }
    public int Column { get; }

    public override string ToString() =>
        Line > 0
            ? string.Format(CultureInfo.InvariantCulture, "({0},{1}) {2}: {3}", Line, Column, Code, Message)
            : string.Format(CultureInfo.InvariantCulture, "{0}: {1}", Code, Message);
}

/// <summary>
/// SVG 无法解析：XML 不是良构的，或输入超出了 <see cref="SvgParseOptions"/> 的硬上限（长度、深度、元素数）。
/// 这是 <see cref="SvgDocument.Parse(string, SvgParseOptions)"/> 唯一会抛出的解析异常。
/// </summary>
public sealed class SvgParseException : FormatException
{
    public SvgParseException(string message, int line, int column)
        : base(line > 0 ? string.Format(CultureInfo.InvariantCulture, "({0},{1}): {2}", line, column, message) : message)
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }
    public int Column { get; }
}

/// <summary>源文本偏移 → 行列号（惰性建立行首表，二分查找）。</summary>
internal sealed class SourceLocator
{
    readonly string _text;
    int[]? _lineStarts;

    public SourceLocator(string text) => _text = text;

    public (int Line, int Column) Locate(int offset)
    {
        if (offset < 0) return (0, 0);
        if (_lineStarts == null)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < _text.Length; i++)
                if (_text[i] == '\n') starts.Add(i + 1);
            _lineStarts = starts.ToArray();
        }
        int idx = Array.BinarySearch(_lineStarts, Math.Min(offset, _text.Length));
        if (idx < 0) idx = ~idx - 1;
        return (idx + 1, offset - _lineStarts[idx] + 1);
    }
}

/// <summary>有上限的诊断收集器。</summary>
internal sealed class DiagnosticBag
{
    readonly List<SvgDiagnostic> _items = new();
    readonly int _max;
    readonly SourceLocator _locator;
    int _dropped;

    public DiagnosticBag(SourceLocator locator, int max)
    {
        _locator = locator;
        _max = Math.Max(0, max);
    }

    public void Add(SvgDiagnosticCode code, string message, int offset = -1)
    {
        if (_items.Count >= _max) { _dropped++; return; }
        var (line, col) = _locator.Locate(offset);
        _items.Add(new SvgDiagnostic(code, message, line, col));
    }

    public SvgDiagnostic[] ToArray()
    {
        if (_dropped == 0) return _items.ToArray();
        var list = new List<SvgDiagnostic>(_items)
        {
            new(SvgDiagnosticCode.DiagnosticsTruncated,
                string.Format(CultureInfo.InvariantCulture, "{0} more diagnostics were omitted.", _dropped), 0, 0),
        };
        return list.ToArray();
    }
}
