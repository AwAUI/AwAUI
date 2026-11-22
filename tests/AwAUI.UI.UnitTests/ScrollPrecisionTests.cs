namespace AwAUI.UI.UnitTests;

/// <summary>
/// 双精度滚动偏移（design/07 9c「双精度滚动偏移」）：控制器累积、很长的内容与程序滚动都按 double 保存，
/// 只在公开的 <see cref="Point"/> / <see cref="Size"/> 边界转成单精度。
/// </summary>
public class ScrollPrecisionTests
{
    static ScrollController WithMetrics(double contentW, double contentH, double viewportW, double viewportH)
    {
        var c = new ScrollController();
        c._contentW = contentW;
        c._contentH = contentH;
        c._viewportW = viewportW;
        c._viewportH = viewportH;
        c._hasMetrics = true;
        return c;
    }

    [Fact]
    public void Successive_fractional_wheel_deltas_accumulate_without_float_drift()
    {
        var c = WithMetrics(1000, 1000, 10, 10);
        // 单精度会把 0.1 的千次累加变成约 100.0004；double 的误差应在 1e-9 以内
        for (int i = 0; i < 1000; i++)
            c.SetOffset(c.Clamp(c._x + 0.1, 0), c._y);

        Assert.Equal(100.0, c.OffsetX, 9);
        Assert.Equal(0.0, c.OffsetY);
    }

    [Fact]
    public void Content_above_two_to_the_24_keeps_distinct_integer_offsets()
    {
        const double height = (1 << 25) + 4096; // 2^24 以上，单精度无法区分相邻整数
        var c = WithMetrics(100, height, 100, 0);
        Assert.Equal(height, c.MaxOffsetY);

        // 2^25 附近单精度的步长为 4：33554433 与 33554434 都变成 33554432
        c.SetOffset(33_554_433.0, 33_554_434.0);

        Assert.Equal(33_554_433.0, c.OffsetX);
        Assert.Equal(33_554_434.0, c.OffsetY);
        Assert.NotEqual(c.OffsetX, c.OffsetY);
        // 单精度的 Point 已经无法区分这两个偏移，所以需要 OffsetX / OffsetY
        Assert.Equal(c.Offset.X, c.Offset.Y);
    }

    [Fact]
    public void JumpTo_and_ScrollTo_still_clamp()
    {
        var c = WithMetrics(500, 500, 100, 100);

        c.JumpTo(new Point(-50, 1000));
        Assert.Equal(0.0, c.OffsetX);
        Assert.Equal(400.0, c.OffsetY);

        var task = c.ScrollTo(new Point(1000, -100), ScrollAnimation.Instant);
        Assert.True(task.IsCompleted);
        Assert.Equal(400.0, c.OffsetX);
        Assert.Equal(0.0, c.OffsetY);

        // double 重载同样钳制
        c.JumpTo(-1.0, 9999.0);
        Assert.Equal(0.0, c.OffsetX);
        Assert.Equal(400.0, c.OffsetY);
    }

    [Fact]
    public void Offset_and_OffsetX_agree_at_normal_magnitudes()
    {
        var c = WithMetrics(1000, 1000, 100, 100);
        c.SetOffset(12.5, 34.25);

        Assert.Equal(12.5, c.OffsetX);
        Assert.Equal(34.25, c.OffsetY);
        Assert.Equal(c.OffsetX, (double)c.Offset.X);
        Assert.Equal(c.OffsetY, (double)c.Offset.Y);
    }

    [Fact]
    public void Scrolling_to_max_on_one_axis_does_not_move_the_other()
    {
        var c = WithMetrics(1000, 1000, 100, 100);
        c.SetOffset(10, 20);

        c.JumpTo(c.MaxOffsetX, c.OffsetY);
        Assert.Equal(900.0, c.OffsetX);
        Assert.Equal(20.0, c.OffsetY);

        c.JumpTo(c.OffsetX, c.MaxOffsetY);
        Assert.Equal(900.0, c.OffsetX);
        Assert.Equal(900.0, c.OffsetY);
    }
}
