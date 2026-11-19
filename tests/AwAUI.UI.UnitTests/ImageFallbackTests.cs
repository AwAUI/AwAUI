using AwAUI.Graphics;
using AwAUI.Imaging;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 图片的占位与替代文本（加载失败：图标 + 替代文本；<c>alt=""</c> 不画；没有来源时只画替代文本），
/// 以及 <see cref="ImageCache.NotifyChanged"/>（调用方改写位图、重新加载）。
/// 测试字体是 Ahem（每个字形 1em 见方），默认样式表的字号为 14px。
/// </summary>
public class ImageFallbackTests
{
    static readonly byte[] Garbage = { 1, 2, 3, 4, 5, 6, 7, 8, 9 };

    static byte[] Png(int w, int h, Color color)
    {
        using var bmp = new Bitmap(new PixelSize(w, h));
        bmp.Clear(color);
        return PngCodec.Encode(bmp);
    }

    static PipelineHarness Harness(Image image, string? css = null) =>
        new(new Div { Style = "padding: 10px", Children = [image] }, css, width: 300, height: 120);

    static Color Pixel(Bitmap bmp, int x, int y)
    {
        var row = bmp.GetRow(y);
        return Color.FromRgba(row[x * 4], row[x * 4 + 1], row[x * 4 + 2], row[x * 4 + 3]);
    }

    static bool IsWhite(Color c) => c.R > 0.99f && c.G > 0.99f && c.B > 0.99f;

    static int NonWhite(Bitmap bmp, Rect area)
    {
        int n = 0;
        for (int y = (int)area.Top; y < (int)area.Bottom; y++)
            for (int x = (int)area.Left; x < (int)area.Right; x++)
                if (!IsWhite(Pixel(bmp, x, y))) n++;
        return n;
    }

    [Fact]
    public void Failed_image_without_alt_shows_the_broken_icon()
    {
        var h = Harness(new Image { Id = "img", Source = ImageSource.FromBytes(Garbage) });
        var img = (ImageElement)h.El("img");
        Assert.Equal(ImageLoadState.Failed, img.LoadState);
        Assert.True(img.ShowsFallback);
        Assert.Equal(new Size(ImageFallback.IconSize, ImageFallback.IconSize), h.Box("img").Size);
        using var bmp = h.Render();
        // 图标画在内容盒内（相框的灰色与红色裂痕），盒外什么也没有
        Assert.True(NonWhite(bmp, new Rect(10, 10, 16, 16)) > 40);
        Assert.Equal(0, NonWhite(bmp, new Rect(27, 0, 273, 120)));
        Assert.Equal(0, NonWhite(bmp, new Rect(0, 27, 300, 93)));
    }

    [Fact]
    public void Failed_image_with_alt_shows_icon_and_text_and_sizes_to_them()
    {
        var h = Harness(new Image { Id = "img", Source = ImageSource.FromBytes(Garbage), Alt = "AB" }, "img { color: #0000ff }");
        var box = h.Box("img");
        // 图标 16 + 间距 4 + 两个 14px 的 Ahem 字形
        Assert.Equal(ImageFallback.IconSize + ImageFallback.Gap + 28, box.Width, 2);
        Assert.True(box.Height >= ImageFallback.IconSize);
        using var bmp = h.Render();
        // 文本按元素颜色（蓝）绘制在图标右侧
        var c = Pixel(bmp, (int)(box.X + 20 + 7), (int)(box.Y + 7));
        Assert.True(c.B > 0.9f && c.R < 0.1f, c.ToString());
        Assert.Equal("AB", ((ImageElement)h.El("img")).AccessibleName);
    }

    [Fact]
    public void Alt_text_is_clipped_to_a_sized_box_and_wraps_within_it()
    {
        var h = Harness(new Image { Id = "img", Source = ImageSource.FromBytes(Garbage), Alt = "AAAA AAAA AAAA AAAA AAAA", Width = 60, Height = 30 },
            "img { color: #000 }");
        var box = h.Box("img");
        Assert.Equal(new Size(60, 30), box.Size);
        using var bmp = h.Render();
        Assert.True(NonWhite(bmp, box) > 100);
        // 文本换行后仍然超出：裁剪到内容盒
        Assert.Equal(0, NonWhite(bmp, new Rect(box.Right + 1, 0, 300 - box.Right - 1, 120)));
        Assert.Equal(0, NonWhite(bmp, new Rect(0, box.Bottom + 1, 300, 120 - box.Bottom - 1)));
    }

    [Fact]
    public void Decorative_image_with_empty_alt_draws_nothing_when_it_fails()
    {
        var h = Harness(new Image { Id = "img", Source = ImageSource.FromBytes(Garbage), Alt = "" });
        Assert.Equal(Size.Empty, h.Box("img").Size);
        Assert.False(((ImageElement)h.El("img")).ShowsFallback);
        using var bmp = h.Render();
        Assert.Equal(0, NonWhite(bmp, new Rect(0, 0, 300, 120)));
    }

    [Fact]
    public void Image_without_source_shows_only_its_alt_text()
    {
        var h = Harness(new Image { Id = "img", Alt = "ABC" });
        var box = h.Box("img");
        Assert.Equal(42, box.Width, 2);
        Assert.True(((ImageElement)h.El("img")).ShowsFallback);
        using var bmp = h.Render();
        // 没有图标：第一个字形从内容盒左边开始
        Assert.False(IsWhite(Pixel(bmp, (int)box.X + 2, (int)box.Y + 4)));
    }

    [Fact]
    public void Loaded_image_does_not_draw_the_placeholder()
    {
        var h = Harness(new Image { Id = "img", Source = ImageSource.FromBytes(Png(20, 10, Color.Parse("#00ff00"))), Alt = "AB" });
        Assert.Equal(new Size(20, 10), h.Box("img").Size);
        Assert.False(((ImageElement)h.El("img")).ShowsFallback);
    }

    [Fact]
    public void Alt_change_relayouts_the_placeholder()
    {
        var alt = new State<string>("A");
        var h = new PipelineHarness(new Lambda { Render = () => new Image { Id = "img", Source = ImageSource.FromBytes(Garbage), Alt = alt.Value } },
            width: 300, height: 120);
        float w1 = h.Box("img").Width;
        alt.Value = "AAAA";
        h.Frame();
        Assert.Equal(w1 + 3 * 14, h.Box("img").Width, 2);
    }

    // ------------------------------------------------------------ NotifyChanged

    [Fact]
    public void Notify_changed_redraws_a_caller_owned_bitmap_modified_in_place()
    {
        using var bitmap = new Bitmap(new PixelSize(8, 8));
        bitmap.Clear(Color.Parse("#ff0000"));
        var source = ImageSource.FromBitmap(bitmap);
        var h = new PipelineHarness(new Div { Children = [new Image { Id = "img", Source = source, Style = "display: block; width: 40px; height: 40px" }] },
            width: 60, height: 60, defaultSheet: false);
        using (var before = h.Render()) Assert.True(Pixel(before, 20, 20).R > 0.99f);
        long version = bitmap.Version;

        bitmap.Clear(Color.Parse("#00ff00"));
        Assert.True(h.Root.Images.NotifyChanged(ImageSource.FromBitmap(bitmap))); // 相等的来源即可
        Assert.True(bitmap.Version > version);
        Assert.True(h.Root.NeedsFrame);
        h.Frame();
        using var after = h.Render();
        var c = Pixel(after, 20, 20);
        Assert.True(c.G > 0.99f && c.R < 0.01f, c.ToString());
    }

    sealed class CountingSource : ImageSource
    {
        readonly object _key = new();
        public int Loads;
        public override object Key => _key;

        protected internal override ImageContent Load(ImageLoadContext context)
        {
            Loads++;
            var bmp = new Bitmap(new PixelSize(10 * Loads, 5));
            bmp.Clear(Color.White);
            return ImageContent.FromBitmap(bmp, takeOwnership: true);
        }
    }

    [Fact]
    public void Notify_changed_reloads_other_sources_and_releases_the_old_content_after_painting()
    {
        var source = new CountingSource();
        var tick = new State<int>(0);
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div { Children = [new Image { Id = "img", Source = source }, new Div { Style = "height: " + (tick.Value + 1) + "px" }] },
        });
        var img = (ImageElement)h.El("img");
        Assert.Equal(new Size(10, 5), h.Box("img").Size);
        var old = img.Content!.Bitmap!;

        Assert.True(h.Root.Images.NotifyChanged(source));
        Assert.Equal(2, source.Loads);
        h.Frame();
        Assert.Equal(new Size(20, 5), h.Box("img").Size);
        // 旧内容在之后的绘制之后释放（已录制的帧可能仍引用它）
        tick.Value++;
        h.Frame();
        Assert.True(old.IsDisposed);
        Assert.False(img.Content!.Bitmap!.IsDisposed);
    }

    /// <summary>异步加载的来源：版本 2 的加载在测试放行之前阻塞，用来构造「过时的结果比新结果晚到」。</summary>
    sealed class GatedSource : ImageSource
    {
        readonly object _key = new();
        public volatile int Version = 1;
        public readonly ManualResetEventSlim Version2Entered = new(false);
        public readonly ManualResetEventSlim ReleaseVersion2 = new(false);
        public readonly System.Collections.Concurrent.ConcurrentDictionary<int, Bitmap> Bitmaps = new();
        public override object Key => _key;
        public override bool PreferAsync => true;

        protected internal override ImageContent Load(ImageLoadContext context)
        {
            int v = Version;
            if (v == 2)
            {
                Version2Entered.Set();
                ReleaseVersion2.Wait(TimeSpan.FromSeconds(60));
            }
            var bmp = new Bitmap(new PixelSize(10 * v, 5));
            bmp.Clear(Color.White);
            Bitmaps[v] = bmp;
            return ImageContent.FromBitmap(bmp, takeOwnership: true);
        }
    }

    static void WaitForPosts(TestDispatcher d, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (d.Pending < count && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Assert.True(d.Pending >= count, "load did not finish");
    }

    [Fact]
    public void Notify_changed_with_async_loads_keeps_old_content_and_drops_results_that_arrive_late()
    {
        var previous = Reactive.CurrentThreadDispatcher;
        var source = new GatedSource();
        try
        {
            var dispatcher = new TestDispatcher();
            var pipeline = new RenderPipeline(new AwAUI.Text.Testing.TestTextEngine());
            pipeline.SetViewport(new Size(400, 400), 1);
            var root = new UIRoot(dispatcher) { Pipeline = pipeline };
            var tick = new State<int>(0);
            root.Mount(new Lambda { Render = () => new Div { Children = [new Image { Id = "img", Source = source }, new Div { Style = "height: " + (tick.Value + 1) + "px" }] } });
            var img = (ImageElement)root.RootElement.ById("img");
            WaitForPosts(dispatcher, 1);
            dispatcher.Pump();
            root.RunFrame();
            Assert.Equal(new Size(10, 5), img.BoundingRect.Size);

            // 第一次重新加载（版本 2）卡在线程池上：旧内容继续显示
            source.Version = 2;
            Assert.True(root.Images.NotifyChanged(source));
            Assert.True(source.Version2Entered.Wait(TimeSpan.FromSeconds(60)));
            root.RunFrame();
            Assert.Equal(ImageLoadState.Ready, img.LoadState);
            Assert.Equal(new Size(10, 5), img.BoundingRect.Size);

            // 第二次重新加载（版本 3）先完成
            source.Version = 3;
            Assert.True(root.Images.NotifyChanged(source));
            WaitForPosts(dispatcher, 1);
            dispatcher.Pump();
            root.RunFrame();
            Assert.Equal(new Size(30, 5), img.BoundingRect.Size);
            Assert.Equal(1, root.Images.PendingLoads);

            // 过时的版本 2 晚到：被丢弃（位图释放），不覆盖版本 3
            source.ReleaseVersion2.Set();
            WaitForPosts(dispatcher, 1);
            dispatcher.Pump();
            Assert.Equal(0, root.Images.PendingLoads);
            tick.Value++;
            root.RunFrame();
            Assert.Equal(new Size(30, 5), img.BoundingRect.Size);
            Assert.Same(source.Bitmaps[3], img.Content!.Bitmap);
            Assert.True(source.Bitmaps[2].IsDisposed);
            // 版本 1 在被替换后的绘制之后释放
            tick.Value++;
            root.RunFrame();
            Assert.True(source.Bitmaps[1].IsDisposed);
            Assert.False(source.Bitmaps[3].IsDisposed);
        }
        finally
        {
            source.ReleaseVersion2.Set();
            Reactive.CurrentThreadDispatcher = previous;
        }
    }

    [Fact]
    public void Notify_changed_returns_false_for_sources_not_in_use()
    {
        var h = new PipelineHarness(new Image { Id = "img", Source = ImageSource.FromBytes(Png(2, 2, Color.White)) });
        Assert.False(h.Root.Images.NotifyChanged(ImageSource.FromBytes(new byte[] { 1 })));
        Assert.Throws<ArgumentNullException>(() => h.Root.Images.NotifyChanged(null!));
    }

    [Fact]
    public void Reload_that_fails_shows_the_placeholder()
    {
        var data = Png(6, 6, Color.White);
        var source = ImageSource.FromBytes(data, "k");
        var h = new PipelineHarness(new Image { Id = "img", Source = source });
        Assert.Equal(new Size(6, 6), h.Box("img").Size);
        // 数组在原处被改写（FromBytes 的契约不允许，这里只为模拟「数据变化后解码失败」）
        Array.Clear(data, 0, data.Length);
        Assert.True(h.Root.Images.NotifyChanged(source));
        h.Frame();
        var img = (ImageElement)h.El("img");
        Assert.Equal(ImageLoadState.Failed, img.LoadState);
        Assert.Equal(new Size(ImageFallback.IconSize, ImageFallback.IconSize), h.Box("img").Size);
    }
}
