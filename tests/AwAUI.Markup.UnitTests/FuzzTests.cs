using System.Diagnostics;
using System.Text;
using AwAUI.Markup.Compiler;
using AwAUI.Markup.Compiler.Formatting;
using AwAUI.Markup.Compiler.Syntax;

namespace AwAUI.Markup.UnitTests;

/// <summary>
/// 模糊测试（design/17「测试」）：随机与变异输入不得导致异常或死循环。每个输入还要满足：区间不变式成立、
/// 格式化与 C# 生成不抛异常；若输入没有语法错误，格式化结果重新解析后与原树结构等价。
/// </summary>
public class FuzzTests
{
    static readonly string[] Tokens =
    {
        "<", ">", "</", "/>", "<>", "</>", "@", "@@", "@(", "@{", "@*", "*@", "@:", "(", ")", "{", "}", "[", "]",
        "\"", "'", "$\"", "@\"", "$@\"", "\"\"\"", "$$\"\"\"", "{{", "}}", "//", "/*", "*/", "#if X", "\n", "\r\n", " ", "  ",
        "\t", ";", ":", "::", "=", ".", "?.", "!", "?", "&amp;", "&#x41;", "&", "-", "div", "span", "text", "Button", "Ns.Card",
        "input", "img", "style", "global", "if", "else", "foreach", "for", "while", "switch", "case", "default", "code",
        "using", "namespace", "typeparam", "attribute", "bind", "@bind", "@bind:event", "@bind-Value", "@key", "@ref",
        "@onclick", "class", "tabindex", "x", "y1", "中", "😀", "a@b.c", "=>", "break;", "\"str\"", "'c'", "0", "page",
        "case Point { X: 0 }:", "case < 0:", "case List<int> l:", "?? ", "@count@unit",
    };

    static readonly TimeSpan Budget = TimeSpan.FromSeconds(120 * Scale(1));

    // 可用环境变量换种子、放大迭代次数做更长的本地模糊测试：AWAUI_FUZZ_SEED、AWAUI_FUZZ_SCALE
    static int Seed(int fallback) => int.TryParse(Environment.GetEnvironmentVariable("AWAUI_FUZZ_SEED"), out int s) ? s : fallback;
    static int Scale(int n) => int.TryParse(Environment.GetEnvironmentVariable("AWAUI_FUZZ_SCALE"), out int k) && k > 0 ? n * k : n;

    static void RunWithTimeout(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        }, 16 * 1024 * 1024);
        thread.Start();
        Assert.True(thread.Join(Budget), $"fuzz run did not finish within {Budget} (possible infinite loop)");
        if (error != null) throw new Exception("fuzz failure", error);
    }

    /// <summary>对一个输入执行完整的前端流程并检查不变式。</summary>
    internal static void Exercise(string text)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var a = MarkupCompiler.Analyze(text, "Fuzz.awaui");
            TestUtil.CheckSpans(a.Tree);
            foreach (var d in a.Diagnostics)
                Assert.True(d.Span.End <= text.Length, $"diagnostic span out of range: {d}");
            SyntaxPrinter.Print(a.NormalizedRoot, includeSpans: true);
            var compiled = MarkupCompiler.Compile(a); // 代码生成（含 AW4012、作用域样式改写、@code 成员扫描）
            foreach (var d in compiled.Diagnostics)
                Assert.True(d.Span.End <= text.Length, $"diagnostic span out of range: {d}");
            // 不检查诊断的内部入口：错误恢复得到的树也不能让格式化抛异常；公开入口遇到语法错误原样返回
            string formatted = MarkupFormatter.Format(a.Tree.Root);
            bool hasSyntaxErrors = a.Tree.Diagnostics.Any(d => d.IsError);
            Assert.Equal(hasSyntaxErrors ? text : formatted, MarkupFormatter.Format(a.Tree));
            var again = MarkupSyntaxTree.Parse(formatted);
            if (a.Tree.Diagnostics.Count == 0)
            {
                Assert.True(again.Diagnostics.Count == 0,
                    "formatted output of an error-free input has errors:\n" + string.Join("\n", again.Diagnostics) + "\n--- formatted ---\n" + formatted);
                string expected = RoundTrip.Structure(a.Tree);
                string actual = RoundTrip.Structure(again);
                if (expected != actual)
                    Assert.Fail("round trip changed the tree\n--- expected ---\n" + expected + "\n--- actual ---\n" + actual + "\n--- formatted ---\n" + formatted);
            }
        }
        catch (Exception e)
        {
            throw new Exception("failing input: " + Escape(text), e);
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"input took {sw.Elapsed}: {Escape(text)}");
    }

    static string Escape(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";

    [Fact]
    public void Random_token_soup_never_throws_or_hangs() => RunWithTimeout(() =>
    {
        var rng = new Random(Seed(12345));
        var sb = new StringBuilder();
        for (int iter = 0; iter < Scale(6000); iter++)
        {
            sb.Clear();
            int count = rng.Next(0, 80);
            for (int i = 0; i < count; i++) sb.Append(Tokens[rng.Next(Tokens.Length)]);
            Exercise(sb.ToString());
        }
    });

    [Fact]
    public void Random_characters_never_throw_or_hang() => RunWithTimeout(() =>
    {
        var rng = new Random(Seed(777));
        const string alphabet = "<>/@(){}[]\"'$;:*!.?&#= \n\r\tabcXYZ019_-中\u00A0";
        for (int iter = 0; iter < Scale(4000); iter++)
        {
            int len = rng.Next(0, 120);
            var chars = new char[len];
            for (int i = 0; i < len; i++)
            {
                int pick = rng.Next(20);
                chars[i] = pick == 0 ? (char)rng.Next(0, 0x10000) : alphabet[rng.Next(alphabet.Length)]; // 含孤立代理项与控制字符
            }
            Exercise(new string(chars));
        }
    });

    [Fact]
    public void Mutated_valid_documents_never_throw_or_hang() => RunWithTimeout(() =>
    {
        var corpus = FormatterTests.Corpus().Select(o => (string)o[0]).ToArray();
        var rng = new Random(Seed(4242));
        for (int iter = 0; iter < Scale(5000); iter++)
        {
            string s = corpus[rng.Next(corpus.Length)];
            int mutations = rng.Next(1, 5);
            for (int m = 0; m < mutations && s.Length > 0; m++)
            {
                int pos = rng.Next(s.Length);
                int len = Math.Min(rng.Next(1, 12), s.Length - pos);
                s = rng.Next(5) switch
                {
                    0 => s.Remove(pos, len),
                    1 => s.Insert(pos, Tokens[rng.Next(Tokens.Length)]),
                    2 => s.Insert(rng.Next(s.Length), s.Substring(pos, len)),
                    3 => s.Substring(0, pos),
                    _ => s.Remove(pos, 1).Insert(rng.Next(s.Length - 1 + 1 > 0 ? s.Length - 1 : 1), s[pos].ToString()),
                };
            }
            Exercise(s);
        }
    });

    /// <summary>按文法随机生成合法文档：检验往返等价在大量组合下成立。</summary>
    [Fact]
    public void Generated_valid_documents_round_trip() => RunWithTimeout(() =>
    {
        var rng = new Random(Seed(99));
        int clean = 0, total = Scale(3000);
        for (int iter = 0; iter < total; iter++)
        {
            var g = new DocGenerator(rng);
            string text = g.Document();
            var tree = MarkupSyntaxTree.Parse(text);
            if (tree.Diagnostics.Count == 0) clean++;
            else if (Environment.GetEnvironmentVariable("AWAUI_FUZZ_DEBUG") == "1" && iter < 400)
                Console.Error.WriteLine("GEN: " + Escape(text) + "\n  " + string.Join("\n  ", tree.Diagnostics));
            Exercise(text);
        }
        Assert.True(clean > total * 9 / 10, $"generator produced too many invalid documents ({clean}/{total} clean)");
    });

    sealed class DocGenerator
    {
        static readonly string[] Words = { "a", "hello", "世界", "x.y", "foo-bar", "1", "&amp;", "&lt;", "@@", "me@mail.com", ",", "!", "(", ")", "[", "}", ".", "\"q\"" };
        static readonly string[] Spaces = { "", " ", "  ", "\n", "\n    ", "\t", " \n  \n " };
        static readonly string[] Exprs = { "@x", "@user.Name", "@a?.b", "@items[0]", "@F(\"(\")", "@(a + b)", "@(\"}\")", "@($\"{v:0}\")", "@obj!.P" };
        static readonly string[] Tags = { "div", "span", "text", "Button", "Ns.Card", "x-y" };
        static readonly string[] CaseLabels =
        {
            "case 1:", "case Point { X: 0, Y: var y } when y > 0:", "case < 0:\n    case > 0 and <= 10:", "case List<int> l:",
            "case (1, 2) or [_, ..]:", "case var s when (s as string) ?? \"\" == \"a:b\":",
        };
        readonly Random _r;
        int _depth;

        public DocGenerator(Random r) => _r = r;

        T Pick<T>(T[] a) => a[_r.Next(a.Length)];

        public string Document()
        {
            var sb = new StringBuilder();
            if (_r.Next(3) == 0) sb.Append("@using A.B\n");
            if (_r.Next(4) == 0) sb.Append("@* top *@\n");
            int n = _r.Next(1, 4);
            for (int i = 0; i < n; i++) sb.Append(Pick(Spaces)).Append(Node(markup: true));
            if (_r.Next(3) == 0) sb.Append("\n@code {\n    int x = \"}\".Length; // }\n    void M() { if (x > 0) { } }\n}\n");
            if (_r.Next(4) == 0) sb.Append("\n<style>\n  .a { color: red; }\n</style>\n");
            return sb.ToString();
        }

        string Content()
        {
            var sb = new StringBuilder();
            int n = _r.Next(0, 5);
            for (int i = 0; i < n; i++)
            {
                sb.Append(Pick(Spaces));
                switch (_r.Next(4))
                {
                    case 0: sb.Append(Pick(Words)); break;
                    case 1: sb.Append(Pick(Exprs)); break;
                    default: sb.Append(Node(markup: true)); break;
                }
            }
            return sb.Append(Pick(Spaces)).ToString();
        }

        string Node(bool markup)
        {
            if (_depth > 4) return markup ? Pick(Words) : "x++;";
            _depth++;
            try
            {
                switch (_r.Next(markup ? 9 : 7))
                {
                    case 0: return "@if (a < b)" + Pick(Spaces) + Body() + (_r.Next(2) == 0 ? " else if (c) " + Body() : "") + (_r.Next(2) == 0 ? "\nelse" + Body() : "");
                    case 1: return "@foreach (var i in xs)" + Pick(Spaces) + Body();
                    case 2: return "@switch (k)\n{\n    " + Pick(CaseLabels) + "\n        " + Node(false) + "\n        break;\n    default:\n        @: line @k\n        break;\n}";
                    case 3: return "@{\n    var s = \"<div>\";\n    " + Node(false) + "\n}";
                    case 4: return "<>" + Content() + "</>";
                    case 5: return "@* c " + Pick(Words) + " *@";
                    case 6: return Element();
                    default: return Pick(Exprs);
                }
            }
            finally { _depth--; }
        }

        string Body() => "{" + Pick(Spaces) + Node(false) + Pick(Spaces) + (_r.Next(2) == 0 ? "x++;" : "") + Pick(Spaces) + Node(false) + "\n}";

        string Element()
        {
            string tag = Pick(Tags);
            var sb = new StringBuilder("<").Append(tag);
            int attrs = _r.Next(0, 4);
            int first = _r.Next(4);
            for (int i = 0; i < attrs; i++)
            {
                sb.Append(' ').Append(((first + i) % 4) switch
                {
                    0 => "class=\"a @x b\"",
                    1 => "@onclick=\"() => Go(1)\"",
                    2 => "title='say \"hi\" @(y)'",
                    _ => "data-k" + i + "=@v",
                });
            }
            if (_r.Next(3) == 0) return sb.Append(" />").ToString();
            return sb.Append('>').Append(Content()).Append("</").Append(tag).Append('>').ToString();
        }
    }

    [Theory]
    [InlineData("<div>", 20000)]
    [InlineData("<>", 20000)]
    [InlineData("@{", 20000)]
    [InlineData("@if (x) {", 20000)]
    [InlineData("@(", 20000)]
    [InlineData("@foreach (x) { <div @key=\"x\">", 5000)]
    [InlineData("@switch (x) { case 1:", 5000)]
    [InlineData("<div title=\"@(", 5000)]
    [InlineData("$\"{", 20000)]
    [InlineData("@x(", 20000)]
    [InlineData("<", 50000)]
    [InlineData("@", 50000)]
    [InlineData("</a>", 20000)]
    [InlineData("&amp", 20000)]
    public void Pathological_repetition_terminates(string unit, int count) => RunWithTimeout(() =>
    {
        string text = string.Concat(Enumerable.Repeat(unit, count));
        var sw = Stopwatch.StartNew();
        var a = MarkupCompiler.Analyze(text);
        TestUtil.CheckSpans(a.Tree);
        MarkupFormatter.Format(a.Tree.Root);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"took {sw.Elapsed}");
    });

    [Fact]
    public void Deeply_nested_valid_markup_reports_nesting_limit_instead_of_overflowing()
    {
        string text = string.Concat(Enumerable.Repeat("<div>", 1000)) + string.Concat(Enumerable.Repeat("</div>", 1000));
        var tree = MarkupSyntaxTree.Parse(text);
        Assert.Contains(tree.Diagnostics, d => d.Code == DiagnosticCodes.UnbalancedDelimiter && d.Message.Contains("嵌套"));
        TestUtil.CheckSpans(tree);
    }

    [Fact]
    public void Moderately_nested_markup_is_fine()
    {
        string text = string.Concat(Enumerable.Repeat("<div>@if (x) {", 45)) + string.Concat(Enumerable.Repeat("}</div>", 45));
        Assert.Empty(MarkupSyntaxTree.Parse(text).Diagnostics);
        RoundTrip.Check(text);
    }
}
