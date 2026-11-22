using System;

namespace AwAUI.UI;

/// <summary>
/// 一段一维运动模拟（惯性、回弹、平滑滚动）。纯函数、不可变：同一时间点总是给出同样的结果，可以交给任何线程求值（design/18、21）。
/// 时间从模拟创建时刻起算（秒）。
/// </summary>
public abstract class Simulation
{
    /// <summary>距离容差：位置与目标的差小于它视为到达（逻辑像素）。</summary>
    public const float DistanceTolerance = 0.5f;

    /// <summary>速度容差：速度小于它视为停止（逻辑像素每秒）。</summary>
    public const float VelocityTolerance = 10f;

    public abstract double Position(double seconds);
    public abstract double Velocity(double seconds);
    public abstract bool IsDone(double seconds);
}

/// <summary>弹簧参数（质量、刚度、阻尼系数）。</summary>
public readonly struct SpringDescription
{
    public SpringDescription(float mass, float stiffness, float damping)
    {
        Mass = mass > 0 ? mass : 1;
        Stiffness = stiffness > 0 ? stiffness : 1;
        Damping = damping >= 0 ? damping : 0;
    }

    /// <summary>按阻尼比（1 = 临界阻尼）给出弹簧。</summary>
    public static SpringDescription WithDampingRatio(float mass, float stiffness, float ratio) =>
        new(mass, stiffness, (float)(ratio * 2 * Math.Sqrt(Math.Max(1e-6, mass) * Math.Max(1e-6, stiffness))));

    public float Mass { get; }
    public float Stiffness { get; }
    public float Damping { get; }
}

/// <summary>阻尼弹簧：从 <c>start</c> 以初速度 <c>velocity</c> 运动到 <c>end</c>（临界、过阻尼、欠阻尼三种解析解）。</summary>
public sealed class SpringSimulation : Simulation
{
    readonly double _end;
    readonly double _r1, _r2, _c1, _c2, _w;
    readonly int _kind; // 0 临界，1 过阻尼，2 欠阻尼

    public SpringSimulation(SpringDescription spring, double start, double end, double velocity)
    {
        _end = end;
        double m = spring.Mass, k = spring.Stiffness, c = spring.Damping;
        double d = start - (double)end, v = Sanitize(velocity);
        double cmk = c * c - 4 * m * k;
        if (Math.Abs(cmk) < 1e-9)
        {
            _kind = 0;
            _r1 = -c / (2 * m);
            _c1 = d;
            _c2 = v - _r1 * d;
        }
        else if (cmk > 0)
        {
            _kind = 1;
            double s = Math.Sqrt(cmk);
            _r1 = (-c - s) / (2 * m);
            _r2 = (-c + s) / (2 * m);
            _c2 = (v - _r1 * d) / (_r2 - _r1);
            _c1 = d - _c2;
        }
        else
        {
            _kind = 2;
            _w = Math.Sqrt(4 * m * k - c * c) / (2 * m);
            _r1 = -(c / 2 / m);
            _c1 = d;
            _c2 = (v - _r1 * d) / _w;
        }
    }

    static double Sanitize(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : v;

    double X(double t) => _kind switch
    {
        0 => (_c1 + _c2 * t) * Math.Exp(_r1 * t),
        1 => _c1 * Math.Exp(_r1 * t) + _c2 * Math.Exp(_r2 * t),
        _ => Math.Exp(_r1 * t) * (_c1 * Math.Cos(_w * t) + _c2 * Math.Sin(_w * t)),
    };

    double Dx(double t)
    {
        switch (_kind)
        {
            case 0:
            {
                double e = Math.Exp(_r1 * t);
                return _r1 * (_c1 + _c2 * t) * e + _c2 * e;
            }
            case 1:
                return _c1 * _r1 * Math.Exp(_r1 * t) + _c2 * _r2 * Math.Exp(_r2 * t);
            default:
            {
                double e = Math.Exp(_r1 * t);
                double cos = Math.Cos(_w * t), sin = Math.Sin(_w * t);
                return e * (_c2 * _w * cos - _c1 * _w * sin) + _r1 * e * (_c2 * sin + _c1 * cos);
            }
        }
    }

    public override double Position(double seconds) => _end + X(Math.Max(0, seconds));

    public override double Velocity(double seconds) => Dx(Math.Max(0, seconds));

    public override bool IsDone(double seconds)
    {
        double t = Math.Max(0, seconds);
        return Math.Abs(X(t)) < DistanceTolerance && Math.Abs(Dx(t)) < VelocityTolerance;
    }
}

/// <summary>摩擦衰减：速度每秒乘以 <c>drag</c>（指数衰减），位置趋于 <see cref="FinalPosition"/>。</summary>
public sealed class FrictionSimulation : Simulation
{
    readonly double _drag, _dragLog, _x, _v;

    public FrictionSimulation(double drag, double position, double velocity)
    {
        _drag = drag > 0 && drag < 1 ? drag : 0.135;
        _dragLog = Math.Log(_drag);
        _x = position;
        _v = double.IsNaN(velocity) || double.IsInfinity(velocity) ? 0 : velocity;
    }

    /// <summary>运动停止时的位置。</summary>
    public double FinalPosition => _x - _v / _dragLog;

    public override double Position(double seconds)
    {
        double t = Math.Max(0, seconds);
        return _x + _v * Math.Pow(_drag, t) / _dragLog - _v / _dragLog;
    }

    public override double Velocity(double seconds) => _v * Math.Pow(_drag, Math.Max(0, seconds));

    /// <summary>到达位置 <paramref name="x"/> 的时间；永远到不了时为正无穷。</summary>
    public double TimeAt(double x)
    {
        if (x == _x) return 0;
        double fin = FinalPosition;
        if (_v == 0 || (_v > 0 ? (x < _x || x > fin) : (x > _x || x < fin))) return double.PositiveInfinity;
        return Math.Log(_dragLog * (x - _x) / _v + 1) / _dragLog;
    }

    public override bool IsDone(double seconds) => Math.Abs(Velocity(seconds)) < VelocityTolerance;
}

/// <summary>iOS 式惯性：范围内按摩擦衰减，越过边界的时刻转为弹簧回到边界；起始就越界时直接用弹簧。</summary>
public sealed class BouncingScrollSimulation : Simulation
{
    readonly FrictionSimulation? _friction;
    readonly SpringSimulation? _spring;
    readonly double _springTime;

    public BouncingScrollSimulation(double position, double velocity, double leadingExtent, double trailingExtent, SpringDescription spring,
        double drag = BouncingPhysics.Drag, double maxSpringTransferVelocity = BouncingPhysics.MaxSpringTransferVelocity)
    {
        trailingExtent = Math.Max(leadingExtent, trailingExtent);
        if (position < leadingExtent)
        {
            _spring = new SpringSimulation(spring, position, leadingExtent, velocity);
            _springTime = double.NegativeInfinity;
        }
        else if (position > trailingExtent)
        {
            _spring = new SpringSimulation(spring, position, trailingExtent, velocity);
            _springTime = double.NegativeInfinity;
        }
        else
        {
            _friction = new FrictionSimulation(drag, position, velocity);
            double fin = _friction.FinalPosition;
            if (velocity > 0 && fin > trailingExtent)
            {
                _springTime = _friction.TimeAt(trailingExtent);
                _spring = new SpringSimulation(spring, trailingExtent, trailingExtent,
                    Math.Min(_friction.Velocity(_springTime), maxSpringTransferVelocity));
            }
            else if (velocity < 0 && fin < leadingExtent)
            {
                _springTime = _friction.TimeAt(leadingExtent);
                _spring = new SpringSimulation(spring, leadingExtent, leadingExtent,
                    Math.Max(_friction.Velocity(_springTime), -maxSpringTransferVelocity));
            }
            else
            {
                _springTime = double.PositiveInfinity;
            }
        }
    }

    public override double Position(double seconds) =>
        seconds < _springTime ? _friction!.Position(seconds) : _spring!.Position(seconds - Math.Max(0, _springTime));

    public override double Velocity(double seconds) =>
        seconds < _springTime ? _friction!.Velocity(seconds) : _spring!.Velocity(seconds - Math.Max(0, _springTime));

    public override bool IsDone(double seconds) =>
        seconds < _springTime ? _friction!.IsDone(seconds) : _spring!.IsDone(seconds - Math.Max(0, _springTime));
}

/// <summary>
/// Android <c>OverScroller</c> 的样条减速（Flutter 的多项式拟合）：时长由初速度决定，位置按 1.2t³ − 3.27t² + 3.065t 推进。
/// </summary>
public sealed class ClampingScrollSimulation : Simulation
{
    const double DecelerationRate = 2.358201815; // ln(0.78) / ln(0.9)
    const double Inflexion = 0.35;
    const double InitialVelocityPenetration = 3.065;

    readonly double _position, _velocity, _duration, _distance;

    public ClampingScrollSimulation(double position, double velocity, double friction = ClampingPhysics.Friction)
    {
        _position = position;
        _velocity = double.IsNaN(velocity) || double.IsInfinity(velocity) ? 0 : velocity;
        double decel = (friction > 0 ? friction : ClampingPhysics.Friction) * 61774.04968; // 9.80665 · 39.37 · 160 · friction
        double v = Math.Abs(_velocity);
        if (v < 1e-6)
        {
            _duration = 0;
            _distance = 0;
            return;
        }
        double l = Math.Log(Inflexion * v / decel);
        _duration = Math.Exp(l / (DecelerationRate - 1.0));
        _distance = Math.Abs(_velocity * _duration / InitialVelocityPenetration);
    }

    /// <summary>运动总时长（秒）。</summary>
    public double Duration => _duration;

    public override double Position(double seconds)
    {
        if (_duration <= 0) return _position;
        double t = Math.Max(0, Math.Min(1, seconds / _duration));
        return _position + _distance * (1.2 * t * t * t - 3.27 * t * t + 3.065 * t) * Math.Sign(_velocity);
    }

    public override double Velocity(double seconds)
    {
        if (_duration <= 0) return 0;
        double t = Math.Max(0, Math.Min(1, seconds / _duration));
        return _velocity * (3.6 * t * t - 6.54 * t + 3.065) / InitialVelocityPenetration;
    }

    public override bool IsDone(double seconds) => seconds >= _duration;
}

/// <summary>把另一段模拟的位置钳制到 [min, max]；到达边界即结束。</summary>
public sealed class ClampedSimulation : Simulation
{
    readonly Simulation _inner;
    readonly double _min, _max;

    public ClampedSimulation(Simulation inner, double min, double max)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _min = min;
        _max = Math.Max(min, max);
    }

    public override double Position(double seconds) => Math.Max(_min, Math.Min(_max, _inner.Position(seconds)));

    public override double Velocity(double seconds) => AtBound(seconds, out double v) ? 0 : v;

    public override bool IsDone(double seconds) => _inner.IsDone(seconds) || AtBound(seconds, out _);

    /// <summary>
    /// 位置在边界上且（内层的）速度朝外或为零：被边界挡住。从边界出发、朝范围内运动的模拟（在顶端向下甩）不算到达边界。
    /// </summary>
    bool AtBound(double seconds, out double velocity)
    {
        double p = _inner.Position(seconds);
        velocity = _inner.Velocity(seconds);
        return (p <= _min && !(velocity > 0)) || (p >= _max && !(velocity < 0));
    }
}

/// <summary>
/// 定时长的平滑滚动（滚轮动画、<see cref="ScrollController.ScrollTo"/>）：从 <c>from</c> 到 <c>to</c>，
/// 曲线为三次缓出（滚轮，起步快）或三次缓入缓出（程序滚动）。
/// </summary>
public sealed class SmoothScrollSimulation : Simulation
{
    readonly double _from, _to, _duration;
    readonly ScrollEasing _easing;

    public SmoothScrollSimulation(double from, double to, TimeSpan duration, ScrollEasing easing = ScrollEasing.EaseOut)
    {
        _from = from;
        _to = to;
        _duration = Math.Max(0, duration.TotalSeconds);
        _easing = easing;
    }

    public double Target => _to;

    public override double Position(double seconds)
    {
        if (_duration <= 0 || seconds >= _duration) return _to;
        double s = Math.Max(0, seconds / _duration);
        return _from + (_to - _from) * Ease(s);
    }

    double Ease(double s) => _easing switch
    {
        ScrollEasing.Linear => s,
        ScrollEasing.EaseInOut => s < 0.5 ? 4 * s * s * s : 1 - Math.Pow(-2 * s + 2, 3) / 2,
        _ => 1 - Math.Pow(1 - s, 3),
    };

    public override double Velocity(double seconds)
    {
        if (_duration <= 0 || seconds >= _duration || seconds < 0) return 0;
        double s = seconds / _duration;
        double d = _easing switch
        {
            ScrollEasing.Linear => 1,
            ScrollEasing.EaseInOut => s < 0.5 ? 12 * s * s : 3 * Math.Pow(-2 * s + 2, 2),
            _ => 3 * (1 - s) * (1 - s),
        };
        return (_to - _from) * d / _duration;
    }

    public override bool IsDone(double seconds) => seconds >= _duration;
}

/// <summary>平滑滚动的缓动曲线。</summary>
public enum ScrollEasing : byte
{
    /// <summary>三次缓出：起步快、平稳停下（滚轮）。</summary>
    EaseOut,

    /// <summary>三次缓入缓出（程序滚动的默认值）。</summary>
    EaseInOut,

    Linear,
}
