using System;
using System.Collections.Generic;
using AwAUI.Platform;

namespace AwAUI.UI.Accessibility;

/// <summary>
/// 语义树（design/10）：按需从 <see cref="UIRoot"/> 的元素树构建，过滤装饰性盒子与
/// <c>aria-hidden</c> 子树，并把纯文本合并到最近的语义父节点。
/// <para>
/// 构建是惰性的：<see cref="UIRoot.Accessibility"/> 只创建对象；第一次读取 <see cref="Root"/> /
/// <see cref="Nodes"/> / 查询或连接平台桥时才遍历元素树。没有辅助技术访问时零遍历开销。
/// 元素树、焦点或布局发生变化会把树标脏并使 <see cref="Version"/> 前进，下一次读取时重建。
/// </para>
/// <para>只能在所属 <see cref="UIRoot"/> 的 UI 线程上使用。</para>
/// </summary>
public sealed partial class AccessibilityTree : ElementTreeObserver
{
    readonly UIRoot _root;
    readonly List<AccessibilityNode> _nodes = new();
    readonly Dictionary<string, AccessibilityNode> _byId = new(StringComparer.Ordinal);
    readonly Dictionary<string, Element> _elementsById = new(StringComparer.Ordinal);

    AccessibilityNode? _rootNode;
    long _version = 1;
    int _buildCount;
    bool _dirty = true;
    bool _building;
    AccessibilityBridge? _bridge;
    Func<AccessibilityActionRequest, bool>? _bridgeHandler;

    internal AccessibilityTree(UIRoot root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        root.AddObserver(this);
        root.FocusManager.FocusChanged += _ => Invalidate();
        root.LayoutCompleted += OnLayoutCompleted;
    }

    /// <summary>语义树的根节点。读取时按需构建。</summary>
    public AccessibilityNode Root
    {
        get
        {
            EnsureBuilt();
            return _rootNode!;
        }
    }

    /// <summary>全部节点，按树序（根在前）。读取时按需构建。</summary>
    public IReadOnlyList<AccessibilityNode> Nodes
    {
        get
        {
            EnsureBuilt();
            return _nodes;
        }
    }

    /// <summary>当前版本；每次失效（元素树变化、焦点变化、布局完成）加一。</summary>
    public long Version => _version;

    /// <summary>已构建的次数。用于验证「未使用时不构建」。</summary>
    public int BuildCount => _buildCount;

    /// <summary>当前缓存是否与元素树一致（false 表示下一次读取会重建）。</summary>
    public bool IsBuilt => _rootNode is not null && !_dirty;

    /// <summary>已连接的平台无障碍桥；没有时为 null。</summary>
    public AccessibilityBridge? Bridge => _bridge;

    /// <summary>把树标脏并使 <see cref="Version"/> 前进；下一次读取时重建。</summary>
    public void Invalidate()
    {
        _dirty = true;
        _version++;
    }

    /// <summary>按标识查找节点；不存在时返回 null。</summary>
    public AccessibilityNode? FindById(string id)
    {
        if (id is null) return null;
        EnsureBuilt();
        return _byId.TryGetValue(id, out var node) ? node : null;
    }

    /// <summary>按 ARIA 角色名查找节点（大小写不敏感）。</summary>
    public IReadOnlyList<AccessibilityNode> FindByRole(string role)
    {
        if (string.IsNullOrEmpty(role)) return Array.Empty<AccessibilityNode>();
        EnsureBuilt();
        var result = new List<AccessibilityNode>();
        foreach (var node in _nodes)
            if (string.Equals(node.Role, role, StringComparison.OrdinalIgnoreCase)) result.Add(node);
        return result;
    }

    /// <summary>按可访问名称查找节点。</summary>
    public IReadOnlyList<AccessibilityNode> FindByName(string name, StringComparison comparison = StringComparison.Ordinal)
    {
        if (name is null) return Array.Empty<AccessibilityNode>();
        EnsureBuilt();
        var result = new List<AccessibilityNode>();
        foreach (var node in _nodes)
            if (string.Equals(node.Name, name, comparison)) result.Add(node);
        return result;
    }

    /// <summary>按名称查找第一个节点；不存在时返回 null。</summary>
    public AccessibilityNode? FindFirstByName(string name, StringComparison comparison = StringComparison.Ordinal)
    {
        if (name is null) return null;
        EnsureBuilt();
        foreach (var node in _nodes)
            if (string.Equals(node.Name, name, comparison)) return node;
        return null;
    }

    /// <summary>生成当前版本的平台无关快照（会按需构建）。</summary>
    public AccessibilitySnapshot CreateSnapshot()
    {
        EnsureBuilt();
        return BuildSnapshotCore();
    }

    /// <summary>
    /// 连接平台无障碍桥：立即推送一版快照，并在之后每帧布局完成时按需重建并推送；
    /// 辅助技术发起的 <see cref="AccessibilityBridge.ActionRequested"/> 转交给 <see cref="PerformAction"/>。
    /// 释放返回值以断开。桥未连接时树保持完全惰性。
    /// </summary>
    public IDisposable ConnectBridge(AccessibilityBridge bridge)
    {
        if (bridge is null) throw new ArgumentNullException(nameof(bridge));
        DisconnectBridge();
        _bridge = bridge;
        _bridgeHandler = request => PerformAction(request.NodeId, ToActionFlag(request.Action), request.Value);
        bridge.ActionHandler = _bridgeHandler;
        EnsureBuilt();
        bridge.Update(BuildSnapshotCore());
        return new BridgeConnection(this, bridge);
    }

    /// <summary>断开当前平台桥（若有）。</summary>
    public void DisconnectBridge()
    {
        if (_bridge is null) return;
        if (_bridgeHandler is not null && ReferenceEquals(_bridge.ActionHandler, _bridgeHandler))
            _bridge.ActionHandler = null;
        _bridge = null;
        _bridgeHandler = null;
    }

    /// <summary>按节点标识执行平台桥动作；节点不存在、动作不可用或处于禁用状态时返回 false。</summary>
    public bool PerformAction(string nodeId, AccessibilityActionKind action, string? value = null) =>
        PerformAction(nodeId, FromActionKind(action), value);

    /// <summary>按节点标识执行动作；节点不存在、动作不可用或处于禁用状态时返回 false。</summary>
    public bool PerformAction(string nodeId, AccessibilityAction action, string? value = null)
    {
        if (nodeId is null) return false;
        var node = FindById(nodeId);
        return node is not null && Perform(node, action, value);
    }

    void OnLayoutCompleted(UIRoot root)
    {
        // 滚动偏移、变换与布局几何可能改变边界与命中顺序；只标脏，桥连接时才立即重建。
        Invalidate();
        if (_bridge is not null)
        {
            EnsureBuilt();
            _bridge.Update(BuildSnapshotCore());
        }
    }

    void EnsureBuilt()
    {
        if (!_dirty && _rootNode is not null)
        {
            // 只读文本选区不属于元素树变化：选区版本前进时同样需要重建（TextPattern 的起止）。
            if (_root.Selection.SelectionVersion == _selectionVersion) return;
            _dirty = true;
            _version++;
        }
        if (_building) return; // 构建期间的通知不再递归构建
        _building = true;
        try
        {
            Rebuild();
        }
        finally
        {
            _building = false;
        }
    }

    internal bool Perform(AccessibilityNode node, AccessibilityAction action, string? value)
    {
        if (node is null || action == AccessibilityAction.None) return false;
        if ((action & (action - 1)) != 0) return false; // 一次一个动作
        if ((node.Actions & action) == 0) return false;

        var element = node.Element;
        if (!element.IsAttached) return false;
        bool disabled = (node.State & AccessibilityState.Disabled) != 0;

        switch (action)
        {
            case AccessibilityAction.Invoke:
            case AccessibilityAction.Toggle:
            case AccessibilityAction.Expand:
            case AccessibilityAction.Collapse:
                if (disabled) return false;
                return _root.Input.DispatchClick(element);

            case AccessibilityAction.Focus:
                return element.Focus();

            case AccessibilityAction.SetValue:
                if (disabled || element is not InputElement input || input.ReadOnly) return false;
                input.SetValue(value ?? "");
                return true;

            case AccessibilityAction.Increment:
            case AccessibilityAction.Decrement:
                if (disabled || !element.IsFocusable) return false;
                if (!element.IsFocused && !element.Focus()) return false;
                DispatchArrow(action == AccessibilityAction.Increment ? PhysicalKey.ArrowUp : PhysicalKey.ArrowDown);
                return true;

            case AccessibilityAction.ScrollIntoView:
                return ScrollIntoView(element);
        }
        return false;
    }

    void DispatchArrow(PhysicalKey key)
    {
        _root.Input.DispatchKey(new RawKeyEvent(RawKeyEventType.Down, key, KeyModifiers.None, false, 0, TimeSpan.Zero));
        _root.Input.DispatchKey(new RawKeyEvent(RawKeyEventType.Up, key, KeyModifiers.None, false, 0, TimeSpan.Zero));
    }

    /// <summary>沿全部滚动祖先滚动到可见；使用现有的 <see cref="ScrollController.ScrollIntoView"/>。</summary>
    bool ScrollIntoView(Element element)
    {
        bool any = false;
        int guard = 0;
        for (var e = element.Parent; e is not null && guard++ < 10_000; e = e.Parent)
        {
            if (e.Parent is null || RenderPipeline.ScrollAxes(e) != 0)
            {
                e.ScrollController.ScrollIntoView(element);
                any = true;
            }
        }
        return any;
    }

    // ------------------------------------------------------------ 观察者：任何元素变化都使语义树失效

    public override void OnChildInserted(Element parent, Element child) => Invalidate();
    public override void OnChildRemoved(Element parent, Element child) => Invalidate();
    public override void OnChildMoved(Element parent, Element child) => Invalidate();
    public override void OnIdChanged(Element element, string? oldId) => Invalidate();
    public override void OnClassesChanged(Element element) => Invalidate();
    public override void OnAttributeChanged(Element element, string name, string? oldValue) => Invalidate();
    public override void OnInlineStyleChanged(Element element) => Invalidate();
    public override void OnPseudoStatesChanged(Element element, PseudoStates oldStates) => Invalidate();
    public override void OnTextChanged(Element element) => Invalidate();
    public override void OnPropertyChanged(Element element, string name) => Invalidate();

    sealed class BridgeConnection : IDisposable
    {
        AccessibilityTree? _tree;
        readonly AccessibilityBridge _bridge;

        public BridgeConnection(AccessibilityTree tree, AccessibilityBridge bridge)
        {
            _tree = tree;
            _bridge = bridge;
        }

        public void Dispose()
        {
            var tree = _tree;
            if (tree is null) return;
            _tree = null;
            if (ReferenceEquals(tree._bridge, _bridge)) tree.DisconnectBridge();
        }
    }
}
