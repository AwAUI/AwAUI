using System.Numerics;

namespace AwAUI.UI.UnitTests;

/// <summary>速度追踪：对最近约 100 ms 的采样做加权最小二乘拟合（design/21「速度追踪」）。</summary>
public class GestureVelocityTrackerTests
{
    static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Constant_velocity_is_recovered_exactly()
    {
        var t = new VelocityTracker();
        for (int i = 0; i <= 10; i++) t.AddSample(Ms(1000 + i * 8), new Point(10 + i * 8 * 0.5f, 300 - i * 8 * 1.2f));
        var v = t.GetVelocity();
        Assert.Equal(500, v.X, 1);
        Assert.Equal(-1200, v.Y, 1);
    }

    [Fact]
    public void Quadratic_motion_reports_the_latest_velocity_not_the_average()
    {
        // x = 0.002·t²（t 毫秒）：t = 80 ms 处速度 0.32 px/ms = 320 px/s，平均速度只有一半
        var t = new VelocityTracker();
        for (int i = 0; i <= 10; i++)
        {
            double ms = i * 8;
            t.AddSample(Ms(ms), new Point((float)(0.002 * ms * ms), 0));
        }
        Assert.Equal(320, t.GetVelocity().X, 0);
    }

    [Fact]
    public void Too_few_samples_or_a_pause_give_zero()
    {
        var t = new VelocityTracker();
        Assert.Equal(Vector2.Zero, t.GetVelocity());
        t.AddSample(Ms(0), new Point(0, 0));
        t.AddSample(Ms(8), new Point(10, 0));
        Assert.Equal(Vector2.Zero, t.GetVelocity());
        t.AddSample(Ms(16), new Point(20, 0));
        Assert.NotEqual(Vector2.Zero, t.GetVelocity());
        // 停顿超过 40 ms 后再来一个采样：只有 1 个有效采样
        t.AddSample(Ms(80), new Point(20, 0));
        Assert.Equal(Vector2.Zero, t.GetVelocity());
    }

    [Fact]
    public void Old_samples_outside_the_horizon_are_ignored_and_reset_on_time_going_backwards()
    {
        var t = new VelocityTracker();
        // 早于 100 ms 的快速运动不影响结果
        for (int i = 0; i < 5; i++) t.AddSample(Ms(i * 8), new Point(i * 100, 0));
        for (int i = 0; i < 12; i++) t.AddSample(Ms(40 + i * 10), new Point(400 + i * 2, 0));
        Assert.Equal(200, t.GetVelocity().X, 1);
        t.AddSample(Ms(5), new Point(0, 0)); // 时间倒退：历史重置
        Assert.Equal(Vector2.Zero, t.GetVelocity());
    }

    [Fact]
    public void Identical_timestamps_and_non_finite_positions_do_not_produce_garbage()
    {
        var t = new VelocityTracker();
        for (int i = 0; i < 5; i++) t.AddSample(Ms(100), new Point(i, i));
        Assert.Equal(Vector2.Zero, t.GetVelocity());
        t.AddSample(Ms(110), new Point(float.NaN, 0));
        t.AddSample(Ms(120), new Point(float.PositiveInfinity, 0));
        var v = t.GetVelocity();
        Assert.False(float.IsNaN(v.X) || float.IsInfinity(v.X));
    }

    [Fact]
    public void Results_are_deterministic()
    {
        Vector2 Run()
        {
            var t = new VelocityTracker();
            var rnd = new Random(42);
            double ms = 0;
            float x = 0;
            for (int i = 0; i < 30; i++)
            {
                ms += 4 + rnd.NextDouble() * 8;
                x += (float)(rnd.NextDouble() * 10);
                t.AddSample(Ms(ms), new Point(x, -x));
            }
            return t.GetVelocity();
        }
        Assert.Equal(Run(), Run());
    }
}
