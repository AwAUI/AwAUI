using System;
using System.Collections;
using System.Collections.Generic;

namespace AwAUI.Controls.Primitives;

/// <summary>集合中的一项：键、是否禁用、用于类型前缀查找的文本。</summary>
public sealed class CollectionItem<TKey>
{
    string? _text;
    string? _normalized;

    internal CollectionItem(TKey key, string? textValue, bool disabled)
    {
        Key = key;
        _text = textValue;
        Disabled = disabled;
    }

    public TKey Key { get; }

    public bool Disabled { get; internal set; }

    /// <summary>类型前缀查找用的文本（Radix 的 <c>textValue</c>）；null 表示不参与查找。</summary>
    public string? TextValue
    {
        get => _text;
        internal set
        {
            if (string.Equals(_text, value, StringComparison.Ordinal)) return;
            _text = value;
            _normalized = null;
        }
    }

    /// <summary>规范化后的文本（缓存）：见 <see cref="TypeAhead.Normalize"/>。</summary>
    internal string NormalizedText => _normalized ??= TypeAhead.Normalize(_text ?? "");

    /// <summary>附加数据（例如项对应的元素），集合不解释它。</summary>
    public object? Tag { get; set; }

    public override string ToString() => Key + (Disabled ? " (disabled)" : "") + (_text is null ? "" : " \"" + _text + "\"");
}

/// <summary>
/// 有序的子项集合（Radix 的 <c>Collection</c>）：子项登记自己的键、禁用状态与文本，父级按顺序遍历（键盘导航、类型前缀查找、选择）。
/// <para>
/// 顺序：默认按首次登记的顺序；子项在同一帧的 Build 中按树序登记，因此通常就是树序。顺序会变时（项被重排）用
/// <see cref="Reorder"/> 给出新顺序，或用 <see cref="Sort"/> 按比较器排序（UI 接入后可按元素的文档顺序比较）。
/// </para>
/// </summary>
public sealed class ItemCollection<TKey> : IReadOnlyList<CollectionItem<TKey>> where TKey : notnull
{
    readonly List<CollectionItem<TKey>> _items = new();
    readonly Dictionary<TKey, int> _index;
    bool _indexDirty;

    public ItemCollection() : this(null) { }

    public ItemCollection(IEqualityComparer<TKey>? comparer) => _index = new Dictionary<TKey, int>(comparer ?? EqualityComparer<TKey>.Default);

    /// <summary>每次结构或项属性变化加一（宿主据此判断是否需要重新计算派生状态）。</summary>
    public int Version { get; private set; }

    public int Count => _items.Count;

    public CollectionItem<TKey> this[int index] => _items[index];

    /// <summary>登记或更新一项；新项排在末尾。返回该项。</summary>
    public CollectionItem<TKey> Register(TKey key, string? textValue = null, bool disabled = false)
    {
        int i = IndexOf(key);
        if (i >= 0)
        {
            var item = _items[i];
            if (item.Disabled != disabled || !string.Equals(item.TextValue, textValue, StringComparison.Ordinal))
            {
                item.Disabled = disabled;
                item.TextValue = textValue;
                Version++;
            }
            return item;
        }
        var created = new CollectionItem<TKey>(key, textValue, disabled);
        _index[key] = _items.Count;
        _items.Add(created);
        Version++;
        return created;
    }

    /// <summary>注销一项；不存在时返回 false。</summary>
    public bool Unregister(TKey key)
    {
        int i = IndexOf(key);
        if (i < 0) return false;
        _items.RemoveAt(i);
        _index.Remove(key);
        _indexDirty |= i < _items.Count;
        Version++;
        return true;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        _index.Clear();
        _indexDirty = false;
        Version++;
    }

    public bool Contains(TKey key) => IndexOf(key) >= 0;

    public int IndexOf(TKey key)
    {
        EnsureIndex();
        return _index.TryGetValue(key, out int i) ? i : -1;
    }

    public bool TryGet(TKey key, out CollectionItem<TKey> item)
    {
        int i = IndexOf(key);
        item = i >= 0 ? _items[i] : null!;
        return i >= 0;
    }

    public bool IsDisabled(int index) => _items[index].Disabled;

    /// <summary>按给定的键序排列：列出的键按此顺序排在前面，未列出的保持原相对顺序排在后面，不存在的键被忽略。</summary>
    public void Reorder(IEnumerable<TKey> order)
    {
        if (order is null) throw new ArgumentNullException(nameof(order));
        var result = new List<CollectionItem<TKey>>(_items.Count);
        var taken = new HashSet<TKey>(_index.Comparer);
        foreach (var k in order)
        {
            int i = IndexOf(k);
            if (i >= 0 && taken.Add(k)) result.Add(_items[i]);
        }
        if (result.Count < _items.Count)
            foreach (var it in _items)
                if (!taken.Contains(it.Key)) result.Add(it);
        Replace(result);
    }

    /// <summary>按比较器稳定排序。</summary>
    public void Sort(Comparison<CollectionItem<TKey>> comparison)
    {
        if (comparison is null) throw new ArgumentNullException(nameof(comparison));
        var keyed = new List<(CollectionItem<TKey> Item, int Order)>(_items.Count);
        for (int i = 0; i < _items.Count; i++) keyed.Add((_items[i], i));
        keyed.Sort((a, b) =>
        {
            int c = comparison(a.Item, b.Item);
            return c != 0 ? c : a.Order.CompareTo(b.Order);
        });
        var result = new List<CollectionItem<TKey>>(keyed.Count);
        foreach (var k in keyed) result.Add(k.Item);
        Replace(result);
    }

    void Replace(List<CollectionItem<TKey>> result)
    {
        bool changed = false;
        for (int i = 0; i < result.Count; i++)
            if (!ReferenceEquals(result[i], _items[i])) { changed = true; break; }
        if (!changed) return;
        _items.Clear();
        _items.AddRange(result);
        _indexDirty = true;
        Version++;
    }

    void EnsureIndex()
    {
        if (!_indexDirty) return;
        _index.Clear();
        for (int i = 0; i < _items.Count; i++) _index[_items[i].Key] = i;
        _indexDirty = false;
    }

    /// <summary>可聚焦（未禁用）的项，按顺序。</summary>
    public IEnumerable<CollectionItem<TKey>> EnabledItems
    {
        get
        {
            foreach (var it in _items)
                if (!it.Disabled) yield return it;
        }
    }

    /// <summary>
    /// 类型前缀查找（见 <see cref="TypeAhead.GetNextMatch(IReadOnlyList{string}, string, int, Func{int, bool})"/>）：
    /// 从 <paramref name="currentKey"/> 之后找文本以 <paramref name="search"/> 开头的第一个未禁用项。
    /// </summary>
    /// <remarks>
    /// <typeparamref name="TKey"/> 为值类型时 <c>TKey?</c> 就是 <typeparamref name="TKey"/> 本身（<c>default</c> 是一个合法的键）：
    /// 没有当前项时请用 <see cref="FindNextMatch(string)"/>。
    /// </remarks>
    public CollectionItem<TKey>? FindNextMatch(string search, TKey? currentKey) =>
        FindNextMatchFrom(search, currentKey is null ? -1 : IndexOf(currentKey));

    /// <summary>没有当前项时的类型前缀查找：从第一项开始。</summary>
    public CollectionItem<TKey>? FindNextMatch(string search) => FindNextMatchFrom(search, -1);

    CollectionItem<TKey>? FindNextMatchFrom(string search, int current)
    {
        int i = TypeAhead.GetNextMatchNormalized(new NormalizedTexts(this), TypeAhead.NormalizeSearch(search), current, k => !_items[k].Disabled && _items[k].TextValue is not null);
        return i >= 0 ? _items[i] : null;
    }

    public IEnumerator<CollectionItem<TKey>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    sealed class NormalizedTexts : IReadOnlyList<string?>
    {
        readonly ItemCollection<TKey> _owner;

        public NormalizedTexts(ItemCollection<TKey> owner) => _owner = owner;

        public string? this[int index] => _owner._items[index].NormalizedText;

        public int Count => _owner._items.Count;

        public IEnumerator<string?> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
