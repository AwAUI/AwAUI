using System;
using System.Collections.Generic;

namespace AwAUI.Diagnostics.Inspection;

/// <summary>节点种类（design/20 公共类型 <c>Node.kind</c>）。</summary>
public enum InspectableNodeKind
{
    /// <summary>元素：有标签、id、class、属性。</summary>
    Element,
    /// <summary>文本节点：只有文本内容。</summary>
    Text,
}

/// <summary>源码位置（design/20 <c>SourceLocation = { file, line, column }</c>）。行、列从 1 开始；0 表示未知。</summary>
public readonly struct SourceLocation : IEquatable<SourceLocation>
{
    public SourceLocation(string file, int line, int column)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        Line = line;
        Column = column;
    }

    /// <summary>文件路径或样式表 URL。</summary>
    public string File { get; }
    public int Line { get; }
    public int Column { get; }

    public bool Equals(SourceLocation other) => File == other.File && Line == other.Line && Column == other.Column;
    public override bool Equals(object? obj) => obj is SourceLocation o && Equals(o);
    public override int GetHashCode() => HashCodeCompat.Combine(File?.GetHashCode() ?? 0, Line, Column);
    public override string ToString() => File + ":" + Line + ":" + Column;
}

/// <summary>
/// 一个节点的描述，由 <see cref="InspectableTree.DescribeNode"/> 按需创建（快照：只在调用当时有效，协议层不会长期持有）。
/// </summary>
/// <remarks>
/// <para><see cref="Id"/> 是节点在其树中的稳定标识：元素存活期间不变，<b>节点移除后不得再分配给别的节点</b>（单调递增计数器即可，
/// 例如 <c>Element.UniqueId</c> 或 <see cref="ObjectRegistry"/>）。必须为正数且不超过 2^53−1（JavaScript 前端用 double 表示数字）。</para>
/// <para><see cref="Attributes"/> 不含 <c>id</c> 与 <c>class</c>：它们分别通过 <see cref="ElementId"/> 与 <see cref="Classes"/> 给出。</para>
/// </remarks>
public sealed class InspectableNodeInfo
{
    public InspectableNodeInfo(long id, InspectableNodeKind kind)
    {
        Id = id;
        Kind = kind;
    }

    public long Id { get; }
    public InspectableNodeKind Kind { get; }

    /// <summary>父节点 id；根节点为 0。</summary>
    public long ParentId { get; set; }

    /// <summary>元素类型名（<c>div</c>、<c>button</c> ...）；文本节点可为 null。</summary>
    public string? Tag { get; set; }

    /// <summary>元素的 id 属性。</summary>
    public string? ElementId { get; set; }

    /// <summary>class 列表（书写顺序）。</summary>
    public IReadOnlyList<string>? Classes { get; set; }

    /// <summary>其余属性（不含 id、class），按书写顺序。</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Attributes { get; set; }

    /// <summary>文本节点的内容（元素为 null）。过长时协议层截断并标记 <c>textTruncated</c>。</summary>
    public string? Text { get; set; }

    /// <summary>产生该节点的组件 id；没有为 0。</summary>
    public long ComponentId { get; set; }

    /// <summary>产生该节点的组件类型名（如 <c>Counter</c>）。</summary>
    public string? ComponentName { get; set; }

    /// <summary>当前生效的伪状态（<c>hover</c>、<c>focus</c> ...），含 DevTools 强制的状态。</summary>
    public IReadOnlyList<string>? PseudoStates { get; set; }

    /// <summary>子节点数（含文本节点）。协议层据此显示未展开节点的展开箭头。</summary>
    public int ChildCount { get; set; }

    /// <summary>创建处的源码位置（<c>.awaui</c> 组件经源映射定位到标签）。</summary>
    public SourceLocation? Source { get; set; }
}

/// <summary>四边形的四个角（窗口坐标，DIP），顺时针，从左上角开始；有变换时不一定是矩形（design/20 <c>Quad</c>）。</summary>
public readonly struct Quad : IEquatable<Quad>
{
    public Quad(Point p1, Point p2, Point p3, Point p4)
    {
        P1 = p1; P2 = p2; P3 = p3; P4 = p4;
    }

    public Point P1 { get; }
    public Point P2 { get; }
    public Point P3 { get; }
    public Point P4 { get; }

    /// <summary>未变换矩形的四个角。</summary>
    public static Quad FromRect(Rect r) => new(r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft);

    public bool Equals(Quad o) => P1 == o.P1 && P2 == o.P2 && P3 == o.P3 && P4 == o.P4;
    public override bool Equals(object? obj) => obj is Quad q && Equals(q);
    public override int GetHashCode() => HashCodeCompat.Combine(P1.GetHashCode(), P2.GetHashCode(), P3.GetHashCode(), P4.GetHashCode());
}

/// <summary>
/// 节点的盒模型（design/20 <c>Layout.getBoxModel</c>），窗口坐标、DIP。由 <see cref="InspectableTree.GetBoxModel"/> 提供。
/// </summary>
public sealed class BoxModelInfo
{
    /// <summary>由四个未变换的矩形构造。</summary>
    public BoxModelInfo(Rect margin, Rect border, Rect padding, Rect content)
        : this(Quad.FromRect(margin), Quad.FromRect(border), Quad.FromRect(padding), Quad.FromRect(content), border.Width, border.Height)
    {
    }

    /// <summary>由变换后的四边形构造；<paramref name="width"/> / <paramref name="height"/> 是边框盒的布局尺寸（变换前）。</summary>
    public BoxModelInfo(Quad margin, Quad border, Quad padding, Quad content, float width, float height)
    {
        Margin = margin; Border = border; Padding = padding; Content = content; Width = width; Height = height;
    }

    public Quad Margin { get; }
    public Quad Border { get; }
    public Quad Padding { get; }
    public Quad Content { get; }

    /// <summary>边框盒宽度。</summary>
    public float Width { get; }
    /// <summary>边框盒高度。</summary>
    public float Height { get; }

    /// <summary>滚动容器的滚动偏移；不是滚动容器为 null。</summary>
    public Point? ScrollOffset { get; set; }
    /// <summary>滚动容器的内容尺寸；不是滚动容器为 null。</summary>
    public Size? ScrollSize { get; set; }
}
