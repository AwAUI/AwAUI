using AwAUI.Browser;

namespace AwAUI.Browser.Tests;

/// <summary>主线程调度队列（BrowserDispatcher 的纯 C# 部分）。</summary>
public class DispatchQueueTests
{
    [Fact]
    public void PostsCoalesceIntoOneDrainAndRunInOrder()
    {
        int requests = 0;
        var q = new DispatchQueue(() => requests++);
        var log = new List<int>();
        q.Enqueue(() => log.Add(1));
        q.Enqueue(() => log.Add(2));
        Assert.Equal(1, requests);
        q.Drain();
        Assert.Equal(new[] { 1, 2 }, log);
        Assert.Equal(1, requests);
        Assert.False(q.DrainScheduled);
    }

    [Fact]
    public void ActionsPostedDuringDrainRunInTheNextDrain()
    {
        int requests = 0;
        var q = new DispatchQueue(() => requests++);
        var log = new List<int>();
        q.Enqueue(() => { log.Add(1); q.Enqueue(() => log.Add(2)); });
        q.Drain();
        Assert.Equal(new[] { 1 }, log);
        Assert.Equal(2, requests); // 执行中的投递安排了下一次 drain
        q.Drain();
        Assert.Equal(new[] { 1, 2 }, log);
    }

    [Fact]
    public void ThrowingActionDoesNotStrandTheRest()
    {
        // 审查发现的问题：旧实现中某个操作抛出后，剩余操作滞留在队列里，直到下一次 Post 才会再安排 drain
        int requests = 0;
        var q = new DispatchQueue(() => requests++);
        var log = new List<int>();
        q.Enqueue(() => throw new InvalidOperationException("boom"));
        q.Enqueue(() => log.Add(2));
        q.Enqueue(() => log.Add(3));
        Assert.Throws<InvalidOperationException>(q.Drain);
        Assert.Equal(2, requests);
        Assert.True(q.DrainScheduled);
        Assert.Equal(2, q.Count);
        q.Drain();
        Assert.Equal(new[] { 2, 3 }, log);
        Assert.Equal(0, q.Count);
        Assert.Equal(2, requests);
    }

    [Fact]
    public void ConcurrentPostsAreAllDelivered()
    {
        int requests = 0, ran = 0;
        var q = new DispatchQueue(() => Interlocked.Increment(ref requests));
        Parallel.For(0, 8, _ => { for (int i = 0; i < 10_000; i++) q.Enqueue(() => ran++); });
        while (q.Count > 0) q.Drain();
        Assert.Equal(80_000, ran);
        Assert.True(requests >= 1);
    }

    [Fact]
    public void NullActionThrows() => Assert.Throws<ArgumentNullException>(() => new DispatchQueue(() => { }).Enqueue(null!));
}

/// <summary>JS attachCanvas 返回值的解析。</summary>
public class AttachResultTests
{
    [Fact]
    public void Success()
    {
        var r = AttachResult.Parse(new[] { 640.0, 440, 2, double.NaN, double.NaN, 0, 1 }, requestedAlpha: false);
        Assert.Equal(AttachStatus.Ok, r.Status);
        Assert.False(r.Alpha);
        Assert.True(r.Visible);
        Assert.Equal(new PixelSize(1280, 880), r.Metrics.PixelSize);
        Assert.Equal(2, r.Metrics.Scale);
    }

    [Fact]
    public void ActualContextAlphaWinsOverRequest()
    {
        // 页面先以 alpha: true 取过 2D 上下文：请求不透明也得到透明上下文，必须走反预乘路径（审查发现的问题）
        Assert.True(AttachResult.Parse(new[] { 10.0, 10, 1, double.NaN, double.NaN, 1, 1 }, requestedAlpha: false).Alpha);
        Assert.False(AttachResult.Parse(new[] { 10.0, 10, 1, double.NaN, double.NaN, 0, 1 }, requestedAlpha: true).Alpha);
        // 非法标志：按请求
        Assert.True(AttachResult.Parse(new[] { 10.0, 10, 1, double.NaN, double.NaN, double.NaN, 1 }, requestedAlpha: true).Alpha);
        Assert.False(AttachResult.Parse(new[] { 10.0, 10, 1, double.NaN, double.NaN, double.NaN, 1 }, requestedAlpha: false).Alpha);
    }

    [Fact]
    public void HiddenPageAtAttach() =>
        Assert.False(AttachResult.Parse(new[] { 10.0, 10, 1, double.NaN, double.NaN, 0, 0 }, false).Visible);

    [Theory]
    // AttachStatus 是 internal 类型，参数用 int（public 测试方法的签名不能引用 internal 类型）
    [InlineData(null, (int)AttachStatus.NotFound)]
    [InlineData(new double[0], (int)AttachStatus.NotFound)]
    [InlineData(new[] { -1.0 }, (int)AttachStatus.AlreadyAttached)]
    [InlineData(new[] { -2.0 }, (int)AttachStatus.NoContext)]
    [InlineData(new[] { -7.0 }, (int)AttachStatus.NotFound)]
    [InlineData(new[] { 1.0, 2, 3, 4, 5 }, (int)AttachStatus.NotFound)] // 旧格式 / 截断：不当作成功
    public void Errors(double[]? values, int expected) => Assert.Equal((AttachStatus)expected, AttachResult.Parse(values, false).Status);
}
