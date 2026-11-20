namespace AwAUI.UI;

/// <summary>
/// 传送门（design/23 第 1 层，Radix <c>Portal</c>）：子节点的元素挂到窗口的覆盖层（<see cref="UIRoot.Overlay"/>）中自己的一层，
/// 画在常规内容之上、在树序上位于其后；而在组件树中，子组件的父级仍是产出 Portal 的组件，所以
/// <c>Use&lt;T&gt;()</c> 上下文、错误边界与逻辑上的嵌套关系（如 DismissableLayer 的父子层）照常可用。
/// <para>
/// 每个 Portal 在挂载时建立一层，按 <see cref="ZOrder"/> 与挂载顺序叠放（后挂载的在上）；卸载时移除该层。
/// 层本身是 <c>display: contents</c>，子元素按视口排版——浮层内容通常是 <c>position: fixed</c>。
/// Portal 不产生常规的宿主元素：父组件的 <see cref="Component.GetRootElements"/> 不包含其中的元素。
/// </para>
/// </summary>
public sealed class Portal : Node
{
    NodeList? _children;

    public Portal() { }

    public Portal(NodeList children) => _children = children;

    /// <summary>子节点。读取时按需创建，因此可以写 <c>Children = { a, b }</c>。</summary>
    public NodeList Children
    {
        get => _children ??= new NodeList();
        set => _children = value;
    }

    internal NodeList? ChildrenOrNull => _children;

    /// <summary>层级（<see cref="OverlayZOrder"/>）：只在挂载时生效，之后的变化被忽略。</summary>
    public int ZOrder { get; set; }
}
