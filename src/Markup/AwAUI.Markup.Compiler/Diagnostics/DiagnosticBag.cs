using System.Collections.Generic;

namespace AwAUI.Markup.Compiler;

/// <summary>收集诊断，负责把偏移换算为行列。</summary>
internal sealed class DiagnosticBag
{
    readonly SourceText _source;
    readonly List<MarkupDiagnostic> _items = new();

    public DiagnosticBag(SourceText source) => _source = source;

    public IReadOnlyList<MarkupDiagnostic> Items => _items;
    public int Count => _items.Count;

    /// <summary>回滚到之前记录的数量（用于试探性解析）。</summary>
    public void Truncate(int count)
    {
        if (count < _items.Count) _items.RemoveRange(count, _items.Count - count);
    }

    public void Error(string code, TextSpan span, string message) => Add(code, DiagnosticSeverity.Error, span, message);
    public void Warning(string code, TextSpan span, string message) => Add(code, DiagnosticSeverity.Warning, span, message);

    public void Add(string code, DiagnosticSeverity severity, TextSpan span, string message)
    {
        // 钳制到文本范围，保证诊断位置总是合法
        int start = span.Start > _source.Length ? _source.Length : span.Start;
        int end = span.End > _source.Length ? _source.Length : span.End;
        var s = TextSpan.FromBounds(start, end);
        _items.Add(new MarkupDiagnostic(code, severity, message, s, _source.GetLinePosition(start), _source.FilePath));
    }
}
