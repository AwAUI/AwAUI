namespace AwAUI.Css.UnitTests;

/// <summary>
/// 增量解析（design/06 的 DevTools 编辑与运行时补全）：从第一个可能变化的顶层规则起点重新解析尾部，
/// 复用前缀规则与诊断对象（它们的绝对源码区间在新文本中仍然有效），结果与整段解析等价。
/// </summary>
public class IncrementalParseTests
{
    static void AssertSameAsFullParse(string text, StyleSheet incremental)
    {
        var full = StyleSheet.Parse(text, incremental.SourceName);
        Assert.Equal(full.Rules.Count, incremental.Rules.Count);
        for (int i = 0; i < full.Rules.Count; i++)
        {
            Assert.Equal(full.Rules[i].GetType(), incremental.Rules[i].GetType());
            Assert.Equal(full.Rules[i].Range, incremental.Rules[i].Range);
        }
        Assert.Equal(
            full.Diagnostics.Select(d => (d.Code, d.Range)),
            incremental.Diagnostics.Select(d => (d.Code, d.Range)));
    }

    [Fact]
    public void Prefix_rules_are_reused_and_tail_ranges_are_absolute()
    {
        const string before = ".a { color: red; }\n.b { color: blue; }\n";
        var previous = StyleSheet.Parse(before, "devtools");
        Assert.Equal(2, previous.Rules.Count);

        const string after = ".a { color: red; }\n.b { color: green; }\n.c { color: black; }";
        int changedAt = after.IndexOf(".b", StringComparison.Ordinal);
        var next = StyleSheet.ParseIncremental(after, "devtools", previous, changedAt);

        // .a 在变化点之前：同一个规则对象，源码区间直接沿用
        Assert.Same(previous.Rules[0], next.Rules[0]);
        Assert.Equal(3, next.Rules.Count);
        // 尾部规则的区间相对整段新文本，而不是相对尾部
        Assert.Equal(after.IndexOf(".b", StringComparison.Ordinal), next.Rules[1].Range.Start.Offset);
        Assert.Equal(after.IndexOf(".c", StringComparison.Ordinal), next.Rules[2].Range.Start.Offset);
        Assert.Equal(after.Length, next.Rules[2].Range.End.Offset);
        Assert.Equal(after, next.Text);
        Assert.Equal("devtools", next.SourceName);
        AssertSameAsFullParse(after, next);
    }

    [Fact]
    public void Group_rules_and_keyframes_in_the_tail_keep_full_text_ranges()
    {
        const string before = "@layer base, utilities;\n.a { color: red }\n@media (min-width: 10px) { .b { color: blue } }\n";
        const string after = "@layer base, utilities;\n.a { color: red }\n@media (min-width: 10px) { .b { color: green } }\n@keyframes spin { to { rotate: 1turn } }\n.c { margin: 0 }\n";
        var previous = StyleSheet.Parse(before);
        var next = StyleSheet.ParseIncremental(after, null, previous, after.IndexOf("@media", StringComparison.Ordinal));

        Assert.Same(previous.Rules[0], next.Rules[0]);
        Assert.Same(previous.Rules[1], next.Rules[1]);
        Assert.Equal(5, next.Rules.Count);
        Assert.Equal(after.IndexOf("@keyframes", StringComparison.Ordinal), next.Rules[3].Range.Start.Offset);
        AssertSameAsFullParse(after, next);
    }

    [Fact]
    public void Prefix_diagnostics_are_reused_by_reference()
    {
        const string before = ".a { color: red; }\n@import url(x.css);\n.b { color: blue; }";
        var previous = StyleSheet.Parse(before, "devtools");
        var importError = Assert.Single(previous.Diagnostics);

        const string after = ".a { color: red; }\n@import url(x.css);\n.b { color: green; }\n.c { margin: 0 }";
        var next = StyleSheet.ParseIncremental(after, "devtools", previous, after.IndexOf(".b", StringComparison.Ordinal));

        Assert.Same(importError, Assert.Single(next.Diagnostics));
        Assert.Same(previous.Rules[0], next.Rules[0]);
        AssertSameAsFullParse(after, next);
    }

    [Fact]
    public void Import_stays_allowed_when_the_reused_prefix_is_only_layer_statements()
    {
        const string before = "@layer base, utilities;\n.a { color: red; }";
        const string after = "@layer base, utilities;\n@import url(x.css);\n.a { color: red; }";
        var previous = StyleSheet.Parse(before);
        var next = StyleSheet.ParseIncremental(after, null, previous, before.IndexOf(".a", StringComparison.Ordinal));

        // 前缀只有 @layer 语句：@import 仍然位于允许的位置，不应报告顺序错误
        Assert.Empty(next.Diagnostics);
        Assert.Equal(3, next.Rules.Count);
        Assert.Same(previous.Rules[0], next.Rules[0]);
        AssertSameAsFullParse(after, next);
    }

    [Fact]
    public void A_changed_prefix_falls_back_to_a_full_parse()
    {
        const string before = ".a { color: red; }\n.b { color: blue; }";
        const string after = ".x { color: red; }\n.b { color: green; }";
        var previous = StyleSheet.Parse(before);
        // 声称 .b 之前没有变化，但前缀实际不同
        var next = StyleSheet.ParseIncremental(after, null, previous, after.IndexOf(".b", StringComparison.Ordinal));

        Assert.NotSame(previous.Rules[0], next.Rules[0]);
        AssertSameAsFullParse(after, next);
    }

    [Fact]
    public void A_change_inside_a_rule_falls_back_to_a_full_parse()
    {
        const string before = ".a { color: red; }\n.b { color: blue; }";
        const string after = ".a { color: red; }\n.b { color: green; }";
        var previous = StyleSheet.Parse(before);
        // 变化点在 .b 内部，不能从规则中间继续
        var next = StyleSheet.ParseIncremental(after, null, previous, before.IndexOf("blue", StringComparison.Ordinal));

        Assert.NotSame(previous.Rules[0], next.Rules[0]);
        AssertSameAsFullParse(after, next);
    }

    [Fact]
    public void An_offset_past_the_previous_text_falls_back_to_a_full_parse()
    {
        var previous = StyleSheet.Parse("");
        var next = StyleSheet.ParseIncremental(".a { color: red; }", null, previous, 5);
        AssertSameAsFullParse(".a { color: red; }", next);
    }

    [Fact]
    public void An_offset_of_zero_behaves_like_a_full_parse()
    {
        const string before = ".a { color: red; }";
        const string after = "@import url(x.css);\n.b { color: blue; }";
        var previous = StyleSheet.Parse(before);
        var next = StyleSheet.ParseIncremental(after, null, previous, 0);

        // 从头解析时 @import 仍位于样式表开头，不报告顺序错误
        Assert.Empty(next.Diagnostics);
        AssertSameAsFullParse(after, next);
    }

    /// <summary>覆盖注释、@layer 语句与块、@import、@media、@keyframes、@font-face、嵌套规则和未知 at 规则的样例。</summary>
    const string Corpus = """
        /* leading comment */
        @layer theme, base, components, utilities;
        @import url(reset.css);
        @layer theme {
          :root { --x: 1; }
        }
        @media (min-width: 10px) {
          .a { color: red; }
          .b:hover { color: blue; }
        }
        @keyframes spin { to { rotate: 1turn; } }
        @font-face { font-family: "Test"; src: url(test.woff2); }
        .outer {
          color: green;
          & .inner { margin: 0; }
        }
        .c { padding: 1px }
        """;

    [Fact]
    public void Inserting_a_rule_at_every_top_level_boundary_matches_a_full_parse()
    {
        var previous = StyleSheet.Parse(Corpus, "corpus");
        Assert.NotEmpty(previous.Rules);
        foreach (int at in previous.Rules.Select(r => r.Range.Start.Offset))
        {
            string after = Corpus.Insert(at, ".zz { color: red; }\n");
            var next = StyleSheet.ParseIncremental(after, "corpus", previous, at);
            AssertSameAsFullParse(after, next);
            // 变化点之前的规则按引用复用，尾部规则使用新文本的绝对区间
            foreach (var rule in previous.Rules)
            {
                if (rule.Range.End.Offset > at) break;
                Assert.True(next.Rules.Any(r => ReferenceEquals(r, rule)), $"prefix rule at {rule.Range.Start.Offset} was not reused");
            }
        }
    }

    [Fact]
    public void Replacing_the_tail_from_every_top_level_boundary_matches_a_full_parse()
    {
        var previous = StyleSheet.Parse(Corpus, "corpus");
        foreach (int at in previous.Rules.Select(r => r.Range.Start.Offset))
        {
            string after = Corpus.Substring(0, at) + ".tail { margin: 0 }";
            var next = StyleSheet.ParseIncremental(after, "corpus", previous, at);
            AssertSameAsFullParse(after, next);
        }
    }

    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        var previous = StyleSheet.Parse(".a { color: red; }");
        Assert.Throws<ArgumentNullException>(() => StyleSheet.ParseIncremental(null!, null, previous, 0));
        Assert.Throws<ArgumentNullException>(() => StyleSheet.ParseIncremental(".a { }", null, null!, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => StyleSheet.ParseIncremental(".a { }", null, previous, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StyleSheet.ParseIncremental(".a { }", null, previous, 7));
    }
}
