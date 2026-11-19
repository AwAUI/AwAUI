using AwAUI.Css.Selectors;

namespace AwAUI.Css.UnitTests;

public class SelectorIndexTests
{
    [Fact]
    public void Buckets_by_rightmost_key_and_collects_in_source_order()
    {
        var sheet = StyleSheet.Parse(@"
            .item { }
            #main .item.special { }
            Item { }
            * { }
            .other { }
            #x { }
            .list > :hover { }
            .btn { &:hover { } & .icon { } }
            x::part(label) { }
        ");
        Assert.Empty(sheet.Diagnostics);
        var index = new SelectorIndex<CssStyleRule>();
        foreach (var r in sheet.EnumerateRules().OfType<CssStyleRule>()) index.Add(r.Selectors, r);
        Assert.Equal(11, index.Count);
        Assert.Equal(2, index.UniversalCount); // '*' 与 '.list > :hover'；'&:hover' 借用了父规则的 .btn

        var item = new El("Item", classes: "item special");
        new El("Div", id: "main").Add(new El("Div", classes: "list").Add(item));

        var candidates = new List<SelectorIndex<CssStyleRule>.Entry>();
        index.CollectCandidates(item, candidates);
        Assert.Equal(new[] { ".item", "#main .item.special", "Item", "*", ".list > :hover" }, candidates.Select(c => c.Selector.ToString()));

        var matches = new List<SelectorIndex<CssStyleRule>.Entry>();
        index.CollectMatches(item, matches);
        Assert.Equal(new[] { ".item", "#main .item.special", "Item", "*" }, matches.Select(c => c.Selector.ToString()));
        Assert.True(matches.Select(m => m.Order).SequenceEqual(matches.Select(m => m.Order).OrderBy(o => o)));

        item.State = PseudoStates.Hover;
        matches.Clear();
        index.CollectMatches(item, matches);
        Assert.Contains(matches, m => m.Selector.ToString() == ".list > :hover");
    }

    [Fact]
    public void Nested_selectors_and_parts_are_found()
    {
        var sheet = StyleSheet.Parse(".btn { &:hover { a: 1 } & .icon { b: 2 } } x::part(label) { c: 3 }");
        var index = new SelectorIndex<string>();
        foreach (var r in sheet.EnumerateRules().OfType<CssStyleRule>()) index.Add(r.Selectors, r.SelectorText);

        var icon = new El("Span", classes: "icon");
        var btn = new El("Button", classes: "btn") { State = PseudoStates.Hover }.Add(icon);
        var host = new El("X");
        var label = new El("Span").Part("label");
        label.Host = host;
        host.Add(label);

        var m = new List<SelectorIndex<string>.Entry>();
        index.CollectMatches(btn, m);
        Assert.Equal(new[] { ".btn", "&:hover" }, m.Select(e => e.Value));
        m.Clear();
        index.CollectMatches(icon, m);
        Assert.Equal(new[] { "& .icon" }, m.Select(e => e.Value));
        m.Clear();
        index.CollectMatches(label, m);
        Assert.Equal(new[] { "x::part(label)" }, m.Select(e => e.Value));
    }

    [Fact]
    public void Pseudo_element_rules_are_collected_separately()
    {
        var index = new SelectorIndex<int>();
        index.Add(SelectorParser.Parse(".a::before")!, 1);
        index.Add(SelectorParser.Parse(".a")!, 2);
        var el = new El("Div", classes: "a a"); // 重复 class 不产生重复候选
        var m = new List<SelectorIndex<int>.Entry>();
        index.CollectMatches(el, m);
        Assert.Equal(new[] { 2 }, m.Select(e => e.Value));
        m.Clear();
        index.CollectMatches(el, m, PseudoElementKind.Before);
        Assert.Equal(new[] { 1 }, m.Select(e => e.Value));
    }
}
