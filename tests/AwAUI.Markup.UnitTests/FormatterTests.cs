using AwAUI.Markup.Compiler;
using AwAUI.Markup.Compiler.Formatting;
using AwAUI.Markup.Compiler.Syntax;

namespace AwAUI.Markup.UnitTests;

public class FormatterTests
{
    public static IEnumerable<object[]> Corpus()
    {
        yield return new object[] { Samples.TodoApp };
        yield return new object[] { Samples.Counter };
        yield return new object[] { "<div class=\"a\"><span>x</span><span>y</span></div>" };
        yield return new object[] { "<text>Hello <span class=\"b\">world</span>!</text>" };
        yield return new object[] { "<text>  leading and trailing  </text>" };
        yield return new object[] { "<div>\n  a <span>b</span>\n  c\n</div>" };
        yield return new object[] { "<text>\n  a\n  @x\n  .y\n</text>" };                     // 换行后 .y 不能并入表达式
        yield return new object[] { "<text>\n  word\n  @x\n</text>" };                        // 不能变成邮箱
        yield return new object[] { "<text>@a\n@b</text>" };
        yield return new object[] { "<text>@x\n(1)</text>" };
        yield return new object[] { "<text>me@example.com &amp; &lt;tag&gt; @@at</text>" };
        yield return new object[] { "<div>@if (a) { <span/> } text after</div>" };
        yield return new object[] { "<div> @if (a) { <span/> } </div>" };
        yield return new object[] { "<div>x@* c *@y</div>" };
        yield return new object[] { "<div title='say \"hi\"' data-x=\"@(a ? \"b\" : 'c'.ToString())\" @onclick=@Go disabled />" };
        yield return new object[] { "<Grid<int> Items=\"rows\"><Header><text>H</text></Header></Grid>" };
        yield return new object[] { "<><div/><span/></>" };
        yield return new object[] { "@using A\n@namespace B.C\n@typeparam T\n@attribute [X]\n<div/>" };
        yield return new object[] { """
            @{
                var a = 1;
                if (a > 0)
                {
                    <div>@a</div>
                }
                else { <span/> }
                foreach (var x in xs)
                    <Item @key="x" />
            }
            """ };
        yield return new object[] { """
            @switch (s)
            {
                case 1:
                case 2 when y:
                    <A />
                    break;
                default:
                    @:fallback @s
                    break;
            }
            """ };
        yield return new object[] { """
            <div>
                @foreach (var g in groups)
                {
                    <Group @key="g.Id">
                        @if (g.Items.Count > 0)
                        {
                            @for (int i = 0; i < 3; i++) { <Item @key="i">@g.Items[i].Name (@i)</Item> }
                        }
                        else if (g.Hidden) { }
                        else
                        {
                            <text>empty</text>
                        }
                    </Group>
                }
                @while (it.MoveNext()) { <x-y @key="it.Current"/> }
            </div>
            """ };
        yield return new object[] { """"
            <div/>
            @code {
                    string verbatim = @"line1
                line2 }
                        line3";
                string raw = """
                       raw { content
                    """;
                /* block
                      comment */
                void M()
                {
                    var q = from x in xs
                        where x > 0
                        select x;
                }
            }
            """" };
        yield return new object[] { "<style global>\n  :root { --a: 1; }\n</style>\n<div/>" };
        yield return new object[] { "text at top @x level" };
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Round_trips(string text) => RoundTrip.Check(text);

    [Fact]
    public void Produces_canonical_layout()
    {
        string formatted = RoundTrip.Check("""
            @using   A.B
            <div   class="row"><input @bind="draft"/>
                     <Button @onclick="Add">添加</Button>
            @if (x) { <text>是</text> } else {<text>否</text>}</div>
            @code { int x; }
            """);
        Assert.Equal("""
            @using A.B

            <div class="row">
                <input @bind="draft" />
                <Button @onclick="Add">添加</Button>
                @if (x)
                {
                    <text>是</text>
                }
                else
                {
                    <text>否</text>
                }
            </div>

            @code {
                int x;
            }

            """.Replace("\r", ""), formatted);
    }

    [Fact]
    public void Mixed_content_breaks_only_where_whitespace_allows()
    {
        string formatted = RoundTrip.Check("<text>a <span>b</span> c<div><span/></div>d</text>");
        Assert.Equal("""
            <text>
                a <span>b</span> c
                <div>
                    <span />
                </div>
                d
            </text>

            """.Replace("\r", ""), formatted);
    }

    [Fact]
    public void Implicit_expressions_become_explicit_when_adjacency_would_change_parsing()
    {
        string formatted = RoundTrip.Check("<text>\n  word\n  @x\n  .y\n</text>");
        Assert.Contains("word@(x).y", formatted);
    }

    [Fact]
    public void Multiline_string_contents_are_preserved_exactly()
    {
        const string text = "@{\n        var s = @\"a\n      b }\n  c\";\n   <div/>\n}";
        string formatted = RoundTrip.Check(text);
        Assert.Contains("@\"a\n      b }\n  c\";", formatted);
    }

    [Fact]
    public void Crlf_inside_verbatim_strings_is_preserved()
    {
        const string text = "<div/>\r\n@code {\r\n    string s = @\"x\r\n  y\";\r\n}\r\n";
        string formatted = MarkupFormatter.Format(MarkupSyntaxTree.Parse(text));
        var code = MarkupSyntaxTree.Parse(formatted).Root.Items.OfType<CodeBlockSyntax>().Single().Code;
        Assert.Contains("@\"x\r\n  y\"", code);
    }

    [Fact]
    public void Formatting_never_throws_on_broken_input()
    {
        foreach (var s in new[] { "<div", "@(", "@{ <span> }", "</x>", "<style>", "@switch (x) { case", "<a b=\"c" })
        {
            var tree = MarkupSyntaxTree.Parse(s);
            Assert.Equal(s, MarkupFormatter.Format(tree));
            MarkupFormatter.Format(tree.Root); // 不检查诊断的内部入口也不能抛异常
        }
    }

    [Theory]
    [InlineData("</div>")]                                        // 多余的结束标签不是语法树节点
    [InlineData("<Button @onclick=\"() => Say(\"hi\")\" />")]       // 属性值在第二个 " 处结束
    [InlineData("<div><span></div>")]                             // 错误恢复补上的结束标签
    [InlineData("<div @* c *@ class=\"a\"></div>")]                // 开始标签内的注释
    public void Input_with_syntax_errors_is_returned_unchanged(string text)
    {
        var tree = MarkupSyntaxTree.Parse(text);
        Assert.False(MarkupFormatter.TryFormat(tree, out string formatted));
        Assert.Equal(text, formatted);
        Assert.Equal(text, MarkupFormatter.Format(tree));
    }

    [Fact]
    public void Validator_errors_do_not_block_formatting()
    {
        // 未知元素（AW4002）不影响语法树的完整性
        var tree = MarkupSyntaxTree.Parse("<dvi   />");
        Assert.True(MarkupFormatter.TryFormat(tree, out string formatted));
        Assert.Equal("<dvi />\n", formatted);
    }

    [Fact]
    public void Indentation_is_capped_for_extremely_deep_braces()
    {
        string text = "@code {\n" + string.Concat(Enumerable.Repeat("{\n", 2000)) + string.Concat(Enumerable.Repeat("}\n", 2000)) + "}";
        string formatted = RoundTrip.Check(text);
        Assert.True(formatted.Length < 2000 * 2 * 300, $"output is {formatted.Length} chars");
    }
}
