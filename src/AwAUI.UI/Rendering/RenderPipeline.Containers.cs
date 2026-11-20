using System;
using System.Collections.Generic;
using AwAUI.Css;
using AwAUI.Css.Values;

namespace AwAUI.UI;

// 容器查询（CSS Containment 3 §2）：@container 的条件与 cq* 单位依赖查询容器的布局尺寸，所以样式要在布局之后再算一遍。
public sealed partial class RenderPipeline
{
    /// <summary>
    /// 每帧在第一次布局之后最多追加的「样式 → 布局」轮数。容器的尺寸可能又依赖于按查询结果算出的样式
    /// （没有实现尺寸包含时，<c>inline-size</c> 容器的高度、收缩适应容器的宽度都随内容变化），轮数有上限以保证每帧时间有界；
    /// 超出时下一帧继续收敛。
    /// </summary>
    public const int MaxContainerQueryPasses = 2;

    /// <summary>样式计算时登记的查询容器（<c>container-type</c> 不是 normal）；离开树或不再是容器的在检查时移除。</summary>
    readonly HashSet<Element> _queryContainers = new();
    readonly List<Element> _containerScratch = new();
    readonly Stack<Element> _containerStack = new();

    /// <summary>
    /// 布局之后：尺寸、<c>container-type</c> 或 <c>container-name</c> 相对上次查询时有变化的容器，把其后代中依赖容器查询的元素
    /// （<see cref="ComputedStyleFlags.DependsOnContainer"/>）标记为样式失效，然后重算样式并重新布局。返回重建的段落数。
    /// </summary>
    int ContainerQueryPasses(UIRoot root, ref int styled, ref bool layoutRan, ref bool geometryChanged)
    {
        bool queries = _queryContainers.Count > 0 && _engine.HasContainerQueries;
        if (!queries && !_gutterChanged) return 0;
        int paragraphs = 0;
        for (int pass = 0; pass < MaxContainerQueryPasses; pass++)
        {
            // 经典滚动条在 overflow: auto 的容器上出现 / 消失（RenderPipeline.Gutter.cs）也在这里再布局
            bool marked = queries && MarkContainerDependents();
            bool gutter = _gutterChanged;
            _gutterChanged = false;
            if (!marked && !gutter) break;
            if (marked) styled += StylePass(root);
            paragraphs += LayoutPass(root, out bool lr, out bool gc);
            layoutRan |= lr;
            geometryChanged |= gc;
            if (!lr) break;
        }
        _gutterChanged = false;
        return paragraphs;
    }

    /// <summary>找出变化了的查询容器并标记依赖它们的后代；返回是否标记了任何元素。</summary>
    bool MarkContainerDependents()
    {
        var changed = _containerScratch;
        changed.Clear();
        List<Element>? removed = null;
        foreach (var c in _queryContainers)
        {
            var box = c.Render;
            if (!c.IsAttached || !ReferenceEquals(c.Root, _root) || box?.Style is not { } style || style.ContainerType == CssContainerType.Normal)
            {
                (removed ??= new List<Element>()).Add(c);
                if (box is not null && c.IsAttached) changed.Add(c); // 不再是容器：依赖它的后代改查更外层的容器
                continue;
            }
            var view = box.GetView(this);
            float w = box.HasBox ? box.ContentWidth : float.NaN, h = box.HasBox ? box.ContentHeight : float.NaN;
            bool styleSame = view.QueryStyle is { } qs && qs.ContainerType == style.ContainerType && qs.ContainerName.Equals(style.ContainerName);
            if (styleSame && SameSize(view.QueryWidth, w) && SameSize(view.QueryHeight, h)) continue;
            view.QueryWidth = w;
            view.QueryHeight = h;
            view.QueryStyle = style;
            changed.Add(c);
        }
        if (removed is not null)
            foreach (var r in removed) _queryContainers.Remove(r);
        if (changed.Count == 0) return false;
        bool any = false;
        var stack = _containerStack;
        foreach (var c in changed)
        {
            stack.Clear();
            var ch0 = c.Children;
            for (int i = 0; i < ch0.Count; i++) stack.Push(ch0[i]);
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                if (e.IsTextNode) continue;
                if (e.Render?.Style is { } s && (s.Flags & ComputedStyleFlags.DependsOnContainer) != 0)
                {
                    e.MarkDirty(ElementDirtyFlags.Style);
                    any = true;
                }
                // 伪元素的样式随原始元素重算；伪元素用了容器查询时也要重算原始元素
                else if (_generated.Count > 0 && GetGenerated(e) is var (b, a)
                         && ((b?.Render?.Style?.Flags ?? 0) & ComputedStyleFlags.DependsOnContainer | (a?.Render?.Style?.Flags ?? 0) & ComputedStyleFlags.DependsOnContainer) != 0)
                {
                    e.MarkDirty(ElementDirtyFlags.Style);
                    any = true;
                }
                var ch = e.Children;
                for (int i = 0; i < ch.Count; i++) stack.Push(ch[i]);
            }
        }
        changed.Clear();
        return any;
    }

    static bool SameSize(float a, float b) => a.Equals(b) || Math.Abs(a - b) < 0.001f;
}
