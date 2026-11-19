using AwAUI.Markup.Compiler.Syntax;
using AwAUI.Markup.Compiler.Transforms;
using static AwAUI.Markup.UnitTests.TestUtil;

namespace AwAUI.Markup.UnitTests;

public class WhitespaceTests
{
    static IReadOnlyList<SyntaxNode> ChildrenOf(string text) =>
        ((ElementSyntax)ParseNormalized(text).Items.Single()).Children;

    static string[] TextsOf(string text) =>
        ChildrenOf(text).Select(c => c switch
        {
            TextSyntax t => "T:" + t.Value,
            ExpressionSyntax e => "E:" + e.Code,
            ElementSyntax e => "<" + e.Name + ">",
            _ => c.Kind.ToString(),
        }).ToArray();

    [Theory]
    [InlineData("a", "a")]
    [InlineData("  a  ", "  a  ")]                     // 单行：行内空白保留
    [InlineData("\n  hello\n  world\n", "hello world")]
    [InlineData("hello   \n   world", "hello world")]
    [InlineData("a\n\n\n b", "a b")]
    [InlineData("  a\n", "  a")]                       // 第一行的行首空白保留
    [InlineData("\n  a  ", "a  ")]                     // 最后一行的行尾空白保留
    [InlineData("a\t\tb", "a  b")]                     // 制表符视为空格
    [InlineData("\r\n  a\r\n  b\r\n", "a b")]
    [InlineData(" ", " ")]
    public void Normalizes_text_like_jsx(string raw, string expected) =>
        Assert.Equal(expected, WhitespaceNormalizer.NormalizeText(raw));

    [Theory]
    [InlineData("\n   \n")]
    [InlineData("\n")]
    [InlineData("")]
    [InlineData("  \r\n\t")]
    public void Drops_whitespace_with_newlines(string raw) => Assert.Null(WhitespaceNormalizer.NormalizeText(raw));

    [Fact]
    public void Drops_indentation_between_elements()
    {
        Assert.Equal(new[] { "<text>", "<span>" }, TextsOf("<div>\n    <text>a</text>\n    <span>b</span>\n</div>"));
    }

    [Fact]
    public void Keeps_inline_spaces_around_elements_and_expressions()
    {
        Assert.Equal(new[] { "T:a ", "<span>", "T: c" }, TextsOf("<text>a <span>b</span> c</text>"));
        Assert.Equal(new[] { "T:计数：", "E:count.Value" }, TextsOf("<text>计数：@count.Value</text>"));
        Assert.Equal(new[] { "E:a", "T: + ", "E:b" }, TextsOf("<text>@a + @b</text>"));
    }

    [Fact]
    public void Line_breaks_between_text_and_expression_add_no_space()
    {
        Assert.Equal(new[] { "T:a", "E:x", "T:b" }, TextsOf("<text>\n    a\n    @x\n    b\n</text>"));
    }

    [Fact]
    public void Entities_are_decoded_after_trimming()
    {
        // &#32; 与 &nbsp; 不是源中的空白，不会被去掉
        Assert.Equal(new[] { "T: a " }, TextsOf("<text>\n    &#32;a&nbsp;\n</text>"));
    }

    [Fact]
    public void Line_text_attributes_code_and_style_are_untouched()
    {
        var doc = ParseNormalized("""
            <div title="  a
                b  ">
                @{
                    @:   spaced   @x
                }
            </div>
            <style>
                .a {   }
            </style>
            """);
        var div = (ElementSyntax)doc.Items[0];
        Assert.Equal("  a\n    b  ", div.Attributes[0].Value!.LiteralValue);
        var lt = div.DescendantNodes().OfType<LineTextSyntax>().Single();
        Assert.Equal("   spaced   ", ((TextSyntax)lt.Children[0]).Value);
        Assert.Equal("\n    .a {   }\n", doc.Items.OfType<StyleBlockSyntax>().Single().Content);
    }

    [Fact]
    public void Top_level_whitespace_is_dropped()
    {
        var doc = ParseNormalized("\n\n@using X\n\n<div/>\n\n@code { }\n\n");
        Assert.Equal(new[] { SyntaxKind.Directive, SyntaxKind.Element, SyntaxKind.CodeBlock }, doc.Items.Select(i => i.Kind));
    }

    [Fact]
    public void Normalization_does_not_change_the_original_tree()
    {
        var tree = Compiler.MarkupSyntaxTree.Parse("<div>\n  a\n</div>");
        var before = SyntaxPrinter.Print(tree.Root, includeSpans: true);
        WhitespaceNormalizer.Normalize(tree.Root);
        Assert.Equal(before, SyntaxPrinter.Print(tree.Root, includeSpans: true));
        Assert.Equal("\n  a\n", ((TextSyntax)((ElementSyntax)tree.Root.Items[0]).Children[0]).Text);
    }
}
