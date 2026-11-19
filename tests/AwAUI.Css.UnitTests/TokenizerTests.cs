using AwAUI.Css.Syntax;

namespace AwAUI.Css.UnitTests;

public class TokenizerTests
{
    static List<CssToken> Tok(string s, List<CssDiagnostic>? d = null)
    {
        var list = CssTokenizer.Tokenize(s, d);
        Assert.Equal(CssTokenType.EndOfFile, list.Last().Type);
        list.RemoveAt(list.Count - 1);
        return list;
    }

    static CssToken Single(string s)
    {
        var t = Tok(s);
        Assert.Single(t);
        return t[0];
    }

    static string Types(string s) => string.Join(" ", Tok(s).Select(t => t.Type.ToString()));

    [Fact]
    public void All_token_types()
    {
        Assert.Equal(
            "Ident Whitespace Function Ident RightParen Whitespace AtKeyword Whitespace Hash Whitespace Hash Whitespace String Whitespace String Whitespace Url Whitespace Number Whitespace Number Whitespace Percentage Whitespace Dimension Whitespace CDO Whitespace CDC Whitespace Colon Semicolon Comma LeftBracket RightBracket LeftParen RightParen LeftBrace RightBrace Delim",
            Types("a b(c) @d #e #1 \"s\" 'x' url(foo) 1 1.5 50% 10px <!-- --> :;,[](){}+"));
    }

    [Theory]
    [InlineData("12", 12.0, CssNumberType.Integer, false)]
    [InlineData("+12", 12.0, CssNumberType.Integer, true)]
    [InlineData("-7", -7.0, CssNumberType.Integer, true)]
    [InlineData("1.5", 1.5, CssNumberType.Number, false)]
    [InlineData(".5", 0.5, CssNumberType.Number, false)]
    [InlineData("-.5", -0.5, CssNumberType.Number, true)]
    [InlineData("1e3", 1000.0, CssNumberType.Number, false)]
    [InlineData("1E-2", 0.01, CssNumberType.Number, false)]
    [InlineData("3.14e+2", 314.0, CssNumberType.Number, false)]
    [InlineData("0.1", 0.1, CssNumberType.Number, false)]
    [InlineData("12345678901234567890", 12345678901234567890.0, CssNumberType.Integer, false)]
    [InlineData("1.0", 1.0, CssNumberType.Number, false)]
    public void Numbers_have_exact_value_and_type_flag(string text, double value, CssNumberType type, bool sign)
    {
        var t = Single(text);
        Assert.Equal(CssTokenType.Number, t.Type);
        Assert.Equal(value, t.Number);
        Assert.Equal(type, t.NumberType);
        Assert.Equal(sign, t.HasSign);
    }

    [Fact]
    public void Negative_zero_is_preserved()
    {
        var t = Single("-0");
        Assert.True(BitConverter.DoubleToInt64Bits(t.Number) < 0); // 负零也算负数（double.IsNegative 在 .NET Framework 上不存在）
        Assert.Equal(0.0, t.Number);
    }

    [Fact]
    public void Huge_exponent_becomes_infinity()
    {
        Assert.Equal(double.PositiveInfinity, Single("1e400").Number);
    }

    [Theory]
    [InlineData("10px", 10.0, "px", CssNumberType.Integer)]
    [InlineData("1.0px", 1.0, "px", CssNumberType.Number)]
    [InlineData("1e", 1.0, "e", CssNumberType.Integer)]
    [InlineData("1--a", 1.0, "--a", CssNumberType.Integer)]
    [InlineData("2n-1", 2.0, "n-1", CssNumberType.Integer)]
    [InlineData("3\\70x", 3.0, "px", CssNumberType.Integer)]
    [InlineData("5E3em", 5000.0, "em", CssNumberType.Number)]
    public void Dimensions(string text, double value, string unit, CssNumberType type)
    {
        var t = Single(text);
        Assert.Equal(CssTokenType.Dimension, t.Type);
        Assert.Equal(value, t.Number);
        Assert.Equal(unit, t.Unit);
        Assert.Equal(type, t.NumberType);
    }

    [Fact]
    public void Exponent_without_digits_is_a_unit()
    {
        var t = Tok("1e+");
        Assert.Equal(CssTokenType.Dimension, t[0].Type);
        Assert.Equal("e", t[0].Unit);
        Assert.True(t[1].IsDelim('+'));
    }

    [Fact]
    public void Percentage_keeps_type()
    {
        var t = Single("12.5%");
        Assert.Equal(CssTokenType.Percentage, t.Type);
        Assert.Equal(12.5, t.Number);
        Assert.Equal(CssNumberType.Number, t.NumberType);
    }

    [Fact]
    public void Adjacent_signed_numbers()
    {
        var t = Tok("1-2");
        Assert.Equal(2, t.Count);
        Assert.Equal(1, t[0].Number);
        Assert.Equal(-2, t[1].Number);
    }

    [Theory]
    [InlineData("\\41 b", "Ab")]          // 十六进制转义后的一个空白被吞掉
    [InlineData("\\41  b", "A")]          // 第二个空白是独立记号
    [InlineData("\\31 23", "123")]
    [InlineData("a\\:b", "a:b")]
    [InlineData("\\0", "\uFFFD")]
    [InlineData("\\110000", "\uFFFD")]
    [InlineData("\\D800", "\uFFFD")]
    [InlineData("\\1F600", "\U0001F600")]
    [InlineData("\\000041x", "Ax")]       // 最多 6 位十六进制
    [InlineData("\\0000411", "A1")]
    [InlineData("-\\-", "--")]
    [InlineData("\\-", "-")]
    [InlineData("日本語", "日本語")]
    [InlineData("_a1-", "_a1-")]
    [InlineData("--", "--")]
    [InlineData("a\\\r\n", "a\uFFFD")]   // 不是转义：反斜杠 + 换行 -> 标识符结束后是 delim
    public void Ident_escapes(string text, string expected)
    {
        var t = Tok(text);
        Assert.Equal(CssTokenType.Ident, t[0].Type);
        if (text == "a\\\r\n")
        {
            Assert.Equal("a", t[0].Value);
            Assert.True(t[1].IsDelim('\\'));
            return;
        }
        Assert.Equal(expected, t[0].Value);
    }

    [Fact]
    public void Escape_at_eof_is_replacement_character()
    {
        var d = new List<CssDiagnostic>();
        var t = Tok("a\\", d);
        Assert.Single(t);
        Assert.Equal("a\uFFFD", t[0].Value);
        Assert.Contains(d, x => x.Code == CssDiagnosticCodes.SyntaxError);
    }

    [Fact]
    public void Backslash_newline_outside_string_is_delim_with_diagnostic()
    {
        var d = new List<CssDiagnostic>();
        var t = Tok("\\\nx", d);
        Assert.True(t[0].IsDelim('\\'));
        Assert.Equal(CssTokenType.Whitespace, t[1].Type);
        Assert.Single(d);
        Assert.Equal(CssDiagnosticSeverity.Error, d[0].Severity);
    }

    [Theory]
    [InlineData("\"abc\"", "abc")]
    [InlineData("'a\"b'", "a\"b")]
    [InlineData("\"a\\\"b\"", "a\"b")]
    [InlineData("\"a\\\nb\"", "ab")]       // 行尾续行
    [InlineData("\"a\\\r\nb\"", "ab")]
    [InlineData("\"\\41\"", "A")]
    [InlineData("\"\\\"", "\"")]            // 未闭合：内容是 "
    public void Strings(string text, string expected)
    {
        var t = Tok(text);
        Assert.Equal(CssTokenType.String, t[0].Type);
        Assert.Equal(expected, t[0].Value);
    }

    [Fact]
    public void Unterminated_string_at_eof_warns()
    {
        var d = new List<CssDiagnostic>();
        var t = Single2("'abc", d);
        Assert.Equal(CssTokenType.String, t.Type);
        Assert.Equal("abc", t.Value);
        Assert.Equal(CssDiagnosticSeverity.Warning, Assert.Single(d).Severity);
    }

    static CssToken Single2(string s, List<CssDiagnostic> d)
    {
        var t = Tok(s, d);
        Assert.Single(t);
        return t[0];
    }

    [Fact]
    public void Newline_in_string_gives_bad_string_and_newline_is_reconsumed()
    {
        var d = new List<CssDiagnostic>();
        var t = Tok("'ab\ncd'", d);
        Assert.Equal(CssTokenType.BadString, t[0].Type);
        Assert.Equal(CssTokenType.Whitespace, t[1].Type);
        Assert.Equal(CssTokenType.Ident, t[2].Type);
        Assert.Equal(CssTokenType.String, t[3].Type); // 后面的 ' 开启新的（未闭合）字符串
        Assert.Contains(d, x => x.Severity == CssDiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("url(foo.png)", "foo.png")]
    [InlineData("url(  foo.png  )", "foo.png")]
    [InlineData("URL(x)", "x")]
    [InlineData("u\\72l(x)", "x")]
    [InlineData("url(a\\)b)", "a)b")]
    [InlineData("url()", "")]
    [InlineData("url(data:image/png;base64,AAA=)", "data:image/png;base64,AAA=")]
    public void Url_tokens(string text, string expected)
    {
        var t = Single(text);
        Assert.Equal(CssTokenType.Url, t.Type);
        Assert.Equal(expected, t.Value);
    }

    [Theory]
    [InlineData("url(\"x\")")]
    [InlineData("url( 'x' )")]
    [InlineData("url(  \"x\")")]
    public void Quoted_url_is_a_function(string text)
    {
        var t = Tok(text);
        Assert.Equal(CssTokenType.Function, t[0].Type);
        Assert.Equal("url", t[0].Value);
        Assert.Contains(t, x => x.Type == CssTokenType.String && x.Value == "x");
    }

    [Theory]
    [InlineData("url(a b)")]
    [InlineData("url(a\"b)")]
    [InlineData("url(a(b)")]
    [InlineData("url(a\\\nb)")]
    [InlineData("url(a\u0001)")]
    public void Bad_urls_consume_remnants(string text)
    {
        var d = new List<CssDiagnostic>();
        var t = Tok(text + " z", d);
        Assert.Equal(CssTokenType.BadUrl, t[0].Type);
        Assert.Equal(CssTokenType.Whitespace, t[1].Type);
        Assert.Equal("z", t[2].Value);
        Assert.NotEmpty(d);
    }

    [Fact]
    public void Comments_produce_no_tokens()
    {
        var t = Tok("a/*x*/b/**/ /* y */c");
        Assert.Equal(new[] { CssTokenType.Ident, CssTokenType.Ident, CssTokenType.Whitespace, CssTokenType.Ident }, t.Select(x => x.Type));
    }

    [Fact]
    public void Unterminated_comment_warns()
    {
        var d = new List<CssDiagnostic>();
        var t = Tok("a /* never", d);
        Assert.Equal(2, t.Count);
        Assert.Equal(CssDiagnosticSeverity.Warning, Assert.Single(d).Severity);
    }

    [Theory]
    [InlineData("#abc", "abc", CssHashType.Id)]
    [InlineData("#123", "123", CssHashType.Unrestricted)]
    [InlineData("#-a", "-a", CssHashType.Id)]
    [InlineData("#--x", "--x", CssHashType.Id)]
    [InlineData("#-1", "-1", CssHashType.Unrestricted)]
    [InlineData("#\\31 a", "1a", CssHashType.Id)]
    public void Hash_tokens(string text, string value, CssHashType type)
    {
        var t = Single(text);
        Assert.Equal(CssTokenType.Hash, t.Type);
        Assert.Equal(value, t.Value);
        Assert.Equal(type, t.HashType);
    }

    [Theory]
    [InlineData("#", "Delim")]
    [InlineData("# a", "Delim Whitespace Ident")]
    [InlineData("@", "Delim")]
    [InlineData("@1", "Delim Number")]
    [InlineData("@-x", "AtKeyword")]
    [InlineData("@--", "AtKeyword")]
    [InlineData("-a", "Ident")]
    [InlineData("-5", "Number")]
    [InlineData("- 5", "Delim Whitespace Number")]
    [InlineData("-->", "CDC")]
    [InlineData("<!--", "CDO")]
    [InlineData("<!-", "Delim Delim Delim")]
    [InlineData(".", "Delim")]
    [InlineData(".a", "Delim Ident")]
    [InlineData("+.5", "Number")]
    [InlineData("a(", "Function")]
    [InlineData("a (", "Ident Whitespace LeftParen")]
    [InlineData("~=", "Delim Delim")]
    [InlineData("\t\n ", "Whitespace")]
    public void Edge_tokenization(string text, string types) => Assert.Equal(types, Types(text));

    [Fact]
    public void At_keyword_value_excludes_at()
    {
        Assert.Equal("media", Single("@media").Value);
    }

    [Fact]
    public void Positions_track_offsets_lines_and_columns()
    {
        var t = Tok("a  bb\n  c");
        Assert.Equal(new SourcePosition(3, 1, 4), t[2].Range.Start);
        Assert.Equal(new SourcePosition(5, 1, 6), t[2].Range.End);
        Assert.Equal(new SourcePosition(8, 2, 3), t[4].Range.Start);
    }

    [Fact]
    public void Crlf_is_one_newline_and_offsets_map_to_original_text()
    {
        var t = Tok("a\r\nb\rc\fd");
        var b = t[2];
        Assert.Equal("b", b.Value);
        Assert.Equal(new SourcePosition(3, 2, 1), b.Range.Start);
        Assert.Equal(new SourcePosition(5, 3, 1), t[4].Range.Start);
        Assert.Equal(new SourcePosition(7, 4, 1), t[6].Range.Start);
        // CRLF 空白记号覆盖原始的两个字符
        Assert.Equal(1, t[1].Range.Start.Offset);
        Assert.Equal(3, t[1].Range.End.Offset);
    }

    [Fact]
    public void Bom_is_skipped_but_offsets_refer_to_original_text()
    {
        var t = Tok("\uFEFFab");
        Assert.Equal("ab", t[0].Value);
        Assert.Equal(new SourcePosition(1, 1, 1), t[0].Range.Start);
    }

    [Fact]
    public void Nul_is_replaced()
    {
        var t = Single("a\0b");
        Assert.Equal("a\uFFFDb", t.Value);
    }

    [Fact]
    public void Tokens_serialize_back_to_css()
    {
        Assert.Equal("\\31 23", Single("\\31 23").ToCss());
        Assert.Equal("\"a\\\"b\"", Single("'a\"b'").ToCss());
        Assert.Equal("url(\"a b\")", Single("url(a\\ b)").ToCss());
        Assert.Equal("+5", Single("+5").ToCss());
        Assert.Equal("1.5px", Single("1.5px").ToCss());
        Assert.Equal("1\\65 3", Single("1\\65 3").ToCss()); // 单位 e3 必须转义
        Assert.Equal("#\\31 a", Single("#\\31 a").ToCss());
    }
}
