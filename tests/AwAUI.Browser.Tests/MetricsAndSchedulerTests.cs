using AwAUI.Browser;
using AwAUI.Platform;

namespace AwAUI.Browser.Tests;

public class CanvasMetricsTests
{
    [Fact]
    public void CssTimesDpr()
    {
        var m = CanvasMetrics.Compute(640, 440, 2);
        Assert.Equal(new Size(640, 440), m.ClientSize);
        Assert.Equal(2, m.Scale);
        Assert.Equal(new PixelSize(1280, 880), m.PixelSize);
    }

    [Fact]
    public void DevicePixelContentBoxWins()
    {
        // 333.33 CSS 像素 × 1.5 = 500 设备像素，但合成器把它放在 501 像素宽的格子上
        var m = CanvasMetrics.Compute(333.33, 100, 1.5, 501, 150);
        Assert.Equal(new PixelSize(501, 150), m.PixelSize);
        Assert.Equal(1.5, m.Scale);
    }

    [Theory]
    [InlineData(333, 1.25, 416)]  // 416.25 → 416
    [InlineData(333, 1.5, 500)]   // 499.5 → 500（四舍五入）
    [InlineData(100.4, 3, 301)]
    [InlineData(0.1, 1, 1)]       // 非零尺寸至少 1 像素
    public void FractionalSizesRound(double css, double dpr, int expected) =>
        Assert.Equal(expected, CanvasMetrics.Compute(css, css, dpr).PixelSize.Width);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidDprFallsBackOrClamps(double dpr)
    {
        var m = CanvasMetrics.Compute(100, 100, dpr);
        Assert.True(m.Scale >= CanvasMetrics.MinScale && m.Scale <= CanvasMetrics.MaxScale);
        if (!DomProtocol.IsFinite(dpr)) Assert.Equal(1, m.Scale);
    }

    [Fact]
    public void ExtremeDprIsClamped()
    {
        Assert.Equal(CanvasMetrics.MaxScale, CanvasMetrics.Compute(10, 10, 1000).Scale);
        Assert.Equal(CanvasMetrics.MinScale, CanvasMetrics.Compute(10, 10, 0.001).Scale);
    }

    [Fact]
    public void ZeroSizeGivesEmptyFramebuffer()
    {
        var m = CanvasMetrics.Compute(0, 300, 2, 0, 600);
        Assert.True(m.PixelSize.IsEmpty);
        Assert.Equal(new Size(0, 300), m.ClientSize);
        Assert.True(CanvasMetrics.Compute(double.NaN, -5, 2).PixelSize.IsEmpty);
    }

    [Fact]
    public void HugeCanvasIsCappedAndScaleReducedConsistently()
    {
        var m = CanvasMetrics.Compute(20000, 10000, 2);
        Assert.True(m.PixelSize.Width <= CanvasMetrics.MaxPixelDimension && m.PixelSize.Height <= CanvasMetrics.MaxPixelDimension);
        Assert.True((long)m.PixelSize.Width * m.PixelSize.Height <= CanvasMetrics.MaxPixelArea);
        Assert.True(m.Scale < 2);
        // 逻辑 → 设备映射仍覆盖整块帧缓冲（误差不超过 1 像素）
        Assert.InRange(m.ClientSize.Width * m.Scale - m.PixelSize.Width, -1.0, 1.0);
        Assert.InRange(m.ClientSize.Height * m.Scale - m.PixelSize.Height, -1.0, 1.0);
    }

    [Fact]
    public void InconsistentDeviceBoxIsIgnored()
    {
        // 与 CSS × DPR 严重不符的设备像素盒（伪造或过期）：退回 CSS × DPR，不会把缩放压到接近 0
        var m = CanvasMetrics.Compute(1000, 1000, 2, 1e9, 1e9);
        Assert.Equal(new PixelSize(2000, 2000), m.PixelSize);
        Assert.Equal(2, m.Scale);
        Assert.Equal(new PixelSize(200, 100), CanvasMetrics.Compute(100, 50, 2, 1, 1).PixelSize);
        Assert.Equal(new PixelSize(2001, 1999), CanvasMetrics.Compute(1000, 1000, 2, 2001, 1999).PixelSize);
    }

    [Fact]
    public void Equality()
    {
        Assert.Equal(CanvasMetrics.Compute(640, 440, 2), CanvasMetrics.Compute(640, 440, 2, double.NaN, double.NaN));
        Assert.NotEqual(CanvasMetrics.Compute(640, 440, 2), CanvasMetrics.Compute(640, 440, 1));
        Assert.True(CanvasMetrics.Compute(640, 440, 2) == CanvasMetrics.Compute(640, 440, 2, 1280, 880));
    }
}

public class FrameSchedulerTests
{
    [Fact]
    public void RequestSchedulesOneCallback()
    {
        var s = new FrameScheduler();
        Assert.True(s.Request());
        Assert.False(s.Request()); // 已挂回调：不重复挂
        Assert.True(s.CallbackPending);
        Assert.True(s.OnAnimationFrame(16, out var t));
        Assert.Equal(160_000, t.Ticks);
        Assert.False(s.CallbackPending);
        Assert.False(s.FrameRequested);
        Assert.Equal(1, s.FramesDelivered);
    }

    [Fact]
    public void RequestDuringFrameSchedulesNextFrame()
    {
        var s = new FrameScheduler();
        s.Request();
        Assert.True(s.OnAnimationFrame(16, out _));
        Assert.True(s.Request()); // 动画：OnFrame 内再次请求
        Assert.True(s.OnAnimationFrame(33, out _));
        Assert.Equal(2, s.FramesDelivered);
    }

    [Fact]
    public void IdleWhenNothingRequested()
    {
        var s = new FrameScheduler();
        s.Request();
        s.OnAnimationFrame(16, out _);
        // 没有新的请求：不挂回调，状态静止
        Assert.False(s.CallbackPending);
        Assert.False(s.OnAnimationFrame(33, out _)); // 迟到 / 多余的回调不交付
        Assert.Equal(1, s.FramesDelivered);
        Assert.Equal(2, s.CallbacksReceived);
    }

    [Fact]
    public void HiddenCancelsAndVisibleResumes()
    {
        var s = new FrameScheduler();
        s.Request();
        Assert.Equal(VisibilityAction.Cancel, s.SetVisible(false));
        Assert.False(s.CallbackPending);
        Assert.False(s.Request()); // 不可见：只登记需求
        Assert.True(s.FrameRequested);
        Assert.Equal(VisibilityAction.None, s.SetVisible(false));
        Assert.Equal(VisibilityAction.Schedule, s.SetVisible(true));
        Assert.True(s.OnAnimationFrame(100, out _));
    }

    [Fact]
    public void HiddenWithoutPendingCallbackNeedsNothing()
    {
        var s = new FrameScheduler();
        Assert.Equal(VisibilityAction.None, s.SetVisible(false));
        Assert.Equal(VisibilityAction.None, s.SetVisible(true)); // 没有需求：不挂
    }

    [Fact]
    public void CallbackWhileHiddenIsNotDelivered()
    {
        var s = new FrameScheduler();
        s.Request();
        s.SetVisible(false);
        Assert.False(s.OnAnimationFrame(16, out _)); // 取消前已在途的回调
        Assert.True(s.FrameRequested);
    }

    [Fact]
    public void CloseCancelsPending()
    {
        var s = new FrameScheduler();
        s.Request();
        Assert.True(s.Close());
        Assert.False(s.Request());
        Assert.False(s.OnAnimationFrame(16, out _));
        Assert.Equal(VisibilityAction.None, s.SetVisible(false));
        Assert.False(new FrameScheduler().Close());
    }

    [Fact]
    public void TimestampsAreMonotonic()
    {
        var s = new FrameScheduler();
        s.Request(); s.OnAnimationFrame(100, out var a);
        s.Request(); s.OnAnimationFrame(50, out var b);
        s.Request(); s.OnAnimationFrame(double.NaN, out var c);
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }
}
