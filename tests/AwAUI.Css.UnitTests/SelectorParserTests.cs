using AwAUI.Css.Selectors;

namespace AwAUI.Css.UnitTests;

public class SelectorParserTests
{
    static SelectorList P(string s)
    {
        var d = new List<CssDiagnostic>();
        var list = SelectorParser.Parse(s, d);
        Assert.True(list is not null, $"'{s}' 应能解析：{string.Join("; ", d)}");
        return list!;
    }

    static List<CssDiagnostic> Fails(string s)
    {
        var d = new List<CssDiagnostic>();
        Assert.Null(SelectorParser.Parse(s, d));
        Assert.NotEmpty(d);
        return d;
    }

    // Selectors 4 §17 的示例与常见组合
    [Theory]
    [InlineData("*", 0, 0, 0)]
    [InlineData("li", 0, 0, 1)]
    [InlineData("ul li", 0, 0, 2)]
    [InlineData("ul ol+li", 0, 0, 3)]
    [InlineData("h1 + *[rel=up]", 0, 1, 1)]
    [InlineData("ul ol li.red", 0, 1, 3)]
    [InlineData("li.red.level", 0, 2, 1)]
    [InlineData("#x34y", 1, 0, 0)]
    [InlineData("#s12:not(FOO)", 1, 0, 1)]
    [InlineData(".foo :is(.bar, #baz)", 1, 1, 0)]
    [InlineData(":where(#a, .b) .c", 0, 1, 0)]
    [InlineData(":is(:where(#a), .b)", 0, 1, 0)]
    [InlineData(":not(.a, #b)", 1, 0, 0)]
    [InlineData(":nth-child(2n+1)", 0, 1, 0)]
    [InlineData(":nth-child(2n+1 of .a, #b)", 1, 1, 0)]
    [InlineData(":nth-last-child(odd of li.x)", 0, 2, 1)]
    [InlineData(":nth-of-type(2)", 0, 1, 0)]
    [InlineData("a::before", 0, 0, 2)]
    [InlineData("a:before", 0, 0, 2)]
    [InlineData("::placeholder", 0, 0, 1)]
    [InlineData("x-btn::part(label)", 0, 0, 2)]
    [InlineData("x-btn::part(label):hover", 0, 1, 2)]
    [InlineData("[x]", 0, 1, 0)]
    [InlineData("[x='y' i]", 0, 1, 0)]
    [InlineData(":hover:focus-visible", 0, 2, 0)]
    [InlineData("&", 0, 0, 0)]
    [InlineData("*|a", 0, 0, 1)]
    [InlineData("|a", 0, 0, 1)]
    [InlineData("div#a.b.c[d]:hover::after", 1, 4, 2)]
    public void Specificity_table(string selector, int a, int b, int c)
    {
        var list = P(selector);
        Assert.Single(list.Selectors);
        Assert.Equal(new Specificity(a, b, c), list[0].Specificity);
    }

    [Fact]
    public void Specificity_ordering()
    {
        Assert.True(new Specificity(1, 0, 0) > new Specificity(0, 99, 99));
        Assert.True(new Specificity(0, 1, 0) > new Specificity(0, 0, 99));
        Assert.Equal("(1,2,3)", new Specificity(1, 2, 3).ToString());
    }

    [Theory]
    [InlineData("a > b + c ~ d e")]
    [InlineData(".a.b#c")]
    [InlineData("[data-x=\"y\" i]")]
    [InlineData("[a|=\"en\"]")]
    [InlineData(":not(.a, .b)")]
    [InlineData(":is(.a > .b, .c)")]
    [InlineData(":nth-child(2n+1 of .a)")]
    [InlineData(":nth-last-of-type(-n+3)")]
    [InlineData("x::part(a b):hover")]
    [InlineData(".\\31 23")]
    [InlineData("a::before")]
    public void Serialization_round_trips(string selector)
    {
        Assert.Equal(selector, P(selector).ToString());
    }

    [Fact]
    public void Selector_list_and_combinators()
    {
        var list = P(" a > b , c~d  e+f ");
        Assert.Equal(2, list.Count);
        Assert.Equal(new[] { Combinator.Child }, list[0].Combinators);
        Assert.Equal(new[] { Combinator.SubsequentSibling, Combinator.Descendant, Combinator.NextSibling }, list[1].Combinators);
        Assert.Equal("a > b, c ~ d e + f", list.ToString());
    }

    [Fact]
    public void Comments_between_compounds_count_as_nothing()
    {
        // 注释不产生记号：a/**/b 是 'ab' 吗？不：是两个相邻记号，类型选择器必须在开头 -> 无效
        Fails("a/**/b");
        Assert.Equal(new[] { Combinator.Descendant }, P("a /**/ b")[0].Combinators);
    }

    [Theory]
    [InlineData("odd", 2, 1)]
    [InlineData("even", 2, 0)]
    [InlineData("EVEN", 2, 0)]
    [InlineData("3", 0, 3)]
    [InlineData("-3", 0, -3)]
    [InlineData("+5", 0, 5)]
    [InlineData("n", 1, 0)]
    [InlineData("N", 1, 0)]
    [InlineData("-n", -1, 0)]
    [InlineData("+n", 1, 0)]
    [InlineData("2n", 2, 0)]
    [InlineData("2n+1", 2, 1)]
    [InlineData("2N+1", 2, 1)]
    [InlineData("2n + 1", 2, 1)]
    [InlineData("2n- 1", 2, -1)]
    [InlineData("2n -1", 2, -1)]
    [InlineData("2n - 1", 2, -1)]
    [InlineData("2n-1", 2, -1)]
    [InlineData("-n+3", -1, 3)]
    [InlineData("-n-3", -1, -3)]
    [InlineData("-n- 3", -1, -3)]
    [InlineData("n-3", 1, -3)]
    [InlineData("+n-3", 1, -3)]
    [InlineData("+n+3", 1, 3)]
    [InlineData("-2n+3", -2, 3)]
    [InlineData("0n+5", 0, 5)]
    [InlineData(" 3n ", 3, 0)]
    public void AnB_microsyntax(string anb, int a, int b)
    {
        var nth = Assert.IsType<NthSelector>(P($":nth-child({anb})")[0].Subject.Selectors[0]);
        Assert.Equal(a, nth.A);
        Assert.Equal(b, nth.B);
    }

    [Theory]
    [InlineData("+ n")]
    [InlineData("2n++1")]
    [InlineData("1.5n")]
    [InlineData("1.5")]
    [InlineData("n 3")]
    [InlineData("- n")]
    [InlineData("3 n")]
    [InlineData("--n")]
    [InlineData("2n - -1")]
    [InlineData("2n + +1")]
    [InlineData("n-")]
    [InlineData("foo")]
    [InlineData("")]
    [InlineData("2px")]
    [InlineData("n-a")]
    public void Invalid_AnB(string anb)
    {
        var d = Fails($":nth-child({anb})");
        Assert.Equal(CssDiagnosticCodes.InvalidSelector, d[0].Code);
    }

    [Fact]
    public void Nth_of_type_does_not_accept_of()
    {
        Fails(":nth-of-type(2n of .a)");
    }

    [Theory]
    [InlineData(":hover:foo")]
    [InlineData(":has(.a)")]
    [InlineData("::first-line")]
    [InlineData(":first-letter")]
    [InlineData(":visited")]
    [InlineData(":lang(en)")]
    [InlineData("::marker")]
    [InlineData("::-webkit-scrollbar")]
    [InlineData("::before:hover")]
    [InlineData("::part(a)::before")]
    [InlineData("svg|rect")]
    [InlineData("::slotted(x)")]
    public void Unsupported_features_report_AW2004(string selector)
    {
        var d = Fails(selector);
        Assert.Equal(CssDiagnosticCodes.UnsupportedSelector, d.Last().Code);
        Assert.Equal(CssDiagnosticSeverity.Error, d.Last().Severity);
    }

    [Theory]
    [InlineData(".a >")]
    [InlineData("> .a")]
    [InlineData(".a,")]
    [InlineData(",.a")]
    [InlineData("#1a")]
    [InlineData(".")]
    [InlineData(".1")]
    [InlineData("a::before .b")]
    [InlineData(".a div")]
    [InlineData(".adiv[")]
    [InlineData("[a=b c]")]
    [InlineData("[a==b]")]
    [InlineData("[a=]")]
    [InlineData("[a='b' x]")]
    [InlineData("[]")]
    [InlineData("[1]")]
    [InlineData("a:")]
    [InlineData(":not()")]
    [InlineData(":not(::before)")]
    [InlineData(":nth-child(1 of ::before)")]
    [InlineData("a b(")]
    [InlineData(".a{")]
    [InlineData("::part()")]
    [InlineData("::part(1)")]
    [InlineData(".a.b c.d e")]
    public void Invalid_selectors_report_errors(string selector)
    {
        var d = new List<CssDiagnostic>();
        var list = SelectorParser.Parse(selector, d);
        if (selector == ".a div" || selector == ".a.b c.d e")
        {
            Assert.NotNull(list); // 合法的对照组
            return;
        }
        Assert.Null(list);
        Assert.Contains(d, x => x.Severity == CssDiagnosticSeverity.Error);
    }

    [Fact]
    public void Forgiving_lists_drop_invalid_items_with_warning()
    {
        var d = new List<CssDiagnostic>();
        var list = SelectorParser.Parse(":is(.a, :foo, .b, ::before)", d);
        Assert.NotNull(list);
        var logical = Assert.IsType<LogicalSelector>(list![0].Subject.Selectors[0]);
        Assert.Equal(".a, .b", logical.Arguments.ToString());
        Assert.Equal(2, d.Count);
        Assert.All(d, x => Assert.Equal(CssDiagnosticSeverity.Warning, x.Severity));
        Assert.Equal(CssDiagnosticCodes.UnsupportedSelector, d[0].Code);
    }

    [Fact]
    public void Empty_forgiving_list_is_valid_but_matches_nothing()
    {
        var list = P(":where(:foo)");
        var el = new El("Div");
        Assert.False(SelectorMatcher.Matches(list, el));
        Assert.Equal(Specificity.Zero, list[0].Specificity);
    }

    [Fact]
    public void Not_is_not_forgiving()
    {
        Fails(":not(.a, :foo)");
    }

    [Fact]
    public void Attribute_selector_forms()
    {
        var cases = new (string sel, AttributeOperator op, string value, AttributeCaseSensitivity cs)[]
        {
            ("[a]", AttributeOperator.Exists, "", AttributeCaseSensitivity.Default),
            ("[ a = b ]", AttributeOperator.Equals, "b", AttributeCaseSensitivity.Default),
            ("[a~='x y']", AttributeOperator.Includes, "x y", AttributeCaseSensitivity.Default),
            ("[a|=en]", AttributeOperator.DashMatch, "en", AttributeCaseSensitivity.Default),
            ("[a^=\"p\" i]", AttributeOperator.Prefix, "p", AttributeCaseSensitivity.Insensitive),
            ("[a$=s S]", AttributeOperator.Suffix, "s", AttributeCaseSensitivity.Sensitive),
            ("[a*=mid]", AttributeOperator.Substring, "mid", AttributeCaseSensitivity.Default),
            ("[*|a=b]", AttributeOperator.Equals, "b", AttributeCaseSensitivity.Default),
            ("[|a]", AttributeOperator.Exists, "", AttributeCaseSensitivity.Default),
        };
        foreach (var (sel, op, value, cs) in cases)
        {
            var attr = Assert.IsType<AttributeSelector>(P(sel)[0].Subject.Selectors[0]);
            Assert.Equal("a", attr.Name);
            Assert.Equal(op, attr.Operator);
            Assert.Equal(value, attr.Value);
            Assert.Equal(cs, attr.CaseSensitivity);
        }
    }

    [Fact]
    public void Pseudo_elements_are_split_from_compound()
    {
        var c = P("x::part(label icon):hover")[0].Subject;
        Assert.IsType<TypeSelector>(Assert.Single(c.Selectors));
        Assert.Equal(PseudoElementKind.Part, c.PseudoElement!.PseudoElement);
        Assert.Equal(new[] { "label", "icon" }, c.PseudoElement.PartNames);
        Assert.Equal(PseudoClass.Hover, Assert.IsType<PseudoClassSelector>(Assert.Single(c.PseudoElementStates)).PseudoClass);

        var legacy = P("a:after")[0].PseudoElement!;
        Assert.True(legacy.IsLegacySyntax);
        Assert.Equal(PseudoElementKind.After, legacy.PseudoElement);
    }

    [Fact]
    public void Case_insensitive_keywords()
    {
        Assert.Equal(PseudoClass.Hover, Assert.IsType<PseudoClassSelector>(P(":HOVER")[0].Subject.Selectors[0]).PseudoClass);
        Assert.IsType<LogicalSelector>(P(":NOT(.a)")[0].Subject.Selectors[0]);
        Assert.Equal(PseudoElementKind.Before, P("::BEFORE")[0].PseudoElement!.PseudoElement);
    }

    [Fact]
    public void Diagnostic_ranges_point_at_offender()
    {
        const string css = ".ok {}\n.a:nope, .b { }";
        var s = StyleSheet.Parse(css);
        var d = Assert.Single(s.Diagnostics);
        Assert.Equal(CssDiagnosticCodes.UnsupportedSelector, d.Code);
        Assert.Equal(2, d.Range.Start.Line);
        Assert.Equal(":nope", css.Substring(d.Range.Start.Offset, d.Range.Length));
        Assert.Contains("nope", d.Message);
    }
}
