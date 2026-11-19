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
/// 偏移的约定：(0, 0) 为内容的起点，向下 / 向右滚动时增大，范围 [0, <see cref="MaxOffset"/>]。滚动在 UI 线程上按帧时钟推进
/// （平滑滚动、惯性、回弹都是 <see cref="Simulation"/>）；<see cref="Scrolled"/> 每帧最多触发一次。只能在 UI 线程上使用。
/// </para>
/// </summary>
public sealed class ScrollController
{
    internal Element? _element;

    // 逻辑偏移（越界回弹期间可能超出范围）
    internal float _x, _y;

    // 最近一次施加到几何上的偏移（按设备像素对齐）
    internal float _appliedX, _appliedY;

    // 度量（最近一次布局）
    internal float _viewportW, _viewportH, _contentW, _contentH;
    internal bool _hasMetrics;

    // 上次 scroll 事件报告的偏移
    internal float _reportedX, _reportedY;
    internal bool _movedSinceEnd;
    internal bool _everAttached;
    internal bool _explicitOffset;

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

    // 覆盖式滚动条的显示
    internal double _lastActivity = double.NegativeInfinity;
    internal float _scrollbarOpacity;
    internal int _scrollbarHover; // 0 无，1 纵向，2 横向
    internal int _scrollbarPressed;

    // 滚动条推迟到定位后代之后绘制的层叠结构版本（RenderPipeline.MarkDeferredScrollbars）
    internal long _scrollbarDeferSerial = -1;

    /// <summary>当前滚动偏移（逻辑像素）。</summary>
    public Point Offset => new(_x, _y);

    /// <summary>可滚动内容的尺寸（CSS 的可滚动溢出区域：从内边距盒起点量起，含末端外边距与内边距）。尚未布局时为 0。</summary>
    public Size ContentSize => new(_contentW, _contentH);

    /// <summary>视口（滚动容器的内边距盒）尺寸。尚未布局时为 0。</summary>
    public Size ViewportSize => new(_viewportW, _viewportH);

    /// <summary>最大偏移（内容尺寸 − 视口尺寸，不小于 0）。</summary>
    public Point MaxOffset => new(Math.Max(0, _contentW - _viewportW), Math.Max(0, _contentH - _viewportH));

    /// <summary>是否正在滚动：拖动、动画、惯性、触控板手势或滚动条拖动进行中。</summary>
    public bool IsScrolling => _dragging || _gesture || _simX is not null || _simY is not null || _scrollbarPressed != 0;

    /// <summary>附着的元素（滚动容器）；还没有附着时为 null。</summary>
    public Element? Element => _element;

    /// <summary>滚动物理；为 null 时用 <see cref="RenderPipeline.DefaultScrollPhysics"/>。触摸拖动与惯性按它进行。</summary>
    public ScrollPhysics? Physics { get; set; }

    /// <summary>
    /// 横向的 <c>overscroll-behavior</c>（滚到头后是否传给祖先、是否允许越界效果）。CSS 属性尚未实现，目前只能在这里设置。
    /// </summary>
    public OverscrollBehavior OverscrollBehaviorX { get; set; }

    /// <summary>纵向的 <c>overscroll-behavior</c>。</summary>
    public OverscrollBehavior OverscrollBehaviorY { get; set; }

    /// <summary>滚动条宽度（CSS <c>scrollbar-width</c> 尚未实现，目前在这里设置）。</summary>
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
    public void JumpTo(Point offset)
    {
        Stop();
        _explicitOffset = true;
        SetOffset(Clamp(offset.X, 0), Clamp(offset.Y, 1));
    }

    /// <summary>
    /// 滚动到 <paramref name="offset"/>（钳制到范围内）。默认平滑滚动（<see cref="ScrollAnimation.Default"/>）。
    /// 返回的任务在动画结束、被 <see cref="Stop"/> / <see cref="JumpTo"/> / 新的滚动打断时完成（结果都是完成，不是取消）。
    /// </summary>
    public Task ScrollTo(Point offset, ScrollAnimation? animation = null)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartScrollTo(offset, animation, tcs);
        return tcs.Task;
    }

    /// <summary><see cref="ScrollTo"/> 的实现：<paramref name="tcs"/> 在动画结束或被打断时完成（立即滚动时立即完成）。</summary>
    internal void StartScrollTo(Point offset, ScrollAnimation? animation, TaskCompletionSource<bool> tcs)
    {
        animation ??= ScrollAnimation.Default;
        _explicitOffset = true;
        float tx = Clamp(offset.X, 0), ty = Clamp(offset.Y, 1);
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
        if (had && (_x < 0 || _y < 0 || (_hasMetrics && (_x > MaxOffset.X || _y > MaxOffset.Y))))
            SetOffset(Clamp(_x, 0), Clamp(_y, 1));
    }

    internal void CompleteAnimation()
    {
        var t = _animationTask;
        _animationTask = null;
        t?.TrySetResult(true);
    }

    /// <summary>钳制到 [0, 最大偏移]；还没有度量时只钳制下限。</summary>
    internal float Clamp(float v, int axis)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) v = 0;
        v = Math.Max(0, v);
        if (_hasMetrics) v = Math.Min(v, axis == 0 ? MaxOffset.X : MaxOffset.Y);
        return Math.Min(v, 1e7f);
    }

    internal void SetOffset(float x, float y)
    {
        if (float.IsNaN(x) || float.IsInfinity(x)) x = _x;
        if (float.IsNaN(y) || float.IsInfinity(y)) y = _y;
        if (x == _x && y == _y) return;
        _x = x;
        _y = y;
        _movedSinceEnd = true;
        Manager?.OffsetChanged(this);
    }

    internal ScrollMetrics MetricsFor(int axis) => axis == 0
        ? new ScrollMetrics(_x, 0, MaxOffset.X, _viewportW)
        : new ScrollMetrics(_y, 0, MaxOffset.Y, _viewportH);

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
        FormattableString.Invariant($"ScrollController(offset {_x:0.##},{_y:0.##} / max {MaxOffset.X:0.##},{MaxOffset.Y:0.##})");
}
