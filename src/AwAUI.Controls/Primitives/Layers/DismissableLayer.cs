using System;
using System.Collections.Generic;

namespace AwAUI.Controls.Primitives;

/// <summary>关闭请求的来源。</summary>
public enum DismissReason : byte
{
    EscapeKey,
    PointerDownOutside,
    FocusOutside,
}

/// <summary>
/// 交给 <c>OnEscapeKeyDown / OnPointerDownOutside / OnFocusOutside / OnInteractOutside</c> 的事件：调用 <see cref="PreventDefault"/> 阻止关闭。
/// </summary>
public sealed class DismissableLayerEvent<TNode>
{
    internal DismissableLayerEvent(DismissReason reason, TNode? target)
    {
        Reason = reason;
        Target = target;
    }

    public DismissReason Reason { get; }

    /// <summary>指针按下 / 获得焦点的目标（Escape 时为 null）。</summary>
    public TNode? Target { get; }

    public bool DefaultPrevented { get; private set; }

    public void PreventDefault() => DefaultPrevented = true;
}

/// <summary>
/// 可关闭层（Radix <c>DismissableLayer</c>）：浮层、菜单、对话框的内容。层的「内部」由 <see cref="Contains"/> 判断，
/// 并且包含它的逻辑子层（<see cref="Parent"/> 指向它的层，例如经 Portal 挂到别处的嵌套弹层，相当于 React 树中的包含关系）。
/// </summary>
public sealed class DismissableLayer<TNode>
{
    public DismissableLayer(Func<TNode, bool> contains) => Contains = contains ?? throw new ArgumentNullException(nameof(contains));

    /// <summary>目标是否在本层（不含子层）之内。</summary>
    public Func<TNode, bool> Contains { get; }

    /// <summary>逻辑父层（嵌套）：子层内的指针 / 焦点对父层而言不算「外部」。</summary>
    public DismissableLayer<TNode>? Parent { get; set; }

    /// <summary>
    /// 禁用外部的指针事件（模态）：在它之下的层与普通内容都不接收指针，也不会因为外部点击而关闭（Radix 的 <c>disableOutsidePointerEvents</c>）。
    /// </summary>
    public bool DisableOutsidePointerEvents { get; set; }

    public Action<DismissableLayerEvent<TNode>>? OnEscapeKeyDown { get; set; }

    public Action<DismissableLayerEvent<TNode>>? OnPointerDownOutside { get; set; }

    public Action<DismissableLayerEvent<TNode>>? OnFocusOutside { get; set; }

    /// <summary>外部指针按下或焦点移出时都调用（在具体回调之后）。</summary>
    public Action<DismissableLayerEvent<TNode>>? OnInteractOutside { get; set; }

    /// <summary>应当关闭（没有被 <c>PreventDefault</c>）。</summary>
    public Action<DismissReason>? OnDismiss { get; set; }

    /// <summary>用于调试的名字。</summary>
    public string? Name { get; set; }

    internal bool ContainsLogically(TNode target, IReadOnlyList<DismissableLayer<TNode>> all)
    {
        if (Contains(target)) return true;
        // 逻辑子层（防御性地限制深度：Parent 链有环时不会死循环）
        foreach (var other in all)
        {
            if (ReferenceEquals(other, this)) continue;
            int depth = 0;
            for (var p = other.Parent; p is not null && depth < all.Count + 1; p = p.Parent, depth++)
            {
                if (ReferenceEquals(p, this))
                {
                    if (other.Contains(target)) return true;
                    break;
                }
            }
        }
        return false;
    }

    public override string ToString() => Name ?? "DismissableLayer";
}

/// <summary>
/// 可关闭层的栈（Radix 的 <c>DismissableLayerContext</c>）：按打开顺序排列，决定
/// <list type="bullet">
/// <item>Escape 只交给最上层；</item>
/// <item>外部指针按下：交给每个「目标在其外部」且指针事件未被更高的模态层禁用的层（于是点击外部会同时关闭多个非模态的嵌套层，与 Radix 相同）；
/// 目标位于某个分支（<see cref="AddBranch"/>，如 Toast 视口）内时不算外部；</item>
/// <item>焦点移出：交给每个目标在其外部的层（不受指针禁用影响），分支同样例外。</item>
/// </list>
/// 分派基于分派开始时的栈快照：回调中关闭（移除）层不影响本次分派中其他层的判断（相当于 React 的批量状态更新）。
/// </summary>
public sealed class DismissableLayerStack<TNode>
{
    readonly List<DismissableLayer<TNode>> _layers = new();
    readonly List<Func<TNode, bool>> _branches = new();

    public IReadOnlyList<DismissableLayer<TNode>> Layers => _layers;

    /// <summary>最上层；栈空时为 null。</summary>
    public DismissableLayer<TNode>? TopLayer => _layers.Count == 0 ? null : _layers[_layers.Count - 1];

    /// <summary>入栈（打开）。同一层重复入栈被忽略。</summary>
    public void Push(DismissableLayer<TNode> layer)
    {
        if (layer is null) throw new ArgumentNullException(nameof(layer));
        if (_layers.Contains(layer)) return;
        _layers.Add(layer);
    }

    /// <summary>出栈（关闭或卸载）；不必是最上层。</summary>
    public bool Remove(DismissableLayer<TNode> layer) => _layers.Remove(layer);

    /// <summary>登记一个分支（Radix <c>DismissableLayerBranch</c>）：其中的指针 / 焦点不算任何层的外部。释放返回值以注销。</summary>
    public IDisposable AddBranch(Func<TNode, bool> contains)
    {
        if (contains is null) throw new ArgumentNullException(nameof(contains));
        _branches.Add(contains);
        return new BranchToken(this, contains);
    }

    /// <summary>是否有层禁用了外部指针事件（宿主据此让普通内容 <c>pointer-events: none</c>）。</summary>
    public bool IsOutsidePointerEventsDisabled
    {
        get
        {
            foreach (var l in _layers)
                if (l.DisableOutsidePointerEvents) return true;
            return false;
        }
    }

    /// <summary>该层是否接收指针：位于最高的「禁用外部指针」层之上（含它自己）。</summary>
    public bool IsPointerEventsEnabled(DismissableLayer<TNode> layer)
    {
        int index = _layers.IndexOf(layer);
        if (index < 0) return false;
        return index >= HighestDisabledIndex(_layers);
    }

    /// <summary>
    /// 分派 Escape：只有最上层处理。返回处理它的层（没有层时为 null）；<paramref name="dismissed"/> 表示该层应关闭
    /// （此时宿主应对按键事件 <c>PreventDefault</c>）。
    /// </summary>
    public DismissableLayer<TNode>? DispatchEscape(out bool dismissed)
    {
        dismissed = false;
        var top = TopLayer;
        if (top is null) return null;
        var e = new DismissableLayerEvent<TNode>(DismissReason.EscapeKey, default);
        top.OnEscapeKeyDown?.Invoke(e);
        if (!e.DefaultPrevented && top.OnDismiss is not null)
        {
            dismissed = true;
            top.OnDismiss(DismissReason.EscapeKey);
        }
        return top;
    }

    /// <summary>分派一次指针按下。返回因此被关闭的层（自上而下）。</summary>
    public IReadOnlyList<DismissableLayer<TNode>> DispatchPointerDown(TNode target) => DispatchOutside(target, DismissReason.PointerDownOutside);

    /// <summary>分派一次焦点变化（新焦点为 <paramref name="target"/>）。返回因此被关闭的层（自上而下）。</summary>
    public IReadOnlyList<DismissableLayer<TNode>> DispatchFocusIn(TNode target) => DispatchOutside(target, DismissReason.FocusOutside);

    IReadOnlyList<DismissableLayer<TNode>> DispatchOutside(TNode target, DismissReason reason)
    {
        var snapshot = _layers.ToArray();
        var dismissed = new List<DismissableLayer<TNode>>();
        if (snapshot.Length == 0) return dismissed;
        foreach (var b in _branches)
            if (b(target)) return dismissed;
        int highestDisabled = HighestDisabledIndex(snapshot);
        // 先判断全部再回调：回调中的移除不影响判断
        var targets = new List<DismissableLayer<TNode>>();
        for (int i = snapshot.Length - 1; i >= 0; i--)
        {
            var layer = snapshot[i];
            if (reason == DismissReason.PointerDownOutside && i < highestDisabled) continue;
            if (layer.ContainsLogically(target, snapshot)) continue;
            targets.Add(layer);
        }
        foreach (var layer in targets)
        {
            var e = new DismissableLayerEvent<TNode>(reason, target);
            if (reason == DismissReason.PointerDownOutside) layer.OnPointerDownOutside?.Invoke(e);
            else layer.OnFocusOutside?.Invoke(e);
            layer.OnInteractOutside?.Invoke(e);
            if (!e.DefaultPrevented && layer.OnDismiss is not null)
            {
                dismissed.Add(layer);
                layer.OnDismiss(reason);
            }
        }
        return dismissed;
    }

    static int HighestDisabledIndex(IReadOnlyList<DismissableLayer<TNode>> layers)
    {
        for (int i = layers.Count - 1; i >= 0; i--)
            if (layers[i].DisableOutsidePointerEvents) return i;
        return -1;
    }

    sealed class BranchToken : IDisposable
    {
        DismissableLayerStack<TNode>? _owner;
        readonly Func<TNode, bool> _branch;

        public BranchToken(DismissableLayerStack<TNode> owner, Func<TNode, bool> branch)
        {
            _owner = owner;
            _branch = branch;
        }

        public void Dispose()
        {
            _owner?._branches.Remove(_branch);
            _owner = null;
        }
    }
}
