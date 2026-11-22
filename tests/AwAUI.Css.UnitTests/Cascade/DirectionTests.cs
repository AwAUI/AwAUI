using AwAUI.Css.Cascade;

namespace AwAUI.Css.UnitTests.Cascade;

/// <summary>
/// <c>direction</c> 的计算与继承，以及 HTML <c>dir</c> 属性作为表现提示的映射（引擎侧 RTL 的第一步）。
/// </summary>
public class DirectionTests
{
    [Fact]
    public void Direction_is_inherited_and_can_be_overridden_by_direction_rule()
    {
        var root = new El("div", classes: "page");
        var child = new El("span", classes: "child");
        root.Add(child);
        var engine = CascadeTestHelper.Engine(".page { direction: rtl } .child { direction: ltr }");
        var map = CascadeTestHelper.ResolveTree(engine, root);
        Assert.Equal("rtl", map[root].GetPropertyValueText("direction"));
        Assert.Equal("ltr", map[child].GetPropertyValueText("direction"));

        var inherited = new El("span");
        root.Add(inherited);
        var map2 = CascadeTestHelper.ResolveTree(engine, root);
        Assert.Equal("rtl", map2[inherited].GetPropertyValueText("direction"));
    }

    [Fact]
    public void Dir_attribute_sets_direction_unless_an_author_rule_wins()
    {
        var hinted = new El("span").Attr("dir", "rtl");
        var root = new El("div").Add(hinted);
        Assert.Equal("rtl", CascadeTestHelper.ResolveTree(CascadeTestHelper.Engine(""), root)[hinted].GetPropertyValueText("direction"));

        var overridden = new El("span").Attr("dir", "rtl");
        var root2 = new El("div").Add(overridden);
        var s = CascadeTestHelper.ResolveTree(CascadeTestHelper.Engine("span { direction: ltr }"), root2)[overridden];
        Assert.Equal("ltr", s.GetPropertyValueText("direction"));
    }

    [Fact]
    public void Nested_dir_attributes_restart_the_inherited_direction()
    {
        var outer = new El("div").Attr("dir", "rtl");
        var inner = new El("div").Attr("dir", "ltr");
        var grandchild = new El("span");
        outer.Add(inner);
        inner.Add(grandchild);
        var map = CascadeTestHelper.ResolveTree(CascadeTestHelper.Engine(""), outer);
        Assert.Equal("rtl", map[outer].GetPropertyValueText("direction"));
        Assert.Equal("ltr", map[inner].GetPropertyValueText("direction"));
        Assert.Equal("ltr", map[grandchild].GetPropertyValueText("direction"));
    }
}
