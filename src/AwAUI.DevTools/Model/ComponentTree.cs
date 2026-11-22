using System;
using System.Collections.Generic;

namespace AwAUI.DevTools;

/// <summary>
/// 组件页里的一个组件实例：协议只提供 <see cref="DomNode.ComponentName"/>，没有实例 id，
/// 因此一个实例取「拥有该名字段的最上层元素」作为代表（父元素的组件名不同，或没有组件名）。
/// </summary>
internal sealed class ComponentInstance
{
    public ComponentInstance(string name, DomNode node, ComponentInstance? parent)
    {
        Name = name;
        Node = node;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    /// <summary>组件类型名（<c>DomNode.ComponentName</c>）。</summary>
    public readonly string Name;
    /// <summary>代表这个实例的元素（该组件输出的第一个元素）。</summary>
    public readonly DomNode Node;
    /// <summary>最近的组件祖先实例；顶层为 null。</summary>
    public readonly ComponentInstance? Parent;
    /// <summary>组件嵌套深度（顶层为 0）。</summary>
    public readonly int Depth;
    /// <summary>嵌套在本实例里的组件实例（元素树顺序）。</summary>
    public readonly List<ComponentInstance> Children = new();

    public override string ToString() => Name + " #" + Node.Id;
}

/// <summary>
/// 从元素树镜像构建组件实例树。只遍历一次镜像（迭代，深树不会耗尽栈），达到实例上限时提前停止并报告截断，
/// 所以十万节点的应用也不会因为每次重建组件页而卡死。
/// </summary>
internal static class ComponentTreeBuilder
{
    /// <summary>最多列出的组件实例数；超出的部分不构建。</summary>
    public const int MaxInstances = 1000;

    /// <summary>
    /// 构建组件实例树。<paramref name="roots"/> 接收顶层实例（元素树顺序），返回构建出的实例数；
    /// 达到 <paramref name="maxInstances"/> 时 <paramref name="truncated"/> 为 true。
    /// </summary>
    public static int Build(TreeMirror tree, int maxInstances, List<ComponentInstance> roots, out bool truncated)
    {
        if (tree is null) throw new ArgumentNullException(nameof(tree));
        if (roots is null) throw new ArgumentNullException(nameof(roots));
        if (maxInstances < 0) maxInstances = 0;
        truncated = false;
        if (tree.Root is not { } root) return 0;

        int count = 0;
        // (节点, 该节点所属的最近组件实例)。先入后出 + 反向压入子节点 = 元素树顺序。
        var stack = new Stack<(DomNode Node, ComponentInstance? Current)>();
        stack.Push((root, null));
        while (stack.Count > 0)
        {
            var (node, current) = stack.Pop();
            string? name = node.ComponentName;
            if (!node.IsText && !string.IsNullOrEmpty(name) && (current is null || !string.Equals(current.Name, name, StringComparison.Ordinal)))
            {
                if (count >= maxInstances)
                {
                    truncated = true;
                    break;
                }
                var instance = new ComponentInstance(name!, node, current);
                if (current is null) roots.Add(instance);
                else current.Children.Add(instance);
                current = instance;
                count++;
            }
            if (node.Children is { } children)
                for (int i = children.Count - 1; i >= 0; i--) stack.Push((children[i], current));
        }
        return count;
    }
}
