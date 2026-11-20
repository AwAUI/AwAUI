using System;
using System.Collections.Generic;
using AwAUI.Css;
using AwAUI.Css.Values;
using AwAUI.Layout;
using AwAUI.Platform;
using AwAUI.Text;

namespace AwAUI.UI;

public sealed partial class RenderPipeline
{
    readonly Stack<Element> _layoutStack = new();
    readonly List<LayoutNode> _desiredChildren = new();
    readonly Stack<(Element Element, int Index, bool Pushed)> _inlineStack = new();

    /// <summary>
    /// 布局阶段：沿 Layout / Paint 脏路径同步布局树（结构、样式映射、段落），然后按需运行布局引擎、对齐像素并算出绝对几何。
    /// 返回重建的段落数。
    /// </summary>
    int LayoutPass(UIRoot root, out bool layoutRan, out bool geometryChanged)
    {
        var rootEl = root.RootElement;
        var rootBox = RenderBox.Get(rootEl);
        rootBox.Kind = BoxKind.Container;
        bool force = _forceParagraphRebuild;
        _forceParagraphRebuild = false;
        int paragraphs = 0;
        if (rootBox.Layout is null || force || ((rootEl.DirtyFlags | rootEl.DescendantDirtyFlags) & (ElementDirtyFlags.Layout | ElementDirtyFlags.Paint)) != 0)
            paragraphs = SyncLayoutTree(rootEl, force);

        layoutRan = false;
        geometryChanged = false;
        var node = rootBox.Layout!;
        if (node.IsDirty || _viewportChanged)
        {
            try
            {
                _layoutEngine.Compute(node, _viewport);
                LayoutEngine.RoundToPixels(node, _scale);
                layoutRan = true;
            }
            catch (InsufficientExecutionStackException ex)
            {
                // 布局引擎在极深的树上抛出而不是让进程栈溢出：保留上一帧的几何，报告错误
                root.ReportDiagnostic(new UIDiagnostic("AW0001", DiagnosticSeverity.Error, "布局失败：" + ex.Message));
            }
        }
        if (layoutRan || _viewportChanged)
        {
            UpdateGeometry(rootEl);
            geometryChanged = true;
        }
        _viewportChanged = false;
        // 滚动：「滚动到可见」与变化了的偏移（没有完整几何遍历时只平移滚动容器的子树）
        ApplyScrollGeometry(fullWalkDone: geometryChanged);
        return paragraphs;
    }

    int SyncLayoutTree(Element rootEl, bool force)
    {
        int paragraphs = 0;
        var stack = _layoutStack;
        stack.Clear();
        stack.Push(rootEl);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            var box = RenderBox.Get(e);
            var flags = e.DirtyFlags | e.DescendantDirtyFlags;
            bool created = false;
            if (box.Layout is null)
            {
                box.Layout = new LayoutNode { Owner = box };
                created = true;
            }
            var node = box.Layout;
            switch (box.Kind)
            {
                case BoxKind.Container:
                {
                    MapLayoutStyle(box, node);
                    if (created || force || (e.DirtyFlags & ElementDirtyFlags.Layout) != 0) SyncChildren(e, node);
                    if (_generated.Count > 0 && (created || force || (e.DirtyFlags & ElementDirtyFlags.Layout) != 0))
                    {
                        var (before, after) = GetGenerated(e);
                        paragraphs += SyncGenerated(before, force) + SyncGenerated(after, force);
                    }
                    var ch = e.Children;
                    for (int i = ch.Count - 1; i >= 0; i--)
                    {
                        var c = ch[i];
                        if (force || c.Render?.Layout is null || ((c.DirtyFlags | c.DescendantDirtyFlags) & (ElementDirtyFlags.Layout | ElementDirtyFlags.Paint)) != 0)
                            stack.Push(c);
                    }
                    break;
                }
                case BoxKind.Replaced:
                {
                    SyncReplaced(box, node, created || force);
                    break;
                }
                case BoxKind.Paragraph:
                case BoxKind.AnonymousText:
                {
                    if (box.Kind == BoxKind.Paragraph) MapLayoutStyle(box, node);
                    else if (!ReferenceEquals(node.Style, LayoutMapper.AnonymousText)) node.Style = LayoutMapper.AnonymousText;
                    bool layoutChange = created || force || (flags & ElementDirtyFlags.Layout) != 0;
                    if (box.Paragraph is null || layoutChange || (flags & ElementDirtyFlags.Paint) != 0)
                    {
                        paragraphs++;
                        var tl = BuildParagraph(box, out bool empty, out float maxFont);
                        if (box.Paragraph is null)
                        {
                            box.Paragraph = new TextParagraph(tl, empty);
                            node.Measure = box.Paragraph;
                        }
                        else
                        {
                            box.Paragraph.Layout = tl;
                            if (box.Paragraph.IsEmpty != empty) layoutChange = true;
                            box.Paragraph.IsEmpty = empty;
                            // 只有颜色等绘制属性变化时，新段落的度量与旧的相同，布局缓存仍然有效
                            if (layoutChange) node.MarkDirty();
                        }
                        box.Paragraph.MaxFontSize = maxFont;
                        // 输入框：field-sizing: fixed（默认）时内容不撑开框宽，content 时随文字增长
                        bool fixedSize = e is InputElement && box.Style?.FieldSizing != CssFieldSizing.Content;
                        if (box.Paragraph.FixedInlineSize != fixedSize)
                        {
                            box.Paragraph.FixedInlineSize = fixedSize;
                            node.MarkDirty();
                        }
                    }
                    break;
                }
            }
            e.ClearDirty(ElementDirtyFlags.Layout);
        }
        return paragraphs;
    }

    /// <summary>
    /// 替换元素：取得（或更新）内容，把固有尺寸作为测量结果、使用的宽高比（CSS <c>aspect-ratio</c> 与固有宽高比合成，
    /// 见 <see cref="ReplacedMeasure"/>）作为 <see cref="LayoutStyle.AspectRatio"/>。
    /// </summary>
    void SyncReplaced(RenderBox box, LayoutNode node, bool force)
    {
        var e = (ReplacedElement)box.Element;
        var content = e.GetContent(this);
        if (!ReferenceEquals(box.Replaced, content))
        {
            box.Replaced = content;
            force = true;
        }
        var measure = e.Measure;
        if (!ReferenceEquals(node.Measure, measure)) node.Measure = measure;
        // width / height 属性是表现提示（经级联成为 CSS 的 width / height / aspect-ratio，见 ElementStyleView.PresentationalHints）。
        // 图片的固有尺寸只来自内容；内联 svg 的 width / height 同时是它的固有尺寸（SVG 根元素的尺寸属性，object-fit 按它放置文档）
        bool sizeChanged = e is SvgElement
            ? measure.Update(content, e.HintWidth, e.HintHeight, box.Style?.AspectRatio ?? default)
            : measure.Update(content, null, null, box.Style?.AspectRatio ?? default);
        if (sizeChanged || force) box.LayoutStyleDirty = true;
        MapLayoutStyle(box, node);
        if (sizeChanged) node.MarkDirty();
    }

    void MapLayoutStyle(RenderBox box, LayoutNode node)
    {
        var s = box.Style;
        if (s is null) return;
        bool scaleSame = box.MappedScale == _scale;
        if (ReferenceEquals(box.MappedStyle, s) && !box.LayoutStyleDirty && scaleSame) return;
        if (box.MappedStyle is null || box.LayoutStyleDirty || !scaleSame || !LayoutMapper.LayoutGroupsSame(box.MappedStyle, s)
            || (!ReferenceEquals(box.MappedStyle.BehaviorGroup, s.BehaviorGroup) && GutterStale(box.Element, box)))
        {
            var mapped = LayoutMapper.Map(s, _scale);
            // 经典滚动条占据的槽（RenderPipeline.Gutter.cs）
            if (box.Kind == BoxKind.Container) mapped = ApplyGutter(box.Element, box, s, mapped);
            // 替换元素：标记 IsReplaced（块级的 auto 宽度是固有宽度、不撑满包含块，CSS 2.1 §10.3.4；绝对定位不拉伸；
            // 网格 normal 对齐按 start），宽高比用测量合成的使用值
            if (box.Kind == BoxKind.Replaced && box.Element is ReplacedElement re)
                mapped = LayoutMapper.ForReplaced(mapped, re.Measure.AspectRatio);
            node.Style = mapped;
        }
        box.MappedStyle = s;
        box.MappedScale = _scale;
        box.LayoutStyleDirty = false;
    }

    /// <summary>容器的子元素决定各自在布局树中的角色，并把布局子节点列表与元素子节点对齐（只在列表真的不同时修改）。</summary>
    void SyncChildren(Element e, LayoutNode node)
    {
        var desired = _desiredChildren;
        desired.Clear();
        // ::before / ::after 是第一个 / 最后一个布局子节点
        var (before, after) = GetGenerated(e);
        AddGeneratedNode(before, desired);
        var ch = e.Children;
        for (int i = 0; i < ch.Count; i++)
        {
            var c = ch[i];
            var cb = RenderBox.Get(c);
            cb.Kind = c.IsTextNode ? BoxKind.AnonymousText
                : c is TextElement or InputElement || string.Equals(c.Tag, "span", StringComparison.Ordinal) ? BoxKind.Paragraph
                : c is ReplacedElement ? BoxKind.Replaced
                : BoxKind.Container;
            cb.Layout ??= new LayoutNode { Owner = cb };
            desired.Add(cb.Layout);
        }
        AddGeneratedNode(after, desired);
        bool same = node.ChildCount == desired.Count;
        for (int i = 0; same && i < desired.Count; i++)
            if (!ReferenceEquals(node.GetChild(i), desired[i])) same = false;
        if (same) return;
        node.ClearChildren();
        foreach (var d in desired)
        {
            d.Parent?.RemoveChild(d);
            node.AddChild(d);
        }
        desired.Clear();
    }

    static void AddGeneratedNode(GeneratedContentElement? g, List<LayoutNode> desired)
    {
        if (g is null) return;
        var gb = RenderBox.Get(g);
        if (gb.Style is null) return;
        gb.Layout ??= new LayoutNode { Owner = gb };
        desired.Add(gb.Layout);
    }

    // ------------------------------------------------------------ 段落

    TextLayout BuildParagraph(RenderBox box, out bool empty, out float maxFontSize)
    {
        var e = box.Element;
        var style = (box.Kind == BoxKind.AnonymousText ? e.Parent?.Render?.Style : box.Style) ?? box.Style ?? ComputedStyle.Initial;
        // 文本装饰从祖先传播（匿名文本的样式就是父元素的样式，从父元素起算）
        _paragraphDecoration = DecorationInfo.Propagated(box.Kind == BoxKind.AnonymousText ? e.Parent : e);
        var ts = _textStyles.Get(style, 1f, _paragraphDecoration);
        _paragraphDecoration = DecorationInfo.Combine(DecorationInfo.Of(style), _paragraphDecoration);
        _inlineDecorations.Clear();
        _inlineShifts.Clear();
        var ps = _textStyles.CreateParagraphStyle(style, ts, singleLine: e is InputElement { IsMultiline: false });
        var builder = TextEngine.CreateParagraphBuilder(ps);
        _maxFontSize = style.FontSize;
        switch (e)
        {
            case TextNodeElement t:
                builder.AddText(t.Text);
                break;
            case InputElement input:
                AddInputText(input, style, ts, builder);
                break;
            case GeneratedContentElement g:
                builder.AddText(g.Text);
                break;
            default:
            {
                var (before, after) = GetGenerated(e);
                AddGeneratedInline(before, builder);
                if (e is TextElement te && te.Content is { Length: > 0 } content) builder.AddText(content);
                AddInlineChildren(e, builder);
                AddGeneratedInline(after, builder);
                break;
            }
        }
        empty = builder.Length == 0;
        maxFontSize = _maxFontSize;
        return builder.Build();
    }

    float _maxFontSize;

    /// <summary>当前段落的基础装饰（段落元素自身的与祖先传播来的）；行内元素在此之上叠加自己的装饰。</summary>
    DecorationInfo _paragraphDecoration;
    readonly Stack<DecorationInfo> _inlineDecorations = new();
    readonly Stack<float> _inlineShifts = new();

    /// <summary>把段落元素的后代（文本节点与 span 等行内元素）按树序追加到段落；元素的计算样式压栈。显式栈，不受嵌套深度影响。</summary>
    void AddInlineChildren(Element paragraph, ParagraphBuilder builder)
    {
        var stack = _inlineStack;
        stack.Clear();
        stack.Push((paragraph, 0, false));
        var decos = _inlineDecorations;
        decos.Clear();
        decos.Push(_paragraphDecoration);
        var shifts = _inlineShifts;
        shifts.Clear();
        shifts.Push(0);
        while (stack.Count > 0)
        {
            var (el, index, pushed) = stack.Pop();
            var ch = el.Children;
            if (index >= ch.Count)
            {
                // 行内元素的 ::after（段落元素自身的由调用方加入）
                if (!ReferenceEquals(el, paragraph) && _generated.Count > 0) AddGeneratedInline(GetGenerated(el).After, builder);
                if (pushed)
                {
                    builder.Pop();
                    decos.Pop();
                    shifts.Pop();
                }
                continue;
            }
            stack.Push((el, index + 1, pushed));
            var c = ch[index];
            var cb = RenderBox.Get(c);
            cb.Kind = BoxKind.Inline;
            cb.HasBox = false;
            c.ClearDirty(ElementDirtyFlags.Layout | ElementDirtyFlags.Paint);
            if (c is TextNodeElement t)
            {
                if (t.Text.Length > 0) builder.AddText(t.Text);
                continue;
            }
            var cs = cb.Style;
            if (cs is not null && cs.Display == CssDisplay.None) continue;
            bool push = cs is not null;
            if (push)
            {
                var outer = decos.Peek();
                // vertical-align（sub / super / 长度）：基线偏移随嵌套累加
                float shift = shifts.Peek() + TextStyleMapper.BaselineShiftOf(cs!, el.Render?.Style);
                builder.PushStyle(_textStyles.Get(cs!, 1f, outer, shift));
                decos.Push(DecorationInfo.Combine(DecorationInfo.Of(cs!), outer));
                shifts.Push(shift);
                if (cs!.FontSize > _maxFontSize) _maxFontSize = cs.FontSize;
            }
            if (_generated.Count > 0) AddGeneratedInline(GetGenerated(c).Before, builder);
            if (c is TextElement inner && inner.Content is { Length: > 0 } innerContent) builder.AddText(innerContent);
            stack.Push((c, 0, push));
        }
    }

    // ------------------------------------------------------------ 绝对几何

    /// <summary>几何遍历的一项：父元素的绝对位置与它所处的滚动平移。</summary>
    readonly struct GeomWork
    {
        public GeomWork(Element element, float px, float py, float ux, float uy, float psx, float psy, float csx, float csy, float asx, float asy)
        {
            Element = element;
            PX = px; PY = py; UX = ux; UY = uy;
            PSX = psx; PSY = psy; CSX = csx; CSY = csy; ASX = asx; ASY = asy;
        }

        public readonly Element Element;

        /// <summary>父元素边框盒的绝对原点（已滚动，对齐 / 未对齐）。</summary>
        public readonly float PX, PY, UX, UY;

        /// <summary>施加于父元素自身的滚动平移。</summary>
        public readonly float PSX, PSY;

        /// <summary>父元素给常规流子元素的平移（父元素的平移 + 父元素自己的滚动偏移）。</summary>
        public readonly float CSX, CSY;

        /// <summary>绝对定位子元素的平移（其包含块，即最近的定位祖先或根，给常规流子元素的平移）。</summary>
        public readonly float ASX, ASY;
    }

    readonly Stack<GeomWork> _geometryStack = new();

    /// <summary>
    /// 布局之后：累加对齐后的边框盒得到绝对矩形（写入 <see cref="Element.BoundingRect"/>），并记录未对齐的内容盒供段落断行。
    /// 滚动容器的偏移（按设备像素对齐）作为平移施加在其内容上：常规流后代随父元素平移，绝对定位后代随其包含块平移，
    /// fixed 不受任何滚动影响（与 CSS 一致：包含块在滚动容器之外的绝对定位元素不随它滚动，也不被它裁剪）。
    /// </summary>
    void UpdateGeometry(Element rootEl)
    {
        var stack = _geometryStack;
        stack.Clear();
        stack.Push(new GeomWork(rootEl, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        WalkGeometry(scrollOnly: false);
    }

    /// <summary>
    /// 几何遍历（显式栈）。<paramref name="scrollOnly"/> 为 true 时是滚动容器子树的平移：布局没有变化，盒只是整体移动——
    /// 墨迹随之平移，位置变化只作废录制缓存而不计入损伤（调用方把容器视口计入损伤），也不更新嵌套滚动容器的度量与偏移
    /// （它们自己的偏移若也变了，由各自的子树遍历处理）。
    /// </summary>
    void WalkGeometry(bool scrollOnly)
    {
        var stack = _geometryStack;
        while (stack.Count > 0)
        {
            var w = stack.Pop();
            var e = w.Element;
            var box = RenderBox.Get(e);
            var node = box.Layout;
            if (node is null || box.Kind == BoxKind.Inline) continue;
            var style = box.Kind == BoxKind.AnonymousText ? e.Parent?.Render?.Style : box.Style;
            if (scrollOnly)
            {
                if (!box.HasBox) continue;
            }
            else
            {
                bool hidden = box.Kind != BoxKind.AnonymousText && style is not null && style.Display == CssDisplay.None;
                if (hidden)
                {
                    HideSubtree(e);
                    continue;
                }
            }
            var pos = box.Kind == BoxKind.AnonymousText || style is null ? CssPosition.Static : style.Position;
            float sx, sy;
            if (pos == CssPosition.Fixed) sx = sy = 0;
            else if (pos == CssPosition.Absolute) { sx = w.ASX; sy = w.ASY; }
            else { sx = w.CSX; sy = w.CSY; }
            var snapped = node.SnappedBorderBox;
            var abs = new Rect(w.PX + w.PSX + snapped.X - sx, w.PY + w.PSY + snapped.Y - sy, snapped.Width, snapped.Height);
            float ux = w.UX + w.PSX + node.X - sx, uy = w.UY + w.PSY + node.Y - sy;
            if (scrollOnly)
            {
                float ddx = abs.X - box.Border.X, ddy = abs.Y - box.Border.Y;
                if (ddx != 0 || ddy != 0)
                {
                    if (box.Ink.Width > 0 || box.Ink.Height > 0) box.Ink = box.Ink.Offset(ddx, ddy);
                }
                else if (pos is CssPosition.Absolute or CssPosition.Fixed)
                {
                    // 不随滚动移动的后代：祖先的墨迹（并集）不能整体平移，本帧完整重算墨迹
                    _scrollInkFull = true;
                }
            }
            var content = node.ContentBox;
            box.Border = abs;
            box.HasBox = true;
            box.ContentX = ux + content.X;
            box.ContentY = uy + content.Y;
            box.ContentWidth = content.Width;
            box.ContentHeight = content.Height;
            e.BoundingRect = abs;
            // 绘制用到的每个对齐结果都要相对边框盒原点不变，缓存才能只平移复用（对齐是按绝对坐标做的）
            var geometry = new PaintGeometry(abs.Width, abs.Height, Snap(box.ContentX) - abs.X, Snap(box.ContentY) - abs.Y, content.Width, content.Height,
                Snap(box.ContentX + content.Width) - abs.X, Snap(box.ContentY + content.Height) - abs.Y,
                e is InputElement ? box.ContentY - abs.Y : 0);
            if (!geometry.Equals(box.Geometry))
            {
                box.Geometry = geometry;
                if (scrollOnly) InvalidateCache(box);
                else OnGeometryChanged(box, self: true);
            }
            float offX = abs.X - w.PX, offY = abs.Y - w.PY;
            if (offX != box.OffsetX || offY != box.OffsetY)
            {
                box.OffsetX = offX;
                box.OffsetY = offY;
                if (!scrollOnly) OnGeometryChanged(box, self: false);
                else if (e.Parent?.Render is { } pb) InvalidateCache(pb);
            }
            if (box.Kind != BoxKind.Container)
            {
                // 段落内部的元素没有自己的盒：报告整个段落的矩形
                if (box.Kind == BoxKind.Paragraph) SetInlineBounds(e, abs);
                continue;
            }
            float scrollX = 0, scrollY = 0;
            int axes = ScrollAxes(e);
            if (axes != 0)
            {
                var c = e.ScrollController;
                if (!scrollOnly)
                {
                    UpdateScrollMetrics(e, node, axes, c);
                    UpdateAutoGutter(e, box, c);
                    float ax = (axes & ScrollContainerX) != 0 ? SnapOffset(c._x) : 0;
                    float ay = (axes & ScrollContainerY) != 0 ? SnapOffset(c._y) : 0;
                    if (ax != c._appliedX || ay != c._appliedY)
                    {
                        // 偏移与布局同帧变化：滑块移动，可见的滚动条计入损伤（内容的移动由子元素的几何变化计入）
                        if (EffectiveScrollbarStyle == ScrollbarStyle.Classic || c._scrollbarOpacity > 0) DamageScrollbarStrips(e);
                        c._appliedX = ax;
                        c._appliedY = ay;
                        LastFrameScrolled = true;
                    }
                }
                scrollX = c._appliedX;
                scrollY = c._appliedY;
            }
            else if (e._scroll is { } stale && !scrollOnly)
            {
                // 不再是滚动容器：偏移保留在控制器里，但不施加
                stale._appliedX = stale._appliedY = 0;
                stale._hasMetrics = false;
            }
            float csx = sx + scrollX, csy = sy + scrollY;
            bool positioned = e.Parent is null || (style is not null && style.Position != CssPosition.Static);
            float asx = positioned ? csx : w.ASX, asy = positioned ? csy : w.ASY;
            var ch = e.Children;
            for (int i = ch.Count - 1; i >= 0; i--) stack.Push(new GeomWork(ch[i], abs.X, abs.Y, ux, uy, sx, sy, csx, csy, asx, asy));
            if (_generated.Count > 0)
            {
                // 伪元素盒：与子元素同样的平移（它们的布局节点是原始元素的子节点）
                var (before, after) = GetGenerated(e);
                if (before?.Render?.Layout is not null) stack.Push(new GeomWork(before, abs.X, abs.Y, ux, uy, sx, sy, csx, csy, asx, asy));
                if (after?.Render?.Layout is not null) stack.Push(new GeomWork(after, abs.X, abs.Y, ux, uy, sx, sy, csx, csy, asx, asy));
            }
        }
    }

    void HideSubtree(Element e)
    {
        var stack = new Stack<Element>();
        stack.Push(e);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            if (x.Render is { } b)
            {
                if (b.HasBox) DamageBox(b);
                b.HasBox = false;
                b.Geometry = default;
            }
            x.BoundingRect = default;
            var ch = x.Children;
            for (int i = 0; i < ch.Count; i++) stack.Push(ch[i]);
            var (before, after) = GetGenerated(x);
            if (before is not null) stack.Push(before);
            if (after is not null) stack.Push(after);
        }
    }

    static void SetInlineBounds(Element paragraph, Rect rect)
    {
        var stack = new Stack<Element>();
        var ch0 = paragraph.Children;
        for (int i = 0; i < ch0.Count; i++) stack.Push(ch0[i]);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            x.BoundingRect = rect;
            var ch = x.Children;
            for (int i = 0; i < ch.Count; i++) stack.Push(ch[i]);
        }
    }
}
