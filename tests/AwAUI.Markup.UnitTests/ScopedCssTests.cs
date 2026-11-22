using AwAUI.Markup.Compiler.Styles;

namespace AwAUI.Markup.UnitTests;

/// <summary>作用域样式的选择器改写（design/12「作用域样式」）：作用域属性选择器附加在每条选择器的最后一个复合选择器上。</summary>
public class ScopedCssTests
{
    const string S = "aw-1";

    static string R(string css) => ScopedCss.Rewrite(css, S);

    [Theory]
    [InlineData(".a { color: red }", ".a[aw-1] { color: red }")]
    [InlineData(".a .b, .c>.d , e + f ~ g { }", ".a .b[aw-1], .c>.d[aw-1] , e + f ~ g[aw-1] { }")]
    [InlineData("div.x#y[data-k=\"a b\"]:hover { }", "div.x#y[data-k=\"a b\"]:hover[aw-1] { }")]
    [InlineData(".a::before, .b:after, .c::part(label) { }", ".a[aw-1]::before, .b[aw-1]:after, .c[aw-1]::part(label) { }")]
    [InlineData(":is(.a, .b) > :where(.c .d):not(.e, .f) { }", ":is(.a, .b) > :where(.c .d):not(.e, .f)[aw-1] { }")]
    [InlineData("* { }", "*[aw-1] { }")]
    [InlineData(".a /* c, d */ .b /* tail */ { }", ".a /* c, d */ .b[aw-1] /* tail */ { }")]
    [InlineData("[title='{x}'] { }", "[title='{x}'][aw-1] { }")]
    [InlineData(".a\\:b { }", ".a\\:b[aw-1] { }")]
    [InlineData("col || td { }", "col || td[aw-1] { }")]
    public void Selectors_get_the_scope_on_their_last_compound(string css, string expected) => Assert.Equal(expected, R(css));

    [Theory]
    [InlineData(".list ::deep .item { }", ".list[aw-1] .item { }")]
    [InlineData(".a > ::deep .b, .c ::deep.d { }", ".a[aw-1] > .b, .c[aw-1] .d { }")]
    [InlineData(".a::DEEP .b::before { }", ".a[aw-1] .b::before { }")]
    [InlineData(".a:hover ::deep .b { color: red }", ".a:hover[aw-1] .b { color: red }")]
    [InlineData(".a::deeper .b { }", ".a::deeper .b[aw-1] { }")]
    public void Deep_puts_the_scope_on_the_compound_before_it(string css, string expected) => Assert.Equal(expected, R(css));

    [Fact]
    public void Conditional_group_rules_are_rewritten_recursively()
    {
        Assert.Equal(
            "@media (min-width: 10px) { .a[aw-1] { } @supports (display: grid) { .b[aw-1] { } } }",
            R("@media (min-width: 10px) { .a { } @supports (display: grid) { .b { } } }"));
        Assert.Equal("@layer base { .a[aw-1] { } } @layer x, y;", R("@layer base { .a { } } @layer x, y;"));
        Assert.Equal("@container card (width > 1px) { .a[aw-1] { } }", R("@container card (width > 1px) { .a { } }"));
    }

    [Fact]
    public void Non_selector_at_rules_are_kept_verbatim()
    {
        const string css = "@import url(\"x.css\");\n@keyframes spin { from { opacity: 0 } 50% { opacity: .5 } to { opacity: 1 } }\n" +
            "@font-face { font-family: X; src: url(x.ttf) }\n@-webkit-keyframes k { 0% { top: 0 } }\n.a { animation: spin 1s }";
        Assert.Equal(css.Replace(".a {", ".a[aw-1] {"), R(css));
    }

    [Fact]
    public void Nested_rules_are_scoped_but_declarations_are_untouched()
    {
        Assert.Equal(
            ".a[aw-1] { color: red; &:hover[aw-1] { color: blue } > .b[aw-1] { x: y } .c &[aw-1] { } @media print { .d[aw-1] { } color: green; } --v: { a: b }; margin: 0 }",
            R(".a { color: red; &:hover { color: blue } > .b { x: y } .c & { } @media print { .d { } color: green; } --v: { a: b }; margin: 0 }"));
    }

    [Fact]
    public void Strings_and_comments_with_braces_do_not_confuse_the_scanner()
    {
        Assert.Equal(".a[aw-1] { content: \"}{\"; } /* .b { } */ .c[aw-1] { }", R(".a { content: \"}{\"; } /* .b { } */ .c { }"));
        Assert.Equal(".a[aw-1]::after { content: '\\'}' }", R(".a::after { content: '\\'}' }"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("}}}")]
    [InlineData(".a {")]
    [InlineData(".a { .b {")]
    [InlineData("@media {")]
    [InlineData("\"unterminated")]
    [InlineData("/* unterminated")]
    [InlineData(".a, { }")]
    [InlineData("> { }")]
    [InlineData("<!-- .a { } -->")]
    public void Broken_css_never_throws_and_only_inserts(string css)
    {
        string result = R(css);
        Assert.Equal(css, result.Replace("[aw-1]", ""));
    }

    [Fact]
    public void Scope_attribute_is_stable_and_distinct()
    {
        Assert.Equal(ScopedCss.ScopeAttributeFor("App.Counter"), ScopedCss.ScopeAttributeFor("App.Counter"));
        Assert.NotEqual(ScopedCss.ScopeAttributeFor("App.Counter"), ScopedCss.ScopeAttributeFor("App.Counter2"));
        Assert.Matches("^aw-[0-9a-f]{8}$", ScopedCss.ScopeAttributeFor("任意"));
    }

    [Fact]
    public void Deep_nesting_and_long_inputs_are_linear()
    {
        string deep = string.Concat(Enumerable.Repeat(".a { ", 100_000)) + string.Concat(Enumerable.Repeat("} ", 100_000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string result = R(deep);
        Assert.Equal(100_000, (result.Length - deep.Length) / "[aw-1]".Length);
        string wide = string.Concat(Enumerable.Repeat(":is(", 50_000)) + ".x" + string.Concat(Enumerable.Repeat(")", 50_000)) + " { }";
        Assert.EndsWith(")[aw-1] { }", R(wide));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
    }

    [Fact]
    public void Random_css_never_throws_and_only_inserts()
    {
        string[] pieces = { ".a", " ", ">", "{", "}", ";", ":", "::before", "(", ")", "[", "]", "\"", "'", "/*", "*/", "@media", "@keyframes", ",", "&", "--x:", "\\", "\n", "<!--", "-->" };
        var r = new Random(1234);
        for (int i = 0; i < 3000; i++)
        {
            var sb = new System.Text.StringBuilder();
            int n = r.Next(1, 40);
            for (int j = 0; j < n; j++) sb.Append(pieces[r.Next(pieces.Length)]);
            string css = sb.ToString();
            Assert.Equal(css, R(css).Replace("[aw-1]", ""));
        }
    }
}
