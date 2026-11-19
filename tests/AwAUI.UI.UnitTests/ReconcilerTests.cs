namespace AwAUI.UI.UnitTests;

/// <summary>以 State 驱动子节点列表的测试组件。</summary>
partial class ListHost : Component
{
    public State<Func<Node[]>> Items { get; set; } = new(() => Array.Empty<Node>());

    protected override Node Build()
    {
        var items = Items.Value();
        var list = new NodeList();
        foreach (var n in items) list.Add(n);
        return new Div { Id = "host", Children = list };
    }
}

public class ReconcilerTests
{
    static (UIRoot root, Element host, RecordingObserver obs, State<Func<Node[]>> items) Setup(Func<Node[]> initial)
    {
        var items = new State<Func<Node[]>>(() => initial);
        var root = new UIRoot();
        var obs = new RecordingObserver();
        root.AddObserver(obs);
        root.Mount(new ListHost { Items = items });
        var host = root.RootElement.ById("host");
        obs.Clear();
        return (root, host, obs, items);
    }

    static Node[] Keyed(params string[] keys) => keys.Select(k => (Node)new Div { Key = k, Id = k }).ToArray();

    [Theory]
    [InlineData("a,b,c,d", "d,a,b,c", 1)]
    [InlineData("a,b,c,d", "b,c,d,a", 1)]
    [InlineData("a,b,c,d", "d,c,b,a", 3)]
    [InlineData("a,b,c,d,e", "a,c,b,e,d", 2)]
    [InlineData("a,b,c,d,e,f,g,h,i,j", "j,a,b,c,d,e,f,g,h,i", 1)]
    [InlineData("a,b,c,d,e,f,g,h,i,j,k,l", "b,a,d,c,f,e,h,g,j,i,l,k", 6)]
    public void Keyed_reorder_uses_minimal_moves(string from, string to, int expectedMoves)
    {
        var (root, host, obs, items) = Setup(() => Keyed(from.Split(',')));
        var before = host.Children.ToDictionary(e => e.Id!);
        items.Value = () => Keyed(to.Split(','));
        root.RunFrame();
        Assert.Equal(to.Replace(",", ",div#").Insert(0, "div#"), host.Shape());
        Assert.Equal(expectedMoves, obs.Count("move"));
        Assert.Equal(0, obs.Count("insert"));
        Assert.Equal(0, obs.Count("remove"));
        foreach (var e in host.Children) Assert.Same(before[e.Id!], e); // 元素按 Key 保留
        for (int i = 0; i < host.Children.Count; i++)
        {
            Assert.Equal(i, host.Children[i].IndexInParent);
            Assert.Equal(i + 1, host.Children[i].GetChildIndex());
        }
    }

    [Fact]
    public void Insert_and_remove_at_every_position()
    {
        string[] baseKeys = { "a", "b", "c", "d" };
        for (int pos = 0; pos <= baseKeys.Length; pos++)
        {
            var (root, host, obs, items) = Setup(() => Keyed(baseKeys));
            var kept = host.Children.ToArray();
            var withNew = baseKeys.ToList();
            withNew.Insert(pos, "x");
            items.Value = () => Keyed(withNew.ToArray());
            root.RunFrame();
            Assert.Equal(string.Join(",", withNew.Select(k => "div#" + k)), host.Shape());
            Assert.Equal(new[] { "insert x" }, obs.Events.Where(e => !e.StartsWith("state", StringComparison.Ordinal)).ToArray());
            foreach (var k in kept) Assert.Same(host, k.Parent);
        }
        for (int pos = 0; pos < baseKeys.Length; pos++)
        {
            var (root, host, obs, items) = Setup(() => Keyed(baseKeys));
            var removedElement = host.Children[pos];
            var without = baseKeys.Where((_, i) => i != pos).ToArray();
            items.Value = () => Keyed(without);
            root.RunFrame();
            Assert.Equal(string.Join(",", without.Select(k => "div#" + k)), host.Shape());
            Assert.Equal(new[] { "remove " + baseKeys[pos] }, obs.Events.ToArray());
            Assert.Null(removedElement.Parent);
            Assert.False(removedElement.IsAttached);
        }
    }

    [Fact]
    public void Unkeyed_children_match_by_type_and_ordinal()
    {
        bool show = true;
        Node[] Make() => show
            ? new Node[] { new Span { Id = "s1" }, new Div { Id = "d1" }, new Span { Id = "s2" }, new Div { Id = "d2" } }
            : new Node[] { new Div { Id = "d1" }, new Span { Id = "s2" }, new Div { Id = "d2" } };
        var (root, host, obs, items) = Setup(Make);
        var spans = host.Children.Where(c => c.Tag == "span").ToArray();
        var divs = host.Children.Where(c => c.Tag == "div").ToArray();

        // 去掉第一个 span：第二个 span 描述按「span 序号 0」匹配到原第一个 span 元素（id 被改写），原第二个 span 被移除
        show = false;
        items.Value = () => Make(); // 注意：State 按 Delegate.Equals 比较，再次写入同一方法组会被忽略
        root.RunFrame();
        Assert.Equal("div#d1,span#s2,div#d2", host.Shape());
        Assert.Same(divs[0], host.Children[0]);
        Assert.Same(divs[1], host.Children[2]);
        Assert.Same(spans[0], host.Children[1]);
        Assert.Contains("remove s2", obs.Events);
    }

    [Fact]
    public void Conditional_null_children_do_not_disturb_matching()
    {
        bool flag = false;
        Node[] Make()
        {
            var list = new NodeList { flag ? new Span { Id = "banner" } : null, new Div { Id = "a" }, null, new Div { Id = "b" } };
            return list.ToArray();
        }
        var (root, host, obs, items) = Setup(Make);
        var a = host.ById("a");
        var b = host.ById("b");
        flag = true;
        items.Value = () => Make();
        root.RunFrame();
        Assert.Equal("span#banner,div#a,div#b", host.Shape());
        Assert.Same(a, host.ById("a"));
        Assert.Same(b, host.ById("b"));
        Assert.Equal(new[] { "insert banner" }, obs.Events.ToArray());
    }

    [Fact]
    public void Type_change_remounts()
    {
        var (root, host, obs, items) = Setup(() => new Node[] { new Div { Key = 1, Id = "x" } });
        var old = host.Children[0];
        items.Value = () => new Node[] { new Span { Key = 1, Id = "x" } };
        root.RunFrame();
        Assert.Equal("span#x", host.Shape());
        Assert.NotSame(old, host.Children[0]);
        Assert.Equal(new[] { "remove x", "insert x" }, obs.Events.ToArray());
    }

    [Fact]
    public void Text_children_update_in_place()
    {
        var (root, host, obs, items) = Setup(() => new Node[] { "hello", new Span { Id = "s" }, "world" });
        Assert.Equal("'hello',span#s,'world'", host.Shape());
        var first = host.Children[0];
        Assert.True(first.IsTextNode);
        Assert.Null(first.PreviousElementSibling);
        Assert.Equal(1, host.ById("s").GetChildIndex()); // 文本节点不参与元素兄弟计数
        items.Value = () => new Node[] { "hi", new Span { Id = "s" }, "world" };
        root.RunFrame();
        Assert.Same(first, host.Children[0]);
        Assert.Equal(new[] { "text 'hi'" }, obs.Events.ToArray());
        Assert.Equal("hiworld", host.TextContent);
    }

    [Fact]
    public void Element_diff_writes_only_changed_fields_with_precise_dirty_flags()
    {
        var (root, host, obs, items) = Setup(() => new Node[] { new Div { Id = "x", Class = "a b", Style = "color: red", Attributes = { ["data-state"] = "open" } } });
        var x = host.ById("x");
        x.ClearDirty(ElementDirtyFlags.All);
        host.ClearDirty(ElementDirtyFlags.All);
        root.RootElement.ClearDirty(ElementDirtyFlags.All);

        // 只换了处理器：没有任何通知与脏标记
        items.Value = () => new Node[] { new Div { Id = "x", Class = "a  b ", Style = "color: red", Attributes = { ["data-state"] = "open" }, OnClick = _ => { } } };
        root.RunFrame();
        Assert.Empty(obs.Events);
        Assert.Equal(ElementDirtyFlags.None, x.DirtyFlags);
        Assert.Equal(ElementDirtyFlags.None, root.RootElement.DescendantDirtyFlags);

        // class 与属性变化：只打 Style，祖先得到后代脏标记
        items.Value = () => new Node[] { new Div { Id = "x", Class = "a c", Style = "color: red", Attributes = { ["data-state"] = "closed" } } };
        root.RunFrame();
        Assert.Equal(new[] { "class x", "attr data-state" }, obs.Events.ToArray());
        Assert.Equal(ElementDirtyFlags.Style, x.DirtyFlags);
        Assert.Equal(ElementDirtyFlags.Style, host.DescendantDirtyFlags);
        Assert.Equal(ElementDirtyFlags.Style, root.RootElement.DescendantDirtyFlags);
        Assert.Equal(new[] { "a", "c" }, x.Classes.ToArray());
        Assert.Equal("closed", x.GetAttribute("data-state"));
        Assert.Equal("a c", x.GetAttribute("class"));
    }

    [Fact]
    public void Fragments_and_nested_components_place_elements_in_host_order()
    {
        var inner = new State<int>(1);
        Node Make() => new Fragment { Children = [new Span { Id = "f1" }, new Lambda { Render = () => inner.Value == 1 ? new Div { Id = "one" } : new Fragment { Children = [new Div { Id = "two-a" }, new Div { Id = "two-b" }] } }, new Span { Id = "f2" }] };
        var (root, host, obs, items) = Setup(() => new Node[] { new Div { Id = "first" }, Make(), new Div { Id = "last" } });
        Assert.Equal("div#first,span#f1,div#one,span#f2,div#last", host.Shape());
        inner.Value = 2;
        root.RunFrame();
        Assert.Equal("div#first,span#f1,div#two-a,div#two-b,span#f2,div#last", host.Shape());
        // 插入从后往前报告（每条插入通知时其后的兄弟都已就位，见 ElementTreeObserver.OnChildMoved 的说明）
        Assert.Equal(new[] { "remove one", "insert two-b", "insert two-a" }, obs.Events.ToArray());
    }

    [Fact]
    public void Duplicate_keys_report_diagnostic_and_remount_later_item()
    {
        var items = new State<Func<Node[]>>(() => () => Keyed("a", "a"));
        var root = new UIRoot();
        var diags = new List<UIDiagnostic>();
        root.Diagnostic += diags.Add;
        root.Mount(new ListHost { Items = items });
        var host = root.RootElement.ById("host");
        Assert.Equal(2, host.ChildCount);
        items.Value = () => Keyed("a", "a");
        root.RunFrame();
        Assert.Contains(diags, d => d.Code == "AW1008");
        Assert.Equal(2, host.ChildCount);
    }

    [Fact]
    public void Large_keyed_list_uses_hashed_matching()
    {
        var keys = Enumerable.Range(0, 200).Select(i => "k" + i).ToArray();
        var (root, host, obs, items) = Setup(() => Keyed(keys));
        var reversed = Enumerable.Reverse(keys).ToArray();
        items.Value = () => Keyed(reversed);
        root.RunFrame();
        Assert.Equal(string.Join(",", reversed.Select(k => "div#" + k)), host.Shape());
        Assert.Equal(199, obs.Count("move"));
        var shuffled = keys.Where((_, i) => i % 3 != 0).Concat(new[] { "n1", "n2" }).ToArray();
        obs.Clear();
        items.Value = () => Keyed(shuffled);
        root.RunFrame();
        Assert.Equal(string.Join(",", shuffled.Select(k => "div#" + k)), host.Shape());
        Assert.Equal(67, obs.Count("remove"));
        Assert.Equal(2, obs.Count("insert"));
    }

    [Fact]
    public void Ref_is_set_on_mount_and_cleared_on_unmount()
    {
        var r = new Ref<Element>();
        var (root, host, obs, items) = Setup(() => new Node[] { new Div { Id = "x", Ref = r } });
        Assert.Same(host.ById("x"), r.Current);
        items.Value = () => Array.Empty<Node>();
        root.RunFrame();
        Assert.Null(r.Current);
    }

    [Fact]
    public void Siblings_and_structural_queries()
    {
        var (root, host, obs, items) = Setup(() => new Node[] { new Div { Id = "a" }, "t", new Div { Id = "b" }, new Div { Id = "c" } });
        var a = host.ById("a");
        var b = host.ById("b");
        var c = host.ById("c");
        Assert.Same(b, a.NextElementSibling);
        Assert.True(a.NextSibling!.IsTextNode);
        Assert.Same(a, b.PreviousElementSibling);
        Assert.Equal(3, c.GetChildIndex());
        Assert.Equal(1, c.GetChildIndexFromEnd());
        Assert.Equal(3, a.GetChildIndexFromEnd());
        Assert.True(a.IsEmpty);
        Assert.False(host.IsEmpty);
        Assert.Equal(new[] { a, b, c }, host.ElementChildren.ToArray());
    }
}
