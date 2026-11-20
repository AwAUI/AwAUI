using System;
using System.Collections.Generic;
using AwAUI.Css;
using AwAUI.Css.Values;
using AwAUI.Layout;
using AwAUI.Platform;
using LayoutLength = AwAUI.Layout.LengthPercentage;

namespace AwAUI.UI;

/// <summary>在内边距上加上滚动条槽宽度的长度（原内边距可能含百分比，布局时解析）。按值相等比较。</summary>
internal sealed class GutterLength : LayoutCalc
{
    readonly LayoutLength _inner;
    readonly float _add;

    public GutterLength(LayoutLength inner, float add)
    {
        _inner = inner;
        _add = add;
    }

    public override float Resolve(float percentBasis)
    {
        float v = _inner.Kind switch
        {
            DimensionKind.Percent => float.IsNaN(percentBasis) ? 0 : _inner.Value / 100f * percentBasis,
            DimensionKind.Calc => _inner.Calc!.Resolve(percentBasis),
            _ => _inner.Value,
        };
        if (float.IsNaN(v) || float.IsInfinity(v)) v = 0;
        return Math.Max(0, v) + _add;
    }

    public override bool HasPercentage => _inner.Kind != DimensionKind.Length;

    public override bool Equals(object? obj) => obj is GutterLength g && g._inner.Equals(_inner) && g._add.Equals(_add);

    public override int GetHashCode() => HashCodeCompat.Combine(_inner.GetHashCode(), _add.GetHashCode());
}

// 经典（占位）滚动条的布局空间（CSS Overflow 3 §3.3、scrollbar-gutter）：滚动条槽位于边框与内边距之间，
// 这里实现为在内边距盒的对应一侧加上槽宽（内容盒相应变窄，滚动条画在内边距盒的边缘上）。覆盖式滚动条不占空间。
public sealed partial class RenderPipeline
{
    /// <summary>
    /// 曾经计算过滚动条槽的滚动容器（滚动条样式在经典与覆盖之间切换时重新映射它们的布局样式）。离开树的在下次切换时移除。
    /// </summary>
    readonly HashSet<Element> _gutterBoxes = new();

    /// <summary>本次布局中 <c>overflow: auto</c> 的容器是否出现 / 消失了溢出而改变了滚动条槽（需要再布局一次）。</summary>
    bool _gutterChanged;

    /// <summary>经典滚动条的宽度（与 <see cref="ScrollbarGeometryFor"/> 一致）；<c>scrollbar-width: none</c> 时为 0。</summary>
    static float ClassicThickness(Element e, ComputedStyle s)
    {
        var width = e._scroll is { } c ? ScrollCss.Width(c) : s.ScrollbarWidth switch
        {
            CssScrollbarWidth.Thin => ScrollbarWidth.Thin,
            CssScrollbarWidth.None => ScrollbarWidth.None,
            _ => ScrollbarWidth.Auto,
        };
        return width switch
        {
            ScrollbarWidth.None => 0,
            ScrollbarWidth.Thin => 8f,
            _ => 12f,
        };
    }

    /// <summary>
    /// 滚动容器的滚动条槽（右、下、左）。只有经典滚动条占空间：纵向在 <c>overflow-y: scroll</c>、<c>scrollbar-gutter: stable</c>
    /// （overflow 为 hidden / scroll / auto）或 <c>overflow-y: auto</c> 且内容溢出时预留在右侧（<c>stable both-edges</c> 时左侧也预留）；
    /// 横向在 <c>overflow-x: scroll</c> 或 <c>auto</c> 且溢出时预留在下方。根元素（视口）不预留。
    /// </summary>
    (float Right, float Bottom, float Left) ComputeGutter(Element e, RenderBox box, ComputedStyle s)
    {
        if (e.Parent is null || box.Kind != BoxKind.Container || EffectiveScrollbarStyle != ScrollbarStyle.Classic) return default;
        int axes = ScrollAxes(e);
        if (axes == 0) return default;
        float t = ClassicThickness(e, s);
        if (t <= 0) return default;
        var view = box.GetView(this);
        float right = 0, bottom = 0, left = 0;
        var oy = s.OverflowY;
        bool stable = s.ScrollbarGutter != CssScrollbarGutter.Auto && oy is CssOverflow.Hidden or CssOverflow.Scroll or CssOverflow.Auto;
        if (stable || oy == CssOverflow.Scroll || (oy == CssOverflow.Auto && view.AutoGutterY))
        {
            right = t;
            if (stable && s.ScrollbarGutter == CssScrollbarGutter.StableBothEdges) left = t;
        }
        var ox = s.OverflowX;
        if (ox == CssOverflow.Scroll || (ox == CssOverflow.Auto && view.AutoGutterX)) bottom = t;
        return (right, bottom, left);
    }

    /// <summary>把滚动条槽加到映射出的布局样式的内边距上（没有槽时原样返回）。</summary>
    LayoutStyle ApplyGutter(Element e, RenderBox box, ComputedStyle s, LayoutStyle mapped)
    {
        var (right, bottom, left) = ComputeGutter(e, box, s);
        var view = box.GetView(this);
        view.GutterRight = right;
        view.GutterBottom = bottom;
        view.GutterLeft = left;
        if (ScrollAxes(e) != 0) _gutterBoxes.Add(e);
        if (right == 0 && bottom == 0 && left == 0) return mapped;
        var p = mapped.Padding;
        return mapped with
        {
            Padding = new Edges<LayoutLength>(p.Top, Add(p.Right, right), Add(p.Bottom, bottom), Add(p.Left, left)),
        };

        static LayoutLength Add(LayoutLength v, float add) =>
            add == 0 ? v : v.Kind == DimensionKind.Length ? LayoutLength.Px(Math.Max(0, v.Value) + add) : LayoutLength.Of(new GutterLength(v, add));
    }

    /// <summary>映射时的滚动条槽与当前应有的是否不同（需要重新映射布局样式）。</summary>
    bool GutterStale(Element e, RenderBox box)
    {
        if (box.Style is not { } s || (ScrollAxes(e) == 0 && !_gutterBoxes.Contains(e))) return false;
        var (right, bottom, left) = ComputeGutter(e, box, s);
        var view = box.GetView(this);
        return right != view.GutterRight || bottom != view.GutterBottom || left != view.GutterLeft;
    }

    /// <summary>
    /// 布局之后（几何遍历中，滚动度量已更新）：<c>overflow: auto</c> 的经典滚动条是否出现。出现溢出时预留槽，内容在预留之后
    /// 仍放得下时取消；变化时标记再布局（<see cref="ContainerQueryPasses"/> 中最多追加有限轮）。
    /// </summary>
    void UpdateAutoGutter(Element e, RenderBox box, ScrollController c)
    {
        if (EffectiveScrollbarStyle != ScrollbarStyle.Classic || e.Parent is null || box.Style is not { } s) return;
        var view = box.GetView(this);
        // 纵向槽不改变视口高度：预留之后仍然溢出才保留（内容因变窄而变高时也保留，不会来回切换）
        bool y = s.OverflowY == CssOverflow.Auto && c._contentH > c._viewportH + 0.5f;
        bool x = s.OverflowX == CssOverflow.Auto && c._contentW > c._viewportW + 0.5f;
        if (y == view.AutoGutterY && x == view.AutoGutterX) return;
        view.AutoGutterY = y;
        view.AutoGutterX = x;
        if (!GutterStale(e, box)) return;
        box.LayoutStyleDirty = true;
        e.MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
        _gutterChanged = true;
    }

    /// <summary>滚动条样式（经典 / 覆盖）变化：重新映射登记过的滚动容器的布局样式。</summary>
    void GuttersForStyleChange()
    {
        List<Element>? stale = null;
        foreach (var e in _gutterBoxes)
        {
            if (!e.IsAttached || e.Render is not { } box)
            {
                (stale ??= new List<Element>()).Add(e);
                continue;
            }
            if (!GutterStale(e, box)) continue;
            box.LayoutStyleDirty = true;
            e.MarkDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
        }
        if (stale is not null)
            foreach (var e in stale) _gutterBoxes.Remove(e);
    }
}
