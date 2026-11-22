using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using AwAUI.Markup.Compiler;
using AwAUI.Markup.Compiler.Syntax;

namespace AwAUI.Markup.Generator;

/// <summary>
/// 目录级的 <c>_Imports.awaui</c>（相当于 Blazor 的 <c>_Imports.razor</c>）：文件里的 <c>@using</c> 作用于同目录及其子目录的全部
/// <c>.awaui</c>，由外向内累积；文件自身的 <c>@using</c> 仍然排在最前，所以可以覆盖导入。导入的命名空间与显式 <c>@using</c> 一样参与
/// 标签解析并写入生成的 C#，因此在 <c>@code</c> 里也能直接写这些命名空间中的类型。
/// </summary>
internal sealed class ImportFile : IEquatable<ImportFile>
{
    public ImportFile(string directory, EquatableArray<string> usings)
    {
        Directory = directory;
        Usings = usings;
    }

    /// <summary>规范化后的目录（分隔符为 <c>/</c>，没有尾随分隔符）。</summary>
    public string Directory { get; }

    public EquatableArray<string> Usings { get; }

    public bool Equals(ImportFile? other) =>
        other is not null && Directory == other.Directory && Usings.Equals(other.Usings);

    public override bool Equals(object? obj) => Equals(obj as ImportFile);

    public override int GetHashCode() => Directory.GetHashCode() * 31 + Usings.GetHashCode();
}

/// <summary><c>_Imports.awaui</c> 的识别、解析与按目录累积。</summary>
internal static class GlobalImports
{
    public const string FileName = "_Imports.awaui";

    public static bool IsImportsFile(string path) =>
        FileNameFrom(path).Equals(FileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>解析一个 <c>_Imports.awaui</c>：只取可导入命名空间的 <c>@using</c>（忽略别名与 <c>static</c>）。</summary>
    public static ImportFile Create(string path, string text)
    {
        var usings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // 复用标记编译器的指令解析：与文件内的 @using 采用同一套语法与空白规则
        var analysis = MarkupCompiler.Analyze(text, path);
        foreach (var item in analysis.NormalizedRoot.Items)
        {
            if (item is not DirectiveSyntax { DirectiveKind: DirectiveKind.Using } d) continue;
            if (d.Value.IndexOf('=') >= 0 || d.Value.StartsWith("static ", StringComparison.Ordinal)) continue;
            if (seen.Add(d.Value.Trim())) usings.Add(d.Value.Trim());
        }
        return new ImportFile(DirectoryOf(path), new EquatableArray<string>(usings));
    }

    /// <summary>
    /// 一个文件可见的导入：同目录与所有祖先目录的 <c>_Imports.awaui</c>，由外向内（内层更靠前，因而优先），去重后返回。
    /// </summary>
    public static EquatableArray<string> Collect(string path, ImmutableArray<ImportFile> imports)
    {
        if (imports.IsDefaultOrEmpty) return EquatableArray<string>.Empty;
        string dir = DirectoryOf(path);
        var matched = new List<ImportFile>();
        foreach (var import in imports)
            if (IsAncestorOrSelf(import.Directory, dir)) matched.Add(import);
        if (matched.Count == 0) return EquatableArray<string>.Empty;
        // 由内向外：更近的 _Imports.awaui 排在前面
        matched.Sort(static (a, b) => b.Directory.Length.CompareTo(a.Directory.Length));
        var usings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var import in matched)
            foreach (var u in import.Usings)
                if (seen.Add(u)) usings.Add(u);
        return new EquatableArray<string>(usings);
    }

    /// <summary>规范化：<c>\</c> → <c>/</c>，去掉文件名与尾随分隔符。</summary>
    static string DirectoryOf(string path)
    {
        string normalized = path.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        return slash <= 0 ? "" : normalized.Substring(0, slash);
    }

    static string FileNameFrom(string path)
    {
        string normalized = path.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        return slash < 0 ? normalized : normalized.Substring(slash + 1);
    }

    /// <summary><paramref name="directory"/> 是 <paramref name="candidate"/> 本身或它的祖先。</summary>
    static bool IsAncestorOrSelf(string directory, string candidate)
    {
        if (directory.Length == 0) return true;
        if (candidate.Equals(directory, StringComparison.OrdinalIgnoreCase)) return true;
        return candidate.Length > directory.Length
            && candidate[directory.Length] == '/'
            && candidate.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
    }
}
