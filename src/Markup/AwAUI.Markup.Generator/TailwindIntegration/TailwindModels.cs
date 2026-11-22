using System;
using System.Collections.Generic;

namespace AwAUI.Markup.Generator.TailwindIntegration;

/// <summary>Tailwind 接入的项目级设置（MSBuild 属性，见 buildTransitive/AwAUI.Markup.props）。</summary>
internal sealed class TailwindSettings : IEquatable<TailwindSettings>
{
    public TailwindSettings(string assemblyName, string rootNamespace, string? projectDir, string themeRoot, bool register, bool autoSource, bool minify, bool originalPreflight, bool runtime)
    {
        AssemblyName = assemblyName;
        RootNamespace = rootNamespace;
        ProjectDir = projectDir;
        ThemeRoot = themeRoot;
        Register = register;
        AutoSource = autoSource;
        Minify = minify;
        OriginalPreflight = originalPreflight;
        Runtime = runtime;
    }

    /// <summary>程序集名（写进来源名，区分不同程序集中同名的入口样式表）。</summary>
    public string AssemblyName { get; }

    public string RootNamespace { get; }

    /// <summary>项目目录（规范化，不以 <c>/</c> 结尾）；未知时为 null。</summary>
    public string? ProjectDir { get; }

    public string ThemeRoot { get; }
    public bool Register { get; }
    public bool AutoSource { get; }
    public bool Minify { get; }
    public bool OriginalPreflight { get; }

    /// <summary><c>AwAUITailwindRuntime</c>：额外生成运行时补全所需的入口 CSS 与 <c>CreateRuntime()</c>。默认 false。</summary>
    public bool Runtime { get; }

    public bool Equals(TailwindSettings? other) =>
        other is not null && AssemblyName == other.AssemblyName && RootNamespace == other.RootNamespace && ProjectDir == other.ProjectDir && ThemeRoot == other.ThemeRoot
        && Register == other.Register && AutoSource == other.AutoSource && Minify == other.Minify && OriginalPreflight == other.OriginalPreflight
        && Runtime == other.Runtime;

    public override bool Equals(object? obj) => Equals(obj as TailwindSettings);

    public override int GetHashCode() => RootNamespace.GetHashCode() * 31 + ThemeRoot.GetHashCode();
}

/// <summary>带 Tailwind 角色的附加文件（入口样式表、被导入的样式表、额外扫描源；其他 <c>.css</c> 附加文件的角色为空串）。</summary>
internal sealed class TailwindFile : IEquatable<TailwindFile>
{
    public const string Stylesheet = "Stylesheet";
    public const string Import = "Import";
    public const string Source = "Source";

    public TailwindFile(string path, string role, string? className, string text)
    {
        Path = path;
        NormalizedPath = SourcePaths.Normalize(path);
        Role = role;
        ClassName = className;
        Text = text;
    }

    public string Path { get; }
    public string NormalizedPath { get; }
    public string Role { get; }

    /// <summary>入口样式表的 <c>ClassName</c> 元数据。</summary>
    public string? ClassName { get; }

    public string Text { get; }

    public bool Equals(TailwindFile? other) =>
        other is not null && Path == other.Path && Role == other.Role && ClassName == other.ClassName && Text == other.Text;

    public override bool Equals(object? obj) => Equals(obj as TailwindFile);

    public override int GetHashCode() => Path.GetHashCode() * 31 + Text.Length;
}

/// <summary>生成阶段需要的编译信息（由 Roslyn 编译推出，按值缓存）。</summary>
internal sealed class TailwindCompilationInfo : IEquatable<TailwindCompilationInfo>
{
    public TailwindCompilationInfo(bool hasAppStyleSheets, bool moduleInitializerAvailable, bool languageSupportsModuleInitializer)
    {
        HasAppStyleSheets = hasAppStyleSheets;
        ModuleInitializerAvailable = moduleInitializerAvailable;
        LanguageSupportsModuleInitializer = languageSupportsModuleInitializer;
    }

    /// <summary>编译引用了 AwAUI.UI（有 <c>AwAUI.UI.AppStyleSheets</c>）。</summary>
    public bool HasAppStyleSheets { get; }

    /// <summary><c>System.Runtime.CompilerServices.ModuleInitializerAttribute</c> 可用（否则需要生成内部的同名特性）。</summary>
    public bool ModuleInitializerAvailable { get; }

    /// <summary>C# 9 及以上。</summary>
    public bool LanguageSupportsModuleInitializer { get; }

    public bool Equals(TailwindCompilationInfo? other) =>
        other is not null && HasAppStyleSheets == other.HasAppStyleSheets && ModuleInitializerAvailable == other.ModuleInitializerAvailable
        && LanguageSupportsModuleInitializer == other.LanguageSupportsModuleInitializer;

    public override bool Equals(object? obj) => Equals(obj as TailwindCompilationInfo);

    public override int GetHashCode() => (HasAppStyleSheets ? 1 : 0) | (ModuleInitializerAvailable ? 2 : 0) | (LanguageSupportsModuleInitializer ? 4 : 0);
}

/// <summary>一个入口样式表及其编译所需的全部输入（导入池、设置、生成的类名）。</summary>
internal sealed class TailwindEntry : IEquatable<TailwindEntry>
{
    public TailwindEntry(TailwindFile file, EquatableArray<TailwindFile> pool, TailwindSettings settings, string className, string relativePath)
    {
        File = file;
        Pool = pool;
        Settings = settings;
        ClassName = className;
        RelativePath = relativePath;
    }

    public TailwindFile File { get; }

    /// <summary>可被 <c>@import</c> 的样式表（全部入口、导入项与 <c>.css</c> 附加文件）。</summary>
    public EquatableArray<TailwindFile> Pool { get; }

    public TailwindSettings Settings { get; }
    public string ClassName { get; }

    /// <summary>相对项目目录的路径（<c>/</c> 分隔），用于来源名与提示名。</summary>
    public string RelativePath { get; }

    public string Directory => SourcePaths.DirectoryOf(File.Path);

    public bool Equals(TailwindEntry? other) =>
        other is not null && File.Equals(other.File) && Pool.Equals(other.Pool) && Settings.Equals(other.Settings)
        && ClassName == other.ClassName && RelativePath == other.RelativePath;

    public override bool Equals(object? obj) => Equals(obj as TailwindEntry);

    public override int GetHashCode() => File.GetHashCode();
}

/// <summary><c>@source</c> 声明（模式已接到所在样式表的目录上并规范化）。</summary>
internal sealed class SourcePattern : IEquatable<SourcePattern>
{
    public SourcePattern(string pattern, bool negated)
    {
        Pattern = pattern;
        Negated = negated;
    }

    public string Pattern { get; }
    public bool Negated { get; }

    public bool Equals(SourcePattern? other) => other is not null && Pattern == other.Pattern && Negated == other.Negated;

    public override bool Equals(object? obj) => Equals(obj as SourcePattern);

    public override int GetHashCode() => Pattern.GetHashCode() ^ (Negated ? 1 : 0);
}

/// <summary>解析过的入口：扫描范围（<c>@source</c>、自动检测的根）或入口本身的错误。</summary>
internal sealed class ParsedEntry : IEquatable<ParsedEntry>
{
    public ParsedEntry(TailwindEntry entry, EquatableArray<SourcePattern> sources, string? autoRoot, string? error, AwAUI.Tailwind.TailwindCompiler? validator)
    {
        Entry = entry;
        Sources = sources;
        AutoRoot = autoRoot;
        Error = error;
        Validator = validator;
    }

    public TailwindEntry Entry { get; }
    public EquatableArray<SourcePattern> Sources { get; }

    /// <summary>自动检测的根目录（规范化）：<c>*.awaui</c> 与 C# 文件在其下时扫描；空串表示不限目录；null 表示关闭自动检测（<c>source(none)</c> 或 AwAUITailwindAutoSource=false）。</summary>
    public string? AutoRoot { get; }

    /// <summary>入口样式表的错误（AW5001）；没有时为 null。</summary>
    public string? Error { get; }

    /// <summary>
    /// 只用于判断候选是否有效的编译器（不调用 <c>Build</c>，不累积候选；其解析缓存在入口不变期间一直复用）。
    /// 不参与相等比较：它由 <see cref="Entry"/> 决定。
    /// </summary>
    public AwAUI.Tailwind.TailwindCompiler? Validator { get; }

    public bool Equals(ParsedEntry? other) =>
        other is not null && Entry.Equals(other.Entry) && Sources.Equals(other.Sources) && AutoRoot == other.AutoRoot && Error == other.Error;

    public override bool Equals(object? obj) => Equals(obj as ParsedEntry);

    public override int GetHashCode() => Entry.GetHashCode();
}

/// <summary>扫描到的文件种类。</summary>
internal enum ScanKind
{
    Markup,
    CSharp,
    Explicit,
}

/// <summary><c>class="..."</c> 属性中的一个字面量类名及其位置（AW5002 用）。</summary>
internal readonly struct ClassToken : IEquatable<ClassToken>
{
    public ClassToken(string value, int start, int length, int startLine, int startColumn, int endLine, int endColumn)
    {
        Value = value;
        Start = start;
        Length = length;
        StartLine = startLine;
        StartColumn = startColumn;
        EndLine = endLine;
        EndColumn = endColumn;
    }

    public string Value { get; }
    public int Start { get; }
    public int Length { get; }

    /// <summary>从 0 开始的行列。</summary>
    public int StartLine { get; }
    public int StartColumn { get; }
    public int EndLine { get; }
    public int EndColumn { get; }

    public bool Equals(ClassToken other) =>
        Value == other.Value && Start == other.Start && Length == other.Length && StartLine == other.StartLine
        && StartColumn == other.StartColumn && EndLine == other.EndLine && EndColumn == other.EndColumn;

    public override bool Equals(object? obj) => obj is ClassToken t && Equals(t);

    public override int GetHashCode() => Value.GetHashCode() * 31 + Start;
}

/// <summary>一个文件的扫描结果：候选类名（排序、去重）与（仅 <c>.awaui</c>）class 属性中的字面量类名。</summary>
internal sealed class FileScan : IEquatable<FileScan>
{
    public FileScan(string path, ScanKind kind, EquatableArray<string> candidates, EquatableArray<ClassToken> classTokens, EquatableArray<string> globalClasses)
    {
        GlobalClasses = globalClasses;
        Path = path;
        NormalizedPath = SourcePaths.Normalize(path);
        Kind = kind;
        Candidates = candidates;
        ClassTokens = classTokens;
    }

    public string Path { get; }
    public string NormalizedPath { get; }
    public ScanKind Kind { get; }
    public EquatableArray<string> Candidates { get; }
    public EquatableArray<ClassToken> ClassTokens { get; }

    /// <summary>（仅 <c>.awaui</c>）<c>&lt;style global&gt;</c> 中以类选择器出现的名字（排序、去重）：它们在所有组件中生效，AW5002 不报告。</summary>
    public EquatableArray<string> GlobalClasses { get; }

    public bool Equals(FileScan? other) =>
        other is not null && Path == other.Path && Kind == other.Kind && Candidates.Equals(other.Candidates) && ClassTokens.Equals(other.ClassTokens)
        && GlobalClasses.Equals(other.GlobalClasses);

    public override bool Equals(object? obj) => Equals(obj as FileScan);

    public override int GetHashCode() => Path.GetHashCode() * 31 + Candidates.Count;
}

/// <summary>
/// 一个入口在扫描范围内的有效候选（排序、去重）与无法生成样式的 class 字面量。只含字符串：改动文本、位置或加入无效的词都不会让
/// <see cref="Candidates"/> 变化，编译步骤因此命中缓存。
/// </summary>
internal sealed class EntryCandidates : IEquatable<EntryCandidates>
{
    public EntryCandidates(ParsedEntry parsed, EquatableArray<string> candidates, EquatableArray<string> invalidClassTokens, EquatableArray<string> scopedMarkupPaths)
    {
        Parsed = parsed;
        Candidates = candidates;
        InvalidClassTokens = invalidClassTokens;
        ScopedMarkupPaths = scopedMarkupPaths;
    }

    public ParsedEntry Parsed { get; }
    public EquatableArray<string> Candidates { get; }

    /// <summary>像工具类、但无法生成样式的 class 字面量（AW5002）。</summary>
    public EquatableArray<string> InvalidClassTokens { get; }

    /// <summary>在本入口扫描范围内、有 class 字面量的 <c>.awaui</c> 文件路径（按序数排序）：AW5002 只报告在范围内的文件。</summary>
    public EquatableArray<string> ScopedMarkupPaths { get; }

    public bool Equals(EntryCandidates? other) =>
        other is not null && Parsed.Equals(other.Parsed) && Candidates.Equals(other.Candidates) && InvalidClassTokens.Equals(other.InvalidClassTokens)
        && ScopedMarkupPaths.Equals(other.ScopedMarkupPaths);

    public override bool Equals(object? obj) => Equals(obj as EntryCandidates);

    public override int GetHashCode() => Parsed.GetHashCode() * 31 + Candidates.Count;
}

/// <summary>编译步骤的输入：入口与有效候选（不含诊断用的数据）。</summary>
internal sealed class EntryBuildInput : IEquatable<EntryBuildInput>
{
    public EntryBuildInput(ParsedEntry parsed, EquatableArray<string> candidates)
    {
        Parsed = parsed;
        Candidates = candidates;
    }

    public ParsedEntry Parsed { get; }
    public EquatableArray<string> Candidates { get; }

    public bool Equals(EntryBuildInput? other) => other is not null && Parsed.Equals(other.Parsed) && Candidates.Equals(other.Candidates);

    public override bool Equals(object? obj) => Equals(obj as EntryBuildInput);

    public override int GetHashCode() => Parsed.GetHashCode() * 31 + Candidates.Count;
}

/// <summary>一个入口的编译结果：CSS 与生成样式的候选，或错误。</summary>
internal sealed class EntryResult : IEquatable<EntryResult>
{
    public EntryResult(TailwindEntry entry, string? css, EquatableArray<string> candidates, string? error)
    {
        Entry = entry;
        Css = css;
        Candidates = candidates;
        Error = error;
    }

    public TailwindEntry Entry { get; }

    /// <summary>生成的样式表；入口有错误时为 null。</summary>
    public string? Css { get; }

    public EquatableArray<string> Candidates { get; }
    public string? Error { get; }

    public bool Equals(EntryResult? other) =>
        other is not null && Entry.Equals(other.Entry) && Css == other.Css && Candidates.Equals(other.Candidates) && Error == other.Error;

    public override bool Equals(object? obj) => Equals(obj as EntryResult);

    public override int GetHashCode() => Entry.GetHashCode() * 31 + (Css?.Length ?? 0);
}

/// <summary>一条 AW5002 诊断。</summary>
internal sealed class ClassDiagnostic : IEquatable<ClassDiagnostic>
{
    public ClassDiagnostic(string path, ClassToken token, string stylesheets)
    {
        Path = path;
        Token = token;
        Stylesheets = stylesheets;
    }

    public string Path { get; }
    public ClassToken Token { get; }

    /// <summary>入口样式表的相对路径（逗号分隔，写进消息）。</summary>
    public string Stylesheets { get; }

    public bool Equals(ClassDiagnostic? other) => other is not null && Path == other.Path && Token.Equals(other.Token) && Stylesheets == other.Stylesheets;

    public override bool Equals(object? obj) => Equals(obj as ClassDiagnostic);

    public override int GetHashCode() => Path.GetHashCode() * 31 + Token.GetHashCode();
}

/// <summary>按序数排序并去重。</summary>
internal static class SortedStrings
{
    public static EquatableArray<string> Of(IEnumerable<string> items)
    {
        var set = new HashSet<string>(items, StringComparer.Ordinal);
        var arr = new string[set.Count];
        set.CopyTo(arr);
        Array.Sort(arr, StringComparer.Ordinal);
        return new EquatableArray<string>(arr);
    }
}
