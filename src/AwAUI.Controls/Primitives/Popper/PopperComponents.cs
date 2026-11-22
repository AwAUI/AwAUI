using System;
using System.Collections.Generic;
using System.Globalization;
using AwAUI.UI;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 一组 Popper 部件共享的状态（Radix 的 <c>PopperContext</c>）：锚点、箭头与最近一次的定位结果。由 <see cref="Popper"/> 提供，
/// <see cref="PopperAnchor"/>、<see cref="PopperContent"/>、<see cref="PopperArrow"/> 读取。
/// </summary>
public sealed class PopperScope
{
    internal Component? Anchor;
    internal Func<Rect>? VirtualAnchor;
    internal PopperArrow? Arrow;

    /// <summary>
    /// 没有 <see cref="PopperAnchor"/> 时用作锚点的组件（复合原语把触发器登记在这里，相当于 Radix 中触发器外包的 <c>PopperAnchor asChild</c>）。
    /// </summary>
    public Component? DefaultAnchor { get; set; }

    /// <summary>当前的锚点元素（<see cref="PopperAnchor"/> 的元素，否则 <see cref="DefaultAnchor"/> 的第一个元素）。</summary>
    public Element? AnchorElement => FirstElementOf(Anchor) ?? FirstElementOf(DefaultAnchor);

    /// <summary>最近一次定位的结果（浮层没有显示时为 null）。</summary>
    public PopperLayoutResult? LastLayout { get; internal set; }

    static Element? FirstElementOf(Component? c) => c is { IsMounted: true } ? DismissableLayer.FirstElement(c) : null;
}

/// <summary>Popper 的根（Radix <c>Popper</c>）：向子树提供 <see cref="PopperScope"/>，自身不产生元素。</summary>
public sealed partial class Popper : Component
{
    readonly PopperScope _scope = new();

    public NodeList? Children { get; set; }

    /// <summary>本组部件共享的状态。</summary>
    public PopperScope Scope => _scope;

    protected override Node Build() => Provide(_scope, new Fragment(Children ?? new NodeList()));
}

/// <summary>
/// 锚点（Radix <c>PopperAnchor</c>）：浮层相对它定位。<see cref="AsChild"/> 时锚点就是子元素（类、样式与属性经 <see cref="Slot"/> 合并进去），
/// 否则包一层 <see cref="Div"/>。也可以给出 <see cref="VirtualRect"/>（窗口坐标的虚拟锚点，例如鼠标位置）。
/// </summary>
public sealed partial class PopperAnchor : Component
{
    PopperScope? _scope;

    public bool AsChild { get; set; }

    public NodeList? Children { get; set; }

    /// <summary>虚拟锚点（Radix 的 <c>virtualRef</c>）：每次定位时调用，返回窗口坐标的矩形。</summary>
    public Func<Rect>? VirtualRect { get; set; }

    public ClassValue Class { get; set; }

    public StyleValue Style { get; set; }

    [CaptureUnmatchedAttributes]
    public AttributeDictionary? Attributes { get; set; }

    protected override Node Build()
    {
        var scope = Use<PopperScope>();
        _scope = scope;
        scope.Anchor = this;
        scope.VirtualAnchor = VirtualRect;
        bool hasProps = !Class.IsEmpty || !Style.IsEmpty || Attributes is { Count: > 0 };
        if (AsChild)
        {
            var child = Slot.SingleChild(Children);
            if (!hasProps) return child ?? new Fragment();
            return Slot.Merge(PartProps.Apply(new Div(), Class, Style, Attributes), child);
        }
        return PartProps.Apply(new Div { Children = Children ?? new NodeList() }, Class, Style, Attributes);
    }

    protected override void OnUnmount()
    {
        if (_scope is not null && ReferenceEquals(_scope.Anchor, this))
        {
            _scope.Anchor = null;
            _scope.VirtualAnchor = null;
        }
    }
}

/// <summary>
/// 浮层内容（Radix <c>PopperContent</c>）：外层包装元素 <c>position: fixed</c>（属性 <c>data-aw-popper-content-wrapper</c>），
/// 内层是内容元素（调用方的类、样式、属性与子节点）。每帧布局完成时（<see cref="UIRoot.LayoutCompleted"/>，同一帧内绘制之前）测量锚点与浮层，
/// 用 <see cref="PopperLayout"/>（Floating UI 语义：offset / shift / flip / size / arrow / hide）计算位置，并以运行时覆盖写入：
/// 包装元素的 <c>left / top</c>、<c>z-index</c>（取内容元素的计算值，与 Radix 相同）与 CSS 变量 <c>--aw-popper-available-width/height</c>、
/// <c>--aw-popper-anchor-width/height</c>、<c>--aw-popper-transform-origin</c>；内容元素的 <c>data-side / data-align</c>；箭头的位置。
/// 锚点移动、尺寸变化或任一祖先滚动后，下一帧（同一帧的布局之后）随之更新。
/// </summary>
public sealed partial class PopperContent : Component
{
    const string WrapperStyle = "position: fixed; left: 0; top: 0; min-width: max-content";

    PopperScope? _scope;
    UIRoot? _attachedRoot;
    Action<UIRoot>? _handler;

    public Side Side { get; set; } = Side.Bottom;

    public float SideOffset { get; set; }

    public PopperAlign Align { get; set; } = PopperAlign.Center;

    public float AlignOffset { get; set; }

    public float ArrowPadding { get; set; }

    public bool AvoidCollisions { get; set; } = true;

    /// <summary>与碰撞边界保持的距离（四边相同）。</summary>
    public float CollisionPadding { get; set; }

    /// <summary>显式的碰撞边界（窗口坐标）；null 时为视口与锚点 / 浮层的裁剪祖先。</summary>
    public Rect? CollisionBoundary { get; set; }

    public PopperSticky Sticky { get; set; } = PopperSticky.Partial;

    /// <summary>锚点被裁剪祖先完全遮住时隐藏浮层（<c>visibility: hidden</c>、不接收指针）。</summary>
    public bool HideWhenDetached { get; set; }

    /// <summary>位置变化后调用（在布局完成的处理中）。</summary>
    public Action<PopperLayoutResult>? OnPlaced { get; set; }

    public NodeList? Children { get; set; }

    public string? Id { get; set; }

    public Role Role { get; set; }

    public int? TabIndex { get; set; }

    /// <summary>Reference to the content element, inside the positioning wrapper.</summary>
    public Ref<Element>? Ref { get; set; }

    public ClassValue Class { get; set; }

    public StyleValue Style { get; set; }

    [CaptureUnmatchedAttributes]
    public AttributeDictionary? Attributes { get; set; }

    protected override Node Build()
    {
        _scope = Use<PopperScope>();
        var content = PartProps.Apply(new Div { Id = Id, Role = Role, TabIndex = TabIndex, Ref = Ref, Children = Children ?? new NodeList() }, Class, Style, Attributes);
        var wrapper = new Div { Style = WrapperStyle, Children = { content } };
        wrapper.Attributes["data-aw-popper-content-wrapper"] = "";
        return wrapper;
    }

    protected override void OnMount()
    {
        var root = Root!;
        _attachedRoot = root;
        _handler = Position;
        root.LayoutCompleted += _handler;
    }

    protected override void OnUnmount()
    {
        if (_attachedRoot is not null && _handler is not null) _attachedRoot.LayoutCompleted -= _handler;
        _attachedRoot = null;
        _handler = null;
        if (_scope is not null) _scope.LastLayout = null;
    }

    /// <summary>布局完成：测量并定位（只在结果变化时改写运行时覆盖，没有变化时不产生任何脏标记）。</summary>
    void Position(UIRoot root)
    {
        var scope = _scope;
        if (scope is null || !IsMounted) return;
        var wrapper = DismissableLayer.FirstElement(this);
        if (wrapper is null || !wrapper.IsRendered) return;
        var content = wrapper.ElementChildren.Count > 0 ? wrapper.ElementChildren[0] : null;
        var anchorElement = scope.AnchorElement;
        Rect anchorRect;
        if (scope.VirtualAnchor is { } virtualAnchor) anchorRect = virtualAnchor();
        else if (anchorElement is not null && anchorElement.IsRendered) anchorRect = anchorElement.GetBoundingClientRect();
        else return;

        var viewport = root.ViewportSize;
        var env = new PopperEnvironment(new Rect(0, 0, viewport.Width, viewport.Height))
        {
            ReferenceClippingRect = anchorElement is not null && scope.VirtualAnchor is null ? anchorElement.GetClippingRect() : null,
            // direction 是继承属性：浮层元素的计算方向决定上 / 下边时 start / end 的物理侧（与 Floating UI 读取浮层元素一致）
            Rtl = (content ?? anchorElement)?.ComputedStyle is { Direction: AwAUI.Css.Values.CssDirection.Rtl },
        };
        var arrow = scope.Arrow is { IsMounted: true } a ? a : null;
        var options = new PopperOptions
        {
            Side = Side,
            SideOffset = SideOffset,
            Align = Align,
            AlignOffset = AlignOffset,
            AvoidCollisions = AvoidCollisions,
            CollisionBoundary = CollisionBoundary,
            CollisionPadding = CollisionPadding,
            Sticky = Sticky,
            HideWhenDetached = HideWhenDetached,
            ArrowPadding = ArrowPadding,
            ArrowSize = arrow is null ? null : new Size(arrow.Width, arrow.Height),
            DevicePixelRatio = root.Pipeline is RenderPipeline p ? p.Scale : 0,
        };
        // 浮层取未变换的布局尺寸（Floating UI 的 getDimensions 即 offsetWidth / offsetHeight）：进场动画的 scale 不影响定位
        var size = wrapper.BoundingRect.Size;
        var result = PopperLayout.Compute(anchorRect, size, env, options);

        wrapper.SetStyleOverride("left", Px(result.X));
        wrapper.SetStyleOverride("top", Px(result.Y));
        foreach (var kv in result.CssVariables) wrapper.SetStyleOverride(kv.Key, kv.Value.Length == 0 ? null : kv.Value);
        // 与 Radix 相同：包装元素取内容的 z-index（内容自身不是定位元素时 z-index 不生效）
        var z = content?.ComputedStyle?.ZIndex;
        wrapper.SetStyleOverride("z-index", z is { IsAuto: false } zi ? zi.Value.ToString(CultureInfo.InvariantCulture) : null);
        bool hidden = HideWhenDetached && result.ReferenceHidden;
        wrapper.SetStyleOverride("visibility", hidden ? "hidden" : null);
        wrapper.SetStyleOverride("pointer-events", hidden ? "none" : null);
        if (content is not null)
        {
            content.SetAttributeOverride("data-side", result.DataSide);
            content.SetAttributeOverride("data-align", result.DataAlign);
        }
        if (arrow is not null && DismissableLayer.FirstElement(arrow) is { } arrowElement) PositionArrow(arrowElement, result, arrow.Height);

        bool changed = scope.LastLayout is not { } last || last.X != result.X || last.Y != result.Y || last.Placement != result.Placement;
        scope.LastLayout = result;
        if (changed) OnPlaced?.Invoke(result);
    }

    /// <summary>
    /// 箭头：绝对定位在浮层朝向锚点的一边之外（Radix 用变换旋转箭头；这里只给出位置与 <c>data-side</c>，形状由样式决定）。
    /// 沿边方向的位置取 Floating UI 的 arrow 中间件结果，向外突出 <paramref name="height"/>。无法对准锚点中心时 <c>visibility: hidden</c>。
    /// </summary>
    static void PositionArrow(Element arrow, PopperLayoutResult result, float height)
    {
        var baseSide = result.ArrowBaseSide;
        bool vertical = result.PlacedSide is Side.Top or Side.Bottom;
        string outside = Px(-height);
        arrow.SetStyleOverride("left", vertical ? Px(result.ArrowX ?? 0) : baseSide == Side.Left ? outside : null);
        arrow.SetStyleOverride("top", vertical ? (baseSide == Side.Top ? outside : null) : Px(result.ArrowY ?? 0));
        arrow.SetStyleOverride("right", !vertical && baseSide == Side.Right ? outside : null);
        arrow.SetStyleOverride("bottom", vertical && baseSide == Side.Bottom ? outside : null);
        arrow.SetAttributeOverride("data-side", result.DataSide);
        arrow.SetStyleOverride("visibility", result.CannotCenterArrow ? "hidden" : null);
    }

    static string Px(float v) => TransformOriginMiddleware.Px(v);
}

/// <summary>
/// 箭头（Radix <c>PopperArrow</c>）：放在 <see cref="PopperContent"/> 的子节点中。尺寸由 <see cref="Width"/> / <see cref="Height"/> 给出（参与偏移与对齐计算），
/// 位置由内容的定位写入（<c>position: absolute</c>，贴在朝向锚点的一边之外，属性 <c>data-side</c> 为浮层的实际方位）。
/// </summary>
public sealed partial class PopperArrow : Component
{
    PopperScope? _scope;

    public float Width { get; set; } = 10;

    public float Height { get; set; } = 5;

    public NodeList? Children { get; set; }

    public ClassValue Class { get; set; }

    public StyleValue Style { get; set; }

    [CaptureUnmatchedAttributes]
    public AttributeDictionary? Attributes { get; set; }

    protected override Node Build()
    {
        var scope = Use<PopperScope>();
        _scope = scope;
        scope.Arrow = this;
        var node = new Div
        {
            Style = "position: absolute; width: " + TransformOriginMiddleware.Px(Width) + "; height: " + TransformOriginMiddleware.Px(Height),
            Children = Children ?? new NodeList(),
        };
        node.Attributes["aria-hidden"] = "true";
        node.Attributes["data-aw-popper-arrow"] = "";
        return PartProps.Apply(node, Class, Style, Attributes);
    }

    protected override void OnUnmount()
    {
        if (_scope is not null && ReferenceEquals(_scope.Arrow, this)) _scope.Arrow = null;
    }
}
