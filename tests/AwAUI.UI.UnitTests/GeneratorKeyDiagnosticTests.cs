using System.Collections.Immutable;
using AwAUI.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace AwAUI.UI.UnitTests;

/// <summary>AW1004：动态列表中的组件子项缺少 Key。</summary>
public class GeneratorKeyDiagnosticTests
{
    static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path) && seen.Add(Path.GetFileName(path)))
                refs.Add(MetadataReference.CreateFromFile(path));
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
            Add(typeof(object).Assembly.Location);
        }
        return refs.ToImmutableArray();
    });

    const string Usings = "using System;\nusing System.Linq;\nusing AwAUI.UI;\n";

    const string Row = """
        public partial class Row : Component
        {
            public int Id { get; set; }
            protected override Node Build() => new Div();
        }
        """;

    static ImmutableArray<Diagnostic> Run(string body)
    {
        var source = Usings + Row + body;
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create("KeyTest",
            new[] { CSharpSyntaxTree.ParseText(source, parse, path: "Test.cs") },
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new ComponentGlueGenerator().AsSourceGenerator() }, parseOptions: parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var errors = output.GetDiagnostics().Where(d => d.Severity == RoslynSeverity.Error).ToArray();
        Assert.Empty(errors);
        return driver.GetRunResult().Diagnostics.Where(d => d.Id == "AW1004").ToImmutableArray();
    }

    static void AssertOneAt(ImmutableArray<Diagnostic> diagnostics, string source, string marker)
    {
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(RoslynSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Key", diagnostic.GetMessage());

        int index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "marker not found");
        // 诊断位置在类型标识符上（"new " 之后）
        int typeIndex = index + "new ".Length;
        var position = diagnostic.Location.GetLineSpan().StartLinePosition;
        int lineStart = source.LastIndexOf('\n', typeIndex) + 1;
        int line = source.Take(typeIndex).Count(c => c == '\n') + 1; // 1-based
        Assert.Equal(line - 1, position.Line);
        Assert.Equal(typeIndex - lineStart, position.Character);
    }

    const string Page = """
        public partial class Page : Component
        {
            protected override Node Build()
            {
                var items = new NodeList();
        """;

    const string PageEnd = """
                return new Div { Children = items };
            }
        }
        """;

    static string Wrap(string loopBody) => "\n" + Page + loopBody + PageEnd + "\n";

    [Fact]
    public void Foreach_add_without_key_warns()
    {
        var source = Usings + Row + Wrap("""
                    foreach (var id in new[] { 1, 2 })
                        items.Add(new Row { Id = id });
            """);
        AssertOneAt(Run(Wrap("""
                    foreach (var id in new[] { 1, 2 })
                        items.Add(new Row { Id = id });
            """)), source, "new Row");
    }

    [Fact]
    public void Foreach_add_with_key_does_not_warn()
    {
        var diagnostics = Run(Wrap("""
                    foreach (var id in new[] { 1, 2 })
                        items.Add(new Row { Key = id, Id = id });
            """));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Select_projection_without_key_warns()
    {
        var body = Wrap("""
                    var rows = new[] { 1, 2 }.Select(id => new Row { Id = id }).ToArray();
                    foreach (var row in rows) items.Add(row);
            """);
        var source = Usings + Row + body;
        AssertOneAt(Run(body), source, "new Row");
    }

    [Fact]
    public void Select_projection_with_key_does_not_warn()
    {
        var diagnostics = Run(Wrap("""
                    var rows = new[] { 1, 2 }.Select(id => new Row { Key = id, Id = id }).ToArray();
                    foreach (var row in rows) items.Add(row);
            """));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Single_return_new_component_does_not_warn()
    {
        var diagnostics = Run("""
            public partial class Single : Component
            {
                protected override Node Build() => new Row();
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Plain_node_in_loop_does_not_warn()
    {
        var diagnostics = Run(Wrap("""
                    foreach (var id in new[] { 1, 2 })
                        items.Add(new Div { Class = "row" });
            """));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Collection_expression_element_without_key_warns()
    {
        var body = Wrap("""
                    foreach (var id in new[] { 1, 2 })
                        items.Add(new Div { Children = [new Row { Id = id }] });
            """);
        var source = Usings + Row + body;
        AssertOneAt(Run(body), source, "new Row");
    }

    [Fact]
    public void Local_variable_added_later_without_key_warns()
    {
        var body = Wrap("""
                    foreach (var id in new[] { 1, 2 })
                    {
                        var row = new Row { Id = id };
                        items.Add(row);
                    }
            """);
        var source = Usings + Row + body;
        AssertOneAt(Run(body), source, "new Row");
    }

    [Fact]
    public void Post_construction_key_assignment_does_not_warn()
    {
        var diagnostics = Run(Wrap("""
                    foreach (var id in new[] { 1, 2 })
                    {
                        var row = new Row { Id = id };
                        row.Key = id;
                        items.Add(row);
                    }
            """));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Counted_for_loop_does_not_warn()
    {
        // 计数循环产生位置稳定的固定列表（月份、年份等），不要求 Key
        var diagnostics = Run(Wrap("""
                    for (int i = 0; i < 2; i++)
                        items.Add(new Row { Id = i });
            """));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Component_outside_a_component_type_does_not_warn()
    {
        // 静态辅助方法里直接拼装描述树不算组件自身的 Build，保守地不报告
        var diagnostics = Run("""
            public static class Helper
            {
                public static NodeList Make(NodeList items)
                {
                    foreach (var id in new[] { 1, 2 })
                        items.Add(new Row { Id = id });
                    return items;
                }
            }
            """);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Direct_child_assignment_outside_a_list_does_not_warn()
    {
        // 循环中仅创建单个组件并直接 return / 赋给字段，不算列表项
        var diagnostics = Run(Wrap("""
                    Row? one = null;
                    foreach (var id in new[] { 1 })
                        one = new Row { Id = id };
                    _ = one;
            """));
        Assert.Empty(diagnostics);
    }
}
