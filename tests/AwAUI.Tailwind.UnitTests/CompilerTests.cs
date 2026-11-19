using System.Diagnostics;
using System.Text;
using AwAUI.Tests.Shared;
using Xunit.Abstractions;

namespace AwAUI.Tailwind.UnitTests;

public class CompilerApiTests
{
    [Fact]
    public void Build_accumulates_candidates_like_tailwind()
    {
        var compiler = TailwindCompiler.CreateDefault();
        string first = compiler.Build(new[] { "p-4" });
        Assert.Contains(".p-4", first);
        string second = compiler.Build(new[] { "m-2" });
        Assert.Contains(".p-4", second);
        Assert.Contains(".m-2", second);
        Assert.Equal(new[] { "p-4", "m-2" }, compiler.Candidates);
    }

    [Fact]
    public void Incremental_add_returns_only_new_rules_and_dependencies()
    {
        var compiler = TailwindCompiler.CreateDefault();
        compiler.Build(new[] { "p-4", "shadow-xs" });
        var delta = compiler.AddCandidates(new[] { "p-4", "bg-red-500", "ring-2", "hover:bg-blue-500/50", "not-a-class", "animate-spin" });
        Assert.Equal(new[] { "bg-red-500", "hover:bg-blue-500/50", "ring-2", "animate-spin" }.OrderBy(x => x), delta.Rules.Select(r => r.Candidate).OrderBy(x => x));
        // 新主题变量只含首次用到的
        Assert.Contains("--color-red-500", delta.ThemeCss);
        Assert.Contains("--color-blue-500", delta.ThemeCss);
        Assert.DoesNotContain("--spacing", delta.ThemeCss);
        // ring 用到的 @property 已经随 shadow-xs 输出过
        Assert.DoesNotContain("--tw-ring-shadow", delta.GlobalCss);
        Assert.Contains("@keyframes spin", delta.GlobalCss);
        // 插入位置是在完整排序中的下标
        var full = compiler.CandidatesToCss(compiler.Candidates);
        Assert.All(delta.Rules, r => Assert.InRange(r.Index, 0, compiler.Candidates.Count + 1));
        Assert.True(compiler.AddCandidates(new[] { "bg-red-500" }).IsEmpty);
        Assert.NotNull(full);
    }

    [Fact]
    public void Validity_queries()
    {
        var compiler = TailwindCompiler.CreateDefault();
        Assert.True(compiler.IsValidCandidate("data-[state=open]:animate-in") == false);
        Assert.True(compiler.IsValidCandidate("group-hover/item:flex"));
        Assert.False(compiler.IsValidCandidate("bg-not-a-color"));
        Assert.Null(compiler.CandidateToCss("foo"));
    }

    [Fact]
    public void AwAUI_preflight_is_used_by_default()
    {
        string css = TailwindCompiler.CreateDefault().Build(Array.Empty<string>());
        Assert.Contains("@layer base", css);
        Assert.DoesNotContain("::file-selector-button", css);
        Assert.DoesNotContain("-webkit-", css);
        string original = TailwindCompiler.CreateDefault(new TailwindOptions { Preflight = PreflightKind.Tailwind }).Build(Array.Empty<string>());
        Assert.Contains("::file-selector-button", original);
    }

    [Fact]
    public void Theme_root_selector_is_configurable()
    {
        var compiler = TailwindCompiler.CreateDefault(new TailwindOptions { ThemeRootSelector = ":root" });
        string css = compiler.Build(new[] { "bg-red-500" });
        Assert.Contains("@layer theme {\n  :root {\n", css);
        Assert.Contains("--color-red-500:", css);
        Assert.DoesNotContain(":host", css);
        Assert.StartsWith(":root {", compiler.AddCandidates(new[] { "text-blue-500" }).ThemeCss);
    }

    [Fact]
    public void Minified_output()
    {
        var compiler = TailwindCompiler.Create("@theme inline { --spacing: 0.25rem; }\n@import \"tailwindcss/utilities.css\";", new TailwindOptions { Minify = true });
        Assert.Equal(".p-4{padding:calc(0.25rem * 4)}", compiler.Build(new[] { "p-4" }).Trim());
        // 没有 --spacing 主题变量时 p-4 不生成（与 Tailwind 相同）
        Assert.Null(TailwindCompiler.Create("@import \"tailwindcss/utilities.css\";").CandidateToCss("p-4"));
    }

    [Fact]
    public void Sources_and_inline_candidates()
    {
        var compiler = TailwindCompiler.Create("@import \"tailwindcss\";\n@source \"../Components/**/*.awaui\";\n@source not \"./legacy\";\n@source inline(\"p-{1..3} {hover:,}underline\");");
        Assert.Equal(2, compiler.Sources.Count);
        Assert.True(compiler.Sources[1].Negated);
        Assert.Contains("p-2", compiler.Candidates);
        Assert.Contains("hover:underline", compiler.Candidates);
        Assert.Contains(".p-3", compiler.Build(Array.Empty<string>()));
    }

    [Fact]
    public void Concurrent_use_is_safe()
    {
        var compiler = TailwindCompiler.CreateDefault();
        var candidates = Enumerable.Range(0, 200).Select(i => $"p-{i % 40} m-{i % 30} w-{i % 50}".Split(' ')).SelectMany(x => x).ToArray();
        Parallel.For(0, 8, t =>
        {
            for (int i = t; i < candidates.Length; i += 8)
            {
                compiler.AddCandidates(new[] { candidates[i] });
                compiler.CandidateToCss(candidates[i]);
            }
        });
        string css = compiler.Build(Array.Empty<string>());
        foreach (var c in candidates.Distinct()) Assert.Contains("." + c.Replace(".", "\\.") + " {", css);
    }

    [Theory]
    [InlineData("@utility foo { @apply foo; }", "circular dependency")]
    [InlineData("@import \"tailwindcss\";\n@utility a { @apply b; }\n@utility b { @apply a; }", "circular dependency")]
    [InlineData("@import \"tailwindcss\";\n.x { @apply does-not-exist; }", "Cannot apply unknown utility class `does-not-exist`")]
    [InlineData("@import \"tailwindcss\";\n.x { @apply nope:flex; }", "the `nope` variant does not exist")]
    [InlineData("@utility Foo { color: red; }", "invalid utility name")]
    [InlineData("@custom-variant foo;", "has no selector or body")]
    [InlineData("@custom-variant a { @variant b { @slot; } }\n@custom-variant b { @variant a { @slot; } }", "Circular dependency detected in custom variants")]
    [InlineData("@plugin \"x\";", "not supported")]
    [InlineData("@import \"missing.css\";", "Could not resolve")]
    [InlineData("@theme { color: red; }", "must only contain custom properties")]
    [InlineData(".a { color: red;", "Missing closing }")]
    public void Invalid_input_reports_tailwind_errors(string css, string message)
    {
        var ex = Assert.Throws<TailwindException>(() => TailwindCompiler.Create(css));
        Assert.Contains(message, ex.Message);
    }
}

public class HostileInputTests
{
    static readonly TailwindCompiler s_compiler = TailwindCompiler.CreateDefault();

    [Fact]
    public void Overlong_candidates_are_ignored()
    {
        Assert.Null(s_compiler.CandidateToCss("w-[" + new string('1', 5000) + "px]"));
        Assert.Null(s_compiler.CandidateToCss(string.Concat(Enumerable.Repeat("hover:", 40)) + "flex"));
    }

    [Fact]
    public void Deep_compound_variants_are_bounded()
    {
        string deep = string.Concat(Enumerable.Repeat("not-", 200)) + "hover:flex";
        Assert.Null(s_compiler.CandidateToCss(deep));
        string groups = string.Concat(Enumerable.Repeat("group-has-", 100)) + "hover:flex";
        Assert.Null(s_compiler.CandidateToCss(groups));
    }

    [Fact]
    public void Deep_arbitrary_values_are_bounded()
    {
        Assert.Null(s_compiler.CandidateToCss("w-[" + new string('(', 400) + "1px" + new string(')', 400) + "]"));
        Assert.Null(s_compiler.CandidateToCss("[" + new string('[', 300) + "]:flex"));
        Assert.NotNull(s_compiler.CandidateToCss("w-[calc(((1px)))]"));
    }

    [Fact]
    public void Deeply_nested_css_input_is_rejected()
    {
        string css = string.Concat(Enumerable.Repeat(".a {", 5000)) + string.Concat(Enumerable.Repeat("}", 5000));
        Assert.Throws<TailwindException>(() => TailwindCompiler.Create(css));
        string value = ".a { width: " + new string('(', 5000) + new string(')', 5000) + "; }";
        Assert.Throws<TailwindException>(() => TailwindCompiler.Create(value));
    }

    [Fact]
    public void Import_cycles_and_brace_explosions_are_bounded()
    {
        var resolver = new LoopResolver();
        Assert.Throws<TailwindException>(() => TailwindCompiler.Create("@import \"loop.css\";", new TailwindOptions { Resolver = resolver }));
        Assert.Throws<TailwindException>(() => TailwindCompiler.Create("@source inline(\"p-{0..100000}\");"));
        Assert.Throws<TailwindException>(() => TailwindCompiler.Create("@source inline(\"{a,b}{a,b}{a,b}{a,b}{a,b}{a,b}{a,b}{a,b}{a,b}{a,b}{a,b}{a,b}{a,b}{a,b}\");"));
    }

    sealed class LoopResolver : StylesheetResolver
    {
        public override ResolvedStylesheet? Resolve(string id, string? baseDirectory) => new("@import \"loop.css\";");
    }

    [Fact]
    public void Cache_trimming_keeps_results_correct()
    {
        var compiler = TailwindCompiler.CreateDefault();
        var candidates = new[] { "md:p-4", "p-2", "hover:bg-red-500", "data-[x=1]:flex" };
        string before = compiler.Build(candidates);
        for (int i = 0; i < 120_000; i++)
        {
            compiler.IsValidCandidate("data-[k" + i + "]:x-" + i);
        }
        Assert.Equal(before, compiler.Build(Array.Empty<string>()));
        Assert.True(compiler.IsValidCandidate("data-[x=2]:flex"));
    }

    [Fact]
    public void Long_apply_chains_are_handled()
    {
        var sb = new StringBuilder("@import \"tailwindcss\";\n@utility u0 { color: red; }\n");
        for (int i = 1; i < 300; i++) sb.Append("@utility u").Append(i).Append(" { @apply u").Append(i - 1).Append("; }\n");
        var compiler = TailwindCompiler.Create(sb.ToString());
        Assert.Contains("color: red", compiler.CandidateToCss("u299"));
    }

    [Fact]
    public void Garbage_candidates_compile_quickly()
    {
        var rng = new Random(7);
        const string alphabet = "abcdefghijklmnopqrstuvwxyz-:/[]()!@*&_.#%=,'\"0123456789";
        var garbage = new List<string>();
        for (int i = 0; i < 5000; i++)
        {
            var len = rng.Next(1, 80);
            var chars = new char[len];
            for (int j = 0; j < len; j++) chars[j] = alphabet[rng.Next(alphabet.Length)];
            garbage.Add(new string(chars));
        }
        double start = ThreadCpuClock.NowMs();
        var compiler = TailwindCompiler.CreateDefault();
        compiler.Build(garbage);
        Assert.True(ThreadCpuClock.NowMs() - start < 30_000);
    }
}

public class PerformanceTests
{
    readonly ITestOutputHelper _output;

    public PerformanceTests(ITestOutputHelper output) => _output = output;

    /// <summary>3000 个不同的类名（工具 × 变体组合）。</summary>
    internal static List<string> Make3000Candidates()
    {
        var bases = TestFiles.ReadReference("default").Where(r => r.Value != "(none)" && !r.Key.Contains(':')).Select(r => r.Key).ToList();
        bases.AddRange(TestFiles.ReadReference("shadcn").Select(r => r.Key).Where(k => !k.Contains(':')));
        bases = bases.Distinct().ToList();
        var variants = new[] { "", "hover:", "md:", "dark:", "focus-visible:", "data-[state=open]:", "group-hover:", "lg:hover:", "aria-invalid:", "[&_svg]:" };
        var result = new List<string>();
        foreach (var v in variants)
        {
            foreach (var b in bases)
            {
                result.Add(v + b);
                if (result.Count == 3000) return result;
            }
        }
        return result;
    }

    [Fact]
    public void Compile_3000_candidates()
    {
        var candidates = Make3000Candidates();
        Assert.Equal(3000, candidates.Count);
        var create = Stopwatch.StartNew();
        double cpu0 = ThreadCpuClock.NowMs();
        var compiler = TailwindCompiler.CreateDefault();
        double cpuCreate = ThreadCpuClock.NowMs() - cpu0;
        create.Stop();
        double cpu1 = ThreadCpuClock.NowMs();
        var wall = Stopwatch.StartNew();
        string css = compiler.Build(candidates);
        wall.Stop();
        double cpuBuild = ThreadCpuClock.NowMs() - cpu1;
        double cpu2 = ThreadCpuClock.NowMs();
        string again = compiler.Build(candidates);
        double cpuRebuild = ThreadCpuClock.NowMs() - cpu2;
        Assert.Equal(css, again);
        _output.WriteLine($"create: {cpuCreate:F1} ms CPU ({create.Elapsed.TotalMilliseconds:F1} ms wall); build 3000: {cpuBuild:F1} ms CPU ({wall.Elapsed.TotalMilliseconds:F1} ms wall); cached rebuild: {cpuRebuild:F1} ms CPU; output {css.Length / 1024} KiB");
        // 宽松上限：机器负载高时也不应超过（优化构建下实测见设计说明）
        Assert.True(cpuBuild < 20_000);
    }

    [Fact]
    public void Cn_throughput()
    {
        var lines = TestFiles.Read("merge", "cases.txt").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\t')[0]).ToArray();
        var merger = new TailwindMerger(new TailwindMergeOptions { CacheSize = 0 });
        double start = ThreadCpuClock.NowMs();
        int n = 0;
        for (int round = 0; round < 5; round++)
        {
            foreach (var l in lines)
            {
                merger.Merge(l);
                n++;
            }
        }
        double ms = ThreadCpuClock.NowMs() - start;
        _output.WriteLine($"Cn uncached: {n} merges in {ms:F1} ms CPU ({ms * 1000 / n:F1} µs each)");
        Assert.True(ms < 20_000);
    }
}
