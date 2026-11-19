using AwAUI.Graphics;

namespace AwAUI.UI.UnitTests;

/// <summary>图片评审：动画定时器早到、卸载后停止，异步解码期间换来源 / 卸载不泄漏。</summary>
public partial class ImageTests
{
    /// <summary>回调由测试手动执行（可以在到期之前执行，模拟系统定时器比单调时钟早到）。</summary>
    sealed class EarlyClock : UIClock
    {
        public TimeSpan Time;
        public readonly List<(TimeSpan Due, Action Callback)> Pending = new();

        public override TimeSpan Now => Time;

        public override IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var item = (Time + delay, callback);
            Pending.Add(item);
            return new Cancel(() => Pending.Remove(item));
        }

        sealed class Cancel(Action a) : IDisposable
        {
            public void Dispose() => a();
        }

        public void FireNext()
        {
            var (_, cb) = Pending[0];
            Pending.RemoveAt(0);
            cb();
        }
    }

    [Fact]
    public void Animation_keeps_playing_when_a_timer_fires_early()
    {
        var gif = WithDelays(TestGif("anim-disposal.gif"), 10); // 100 ms
        var clock = new EarlyClock();
        var h = new PipelineHarness(new Lambda { Render = () => new Image { Id = "img", Source = ImageSource.FromBytes(gif) } }, deferMount: true);
        h.Root.Clock = clock;
        h.MountNow();
        var img = Img(h);
        Assert.Single(clock.Pending);
        // 定时器在 99.5 ms 时就执行了：还不该换帧，但必须续上一个定时器
        clock.Time = TimeSpan.FromMilliseconds(99.5);
        clock.FireNext();
        h.Frame();
        Assert.Equal(0, img.CurrentFrame);
        Assert.Single(clock.Pending);
        Assert.True(clock.Pending[0].Due >= TimeSpan.FromMilliseconds(100), clock.Pending[0].Due.ToString());
        clock.Time = TimeSpan.FromMilliseconds(100);
        clock.FireNext();
        h.Frame();
        Assert.Equal(1, img.CurrentFrame);
        Assert.Single(clock.Pending);
    }

    [Fact]
    public void Unmounting_an_animation_cancels_its_timer_and_releases_the_entry()
    {
        var gif = WithDelays(TestGif("anim-disposal.gif"), 10);
        var show = new State<bool>(true);
        var a = new AnimHarness(() => new Div { Children = show.Value ? [new Image { Id = "img", Source = ImageSource.FromBytes(gif) }] : [] });
        Assert.Equal(1, a.Clock.PendingCount);
        show.Value = false;
        a.H.Frame();
        Assert.Equal(0, a.Clock.PendingCount);
        Assert.Equal(0, a.H.Root.Images.Count);
        a.Advance(1000);
        Assert.False(a.H.Root.NeedsFrame);
    }

    static byte[] PaddedPng(int w, int h, Color color)
    {
        var png = Png(w, h, color);
        var padded = new byte[Math.Max(png.Length, ImageLoadContext.AsyncThresholdBytes + 16)];
        Buffer.BlockCopy(png, 0, padded, 0, png.Length);
        return padded;
    }

    static void WithAsyncRoot(Action<UIRoot, TestDispatcher> body)
    {
        var previous = Reactive.CurrentThreadDispatcher;
        try
        {
            var dispatcher = new TestDispatcher();
            var pipeline = new RenderPipeline(new AwAUI.Text.Testing.TestTextEngine());
            pipeline.SetViewport(new Size(400, 400), 1);
            var root = new UIRoot(dispatcher) { Pipeline = pipeline };
            body(root, dispatcher);
        }
        finally
        {
            Reactive.CurrentThreadDispatcher = previous;
        }
    }

    static void WaitForPosts(TestDispatcher d, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (d.Pending < count && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Assert.True(d.Pending >= count, "decode did not finish");
    }

    [Fact]
    public void Source_change_during_async_decode_drops_the_stale_result()
    {
        WithAsyncRoot((root, dispatcher) =>
        {
            var which = new State<int>(0);
            var big = PaddedPng(300, 200, Color.Parse("#0000ff"));
            var small = Png(10, 20, Color.Parse("#ff0000"));
            root.Mount(new Lambda { Render = () => new Image { Id = "img", Source = ImageSource.FromBytes(which.Value == 0 ? big : small) } });
            var img = (ImageElement)root.RootElement.ById("img");
            Assert.Equal(ImageLoadState.Loading, img.LoadState);
            // 解码还在进行：换成一张同步解码的小图
            which.Value = 1;
            root.RunFrame();
            Assert.Equal(ImageLoadState.Ready, img.LoadState);
            Assert.Equal(new Size(10, 20), img.BoundingRect.Size);
            Assert.Equal(1, root.Images.Count);
            // 旧的解码结果回到 UI 线程：被丢弃，不影响当前图片，也不留在缓存里
            WaitForPosts(dispatcher, 1);
            dispatcher.Pump();
            Assert.Equal(0, root.Images.PendingLoads);
            Assert.Equal(1, root.Images.Count);
            root.RunFrame();
            Assert.Equal(new Size(10, 20), img.BoundingRect.Size);
            Assert.Same(img.Content, ((ImageElement)root.RootElement.ById("img")).Content);
            Assert.Equal(10, img.Content!.Bitmap!.Width);
        });
    }

    [Fact]
    public void Unmount_during_async_decode_leaves_nothing_behind()
    {
        WithAsyncRoot((root, dispatcher) =>
        {
            var show = new State<bool>(true);
            var big = PaddedPng(64, 64, Color.White);
            ImageSource? loaded = null;
            root.Images.Loaded += (s, _) => loaded = s;
            root.Mount(new Lambda { Render = () => new Div { Children = show.Value ? [new Image { Source = ImageSource.FromBytes(big) }] : [] } });
            Assert.Equal(1, root.Images.PendingLoads);
            show.Value = false;
            root.RunFrame();
            Assert.Equal(0, root.Images.Count);
            WaitForPosts(dispatcher, 1);
            dispatcher.Pump();
            Assert.Equal(0, root.Images.PendingLoads);
            Assert.Equal(0, root.Images.Count);
            // 没有使用者了：不触发 Loaded，不请求重绘
            Assert.Null(loaded);
        });
    }
}
