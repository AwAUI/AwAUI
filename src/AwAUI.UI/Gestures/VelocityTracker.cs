using System;
using System.Numerics;

namespace AwAUI.UI;

/// <summary>
/// 速度追踪（design/21「速度追踪」）：对最近约 100 ms 的采样按时间做二次多项式的加权最小二乘拟合，取 t = 0（最新采样）处的一阶导数。
/// <list type="bullet">
/// <item>采样不足 3 个，或最新采样之前停顿超过 <see cref="AssumePointerStoppedMs"/> 毫秒，返回零。</item>
/// <item>权重：40 ms 以内为 1，更早的线性降到 100 ms 处的 0.5（近期的方向更可信）。</item>
/// <item>只保存最近 <see cref="HistorySize"/> 个采样（环形缓冲），时间戳倒退的采样使历史重置。</item>
/// </list>
/// 纯计算、确定性：给定同样的「时间 + 位置」序列总得到同样的速度。
/// </summary>
public sealed class VelocityTracker
{
    /// <summary>拟合使用的时间窗口（毫秒）。</summary>
    public const double HorizonMs = 100;

    /// <summary>相邻采样间隔超过它视为指针曾停下，更早的采样不参与拟合（毫秒）。</summary>
    public const double AssumePointerStoppedMs = 40;

    /// <summary>环形缓冲的容量。</summary>
    public const int HistorySize = 20;

    const int MinSamples = 3;

    readonly double[] _t = new double[HistorySize];
    readonly float[] _x = new float[HistorySize];
    readonly float[] _y = new float[HistorySize];
    int _index = -1;
    int _count;

    /// <summary>加入一个采样。非有限的位置被忽略。</summary>
    public void AddSample(TimeSpan time, Point position)
    {
        if (float.IsNaN(position.X) || float.IsNaN(position.Y) || float.IsInfinity(position.X) || float.IsInfinity(position.Y)) return;
        double ms = time.TotalMilliseconds;
        if (_count > 0 && ms < _t[_index]) Reset();
        _index = (_index + 1) % HistorySize;
        _t[_index] = ms;
        _x[_index] = position.X;
        _y[_index] = position.Y;
        if (_count < HistorySize) _count++;
    }

    /// <summary>清空历史（新的一次按下）。</summary>
    public void Reset()
    {
        _index = -1;
        _count = 0;
    }

    /// <summary>当前速度（逻辑像素每秒）；采样不足或停顿后为零。</summary>
    public Vector2 GetVelocity()
    {
        if (_count == 0) return Vector2.Zero;
        Span<double> ts = stackalloc double[HistorySize];
        Span<double> xs = stackalloc double[HistorySize];
        Span<double> ys = stackalloc double[HistorySize];
        Span<double> ws = stackalloc double[HistorySize];
        double newest = _t[_index];
        double previous = newest;
        int n = 0;
        int idx = _index;
        for (int k = 0; k < _count; k++)
        {
            double t = _t[idx];
            double age = newest - t;
            if (age > HorizonMs || previous - t > AssumePointerStoppedMs) break;
            previous = t;
            ts[n] = -age;
            xs[n] = _x[idx];
            ys[n] = _y[idx];
            ws[n] = age <= 40 ? 1.0 : 1.0 - 0.5 * (age - 40) / (HorizonMs - 40);
            n++;
            idx = (idx - 1 + HistorySize) % HistorySize;
        }
        if (n < MinSamples) return Vector2.Zero;
        if (!Fit(ts.Slice(0, n), xs.Slice(0, n), ws.Slice(0, n), out double vx)) return Vector2.Zero;
        if (!Fit(ts.Slice(0, n), ys.Slice(0, n), ws.Slice(0, n), out double vy)) return Vector2.Zero;
        // 时间以毫秒计：系数乘以 1000 得到每秒
        return new Vector2((float)(vx * 1000), (float)(vy * 1000));
    }

    /// <summary>
    /// 加权最小二乘拟合 y ≈ b0 + b1·t + b2·t²，返回 b1（t = 0 处的导数）。用改进的 Gram–Schmidt 做 QR 分解（数值稳定）。
    /// 设计矩阵退化（时间戳全部相同等）时返回 false。
    /// </summary>
    static bool Fit(ReadOnlySpan<double> t, ReadOnlySpan<double> y, ReadOnlySpan<double> w, out double slope)
    {
        const int degree = 2;
        const int m = degree + 1;
        int n = t.Length;
        slope = 0;
        // A 的列 j：sqrt(w_i) · t_i^j
        Span<double> a = stackalloc double[m * HistorySize];
        Span<double> q = stackalloc double[m * HistorySize];
        Span<double> r = stackalloc double[m * m];
        for (int i = 0; i < n; i++)
        {
            double sw = Math.Sqrt(w[i]);
            a[i] = sw;
            for (int j = 1; j < m; j++) a[j * HistorySize + i] = a[(j - 1) * HistorySize + i] * t[i];
        }
        for (int j = 0; j < m; j++)
        {
            for (int i = 0; i < n; i++) q[j * HistorySize + i] = a[j * HistorySize + i];
            for (int k = 0; k < j; k++)
            {
                double dot = 0;
                for (int i = 0; i < n; i++) dot += q[j * HistorySize + i] * q[k * HistorySize + i];
                for (int i = 0; i < n; i++) q[j * HistorySize + i] -= dot * q[k * HistorySize + i];
            }
            double norm = 0;
            for (int i = 0; i < n; i++) norm += q[j * HistorySize + i] * q[j * HistorySize + i];
            norm = Math.Sqrt(norm);
            if (norm < 1e-6) return false;
            for (int i = 0; i < n; i++) q[j * HistorySize + i] /= norm;
            for (int k = 0; k < m; k++)
            {
                double dot = 0;
                for (int i = 0; i < n; i++) dot += q[j * HistorySize + i] * a[k * HistorySize + i];
                r[j * m + k] = k < j ? 0 : dot;
            }
        }
        // 解 R·b = Qᵀ·(sqrt(w)·y)
        Span<double> b = stackalloc double[m];
        for (int j = m - 1; j >= 0; j--)
        {
            double sum = 0;
            for (int i = 0; i < n; i++) sum += q[j * HistorySize + i] * y[i] * Math.Sqrt(w[i]);
            for (int k = m - 1; k > j; k--) sum -= r[j * m + k] * b[k];
            double d = r[j * m + j];
            if (Math.Abs(d) < 1e-12) return false;
            b[j] = sum / d;
        }
        slope = b[1];
        return !(double.IsNaN(slope) || double.IsInfinity(slope));
    }
}
