using System;
using System.Threading.Tasks;

namespace AwAUI.UI;

/// <summary>
/// 一个滚动容器的滚动状态与命令（design/21「对组件暴露的 API」）。
/// <para>
/// 每个滚动容器（<c>overflow: auto / scroll / hidden</c> 的元素，以及视口即根元素）都有一个控制器：没有指定时由元素按需创建
/// （<see cref="Element.ScrollController"/>）；也可以在节点上指定 <c>new Div { Style = "overflow:auto", ScrollController = controller }</c>
/// 以便在组件中持有它。滚动位置保存在控制器里，跨重建（协调）与重新布局保留，内容变小时钳制到新的范围。
/// </para>
/// <para>
/// 偏移的约定：(0, 0) 为内容的行内起点：LTR 是左上角，RTL 的横向偏移从右边缘量起、向左增大；纵向始终向下增大。
/// 范围 [0, <see cref="MaxOffset"/>]。滚动在 UI 线程上按帧时钟推进（平滑滚动、惯性、回弹都是 <see cref="Simulation"/>）；
/// <see cref="Scrolled"/> 每帧最多触发一次。只能在 UI 线程上使用。
/// </para>
/// </summary>
public sealed class ScrollController
{
    internal Element? _element;

    // 逻辑偏移（越界回弹期间可能超出范围）；用 double 累积，超过 2^24 或亚像素增量时不丢精度
    internal double _x, _y;

    // 最近一次施加到几何上的偏移（按设备像素对齐）；横向是物理平移量（RTL 时为 MaxOffsetX - _x）
    internal double _appliedX, _appliedY;

    // 容器的使用方向（由布局从计算样式写入）：RTL 时 _x 从右边缘量起，物理平移量相反
    internal bool _rtl;

    // 度量（最近一次布局）；double 使很大的内容尺寸仍能区分整数偏移
    internal double _viewportW, _viewportH, _contentW, _contentH;
    internal bool _hasMetrics;

    // 上次 scroll 事件报告的偏移
    internal double _reportedX, _reportedY;
    internal bool _movedSinceEnd;
    internal bool _everAttached;
    internal bool _explicitOffset;

    // 滚动锚定：锚点采样状态。JumpTo / ScrollTo 当帧置 _anchorSuspended，下一次锚定处理时消费并重新采样，
    // 因此程序滚动不会在当帧被锚定校正覆盖掉。
    internal readonly ScrollAnchor _anchor = new();
    internal bool _anchorSuspended;

    // 动画（每个轴各自一段模拟；Task 在结束或被打断时完成）
    internal Simulation? _simX, _simY;
    internal double _simStartX, _simStartY;
    internal TaskCompletionSource<bool>? _animationTask;
    internal bool _animationIsFling;

    // 输入驱动的滚动状态（拖动、滚动条拖动、触控板手势）
    internal bool _dragging;
    internal bool _gesture;
    internal double _gestureEndDeadline = double.NaN;
    /// <summary>手势进行中（还没收到 Ended / Cancelled）时的兜底结束时刻：到时仍没有新的滚轮事件就视为手势结束。</summary>
    internal double _gestureStaleAt = double.PositiveInfinity;

    // 吸附（scroll-snap-*）：用户输入引起的一段运动（惯性、滚轮动画、拖动、触控板手势）结束后尝试一次吸附；
    // 吸附自己的平滑动画结束时它已经为 false，不会反复吸附。
    internal bool _snapEligible;

    // 覆盖式滚动条的显示
    internal double _lastActivity = double.NegativeInfinity;
    internal float _scrollbarOpacity;
    internal int _scrollbarHover; // 0 无，1 纵向，2 横向
    internal int _scrollbarPressed;

    // 滚动条推迟到定位后代之后绘制的层叠结构版本（RenderPipeline.MarkDeferredScrollbars）
    internal long _scrollbarDeferSerial = -1;

    /// <summary>当前滚动偏移（逻辑像素）。这是 <see cref="OffsetX"/> / <see cref="OffsetY"/> 转成单精度的结果。</summary>
    public Point Offset => new((float)_x, (float)_y);

    /// <summary>当前横向滚动偏移（逻辑像素，double）；不经过单精度舍入，适合很长的内容与亚像素累积。</summary>
    public double OffsetX => _x;

    /// <summary>当前纵向滚动偏移（逻辑像素，double）；不经过单精度舍入，适合很长的内容与亚像素累积。</summary>
    public double OffsetY => _y;

    /// <summary>可滚动内容的尺寸（CSS 的可滚动溢出区域：从内边距盒起点量起，含末端外边距与内边距）。尚未布局时为 0。</summary>
    public Size ContentSize => new((float)_contentW, (float)_contentH);

    /// <summary>视口（滚动容器的内边距盒）尺寸。尚未布局时为 0。</summary>
    public Size ViewportSize => new((float)_viewportW, (float)_viewportH);

    /// <summary>最大偏移（内容尺寸 − 视口尺寸，不小于 0）。这是 <see cref="MaxOffsetX"/> / <see cref="MaxOffsetY"/> 转成单精度的结果。</summary>
    public Point MaxOffset => new((float)MaxOffsetX, (float)MaxOffsetY);

    /// <summary>横向最大偏移（double，不经过单精度舍入）。</summary>
    internal double MaxOffsetX => Math.Max(0, _contentW - _viewportW);

    /// <summary>纵向最大偏移（double，不经过单精度舍入）。</summary>
    internal double MaxOffsetY => Math.Max(0, _contentH - _viewportH);

    /// <summary>是否正在滚动：拖动、动画、惯性、触控板手势或滚动条拖动进行中。</summary>
    public bool IsScrolling => _dragging || _gesture || _simX is not null || _simY is not null || _scrollbarPressed != 0;

    /// <summary>附着的元素（滚动容器）；还没有附着时为 null。</summary>
    public Element? Element => _element;

    /// <summary>滚动物理；为 null 时用 <see cref="RenderPipeline.DefaultScrollPhysics"/>。触摸拖动与惯性按它进行。</summary>
    public ScrollPhysics? Physics { get; set; }

    /// <summary>
    /// 滚动锚定（默认开启）：内容在视口内锚点上方插入 / 删除、而偏移没有被用户输入或
    /// <see cref="ScrollTo(Point, ScrollAnimation?)"/> / <see cref="JumpTo(Point)"/> / 滚动条拖动改变时，
    /// 调整本容器的偏移，使锚点保持在视口内的同一位置（钳制到 [0, <see cref="MaxOffset"/>]）。
    /// 拖动、惯性、平滑动画与程序滚动当帧不校正；锚点被移除时重新选择锚点而不是回到 0。嵌套容器各自独立。
    /// 关闭后不校正，也不记录锚点。
    /// </summary>
    public bool ScrollAnchoring { get; set; } = true;

    /// <summary>
    /// 嵌套滚动的联动钩子（design/21「嵌套滚动」）；为 null 时没有联动。滚轮、触控板、触摸拖动 / 惯性、键盘与滚动条翻页的
    /// 滚动命令会在应用前后调用它（<see cref="ScrollSource"/>）；程序滚动与拖动滚动条滑块不调用（见 <see cref="NestedScrollConnection"/>）。
    /// </summary>
    public NestedScrollConnection? NestedConnection { get; set; }

    /// <summary>
    /// 横向的 <c>overscroll-behavior</c>（滚到头后是否传给祖先、是否允许越界效果）。不是 <see cref="OverscrollBehavior.Auto"/> 时优先于
    /// 容器的 CSS <c>overscroll-behavior-x</c>（见 ScrollCss）。
    /// </summary>
    public OverscrollBehavior OverscrollBehaviorX { get; set; }

    /// <summary>纵向的 <c>overscroll-behavior</c>。</summary>
    public OverscrollBehavior OverscrollBehaviorY { get; set; }

    /// <summary>滚动条宽度；不是 <see cref="ScrollbarWidth.Auto"/> 时优先于容器的 CSS <c>scrollbar-width</c>。</summary>
    public ScrollbarWidth ScrollbarWidth { get; set; }

    /// <summary>滚动条滑块颜色（CSS <c>scrollbar-color</c> 的第一个值）；为 null 时用平台默认。</summary>
    public Color? ScrollbarThumbColor { get; set; }

    /// <summary>滚动条轨道颜色（CSS <c>scrollbar-color</c> 的第二个值，只用于经典样式）；为 null 时用平台默认。</summary>
    public Color? ScrollbarTrackColor { get; set; }

    /// <summary>偏移变化（每帧最多一次，在绘制之后触发）。</summary>
    public event Action<ScrollEvent>? Scrolled;

    /// <summary>一次滚动结束（动画、惯性、拖动、触控板手势结束，或立即滚动所在的那一帧之后）。</summary>
    public event Action? ScrollEnd;

    internal ScrollManager? Manager => (_element?.Root?.Pipeline as RenderPipeline)?.Scroll;

    /// <summary>立即跳到 <paramref name="offset"/>（钳制到 [0, MaxOffset]），停止进行中的动画。</summary>
    public void JumpTo(Point offset) => JumpTo(offset.X, offset.Y);

    /// <summary>立即跳到 (<paramref name="x"/>, <paramref name="y"/>)（钳制到 [0, MaxOffset]），停止进行中的动画。</summary>
    public void JumpTo(double x, double y)
    {
        Stop();
        _explicitOffset = true;
        _anchorSuspended = true;
        SetOffset(Clamp(x, 0), Clamp(y, 1));
    }

    /// <summary>
    /// 滚动到 <paramref name="offset"/>（钳制到范围内）。不给动画时按容器的 CSS <c>scroll-behavior</c>：smooth（默认样式表的取值）为 <see cref="ScrollAnimation.Default"/>，auto 为立即。
    /// 返回的任务在动画结束、被 <see cref="Stop"/> / <see cref="JumpTo"/> / 新的滚动打断时完成（结果都是完成，不是取消）。
    /// </summary>
    public Task ScrollTo(Point offset, ScrollAnimation? animation = null) => ScrollTo(offset.X, offset.Y, animation);

    /// <summary><see cref="ScrollTo(Point, ScrollAnimation?)"/> 的 double 版本：目标偏移不经过单精度舍入。</summary>
    public Task ScrollTo(double x, double y, ScrollAnimation? animation = null)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartScrollTo(x, y, animation, tcs);
        return tcs.Task;
    }

    /// <summary><see cref="ScrollTo(double, double, ScrollAnimation?)"/> 的实现：<paramref name="tcs"/> 在动画结束或被打断时完成（立即滚动时立即完成）。</summary>
    internal void StartScrollTo(double x, double y, ScrollAnimation? animation, TaskCompletionSource<bool> tcs)
    {
        animation ??= ScrollCss.DefaultAnimation(this); // CSS scroll-behavior（默认样式表为 smooth）
        _explicitOffset = true;
        _anchorSuspended = true;
        double tx = Clamp(x, 0), ty = Clamp(y, 1);
        var manager = Manager;
        if (animation.Duration <= TimeSpan.Zero || manager is null || !_hasMetrics)
        {
            Stop();
            SetOffset(tx, ty);
            tcs.TrySetResult(true);
            return;
        }
        CompleteAnimation();
        double now = manager.Now;
        _simX = tx != _x ? new SmoothScrollSimulation(_x, tx, animation.Duration, animation.Easing) : null;
        _simY = ty != _y ? new SmoothScrollSimulation(_y, ty, animation.Duration, animation.Easing) : null;
        _simStartX = _simStartY = now;
        _animationIsFling = false;
        if (_simX is null && _simY is null)
        {
            tcs.TrySetResult(true);
            return;
        }
        _animationTask = tcs;
        manager.Activate(this);
    }

    /// <summary>
    /// 把 <paramref name="element"/>（本容器的后代）滚动到可见（只滚动本容器；默认平滑）。在下一帧布局之后执行，任务在滚动结束时完成。
    /// 元素不是本容器的后代时立即完成。
    /// </summary>
    public Task ScrollIntoView(Element element, ScrollAlignment alignment = default)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        var manager = Manager;
        if (manager is null || _element is null || !_element.IsInclusiveAncestorOf(element) || ReferenceEquals(_element, element))
            return Task.CompletedTask;
        return manager.RequestScrollIntoView(this, element, alignment);
    }

    /// <summary>停止进行中的动画、惯性与回弹（越界时停在当前位置，下一帧钳制回范围内）。</summary>
    public void Stop()
    {
        bool had = _simX is not null || _simY is not null;
        _simX = _simY = null;
        CompleteAnimation();
        if (had && (_x < 0 || _y < 0 || (_hasMetrics && (_x > MaxOffsetX || _y > MaxOffsetY))))
            SetOffset(Clamp(_x, 0), Clamp(_y, 1));
    }

    internal void CompleteAnimation()
    {
        var t = _animationTask;
        _animationTask = null;
        t?.TrySetResult(true);
    }

    /// <summary>钳制到 [0, 最大偏移]；还没有度量时只钳制下限。</summary>
    internal double Clamp(double v, int axis)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) v = 0;
        v = Math.Max(0, v);
        if (_hasMetrics) v = Math.Min(v, axis == 0 ? MaxOffsetX : MaxOffsetY);
        return Math.Min(v, 1e7);
    }

    /// <summary>
    /// 横向逻辑偏移对应的几何平移量（会从子元素的位置里减去）：LTR 向左为正，RTL 从右边缘量起、向右为正（取负）。
    /// </summary>
    internal double TranslationX(double x) => _rtl ? -x : x;

    internal void SetOffset(double x, double y)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) x = _x;
        if (double.IsNaN(y) || double.IsInfinity(y)) y = _y;
        if (x == _x && y == _y) return;
        _x = x;
        _y = y;
        _movedSinceEnd = true;
        Manager?.OffsetChanged(this);
    }

    internal ScrollMetrics MetricsFor(int axis) => axis == 0
        ? new ScrollMetrics(_x, 0, MaxOffsetX, _viewportW)
        : new ScrollMetrics(_y, 0, MaxOffsetY, _viewportH);

    internal void RaiseScrolled(ScrollEvent e)
    {
        Scrolled?.Invoke(e);
    }

    internal void RaiseScrollEnd() => ScrollEnd?.Invoke();

    /// <summary>附着到 <paramref name="element"/>（从之前的元素上摘下）。</summary>
    internal void Attach(Element element)
    {
        if (ReferenceEquals(_element, element)) return;
        if (_element is not null && ReferenceEquals(_element._scroll, this)) _element.DetachScrollController(this);
        _element = element;
        _everAttached = true;
    }

    internal void Detach()
    {
        Stop();
        _element = null;
    }

    public override string ToString() =>
        FormattableString.Invariant($"ScrollController(offset {_x:0.##},{_y:0.##} / max {MaxOffsetX:0.##},{MaxOffsetY:0.##})");
}
