namespace AwAUI.Css.UnitTests.Cascade;

/// <summary>
/// 行内起止方向的逻辑属性（<c>padding-inline-*</c> / <c>margin-inline-*</c> / <c>inset-inline-*</c> /
/// <c>border-inline-*</c> / <c>scroll-margin-inline-*</c> / <c>scroll-padding-inline-*</c> /
/// <c>border-start-start-radius</c> 一族）按计算后的 <c>direction</c> 映射到物理长属性：LTR 的 start 在左、end 在右，RTL 相反。
/// 映射发生在级联中，因此与同名物理属性的先后 / <c>!important</c> 比较遵守普通级联规则。
/// </summary>
public class LogicalInlineTests
{
    [Fact]
    public void Padding_inset_and_border_flip_with_dir_rtl()
    {
        const string css = """
            .ltr { direction: ltr; padding-inline-start: 8px; padding-inline-end: 32px;
                   margin-inline-start: 4px; margin-inline-end: 6px;
                   position: absolute; inset-inline-start: 5px; inset-inline-end: 7px;
                   border-inline-start: 2px solid rgb(255, 0, 0); }
            .rtl { direction: rtl; padding-inline-start: 8px; padding-inline-end: 32px;
                   margin-inline-start: 4px; margin-inline-end: 6px;
                   position: absolute; inset-inline-start: 5px; inset-inline-end: 7px;
                   border-inline-start: 2px solid rgb(255, 0, 0); }
            """;
        var ltr = CascadeTestHelper.One(css, new El("div", classes: "ltr"));
        Assert.Equal("8px", ltr.GetPropertyValueText("padding-left"));
        Assert.Equal("32px", ltr.GetPropertyValueText("padding-right"));
        Assert.Equal("4px", ltr.GetPropertyValueText("margin-left"));
        Assert.Equal("6px", ltr.GetPropertyValueText("margin-right"));
        Assert.Equal("5px", ltr.GetPropertyValueText("left"));
        Assert.Equal("7px", ltr.GetPropertyValueText("right"));
        Assert.Equal("2px", ltr.GetPropertyValueText("border-left-width"));
        Assert.Equal("solid", ltr.GetPropertyValueText("border-left-style"));
        Assert.Equal("0px", ltr.GetPropertyValueText("border-right-width"));

        var rtl = CascadeTestHelper.One(css, new El("div", classes: "rtl"));
        Assert.Equal("32px", rtl.GetPropertyValueText("padding-left"));
        Assert.Equal("8px", rtl.GetPropertyValueText("padding-right"));
        Assert.Equal("6px", rtl.GetPropertyValueText("margin-left"));
        Assert.Equal("4px", rtl.GetPropertyValueText("margin-right"));
        Assert.Equal("7px", rtl.GetPropertyValueText("left"));
        Assert.Equal("5px", rtl.GetPropertyValueText("right"));
        Assert.Equal("2px", rtl.GetPropertyValueText("border-right-width"));
        Assert.Equal("solid", rtl.GetPropertyValueText("border-right-style"));
        Assert.Equal("0px", rtl.GetPropertyValueText("border-left-width"));
    }

    [Fact]
    public void Dir_attribute_is_used_when_direction_comes_from_a_lower_specificity_rule()
    {
        // 方向来自特异性更低的规则：逻辑属性仍按最终方向映射
        var root = new El("div").Attr("dir", "rtl");
        var child = new El("span", classes: "child");
        root.Add(child);
        var map = CascadeTestHelper.ResolveTree(
            CascadeTestHelper.Engine("div { direction: rtl } .child { padding-inline-start: 12px }"), root);
        Assert.Equal("12px", map[child].GetPropertyValueText("padding-right"));
        Assert.Equal("0px", map[child].GetPropertyValueText("padding-left"));
    }

    [Fact]
    public void Shorthand_pair_assigns_start_then_end()
    {
        var ltr = CascadeTestHelper.One(".x { padding-inline: 1px 2px; margin-inline: 3px 4px }", new El("div", classes: "x"));
        Assert.Equal("1px", ltr.GetPropertyValueText("padding-left"));
        Assert.Equal("2px", ltr.GetPropertyValueText("padding-right"));
        Assert.Equal("3px", ltr.GetPropertyValueText("margin-left"));
        Assert.Equal("4px", ltr.GetPropertyValueText("margin-right"));

        var rtlEl = new El("div", classes: "x").Attr("dir", "rtl");
        var rtl = CascadeTestHelper.One(".x { padding-inline: 1px 2px; margin-inline: 3px 4px }", rtlEl);
        Assert.Equal("2px", rtl.GetPropertyValueText("padding-left"));
        Assert.Equal("1px", rtl.GetPropertyValueText("padding-right"));
        Assert.Equal("4px", rtl.GetPropertyValueText("margin-left"));
        Assert.Equal("3px", rtl.GetPropertyValueText("margin-right"));
    }

    [Fact]
    public void Physical_and_logical_declarations_follow_cascade_order_and_importance()
    {
        // 同块内后出现的声明优先：逻辑属性在后时覆盖物理左值
        var laterLogical = CascadeTestHelper.One(".x { padding-left: 1px; padding-inline-start: 2px }", new El("div", classes: "x"));
        Assert.Equal("2px", laterLogical.GetPropertyValueText("padding-left"));

        var laterPhysical = CascadeTestHelper.One(".x { padding-inline-start: 2px; padding-left: 1px }", new El("div", classes: "x"));
        Assert.Equal("1px", laterPhysical.GetPropertyValueText("padding-left"));

        // 物理声明的 !important 仍高于普通逻辑声明
        var important = CascadeTestHelper.One(".x { padding-inline-start: 2px; padding-left: 1px !important }", new El("div", classes: "x"));
        Assert.Equal("1px", important.GetPropertyValueText("padding-left"));

        // RTL 下 start 指向右：物理左值保持不变
        var rtlEl = new El("div", classes: "x").Attr("dir", "rtl");
        var rtl = CascadeTestHelper.One(".x { padding-left: 1px; padding-inline-start: 2px }", rtlEl);
        Assert.Equal("1px", rtl.GetPropertyValueText("padding-left"));
        Assert.Equal("2px", rtl.GetPropertyValueText("padding-right"));
    }

    [Fact]
    public void Scroll_offsets_and_logical_corners_flip_with_dir_rtl()
    {
        const string css = """
            .ltr, .rtl { scroll-margin-inline-start: 5px; scroll-margin-inline-end: 7px;
                         scroll-padding-inline: 3px 9px;
                         border-start-start-radius: 2px; border-start-end-radius: 4px;
                         border-end-start-radius: 6px; border-end-end-radius: 8px; }
            """;
        var ltr = CascadeTestHelper.One(css, new El("div", classes: "ltr"));
        Assert.Equal("5px", ltr.GetPropertyValueText("scroll-margin-left"));
        Assert.Equal("7px", ltr.GetPropertyValueText("scroll-margin-right"));
        Assert.Equal("3px", ltr.GetPropertyValueText("scroll-padding-left"));
        Assert.Equal("9px", ltr.GetPropertyValueText("scroll-padding-right"));
        Assert.Equal("2px", ltr.GetPropertyValueText("border-top-left-radius"));
        Assert.Equal("4px", ltr.GetPropertyValueText("border-top-right-radius"));
        Assert.Equal("6px", ltr.GetPropertyValueText("border-bottom-left-radius"));
        Assert.Equal("8px", ltr.GetPropertyValueText("border-bottom-right-radius"));

        var rtl = CascadeTestHelper.One(css, new El("div", classes: "rtl").Attr("dir", "rtl"));
        Assert.Equal("7px", rtl.GetPropertyValueText("scroll-margin-left"));
        Assert.Equal("5px", rtl.GetPropertyValueText("scroll-margin-right"));
        Assert.Equal("9px", rtl.GetPropertyValueText("scroll-padding-left"));
        Assert.Equal("3px", rtl.GetPropertyValueText("scroll-padding-right"));
        Assert.Equal("4px", rtl.GetPropertyValueText("border-top-left-radius"));
        Assert.Equal("2px", rtl.GetPropertyValueText("border-top-right-radius"));
        Assert.Equal("8px", rtl.GetPropertyValueText("border-bottom-left-radius"));
        Assert.Equal("6px", rtl.GetPropertyValueText("border-bottom-right-radius"));
    }

    [Fact]
    public void Var_substitution_in_the_inline_shorthand_is_mapped_after_direction()
    {
        const string css = ".x { --pad: 3px 5px; padding-inline: var(--pad) }";
        var ltr = CascadeTestHelper.One(css, new El("div", classes: "x"));
        Assert.Equal("3px", ltr.GetPropertyValueText("padding-left"));
        Assert.Equal("5px", ltr.GetPropertyValueText("padding-right"));

        var rtl = CascadeTestHelper.One(css, new El("div", classes: "x").Attr("dir", "rtl"));
        Assert.Equal("5px", rtl.GetPropertyValueText("padding-left"));
        Assert.Equal("3px", rtl.GetPropertyValueText("padding-right"));
    }
}
