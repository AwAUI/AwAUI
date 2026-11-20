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
    public void Incremental_rule_indices_reproduce_build_order()
    {
        // 运行时 JIT 的用法：Build([]) 之后逐批 AddCandidates，按 Index 插入得到的规则顺序与一次性 Build 相同
        var batches = new[]
        {
            new[] { "p-4", "hover:bg-red-500", "md:flex", "text-sm" },
            new[] { "hover:bg-blue-500", "p-2", "md:grid", "px-3", "text-lg/7" },
            new[] { "hover:underline", "m-1", "lg:p-8", "dark:md:hover:bg-red-500", "focus:outline-none" },
        };
        var compiler = TailwindCompiler.CreateDefault();
        compiler.Build(Array.Empty<string>());
        var rules = new List<TailwindGeneratedRule>();
        foreach (var batch in batches)
        {
            foreach (var r in compiler.AddCandidates(batch).Rules) rules.Insert(r.Index, r);
        }
        string full = TailwindCompiler.CreateDefault().Build(batches.SelectMany(b => b));
        var positions = rules.Select(r =>
        {
            var m = System.Text.RegularExpressions.Regex.Match(full, System.Text.RegularExpressions.Regex.Escape("." + r.Candidate.Replace(":", "\\:").Replace("/", "\\/")) + @"(?![\w\\-])");
            return m.Success ? m.Index : -1;
        }).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.Equal(batches.Sum(b => b.Length), rules.Count);
    }

    [Fact]
    public void Incremental_add_emits_keyframes_of_inline_theme_animations()
    {
        // tw-animate-css 与 shadcn 的动画定义在 @theme inline 中：工具类直接内联 animation 值，不引用 --animate-* 变量
        const string css = "@import \"tailwindcss\";\n@import \"tw-animate-css\";\n@theme inline { --animate-wiggle: wiggle 1s ease-in-out infinite; @keyframes wiggle { 50% { rotate: 3deg; } } }";
        var candidates = new[] { "animate-wiggle", "data-[state=open]:animate-in" };
        string full = TailwindCompiler.Create(css).Build(candidates);
        Assert.Contains("@keyframes wiggle", full);
        Assert.Contains("@keyframes enter", full);
        var compiler = TailwindCompiler.Create(css);
        Assert.DoesNotContain("@keyframes wiggle", compiler.Build(Array.Empty<string>()));
        var delta = compiler.AddCandidates(candidates);
        Assert.Contains("@keyframes wiggle", delta.GlobalCss);
        Assert.Contains("@keyframes enter", delta.GlobalCss);
        Assert.DoesNotContain("@keyframes exit", delta.GlobalCss);
        Assert.Equal("", compiler.AddCandidates(new[] { "animate-in" }).GlobalCss);
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
        // AwAUI 的根元素是 root（没有 html）；根字号恢复为 Tailwind rem 刻度所依据的 16px（AwAUI 默认样式表是 14px）
        Assert.Contains("  :root {\n    font-size: 16px;\n    line-height: 1.5;", css);
        Assert.DoesNotContain("html", css);
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
    public void Selector_explosion_from_stacked_variants_is_bounded()
    {
        static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));
        double start = ThreadCpuClock.NowMs();
        // 每层 [&_a,&_b,&_c,&_d] 让展开后的选择器数量 ×4：9 层约 75 MB 输出，30 层不可能完成
        Assert.Null(s_compiler.CandidateToCss(Repeat("[&_a,&_b,&_c,&_d]:", 9) + "flex"));
        Assert.Null(s_compiler.CandidateToCss(Repeat("[&_a,&_b,&_c,&_d]:", 30) + "flex"));
        // 复合变体每层把选择器长度翻倍以上
        Assert.Null(s_compiler.CandidateToCss(Repeat("not-group-has-hover:", 14) + "flex"));
        Assert.Null(s_compiler.CandidateToCss(Repeat("not-group-has-hover:", 32) + "flex"));
        Assert.Null(s_compiler.CandidateToCss(Repeat("[&_&]:", 32) + "flex"));
        // 样式表里定义的变体本身无害，但候选可以任意叠加它
        var custom = TailwindCompiler.Create("@import \"tailwindcss\";\n@custom-variant x (&:hover, &:focus, &:active, &:visited);\n@custom-variant y { &:hover { @slot; } &:focus { @slot; } &:active { @slot; } }");
        Assert.Null(custom.CandidateToCss(Repeat("x:", 30) + "flex"));
        Assert.Null(custom.CandidateToCss(Repeat("y:", 30) + "flex"));
        Assert.False(custom.IsValidCandidate(Repeat("y:", 30) + "flex"));
        Assert.True(custom.AddCandidates(new[] { Repeat("x:", 30) + "flex" }).IsEmpty);
        Assert.True(ThreadCpuClock.NowMs() - start < 30_000);
        // 正常的多层变体不受影响
        Assert.NotNull(s_compiler.CandidateToCss(Repeat("[&_a,&_b]:", 3) + "hover:flex"));
        Assert.NotNull(s_compiler.CandidateToCss(Repeat("hover:", 32) + "flex"));
        Assert.NotNull(custom.CandidateToCss(Repeat("x:", 3) + "flex"));
        // 带大量嵌套规则的自定义工具（类似 typography 的 prose）叠加变体后仍然有效
        var prose = new StringBuilder("@import \"tailwindcss\";\n@utility prose {");
        for (int i = 0; i < 150; i++) prose.Append(" & :where(.p").Append(i).Append("):not(:where([class~=\"not-prose\"], [class~=\"not-prose\"] *)) { margin-top: ").Append(i).Append("px; }");
        prose.Append(" }");
        var typography = TailwindCompiler.Create(prose.ToString());
        Assert.NotNull(typography.CandidateToCss("dark:md:hover:prose"));
        Assert.NotNull(typography.CandidateToCss("[&_a,&_b]:group-hover:not-first:prose"));
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

    [Fact]
    public void Stylesheet_fan_out_is_bounded()
    {
        double start = ThreadCpuClock.NowMs();
        // 每个 @utility 把上一个 @apply 两次：第 n 个展开为 2^n 条规则
        var apply = new StringBuilder("@import \"tailwindcss\";\n@utility u0 { color: red; }\n");
        for (int i = 1; i < 40; i++) apply.Append("@utility u").Append(i).Append(" { @apply u").Append(i - 1).Append("; &:hover { @apply u").Append(i - 1).Append("; } }\n");
        var ex = Assert.Throws<TailwindException>(() => TailwindCompiler.Create(apply.ToString()));
        Assert.Contains("@apply", ex.Message);
        // 每个 @custom-variant 嵌套两次上一个：第 n 个的主体为 2^n 层
        var variants = new StringBuilder("@import \"tailwindcss\";\n@custom-variant v0 { &:hover, &:focus { @slot; } }\n");
        for (int i = 1; i < 40; i++) variants.Append("@custom-variant v").Append(i).Append(" { @variant v").Append(i - 1).Append(" { @variant v").Append(i - 1).Append(" { @slot; } } }\n");
        var compiler = TailwindCompiler.Create(variants.ToString());
        Assert.Throws<TailwindException>(() => compiler.CandidateToCss("v39:flex"));
        Assert.NotNull(compiler.CandidateToCss("v2:flex"));
        // @import 扇出：每个文件导入两个新文件
        Assert.Throws<TailwindException>(() => TailwindCompiler.Create("@import \"f\";", new TailwindOptions { Resolver = new FanResolver() }));
        Assert.True(ThreadCpuClock.NowMs() - start < 30_000);
    }

    [Fact]
    public void Circular_theme_function_references_are_rejected()
    {
        double start = ThreadCpuClock.NowMs();
        // Tailwind v4.3.5 在这里死循环：展开结果会被再次展开
        var ex = Assert.Throws<TailwindException>(() => TailwindCompiler.Create("@import \"tailwindcss\";\n@theme { --spacing-a: theme(--spacing-b); --spacing-b: theme(--spacing-a); }\n.x { width: theme(--spacing-a); }"));
        Assert.Contains("theme(", ex.Message);
        // 非循环的多层引用照常展开
        var ok = TailwindCompiler.Create("@import \"tailwindcss\";\n@theme { --spacing-a: theme(--spacing-b); --spacing-b: 3px; }\n.x { width: theme(--spacing-a); }");
        Assert.Contains("width: 3px", ok.Build(Array.Empty<string>()));
        Assert.True(ThreadCpuClock.NowMs() - start < 30_000);
    }

    sealed class FanResolver : StylesheetResolver
    {
        public override ResolvedStylesheet? Resolve(string id, string? baseDirectory) => new(id.Length < 24 ? $"@import \"{id}a\";\n@import \"{id}b\";" : ".z { color: red; }");
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
