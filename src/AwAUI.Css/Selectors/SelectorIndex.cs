using System;
using System.Collections.Generic;
using AwAUI.Css.Syntax;

namespace AwAUI.Css.Selectors;

/// <summary>
/// 规则索引：按主体复合选择器中最有区分度的键（ID &gt; class &gt; 类型 &gt; part 名）分桶，
/// 查询时只取元素可能命中的桶作为候选，再逐个精确匹配（design/08「匹配性能」）。
/// </summary>
/// <typeparam name="T">随选择器存放的数据（通常是规则及其级联信息）。</typeparam>
public sealed class SelectorIndex<T>
{
    /// <summary>索引中的一项。</summary>
    public sealed class Entry
    {
        internal Entry(ComplexSelector selector, T value, int order)
        {
            Selector = selector;
            Value = value;
            Order = order;
        }

        public ComplexSelector Selector { get; }

        public T Value { get; }

        /// <summary>加入索引的顺序（即出现顺序）。</summary>
        public int Order { get; }

        public Specificity Specificity => Selector.Specificity;
    }

    enum BucketKind { Id, Class, Tag, Part, Universal }

    static readonly IComparer<Entry> ByOrder = Comparer<Entry>.Create((a, b) => a.Order.CompareTo(b.Order));

    readonly Dictionary<string, List<Entry>> _ids = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<Entry>> _classes = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<Entry>> _tags = new(AsciiCase.Comparer);
    readonly Dictionary<string, List<Entry>> _parts = new(StringComparer.Ordinal);
    readonly List<Entry> _universal = new();

    /// <summary>索引中的选择器数。</summary>
    public int Count { get; private set; }

    /// <summary>落入通配桶（每个元素都要检查）的选择器数，可用于诊断索引效率。</summary>
    public int UniversalCount => _universal.Count;

    /// <summary>加入一个选择器。</summary>
    public Entry Add(ComplexSelector selector, T value)
    {
        if (selector is null) throw new ArgumentNullException(nameof(selector));
        var entry = new Entry(selector, value, Count++);
        var (kind, key) = KeyOf(selector);
        switch (kind)
        {
            case BucketKind.Id: AddTo(_ids, key!, entry); break;
            case BucketKind.Class: AddTo(_classes, key!, entry); break;
            case BucketKind.Tag: AddTo(_tags, key!, entry); break;
            case BucketKind.Part: AddTo(_parts, key!, entry); break;
            default: _universal.Add(entry); break;
        }
        return entry;
    }

    /// <summary>把列表中的每个选择器分别加入（同一规则的多个选择器各自匹配、各自带特异性）。</summary>
    public void Add(SelectorList list, T value)
    {
        if (list is null) throw new ArgumentNullException(nameof(list));
        foreach (var s in list.Selectors) Add(s, value);
    }

    static void AddTo(Dictionary<string, List<Entry>> map, string key, Entry entry)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<Entry>();
        list.Add(entry);
    }

    static (BucketKind, string?) KeyOf(ComplexSelector selector)
    {
        var subject = selector.Subject;
        if (subject.PseudoElement is { PseudoElement: PseudoElementKind.Part } part && part.PartNames.Count > 0)
            return (BucketKind.Part, part.PartNames[0]);
        return KeyOfCompound(subject, depth: 0);
    }

    static (BucketKind, string?) KeyOfCompound(CompoundSelector compound, int depth)
    {
        string? cls = null, tag = null;
        foreach (var s in compound.Selectors)
        {
            switch (s)
            {
                case IdSelector id: return (BucketKind.Id, id.Id);
                case ClassSelector c: cls ??= c.Name; break;
                case TypeSelector t: tag ??= t.Name; break;
            }
        }
        if (cls is not null) return (BucketKind.Class, cls);
        if (tag is not null) return (BucketKind.Tag, tag);

        // `&:hover` 之类：父规则只有一个选择器时，主体必须匹配父选择器的主体，可借用其键
        if (depth < 8)
        {
            foreach (var s in compound.Selectors)
            {
                if (s is NestingSelector { Parent: { Count: 1 } parent } && parent[0].PseudoElement is null)
                {
                    var key = KeyOfCompound(parent[0].Subject, depth + 1);
                    if (key.Item1 != BucketKind.Universal) return key;
                }
            }
        }
        return (BucketKind.Universal, null);
    }

    /// <summary>收集元素的候选项（未精确匹配），按出现顺序排列。</summary>
    public void CollectCandidates(StyleElement element, List<Entry> result)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        if (result is null) throw new ArgumentNullException(nameof(result));
        int start = result.Count;
        if (element.Id is { } id && _ids.TryGetValue(id, out var byId)) result.AddRange(byId);
        var classes = element.Classes;
        for (int i = 0; i < classes.Count; i++)
        {
            var c = classes[i];
            if (IsDuplicate(classes, i)) continue;
            if (_classes.TryGetValue(c, out var byClass)) result.AddRange(byClass);
        }
        if (_tags.TryGetValue(element.Tag, out var byTag)) result.AddRange(byTag);
        var parts = element.Parts;
        for (int i = 0; i < parts.Count; i++)
        {
            if (IsDuplicate(parts, i)) continue;
            if (_parts.TryGetValue(parts[i], out var byPart)) result.AddRange(byPart);
        }
        result.AddRange(_universal);
        if (result.Count - start > 1) result.Sort(start, result.Count - start, ByOrder);
    }

    /// <summary>收集与元素（或其伪元素）匹配的项，按出现顺序排列。</summary>
    public void CollectMatches(StyleElement element, List<Entry> result, PseudoElementKind pseudoElement = PseudoElementKind.None)
        => CollectMatches(element, result, pseudoElement, null, out _);

    /// <summary>
    /// 收集匹配项，<c>:has()</c> 的结果经 <paramref name="hasCache"/> 缓存；<paramref name="evaluatedHas"/> 报告是否有含 <c>:has()</c> 的候选被求值
    /// （元素的样式因此依赖其后代或兄弟）。
    /// </summary>
    public void CollectMatches(StyleElement element, List<Entry> result, PseudoElementKind pseudoElement, HasMatchCache? hasCache, out bool evaluatedHas)
    {
        int start = result.Count;
        CollectCandidates(element, result);
        int write = start;
        evaluatedHas = false;
        for (int i = start; i < result.Count; i++)
        {
            var e = result[i];
            if (e.Selector.ContainsHas)
            {
                evaluatedHas = true;
                hasCache ??= new HasMatchCache(); // 这次收集内的候选共享一个访问预算
            }
            if (SelectorMatcher.Matches(e.Selector, element, pseudoElement, hasCache)) result[write++] = e;
        }
        result.RemoveRange(write, result.Count - write);
    }

    static bool IsDuplicate(IReadOnlyList<string> list, int index)
    {
        var v = list[index];
        for (int j = 0; j < index; j++)
            if (string.Equals(list[j], v, StringComparison.Ordinal)) return true;
        return false;
    }
}
