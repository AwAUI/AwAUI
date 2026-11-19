using AwAUI.Markup.Compiler;
using AwAUI.Markup.Compiler.Syntax;
using AwAUI.Markup.Compiler.Transforms;
using static AwAUI.Markup.UnitTests.TestUtil;

namespace AwAUI.Markup.UnitTests;

public class ParserTests
{
    static string Norm(string text) => Dump(ParseNormalized(text)).Replace("\r", "");

    static void AssertTree(string text, string expected) =>
        Assert.Equal(expected.Replace("\r", "").TrimEnd() + "\n", Norm(text));

    // ---------------------------------------------------------------- 指令

    [Fact]
    public void Parses_directives()
    {
        var doc = ParseClean("""
            @using System.Text
            @using Json = System.Text.Json;
            @namespace MyApp.Pages
            @inherits LayoutBase
            @implements System.IDisposable
            @typeparam TItem where TItem : class
            @attribute [Obsolete("x")]
            <div />
            """);
        var ds = doc.Items.OfType<DirectiveSyntax>().ToList();
        Assert.Equal(7, ds.Count);
        Assert.Equal((DirectiveKind.Using, "System.Text"), (ds[0].DirectiveKind, ds[0].Value));
        Assert.Equal("Json = System.Text.Json", ds[1].Value); // 末尾分号去掉
        Assert.Equal(DirectiveKind.Namespace, ds[2].DirectiveKind);
        Assert.Equal("LayoutBase", ds[3].Value);
        Assert.Equal(DirectiveKind.Implements, ds[4].DirectiveKind);
        Assert.Equal("TItem where TItem : class", ds[5].Value);
        Assert.Equal("[Obsolete(\"x\")]", ds[6].Value);
        Assert.Equal("@using", doc.Items[0] is DirectiveSyntax d0 ? "@" + d0.Name : null);
        Assert.Equal(new TextSpan(0, 6), ds[0].NameSpan);
        Assert.Equal(new TextSpan(7, 11), ds[0].ValueSpan);
    }

    [Fact]
    public void Attribute_directive_may_span_lines()
    {
        var doc = ParseClean("@attribute [Foo(\n  1, 2)]\n<div />");
        Assert.Equal("[Foo(\n  1, 2)]", doc.Items.OfType<DirectiveSyntax>().Single().Value);
    }

    [Fact]
    public void Comments_may_precede_directives()
    {
        var doc = ParseClean("@* header *@\n@using X\n<div/>");
        Assert.IsType<CommentSyntax>(doc.Items[0]);
        Assert.Contains(doc.Items, n => n is DirectiveSyntax);
    }

    // ---------------------------------------------------------------- @code 与 <style>

    [Fact]
    public void Parses_code_block_with_nasty_csharp()
    {
        const string code = """"

                string s = "}";
                char c = '}';
                string v = @"}""{";
                string i = $"{(a ? "}" : "{")}";
                string r = $$"""{{x}} } """;
                /* } */ // }
            #if DEBUG }
            #endif
                void M() { if (x) { } }

            """";
        var doc = ParseClean("<div/>\n@code {" + code + "}\n");
        var cb = doc.Items.OfType<CodeBlockSyntax>().Single();
        Assert.Equal(code, cb.Code);
        Assert.True(cb.IsClosed);
    }

    [Fact]
    public void Parses_style_blocks_raw()
    {
        var doc = ParseClean("""
            <div/>
            <style>
                .a { color: red; } @media (min-width: 10px) { .b { x: "@y" } }
            </style>
            <style global>
                :root { --x: 1; }
            </style>
            """);
        var styles = doc.Items.OfType<StyleBlockSyntax>().ToList();
        Assert.Equal(2, styles.Count);
        Assert.False(styles[0].IsGlobal);
        Assert.Contains("@media (min-width: 10px)", styles[0].Content);
        Assert.Contains("\"@y\"", styles[0].Content); // @ 不在样式中解释
        Assert.True(styles[1].IsGlobal);
        Assert.Equal("\n    :root { --x: 1; }\n", styles[1].Content);
    }

    [Fact]
    public void Style_content_may_contain_tag_like_text()
    {
        var doc = ParseClean("<style>.a::after { content: \"</div>\"; } </styles> </style>");
        Assert.Contains("</styles>", doc.Items.OfType<StyleBlockSyntax>().Single().Content);
    }

    // ---------------------------------------------------------------- 元素

    [Fact]
    public void Parses_intrinsic_and_component_elements()
    {
        AssertTree("""<div class="a"><Button Size="Large" /><Ui.Controls.Card></Ui.Controls.Card></div>""", """
            Document
              Element div
                Attribute class
                  AttributeValue "
                    AttributeLiteral "a"
                Element Button />
                  Attribute Size
                    AttributeValue "
                      AttributeLiteral "Large"
                Element Ui.Controls.Card
            """);
        var doc = ParseClean("<div><Button/><Ns.card/></div>");
        var div = (ElementSyntax)doc.Items[0];
        Assert.Equal(ElementKind.Intrinsic, div.ElementKind);
        Assert.Equal(ElementKind.Component, ((ElementSyntax)div.Children[0]).ElementKind);
        Assert.Equal(ElementKind.Component, ((ElementSyntax)div.Children[1]).ElementKind); // 限定名
    }

    [Fact]
    public void Parses_generic_component_tags()
    {
        var doc = ParseClean("<Grid<Dictionary<string, int>> Items=\"rows\"></Grid>");
        var e = (ElementSyntax)doc.Items[0];
        Assert.Equal("Grid", e.Name);
        Assert.Equal("Dictionary<string, int>", e.TypeArguments);
        Assert.True(e.IsClosed);
    }

    [Fact]
    public void Element_spans_are_precise()
    {
        const string text = "<div id=\"x\">hi</div>";
        var e = (ElementSyntax)ParseClean(text).Items[0];
        Assert.Equal(new TextSpan(0, text.Length), e.Span);
        Assert.Equal(new TextSpan(1, 3), e.NameSpan);
        Assert.Equal(new TextSpan(0, 12), e.StartTagSpan);
        Assert.Equal(new TextSpan(14, 6), e.EndTagSpan);
        var attr = e.Attributes[0];
        Assert.Equal(new TextSpan(5, 6), attr.Span);
        Assert.Equal(new TextSpan(8, 3), attr.Value!.Span);
        Assert.Equal(new TextSpan(12, 2), e.Children[0].Span);
    }

    [Fact]
    public void Void_like_elements_accept_explicit_end_tag()
    {
        var e = (ElementSyntax)ParseClean("<img src=\"a.png\"></img>").Items[0];
        Assert.True(e.IsClosed);
        Assert.False(e.IsSelfClosing);
        Assert.Empty(e.Children);
    }

    [Fact]
    public void Parses_fragments()
    {
        AssertTree("<><div/>text</>", """
            Document
              Fragment
                Element div />
                Text "text"
            """);
    }

    [Fact]
    public void Multiple_roots_are_allowed()
    {
        var doc = ParseNormalized("<div/>\n<span/>\n<text>x</text>");
        Assert.Equal(3, doc.Items.Count);
    }

    // ---------------------------------------------------------------- 属性

    [Fact]
    public void Parses_static_boolean_and_mixed_attributes()
    {
        AssertTree("""<input disabled type='text' class="tab @(active ? "on" : "") x-@id" aria-label="名称" data-row-id="1" />""", """
            Document
              Element input />
                Attribute disabled
                Attribute type
                  AttributeValue '
                    AttributeLiteral "text"
                Attribute class
                  AttributeValue "
                    AttributeLiteral "tab "
                    Expression explicit "active ? \"on\" : \"\""
                    AttributeLiteral " x-"
                    Expression implicit "id"
                Attribute aria-label
                  AttributeValue "
                    AttributeLiteral "名称"
                Attribute data-row-id
                  AttributeValue "
                    AttributeLiteral "1"
            """);
    }

    [Fact]
    public void Parses_directive_attributes()
    {
        var e = (ElementSyntax)ParseClean("""
            <input @bind="draft" @bind:event="change" @onkeydown="e => Key(e, item)" @key="t.Id" @ref="box" @attributes="extra" />
            """).Items[0];
        var names = e.Attributes.Select(a => a.FullName).ToArray();
        Assert.Equal(new[] { "@bind", "@bind:event", "@onkeydown", "@key", "@ref", "@attributes" }, names);
        Assert.True(e.Attributes.All(a => a.IsDirective));
        Assert.Equal("event", e.Attributes[1].Parameter);
        Assert.Equal("e => Key(e, item)", e.Attributes[2].Value!.LiteralText);
        var c = (ElementSyntax)ParseClean("<Slider @bind-Value=\"level\" />").Items[0];
        Assert.Equal("bind-Value", c.Attributes[0].Name);
    }

    [Fact]
    public void Parses_unquoted_expression_attribute_values()
    {
        var e = (ElementSyntax)ParseClean("<Counter Start=@initial Step=@(a + b) />").Items[0];
        Assert.Equal("initial", e.Attributes[0].Value!.SingleExpression!.Code);
        Assert.Equal('\0', e.Attributes[0].Value!.Quote);
        Assert.Equal("a + b", e.Attributes[1].Value!.SingleExpression!.Code);
    }

    [Fact]
    public void Attribute_values_decode_entities_and_escapes()
    {
        var e = (ElementSyntax)ParseClean("<div title=\"a &amp; b &quot;c&quot; @@d &#x4E2D;\" />").Items[0];
        var lit = Assert.IsType<AttributeLiteralSyntax>(Assert.Single(e.Attributes[0].Value!.Parts));
        Assert.Equal("a & b \"c\" @d 中", lit.Value);
        Assert.Equal("a &amp; b &quot;c&quot; @@d &#x4E2D;", lit.Text);
    }

    [Fact]
    public void Email_in_attribute_value_is_literal()
    {
        var e = (ElementSyntax)ParseClean("<div title=\"mailto:me@example.com\" />").Items[0];
        Assert.Equal("mailto:me@example.com", e.Attributes[0].Value!.LiteralValue);
    }

    [Fact]
    public void Quoted_value_with_expression_containing_quotes()
    {
        var e = (ElementSyntax)ParseClean("<div title=\"@Format(\"x > y\", name)!\" />").Items[0];
        var parts = e.Attributes[0].Value!.Parts;
        Assert.Equal("Format(\"x > y\", name)", ((ExpressionSyntax)parts[0]).Code);
        Assert.Equal("!", ((AttributeLiteralSyntax)parts[1]).Value);
    }

    [Fact]
    public void Multiline_attribute_values_are_allowed()
    {
        var e = (ElementSyntax)ParseClean("<div class=\"a\n   b\" />").Items[0];
        Assert.Equal("a\n   b", e.Attributes[0].Value!.LiteralValue);
    }

    // ---------------------------------------------------------------- 文本与表达式

    [Fact]
    public void Parses_text_entities_and_at_escape()
    {
        var t = (TextSyntax)ParseClean("a &lt;b&gt; &copy; &#169; &unknown; @@ x").Items[0];
        Assert.Equal("a <b> © © &unknown; @ x", t.Value);
    }

    [Theory]
    [InlineData("@user.Name", "user.Name", "")]
    [InlineData("@user.Name.", "user.Name", ".")]
    [InlineData("@a?.b?.c!", "a?.b?.c", "!")]
    [InlineData("@a!.b", "a!.b", "")]
    [InlineData("@a![0].b", "a![0].b", "")]
    [InlineData("@items[i + 1].Title", "items[i + 1].Title", "")]
    [InlineData("@Format(\"(\", x)(y)", "Format(\"(\", x)(y)", "")]
    [InlineData("@a.b-c", "a.b", "-c")]
    [InlineData("@x+1", "x", "+1")]
    [InlineData("@obj.Method<int>()", "obj.Method", "<int>()")]
    [InlineData("@count.Value）", "count.Value", "）")]
    [InlineData("@a ?. b", "a", " ?. b")]
    [InlineData("@a.1", "a", ".1")]
    [InlineData("@_x9", "_x9", "")]
    [InlineData("@名字.长度", "名字.长度", "")]
    public void Implicit_expression_greedy_rules(string text, string code, string rest)
    {
        // 用 <text> 包住，便于剩余部分成为文本（< 之后的 int> 会出错，所以只取第一个表达式与紧随的文本）
        var tree = MarkupSyntaxTree.Parse(text);
        var expr = Assert.IsType<ExpressionSyntax>(tree.Root.Items[0]);
        Assert.False(expr.IsExplicit);
        Assert.Equal(code, expr.Code);
        Assert.Equal(new TextSpan(1, code.Length), expr.CodeSpan);
        string actualRest = text.Substring(expr.Span.End);
        Assert.Equal(rest, actualRest);
    }

    [Theory]
    [InlineData("me@example.com")]
    [InlineData("a1@b")]
    [InlineData("中文@x")]
    public void Email_like_text_is_not_an_expression(string text)
    {
        var t = Assert.IsType<TextSyntax>(Assert.Single(ParseClean(text).Items));
        Assert.Equal(text, t.Value);
    }

    [Fact]
    public void At_after_punctuation_is_an_expression()
    {
        var items = ParseClean("(@x)").Items;
        Assert.Equal(3, items.Count);
        Assert.Equal("x", ((ExpressionSyntax)items[1]).Code);
    }

    [Fact]
    public void Explicit_expression_after_letter_is_still_an_expression()
    {
        var items = ParseClean("a@(b)").Items;
        Assert.Equal("b", Assert.IsType<ExpressionSyntax>(items[1]).Code);
    }

    [Fact]
    public void Parses_explicit_expressions()
    {
        var e = (ExpressionSyntax)ParseClean("@(await LoadAsync(\")\") + $\"{x})\")").Items[0];
        Assert.True(e.IsExplicit);
        Assert.Equal("await LoadAsync(\")\") + $\"{x})\"", e.Code);
    }

    [Fact]
    public void Parses_comments()
    {
        AssertTree("<div>a@* <b>x</b> @if *@b</div>", """
            Document
              Element div
                Text "a"
                Comment " <b>x</b> @if "
                Text "b"
            """);
    }

    // ---------------------------------------------------------------- 语句块与控制流

    [Fact]
    public void Parses_statement_block_with_markup()
    {
        AssertTree("""
            @{
                var total = items.Sum(i => i.Price);
                if (total > 100) { <text>贵</text> }
                string s = "<div>";
            }
            """, """
            Document
              StatementBlock
                CodeBody
                  CSharpCode "\n    var total = items.Sum(i => i.Price);\n    if (total > 100) { "
                  Element text
                    Text "贵"
                  CSharpCode " }\n    string s = \"<div>\";\n"
            """);
    }

    [Fact]
    public void Less_than_in_code_is_not_markup()
    {
        var doc = ParseNormalized("""
            @{
                if (a < b && c<d) { x = y < z ? 1 : 2; }
                var l = new List<int>();
                Foo(a<b, c>d);
            }
            """);
        var body = ((StatementBlockSyntax)doc.Items[0]).Body;
        Assert.IsType<CSharpCodeSyntax>(Assert.Single(body.Nodes));
    }

    [Fact]
    public void Markup_in_braceless_if_inside_code_block()
    {
        var doc = ParseNormalized("@{ if (x) <div/> else <span/> }");
        var nodes = ((StatementBlockSyntax)doc.Items[0]).Body.Nodes;
        Assert.Equal(new[] { SyntaxKind.CSharpCode, SyntaxKind.Element, SyntaxKind.CSharpCode, SyntaxKind.Element }, nodes.Select(n => n.Kind));
    }

    [Fact]
    public void Parses_if_else_chain()
    {
        AssertTree("""
            @if (a > 0)
            {
                <text>正</text>
            }
            else if (a < 0) {
                <text>负</text>
            }
            else
            {
                @: 零 @a
            }
            """, """
            Document
              If
                IfBranch If "a > 0"
                  CodeBody
                    Element text
                      Text "正"
                IfBranch ElseIf "a < 0"
                  CodeBody
                    Element text
                      Text "负"
                IfBranch Else
                  CodeBody
                    LineText
                      Text " 零 "
                      Expression implicit "a"
            """);
    }

    [Fact]
    public void Else_must_follow_if_closing_brace_otherwise_it_is_text()
    {
        var doc = ParseNormalized("@if (x) { <A/> } elsewhere");
        Assert.Equal(2, doc.Items.Count);
        Assert.Equal(" elsewhere", ((TextSyntax)doc.Items[1]).Value);
    }

    [Fact]
    public void Parses_loops()
    {
        AssertTree("""
            @foreach (var (k, v) in map) { <Row @key="k" /> }
            @for (int i = 0; i < n; i++) { <Cell @key="i">@i</Cell> }
            @while (it.MoveNext()) { <Item @key="it.Current" /> }
            """, """
            Document
              Loop foreach "var (k, v) in map"
                CodeBody
                  Element Row />
                    Attribute @key
                      AttributeValue "
                        AttributeLiteral "k"
              Loop for "int i = 0; i < n; i++"
                CodeBody
                  Element Cell
                    Attribute @key
                      AttributeValue "
                        AttributeLiteral "i"
                    Expression implicit "i"
              Loop while "it.MoveNext()"
                CodeBody
                  Element Item />
                    Attribute @key
                      AttributeValue "
                        AttributeLiteral "it.Current"
            """);
    }

    [Fact]
    public void Parses_switch_with_markup_sections()
    {
        AssertTree("""
            @switch (state)
            {
                case State.Loading:
                case State.Idle when x > 0:
                    <Spinner />
                    break;
                case "a:b":
                    <text>@("x")</text>
                    break;
                default:
                    { <text>?</text> }
                    break;
            }
            """, """
            Document
              Switch "state"
                SwitchSection
                  SwitchLabel "case State.Loading:"
                  SwitchLabel "case State.Idle when x > 0:"
                  Element Spinner />
                  CSharpCode "\n        break;\n    "
                SwitchSection
                  SwitchLabel "case \"a:b\":"
                  Element text
                    Expression explicit "\"x\""
                  CSharpCode "\n        break;\n    "
                SwitchSection
                  SwitchLabel "default:"
                  CSharpCode "\n        { "
                  Element text
                    Text "?"
                  CSharpCode " }\n        break;\n"
            """);
    }

    [Fact]
    public void Nested_control_flow_and_markup_in_code()
    {
        var doc = ParseNormalized("""
            <ul>
                @foreach (var g in groups)
                {
                    <Group @key="g.Id">
                        @if (g.Items.Count > 0)
                        {
                            @foreach (var i in g.Items)
                            {
                                <Item @key="i.Id">@i.Name (@i.Count)</Item>
                            }
                        }
                    </Group>
                }
            </ul>
            """.Replace("ul", "div"));
        var item = doc.DescendantNodes().OfType<ElementSyntax>().Single(e => e.Name == "Item");
        Assert.Equal(new[] { "i.Name", " (", "i.Count", ")" },
            item.Children.Select(c => c is ExpressionSyntax x ? x.Code : ((TextSyntax)c).Value));
    }

    [Fact]
    public void Markup_inside_code_uses_markup_rules()
    {
        // 代码体内的元素内容是标记：} 只是文本，@ 是表达式
        var doc = ParseNormalized("@if (x) { <text>a } b @y</text> }");
        var text = doc.DescendantNodes().OfType<ElementSyntax>().Single();
        Assert.Equal(2, text.Children.Count);
        Assert.Equal("a } b ", ((TextSyntax)text.Children[0]).Value);
    }

    [Fact]
    public void Line_text_in_code_block()
    {
        var doc = ParseClean("@{\n    @:Hello, @name! <span>x</span>\n    var y = 1;\n}");
        var nodes = ((StatementBlockSyntax)doc.Items[0]).Body.Nodes;
        var lt = Assert.IsType<LineTextSyntax>(nodes[0]);
        Assert.Equal(new[] { SyntaxKind.Text, SyntaxKind.Expression, SyntaxKind.Text, SyntaxKind.Element }, lt.Children.Select(c => c.Kind));
        Assert.Equal("var y = 1;", ((CSharpCodeSyntax)nodes[1]).Code.Trim());
    }

    [Fact]
    public void Expression_at_statement_start_in_code_body()
    {
        var doc = ParseClean("@foreach (var x in xs) { @x }");
        var body = ((LoopSyntax)doc.Items[0]).Body;
        Assert.Equal("x", Assert.IsType<ExpressionSyntax>(Assert.Single(body.Nodes)).Code);
    }

    [Fact]
    public void Verbatim_identifiers_and_strings_in_code_are_code()
    {
        var doc = ParseClean("@{ var s = @\"<div>\"; var @class = 1; x = @class; }");
        var body = ((StatementBlockSyntax)doc.Items[0]).Body;
        Assert.IsType<CSharpCodeSyntax>(Assert.Single(body.Nodes));
    }

    [Fact]
    public void Comments_in_code_do_not_hide_markup_boundaries()
    {
        var doc = ParseClean("@{ // }\n /* } */ <div/> }");
        var body = ((StatementBlockSyntax)doc.Items[0]).Body;
        Assert.True(body.IsClosed);
        Assert.Single(body.Nodes.OfType<ElementSyntax>());
    }

    [Fact]
    public void Switch_labels_accept_modern_patterns()
    {
        const string text = """
            @switch (o)
            {
                case Point { X: 0, Y: var y } when y > 0:
                    <text>origin</text>
                    break;
                case < 0:
                case > 0 and <= 10:
                    <text>small</text>
                    break;
                case List<int> l:
                    <text>list</text>
                    break;
                case (1, 2) or [_, ..]:
                    break;
                case var s when (s as string) ?? "" == "a:b":
                    <text>s</text>
                    break;
                case Dictionary<string, List<int>> { Count: > 0 }:
                    break;
                default:
                    <text>other</text>
                    break;
            }
            """;
        var doc = ParseClean(text);
        Assert.Equal(new[]
        {
            "case Point { X: 0, Y: var y } when y > 0:", "case < 0:", "case > 0 and <= 10:", "case List<int> l:",
            "case (1, 2) or [_, ..]:", "case var s when (s as string) ?? \"\" == \"a:b\":",
            "case Dictionary<string, List<int>> { Count: > 0 }:", "default:",
        }, doc.DescendantNodes().OfType<SwitchLabelSyntax>().Select(l => l.Text).ToArray());
        Assert.Equal(new[] { "origin", "small", "list", "s", "other" },
            doc.DescendantNodes().OfType<ElementSyntax>().Select(e => ((TextSyntax)e.Children[0]).Value).ToArray());
        RoundTrip.Check(text);
    }

    [Fact]
    public void Raw_csharp_switch_with_property_pattern_supports_markup_after_label()
    {
        var doc = ParseClean("@{ switch (p) { case Point { X: 0 }: <A1/> break; case { Length: > 2 } s when s.Length < 9: <A2/> break; } }");
        Assert.Equal(new[] { "A1", "A2" }, doc.DescendantNodes().OfType<ElementSyntax>().Select(e => e.Name).ToArray());
    }

    [Fact]
    public void At_right_after_an_implicit_expression_starts_another_expression()
    {
        // 邮箱启发式只看文本中的字母数字；@count 的结尾不算
        var text = (ElementSyntax)ParseClean("<text>@count@unit a@b.c</text>").Items[0];
        Assert.Equal("count", Assert.IsType<ExpressionSyntax>(text.Children[0]).Code);
        Assert.Equal("unit", Assert.IsType<ExpressionSyntax>(text.Children[1]).Code);
        Assert.Equal(" a@b.c", Assert.IsType<TextSyntax>(text.Children[2]).Value);

        var div = (ElementSyntax)ParseClean("<div title=\"@a@b x@y\" />").Items[0];
        var parts = div.Attributes[0].Value!.Parts;
        Assert.Equal(new[] { "a", "b" }, parts.OfType<ExpressionSyntax>().Select(e => e.Code).ToArray());
        Assert.Equal(" x@y", Assert.IsType<AttributeLiteralSyntax>(parts[2]).Value);

        Assert.Contains("@count@unit", RoundTrip.Check("<text>@count@unit</text>"));
        Assert.Contains("@count@@unit", RoundTrip.Check("<text>@count@@unit</text>"));
    }

    [Fact]
    public void Raw_csharp_switch_inside_code_block_supports_markup_after_labels()
    {
        var doc = ParseClean("@{ switch (k) { case 1: <A1/> break; default: <A2/> break; } }");
        Assert.Equal(2, doc.DescendantNodes().OfType<ElementSyntax>().Count());
    }

    [Fact]
    public void Full_example_from_design_parses_cleanly()
    {
        var doc = ParseClean(Samples.TodoApp);
        Assert.Single(doc.Items.OfType<CodeBlockSyntax>());
        Assert.Single(doc.Items.OfType<StyleBlockSyntax>());
        Assert.Equal(2, doc.DescendantNodes().OfType<ElementSyntax>().Count(e => e.Name == "Button" || e.Name == "TodoRow"));
    }

    [Fact]
    public void Crlf_line_endings_are_supported()
    {
        var doc = ParseNormalized("<div>\r\n    a\r\n    b\r\n</div>\r\n@code {\r\n}\r\n");
        var div = (ElementSyntax)doc.Items[0];
        Assert.Equal("a b", ((TextSyntax)Assert.Single(div.Children)).Value);
    }
}
