using AwAUI.Css.Cascade;
using AwAUI.Css.Media;
using AwAUI.Css.Selectors;
using AwAUI.Css.Values;

namespace AwAUI.Css.UnitTests.Cascade;

public class InheritanceTests
{
    static Dictionary<El, ComputedStyle> Tree(string css, El root, CssEnvironment? env = null, string? ua = null)
        => CascadeTestHelper.ResolveTree(CascadeTestHelper.Engine(css, ua, env), root);

    [Fact]
    public void Inherited_and_non_inherited_properties()
    {
        var child = new El("span");
        var root = new El("div").Add(child);
        var s = Tree("div { color: red; font-weight: bold; width: 10px; cursor: pointer; visibility: hidden }", root);
        Assert.Equal("rgb(255, 0, 0)", s[child].GetPropertyValueText("color"));
        Assert.Equal(700f, s[child].FontWeight);
        Assert.Equal(CssCursor.Pointer, s[child].Cursor);
        Assert.Equal(CssVisibility.Hidden, s[child].Visibility);
        Assert.Equal(SizeKind.Auto, s[child].Width.Kind);
    }

    [Fact]
    public void Groups_are_shared_by_reference()
    {
        var child = new El("span");
        var root = new El("div").Add(child);
        var s = Tree("div { color: red; width: 10px }", root);
        var p = s[root];
        var c = s[child];
        Assert.Same(p.TextGroup, c.TextGroup);
        Assert.Same(p.FontGroup, c.FontGroup);
        Assert.Same(ComputedStyle.Initial.BoxGroup, c.BoxGroup);
        Assert.NotSame(ComputedStyle.Initial.BoxGroup, p.BoxGroup);
        Assert.Same(ComputedStyle.Initial.BorderGroup, p.BorderGroup);
        // 显式设成与继承值相同的值也不复制分组
        var same = Tree("div { color: red } span { color: red }", root);
        Assert.Same(same[root].TextGroup, same[child].TextGroup);
    }

    [Fact]
    public void Inherit_initial_unset_keywords()
    {
        var child = new El("span");
        var root = new El("div").Add(child);
        var s = Tree("div { color: red; width: 10px; border-top-style: solid } span { width: inherit; color: initial; border-top-style: unset; font-size: unset }", root);
        var c = s[child];
        Assert.Equal("10px", c.GetPropertyValueText("width"));
        Assert.Equal("rgb(0, 0, 0)", c.GetPropertyValueText("color"));
        Assert.Equal(CssLineStyle.None, c.BorderTopStyle);
        Assert.Equal(16f, c.FontSize);
        var u = Tree("div { color: red } span { color: unset }", root);
        Assert.Equal("rgb(255, 0, 0)", u[child].GetPropertyValueText("color"));
    }

    [Fact]
    public void Root_inherits_from_initial()
    {
        var root = new El("div");
        var s = Tree("div { width: inherit; color: inherit }", root);
        Assert.Equal("auto", s[root].GetPropertyValueText("width"));
        Assert.Equal("rgb(0, 0, 0)", s[root].GetPropertyValueText("color"));
    }

    [Fact]
    public void Font_size_resolution_chain()
    {
        var c3 = new El("i");
        var c2 = new El("b").Add(c3);
        var c1 = new El("span").Add(c2);
        var root = new El("html").Add(c1);
        var s = Tree("html { font-size: 20px } span { font-size: 1.5em; width: 2em; height: 1rem } b { font-size: 2rem; margin-top: 1em } i { font-size: 50%; padding-top: 1em; margin-left: 10rem }", root);
        Assert.Equal(20f, s[root].FontSize);
        Assert.Equal(30f, s[c1].FontSize);
        Assert.Equal("60px", s[c1].GetPropertyValueText("width"));
        Assert.Equal("20px", s[c1].GetPropertyValueText("height"));
        Assert.Equal(40f, s[c2].FontSize);
        Assert.Equal("40px", s[c2].GetPropertyValueText("margin-top"));
        Assert.Equal(20f, s[c3].FontSize);
        Assert.Equal("20px", s[c3].GetPropertyValueText("padding-top"));
        Assert.Equal("200px", s[c3].GetPropertyValueText("margin-left"));
        Assert.Equal(20f, s[c3].RootFontSize);
    }

    [Fact]
    public void Rem_on_root_font_size_uses_initial_font_size()
    {
        var root = new El("html");
        var s = Tree("html { font-size: 2rem; width: 1rem }", root);
        Assert.Equal(32f, s[root].FontSize);
        Assert.Equal("32px", s[root].GetPropertyValueText("width"));
    }

    [Fact]
    public void Em_in_font_size_and_line_height_and_keyword_relative_sizes()
    {
        var child = new El("span");
        var root = new El("div").Add(child);
        var s = Tree("div { font-size: 10px; font-weight: 300 } span { font-size: larger; line-height: 2em; font-weight: bolder }", root);
        Assert.Equal(12f, s[child].FontSize, 3);
        Assert.Equal(24f, s[child].LineHeight.Value, 3);
        Assert.Equal(400f, s[child].FontWeight);
    }

    [Fact]
    public void Blockification()
    {
        var a = new El("a");
        var b = new El("b");
        var flex = new El("div", classes: "flex").Add(a);
        var root = new El("main").Add(flex, b);
        var s = Tree("* { display: inline } .flex { display: inline-flex } b { position: absolute; display: inline-grid }", root);
        Assert.Equal(CssDisplay.Block, s[root].Display);       // 根元素
        Assert.Equal(CssDisplay.InlineFlex, s[flex].Display);
        Assert.Equal(CssDisplay.Block, s[a].Display);          // flex 项目
        Assert.Equal(CssDisplay.Grid, s[b].Display);           // 绝对定位
    }

    [Fact]
    public void Media_switching()
    {
        var env = new CssEnvironment(new MediaEnvironment { Width = 500 });
        var engine = CascadeTestHelper.Engine(
            "div { color: red } @media (min-width: 600px) { div { color: blue } @media (orientation: landscape) { div { width: 1px } } }"
            + " div { @media (max-width: 400px) { height: 3px } }", env: env);
        var el = new El("div");
        var s = engine.Resolve(el, null);
        Assert.Equal("rgb(255, 0, 0)", s.GetPropertyValueText("color"));
        Assert.Equal("auto", s.GetPropertyValueText("width"));

        env.Media.Width = 800;
        env.Media.Height = 600;
        Assert.True(engine.EnvironmentChanged());
        s = engine.Resolve(el, null);
        Assert.Equal("rgb(0, 0, 255)", s.GetPropertyValueText("color"));
        Assert.Equal("1px", s.GetPropertyValueText("width"));

        env.Media.Width = 300;
        Assert.True(engine.EnvironmentChanged());
        s = engine.Resolve(el, null);
        Assert.Equal("3px", s.GetPropertyValueText("height"));
        Assert.Equal("rgb(255, 0, 0)", s.GetPropertyValueText("color"));

        env.Media.Width = 350;
        Assert.False(engine.EnvironmentChanged());

        env.Media.ColorScheme = MediaColorScheme.Dark;
        Assert.False(engine.EnvironmentChanged());
    }

    [Fact]
    public void Dark_theme_via_media_and_custom_properties()
    {
        var env = new CssEnvironment();
        var engine = CascadeTestHelper.Engine(":root { --fg: black } @media (prefers-color-scheme: dark) { :root { --fg: white } } p { color: var(--fg) }", env: env);
        var p = new El("p");
        var root = new El("html").Add(p);
        var s = CascadeTestHelper.ResolveTree(engine, root);
        Assert.Equal("rgb(0, 0, 0)", s[p].GetPropertyValueText("color"));
        env.Media.ColorScheme = MediaColorScheme.Dark;
        Assert.True(engine.EnvironmentChanged());
        s = CascadeTestHelper.ResolveTree(engine, root);
        Assert.Equal("rgb(255, 255, 255)", s[p].GetPropertyValueText("color"));
    }

    [Fact]
    public void Inline_style()
    {
        var diags = new List<CssDiagnostic>();
        var inline = CssInlineStyle.Parse("width: 5px; colr: red; height: x; --k: 3px; margin-top: var(--k)", diags);
        Assert.Equal(2, diags.Count);
        Assert.Contains(diags, d => d.Code == CssDiagnosticCodes.UnsupportedProperty);
        Assert.Contains(diags, d => d.Code == CssDiagnosticCodes.InvalidValue);
        var el = new El("div", "x") { Inline = inline };
        var s = CascadeTestHelper.One("#x { width: 1px; height: 2px }", el);
        Assert.Equal("5px", s.GetPropertyValueText("width"));
        Assert.Equal("2px", s.GetPropertyValueText("height"));
        Assert.Equal("3px", s.GetPropertyValueText("margin-top"));

        var fromDecls = new CssInlineStyle(CssInlineStyle.ParseDeclarations("width: 6px"));
        Assert.Null(fromDecls.Text);
        var el2 = new El("div") { Inline = fromDecls };
        Assert.Equal("6px", CascadeTestHelper.One("div { width: 1px }", el2).GetPropertyValueText("width"));
    }

    [Fact]
    public void Pseudo_elements()
    {
        var engine = CascadeTestHelper.Engine("div { color: red; display: flex } div::before { color: blue; width: 3px; display: inline } .a::after { color: green } span::before { color: black }");
        var div = new El("div", classes: "a").Style("width: 9px");
        var s = engine.Resolve(div, null);
        Assert.Equal("rgb(255, 0, 0)", s.GetPropertyValueText("color"));
        Assert.Equal("9px", s.GetPropertyValueText("width"));
        var before = engine.Resolve(div, s, PseudoElementKind.Before, null);
        Assert.Equal("rgb(0, 0, 255)", before.GetPropertyValueText("color"));
        Assert.Equal("3px", before.GetPropertyValueText("width"));
        Assert.Equal(CssDisplay.Block, before.Display);  // flex 容器的伪元素子项被块级化
        var after = engine.Resolve(div, s, PseudoElementKind.After, null);
        Assert.Equal("rgb(0, 128, 0)", after.GetPropertyValueText("color"));
        Assert.Equal("auto", after.GetPropertyValueText("width"));  // 内联样式不作用于伪元素
        Assert.Same(s.FontGroup, after.FontGroup);
    }
}
