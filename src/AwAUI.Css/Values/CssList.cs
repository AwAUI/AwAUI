using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace AwAUI.Css.Values;

/// <summary>
/// 不可变的计算值列表（<c>transition-duration</c>、<c>animation-name</c>、<c>container-name</c> 等逗号或空白分隔的列表），按元素值相等。
/// </summary>
public sealed class CssList<T> : IReadOnlyList<T>, IEquatable<CssList<T>>
{
    readonly T[] _items;
    readonly string _separator;

    /// <summary>空列表（<c>none</c> 等）。</summary>
    public static readonly CssList<T> Empty = new(Array.Empty<T>(), ", ");

    internal CssList(T[] items, string separator = ", ")
    {
        _items = items;
        _separator = separator;
    }

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    /// <summary>按 CSS 的「列表重复」规则取第 <paramref name="index"/> 项（下标对长度取模，空列表抛出）。</summary>
    public T GetRepeated(int index) => _items[index % _items.Length];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(CssList<T>? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || other._items.Length != _items.Length) return false;
        var cmp = EqualityComparer<T>.Default;
        for (int i = 0; i < _items.Length; i++)
            if (!cmp.Equals(_items[i], other._items[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is CssList<T> l && Equals(l);

    public override int GetHashCode()
    {
        int h = _items.Length;
        var cmp = EqualityComparer<T>.Default;
        foreach (var x in _items) h = HashCodeCompat.Combine(h, x is null ? 0 : cmp.GetHashCode(x));
        return h;
    }

    /// <summary>以 <paramref name="format"/> 序列化每一项并连接；空列表得到 <paramref name="empty"/>。</summary>
    internal string Join(Func<T, string> format, string empty)
    {
        if (_items.Length == 0) return empty;
        var sb = new StringBuilder();
        for (int i = 0; i < _items.Length; i++)
        {
            if (i > 0) sb.Append(_separator);
            sb.Append(format(_items[i]));
        }
        return sb.ToString();
    }

    public override string ToString() => Join(x => x?.ToString() ?? string.Empty, "none");
}

/// <summary>不依赖上下文、解析时即已算好的指定值（关键字列表、时间、缓动函数、字体特性等）。</summary>
internal sealed class PrecomputedValue<T> : CssValue
{
    public PrecomputedValue(T value) => Value = value;

    public T Value { get; }

    public override string ToString() => Value?.ToString() ?? string.Empty;
}
