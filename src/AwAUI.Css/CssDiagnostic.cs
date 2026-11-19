using System;
using System.Collections.Generic;

namespace AwAUI.Css;

/// <summary>诊断严重程度。</summary>
public enum CssDiagnosticSeverity
{
    /// <summary>提示：不影响结果。</summary>
    Info,
    /// <summary>警告：内容有效但不被支持或已按规范恢复，部分内容可能不生效。</summary>
    Warning,
    /// <summary>错误：语法错误导致内容被丢弃。</summary>
    Error,
}

/// <summary>
/// 一条带位置的 CSS 诊断。诊断即数据：构建期、运行时解析与 DevTools 共用同一结构（design/12、design/20）。
/// </summary>
public sealed class CssDiagnostic
{
    public CssDiagnostic(string code, CssDiagnosticSeverity severity, string message, SourceRange range)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Severity = severity;
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Range = range;
    }

    /// <summary>诊断代码，见 <see cref="CssDiagnosticCodes"/>。</summary>
    public string Code { get; }

    public CssDiagnosticSeverity Severity { get; }

    /// <summary>面向人与 AI 的说明，尽量包含修复建议。</summary>
    public string Message { get; }

    /// <summary>出问题的源区间。</summary>
    public SourceRange Range { get; }

    public override string ToString()
        => FormattableString.Invariant($"{Range.Start.Line}:{Range.Start.Column}: {Severity} {Code}: {Message}");
}

/// <summary>
/// CSS 相关诊断代码（AW2xxx）。AW2001–AW2003 见 design/12；其余为 CSS 引擎第一层新增，需同步到设计文档。
/// </summary>
public static class CssDiagnosticCodes
{
    /// <summary>使用了任何样式表中都未定义的 class（分析器使用）。</summary>
    public const string UndefinedClass = "AW2001";

    /// <summary>CSS 属性不被支持。</summary>
    public const string UnsupportedProperty = "AW2002";

    /// <summary>CSS 值无效。</summary>
    public const string InvalidValue = "AW2003";

    /// <summary>选择器使用了不支持或未知的伪类、伪元素或其他特性；所在规则按规范被丢弃。</summary>
    public const string UnsupportedSelector = "AW2004";

    /// <summary>不支持或未知的 at 规则（如 <c>@supports</c>、<c>@container</c>）；内容被保留但不生效。</summary>
    public const string UnsupportedAtRule = "AW2005";

    /// <summary>选择器语法错误；所在规则被丢弃。</summary>
    public const string InvalidSelector = "AW2006";

    /// <summary>媒体查询无效（整条查询按规范视为 <c>not all</c>）或使用了不支持的媒体特性。</summary>
    public const string InvalidMediaQuery = "AW2007";

    /// <summary>CSS 语法错误（分词或规则结构），按 CSS Syntax 3 的错误恢复处理。</summary>
    public const string SyntaxError = "AW2008";

    /// <summary>at 规则的序言或位置无效（如 <c>@import</c> 出现在其他规则之后）；该规则被丢弃。</summary>
    public const string InvalidAtRule = "AW2009";

    /// <summary>声明出现在不允许声明的位置（如顶层 <c>@media</c> 块内），或以此处不允许的形式出现（如关键帧中的 <c>!important</c>）；该声明被丢弃。</summary>
    public const string MisplacedDeclaration = "AW2010";

    /// <summary>规则出现在不允许嵌套规则的位置（如 <c>@font-face</c> 块内）；该规则被丢弃。</summary>
    public const string MisplacedRule = "AW2011";
}

/// <summary>诊断收集的内部辅助。</summary>
internal static class CssDiagnosticListExtensions
{
    public static void Report(this List<CssDiagnostic>? list, string code, CssDiagnosticSeverity severity, string message, SourceRange range)
        => list?.Add(new CssDiagnostic(code, severity, message, range));
}
