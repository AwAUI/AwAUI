extern alias markupgen;

using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using AwAUI.Generators;
using AwAUI.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using MarkupSourceGenerator = markupgen::AwAUI.Markup.Generator.MarkupSourceGenerator;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
#if NET
using System.Runtime.Loader;
#endif

namespace AwAUI.Markup.UnitTests;

/// <summary>
/// 用真实的 <c>.awaui</c> 生成器（以及 C# 胶水生成器）编译一组 <c>.awaui</c> 与 C# 源文件，引用 AwAUI.UI，
/// 得到生成的源码、全部诊断，并可把结果程序集加载到可回收的加载上下文中运行。
/// </summary>
sealed class GeneratorHarness
{
    public const string ProjectDir = "/proj/";

    static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path) && seen.Add(Path.GetFileName(path)))
                refs.Add(MetadataReference.CreateFromFile(path!));
        }
        Add(typeof(Component).Assembly.Location);
        Add(typeof(Point).Assembly.Location);
        Add(typeof(AwAUI.Platform.PhysicalKey).Assembly.Location);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (var p in tpa.Split(Path.PathSeparator)) Add(p);
        }
        else
        {
            // .NET Framework：引用已加载的程序集与 netstandard 门面
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!a.IsDynamic) Add(a.Location);
            }
            Add(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "Facades", "netstandard.dll"));
        }
        return refs.ToImmutableArray();
    });

    public sealed class Result
    {
        public Result(GeneratorDriverRunResult run, Compilation output, ImmutableArray<Diagnostic> generatorDiagnostics)
        {
            Run = run;
            Output = output;
            GeneratorDiagnostics = generatorDiagnostics;
        }

        public GeneratorDriverRunResult Run { get; }
        public Compilation Output { get; }
        public ImmutableArray<Diagnostic> GeneratorDiagnostics { get; }

        /// <summary>生成器诊断 + 最终编译的诊断。</summary>
        public IEnumerable<Diagnostic> AllDiagnostics => GeneratorDiagnostics.Concat(Output.GetDiagnostics());

        public IEnumerable<Diagnostic> Errors => AllDiagnostics.Where(d => d.Severity == RoslynSeverity.Error);

        public string Generated(string fileNamePart) =>
            Run.GeneratedTrees.Single(t => Path.GetFileName(t.FilePath).Contains(fileNamePart, StringComparison.Ordinal)).GetText().ToString();

        public void AssertNoErrors()
        {
            var errors = Errors.ToList();
            Assert.True(errors.Count == 0, "errors:\n" + string.Join("\n", errors) + "\n\n" + DumpGenerated());
        }

        public string DumpGenerated() =>
            string.Join("\n\n", Run.GeneratedTrees.Select(t => "// ===== " + t.FilePath + "\n" + t.GetText()));

        /// <summary>编译并加载（.NET 上进入可回收的加载上下文；AwAUI.UI 回落到默认上下文，与测试共享同一份类型）。</summary>
        public LoadedAssembly Load()
        {
            AssertNoErrors();
            using var ms = new MemoryStream();
            var emit = Output.Emit(ms);
            Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));
            ms.Position = 0;
            return new LoadedAssembly(ms);
        }
    }

    readonly List<AdditionalText> _markup = new();
    readonly List<(string Path, string Text)> _csharp = new();

    public LanguageVersion LanguageVersion { get; set; } = LanguageVersion.Latest;

    public NullableContextOptions Nullable { get; set; } = NullableContextOptions.Enable;

    public string RootNamespace { get; set; } = "TestApp";

    /// <summary>同时运行 C# 胶水生成器（AwAUI.Generators），与真实项目相同。</summary>
    public bool IncludeCSharpGlueGenerator { get; set; } = true;

    public GeneratorHarness Awaui(string relativePath, string text)
    {
        _markup.Add(new InMemoryAdditionalText(ProjectDir + relativePath, text));
        return this;
    }

    /// <summary>以完整路径加入 .awaui（模拟项目目录之外的链接文件）。</summary>
    public GeneratorHarness AwauiAt(string fullPath, string text)
    {
        _markup.Add(new InMemoryAdditionalText(fullPath, text));
        return this;
    }

    public GeneratorHarness CSharp(string relativePath, string text)
    {
        _csharp.Add((ProjectDir + relativePath, text));
        return this;
    }

    public CSharpParseOptions ParseOptions => new(LanguageVersion);

    /// <summary>传给生成器的 .awaui 文本（增量测试用它们调用 <c>ReplaceAdditionalText</c>）。</summary>
    public IReadOnlyList<AdditionalText> AdditionalTexts => _markup;

    public CSharpCompilation CreateCompilation() =>
        CSharpCompilation.Create("AwauiE2E_" + Guid.NewGuid().ToString("N"),
            _csharp.Select(c => CSharpSyntaxTree.ParseText(c.Text, ParseOptions, path: c.Path, encoding: Encoding.UTF8)),
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: Nullable));

    public GeneratorDriver CreateDriver(bool trackSteps = false)
    {
        var generators = new List<ISourceGenerator> { new MarkupSourceGenerator().AsSourceGenerator() };
        if (IncludeCSharpGlueGenerator) generators.Add(new ComponentGlueGenerator().AsSourceGenerator());
        return CSharpGeneratorDriver.Create(generators,
            _markup,
            ParseOptions,
            new OptionsProvider(RootNamespace, ProjectDir),
            new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: trackSteps));
    }

    public Result Run()
    {
        var driver = CreateDriver().RunGeneratorsAndUpdateCompilation(CreateCompilation(), out var output, out var diagnostics);
        return new Result(driver.GetRunResult(), output, diagnostics);
    }

    public sealed class InMemoryAdditionalText : AdditionalText
    {
        readonly SourceText _text;

        public InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text, Encoding.UTF8);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }

    sealed class OptionsProvider : AnalyzerConfigOptionsProvider
    {
        readonly Options _global;

        public OptionsProvider(string rootNamespace, string projectDir) =>
            _global = new Options(new Dictionary<string, string>
            {
                ["build_property.RootNamespace"] = rootNamespace,
                ["build_property.ProjectDir"] = projectDir,
            });

        public override AnalyzerConfigOptions GlobalOptions => _global;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;

        sealed class Options : AnalyzerConfigOptions
        {
            public static readonly Options Empty = new(new Dictionary<string, string>());
            readonly Dictionary<string, string> _values;

            public Options(Dictionary<string, string> values) => _values = values;

            public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
        }
    }
}

/// <summary>加载后的测试程序集。</summary>
sealed class LoadedAssembly : IDisposable
{
#if NET
    readonly AssemblyLoadContext _context;
#endif

    public LoadedAssembly(Stream image)
    {
#if NET
        _context = new AssemblyLoadContext("awaui-e2e", isCollectible: true);
        Assembly = _context.LoadFromStream(image);
#else
        using var copy = new MemoryStream();
        image.CopyTo(copy);
        Assembly = Assembly.Load(copy.ToArray());
#endif
    }

    public Assembly Assembly { get; }

    public Type Type(string fullName) => Assembly.GetType(fullName, throwOnError: true)!;

    public Component Create(string fullName) => (Component)Activator.CreateInstance(Type(fullName))!;

    public void Dispose()
    {
#if NET
        _context.Unload();
#endif
    }
}

/// <summary>元素树的规范化文本，用于比较 .awaui 与手写 C# 产出的界面。</summary>
static class TreeDump
{
    public static string Dump(Element root, bool includeScope = false)
    {
        var sb = new StringBuilder();
        foreach (var c in root.Children) Write(c, sb, 0, includeScope);
        return sb.ToString();
    }

    static void Write(Element e, StringBuilder sb, int depth, bool includeScope)
    {
        sb.Append(' ', depth * 2);
        if (e is TextNodeElement t)
        {
            sb.Append('\'').Append(t.Text).Append("'\n");
            return;
        }
        sb.Append(e.Tag);
        if (e.Id != null) sb.Append('#').Append(e.Id);
        foreach (var c in e.Classes) sb.Append('.').Append(c);
        foreach (var name in new[] { "role", "aria-label", "tabindex", "style", "src", "alt", "disabled", "type", "value", "placeholder", "readonly" })
        {
            var v = e.GetAttribute(name);
            if (v != null) sb.Append('[').Append(name).Append('=').Append(v).Append(']');
        }
        foreach (var kv in e.Attributes) sb.Append('[').Append(kv.Key).Append('=').Append(kv.Value).Append(']');
        if (includeScope && e.Scope != null) sb.Append("[scope=").Append(e.Scope).Append(']');
        if (e is TextElement te && te.Content != null) sb.Append(" content='").Append(te.Content).Append('\'');
        sb.Append('\n');
        foreach (var c in e.Children) Write(c, sb, depth + 1, includeScope);
    }

    public static List<Element> FindAll(this Element root, Func<Element, bool> predicate)
    {
        var result = new List<Element>();
        var stack = new Stack<Element>();
        for (int i = root.Children.Count - 1; i >= 0; i--) stack.Push(root.Children[i]);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (predicate(e)) result.Add(e);
            for (int i = e.Children.Count - 1; i >= 0; i--) stack.Push(e.Children[i]);
        }
        return result;
    }

    public static Element ByClass(this Element root, string cls) => root.FindAll(e => e.HasClass(cls)).Single();
}
