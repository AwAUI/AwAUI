using Microsoft.CodeAnalysis;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace AwAUI.Markup.UnitTests;

/// <summary>
/// 诊断的位置（design/14「类型安全全部来自 C# 编译器……经 #line 映射回 .awaui」）：
/// 生成的 C# 中的编译错误落在 .awaui 的行列上；标记编译器自己的诊断由生成器按 .awaui 的位置报告。
/// </summary>
public class GeneratorDiagnosticsTests
{
    const string Path = GeneratorHarness.ProjectDir + "Bad.awaui";

    static Diagnostic Single(GeneratorHarness.Result r, string id) =>
        Assert.Single(r.AllDiagnostics, d => d.Id == id);

    /// <summary>断言诊断映射到 Bad.awaui 的（从 1 开始的）行列。</summary>
    static void AssertAt(Diagnostic d, int line, int column)
    {
        var span = d.Location.GetMappedLineSpan();
        Assert.Equal(Path, span.Path);
        Assert.Equal((line, column), (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
    }

    static GeneratorHarness.Result Run(string text) => new GeneratorHarness().Awaui("Bad.awaui", text).Run();

    [Fact]
    public void Error_inside_code_block_maps_to_awaui_line_and_column()
    {
        var r = Run("""
            <div>ok</div>

            @code {
                int count = "not a number";
            }
            """);
        AssertAt(Single(r, "CS0029"), 4, 17);
    }

    [Fact]
    public void Error_in_markup_expression_maps_to_its_column()
    {
        var r = Run("""
            <div class="a">
                <text>值：@missing.Value</text>
            </div>
            """);
        AssertAt(Single(r, "CS0103"), 2, 14);
    }

    [Fact]
    public void Errors_in_control_flow_attribute_values_and_handlers_map_to_source()
    {
        var r = Run("""
            @if (undefinedFlag)
            {
                <div tabindex="@("x")" @onclick="NoSuchMethod">x</div>
            }
            @code {
            }
            """);
        var errors = r.Errors.ToList();
        AssertAt(errors.Single(d => d.GetMessage().Contains("undefinedFlag")), 1, 6);
        AssertAt(errors.Single(d => d.GetMessage().Contains("NoSuchMethod")), 3, 38);
        AssertAt(errors.Single(d => d.Id == "CS0029"), 3, 22); // string → int?
    }

    [Fact]
    public void Unknown_component_maps_to_tag_name()
    {
        var r = Run("<div>\n    <NoSuchComponent Value=\"1\" />\n</div>");
        AssertAt(Single(r, "CS0246"), 2, 6);
    }

    [Fact]
    public void Markup_diagnostics_are_reported_at_awaui_locations()
    {
        var r = Run("""
            <div>
              <dvi>x</dvi>
              <canvas />
              <input maxlength="3" />
              <span @ondblclick="Go">y</span>
            </div>
            @code { void Go() { } }
            """);
        var d = Single(r, "AW4002");
        Assert.Equal(RoslynSeverity.Error, d.Severity);
        AssertAt(d, 2, 4);
        var unsupported = r.AllDiagnostics.Where(x => x.Id == "AW4012").OrderBy(x => x.Location.GetMappedLineSpan().StartLinePosition.Line).ToList();
        Assert.Equal(3, unsupported.Count);
        AssertAt(unsupported[0], 3, 4);   // <canvas>：运行时没有该元素 → 错误，不生成代码
        Assert.Equal(RoslynSeverity.Error, unsupported[0].Severity);
        AssertAt(unsupported[1], 4, 10);  // maxlength：行为未实现 → 警告，透传为自定义属性
        Assert.Equal(RoslynSeverity.Warning, unsupported[1].Severity);
        AssertAt(unsupported[2], 5, 9);   // @ondblclick：没有处理器属性 → 错误
        Assert.Equal(RoslynSeverity.Error, unsupported[2].Severity);
        // 出错的元素被跳过：生成的 C# 本身没有编译错误
        Assert.DoesNotContain(r.Output.GetDiagnostics(), x => x.Severity == RoslynSeverity.Error);
    }

    [Fact]
    public void Unknown_role_literal_is_a_conversion_error()
    {
        var r = Run("<div role=\"buton\" />");
        var d = Single(r, "AW4004");
        Assert.Contains("button", d.GetMessage());
        AssertAt(d, 1, 11);
    }

    [Fact]
    public void Generator_reports_invalid_file_names_and_still_produces_no_crash()
    {
        var r = new GeneratorHarness().Awaui("my-page.awaui", "<div/>").Awaui("Fine.awaui", "<div/>").Run();
        Assert.Contains(r.AllDiagnostics, d => d.Id == "AW4011");
        Assert.DoesNotContain(r.AllDiagnostics, d => d.Id == "CS8785"); // 生成器异常
        // 无法成为类名的文件不生成代码：只有 AW4011，而不是一串指向生成文件的 C# 语法错误；其他文件照常生成
        Assert.Equal(new[] { "AW4011" }, r.Errors.Select(d => d.Id).ToArray());
        Assert.Single(r.Run.GeneratedTrees, t => t.FilePath.Contains("Fine.awaui", StringComparison.Ordinal));
    }

    [Fact]
    public void Line_directive_paths_are_written_verbatim_so_windows_paths_map_back()
    {
        // #line 的文件名不处理转义：写成 "C:\\proj\\..." 会让诊断指向一个不存在的路径（Windows 上的全部 #line 映射都会失效）
        const string path = @"C:\proj\Pages\Bad.awaui";
        var r = new GeneratorHarness().AwauiAt(path, "<div/>\n@code {\n    int count = \"x\";\n}").Run();
        var span = Single(r, "CS0029").Location.GetMappedLineSpan();
        Assert.Equal(path, span.Path);
        Assert.Equal((3, 17), (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
        Assert.Contains("#line 2 \"" + path + "\"", r.Run.GeneratedTrees.Single().GetText().ToString());
    }

    [Fact]
    public void Paths_that_line_directives_cannot_express_fall_back_to_the_generated_file()
    {
        // 类 Unix 系统上目录名可以含 "：#line 无法表示，不输出 #line，诊断落在生成的文件上（而不是一个截断的路径），代码照常编译
        var ok = new GeneratorHarness().AwauiAt("/proj/a\"b/Ok.awaui", "<div class=\"x\">@n</div>\n@code { int n = 1; }").Run();
        ok.AssertNoErrors();
        Assert.DoesNotContain("#line", ok.Run.GeneratedTrees.Single().GetText().ToString());

        var bad = new GeneratorHarness().AwauiAt("/proj/a\"b/Bad.awaui", "<div/>\n@code { int n = \"x\"; }").Run();
        var span = Single(bad, "CS0029").Location.GetMappedLineSpan();
        Assert.Equal(bad.Run.GeneratedTrees.Single().FilePath, span.Path);
    }

    [Fact]
    public void Linked_files_with_the_same_relative_path_get_unique_hint_names()
    {
        // 项目目录之外的链接文件只用文件名作相对路径：同名时提示名曾经相同，AddSource 抛异常，整个生成器的输出都丢失（CS8785）
        var r = new GeneratorHarness()
            .AwauiAt("/shared/a/Foo.awaui", "@namespace Shared.A\n<div class=\"a\"/>")
            .AwauiAt("/shared/b/Foo.awaui", "@namespace Shared.B\n<div class=\"b\"/>")
            .Awaui("Ok.awaui", "<Shared.A.Foo /><Shared.B.Foo />")
            .Run();
        r.AssertNoErrors();
        Assert.DoesNotContain(r.AllDiagnostics, d => d.Id == "CS8785");
        Assert.Equal(3, r.Run.GeneratedTrees.Length);
        Assert.Equal(3, r.Run.GeneratedTrees.Select(t => t.FilePath).Distinct().Count());
    }

    [Fact]
    public void Root_namespace_is_sanitized_into_identifiers()
    {
        // SDK 的默认 RootNamespace 只替换空格：项目 my-app 的根命名空间是 my-app，原样写进 namespace 会无法编译
        var r = new GeneratorHarness { RootNamespace = "my-app.2d" }.Awaui("Views/Home.awaui", "<div/>").Run();
        r.AssertNoErrors();
        Assert.Contains("namespace my_app._2d.Views", r.Run.GeneratedTrees.Single().GetText().ToString());
    }

    [Fact]
    public void Ref_and_attributes_on_components_report_runtime_gap_instead_of_uncompilable_assignment()
    {
        var r = new GeneratorHarness()
            .Awaui("Child.awaui", "<div/>\n@code { public int Value { get; set; } }")
            .Awaui("Bad.awaui", "<div>\n  <Child @ref=\"child\" Value=\"1\" />\n  <Child @attributes=\"extra\" />\n  <Button @ref=\"button\" />\n</div>\n@code {\n  object? child;\n  Dictionary<string, object?> extra = new();\n  Ref<Element> button = new();\n}")
            .Run();
        var gaps = r.AllDiagnostics.Where(d => d.Id == "AW4012").OrderBy(d => d.Location.GetMappedLineSpan().StartLinePosition.Line).ToList();
        Assert.Equal(2, gaps.Count);
        Assert.All(gaps, d => Assert.Equal(RoslynSeverity.Error, d.Severity));
        AssertAt(gaps[0], 2, 10);
        AssertAt(gaps[1], 3, 10);
        // 元素描述类型（<Button> → AwAUI.UI.Button）有 Ref：正常生成；生成的 C# 本身没有错误（CS1061 之类）
        Assert.DoesNotContain(r.Output.GetDiagnostics(), d => d.Severity == RoslynSeverity.Error);
    }

    [Fact]
    public void Template_context_must_be_an_identifier()
    {
        var r = new GeneratorHarness()
            .Awaui("Rows.awaui", "<div/>\n@code { public Func<string, Node>? Row { get; set; } }")
            .Awaui("Bad.awaui", "<Rows>\n  <Row Context=\"a b\"><text>@context</text></Row>\n</Rows>")
            .Run();
        AssertAt(Single(r, "AW4004"), 2, 16); // 值的范围含引号（与其他 AW4004 一致）
        Assert.DoesNotContain(r.Output.GetDiagnostics(), d => d.Severity == RoslynSeverity.Error);
    }
}
