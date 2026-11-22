using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Threading;
using AwAUI.Markup.Compiler.Syntax;
using AwAUI.Tailwind;
using AwAUI.Tailwind.Compilation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using RoslynSourceText = Microsoft.CodeAnalysis.Text.SourceText;
using RoslynTextSpan = Microsoft.CodeAnalysis.Text.TextSpan;
using LinePosition = Microsoft.CodeAnalysis.Text.LinePosition;
using LinePositionSpan = Microsoft.CodeAnalysis.Text.LinePositionSpan;
using MarkupNode = AwAUI.Markup.Compiler.Syntax.SyntaxNode;
using SyntaxKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind;

namespace AwAUI.Markup.Generator.TailwindIntegration;

/// <summary>
/// Tailwind 构建期接入（design/23 第 3 层）：项目有 <c>AwAUITailwindStylesheet</c> 入口样式表时，扫描候选类名并为每个入口生成
/// <c>TailwindStyles</c> 类（CSS 文本、生成样式的候选列表、登记到 <c>AwAUI.UI.AppStyleSheets</c> 的模块初始化器）。
/// <para>增量管线（每一步按值缓存，步骤名用于增量测试）：</para>
/// <list type="number">
/// <item><b>TailwindEntry</b>：入口样式表 + 导入池 → 编译一次得到 <c>@source</c> 范围与入口错误（AW5001）。只在样式表变化时运行。</item>
/// <item><b>TailwindScan*</b>：逐文件提取候选。<c>.awaui</c> 扫描全文并记下 <c>class="..."</c> 中的字面量类名；C# 文件只扫描字符串字面量
/// （取解码后的值，<c>"before:content-[\"a\"]"</c> 得到运行时的类名）；<c>AwAUITailwindSource</c> 扫描全文。未改动的文件不重新扫描。</item>
/// <item><b>TailwindCandidates</b>：按入口的范围筛选文件，合并、排序、去重。结果只含字符串，所以只改动位置或无关文本时它不变。</item>
/// <item><b>TailwindBuild</b>：候选集合变化时重新编译（新建编译器 → <c>Build</c>），并检查 class 字面量；集合不变时整步命中缓存。</item>
/// </list>
/// 没有入口样式表时扫描步骤直接返回空结果，不做任何工作。
/// </summary>
internal static class TailwindPipeline
{
    public const string EntryStep = "TailwindEntry";
    public const string MarkupScanStep = "TailwindScanMarkup";
    public const string CSharpScanStep = "TailwindScanCSharp";
    public const string CandidatesStep = "TailwindCandidates";
    public const string BuildStep = "TailwindBuild";

    const string MetadataRole = "build_metadata.AdditionalFiles.AwAUITailwind";
    const string MetadataClassName = "build_metadata.AdditionalFiles.ClassName";

    public static void Register(IncrementalGeneratorInitializationContext context, IncrementalValuesProvider<FileInput> markupInputs)
    {
        var settings = context.AnalyzerConfigOptionsProvider
            .Combine(context.CompilationProvider.Select(static (c, _) => c.AssemblyName ?? ""))
            .Select(static (pair, _) => ReadSettings(pair.Left.GlobalOptions, pair.Right));

        var files = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, ct) => Tag(pair.Left, pair.Right, ct))
            .Where(static f => f is not null)
            .Select(static (f, _) => f!);

        var stylesheets = files
            .Where(static f => f.Role is TailwindFile.Stylesheet or TailwindFile.Import || f.NormalizedPath.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            .Collect()
            .Select(static (all, _) => SortedFiles(all));

        var hasEntries = stylesheets.Select(static (all, _) =>
        {
            foreach (var f in all)
                if (f.Role == TailwindFile.Stylesheet) return true;
            return false;
        });

        var parsed = stylesheets.Combine(settings)
            .SelectMany(static (pair, _) => CreateEntries(pair.Left, pair.Right))
            .Select(static (entry, ct) => Parse(entry, ct))
            .WithTrackingName(EntryStep);

        var markupScans = markupInputs.Combine(hasEntries)
            .Select(static (pair, ct) => pair.Right ? ScanMarkup(pair.Left, ct) : Empty(pair.Left.Path, ScanKind.Markup))
            .WithTrackingName(MarkupScanStep);

        var csharpScans = context.SyntaxProvider
            .CreateSyntaxProvider(static (node, _) => node is CompilationUnitSyntax, static (ctx, _) => ctx.Node.SyntaxTree)
            .Combine(hasEntries)
            .Select(static (pair, ct) => pair.Right ? ScanCSharp(pair.Left, ct) : Empty(pair.Left.FilePath, ScanKind.CSharp))
            .WithTrackingName(CSharpScanStep);

        var explicitScans = files.Where(static f => f.Role == TailwindFile.Source)
            .Combine(hasEntries)
            .Select(static (pair, ct) => pair.Right ? ScanText(pair.Left, ct) : Empty(pair.Left.Path, ScanKind.Explicit));

        var allScans = markupScans.Collect()
            .Combine(csharpScans.Collect())
            .Combine(explicitScans.Collect())
            .Select(static (pair, _) =>
            {
                var list = new List<FileScan>(pair.Left.Left.Length + pair.Left.Right.Length + pair.Right.Length);
                list.AddRange(pair.Left.Left);
                list.AddRange(pair.Left.Right);
                list.AddRange(pair.Right);
                return new EquatableArray<FileScan>(list);
            });

        var candidates = parsed.Combine(allScans)
            .Select(static (pair, ct) => Merge(pair.Left, pair.Right, ct))
            .WithTrackingName(CandidatesStep);

        var results = candidates
            .Select(static (c, _) => new EntryBuildInput(c.Parsed, c.Candidates))
            .Select(static (c, ct) => Build(c, ct))
            .WithTrackingName(BuildStep);

        var compilationInfo = context.CompilationProvider
            .Combine(context.ParseOptionsProvider)
            .Select(static (pair, _) => ReadCompilationInfo(pair.Left, pair.Right));

        context.RegisterSourceOutput(results.Combine(compilationInfo), static (spc, pair) => Produce(spc, pair.Left, pair.Right));

        // 没有 ModuleInitializerAttribute 的目标（netstandard2.0、.NET Framework）：生成一份内部的同名特性（编译器按名称识别）
        context.RegisterSourceOutput(hasEntries.Combine(compilationInfo).Combine(settings), static (spc, pair) =>
        {
            var info = pair.Left.Right;
            if (pair.Left.Left && pair.Right.Register && info.HasAppStyleSheets && info.LanguageSupportsModuleInitializer && !info.ModuleInitializerAvailable)
                spc.AddSource("AwAUI.ModuleInitializerAttribute.g.cs", RoslynSourceText.From(TailwindStylesEmitter.ModuleInitializerPolyfill, Encoding.UTF8));
        });

        var classDiagnostics = candidates.Collect()
            .Combine(markupScans.Collect())
            .Select(static (pair, ct) => FindUnknownClasses(pair.Left, pair.Right, ct));
        context.RegisterSourceOutput(classDiagnostics, static (spc, diagnostics) =>
        {
            foreach (var d in diagnostics) spc.ReportDiagnostic(ToRoslyn(d));
        });
    }

    // ------------------------------------------------------------ 设置与文件

    static TailwindSettings ReadSettings(AnalyzerConfigOptions options, string assemblyName)
    {
        options.TryGetValue("build_property.RootNamespace", out var root);
        options.TryGetValue("build_property.ProjectDir", out var dir);
        if (string.IsNullOrWhiteSpace(root)) root = assemblyName;
        string ns = MarkupSourceGenerator.SanitizeNamespace(root!.Trim());
        string? themeRoot = Get(options, "AwAUITailwindThemeRoot");
        return new TailwindSettings(
            assemblyName,
            ns.Length > 0 ? ns : "AwAUI.Generated",
            string.IsNullOrWhiteSpace(dir) ? null : SourcePaths.Normalize(dir!),
            string.IsNullOrWhiteSpace(themeRoot) ? ":root" : themeRoot!.Trim(),
            !IsFalse(Get(options, "AwAUITailwindRegister")),
            !IsFalse(Get(options, "AwAUITailwindAutoSource")),
            IsTrue(Get(options, "AwAUITailwindMinify")),
            string.Equals(Get(options, "AwAUITailwindPreflight")?.Trim(), "Tailwind", StringComparison.OrdinalIgnoreCase),
            IsTrue(Get(options, "AwAUITailwindRuntime")));
    }

    static string? Get(AnalyzerConfigOptions options, string property) =>
        options.TryGetValue("build_property." + property, out var v) ? v : null;

    static bool IsFalse(string? v) => string.Equals(v?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    static bool IsTrue(string? v) => string.Equals(v?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

    static TailwindFile? Tag(AdditionalText text, AnalyzerConfigOptionsProvider options, CancellationToken ct)
    {
        var o = options.GetOptions(text);
        o.TryGetValue(MetadataRole, out var role);
        role = role?.Trim() ?? "";
        if (!string.Equals(role, TailwindFile.Stylesheet, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(role, TailwindFile.Import, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(role, TailwindFile.Source, StringComparison.OrdinalIgnoreCase))
        {
            role = "";
            if (!text.Path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)) return null;
        }
        else if (string.Equals(role, TailwindFile.Stylesheet, StringComparison.OrdinalIgnoreCase)) role = TailwindFile.Stylesheet;
        else if (string.Equals(role, TailwindFile.Import, StringComparison.OrdinalIgnoreCase)) role = TailwindFile.Import;
        else role = TailwindFile.Source;
        string? className = null;
        if (role == TailwindFile.Stylesheet && o.TryGetValue(MetadataClassName, out var cn) && !string.IsNullOrWhiteSpace(cn)) className = cn.Trim();
        return new TailwindFile(text.Path, role, className, text.GetText(ct)?.ToString() ?? "");
    }

    static EquatableArray<TailwindFile> SortedFiles(ImmutableArray<TailwindFile> all)
    {
        var list = new List<TailwindFile>(all);
        list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new EquatableArray<TailwindFile>(list);
    }

    /// <summary>入口列表：按路径排序；类名取 ClassName 元数据，否则单个入口为 TailwindStyles、多个时为 TailwindStyles_文件名（重名再加序号）。</summary>
    static ImmutableArray<TailwindEntry> CreateEntries(EquatableArray<TailwindFile> files, TailwindSettings settings)
    {
        var entries = new List<TailwindFile>();
        foreach (var f in files)
            if (f.Role == TailwindFile.Stylesheet) entries.Add(f);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = ImmutableArray.CreateBuilder<TailwindEntry>(entries.Count);
        foreach (var f in entries)
        {
            string name;
            if (f.ClassName != null && IsIdentifier(f.ClassName)) name = f.ClassName;
            else if (entries.Count == 1) name = "TailwindStyles";
            else
            {
                string file = f.NormalizedPath.Substring(f.NormalizedPath.LastIndexOf('/') + 1);
                int dot = file.LastIndexOf('.');
                name = "TailwindStyles_" + FileInput.SanitizeIdentifier(dot > 0 ? file.Substring(0, dot) : file);
            }
            string unique = name;
            for (int i = 2; !used.Add(unique); i++) unique = name + "_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            // 相对路径决定提示名与来源名，必须唯一：重复的提示名会让 AddSource 抛异常（整个生成器失败），重复的来源名会让 AppStyleSheets 丢掉后一张
            string relative = RelativePath(f.NormalizedPath, settings.ProjectDir);
            string uniquePath = relative;
            for (int i = 2; !usedPaths.Add(uniquePath); i++) uniquePath = relative + "#" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            result.Add(new TailwindEntry(f, files, settings, unique, uniquePath));
        }
        return result.MoveToImmutable();
    }

    static bool IsIdentifier(string s)
    {
        if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
        foreach (char c in s)
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        return SyntaxFacts.GetKeywordKind(s) == SyntaxKind.None;
    }

    /// <summary>
    /// 相对项目目录的路径（<c>/</c> 分隔）：项目外的文件（链接进来的共享样式表）用 <c>../</c> 表示；没有项目目录或不在同一个根（另一个盘符）时只取文件名。
    /// 不含绝对路径，生成的代码与机器无关。
    /// </summary>
    internal static string RelativePath(string normalized, string? projectDir)
    {
        if (projectDir != null && SourcePaths.IsUnder(normalized, projectDir) && normalized.Length > projectDir.Length)
            return normalized.Substring(projectDir.EndsWith("/", StringComparison.Ordinal) ? projectDir.Length : projectDir.Length + 1);
        if (projectDir != null && projectDir.Length > 0)
        {
            var dir = projectDir.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var path = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            bool sameRoot = projectDir[0] == '/' == (normalized.Length > 0 && normalized[0] == '/');
            int common = 0;
            while (sameRoot && common < dir.Length && common < path.Length - 1 && string.Equals(dir[common], path[common], StringComparison.OrdinalIgnoreCase)) common++;
            // 至少共享根（Unix 的 / 或同一个盘符）
            if (sameRoot && (projectDir[0] == '/' || common > 0))
            {
                var sb = new StringBuilder();
                for (int i = common; i < dir.Length; i++) sb.Append("../");
                for (int i = common; i < path.Length; i++) sb.Append(path[i]).Append(i + 1 < path.Length ? "/" : "");
                return sb.ToString();
            }
        }
        return normalized.Substring(normalized.LastIndexOf('/') + 1);
    }

    static TailwindCompilationInfo ReadCompilationInfo(Compilation compilation, ParseOptions parseOptions)
    {
        bool hasSheets = compilation.GetTypeByMetadataName("AwAUI.UI.AppStyleSheets") is { } t && compilation.IsSymbolAccessibleWithin(t, compilation.Assembly);
        var attr = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.ModuleInitializerAttribute");
        bool attrAvailable = attr is not null && compilation.IsSymbolAccessibleWithin(attr, compilation.Assembly);
        bool language = parseOptions is CSharpParseOptions p && p.LanguageVersion >= LanguageVersion.CSharp9;
        return new TailwindCompilationInfo(hasSheets, attrAvailable, language);
    }

    // ------------------------------------------------------------ 入口

    /// <summary>经附加文件解析 <c>@import</c>（生成器不能读磁盘）：先按原样、再补 <c>.css</c> 扩展名查找。</summary>
    sealed class PoolResolver : StylesheetResolver
    {
        readonly Dictionary<string, TailwindFile> _files = new(StringComparer.OrdinalIgnoreCase);

        public PoolResolver(EquatableArray<TailwindFile> pool)
        {
            foreach (var f in pool)
                if (!_files.ContainsKey(f.NormalizedPath)) _files[f.NormalizedPath] = f;
        }

        public override ResolvedStylesheet? Resolve(string id, string? baseDirectory)
        {
            foreach (var candidate in new[] { id, id + ".css" })
            {
                if (_files.TryGetValue(SourcePaths.Combine(baseDirectory, candidate), out var f))
                    return new ResolvedStylesheet(f.Text, SourcePaths.DirectoryOf(f.Path));
            }
            return null;
        }
    }

    static TailwindOptions Options(TailwindEntry entry) => new()
    {
        Resolver = new PoolResolver(entry.Pool),
        BaseDirectory = entry.Directory,
        ThemeRootSelector = entry.Settings.ThemeRoot,
        Minify = entry.Settings.Minify,
        Preflight = entry.Settings.OriginalPreflight ? PreflightKind.Tailwind : PreflightKind.AwAUI,
    };

    /// <summary>编译入口样式表（不含候选），得到扫描范围；失败时记下错误。</summary>
    internal static ParsedEntry Parse(TailwindEntry entry, CancellationToken ct)
    {
        TailwindCompiler compiler;
        try
        {
            compiler = TailwindCompiler.Create(entry.File.Text, Options(entry));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ParsedEntry(entry, EquatableArray<SourcePattern>.Empty, null, Describe(ex), null);
        }
        ct.ThrowIfCancellationRequested();
        var sources = new List<SourcePattern>();
        foreach (var s in compiler.Sources)
            sources.Add(new SourcePattern(SourcePaths.Combine(s.BaseDirectory.Length > 0 ? s.BaseDirectory : entry.Directory, s.Pattern), s.Negated));
        string? autoRoot = entry.Settings.AutoSource ? entry.Settings.ProjectDir ?? "" : null;
        // @import "tailwindcss" source(none) / source("../src")：关闭自动检测或改变其根目录
        if (autoRoot != null && compiler.UtilitiesParams is { } utilities)
        {
            int i = utilities.LastIndexOf("source(", StringComparison.Ordinal);
            if (i >= 0)
            {
                int end = utilities.IndexOf(')', i);
                string arg = (end < 0 ? utilities.Substring(i + 7) : utilities.Substring(i + 7, end - i - 7)).Trim();
                if (arg == "none") autoRoot = null;
                else if (arg.Length >= 2 && (arg[0] == '"' || arg[0] == '\'') && arg[arg.Length - 1] == arg[0])
                    autoRoot = SourcePaths.Combine(entry.Directory, arg.Substring(1, arg.Length - 2));
            }
        }
        return new ParsedEntry(entry, new EquatableArray<SourcePattern>(sources), autoRoot, null, compiler);
    }

    static string Describe(Exception ex) =>
        ex is TailwindException ? ex.Message : "Tailwind 编译器内部错误（" + ex.GetType().Name + "）：" + ex.Message;

    // ------------------------------------------------------------ 扫描

    static FileScan Empty(string path, ScanKind kind) =>
        new(path ?? "", kind, EquatableArray<string>.Empty, EquatableArray<ClassToken>.Empty, EquatableArray<string>.Empty);

    /// <summary><c>.awaui</c>：全文提取候选（与 Tailwind 的扫描器相同，含 <c>@code</c> 中的字符串），并记下 class 属性里的字面量类名。</summary>
    internal static FileScan ScanMarkup(FileInput input, CancellationToken ct)
    {
        string text = input.Text;
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CandidateScanner.Extract(text, seen, candidates);
        ct.ThrowIfCancellationRequested();
        var tokens = new List<ClassToken>();
        var tree = Compiler.MarkupSyntaxTree.Parse(text, input.Path);
        var localClasses = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string>? globalClasses = null;
        var lines = LineStarts(text);
        var stack = new Stack<MarkupNode>();
        stack.Push(tree.Root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is StyleBlockSyntax style && !style.IsSvg)
            {
                CollectClassSelectors(style.Content, localClasses);
                // <style global> 的类名在所有组件中生效：其他文件使用它们时也不报 AW5002
                if (style.IsGlobal) CollectClassSelectors(style.Content, globalClasses ??= new HashSet<string>(StringComparer.Ordinal));
            }
            // 属性值里的 @@ 与字符引用在运行时是解码后的文本（class="@@container/card" 即类名 @container/card）：解码后的值也要扫描
            if (node is AttributeLiteralSyntax literal && literal.Text != literal.Value) CandidateScanner.Extract(literal.Value, seen, candidates);
            if (node is ElementSyntax element)
            {
                foreach (var a in element.Attributes)
                    if (!a.IsDirective && a.Parameter == null && a.Name == "class" && a.Value != null) CollectClassTokens(a.Value, lines, tokens);
            }
            foreach (var child in node.ChildNodes()) stack.Push(child);
        }
        if (localClasses.Count > 0) tokens.RemoveAll(t => localClasses.Contains(t.Value));
        tokens.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new FileScan(input.Path, ScanKind.Markup, SortedStrings.Of(candidates), new EquatableArray<ClassToken>(tokens),
            globalClasses is null ? EquatableArray<string>.Empty : SortedStrings.Of(globalClasses));
    }

    /// <summary>
    /// class 属性值中的字面量类名：按空白切分字面量片段；紧贴表达式的片段（如 <c>p-@(n)</c> 中的 <c>p-</c>）不是完整的类名，跳过。
    /// 字面量含字符引用时位置取整个片段。
    /// </summary>
    static void CollectClassTokens(AttributeValueSyntax value, int[] lines, List<ClassToken> output)
    {
        var parts = value.Parts;
        for (int p = 0; p < parts.Count; p++)
        {
            if (parts[p] is not AttributeLiteralSyntax lit) continue;
            bool exprBefore = p > 0 && parts[p - 1] is not AttributeLiteralSyntax;
            bool exprAfter = p + 1 < parts.Count && parts[p + 1] is not AttributeLiteralSyntax;
            string v = lit.Value;
            bool exact = lit.Text == v;
            int i = 0;
            while (i < v.Length)
            {
                while (i < v.Length && IsSpace(v[i])) i++;
                int start = i;
                while (i < v.Length && !IsSpace(v[i])) i++;
                if (i == start) break;
                if ((start == 0 && exprBefore) || (i == v.Length && exprAfter)) continue;
                int s = exact ? lit.Span.Start + start : lit.Span.Start;
                int e = exact ? lit.Span.Start + i : lit.Span.End;
                Position(lines, s, out int sl, out int sc);
                Position(lines, e, out int el, out int ec);
                output.Add(new ClassToken(v.Substring(start, i - start), s, e - s, sl, sc, el, ec));
            }
        }
    }

    static bool IsSpace(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f';

    static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                starts.Add(i + 1);
            }
            else if (c == '\n' || c == '\u2028' || c == '\u2029' || c == '\u0085') starts.Add(i + 1);
        }
        return starts.ToArray();
    }

    static void Position(int[] lines, int offset, out int line, out int column)
    {
        int idx = Array.BinarySearch(lines, offset);
        if (idx < 0) idx = ~idx - 1;
        line = Math.Max(0, idx);
        column = offset - lines[line];
    }

    /// <summary>CSS 文本中以类选择器出现的名字（粗略：<c>.</c> 之后的标识符字符；多收无害，只用于排除 AW5002）。</summary>
    internal static void CollectClassSelectors(string css, HashSet<string> output)
    {
        for (int i = 0; i < css.Length; i++)
        {
            if (css[i] != '.') continue;
            int start = i + 1, j = start;
            while (j < css.Length && (char.IsLetterOrDigit(css[j]) || css[j] == '-' || css[j] == '_' || css[j] > 0x7f)) j++;
            if (j > start) output.Add(css.Substring(start, j - start));
            i = j - 1;
        }
    }

    /// <summary>C# 文件：只扫描字符串字面量（普通、逐字、原始、UTF-8、插值字符串的文本片段），取解码后的值。</summary>
    internal static FileScan ScanCSharp(SyntaxTree tree, CancellationToken ct)
    {
        var root = tree.GetRoot(ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in root.DescendantTokens())
        {
            switch (token.Kind())
            {
                case SyntaxKind.StringLiteralToken:
                case SyntaxKind.Utf8StringLiteralToken:
                case SyntaxKind.SingleLineRawStringLiteralToken:
                case SyntaxKind.MultiLineRawStringLiteralToken:
                case SyntaxKind.Utf8SingleLineRawStringLiteralToken:
                case SyntaxKind.Utf8MultiLineRawStringLiteralToken:
                case SyntaxKind.InterpolatedStringTextToken:
                    CandidateScanner.Extract(token.ValueText, seen, null);
                    break;
            }
        }
        ct.ThrowIfCancellationRequested();
        return new FileScan(tree.FilePath ?? "", ScanKind.CSharp, SortedStrings.Of(seen), EquatableArray<ClassToken>.Empty, EquatableArray<string>.Empty);
    }

    static FileScan ScanText(TailwindFile file, CancellationToken ct)
    {
        var candidates = CandidateScanner.Extract(file.Text);
        ct.ThrowIfCancellationRequested();
        return new FileScan(file.Path, ScanKind.Explicit, SortedStrings.Of(candidates), EquatableArray<ClassToken>.Empty, EquatableArray<string>.Empty);
    }

    // ------------------------------------------------------------ 合并与编译

    /// <summary>
    /// 按入口的扫描范围筛选文件，合并候选并只保留有效的（扫描器会多提取普通单词；去掉它们之后，改动文字内容不会让集合变化），
    /// 同时找出像工具类、但无法生成样式的 class 字面量。
    /// </summary>
    internal static EntryCandidates Merge(ParsedEntry parsed, EquatableArray<FileScan> scans, CancellationToken ct)
    {
        var validator = parsed.Validator;
        if (parsed.Error != null || validator == null)
            return new EntryCandidates(parsed, EquatableArray<string>.Empty, EquatableArray<string>.Empty, EquatableArray<string>.Empty);
        var include = new List<SourceMatcher>();
        var exclude = new List<SourceMatcher>();
        int budget = SourcePaths.GlobBudget;
        foreach (var s in parsed.Sources) (s.Negated ? exclude : include).Add(SourcePaths.CompilePattern(s.Pattern, ref budget));
        string? projectDir = parsed.Entry.Settings.ProjectDir;
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        var classTokens = new HashSet<string>(StringComparer.Ordinal);
        var scopedMarkup = new List<string>();
        foreach (var scan in scans)
        {
            ct.ThrowIfCancellationRequested();
            if (scan.Candidates.Count == 0 && scan.ClassTokens.Count == 0) continue;
            if (!InScope(scan, parsed.AutoRoot, projectDir, include, exclude)) continue;
            foreach (var c in scan.Candidates) candidates.Add(c);
            foreach (var t in scan.ClassTokens) classTokens.Add(t.Value);
            if (scan.ClassTokens.Count > 0) scopedMarkup.Add(scan.Path);
        }
        var valid = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            // --x：主题变量的引用（Tailwind 据此输出该变量），无法单独判断有效性，保留
            if (c.StartsWith("--", StringComparison.Ordinal) ? c.Length > 2 : validator.IsValidCandidate(c)) valid.Add(c);
        }
        var invalid = new List<string>();
        HashSet<string>? defined = null;
        foreach (var t in classTokens)
        {
            if (valid.Contains(t) || validator.IsValidCandidate(t) || !LooksLikeUtility(validator, t)) continue;
            if (defined == null)
            {
                defined = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in parsed.Entry.Pool) CollectClassSelectors(f.Text, defined);
            }
            if (!defined.Contains(t)) invalid.Add(t);
        }
        return new EntryCandidates(parsed, SortedStrings.Of(valid), SortedStrings.Of(invalid), SortedStrings.Of(scopedMarkup));
    }

    static bool InScope(FileScan scan, string? autoRoot, string? projectDir, List<SourceMatcher> include, List<SourceMatcher> exclude)
    {
        string path = scan.NormalizedPath;
        foreach (var m in exclude)
            if (m.Matches(path)) return false;
        if (scan.Kind == ScanKind.Explicit) return true;
        if (autoRoot != null && SourcePaths.IsUnder(path, autoRoot) && !InBuildOutput(path, projectDir ?? autoRoot)) return true;
        foreach (var m in include)
            if (m.Matches(path)) return true;
        return false;
    }

    /// <summary>项目的 bin/ 与 obj/ 下的文件（生成的 AssemblyInfo、全局 using 等）不参与自动检测。</summary>
    static bool InBuildOutput(string path, string root)
    {
        if (root.Length == 0 || !SourcePaths.IsUnder(path, root)) return false;
        string rel = path.Substring(Math.Min(path.Length, root.Length)).TrimStart('/');
        return rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>编译入口：新建编译器（<c>Build</c> 会在实例上累积候选，所以每次都新建），生成 CSS。</summary>
    internal static EntryResult Build(EntryBuildInput input, CancellationToken ct)
    {
        var entry = input.Parsed.Entry;
        if (input.Parsed.Error != null) return new EntryResult(entry, null, EquatableArray<string>.Empty, input.Parsed.Error);
        try
        {
            var compiler = TailwindCompiler.Create(entry.File.Text, Options(entry));
            ct.ThrowIfCancellationRequested();
            string css = compiler.Build(input.Candidates);
            ct.ThrowIfCancellationRequested();
            // 运行时补全的基线：生成了样式的类名，以及输出中出现的主题变量引用（--x）
            var used = new List<string>(input.Candidates.Count);
            foreach (var c in input.Candidates)
                if (!c.StartsWith("--", StringComparison.Ordinal) || css.IndexOf(c + ":", StringComparison.Ordinal) >= 0) used.Add(c);
            return new EntryResult(entry, css, new EquatableArray<string>(used), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EntryResult(entry, null, EquatableArray<string>.Empty, Describe(ex));
        }
    }

    /// <summary>
    /// 无法生成样式的类名是否「像」Tailwind 工具类（只对这些报 AW5002，普通的 CSS 类名如 <c>card</c>、<c>todo-item</c> 不报）：
    /// 带变体、任意值、重要性标记，或以已注册的工具名为前缀（<c>bg-primry</c>、<c>p-17x</c>）。
    /// </summary>
    internal static bool LooksLikeUtility(TailwindCompiler compiler, string token)
    {
        if (token.Length == 0) return false;
        if (token.IndexOf(':') >= 0 || token.IndexOf('[') >= 0 || token.IndexOf('(') >= 0 || token[0] == '!' || token[token.Length - 1] == '!') return true;
        string s = token[0] == '-' ? token.Substring(1) : token;
        if (s.Length == 0) return false;
        // 工具注册表在编译器创建后只读，可以不经编译器的锁读取
        var utilities = compiler.Design.Utilities;
        if (utilities.Has(s, UtilityKind.Static)) return true;
        for (int i = s.IndexOf('-'); i > 0; i = s.IndexOf('-', i + 1))
        {
            if (utilities.Has(s.Substring(0, i), UtilityKind.Functional)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------ 输出与诊断

    static void Produce(SourceProductionContext spc, EntryResult result, TailwindCompilationInfo info)
    {
        var entry = result.Entry;
        if (result.Error != null)
        {
            spc.ReportDiagnostic(Diagnostic.Create(TailwindDiagnostics.StylesheetError,
                Location.Create(entry.File.Path, new RoslynTextSpan(0, 0), new LinePositionSpan(new LinePosition(0, 0), new LinePosition(0, 0))),
                entry.RelativePath, result.Error));
            return;
        }
        string code = TailwindStylesEmitter.Emit(result, info);
        spc.AddSource(HintName(entry), RoslynSourceText.From(code, Encoding.UTF8));
    }

    internal static string HintName(TailwindEntry entry)
    {
        string hint = MarkupSourceGenerator.HintName(entry.RelativePath);
        return "TailwindStyles." + hint;
    }

    /// <summary>
    /// AW5002：<c>.awaui</c> 文件中像工具类、但在扫描范围包含该文件的每个入口中都无法生成样式的 class 字面量。
    /// 不在任何入口扫描范围内的文件（<c>@source not</c>、<c>source(none)</c> 之外）不由 Tailwind 管理，不报告。
    /// </summary>
    static EquatableArray<ClassDiagnostic> FindUnknownClasses(ImmutableArray<EntryCandidates> results, ImmutableArray<FileScan> scans, CancellationToken ct)
    {
        if (results.IsDefaultOrEmpty) return EquatableArray<ClassDiagnostic>.Empty;
        var invalid = new HashSet<string>[results.Length];
        var scoped = new HashSet<string>[results.Length];
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i].Parsed.Error != null) return EquatableArray<ClassDiagnostic>.Empty; // 入口有错误时（AW5001）不再逐个报告类名
            invalid[i] = new HashSet<string>(results[i].InvalidClassTokens, StringComparer.Ordinal);
            scoped[i] = new HashSet<string>(results[i].ScopedMarkupPaths, StringComparer.Ordinal);
        }
        // 任何 .awaui 的 <style global> 定义的类名（全局生效，不是 Tailwind 的类名）
        var globalClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scan in scans)
            foreach (var c in scan.GlobalClasses) globalClasses.Add(c);
        var list = new List<ClassDiagnostic>();
        var covering = new List<int>();
        var names = new StringBuilder();
        foreach (var scan in scans)
        {
            ct.ThrowIfCancellationRequested();
            if (scan.ClassTokens.Count == 0) continue;
            covering.Clear();
            for (int i = 0; i < results.Length; i++)
                if (scoped[i].Contains(scan.Path)) covering.Add(i);
            if (covering.Count == 0) continue;
            string? stylesheets = null;
            foreach (var t in scan.ClassTokens)
            {
                if (globalClasses.Contains(t.Value)) continue;
                bool all = true;
                foreach (int i in covering)
                    if (!invalid[i].Contains(t.Value)) { all = false; break; }
                if (!all) continue;
                if (stylesheets == null)
                {
                    names.Clear();
                    foreach (int i in covering) names.Append(names.Length > 0 ? ", " : "").Append(results[i].Parsed.Entry.RelativePath);
                    stylesheets = names.ToString();
                }
                list.Add(new ClassDiagnostic(scan.Path, t, stylesheets));
            }
        }
        return new EquatableArray<ClassDiagnostic>(list);
    }

    static Diagnostic ToRoslyn(ClassDiagnostic d)
    {
        var t = d.Token;
        var location = Location.Create(d.Path, new RoslynTextSpan(t.Start, t.Length),
            new LinePositionSpan(new LinePosition(t.StartLine, t.StartColumn), new LinePosition(t.EndLine, t.EndColumn)));
        return Diagnostic.Create(TailwindDiagnostics.UnknownClass, location, t.Value, d.Stylesheets);
    }
}

/// <summary>Tailwind 接入的诊断（AW5xxx，design/23）。</summary>
internal static class TailwindDiagnostics
{
    const string Category = "AwAUI.Tailwind";

    /// <summary>AW5001：入口样式表无法编译（语法、未知的 @import、不支持的指令、超出上限 ...）。</summary>
    public static readonly DiagnosticDescriptor StylesheetError = new(
        "AW5001", "Tailwind 入口样式表无法编译", "Tailwind 样式表 {0} 无法编译：{1}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    /// <summary>AW5002：<c>.awaui</c> 的 class 属性中像工具类、但无法生成样式的字面量类名。</summary>
    public static readonly DiagnosticDescriptor UnknownClass = new(
        "AW5002", "未知的 Tailwind 类名", "类名 '{0}' 不能由 Tailwind 样式表 {1} 生成样式（未知的工具、取值或变体）", Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);
}
