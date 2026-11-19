using AwAUI.Css.Cascade;
using AwAUI.Css.Selectors;

namespace AwAUI.Css.UnitTests.Cascade;

/// <summary>级联排序矩阵：来源 × 重要性 × 层 × 特异性 × 出现顺序（CSS Cascade 5）。</summary>
public class CascadeOrderTests
{
    static string Color(StyleEngine e, El el)
    {
        var chain = new List<El>();
        for (StyleElement? x = el; x is not null; x = x.Parent) chain.Add((El)x);
        chain.Reverse();
        ComputedStyle? s = null;
        foreach (var c in chain) s = e.Resolve(c, s);
        return s!.GetPropertyValueText("color")!;
    }

    const string Red = "rgb(255, 0, 0)", Blue = "rgb(0, 0, 255)", Green = "rgb(0, 128, 0)";

    static StyleEngine E(string? ua = null, string? author = null, string? devtools = null, string? author2 = null)
    {
        var e = new StyleEngine();
        if (ua is not null) e.AddStyleSheet(StyleSheet.Parse(ua), CascadeOrigin.UserAgent);
        if (author is not null) e.AddStyleSheet(StyleSheet.Parse(author), CascadeOrigin.Author);
        if (author2 is not null) e.AddStyleSheet(StyleSheet.Parse(author2), CascadeOrigin.Author);
        if (devtools is not null) e.AddStyleSheet(StyleSheet.Parse(devtools), CascadeOrigin.DevTools);
        return e;
    }

    static El Div(string? inline = null)
    {
        var d = new El("div", "x", "a b c");
        if (inline is not null) d.Style(inline);
        return d;
    }

    // ================= 原位替换（DevTools 编辑、热重载） =================

    [Fact]
    public void Replacing_a_sheet_keeps_its_origin_and_position_in_the_cascade()
    {
        var e = new StyleEngine();
        var first = e.AddStyleSheet(StyleSheet.Parse("div { color: red }"));
        e.AddStyleSheet(StyleSheet.Parse("div { color: blue }"));
        Assert.Equal(Blue, Color(e, Div()));
        // 替换第一张：仍排在第二张之前，第二张照样胜出；移除再加入则会排到最后
        var replaced = e.ReplaceStyleSheet(first, StyleSheet.Parse("div { color: green; background-color: red }"))!;
        Assert.Equal(CascadeOrigin.Author, replaced.Origin);
        Assert.Same(replaced, e.StyleSheets[0]);
        Assert.Equal(Blue, Color(e, Div()));
        var again = e.ReplaceStyleSheet(replaced, StyleSheet.Parse("div { color: green !important }"))!;
        Assert.Equal(Green, Color(e, Div()));
        Assert.Null(e.ReplaceStyleSheet(first, StyleSheet.Parse("")));
        Assert.Equal(2, e.StyleSheets.Count);
        Assert.Same(again, e.StyleSheets[0]);
    }

    // ================= 来源与重要性 =================

    [Fact]
    public void Author_normal_beats_ua_normal_regardless_of_specificity()
        => Assert.Equal(Blue, Color(E(ua: "#x.a.b { color: red }", author: "div { color: blue }"), Div()));

    [Fact]
    public void Inline_normal_beats_author_normal_with_id()
        => Assert.Equal(Green, Color(E(author: "#x#x { color: blue }"), Div("color: green")));

    [Fact]
    public void Author_important_beats_inline_normal()
        => Assert.Equal(Blue, Color(E(author: "div { color: blue !important }"), Div("color: green")));

    [Fact]
    public void Inline_important_beats_author_important()
        => Assert.Equal(Green, Color(E(author: "#x { color: blue !important }"), Div("color: green !important")));

    [Fact]
    public void Ua_important_beats_author_and_inline_important()
        => Assert.Equal(Red, Color(E(ua: "div { color: red !important }", author: "#x { color: blue !important }"), Div("color: green !important")));

    [Fact]
    public void Ua_important_beats_ua_normal_and_author_normal()
        => Assert.Equal(Red, Color(E(ua: "div { color: red !important } #x { color: green }", author: "#x { color: blue }"), Div()));

    [Fact]
    public void Devtools_override_beats_everything()
    {
        Assert.Equal(Green, Color(E(ua: "div { color: red !important }", author: "#x { color: blue !important }", devtools: "div { color: green }"), Div("color: blue !important")));
        Assert.Equal(Red, Color(E(devtools: "div { color: red !important } #x { color: green }"), Div()));
    }

    [Theory]
    // (UA, author, inline) 三方都有普通/重要声明时的胜者
    [InlineData(false, false, false, "inline")]
    [InlineData(true, false, false, "ua")]
    [InlineData(false, true, false, "author")]
    [InlineData(false, false, true, "inline")]
    [InlineData(true, true, false, "ua")]
    [InlineData(false, true, true, "inline")]
    [InlineData(true, false, true, "ua")]
    [InlineData(true, true, true, "ua")]
    public void Origin_importance_matrix(bool uaImportant, bool authorImportant, bool inlineImportant, string winner)
    {
        string Imp(bool b) => b ? " !important" : "";
        var e = E(ua: $"div {{ color: red{Imp(uaImportant)} }}", author: $"div {{ color: blue{Imp(authorImportant)} }}");
        var c = Color(e, Div("color: green" + Imp(inlineImportant)));
        Assert.Equal(winner switch { "ua" => Red, "author" => Blue, _ => Green }, c);
    }

    // ================= 层 =================

    [Fact]
    public void Later_layer_wins_for_normal_declarations()
        => Assert.Equal(Blue, Color(E(author: "@layer a { #x { color: red } } @layer b { div { color: blue } }"), Div()));

    [Fact]
    public void Unlayered_wins_over_layers_for_normal_declarations()
        => Assert.Equal(Green, Color(E(author: "div { color: green } @layer a { #x { color: red } }"), Div()));

    [Fact]
    public void Earlier_layer_wins_for_important_declarations()
        => Assert.Equal(Red, Color(E(author: "@layer a { div { color: red !important } } @layer b { #x { color: blue !important } }"), Div()));

    [Fact]
    public void Layered_important_beats_unlayered_important()
        => Assert.Equal(Red, Color(E(author: "#x { color: green !important } @layer a { div { color: red !important } }"), Div()));

    [Fact]
    public void Layer_statement_fixes_order()
        => Assert.Equal(Red, Color(E(author: "@layer b, a; @layer a { div { color: red } } @layer b { #x { color: blue } }"), Div()));

    [Fact]
    public void Nested_layers_come_before_parent_direct_rules()
    {
        Assert.Equal(Blue, Color(E(author: "@layer a { div { color: blue } @layer x { #x { color: red } } }"), Div()));
        Assert.Equal(Green, Color(E(author: "@layer a { @layer x { div { color: red } } } @layer a.y { div { color: green } }"), Div()));
        Assert.Equal(Blue, Color(E(author: "@layer a.x { div { color: red } } @layer a { @layer x { } } @layer b.x { } @layer a { @layer z { div { color: blue } } } @layer a.x { #x.a { color: red } }"), Div()));
    }

    [Fact]
    public void Nested_layer_important_order_is_reversed()
        => Assert.Equal(Red, Color(E(author: "@layer a { @layer x { div { color: red !important } } div { color: blue !important } }"), Div()));

    [Fact]
    public void Anonymous_layers_are_distinct_and_ordered()
    {
        Assert.Equal(Blue, Color(E(author: "@layer { #x { color: red } } @layer { div { color: blue } }"), Div()));
        Assert.Equal(Green, Color(E(author: "div { color: green } @layer { #x { color: red } }"), Div()));
    }

    [Fact]
    public void Layer_order_spans_style_sheets()
    {
        Assert.Equal(Blue, Color(E(author: "@layer a { #x { color: red } }", author2: "@layer b { div { color: blue } }"), Div()));
        // 第二个表复用第一个表中已有的层 a：a 仍排在 b 之前
        Assert.Equal(Blue, Color(E(author: "@layer a, b; @layer b { div { color: blue } }", author2: "@layer a { #x { color: red } }"), Div()));
    }

    [Fact]
    public void Ua_layers_do_not_mix_with_author_layers()
        => Assert.Equal(Blue, Color(E(ua: "div { color: red }", author: "@layer base { div { color: blue } }"), Div()));

    [Fact]
    public void Layer_inside_nested_style_rule()
        => Assert.Equal(Blue, Color(E(author: "div { @layer q { color: red } } @layer q { div { color: blue } }"), Div()));

    // ================= 特异性与出现顺序 =================

    [Fact]
    public void Specificity_beats_order()
    {
        Assert.Equal(Red, Color(E(author: ".a { color: red } div { color: blue }"), Div()));
        Assert.Equal(Red, Color(E(author: "#x { color: red } .a.b.c { color: blue }"), Div()));
        Assert.Equal(Blue, Color(E(author: ":where(#x) { color: red } div { color: blue }"), Div()));
        Assert.Equal(Red, Color(E(author: ":is(#x, div) { color: red } .a { color: blue }"), Div()));
    }

    [Fact]
    public void Selector_list_uses_the_most_specific_matching_selector()
    {
        Assert.Equal(Red, Color(E(author: "div, #x { color: red } .a { color: blue }"), Div()));
        Assert.Equal(Blue, Color(E(author: "div, #nomatch { color: red } .a { color: blue }"), Div()));
    }

    [Fact]
    public void Later_declaration_wins_at_equal_specificity()
    {
        Assert.Equal(Blue, Color(E(author: ".a { color: red } .b { color: blue }"), Div()));
        Assert.Equal(Blue, Color(E(author: ".a { color: red; color: blue }"), Div()));
        Assert.Equal(Red, Color(E(author: ".a { color: red !important; color: blue }"), Div()));
        Assert.Equal(Blue, Color(E(author: ".a { color: red }", author2: ".b { color: blue }"), Div()));
        Assert.Equal(Red, Color(E(author: ".a { color: blue }", author2: ".b { color: red }"), Div()));
    }

    [Fact]
    public void Nested_rules_participate_with_resolved_specificity_and_order()
    {
        Assert.Equal(Blue, Color(E(author: "div { color: red; &.a { color: blue } }"), Div()));
        Assert.Equal(Green, Color(E(author: ".a { .nomatch & { color: red } color: blue; @media all { color: green } }"), Div()));
        // 嵌套声明规则在嵌套规则之后出现，顺序更晚；其特异性即外层规则的特异性
        Assert.Equal(Blue, Color(E(author: "div { & { color: red } color: blue }"), Div()));
        Assert.Equal(Red, Color(E(author: "div { &.a { color: red } color: blue }"), Div()));
    }

    [Fact]
    public void Inheritance_is_lower_than_any_declaration()
    {
        var parent = new El("section").Add(Div());
        Assert.Equal(Blue, Color(E(author: "section { color: red !important } * { color: blue }"), parent[0]));
    }

    [Fact]
    public void Removing_a_style_sheet_recompiles()
    {
        var e = new StyleEngine();
        e.AddStyleSheet(StyleSheet.Parse("div { color: red }"));
        var second = e.AddStyleSheet(StyleSheet.Parse("div { color: blue }"));
        Assert.Equal(Blue, Color(e, Div()));
        Assert.True(e.RemoveStyleSheet(second));
        Assert.False(e.RemoveStyleSheet(second));
        Assert.Equal(Red, Color(e, Div()));
        Assert.Single(e.StyleSheets);
    }
}
