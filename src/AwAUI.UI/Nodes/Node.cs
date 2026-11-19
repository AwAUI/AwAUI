using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace AwAUI.UI;

/// <summary>
/// 界面描述对象（design/12）。<see cref="Component.Build"/> 每次返回新的描述树，协调器把它与上一次的结果比较后
/// 把变化写到保留的 <see cref="Element"/> 树上。描述交给框架后不应再修改。
/// </summary>
public abstract class Node
{
    /// <summary>同级之间的身份。带 Key 的节点按「类型 + Key」匹配，否则按「类型 + 同类型中的序号」匹配。</summary>
    public object? Key { get; set; }

    /// <summary>字符串隐式成为文本节点。</summary>
    public static implicit operator Node(string text) => new TextNode(text);

    /// <summary>
    /// 把任意值转换为节点（<c>.awaui</c> 生成代码中的 <c>@expr</c> 使用）：null → null；<see cref="Node"/> 原样；
    /// <see cref="NodeList"/> → 片段；其他值按当前区域格式化为文本。
    /// </summary>
    public static Node? From(object? value)
    {
        switch (value)
        {
            case null: return null;
            case Node n: return n;
            case string s: return new TextNode(s);
            case NodeList list: return new Fragment { Children = list };
            case IFormattable f: return new TextNode(f.ToString(null, CultureInfo.CurrentCulture));
            default: return new TextNode(value.ToString() ?? "");
        }
    }

    /// <summary>协调匹配用的类型身份。默认是运行时类型。</summary>
    internal virtual Type MatchType => GetType();
}

/// <summary>纯文本节点：<see cref="NodeList"/> 中的字符串会成为它，对应元素树中的 <see cref="TextNodeElement"/>。</summary>
public sealed class TextNode : Node
{
    public TextNode(string text) => Text = text ?? "";

    public string Text { get; }

    public override string ToString() => Text;
}

/// <summary>片段：把多个节点作为一个整体返回（允许多个根）。片段本身不产生元素。</summary>
public sealed class Fragment : Node
{
    NodeList? _children;

    public Fragment() { }

    public Fragment(NodeList children) => _children = children;

    /// <summary>子节点。读取时按需创建，因此可以写 <c>Children = { a, b }</c>。</summary>
    public NodeList Children
    {
        get => _children ??= new NodeList();
        set => _children = value;
    }

    internal NodeList? ChildrenOrNull => _children;
}

/// <summary>
/// 子节点列表。支持集合表达式 <c>[a, "文本", null, .. seq]</c> 与集合初始化器 <c>{ a, b }</c>：
/// 字符串隐式成为 <see cref="TextNode"/>，null 被忽略（用于条件渲染），展开的序列逐项加入。
/// </summary>
public sealed class NodeList : IReadOnlyList<Node>
{
    static readonly Node[] s_empty = Array.Empty<Node>();
    Node[] _items = s_empty;
    int _count;

    public NodeList() { }

    public NodeList(IEnumerable<Node?>? nodes) => AddRange(nodes);

    public int Count => _count;

    public Node this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _items[index];
        }
    }

    /// <summary>加入一个节点；null 被忽略。</summary>
    public void Add(Node? node)
    {
        if (node is null) return;
        if (_count == _items.Length)
            Array.Resize(ref _items, _items.Length == 0 ? 4 : _items.Length * 2);
        _items[_count++] = node;
    }

    /// <summary>加入文本；null 被忽略。</summary>
    public void Add(string? text)
    {
        if (text is null) return;
        Add(new TextNode(text));
    }

    /// <summary>逐项加入序列；序列本身或其中的 null 被忽略。</summary>
    public void AddRange(IEnumerable<Node?>? nodes)
    {
        if (nodes is null) return;
        if (ReferenceEquals(nodes, this))
        {
            // 展开自身：只加入调用时已有的项（枚举器读取实时的 Count，否则会无限增长）
            int n0 = _count;
            for (int i = 0; i < n0; i++) Add(_items[i]);
            return;
        }
        foreach (var n in nodes) Add(n);
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<Node> IEnumerable<Node>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>无分配的枚举器。</summary>
    public struct Enumerator : IEnumerator<Node>
    {
        readonly NodeList _list;
        int _index;

        internal Enumerator(NodeList list)
        {
            _list = list;
            _index = -1;
        }

        public Node Current => _list._items[_index];

        object IEnumerator.Current => Current;

        public bool MoveNext() => ++_index < _list._count;

        public void Reset() => _index = -1;

        public void Dispose() { }
    }
}

/// <summary>
/// 引用：协调器在元素挂载时写入 <see cref="Current"/>，卸载时清空。用于聚焦、测量等命令式操作。
/// </summary>
public sealed class Ref<T> where T : class
{
    public T? Current { get; internal set; }
}
