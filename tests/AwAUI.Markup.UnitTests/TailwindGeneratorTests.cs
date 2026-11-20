using System.Runtime.CompilerServices;
using AwAUI.Css;
using AwAUI.Tests.Shared;
using AwAUI.Text.Testing;
using AwAUI.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace AwAUI.Markup.UnitTests;

/// <summary>
/// Tailwind 构建期接入（design/23 第 3 层）：<c>AwAUITailwindStylesheet</c> 入口 → 扫描 <c>.awaui</c> 与 C# 字符串中的类名 → 生成 <c>TailwindStyles</c>；
/// 诊断 AW5001 / AW5002；增量缓存；生成的 CSS 经 AwAUI.Css 级联得到正确的计算值。
/// 除专门检查模块初始化器的测试外都关闭自动登记（AwAUITailwindRegister=false），避免测试进程里的全局 <c>AppStyleSheets</c> 被写入。
/// </summary>
public class TailwindGeneratorTests
{
    // 与生成器中 TailwindPipeline 的步骤名一致（生成器的内部类型对测试不可见）
    static class Steps
    {
        public const string Entry = "TailwindEntry";
        public const string MarkupScan = "TailwindScanMarkup";
        public const string CSharpScan = "TailwindScanCSharp";
        public const string Candidates = "TailwindCandidates";
        public const string Build = "TailwindBuild";
    }

    static GeneratorHarness Harness() => new GeneratorHarness().Property("AwAUITailwindRegister", "false");

    static string Styles(GeneratorHarness.Result r, string part = "TailwindStyles.") => r.Generated(part);

    static IEnumerable<Diagnostic> Diags(GeneratorHarness.Result r, string id) => r.GeneratorDiagnostics.Where(d => d.Id == id);

    [Fact]
    public void Classes_from_markup_code_blocks_and_csharp_string_literals_are_generated()
    {
        var r = Harness()
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss\";")
            .Awaui("Card.awaui", """
                <div class="p-4 bg-red-500 hover:bg-red-600">
                    <span class="@(Active ? "font-bold" : "italic")">x</span>
                </div>
                @code { public bool Active { get; set; } string extra = "mt-2"; }
                """)
            .CSharp("Helpers.cs", """"
                static class Helpers
                {
                    public const string Row = "flex gap-[3px]";
                    public static string Width(int n) => $"w-{n} grid";
                    public const string Escaped = "before:content-[\"a\"]";
                    public const string Raw = """rounded-lg""";
                    // 注释里的 underline 不扫描
                }
                """")
            .Run();
        r.AssertNoErrors();
        string code = Styles(r);
        foreach (var selector in new[] { ".p-4 {", ".bg-red-500 {", ".hover\\:bg-red-600:hover {", ".font-bold {", ".italic {", ".mt-2 {", ".flex {", ".gap-\\[3px\\] {", ".grid {", ".rounded-lg {", ".before\\:content-\\[\\\"a\\\"\\]::before {" })
            Assert.Contains(selector.Replace("\"", "\"\""), code);
        Assert.DoesNotContain(".underline", code);
        // 主题根：AwAUI.Css 支持 :host 之前默认 :root
        Assert.Contains(":root {", code);
        Assert.DoesNotContain(":host", code);
        // 候选列表只含生成了样式的类名，按序数排序
        var candidates = CandidateList(code);
        Assert.Contains("p-4", candidates);
        Assert.Contains("before:content-[\"a\"]", candidates);
        Assert.DoesNotContain("Active", candidates);
        Assert.DoesNotContain("x", candidates);
        Assert.Equal(candidates.OrderBy(c => c, StringComparer.Ordinal), candidates);
        Assert.DoesNotContain("ModuleInitializer", code);
        Assert.Contains("public static void Register()", code);
    }

    /// <summary>生成代码中 <c>Candidates</c> 数组的元素（解码 C# 字符串字面量）。</summary>
    static List<string> CandidateList(string code)
    {
        int start = code.IndexOf("Candidates { get; } = new string[]", StringComparison.Ordinal);
        int end = code.IndexOf("};", start, StringComparison.Ordinal);
        var tree = CSharpSyntaxTree.ParseText("class C { string[] a = new string[] " + code.Substring(code.IndexOf('{', start + 20), end - code.IndexOf('{', start + 20) + 1) + "; }");
        return tree.GetRoot().DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken)).Select(t => t.ValueText).ToList();
    }

    [Fact]
    public void Generated_class_matches_snapshot()
    {
        var r = new GeneratorHarness { AssemblyName = "TestApp" }
            .TailwindStylesheet("Styles/app.css", """
                @import "tailwindcss/utilities.css" layer(utilities);
                @theme { --spacing: 4px; --color-brand: #123456; }
                """)
            .Awaui("Button.awaui", """<span class="p-2 bg-brand hover:bg-brand/50 text-[13px]">x</span>""")
            .Run();
        r.AssertNoErrors();
        string actual = Styles(r);
        if (Environment.GetEnvironmentVariable("AWAUI_UPDATE_SNAPSHOTS") == "1")
        {
            File.WriteAllText(Path.Combine(SourceSnapshotDir(), "TailwindStyles.g.cs.txt"), actual);
            return;
        }
        string file = Path.Combine(AppContext.BaseDirectory, "Snapshots", "TailwindStyles.g.cs.txt");
        Assert.True(File.Exists(file), $"snapshot {file} missing; run with AWAUI_UPDATE_SNAPSHOTS=1");
        Assert.Equal(File.ReadAllText(file).Replace("\r\n", "\n"), actual);
    }

    static string SourceSnapshotDir([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Snapshots");

    [Fact]
    public void Module_initializer_registers_the_stylesheet_by_default()
    {
        var r = new GeneratorHarness { AssemblyName = "My.App" }
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss/utilities.css\";")
            .Awaui("A.awaui", """<div class="flex"></div>""")
            .Run();
        r.AssertNoErrors();
        string code = Styles(r);
        Assert.Contains("[global::System.Runtime.CompilerServices.ModuleInitializer]\n        public static void Register() => global::AwAUI.UI.AppStyleSheets.Register(Css, SourceName);", code);
        Assert.Contains("public const string SourceName = \"tailwind:My.App/Styles/app.css\";", code);
        Assert.Contains("namespace TestApp", code);
        // net10 自带 ModuleInitializerAttribute：不生成内部的同名特性
        Assert.DoesNotContain(r.Run.GeneratedTrees, t => t.FilePath.Contains("ModuleInitializerAttribute", StringComparison.Ordinal));
    }

    [Fact]
    public void Below_csharp_9_the_application_registers_manually()
    {
        var h = new GeneratorHarness { LanguageVersion = LanguageVersion.CSharp8 }
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss/utilities.css\";")
            .CSharp("A.cs", "static class A { const string C = \"flex\"; }");
        var r = h.Run();
        r.AssertNoErrors();
        string code = Styles(r);
        Assert.DoesNotContain("ModuleInitializer]", code);
        Assert.Contains("public static void Register()", code);
    }

    [Fact]
    public void Without_a_stylesheet_nothing_is_generated_or_scanned()
    {
        var h = new GeneratorHarness().Awaui("A.awaui", """<div class="p-4 bg-nope-17"></div>""");
        var driver = h.CreateDriver(trackSteps: true).RunGenerators(h.CreateCompilation());
        var run = driver.GetRunResult();
        Assert.DoesNotContain(run.GeneratedTrees, t => t.FilePath.Contains("TailwindStyles", StringComparison.Ordinal));
        Assert.Empty(run.Diagnostics);
        // 扫描步骤照常运行（结果为空），编译步骤没有任何输入
        Assert.Single(run.Results[0].TrackedSteps[Steps.MarkupScan]);
        Assert.False(run.Results[0].TrackedSteps.ContainsKey(Steps.Build) && run.Results[0].TrackedSteps[Steps.Build].Length > 0);
    }

    [Fact]
    public void Stylesheet_errors_are_reported_as_AW5001_on_the_entry()
    {
        var r = Harness()
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss\";\n@import \"./missing.css\";")
            .Awaui("A.awaui", """<div class="p-4 bg-nope"></div>""")
            .Run();
        var d = Assert.Single(Diags(r, "AW5001"));
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal(GeneratorHarness.ProjectDir + "Styles/app.css", d.Location.GetLineSpan().Path);
        Assert.Contains("Styles/app.css", d.GetMessage());
        Assert.Contains("missing.css", d.GetMessage());
        Assert.DoesNotContain(r.Run.GeneratedTrees, t => t.FilePath.Contains("TailwindStyles", StringComparison.Ordinal));
        // 入口有错时不逐个报告类名
        Assert.Empty(Diags(r, "AW5002"));

        foreach (var css in new[] { "@plugin \"x\";", "@import \"tailwindcss\";\n@utility { }", "@import \"tailwindcss\";\n@theme { --spacing-a: theme(--spacing-b); --spacing-b: theme(--spacing-a); }\n.x { width: theme(--spacing-a); }" })
        {
            var bad = Harness().TailwindStylesheet("app.css", css).Awaui("A.awaui", "<div class=\"p-4\"></div>").Run();
            Assert.Single(Diags(bad, "AW5001"));
        }
    }

    [Fact]
    public void Local_imports_resolve_through_additional_files()
    {
        var r = Harness()
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss/utilities.css\";\n@import \"./tokens.css\";\n@import \"../Shared/brand\";")
            .AdditionalFile("Styles/tokens.css", "@theme { --color-ink: #010203; }", ("AwAUITailwind", "Import"))
            .AdditionalFile("Shared/brand.css", "@utility brand-box { padding: 7px; }")
            .Awaui("A.awaui", """<div class="text-ink brand-box"></div>""")
            .Run();
        r.AssertNoErrors();
        string code = Styles(r);
        Assert.Contains("--color-ink: #010203", code);
        Assert.Contains(".brand-box {\n  padding: 7px;\n}", code);
        // 导入的文件本身不是入口
        Assert.Single(r.Run.GeneratedTrees, t => t.FilePath.Contains("TailwindStyles", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_literal_classes_in_class_attributes_are_reported_as_AW5002()
    {
        const string markup = """
            <div class="p-4 bg-primry card todo-item text-muted hover:fooo [&_svg]:size-4 p-@(N) @(N)-x"
                 data-x="bg-nope2">
                <span class="mt-2 -mx-nope!"></span>
            </div>
            <style>
            .text-muted { color: gray; }
            </style>
            @code { public int N { get; set; } string s = "bg-nope3"; }
            """;
        var r = Harness()
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss\";\n.app-shell { display: flex; }")
            .Awaui("Pages/Home.awaui", markup)
            .Awaui("Other.awaui", """<div class="app-shell p-nine"></div>""")
            .Run();
        r.AssertNoErrors();
        string Token(Diagnostic d) =>
            d.Location.GetLineSpan().Path.EndsWith("Home.awaui", StringComparison.Ordinal)
                ? markup.Substring(d.Location.SourceSpan.Start, d.Location.SourceSpan.Length)
                : "Other:" + d.Location.SourceSpan.Start;
        var diags = Diags(r, "AW5002").OrderBy(d => d.Location.GetLineSpan().Path, StringComparer.Ordinal).ThenBy(d => d.Location.SourceSpan.Start).ToList();
        Assert.Equal(new[] { "Other:22", "bg-primry", "hover:fooo", "-mx-nope!" }, diags.Select(Token).ToArray());
        var first = diags[1];
        Assert.Equal(DiagnosticSeverity.Warning, first.Severity);
        Assert.Equal(GeneratorHarness.ProjectDir + "Pages/Home.awaui", first.Location.GetLineSpan().Path);
        Assert.Equal(new LinePosition(0, markup.IndexOf("bg-primry", StringComparison.Ordinal)), first.Location.GetLineSpan().StartLinePosition);
        Assert.Equal(new LinePosition(2, 22), diags[3].Location.GetLineSpan().StartLinePosition);
        Assert.Contains("bg-primry", first.GetMessage());
        Assert.Contains("Styles/app.css", first.GetMessage());
    }

    [Fact]
    public void AW5002_only_checks_files_against_the_entries_whose_scope_contains_them()
    {
        // app.css 不管 Legacy/ 与 Admin/；admin.css 只管 Admin/ 且定义了 brand 颜色
        var r = Harness()
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss\";\n@source not \"../Legacy\";\n@source not \"../Admin\";")
            .TailwindStylesheet("Styles/admin.css", "@import \"tailwindcss\" source(none);\n@source \"../Admin\";\n@theme { --color-brand: #102030; }")
            .Awaui("Pages/P.awaui", """<div class="bg-primry bg-brand"></div>""")
            .Awaui("Legacy/L.awaui", """<div class="bg-primry bg-legacy2"></div>""")
            .Awaui("Admin/X.awaui", """<div class="bg-brand bg-admin3"></div>""")
            .Run();
        r.AssertNoErrors();
        var reported = Diags(r, "AW5002")
            .Select(d => Path.GetFileName(d.Location.GetLineSpan().Path) + ":" + d.GetMessage().Split('\'')[1] + ":" + d.GetMessage().Split(' ').Last(p => p.EndsWith(".css", StringComparison.Ordinal)))
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();
        // P 只受 app.css 管：bg-brand 在 app.css 中无法生成（只有 admin.css 定义了它），照样报告；
        // L 不在任何入口的范围内，不报告；X 只受 admin.css 管，bg-brand 有效，只报 bg-admin3
        Assert.Equal(new[] { "P.awaui:bg-brand:Styles/app.css", "P.awaui:bg-primry:Styles/app.css", "X.awaui:bg-admin3:Styles/admin.css" }, reported);
    }

    [Fact]
    public void Tailwind_syntax_in_class_attributes_passes_through_unchanged()
    {
        const string classes = "[&_svg]:size-4 has-[>svg]:px-3 data-[state=open]:animate-in w-1/2 !p-4 hover:bg-primary/90 " +
                               "[&_svg:not([class*='size-'])]:size-4 aria-invalid:border-destructive *:data-[slot=icon]:size-3 group-data-[size=sm]/card:gap-2";
        var r = Harness()
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss\";\n@import \"tw-animate-css\";\n@theme inline { --color-primary: #111111; --color-destructive: #ee0000; }")
            .Awaui("Button.awaui", $"""
                <div class="{classes} @@container/card @@md:flex" data-slot="button" data-state="open" aria-expanded="true">x</div>
                """)
            .Run();
        r.AssertNoErrors();
        Assert.Empty(Diags(r, "AW5002"));
        string component = r.Generated("Button.awaui");
        Assert.Contains($"Class = \"{classes.Replace("\"", "\\\"")} @container/card @md:flex\"", component);
        Assert.Contains(".Attributes[\"data-slot\"] = \"button\";", component);
        Assert.Contains(".Attributes[\"aria-expanded\"] = \"true\";", component);
        string css = Styles(r);
        foreach (var selector in new[]
                 {
                     ".\\[\\&_svg\\]\\:size-4 svg", ".has-\\[\\>svg\\]\\:px-3:has( > svg)", ".data-\\[state\\=open\\]\\:animate-in[data-state=\"open\"]",
                     ".w-1\\/2", ".\\!p-4", ".hover\\:bg-primary\\/90:hover", ".\\@container\\/card", "@keyframes enter",
                 })
            Assert.Contains(selector.Replace("\"", "\"\""), css);
        Assert.Contains("@container (width >= 28rem)", css); // @md:flex（容器尺寸 --container-md）
    }

    [Fact]
    public void Source_directives_and_source_none_limit_the_scanned_files()
    {
        var r = Harness()
            .TailwindStylesheet("Styles/app.css", """
                @import "tailwindcss" source(none);
                @source "../Components";
                @source "../Extra/*.txt";
                @source not "../Components/Legacy/**";
                """)
            .Awaui("Components/A.awaui", """<div class="p-1"></div>""")
            .Awaui("Components/Legacy/B.awaui", """<div class="p-2"></div>""")
            .Awaui("Pages/C.awaui", """<div class="p-3"></div>""")
            .CSharp("Components/D.cs", "static class D { const string C = \"p-4\"; }")
            .CSharp("E.cs", "static class E { const string C = \"p-5\"; }")
            .AdditionalFile("Extra/notes.txt", "class: p-6", ("AwAUITailwind", "Source"))
            .Run();
        r.AssertNoErrors();
        string css = Styles(r);
        Assert.Contains(".p-1 {", css);
        Assert.Contains(".p-4 {", css);
        Assert.Contains(".p-6 {", css);
        Assert.DoesNotContain(".p-2 {", css);
        Assert.DoesNotContain(".p-3 {", css);
        Assert.DoesNotContain(".p-5 {", css);

        // 自动检测（默认）：项目内的 .awaui 与 C#，bin/obj 除外
        var auto = Harness()
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss\";\n@source not \"../Pages\";")
            .Awaui("Components/A.awaui", """<div class="p-1"></div>""")
            .Awaui("Pages/C.awaui", """<div class="p-3"></div>""")
            .CSharp("E.cs", "static class E { const string C = \"p-5\"; }")
            .CSharp("obj/Debug/X.cs", "static class X { const string C = \"p-7\"; }")
            .Run();
        string autoCss = Styles(auto);
        Assert.Contains(".p-1 {", autoCss);
        Assert.Contains(".p-5 {", autoCss);
        Assert.DoesNotContain(".p-3 {", autoCss);
        Assert.DoesNotContain(".p-7 {", autoCss);
    }

    [Fact]
    public void Hostile_source_globs_and_markup_stay_bounded()
    {
        // 样式表与标记都是不可信输入：大量花括号展开的 glob、很长的模式、深层的 ** 不应让匹配的代价失控
        var css = new System.Text.StringBuilder("@import \"tailwindcss\" source(none);\n");
        for (int i = 0; i < 200; i++)
            css.Append("@source \"../").Append(string.Concat(Enumerable.Repeat("{a,b}", 8))).Append(string.Concat(Enumerable.Repeat("**/*", 60))).Append(i).Append(".awaui\";\n");
        css.Append("@source \"../Real/**/*.awaui\";\n");
        var h = Harness().TailwindStylesheet("Styles/app.css", css.ToString());
        for (int i = 0; i < 300; i++)
            h.Awaui($"Deep/{string.Concat(Enumerable.Repeat("x/", 40))}F{i}.awaui", $"<div class=\"p-{i % 10} {new string('a', 3000)}\"></div>");
        h.Awaui("Real/A.awaui", "<div class=\"m-3\"></div>");
        double start = ThreadCpuClock.NowMs();
        var r = h.Run();
        double cpu = ThreadCpuClock.NowMs() - start;
        r.AssertNoErrors();
        Assert.Contains(".m-3 {", Styles(r));
        Assert.True(cpu < 60_000, $"cpu {cpu} ms");
    }

    [Fact]
    public void Multiple_entries_get_distinct_class_names()
    {
        var r = Harness()
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss/utilities.css\";")
            .TailwindStylesheet("Styles/admin.css", "@import \"tailwindcss/utilities.css\";")
            .TailwindStylesheet("Styles/print.css", "@import \"tailwindcss/utilities.css\";", className: "PrintStyles")
            .Awaui("A.awaui", """<div class="flex"></div>""")
            .Run();
        r.AssertNoErrors();
        var names = r.Run.GeneratedTrees.Where(t => t.FilePath.Contains("TailwindStyles.", StringComparison.Ordinal))
            .Select(t => t.GetText().ToString()).Select(s => s.Substring(s.IndexOf("internal static partial class ", StringComparison.Ordinal) + 30).Split('\n')[0]).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "PrintStyles", "TailwindStyles_admin", "TailwindStyles_app" }, names);
    }

    [Fact]
    public void Entries_with_the_same_file_name_inside_and_outside_the_project_get_distinct_outputs()
    {
        // 项目外的入口（链接进来的共享样式表）没有项目内的相对路径：只取文件名会与项目根下的同名入口相撞，
        // 提示名重复让 AddSource 抛异常（整个生成器失败，组件也不再生成），来源名重复让 AppStyleSheets 丢掉第二张
        var r = Harness()
            .TailwindStylesheet("app.css", "@import \"tailwindcss/utilities.css\";")
            .TailwindStylesheet("../Shared/app.css", "@import \"tailwindcss/utilities.css\";")
            .Awaui("A.awaui", """<div class="flex"></div>""")
            .Run();
        r.AssertNoErrors();
        Assert.DoesNotContain(r.Run.Results, x => x.Exception is not null);
        var sources = r.Run.GeneratedTrees.Where(t => t.FilePath.Contains("TailwindStyles.", StringComparison.Ordinal))
            .Select(t => t.GetText().ToString())
            .Select(s => s.Substring(s.IndexOf("SourceName = \"", StringComparison.Ordinal) + 14).Split('"')[0])
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, sources.Length);
        Assert.Equal(sources.Length, sources.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(r.Run.GeneratedTrees, t => t.FilePath.Contains("A.awaui", StringComparison.Ordinal));
    }

    [Fact]
    public void Classes_defined_in_global_component_styles_are_not_reported_as_AW5002()
    {
        // App.awaui 的 <style global> 定义了 text-muted / bg-surface-hover（前缀像工具名）：在其他组件里使用它们不是 Tailwind 的错误
        var r = Harness()
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss\";")
            .Awaui("App.awaui", """
                <div class="text-muted"></div>
                <style global>
                .text-muted { color: gray; }
                .panel > .bg-surface-hover:hover { color: red; }
                </style>
                """)
            .Awaui("Card.awaui", """<div class="text-muted bg-surface-hover bg-primry"></div>""")
            .Run();
        r.AssertNoErrors();
        var reported = Diags(r, "AW5002").Select(d => d.GetMessage().Split('\'')[1]).ToArray();
        Assert.Equal(new[] { "bg-primry" }, reported);
    }

    [Fact]
    public void Output_is_independent_of_the_order_of_input_files()
    {
        var files = new (string Path, string Text)[]
        {
            ("A.awaui", """<div class="p-4 hover:bg-red-500 bg-primry"></div>"""),
            ("B/B.awaui", """<div class="m-2 [&_svg]:size-4 text-nope3"></div>"""),
            ("C.awaui", """<div class="grid grid-cols-3 data-[state=open]:p-6"></div>"""),
        };
        (string Css, string Diags) RunWith(IEnumerable<(string Path, string Text)> order, bool reverseCSharp)
        {
            var h = new GeneratorHarness { AssemblyName = "OrderApp" }.Property("AwAUITailwindRegister", "false")
                .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss\";");
            foreach (var (path, text) in order) h.Awaui(path, text);
            var cs = new[] { ("X.cs", "static class X { const string A = \"flex gap-2\"; }"), ("Y.cs", "static class Y { const string A = \"rounded-lg\"; }") };
            foreach (var (path, text) in reverseCSharp ? Enumerable.Reverse(cs) : cs) h.CSharp(path, text);
            var r = h.Run();
            var diags = string.Join("\n", Diags(r, "AW5002").Select(d => d.Location.GetLineSpan() + " " + d.GetMessage()).OrderBy(x => x, StringComparer.Ordinal));
            return (Styles(r), diags);
        }
        var forward = RunWith(files, false);
        var backward = RunWith(Enumerable.Reverse(files), true);
        Assert.Equal(forward.Css, backward.Css);
        Assert.Equal(forward.Diags, backward.Diags);
        Assert.Contains("bg-primry", forward.Diags);
        Assert.Contains("text-nope3", forward.Diags);
    }

    // ------------------------------------------------------------ 增量

    static IncrementalStepRunReason[] Reasons(GeneratorDriver driver, string step) =>
        driver.GetRunResult().Results[0].TrackedSteps.TryGetValue(step, out var steps)
            ? steps.SelectMany(s => s.Outputs).Select(o => o.Reason).ToArray()
            : Array.Empty<IncrementalStepRunReason>();

    static bool Reused(IncrementalStepRunReason r) => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged;

    [Fact]
    public void Editing_one_file_only_rescans_that_file_and_rebuilds_only_when_the_candidate_set_changes()
    {
        var h = Harness()
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss\";")
            .Awaui("A.awaui", """<div class="p-4">a</div>""")
            .Awaui("B.awaui", """<div class="m-2">b</div>""")
            .CSharp("C.cs", "static class C { const string X = \"flex\"; static int F() => 1; }");
        var compilation = h.CreateCompilation();
        var driver = h.CreateDriver(trackSteps: true).RunGenerators(compilation);
        Assert.Equal(new[] { IncrementalStepRunReason.New }, Reasons(driver, Steps.Build));

        // 1. 改 A.awaui 的文本但不改类名：只重新扫描 A；候选集合不变，编译整步命中缓存
        var a = h.AdditionalTexts.Single(t => t.Path.EndsWith("A.awaui", StringComparison.Ordinal));
        var a2 = new GeneratorHarness.InMemoryAdditionalText(a.Path, """<div class="p-4">changed text, same classes</div>""");
        driver = driver.ReplaceAdditionalText(a, a2).RunGenerators(compilation);
        var scans = Reasons(driver, Steps.MarkupScan);
        Assert.Single(scans, r => r == IncrementalStepRunReason.Modified || r == IncrementalStepRunReason.New);
        Assert.Single(scans, Reused);
        Assert.All(Reasons(driver, Steps.Candidates), r => Assert.True(Reused(r), r.ToString()));
        Assert.All(Reasons(driver, Steps.Build), r => Assert.True(Reused(r), r.ToString()));
        Assert.All(Reasons(driver, Steps.Entry), r => Assert.True(Reused(r), r.ToString()));

        // 2. 改 C# 文件但不改字符串：C# 扫描结果不变，编译命中缓存
        var tree = compilation.SyntaxTrees.Single(t => t.FilePath.EndsWith("C.cs", StringComparison.Ordinal));
        var compilation2 = compilation.ReplaceSyntaxTree(tree, tree.WithChangedText(SourceText.From("static class C { const string X = \"flex\"; static int F() => 2; }")));
        driver = driver.RunGenerators(compilation2);
        Assert.All(Reasons(driver, Steps.CSharpScan), r => Assert.True(Reused(r), r.ToString()));
        Assert.All(Reasons(driver, Steps.Build), r => Assert.True(Reused(r), r.ToString()));

        // 3. 加入新类名：重新编译，输出包含它
        var a3 = new GeneratorHarness.InMemoryAdditionalText(a.Path, """<div class="p-4 underline">changed</div>""");
        driver = driver.ReplaceAdditionalText(a2, a3).RunGenerators(compilation2);
        Assert.Equal(new[] { IncrementalStepRunReason.Modified }, Reasons(driver, Steps.Build));
        Assert.Contains(".underline {", driver.GetRunResult().GeneratedTrees.Single(t => t.FilePath.Contains("TailwindStyles.", StringComparison.Ordinal)).GetText().ToString());
    }

    // ------------------------------------------------------------ 运行时

    [Fact]
    public void Generated_css_cascades_to_tailwind_classes_through_AwAUI_Css()
    {
        var r = Harness()
            .TailwindStylesheet("Styles/app.css", "@import \"tailwindcss\";\n@theme { --color-primary: #3366ff; --color-primary-foreground: #fafafa; }")
            .Awaui("Panel.awaui", """
                <div id="box" class="p-4 bg-primary text-primary-foreground rounded-md">
                    <span id="label" class="text-sm font-medium">Hi</span>
                </div>
                """)
            .Run();
        using var loaded = r.Load();
        string css = (string)loaded.Type("TestApp.TailwindStyles").GetField("Css")!.GetRawConstantValue()!;
        var pipeline = new RenderPipeline(new TestTextEngine()) { IncludeAppStyleSheets = false };
        var sheet = StyleSheet.Parse(css, "tailwind");
        pipeline.AddStyleSheet(sheet);
        pipeline.SetViewport(new Size(400, 300), 1);
        var root = new UIRoot { Pipeline = pipeline };
        root.Mount(loaded.Create("TestApp.Panel"));
        root.RunFrame();
        var box = root.RootElement.FindAll(e => e.Id == "box").Single().ComputedStyle!;
        Assert.Equal(16f, box.PaddingTop.Resolve(0));
        Assert.Equal(16f, box.PaddingLeft.Resolve(0));
        Assert.Equal(Color.Parse("#3366ff"), box.ResolveColor(box.BackgroundColor));
        var label = root.RootElement.FindAll(e => e.Id == "label").Single().ComputedStyle!;
        Assert.Equal(Color.Parse("#fafafa"), label.ResolveColor(label.Color));
        Assert.Equal(14f, label.FontSize);
    }
}
