using AwAUI.Css.Values;
using AwAUI.Graphics;
using AwAUI.Svg;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 图片的 CSS 映射（object-fit / object-position / image-rendering / aspect-ratio）、<see cref="ImageSource.FromStream"/> 的重复加载、
/// <see cref="ImageCache.Loaded"/> 不在布局中途触发。
/// </summary>
public class ImageCssTests
{
    const string RedSquare = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10' width='10' height='10'><rect width='10' height='10' fill='#ff0000'/></svg>";

    static readonly Color Red = Color.Parse("#ff0000");

    static byte[] Png(int w, int h)
    {
        using var bmp = new Bitmap(new PixelSize(w, h));
        bmp.Clear(Color.Parse("#336699"));
        return AwAUI.Imaging.PngCodec.Encode(bmp);
    }

    static bool IsRed(Color c) => c.R > 0.9f && c.G < 0.1f && c.B < 0.1f;

    // ------------------------------------------------------------ object-fit / object-position

    [Fact]
    public void Object_fit_and_position_come_from_css_and_changes_only_repaint()
    {
        var doc = SvgDocument.Parse(RedSquare);
        var position = new State<string>("center");
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Children = [new Image { Id = "img", Source = ImageSource.FromSvg(doc), Style = "object-position: " + position.Value }],
            },
        }, "img { display: block; width: 40px; height: 20px; object-fit: contain }", defaultSheet: false);
        Assert.Equal(new Rect(0, 0, 40, 20), h.Box("img"));
        using (var bmp = h.Render())
        {
            Assert.True(IsRed(bmp.GetPixel(20, 10)));
            ColorAssert.Near(Color.White, bmp.GetPixel(5, 10));
            ColorAssert.Near(Color.White, bmp.GetPixel(35, 10));
        }

        position.Value = "right";
        h.Frame();
        Assert.Equal(CssObjectFit.Contain, h.El("img").ComputedStyle!.ObjectFit);
        Assert.Equal(new Rect(0, 0, 40, 20), h.Box("img"));
        using (var bmp = h.Render())
        {
            Assert.True(IsRed(bmp.GetPixel(35, 10)));
            ColorAssert.Near(Color.White, bmp.GetPixel(15, 10));
        }
    }

    [Fact]
    public void Contain_uses_the_image_natural_ratio_even_with_width_and_height_attributes()
    {
        var doc = SvgDocument.Parse(RedSquare);
        var h = new PipelineHarness(new Div
        {
            Children = [new Image { Id = "img", Source = ImageSource.FromSvg(doc), Width = 100, Height = 50, Style = "display: block; object-fit: contain" }],
        }, defaultSheet: false);
        Assert.Equal(new Size(100, 50), h.Box("img").Size);
        using var bmp = h.Render();
        Assert.True(IsRed(bmp.GetPixel(50, 25)));
        ColorAssert.Near(Color.White, bmp.GetPixel(10, 25));
        ColorAssert.Near(Color.White, bmp.GetPixel(90, 25));
    }

    [Fact]
    public void Image_rendering_is_inherited_and_bitmaps_reach_the_painter()
    {
        long before = ImagePainter.DrawRequests;
        var h = new PipelineHarness(new Div
        {
            Style = "image-rendering: pixelated",
            Children = [new Image { Id = "img", Source = ImageSource.FromBytes(Png(4, 4)), Style = "width: 32px; height: 32px" }],
        });
        Assert.Equal(CssImageRendering.Pixelated, h.El("img").ComputedStyle!.ImageRendering);
        Assert.True(ImagePainter.DrawRequests > before);
    }

    // ------------------------------------------------------------ aspect-ratio

    [Theory]
    [InlineData("", 40, 20)]
    [InlineData("aspect-ratio: 1", 40, 40)]
    [InlineData("aspect-ratio: 1 / 2", 40, 80)]
    [InlineData("aspect-ratio: auto 1", 40, 20)]          // 有固有宽高比：auto 优先
    [InlineData("aspect-ratio: 0 / 1", 40, 20)]           // 退化比值按 auto
    [InlineData("aspect-ratio: 2; width: 100px", 100, 50)]
    [InlineData("aspect-ratio: 2; height: 10px", 20, 10)]
    [InlineData("aspect-ratio: auto 4; width: 100px", 100, 50)]
    public void Aspect_ratio_on_images(string style, float w, float hgt)
    {
        var h = new PipelineHarness(new Div
        {
            Children = [new Image { Id = "img", Source = ImageSource.FromBytes(Png(40, 20)), Style = style }],
        }, "div { display: flex; align-items: flex-start }");
        Assert.Equal(new Size(w, hgt), h.Box("img").Size);
    }

    [Fact]
    public void Width_and_height_attributes_win_over_a_css_ratio()
    {
        var h = new PipelineHarness(new Div
        {
            Children = [new Image { Id = "img", Source = ImageSource.FromBytes(Png(40, 20)), Width = 100, Height = 50, Style = "aspect-ratio: 1" }],
        }, "div { display: flex; align-items: flex-start }");
        Assert.Equal(new Size(100, 50), h.Box("img").Size);
    }

    [Fact]
    public void Image_without_content_reserves_space_from_the_css_fallback_ratio()
    {
        var h = new PipelineHarness(new Div
        {
            Children = [new Image { Id = "img", Source = ImageSource.FromBytes(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), Style = "width: 120px; aspect-ratio: auto 4 / 3" }],
        }, "div { display: flex; align-items: flex-start }");
        Assert.Equal(ImageLoadState.Failed, ((ImageElement)h.El("img")).LoadState);
        Assert.Equal(new Size(120, 90), h.Box("img").Size);
    }

    [Fact]
    public void Aspect_ratio_applies_to_non_replaced_boxes_and_relayouts_on_change()
    {
        var ratio = new State<string>("4 / 1");
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div
            {
                Children =
                [
                    new Div { Id = "fixed", Style = "width: 100px; aspect-ratio: " + ratio.Value },
                    new Div { Id = "auto", Style = "aspect-ratio: 2" },
                    new Div { Id = "bordered", Style = "width: 100px; aspect-ratio: 1; padding: 10px; box-sizing: border-box" },
                ],
            },
        }, defaultSheet: true);
        Assert.Equal(new Size(100, 25), h.Box("fixed").Size);
        Assert.Equal(new Size(400, 200), h.Box("auto").Size);
        Assert.Equal(25f, h.Box("auto").Y);
        Assert.Equal(new Size(100, 100), h.Box("bordered").Size);

        ratio.Value = "1";
        h.Frame();
        Assert.Equal(new Size(100, 100), h.Box("fixed").Size);
        Assert.Equal(100f, h.Box("auto").Y); // 后续盒子随之下移
    }

    // ------------------------------------------------------------ FromStream

    [Fact]
    public void Stream_source_can_be_loaded_again_after_release_and_by_another_root()
    {
        var stream = new MemoryStream(Png(6, 4));
        var source = ImageSource.FromStream(stream);
        var show = new State<bool>(true);
        var diags = new List<UIDiagnostic>();
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div { Children = show.Value ? [new Image { Id = "img", Source = source }] : [] },
        }, "div { display: flex; align-items: flex-start }", deferMount: true);
        h.Root.Diagnostic += diags.Add;
        h.MountNow();
        Assert.Equal(new Size(6, 4), h.Box("img").Size);
        Assert.False(stream.CanRead); // 读完即释放

        show.Value = false;
        h.Frame();
        Assert.Equal(0, h.Root.Images.Count);
        show.Value = true;
        h.Frame();
        Assert.Equal(ImageLoadState.Ready, ((ImageElement)h.El("img")).LoadState);
        Assert.Equal(new Size(6, 4), h.Box("img").Size);

        var other = new PipelineHarness(new Div { Children = [new Image { Id = "img", Source = source }] }, "div { display: flex; align-items: flex-start }");
        Assert.Equal(new Size(6, 4), other.Box("img").Size);
        Assert.DoesNotContain(diags, d => d.Code == "AW1010");
    }

    [Fact]
    public void Oversized_stream_is_not_retained_and_a_second_load_explains_why()
    {
        var source = ImageSource.FromStream(new MemoryStream(new byte[ImageSource.MaxRetainedStreamBytes + 1]));
        var show = new State<bool>(true);
        var diags = new List<UIDiagnostic>();
        var h = new PipelineHarness(new Lambda
        {
            Render = () => new Div { Children = show.Value ? [new Image { Id = "img", Source = source }] : [] },
        }, deferMount: true);
        h.Root.Diagnostic += diags.Add;
        h.MountNow();
        Assert.Equal(ImageLoadState.Failed, ((ImageElement)h.El("img")).LoadState); // 全零：无法识别的格式
        show.Value = false;
        h.Frame();
        show.Value = true;
        h.Frame();
        Assert.Equal(ImageLoadState.Failed, ((ImageElement)h.El("img")).LoadState);
        Assert.Contains(diags, d => d.Code == "AW1010" && d.Message.Contains("MaxRetainedStreamBytes"));
    }

    // ------------------------------------------------------------ Loaded

    [Fact]
    public void Loaded_for_synchronous_decodes_fires_after_paint_not_mid_layout()
    {
        var calls = new List<(long FrameVersion, float Width)>();
        var diagnosticsAfterPaint = new List<bool>();
        var h = new PipelineHarness(new Div
        {
            Style = "display: flex; align-items: flex-start",
            Children =
            [
                new Image { Id = "img", Source = ImageSource.FromBytes(Png(30, 10)) },
                new Image { Id = "bad", Source = ImageSource.FromBytes(new byte[] { 9, 9, 9, 9 }) },
            ],
        }, deferMount: true);
        h.Root.Images.Loaded += (s, e) => calls.Add((h.Pipeline.FrameVersion, h.Box("img").Width));
        h.Root.Diagnostic += d =>
        {
            if (d.Code == "AW1010") diagnosticsAfterPaint.Add(h.Pipeline.FrameVersion > 0);
        };
        h.MountNow();
        // 两次加载各触发一次；触发时布局与绘制都已完成
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal(1, c.FrameVersion));
        Assert.All(calls, c => Assert.Equal(30, c.Width));
        Assert.Equal(new[] { true }, diagnosticsAfterPaint);
    }

    [Fact]
    public void Loaded_with_a_dispatcher_fires_once_and_handler_errors_go_to_unhandled_error()
    {
        var previous = Reactive.CurrentThreadDispatcher;
        try
        {
            var dispatcher = new TestDispatcher();
            var pipeline = new RenderPipeline(new AwAUI.Text.Testing.TestTextEngine());
            pipeline.SetViewport(new Size(200, 200), 1);
            var root = new UIRoot(dispatcher) { Pipeline = pipeline };
            var errors = new List<UIError>();
            root.UnhandledError += errors.Add;
            int calls = 0;
            root.Images.Loaded += (s, e) =>
            {
                calls++;
                throw new InvalidOperationException("boom");
            };
            // 小图同步解码（不超过异步阈值）
            root.Mount(new Div { Children = [new Image { Source = ImageSource.FromBytes(Png(3, 3)) }, new Image { Source = ImageSource.FromBytes(Png(4, 4)) }] });
            Assert.Equal(2, calls);           // 绘制结束时已触发；一个处理器抛出不影响另一张图片的通知
            Assert.Equal(2, errors.Count);
            Assert.All(errors, e => Assert.Equal(ErrorPhase.EventHandler, e.Phase));
            dispatcher.Pump();                // 投递的回调发现队列已空
            Assert.Equal(2, calls);
        }
        finally
        {
            Reactive.CurrentThreadDispatcher = previous;
        }
    }
}
