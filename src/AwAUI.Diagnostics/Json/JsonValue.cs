using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AwAUI.Diagnostics.Json;

/// <summary>JSON 值的种类。</summary>
public enum JsonValueKind : byte
{
    Null,
    Boolean,
    Number,
    String,
    Array,
    Object,
}

/// <summary>
/// 极简 JSON DOM 的基类（用于协议参数与结果）。子类：<see cref="JsonObject"/>、<see cref="JsonArray"/>、
/// <see cref="JsonString"/>、<see cref="JsonNumber"/>、<see cref="JsonBoolean"/>、<see cref="JsonNull"/>。
/// 无反射序列化：需要哪个字段就显式读写哪个字段。
/// </summary>
public abstract class JsonValue
{
    private protected JsonValue() { }

    public abstract JsonValueKind Kind { get; }

    public static JsonValue Null => JsonNull.Instance;
    public static JsonValue True => JsonBoolean.TrueValue;
    public static JsonValue False => JsonBoolean.FalseValue;

    public static JsonValue Create(string? value) => value is null ? JsonNull.Instance : new JsonString(value);
    public static JsonValue Create(bool value) => value ? JsonBoolean.TrueValue : JsonBoolean.FalseValue;
    public static JsonValue Create(long value) => new JsonNumber(value);
    public static JsonValue Create(double value) => new JsonNumber(value);

    public static implicit operator JsonValue(string? value) => Create(value);
    public static implicit operator JsonValue(bool value) => Create(value);
    public static implicit operator JsonValue(int value) => Create(value);
    public static implicit operator JsonValue(long value) => Create(value);
    public static implicit operator JsonValue(double value) => Create(value);

    public bool IsNull => Kind == JsonValueKind.Null;

    /// <summary>字符串值；不是字符串时抛 <see cref="InvalidOperationException"/>。</summary>
    public virtual string GetString() => throw WrongKind("string");
    public virtual bool GetBoolean() => throw WrongKind("boolean");
    public virtual double GetDouble() => throw WrongKind("number");
    /// <summary>整数值。数字带小数或指数时，只要值是整数且在 ±2^53 内也接受。</summary>
    public virtual bool TryGetInt64(out long value) { value = 0; return false; }
    public long GetInt64() => TryGetInt64(out long v) ? v : throw WrongKind("integer");

    /// <summary>对象属性；不是对象或没有该属性时返回 null。</summary>
    public JsonValue? this[string name] => this is JsonObject o ? o.Get(name) : null;
    /// <summary>数组元素；不是数组时抛异常。</summary>
    public virtual JsonValue this[int index] => throw WrongKind("array");

    public JsonObject AsObject() => this as JsonObject ?? throw WrongKind("object");
    public JsonArray AsArray() => this as JsonArray ?? throw WrongKind("array");

    /// <summary>写入一个写入器。</summary>
    public abstract void WriteTo(JsonWriter writer);

    /// <summary>序列化为紧凑的 JSON 文本。</summary>
    public string ToJsonString() => Encoding.UTF8.GetString(ToUtf8Bytes());

    /// <summary>序列化为 UTF-8 字节。</summary>
    public byte[] ToUtf8Bytes()
    {
        var buf = new JsonBufferWriter();
        WriteTo(new JsonWriter(buf));
        return buf.ToArray();
    }

    public override string ToString() => ToJsonString();

    /// <summary>深度比较（对象按键集合比较，与顺序无关；数字按数值比较）。</summary>
    public abstract bool DeepEquals(JsonValue? other);

    private protected InvalidOperationException WrongKind(string expected) =>
        new($"The JSON value is {Kind}, not {expected}.");

    // ---- 解析 ----

    public static JsonValue Parse(string json, JsonReaderOptions options = default) => Parse(Encoding.UTF8.GetBytes(json), options);

    public static JsonValue Parse(ReadOnlySpan<byte> utf8Json, JsonReaderOptions options = default)
    {
        var reader = new JsonReader(utf8Json, options);
        reader.Read();
        var v = ReadFrom(ref reader);
        if (reader.Read()) throw new InvalidOperationException("unreachable");
        return v;
    }

    /// <summary>从读取器的当前记号读出一个完整的值（读取器停在该值的最后一个记号上）。</summary>
    public static JsonValue ReadFrom(ref JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String: return new JsonString(reader.GetString()!);
            case JsonTokenType.Number: return JsonNumber.FromRaw(reader.ValueSpan);
            case JsonTokenType.True: return JsonBoolean.TrueValue;
            case JsonTokenType.False: return JsonBoolean.FalseValue;
            case JsonTokenType.Null: return JsonNull.Instance;
            case JsonTokenType.StartArray:
            {
                var arr = new JsonArray();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    arr.Add(ReadFrom(ref reader));
                return arr;
            }
            case JsonTokenType.StartObject:
            {
                var obj = new JsonObject();
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    string name = reader.GetString()!;
                    reader.Read();
                    obj[name] = ReadFrom(ref reader);
                }
                return obj;
            }
            case JsonTokenType.PropertyName:
                reader.Read();
                return ReadFrom(ref reader);
            default:
                throw new InvalidOperationException($"Cannot read a value at token {reader.TokenType}.");
        }
    }
}

public sealed class JsonNull : JsonValue
{
    internal static readonly JsonNull Instance = new();
    JsonNull() { }
    public override JsonValueKind Kind => JsonValueKind.Null;
    public override void WriteTo(JsonWriter writer) => writer.WriteNull();
    public override bool DeepEquals(JsonValue? other) => other is null || other.Kind == JsonValueKind.Null;
}

public sealed class JsonBoolean : JsonValue
{
    internal static readonly JsonBoolean TrueValue = new(true);
    internal static readonly JsonBoolean FalseValue = new(false);
    readonly bool _value;
    JsonBoolean(bool value) => _value = value;
    public override JsonValueKind Kind => JsonValueKind.Boolean;
    public override bool GetBoolean() => _value;
    public override void WriteTo(JsonWriter writer) => writer.WriteBoolean(_value);
    public override bool DeepEquals(JsonValue? other) => other is JsonBoolean b && b._value == _value;
}

public sealed class JsonString : JsonValue
{
    public JsonString(string value) => Value = value ?? throw new ArgumentNullException(nameof(value));
    public string Value { get; }
    public override JsonValueKind Kind => JsonValueKind.String;
    public override string GetString() => Value;
    public override void WriteTo(JsonWriter writer) => writer.WriteString(Value);
    public override bool DeepEquals(JsonValue? other) => other is JsonString s && s.Value == Value;
}

/// <summary>数字。保留原始文本，读取时按需解析，因此大整数与高精度小数不会在转发中失真。</summary>
public sealed class JsonNumber : JsonValue
{
    readonly string _raw;

    public JsonNumber(long value) => _raw = value.ToString(CultureInfo.InvariantCulture);

    public JsonNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentException("NaN and Infinity cannot be represented in JSON.", nameof(value));
        _raw = value.ToString("R", CultureInfo.InvariantCulture);
    }

    JsonNumber(string raw, bool _) => _raw = raw;

    internal static JsonNumber FromRaw(ReadOnlySpan<byte> utf8)
    {
        var chars = new char[utf8.Length];
        for (int i = 0; i < utf8.Length; i++) chars[i] = (char)utf8[i];
        return new JsonNumber(new string(chars), true);
    }

    /// <summary>JSON 数字文本。</summary>
    public string RawText => _raw;
    public override JsonValueKind Kind => JsonValueKind.Number;

    public override double GetDouble()
    {
        var bytes = new byte[_raw.Length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)_raw[i];
        return JsonReader.ParseDouble(bytes);
    }

    /// <summary>
    /// 数值恰好是 <see cref="long"/> 范围内的整数时返回 true。按十进制文本精确判断（不经过 double），
    /// 所以 <c>1.5e3</c>、<c>2.0</c> 可以读出，而 <c>9007199254740993.0</c> 不会被舍入成别的整数，<c>1.5</c> 与越界值返回 false。
    /// </summary>
    public override bool TryGetInt64(out long value) => TryParseIntegral(_raw, out value);

    /// <summary>精确解析 JSON 数字文本表示的整数（允许小数部分为零与指数）。</summary>
    internal static bool TryParseIntegral(string raw, out long value)
    {
        value = 0;
        int i = 0, n = raw.Length;
        bool negative = i < n && raw[i] == '-';
        if (negative) i++;
        int intStart = i;
        while (i < n && IsDigit(raw[i])) i++;
        int intEnd = i;
        if (intEnd == intStart) return false;
        int fracStart = i, fracEnd = i;
        if (i < n && raw[i] == '.')
        {
            fracStart = ++i;
            while (i < n && IsDigit(raw[i])) i++;
            fracEnd = i;
            if (fracEnd == fracStart) return false;
        }
        long exponent = 0;
        if (i < n && (raw[i] == 'e' || raw[i] == 'E'))
        {
            i++;
            bool expNegative = false;
            if (i < n && (raw[i] == '+' || raw[i] == '-')) expNegative = raw[i++] == '-';
            int expStart = i;
            while (i < n && IsDigit(raw[i]))
            {
                if (exponent < 100000) exponent = exponent * 10 + (raw[i] - '0');    // 饱和：更大的指数结果相同
                i++;
            }
            if (i == expStart) return false;
            if (expNegative) exponent = -exponent;
        }
        if (i != n) return false;

        // 有效数字串 D = 整数部分 + 小数部分；小数点位于 D 的第 point 位之后
        string digits = raw.Substring(intStart, intEnd - intStart) + raw.Substring(fracStart, fracEnd - fracStart);
        int lead = 0;
        while (lead < digits.Length && digits[lead] == '0') lead++;
        if (lead == digits.Length) return true;    // 零
        long point = (intEnd - intStart) + exponent - lead;
        digits = digits.Substring(lead);
        if (point <= 0) return false;              // 0 < |v| < 1
        for (long k = point; k < digits.Length; k++)
            if (digits[(int)k] != '0') return false;    // 有非零小数
        if (point > 19) return false;              // ≥ 10^19，超出 long
        ulong acc = 0;
        for (int k = 0; k < point; k++) acc = acc * 10 + (ulong)(k < digits.Length ? digits[k] - '0' : 0);
        if (negative)
        {
            if (acc > 9223372036854775808UL) return false;
            value = acc == 9223372036854775808UL ? long.MinValue : -(long)acc;
            return true;
        }
        if (acc > long.MaxValue) return false;
        value = (long)acc;
        return true;
    }

    static bool IsDigit(char c) => c >= '0' && c <= '9';

    public override void WriteTo(JsonWriter writer) => writer.WriteNumberRaw(_raw);

    public override bool DeepEquals(JsonValue? other)
    {
        if (other is not JsonNumber n) return false;
        if (n._raw == _raw) return true;
        if (TryGetInt64(out long a) && n.TryGetInt64(out long b)) return a == b;
        return GetDouble().Equals(n.GetDouble());
    }
}

/// <summary>JSON 数组。</summary>
public sealed class JsonArray : JsonValue, IEnumerable<JsonValue>
{
    readonly List<JsonValue> _items = new();

    public JsonArray() { }
    public JsonArray(IEnumerable<JsonValue?> items) { foreach (var i in items) Add(i); }

    public override JsonValueKind Kind => JsonValueKind.Array;
    public int Count => _items.Count;
    public override JsonValue this[int index] => _items[index];

    /// <summary>追加元素（null 记为 JSON null）。</summary>
    public void Add(JsonValue? value) => _items.Add(value ?? JsonNull.Instance);

    public List<JsonValue>.Enumerator GetEnumerator() => _items.GetEnumerator();
    IEnumerator<JsonValue> IEnumerable<JsonValue>.GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public override void WriteTo(JsonWriter writer)
    {
        writer.WriteStartArray();
        foreach (var v in _items) v.WriteTo(writer);
        writer.WriteEndArray();
    }

    public override bool DeepEquals(JsonValue? other)
    {
        if (other is not JsonArray a || a.Count != Count) return false;
        for (int i = 0; i < Count; i++)
            if (!_items[i].DeepEquals(a._items[i])) return false;
        return true;
    }
}

/// <summary>JSON 对象。保持插入顺序；同名键后写覆盖先写（解析时取最后一个）。</summary>
public sealed class JsonObject : JsonValue, IEnumerable<KeyValuePair<string, JsonValue>>
{
    readonly List<KeyValuePair<string, JsonValue>> _items = new();
    Dictionary<string, int>? _index;   // 属性较多时建立索引

    public override JsonValueKind Kind => JsonValueKind.Object;
    public int Count => _items.Count;

    /// <summary>读写属性。读不存在的属性返回 null；写 null 记为 JSON null。</summary>
    public new JsonValue? this[string name]
    {
        get => Get(name);
        set => Set(name, value);
    }

    internal JsonValue? Get(string name)
    {
        int i = IndexOf(name);
        return i >= 0 ? _items[i].Value : null;
    }

    /// <summary>设置属性（存在则替换，保持原位置）。</summary>
    public void Set(string name, JsonValue? value)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        var v = value ?? JsonNull.Instance;
        int i = IndexOf(name);
        if (i >= 0) { _items[i] = new KeyValuePair<string, JsonValue>(name, v); return; }
        _items.Add(new KeyValuePair<string, JsonValue>(name, v));
        if (_index != null) _index[name] = _items.Count - 1;
        else if (_items.Count > 8) BuildIndex();
    }

    /// <summary>集合初始化器用：<c>new JsonObject { ["a"] = 1 }</c> 或 <c>{ { "a", 1 } }</c>。</summary>
    public void Add(string name, JsonValue? value) => Set(name, value);

    public bool ContainsKey(string name) => IndexOf(name) >= 0;

    public bool TryGetValue(string name, out JsonValue value)
    {
        int i = IndexOf(name);
        if (i >= 0) { value = _items[i].Value; return true; }
        value = JsonNull.Instance;
        return false;
    }

    public bool Remove(string name)
    {
        int i = IndexOf(name);
        if (i < 0) return false;
        _items.RemoveAt(i);
        if (_index != null) BuildIndex();
        return true;
    }

    int IndexOf(string name)
    {
        if (_index != null) return _index.TryGetValue(name, out int i) ? i : -1;
        for (int i = 0; i < _items.Count; i++)
            if (string.Equals(_items[i].Key, name, StringComparison.Ordinal)) return i;
        return -1;
    }

    void BuildIndex()
    {
        _index = new Dictionary<string, int>(_items.Count * 2, StringComparer.Ordinal);
        for (int i = 0; i < _items.Count; i++) _index[_items[i].Key] = i;
    }

    // 便捷读取：缺失或类型不符时返回默认值
    public string? GetStringOrDefault(string name, string? fallback = null) =>
        this[name] is JsonString s ? s.Value : fallback;

    public List<KeyValuePair<string, JsonValue>>.Enumerator GetEnumerator() => _items.GetEnumerator();
    IEnumerator<KeyValuePair<string, JsonValue>> IEnumerable<KeyValuePair<string, JsonValue>>.GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public override void WriteTo(JsonWriter writer)
    {
        writer.WriteStartObject();
        foreach (var kv in _items)
        {
            writer.WritePropertyName(kv.Key);
            kv.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    public override bool DeepEquals(JsonValue? other)
    {
        if (other is not JsonObject o || o.Count != Count) return false;
        foreach (var kv in _items)
        {
            var v = o[kv.Key];
            if (v is null || !kv.Value.DeepEquals(v)) return false;
        }
        return true;
    }
}
