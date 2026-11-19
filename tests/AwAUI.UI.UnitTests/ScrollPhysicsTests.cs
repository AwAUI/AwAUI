namespace AwAUI.UI.UnitTests;

/// <summary>滚动物理与模拟：纯函数、确定性，在固定时间点求值比对（design/21「测试」）。</summary>
public class ScrollPhysicsTests
{
    static readonly ScrollMetrics InRange = new(500, 0, 2000, 600);

    [Fact]
    public void Friction_simulation_decays_exponentially_and_reports_its_final_position()
    {
        var sim = new FrictionSimulation(0.135f, 100, 1000);
        Assert.Equal(100, sim.Position(0), 3);
        Assert.Equal(1000, sim.Velocity(0), 3);
        // v(t) = v0 · drag^t
        Assert.Equal(1000 * 0.135, sim.Velocity(1), 2);
        // x(∞) = x0 − v0 / ln(drag) ≈ 100 + 499.5
        Assert.Equal(100 - 1000 / Math.Log(0.135), sim.FinalPosition, 2);
        Assert.Equal(sim.Position(0.5), sim.Position(0.5));
        Assert.True(sim.TimeAt(300) is > 0 and < 1);
        Assert.Equal(300, sim.Position(sim.TimeAt(300)), 2);
        Assert.True(double.IsPositiveInfinity(sim.TimeAt(10_000)));
        Assert.False(sim.IsDone(0.5));
        Assert.True(sim.IsDone(3));
    }

    [Fact]
    public void Spring_simulations_converge_to_the_target_for_all_damping_kinds()
    {
        foreach (var ratio in new[] { 0.5f, 1f, 1.1f, 3f })
        {
            var spring = SpringDescription.WithDampingRatio(0.5f, 100, ratio);
            var sim = new SpringSimulation(spring, -80, 0, 0);
            Assert.Equal(-80, sim.Position(0), 3);
            Assert.Equal(0, sim.Velocity(0), 3);
            Assert.True(Math.Abs(sim.Position(3)) < 0.5f, $"ratio {ratio}: {sim.Position(3)}");
            Assert.True(sim.IsDone(5));
            Assert.False(sim.IsDone(0));
            // 数值导数与解析速度一致
            double t = 0.1, h = 1e-4;
            double numeric = (sim.Position(t + h) - sim.Position(t - h)) / (2 * h);
            Assert.Equal(numeric, sim.Velocity(t), 0);
        }
        // 过阻尼（1.1，回弹物理所用）不越过目标
        var over = new SpringSimulation(BouncingPhysics.Spring, -80, 0, 0);
        for (double t = 0; t < 2; t += 0.01) Assert.True(over.Position(t) <= 0.001f);
    }

    [Fact]
    public void Bouncing_physics_rubber_bands_drags_and_springs_back_from_overscroll()
    {
        var p = new BouncingPhysics();
        Assert.Equal(OverscrollMode.Bounce, p.Overscroll);
        // 范围内：原样
        Assert.Equal(30, p.ApplyDrag(InRange, 30));
        // 越过起点 100 像素后继续向外拖：位移被阻尼（摩擦系数 0.52·(1 − 100/600)² ≈ 0.361）
        var outside = new ScrollMetrics(-100, 0, 2000, 600);
        float applied = p.ApplyDrag(outside, -20);
        Assert.Equal(-20 * 0.52 * Math.Pow(1 - 100.0 / 600, 2), applied, 3);
        // 向范围内拉回：越过边界的部分不再阻尼
        Assert.True(p.ApplyDrag(outside, 20) > applied * -1);
        // 越界松手：弹簧回到起点
        var back = p.CreateFling(outside, 0)!;
        Assert.NotNull(back);
        Assert.Equal(-100, back.Position(0), 3);
        Assert.True(Math.Abs(back.Position(2)) < 0.5f);
        // 范围内的惯性撞到终点后转为弹簧，最终停在终点
        var fling = p.CreateFling(new ScrollMetrics(1900, 0, 2000, 600), 3000)!;
        Assert.True(fling.Position(0.2) > 2000);
        Assert.Equal(2000, fling.Position(10), 0);
        // 低于最小速度不产生惯性
        Assert.Null(p.CreateFling(InRange, 20));
    }

    [Fact]
    public void Clamping_physics_never_overscrolls_and_follows_the_android_spline()
    {
        var p = new ClampingPhysics();
        Assert.Equal(-10, p.ApplyDrag(new ScrollMetrics(10, 0, 100, 50), -30));
        Assert.Equal(0, p.ApplyDrag(new ScrollMetrics(100, 0, 100, 50), 30));
        var sim = new ClampingScrollSimulation(0, 2000);
        Assert.Equal(0, sim.Position(0));
        Assert.Equal(2000, sim.Velocity(0), 1);
        Assert.True(sim.Duration is > 0.3 and < 3, sim.Duration.ToString());
        // 单调减速、到时长末停止；样条的总距离 = v·T / 3.065 ·（1.2 − 3.27 + 3.065）
        float prev = 0;
        for (double t = 0.05; t <= sim.Duration; t += 0.05)
        {
            float x = sim.Position(t);
            Assert.True(x >= prev);
            prev = x;
        }
        Assert.Equal(2000 * sim.Duration / 3.065 * (1.2 - 3.27 + 3.065), sim.Position(sim.Duration), 1);
        Assert.True(sim.IsDone(sim.Duration));
        // 拟合多项式在末端的速度约为初速度的 4%（之后模拟结束）
        Assert.True(Math.Abs(sim.Velocity(sim.Duration)) < 0.05f * 2000);
        // 带边界：撞到 max 即停
        var fling = p.CreateFling(new ScrollMetrics(90, 0, 100, 50), 2000)!;
        Assert.Equal(100, fling.Position(5));
        Assert.True(fling.IsDone(5));
        Assert.Null(p.CreateFling(new ScrollMetrics(100, 0, 100, 50), 2000));
    }

    [Fact]
    public void Desktop_physics_clamps_drags_and_reuses_the_spline_fling()
    {
        var p = new DesktopPhysics();
        Assert.Equal(OverscrollMode.None, p.Overscroll);
        Assert.Equal(-500, p.ApplyDrag(InRange, -900));
        Assert.Equal(1500, p.ApplyDrag(InRange, 9999));
        var fling = p.CreateFling(InRange, -1500)!;
        Assert.True(fling.Position(0.3) < 500);
        Assert.True(fling.Position(100) >= 0);
        Assert.Equal(fling.Position(0.123), new DesktopPhysics().CreateFling(InRange, -1500)!.Position(0.123));
    }

    [Fact]
    public void Smooth_scroll_simulation_eases_to_the_target_in_the_given_duration()
    {
        var d = TimeSpan.FromMilliseconds(200);
        var o = new SmoothScrollSimulation(0, 100, d, ScrollEasing.EaseOut);
        var io = new SmoothScrollSimulation(0, 100, d, ScrollEasing.EaseInOut);
        Assert.Equal(0, o.Position(0));
        Assert.Equal(100, o.Position(0.2));
        Assert.True(o.IsDone(0.2));
        Assert.False(o.IsDone(0.19));
        // 缓出起步快，缓入缓出中点对称
        Assert.True(o.Position(0.05) > io.Position(0.05));
        Assert.Equal(50, io.Position(0.1), 3);
        Assert.Equal(3 * 100 / 0.2, o.Velocity(0), 1);
        Assert.Equal(0, o.Velocity(0.25));
        Assert.Equal(100, o.Target);
    }

    [Fact]
    public void Physics_ignore_non_finite_inputs()
    {
        foreach (ScrollPhysics p in new ScrollPhysics[] { new BouncingPhysics(), new ClampingPhysics(), new DesktopPhysics() })
        {
            Assert.Equal(0, p.ApplyDrag(InRange, float.NaN));
            Assert.Equal(0, p.ApplyDrag(InRange, float.PositiveInfinity));
            Assert.Null(p.CreateFling(InRange, float.NaN));
        }
        var spring = new SpringSimulation(BouncingPhysics.Spring, 0, 10, float.NaN);
        Assert.False(float.IsNaN(spring.Position(0.1)));
    }
}
