using System.Text;
using AwAUI.Diagnostics.Json;

namespace AwAUI.Diagnostics.UnitTests;

public class JsonWriterTests
{
    static string Write(Action<JsonWriter> action)
    {
        var buf = new JsonBufferWriter(4);
        var w = new JsonWriter(buf);
        action(w);
        Assert.True(w.IsComplete);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    static string ReadString(string json)
    {
        var r = new JsonReader(Encoding.UTF8.GetBytes(json));
        Assert.True(r.Read());
        var s = r.GetString()!;
        Assert.False(r.Read());
        return s;
    }

    [Fact]
    public void Escapes_strings()
    {
        string s = "a\"b\\c/\n\r\t\b\f\u0001\u001f\u007fé中😀";
        string json = Write(w => w.WriteString(s));
        Assert.Equal("\"a\\\"b\\\\c/\\n\\r\\t\\b\\f\\u0001\\u001F\u007fé中😀\"", json);
        Assert.Equal(s, ReadString(json));
    }

    [Fact]
    public void Lone_surrogates_are_escaped_and_round_trip()
    {
        string s = "x\uD800y\uDC00z\uDBFF";
        string json = Write(w => w.WriteString(s));
        Assert.Equal("\"x\\uD800y\\uDC00z\\uDBFF\"", json);
        Assert.Equal(s, ReadString(json));
    }

    [Fact]
    public void Utf8_string_and_name_overloads_escape()
    {
        string json = Write(w =>
        {
            w.WriteStartObject();
            w.WritePropertyName("k\"\n"u8);
            w.WriteStringUtf8("v\\\u0002é"u8);
            w.WriteEndObject();
        });
        Assert.Equal("{\"k\\\"\\n\":\"v\\\\\\u0002é\"}", json);
    }

    [Fact]
    public void Long_strings_round_trip_across_chunks()
    {
        var sb = new StringBuilder();
        var rng = new Random(1234);
        for (int i = 0; i < 5000; i++)
        {
            switch (rng.Next(7))
            {
                case 0: sb.Append((char)rng.Next(0, 0x20)); break;
                case 1: sb.Append((char)rng.Next(0x20, 0x80)); break;
                case 2: sb.Append((char)rng.Next(0x80, 0x800)); break;
                case 3: sb.Append((char)rng.Next(0x800, 0xD800)); break;
                case 4: sb.Append(char.ConvertFromUtf32(rng.Next(0x10000, 0x110000))); break;
                case 5: sb.Append((char)rng.Next(0xD800, 0xE000)); break;   // 孤立代理（可能恰好组成一对，同样合法）
                default: sb.Append('"').Append('\\'); break;
            }
        }
        string s = sb.ToString();
        string json = Write(w => w.WriteString(s));
        Assert.Equal(s, ReadString(json));
    }

    [Fact]
    public void Writes_structures_and_numbers()
    {
        string json = Write(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("a", 1);
            w.WriteNumber("min", long.MinValue);
            w.WriteNumber("d", 0.1);
            w.WriteNumber("big", 1e21);
            w.WriteNumber("neg", -2.5e-7);
            w.WriteBoolean("t", true);
            w.WriteNull("n");
            w.WriteStartArray("arr");
            w.WriteNumber(1);
            w.WriteString("x");
            w.WriteStartObject();
            w.WriteEndObject();
            w.WriteStartArray();
            w.WriteEndArray();
            w.WriteEndArray();
            w.WriteStartObject("o");
            w.WriteString("s", (string?)null);
            w.WriteEndObject();
            w.WriteEndObject();
        });
        var v = JsonValue.Parse(json).AsObject();
        Assert.Equal(1, v["a"]!.GetInt64());
        Assert.Equal(long.MinValue, v["min"]!.GetInt64());
        Assert.Equal(0.1, v["d"]!.GetDouble());
        Assert.Equal(1e21, v["big"]!.GetDouble());
        Assert.Equal(-2.5e-7, v["neg"]!.GetDouble());
        Assert.True(v["t"]!.GetBoolean());
        Assert.True(v["n"]!.IsNull);
        Assert.Equal(4, v["arr"]!.AsArray().Count);
        Assert.True(v["o"]!["s"]!.IsNull);
        Assert.StartsWith("{\"a\":1,\"min\":-9223372036854775808,\"d\":0.1,", json);
        Assert.Contains("\"arr\":[1,\"x\",{},[]]", json);
    }

    [Fact]
    public void Rejects_invalid_structure()
    {
        var w = new JsonWriter(new JsonBufferWriter());
        w.WriteStartObject();
        Assert.Throws<InvalidOperationException>(() => w.WriteNumber(1));       // 对象中缺属性名
        Assert.Throws<InvalidOperationException>(() => w.WriteEndArray());
        w.WritePropertyName("a");
        Assert.Throws<InvalidOperationException>(() => w.WritePropertyName("b")); // 连续两个属性名
        Assert.Throws<InvalidOperationException>(() => w.WriteEndObject());      // 属性名后缺值
        w.WriteNumber(1);
        w.WriteEndObject();
        Assert.Throws<InvalidOperationException>(() => w.WriteNumber(2));       // 第二个顶层值

        var a = new JsonWriter(new JsonBufferWriter());
        a.WriteStartArray();
        Assert.Throws<InvalidOperationException>(() => a.WritePropertyName("x"));
        Assert.Throws<InvalidOperationException>(() => a.WriteEndObject());
    }

    [Fact]
    public void Rejects_non_finite_numbers()
    {
        var w = new JsonWriter(new JsonBufferWriter());
        Assert.Throws<ArgumentException>(() => w.WriteNumber(double.NaN));
        Assert.Throws<ArgumentException>(() => w.WriteNumber(double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => JsonValue.Create(double.NegativeInfinity));
    }

    [Fact]
    public void Reset_allows_next_document()
    {
        var buf = new JsonBufferWriter();
        var w = new JsonWriter(buf);
        w.WriteNumber(1);
        w.Reset();
        w.WriteNumber(2);
        Assert.Equal("12", Encoding.UTF8.GetString(buf.WrittenSpan));
    }

    [Fact]
    public void Deep_nesting_beyond_64()
    {
        string json = Write(w =>
        {
            for (int i = 0; i < 100; i++) { if (i % 2 == 0) w.WriteStartArray(); else { w.WriteStartObject(); w.WritePropertyName("k"); } }
            w.WriteNumber(0);
            for (int i = 99; i >= 0; i--) { if (i % 2 == 0) w.WriteEndArray(); else w.WriteEndObject(); }
        });
        JsonValue.Parse(Encoding.UTF8.GetBytes(json), new JsonReaderOptions { MaxDepth = 100 });
    }
}

public class JsonValueTests
{
    [Fact]
    public void Parse_and_serialize_round_trip()
    {
        const string json = "{\"a\":[1,2.5,-3e-2,\"x\",true,false,null],\"b\":{\"c\":\"\\u00e9\\n\"},\"big\":123456789012345678901234567890}";
        var v = JsonValue.Parse(json);
        Assert.Equal(JsonValueKind.Object, v.Kind);
        Assert.Equal("{\"a\":[1,2.5,-3e-2,\"x\",true,false,null],\"b\":{\"c\":\"é\\n\"},\"big\":123456789012345678901234567890}", v.ToJsonString());
        Assert.True(v.DeepEquals(JsonValue.Parse(v.ToJsonString())));
    }

    [Fact]
    public void Duplicate_keys_last_wins()
    {
        var o = JsonValue.Parse("{\"a\":1,\"b\":2,\"a\":3}").AsObject();
        Assert.Equal(2, o.Count);
        Assert.Equal(3, o["a"]!.GetInt64());
    }

    [Fact]
    public void Object_builds_index_for_many_properties_and_preserves_order()
    {
        var o = new JsonObject();
        for (int i = 0; i < 20; i++) o["k" + i] = i;
        o["k5"] = "five";
        Assert.Equal(20, o.Count);
        Assert.Equal("five", o["k5"]!.GetString());
        Assert.True(o.Remove("k0"));
        Assert.False(o.ContainsKey("k0"));
        Assert.Equal(19, o["k19"]!.GetInt64());
        Assert.Equal("k1", o.First().Key);
    }

    [Fact]
    public void Accessors_and_kinds()
    {
        JsonValue v = new JsonObject { ["s"] = "x", ["n"] = 5, ["d"] = 1.5, ["b"] = true, ["z"] = (string?)null, ["arr"] = new JsonArray { 1, "two" } };
        Assert.Equal("x", v["s"]!.GetString());
        Assert.Equal(5, v["n"]!.GetInt64());
        Assert.Equal(1.5, v["d"]!.GetDouble());
        Assert.True(v["b"]!.GetBoolean());
        Assert.True(v["z"]!.IsNull);
        Assert.Null(v["missing"]);
        Assert.Equal("two", v["arr"]![1].GetString());
        Assert.Throws<InvalidOperationException>(() => v["s"]!.GetInt64());
        Assert.Throws<InvalidOperationException>(() => v["n"]!.GetString());
        Assert.Null(JsonValue.Create("x")["a"]);
    }

    [Theory]
    [InlineData("1.0", true, 1)]
    [InlineData("1e3", true, 1000)]
    [InlineData("1.5", false, 0)]
    [InlineData("1e300", false, 0)]
    [InlineData("9007199254740993.0", true, 9007199254740993)]       // 不经 double：不会被舍入成 2^53
    [InlineData("9007199254740993.5", false, 0)]
    [InlineData("1.5e3", true, 1500)]
    [InlineData("12300e-2", true, 123)]
    [InlineData("1230e-2", false, 0)]
    [InlineData("1E+2", true, 100)]
    [InlineData("-0", true, 0)]
    [InlineData("0.000e99999", true, 0)]
    [InlineData("1e-5", false, 0)]
    [InlineData("100000000000000000000e-2", true, 1000000000000000000)]
    [InlineData("9223372036854775807.0", true, long.MaxValue)]
    [InlineData("9223372036854775808.0", false, 0)]
    [InlineData("-9223372036854775808.0", true, long.MinValue)]
    [InlineData("-9223372036854775809", false, 0)]
    [InlineData("1e19", false, 0)]
    public void Number_int64_accepts_integral_doubles(string raw, bool ok, long expected)
    {
        var n = JsonValue.Parse(raw);
        Assert.Equal(ok, n.TryGetInt64(out long v));
        if (ok) Assert.Equal(expected, v);
    }

    [Fact]
    public void Numbers_compare_by_value()
    {
        Assert.True(JsonValue.Parse("1.0").DeepEquals(JsonValue.Parse("1")));
        Assert.True(JsonValue.Parse("{\"a\":1,\"b\":2}").DeepEquals(JsonValue.Parse("{\"b\":2,\"a\":1}")));
        Assert.False(JsonValue.Parse("[1,2]").DeepEquals(JsonValue.Parse("[2,1]")));
    }

    [Fact]
    public void Parse_rejects_invalid()
    {
        Assert.Throws<JsonException>(() => JsonValue.Parse("{\"a\":}"));
        Assert.Throws<JsonException>(() => JsonValue.Parse("[1] 2"));
    }
}
