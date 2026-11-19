using AwAUI.Css.Cascade;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Cascade;

public class RevertTests
{
    static (ComputedStyle Parent, ComputedStyle Child) Resolve(string? ua, string author, string? devtools = null, string? inline = null)
    {
        var e = new StyleEngine();
        if (ua is not null) e.AddStyleSheet(StyleSheet.Parse(ua), CascadeOrigin.UserAgent);
        e.AddStyleSheet(StyleSheet.Parse(author));
        if (devtools is not null) e.AddStyleSheet(StyleSheet.Parse(devtools), CascadeOrigin.DevTools);
        var child = new El("div", "x", "a");
        if (inline is not null) child.Style(inline);
        var root = new El("section").Add(child);
        var ps = e.Resolve(root, null);
        return (ps, e.Resolve(child, ps));
    }

    [Fact]
    public void Revert_in_author_rolls_back_to_ua()
    {
        var (_, c) = Resolve("div { display: flex; color: red }", "div { display: revert; color: blue } .a { color: revert }");
        Assert.Equal(CssDisplay.Flex, c.Display);
        Assert.Equal("rgb(255, 0, 0)", c.GetPropertyValueText("color"));
    }

    [Fact]
    public void Revert_without_ua_value_acts_as_unset()
    {
        var (p, c) = Resolve(null, "section { color: green; width: 5px } div { color: revert; width: revert }");
        Assert.Equal(p.Color, c.Color);
        Assert.Equal(SizeKind.Auto, c.Width.Kind);
    }

    [Fact]
    public void Important_author_revert_rolls_back_to_ua_normal()
    {
        var (_, c) = Resolve("div { width: 7px }", "#x { width: 1px } div { width: revert !important }");
        Assert.Equal("7px", c.GetPropertyValueText("width"));
    }

    [Fact]
    public void Inline_revert_rolls_back_to_ua()
    {
        var (_, c) = Resolve("div { width: 7px }", "#x { width: 1px }", inline: "width: revert");
        Assert.Equal("7px", c.GetPropertyValueText("width"));
    }

    [Fact]
    public void Devtools_revert_rolls_back_to_author()
    {
        var (_, c) = Resolve("div { width: 7px }", "#x { width: 1px }", devtools: "div { width: revert }");
        Assert.Equal("1px", c.GetPropertyValueText("width"));
    }

    [Fact]
    public void Revert_layer_rolls_back_to_previous_layer()
    {
        var (_, c) = Resolve(null, "@layer a { div { width: 1px } } @layer b { #x { width: 2px } } @layer c { div { width: revert-layer } }");
        Assert.Equal("2px", c.GetPropertyValueText("width"));
        var (_, d) = Resolve(null, "@layer a { div { width: 1px } } div { width: revert-layer }");
        Assert.Equal("1px", d.GetPropertyValueText("width"));
        var (_, e) = Resolve("div { width: 9px }", "@layer a { div { width: revert-layer } }");
        Assert.Equal("9px", e.GetPropertyValueText("width"));
        // 同一层中的其他声明也被跳过
        var (_, f) = Resolve(null, "@layer a { div { width: 1px } } @layer b { div { width: 2px } #x { width: revert-layer } }");
        Assert.Equal("1px", f.GetPropertyValueText("width"));
    }

    [Fact]
    public void Revert_in_shorthand_applies_to_each_longhand()
    {
        var (_, c) = Resolve("div { margin: 1px 2px }", "div { margin: 5px } #x { margin: revert }");
        Assert.Equal("1px", c.GetPropertyValueText("margin-top"));
        Assert.Equal("2px", c.GetPropertyValueText("margin-right"));
    }

    [Fact]
    public void Revert_custom_property()
    {
        var (_, c) = Resolve(":root, div { --k: ua }", "div { --k: author } #x { --k: revert }");
        Assert.Equal("ua", c.GetCustomPropertyText("--k"));
    }
}
