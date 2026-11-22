using AwAUI.Css.Values;
using AwAUI.Graphics;

namespace AwAUI.UI;

/// <summary>
/// 滚动锚定的采样状态（design/09「滚动」）：视口内第一个（文档序）可见常规流锚点元素、它在视口内的纵向位置、
/// 采样时的偏移与视口高度。每个滚动控制器一个；只在需要时重新选择锚点，正常帧里只读锚点元素的几何（O(1)）。
/// <para>
/// 位置以「锚点边框盒上边缘 − 容器视口上边缘」表示：祖先滚动容器平移会让两者一起移动，差值不变，
/// 因此嵌套容器各自独立，互不干扰。
/// </para>
/// </summary>
internal sealed class ScrollAnchor
{
    /// <summary>锚点元素；没有有效锚点时恰好为 null。</summary>
    public Element? Element;

    /// <summary>锚点边框盒上边缘相对容器视口上边缘的位置（逻辑像素）。</summary>
    public double ViewportPos;

    /// <summary>采样时容器已施加的纵向偏移（设备像素对齐），用于发现用户输入 / 程序滚动。</summary>
    public double AppliedOffset;

    /// <summary>采样时的视口高度，用于发现视口尺寸变化。</summary>
    public double ViewportSize;

    /// <summary>是否有可用的锚点。</summary>
    public bool Valid;

    /// <summary>清除锚点（关闭开关、容器不再可滚动或锚点已不可用时）。</summary>
    public void Clear()
    {
        Element = null;
        Valid = false;
        ViewportPos = 0;
    }

    /// <summary>
    /// 锚点是否仍可参与校正：已挂载、有边框盒、处于常规流且仍是容器的后代。
    /// 不要求它当前仍与视口相交——内容在它上方插入把它推出视口时仍要用它校正回原位。
    /// </summary>
    public bool Usable(Element container)
    {
        if (!Valid || Element is not { } a) return false;
        if (!a.IsAttached || a.Render is not { HasBox: true } box) return false;
        if (!ReferenceEquals(a.Root, container.Root)) return false;
        var s = box.Style;
        if (s is not null)
        {
            if (s.Visibility != CssVisibility.Visible) return false;
            if (s.Position is CssPosition.Absolute or CssPosition.Fixed or CssPosition.Sticky) return false;
        }
        // 必须是本容器的后代，且路径上不能跨过另一个滚动容器（否则内层的滚动会让锚点相对本容器移动）
        for (var x = a.Parent; x is not null; x = x.Parent)
        {
            if (ReferenceEquals(x, container)) return true;
            if (RenderPipeline.ScrollAxes(x) != 0) return false;
        }
        return false;
    }

    /// <summary>锚点边框盒是否与视口纵向相交。</summary>
    public bool Intersects(Element container, Rect viewport) => Element is { } a &&
        a.BoundingRect.Bottom > viewport.Top && a.BoundingRect.Top < viewport.Bottom;

    /// <summary>
    /// 选择容器视口内文档序第一个可见的常规流元素：从直接子元素开始，沿「第一个可见子元素」向下，
    /// 遇到叶节点或嵌套滚动容器为止。复杂度为沿这条路径每层的子元素数之和，不遍历整棵子树。
    /// </summary>
    public static Element? Select(Element container, Rect viewport)
    {
        Element? current = null;
        for (int depth = 0; depth < 64; depth++)
        {
            var list = current is null ? container.Children : current.Children;
            Element? next = null;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!IsVisibleInFlow(e, viewport)) continue;
                next = e;
                break;
            }
            if (next is null) break;
            current = next;
            if (!CanDescend(next)) break;
        }
        return current;
    }

    /// <summary>可见、处于常规流、有边框盒，且与视口纵向相交。</summary>
    static bool IsVisibleInFlow(Element e, Rect viewport)
    {
        var b = e.Render;
        if (b is null || !b.HasBox) return false;
        var s = b.Style;
        if (s is not null)
        {
            if (s.Visibility != CssVisibility.Visible) return false;
            if (s.Position is CssPosition.Absolute or CssPosition.Fixed or CssPosition.Sticky) return false;
        }
        var r = e.BoundingRect;
        return r.Bottom > viewport.Top && r.Top < viewport.Bottom;
    }

    /// <summary>还能沿它向下找锚点：普通容器（不是嵌套滚动容器，也不是段落 / 替换元素），并且有子元素。</summary>
    static bool CanDescend(Element e)
    {
        var b = e.Render;
        if (b is null || b.Kind != BoxKind.Container) return false;
        if (RenderPipeline.ScrollAxes(e) != 0) return false;
        return e.Children.Count > 0;
    }
}
