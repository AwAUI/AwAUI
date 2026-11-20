namespace AwAUI.UI.UnitTests;

/// <summary>
/// 属性展开的运行时部分（<c>.awaui</c> 的 <c>@attributes</c>，design/17）：<see cref="AttributeDictionary"/> 的语义与
/// <see cref="ElementNode.MergeAttributes(IEnumerable{KeyValuePair{string, object}})"/> 的合并规则。
/// </summary>
public class AttributeSplatTests
{
    static Element Mount(Node node, out UIRoot root)
    {
        root = new UIRoot();
        root.Mount(new Lambda { Render = () => node });
        return root.RootElement.Children[0];
    }

    [Fact]
    public void Dictionary_is_ordered_case_insensitive_and_null_removes()
    {
        var d = new AttributeDictionary { ["data-a"] = "1", ["Data-B"] = "2" };
        d["DATA-A"] = "3";
        Assert.Equal(new[] { "data-a", "Data-B" }, d.Select(kv => kv.Key).ToArray());
        Assert.Equal("3", d["data-a"]);
        d.Add("data-b", null);
        Assert.False(d.ContainsKey("data-b"));
        Assert.Single(d);
        Assert.Null(d["missing"]);

        // 超过阈值后改用索引，行为不变
        var big = new AttributeDictionary();
        for (int i = 0; i < 40; i++) big["k" + i] = i;
        big["K39"] = "x";
        big["k5"] = null;
        Assert.Equal(39, big.Count);
        Assert.Equal("x", big["k39"]);
        Assert.False(big.ContainsKey("k5"));
        Assert.Equal(38, big["k38"]);
        Assert.True(big.TryGetValue("k0", out var v0) && (int)v0! == 0);
    }

    [Fact]
    public void Dictionary_equality_is_by_content_and_order_independent()
    {
        Action handler = () => { };
        var a = new AttributeDictionary { ["data-x"] = "1", ["onclick"] = handler, ["n"] = 2 };
        var b = new AttributeDictionary { ["n"] = 2, ["onclick"] = handler, ["DATA-X"] = "1" };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        b["data-x"] = "2";
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, new AttributeDictionary { ["data-x"] = "1", ["onclick"] = new Action(() => { }), ["n"] = 2 });
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void Explicit_attributes_win_class_and_style_concatenate()
    {
        int explicitClicks = 0, splatClicks = 0, splatKeys = 0;
        var div = new Div
        {
            Id = "explicit",
            Class = "a",
            Style = "color: red",
            Role = Role.Button,
            OnClick = _ => explicitClicks++,
        };
        div.Attributes["data-slot"] = "card";
        div.MergeAttributes(new AttributeDictionary
        {
            ["id"] = "splat",
            ["class"] = new ClassValue("b c"),
            ["style"] = "color: blue; margin: 1px;",
            ["role"] = "dialog",
            ["data-slot"] = "other",
            ["data-state"] = "open",
            ["aria-expanded"] = true,
            ["data-flag"] = false,
            ["hidden"] = true,
            ["inert"] = false,
            ["data-n"] = 1.5,
            ["tabindex"] = "3",
            ["aria-label"] = "标签",
            ["onclick"] = new Action(() => splatClicks++),
            ["onkeydown"] = new Action<KeyEvent>(_ => splatKeys++),
            ["skipped"] = null,
        });
        Assert.Equal("explicit", div.Id);
        Assert.Equal("a b c", div.Class.Text);
        Assert.Equal("color: blue; margin: 1px; color: red", div.Style.Text);
        Assert.Equal(Role.Button, div.Role);
        Assert.Equal(3, div.TabIndex);
        Assert.Equal("标签", div.AriaLabel);
        Assert.Equal("card", div.Attributes["data-slot"]);
        Assert.Equal("open", div.Attributes["data-state"]);
        Assert.Equal("true", div.Attributes["aria-expanded"]);
        Assert.Equal("false", div.Attributes["data-flag"]);
        Assert.Equal("", div.Attributes["hidden"]);
        Assert.False(div.Attributes.Contains("inert"));
        Assert.False(div.Attributes.Contains("skipped"));
        Assert.Equal("1.5", div.Attributes["data-n"]);

        var el = Mount(div, out var root);
        Assert.Equal("explicit", el.Id);
        Assert.Equal(new[] { "a", "b", "c" }, el.Classes.ToArray());
        root.Input.Click(el);
        Assert.Equal(1, explicitClicks);
        Assert.Equal(0, splatClicks); // 显式的处理器优先
        Assert.NotNull(div.OnKeyDown); // 节点上原来没有：写入展开的处理器
        div.OnKeyDown!(null!);
        Assert.Equal(1, splatKeys);
    }

    [Fact]
    public void Unset_members_take_splatted_values()
    {
        int clicks = 0;
        var div = new Div();
        div.MergeAttributes(new Dictionary<string, object?>
        {
            ["id"] = "s",
            ["role"] = "img",
            ["onclick"] = new Action<PointerEvent>(_ => clicks++),
            ["class"] = "x",
            ["style"] = "padding: 2px",
        });
        Assert.Equal("s", div.Id);
        Assert.Equal(Role.Image, div.Role);
        Assert.Equal("x", div.Class.Text);
        Assert.Equal("padding: 2px", div.Style.Text);
        var el = Mount(div, out var root);
        root.Input.Click(el);
        Assert.Equal(1, clicks);

        // 字符串值的表（如 AttributeMap）也可以展开
        var span = new Span();
        var map = new AttributeMap();
        map["data-k"] = "v";
        span.MergeAttributes(map);
        Assert.Equal("v", span.Attributes["data-k"]);
        span.MergeAttributes((IEnumerable<KeyValuePair<string, object?>>?)null);
    }

    [Fact]
    public void Role_names_that_are_not_a_single_known_role_are_ignored()
    {
        // Enum.TryParse 接受逗号分隔的名字与数字（把多个角色按位或成一个不存在的值），展开时它们都不是 ARIA 角色
        foreach (var text in new[] { "button, link", "button,link", "1", "nope" })
        {
            var div = new Div();
            div.MergeAttributes(new AttributeDictionary { ["role"] = text });
            Assert.Equal(Role.None, div.Role);
        }
        var ok = new Div();
        ok.MergeAttributes(new AttributeDictionary { ["role"] = " Dialog " });
        Assert.Equal(Role.Dialog, ok.Role);
    }

    [Fact]
    public void Handler_type_mismatch_throws()
    {
        var div = new Div();
        var ex = Assert.Throws<ArgumentException>(() => div.MergeAttributes(new AttributeDictionary { ["onclick"] = "not a delegate" }));
        Assert.Contains("onclick", ex.Message);
        Assert.Throws<ArgumentException>(() => div.MergeAttributes(new AttributeDictionary { ["onkeydown"] = new Action<PointerEvent>(_ => { }) }));
    }

    [Fact]
    public void Element_specific_attributes_merge_into_typed_members()
    {
        var input = new Input { Placeholder = "explicit" };
        input.MergeAttributes(new AttributeDictionary { ["placeholder"] = "splat", ["value"] = "v", ["type"] = "password", ["readonly"] = "", ["disabled"] = "false" });
        Assert.Equal("explicit", input.Placeholder);
        Assert.Equal("v", input.Value);
        Assert.Equal("password", input.InputType);
        Assert.True(input.ReadOnly);
        Assert.False(input.Disabled);

        var button = new Button();
        button.MergeAttributes(new AttributeDictionary { ["disabled"] = true });
        Assert.True(button.Disabled);
    }
}
