using AwAUI.Css.Cascade;
using AwAUI.Css.Selectors;
using AwAUI.Css.UnitTests.Cascade;
using AwAUI.Tests.Shared;

namespace AwAUI.Css.UnitTests;

/// <summary><c>:has()</c>：解析、匹配、缓存、上限与失效特征。</summary>
public class HasSelectorTests
{
    static bool M(string selector, StyleElement e)
    {
        var list = SelectorParser.Parse(selector);
        Assert.NotNull(list);
        return SelectorMatcher.Matches(list!, e);
    }

    static El Tree()
    {
        // <div#root> <section.a> <p.x/> <span><b.y/></span> </section> <section.b/> <i.z/> </div>
        return new El("div", "root").Add(
            new El("section", null, "a").Add(new El("p", null, "x"), new El("span").Add(new El("b", null, "y"))),
            new El("section", null, "b"),
            new El("i", null, "z"));
    }

    [Fact]
    public void Descendant_child_and_sibling_relative_selectors()
    {
        var root = Tree();
        var a = root[0];
        var b = root[1];
        Assert.True(M(":has(.y)", a));
        Assert.True(M(":has(> .x)", a));
        Assert.False(M(":has(> .y)", a));
        Assert.True(M(":has(> span > .y)", a));
        Assert.True(M(":has(span .y)", a));
        Assert.False(M(":has(.y)", b));
        Assert.True(M(":has(+ .b)", a));
        Assert.False(M(":has(+ .z)", a));
        Assert.True(M(":has(~ .z)", a));
        Assert.True(M(":has(~ i.z)", b));
        Assert.False(M(":has(~ .a)", b));
        Assert.True(M(":has(.x + span)", a));
        Assert.True(M(":has(> p ~ span)", a));
        Assert.True(M("div:has(> section.b)", root));
        Assert.True(M(":has(.nope, .x)", a));
        Assert.True(M(":not(:has(.nope))", a));
        Assert.True(M(":is(:has(> .x))", a));
        // 兄弟形式中跟后代组合器：后续兄弟的子树
        Assert.True(M(":has(~ section .q, ~ i)", a));
    }

    [Fact]
    public void Has_in_non_subject_position()
    {
        var root = Tree();
        var y = root[0][1][0];
        Assert.True(M("section:has(.x) b", y));
        Assert.False(M("section:has(.z) b", y));
        Assert.True(M(":has(> section.b) .y", y));
    }

    [Fact]
    public void Relative_selectors_are_anchored_at_the_has_element()
    {
        // <div.a> <div#anchor.x> <div.a> <b.b/> </div> <i.b/> </div> <div.n><em.c/></div> <div.n2/> </div>
        var outer = new El("div", null, "a").Add(
            new El("div", "anchor", "x").Add(new El("div", null, "a").Add(new El("b", null, "b")), new El("i", null, "b")),
            new El("div", null, "n").Add(new El("em", null, "c")),
            new El("div", null, "n2"));
        var x = outer[0];
        // 外层的 .a 不在锚点的子树中，不能满足 :has(.a .b)；内层的 .a 可以
        Assert.True(M(":has(.a .b)", x));
        Assert.True(M(":has(.a > .b)", x));
        Assert.False(M(":has(.a > i.b)", x));
        Assert.False(M(":has(> .b > .b)", x));
        Assert.True(M(":has(> .a > b)", x));
        // 混合组合器：+ 之后的后代 / 子
        Assert.True(M(":has(+ .n > .c)", x));
        Assert.True(M(":has(+ .n .c)", x));
        Assert.False(M(":has(+ .n2)", x));
        Assert.True(M(":has(+ .n + .n2)", x));
        Assert.True(M(":has(~ .n2)", x));
        Assert.False(M(":has(~ .n2 .c)", x));
        Assert.True(M(":has(+ .n ~ .n2)", x));
        // 去掉内层 .a 后，只剩外层 .a，不再匹配
        var lone = new El("div", null, "a").Add(new El("div", null, "x").Add(new El("b", null, "b")));
        Assert.False(M(":has(.a .b)", lone[0]));
        Assert.True(M(".a :has(.b)", lone[0]));
    }

    [Theory]
    [InlineData(":has()")]
    [InlineData(":has(:has(.a))")]
    [InlineData(":has(::before)")]
    [InlineData(":has(> )")]
    [InlineData(":has(.a,)")]
    public void Invalid_has(string selector) => Assert.Null(SelectorParser.Parse(selector));

    [Fact]
    public void Specificity_is_the_most_specific_argument()
    {
        var s = SelectorParser.Parse("a:has(#x, .y)")!;
        Assert.Equal(new Specificity(1, 0, 1), s[0].Specificity);
        Assert.Equal(":has(.x, > .y, + .z)", SelectorParser.Parse(":has(.x, > .y, + .z)")![0].ToString());
        Assert.True(SelectorParser.Parse(":has(.x)")![0].ContainsHas);
        Assert.False(SelectorParser.Parse(":is(.x)")![0].ContainsHas);
    }

    [Fact]
    public void Cascade_uses_has_and_flags_dependence()
    {
        var engine = CascadeTestHelper.Engine("section:has(> .x) { color: red } section { color: blue } .x { color: green }");
        var root = Tree();
        var map = CascadeTestHelper.ResolveTree(engine, root);
        Assert.Equal("rgb(255, 0, 0)", map[(El)root[0]].GetPropertyValueText("color"));
        Assert.Equal("rgb(0, 0, 255)", map[(El)root[1]].GetPropertyValueText("color"));
        Assert.NotEqual(0, (int)(map[(El)root[0]].Flags & ComputedStyleFlags.DependsOnHas));
        Assert.Equal(0, (int)(map[(El)root[0][0]].Flags & ComputedStyleFlags.DependsOnHas));
    }

    [Fact]
    public void Shadcn_has_selectors_match()
    {
        // shadcn 的 has-[>svg]:px-3 与 has-data-[slot=card-action]:grid-cols-[1fr_auto]
        var engine = CascadeTestHelper.Engine(".has-\\[\\>svg\\]\\:px-3:has(>svg) { padding-left: 12px } .has-data-\\[slot\\=card-action\\]\\:grid-cols-\\[1fr_auto\\]:has([data-slot=card-action]) { grid-template-columns: 1fr auto }");
        var btn = new El("button", null, "has-[>svg]:px-3").Add(new El("svg"));
        var card = new El("div", null, "has-data-[slot=card-action]:grid-cols-[1fr_auto]").Add(new El("div").Add(new El("div").Attr("data-slot", "card-action")));
        var root = new El("body").Add(btn, card, new El("button", null, "has-[>svg]:px-3"));
        var map = CascadeTestHelper.ResolveTree(engine, root);
        Assert.Equal("12px", map[btn].GetPropertyValueText("padding-left"));
        Assert.Equal("0px", map[(El)root[2]].GetPropertyValueText("padding-left"));
        Assert.Equal("1fr auto", map[card].GetPropertyValueText("grid-template-columns"));
    }

    [Fact]
    public void Batch_cache_reuses_results_and_is_cleared_after()
    {
        var engine = CascadeTestHelper.Engine(".g:has(.hot) .item { color: red }");
        var items = new List<El>();
        var g = new El("div", null, "g");
        for (int i = 0; i < 50; i++)
        {
            var it = new El("p", null, "item");
            items.Add(it);
            g.Add(it);
        }
        var hot = new El("i", null, "hot");
        g.Add(hot);
        var root = new El("body").Add(g);
        using (engine.BeginRecalcBatch())
        {
            var map = CascadeTestHelper.ResolveTree(engine, root);
            Assert.All(items, it => Assert.Equal("rgb(255, 0, 0)", map[it].GetPropertyValueText("color")));
        }
        // 批次结束后元素树变化：新结果不受旧缓存影响
        g.Remove(hot);
        var map2 = CascadeTestHelper.ResolveTree(engine, root);
        Assert.All(items, it => Assert.Equal("rgb(0, 0, 0)", map2[it].GetPropertyValueText("color")));
    }

    [Fact]
    public void Hostile_has_is_bounded()
    {
        // 深链与宽树：每个元素都求值整棵子树的 :has(.never)。批次内缓存使总开销为 O(Σ子树)，批次外每次解析有访问预算
        var engine = CascadeTestHelper.Engine("*:has(.never) { color: red } *:has(+ * ~ * .never) { color: blue }");
        var root = new El("div");
        var cur = root;
        for (int i = 0; i < 300; i++)
        {
            var next = new El("div");
            cur.Add(next, new El("span"), new El("span"));
            cur = next;
        }
        double start = ThreadCpuClock.NowMs();
        using (engine.BeginRecalcBatch()) CascadeTestHelper.ResolveTree(engine, root);
        CascadeTestHelper.ResolveTree(engine, root);
        Assert.True(ThreadCpuClock.NowMs() - start < 20_000);
    }

    [Fact]
    public void Hostile_has_with_ancestor_walks_is_bounded()
    {
        // 深链上每个元素都是锚点、每个后代都是候选，且每个候选都要向上找 .a（不存在）：
        // 候选的祖先回溯必须止于锚点、并计入访问预算，批次内另有总预算——否则开销为 O(n³)（深度 1500 约需数十秒）
        var engine = CascadeTestHelper.Engine(".x:has(.a .b) { color: red } .x:has(~ .a .b) { color: blue }");
        var root = new El("div", null, "x b");
        var cur = root;
        for (int i = 0; i < 1500; i++)
        {
            var next = new El("div", null, "x b");
            cur.Add(next);
            cur = next;
        }
        double start = ThreadCpuClock.NowMs();
        using (engine.BeginRecalcBatch()) CascadeTestHelper.ResolveTree(engine, root);
        Assert.True(ThreadCpuClock.NowMs() - start < 8_000, $"{ThreadCpuClock.NowMs() - start} ms");
    }

    [Fact]
    public void Relative_match_stops_at_the_anchor()
    {
        // .a 在锚点之上：不能满足 :has(.a .b)（相对选择器的全部复合选择器都在锚点的子树中）
        var engine = CascadeTestHelper.Engine(".x:has(.a .b) { color: red } .y:has(~ .a .b) { color: blue } .z:has(> .a > .b) { color: green }");
        var b1 = new El("i", null, "b");
        var x = new El("div", null, "x").Add(b1);
        var y = new El("div", null, "y");
        var sib = new El("div").Add(new El("i", null, "b"));
        var z = new El("div", null, "z").Add(new El("p", null, "a").Add(new El("i", null, "b")));
        var root = new El("section", null, "a").Add(x, y, sib, z);
        var map = CascadeTestHelper.ResolveTree(engine, root);
        Assert.Equal("rgb(0, 0, 0)", map[x].GetPropertyValueText("color"));
        Assert.Equal("rgb(0, 0, 0)", map[y].GetPropertyValueText("color"));
        Assert.Equal("rgb(0, 128, 0)", map[z].GetPropertyValueText("color"));
        // 对照：.a 在锚点之内 / 在锚点之后的兄弟上时匹配
        x.Add(new El("p", null, "a").Add(new El("i", null, "b")));
        sib.SetClasses("a");
        map = CascadeTestHelper.ResolveTree(engine, root);
        Assert.Equal("rgb(255, 0, 0)", map[x].GetPropertyValueText("color"));
        Assert.Equal("rgb(0, 0, 255)", map[y].GetPropertyValueText("color"));
    }

    [Fact]
    public void Nested_functional_selectors_can_match_ancestors_outside_the_has_anchor()
    {
        var anchor = new El("div").Add(new El("b", null, "b"));
        _ = new El("section", null, "outer").Add(anchor);

        Assert.False(M(":has(.outer .b)", anchor));
        Assert.True(M(":has(> .b:is(.outer .b))", anchor));
        Assert.True(M(":has(> .b:where(.outer .b))", anchor));
        Assert.False(M(":has(> .b:not(.outer .b))", anchor));
        Assert.True(M(":has(> .b:not(.missing .b))", anchor));
    }

    [Fact]
    public void Relative_anchor_cutoff_does_not_exhaust_the_budget()
    {
        var anchor = new El("div", null, "a").Add(new El("b", null, "b"));
        var selector = SelectorParser.Parse(":not(:has(.a .b))")![0];
        var cache = new HasMatchCache(1, 1); // 唯一的候选消耗一步，越过锚点的尝试不消耗预算

        Assert.True(SelectorMatcher.Matches(selector, anchor, PseudoElementKind.None, cache));
        Assert.Equal(0, cache.BudgetExceededCount);
        Assert.Equal(1, cache.Count);
    }

    [Theory]
    [InlineData(":has(> .b:is(.outer .b))", true)]
    [InlineData(":has(> .b:where(.outer .b))", true)]
    [InlineData(":has(> .b:not(.outer .b))", false)]
    [InlineData(":has(> .b:not(.missing .b))", true)]
    [InlineData(":not(:has(> .b:is(.outer .b)))", false)]
    [InlineData(":not(:is(:has(> .b:is(.outer .b))))", false)]
    [InlineData(":not(:has(> .b:is(.missing .b)))", true)]
    [InlineData(":has(> .b:lang(en))", true)]
    public void Nested_has_ancestor_walks_are_budgeted_and_exhaustion_is_not_negated(string text, bool expected)
    {
        var anchor = new El("div").Add(new El("b", null, "b"));
        _ = new El("section", null, "outer") { Lang = "en" }.Add(anchor);
        var selector = SelectorParser.Parse(text)![0];
        var cache = new HasMatchCache(5, 16);
        Assert.True(cache.Visit());
        Assert.True(cache.Visit());
        Assert.True(cache.Visit());

        // 只剩两步：候选与内部的第一步可访问；内部遍历继续时耗尽预算，不能由 :not() 翻转。
        Assert.False(SelectorMatcher.Matches(selector, anchor, PseudoElementKind.None, cache));
        Assert.Equal(1, cache.BudgetExceededCount);
        Assert.Equal(0, cache.Count);

        // 同一个选择器、锚点和缓存：重置后可得到完整结果，证明上一次没有留下错误的缓存值。
        cache.ResetBudget();
        Assert.Equal(expected, SelectorMatcher.Matches(selector, anchor, PseudoElementKind.None, cache));
        Assert.Equal(1, cache.BudgetExceededCount);
        Assert.Equal(1, cache.Count);
    }

    [Theory]
    [InlineData(":is(.a ~ .b)")]
    [InlineData(":nth-child(2 of .a, .b)")]
    [InlineData(":nth-of-type(1)")]
    [InlineData(":first-of-type")]
    public void Nested_has_sibling_walks_use_the_batch_budget(string pseudo)
    {
        var anchor = new El("div").Add(new El("i", null, "a"), new El("i"), new El("b", null, "b"));
        var selector = SelectorParser.Parse(":has(> .b" + pseudo + ")")![0];
        var cache = new HasMatchCache(16, 4);

        // 逐一扫描三个候选后，只够检查一个前面的兄弟。
        Assert.False(SelectorMatcher.Matches(selector, anchor, PseudoElementKind.None, cache));
        Assert.Equal(1, cache.BudgetExceededCount);
        Assert.Equal(0, cache.Count);
        cache.ResetBudget();
        Assert.False(SelectorMatcher.Matches(selector, anchor, PseudoElementKind.None, cache));
        Assert.Equal(2, cache.BudgetExceededCount);
        Assert.True(SelectorMatcher.Matches(selector, anchor));
    }

    [Fact]
    public void Has_without_a_cache_is_still_budgeted()
    {
        // 公共 Matches 不带缓存时也有访问预算（不会无界）
        var list = SelectorParser.Parse(":has(.a .b)")!;
        var root = new El("div", null, "b");
        var cur = root;
        for (int i = 0; i < 3000; i++)
        {
            var next = new El("div", null, "b");
            cur.Add(next);
            cur = next;
        }
        double start = ThreadCpuClock.NowMs();
        Assert.False(SelectorMatcher.Matches(list[0], root));
        Assert.True(ThreadCpuClock.NowMs() - start < 8_000, $"{ThreadCpuClock.NowMs() - start} ms");
    }

    [Fact]
    public void Visit_budget_limits_a_single_resolve()
    {
        var cache = new HasMatchCache();
        var list = SelectorParser.Parse(":has(.never)")!;
        var root = new El("div");
        for (int i = 0; i < 2000; i++) root.Add(new El("p"));
        Assert.False(SelectorMatcher.Matches(list[0], root, PseudoElementKind.None, cache));
        Assert.Equal(0, cache.BudgetExceededCount);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Invalidation_set_collects_has_features()
    {
        var engine = CascadeTestHelper.Engine(".a:has(> .b[data-x]:hover, + #c:invalid) .d { color: red } .e:has(:first-child) { color: blue } .f { color: green }");
        var set = engine.HasInvalidation;
        Assert.True(set.UsesHas);
        Assert.True(set.HasSiblingRelative);
        Assert.True(set.HasNonSubjectHas);
        Assert.True(set.UsesStructural);
        Assert.True(set.UsesStatePseudoClasses);
        Assert.True(set.IsRelevantClass("b"));
        Assert.False(set.IsRelevantClass("a"));
        Assert.False(set.IsRelevantClass("f"));
        Assert.True(set.IsRelevantId("c"));
        Assert.True(set.IsRelevantAttribute("DATA-X"));
        Assert.Equal(PseudoStates.Hover, set.States);
        Assert.False(CascadeTestHelper.Engine(".a .b { color: red }").HasInvalidation.UsesHas);
    }
}
