using System.Text;
using AwAUI.Tests.Shared;
using Xunit.Abstractions;
using static AwAUI.Tailwind.TailwindMerge;

namespace AwAUI.Tailwind.UnitTests;

/// <summary>Cn 与 tailwind-merge v3.7.0 的一致性（参考结果由 tools/merge-cases.mjs 用官方 twMerge 生成）。</summary>
public class MergeTests
{
    readonly ITestOutputHelper _output;

    public MergeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Matches_tailwind_merge_reference()
    {
        var lines = TestFiles.Read("merge", "cases.txt").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var failures = new StringBuilder();
        int pass = 0;
        var merger = new TailwindMerger(new TailwindMergeOptions { CacheSize = 0 });
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            string actual = merger.Merge(parts[0]);
            if (actual == parts[1]) pass++;
            else failures.Append(parts[0]).Append("\n  expected: ").Append(parts[1]).Append("\n  actual:   ").Append(actual).Append('\n');
        }
        _output.WriteLine($"tailwind-merge: {pass}/{lines.Length} cases match");
        Assert.True(pass == lines.Length, failures.ToString());
    }

    [Theory]
    [InlineData("px-2 py-1 p-3", "p-3")]
    [InlineData("p-3 px-2", "p-3 px-2")]
    [InlineData("text-sm/6 leading-7", "text-sm/6 leading-7")]
    [InlineData("leading-7 text-sm/6", "text-sm/6")]
    [InlineData("hover:bg-red-500 bg-blue-500 hover:bg-green-500", "bg-blue-500 hover:bg-green-500")]
    [InlineData("md:hover:p-2 hover:md:p-4", "hover:md:p-4")]
    [InlineData("!p-2 p-4", "!p-2 p-4")]
    [InlineData("[mask-type:luminance] [mask-type:alpha]", "[mask-type:alpha]")]
    [InlineData("my-class p-2 other p-4", "my-class other p-4")]
    public void Cn_resolves_conflicts(string input, string expected) => Assert.Equal(expected, Cn(input));

    [Fact]
    public void Cn_overloads_and_conditions()
    {
        Assert.Equal("p-3", Cn("px-2 py-1", "p-3"));
        Assert.Equal("px-2 py-1", Cn("px-2 py-1", null));
        Assert.Equal("bg-red-500 text-white", Cn(null, "bg-red-500", "text-white"));
        Assert.Equal("px-4 h-10", Cn("h-9 px-4", "h-10"));
        Assert.Equal("a c", Cn("a", ClassValue.When(false, "b"), "c"));
        Assert.Equal("", Cn());
    }

    [Fact]
    public void Two_argument_cn_does_not_allocate_on_cache_hit()
    {
        string a = "inline-flex items-center px-4 py-2 bg-primary";
        string b = "px-8 bg-red-500";
        string first = Cn(a, b);
        string second = Cn(a, b);
        Assert.Same(first, second);
#if NET
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) _ = Cn(a, b);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024, $"allocated {allocated} bytes");
#endif
    }

    [Fact]
    public void Extended_config_merges_custom_groups()
    {
        var merger = new TailwindMerger(new TailwindMergeOptions().AddClassGroup("scroll-fade", "scroll-fade", "scroll-fade-x", "scroll-fade-y").AddThemeValues("text", "2xs"));
        Assert.Equal("scroll-fade-x", merger.Merge("scroll-fade scroll-fade-x"));
        Assert.Equal("text-2xs", merger.Merge("text-sm text-2xs"));
    }

    [Fact]
    public void Prefix_only_merges_prefixed_classes()
    {
        var merger = new TailwindMerger(new TailwindMergeOptions { Prefix = "tw" });
        Assert.Equal("p-2 tw:p-4", merger.Merge("p-2 tw:p-2 tw:p-4"));
    }

    [Fact]
    public void Hostile_class_lists_are_bounded()
    {
        var huge = new string('x', 200_000) + " " + string.Join(" ", Enumerable.Repeat("calc(", 2000)) + " [" + new string('(', 5000);
        double start = ThreadCpuClock.NowMs();
        var result = new TailwindMerger(new TailwindMergeOptions { CacheSize = 0 }).Merge(huge + " p-2 p-4");
        Assert.EndsWith("p-4", result);
        Assert.True(ThreadCpuClock.NowMs() - start < 10_000);
    }
}

public class CvaTests
{
    enum Variant { Default, Destructive, Outline }

    enum Size { Default, Sm, Icon }

    static readonly Cva<Variant, Size> s_button = new(
        "inline-flex items-center justify-center gap-2 rounded-md text-sm font-medium",
        new Dictionary<Variant, string>
        {
            [Variant.Default] = "bg-primary text-primary-foreground hover:bg-primary/90",
            [Variant.Destructive] = "bg-destructive text-white hover:bg-destructive/90",
            [Variant.Outline] = "border bg-background shadow-xs",
        },
        new Dictionary<Size, string>
        {
            [Size.Default] = "h-9 px-4 py-2 has-[>svg]:px-3",
            [Size.Sm] = "h-8 rounded-md gap-1.5 px-3",
            [Size.Icon] = "size-9",
        },
        new List<(Func<Variant, Size, bool>, string)> { ((v, s) => v == Variant.Outline && s == Size.Icon, "rounded-full") });

    [Fact]
    public void Generic_cva_selects_and_merges()
    {
        Assert.Equal("inline-flex items-center justify-center gap-2 rounded-md text-sm font-medium bg-primary text-primary-foreground hover:bg-primary/90 h-9 px-4 py-2 has-[>svg]:px-3", s_button.Of(Variant.Default, Size.Default));
        // Sm 的 gap-1.5 覆盖 base 的 gap-2；rounded-md 重复项合并
        Assert.Equal("inline-flex items-center justify-center text-sm font-medium bg-destructive text-white hover:bg-destructive/90 h-8 rounded-md gap-1.5 px-3", s_button.Of(Variant.Destructive, Size.Sm));
        Assert.Equal("inline-flex items-center justify-center gap-2 text-sm font-medium border bg-background shadow-xs size-9 rounded-full", s_button.Of(Variant.Outline, Size.Icon));
        Assert.EndsWith("px-8", s_button.Of(Variant.Default, Size.Default, "px-8"));
        Assert.DoesNotContain("px-4", s_button.Of(Variant.Default, Size.Default, "px-8"));
        Assert.Same(s_button.Of(Variant.Default, Size.Sm), s_button.Of(Variant.Default, Size.Sm));
    }

    [Fact]
    public void String_cva_follows_class_variance_authority()
    {
        var cva = new Cva("base",
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["intent"] = new Dictionary<string, string> { ["primary"] = "bg-blue-500", ["secondary"] = "bg-white" },
                ["size"] = new Dictionary<string, string> { ["small"] = "text-sm", ["medium"] = "text-base" },
                ["disabled"] = new Dictionary<string, string> { ["true"] = "opacity-50", ["false"] = "" },
            },
            new[]
            {
                new CompoundVariant(new Dictionary<string, string[]> { ["intent"] = new[] { "primary" }, ["size"] = new[] { "medium" } }, "uppercase"),
                new CompoundVariant(new Dictionary<string, string[]> { ["intent"] = new[] { "primary", "secondary" }, ["disabled"] = new[] { "true" } }, "cursor-not-allowed"),
            },
            new Dictionary<string, string> { ["intent"] = "primary", ["size"] = "medium", ["disabled"] = "false" });
        Assert.Equal("base bg-blue-500 text-base uppercase", cva.Invoke());
        Assert.Equal("base bg-white text-sm", cva.Invoke(new Dictionary<string, string?> { ["intent"] = "secondary", ["size"] = "small" }));
        Assert.Equal("base bg-white text-base opacity-50 cursor-not-allowed", cva.Invoke(new Dictionary<string, string?> { ["intent"] = "secondary", ["disabled"] = "true" }));
        // 显式 null：该变体不输出类，且不回退默认值参与复合变体
        Assert.Equal("base text-base", cva.Invoke(new Dictionary<string, string?> { ["intent"] = null }));
        Assert.Equal("base bg-blue-500 uppercase text-lg", cva.Invoke(null, "text-lg"));
    }
}

public class ScannerTests
{
    [Fact]
    public void Extracts_from_markup_csharp_and_css()
    {
        const string source = """
            <div class="flex items-center hover:bg-red-500 [&_svg:not([class*='size-'])]:size-4 data-[state=open]:animate-in">
                <Button class="@Cn(Variants.Of(Variant, Size), Class)" Class="max-h-(--radix-select-content-available-height)">
            </div>
            @code {
                string Css => $"p-{size} text-sm/6 {(active ? "bg-primary/90" : "opacity-50")}";
                static readonly string Raw = @"grid-cols-[1fr_auto] -mt-2 !p-4 p-4!";
            }
            .card { @apply rounded-xl border bg-card; }
            """;
        var found = CandidateScanner.Extract(source);
        foreach (var expected in new[]
                 {
                     "flex", "items-center", "hover:bg-red-500", "[&_svg:not([class*='size-'])]:size-4", "data-[state=open]:animate-in",
                     "max-h-(--radix-select-content-available-height)", "text-sm/6", "bg-primary/90", "opacity-50", "grid-cols-[1fr_auto]", "-mt-2", "!p-4", "p-4!",
                     "rounded-xl", "border", "bg-card",
                 })
        {
            Assert.Contains(expected, found);
        }
        Assert.Equal(found.Count, found.Distinct().Count());
    }

    [Fact]
    public void Extracted_candidates_compile_with_the_compiler()
    {
        var compiler = TailwindCompiler.CreateDefault();
        var found = CandidateScanner.Extract("<p class=\"text-red-500 md:flex unknown-thing\">Hello world.</p>");
        var valid = found.Where(compiler.IsValidCandidate).ToArray();
        Assert.Equal(new[] { "text-red-500", "md:flex" }, valid);
    }

    [Fact]
    public void Hostile_input_is_linear()
    {
        static double Measure(int n)
        {
            var text = new StringBuilder();
            for (int i = 0; i < n; i++) text.Append("[[[[(((('\"x ").Append(new string('a', 2000)).Append("[\n");
            string s = text.ToString();
            double start = ThreadCpuClock.NowMs();
            CandidateScanner.Extract(s);
            return ThreadCpuClock.NowMs() - start;
        }
        Measure(50);
        double small = Math.Max(Measure(200), 1);
        double large = Measure(1600);
        Assert.True(large < small * 8 * 4 + 50, $"scan time grew superlinearly: {small} ms -> {large} ms");
    }
}
