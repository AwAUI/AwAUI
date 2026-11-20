using System;
using System.Buffers;
using System.Collections.Generic;

namespace AwAUI.UI;

/// <summary>
/// 协调器（design/12「协调器」）：把新的描述与上一次的实例记录比较，产生元素树上的最少变更。
/// <list type="number">
/// <item>按「类型 + Key」匹配；无 Key 的按「类型 + 同类型无 Key 节点中的序号」匹配（因此条件插入其他类型的节点不会打乱匹配）。</item>
/// <item>未匹配的旧节点先卸载（按旧顺序），然后按新顺序更新匹配项、挂载新项。</item>
/// <item>元素：类型自己的 Diff 只写变化的字段；组件：PropsEqual 为真且无脏依赖时整棵子树跳过，否则 AdoptProps 后重建。</item>
/// <item>宿主同步：每个元素的子元素按新顺序排列，保持在最长递增子序列中的不动，其余移动/插入，因此移动次数最少。</item>
/// </list>
/// </summary>
internal sealed class Reconciler
{
    const int LinearMatchLimit = 8;

    readonly UIRoot _root;
    long _stamp;
    readonly Stack<List<Element>> _elementLists = new();
    readonly Stack<List<Fiber>> _fiberLists = new();

    public Reconciler(UIRoot root) => _root = root;

    // ------------------------------------------------------------ 子列表

    /// <summary>协调 <paramref name="parent"/> 的子实例。根、元素、片段传 <paramref name="list"/>；组件传单个输出 <paramref name="single"/>。</summary>
    public void ReconcileChildren(Fiber parent, NodeList? list, Node? single, Component? owner)
    {
        int count = list?.Count ?? (single is null ? 0 : 1);
        var old = parent.Children;
        int oldCount = old?.Count ?? 0;
        if (count == 0 && oldCount == 0) return;

        var matches = ArrayPool<Fiber?>.Shared.Rent(Math.Max(count, 1));
        try
        {
            long stamp = ++_stamp;
            if (oldCount > 0 && count > 0)
            {
                if (oldCount <= LinearMatchLimit && count <= LinearMatchLimit) MatchLinear(old!, list, single, count, matches, stamp, owner);
                else MatchHashed(old!, list, single, count, matches, stamp, owner);
            }
            else
            {
                Array.Clear(matches, 0, count);
                // 首次挂载时同样报告重复 Key（之后的更新中后出现的那个会被重新挂载）
                if (oldCount == 0 && list is not null && count > 1) ReportDuplicateKeysOnMount(list, count, owner);
            }

            // 未匹配的旧实例先卸载
            if (oldCount > 0)
            {
                for (int i = 0; i < oldCount; i++)
                {
                    var f = old![i];
                    if (f.Stamp != stamp) Unmount(f);
                }
            }

            var next = RentFiberList();
            for (int i = 0; i < count; i++)
            {
                var node = list is not null ? list[i] : single!;
                var m = matches[i];
                if (m is not null)
                {
                    Update(m, node, owner);
                    next.Add(m);
                }
                else
                {
                    var f = Mount(node, parent, owner);
                    if (f is not null) next.Add(f);
                }
            }

            if (old is null)
            {
                parent.Children = new List<Fiber>(next);
            }
            else
            {
                old.Clear();
                old.AddRange(next);
            }
            ReturnFiberList(next);
        }
        finally
        {
            Array.Clear(matches, 0, Math.Max(count, 1));
            ArrayPool<Fiber?>.Shared.Return(matches);
        }

        if (parent.IsHost) SyncHost(parent);
    }

    void MatchLinear(List<Fiber> old, NodeList? list, Node? single, int count, Fiber?[] matches, long stamp, Component? owner)
    {
        for (int i = 0; i < count; i++)
        {
            var node = list is not null ? list[i] : single!;
            var type = node.MatchType;
            var key = node.Key;
            Fiber? found = null;
            if (key is not null)
            {
                foreach (var f in old)
                {
                    if (ReferenceEquals(f.Type, type) && f.Key is not null && f.Key.Equals(key))
                    {
                        if (f.Stamp == stamp) ReportDuplicateKey(type, key, owner);
                        else found = f;
                        break;
                    }
                }
            }
            else
            {
                int ordinal = 0;
                for (int j = 0; j < i; j++)
                {
                    var prev = list![j];
                    if (prev.Key is null && ReferenceEquals(prev.MatchType, type)) ordinal++;
                }
                foreach (var f in old)
                {
                    if (f.Key is null && ReferenceEquals(f.Type, type))
                    {
                        if (ordinal == 0)
                        {
                            found = f;
                            break;
                        }
                        ordinal--;
                    }
                }
            }
            matches[i] = Accept(found, node, stamp);
        }
    }

    void MatchHashed(List<Fiber> old, NodeList? list, Node? single, int count, Fiber?[] matches, long stamp, Component? owner)
    {
        var keyed = new Dictionary<MatchKey, Fiber>();
        var ordinals = new Dictionary<Type, int>();
        foreach (var f in old)
        {
            MatchKey k;
            if (f.Key is not null)
            {
                k = new MatchKey(f.Type!, f.Key, -1);
            }
            else
            {
                ordinals.TryGetValue(f.Type!, out int o);
                ordinals[f.Type!] = o + 1;
                k = new MatchKey(f.Type!, null, o);
            }
            if (!keyed.ContainsKey(k)) keyed.Add(k, f);
        }
        ordinals.Clear();
        for (int i = 0; i < count; i++)
        {
            var node = list is not null ? list[i] : single!;
            var type = node.MatchType;
            MatchKey k;
            if (node.Key is not null)
            {
                k = new MatchKey(type, node.Key, -1);
            }
            else
            {
                ordinals.TryGetValue(type, out int o);
                ordinals[type] = o + 1;
                k = new MatchKey(type, null, o);
            }
            Fiber? found = null;
            if (keyed.TryGetValue(k, out var f))
            {
                if (f.Stamp == stamp) ReportDuplicateKey(type, node.Key!, owner);
                else found = f;
            }
            matches[i] = Accept(found, node, stamp);
        }
    }

    /// <summary>首次挂载的列表中查找重复的「类型 + Key」。O(n)。</summary>
    void ReportDuplicateKeysOnMount(NodeList list, int count, Component? owner)
    {
        if (count <= LinearMatchLimit)
        {
            for (int i = 1; i < count; i++)
            {
                var n = list[i];
                if (n.Key is null) continue;
                var type = n.MatchType;
                for (int j = 0; j < i; j++)
                {
                    var p = list[j];
                    if (p.Key is not null && ReferenceEquals(p.MatchType, type) && p.Key.Equals(n.Key))
                    {
                        ReportDuplicateKey(type, n.Key, owner);
                        break;
                    }
                }
            }
            return;
        }
        HashSet<MatchKey>? seen = null;
        for (int i = 0; i < count; i++)
        {
            var n = list[i];
            if (n.Key is null) continue;
            var type = n.MatchType;
            if (!(seen ??= new HashSet<MatchKey>()).Add(new MatchKey(type, n.Key, -1))) ReportDuplicateKey(type, n.Key, owner);
        }
    }

    /// <summary>确认匹配：组件的只读 Props 变化时不能沿用实例（返回 null 使其重新挂载）。</summary>
    static Fiber? Accept(Fiber? found, Node node, long stamp)
    {
        if (found is null) return null;
        if (found.Kind == FiberKind.Component)
        {
            var live = found.Component!;
            var carrier = (Component)node;
            if (!ReferenceEquals(live, carrier) && !live.InvokeCanAdoptProps(carrier)) return null;
        }
        found.Stamp = stamp;
        return found;
    }

    void ReportDuplicateKey(Type type, object key, Component? owner) =>
        _root.ReportDiagnostic(new UIDiagnostic(
            "AW1008", DiagnosticSeverity.Warning,
            "同级节点中出现重复的 Key「" + key + "」（类型 " + type.Name + "），后出现的节点会被重新挂载。",
            owner));

    readonly struct MatchKey : IEquatable<MatchKey>
    {
        readonly Type _type;
        readonly object? _key;
        readonly int _ordinal;

        public MatchKey(Type type, object? key, int ordinal)
        {
            _type = type;
            _key = key;
            _ordinal = ordinal;
        }

        public bool Equals(MatchKey other) =>
            ReferenceEquals(_type, other._type) && _ordinal == other._ordinal && Equals(_key, other._key);

        public override bool Equals(object? obj) => obj is MatchKey m && Equals(m);

        public override int GetHashCode() => HashCodeCompat.Combine(_type.GetHashCode(), _key?.GetHashCode() ?? 0, _ordinal);
    }

    // ------------------------------------------------------------ 更新与挂载

    void Update(Fiber f, Node node, Component? owner)
    {
        if (f.Kind != FiberKind.Text && !StackGuard.HasSufficientStack())
        {
            _root.ReportError(StackGuard.CreateException(), owner, ErrorPhase.Build);
            return;
        }
        switch (f.Kind)
        {
            case FiberKind.Element:
            {
                var en = (ElementNode)node;
                if (ReferenceEquals(en, f.ElementNode)) return; // 同一描述对象：整棵子树不变（其中的脏组件会单独重建）
                en.Diff(f.ElementNode, f.Element!);
                f.ElementNode = en;
                ReconcileChildren(f, en.ChildrenOrNull, null, owner);
                return;
            }
            case FiberKind.Text:
            {
                var tn = (TextNode)node;
                if (ReferenceEquals(tn, f.TextNode)) return;
                ((TextNodeElement)f.Element!).SetText(tn.Text);
                f.TextNode = tn;
                return;
            }
            case FiberKind.Fragment:
            {
                var fr = (Fragment)node;
                if (ReferenceEquals(fr, f.FragmentNode)) return;
                f.FragmentNode = fr;
                ReconcileChildren(f, fr.ChildrenOrNull, null, owner);
                return;
            }
            case FiberKind.Portal:
            {
                var pn = (Portal)node;
                if (ReferenceEquals(pn, f.PortalNode)) return;
                f.PortalNode = pn;
                ReconcileChildren(f, pn.ChildrenOrNull, null, owner);
                return;
            }
            case FiberKind.Component:
                UpdateComponent(f.Component!, (Component)node);
                return;
        }
    }

    void UpdateComponent(Component live, Component carrier)
    {
        bool rebuild = false;
        if (!ReferenceEquals(live, carrier) && !live.InvokePropsEqual(carrier))
        {
            live.InvokeAdoptProps(carrier);
            rebuild = true;
        }
        if (!rebuild)
        {
            var obs = live._observer!;
            rebuild = live._forceRebuild || (obs._status != CellStatus.Clean && ReactiveRuntime.ShouldRun(obs));
        }
        if (!rebuild) return;
        if (live._builtFrame == _root.FrameNumber && !live._extraBuildAllowed)
        {
            // 每帧最多重建一次：推迟到下一帧
            live._forceRebuild = true;
            _root.ScheduleRebuild(live);
            return;
        }
        Render(live, firstMount: false);
    }

    Fiber? Mount(Node node, Fiber parent, Component? owner)
    {
        if (!StackGuard.HasSufficientStack())
        {
            _root.ReportError(StackGuard.CreateException(), owner, ErrorPhase.Build);
            return null;
        }
        switch (node)
        {
            case TextNode tn:
            {
                var el = new TextNodeElement(tn.Text) { _owner = owner };
                return new Fiber { Kind = FiberKind.Text, Type = typeof(TextNode), Key = tn.Key, Parent = parent, Element = el, TextNode = tn };
            }
            case ElementNode en:
            {
                var el = en.CreateElement();
                el._owner = owner;
                var f = new Fiber { Kind = FiberKind.Element, Type = en.MatchType, Key = en.Key, Parent = parent, Element = el, ElementNode = en };
                en.Diff(null, el);
                ReconcileChildren(f, en.ChildrenOrNull, null, owner);
                return f;
            }
            case Fragment fr:
            {
                var f = new Fiber { Kind = FiberKind.Fragment, Type = typeof(Fragment), Key = fr.Key, Parent = parent, FragmentNode = fr };
                ReconcileChildren(f, fr.ChildrenOrNull, null, owner);
                return f;
            }
            case Portal pn:
            {
                // 先建立覆盖层中的一层，再协调子节点：嵌套的 Portal 因此叠在外层之上（打开顺序）
                var layer = _root.Overlay.CreateLayer(pn.ZOrder, owner);
                var f = new Fiber { Kind = FiberKind.Portal, Type = typeof(Portal), Key = pn.Key, Parent = parent, Element = layer, PortalNode = pn };
                ReconcileChildren(f, pn.ChildrenOrNull, null, owner);
                return f;
            }
            case Component c:
            {
                if (c._mounted || c._fiber is not null)
                {
                    _root.ReportError(new InvalidOperationException(
                        "组件实例 " + c.GetType().Name + " 已经挂载在别处：Build() 每次都应创建新的组件对象，不能复用。"), owner, ErrorPhase.Build);
                    return null;
                }
                var f = new Fiber { Kind = FiberKind.Component, Type = c.MatchType, Key = c.Key, Parent = parent, Component = c };
                c._fiber = f;
                c._root = _root;
                c._parent = owner;
                c._depth = owner is null ? 0 : owner._depth + 1;
                c._observer = new ComponentObserver(c);
                c._mounted = true;
                _root.RegisterComponentStyles(c);
                Render(c, firstMount: true);
                return f;
            }
            default:
                _root.ReportError(new NotSupportedException("不支持的节点类型：" + node.GetType().FullName), owner, ErrorPhase.Build);
                return null;
        }
    }

    /// <summary>执行组件的 Build（收集依赖）并协调其输出。Build 抛出异常时保留上一次的输出并交给错误边界。</summary>
    public void Render(Component c, bool firstMount)
    {
        c._builtFrame = _root.FrameNumber;
        c._forceRebuild = false;
        c._extraBuildAllowed = false;
        var obs = c._observer!;
        obs._status = CellStatus.Clean;
        var rt = ReactiveRuntime.Current;
        var frame = rt.PushFrame(obs);
        Node? output = null;
        Exception? error = null;
        bool stale;
        try
        {
            try
            {
                output = c.InvokeBuild();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            stale = rt.FinishTracking(obs, frame);
        }
        finally
        {
            rt.PopFrame();
            c.EndBuild(error is null);
        }
        if (stale && obs._status == CellStatus.Clean)
        {
            // Build 读取之后又被写入（通常是 Build 中写 State）：下一帧重建
            obs._status = CellStatus.Dirty;
            _root.ScheduleRebuild(c);
        }
        if (error is not null)
        {
            _root.ReportError(error, c, ErrorPhase.Build);
            return;
        }
        ReconcileChildren(c._fiber!, null, output, c);
        _root.EnqueueCommit(c);
    }

    /// <summary>独立重建一个脏组件；若其顶层元素序列变化，同步其宿主。</summary>
    public void RebuildStandalone(Component c)
    {
        var before = RentElementList();
        var after = RentElementList();
        try
        {
            CollectHosts(c._fiber!, before);
            Render(c, firstMount: false);
            if (c._fiber is null) return;
            CollectHosts(c._fiber, after);
            if (!SameSequence(before, after)) SyncHost(HostOf(c._fiber));
        }
        finally
        {
            ReturnElementList(before);
            ReturnElementList(after);
        }
    }

    // ------------------------------------------------------------ 卸载

    /// <summary>
    /// 卸载一棵实例子树：先对整棵子树按后序执行组件回调（副作用清理 → OnUnmount），再清空 Ref、标记组件已卸载。
    /// 元素本身的摘离由宿主同步完成。
    /// </summary>
    public void Unmount(Fiber f)
    {
        RunUnmountCallbacks(f);
        Detach(f);
    }

    readonly Stack<(Fiber Fiber, int Next)> _unmountStack = new();
    readonly Stack<Fiber> _detachStack = new();

    /// <summary>后序遍历（子先于父，同级按顺序）执行组件回调；显式栈，不受树深限制。</summary>
    void RunUnmountCallbacks(Fiber top)
    {
        // 回调中的用户代码理论上不会重入卸载；万一重入则使用独立的栈
        var stack = _unmountStack.Count == 0 ? _unmountStack : new Stack<(Fiber, int)>();
        BeginUnmount(top);
        stack.Push((top, 0));
        while (stack.Count > 0)
        {
            var (f, next) = stack.Pop();
            var children = f.Children;
            if (children is not null && next < children.Count)
            {
                stack.Push((f, next + 1));
                var child = children[next];
                BeginUnmount(child);
                stack.Push((child, 0));
                continue;
            }
            FinishUnmount(f);
        }
    }

    static void BeginUnmount(Fiber f)
    {
        if (f.Kind == FiberKind.Component) f.Component!._unmounting = true;
    }

    void FinishUnmount(Fiber f)
    {
        if (f.Kind != FiberKind.Component) return;
        var c = f.Component!;
        c.RunAllCleanups(_root);
        if (c._mountCalled)
        {
            try
            {
                c.InvokeOnUnmount();
            }
            catch (Exception ex)
            {
                _root.ReportError(ex, c, ErrorPhase.Unmount);
            }
        }
        if (c._observer is not null) ReactiveRuntime.UnlinkAll(c._observer);
    }

    /// <summary>
    /// 摘离实例子树：清空 Ref，把组件恢复为「未挂载」。同一个组件对象之后可以再次挂载（例如被缓存的实例、
    /// <see cref="ErrorBoundary.Reset"/> 后重新渲染的 Children），届时它会重新收到 OnMount，与 OnUnmount 成对。
    /// </summary>
    void Detach(Fiber top)
    {
        var stack = _detachStack.Count == 0 ? _detachStack : new Stack<Fiber>();
        stack.Push(top);
        while (stack.Count > 0)
        {
            var f = stack.Pop();
            var children = f.Children;
            if (children is not null)
                for (int i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            switch (f.Kind)
            {
                case FiberKind.Element:
                {
                    var r = f.ElementNode?.Ref;
                    if (r is not null && ReferenceEquals(r.Current, f.Element)) r.Current = null;
                    break;
                }
                case FiberKind.Portal:
                    // 覆盖层中的层不属于任何父宿主的子节点：在这里摘离（整个子树的卸载回调已执行完）
                    _root.Overlay.RemoveLayer(f.Element!);
                    break;
                case FiberKind.Component:
                {
                    var c = f.Component!;
                    c._mounted = false;
                    c._unmounting = false;
                    c._mountCalled = false;
                    c._forceRebuild = false;
                    c._extraBuildAllowed = false;
                    c._builtFrame = -1;
                    c._fiber = null;
                    c._root = null;
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------ 宿主同步

    /// <summary>收集实例的顶层宿主元素（组件与片段展开）。</summary>
    public static void CollectHosts(Fiber f, List<Element> into)
    {
        var children = f.Children;
        if (children is null) return;
        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            if (c.Kind == FiberKind.Element || c.Kind == FiberKind.Text) into.Add(c.Element!);
            else if (c.Kind != FiberKind.Portal) CollectHosts(c, into); // 传送门的元素在覆盖层中
        }
    }

    static Fiber HostOf(Fiber f)
    {
        var p = f.Parent;
        while (p is not null && !p.IsHost) p = p.Parent;
        return p ?? throw new InvalidOperationException("实例没有宿主。");
    }

    static bool SameSequence(List<Element> a, List<Element> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    long _syncStamp;

    /// <summary>
    /// 让宿主元素的子节点与实例树展开后的顺序一致：移除不再出现的、插入新的，已有的按最长递增子序列保留，
    /// 其余报告为移动（移动次数最少）。挂在根上的宿主才发出观察者通知。
    /// </summary>
    public void SyncHost(Fiber hostFiber)
    {
        var host = hostFiber.Element!;
        var desired = RentElementList();
        try
        {
            CollectHosts(hostFiber, desired);
            // 根元素的最后一个子元素是覆盖层根（有 Portal 时），不由实例树管理
            if (hostFiber.Kind == FiberKind.Root && _root.Overlay.IsAttached) desired.Add(_root.Overlay.Element);
            var current = host.MutableChildren;
            if (desired.Count == current.Count)
            {
                bool same = true;
                for (int i = 0; i < desired.Count; i++)
                {
                    if (!ReferenceEquals(desired[i], current[i]) || !ReferenceEquals(desired[i]._parent, host))
                    {
                        same = false;
                        break;
                    }
                }
                if (same) return;
            }

            long stamp = ++_syncStamp;
            foreach (var d in desired) d._syncStamp = stamp;
            bool attached = host._root is not null;

            // 移除
            List<Element>? removed = null;
            for (int i = 0; i < current.Count; i++)
            {
                var c = current[i];
                c._index = i; // 旧位置，供 LIS
                if (c._syncStamp != stamp && ReferenceEquals(c._parent, host))
                {
                    c._parent = null;
                    (removed ??= new List<Element>()).Add(c);
                }
            }

            // 已有元素的旧位置 → LIS
            int n = desired.Count;
            var positions = ArrayPool<int>.Shared.Rent(Math.Max(n, 1));
            var keep = ArrayPool<bool>.Shared.Rent(Math.Max(n, 1));
            // 每个位置的变化：0 保留、1 插入、2 移动（供下面按最终顺序发出通知）
            var kinds = ArrayPool<byte>.Shared.Rent(Math.Max(n, 1));
            try
            {
                bool anyInserted = false, anyMoved = false;
                for (int i = 0; i < n; i++)
                {
                    var d = desired[i];
                    positions[i] = ReferenceEquals(d._parent, host) ? d._index : -1;
                }
                Lis.Compute(positions, n, keep);
                for (int i = 0; i < n; i++)
                {
                    var d = desired[i];
                    if (positions[i] < 0)
                    {
                        if (d._parent is not null)
                            throw new InvalidOperationException("元素 " + d + " 已有父节点，不能再插入到 " + host + "。");
                        d._parent = host;
                        kinds[i] = 1;
                        anyInserted = true;
                    }
                    else if (!keep[i])
                    {
                        kinds[i] = 2;
                        anyMoved = true;
                    }
                    else kinds[i] = 0;
                }

                // 写入新顺序
                List<Element> list = current.Capacity == 0 ? new List<Element>(n) : current;
                list.Clear();
                list.AddRange(desired);
                host.ReplaceChildren(list);

                if (removed is not null)
                {
                    foreach (var r in removed)
                    {
                        if (attached) _root.OnSubtreeRemoved(host, r);
                        Element.SetRootRecursive(r, null);
                        if (attached) _root.NotifyChildRemoved(host, r);
                    }
                }
                if (anyInserted)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (kinds[i] != 1) continue;
                        var e = desired[i];
                        if (attached) Element.SetRootRecursive(e, _root);
                        e.MarkInsertedDirty();
                    }
                }
                if (attached && (anyMoved || anyInserted))
                {
                    // 通知顺序让增量维护子节点顺序的观察者（DevTools 的元素树镜像）每一步都能定位：
                    // 先从后往前报告移动，before 取其后第一个不是新插入的兄弟（此时它们都已在最终位置）；
                    // 再从后往前报告插入，此时其后的兄弟全部已报告且位于最终位置。总计 O(子节点数)。
                    if (anyMoved)
                    {
                        Element? settled = null;
                        for (int i = n - 1; i >= 0; i--)
                        {
                            if (kinds[i] == 1) continue;
                            if (kinds[i] == 2) _root.NotifyChildMoved(host, desired[i], settled);
                            settled = desired[i];
                        }
                    }
                    if (anyInserted)
                    {
                        for (int i = n - 1; i >= 0; i--)
                            if (kinds[i] == 1) _root.NotifyChildInserted(host, desired[i]);
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(positions);
                ArrayPool<bool>.Shared.Return(keep);
                ArrayPool<byte>.Shared.Return(kinds);
            }
            host.MarkDirty(ElementDirtyFlags.Style | ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
        }
        finally
        {
            ReturnElementList(desired);
        }
    }

    // ------------------------------------------------------------ 临时列表池

    List<Element> RentElementList() => _elementLists.Count > 0 ? _elementLists.Pop() : new List<Element>();

    void ReturnElementList(List<Element> list)
    {
        list.Clear();
        _elementLists.Push(list);
    }

    List<Fiber> RentFiberList() => _fiberLists.Count > 0 ? _fiberLists.Pop() : new List<Fiber>();

    void ReturnFiberList(List<Fiber> list)
    {
        list.Clear();
        _fiberLists.Push(list);
    }
}
