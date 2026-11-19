using AwAUI.Css.Selectors;

namespace AwAUI.Css.UnitTests;

public class MatcherTests
{
    // Div#root.app
    //   Div.header[data-x="foo-bar baz"][lang=en-US]
    //     Span.title (text)
    //   Div.list
    //     Item.item, Item.item.special, Item#third.item, Text.note, Item.item.special, Item.item
    //   Div.empty
    //   Input (disabled), Input (focus)
    readonly El _root, _header, _title, _list, _empty, _in1, _in2;
    readonly El[] _items;

    public MatcherTests()
    {
        _title = new El("Span", classes: "title") { Text = "hello" };
        _header = new El("Div", classes: "header").Attr("data-x", "foo-bar baz").Attr("lang", "en-US").Add(_title);
        _items = new[]
        {
            new El("Item", classes: "item"),
            new El("Item", classes: "item special"),
            new El("Item", id: "third", classes: "item"),
            new El("Text", classes: "note") { Text = "x" },
            new El("Item", classes: "item special"),
            new El("Item", classes: "item"),
        };
        _list = new El("Div", classes: "list").Add(_items);
        _empty = new El("Div", classes: "empty");
        _in1 = new El("Input") { Disableable = true, State = PseudoStates.Disabled };
        _in2 = new El("Input") { Disableable = true, State = PseudoStates.Focus | PseudoStates.FocusVisible };
        _root = new El("Div", id: "root", classes: "app").Add(_header, _list, _empty, _in1, _in2);
    }

    static bool M(string selector, El e, PseudoElementKind pe = PseudoElementKind.None)
    {
        var list = SelectorParser.Parse(selector) ?? throw new InvalidOperationException("bad selector " + selector);
        return SelectorMatcher.Matches(list, e, pe);
    }

    IEnumerable<El> All()
    {
        var stack = new Stack<El>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            for (int i = e.Children.Count - 1; i >= 0; i--) stack.Push(e.Children[i]);
        }
    }

    string Q(string selector) => string.Join(",", All().Where(e => M(selector, e)).Select(e => e.ToString()));

    int[] ItemIndexes(string selector) => _items.Select((e, i) => (e, i)).Where(x => M(selector, x.e)).Select(x => x.i).ToArray();

    [Fact]
    public void Type_id_class_universal()
    {
        Assert.True(M("div", _root));          // 类型选择器大小写不敏感
        Assert.True(M("DIV", _root));
        Assert.True(M("#root", _root));
        Assert.False(M("#ROOT", _root));       // ID 大小写敏感
        Assert.True(M(".app", _root));
        Assert.False(M(".App", _root));
        Assert.True(M("*", _title));
        Assert.True(M("div.app#root", _root));
        Assert.False(M("span.app", _root));
    }

    [Fact]
    public void Combinators()
    {
        Assert.True(M(".app .title", _title));
        Assert.True(M(".app > .header > .title", _title));
        Assert.False(M(".app > .title", _title));
        Assert.True(M(".header + .list", _list));
        Assert.False(M(".header + .empty", _empty));
        Assert.True(M(".header ~ .empty", _empty));
        Assert.False(M(".empty ~ .header", _header));
        Assert.True(M("#third + Text + .special", _items[4]));
        Assert.True(M(".app > :not(.header) ~ Input", _in2));
        Assert.Equal("Item.item.special,Item.item.special", Q(".list > .special"));
    }

    [Fact]
    public void Descendant_backtracking_finds_a_farther_ancestor()
    {
        // A.x > B.y > C.y > D.z ；选择器 .x > .y .z 必须越过 C（其父不是 .x）找到 B
        var d = new El("D", classes: "z");
        var c = new El("C", classes: "y").Add(d);
        var b = new El("B", classes: "y").Add(c);
        new El("A", classes: "x").Add(b);
        Assert.True(M(".x > .y .z", d));
        Assert.False(M(".x > .y > .z", d));
        Assert.True(M(".x .y > .z", d));
        Assert.False(M(".q .y .z", d));
    }

    [Fact]
    public void Sibling_backtracking()
    {
        // .a ~ .b + .c：第一个 .b 前面没有 .a，第二个 .b 前面有
        var p = new El("P");
        var b1 = new El("E", classes: "b");
        var a = new El("E", classes: "a");
        var b2 = new El("E", classes: "b");
        var c = new El("E", classes: "c");
        p.Add(b1, a, b2, c);
        Assert.True(M(".a ~ .b + .c", c));
        Assert.True(M("P > .a ~ .c", c));
        Assert.False(M(".c ~ .a", a));
        Assert.True(M(".b ~ .b", b2));
        Assert.False(M(".b ~ .b", b1));
    }

    [Fact]
    public void Matching_is_not_exponential_on_deep_trees()
    {
        El leaf = new("X", classes: "leaf");
        El node = leaf;
        for (int i = 0; i < 300; i++) node = new El("A").Add(node);
        var sel = SelectorParser.Parse("B A A A A A A A A A A A A A A A A A A A A .leaf")!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(SelectorMatcher.Matches(sel, leaf));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds}ms");
        var sel2 = SelectorParser.Parse("A ~ B A ~ A + A .leaf")!;
        Assert.False(SelectorMatcher.Matches(sel2, leaf));
    }

    [Fact]
    public void Structural_pseudo_classes()
    {
        Assert.Equal(new[] { 0 }, ItemIndexes(":first-child"));
        Assert.Equal(new[] { 5 }, ItemIndexes(":last-child"));
        Assert.Equal(new[] { 0, 2, 4 }, ItemIndexes(":nth-child(odd)"));
        Assert.Equal(new[] { 1, 3, 5 }, ItemIndexes(":nth-child(2n)"));
        Assert.Equal(new[] { 0, 1, 2 }, ItemIndexes(":nth-child(-n+3)"));
        Assert.Equal(new[] { 5 }, ItemIndexes(":nth-last-child(1)"));
        Assert.Equal(new[] { 4, 5 }, ItemIndexes(":nth-last-child(-n+2)"));
        Assert.Equal(new[] { 1 }, ItemIndexes("Item:nth-of-type(2)"));
        Assert.Equal(new[] { 4 }, ItemIndexes("Item:nth-last-of-type(2)"));   // Text 不计入 Item 的同类型序号
        Assert.Equal(new[] { 0 }, ItemIndexes(":first-of-type:not(Text)"));
        Assert.Equal(new[] { 5 }, ItemIndexes("Item:last-of-type"));
        Assert.Equal(new[] { 3 }, ItemIndexes(":only-of-type"));
        Assert.Empty(ItemIndexes(":only-child"));
        Assert.True(M(":only-child", _title));
        Assert.True(M(":first-child:last-child", _title));
        Assert.Empty(ItemIndexes(":nth-child(0)"));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, ItemIndexes(":nth-child(n)"));
    }

    [Fact]
    public void Nth_child_of_selector()
    {
        // .special 项是第 2 与第 5 个子元素
        Assert.Equal(new[] { 4 }, ItemIndexes(":nth-child(2 of .special)"));
        Assert.Equal(new[] { 1 }, ItemIndexes(":nth-child(1 of .special)"));
        Assert.Equal(new[] { 1 }, ItemIndexes(":nth-last-child(2 of .special)"));
        // 奇数项（只数 .item）：items 0,1,2,4,5 中的第 1、3、5 个 -> 0,2,5
        Assert.Equal(new[] { 0, 2, 5 }, ItemIndexes(":nth-child(odd of .item)"));
        Assert.Equal(new[] { 0, 2, 4 }, ItemIndexes(":nth-child(odd)"));
        Assert.Empty(ItemIndexes(".note:nth-child(1 of .item)")); // 自身必须匹配 S
        Assert.Equal(new[] { 2 }, ItemIndexes(":nth-child(3 of :is(.item, .note))"));
    }

    [Fact]
    public void Logical_pseudo_classes_and_specificity()
    {
        Assert.Equal(new[] { 0, 2, 5 }, ItemIndexes(".item:not(.special)"));
        Assert.Equal(new[] { 2, 3 }, ItemIndexes(":is(#third, .note)"));
        Assert.Equal(new[] { 2, 3 }, ItemIndexes(":where(#third, .note)"));
        Assert.Equal(new[] { 0, 1, 4, 5 }, ItemIndexes(":not(#third, .note)"));
        Assert.Equal(new[] { 2 }, ItemIndexes(":is(.list > :nth-child(3))"));
        Assert.Equal(new[] { 1, 4 }, ItemIndexes(":is(.app .list) > .special"));
        Assert.Equal(new Specificity(1, 0, 0), SelectorParser.Parse(":is(#third, .note)")![0].Specificity);
        Assert.Equal(Specificity.Zero, SelectorParser.Parse(":where(#third, .note)")![0].Specificity);
    }

    [Fact]
    public void State_pseudo_classes()
    {
        _items[1].State = PseudoStates.Hover | PseudoStates.Active;
        Assert.Equal(new[] { 1 }, ItemIndexes(":hover"));
        Assert.Equal(new[] { 1 }, ItemIndexes(":hover:active"));
        Assert.Empty(ItemIndexes(":focus"));
        Assert.True(M("Input:disabled", _in1));
        Assert.False(M("Input:enabled", _in1));
        Assert.True(M("Input:enabled", _in2));
        Assert.False(M(":enabled", _root));    // 不可禁用的元素不匹配 :enabled
        Assert.True(M(":focus:focus-visible", _in2));
        _root.State = PseudoStates.FocusWithin;
        Assert.True(M(":focus-within > Input:focus", _in2));
        _in2.State |= PseudoStates.Checked | PseudoStates.PlaceholderShown;
        Assert.True(M(":checked:placeholder-shown", _in2));
    }

    [Fact]
    public void Empty_and_root()
    {
        Assert.True(M(":empty", _empty));
        Assert.False(M(":empty", _title));     // 有文本
        Assert.False(M(":empty", _list));
        Assert.True(M(":root", _root));
        Assert.True(M(":scope > .list", _list));
        Assert.False(M(":root", _list));
    }

    [Theory]
    [InlineData("[data-x]", true)]
    [InlineData("[data-y]", false)]
    [InlineData("[data-x='foo-bar baz']", true)]
    [InlineData("[data-x='FOO-BAR BAZ']", false)]
    [InlineData("[data-x='FOO-BAR BAZ' i]", true)]
    [InlineData("[data-x='FOO-BAR BAZ' s]", false)]
    [InlineData("[data-x~=baz]", true)]
    [InlineData("[data-x~=foo]", false)]
    [InlineData("[data-x~='foo-bar']", true)]
    [InlineData("[data-x~='BAZ' i]", true)]
    [InlineData("[data-x~='']", false)]
    [InlineData("[data-x~='foo-bar baz']", false)]
    [InlineData("[data-x|=foo]", true)]
    [InlineData("[data-x|=fo]", false)]
    [InlineData("[lang|=en]", true)]
    [InlineData("[lang|=EN i]", true)]
    [InlineData("[lang|=en-US]", true)]
    [InlineData("[data-x^=foo]", true)]
    [InlineData("[data-x^='']", false)]
    [InlineData("[data-x^=FOO i]", true)]
    [InlineData("[data-x$=baz]", true)]
    [InlineData("[data-x$='']", false)]
    [InlineData("[data-x*='bar b']", true)]
    [InlineData("[data-x*='']", false)]
    [InlineData("[data-x*='BAR' i]", true)]
    [InlineData("[data-x*='BAR']", false)]
    public void Attribute_operators(string selector, bool expected) => Assert.Equal(expected, M(selector, _header));

    [Fact]
    public void Pseudo_elements_match_only_when_requested()
    {
        Assert.False(M(".item::before", _items[0]));
        Assert.True(M(".item::before", _items[0], PseudoElementKind.Before));
        Assert.False(M(".item::before", _items[0], PseudoElementKind.After));
        Assert.True(M(".item:after", _items[0], PseudoElementKind.After));
        Assert.False(M(".item", _items[0], PseudoElementKind.Before));
        Assert.True(M("Input::placeholder", _in2, PseudoElementKind.Placeholder));
        Assert.True(M("::selection", _title, PseudoElementKind.Selection));
    }

    [Fact]
    public void Part_matches_exposed_element_through_its_host()
    {
        var host = new El("XButton", classes: "primary");
        var label = new El("Span").Part("label").Part("text");
        label.Host = host;
        var icon = new El("Span").Part("icon");
        icon.Host = host;
        host.Add(new El("Div").Add(label, icon));
        new El("Div", classes: "toolbar").Add(host);

        Assert.True(M("XButton::part(label)", label));
        Assert.True(M(".toolbar > .primary::part(label text)", label));
        Assert.False(M("XButton::part(label)", icon));
        Assert.False(M("Other::part(label)", label));
        Assert.False(M("XButton::part(label):hover", label));
        label.State = PseudoStates.Hover;
        Assert.True(M("XButton::part(label):hover", label));
        Assert.False(M("::part(label)", host)); // 宿主本身没有该 part
    }
}
