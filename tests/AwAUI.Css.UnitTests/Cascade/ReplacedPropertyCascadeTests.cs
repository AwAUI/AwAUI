using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Cascade;

/// <summary><c>object-fit / object-position / image-rendering / aspect-ratio</c> 在级联中的行为：继承性、全局关键字、var()、层叠顺序与差异。</summary>
public class ReplacedPropertyCascadeTests
{
    static Dictionary<El, ComputedStyle> Tree(string css, El root, string? ua = null)
        => CascadeTestHelper.ResolveTree(CascadeTestHelper.Engine(css, ua), root);

    [Fact]
    public void Image_rendering_inherits_and_object_properties_do_not()
    {
        var img = new El("img");
        var root = new El("div").Add(img);
        var s = Tree("div { image-rendering: pixelated; object-fit: cover; object-position: left top; aspect-ratio: 2 }", root);
        Assert.Equal(CssImageRendering.Pixelated, s[img].ImageRendering);
        Assert.Equal(CssObjectFit.Fill, s[img].ObjectFit);
        Assert.Equal(PositionValue.Center, s[img].ObjectPosition);
        Assert.Equal(AspectRatioValue.Auto, s[img].AspectRatio);
        // image-rendering 位于继承的 Text 分组：子元素与父元素共享分组实例
        Assert.Same(s[root].TextGroup, s[img].TextGroup);
    }

    [Fact]
    public void Global_keywords_apply()
    {
        var img = new El("img");
        var root = new El("div").Add(img);
        var s = Tree("div { image-rendering: pixelated; object-fit: cover; object-position: 10px 20px; aspect-ratio: auto 4 / 3 } " +
                     "img { object-fit: inherit; object-position: inherit; aspect-ratio: inherit; image-rendering: initial }", root);
        Assert.Equal(CssObjectFit.Cover, s[img].ObjectFit);
        Assert.Equal("10px 20px", s[img].GetPropertyValueText("object-position"));
        Assert.Equal("auto 4 / 3", s[img].GetPropertyValueText("aspect-ratio"));
        Assert.Equal(CssImageRendering.Auto, s[img].ImageRendering);
        Assert.True((s[img].Flags & ComputedStyleFlags.InheritsNonInherited) != 0);

        var u = Tree("div { image-rendering: crisp-edges; object-fit: cover } img { image-rendering: unset; object-fit: unset }", root);
        Assert.Equal(CssImageRendering.CrispEdges, u[img].ImageRendering); // 继承属性：unset = inherit
        Assert.Equal(CssObjectFit.Fill, u[img].ObjectFit);                  // 非继承属性：unset = initial
    }

    [Fact]
    public void Cascade_order_and_specificity()
    {
        var img = new El("img", "hero", "photo");
        var root = new El("div").Add(img);
        var s = Tree("#hero { object-fit: contain } .photo { object-fit: cover; aspect-ratio: 1 } img { aspect-ratio: 16 / 9 !important }", root,
            ua: "img { object-fit: none; aspect-ratio: 3 / 2 }");
        Assert.Equal(CssObjectFit.Contain, s[img].ObjectFit);
        Assert.Equal(16f / 9f, s[img].AspectRatio.Ratio!.Value, 5);
    }

    [Fact]
    public void Var_substitution_and_invalid_at_computed_value_time()
    {
        var img = new El("img");
        var root = new El("div").Add(img);
        var s = Tree("div { --fit: scale-down; --pos: right 5px top 0; --ratio: 3 / 1 } " +
                     "img { object-fit: var(--fit); object-position: var(--pos); aspect-ratio: auto var(--ratio) }", root);
        Assert.Equal(CssObjectFit.ScaleDown, s[img].ObjectFit);
        Assert.Equal("calc(100% - 5px) 0px", s[img].GetPropertyValueText("object-position"));
        Assert.Equal("auto 3 / 1", s[img].GetPropertyValueText("aspect-ratio"));

        // 替换后语法无效：计算值时无效，按 unset 处理
        var bad = Tree("div { --x: 10px } img { object-fit: var(--x); aspect-ratio: var(--x); image-rendering: var(--x) }", root);
        Assert.Equal(CssObjectFit.Fill, bad[img].ObjectFit);
        Assert.Equal(AspectRatioValue.Auto, bad[img].AspectRatio);
        Assert.Equal(CssImageRendering.Auto, bad[img].ImageRendering);
    }

    [Fact]
    public void Em_in_object_position_uses_the_element_font_size()
    {
        var img = new El("img");
        var root = new El("div").Add(img);
        // 根元素（div）的字体大小是 rem 的基准
        var s = Tree("div { font-size: 10px } img { font-size: 20px; object-position: 1em 3rem }", root);
        Assert.Equal("20px 30px", s[img].GetPropertyValueText("object-position"));
    }

    [Fact]
    public void Diff_reports_paint_for_object_properties_and_layout_for_aspect_ratio()
    {
        var img = new El("img");
        StyleDifference DiffOf(string a, string b)
        {
            var x = CascadeTestHelper.One(a, img);
            var y = CascadeTestHelper.One(b, img);
            return ComputedStyle.Diff(x, y);
        }
        Assert.Equal(StyleDifference.Paint, DiffOf("img { object-fit: fill }", "img { object-fit: cover }"));
        Assert.Equal(StyleDifference.Paint, DiffOf("img { object-position: left }", "img { object-position: right }"));
        Assert.Equal(StyleDifference.Paint, DiffOf("img { image-rendering: auto }", "img { image-rendering: pixelated }"));
        Assert.Equal(StyleDifference.Layout, DiffOf("img { aspect-ratio: 1 }", "img { aspect-ratio: 2 }"));
        Assert.Equal(StyleDifference.Layout, DiffOf("img { aspect-ratio: 1 }", "img { aspect-ratio: auto 1 }"));
        Assert.Equal(StyleDifference.None, DiffOf("img { aspect-ratio: 2 / 1 }", "img { aspect-ratio: 2 }"));
        Assert.Equal(StyleDifference.None, DiffOf("img { object-position: center }", "img { object-position: 50% 50% }"));
    }

    [Fact]
    public void Inline_style_wins_over_stylesheet()
    {
        var img = new El("img").Style("object-fit: none; aspect-ratio: 5");
        var root = new El("div").Add(img);
        var s = Tree("img { object-fit: cover; aspect-ratio: 1 }", root);
        Assert.Equal(CssObjectFit.None, s[img].ObjectFit);
        Assert.Equal(5f, s[img].AspectRatio.Ratio);
    }
}
