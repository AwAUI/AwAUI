using AwAUI.Css.Syntax;

namespace AwAUI.Css.UnitTests;

/// <summary>组件值序列化后重新分词必须得到相同的记号（CSS Syntax 3 §9）。</summary>
public class SerializationTests
{
    static string Roundtrip(string css) => CssComponentValue.ToCss(CssComponentValue.ParseList(css));

    static IEnumerable<string> Describe(IReadOnlyList<CssComponentValue> values)
    {
        foreach (var v in values)
        {
            switch (v)
            {
                case CssToken t:
                    yield return t.Describe() + (t.HasSign ? "±" : "");
                    break;
                case CssFunction f:
                    yield return "fn " + f.Name;
                    foreach (var x in Describe(f.Arguments)) yield return x;
                    yield return "end";
                    break;
                case CssSimpleBlock b:
                    yield return "block " + b.Open;
                    foreach (var x in Describe(b.Values)) yield return x;
                    yield return "end";
                    break;
            }
        }
    }

    static void AssertStable(string css)
    {
        var first = CssComponentValue.ParseList(css);
        var text = CssComponentValue.ToCss(first);
        var second = CssComponentValue.ParseList(text);
        Assert.True(Describe(first).SequenceEqual(Describe(second)),
            $"输入 '{css}' 序列化为 '{text}' 后记号不同：\n{string.Join(" ", Describe(first))}\n{string.Join(" ", Describe(second))}");
    }

    [Theory]
    [InlineData("1/**/px", "1/**/px")]
    [InlineData("a/**/b", "a/**/b")]
    [InlineData("a/**/(b)", "a/**/(b)")]
    [InlineData("a/**/-->", "a/**/-->")]
    [InlineData("#a/**/b", "#a/**/b")]
    [InlineData("@a/**/b", "@a/**/b")]
    [InlineData("1px/**/2", "1px/**/2")]
    [InlineData("1/**/%", "1/**/%")]
    [InlineData("./**/5", "./**/5")]
    [InlineData("+/**/5", "+/**/5")]
    [InlineData("-/**/a", "-/**/a")]
    [InlineData("@/**/a", "@/**/a")]
    [InlineData("//**/*", "//**/*")]
    [InlineData("a b", "a b")]
    [InlineData("a,b", "a,b")]
    [InlineData("f(a/**/b)", "f(a/**/b)")]
    public void Separators_are_inserted_where_needed(string input, string expected)
    {
        Assert.Equal(expected, Roundtrip(input));
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    [InlineData("1e400px")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("-0")]
    [InlineData("-0.0")]
    [InlineData("+1")]
    [InlineData("12345678901234567890")]
    [InlineData("1.5e-7")]
    [InlineData("100%")]
    public void Numbers_roundtrip_with_value_and_type(string input) => AssertStable(input);

    [Fact]
    public void Infinite_integer_roundtrips_as_integer()
    {
        var css = "1" + new string('0', 400);
        var t = Assert.IsType<CssToken>(Assert.Single(CssComponentValue.ParseList(css)));
        Assert.True(double.IsPositiveInfinity(t.Number));
        Assert.Equal(CssNumberType.Integer, t.NumberType);
        AssertStable(css);
    }

    [Fact]
    public void Random_token_sequences_roundtrip()
    {
        var rnd = new Random(7);
        const string alphabet = "abcxe-_019.+#@%/*()[]{},:;<>!= \n";
        for (int n = 0; n < 5000; n++)
        {
            var chars = new char[rnd.Next(1, 24)];
            for (int i = 0; i < chars.Length; i++) chars[i] = alphabet[rnd.Next(alphabet.Length)];
            AssertStable(new string(chars));
        }
    }
}
