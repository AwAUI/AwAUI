using System;
using System.Collections;
using System.Collections.Generic;

namespace AwAUI.UI;

/// <summary>
/// 属性展开（<c>.awaui</c> 的 <c>@attributes="..."</c>，design/17）的值：有序的「属性名 → 值」表，属性名按 ASCII 大小写不敏感比较，
/// 同名后写覆盖先写（保持首次出现的位置），写入 null 删除。值可以是 <see cref="string"/>、<see cref="ClassValue"/>、
/// <see cref="StyleValue"/>、<see cref="bool"/>、数值、<see cref="Role"/>，或事件处理器（<c>onclick</c> → <see cref="Action{T}"/> / <see cref="Action"/>）。
/// <para>
/// 用作组件 Prop 时（标 <see cref="CaptureUnmatchedAttributesAttribute"/>，相当于 Blazor 的 <c>CaptureUnmatchedValues</c>），
/// 标签上没有对应 Prop 的属性由编译器收集到这里；按内容比较相等，因此生成的 <c>PropsEqual</c> 在内容不变时跳过重建
/// （委托按 <see cref="Delegate.Equals(object)"/>，即同一方法 + 同一目标）。
/// </para>
/// <para>展开到元素上的合并规则见 <see cref="ElementNode.MergeAttributes(IEnumerable{KeyValuePair{string, object}})"/>。</para>
/// </summary>
public sealed class AttributeDictionary : IReadOnlyList<KeyValuePair<string, object?>>, IEquatable<AttributeDictionary>
{
    const int IndexThreshold = 16;
    readonly List<KeyValuePair<string, object?>> _items = new();
    Dictionary<string, int>? _index;

    public AttributeDictionary() { }

    /// <summary>复制（逐项按 <see cref="Add"/> 写入）。</summary>
    public AttributeDictionary(IEnumerable<KeyValuePair<string, object?>>? attributes) => AddRange(attributes);

    public int Count => _items.Count;

    public KeyValuePair<string, object?> this[int index] => _items[index];

    /// <summary>读写属性；读取不存在的属性返回 null，写入 null 删除该属性。</summary>
    public object? this[string name]
    {
        get
        {
            int i = IndexOf(name);
            return i < 0 ? null : _items[i].Value;
        }
        set
        {
            if (name is null) throw new ArgumentNullException(nameof(name));
            int i = IndexOf(name);
            if (value is null)
            {
                if (i >= 0)
                {
                    _items.RemoveAt(i);
                    _index = null;
                }
            }
            else if (i >= 0)
            {
                _items[i] = new KeyValuePair<string, object?>(_items[i].Key, value);
            }
            else
            {
                _items.Add(new KeyValuePair<string, object?>(name, value));
                _index?.Add(name, _items.Count - 1);
            }
        }
    }

    /// <summary>写入（同名覆盖，null 删除）。生成的代码用它收集未匹配的属性。</summary>
    public void Add(string name, object? value) => this[name] = value;

    /// <summary>
    /// 写入显式属性（生成的代码在展开值之后用它写标签上的属性）：<c>class</c> 接在已有的类名之后，<c>style</c> 接在已有的声明之后
    /// （同名声明以显式的为准），其余同名覆盖；<paramref name="value"/> 为 null 时不做任何事（显式属性「未设置」，保留展开值）。
    /// 与 <see cref="ElementNode.MergeAttributes(IEnumerable{KeyValuePair{string, object}})"/> 的规则一致：显式属性优先，class / style 拼接。
    /// </summary>
    public void Merge(string name, object? value)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (value is null) return;
        int i = IndexOf(name);
        if (i >= 0 && _items[i].Value is { } existing)
        {
            if (string.Equals(name, "class", StringComparison.OrdinalIgnoreCase))
            {
                string a = TextOf(existing), b = TextOf(value);
                value = a.Length == 0 ? b : b.Length == 0 ? a : a + " " + b;
            }
            else if (string.Equals(name, "style", StringComparison.OrdinalIgnoreCase))
            {
                string a = TextOf(existing).Trim().TrimEnd(';').Trim(), b = TextOf(value).Trim();
                value = a.Length == 0 ? b : b.Length == 0 ? a : a + "; " + b;
            }
        }
        this[name] = value;
    }

    static string TextOf(object value) => value switch
    {
        ClassValue c => c.Text,
        StyleValue s => s.Text ?? "",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>逐项写入（同名后写覆盖）。</summary>
    public void AddRange(IEnumerable<KeyValuePair<string, object?>>? attributes)
    {
        if (attributes is null) return;
        if (ReferenceEquals(attributes, this)) return;
        foreach (var kv in attributes) this[kv.Key] = kv.Value;
    }

    /// <summary>逐项写入字符串值（同名后写覆盖）。</summary>
    public void AddRange(IEnumerable<KeyValuePair<string, string>>? attributes)
    {
        if (attributes is null) return;
        foreach (var kv in attributes) this[kv.Key] = kv.Value;
    }

    public bool ContainsKey(string name) => IndexOf(name) >= 0;

    public bool TryGetValue(string name, out object? value)
    {
        int i = IndexOf(name);
        value = i < 0 ? null : _items[i].Value;
        return i >= 0;
    }

    int IndexOf(string name)
    {
        if (name is null) return -1;
        if (_items.Count > IndexThreshold)
        {
            if (_index is null)
            {
                _index = new Dictionary<string, int>(_items.Count, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _items.Count; i++) _index[_items[i].Key] = i;
            }
            return _index.TryGetValue(name, out int idx) ? idx : -1;
        }
        for (int i = 0; i < _items.Count; i++)
            if (string.Equals(_items[i].Key, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>内容相等（与顺序无关）：字符串按序数比较，其余按 <see cref="object.Equals(object, object)"/>（委托即同一方法 + 同一目标）。</summary>
    public bool Equals(AttributeDictionary? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (_items.Count != other._items.Count) return false;
        foreach (var kv in _items)
        {
            if (!other.TryGetValue(kv.Key, out var v) || !ValueEquals(kv.Value, v)) return false;
        }
        return true;
    }

    static bool ValueEquals(object? a, object? b) =>
        a is string sa && b is string sb ? string.Equals(sa, sb, StringComparison.Ordinal) : Equals(a, b);

    public override bool Equals(object? obj) => Equals(obj as AttributeDictionary);

    /// <summary>与内容相等一致的散列（只取数量：值可能是委托，其散列不稳定）。</summary>
    public override int GetHashCode() => _items.Count;

    public List<KeyValuePair<string, object?>>.Enumerator GetEnumerator() => _items.GetEnumerator();

    IEnumerator<KeyValuePair<string, object?>> IEnumerable<KeyValuePair<string, object?>>.GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}

/// <summary>
/// 标记组件上类型为 <see cref="AttributeDictionary"/> 的 Prop：<c>.awaui</c> 中写在该组件标签上、但组件没有对应 Prop 的属性
/// （<c>data-*</c>、<c>aria-*</c>、<c>id</c>、<c>@onclick</c> ...）与 <c>@attributes="..."</c> 的展开值被收集到这个 Prop
/// （相当于 Blazor 的 <c>[Parameter(CaptureUnmatchedValues = true)]</c>）。组件再用 <c>@attributes="Rest"</c> 把它们展开到根部件上。
/// 每个组件最多一个（多个时取声明在最派生类型中、按名称排序的第一个）。
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class CaptureUnmatchedAttributesAttribute : Attribute
{
}
