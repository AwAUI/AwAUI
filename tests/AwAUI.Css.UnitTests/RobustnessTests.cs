using System.Diagnostics;
using System.Runtime.ExceptionServices;
using AwAUI.Css.Selectors;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.UnitTests;

/// <summary>病态输入：深嵌套选择器不得栈溢出、嵌套 &amp; / :is() 不得指数级匹配、ASCII 大小写规则、诊断代码。</summary>
public class RobustnessTests
{
    /// <summary>在 1 MB 栈的线程上运行（Windows 主线程默认栈大小），栈溢出会直接终止测试进程。</summary>
    static void OnOneMegabyteStack(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { error = e; }
        }, 1024 * 1024);
        t.Start();
        t.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    static string Nest(string open, int depth, string inner = "a")
        => string.Concat(Enumerable.Repeat(open, depth)) + inner + new string(')', depth);

    [Theory]
    [InlineData(":is(", 255)]
    [InlineData(":where(", 255)]
    [InlineData(":not(", 255)]
    [InlineData(":nth-child(1 of ", 200)]
    [InlineData(":nth-last-child(1 of ", 255)]
    [InlineData(":is(", 5000)]
    [InlineData(":not(", 5000)]
    [InlineData(":nth-child(1 of ", 5000)]
    public void Deeply_nested_selector_functions_produce_diagnostics_not_stack_overflow(string open, int depth)
    {
        OnOneMegabyteStack(() =>
        {
            var s = StyleSheet.Parse(Nest(open, depth) + " { color: red } .ok { color: blue }");
            Assert.Equal(".ok", Assert.IsType<CssStyleRule>(s.Rules[s.Rules.Count - 1]).SelectorText);
            Assert.Contains(s.Diagnostics, d => d.Code == CssDiagnosticCodes.InvalidSelector);
        });
    }

    [Fact]
    public void Selector_function_nesting_limit_is_exact()
    {
        Assert.NotNull(SelectorParser.Parse(Nest(":not(", SelectorParser.MaxFunctionNesting)));
        var d = new List<CssDiagnostic>();
        Assert.Null(SelectorParser.Parse(Nest(":not(", SelectorParser.MaxFunctionNesting + 1), d));
        Assert.Contains(d, x => x.Code == CssDiagnosticCodes.InvalidSelector && x.Message.Contains("嵌套"));
    }

    [Fact]
    public void Nesting_limit_in_forgiving_list_drops_only_the_inner_item()
    {
        var d = new List<CssDiagnostic>();
        var list = SelectorParser.Parse(Nest(":is(", SelectorParser.MaxFunctionNesting + 1), d);
        Assert.NotNull(list);
        Assert.Contains(d, x => x.Code == CssDiagnosticCodes.InvalidSelector && x.Severity == CssDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Too_many_compounds_is_rejected()
    {
        var d = new List<CssDiagnostic>();
        var text = "a" + string.Concat(Enumerable.Repeat(" + a", SelectorParser.MaxMatchDepth + 10));
        Assert.Null(SelectorParser.Parse(text, d));
        Assert.Contains(d, x => x.Code == CssDiagnosticCodes.InvalidSelector && x.Message.Contains("过于复杂"));
    }

    [Fact]
    public void Deepest_allowed_selector_matches_on_one_megabyte_stack()
    {
        // 31 层 :is()，每层 30 个复合选择器；最内层在最左侧，匹配时递归深度约 31 × 30
        string sel = "a";
        for (int i = 0; i < 31; i++) sel = ":is(" + sel + ")" + string.Concat(Enumerable.Repeat(" + a", 29));
        var list = SelectorParser.Parse(sel);
        Assert.NotNull(list);
        Assert.True(list!.MatchDepth <= SelectorParser.MaxMatchDepth);

        var parent = new El("p");
        for (int i = 0; i < 1000; i++) parent.Add(new El("a"));
        var last = parent[parent.Children.Count - 1];
        OnOneMegabyteStack(() => Assert.True(SelectorMatcher.Matches(list, last)));

        var flat = SelectorParser.Parse("a" + string.Concat(Enumerable.Repeat(" ~ a", 999)));
        Assert.NotNull(flat);
        OnOneMegabyteStack(() => Assert.True(SelectorMatcher.Matches(flat!, last)));
    }

    static El Chain(int depth, string tag = "a")
    {
        var root = new El(tag);
        var e = root;
        for (int i = 1; i < depth; i++)
        {
            var c = new El(tag);
            e.Add(c);
            e = c;
        }
        return e;
    }

    static CssStyleRule Deepest(StyleSheet s) => s.EnumerateRules().OfType<CssStyleRule>().Last();

    [Fact]
    public void Nested_ampersand_combinators_match_in_polynomial_time()
    {
        // a { & & { & & { … } } }：每层都是 :is(父) :is(父)，无记忆时匹配耗时随树深指数增长
        const int levels = 60;
        var css = "a{" + string.Concat(Enumerable.Repeat("& &{", levels)) + new string('}', levels + 1);
        var rule = Deepest(StyleSheet.Parse(css));
        var sw = Stopwatch.StartNew();
        Assert.False(SelectorMatcher.Matches(rule.Selectors, Chain(40)));
        Assert.True(SelectorMatcher.Matches(rule.Selectors, Chain(levels + 1)));
        Assert.False(SelectorMatcher.Matches(rule.Selectors, Chain(levels)));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 5000, $"耗时 {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Nested_is_with_combinators_is_fast_on_deep_tree()
    {
        var list = SelectorParser.Parse(":is(a :is(a :is(a :is(a b) a) a) a) a")!;
        var leaf = Chain(400);
        var sw = Stopwatch.StartNew();
        Assert.False(SelectorMatcher.Matches(list, leaf));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"耗时 {sw.ElapsedMilliseconds} ms");

        // 正例仍然正确
        var b = new El("b");
        var top = new El("a").Add(new El("a").Add(b.Add(new El("a").Add(new El("a").Add(new El("a").Add(new El("a")))))));
        var deepest = (El)top[0][0][0][0][0][0];
        Assert.True(SelectorMatcher.Matches(SelectorParser.Parse(":is(a b) a")!, deepest));
        Assert.True(SelectorMatcher.Matches(SelectorParser.Parse(":is(a :is(a b) a) a")!, deepest));
        Assert.False(SelectorMatcher.Matches(SelectorParser.Parse(":is(b :is(a b) a) a")!, deepest));
    }

    [Fact]
    public void Memo_is_per_match_and_sees_state_changes()
    {
        var list = SelectorParser.Parse(":is(.x a) a")!;
        var leaf = Chain(5);
        Assert.False(SelectorMatcher.Matches(list, leaf));
        var top = (El)leaf.Parent!.Parent!.Parent!.Parent!;
        var tree = new El("a", classes: "x").Add(top);
        Assert.True(SelectorMatcher.Matches(list, leaf));
        Assert.NotNull(tree);
    }

    // ---------- ASCII 大小写不敏感 ----------

    [Fact]
    public void Type_selector_folds_only_ascii_case()
    {
        Assert.True(SelectorMatcher.Matches(SelectorParser.Parse("button")!, new El("Button")));
        Assert.False(SelectorMatcher.Matches(SelectorParser.Parse("é")!, new El("É")));
        Assert.False(SelectorMatcher.Matches(SelectorParser.Parse("ǅ")!, new El("ǆ")));
    }

    [Theory]
    [InlineData("[t=\"Abc\" i]", "aBC", true)]
    [InlineData("[t=\"é\" i]", "É", false)]
    [InlineData("[t^=\"É\" i]", "éx", false)]
    [InlineData("[t$=\"X\" i]", "ax", true)]
    [InlineData("[t*=\"B\" i]", "abc", true)]
    [InlineData("[t*=\"Ä\" i]", "aäc", false)]
    [InlineData("[t~=\"B\" i]", "a b c", true)]
    [InlineData("[t|=\"EN\" i]", "en-US", true)]
    [InlineData("[t|=\"EN\" i]", "enx", false)]
    public void Attribute_i_flag_folds_only_ascii_case(string selector, string value, bool expected)
        => Assert.Equal(expected, SelectorMatcher.Matches(SelectorParser.Parse(selector)!, new El("a").Attr("t", value)));

    [Fact]
    public void Of_type_counts_with_ascii_case()
    {
        var p = new El("p").Add(new El("É"), new El("é"), new El("Item"), new El("item"));
        Assert.True(SelectorMatcher.Matches(SelectorParser.Parse(":first-of-type")!, p[1]));
        Assert.False(SelectorMatcher.Matches(SelectorParser.Parse(":first-of-type")!, p[3]));
    }

    [Fact]
    public void Index_tag_bucket_is_ascii_case_insensitive()
    {
        var index = new SelectorIndex<int>();
        index.Add(SelectorParser.Parse("button")!, 1);
        index.Add(SelectorParser.Parse("é")!, 2);
        var r = new List<SelectorIndex<int>.Entry>();
        index.CollectCandidates(new El("BUTTON"), r);
        Assert.Equal(new[] { 1 }, r.Select(e => e.Value));
        r.Clear();
        index.CollectCandidates(new El("É"), r);
        Assert.Empty(r);
    }

    // ---------- 诊断 ----------

    [Theory]
    [InlineData("color")]
    [InlineData("color: red; c")]
    [InlineData("color red; margin: 0")]
    public void Invalid_inline_declaration_is_reported_as_declaration(string text)
    {
        var d = new List<CssDiagnostic>();
        StyleSheet.ParseDeclarations(text, d);
        var diag = Assert.Single(d);
        Assert.Equal(CssDiagnosticCodes.SyntaxError, diag.Code);
        Assert.Contains("无效的声明", diag.Message);
        Assert.DoesNotContain("声明块", diag.Message);
    }

    [Fact]
    public void Keyframe_important_uses_misplaced_declaration_code()
    {
        var s = StyleSheet.Parse("@keyframes k { from { color: red !important } }");
        var d = Assert.Single(s.Diagnostics);
        Assert.Equal(CssDiagnosticCodes.MisplacedDeclaration, d.Code);
        Assert.Equal(CssDiagnosticSeverity.Warning, d.Severity);
    }
}
