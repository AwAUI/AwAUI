using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using AwAUI.Markup.Compiler.Analysis;
using AwAUI.Markup.Compiler.CodeGen;

namespace AwAUI.Markup.Generator;

/// <summary>按元素值比较的只读数组（增量管线的缓存键需要值相等）。</summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T> where T : IEquatable<T>
{
    readonly T[]? _items;

    public EquatableArray(T[] items) => _items = items;

    public EquatableArray(IEnumerable<T> items) => _items = new List<T>(items).ToArray();

    public static EquatableArray<T> Empty => new(Array.Empty<T>());

    public int Count => _items?.Length ?? 0;

    public T this[int index] => _items![index];

    public bool Equals(EquatableArray<T> other)
    {
        int n = Count;
        if (n != other.Count) return false;
        for (int i = 0; i < n; i++)
            if (!_items![i].Equals(other._items![i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> o && Equals(o);

    public override int GetHashCode()
    {
        int h = Count;
        if (_items is null) return h;
        foreach (var item in _items) h = h * 31 + (item?.GetHashCode() ?? 0);
        return h;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>项目级设置（MSBuild 属性）。</summary>
internal sealed class ProjectSettings : IEquatable<ProjectSettings>
{
    public ProjectSettings(string rootNamespace, string? projectDir, EquatableArray<string> tagNamespaces = default,
        EquatableArray<string>? builtInNamespaces = null)
    {
        RootNamespace = rootNamespace;
        ProjectDir = projectDir;
        TagNamespaces = tagNamespaces;
        BuiltInNamespaces = builtInNamespaces;
    }

    public string RootNamespace { get; }

    /// <summary>项目目录（以分隔符结尾）；未知时为 null。</summary>
    public string? ProjectDir { get; }

    /// <summary>
    /// 额外的标签命名空间（<c>AwAUIMarkupNamespaces</c>，分号或逗号分隔）：与框架内置命名空间一起在
    /// <c>_Imports.awaui</c> 与项目级 <c>global using</c> 之后解析，用于项目自己的组件库。
    /// </summary>
    public EquatableArray<string> TagNamespaces { get; }

    /// <summary>
    /// 替换框架内置标签命名空间（<c>AwAUIMarkupBuiltInNamespaces</c>）；null 表示沿用默认的
    /// <see cref="CSharpEmitter.SystemNamespaces"/>，空列表表示禁用内置查找。
    /// </summary>
    public EquatableArray<string>? BuiltInNamespaces { get; }

    public bool Equals(ProjectSettings? other) => other is not null && RootNamespace == other.RootNamespace
        && ProjectDir == other.ProjectDir && TagNamespaces.Equals(other.TagNamespaces)
        && Nullable.Equals(BuiltInNamespaces, other.BuiltInNamespaces);

    public override bool Equals(object? obj) => Equals(obj as ProjectSettings);

    public override int GetHashCode() => RootNamespace.GetHashCode() * 31 + (ProjectDir?.GetHashCode() ?? 0)
        + TagNamespaces.GetHashCode() + (BuiltInNamespaces?.GetHashCode() ?? 0);
}

/// <summary>一个 <c>.awaui</c> 文件的输入：路径、文本与按目录推出的默认命名空间。</summary>
internal sealed class FileInput : IEquatable<FileInput>
{
    public FileInput(string path, string text, string relativePath, string defaultNamespace,
        EquatableArray<string> globalUsings = default, EquatableArray<string> tagNamespaces = default,
        EquatableArray<string>? builtInNamespaces = null, string? scopedCss = null, string? globalCss = null)
    {
        Path = path;
        Text = text;
        RelativePath = relativePath;
        DefaultNamespace = defaultNamespace;
        GlobalUsings = globalUsings;
        TagNamespaces = tagNamespaces;
        BuiltInNamespaces = builtInNamespaces ?? new EquatableArray<string>(CSharpEmitter.SystemNamespaces);
        ScopedCss = scopedCss;
        GlobalCss = globalCss;
    }

    /// <summary>完整路径（写入 <c>#line</c>，诊断位置用它）。</summary>
    public string Path { get; }

    public string Text { get; }

    /// <summary>相对项目目录的路径，分隔符为 <c>/</c>（写入 <c>[ComponentSource]</c>）。</summary>
    public string RelativePath { get; }

    /// <summary>项目根命名空间 + 目录（没有 <c>@namespace</c> 时使用）。</summary>
    public string DefaultNamespace { get; }

    /// <summary>
    /// 目录级 <c>_Imports.awaui</c> 提供的 <c>@using</c>（由外向内累积）；写在文件自身的 <c>@using</c> 之后，
    /// 因此文件里的显式 using 优先。相当于 Blazor 的 <c>_Imports.razor</c>。
    /// </summary>
    public EquatableArray<string> GlobalUsings { get; }

    /// <summary>项目级额外的标签命名空间（<c>AwAUIMarkupNamespaces</c>）；排在框架内置命名空间之前。</summary>
    public EquatableArray<string> TagNamespaces { get; }

    /// <summary>框架内置标签命名空间（未配置时为 <see cref="CSharpEmitter.SystemNamespaces"/>；可被项目替换或禁用）。</summary>
    public EquatableArray<string> BuiltInNamespaces { get; }

    /// <summary>同名的 <c>X.awaui.css</c> 的内容（组件唯一的作用域样式）；没有文件时为 null。</summary>
    public string? ScopedCss { get; }

    /// <summary>组件旁的 <c>X.awaui.global.css</c> 的内容（随组件注册的全局样式）；没有文件时为 null。</summary>
    public string? GlobalCss { get; }

    public bool Equals(FileInput? other) =>
        other is not null && Path == other.Path && Text == other.Text && RelativePath == other.RelativePath
        && DefaultNamespace == other.DefaultNamespace && GlobalUsings.Equals(other.GlobalUsings)
        && TagNamespaces.Equals(other.TagNamespaces) && BuiltInNamespaces.Equals(other.BuiltInNamespaces)
        && ScopedCss == other.ScopedCss && GlobalCss == other.GlobalCss;

    public override bool Equals(object? obj) => Equals(obj as FileInput);

    public override int GetHashCode() => Path.GetHashCode() * 31 + Text.Length
        + (ScopedCss?.Length ?? 0) * 17 + (GlobalCss?.Length ?? 0) * 13;

    public static FileInput Create(string path, string text, ProjectSettings settings,
        EquatableArray<string> globalUsings = default, string? scopedCss = null, string? globalCss = null)
    {
        string normalized = path.Replace('\\', '/');
        // 项目目录之外的文件（链接文件）只取文件名；两种分隔符都认（在类 Unix 系统上 Path.GetFileName 不把 \ 当分隔符）
        string relative = normalized.Substring(normalized.LastIndexOf('/') + 1);
        if (settings.ProjectDir is { Length: > 0 } dir)
        {
            string d = dir.Replace('\\', '/');
            if (!d.EndsWith("/", StringComparison.Ordinal)) d += "/";
            if (normalized.StartsWith(d, StringComparison.OrdinalIgnoreCase)) relative = normalized.Substring(d.Length);
        }
        var ns = new StringBuilder(settings.RootNamespace);
        string[] segments = relative.Split('/');
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string seg = SanitizeIdentifier(segments[i]);
            if (seg.Length == 0) continue;
            if (ns.Length > 0) ns.Append('.');
            ns.Append(seg);
        }
        return new FileInput(path, text, relative, ns.Length > 0 ? ns.ToString() : "AwAUI.Generated", globalUsings,
            settings.TagNamespaces, settings.BuiltInNamespaces, scopedCss, globalCss);
    }

    /// <summary>目录名 → 命名空间段：非法字符替换为 <c>_</c>，数字开头加 <c>_</c>（与 Razor 相同）。</summary>
    internal static string SanitizeIdentifier(string s)
    {
        var sb = new StringBuilder(s.Length + 1);
        foreach (char c in s)
            sb.Append(c == '_' || char.IsLetterOrDigit(c) ? c : '_');
        if (sb.Length > 0 && char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }
}

/// <summary>声明阶段（design/17）：一个文件的类骨架与需要语义解析的内容。只依赖文件文本，按值缓存。</summary>
internal sealed class FileDeclaration : IEquatable<FileDeclaration>
{
    public FileDeclaration(FileInput input, string ns, string className, int arity, string skeleton,
        EquatableArray<string> usings, int explicitUsingCount, EquatableArray<string> componentTags,
        EquatableArray<string> bindTargets)
    {
        Input = input;
        Namespace = ns;
        ClassName = className;
        Arity = arity;
        Skeleton = skeleton;
        Usings = usings;
        ExplicitUsingCount = explicitUsingCount;
        ComponentTags = componentTags;
        BindTargets = bindTargets;
    }

    public FileInput Input { get; }

    /// <summary>实际命名空间（<c>@namespace</c> 优先）。</summary>
    public string Namespace { get; }

    public string ClassName { get; }

    /// <summary><c>@typeparam</c> 的个数。</summary>
    public int Arity { get; }

    /// <summary>只含指令与 <c>@code</c> 成员的类骨架（C#），加入临时编译以便其他文件解析本组件的 Props。</summary>
    public string Skeleton { get; }

    /// <summary>解析标签时的 using（文件的 @using 与 _Imports.awaui 在前，默认导入在后）。</summary>
    public EquatableArray<string> Usings { get; }

    /// <summary><see cref="Usings"/> 中属于用户的前缀长度（文件的 @using + <c>_Imports.awaui</c>）：框架内置命名空间插在它之后。</summary>
    public int ExplicitUsingCount { get; }

    public EquatableArray<string> ComponentTags { get; }

    public EquatableArray<string> BindTargets { get; }

    /// <summary>类的元数据名（命名空间 + 类名 + 泛型元数）。</summary>
    public string MetadataName => (Namespace.Length > 0 ? Namespace + "." : "") + ClassName + (Arity > 0 ? "`" + Arity : "");

    public bool Equals(FileDeclaration? other) =>
        other is not null && Input.Equals(other.Input) && Namespace == other.Namespace && ClassName == other.ClassName
        && Arity == other.Arity && Skeleton == other.Skeleton && Usings.Equals(other.Usings)
        && ExplicitUsingCount == other.ExplicitUsingCount
        && ComponentTags.Equals(other.ComponentTags) && BindTargets.Equals(other.BindTargets);

    public override bool Equals(object? obj) => Equals(obj as FileDeclaration);

    public override int GetHashCode() => Input.GetHashCode();
}

/// <summary>标签 → 组件信息（解析结果；null 表示未找到）。</summary>
internal sealed class ResolvedTag : IEquatable<ResolvedTag>
{
    public ResolvedTag(string tag, ComponentInfo? info)
    {
        Tag = tag;
        Info = info;
    }

    public string Tag { get; }
    public ComponentInfo? Info { get; }

    public bool Equals(ResolvedTag? other) => other is not null && Tag == other.Tag && Equals(Info, other.Info);

    public override bool Equals(object? obj) => Equals(obj as ResolvedTag);

    public override int GetHashCode() => Tag.GetHashCode();
}

/// <summary>生成阶段的输入：声明 + 语义解析的结果。按值缓存，没有变化的文件不会重新生成。</summary>
internal sealed class ResolvedFile : IEquatable<ResolvedFile>
{
    public ResolvedFile(FileDeclaration declaration, EquatableArray<ResolvedTag> tags, ComponentClassInfo? classInfo, bool nullableDirective, string hintName)
    {
        Declaration = declaration;
        Tags = tags;
        ClassInfo = classInfo;
        NullableDirective = nullableDirective;
        HintName = hintName;
    }

    public FileDeclaration Declaration { get; }

    /// <summary>生成文件的提示名（在本次生成中唯一）。</summary>
    public string HintName { get; }
    public EquatableArray<ResolvedTag> Tags { get; }
    public ComponentClassInfo? ClassInfo { get; }

    /// <summary>项目语言版本 ≥ C# 8：生成 <c>#nullable restore</c>。</summary>
    public bool NullableDirective { get; }

    public bool Equals(ResolvedFile? other) =>
        other is not null && Declaration.Equals(other.Declaration) && Tags.Equals(other.Tags)
        && Equals(ClassInfo, other.ClassInfo) && NullableDirective == other.NullableDirective && HintName == other.HintName;

    public override bool Equals(object? obj) => Equals(obj as ResolvedFile);

    public override int GetHashCode() => Declaration.GetHashCode();
}

/// <summary>预先解析好的组件目录：代码生成阶段不再接触 Roslyn。</summary>
internal sealed class ResolvedCatalog : ComponentCatalog
{
    readonly Dictionary<string, ComponentInfo?> _tags = new(StringComparer.Ordinal);

    public ResolvedCatalog(IEnumerable<ResolvedTag> tags)
    {
        foreach (var t in tags) _tags[t.Tag] = t.Info;
    }

    public override ComponentInfo? Resolve(string tagName, ImportScope scope) => _tags.TryGetValue(tagName, out var info) ? info : null;
}
