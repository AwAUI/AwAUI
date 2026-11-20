using AwAUI.Css.Cascade;
using AwAUI.Css.Selectors;
using AwAUI.Css.UnitTests.Cascade;
using AwAUI.Css.Values;
using static AwAUI.Css.UnitTests.Values.ValueTestHelper;

namespace AwAUI.Css.UnitTests.Values;

/// <summary>文本、字体、内容、列表、表格与 SVG 绘制属性（第九批）。</summary>
public class TextAndContentPropertyTests
{
    [Theory]
    [InlineData("letter-spacing", "normal", "normal")]
    [InlineData("letter-spacing", "-0.025em", "-0.4px")]
    [InlineData("word-spacing", "4px", "4px")]
    [InlineData("vertical-align", "middle", "middle")]
    [InlineData("vertical-align", "text-bottom", "text-bottom")]
    [InlineData("vertical-align", "-0.125em", "-2px")]
    [InlineData("vertical-align", "10%", "10%")]
    [InlineData("text-indent", "2em hanging", "32px hanging")]
    [InlineData("text-indent", "each-line 10%", "10% each-line")]
    [InlineData("tab-size", "4", "4")]
    [InlineData("tab-size", "2em", "32px")]
    [InlineData("text-wrap-mode", "nowrap", "nowrap")]
    [InlineData("text-wrap-style", "balance", "balance")]
    [InlineData("text-transform", "uppercase", "uppercase")]
    [InlineData("hyphens", "auto", "auto")]
    [InlineData("text-decoration-line", "underline line-through", "underline line-through")]
    [InlineData("text-decoration-style", "wavy", "wavy")]
    [InlineData("text-decoration-thickness", "from-font", "from-font")]
    [InlineData("text-decoration-thickness", "0.1em", "1.6px")]
    [InlineData("text-underline-offset", "4px", "4px")]
    [InlineData("text-underline-offset", "auto", "auto")]
    [InlineData("text-underline-position", "under left", "under left")]
    [InlineData("text-shadow", "1px 1px 2px black", "rgb(0, 0, 0) 1px 1px 2px")]
    [InlineData("font-feature-settings", "\"liga\" 0, \"tnum\"", "\"liga\" 0, \"tnum\"")]
    [InlineData("font-feature-settings", "\"ss01\" on", "\"ss01\"")]
    [InlineData("font-variation-settings", "\"wght\" 650", "\"wght\" 650")]
    [InlineData("font-variant-numeric", "tabular-nums slashed-zero", "tabular-nums slashed-zero")]
    [InlineData("font-variant-caps", "small-caps", "small-caps")]
    [InlineData("font-kerning", "none", "none")]
    [InlineData("-webkit-font-smoothing", "antialiased", "antialiased")]
    [InlineData("-moz-osx-font-smoothing", "grayscale", "grayscale")]
    [InlineData("-webkit-line-clamp", "3", "3")]
    [InlineData("-webkit-line-clamp", "none", "none")]
    [InlineData("-webkit-box-orient", "vertical", "vertical")]
    [InlineData("list-style-type", "decimal", "decimal")]
    [InlineData("list-style-type", "\"- \"", "\"- \"")]
    [InlineData("list-style-position", "inside", "inside")]
    [InlineData("border-collapse", "collapse", "collapse")]
    [InlineData("border-spacing", "2px 4px", "2px 4px")]
    [InlineData("caption-side", "bottom", "bottom")]
    [InlineData("table-layout", "fixed", "fixed")]
    [InlineData("field-sizing", "content", "content")]
    [InlineData("quotes", "\"«\" \"»\"", "\"«\" \"»\"")]
    [InlineData("quotes", "none", "none")]
    [InlineData("fill", "currentcolor", "currentcolor")]
    [InlineData("fill", "none", "none")]
    [InlineData("stroke", "url(#g) red", "url(\"#g\")")]
    [InlineData("stroke-width", "2", "2px")]
    [InlineData("caret-color", "red", "rgb(255, 0, 0)")]
    [InlineData("accent-color", "auto", "auto")]
    [InlineData("scrollbar-color", "red blue", "rgb(255, 0, 0) rgb(0, 0, 255)")]
    [InlineData("color-scheme", "light dark", "light dark")]
    [InlineData("forced-color-adjust", "none", "none")]
    [InlineData("-webkit-text-fill-color", "transparent", "rgba(0, 0, 0, 0)")]
    [InlineData("appearance", "none", "none")]
    [InlineData("resize", "vertical", "vertical")]
    [InlineData("contain", "strict", "strict")]
    [InlineData("contain", "layout paint", "layout paint")]
    public void Computed_values(string property, string value, string expected) => Assert.Equal(expected, Text(property, value));

    [Theory]
    [InlineData("letter-spacing", "10%")]
    [InlineData("vertical-align", "auto")]
    [InlineData("tab-size", "-1")]
    [InlineData("text-decoration-line", "none underline")]
    [InlineData("text-decoration-line", "underline underline")]
    [InlineData("font-feature-settings", "\"toolong\" 1")]
    [InlineData("font-feature-settings", "liga")]
    [InlineData("font-variant-numeric", "lining-nums oldstyle-nums")]
    [InlineData("text-shadow", "1px 1px 1px 1px red")]
    [InlineData("text-shadow", "inset 1px 1px")]
    [InlineData("-webkit-line-clamp", "0")]
    [InlineData("quotes", "\"a\"")]
    [InlineData("list-style-type", "inherit inherit")]
    [InlineData("contain", "size inline-size")]
    public void Invalid_values(string property, string value) => Assert.False(IsValid(property, value), property + ": " + value);

    [Fact]
    public void Letter_spacing_inherits_as_absolute_length()
    {
        var engine = new StyleEngine();
        engine.AddStyleSheet(StyleSheet.Parse("p { font-size: 20px; letter-spacing: 0.1em } span { font-size: 10px }"));
        var root = new El("p").Add(new El("span"));
        var ps = engine.Resolve(root, null);
        var ss = engine.Resolve(root[0], ps);
        Assert.Equal(2f, ps.LetterSpacing);
        Assert.Equal(2f, ss.LetterSpacing); // 计算值为 px，子元素继承 2px 而不是 0.1em
        Assert.Same(ps.TextGroup.LetterSpacing.GetType(), typeof(float));
    }

    [Fact]
    public void Text_decoration_shorthand_and_inherit()
    {
        var s = Compute("text-decoration", "underline dotted red 2px");
        Assert.Equal(CssTextDecorationLine.Underline, s.TextDecorationLine);
        Assert.Equal(CssTextDecorationStyle.Dotted, s.TextDecorationStyle);
        Assert.Equal(Color.FromRgb(255, 0, 0), s.TextDecorationColor.Value);
        Assert.Equal(CssDecorationThicknessKind.Length, s.TextDecorationThickness.Kind);
        var none = Compute("text-decoration", "none");
        Assert.Equal(CssTextDecorationLine.None, none.TextDecorationLine);
        Assert.True(none.TextDecorationColor.IsCurrentColor);
        Assert.True(IsValid("text-decoration", "inherit"));
        Assert.False(IsValid("text-decoration", "underline underline"));
    }

    [Fact]
    public void Text_wrap_shorthand_and_line_clamp_alias()
    {
        var s = Compute("text-wrap", "balance");
        Assert.Equal(CssTextWrapStyle.Balance, s.TextWrapStyle);
        Assert.Equal(CssTextWrapMode.Wrap, s.TextWrapMode);
        Assert.Equal(CssTextWrapMode.Nowrap, Compute("text-wrap", "nowrap").TextWrapMode);
        Assert.Equal(2, Compute("line-clamp", "2").WebkitLineClamp);
        Assert.Equal(CssOverflowWrap.BreakWord, Compute("word-wrap", "break-word").OverflowWrap);
    }

    [Fact]
    public void List_style_shorthand()
    {
        var none = Compute("list-style", "none");
        Assert.True(none.ListStyleType.IsNone);
        Assert.True(none.ListStyleImage.Layers[0] is null);
        var s = Compute("list-style", "inside square");
        Assert.Equal("square", s.ListStyleType.CounterStyle);
        Assert.Equal(CssListStylePosition.Inside, s.ListStylePosition);
        var img = Compute("list-style", "url(a.png) none");
        Assert.True(img.ListStyleType.IsNone);
        Assert.IsType<UrlImage>(img.ListStyleImage.Layers[0]);
        Assert.False(IsValid("list-style", "none none none"));
    }

    [Fact]
    public void Content_strings_attr_counters_and_quotes()
    {
        var engine = new StyleEngine();
        engine.AddStyleSheet(StyleSheet.Parse(
            "a::before { content: \"[\" attr(href) \"]\" } a::after { content: attr(missing, \"?\") counter(item) counters(sec, \".\", upper-roman) open-quote / \"alt\" }"
            + " b::before { content: var(--tw-content) } b { --tw-content: '' } i::before { content: none } i::after { content: url(x.png) }"));
        var a = new El("a").Attr("href", "/x");
        var root = new El("div").Add(a, new El("b"), new El("i"));
        var rs = engine.Resolve(root, null);
        var aStyle = engine.Resolve(a, rs);
        var before = engine.Resolve(a, aStyle, PseudoElementKind.Before, null);
        Assert.Equal(CssContentKind.List, before.Content.Kind);
        Assert.Equal("[/x]", before.Content.PlainText);
        var after = engine.Resolve(a, aStyle, PseudoElementKind.After, null);
        Assert.Equal("alt", after.Content.AltText);
        Assert.Equal(CssContentItemKind.String, after.Content.Items[0].Kind);
        Assert.Equal("?", after.Content.Items[0].Text);
        Assert.Equal(CssContentItemKind.Counter, after.Content.Items[1].Kind);
        Assert.Equal("item", after.Content.Items[1].Text);
        Assert.Equal(CssContentItemKind.Counters, after.Content.Items[2].Kind);
        Assert.Equal(".", after.Content.Items[2].Separator);
        Assert.Equal("upper-roman", after.Content.Items[2].CounterStyle);
        Assert.Equal(CssContentItemKind.OpenQuote, after.Content.Items[3].Kind);
        Assert.Null(after.Content.PlainText);

        var b = (El)root[1];
        var bStyle = engine.Resolve(b, rs);
        Assert.Equal("", engine.Resolve(b, bStyle, PseudoElementKind.Before, null).Content.PlainText);
        var i = (El)root[2];
        var iStyle = engine.Resolve(i, rs);
        Assert.Equal(CssContentKind.None, engine.Resolve(i, iStyle, PseudoElementKind.Before, null).Content.Kind);
        Assert.Equal(CssContentItemKind.Image, engine.Resolve(i, iStyle, PseudoElementKind.After, null).Content.Items[0].Kind);
        Assert.Equal(CssContentKind.Normal, rs.Content.Kind);
    }

    [Theory]
    [InlineData("content", "counter(none)")]
    [InlineData("content", "attr()")]
    [InlineData("content", "\"a\" / ")]
    [InlineData("content", "normal \"a\"")]
    public void Invalid_content(string property, string value) => Assert.False(IsValid(property, value), value);

    [Theory]
    [InlineData("table", "table")]
    [InlineData("table-row", "table-row")]
    [InlineData("table-cell", "table-cell")]
    [InlineData("table-caption", "table-caption")]
    [InlineData("list-item", "list-item")]
    [InlineData("-webkit-box", "-webkit-box")]
    [InlineData("-webkit-inline-box", "-webkit-inline-box")]
    [InlineData("inline-table", "inline-table")]
    public void Display_table_family_and_webkit_box(string value, string expected)
    {
        var engine = new StyleEngine();
        engine.AddStyleSheet(StyleSheet.Parse($"span {{ display: {value} }}"));
        var root = new El("div").Add(new El("span"));
        var rs = engine.Resolve(root, null);
        Assert.Equal(expected, engine.Resolve(root[0], rs).GetPropertyValueText("display"));
    }

    [Fact]
    public void Blockification_keeps_block_level_displays_and_maps_inline_variants()
    {
        var engine = new StyleEngine();
        engine.AddStyleSheet(StyleSheet.Parse(".f { display: flex } .a { display: -webkit-box } .b { display: -webkit-inline-box } .c { display: inline-table } .d { display: table-cell } .e { display: list-item }"));
        var root = new El("div", null, "f").Add(new El("i", null, "a"), new El("i", null, "b"), new El("i", null, "c"), new El("i", null, "d"), new El("i", null, "e"));
        var rs = engine.Resolve(root, null);
        Assert.Equal(CssDisplay.WebkitBox, engine.Resolve(root[0], rs).Display);
        Assert.Equal(CssDisplay.WebkitBox, engine.Resolve(root[1], rs).Display);
        Assert.Equal(CssDisplay.Table, engine.Resolve(root[2], rs).Display);
        Assert.Equal(CssDisplay.Block, engine.Resolve(root[3], rs).Display);
        Assert.Equal(CssDisplay.ListItem, engine.Resolve(root[4], rs).Display);
    }

    [Fact]
    public void Svg_paint_typed_access()
    {
        var s = Compute("fill", "currentcolor", extra: "color: blue;");
        Assert.Equal(CssSvgPaintKind.Color, s.Fill.Kind);
        Assert.Equal(Color.FromRgb(0, 0, 255), s.ResolveColor(s.Fill.Color));
        Assert.Equal(CssSvgPaintKind.None, ComputedStyle.Initial.Stroke.Kind);
        Assert.Equal(CssSvgPaintKind.Color, ComputedStyle.Initial.Fill.Kind);
        Assert.Equal(1f, ComputedStyle.Initial.StrokeWidth.Length);
    }

    [Fact]
    public void Inherited_text_properties_inherit()
    {
        var engine = CascadeTestHelper.Engine("p { letter-spacing: 1px; text-transform: uppercase; tab-size: 2; list-style-type: square; quotes: none; text-wrap: nowrap; font-feature-settings: \"tnum\"; text-shadow: 0 0 1px red; fill: red }");
        var root = new El("p").Add(new El("span"));
        var map = CascadeTestHelper.ResolveTree(engine, root);
        var child = map[(El)root[0]];
        Assert.Equal(1f, child.LetterSpacing);
        Assert.Equal(CssTextTransform.Uppercase, child.TextTransform);
        Assert.Equal(2f, child.TabSize.Value);
        Assert.Equal("square", child.ListStyleType.CounterStyle);
        Assert.Same(map[root].Quotes, child.Quotes);
        Assert.Equal(CssTextWrapMode.Nowrap, child.TextWrapMode);
        Assert.Single(child.FontFeatureSettings);
        Assert.False(child.TextShadow.IsNone);
        Assert.Equal(CssSvgPaintKind.Color, child.Fill.Kind);
        // 不继承的：vertical-align、text-decoration-line、content
        var e2 = CascadeTestHelper.Engine("p { vertical-align: middle; text-decoration-line: underline; content: 'x' }");
        var m2 = CascadeTestHelper.ResolveTree(e2, root);
        Assert.Equal(CssVerticalAlignKind.Baseline, m2[(El)root[0]].VerticalAlign.Kind);
        Assert.Equal(CssTextDecorationLine.None, m2[(El)root[0]].TextDecorationLine);
        Assert.Equal(CssContentKind.Normal, m2[(El)root[0]].Content.Kind);
    }

    [Fact]
    public void Lh_and_rlh_units()
    {
        var engine = new StyleEngine();
        engine.AddStyleSheet(StyleSheet.Parse("html { font-size: 10px; line-height: 2 } p { font-size: 20px; line-height: 30px; height: 2lh; width: 1rlh } i { line-height: 1lh } b { min-height: 1lh }"));
        var p = new El("p").Add(new El("i"));
        var root = new El("html").Add(p, new El("b"));
        var map = CascadeTestHelper.ResolveTree(engine, root);
        Assert.Equal("60px", map[p].GetPropertyValueText("height"));
        Assert.Equal("20px", map[p].GetPropertyValueText("width"));
        Assert.Equal("30px", map[(El)p[0]].GetPropertyValueText("line-height")); // line-height 中的 lh 用父元素的行高
        Assert.Equal("20px", map[(El)root[1]].GetPropertyValueText("min-height"));
        Assert.Equal(20f, map[root].RootLineHeight);
    }
}
