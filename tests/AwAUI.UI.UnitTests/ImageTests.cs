using AwAUI.Css.Values;
using AwAUI.Graphics;
using AwAUI.Imaging;
using AwAUI.Svg;

namespace AwAUI.UI.UnitTests;

/// <summary>Image 元素：来源、解码（同步 / 异步）、缓存与释放、固有尺寸布局、object-fit、动画播放。</summary>
public partial class ImageTests
{
    static byte[] Png(int w, int h, Color color)
    {
        using var bmp = new Bitmap(new PixelSize(w, h));
        bmp.Clear(color);
        return PngCodec.Encode(bmp);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    static byte[] TestGif(string name) => File.ReadAllBytes(Path.Combine(RepoRoot(), "tests", "TestFiles", "Images", "gif", name));

    /// <summary>把 GIF 所有图形控制扩展（21 F9 04）的帧延迟改为 <paramref name="centiseconds"/>。</summary>
    static byte[] WithDelays(byte[] gif, int centiseconds)
    {
        var d = (byte[])gif.Clone();
        for (int i = 0; i + 6 < d.Length; i++)
        {
            if (d[i] == 0x21 && d[i + 1] == 0xF9 && d[i + 2] == 0x04)
            {
                d[i + 4] = (byte)(centiseconds & 0xFF);
                d[i + 5] = (byte)(centiseconds >> 8);
            }
        }
        return d;
    }

    static ImageElement Img(PipelineHarness h, string id = "img") => (ImageElement)h.El(id);

    [Fact]
    public void Png_bytes_decode_and_give_intrinsic_size()
    {
        var h = new PipelineHarness(new Div { Children = [new Image { Id = "img", Source = ImageSource.FromBytes(Png(30, 20, Color.Parse("#ff0000"))) }] });
        var img = Img(h);
        Assert.Equal(ImageLoadState.Ready, img.LoadState);
        Assert.Equal(new Size(30, 20), h.Box("img").Size);
        Assert.NotNull(img.Content?.Bitmap);
        Assert.Equal(1, h.Root.Images.Count);
    }

    [Fact]
    public void Css_width_keeps_the_aspect_ratio_and_max_width_shrinks()
    {
        var bytes = Png(40, 20, Color.Parse("#00ff00"));
        var h = new PipelineHarness(new Div
        {
            Style = "width: 100px",
            Children =
            [
                new Image { Id = "a", Source = ImageSource.FromBytes(bytes, "g"), Style = "width: 80px; display: block" },
                new Image { Id = "b", Source = ImageSource.FromBytes(bytes, "g"), Style = "height: 10px; display: block" },
                new Image { Id = "c", Source = ImageSource.FromBytes(Png(400, 100, Color.White), "wide"), Style = "max-width: 100%; display: block" },
            ],
        });
        Assert.Equal(new Size(80, 40), h.Box("a").Size);
        Assert.Equal(new Size(20, 10), h.Box("b").Size);
        Assert.Equal(new Size(100, 25), h.Box("c").Size);
        // 同一个键共享一份解码结果
        Assert.Equal(2, h.Root.Images.Count);
    }

    [Fact]
    public void Size_hints_reserve_space_and_override_intrinsic_size()
    {
        var h = new PipelineHarness(new Div
        {
            Children =
            [
                new Image { Id = "img", Source = ImageSource.FromBytes(Png(40, 20, Color.White)), Width = 60 },
                new Image { Id = "none", Width = 16, Height = 12 },
            ],
        });
        Assert.Equal(new Size(60, 30), h.Box("img").Size);
        Assert.Equal(new Size(16, 12), h.Box("none").Size);
    }

    [Fact]
    public void Released_when_the_last_user_unmounts()
    {
        var show = new State<int>(2);
        var bytes = Png(8, 8, Color.White);
        var h = new PipelineHarness(new Lambda
        {
            Render = () =>
            {
                var list = new NodeList();
                for (int i = 0; i < show.Value; i++) list.Add(new Image { Key = i, Source = ImageSource.FromBytes(bytes) });
                return new Div { Children = list };
            },
        });
        Assert.Equal(1, h.Root.Images.Count);
        var content = ((ImageElement)h.Root.RootElement.FindAll(e => e.Tag == "img").First()).Content!;
        show.Value = 1;
        h.Frame();
        Assert.Equal(1, h.Root.Images.Count);
        show.Value = 0;
        h.Frame();
        Assert.Equal(0, h.Root.Images.Count);
        // 位图在下一次绘制之后才释放（仍可能被正在光栅化的帧引用）
        Assert.NotNull(content.Bitmap);
        h.Pipeline.InvalidateAll();
        h.Frame();
        h.Pipeline.InvalidateAll();
        h.Frame();
        Assert.Null(content.Bitmap);
    }

    [Fact]
    public void Source_change_releases_the_old_entry()
    {
        var src = new State<int>(0);
        var a = Png(4, 4, Color.White);
        var b = Png(6, 2, Color.White);
        var h = new PipelineHarness(new Lambda { Render = () => new Image { Id = "img", Source = ImageSource.FromBytes(src.Value == 0 ? a : b) } });
        Assert.Equal(new Size(4, 4), h.Box("img").Size);
        src.Value = 1;
        h.Frame();
        Assert.Equal(new Size(6, 2), h.Box("img").Size);
        Assert.Equal(1, h.Root.Images.Count);
    }

    [Fact]
    public void Async_decoding_shows_a_placeholder_then_relayouts_on_the_ui_thread()
    {
        var previous = Reactive.CurrentThreadDispatcher;
        try
        {
            var dispatcher = new TestDispatcher();
            var pipeline = new RenderPipeline(new AwAUI.Text.Testing.TestTextEngine());
            pipeline.SetViewport(new Size(200, 200), 1);
            var root = new UIRoot(dispatcher) { Pipeline = pipeline };
            var big = Png(300, 200, Color.Parse("#0000ff"));
            // 大于阈值的编码数据异步解码；把数据填充到阈值以上（PNG 尾部之后的字节被忽略）
            var padded = new byte[Math.Max(big.Length, ImageLoadContext.AsyncThresholdBytes + 16)];
            Buffer.BlockCopy(big, 0, padded, 0, big.Length);
            root.Mount(new Lambda { Render = () => new Div { Children = [new Image { Id = "img", Source = ImageSource.FromBytes(padded) }] } });
            var img = (ImageElement)root.RootElement.ById("img");
            Assert.Equal(ImageLoadState.Loading, img.LoadState);
            Assert.Equal(0, img.BoundingRect.Width);
            Assert.Equal(1, root.Images.PendingLoads);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (dispatcher.Pending == 0 && DateTime.UtcNow < deadline) Thread.Sleep(5);
            Assert.Equal(1, dispatcher.Pump());
            Assert.Equal(ImageLoadState.Ready, img.LoadState);
            Assert.True(root.NeedsFrame);
            root.RunFrame();
            Assert.Equal(new Size(300, 200), img.BoundingRect.Size);
            Assert.Equal(0, root.Images.PendingLoads);
        }
        finally
        {
            // UIRoot 把调度器登记为本线程的响应式调度器：恢复，避免影响同一线程上之后的测试
            Reactive.CurrentThreadDispatcher = previous;
        }
    }

    [Fact]
    public void Undecodable_data_reports_a_diagnostic_and_shows_the_broken_image_icon()
    {
        var diags = new List<UIDiagnostic>();
        var pipeline = new RenderPipeline(new AwAUI.Text.Testing.TestTextEngine());
        var root = new UIRoot { Pipeline = pipeline };
        root.Diagnostic += diags.Add;
        root.Mount(new Lambda { Render = () => new Image { Id = "img", Source = ImageSource.FromBytes(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }) } });
        var img = (ImageElement)root.RootElement.ById("img");
        Assert.Equal(ImageLoadState.Failed, img.LoadState);
        // 与浏览器一样显示「图片损坏」图标（没有替代文本时只有图标，见 ImageFallbackTests）
        Assert.Equal(ImageFallback.IconSize, img.BoundingRect.Width);
        Assert.Contains(diags, d => d.Code == "AW1010");
    }

    [Fact]
    public void String_sources_support_data_uris_resources_and_files()
    {
        var png = Png(5, 7, Color.White);
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(png);
        var file = Path.Combine(Path.GetTempPath(), "awaui-img-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(file, Png(9, 3, Color.White));
        try
        {
            var pipeline = new RenderPipeline(new AwAUI.Text.Testing.TestTextEngine());
            var root = new UIRoot { Pipeline = pipeline };
            root.Images.ResourceResolver = name => name == "logo" ? new MemoryStream(Png(11, 2, Color.White)) : null;
            root.Mount(new Lambda
            {
                Render = () => new Div
                {
                    Children =
                    [
                        new Image { Id = "data", Source = dataUri },
                        new Image { Id = "res", Source = "logo" },
                        new Image { Id = "file", Source = file },
                        new Image { Id = "svg", Source = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='12' height='6'/%3E" },
                    ],
                },
            });
            Assert.Equal(new Size(5, 7), root.RootElement.ById("data").BoundingRect.Size);
            Assert.Equal(new Size(11, 2), root.RootElement.ById("res").BoundingRect.Size);
            Assert.Equal(new Size(9, 3), root.RootElement.ById("file").BoundingRect.Size);
            Assert.Equal(new Size(12, 6), root.RootElement.ById("svg").BoundingRect.Size);
            Assert.NotNull(((ImageElement)root.RootElement.ById("svg")).Content?.Vector);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Encoded_size_limit_is_enforced()
    {
        var pipeline = new RenderPipeline(new AwAUI.Text.Testing.TestTextEngine());
        var root = new UIRoot { Pipeline = pipeline };
        root.Images.MaxEncodedBytes = 16;
        root.Mount(new Lambda { Render = () => new Image { Id = "img", Source = ImageSource.FromBytes(Png(4, 4, Color.White)) } });
        Assert.Equal(ImageLoadState.Failed, ((ImageElement)root.RootElement.ById("img")).LoadState);
    }

    [Fact]
    public void Sources_compare_by_key()
    {
        var bytes = new byte[] { 1 };
        Assert.Equal(ImageSource.FromUri("a.png"), (ImageSource?)"a.png");
        Assert.NotEqual(ImageSource.FromUri("a.png"), ImageSource.FromFile("a.png"));
        Assert.Equal(ImageSource.FromBytes(bytes), ImageSource.FromBytes(bytes));
        Assert.NotEqual(ImageSource.FromBytes(bytes), ImageSource.FromBytes(new byte[] { 1 }));
        Assert.Equal(ImageSource.FromBytes(new byte[] { 1 }, "k"), ImageSource.FromBytes(new byte[] { 2 }, "k"));
    }

    // ------------------------------------------------------------ object-fit

    static PositionValue Pos(float xPercent, float yPercent, float xPx = 0, float yPx = 0) =>
        new(new LengthPercentage(xPx, xPercent), new LengthPercentage(yPx, yPercent));

    [Theory]
    [InlineData(CssObjectFit.Fill, 0, 0, 100, 50)]
    [InlineData(CssObjectFit.Contain, 25, 0, 50, 50)]
    [InlineData(CssObjectFit.Cover, 0, -25, 100, 100)]
    [InlineData(CssObjectFit.None, 30, 5, 40, 40)]
    [InlineData(CssObjectFit.ScaleDown, 30, 5, 40, 40)]
    public void Object_fit_places_content_like_css(CssObjectFit fit, float x, float y, float w, float h)
    {
        var r = ObjectFitting.Place(fit, PositionValue.Center, new Size(40, 40), null, new Rect(0, 0, 100, 50));
        Assert.Equal(new Rect(x, y, w, h), r);
    }

    [Fact]
    public void Scale_down_shrinks_large_content_and_position_resolves_against_free_space()
    {
        var box = new Rect(0, 0, 100, 50);
        Assert.Equal(new Rect(25, 0, 50, 50), ObjectFitting.Place(CssObjectFit.ScaleDown, PositionValue.Center, new Size(400, 400), null, box));
        Assert.Equal(new Rect(0, 0, 50, 50), ObjectFitting.Place(CssObjectFit.Contain, Pos(0, 0), new Size(10, 10), null, box));
        Assert.Equal(new Rect(50, 0, 50, 50), ObjectFitting.Place(CssObjectFit.Contain, Pos(100, 100), new Size(10, 10), null, box));
        Assert.Equal(new Rect(10, 0, 50, 50), ObjectFitting.Place(CssObjectFit.Contain, Pos(0, 25, xPx: 10), new Size(10, 10), null, box));
        // calc(100% - 10px)：右边距 10px
        Assert.Equal(new Rect(40, 0, 50, 50), ObjectFitting.Place(CssObjectFit.Contain, Pos(100, 0, xPx: -10), new Size(10, 10), null, box));
        // fill 没有剩余空间：百分比不起作用，长度仍然平移
        Assert.Equal(new Rect(5, 0, 100, 50), ObjectFitting.Place(CssObjectFit.Fill, Pos(100, 100, xPx: 5), new Size(10, 10), null, box));
    }

    [Theory]
    [InlineData(CssObjectFit.Contain, 25, 0, 50, 50)]
    [InlineData(CssObjectFit.ScaleDown, 25, 0, 50, 50)]
    [InlineData(CssObjectFit.None, 25, 0, 50, 50)]
    [InlineData(CssObjectFit.Cover, 0, -25, 100, 100)]
    [InlineData(CssObjectFit.Fill, 0, 0, 100, 50)]
    public void Content_with_only_an_aspect_ratio_fits_by_ratio(CssObjectFit fit, float x, float y, float w, float h)
    {
        var r = ObjectFitting.Place(fit, PositionValue.Center, default, 1f, new Rect(0, 0, 100, 50));
        Assert.Equal(new Rect(x, y, w, h), r);
    }

    [Fact]
    public void Placement_is_finite_for_degenerate_input()
    {
        foreach (var fit in new[] { CssObjectFit.Fill, CssObjectFit.Contain, CssObjectFit.Cover, CssObjectFit.None, CssObjectFit.ScaleDown })
        {
            foreach (var natural in new[] { default, new Size(float.NaN, 5), new Size(float.PositiveInfinity, 1), new Size(1e30f, 1e-30f) })
            {
                var r = ObjectFitting.Place(fit, Pos(1e30f, -1e30f), natural, float.PositiveInfinity, new Rect(0, 0, 0, 10));
                Assert.False(float.IsNaN(r.X) || float.IsNaN(r.Y) || float.IsNaN(r.Width) || float.IsNaN(r.Height), fit + " " + natural);
            }
        }
    }

    [Fact]
    public void Svg_image_is_drawn_with_object_fit_and_clipped_to_the_content_box()
    {
        var doc = SvgDocument.Parse("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10' width='10' height='10'><rect width='10' height='10' fill='#ff0000'/></svg>");
        var h = new PipelineHarness(new Div
        {
            Children = [new Image { Id = "img", Source = ImageSource.FromSvg(doc), Style = "object-fit: cover; object-position: left top; width: 40px; height: 20px; display: block; margin: 10px" }],
        }, defaultSheet: false);
        using var bmp = h.Render();
        Assert.True(bmp.GetPixel(15, 15).R > 0.9f);
        Assert.True(bmp.GetPixel(45, 25).R > 0.9f);
        // cover 放大到 40×40，超出内容盒的部分被裁掉
        ColorAssert.Near(Color.White, bmp.GetPixel(15, 35));
    }

    // ------------------------------------------------------------ 动画

    sealed class AnimHarness
    {
        public readonly ManualUIClock Clock = new();
        public readonly PipelineHarness H;

        public AnimHarness(Func<Node> render, float height = 300)
        {
            H = new PipelineHarness(new Lambda { Render = render }, height: height, deferMount: true);
            H.Root.Clock = Clock;
            H.MountNow();
        }

        public void Advance(int ms)
        {
            Clock.Advance(TimeSpan.FromMilliseconds(ms));
            H.Frame();
        }
    }

    [Fact]
    public void Animated_gif_advances_on_frame_duration_timers()
    {
        var gif = WithDelays(TestGif("anim-disposal.gif"), 20); // 200 ms
        var anim = ImageDecoder.DecodeAnimation(gif);
        Assert.True(anim.FrameCount > 2);
        var a = new AnimHarness(() => new Image { Id = "img", Source = ImageSource.FromBytes(gif) });
        var img = Img(a.H);
        Assert.Equal(0, img.CurrentFrame);
        // 只有一个待执行的定时器，不是每帧轮询
        Assert.Equal(1, a.Clock.PendingCount);
        Assert.False(a.H.Root.NeedsFrame);
        a.Advance(199);
        Assert.Equal(0, img.CurrentFrame);
        a.Advance(1);
        Assert.Equal(1, img.CurrentFrame);
        Assert.Equal(1, a.Clock.PendingCount);
        a.Advance(200);
        Assert.Equal(2, img.CurrentFrame);
        // 换帧只重录这张图片（及其祖先单元）
        Assert.Contains(img, a.H.Pipeline.LastRecordedElements);
    }

    [Fact]
    public void Tiny_frame_delays_are_clamped_to_100ms_like_browsers()
    {
        var gif = WithDelays(TestGif("anim-disposal.gif"), 0);
        var a = new AnimHarness(() => new Image { Id = "img", Source = ImageSource.FromBytes(gif) });
        var img = Img(a.H);
        a.Advance(99);
        Assert.Equal(0, img.CurrentFrame);
        a.Advance(1);
        Assert.Equal(1, img.CurrentFrame);
        Assert.Equal(TimeSpan.FromMilliseconds(100), ImageCache.FrameDuration(ImageDecoder.DecodeAnimation(gif), 0));
    }

    [Fact]
    public void Loop_count_is_honoured_and_playback_stops_on_the_last_frame()
    {
        var gif = WithDelays(TestGif("anim-disposal.gif"), 10); // 100 ms
        var anim = ImageDecoder.DecodeAnimation(gif);
        var a = new AnimHarness(() => new Image { Id = "img", Source = ImageSource.FromBytes(gif) });
        var img = Img(a.H);
        int frames = anim.FrameCount;
        int loops = anim.LoopCount;
        if (loops == 0)
        {
            // 无限循环：走两轮后仍在播放
            for (int i = 0; i < frames * 2; i++) a.Advance(100);
            Assert.Equal(0, img.CurrentFrame);
            Assert.Equal(1, a.Clock.PendingCount);
            return;
        }
        for (int i = 0; i < frames * loops + 5; i++) a.Advance(100);
        Assert.Equal(frames - 1, img.CurrentFrame);
        Assert.Equal(0, a.Clock.PendingCount);
    }

    [Fact]
    public void Late_timers_skip_to_the_frame_that_should_be_showing()
    {
        var gif = WithDelays(TestGif("anim-disposal.gif"), 10);
        var a = new AnimHarness(() => new Image { Id = "img", Source = ImageSource.FromBytes(gif) });
        var img = Img(a.H);
        // 时钟一次跳过 350 ms：定时器在 100 ms 到期时执行，此时只该显示第 1 帧
        a.Clock.Advance(TimeSpan.FromMilliseconds(350));
        a.H.Frame();
        Assert.Equal(1, img.CurrentFrame);
    }

    [Fact]
    public void Invisible_animations_do_not_schedule_timers()
    {
        var gif = WithDelays(TestGif("anim-disposal.gif"), 10);
        var hidden = new State<bool>(false);
        var a = new AnimHarness(() => new Div
        {
            Children =
            [
                new Image { Id = "img", Source = ImageSource.FromBytes(gif), Style = hidden.Value ? "display: none" : null },
                new Image { Id = "far", Source = ImageSource.FromBytes(gif, "far"), Style = "position: absolute; top: 5000px" },
            ],
        });
        var img = Img(a.H);
        // 视口外的那张从不安排定时器；可见的一张安排一个
        Assert.Equal(1, a.Clock.PendingCount);
        hidden.Value = true;
        a.H.Frame();
        a.Advance(100);
        // 到期时它在最近一轮绘制中不可见：不换帧、不再安排
        Assert.Equal(0, img.CurrentFrame);
        Assert.Equal(0, a.Clock.PendingCount);
        a.Advance(1000);
        Assert.Equal(0, a.Clock.PendingCount);
        Assert.False(a.H.Root.NeedsFrame);
        hidden.Value = false;
        a.H.Frame();
        Assert.Equal(1, a.Clock.PendingCount);
        a.Advance(100);
        Assert.Equal(1, img.CurrentFrame);
    }

    [Fact]
    public void Same_animation_shares_one_timeline_and_one_timer()
    {
        var gif = WithDelays(TestGif("anim-disposal.gif"), 10);
        var a = new AnimHarness(() => new Div
        {
            Children = [new Image { Id = "a", Source = ImageSource.FromBytes(gif) }, new Image { Id = "b", Source = ImageSource.FromBytes(gif) }],
        });
        Assert.Equal(1, a.Clock.PendingCount);
        a.Advance(100);
        Assert.Equal(1, Img(a.H, "a").CurrentFrame);
        Assert.Equal(1, Img(a.H, "b").CurrentFrame);
    }

    [Fact]
    public void Without_a_clock_animations_stay_on_the_first_frame()
    {
        var gif = WithDelays(TestGif("anim-disposal.gif"), 10);
        var h = new PipelineHarness(new Image { Id = "img", Source = ImageSource.FromBytes(gif) });
        Assert.Null(h.Root.Clock);
        Assert.Equal(0, Img(h).CurrentFrame);
        Assert.NotNull(Img(h).Content?.Animation);
    }
}
