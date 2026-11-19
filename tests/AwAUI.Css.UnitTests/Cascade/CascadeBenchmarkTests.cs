using System.Diagnostics;
using System.Text;
using AwAUI.Css.Cascade;
using Xunit.Abstractions;

namespace AwAUI.Css.UnitTests.Cascade;

/// <summary>微基准：2000 个元素 × 500 条规则。只打印测量结果，不做严格断言（CI 机器差异大）。</summary>
public class CascadeBenchmarkTests
{
    readonly ITestOutputHelper _output;

    public CascadeBenchmarkTests(ITestOutputHelper output) => _output = output;

    static string BuildCss(int rules)
    {
        var sb = new StringBuilder(":root { --accent: #3366ff; --gap: 4px }\n");
        var props = new[]
        {
            "color: var(--accent)", "padding: 4px 8px", "margin: var(--gap) auto", "border: 1px solid #ccc", "display: flex",
            "font-size: 1.1em", "background: linear-gradient(red, blue)", "width: calc(100% - 2em)", "opacity: .9",
            "border-radius: 4px", "flex: 1 1 0", "line-height: 1.4", "box-shadow: 0 1px 2px rgb(0 0 0 / 20%)",
        };
        for (int i = 0; i < rules; i++)
        {
            string selector = (i % 5) switch
            {
                0 => $".c{i % 97}",
                1 => $"div.c{i % 89} > .c{(i + 7) % 97}",
                2 => $".c{i % 83}:hover",
                3 => $"#id{i % 50}",
                _ => $"span .c{i % 71}",
            };
            if (i % 50 == 0) sb.Append("@media (min-width: 600px) {");
            sb.Append(selector).Append(" { ").Append(props[i % props.Length]).Append("; ").Append(props[(i * 7) % props.Length]).Append(" }");
            if (i % 50 == 0) sb.Append('}');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    static El BuildTree(int count)
    {
        var root = new El("div", classes: "c0");
        var all = new List<El> { root };
        var rng = new Random(1);
        for (int i = 1; i < count; i++)
        {
            var parent = all[rng.Next(all.Count)];
            var el = new El(i % 3 == 0 ? "span" : "div", i % 40 == 0 ? "id" + (i % 50) : null, $"c{i % 97} c{(i * 13) % 89}");
            parent.Add(el);
            all.Add(el);
        }
        return root;
    }

    [Fact]
    public void Resolve_2000_elements_with_500_rules()
    {
        var css = BuildCss(500);
        var swParse = Stopwatch.StartNew();
        var sheet = StyleSheet.Parse(css);
        var engine = new StyleEngine();
        engine.AddStyleSheet(sheet);
        swParse.Stop();
        var root = BuildTree(2000);

        // 预热（JIT、索引编译）
        CascadeTestHelper.ResolveTree(engine, root);

        const int iterations = 5;
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        Dictionary<El, ComputedStyle>? styles = null;
        for (int i = 0; i < iterations; i++) styles = CascadeTestHelper.ResolveTree(engine, root);
        sw.Stop();
        long alloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        Assert.Equal(2000, styles!.Count);
        double perPass = sw.Elapsed.TotalMilliseconds / iterations;
        _output.WriteLine($"parse+compile 500 rules: {swParse.Elapsed.TotalMilliseconds:F1} ms");
        _output.WriteLine($"resolve 2000 elements: {perPass:F2} ms/pass ({perPass * 1000 / 2000:F2} µs/element), " +
                          $"{alloc / iterations / 2000} bytes allocated/element");
        Console.WriteLine($"[CascadeBenchmark] parse {swParse.Elapsed.TotalMilliseconds:F1} ms; resolve {perPass:F2} ms/pass; {alloc / iterations / 2000} B/element");
        // 宽松上界：只捕捉数量级的退化
        Assert.True(perPass < 5000, $"resolve pass took {perPass} ms");
    }
}
