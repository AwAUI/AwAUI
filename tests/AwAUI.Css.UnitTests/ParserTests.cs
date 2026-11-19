using AwAUI.Css.Media;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.UnitTests;

public class ParserTests
{
    static CssStyleRule Style(CssRule r) => Assert.IsType<CssStyleRule>(r);

    static bool Has(StyleSheet s, string code) => s.Diagnostics.Any(d => d.Code == code);

    [Fact]
    public void Basic_rule_with_declarations()
    {
        var s = StyleSheet.Parse(".a { color: red; margin: 0 auto !important }");
        Assert.Empty(s.Diagnostics);
        var r = Style(Assert.Single(s.Rules));
        Assert.Equal(".a", r.SelectorText);
        Assert.Equal(2, r.Declarations.Count);
        Assert.Equal("color", r.Declarations[0].Name);
        Assert.Equal("red", r.Declarations[0].ValueText);
        Assert.False(r.Declarations[0].Important);
        Assert.Equal("margin", r.Declarations[1].Name);
        Assert.Equal("0 auto", r.Declarations[1].ValueText);
        Assert.True(r.Declarations[1].Important);
        Assert.Equal(3, r.Declarations[1].Value.Count); // 0, 空白, auto
    }

    [Theory]
    [InlineData("a: b ! important", true, "b")]
    [InlineData("a: b !IMPORTANT", true, "b")]
    [InlineData("a: b!important", true, "b")]
    [InlineData("a: b important", false, "b important")]
    [InlineData("a: !important", true, "")]
    [InlineData("a: b ! imp", false, "b ! imp")]
    public void Important_detection(string decl, bool important, string value)
    {
        var d = Assert.Single(StyleSheet.ParseDeclarations(decl));
        Assert.Equal(important, d.Important);
        Assert.Equal(value, d.ValueText);
    }

    [Fact]
    public void Property_names_are_lowercased_but_custom_properties_are_not()
    {
        var decls = StyleSheet.ParseDeclarations("COLOR: Red; --Main-Color:  #FFF  ; --empty:;");
        Assert.Equal("color", decls[0].Name);
        Assert.Equal("Red", decls[0].ValueText);
        Assert.Equal("--Main-Color", decls[1].Name);
        Assert.True(decls[1].IsCustomProperty);
        Assert.Equal("#FFF", decls[1].ValueText);
        Assert.Equal("--empty", decls[2].Name);
        Assert.Empty(decls[2].Value);
    }

    [Fact]
    public void Declaration_ranges_point_into_source()
    {
        const string css = ".a {\n  color : red ;\n}";
        var s = StyleSheet.Parse(css);
        var d = Style(s.Rules[0]).Declarations[0];
        Assert.Equal("color : red", css.Substring(d.Range.Start.Offset, d.Range.Length));
        Assert.Equal(2, d.Range.Start.Line);
        Assert.Equal(3, d.Range.Start.Column);
        Assert.Equal("red", css.Substring(d.ValueRange.Start.Offset, d.ValueRange.Length));
        Assert.Equal("color", css.Substring(d.NameRange.Start.Offset, d.NameRange.Length));
        Assert.Equal(css.Length, s.Rules[0].Range.End.Offset);
    }

    [Fact]
    public void Bad_declaration_is_dropped_and_parsing_continues()
    {
        var s = StyleSheet.Parse(".a { color red; background: blue }");
        var r = Style(Assert.Single(s.Rules));
        var d = Assert.Single(r.Declarations);
        Assert.Equal("background", d.Name);
        Assert.True(Has(s, CssDiagnosticCodes.SyntaxError));
    }

    [Fact]
    public void Invalid_selector_drops_rule_but_not_the_next_one()
    {
        var s = StyleSheet.Parse(".a:foo { x: 1 } .b { y: 2 }");
        var r = Style(Assert.Single(s.Rules));
        Assert.Equal(".b", r.SelectorText);
        var diag = Assert.Single(s.Diagnostics);
        Assert.Equal(CssDiagnosticCodes.UnsupportedSelector, diag.Code);
        Assert.Equal(1, diag.Range.Start.Line);
        Assert.Equal(3, diag.Range.Start.Column);
    }

    [Fact]
    public void Unclosed_block_at_eof_keeps_rule()
    {
        var s = StyleSheet.Parse(".a { color: red");
        var r = Style(Assert.Single(s.Rules));
        Assert.Equal("red", r.Declarations[0].ValueText);
        Assert.Contains(s.Diagnostics, d => d.Severity == CssDiagnosticSeverity.Warning);
    }

    [Fact]
    public void Rule_without_block_at_eof_is_dropped()
    {
        var s = StyleSheet.Parse(".a { } .b");
        Assert.Single(s.Rules);
        Assert.True(Has(s, CssDiagnosticCodes.SyntaxError));
    }

    [Fact]
    public void Top_level_declaration_swallows_the_next_rule_like_browsers()
    {
        // 顶层没有声明：'color: red; .b' 成为限定规则的序言，选择器无效，整条丢弃
        var s = StyleSheet.Parse("color: red; .b { x: 1 } .c { y: 2 }");
        var r = Style(Assert.Single(s.Rules));
        Assert.Equal(".c", r.SelectorText);
    }

    [Fact]
    public void Stray_closing_brace_at_top_level()
    {
        var s = StyleSheet.Parse("} .a { x: 1 } .b { y: 2 }");
        Assert.Equal(".b", Style(Assert.Single(s.Rules)).SelectorText);
    }

    [Fact]
    public void Cdo_cdc_are_ignored_at_top_level()
    {
        var s = StyleSheet.Parse("<!-- .a { x: 1 } -->");
        Assert.Single(s.Rules);
        Assert.Empty(s.Diagnostics);
    }

    [Fact]
    public void Brace_block_is_allowed_as_whole_value_only()
    {
        var s = StyleSheet.Parse(".a { --x: {a:b} c; foo: {bar}; baz: 1 {x} }");
        var r = Style(Assert.Single(s.Rules));
        Assert.Equal(new[] { "--x", "foo" }, r.Declarations.Select(d => d.Name));
        Assert.Equal("{a:b} c", r.Declarations[0].ValueText);
        // 'baz: 1 {x}' 不是声明，按嵌套规则解析，选择器无效被丢弃
        Assert.Empty(r.ChildRules);
    }

    [Fact]
    public void Media_rule_is_structured()
    {
        var s = StyleSheet.Parse("@media screen and (min-width: 600px) { .a { x: 1 } .b { y: 2 } }");
        Assert.Empty(s.Diagnostics);
        var m = Assert.IsType<CssMediaRule>(Assert.Single(s.Rules));
        Assert.Equal(2, m.Rules.Count);
        Assert.Same(m, m.Rules[0].Parent);
        Assert.True(m.Media.Matches(new MediaEnvironment { Width = 800 }));
        Assert.False(m.Media.Matches(new MediaEnvironment { Width = 500 }));
        Assert.Equal("screen and (min-width: 600px)", m.Media.ToString());
        Assert.NotEmpty(m.Prelude);
    }

    [Fact]
    public void Declarations_directly_in_top_level_media_are_dropped()
    {
        var s = StyleSheet.Parse("@media all { color: red; .a { x: 1 } }");
        var m = Assert.IsType<CssMediaRule>(Assert.Single(s.Rules));
        Assert.Single(m.Rules);
        Assert.True(Has(s, CssDiagnosticCodes.MisplacedDeclaration));
    }

    [Fact]
    public void Layer_statement_and_block()
    {
        var s = StyleSheet.Parse("@layer base, components.buttons; @layer base { .a {} } @layer { .b {} }");
        Assert.Empty(s.Diagnostics);
        var st = Assert.IsType<CssLayerStatementRule>(s.Rules[0]);
        Assert.Equal(new[] { "base", "components.buttons" }, st.Names);
        var b1 = Assert.IsType<CssLayerBlockRule>(s.Rules[1]);
        Assert.Equal("base", b1.Name);
        Assert.Single(b1.Rules);
        var b2 = Assert.IsType<CssLayerBlockRule>(s.Rules[2]);
        Assert.Null(b2.Name);
    }

    [Theory]
    [InlineData("@layer a b;")]
    [InlineData("@layer a. b;")]
    [InlineData("@layer a, ;")]
    [InlineData("@layer initial;")]
    [InlineData("@layer a, b { }")]
    [InlineData("@layer;")]
    public void Invalid_layer_preludes(string css)
    {
        var s = StyleSheet.Parse(css);
        Assert.Empty(s.Rules);
        Assert.True(Has(s, CssDiagnosticCodes.InvalidAtRule));
    }

    [Fact]
    public void Keyframes()
    {
        var s = StyleSheet.Parse("@keyframes spin { from { opacity: 0 } 50%, 75% { opacity: .5; color: red !important } to { opacity: 1 } 150% { x: 1 } }");
        var k = Assert.IsType<CssKeyframesRule>(Assert.Single(s.Rules));
        Assert.Equal("spin", k.Name);
        Assert.Equal(3, k.Keyframes.Count);
        Assert.Equal(new[] { 0.0 }, k.Keyframes[0].Offsets);
        Assert.Equal(new[] { 0.5, 0.75 }, k.Keyframes[1].Offsets);
        Assert.Equal("50%, 75%", k.Keyframes[1].KeyText);
        Assert.Single(k.Keyframes[1].Declarations); // !important 被忽略
        Assert.Equal(new[] { 1.0 }, k.Keyframes[2].Offsets);
        Assert.True(Has(s, CssDiagnosticCodes.InvalidAtRule));  // 150%
        Assert.True(Has(s, CssDiagnosticCodes.MisplacedDeclaration)); // !important
    }

    [Theory]
    [InlineData("@keyframes \"quoted name\" { }", "quoted name")]
    [InlineData("@KEYFRAMES Foo { }", "Foo")]
    public void Keyframes_names(string css, string name)
    {
        Assert.Equal(name, Assert.IsType<CssKeyframesRule>(Assert.Single(StyleSheet.Parse(css).Rules)).Name);
    }

    [Theory]
    [InlineData("@keyframes none { }")]
    [InlineData("@keyframes inherit { }")]
    [InlineData("@keyframes a b { }")]
    [InlineData("@keyframes { }")]
    public void Invalid_keyframes_names(string css) => Assert.Empty(StyleSheet.Parse(css).Rules);

    [Fact]
    public void Font_face()
    {
        var s = StyleSheet.Parse("@font-face { font-family: \"X\"; src: url(x.woff2) format(\"woff2\"); .nested { } }");
        var f = Assert.IsType<CssFontFaceRule>(Assert.Single(s.Rules));
        Assert.Equal(2, f.Declarations.Count);
        Assert.Equal("url(x.woff2) format(\"woff2\")", f.Declarations[1].ValueText);
        Assert.True(Has(s, CssDiagnosticCodes.MisplacedRule));
    }

    [Fact]
    public void Import_rules_and_ordering()
    {
        var s = StyleSheet.Parse("@charset \"utf-8\"; @layer x; @import url(\"a.css\") layer(base) screen; @import 'b.css' layer supports(display: grid); .a {} @import \"c.css\";");
        Assert.Equal(4, s.Rules.Count);
        var i1 = Assert.IsType<CssImportRule>(s.Rules[1]);
        Assert.Equal("a.css", i1.Url);
        Assert.True(i1.HasLayer);
        Assert.Equal("base", i1.LayerName);
        Assert.Equal("screen", i1.Media.ToString());
        var i2 = Assert.IsType<CssImportRule>(s.Rules[2]);
        Assert.Equal("b.css", i2.Url);
        Assert.True(i2.HasLayer);
        Assert.Null(i2.LayerName);
        Assert.Equal("display: grid", i2.SupportsText);
        Assert.Empty(i2.Media.Queries);
        Assert.True(Has(s, CssDiagnosticCodes.InvalidAtRule)); // c.css 位置无效
        Assert.True(Has(s, CssDiagnosticCodes.UnsupportedAtRule)); // supports()
    }

    [Fact]
    public void Unknown_at_rule_does_not_block_import()
    {
        var s = StyleSheet.Parse("@foo; @import 'a.css'; @supports (x: y) {} @import 'b.css';");
        Assert.Equal(new[] { "UnknownAtRule", "Import", "UnknownAtRule" }, s.Rules.Select(r => r.Kind.ToString()));
    }

    [Fact]
    public void Parse_component_value_list()
    {
        var d = new List<CssDiagnostic>();
        var values = CssComponentValue.ParseList("rgb(0 0 0 / 50%) 1px, [a]", d);
        Assert.Empty(d);
        Assert.Equal(6, values.Count);
        Assert.Equal("rgb", Assert.IsType<CssFunction>(values[0]).Name);
        Assert.Equal("rgb(0 0 0 / 50%) 1px, [a]", CssComponentValue.ToCss(values));
    }

    [Fact]
    public void Import_with_url_token()
    {
        var i = Assert.IsType<CssImportRule>(Assert.Single(StyleSheet.Parse("@import url(theme.css) print, screen and (width > 1px);").Rules));
        Assert.Equal("theme.css", i.Url);
        Assert.Equal(2, i.Media.Queries.Count);
    }

    [Fact]
    public void Supports_is_kept_unparsed_with_diagnostic()
    {
        var s = StyleSheet.Parse("@supports (display: grid) { .a { x: 1 } } .b { y: 2 }");
        Assert.Equal(2, s.Rules.Count);
        var u = Assert.IsType<CssUnknownAtRule>(s.Rules[0]);
        Assert.Equal("supports", u.Name);
        Assert.True(u.IsKnownUnsupported);
        Assert.NotNull(u.Block);
        Assert.Equal("(display: grid)", CssComponentValue.ToCss(u.Prelude).Trim());
        var d = Assert.Single(s.Diagnostics);
        Assert.Equal(CssDiagnosticCodes.UnsupportedAtRule, d.Code);
        Assert.Equal(CssDiagnosticSeverity.Warning, d.Severity);
        Assert.IsType<CssStyleRule>(s.Rules[1]);
    }

    [Fact]
    public void Unknown_at_rule_statement()
    {
        var s = StyleSheet.Parse("@foo bar baz; .a {}");
        Assert.Equal(2, s.Rules.Count);
        Assert.False(Assert.IsType<CssUnknownAtRule>(s.Rules[0]).IsKnownUnsupported);
        Assert.True(Has(s, CssDiagnosticCodes.UnsupportedAtRule));
    }

    [Fact]
    public void Font_face_inside_style_rule_is_misplaced()
    {
        var s = StyleSheet.Parse(".a { @font-face { x: 1 } color: red }");
        var r = Style(Assert.Single(s.Rules));
        Assert.Empty(r.ChildRules);
        Assert.Single(r.Declarations);
        Assert.True(Has(s, CssDiagnosticCodes.MisplacedRule));
    }

    [Fact]
    public void Inline_declarations_drop_rules()
    {
        var diags = new List<CssDiagnostic>();
        var decls = StyleSheet.ParseDeclarations("padding: 8px; color: red !important; .x { } ; margin: 0", diags);
        Assert.Equal(new[] { "padding", "color", "margin" }, decls.Select(d => d.Name));
        Assert.Contains(diags, d => d.Code == CssDiagnosticCodes.MisplacedRule);
    }

    [Fact]
    public void Component_values_preserve_functions_and_blocks()
    {
        var d = Assert.Single(StyleSheet.ParseDeclarations("x: calc(1px + (2 * 3%)) [a b]"));
        var fn = Assert.IsType<CssFunction>(d.Value[0]);
        Assert.Equal("calc", fn.Name);
        Assert.Contains(fn.Arguments, v => v is CssSimpleBlock { Open: '(' });
        var block = Assert.IsType<CssSimpleBlock>(d.Value[2]);
        Assert.Equal('[', block.Open);
        Assert.Equal("calc(1px + (2 * 3%)) [a b]", CssComponentValue.ToCss(d.Value));
    }

    [Fact]
    public void Unclosed_function_in_value()
    {
        var s = StyleSheet.Parse(".a { x: f(1, 2");
        var d = Style(Assert.Single(s.Rules)).Declarations[0];
        Assert.False(Assert.IsType<CssFunction>(d.Value[0]).IsClosed);
    }

    [Fact]
    public void Deeply_nested_blocks_do_not_overflow_the_stack()
    {
        var css = ".a { x: " + new string('(', 100_000) + " }";
        var s = StyleSheet.Parse(css);
        Assert.Single(s.Rules);
        Assert.NotEmpty(s.Diagnostics);
    }

    [Fact]
    public void Deeply_nested_rules_are_cut_off()
    {
        var css = string.Concat(Enumerable.Repeat(".a{", 5000)) + string.Concat(Enumerable.Repeat("}", 5000)) + ".b{}";
        var s = StyleSheet.Parse(css);
        Assert.Equal(2, s.Rules.Count);
        Assert.Contains(s.Diagnostics, d => d.Message.Contains("嵌套"));
    }

    [Fact]
    public void Enumerate_rules_is_preorder()
    {
        var s = StyleSheet.Parse(".a { .b { } } @media all { .c { } } .d { }");
        var kinds = s.EnumerateRules().Select(r => r is CssStyleRule sr ? sr.SelectorText : r.Kind.ToString()).ToArray();
        Assert.Equal(new[] { ".a", ".b", "Media", ".c", ".d" }, kinds);
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var rnd = new Random(42);
        const string alphabet = "{}()[];:,.#@!\"'\\/*-+<>=~|^$&% \n\tabc019url";
        for (int n = 0; n < 2000; n++)
        {
            var chars = new char[rnd.Next(0, 60)];
            for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[rnd.Next(alphabet.Length)];
            var s = StyleSheet.Parse(new string(chars));
            Assert.NotNull(s);
            foreach (var r in s.EnumerateRules()) Assert.True(r.Range.End.Offset >= r.Range.Start.Offset);
        }
    }
}
