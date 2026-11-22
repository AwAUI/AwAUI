using System;
using System.Collections.Generic;

namespace AwAUI.UI;

/// <summary>
/// 运行时 Tailwind 补全的类名收集器：观察元素树，按帧比较整棵树上的类名集合，把新出现与不再出现的类名去重后
/// 在帧末（<see cref="UIDevTools"/> 的帧钩子）交给回调。
/// <para>
/// 只读地遍历元素树，不在协调过程中修改它；回调在帧末、观察者之外触发。只有集合真的变化时才报告，稳定之后的帧
/// 不调用回调；类名消失后再出现会重新报告一次，因此新增回调可以直接接到 <c>TailwindRuntime.AddCandidates</c>、
/// 移除回调可以直接接到 <c>TailwindRuntime.RemoveCandidates</c>（两者自身也会去重）。
/// </para>
/// </summary>
internal sealed class ClassCandidateObserver : ElementTreeObserver
{
    readonly UIRoot _root;
    readonly Action<IReadOnlyList<string>>? _added;
    readonly Action<IReadOnlyList<string>>? _removed;

    // 上一次报告时树上的类名集合；帧末与新的集合做差
    readonly HashSet<string> _live = new(StringComparer.Ordinal);
    bool _dirty = true;

    public ClassCandidateObserver(UIRoot root, Action<IReadOnlyList<string>>? added,
        Action<IReadOnlyList<string>>? removed = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        if (added is null && removed is null) throw new ArgumentNullException(nameof(added));
        _added = added;
        _removed = removed;
    }

    public override void OnChildInserted(Element parent, Element child) => _dirty = true;

    public override void OnChildRemoved(Element parent, Element child) => _dirty = true;

    public override void OnClassesChanged(Element element) => _dirty = true;

    /// <summary>报告自上一帧以来消失与新增的类名（没有变化时什么也不做）。</summary>
    public void Flush()
    {
        if (!_dirty) return;
        _dirty = false;

        var current = new HashSet<string>(StringComparer.Ordinal);
        Collect(_root.RootElement, current);

        List<string>? added = null;
        List<string>? removed = null;
        foreach (var c in current)
            if (!_live.Contains(c)) (added ??= new List<string>()).Add(c);
        foreach (var c in _live)
            if (!current.Contains(c)) (removed ??= new List<string>()).Add(c);

        _live.Clear();
        _live.UnionWith(current);

        if (removed is { Count: > 0 }) _removed?.Invoke(removed);
        if (added is { Count: > 0 }) _added?.Invoke(added);
    }

    static void Collect(Element? element, HashSet<string> into)
    {
        if (element is null) return;
        var text = element.GetAttribute("class");
        if (!string.IsNullOrEmpty(text))
            foreach (var token in text!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                into.Add(token);
        foreach (var child in element.ElementChildren) Collect(child, into);
    }
}
