using System.Text;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.UnitTests;

public class JsonReaderTests
{
    static void ReadAll(byte[] utf8, JsonReaderOptions options = default)
    {
        var r = new JsonReader(utf8, options);
        while (r.Read())
        {
            // 取值也要走一遍，以覆盖解码路径
            switch (r.TokenType)
            {
                case JsonTokenType.String:
                case JsonTokenType.PropertyName:
                    _ = r.GetString();
                    break;
                case JsonTokenType.Number:
                    _ = r.GetDouble();
                    break;
            }
        }
    }

    // JSONTestSuite 风格的 y_ 用例：必须接受
    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData(" \t\r\n[ 1 , 2 ]\n ")]
    [InlineData("\"a\"")]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("-0.0e-0")]
    [InlineData("1.5e10")]
    [InlineData("1E-2")]
    [InlineData("1e+2")]
    [InlineData("-1.0e+5")]
    [InlineData("123456789012345678901234567890")]
    [InlineData("1e400")]
    [InlineData("[1e-400]")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("[true,false,null]")]
    [InlineData("[[[[[]]]]]")]
    [InlineData("{\"a\":{\"b\":[1,2,{\"c\":null}]},\"d\":\"e\"}")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"\":0}")]
    [InlineData("\"\\u0000\"")]
    [InlineData("\"\\uD83D\\uDE00\"")]
    [InlineData("\"\\uDEAD\"")]
    [InlineData("\"\\uD800\\uD800\"")]
    [InlineData("\"\\\"\\\\\\/\\b\\f\\n\\r\\t\"")]
    [InlineData("\"\\u00e9\\u00E9\"")]
    [InlineData("\"é 中文 😀\"")]
    [InlineData("\"\u007f\"")]
    [InlineData("\"\u2028\u2029\"")]
    [InlineData("[\"a\",[],{},-1,0.5]")]
    public void Accepts(string json) => ReadAll(Encoding.UTF8.GetBytes(json));

    // n_ 用例：必须拒绝，并报告精确的出错字节偏移
    [Theory]
    [InlineData("", 0)]
    [InlineData(" ", 1)]
    [InlineData("[", 1)]
    [InlineData("[1,]", 3)]
    [InlineData("{\"a\":1,}", 7)]
    [InlineData("[01]", 2)]
    [InlineData("-01", 2)]
    [InlineData("-", 1)]
    [InlineData("[-]", 2)]
    [InlineData("1.", 2)]
    [InlineData("1.e5", 2)]
    [InlineData(".5", 0)]
    [InlineData("1e", 2)]
    [InlineData("1e+", 3)]
    [InlineData("+1", 0)]
    [InlineData("0x10", 1)]
    [InlineData("1.5.2", 3)]
    [InlineData("NaN", 0)]
    [InlineData("Infinity", 0)]
    [InlineData("-Infinity", 1)]
    [InlineData("[1 2]", 3)]
    [InlineData("{'a':1}", 1)]
    [InlineData("['a']", 1)]
    [InlineData("{\"a\" 1}", 5)]
    [InlineData("{a:1}", 1)]
    [InlineData("{\"a\":}", 5)]
    [InlineData("{\"a\":1]", 6)]
    [InlineData("[1}", 2)]
    [InlineData("[\"a\":1]", 4)]
    [InlineData("[,1]", 1)]
    [InlineData("{,}", 1)]
    [InlineData("[1,,2]", 3)]
    [InlineData("\"abc", 4)]
    [InlineData("\"\\x\"", 2)]
    [InlineData("\"\\u12\"", 5)]
    [InlineData("\"\\u12", 5)]
    [InlineData("\"\\uZZZZ\"", 3)]
    [InlineData("\"\\", 2)]
    [InlineData("\"\t\"", 1)]
    [InlineData("\"a\nb\"", 2)]
    [InlineData("\"\u0000\"", 1)]
    [InlineData("[1]x", 3)]
    [InlineData("[1] [2]", 4)]
    [InlineData("1 2", 2)]
    [InlineData("tru", 3)]
    [InlineData("trux", 3)]
    [InlineData("nul", 3)]
    [InlineData("True", 0)]
    [InlineData("[1,2,3", 6)]
    [InlineData("{\"a\":1", 6)]
    [InlineData("\f[]", 0)]
    [InlineData("/* c */ []", 0)]
    [InlineData("[] // c", 3)]
    [InlineData("\u00a0[]", 0)]
    [InlineData("\ufeff{}", 0)]
    [InlineData("]", 0)]
    [InlineData("}", 0)]
    [InlineData("{]", 1)]
    [InlineData("[1.]", 3)]
    [InlineData("[1e]", 3)]
    public void Rejects(string json, int position)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var ex = Assert.Throws<JsonException>(() => ReadAll(bytes));
        Assert.Equal(position, ex.BytePosition);
    }

    // 字符串中的无效 UTF-8：截断、过长编码、编码的代理、超出范围、孤立续字节
    [Theory]
    [InlineData(new byte[] { 0x22, 0xFF, 0x22 }, 1)]
    [InlineData(new byte[] { 0x22, 0xC0, 0xAF, 0x22 }, 1)]
    [InlineData(new byte[] { 0x22, 0xE0, 0x80, 0xAF, 0x22 }, 1)]
    [InlineData(new byte[] { 0x22, 0xED, 0xA0, 0x80, 0x22 }, 1)]
    [InlineData(new byte[] { 0x22, 0xF4, 0x90, 0x80, 0x80, 0x22 }, 1)]
    [InlineData(new byte[] { 0x22, 0xE2, 0x82, 0x22 }, 1)]
    [InlineData(new byte[] { 0x22, 0x61, 0x80, 0x22 }, 2)]
    [InlineData(new byte[] { 0x22, 0xE2, 0x82 }, 1)]
    [InlineData(new byte[] { 0x22, 0xF8, 0x88, 0x80, 0x80, 0x80, 0x22 }, 1)]
    public void Rejects_invalid_utf8(byte[] bytes, int position)
    {
        var ex = Assert.Throws<JsonException>(() => ReadAll(bytes));
        Assert.Equal(position, ex.BytePosition);
    }

    [Fact]
    public void Accepts_four_byte_utf8_and_max_code_point()
    {
        ReadAll(new byte[] { 0x22, 0xF0, 0x9F, 0x98, 0x80, 0xF4, 0x8F, 0xBF, 0xBF, 0x22 });
    }

    [Fact]
    public void Error_reports_line_and_column()
    {
        var bytes = Encoding.UTF8.GetBytes("{\n  \"a\": tru\n}");
        var ex = Assert.Throws<JsonException>(() => ReadAll(bytes));
        Assert.Equal(12, ex.BytePosition);
        Assert.Equal(2, ex.Line);
        Assert.Equal(11, ex.Column);
        Assert.Contains("line 2", ex.Message);
    }

    [Fact]
    public void Depth_limit_default_64()
    {
        ReadAll(Encoding.UTF8.GetBytes(new string('[', 64) + new string(']', 64)));
        var ex = Assert.Throws<JsonException>(() => ReadAll(Encoding.UTF8.GetBytes(new string('[', 65) + new string(']', 65))));
        Assert.Equal(64, ex.BytePosition);
    }

    [Fact]
    public void Depth_limit_configurable_beyond_64_with_mixed_containers()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 300; i++) sb.Append(i % 2 == 0 ? "{\"k\":" : "[");
        sb.Append('1');
        for (int i = 299; i >= 0; i--) sb.Append(i % 2 == 0 ? "}" : "]");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        ReadAll(bytes, new JsonReaderOptions { MaxDepth = 300 });
        Assert.Throws<JsonException>(() => ReadAll(bytes, new JsonReaderOptions { MaxDepth = 299 }));

        // 64 层以上仍能正确发现括号不匹配
        var bad = sb.ToString();
        bad = bad.Substring(0, bad.Length - 100) + "}" + bad.Substring(bad.Length - 99);  // 该位置原本是数组的 ']'
        var ex = Assert.Throws<JsonException>(() => ReadAll(Encoding.UTF8.GetBytes(bad), new JsonReaderOptions { MaxDepth = 300 }));
        Assert.Equal(bad.Length - 100, ex.BytePosition);
    }

    [Fact]
    public void Tokens_and_values()
    {
        var r = new JsonReader(Encoding.UTF8.GetBytes("{\"name\":\"a\\nb\",\"n\":-12,\"d\":1.5e3,\"t\":true,\"z\":null,\"arr\":[]}"));
        Assert.True(r.Read()); Assert.Equal(JsonTokenType.StartObject, r.TokenType); Assert.Equal(1, r.Depth);
        Assert.True(r.Read()); Assert.Equal(JsonTokenType.PropertyName, r.TokenType); Assert.True(r.ValueTextEquals("name"u8));
        Assert.True(r.Read()); Assert.Equal("a\nb", r.GetString()); Assert.True(r.ValueIsEscaped); Assert.True(r.ValueTextEquals("a\nb"));
        Assert.True(r.Read()); Assert.True(r.ValueTextEquals("n"));
        Assert.True(r.Read()); Assert.Equal(-12, r.GetInt64());
        Assert.True(r.Read()); Assert.True(r.Read()); Assert.Equal(1500.0, r.GetDouble()); Assert.False(r.TryGetInt64(out _));
        Assert.True(r.Read()); Assert.True(r.Read()); Assert.True(r.GetBoolean());
        Assert.True(r.Read()); Assert.True(r.Read()); Assert.Equal(JsonTokenType.Null, r.TokenType); Assert.Null(r.GetString());
        Assert.True(r.Read()); Assert.True(r.Read()); Assert.Equal(JsonTokenType.StartArray, r.TokenType); Assert.Equal(2, r.Depth);
        Assert.True(r.Read()); Assert.Equal(JsonTokenType.EndArray, r.TokenType); Assert.Equal(1, r.Depth);
        Assert.True(r.Read()); Assert.Equal(JsonTokenType.EndObject, r.TokenType); Assert.Equal(0, r.Depth);
        Assert.False(r.Read());
        Assert.False(r.Read());
    }

    [Theory]
    [InlineData("\"\\uD83D\\uDE00\"", "\U0001F600")]
    [InlineData("\"x\\u0041\\u00e9\\u4E2D\"", "xAé中")]
    [InlineData("\"\\\"\\\\\\/\\b\\f\\n\\r\\t\"", "\"\\/\b\f\n\r\t")]
    [InlineData("\"中文😀\"", "中文😀")]
    [InlineData("\"a\\u0000b\"", "a\0b")]
    public void Decodes_strings(string json, string expected)
    {
        var r = new JsonReader(Encoding.UTF8.GetBytes(json));
        Assert.True(r.Read());
        Assert.Equal(expected, r.GetString());
    }

    [Fact]
    public void Lone_surrogate_escapes_are_preserved()
    {
        // 孤立代理不能放进特性参数（元数据里按 UTF-8 存储会被替换），所以单独写
        foreach (var (json, expected) in new[] { ("\"\\uDEAD\"", "\uDEAD"), ("\"a\\uD800\\uD800b\"", "a\uD800\uD800b"), ("\"\\uDC00\\uD800\"", "\uDC00\uD800") })
        {
            var r = new JsonReader(Encoding.UTF8.GetBytes(json));
            Assert.True(r.Read());
            Assert.Equal(expected, r.GetString());
        }
    }

    [Fact]
    public void Decodes_long_escaped_string_using_pooled_buffer()
    {
        var expected = string.Concat(Enumerable.Repeat("ab\n中😀", 500));
        var json = "\"" + expected.Replace("\n", "\\n") + "\"";
        var r = new JsonReader(Encoding.UTF8.GetBytes(json));
        Assert.True(r.Read());
        Assert.Equal(expected, r.GetString());
    }

    [Theory]
    [InlineData("9223372036854775807", true, long.MaxValue)]
    [InlineData("-9223372036854775808", true, long.MinValue)]
    [InlineData("9223372036854775808", false, 0)]
    [InlineData("-9223372036854775809", false, 0)]
    [InlineData("0", true, 0)]
    [InlineData("-0", true, 0)]
    [InlineData("1.0", false, 0)]
    [InlineData("1e2", false, 0)]
    public void Int64_parsing(string json, bool ok, long expected)
    {
        var r = new JsonReader(Encoding.UTF8.GetBytes(json));
        Assert.True(r.Read());
        Assert.Equal(ok, r.TryGetInt64(out long v));
        if (ok) Assert.Equal(expected, v);
    }

    [Fact]
    public void Huge_exponent_yields_infinity()
    {
        var r = new JsonReader("1e400"u8.ToArray());
        Assert.True(r.Read());
        Assert.True(double.IsPositiveInfinity(r.GetDouble()));
    }

    [Fact]
    public void Skip_skips_nested_values()
    {
        var r = new JsonReader(Encoding.UTF8.GetBytes("{\"a\":{\"x\":[1,{\"y\":2}]},\"b\":3}"));
        r.Read();
        r.Read(); // a
        r.Skip();
        Assert.Equal(JsonTokenType.EndObject, r.TokenType);
        Assert.Equal(1, r.Depth);
        r.Read();
        Assert.True(r.ValueTextEquals("b"));
        r.Read();
        Assert.Equal(3, r.GetInt64());
    }

    [Fact]
    public void Reader_over_memory_slice_reports_relative_positions()
    {
        var bytes = Encoding.UTF8.GetBytes("xx[1,]yy");
        var mem = new ReadOnlyMemory<byte>(bytes, 2, 4);
        var ex = Assert.Throws<JsonException>(() =>
        {
            var r = new JsonReader(mem);
            while (r.Read()) { }
        });
        Assert.Equal(3, ex.BytePosition);
    }

    [Fact]
    public void Reading_plain_tokens_does_not_allocate()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"a\":[1,2,3,{\"b\":\"cc\"}],\"d\":true,\"e\":null,\"f\":-1.5e3}");
        static int Walk(byte[] b)
        {
            int n = 0;
            var r = new JsonReader(b);
            while (r.Read()) { n++; if (r.TokenType == JsonTokenType.PropertyName && r.ValueTextEquals("b"u8)) n++; }
            return n;
        }
        Walk(bytes); // 预热
        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = Walk(bytes);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(count > 10);
        Assert.Equal(0, after - before);
    }
}
