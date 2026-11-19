using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using AwAUI.Markup.Compiler;
using AwAUI.Markup.Compiler.CodeGen;
using AwAUI.Markup.Compiler.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using MarkupSeverity = AwAUI.Markup.Compiler.DiagnosticSeverity;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using RoslynSourceText = Microsoft.CodeAnalysis.Text.SourceText;
using RoslynTextSpan = Microsoft.CodeAnalysis.Text.TextSpan;
using LinePosition = Microsoft.CodeAnalysis.Text.LinePosition;
using LinePositionSpan = Microsoft.CodeAnalysis.Text.LinePositionSpan;

namespace AwAUI.Markup.Generator;

/// <summary>
/// <c>.awaui</c> 源生成器（design/14、17）：
/// <list type="number">
/// <item><b>输入</b>：AdditionalFiles 中的 <c>*.awaui</c>；命名空间 = <c>RootNamespace</c> + 相对项目目录的文件夹（<c>@namespace</c> 可覆盖），类名 = 文件名。</item>
/// <item><b>声明阶段</b>：每个文件只依赖自身文本，解析出类骨架（指令 + <c>@code</c> 成员）、用到的组件标签与 <c>@bind</c> 目标。</item>
/// <item><b>解析阶段</b>：把全部骨架加入一份临时编译，基于它解析组件标签（手写 C# 组件、引用程序集中的组件、其他 <c>.awaui</c> 组件）的 Props 类别，
/// 并用 Roslyn 符号得出本组件的 Props（含代码后置文件与基类）、手写的胶水方法、哪些 <c>@bind</c> 目标是 <c>State&lt;T&gt;</c>。</item>
/// <item><b>生成阶段</b>：调用标记编译器，加入生成的 C#，把编译器诊断按 <c>.awaui</c> 的位置报告为 Roslyn 诊断（AW4001–AW4012）。</item>
/// </list>
/// 本生成器是 <c>.awaui</c> 组件 Props 胶水的唯一产出者：C# 胶水生成器（AwAUI.Generators）看不到本生成器的输出，
/// 代码后置文件（<c>X.awaui.cs</c>）不应再声明基类，否则那边也会生成胶水而重复定义（见 design/17）。
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class MarkupSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var settings = context.AnalyzerConfigOptionsProvider
            .Combine(context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? ""))
            .Select(static (pair, _) => ReadSettings(pair.Left.GlobalOptions, pair.Right));

        var inputs = context.AdditionalTextsProvider
            .Where(static t => t.Path.EndsWith(".awaui", StringComparison.OrdinalIgnoreCase))
            .Combine(settings)
            .Select(static (pair, ct) => FileInput.Create(pair.Left.Path, pair.Left.GetText(ct)?.ToString() ?? "", pair.Right));

        var declarations = inputs.Select(static (input, ct) => Declare(input, ct));

        var resolved = declarations.Collect()
            .Combine(context.CompilationProvider)
            .Combine(context.ParseOptionsProvider)
            .SelectMany(static (pair, ct) => ComponentResolver.Resolve(pair.Left.Left, pair.Left.Right, pair.Right as CSharpParseOptions, ct));

        context.RegisterSourceOutput(resolved, static (spc, file) => Produce(spc, file));
    }

    static ProjectSettings ReadSettings(AnalyzerConfigOptions options, string assemblyName)
    {
        options.TryGetValue("build_property.RootNamespace", out var root);
        options.TryGetValue("build_property.ProjectDir", out var dir);
        if (string.IsNullOrWhiteSpace(root)) root = assemblyName;
        return new ProjectSettings(SanitizeNamespace(root!.Trim()), string.IsNullOrWhiteSpace(dir) ? null : dir);
    }

    /// <summary>
    /// 根命名空间逐段转为合法标识符：SDK 的默认值只把项目名中的空格换成 <c>_</c>（<c>my-app</c> 仍是 <c>my-app</c>），
    /// 程序集名也可能含 <c>-</c>。空段被丢弃。
    /// </summary>
    internal static string SanitizeNamespace(string ns)
    {
        var sb = new StringBuilder(ns.Length);
        foreach (var part in ns.Split('.'))
        {
            string seg = FileInput.SanitizeIdentifier(part.Trim());
            if (seg.Length == 0) continue;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(seg);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ 声明阶段

    internal static FileDeclaration Declare(FileInput input, CancellationToken ct)
    {
        var analysis = MarkupCompiler.Analyze(input.Text, input.Path);
        ct.ThrowIfCancellationRequested();
        string className = Compiler.Analysis.MarkupValidator.ClassNameFromPath(input.Path);
        string ns = input.DefaultNamespace;
        string baseType = CSharpEmitter.DefaultBase;
        var usings = new List<string>();
        var usingLines = new List<string>();
        var interfaces = new List<string>();
        var typeParams = new List<string>();
        var constraints = new List<string>();
        var code = new StringBuilder();
        foreach (var item in analysis.NormalizedRoot.Items)
        {
            switch (item)
            {
                case DirectiveSyntax d:
                    switch (d.DirectiveKind)
                    {
                        case DirectiveKind.Namespace: ns = d.Value; break;
                        case DirectiveKind.Using:
                            usingLines.Add(d.Value);
                            if (d.Value.IndexOf('=') < 0 && !d.Value.StartsWith("static ", StringComparison.Ordinal)) usings.Add(d.Value.Trim());
                            break;
                        case DirectiveKind.Inherits: baseType = d.Value; break;
                        case DirectiveKind.Implements: interfaces.Add(d.Value); break;
                        case DirectiveKind.TypeParam:
                        {
                            int i = 0;
                            while (i < d.Value.Length && (char.IsLetterOrDigit(d.Value[i]) || d.Value[i] == '_')) i++;
                            typeParams.Add(d.Value.Substring(0, i));
                            string rest = d.Value.Substring(i).Trim();
                            if (rest.Length > 0) constraints.Add(rest);
                            break;
                        }
                    }
                    break;
                case CodeBlockSyntax c:
                    code.Append(c.Code).Append('\n');
                    break;
            }
        }
        usings.AddRange(CSharpEmitter.DefaultUsings);

        var sb = new StringBuilder();
        sb.Append("namespace ").Append(ns).Append("\n{\n");
        foreach (var u in CSharpEmitter.DefaultUsings) sb.Append("using global::").Append(u).Append(";\n");
        foreach (var u in usingLines) sb.Append("using ").Append(u).Append(";\n");
        sb.Append("public partial class ").Append(className);
        if (typeParams.Count > 0) sb.Append('<').Append(string.Join(", ", typeParams)).Append('>');
        sb.Append(" : ").Append(baseType);
        foreach (var i in interfaces) sb.Append(", ").Append(i);
        sb.Append('\n');
        foreach (var c in constraints) sb.Append(c).Append('\n');
        sb.Append("{\nprivate void ").Append(ComponentResolver.ProbeMethod).Append("() { }\n");
        sb.Append(code);
        sb.Append("\n}\n}\n");

        return new FileDeclaration(input, ns, className, typeParams.Count, sb.ToString(),
            new EquatableArray<string>(usings),
            new EquatableArray<string>(CSharpEmitter.CollectComponentTags(analysis.NormalizedRoot)),
            new EquatableArray<string>(CSharpEmitter.CollectBindTargets(analysis.NormalizedRoot)));
    }

    // ------------------------------------------------------------ 生成阶段

    static void Produce(SourceProductionContext spc, ResolvedFile file)
    {
        var compilation = Compile(file);
        var decl = file.Declaration;
        var source = compilation.Analysis.Tree.Source;
        bool invalidClassName = false;
        foreach (var d in compilation.Diagnostics)
        {
            spc.CancellationToken.ThrowIfCancellationRequested();
            spc.ReportDiagnostic(ToRoslyn(d, source, decl.Input.Path));
            if (d.Code == Compiler.DiagnosticCodes.InvalidClassName) invalidClassName = true;
        }
        // 文件名不是合法类名（AW4011）：生成的类声明无法解析，只会带来一串指向生成文件的语法错误，所以只报告 AW4011
        if (!invalidClassName)
            spc.AddSource(file.HintName, RoslynSourceText.From(compilation.GeneratedCode, Encoding.UTF8));
    }

    internal static MarkupCompilation Compile(ResolvedFile file)
    {
        var decl = file.Declaration;
        var options = new CSharpEmitOptions
        {
            Namespace = decl.Input.DefaultNamespace,
            ClassName = decl.ClassName,
            LineDirectivePath = decl.Input.Path,
            ComponentSourcePath = decl.Input.RelativePath,
            Catalog = new ResolvedCatalog(file.Tags),
            ClassInfo = file.ClassInfo,
            EmitNullableRestore = file.NullableDirective,
        };
        return MarkupCompiler.Compile(decl.Input.Text, decl.Input.Path, options);
    }

    internal static Diagnostic ToRoslyn(MarkupDiagnostic d, Compiler.SourceText source, string path)
    {
        int start = Math.Min(d.Span.Start, source.Length);
        int end = Math.Min(d.Span.End, source.Length);
        var s = source.GetLinePosition(start);
        var e = source.GetLinePosition(end);
        var location = Location.Create(path, new RoslynTextSpan(start, end - start),
            new LinePositionSpan(new LinePosition(s.Line - 1, s.Column - 1), new LinePosition(e.Line - 1, e.Column - 1)));
        var descriptor = MarkupDiagnosticDescriptors.For(d.Code);
        var severity = d.Severity == MarkupSeverity.Error ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
        return Diagnostic.Create(descriptor, location, severity, additionalLocations: null, properties: null, d.Message);
    }

    /// <summary>
    /// 生成文件的提示名：相对路径中的非法字符替换为 <c>_</c>，附加路径的哈希（区分只有大小写或非法字符不同的路径）；
    /// 相对路径相同的第 n 个文件（n ≥ 2）再附加 <c>_n</c>（见 <see cref="ComponentResolver"/>）。
    /// </summary>
    internal static string HintName(string relativePath, int duplicate = 1)
    {
        var sb = new StringBuilder(relativePath.Length + 16);
        foreach (char c in relativePath)
            sb.Append((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-' ? c : c == '/' ? '.' : '_');
        uint h = 2166136261;
        foreach (char c in relativePath)
        {
            h ^= c;
            h *= 16777619;
        }
        sb.Append('.').Append(h.ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
        if (duplicate > 1) sb.Append('_').Append(duplicate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return sb.Append(".g.cs").ToString();
    }
}

/// <summary>标记编译器诊断（AW4001–AW4012，design/17）对应的 Roslyn 描述符。</summary>
internal static class MarkupDiagnosticDescriptors
{
    const string Category = "AwAUI.Markup";

    static readonly DiagnosticDescriptor UnclosedTag = Create("AW4001", "标签未闭合或结束标签不匹配", DiagnosticSeverity.Error);
    static readonly DiagnosticDescriptor UnknownElement = Create("AW4002", "未知的内建元素", DiagnosticSeverity.Error);
    static readonly DiagnosticDescriptor UnknownAttribute = Create("AW4003", "内建元素上的未知属性", DiagnosticSeverity.Error);
    static readonly DiagnosticDescriptor InvalidAttributeValue = Create("AW4004", "属性值无法转换为目标类型", DiagnosticSeverity.Error);
    static readonly DiagnosticDescriptor DuplicateAttribute = Create("AW4005", "重复的属性", DiagnosticSeverity.Error);
    static readonly DiagnosticDescriptor UnbalancedDelimiter = Create("AW4006", "表达式或代码块定界符不配对", DiagnosticSeverity.Error);
    static readonly DiagnosticDescriptor InvalidDirective = Create("AW4007", "未知指令或指令位置错误", DiagnosticSeverity.Error);
    static readonly DiagnosticDescriptor MissingKeyInLoop = Create("AW4008", "循环内的元素或组件缺少 @key", DiagnosticSeverity.Warning);
    static readonly DiagnosticDescriptor InvalidBind = Create("AW4009", "@bind 目标不支持或与显式的值/事件属性冲突", DiagnosticSeverity.Error);
    static readonly DiagnosticDescriptor InvalidStyleBlock = Create("AW4010", "样式块问题", DiagnosticSeverity.Error);
    static readonly DiagnosticDescriptor InvalidClassName = Create("AW4011", "文件名不是合法的 C# 类名", DiagnosticSeverity.Error);
    static readonly DiagnosticDescriptor NotSupportedByRuntime = Create("AW4012", "运行时尚未支持", DiagnosticSeverity.Warning);

    static DiagnosticDescriptor Create(string id, string title, DiagnosticSeverity severity) =>
        new(id, title, "{0}", Category, severity, isEnabledByDefault: true);

    public static DiagnosticDescriptor For(string code) => code switch
    {
        "AW4001" => UnclosedTag,
        "AW4002" => UnknownElement,
        "AW4003" => UnknownAttribute,
        "AW4004" => InvalidAttributeValue,
        "AW4005" => DuplicateAttribute,
        "AW4006" => UnbalancedDelimiter,
        "AW4007" => InvalidDirective,
        "AW4008" => MissingKeyInLoop,
        "AW4009" => InvalidBind,
        "AW4010" => InvalidStyleBlock,
        "AW4011" => InvalidClassName,
        _ => NotSupportedByRuntime,
    };

    public static ImmutableArray<DiagnosticDescriptor> All => ImmutableArray.Create(
        UnclosedTag, UnknownElement, UnknownAttribute, InvalidAttributeValue, DuplicateAttribute, UnbalancedDelimiter,
        InvalidDirective, MissingKeyInLoop, InvalidBind, InvalidStyleBlock, InvalidClassName, NotSupportedByRuntime);
}
