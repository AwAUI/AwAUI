using System;
using System.Collections.Generic;

namespace AwAUI.Controls.Primitives;

/// <summary>
/// 子菜单的指针宽限区（Radix Menu 的 pointer grace intent，「安全三角形」）：指针离开子菜单触发项、斜向移往已打开的子菜单时，
/// 会经过其他菜单项；只要指针仍朝子菜单方向移动且位于「离开点 + 子菜单近边 + 远边」构成的多边形内，就不应切换高亮或关闭子菜单。
/// 宽限区在 <see cref="GraceDuration"/>（300 ms）后失效。纯几何与时间计算，时间由调用方传入。
/// </summary>
public sealed class SubmenuGraceArea
{
    readonly List<Point> _polygon = new(5);
    TimeSpan _expires;
    float _lastPointerX = float.NaN;

    /// <summary>宽限区的有效期（Radix 为 300 ms）。</summary>
    public TimeSpan GraceDuration { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>离开点沿水平方向向子菜单外侧放宽的距离（Radix 为 5px），保证离开点本身落在多边形内。</summary>
    public float Bleed { get; set; } = 5;

    /// <summary>当前宽限区多边形（未激活时为空）。</summary>
    public IReadOnlyList<Point> Polygon => _polygon;

    /// <summary>子菜单所在的一侧。</summary>
    public Side Side { get; private set; }

    /// <summary>最近一次水平移动的方向（<see cref="Side.Left"/> 或 <see cref="Side.Right"/>）；还没有水平移动时为 null。</summary>
    public Side? PointerDirection { get; private set; }

    /// <summary>宽限区在 <paramref name="now"/> 时是否有效。</summary>
    public bool IsActive(TimeSpan now) => _polygon.Count > 0 && now < _expires;

    /// <summary>
    /// 指针离开子菜单触发项：以离开点与子菜单矩形建立宽限区（子菜单在 <paramref name="side"/> 一侧，只支持左 / 右）。
    /// </summary>
    public void Begin(Point exitPoint, Rect submenuRect, Side side, TimeSpan now)
    {
        _polygon.Clear();
        Side = side;
        bool right = side != Side.Left;
        float bleed = right ? -Bleed : Bleed;
        float near = right ? submenuRect.Left : submenuRect.Right;
        float far = right ? submenuRect.Right : submenuRect.Left;
        _polygon.Add(new Point(exitPoint.X + bleed, exitPoint.Y));
        _polygon.Add(new Point(near, submenuRect.Top));
        _polygon.Add(new Point(far, submenuRect.Top));
        _polygon.Add(new Point(far, submenuRect.Bottom));
        _polygon.Add(new Point(near, submenuRect.Bottom));
        _expires = now + GraceDuration;
    }

    /// <summary>清除宽限区（指针进入子菜单、子菜单关闭或宽限期结束）。</summary>
    public void Clear() => _polygon.Clear();

    /// <summary>菜单内的指针移动：记录水平移动方向（只有 x 变化时才更新，与 Radix 相同）。</summary>
    public void TrackPointerMove(Point position)
    {
        if (!float.IsNaN(_lastPointerX) && position.X != _lastPointerX)
            PointerDirection = position.X > _lastPointerX ? Side.Right : Side.Left;
        _lastPointerX = position.X;
    }

    /// <summary>指针是否正移往子菜单（方向朝向子菜单且在有效的宽限区内）：此时应忽略对其他菜单项的进入 / 离开。</summary>
    public bool IsPointerMovingToSubmenu(Point position, TimeSpan now) =>
        IsActive(now) && PointerDirection == Side && IsPointInPolygon(position, _polygon);

    /// <summary>射线法判断点是否在多边形内（与 Radix 的 <c>isPointInPolygon</c> 相同；边界上的点可能归入任一侧）。</summary>
    public static bool IsPointInPolygon(Point point, IReadOnlyList<Point> polygon)
    {
        if (polygon is null) throw new ArgumentNullException(nameof(polygon));
        bool inside = false;
        int n = polygon.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            bool intersect = (a.Y > point.Y) != (b.Y > point.Y) &&
                             point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (intersect) inside = !inside;
        }
        return inside;
    }
}
