using System;
using System.Numerics;
using AwAUI.Graphics;

namespace AwAUI.Render.Raster;

/// <summary>为一行像素产生预乘颜色（RGBA，0..1）。</summary>
internal abstract class Shader
{
    /// <summary>所有像素颜色相同（可跳过逐像素求值）。</summary>
    public virtual bool IsConstant => false;

    public abstract void Shade(int x, int y, Span<Vector4> destination);

    /// <summary>非预乘 → 预乘（NaN 分量按 0，与 GPU 后端相同）。</summary>
    public static Vector4 Premultiply(Color c) => GradientLut.Premultiply(c);

    /// <summary>
    /// 按画刷创建着色器。<paramref name="deviceTransform"/> 为局部坐标到设备像素的完整变换。
    /// 渐变的查找表从 <paramref name="luts"/> 取（按色标内容缓存）；为 null 时每次重新构造。
    /// </summary>
    public static Shader Create(Brush? brush, Color solid, in Matrix3x2 deviceTransform, GradientLutCache? luts = null)
    {
        switch (brush)
        {
            case null:
                return new SolidShader(Premultiply(solid));
            case SolidColorBrush s:
                return new SolidShader(Premultiply(s.Color));
            case GradientBrush g:
                // 设备像素 → 渐变空间：inverse(brush.Transform · deviceTransform)
                if (!Matrix3x2.Invert(g.Transform * deviceTransform, out var inv))
                    return new SolidShader(GradientLut.Premultiply(g.Stops[g.Stops.Count - 1].Color));
                return new GradientShader(g, inv, luts);
            default:
                return new SolidShader(Premultiply(solid));
        }
    }
}

internal sealed class SolidShader : Shader
{
    public SolidShader(Vector4 premultiplied) => Color = premultiplied;
    public Vector4 Color { get; }
    public override bool IsConstant => true;
    public override void Shade(int x, int y, Span<Vector4> destination) => destination.Fill(Color);
}

internal sealed class GradientShader : Shader
{
    const int LutSize = GradientLut.Size;
    readonly Vector4[] _lut;
    readonly Matrix3x2 _inverse;
    readonly GradientBrush _brush;
    readonly SpreadMode _spread;

    // 线性渐变
    readonly Vector2 _start;
    readonly Vector2 _axis;
    readonly float _invAxisLenSq;
    // 径向 / 锥形
    readonly Vector2 _center;
    readonly float _invRadius;
    readonly float _startAngle;
    // 双点锥形（带焦点的径向渐变），见 TwoPointConical
    readonly bool _conical;
    readonly double _fx, _fy, _norm, _cdx, _cdy, _r0, _dr, _a;

    public GradientShader(GradientBrush brush, Matrix3x2 inverse, GradientLutCache? luts = null)
    {
        _brush = brush;
        _inverse = inverse;
        _spread = brush.Spread;
        if (luts is not null) _lut = luts.Get(brush);
        else
        {
            _lut = new Vector4[LutSize];
            GradientLut.Build(brush.Stops, _lut);
        }
        switch (brush)
        {
            case LinearGradientBrush lin:
                _start = lin.Start;
                _axis = (Vector2)lin.End - _start;
                float lenSq = _axis.LengthSquared();
                _invAxisLenSq = lenSq > 1e-12f ? 1 / lenSq : 0;
                break;
            case RadialGradientBrush { HasFocus: true } rad:
                _conical = true;
                ConicalParameters(rad, out _fx, out _fy, out _norm, out _cdx, out _cdy, out _r0, out _dr, out _a);
                break;
            case RadialGradientBrush rad:
                _center = rad.Center;
                _invRadius = rad.Radius > 1e-6f ? 1 / rad.Radius : 0;
                break;
            case ConicGradientBrush con:
                _center = con.Center;
                _startAngle = con.StartAngle;
                break;
        }
    }

    /// <summary>
    /// 双点锥形渐变的归一化参数（GPU 后端用同一组参数，见 <c>QuadPaint.TwoPointConical</c>）：以焦点为原点、长度除以
    /// <paramref name="norm"/>（结束圆半径、起始圆半径与圆心距中的最大者），使各量为 O(1)。
    /// cd = 两圆心之差，r0 = 起始圆半径，dr = 半径之差，a = cd·cd − dr²。
    /// </summary>
    internal static void ConicalParameters(RadialGradientBrush rad, out double fx, out double fy, out double norm,
        out double cdx, out double cdy, out double r0, out double dr, out double a)
    {
        fx = rad.FocalPoint.X;
        fy = rad.FocalPoint.Y;
        double dx = (double)rad.Center.X - fx, dy = (double)rad.Center.Y - fy;
        double r1 = rad.Radius, rf = rad.FocalRadius;
        norm = Math.Max(Math.Max(Math.Abs(r1), rf), Math.Sqrt(dx * dx + dy * dy));
        if (!(norm > 0) || double.IsInfinity(norm)) norm = 1;
        cdx = dx / norm;
        cdy = dy / norm;
        r0 = rf / norm;
        dr = (r1 - rf) / norm;
        a = cdx * cdx + cdy * cdy - dr * dr;
    }

    /// <summary>|a| 小于此值时按「焦点恰在结束圆上」的一次方程求解（归一化单位）；否则二次方程的一个根会因相消而失真。</summary>
    internal const double ConicalLinearEpsilon = 1e-5;

    /// <summary>
    /// 双点锥形渐变在（归一化、以焦点为原点的）点 (px, py) 处的参数 t：圆心 c(t) = t·cd、半径 r(t) = r0 + t·dr，
    /// 取 |p − c(t)| = r(t) 且 r(t) ≥ 0 的最大 t；不存在时返回 NaN（该像素不绘制）。
    /// </summary>
    internal static double TwoPointConical(double px, double py, double cdx, double cdy, double r0, double dr, double a)
    {
        double b = px * cdx + py * cdy + r0 * dr;
        double c = px * px + py * py - r0 * r0;
        if (Math.Abs(a) < ConicalLinearEpsilon)
        {
            // a ≈ 0：−2bt + c = 0
            if (b == 0) return double.NaN;
            double t = c / (2 * b);
            return r0 + t * dr >= 0 ? t : double.NaN;
        }
        double disc = b * b - a * c;
        if (disc < 0) return double.NaN;
        double sq = Math.Sqrt(disc);
        double t1 = (b + sq) / a, t2 = (b - sq) / a;
        double hi = Math.Max(t1, t2), lo = Math.Min(t1, t2);
        if (r0 + hi * dr >= 0) return hi;
        if (r0 + lo * dr >= 0) return lo;
        return double.NaN;
    }

    float[] _t = Array.Empty<float>();

    /// <summary>
    /// 先求整行的渐变参数 t（线性与径向走 <see cref="Kernels"/> 的三档内核），再查表插值。
    /// 第 i 个像素的位置按 p0 + i·step 计算（而不是逐像素累加），各档逐元素运算相同，结果逐字节一致。
    /// t 为 NaN 的像素（双点锥形渐变中没有经过它的圆、或非有限的几何）输出透明。
    /// </summary>
    public override void Shade(int x, int y, Span<Vector4> destination)
    {
        int n = destination.Length;
        if (_t.Length < n) _t = new float[Math.Max(n, 64)];
        var t = new Span<float>(_t, 0, n);
        // 本行第 0 列像素中心；第 c 列为 p + c·step（c 为绝对列号）。按绝对列号求值使每个像素的结果与行从哪一列开始
        // （裁剪区、去掉零覆盖之后的起点）无关
        var p = Vector2.Transform(new Vector2(0.5f, y + 0.5f), _inverse);
        var step = new Vector2(_inverse.M11, _inverse.M12);
        bool pad = _spread == SpreadMode.Pad;
        switch (_brush)
        {
            case LinearGradientBrush:
            {
                float t0 = ((p.X - _start.X) * _axis.X + (p.Y - _start.Y) * _axis.Y) * _invAxisLenSq;
                float dt = (step.X * _axis.X + step.Y * _axis.Y) * _invAxisLenSq;
                Kernels.Ramp(t, t0, dt, pad, x);
                break;
            }
            case RadialGradientBrush when _conical:
            {
                double inv = 1 / _norm;
                for (int i = 0; i < n; i++)
                {
                    double px = ((double)p.X + (x + i) * (double)step.X - _fx) * inv;
                    double py = ((double)p.Y + (x + i) * (double)step.Y - _fy) * inv;
                    t[i] = (float)TwoPointConical(px, py, _cdx, _cdy, _r0, _dr, _a);
                }
                pad = false;
                break;
            }
            case RadialGradientBrush:
                if (_invRadius > 0) Kernels.RadialDistance(t, p.X - _center.X, p.Y - _center.Y, step.X, step.Y, _invRadius, pad, x);
                else t.Fill(1);
                break;
            default:
                for (int i = 0; i < n; i++)
                {
                    float k = x + i;
                    float dx = p.X + k * step.X - _center.X;
                    float dy = p.Y + k * step.Y - _center.Y;
                    float a = MathF.Atan2(dy, dx) - _startAngle;
                    a %= 2 * MathF.PI;
                    if (a < 0) a += 2 * MathF.PI;
                    t[i] = a / (2 * MathF.PI);
                }
                pad = false;
                break;
        }
        if (!pad)
            for (int i = 0; i < n; i++) t[i] = ApplySpread(t[i]);

        Sample(t, destination);
    }

    unsafe void Sample(ReadOnlySpan<float> t, Span<Vector4> destination)
    {
        fixed (Vector4* lut = _lut)
        fixed (float* pt = t)
        fixed (Vector4* d = destination)
        {
            for (int i = 0; i < destination.Length; i++)
            {
                float f = pt[i] * (LutSize - 1);
                if (float.IsNaN(f))
                {
                    d[i] = default;
                    continue;
                }
                int k = (int)f;
                if (k >= LutSize - 1) d[i] = lut[LutSize - 1];
                else if (k < 0) d[i] = lut[0];
                else
                {
                    var a = lut[k];
                    d[i] = a + (lut[k + 1] - a) * (f - k);
                }
            }
        }
    }

    float ApplySpread(float t)
    {
        switch (_spread)
        {
            case SpreadMode.Repeat:
                return t - MathF.Floor(t);
            case SpreadMode.Reflect:
            {
                float m = t % 2f;
                if (m < 0) m += 2;
                return m > 1 ? 2 - m : m;
            }
            default:
                return t < 0 ? 0 : t > 1 ? 1 : t;
        }
    }
}

/// <summary>从位图读取预乘像素（用于图层合成）。</summary>
internal sealed class BitmapRowShader : Shader
{
    readonly Bitmap _bitmap;
    readonly int _offsetX, _offsetY;

    public BitmapRowShader(Bitmap bitmap, int offsetX, int offsetY)
    {
        _bitmap = bitmap; _offsetX = offsetX; _offsetY = offsetY;
    }

    public override void Shade(int x, int y, Span<Vector4> destination)
    {
        var row = _bitmap.GetRow(y - _offsetY);
        int start = (x - _offsetX) * 4;
        bool bgra = _bitmap.Format == PixelFormat.Bgra8888;
        const float k = 1 / 255f;
        for (int i = 0; i < destination.Length; i++)
        {
            int o = start + i * 4;
            float r = row[o] * k, g = row[o + 1] * k, b = row[o + 2] * k, a = row[o + 3] * k;
            destination[i] = bgra ? new Vector4(b, g, r, a) : new Vector4(r, g, b, a);
        }
    }
}
