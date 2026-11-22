using System;

namespace AwAUI.UI;

/// <summary>
/// 一个轴上的滚动度量（<see cref="ScrollPhysics"/> 的输入）。偏移量的约定：0 为内容起点，增大 = 内容向上 / 向左移动。
/// </summary>
public readonly struct ScrollMetrics
{
    public ScrollMetrics(double pixels, double minScrollExtent, double maxScrollExtent, double viewportDimension)
    {
        Pixels = pixels;
        MinScrollExtent = minScrollExtent;
        MaxScrollExtent = Math.Max(minScrollExtent, maxScrollExtent);
        ViewportDimension = Math.Max(0, viewportDimension);
    }

    /// <summary>当前偏移（可能越界）。</summary>
    public double Pixels { get; }

    /// <summary>最小偏移（通常为 0）。</summary>
    public double MinScrollExtent { get; }

    /// <summary>最大偏移（内容尺寸 − 视口尺寸，不小于 <see cref="MinScrollExtent"/>）。</summary>
    public double MaxScrollExtent { get; }

    /// <summary>视口在该轴上的尺寸。</summary>
    public double ViewportDimension { get; }

    /// <summary>当前是否越界。</summary>
    public bool OutOfRange => Pixels < MinScrollExtent || Pixels > MaxScrollExtent;

    /// <summary>越过起点（&gt; 0）或终点（&lt; 0）的距离；未越界为 0。</summary>
    public double Overscroll => Pixels < MinScrollExtent ? MinScrollExtent - Pixels : Pixels > MaxScrollExtent ? MaxScrollExtent - Pixels : 0;

    public ScrollMetrics WithPixels(double pixels) => new(pixels, MinScrollExtent, MaxScrollExtent, ViewportDimension);
}

/// <summary>越界的呈现方式。</summary>
public enum OverscrollMode : byte
{
    /// <summary>不越界。</summary>
    None,

    /// <summary>内容整体越界再弹回（iOS）。</summary>
    Bounce,

    /// <summary>边缘拉伸（Android 12+）。目前只作为声明，绘制层未实现拉伸效果。</summary>
    Stretch,

    /// <summary>边缘辉光（旧版 Android）。目前只作为声明，绘制层未实现。</summary>
    Glow,
}

/// <summary>
/// 滚动物理（design/21「滚动物理」）：把拖动位移换算成实际位移、松手后给出运动模拟。
/// 实现必须是无副作用的纯计算：<see cref="Simulation"/> 将来会交给渲染线程逐帧求值（design/18）；目前在 UI 线程上按帧时钟求值。
/// 所有位移与速度都是「偏移量」的单位（逻辑像素、逻辑像素每秒；正值 = 偏移增大）。
/// </summary>
public abstract class ScrollPhysics
{
    /// <summary>拖动中：把手指位移 <paramref name="delta"/>（换算成偏移的增量）换算成实际施加的增量（越界时加阻尼，不允许越界时截断）。</summary>
    public abstract double ApplyDrag(in ScrollMetrics m, double delta);

    /// <summary>松手后：返回一段运动模拟（惯性、回弹）；返回 null 表示立即停止（越界时也必须给出回到范围内的模拟）。</summary>
    public abstract Simulation? CreateFling(in ScrollMetrics m, double velocity);

    /// <summary>是否允许越界，以及越界的呈现方式。</summary>
    public abstract OverscrollMode Overscroll { get; }

    /// <summary>低于它的松手速度不产生惯性（逻辑像素每秒）。</summary>
    public virtual float MinFlingVelocity => 50f;

    /// <summary>松手速度的上限（逻辑像素每秒）。</summary>
    public virtual float MaxFlingVelocity => 8000f;

    /// <summary>把速度钳制到 [−<see cref="MaxFlingVelocity"/>, <see cref="MaxFlingVelocity"/>]，低于 <see cref="MinFlingVelocity"/> 时为 0。</summary>
    protected double ClampVelocity(double velocity)
    {
        if (double.IsNaN(velocity) || double.IsInfinity(velocity)) return 0;
        if (Math.Abs(velocity) < MinFlingVelocity) return 0;
        return Math.Max(-MaxFlingVelocity, Math.Min(MaxFlingVelocity, velocity));
    }

    /// <summary>不越界的拖动：截断到 [min, max]（已经越界时只允许向范围内移动）。</summary>
    protected static double ClampDrag(in ScrollMetrics m, double delta)
    {
        double target = m.Pixels + delta;
        double lo = Math.Min(m.MinScrollExtent, m.Pixels), hi = Math.Max(m.MaxScrollExtent, m.Pixels);
        target = Math.Max(lo, Math.Min(hi, target));
        if (m.Pixels >= m.MinScrollExtent && target < m.MinScrollExtent) target = m.MinScrollExtent;
        if (m.Pixels <= m.MaxScrollExtent && target > m.MaxScrollExtent) target = m.MaxScrollExtent;
        return target - m.Pixels;
    }
}

/// <summary>
/// iOS 风格：拖动越界时橡皮筋阻尼（位移随越界距离递减），惯性按 UIScrollView 的减速率指数衰减，到边界后转为弹簧回弹
/// （参数与 Flutter 的 <c>BouncingScrollPhysics</c> 一致）。
/// </summary>
public sealed class BouncingPhysics : ScrollPhysics
{
    /// <summary>每秒速度保留的比例（UIScrollView 的 0.998 每毫秒 → 0.135 每秒）。</summary>
    public const float Drag = 0.135f;

    /// <summary>越界回弹弹簧：质量 0.5、刚度 100、阻尼比 1.1（过阻尼，不来回振荡）。</summary>
    public static readonly SpringDescription Spring = SpringDescription.WithDampingRatio(0.5f, 100f, 1.1f);

    /// <summary>撞到边界时转交给弹簧的最大速度。</summary>
    public const float MaxSpringTransferVelocity = 5000f;

    public override OverscrollMode Overscroll => OverscrollMode.Bounce;

    public override double ApplyDrag(in ScrollMetrics m, double delta)
    {
        if (delta == 0 || double.IsNaN(delta) || double.IsInfinity(delta)) return 0;
        if (!m.OutOfRange) return delta;
        double pastStart = Math.Max(m.MinScrollExtent - m.Pixels, 0);
        double pastEnd = Math.Max(m.Pixels - m.MaxScrollExtent, 0);
        double past = Math.Max(pastStart, pastEnd);
        // 向范围内回拉：按回拉后的越界量计算摩擦
        bool easing = (pastStart > 0 && delta > 0) || (pastEnd > 0 && delta < 0);
        double viewport = Math.Max(1, m.ViewportDimension);
        double friction = easing ? FrictionFactor((past - Math.Abs(delta)) / viewport) : FrictionFactor(past / viewport);
        double abs = Math.Abs(delta);
        double applied = easing ? ApplyFriction(past, abs, friction) : abs * friction;
        return Math.Sign(delta) * applied;
    }

    static double FrictionFactor(double overscrollFraction)
    {
        double f = 1 - Math.Max(0, Math.Min(1, overscrollFraction));
        return 0.52 * f * f;
    }

    static double ApplyFriction(double extentOutside, double absDelta, double gamma)
    {
        if (gamma <= 0) return absDelta;
        double total = 0;
        if (extentOutside > 0)
        {
            double deltaToLimit = extentOutside / gamma;
            if (absDelta < deltaToLimit) return absDelta * gamma;
            total += extentOutside;
            absDelta -= deltaToLimit;
        }
        return total + absDelta;
    }

    public override Simulation? CreateFling(in ScrollMetrics m, double velocity)
    {
        velocity = ClampVelocity(velocity);
        if (velocity == 0 && !m.OutOfRange) return null;
        return new BouncingScrollSimulation(m.Pixels, velocity, m.MinScrollExtent, m.MaxScrollExtent, Spring, Drag, MaxSpringTransferVelocity);
    }
}

/// <summary>
/// Android 风格：不越界，惯性用 Android <c>OverScroller</c> 的样条减速曲线（Flutter <c>ClampingScrollSimulation</c> 的拟合），到边界即停。
/// 越界呈现声明为 <see cref="OverscrollMode.Stretch"/>（拉伸效果的绘制尚未实现）。
/// </summary>
public sealed class ClampingPhysics : ScrollPhysics
{
    /// <summary>滚动摩擦（ViewConfiguration.getScrollFriction）。</summary>
    public const float Friction = 0.015f;

    public override OverscrollMode Overscroll => OverscrollMode.Stretch;

    public override double ApplyDrag(in ScrollMetrics m, double delta) =>
        double.IsNaN(delta) || double.IsInfinity(delta) ? 0 : ClampDrag(m, delta);

    public override Simulation? CreateFling(in ScrollMetrics m, double velocity)
    {
        if (m.OutOfRange)
            return new ClampedSimulation(new SpringSimulation(BouncingPhysics.Spring, m.Pixels, m.Pixels < m.MinScrollExtent ? m.MinScrollExtent : m.MaxScrollExtent, 0),
                double.NegativeInfinity, double.PositiveInfinity);
        velocity = ClampVelocity(velocity);
        if (velocity == 0) return null;
        if ((velocity < 0 && m.Pixels <= m.MinScrollExtent) || (velocity > 0 && m.Pixels >= m.MaxScrollExtent)) return null;
        return new ClampedSimulation(new ClampingScrollSimulation(m.Pixels, velocity, Friction), m.MinScrollExtent, m.MaxScrollExtent);
    }
}

/// <summary>
/// 桌面：不越界。滚轮的平滑动画与触控板跟随由滚动系统处理（触控板有系统惯性相位时不叠加自己的惯性）；
/// 触摸屏拖动松手后的惯性沿用 <see cref="ClampingPhysics"/> 的样条曲线。
/// </summary>
public sealed class DesktopPhysics : ScrollPhysics
{
    /// <summary>单格滚轮平滑动画的时长（毫秒）；动画进行中的新增量叠加到目标上并重新计时。</summary>
    public const double WheelAnimationMs = 140;

    static readonly ClampingPhysics s_clamping = new();

    public override OverscrollMode Overscroll => OverscrollMode.None;

    public override double ApplyDrag(in ScrollMetrics m, double delta) =>
        double.IsNaN(delta) || double.IsInfinity(delta) ? 0 : ClampDrag(m, delta);

    public override Simulation? CreateFling(in ScrollMetrics m, double velocity) => s_clamping.CreateFling(m, velocity);
}
