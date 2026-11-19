using AwAUI.Css.Selectors;

namespace AwAUI.Css.UnitTests;

public class NestingTests
{
    static CssStyleRule Style(CssRule r) => Assert.IsType<CssStyleRule>(r);

    [Fact]
    public void Nested_rules_get_implicit_nesting_selector()
    {
        var s = StyleSheet.Parse(".a { color: red; .b { color: blue } &:hover { x: 1 } > .c { } + .d {} .e & {} }");
        Assert.Empty(s.Diagnostics);
        var a = Style(Assert.Single(s.Rules));
        Assert.Single(a.Declarations);
        var children = a.ChildRules.Cast<CssStyleRule>().ToArray();
        Assert.Equal(5, children.Length);

        Assert.Equal("& .b", children[0].Selectors[0].ToResolvedString());
        Assert.Equal(".b", children[0].Selectors[0].ToString());
        Assert.True(children[0].Selectors[0].HasImplicitNesting);
        Assert.Equal(new Specificity(0, 2, 0), children[0].Selectors[0].Specificity);

        Assert.Equal("&:hover", children[1].Selectors[0].ToResolvedString());
        Assert.False(children[1].Selectors[0].HasImplicitNesting);
        Assert.Equal(new Specificity(0, 2, 0), children[1].Selectors[0].Specificity);

        Assert.Equal("& > .c", children[2].Selectors[0].ToResolvedString());
        Assert.Equal("> .c", children[2].Selectors[0].ToString());
        Assert.Equal("& + .d", children[3].Selectors[0].ToResolvedString());
        Assert.Equal(".e &", children[4].Selectors[0].ToResolvedString());
        Assert.All(children, c => Assert.Same(a, c.ParentStyleRule));
        Assert.All(children, c => Assert.Same(a, c.Parent));
    }

    [Fact]
    public void Tag_with_pseudo_class_is_a_rule_not_a_declaration()
    {
        var s = StyleSheet.Parse(".p { a:hover { color: red } color: blue; div { } }");
        var p = Style(Assert.Single(s.Rules));
        Assert.Empty(p.Declarations);
        Assert.Equal(3, p.ChildRules.Count);
        Assert.Equal("a:hover", Style(p.ChildRules[0]).SelectorText);
        var nd = Assert.IsType<CssNestedDeclarationsRule>(p.ChildRules[1]);
        Assert.Equal("color", Assert.Single(nd.Declarations).Name);
        Assert.Same(p, nd.ParentStyleRule);
        Assert.Equal("div", Style(p.ChildRules[2]).SelectorText);
    }

    [Fact]
    public void Declarations_after_nested_rules_become_nested_declarations_rules()
    {
        var s = StyleSheet.Parse(".a { x: 1; .b { } y: 2; z: 3; .c { } w: 4 }");
        var a = Style(Assert.Single(s.Rules));
        Assert.Equal("x", Assert.Single(a.Declarations).Name);
        Assert.Equal(new[] { "Style", "NestedDeclarations", "Style", "NestedDeclarations" }, a.ChildRules.Select(r => r.Kind.ToString()));
        Assert.Equal(new[] { "y", "z" }, ((CssNestedDeclarationsRule)a.ChildRules[1]).Declarations.Select(d => d.Name));
    }

    [Fact]
    public void Nested_media_wraps_declarations_with_parent_selector()
    {
        var s = StyleSheet.Parse(".a { @media (width > 1px) { color: red; .b { } } }");
        Assert.Empty(s.Diagnostics);
        var a = Style(Assert.Single(s.Rules));
        var m = Assert.IsType<CssMediaRule>(Assert.Single(a.ChildRules));
        var nd = Assert.IsType<CssNestedDeclarationsRule>(m.Rules[0]);
        Assert.Same(a, nd.ParentStyleRule);
        Assert.Same(a.Selectors, nd.Selectors);
        Assert.Equal("& .b", Style(m.Rules[1]).Selectors[0].ToResolvedString());
        Assert.Same(a, Style(m.Rules[1]).ParentStyleRule);
    }

    [Fact]
    public void Nested_layer_block()
    {
        var s = StyleSheet.Parse(".a { @layer x { color: red; } }");
        var l = Assert.IsType<CssLayerBlockRule>(Assert.Single(Style(Assert.Single(s.Rules)).ChildRules));
        Assert.IsType<CssNestedDeclarationsRule>(Assert.Single(l.Rules));
    }

    [Fact]
    public void Nesting_selector_specificity_uses_most_specific_parent()
    {
        var s = StyleSheet.Parse(".a, #b { .c { } }");
        var c = Style(Style(s.Rules[0]).ChildRules[0]);
        Assert.Equal(new Specificity(1, 1, 0), c.Selectors[0].Specificity);
    }

    [Fact]
    public void Nested_selector_matching()
    {
        var s = StyleSheet.Parse(".a { .b { } & > .c { } .x & { } &.b { } }");
        var a = Style(s.Rules[0]);
        var desc = Style(a.ChildRules[0]).Selectors;
        var child = Style(a.ChildRules[1]).Selectors;
        var reversed = Style(a.ChildRules[2]).Selectors;
        var same = Style(a.ChildRules[3]).Selectors;

        var inner = new El("Div", classes: "b");
        var c = new El("Div", classes: "c");
        var aEl = new El("Div", classes: "a");
        var root = new El("Div", classes: "x").Add(aEl.Add(new El("Div").Add(inner), c));
        var outside = new El("Div", classes: "b");
        root.Add(outside);
        var both = new El("Div", classes: "a b");
        root.Add(both);

        Assert.True(SelectorMatcher.Matches(desc, inner));
        Assert.False(SelectorMatcher.Matches(desc, outside));
        Assert.True(SelectorMatcher.Matches(child, c));
        Assert.False(SelectorMatcher.Matches(child, inner));
        Assert.True(SelectorMatcher.Matches(reversed, aEl));
        Assert.True(SelectorMatcher.Matches(same, both));
        Assert.False(SelectorMatcher.Matches(same, inner));
    }

    [Fact]
    public void Deep_nesting_chains_parents()
    {
        var s = StyleSheet.Parse(".a { .b { .c { } } }");
        var c = Style(Style(Style(s.Rules[0]).ChildRules[0]).ChildRules[0]);
        Assert.Equal(new Specificity(0, 3, 0), c.Selectors[0].Specificity);
        var leaf = new El("Div", classes: "c");
        new El("Div", classes: "a").Add(new El("Div", classes: "b").Add(leaf));
        Assert.True(SelectorMatcher.Matches(c.Selectors, leaf));
        var orphan = new El("Div", classes: "c");
        new El("Div", classes: "b").Add(orphan);
        Assert.False(SelectorMatcher.Matches(c.Selectors, orphan));
    }

    [Fact]
    public void Top_level_nesting_selector_matches_root()
    {
        var s = StyleSheet.Parse("& .x { }");
        var sel = Style(s.Rules[0]).Selectors;
        Assert.Equal(new Specificity(0, 1, 0), sel[0].Specificity);
        var x = new El("Div", classes: "x");
        new El("Root").Add(x);
        Assert.True(SelectorMatcher.Matches(sel, x));
    }

    [Fact]
    public void Nested_rule_with_invalid_selector_is_dropped_only_itself()
    {
        var s = StyleSheet.Parse(".a { :foo { } color: red; .ok { } }");
        var a = Style(Assert.Single(s.Rules));
        Assert.Single(s.Diagnostics);
        Assert.Equal("color", Assert.Single(a.Declarations).Name);
        Assert.Single(a.ChildRules);
    }

    [Fact]
    public void Invalid_parent_drops_children()
    {
        var s = StyleSheet.Parse(".a:foo { .b { } } .c { }");
        Assert.Equal(".c", Style(Assert.Single(s.Rules)).SelectorText);
    }

    [Fact]
    public void Missing_semicolon_inside_nested_block_recovers_at_next_semicolon()
    {
        var s = StyleSheet.Parse(".a { color: red .b { } x: 1; y: 2 }");
        var a = Style(Assert.Single(s.Rules));
        // 'color: red .b { }' 中出现了 {} 块，不是合法声明；作为规则其选择器无效被丢弃。
        // 无效规则之前没有声明，因此其后的声明仍是规则自身的声明（CSS Syntax「消费块内容」）
        Assert.Equal(new[] { "x", "y" }, a.Declarations.Select(d => d.Name));
        Assert.Empty(a.ChildRules);
        Assert.NotEmpty(s.Diagnostics);
    }
}
