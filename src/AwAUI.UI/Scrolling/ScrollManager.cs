using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// 滚动系统的运行时（每个 <see cref="RenderPipeline"/> 一个）：按帧推进动画与模拟、汇总每帧的 scroll 事件、
/// 把滚轮 / 键盘 / 触摸拖动 / 滚动条操作路由到滚动容器（design/21「嵌套滚动」「滚轮、触控板、键盘」）。
/// 偏移变化只记录在控制器上，几何在下一帧的布局阶段之后施加（<see cref="RenderPipeline"/> 的滚动几何）。
/// </summary>
internal sealed class ScrollManager
{
    readonly RenderPipeline _p;
    readonly List<ScrollController> _active = new();
    readonly List<ScrollController> _geometryDirty = new();
    readonly List<ScrollController> _changed = new();
    readonly List<RevealRequest> _reveals = new();
    UIRoot? _root;
    IDisposable? _wakeTimer;
    double _wakeTimerDue = double.PositiveInfinity;

    /// <summary>键盘方向键一次滚动的距离（逻辑像素）。</summary>
    public const float KeyLineStep = 40f;

    /// <summary>覆盖式滚动条在最后一次活动之后保持可见的时间（秒）与淡出时长（秒）。</summary>
    public const double OverlayHoldSeconds = 1.0, OverlayFadeSeconds = 0.3;

    /// <summary>没有相位信息的精确滚动（逐事件跟随）在最后一个事件之后多久算结束（秒）。</summary>
    public const double GestureEndDelaySeconds = 0.1;

    /// <summary>
    /// 有相位的触控板手势在最后一个滚轮事件之后多久仍没有 Ended / Cancelled 就强制结束（秒）：窗口在手势中途失去焦点、
    /// 平台丢了结束事件时，控制器不会一直处于 <see cref="ScrollController.IsScrolling"/>（覆盖式滚动条不淡出、ScrollEnd 不触发）。
    /// 手指停在触控板上不动时平台也不发事件，所以之后继续滑动会开始一段新的「滚动中」（仍锁定在同一个容器上）。
    /// 由时钟定时器唤醒；没有时钟时随帧推进的时间判定，不为它单独请求帧。
    /// </summary>
    public const double GestureTimeoutSeconds = 0.3;

    /// <summary>
    /// 一段模拟最多推进的时间（秒）：自定义的 <see cref="ScrollPhysics"/> 给出永不结束的模拟（例如无阻尼的弹簧）时，到时强制结束，
    /// 不会无限地请求动画帧。内置物理的模拟都远短于它。
    /// </summary>
    public const double MaxSimulationSeconds = 30;

    public ScrollManager(RenderPipeline pipeline) => _p = pipeline;

    internal void Attach(UIRoot root) => _root = root;

    /// <summary>没有时钟时每帧推进的时间（秒）。</summary>
    const double FrameSeconds = 1.0 / 60;

    double _frameTime;

    /// <summary>
    /// 当前时间（秒）：<see cref="UIRoot.Clock"/>（测试用 <see cref="ManualUIClock"/>）。没有时钟（无窗口的确定性渲染）时按帧推进，
    /// 每帧 1/60 秒，不读墙上时间：同样的输入与帧序列总是给出同样的结果。
    /// </summary>
    public double Now => _root?.Clock is { } clock ? clock.Now.TotalSeconds : _frameTime;

    // ------------------------------------------------------------ 状态变化

    /// <summary>偏移变了：下一帧施加几何、报告 scroll 事件，覆盖式滚动条显示。</summary>
    public void OffsetChanged(ScrollController c)
    {
        if (!_geometryDirty.Contains(c)) _geometryDirty.Add(c);
        if (!_changed.Contains(c)) _changed.Add(c);
        c._lastActivity = Now;
        Activate(c);
    }

    /// <summary>布局后内容变小、偏移被钳制：报告 scroll（几何已在本次遍历中施加）。</summary>
    internal void NoteClamped(ScrollController c)
    {
        if (!_changed.Contains(c)) _changed.Add(c);
    }

    /// <summary>控制器有进行中的动画、手势或滚动条淡出：每帧推进它。</summary>
    public void Activate(ScrollController c)
    {
        if (!_active.Contains(c)) _active.Add(c);
        RequestFrame();
    }

    void RequestFrame() => _root?.OnElementDirty();

    internal bool HasGeometryWork => _geometryDirty.Count > 0 || _reveals.Count > 0;

    /// <summary>需要下一帧：有动画 / 模拟、手势结束的等待、滚动条淡出，或待施加的几何与待报告的事件。</summary>
    public bool NeedsAnimationFrame
    {
        get
        {
            if (_geometryDirty.Count > 0 || _reveals.Count > 0 || _changed.Count > 0) return true;
            double now = Now;
            bool overlay = _p.EffectiveScrollbarStyle == ScrollbarStyle.Overlay;
            bool clock = _root?.Clock is not null;
            foreach (var c in _active)
            {
                if (c._simX is not null || c._simY is not null) return true;
                if (c._gesture && !double.IsNaN(c._gestureEndDeadline)) return true;
                if (overlay && c._scrollbarOpacity > 0 && !c.IsScrolling && c._scrollbarHover == 0 && c._scrollbarPressed == 0)
                {
                    // 保持可见期间由时钟定时器唤醒；没有时钟时只能逐帧推进
                    if (!clock || now - c._lastActivity >= OverlayHoldSeconds) return true;
                }
            }
            return false;
        }
    }

    internal List<ScrollController> TakeGeometryDirty() => _geometryDirty;

    // ------------------------------------------------------------ 帧

    /// <summary>帧开始（样式之前）：推进动画与模拟，结束到期的手势，更新覆盖式滚动条的不透明度。</summary>
    public void BeginFrame()
    {
        if (_root?.Clock is null) _frameTime += FrameSeconds;
        if (_active.Count == 0) return;
        double now = Now;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var c = _active[i];
            if (c._element is not { IsAttached: true })
            {
                c._simX = c._simY = null;
                c.CompleteAnimation();
                c._gesture = c._dragging = false;
                c._scrollbarPressed = 0;
                _active.RemoveAt(i);
                continue;
            }
            Advance(c, now);
            UpdateScrollbarOpacity(c, now);
            if (c._simX is null && c._simY is null && !c._gesture && !c._dragging && c._scrollbarPressed == 0
                && c._scrollbarOpacity <= 0 && c._scrollbarHover == 0)
                _active.RemoveAt(i);
        }
    }

    void Advance(ScrollController c, double now)
    {
        double x = c._x, y = c._y;
        bool hadSimulation = c._simX is not null || c._simY is not null;
        if (c._simX is { } sx)
        {
            double t = now - c._simStartX;
            x = sx.Position(t);
            if (sx.IsDone(t) || t > MaxSimulationSeconds) c._simX = null;
        }
        if (c._simY is { } sy)
        {
            double t = now - c._simStartY;
            y = sy.Position(t);
            if (sy.IsDone(t) || t > MaxSimulationSeconds) c._simY = null;
        }
        // 平滑滚动（滚轮、键盘、程序滚动）不越界：内容在动画中变小时立即钳制到新的范围（惯性 / 回弹的越界由物理决定）
        if (hadSimulation && !c._animationIsFling && c._hasMetrics && !c._dragging)
        {
            x = Math.Max(0, Math.Min(c.MaxOffsetX, x));
            y = Math.Max(0, Math.Min(c.MaxOffsetY, y));
        }
        if (hadSimulation && c._simX is null && c._simY is null)
        {
            // 模拟结束：越界的残余（回弹的容差）钳制回范围内
            if (c._hasMetrics && !c._dragging)
            {
                x = Math.Max(0, Math.Min(c.MaxOffsetX, x));
                y = Math.Max(0, Math.Min(c.MaxOffsetY, y));
            }
            c.CompleteAnimation();
        }
        bool gestureEnded = false;
        if (c._gesture && !double.IsNaN(c._gestureEndDeadline) && now >= c._gestureEndDeadline)
        {
            c._gesture = false;
            c._gestureEndDeadline = double.NaN;
            gestureEnded = true;
        }
        else if (c._gesture && double.IsNaN(c._gestureEndDeadline))
        {
            // 手势没有收到结束相位：超时兜底（定时器在最早的到期时刻唤醒一次，此后的事件推迟了到期时刻时在这里重新预约）
            if (now >= c._gestureStaleAt)
            {
                c._gesture = false;
                c._gestureStaleAt = double.PositiveInfinity;
                gestureEnded = true;
            }
            else ScheduleWakeup(c._gestureStaleAt);
        }
        c.SetOffset(x, y);
        // 一次用户输入结束（惯性 / 滚轮动画 / 触控板手势）：尝试吸附到最近的吸附点
        if (c._snapEligible && !c._dragging && !c._gesture && c._simX is null && c._simY is null
            && (gestureEnded || hadSimulation))
        {
            c._snapEligible = false;
            MaybeStartSnap(c);
        }
    }

    void UpdateScrollbarOpacity(ScrollController c, double now)
    {
        float target;
        if (_p.EffectiveScrollbarStyle != ScrollbarStyle.Overlay) target = 0;
        else if (c.IsScrolling || c._scrollbarHover != 0 || c._scrollbarPressed != 0) target = 1;
        else
        {
            double idle = now - c._lastActivity;
            target = idle < OverlayHoldSeconds ? 1 : (float)Math.Max(0, 1 - (idle - OverlayHoldSeconds) / OverlayFadeSeconds);
            if (target > 0 && idle < OverlayHoldSeconds) ScheduleWakeup(c._lastActivity + OverlayHoldSeconds);
        }
        if (target == c._scrollbarOpacity) return;
        c._scrollbarOpacity = target;
        _p.InvalidateScrollbars(c);
    }

    /// <summary>
    /// 等待期间不出帧：用时钟定时器在 <paramref name="due"/> 唤醒（覆盖式滚动条淡出开始、触控板手势超时；没有时钟时靠每帧推进）。
    /// 只保留最早的一个到期时刻。
    /// </summary>
    void ScheduleWakeup(double due)
    {
        var clock = _root?.Clock;
        if (clock is null || due >= _wakeTimerDue) return;
        _wakeTimer?.Dispose();
        _wakeTimerDue = due;
        _wakeTimer = clock.Schedule(TimeSpan.FromSeconds(Math.Max(0, due - Now)), () =>
        {
            _wakeTimer = null;
            _wakeTimerDue = double.PositiveInfinity;
            RequestFrame();
        });
    }

    readonly List<ScrollController> _pendingEnd = new();

    /// <summary>帧末（绘制之后）：每个偏移变化的控制器报告一次 scroll，停下来的报告 ScrollEnd。</summary>
    public void EndFrame()
    {
        if (_changed.Count == 0 && _pendingEnd.Count == 0) return;
        var list = new List<ScrollController>(_changed);
        foreach (var c in _pendingEnd)
            if (!list.Contains(c)) list.Add(c);
        _changed.Clear();
        _pendingEnd.Clear();
        foreach (var c in list)
        {
            if (c._x != c._reportedX || c._y != c._reportedY)
            {
                var e = new ScrollEvent
                {
                    Offset = new Point((float)c._x, (float)c._y),
                    Delta = new Point((float)(c._x - c._reportedX), (float)(c._y - c._reportedY)),
                    Controller = c,
                };
                c._reportedX = c._x;
                c._reportedY = c._y;
                Raise(c, e);
            }
            if (c.IsScrolling && c._element is { IsAttached: true })
            {
                // 还在滚动：滚动停止的那一帧（模拟结束、手势结束、拖动松手都会请求帧）再检查
                _pendingEnd.Add(c);
            }
            else if (c._movedSinceEnd)
            {
                c._movedSinceEnd = false;
                try
                {
                    c.RaiseScrollEnd();
                }
                catch (Exception ex)
                {
                    _root?.ReportError(ex, c._element?.Owner, ErrorPhase.EventHandler);
                }
            }
        }
    }

    void Raise(ScrollController c, ScrollEvent e)
    {
        try
        {
            c.RaiseScrolled(e);
        }
        catch (Exception ex)
        {
            _root?.ReportError(ex, c._element?.Owner, ErrorPhase.EventHandler);
        }
        if (c._element is { IsAttached: true } el && _root is not null)
        {
            var fresh = new ScrollEvent { Offset = e.Offset, Delta = e.Delta, Controller = c };
            _root.Input.Dispatch(el, fresh, EventKind.Scroll, null);
        }
    }

    // ------------------------------------------------------------ 滚动命令（输入路由用）

    /// <summary>
    /// 按增量滚动：平滑时叠加到进行中的平滑动画的目标上（滚轮、键盘）。<paramref name="source"/> 不是
    /// <see cref="ScrollSource.Programmatic"/> 时调用 <see cref="NestedScrollConnection"/> 的 pre / post 钩子，并在结束后
    /// 允许一次吸附。
    /// </summary>
    void ScrollBy(ScrollController c, double dx, double dy, bool smooth, double durationMs, ScrollSource source = ScrollSource.Programmatic)
    {
        bool hooks = source != ScrollSource.Programmatic && c.NestedConnection is not null;
        var available = new Vector2((float)dx, (float)dy);
        if (hooks)
        {
            var pre = PreScroll(c, available, source);
            dx -= pre.X;
            dy -= pre.Y;
        }
        if (dx == 0 && dy == 0)
        {
            if (hooks) PostScroll(c, default, available, source);
            return;
        }
        // 惯性与程序滚动（ScrollTo / ScrollIntoView）被用户输入整体打断：两个轴都停下，任务完成之后不再推进
        // （否则另一轴上的输入只完成了任务，程序动画仍在本轴上继续）
        if (c._animationIsFling || c._animationTask is not null)
        {
            c._simX = c._simY = null;
            c._animationIsFling = false;
        }
        c.CompleteAnimation();
        double consumedX = 0, consumedY = 0;
        if (!smooth || !_p.SmoothScrolling)
        {
            c._simX = c._simY = null;
            double beforeX = c._x, beforeY = c._y;
            c.SetOffset(c.Clamp(c._x + dx, 0), c.Clamp(c._y + dy, 1));
            consumedX = c._x - beforeX;
            consumedY = c._y - beforeY;
        }
        else
        {
            double now = Now;
            var duration = TimeSpan.FromMilliseconds(durationMs);
            if (dx != 0)
            {
                double from = c._x;
                double baseTarget = c._simX is SmoothScrollSimulation s ? s.Target : c._x;
                double target = c.Clamp(baseTarget + dx, 0);
                c._simX = target != from ? new SmoothScrollSimulation(from, target, duration, ScrollEasing.EaseOut) : null;
                c._simStartX = now;
                consumedX = target - from;
            }
            if (dy != 0)
            {
                double from = c._y;
                double baseTarget = c._simY is SmoothScrollSimulation s ? s.Target : c._y;
                double target = c.Clamp(baseTarget + dy, 1);
                c._simY = target != from ? new SmoothScrollSimulation(from, target, duration, ScrollEasing.EaseOut) : null;
                c._simStartY = now;
                consumedY = target - from;
            }
            c._lastActivity = now;
            Activate(c);
        }
        if (hooks) PostScroll(c, new Vector2((float)consumedX, (float)consumedY), available, source);
        if (source == ScrollSource.Programmatic) return;
        // 立即滚动（SmoothScrolling 关闭）没有模拟来触发结束回调，直接尝试吸附
        if (c._simX is null && c._simY is null)
        {
            c._snapEligible = false;
            MaybeStartSnap(c);
        }
        else
        {
            c._snapEligible = true;
        }
    }

    /// <summary>滚动容器在 <paramref name="axis"/> 方向上还能否按 <paramref name="delta"/> 的符号滚动（用户滚动）。</summary>
    bool CanScroll(Element e, ScrollController c, int axis, double delta)
    {
        if (delta == 0 || !c._hasMetrics) return false;
        int axes = RenderPipeline.ScrollAxes(e);
        if ((axes & (axis == 0 ? RenderPipeline.UserScrollX : RenderPipeline.UserScrollY)) == 0) return false;
        double max = axis == 0 ? c.MaxOffsetX : c.MaxOffsetY;
        if (max <= 0) return false;
        double cur = PendingTarget(c, axis);
        return delta < 0 ? cur > 0.01 : cur < max - 0.01;
    }

    /// <summary>平滑动画进行中时以其目标为准（连续的滚轮格数叠加时，判断「还能不能滚」看的是目标）。</summary>
    static double PendingTarget(ScrollController c, int axis)
    {
        var sim = axis == 0 ? c._simX : c._simY;
        if (sim is SmoothScrollSimulation s) return s.Target;
        return axis == 0 ? c._x : c._y;
    }

    /// <summary>控制器显式设置的值优先，否则取 CSS <c>overscroll-behavior</c>（<see cref="ScrollCss"/>）。</summary>
    static OverscrollBehavior BehaviorFor(ScrollController c, int axis) => ScrollCss.Overscroll(c, axis);

    // ------------------------------------------------------------ 吸附（scroll-snap-*）

    /// <summary>吸附动画的时长（毫秒）：用户输入结束后从当前位置平滑到吸附点。</summary>
    public const double SnapAnimationMs = 220;

    readonly List<SnapPoint> _snapPoints = new();

    /// <summary>
    /// 一段用户输入引起的运动结束后调用：按 <c>scroll-snap-type</c> 选择最近的吸附点，用平滑模拟动画到那里。
    /// mandatory 总是吸附；proximity 只在距离不超过 <see cref="ScrollSnap.ProximityThresholdRatio"/> × 视口（且不小于
    /// <see cref="ScrollSnap.MinProximityPixels"/>）时吸附。没有时钟时立即跳过去；目标钳制在 [0, MaxOffset]，
    /// 因此不会破坏越界 / 钳制行为。内容 / 视口变化不会立即重新吸附（等到下一次用户滚动），见 design/21。
    /// </summary>
    void MaybeStartSnap(ScrollController c)
    {
        if (!c._hasMetrics || c._dragging || c._element is not { IsAttached: true }) return;
        bool snapX = ScrollSnap.Enabled(c, 0, out bool mandatoryX);
        bool snapY = ScrollSnap.Enabled(c, 1, out bool mandatoryY);
        if (!snapX && !snapY) return;
        double? targetX = null, targetY = null;
        if (snapX)
        {
            ScrollSnap.Collect(c, 0, _p, _snapPoints);
            targetX = ScrollSnap.Nearest(_snapPoints, c._x, mandatoryX, c._viewportW);
        }
        if (snapY)
        {
            ScrollSnap.Collect(c, 1, _p, _snapPoints);
            targetY = ScrollSnap.Nearest(_snapPoints, c._y, mandatoryY, c._viewportH);
        }
        if (targetX is null && targetY is null) return;
        // 没有时钟（确定的离屏渲染）：没有可推进的时间，直接跳到吸附点
        if (_root?.Clock is null)
        {
            c.SetOffset(targetX ?? c._x, targetY ?? c._y);
            return;
        }
        double now = Now;
        var duration = TimeSpan.FromMilliseconds(SnapAnimationMs);
        if (targetX is { } nx)
        {
            c._simX = new SmoothScrollSimulation(c._x, nx, duration, ScrollEasing.EaseOut);
            c._simStartX = now;
        }
        if (targetY is { } ny)
        {
            c._simY = new SmoothScrollSimulation(c._y, ny, duration, ScrollEasing.EaseOut);
            c._simStartY = now;
        }
        c._animationIsFling = false;
        c._lastActivity = now;
        Activate(c);
    }

    // ------------------------------------------------------------ 嵌套滚动（NestedScrollConnection）

    Vector2 PreScroll(ScrollController c, Vector2 available, ScrollSource source)
    {
        if (c.NestedConnection is not { } connection) return default;
        try
        {
            return SanitizeConsumed(connection.OnPreScroll(available, source), available);
        }
        catch (Exception ex)
        {
            ReportHookError(c, ex);
            return default;
        }
    }

    Vector2 PostScroll(ScrollController c, Vector2 consumed, Vector2 available, ScrollSource source)
    {
        if (c.NestedConnection is not { } connection) return default;
        try
        {
            return SanitizeConsumed(connection.OnPostScroll(consumed, available, source), available - consumed);
        }
        catch (Exception ex)
        {
            ReportHookError(c, ex);
            return default;
        }
    }

    Vector2 PreFling(ScrollController c, Vector2 velocity)
    {
        if (c.NestedConnection is not { } connection) return default;
        try
        {
            return SanitizeConsumed(connection.OnPreFling(velocity), velocity);
        }
        catch (Exception ex)
        {
            ReportHookError(c, ex);
            return default;
        }
    }

    Vector2 PostFling(ScrollController c, Vector2 consumed, Vector2 available)
    {
        if (c.NestedConnection is not { } connection) return default;
        try
        {
            return SanitizeConsumed(connection.OnPostFling(consumed, available), available - consumed);
        }
        catch (Exception ex)
        {
            ReportHookError(c, ex);
            return default;
        }
    }

    void ReportHookError(ScrollController c, Exception ex) =>
        _root?.ReportError(ex, c._element?.Owner, ErrorPhase.EventHandler);

    /// <summary>
    /// 钩子返回值只保留「与可用量同方向、且不超过可用量」的部分：非有限值、反向值、超额值都被忽略（按未消费处理）。
    /// </summary>
    static Vector2 SanitizeConsumed(Vector2 result, Vector2 available) =>
        new(ClampComponent(result.X, available.X), ClampComponent(result.Y, available.Y));

    static float ClampComponent(float v, float available)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return 0;
        if (available >= 0) return v <= 0 ? 0 : v >= available ? available : v;
        return v >= 0 ? 0 : v <= available ? available : v;
    }

    /// <summary>
    /// 从 <paramref name="start"/> 向上找第一个能按增量滚动的滚动容器（滚动链）。某个容器自己滚不动但其 overscroll-behavior
    /// 为 contain / none 时链在它这里截断（返回 null，且 <paramref name="blocked"/> 为 true）。
    /// </summary>
    ScrollController? FindScroller(Element? start, double dx, double dy, out bool blocked, bool physicalX = false)
    {
        blocked = false;
        int dominant = Math.Abs(dx) > Math.Abs(dy) ? 0 : 1;
        for (var e = start; e is not null; e = e.Parent)
        {
            if (RenderPipeline.ScrollAxes(e) == 0 || e.Render is not { HasBox: true }) continue;
            var c = e.ScrollController;
            // 键盘 / 触摸给的是物理方向；RTL 容器的行内偏移方向相反
            double cx = physicalX && c._rtl ? -dx : dx;
            if (CanScroll(e, c, 0, cx) || CanScroll(e, c, 1, dy)) return c;
            // 只有在该方向上真的有可滚动溢出的容器才截断滚动链（CSS：overscroll-behavior 作用于滚动容器）
            if (BehaviorFor(c, dominant) != OverscrollBehavior.Auto && HasOverflow(c, dominant))
            {
                blocked = true;
                return null;
            }
        }
        return null;
    }

    static bool HasOverflow(ScrollController c, int axis) => (axis == 0 ? c.MaxOffsetX : c.MaxOffsetY) > 0;

    // ------------------------------------------------------------ 滚轮与触控板

    ScrollController? _latched;

    /// <summary>
    /// 滚轮：先向指针下的元素派发 <c>wheel</c>（可取消），再滚动最内层能在该方向滚动的容器（不能滚时沿滚动链交给祖先）。
    /// 触控板手势（有相位）锁定在开始时选中的容器上直到惯性结束，并直接跟随增量；普通滚轮做可叠加的短动画。
    /// </summary>
    public void OnWheel(in RawPointerEvent raw, Element? target)
    {
        double dx = -raw.WheelDeltaX, dy = -raw.WheelDeltaY;
        bool precise = raw.PreciseWheel;
        // Shift + 纵向滚轮 = 横向（触控板自己就能横向滚动，不交换）
        if ((raw.Modifiers & KeyModifiers.Shift) != 0 && !precise && dx == 0)
        {
            dx = dy;
            dy = 0;
        }
        if (target is not null && _root is not null)
        {
            var we = new WheelEvent
            {
                Position = raw.Position,
                DeltaX = (float)dx,
                DeltaY = (float)dy,
                DeltaMode = raw.WheelDeltaMode,
                IsPrecise = precise,
                ScrollPhase = raw.ScrollPhase,
                MomentumPhase = raw.MomentumPhase,
                Modifiers = raw.Modifiers,
                Timestamp = raw.Timestamp,
            };
            if (!_root.Input.Dispatch(target, we, EventKind.Wheel, null)) return;
        }
        var phase = raw.ScrollPhase;
        var momentum = raw.MomentumPhase;
        if (phase == ScrollPhase.MayBegin)
        {
            // 手指放上触控板：停止进行中的惯性 / 动画
            for (var e = target; e is not null; e = e.Parent)
                if (e._scroll is { } sc && (sc._simX is not null || sc._simY is not null)) sc.Stop();
            return;
        }
        bool hasPhase = phase != ScrollPhase.None || momentum != ScrollPhase.None;
        if (phase == ScrollPhase.Began || (momentum == ScrollPhase.Began && _latched is null)) _latched = null;
        if (raw.WheelDeltaMode == WheelDeltaMode.Page)
        {
            // 平台按窗口一页换算：换回页数，再按目标容器的页尺寸换算
            var vp = _p.Viewport;
            dx = vp.Width > 0 ? dx / vp.Width : 0;
            dy = vp.Height > 0 ? dy / vp.Height : 0;
        }

        ScrollController? c = null;
        if (hasPhase && _latched is { _element.IsAttached: true } latched) c = latched;
        else
        {
            double probeX = dx, probeY = dy;
            c = FindScroller(target ?? _root?.RootElement, probeX, probeY, out _, physicalX: true);
            if (hasPhase) _latched = c;
        }
        if (c is not null)
        {
            // 滚轮增量表达物理方向；RTL 容器从右边缘量起，横向取反
            if (c._rtl) dx = -dx;
            if (raw.WheelDeltaMode == WheelDeltaMode.Page)
            {
                dx *= PageSize(c._viewportW);
                dy *= PageSize(c._viewportH);
            }
            // 只在本容器能滚的轴上滚（另一轴的增量不传给别的容器，与浏览器一致）
            var el = c._element!;
            if (!CanScroll(el, c, 0, dx)) dx = 0;
            if (!CanScroll(el, c, 1, dy)) dy = 0;
            if (precise || hasPhase)
            {
                // 嵌套滚动：父级先拿走一部分（折叠标题栏等），剩下的交给本容器
                var available = new Vector2((float)dx, (float)dy);
                if (c.NestedConnection is not null)
                {
                    var pre = PreScroll(c, available, ScrollSource.Wheel);
                    dx -= pre.X;
                    dy -= pre.Y;
                }
                // 精确设备：直接跟随，不做平滑；系统惯性相位同样直接跟随（不叠加自己的惯性）
                c._simX = c._simY = null;
                c.CompleteAnimation();
                c._gesture = true;
                bool ended = (phase is ScrollPhase.Ended or ScrollPhase.Cancelled && momentum == ScrollPhase.None)
                             || momentum == ScrollPhase.Ended || !hasPhase;
                // 手势结束后惯性可能紧接着开始：留一小段时间再判定结束
                c._gestureEndDeadline = ended ? Now + GestureEndDelaySeconds : double.NaN;
                c._gestureStaleAt = ended ? double.PositiveInfinity : Now + GestureTimeoutSeconds;
                if (!ended) ScheduleWakeup(c._gestureStaleAt);
                double beforeX = c._x, beforeY = c._y;
                c.SetOffset(c.Clamp(c._x + dx, 0), c.Clamp(c._y + dy, 1));
                c._lastActivity = Now;
                Activate(c);
                if (c.NestedConnection is not null)
                    PostScroll(c, new Vector2((float)(c._x - beforeX), (float)(c._y - beforeY)), available, ScrollSource.Wheel);
                c._snapEligible = true;
            }
            else
            {
                ScrollBy(c, dx, dy, smooth: true, DesktopPhysics.WheelAnimationMs, ScrollSource.Wheel);
            }
        }
        if (momentum == ScrollPhase.Ended || (phase is ScrollPhase.Ended or ScrollPhase.Cancelled && momentum == ScrollPhase.None))
        {
            if (momentum == ScrollPhase.Ended) _latched = null;
        }
    }

    static double PageSize(double viewport) => Math.Max(KeyLineStep, viewport * 0.875);

    // ------------------------------------------------------------ 键盘

    Element? _lastPointerScope;

    /// <summary>
    /// 未被处理的按键：方向键、PageUp / PageDown、Home / End、空格（Shift+空格向上）滚动焦点所在的最近滚动容器
    /// （没有焦点时为最近一次点击处的容器，再没有则为视口）。输入框内不处理。返回是否消费。
    /// </summary>
    public bool OnKey(Element target, in RawKeyEvent raw)
    {
        if (raw.Type != RawKeyEventType.Down || target is InputElement) return false;
        if (raw.Key == PhysicalKey.Space && target is ButtonElement) return false;
        var mods = raw.Modifiers;
        if ((mods & (KeyModifiers.Alt | KeyModifiers.Meta | KeyModifiers.AltGraph)) != 0) return false;
        bool ctrl = (mods & KeyModifiers.Control) != 0, shift = (mods & KeyModifiers.Shift) != 0;
        double dx = 0, dy = 0;
        bool page = false, toStart = false, toEnd = false;
        switch (raw.Key)
        {
            case PhysicalKey.ArrowUp when !ctrl: dy = -KeyLineStep; break;
            case PhysicalKey.ArrowDown when !ctrl: dy = KeyLineStep; break;
            case PhysicalKey.ArrowLeft when !ctrl: dx = -KeyLineStep; break;
            case PhysicalKey.ArrowRight when !ctrl: dx = KeyLineStep; break;
            case PhysicalKey.PageUp when !ctrl: dy = -1; page = true; break;
            case PhysicalKey.PageDown when !ctrl: dy = 1; page = true; break;
            case PhysicalKey.Space when !ctrl: dy = shift ? -1 : 1; page = true; break;
            case PhysicalKey.Home: dy = -1; toStart = true; break;
            case PhysicalKey.End: dy = 1; toEnd = true; break;
            default: return false;
        }
        Element? start = target;
        if (_root is not null && ReferenceEquals(target, _root.RootElement) && _lastPointerScope is { IsAttached: true } scope) start = scope;
        var c = FindScroller(start, dx, dy, out bool blocked, physicalX: true);
        if (c is null) return blocked;
        // 方向键表达的是物理方向：RTL 容器中 ArrowLeft 向行内末尾滚动
        if (c._rtl) dx = -dx;
        if (page) dy *= PageSize(c._viewportH);
        if (toStart) dy = -PendingTarget(c, 1);
        if (toEnd) dy = c.MaxOffsetY - PendingTarget(c, 1);
        ScrollBy(c, dx, dy, smooth: true, page || toStart || toEnd ? 200 : DesktopPhysics.WheelAnimationMs, ScrollSource.Keyboard);
        return true;
    }

    // ------------------------------------------------------------ 程序滚动到可见

    readonly struct RevealRequest
    {
        public RevealRequest(ScrollController? controller, Element element, ScrollAlignment alignment, TaskCompletionSource<bool>? task)
        {
            Controller = controller;
            Element = element;
            Alignment = alignment;
            Task = task;
        }

        /// <summary>为 null 表示沿全部滚动祖先立即揭示（焦点导航）。</summary>
        public ScrollController? Controller { get; }
        public Element Element { get; }
        public ScrollAlignment Alignment { get; }
        public TaskCompletionSource<bool>? Task { get; }
    }

    public Task RequestScrollIntoView(ScrollController c, Element element, ScrollAlignment alignment)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reveals.Add(new RevealRequest(c, element, alignment, tcs));
        RequestFrame();
        return tcs.Task;
    }

    /// <summary>焦点导航（键盘 / 程序聚焦）：下一帧布局之后立即把焦点元素滚动到可见（沿全部滚动祖先，最小滚动量）。</summary>
    public void RevealFocused(Element element)
    {
        for (int i = _reveals.Count - 1; i >= 0; i--)
            if (_reveals[i].Controller is null) _reveals.RemoveAt(i);
        _reveals.Add(new RevealRequest(null, element, ScrollAlignment.Nearest, null));
        RequestFrame();
    }

    /// <summary>布局与几何都是最新的之后执行待处理的「滚动到可见」。</summary>
    internal void ProcessReveals()
    {
        if (_reveals.Count == 0) return;
        var list = _reveals.ToArray();
        _reveals.Clear();
        foreach (var r in list)
        {
            var el = r.Element;
            if (!el.IsAttached || el.Render is not { HasBox: true })
            {
                r.Task?.TrySetResult(false);
                continue;
            }
            if (r.Controller is { } c)
            {
                if (c._element is not { IsAttached: true } se || se.Render is not { HasBox: true })
                {
                    r.Task?.TrySetResult(false);
                    continue;
                }
                var target = TargetFor(c, se, ScrollCss.WithScrollMargin(el, el.BoundingRect), r.Alignment);
                c.StartScrollTo(target.X, target.Y, ScrollCss.DefaultAnimation(c), r.Task ?? new TaskCompletionSource<bool>());
                continue;
            }
            // 焦点揭示：从内到外，每层用最小的滚动量，立即生效
            var rect = ScrollCss.WithScrollMargin(el, el.BoundingRect);
            for (var e = el.Parent; e is not null; e = e.Parent)
            {
                if (RenderPipeline.ScrollAxes(e) == 0 || e.Render is not { HasBox: true }) continue;
                var sc = e.ScrollController;
                if (!sc._hasMetrics) continue;
                var t = TargetFor(sc, e, rect, ScrollAlignment.Nearest);
                double ox = sc._appliedX, oy = sc._appliedY;
                if (t.X == sc._x && t.Y == sc._y) continue;
                sc.Stop();
                sc.SetOffset(t.X, t.Y);
                double nx = _p.SnapOffset(sc.TranslationX(sc._x)), ny = _p.SnapOffset(sc._y);
                rect = rect.Offset((float)-(nx - ox), (float)-(ny - oy));
            }
        }
    }

    /// <summary>使 <paramref name="rect"/>（绝对坐标，按当前已施加的偏移）在容器视口中按对齐方式可见的目标偏移。</summary>
    (double X, double Y) TargetFor(ScrollController c, Element scroller, Rect rect, ScrollAlignment alignment)
    {
        // scroll-padding：视口中用于对齐的区域内缩
        var vp = ScrollCss.WithScrollPadding(scroller, _p.ScrollViewportRect(scroller));
        double x = c._appliedX + Delta(rect.Left, rect.Right, vp.Left, vp.Right, alignment);
        double y = c._appliedY + Delta(rect.Top, rect.Bottom, vp.Top, vp.Bottom, alignment);
        var axes = RenderPipeline.ScrollAxes(scroller);
        // 横向 delta 是物理平移量；RTL 下换算回从右边缘量起的逻辑偏移
        if ((axes & RenderPipeline.ScrollContainerX) == 0) x = c._x;
        else if (c._rtl) x = -x;
        if ((axes & RenderPipeline.ScrollContainerY) == 0) y = c._y;
        return (c.Clamp(x, 0), c.Clamp(y, 1));
    }

    static float Delta(float lo, float hi, float vlo, float vhi, ScrollAlignment alignment)
    {
        switch (alignment)
        {
            case ScrollAlignment.Start: return lo - vlo;
            case ScrollAlignment.End: return hi - vhi;
            case ScrollAlignment.Center: return (lo + hi) / 2 - (vlo + vhi) / 2;
            default:
                if (lo >= vlo && hi <= vhi) return 0;
                // 比视口大时对齐起始边
                if (lo < vlo || hi - lo > vhi - vlo) return lo - vlo;
                return hi - vhi;
        }
    }

    // ------------------------------------------------------------ 指针：滚动条与触摸拖动

    sealed class TouchTrack
    {
        public long PointerId;
        public Point Start, Last;
        public Element? Hit;
        public readonly VelocityTracker Tracker = new();
        public ScrollController? Controller;
        public bool LockX, LockY;
        public ScrollPhysics? Physics;
    }

    TouchTrack? _touch;

    // 滚动条拖动
    ScrollController? _thumbDrag;
    long _thumbPointer;
    int _thumbAxis;
    float _thumbStartPointer;
    double _thumbStartOffset;

    /// <summary>
    /// 指针事件的预处理：滚动条上的按下 / 拖动、触摸拖动滚动（越过触摸容差后取消点击）。返回 true 表示事件被滚动系统消费，不再派发给元素。
    /// </summary>
    public bool PreviewPointer(in RawPointerEvent raw, Element? hit)
    {
        if (_thumbDrag is { } td && raw.PointerId == _thumbPointer)
        {
            switch (raw.Type)
            {
                case RawPointerEventType.Move:
                    DragThumb(td, raw.Position);
                    return true;
                case RawPointerEventType.Up:
                case RawPointerEventType.Cancel:
                    EndThumbDrag();
                    return true;
                default:
                    return true;
            }
        }
        switch (raw.Type)
        {
            case RawPointerEventType.Down:
            {
                _lastPointerScope = hit;
                if (raw.Device == PointerType.Touch) _p.LastPointerWasTouch = true;
                else if (raw.Device == PointerType.Mouse) _p.LastPointerWasTouch = false;
                if ((raw.Device != PointerType.Mouse || raw.ChangedButton == PointerButtons.Left) && TryScrollbarDown(raw)) return true;
                if (raw.Device != PointerType.Mouse && _touch is null)
                {
                    var t = new TouchTrack { PointerId = raw.PointerId, Start = raw.Position, Last = raw.Position, Hit = hit };
                    t.Tracker.AddSample(raw.Timestamp, raw.Position);
                    // 按下时停止惯性（点按停止）
                    for (var e = hit; e is not null; e = e.Parent)
                        if (e._scroll is { } sc && sc._animationIsFling && (sc._simX is not null || sc._simY is not null)) sc.Stop();
                    _touch = t;
                }
                return false;
            }
            case RawPointerEventType.Move:
            {
                if (_touch is { } t && raw.PointerId == t.PointerId) return TouchMove(t, raw);
                if (raw.Device == PointerType.Mouse) UpdateHover(raw.Position, hit);
                return false;
            }
            case RawPointerEventType.Up:
            case RawPointerEventType.Cancel:
            {
                if (_touch is { } t && raw.PointerId == t.PointerId)
                {
                    _touch = null;
                    if (t.Controller is { } c)
                    {
                        t.Tracker.AddSample(raw.Timestamp, raw.Position);
                        EndDrag(t, c, raw.Type == RawPointerEventType.Cancel);
                        return true;
                    }
                }
                return false;
            }
            case RawPointerEventType.Leave:
                UpdateHover(new Point(float.NaN, float.NaN), null);
                return false;
        }
        return false;
    }

    bool TouchMove(TouchTrack t, in RawPointerEvent raw)
    {
        t.Tracker.AddSample(raw.Timestamp, raw.Position);
        if (t.Controller is null)
        {
            float mx = raw.Position.X - t.Start.X, my = raw.Position.Y - t.Start.Y;
            float slop = GestureSettings.Default.SlopFor(raw.Device);
            if (mx * mx + my * my <= slop * slop) return false;
            var settings = GestureSettings.Default;
            bool lockX = Math.Abs(my) > settings.AxisLockRatio * Math.Abs(mx);
            bool lockY = Math.Abs(mx) > settings.AxisLockRatio * Math.Abs(my);
            float probeX = lockX ? 0 : -mx, probeY = lockY ? 0 : -my;
            var c = FindScroller(t.Hit, probeX, probeY, out bool blocked, physicalX: true);
            // 允许越界的物理（回弹）：在边缘也可以拖动（橡皮筋），选最内层在该方向上有可滚动溢出的容器
            if (c is null && !blocked) c = FindBouncingScroller(t.Hit, probeX, probeY);
            if (c is null)
            {
                // 没有能滚的容器：不再跟踪，事件照常派发
                _touch = null;
                return false;
            }
            t.Controller = c;
            t.LockX = lockX;
            t.LockY = lockY;
            t.Physics = c.Physics ?? _p.TouchScrollPhysics ?? _p.DefaultScrollPhysics;
            t.Last = raw.Position;
            c.Stop();
            c._dragging = true;
            Activate(c);
            // 按下变成了滚动：取消这次按下（pointercancel，之后不再有 click）
            _root?.Input.CancelPointerForScroll(raw);
            return true;
        }
        var ctl = t.Controller;
        if (ctl._element is not { IsAttached: true })
        {
            _touch = null;
            return true;
        }
        float dx = t.LockX ? 0 : t.Last.X - raw.Position.X;
        float dy = t.LockY ? 0 : t.Last.Y - raw.Position.Y;
        // 手指位移是物理方向：RTL 容器从右边缘量起，横向增量取反
        if (ctl._rtl) dx = -dx;
        t.Last = raw.Position;
        var physics = t.Physics!;
        var available = new Vector2(dx, dy);
        if (ctl.NestedConnection is not null)
        {
            var pre = PreScroll(ctl, available, ScrollSource.Touch);
            dx -= pre.X;
            dy -= pre.Y;
        }
        double ax = AxisDrag(ctl, 0, dx, physics);
        double ay = AxisDrag(ctl, 1, dy, physics);
        ctl.SetOffset(ctl._x + ax, ctl._y + ay);
        if (ctl.NestedConnection is not null)
            PostScroll(ctl, new Vector2((float)ax, (float)ay), available, ScrollSource.Touch);
        return true;
    }

    ScrollController? FindBouncingScroller(Element? start, float dx, float dy)
    {
        int axis = Math.Abs(dx) > Math.Abs(dy) ? 0 : 1;
        for (var e = start; e is not null; e = e.Parent)
        {
            int axes = RenderPipeline.ScrollAxes(e);
            if ((axes & (axis == 0 ? RenderPipeline.UserScrollX : RenderPipeline.UserScrollY)) == 0 || e.Render is not { HasBox: true }) continue;
            var c = e.ScrollController;
            if (!HasOverflow(c, axis)) continue;
            var physics = c.Physics ?? _p.TouchScrollPhysics ?? _p.DefaultScrollPhysics;
            if (physics.Overscroll == OverscrollMode.Bounce && BehaviorFor(c, axis) != OverscrollBehavior.None) return c;
            return null;
        }
        return null;
    }

    double AxisDrag(ScrollController c, int axis, double delta, ScrollPhysics physics)
    {
        if (delta == 0) return 0;
        var el = c._element!;
        int axes = RenderPipeline.ScrollAxes(el);
        if ((axes & (axis == 0 ? RenderPipeline.UserScrollX : RenderPipeline.UserScrollY)) == 0) return 0;
        var m = c.MetricsFor(axis);
        if (BehaviorFor(c, axis) == OverscrollBehavior.None || physics.Overscroll == OverscrollMode.None || physics.Overscroll != OverscrollMode.Bounce)
        {
            // 不越界：截断（拉伸 / 辉光效果尚未绘制，按截断处理）
            double applied = physics.ApplyDrag(m, delta);
            double target = Math.Max(Math.Min(m.Pixels, 0), Math.Min(Math.Max(m.Pixels, m.MaxScrollExtent), m.Pixels + applied));
            return target - m.Pixels;
        }
        return physics.ApplyDrag(m, delta);
    }

    void EndDrag(TouchTrack t, ScrollController c, bool cancelled)
    {
        c._dragging = false;
        if (c._element is not { IsAttached: true }) return;
        var v = cancelled ? Vector2.Zero : t.Tracker.GetVelocity();
        var physics = t.Physics ?? _p.DefaultScrollPhysics;
        double now = Now;
        // 手指速度向上 = 偏移增大；RTL 容器从右边缘量起，横向速度取反
        double velocityX = c._rtl ? v.X : -v.X;
        double velocityY = -v.Y;
        var availableVelocity = new Vector2((float)velocityX, (float)velocityY);
        if (c.NestedConnection is not null)
        {
            // 嵌套滚动：联结可以先消费一部分松手速度（下拉刷新、折叠头部等）
            var pre = PreFling(c, availableVelocity);
            velocityX -= pre.X;
            velocityY -= pre.Y;
        }
        var fx = t.LockX ? null : physics.CreateFling(c.MetricsFor(0), velocityX);
        var fy = t.LockY ? null : physics.CreateFling(c.MetricsFor(1), velocityY);
        // scroll-snap-stop: always —— 惯性不能越过第一个 always 吸附点
        if (fx is not null && ScrollSnap.Enabled(c, 0, out _))
        {
            ScrollSnap.Collect(c, 0, _p, _snapPoints);
            if (ScrollSnap.FirstAlwaysStop(_snapPoints, c._x, velocityX) is { } cap)
                fx = new SnapStopSimulation(fx, cap, velocityX > 0);
        }
        if (fy is not null && ScrollSnap.Enabled(c, 1, out _))
        {
            ScrollSnap.Collect(c, 1, _p, _snapPoints);
            if (ScrollSnap.FirstAlwaysStop(_snapPoints, c._y, velocityY) is { } cap)
                fy = new SnapStopSimulation(fy, cap, velocityY > 0);
        }
        if (BehaviorFor(c, 0) == OverscrollBehavior.None && fx is not null) fx = new ClampedSimulation(fx, 0, c.MaxOffsetX);
        if (BehaviorFor(c, 1) == OverscrollBehavior.None && fy is not null) fy = new ClampedSimulation(fy, 0, c.MaxOffsetY);
        c._simX = fx;
        c._simY = fy;
        c._simStartX = c._simStartY = now;
        c._animationIsFling = fx is not null || fy is not null;
        if (c.NestedConnection is not null)
        {
            var consumedVelocity = new Vector2(fx is null ? 0 : (float)velocityX, fy is null ? 0 : (float)velocityY);
            PostFling(c, consumedVelocity, availableVelocity);
        }
        // 没有惯性但越界（拖动松手在橡皮筋里）：交给下一帧钳制
        if (!c._animationIsFling && (c._x < 0 || c._y < 0 || c._x > c.MaxOffsetX || c._y > c.MaxOffsetY))
            c.SetOffset(c.Clamp(c._x, 0), c.Clamp(c._y, 1));
        Activate(c);
        // 没有惯性（停住松手 / 取消）：直接进入吸附
        if (!c._animationIsFling)
        {
            c._snapEligible = false;
            MaybeStartSnap(c);
        }
        else c._snapEligible = true;
    }

    bool TryScrollbarDown(in RawPointerEvent raw)
    {
        var sb = _p.HitTestScrollbar(raw.Position);
        if (sb.Controller is not { } c) return false;
        var geo = sb.Geometry;
        int axis = sb.Axis;
        var thumb = axis == 0 ? geo.ThumbX : geo.ThumbY;
        float p = axis == 0 ? raw.Position.X : raw.Position.Y;
        float t0 = axis == 0 ? thumb.Left : thumb.Top, t1 = axis == 0 ? thumb.Right : thumb.Bottom;
        if (thumb.Width > 0 && thumb.Height > 0 && p >= t0 && p < t1)
        {
            c.Stop();
            _thumbDrag = c;
            _thumbPointer = raw.PointerId;
            _thumbAxis = axis;
            _thumbStartPointer = p;
            _thumbStartOffset = axis == 0 ? c._x : c._y;
            c._scrollbarPressed = axis + 1;
            c._lastActivity = Now;
            _p.InvalidateScrollbars(c);
            Activate(c);
            return true;
        }
        // 点击轨道：朝指针方向翻一页
        double page = PageSize(axis == 0 ? c._viewportW : c._viewportH);
        float dir = p < t0 ? -1 : 1;
        // RTL：滑块从右端开始，点轨道左侧是往行内末尾翻页
        if (axis == 0 && c._rtl) dir = -dir;
        if (axis == 0) ScrollBy(c, dir * page, 0, smooth: true, 200, ScrollSource.Scrollbar);
        else ScrollBy(c, 0, dir * page, smooth: true, 200, ScrollSource.Scrollbar);
        return true;
    }

    void DragThumb(ScrollController c, Point position)
    {
        if (c._element is not { IsAttached: true } el)
        {
            EndThumbDrag();
            return;
        }
        var geo = _p.ScrollbarGeometryFor(el);
        var track = _thumbAxis == 0 ? geo.TrackX : geo.TrackY;
        var thumb = _thumbAxis == 0 ? geo.ThumbX : geo.ThumbY;
        float trackLen = _thumbAxis == 0 ? track.Width : track.Height;
        float thumbLen = _thumbAxis == 0 ? thumb.Width : thumb.Height;
        double max = _thumbAxis == 0 ? c.MaxOffsetX : c.MaxOffsetY;
        double free = trackLen - thumbLen;
        if (free <= 0 || max <= 0) return;
        float p = _thumbAxis == 0 ? position.X : position.Y;
        if (float.IsNaN(p) || float.IsInfinity(p)) return;
        double delta = (p - _thumbStartPointer) * max / free;
        // RTL 的滑块从右端开始：向左拖动增大行内偏移
        double offset = _thumbAxis == 0 && c._rtl ? _thumbStartOffset - delta : _thumbStartOffset + delta;
        if (_thumbAxis == 0) c.SetOffset(c.Clamp(offset, 0), c._y);
        else c.SetOffset(c._x, c.Clamp(offset, 1));
        c._lastActivity = Now;
    }

    void EndThumbDrag()
    {
        var c = _thumbDrag;
        _thumbDrag = null;
        if (c is null) return;
        c._scrollbarPressed = 0;
        c._lastActivity = Now;
        _p.InvalidateScrollbars(c);
        Activate(c);
    }

    ScrollController? _hovered;

    /// <summary>鼠标悬停：经典滚动条的滑块高亮；覆盖式滚动条在指针移到其轨道上时显示。</summary>
    void UpdateHover(Point position, Element? hit)
    {
        ScrollController? next = null;
        int part = 0;
        if (!float.IsNaN(position.X))
        {
            var sb = _p.HitTestScrollbar(position);
            if (sb.Controller is { } c)
            {
                next = c;
                part = sb.Axis + 1;
            }
            else if (_p.EffectiveScrollbarStyle == ScrollbarStyle.Overlay)
            {
                // 隐藏的覆盖式滚动条不参与命中：按几何判断指针是否在某个滚动祖先的轨道上
                for (var e = hit; e is not null; e = e.Parent)
                {
                    if (e._scroll is not { } sc || RenderPipeline.ScrollAxes(e) == 0) continue;
                    var geo = _p.ScrollbarGeometryFor(e);
                    if (geo.TrackY.Contains(position)) { next = sc; part = 2; break; }
                    if (geo.TrackX.Contains(position)) { next = sc; part = 1; break; }
                }
            }
        }
        if (_hovered is { } old && (!ReferenceEquals(old, next) || old._scrollbarHover != part))
        {
            old._scrollbarHover = 0;
            old._lastActivity = Now;
            _p.InvalidateScrollbars(old);
            Activate(old);
        }
        _hovered = next;
        if (next is not null && next._scrollbarHover != part)
        {
            next._scrollbarHover = part;
            next._lastActivity = Now;
            _p.InvalidateScrollbars(next);
            Activate(next);
        }
    }

    // ------------------------------------------------------------ 树变更

    internal void OnSubtreeRemoved(Element removed)
    {
        if (_touch?.Hit is { } h && removed.IsInclusiveAncestorOf(h)) _touch = null;
        if (_thumbDrag?._element is { } te && removed.IsInclusiveAncestorOf(te)) _thumbDrag = null;
        if (_latched?._element is { } le && removed.IsInclusiveAncestorOf(le)) _latched = null;
        if (_hovered?._element is { } he && removed.IsInclusiveAncestorOf(he)) _hovered = null;
        if (_lastPointerScope is { } ps && removed.IsInclusiveAncestorOf(ps)) _lastPointerScope = null;
    }
}
