using System.Text;
using Xunit.Abstractions;

namespace AwAUI.Tailwind.UnitTests;

/// <summary>
/// 与 Tailwind CSS v4.3.5 官方输出逐字比对（design/23「质量门槛」第 2 条）。
/// shadcn 语料：new-york-v4 S1 组件里的全部类名 + shadcn 主题；default 语料：按工具族手写的广覆盖语料（含变体、任意值、无效类名）。
/// </summary>
public class ConformanceTests
{
    readonly ITestOutputHelper _output;

    public ConformanceTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("shadcn")]
    [InlineData("default")]
    [InlineData("directives")]
    [InlineData("prefix")]
    public void Per_candidate_output_matches_tailwind(string corpus)
    {
        var compiler = TestFiles.CreateCompiler(corpus);
        var reference = TestFiles.ReadReference(corpus);
        var candidates = reference.Select(r => r.Key).ToArray();
        var actual = compiler.CandidatesToCss(candidates);
        var failures = new StringBuilder();
        int pass = 0;
        for (int i = 0; i < reference.Count; i++)
        {
            string got = TestFiles.Normalize(actual[i]);
            if (got == reference[i].Value)
            {
                pass++;
                continue;
            }
            failures.Append("### ").Append(reference[i].Key).Append('\n')
                .Append("--- expected\n").Append(reference[i].Value).Append('\n')
                .Append("--- actual\n").Append(got).Append("\n\n");
        }
        _output.WriteLine($"{corpus}: {pass}/{reference.Count} candidates match ({100.0 * pass / reference.Count:F1}%)");
        if (failures.Length > 0) _output.WriteLine(failures.ToString());
        Assert.True(pass == reference.Count, $"{reference.Count - pass} mismatches:\n" + failures);
    }

    [Theory]
    [InlineData("shadcn")]
    [InlineData("default")]
    [InlineData("directives")]
    [InlineData("prefix")]
    public void Full_stylesheet_matches_tailwind(string corpus)
    {
        var compiler = TestFiles.CreateCompiler(corpus);
        var candidates = TestFiles.ReadReference(corpus).Select(r => r.Key).ToArray();
        string expected = TestFiles.Read(corpus, "full.css");
        string actual = compiler.Build(candidates);
        // Tailwind 的 build() 在开头加许可证注释；AwAUI 不加（输出不是 Tailwind 本身）
        expected = expected.Substring(expected.IndexOf('\n') + 1);
        if (actual != expected)
        {
            var e = expected.Split('\n');
            var a = actual.Split('\n');
            int line = 0;
            while (line < e.Length && line < a.Length && e[line] == a[line]) line++;
            string ctxE = string.Join("\n", e.Skip(Math.Max(0, line - 5)).Take(15));
            string ctxA = string.Join("\n", a.Skip(Math.Max(0, line - 5)).Take(15));
            Assert.Fail($"First difference at line {line + 1}:\n--- expected\n{ctxE}\n--- actual\n{ctxA}");
        }
    }
}
