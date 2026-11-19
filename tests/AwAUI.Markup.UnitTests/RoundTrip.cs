using AwAUI.Markup.Compiler;
using AwAUI.Markup.Compiler.Formatting;
using AwAUI.Markup.Compiler.Syntax;
using AwAUI.Markup.Compiler.Transforms;

namespace AwAUI.Markup.UnitTests;

static class RoundTrip
{
    public static string Structure(MarkupSyntaxTree tree) =>
        SyntaxPrinter.Print(WhitespaceNormalizer.Normalize(tree.Root), structuralOnly: true);

    /// <summary>
    /// 断言：format(parse(x)) 重新解析后没有语法诊断、结构与原树等价，且格式化幂等。返回格式化结果。
    /// </summary>
    public static string Check(string text)
    {
        var tree = MarkupSyntaxTree.Parse(text);
        Assert.True(tree.Diagnostics.Count == 0, "input has syntax errors:\n" + string.Join("\n", tree.Diagnostics));
        string formatted = MarkupFormatter.Format(tree);
        var again = MarkupSyntaxTree.Parse(formatted);
        Assert.True(again.Diagnostics.Count == 0,
            "formatted output has syntax errors:\n" + string.Join("\n", again.Diagnostics) + "\n--- output ---\n" + formatted);
        string expected = Structure(tree);
        string actual = Structure(again);
        if (expected != actual)
            Assert.Fail("round trip changed the tree\n--- input ---\n" + text + "\n--- output ---\n" + formatted
                        + "\n--- expected ---\n" + expected + "\n--- actual ---\n" + actual);
        string twice = MarkupFormatter.Format(again);
        Assert.True(formatted == twice, "format is not idempotent\n--- once ---\n" + formatted + "\n--- twice ---\n" + twice);
        return formatted;
    }
}
