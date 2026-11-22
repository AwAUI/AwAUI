using System;
using System.Collections.Generic;
using AwAUI.Css;
using AwAUI.Css.Values;
using AwAUI.Graphics;
using AwAUI.Layout;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>命中滚动条的结果。</summary>
internal readonly struct ScrollbarHit
{
    public ScrollbarHit(ScrollController controller, int axis, in ScrollbarGeometry geometry)
    {
        Controller = controller;
        Axis = axis;
        Geometry = geometry;
    }

    public ScrollController? Controller { get; }

    /// <summary>0 横向滚动条，1 纵向滚动条。</summary>
    public int Axis { get; }

    public ScrollbarGeometry Geometry { get; }
}

/// <summary>一个滚动容器的滚动条几何（绝对坐标；不显示的部分为空矩形）。</summary>
internal readonly struct ScrollbarGeometry
{
    public ScrollbarGeometry(Rect trackX, Rect thumbX, Rect trackY, Rect thumbY, Rect corner, float thickness)
    {
        TrackX = trackX;
        ThumbX = thumbX;
        TrackY = trackY;
        ThumbY = thumbY;
        Corner = corner;
        Thickness = thickness;
    }

    public Rect TrackX { get; }
    public Rect ThumbX { get; }
    public Rect TrackY { get; }
    public Rect ThumbY { get; }
    public Rect Corner { get; }
    public float Thickness { get; }
    public bool IsEmpty => TrackX.IsEmpty && TrackY.IsEmpty;
}

public sealed partial class RenderPipeline
{
    /// <summary><see cref="ScrollAxes"/> 的位：用户可滚动（滚轮、拖动、键盘）的轴与可程序滚动（滚动容器）的轴。</summary>
    internal const int UserScrollX = 1, UserScrollY = 2, ScrollContainerX = 4, ScrollContainerY = 8;

    ScrollManager? _scrollManager;
    // 滚动锚定：曾经是可纵向滚动容器的控制器（由 UpdateScrollMetrics 登记；失效的在锚定处理时移除）
    readonly List<ScrollController> _anchorCandidates = new();
    bool _scrollRepaint;
    bool _scrollInkFull;
    ScrollbarStyle _scrollbarStyle = ScrollbarStyle.Overlay;
    bool _lastPointerWasTouch;

    internal ScrollManager Scroll => _scrollManager ??= new ScrollManager(this);

    /// <summary>默认的滚动物理（控制器没有指定 <see cref="ScrollController.Physics"/> 时）。默认为 <see cref="DesktopPhysics"/>。</summary>
    public ScrollPhysics DefaultScrollPhysics { get; set; } = new DesktopPhysics();

    /// <summary>触摸拖动所用的物理；为 null 时用 <see cref="DefaultScrollPhysics"/>。移动平台可设为 <see cref="BouncingPhysics"/> / <see cref="ClampingPhysics"/>。</summary>
    public ScrollPhysics? TouchScrollPhysics { get; set; }

    /// <summary>滚轮与键盘滚动是否做平滑动画（默认开启）；关闭时立即跳到目标。</summary>
    public bool SmoothScrolling { get; set; } = true;

    /// <summary>
    /// 滚动条样式（平台偏好，<see cref="UIHost"/> 按 <see cref="WindowImpl.PreferredScrollbarStyle"/> 设置）。最近一次指针输入是触摸时总是用覆盖式。
    /// 经典样式在 v1 中不占布局空间（画在内边距盒内侧，覆盖内容的边缘）。
    /// </summary>
    public ScrollbarStyle ScrollbarStyle
    {
        get => _scrollbarStyle;
        set
        {
            if (_scrollbarStyle == value) return;
            var before = EffectiveScrollbarStyle;
            _scrollbarStyle = value;
            if (before != EffectiveScrollbarStyle) ScrollbarStyleChanged();
        }
    }

    /// <summary>最近一次按下是否来自触摸（决定覆盖式 / 经典滚动条）。</summary>
    internal bool LastPointerWasTouch
    {
        get => _lastPointerWasTouch;
        set
        {
            if (_lastPointerWasTouch == value) return;
            var before = EffectiveScrollbarStyle;
            _lastPointerWasTouch = value;
            if (before != EffectiveScrollbarStyle) ScrollbarStyleChanged();
        }
    }

    /// <summary>实际使用的滚动条样式。</summary>
    public ScrollbarStyle EffectiveScrollbarStyle => _lastPointerWasTouch ? ScrollbarStyle.Overlay : _scrollbarStyle;

    void ScrollbarStyleChanged()
    {
        _scrollbarVisualChanged = true;
        GuttersForStyleChange(); // 经典滚动条占布局空间，覆盖式不占（RenderPipeline.Gutter.cs）
        _cacheEpoch++;
        _damageAll = true;
        _forceRepaint = true;
        _root?.OnElementDirty();
    }

    /// <summary>
    /// 是否需要下一帧来推进 CSS 动画或滚动（平滑滚动、惯性、回弹、滚动条淡出、待报告的 scroll 事件）。<see cref="UIHost"/> 每帧之后检查它；
    /// 自己驱动 <see cref="UIRoot.RunFrame"/> 的宿主也应如此。
    /// </summary>
    public bool NeedsAnimationFrame => HasRunningAnimations || _scrollManager?.NeedsAnimationFrame == true;

    /// <summary>最近一帧是否因滚动改变了元素的位置（不经布局）。宿主据此在指针下重新命中、更新悬停。</summary>
    public bool LastFrameScrolled { get; private set; }

    /// <summary>
    /// 元素作为滚动容器的轴（<see cref="UserScrollX"/> 等位的组合）；不是滚动容器时为 0。
    /// <list type="bullet">
    /// <item>普通容器：<c>overflow-x / overflow-y</c> 至少一个轴不在 { visible, clip } 中；auto / scroll（以及因此计算为 auto 的 visible）可由用户滚动，
    /// hidden（以及因此计算为 hidden 的 clip）只能程序滚动。两轴都在 { visible, clip } 中时不是滚动容器（clip 只裁剪）。
    /// 段落（<c>text</c>、<c>input</c>）与替换元素不是滚动容器。</item>
    /// <item>根元素即视口：visible 按 auto 处理（与浏览器的视口一致）；hidden 与 clip 只能程序滚动。</item>
    /// </list>
    /// </summary>
    internal static int ScrollAxes(Element e)
    {
        var b = e.Render;
        if (b?.Style is not { } s || b.Kind != BoxKind.Container) return 0;
        if (s.Display is CssDisplay.Contents or CssDisplay.None) return 0;
        var ox = s.OverflowX;
        var oy = s.OverflowY;
        // CSS overflow：只有一个轴既不是 visible 也不是 clip 时，另一轴的 visible / clip 才计算为 auto / hidden；
        // 两轴都在 { visible, clip } 中（如 overflow-x: clip + overflow-y: visible）时元素不是滚动容器
        if (e.Parent is not null && ox is CssOverflow.Visible or CssOverflow.Clip && oy is CssOverflow.Visible or CssOverflow.Clip) return 0;
        return AxisBits(ox, UserScrollX, ScrollContainerX) | AxisBits(oy, UserScrollY, ScrollContainerY);
    }

    static int AxisBits(CssOverflow o, int user, int container) => o switch
    {
        CssOverflow.Visible or CssOverflow.Auto or CssOverflow.Scroll => user | container,
        // 另一轴可滚动时 clip 计算为 hidden（CSS overflow）：可程序滚动
        CssOverflow.Hidden or CssOverflow.Clip => container,
        _ => 0,
    };

    /// <summary>把偏移对齐到设备像素（缓存的 DisplayList 平移整数个设备像素后逐像素不变）。</summary>
    internal double SnapOffset(double v) => Math.Round(v * (double)_scale) / _scale;

    /// <summary>滚动容器的视口（绝对坐标）：普通容器为内边距盒，根元素为窗口客户区。</summary>
    internal Rect ScrollViewportRect(Element e)
    {
        if (e.Parent is null) return new Rect(0, 0, _viewport.Width, _viewport.Height);
        var b = e.Render;
        if (b?.Style is not { } s || !b.HasBox) return Rect.Empty;
        var outer = BoxPainter.OuterShape(s, b.Border);
        return BoxPainter.InnerShape(outer, BoxPainter.GetBorderWidths(s, _scale)).Rect;
    }

    // ------------------------------------------------------------ 度量

    /// <summary>布局之后更新滚动容器的视口与内容尺寸；内容变小时把偏移钳制到新的范围（拖动与惯性 / 回弹进行中除外）。</summary>
    void UpdateScrollMetrics(Element e, LayoutNode node, int axes, ScrollController c)
    {
        float vw, vh, cw, ch;
        if (e.Parent is null)
        {
            // 视口：根元素的边框盒（含外边距）与其可滚动溢出
            vw = _viewport.Width;
            vh = _viewport.Height;
            var m = node.Margin;
            var ss = node.ScrollSize;
            float right = node.X + Math.Max(node.Width, node.Border.Left + ss.Width) + m.Right;
            float bottom = node.Y + Math.Max(node.Height, node.Border.Top + ss.Height) + m.Bottom;
            cw = Math.Max(vw, right);
            ch = Math.Max(vh, bottom);
        }
        else
        {
            var pb = node.PaddingBox;
            vw = pb.Width;
            vh = pb.Height;
            var ss = node.ScrollSize;
            cw = Math.Max(vw, ss.Width);
            ch = Math.Max(vh, ss.Height);
            // RTL 的行内末尾在左：向左溢出（InlineStartOverflow < 0）的内容也要计入可滚动宽度
            if (node.Style.Direction == Direction.Rtl)
            {
                float start = node.InlineStartOverflow();
                if (start < 0) cw = Math.Max(vw, ss.Width - start);
            }
        }
        if ((axes & ScrollContainerX) == 0) cw = vw;
        if ((axes & ScrollContainerY) == 0) ch = vh;
        if (!(cw < 1e7f)) cw = 1e7f;
        if (!(ch < 1e7f)) ch = 1e7f;
        bool metricsChanged = !c._hasMetrics || vw != c._viewportW || vh != c._viewportH || cw != c._contentW || ch != c._contentH;
        c._viewportW = vw;
        c._viewportH = vh;
        c._contentW = cw;
        c._contentH = ch;
        c._hasMetrics = true;
        // 滚动锚定：登记为候选，本帧完整几何遍历之后统一采样 / 校正（UpdateScrollMetrics 只在完整遍历里被调用）
        if ((axes & ScrollContainerY) != 0 && !_anchorCandidates.Contains(c)) _anchorCandidates.Add(c);
        // 滑块的长度与位置随内容 / 视口尺寸变化：可见的滚动条所在的条带计入损伤
        if (metricsChanged && (EffectiveScrollbarStyle == ScrollbarStyle.Classic || c._scrollbarOpacity > 0)) DamageScrollbarStrips(e);
        // 拖动与惯性 / 回弹期间由物理决定（可以越界）；平滑滚动的动画不越界，同样钳制（Advance 在后续帧里继续钳制）
        bool free = c._dragging || (c._animationIsFling && (c._simX is not null || c._simY is not null));
        if (free) return;
        double nx = c.Clamp(c._x, 0), ny = c.Clamp(c._y, 1);
        if (nx == c._x && ny == c._y) return;
        c._x = nx;
        c._y = ny;
        c._movedSinceEnd = true;
        Scroll.NoteClamped(c);
    }

    // ------------------------------------------------------------ 滚动几何

    /// <summary>
    /// 布局阶段末尾：执行待处理的「滚动到可见」，再把变化了的滚动偏移施加到几何上。本帧做过完整几何遍历时偏移已经施加；
    /// 否则只遍历各个滚动容器的子树：平移位置与墨迹、作废该容器（及祖先）的录制缓存、把容器视口计入损伤。
    /// </summary>
    void ApplyScrollGeometry(bool fullWalkDone)
    {
        var m = _scrollManager;
        if (m is null) return;
        var dirty = m.TakeGeometryDirty();
        if (fullWalkDone) dirty.Clear();
        m.ProcessReveals();
        if (dirty.Count > 0)
        {
            var list = dirty.ToArray();
            dirty.Clear();
            foreach (var c in list) ScrollSubtree(c);
        }
        // 几何已是最新：采样 / 校正滚动锚定（fullWalkDone 时偏移已在完整遍历中施加，否则由上面的 ScrollSubtree 施加）
        ApplyScrollAnchoring();
    }

    void ScrollSubtree(ScrollController c)
    {
        var e = c._element;
        if (e is null || !e.IsAttached || !ReferenceEquals(e.Root, _root)) return;
        var box = e.Render;
        if (box is null || !box.HasBox || box.Layout is not { } node || box.Kind != BoxKind.Container) return;
        int axes = ScrollAxes(e);
        if (axes == 0) return;
        double nx = (axes & ScrollContainerX) != 0 ? SnapOffset(c.TranslationX(c._x)) : 0;
        double ny = (axes & ScrollContainerY) != 0 ? SnapOffset(c._y) : 0;
        if (nx == c._appliedX && ny == c._appliedY) return;
        double oldX = c._appliedX, oldY = c._appliedY;
        c._appliedX = nx;
        c._appliedY = ny;
        NoteScrollShiftCandidate(c, e, oldX, oldY, nx, ny);
        var style = box.Style!;
        var shift = ShiftOf(e);
        float csx = shift.X + (float)nx, csy = shift.Y + (float)ny;
        Point acs;
        if (e.Parent is null || style.Position != CssPosition.Static) acs = new Point(csx, csy);
        else
        {
            var cb = e.Parent;
            while (cb.Parent is not null && (cb.Render?.Style?.Position ?? CssPosition.Static) == CssPosition.Static) cb = cb.Parent;
            var cbShift = ShiftOf(cb);
            acs = new Point(cbShift.X + AppliedOf(cb).X, cbShift.Y + AppliedOf(cb).Y);
        }
        var stack = _geometryStack;
        stack.Clear();
        float ux = box.ContentX - node.ContentBox.X, uy = box.ContentY - node.ContentBox.Y;
        var ch = e.Children;
        for (int i = ch.Count - 1; i >= 0; i--)
            stack.Push(new GeomWork(ch[i], box.Border.X, box.Border.Y, ux, uy, shift.X, shift.Y, csx, csy, acs.X, acs.Y));
        var (before, after) = GetGenerated(e);
        if (before?.Render?.Layout is not null)
            stack.Push(new GeomWork(before, box.Border.X, box.Border.Y, ux, uy, shift.X, shift.Y, csx, csy, acs.X, acs.Y));
        if (after?.Render?.Layout is not null)
            stack.Push(new GeomWork(after, box.Border.X, box.Border.Y, ux, uy, shift.X, shift.Y, csx, csy, acs.X, acs.Y));
        WalkGeometry(scrollOnly: true);
        RecomputeInkUpward(e);
        InvalidateCache(box);
        // 视口在容器自己的（未变换）空间：经容器与祖先的变换映射到根空间
        AddDamageIn(e, ScrollViewportRect(e).Inflate(1));
        _scrollRepaint = true;
        LastFrameScrolled = true;
    }

    // ------------------------------------------------------------ 滚动锚定

    /// <summary>
    /// 滚动锚定（design/09「滚动」）：对每个可纵向滚动的容器，采样视口内第一个（文档序）可见常规流锚点，
    /// 内容在锚点上方变化而偏移没有被用户输入 / 程序滚动改变时，调整偏移让锚点停在视口内的同一位置。
    /// 每帧只遍历登记的容器（O(容器数)）；只有锚点失效或视口尺寸变化时才重新选择（沿第一条可见路径解析，不做整棵子树遍历）。
    /// </summary>
    void ApplyScrollAnchoring()
    {
        var list = _anchorCandidates;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var c = list[i];
            var e = c._element;
            if (e is null || !e.IsAttached || !ReferenceEquals(e.Root, _root) || e.Render is not { HasBox: true } box
                || box.Kind != BoxKind.Container || (ScrollAxes(e) & ScrollContainerY) == 0)
            {
                c._anchor.Clear();
                list.RemoveAt(i);
                continue;
            }
            ApplyScrollAnchor(c, e);
        }
    }

    void ApplyScrollAnchor(ScrollController c, Element e)
    {
        var anchor = c._anchor;
        bool requested = c._anchorSuspended; // JumpTo / ScrollTo 当帧：重新采样，不校正
        c._anchorSuspended = false;
        if (!c.ScrollAnchoring)
        {
            anchor.Clear();
            return;
        }
        var vp = ScrollViewportRect(e);
        if (vp.IsEmpty || vp.Height <= 0 || vp.Width <= 0)
        {
            anchor.Clear();
            return;
        }
        // 拖动、惯性 / 平滑动画、滚动条拖动期间不校正；只更新采样，之后的内容变化从滚定后的位置量起
        bool suppressed = c._dragging || c._gesture || c._simX is not null || c._simY is not null || c._scrollbarPressed != 0;
        if (!anchor.Usable(e))
        {
            if (requested || suppressed) anchor.Clear();
            else ReselectAnchor(c, e, vp);
            return;
        }
        var a = anchor.Element!;
        double pos = a.BoundingRect.Top - vp.Top;
        if (requested || suppressed)
        {
            anchor.ViewportPos = pos;
            anchor.AppliedOffset = c._appliedY;
            anchor.ViewportSize = c._viewportH;
            return;
        }
        // 视口尺寸变化不校正；偏移不是由内容变化引起（用户输入 / ScrollTo / JumpTo / 滚动条）时也重新采样
        if (anchor.ViewportSize != c._viewportH || c._appliedY != anchor.AppliedOffset)
        {
            ReselectAnchor(c, e, vp);
            return;
        }
        double delta = pos - anchor.ViewportPos;
        if (Math.Abs(delta) < 0.01)
        {
            // 内容相对锚点没有变化；滚动停下来之后锚点若已不在视口内，重新选择下一个可见锚点（不回 0）
            if (!anchor.Intersects(e, vp)) ReselectAnchor(c, e, vp);
            else anchor.ViewportPos = pos;
            return;
        }
        // 内容在锚点上方变化：把偏移调整同样的量并立即施加到几何上（钳制在合法范围）
        double ny = c.Clamp(c._y + delta, 1);
        if (ny != c._y)
        {
            c._y = ny;
            c._movedSinceEnd = true;
            Scroll.NoteClamped(c);
            ScrollSubtree(c);
            pos = a.BoundingRect.Top - vp.Top;
        }
        anchor.ViewportPos = pos;
        anchor.AppliedOffset = c._appliedY;
        anchor.ViewportSize = c._viewportH;
    }

    void ReselectAnchor(ScrollController c, Element container, Rect vp)
    {
        var anchor = c._anchor;
        var a = ScrollAnchor.Select(container, vp);
        anchor.Element = a;
        anchor.Valid = a is not null;
        anchor.ViewportPos = a is null ? 0 : a.BoundingRect.Top - vp.Top;
        anchor.AppliedOffset = c._appliedY;
        anchor.ViewportSize = c._viewportH;
    }

    // ------------------------------------------------------------ 纯滚动帧的像素复用

    /// <summary>本帧唯一变化的滚动容器（偏移按设备像素对齐）。</summary>
    sealed class ScrollShiftCandidate
    {
        public ScrollController Controller = null!;
        public Element Element = null!;
        public double OldX, OldY, NewX, NewY;
    }

    ScrollShiftCandidate? _shiftCandidate;
    bool _shiftMultiple;
    bool _allowScrollShift;
    bool _scrollbarVisualChanged;
    int _shiftCandidateHitIndex = -1;

    /// <summary>
    /// 纯滚动帧的像素复用（默认开启）。只改变一个滚动容器偏移的帧，把帧缓冲里已有的像素按位移搬移（memmove 语义），
    /// 只重画新露出的条带与滚动条；无法证明安全时退回整视口重画（design/18「滚动：位块搬移已有像素 + 重画新露出的条带」）。
    /// 关闭后行为与优化前相同（整视口重画）。
    /// </summary>
    public bool ScrollShiftOptimization { get; set; } = true;

    /// <summary>
    /// 最近一帧可用的帧间位块搬移；null 表示这一帧没有走纯滚动快路径（宿主按 <see cref="LastDamage"/> 完整重画）。
    /// 只在 <see cref="FrameVersion"/> 恰好前进一帧时有效，与 <see cref="LastDamage"/> 一起使用。
    /// </summary>
    public FramebufferShift? LastScrollShift { get; private set; }

    /// <summary>
    /// 纯滚动帧的精确受损区域（设备像素）：新露出的条带与滚动条条带。它们的包围盒是整个视口
    /// （<see cref="LastDamage"/> 报告包围盒），后端应按这些矩形分别重画。与 <see cref="LastScrollShift"/> 同时有效。
    /// </summary>
    public IReadOnlyList<PixelRect>? LastScrollDamage { get; private set; }

    /// <summary>帧开始：丢弃上一帧的纯滚动候选与快路径结果。</summary>
    void ResetScrollShiftState()
    {
        _shiftCandidate = null;
        _shiftMultiple = false;
        _allowScrollShift = false;
        _shiftCandidateHitIndex = -1;
        LastScrollShift = null;
        LastScrollDamage = null;
    }

    void NoteScrollShiftCandidate(ScrollController c, Element e, double oldX, double oldY, double newX, double newY)
    {
        if (_shiftMultiple) return;
        if (_shiftCandidate is null)
        {
            _shiftCandidate = new ScrollShiftCandidate { Controller = c, Element = e, OldX = oldX, OldY = oldY, NewX = newX, NewY = newY };
            return;
        }
        if (ReferenceEquals(_shiftCandidate.Element, e)) return;
        // 同一帧有两个滚动容器的偏移变化：损伤是多个不相连的区域，且一个容器的像素可能落在另一个的搬移区域里
        _shiftMultiple = true;
        _shiftCandidate = null;
    }

    /// <summary>绘制阶段末尾：本帧只滚动了（其余都没有变化）时，计算位块搬移并把损伤缩小到新露出的条带。</summary>
    void TryApplyScrollShift()
    {
        LastScrollShift = null;
        if (!_allowScrollShift || _shiftMultiple || _shiftCandidate is not { } candidate) return;
        if (_damageAll || _backdrops.Count != 0) return;
        // 全局保守检查：只要树里有任何 position: sticky 的元素就不走像素搬移。粘性元素的内容不随容器的像素整体平移
        // （它相对滚动视口固定），把帧缓冲按容器偏移搬走会让它的像素错位；逐元素判断 sticky 的位置成本与收益不成比例。
        if (HasAnyStickyElement()) return;
        if (!TryComputeScrollShift(candidate, out var shift, out var rects)) return;
        PixelRect box = default;
        for (int i = 0; i < rects.Length; i++) box = box.Union(rects[i]);
        var d = box.ToRect();
        var scale = _scale;
        Rect logical = Rect.FromLTRB(d.Left / scale, d.Top / scale, d.Right / scale, d.Bottom / scale).Intersect(_cull);
        if (logical.IsEmpty) return;
        _lastDamage = logical;
        LastScrollShift = shift;
        LastScrollDamage = rects;
    }

    /// <summary>把安全条件逐项验证后算出源矩形、位移与需要重画的条带（设备像素）。</summary>
    bool TryComputeScrollShift(ScrollShiftCandidate cand, out FramebufferShift shift, out PixelRect[] damageRects)
    {
        shift = default;
        damageRects = Array.Empty<PixelRect>();
        var e = cand.Element;
        if (e.Parent is null) return false;
        var b = e.Render;
        if (b is null || !b.HasBox || b.Kind != BoxKind.Container || b.Style is not { } s) return false;
        if (b.ClipEscape || b.HasEffects) return false;
        if (s.Opacity < 1f || s.MixBlendMode != CssBlendMode.Normal) return false;
        if (s.Position is CssPosition.Fixed or CssPosition.Sticky) return false;
        // 容器内有 position: sticky 的后代时不搬移像素：粘性内容不随容器的像素平移（它相对滚动视口固定），
        // 搬移后的像素会与重画的粘性内容错位。保守但线性、便宜。
        if (HasStickyDescendant(e)) return false;
        // 容器自己的背景必须是静止的不透明纯色：搬移的像素里含背景，渐变 / 图片 / 内阴影会跟着内容移动
        if (!s.ResolveColor(s.BackgroundColor).IsOpaque) return false;
        if (!s.BackgroundImage.IsNone || !s.BoxShadow.IsNone) return false;
        var outer = BoxPainter.OuterShape(s, b.Border);
        var inner = BoxPainter.InnerShape(outer, BoxPainter.GetBorderWidths(s, _scale));
        if (!inner.IsRect) return false;
        var vp = ScrollViewportRect(e);
        if (vp.IsEmpty) return false;
        int dx = -(DevOf(cand.NewX) - DevOf(cand.OldX));
        int dy = -(DevOf(cand.NewY) - DevOf(cand.OldY));
        if (dx == 0 && dy == 0) return false;
        // 双轴同时变化：两次搬移的源 / 目的互相交错，损伤也不再是两条边带
        if (dx != 0 && dy != 0) return false;
        var region = vp.Intersect(_cull);
        if (region.IsEmpty) return false;
        // 祖先：不能有变换 / 滤镜 / 不透明度组（那些像素不是内容的纯平移），裁剪必须是轴对齐矩形并与视口求交
        for (var x = e.Parent; x is not null; x = x.Parent)
        {
            var xb = x.Render;
            if (xb is null) continue;
            if (xb.HasEffects) return false;
            if (xb.Style is not { } xs) continue;
            if (xs.Opacity < 1f || xs.MixBlendMode != CssBlendMode.Normal) return false;
            if (x.Parent is not null && ClipsOverflow(xs))
            {
                var ai = BoxPainter.InnerShape(BoxPainter.OuterShape(xs, xb.Border), BoxPainter.GetBorderWidths(xs, _scale));
                if (!ai.IsRect) return false;
                region = region.Intersect(ai.Rect);
                if (region.IsEmpty) return false;
            }
        }
        // 子树里不能有 fixed / sticky（不随内容平移）或逃逸裁剪的后代
        if (!ScrollSubtreeIsShiftable(e)) return false;
        // 容器视口之内、搬移区域之上不能有来自子树之外的内容（滚动条由容器自己重画）
        if (!NothingPaintedOver(e, region)) return false;

        int rx0 = RoundDev(region.Left), ry0 = RoundDev(region.Top);
        int rx1 = RoundDev(region.Right), ry1 = RoundDev(region.Bottom);
        if (rx1 <= rx0 || ry1 <= ry0) return false;
        var regionPx = PixelRect.FromLTRB(rx0, ry0, rx1, ry1);
        // 可见滚动条所在的条带不参与搬移（滑块位置随偏移变化），整条重画
        var g = ScrollbarGeometryFor(e);
        float opacity = EffectiveScrollbarStyle == ScrollbarStyle.Classic ? 1f : cand.Controller._scrollbarOpacity;
        bool showY = opacity > 0 && !g.TrackY.IsEmpty;
        bool showX = opacity > 0 && !g.TrackX.IsEmpty;
        int bx0 = showY ? Math.Max(rx0, RoundDev(vp.Right - g.Thickness)) : rx1;
        int by0 = showX ? Math.Max(ry0, RoundDev(vp.Bottom - g.Thickness)) : ry1;
        int sx1 = showY && bx0 < rx1 ? bx0 : rx1;
        int sy1 = showX && by0 < ry1 ? by0 : ry1;
        if (sx1 <= rx0 || sy1 <= ry0) return false;
        var src0 = new PixelRect(rx0, ry0, sx1 - rx0, sy1 - ry0);
        var dest = new PixelRect(src0.X + dx, src0.Y + dy, src0.Width, src0.Height).Intersect(regionPx);
        if (dest.IsEmpty) return false;
        var src = new PixelRect(dest.X - dx, dest.Y - dy, dest.Width, dest.Height);
        PixelRect strip;
        int ad = Math.Abs(dy != 0 ? dy : dx);
        if (dy != 0)
        {
            if (ad >= src0.Height) return false;
            strip = dy > 0
                ? new PixelRect(src0.X, src0.Y, src0.Width, ad)
                : new PixelRect(src0.X, src0.Bottom - ad, src0.Width, ad);
        }
        else
        {
            if (ad >= src0.Width) return false;
            strip = dx > 0
                ? new PixelRect(src0.X, src0.Y, ad, src0.Height)
                : new PixelRect(src0.Right - ad, src0.Y, ad, src0.Height);
        }
        // 多个矩形：新露出的条带 + 可见滚动条的条带（包围盒是整个视口，必须分开重画）
        var rects = new PixelRect[3];
        int count = 0;
        rects[count++] = strip;
        if (showY && bx0 < rx1) rects[count++] = new PixelRect(bx0, ry0, rx1 - bx0, ry1 - ry0);
        if (showX && by0 < ry1) rects[count++] = new PixelRect(rx0, by0, rx1 - rx0, ry1 - by0);
        if (count == 3) damageRects = rects;
        else
        {
            var trimmed = new PixelRect[count];
            Array.Copy(rects, trimmed, count);
            damageRects = trimmed;
        }
        shift = new FramebufferShift(src, dx, dy);
        return true;
    }

    /// <summary>
    /// 树上是否存在任何 <c>position: sticky</c> 的元素。纯滚动快路径的全局保守检查：粘性元素相对滚动视口固定，
    /// 其像素不随容器整体平移。线性遍历，只在存在纯滚动候选时每帧做一次。
    /// </summary>
    bool HasAnyStickyElement()
    {
        var root = _root?.RootElement;
        if (root is null) return false;
        var stack = new Stack<Element>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.Render?.Style is { Position: CssPosition.Sticky }) return true;
            var children = e.Children;
            for (int i = 0; i < children.Count; i++) stack.Push(children[i]);
        }
        return false;
    }

    /// <summary>容器子树里是否有 <c>position: sticky</c> 的后代（保守：粘性内容不随容器像素平移）。</summary>
    bool HasStickyDescendant(Element container)
    {
        var stack = new Stack<Element>();
        var children = container.Children;
        for (int i = 0; i < children.Count; i++) stack.Push(children[i]);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            if (e.Render?.Style is { Position: CssPosition.Sticky }) return true;
            var gc = e.Children;
            for (int i = 0; i < gc.Count; i++) stack.Push(gc[i]);
        }
        return false;
    }

    /// <summary>子树里没有 fixed / sticky 后代，也没有逃逸裁剪链的层叠上下文（它们不随内容整体平移）。</summary>
    bool ScrollSubtreeIsShiftable(Element root)
    {
        var stack = new Stack<Element>();
        var ch = root.Children;
        for (int i = 0; i < ch.Count; i++) stack.Push(ch[i]);
        while (stack.Count > 0)
        {
            var x = stack.Pop();
            var b = x.Render;
            if (b is null || !b.HasBox || b.Style is not { } s) continue;
            if (s.Position is CssPosition.Fixed or CssPosition.Sticky) return false;
            if (b.ClipEscape) return false;
            var gc = x.Children;
            for (int i = 0; i < gc.Count; i++) stack.Push(gc[i]);
            var (before, after) = GetGenerated(x);
            if (before is not null) stack.Push(before);
            if (after is not null) stack.Push(after);
        }
        return true;
    }

    /// <summary>绘制顺序中容器之后、且不属于它的内容不能与搬移区域相交（否则那些像素会被错误地搬走或覆盖）。</summary>
    bool NothingPaintedOver(Element container, Rect region)
    {
        if (_shiftCandidateHitIndex < 0) return false;
        for (int i = _shiftCandidateHitIndex + 1; i < _hits.Count; i++)
        {
            var h = _hits[i];
            if (IsInSubtree(h.Element, container)) continue;
            // 变换 / clip-path 上下文里的命中项坐标不在根空间：保守退回
            if (h.Space is not null) return false;
            if (!h.Shape.Rect.Intersects(region)) continue;
            if (!h.Clip.Intersects(region)) continue;
            return false;
        }
        return true;
    }

    static bool IsInSubtree(Element e, Element root)
    {
        for (var x = e; x is not null; x = x.Parent)
            if (ReferenceEquals(x, root)) return true;
        return false;
    }

    int RoundDev(float v) => (int)MathF.Round(v * _scale);

    /// <summary>已施加的滚动偏移（逻辑像素，设备像素对齐）对应的设备像素。</summary>
    int DevOf(double applied) => (int)MathF.Round((float)(applied * _scale));

    /// <summary>当前施加于元素边框盒的滚动平移（沿包含块链累加滚动祖先的已施加偏移；fixed 不受任何滚动影响）。</summary>
    static Point ShiftOf(Element e)
    {
        float x = 0, y = 0;
        var cur = e;
        for (int guard = 0; guard < 1_000_000; guard++)
        {
            var b = cur.Render;
            var pos = b is null || b.Kind == BoxKind.AnonymousText || b.Style is null ? CssPosition.Static : b.Style.Position;
            if (pos == CssPosition.Fixed) break;
            var next = cur.Parent;
            if (pos == CssPosition.Absolute)
                while (next is not null && next.Parent is not null && (next.Render?.Style?.Position ?? CssPosition.Static) == CssPosition.Static) next = next.Parent;
            if (next is null) break;
            var a = AppliedOf(next);
            x += a.X;
            y += a.Y;
            cur = next;
        }
        return new Point(x, y);
    }

    static Point AppliedOf(Element e) => e._scroll is { } c ? new Point((float)c._appliedX, (float)c._appliedY) : default;

    /// <summary>子树平移之后，容器自身与祖先的墨迹（后代墨迹之并）重新合并。</summary>
    void RecomputeInkUpward(Element e)
    {
        for (var x = e; x is not null; x = x.Parent)
        {
            var b = x.Render;
            if (b is null || !b.HasBox || b.Kind != BoxKind.Container) break;
            FinishInk(b, UnionChildInk(x, OwnInk(b)));
        }
    }

    // ------------------------------------------------------------ 滚动条

    internal ScrollbarGeometry ScrollbarGeometryFor(Element e)
    {
        if (e._scroll is not { _hasMetrics: true } c) return default;
        int axes = ScrollAxes(e);
        var width = ScrollCss.Width(c); // scrollbar-width（控制器显式设置的优先）
        if ((axes & (UserScrollX | UserScrollY)) == 0 || width == ScrollbarWidth.None) return default;
        var s = e.Render?.Style;
        if (s is null) return default;
        var vp = ScrollViewportRect(e);
        if (vp.IsEmpty) return default;
        bool classic = EffectiveScrollbarStyle == ScrollbarStyle.Classic;
        bool thin = width == ScrollbarWidth.Thin;
        float t = classic ? (thin ? 8f : 12f) : (thin ? 6f : (c._scrollbarHover != 0 || c._scrollbarPressed != 0 ? 11f : 8f));
        bool showY = (axes & UserScrollY) != 0 && (c._contentH > c._viewportH + 0.5f || (classic && s.OverflowY == CssOverflow.Scroll));
        bool showX = (axes & UserScrollX) != 0 && (c._contentW > c._viewportW + 0.5f || (classic && s.OverflowX == CssOverflow.Scroll));
        if (vp.Width < 2 * t) showY = false;
        if (vp.Height < 2 * t) showX = false;
        if (!showX && !showY) return default;
        Rect trackY = default, thumbY = default, trackX = default, thumbX = default, corner = default;
        if (showY)
        {
            trackY = new Rect(vp.Right - t, vp.Top, t, vp.Height - (showX ? t : 0));
            thumbY = Thumb(trackY.Top, trackY.Height, (float)c._viewportH, (float)c._contentH, (float)c._appliedY, c.MaxOffset.Y, out float pos, out float len)
                ? new Rect(trackY.X, pos, t, len) : default;
        }
        if (showX)
        {
            trackX = new Rect(vp.Left, vp.Bottom - t, vp.Width - (showY ? t : 0), t);
            // RTL 的滑块从右端开始：物理位置取「从内容左边缘量起的偏移」
            double offsetX = c._rtl ? c.MaxOffsetX - c._x : c._appliedX;
            thumbX = Thumb(trackX.Left, trackX.Width, (float)c._viewportW, (float)c._contentW, (float)offsetX, c.MaxOffset.X, out float pos, out float len)
                ? new Rect(pos, trackX.Y, len, t) : default;
        }
        if (showX && showY) corner = new Rect(vp.Right - t, vp.Bottom - t, t, t);
        return new ScrollbarGeometry(trackX, thumbX, trackY, thumbY, corner, t);
    }

    /// <summary>
    /// 滑块的位置与长度：长度按视口 / 内容的比例（不小于 20 像素），位置按偏移在 [0, max] 中的比例。
    /// 偏移用已施加到几何上的值（对齐设备像素）：滑块与内容在同一帧、同一个缓存失效条件下变化。
    /// </summary>
    static bool Thumb(float start, float trackLen, float viewport, float content, float offset, float max, out float pos, out float len)
    {
        pos = len = 0;
        if (max <= 0 || content <= 0 || trackLen <= 0) return false;
        len = Math.Min(trackLen, Math.Max(20f, trackLen * viewport / content));
        float f = Math.Max(0, Math.Min(1, offset / max));
        pos = start + (trackLen - len) * f;
        return len > 0;
    }

    /// <summary>
    /// 在元素的轮廓之前画滚动条（层叠上下文在其全部层叠项之后），并登记命中项（经典样式总是，覆盖式只在可见时）。
    /// 滚动条受祖先的裁剪，但不受容器自己的溢出裁剪。
    /// </summary>
    void PaintScrollbars(RenderBox b)
    {
        var e = b.Element;
        if (e._scroll is not { _hasMetrics: true } c) return;
        // 有定位后代由外层层叠上下文绘制：滚动条推迟到它们之后（见 PaintDeferredScrollbars）
        if (c._scrollbarDeferSerial == _layeringSerial) return;
        PaintScrollbarsNow(b, c);
    }

    /// <summary>步数上限：标记推迟的滚动条时沿祖先链走的总步数（极深的树退化为不推迟）。</summary>
    const int MaxScrollbarDeferSteps = 1_000_000;

    /// <summary>
    /// 层叠上下文 <paramref name="ctx"/> 的 0 / auto 层叠项（定位元素、子上下文）中，凡是位于某个滚动容器之内（且该容器不是层叠上下文、
    /// 属于 <paramref name="ctx"/>）的，该容器的滚动条要等这些项画完再画——否则定位的内容会盖住滚动条。
    /// z 为正的后代仍画在滚动条之上（与浏览器的细节不同，见 design/21）。
    /// </summary>
    void MarkDeferredScrollbars(RenderBox ctx, List<RenderBox> zero)
    {
        long steps = 0;
        foreach (var item in zero)
        {
            for (var p = item.Element.Parent; p is not null && !ReferenceEquals(p, ctx.Element); p = p.Parent)
            {
                if (++steps > MaxScrollbarDeferSteps) return;
                if (p._scroll is { } c && (ScrollAxes(p) & (UserScrollX | UserScrollY)) != 0) c._scrollbarDeferSerial = _layeringSerial;
            }
        }
    }

    /// <summary>画完 0 / auto 层叠项 <paramref name="index"/> 之后：它所在的、已推迟的滚动容器若再没有后续的层叠项，就在这里画滚动条。</summary>
    void PaintDeferredScrollbars(RenderBox ctx, List<RenderBox> zero, int index)
    {
        var next = index + 1 < zero.Count ? zero[index + 1].Element : null;
        int guard = 0;
        for (var p = zero[index].Element.Parent; p is not null && !ReferenceEquals(p, ctx.Element); p = p.Parent)
        {
            if (++guard > 100_000) return;
            if (p._scroll is not { } c || c._scrollbarDeferSerial != _layeringSerial) continue;
            if (next is not null && p.IsInclusiveAncestorOf(next)) continue;
            if (p.Render is not { HasBox: true, Style: not null } pb || Culled(pb)) continue;
            var savedClip = _hitClip;
            var savedShape = _hitClipShape;
            _dc?.Save();
            ApplyClipChain(pb);
            PaintScrollbarsNow(pb, c);
            _dc?.Restore();
            _hitClip = savedClip;
            _hitClipShape = savedShape;
        }
    }

    void PaintScrollbarsNow(RenderBox b, ScrollController c)
    {
        var e = b.Element;
        var s = b.Style!;
        if (s.Visibility != CssVisibility.Visible || s.Display == CssDisplay.Contents || _suppress != 0) return;
        bool classic = EffectiveScrollbarStyle == ScrollbarStyle.Classic;
        float opacity = classic ? 1 : c._scrollbarOpacity;
        if (opacity <= 0) return;
        var g = ScrollbarGeometryFor(e);
        if (g.IsEmpty) return;
        if (_dc is { } dc)
        {
            // 圆角容器：滚动条裁剪到内边距盒的圆角（与浏览器一致）
            var inner = e.Parent is null ? default : BoxPainter.InnerShape(BoxPainter.OuterShape(s, b.Border), BoxPainter.GetBorderWidths(s, _scale));
            bool rounded = e.Parent is not null && !inner.IsRect;
            if (rounded)
            {
                dc.Save();
                dc.ClipRoundRect(inner);
            }
            if (classic)
            {
                var track = ScrollCss.TrackColor(c) ?? Color.FromRgb(0xF1, 0xF1, 0xF1);
                if (!g.TrackY.IsEmpty) dc.FillRect(g.TrackY, track);
                if (!g.TrackX.IsEmpty) dc.FillRect(g.TrackX, track);
                if (!g.Corner.IsEmpty) dc.FillRect(g.Corner, track);
                PaintClassicThumb(dc, c, g.ThumbY, 2, g.Thickness);
                PaintClassicThumb(dc, c, g.ThumbX, 1, g.Thickness);
            }
            else
            {
                PaintOverlayThumb(dc, c, g.ThumbY, 2, opacity);
                PaintOverlayThumb(dc, c, g.ThumbX, 1, opacity);
            }
            if (rounded) dc.Restore();
        }
        if (!g.TrackY.IsEmpty) _hits.Add(new HitEntry(e, new RoundRect(g.TrackY, 0), _hitClip, _hitClipShape, false, _hitSpace, 2));
        if (!g.TrackX.IsEmpty) _hits.Add(new HitEntry(e, new RoundRect(g.TrackX, 0), _hitClip, _hitClipShape, false, _hitSpace, 1));
    }

    static void PaintClassicThumb(DrawingContext dc, ScrollController c, Rect thumb, int part, float thickness)
    {
        if (thumb.IsEmpty) return;
        var color = ScrollCss.ThumbColor(c)
                    ?? (c._scrollbarPressed == part ? Color.FromRgb(0x78, 0x78, 0x78)
                        : c._scrollbarHover == part ? Color.FromRgb(0xA8, 0xA8, 0xA8) : Color.FromRgb(0xC1, 0xC1, 0xC1));
        float inset = thickness >= 10 ? 2 : 1;
        var r = part == 2 ? new Rect(thumb.X + inset, thumb.Y + inset, thumb.Width - 2 * inset, thumb.Height - 2 * inset)
            : new Rect(thumb.X + inset, thumb.Y + inset, thumb.Width - 2 * inset, thumb.Height - 2 * inset);
        if (r.Width <= 0 || r.Height <= 0) return;
        dc.FillRoundRect(new RoundRect(r, Math.Min(r.Width, r.Height) / 2), color);
    }

    static void PaintOverlayThumb(DrawingContext dc, ScrollController c, Rect thumb, int part, float opacity)
    {
        if (thumb.IsEmpty) return;
        const float inset = 2;
        var r = new Rect(thumb.X + inset, thumb.Y + inset, thumb.Width - 2 * inset, thumb.Height - 2 * inset);
        if (r.Width <= 0 || r.Height <= 0) return;
        var baseColor = ScrollCss.ThumbColor(c) ?? new Color(0, 0, 0, c._scrollbarHover == part || c._scrollbarPressed == part ? 0.55f : 0.42f);
        dc.FillRoundRect(new RoundRect(r, Math.Min(r.Width, r.Height) / 2), baseColor.WithAlpha(baseColor.A * opacity));
    }

    /// <summary>滚动条外观变化（悬停、按下、覆盖式的不透明度）：作废容器（及祖先）的录制缓存，损伤为滚动条区域，请求一帧。</summary>
    internal void InvalidateScrollbars(ScrollController c)
    {
        if (c._element is not { IsAttached: true } e || !ReferenceEquals(e.Root, _root) || e.Render is not { HasBox: true }) return;
        DamageScrollbarStrips(e);
        _scrollbarVisualChanged = true;
        _scrollRepaint = true;
        _root?.OnElementDirty();
    }

    /// <summary>容器视口右侧与底部可能画滚动条的条带（按最粗的样式）计入损伤，并作废容器（及祖先）的录制缓存。</summary>
    void DamageScrollbarStrips(Element e)
    {
        if (e.Render is not { HasBox: true } b) return;
        var vp = ScrollViewportRect(e);
        if (vp.IsEmpty) return;
        AddDamageIn(e, Rect.FromLTRB(vp.Right - 13, vp.Top, vp.Right, vp.Bottom).Inflate(1));
        AddDamageIn(e, Rect.FromLTRB(vp.Left, vp.Bottom - 13, vp.Right, vp.Bottom).Inflate(1));
        InvalidateCache(b);
    }

    /// <summary>指针处最上层的命中项若是某个滚动容器的滚动条，返回它；否则返回空结果（内容在滚动条之上，或没有命中）。</summary>
    internal ScrollbarHit HitTestScrollbar(Point p)
    {
        _hitQuery++;
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            var h = _hits[i];
            if (h.PointerEventsNone) continue;
            if (!HitsEntry(h, p)) continue;
            if (!h.Element.IsAttached || !ReferenceEquals(h.Element.Root, _root)) continue;
            if (h.ScrollbarAxis == 0 || h.Element._scroll is not { } c) return default;
            return new ScrollbarHit(c, h.ScrollbarAxis - 1, ScrollbarGeometryFor(h.Element));
        }
        return default;
    }
}
