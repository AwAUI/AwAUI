using System;

namespace AwAUI.Markup.Compiler;

/// <summary>诊断的严重程度。</summary>
public enum DiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>标记编译器的诊断代码（design/17「诊断」）。</summary>
public static class DiagnosticCodes
{
    /// <summary>标签未闭合 / 结束标签不匹配。</summary>
    public const string UnclosedTag = "AW4001";
    /// <summary>未知的内建元素。</summary>
    public const string UnknownElement = "AW4002";
    /// <summary>内建元素上的未知属性。</summary>
    public const string UnknownAttribute = "AW4003";
    /// <summary>属性值无法转换为目标类型。</summary>
    public const string InvalidAttributeValue = "AW4004";
    /// <summary>重复的属性。</summary>
    public const string DuplicateAttribute = "AW4005";
    /// <summary>表达式或代码块定界符不配对。</summary>
    public const string UnbalancedDelimiter = "AW4006";
    /// <summary>未知指令或指令位置错误。</summary>
    public const string InvalidDirective = "AW4007";
    /// <summary>循环内的元素或组件缺少 @key（警告）。</summary>
    public const string MissingKeyInLoop = "AW4008";
    /// <summary>@bind 目标不支持或与显式的值/事件属性冲突。</summary>
    public const string InvalidBind = "AW4009";
    /// <summary>样式块问题。</summary>
    public const string InvalidStyleBlock = "AW4010";
    /// <summary>文件名不是合法的 C# 类名。</summary>
    public const string InvalidClassName = "AW4011";
    /// <summary>
    /// 语言允许、但目标运行时（AwAUI.UI）尚未实现的内建元素、事件（错误，不生成代码）或属性行为（警告，透传为自定义属性）。
    /// 由代码生成阶段报告（<see cref="MarkupCompiler.Compile(MarkupAnalysis, CodeGen.CSharpEmitOptions?)"/>）。
    /// </summary>
    public const string NotSupportedByRuntime = "AW4012";
}

/// <summary>一条带位置的诊断。</summary>
public sealed class MarkupDiagnostic
{
    public MarkupDiagnostic(string code, DiagnosticSeverity severity, string message, TextSpan span, LinePosition start, string? filePath)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Severity = severity;
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Span = span;
        Start = start;
        FilePath = filePath;
    }

    public string Code { get; }
    public DiagnosticSeverity Severity { get; }
    public string Message { get; }
    public TextSpan Span { get; }
    /// <summary>起点的行列（从 1 开始）。</summary>
    public LinePosition Start { get; }
    public string? FilePath { get; }

    public bool IsError => Severity == DiagnosticSeverity.Error;

    /// <summary>MSBuild 风格：<c>file(line,col): error AW4001: message</c>。</summary>
    public override string ToString() =>
        $"{FilePath ?? "<input>"}({Start.Line},{Start.Column}): {(IsError ? "error" : "warning")} {Code}: {Message}";
}
