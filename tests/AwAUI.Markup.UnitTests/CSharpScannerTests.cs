using AwAUI.Markup.Compiler.Parsing;

namespace AwAUI.Markup.UnitTests;

public class CSharpScannerTests
{
    /// <summary>在文本中找到第一个开括号，断言其配对的结束符位于标记 ‹ 之后的位置（‹ 本身被删除）。</summary>
    static void AssertBalanced(string marked)
    {
        int expected = marked.IndexOf('‹');
        Assert.True(expected >= 0, "test input needs a ‹ marker before the expected closer");
        string text = marked.Remove(expected, 1);
        int open = text.IndexOfAny(new[] { '(', '[', '{' });
        Assert.Equal(expected, CSharpScanner.FindClose(text, open));
    }

    [Theory]
    [InlineData("(a + b‹)")]
    [InlineData("(f(x)[1] + g({ 1, 2 })‹) )")]
    [InlineData("(\")\"‹)")]
    [InlineData("(\"a\\\")\"‹)")]
    [InlineData("(@\"c:\\\"‹) \")")]                        // 逐字字符串里的 \ 不转义
    [InlineData("(@\"a\"\")\"\"\"‹)")]                     // 逐字字符串的 "" 转义
    [InlineData("(')'‹)")]
    [InlineData("('\\''‹)")]
    [InlineData("('\\u0029'‹)")]
    [InlineData("(a /* ) */‹)")]
    [InlineData("(a // )\n‹)")]
    [InlineData("($\"{x})\"‹)")]
    [InlineData("($\"{(a ? \")\" : \"(\")} )\"‹)")]       // 内插洞中的字符串
    [InlineData("($\"{{ ) }}\"‹)")]                         // {{ }} 是字面量
    [InlineData("($\"{x:0.00)}\"‹)")]                       // 格式说明中的 )
    [InlineData("($\"{global::System.Math.PI:F2} )\"‹)")]   // :: 不是格式说明
    [InlineData("($\"{$\"{$\"{\")\"}\"}\"}\"‹)")]          // 嵌套内插
    [InlineData("($@\"{x}\"\")\n\"‹)")]                     // 逐字内插：换行与 "" 转义
    [InlineData("(@$\"{x})\"‹)")]
    [InlineData("(\"\"\" ) \"\" \"\"\"‹)")]                 // 原始字符串
    [InlineData("(\"\"\"\"\n ) \"\"\" \n\"\"\"\"‹)")]       // 4 引号原始字符串，内部可含 3 引号
    [InlineData("($\"\"\"{x} ) {(y)}\"\"\"‹)")]            // 内插原始字符串
    [InlineData("($$\"\"\"{x} ) {{y}} {{{z}}}\"\"\"‹)")]   // $$：单个 { 是内容
    [InlineData("($$\"\"\"{{\")\"}}\"\"\"‹)")]             // $$ 洞里的普通字符串
    [InlineData("(\"\"‹)")]                                  // 空字符串
    [InlineData("(x\n#if DEBUG ) \n#endif\n‹)")]             // 预处理行
    [InlineData("(u8\"a)\"u8‹)")]
    [InlineData("(a[b(c)]‹)")]
    public void Finds_matching_closer(string marked) => AssertBalanced(marked);

    [Theory]
    [InlineData("(a")]
    [InlineData("(\")")]
    [InlineData("(/* )")]
    [InlineData("($\"{)\"")]
    [InlineData("(\"\"\" )")]
    [InlineData("(@\" )")]
    public void Reports_unbalanced(string text) => Assert.Equal(-1, CSharpScanner.FindClose(text, 0));

    [Fact]
    public void Mismatched_closers_are_tolerated()
    {
        // ) 与栈顶的 [ 不配：弹出到 ( 为止
        Assert.Equal(3, CSharpScanner.FindClose("([a)", 0));
        // 栈中不存在的结束符：判为不配对（不越过外层边界）
        Assert.Equal(-1, CSharpScanner.FindClose("(])", 0));
        Assert.Equal(-1, CSharpScanner.FindClose("(a } b)", 0));
    }

    [Theory]
    [InlineData("\"abc\" rest", 5)]
    [InlineData("@\"a\"\"b\" rest", 7)]
    [InlineData("$\"a{b}c\" rest", 8)]
    [InlineData("'x' rest", 3)]
    [InlineData("// c\nrest", 4)]
    [InlineData("/* c */rest", 7)]
    [InlineData("\"\"\"a\"\"\" rest", 7)]
    [InlineData("x", 0)]
    [InlineData("$x", 0)]
    public void Skips_atoms(string text, int end) => Assert.Equal(end, CSharpScanner.SkipAtom(text, 0));

    [Fact]
    public void Unterminated_regular_string_stops_at_newline()
    {
        int end = CSharpScanner.SkipAtom("\"abc\nnext", 0, out bool unterminated);
        Assert.True(unterminated);
        Assert.Equal(4, end);
    }

    [Fact]
    public void Stray_apostrophe_only_consumes_itself()
    {
        int end = CSharpScanner.SkipAtom("'abcdefghijklmnop'", 0, out bool unterminated);
        Assert.True(unterminated);
        Assert.Equal(1, end);
    }

    [Fact]
    public void Deep_interpolation_nesting_does_not_overflow()
    {
        string s = "(" + string.Concat(Enumerable.Repeat("$\"{", 5000)) + ")";
        Assert.Equal(-1, CSharpScanner.FindClose(s, 0));
    }
}
