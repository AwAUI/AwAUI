using AwAUI.Graphics;
using AwAUI.Graphics.Commands;
using AwAUI.Headless;
using AwAUI.Imaging;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Svg;
using AwAUI.Text.Testing;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 位图像素经 DrawImage 画出：各 object-fit 的像素位置、最近邻与双线性采样、圆角裁剪、动画换帧（位图版本）、
/// 局部光栅与整帧光栅一致、宿主的丢帧重画。
/// </summary>
public partial class ImageDrawTests
{
    static readonly Color Red = Color.Parse("#ff0000"), Green = Color.Parse("#00ff00"), Blue = Color.Parse("#0000ff"),
        Yellow = Color.Parse("#ffff00"), Black = Color.Parse("#000000"), White = Color.Parse("#ffffff");

    /// <summary>不透明位图（RGBA8888，不透明时预乘与否相同）。</summary>
    static Bitmap Make(int w, int h, Func<int, int, (byte R, byte G, byte B)> f)
    {
        var b = new Bitmap(new PixelSize(w, h));
        for (int y = 0; y < h; y++)
        {
            var row = b.GetRow(y);
            for (int x = 0; x < w; x++)
            {
                var (r, g, bl) = f(x, y);
                row[x * 4] = r;
                row[x * 4 + 1] = g;
                row[x * 4 + 2] = bl;
                row[x * 4 + 3] = 255;
            }
        }
        return b;
    }

    /// <summary>4×2：四列依次为红、绿、蓝、黄。</summary>
    static Bitmap Columns() => Make(4, 2, (x, _) => x switch
    {
        0 => ((byte)255, (byte)0, (byte)0),
        1 => ((byte)0, (byte)255, (byte)0),
        2 => ((byte)0, (byte)0, (byte)255),
        _ => ((byte)255, (byte)255, (byte)0),
    });

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>tests/TestFiles 下的动画 GIF，帧延迟改为 200 ms。</summary>
    static byte[] AnimatedGif()
    {
        var d = File.ReadAllBytes(Path.Combine(RepoRoot(), "tests", "TestFiles", "Images", "gif", "anim-disposal.gif"));
        for (int i = 0; i + 6 < d.Length; i++)
            if (d[i] == 0x21 && d[i + 1] == 0xF9 && d[i + 2] == 0x04)
            {
                d[i + 4] = 20;
                d[i + 5] = 0;
            }
        return d;
    }

    /// <summary>一张 40×40 的图片，内容盒在 (10, 10)，背景黑色（留白处可见）。页面白色。</summary>
    static Bitmap RenderFit(Bitmap bitmap, string style, float scale = 1)
    {
        var h = new PipelineHarness(new Div
        {
            Style = "padding: 10px",
            Children = [new Image { Id = "img", Source = ImageSource.FromBitmap(bitmap), Style = "display: block; width: 40px; height: 40px; background-color: #000; " + style }],
        }, width: 80, height: 70, scale: scale, defaultSheet: false);
        return h.Render();
    }

    static void At(Bitmap bmp, int x, int y, Color expected, float tolerance = 0.01f) =>
        ColorAssert.Near(expected, bmp.GetPixel(x, y), tolerance);

    // ------------------------------------------------------------ object-fit（最近邻，像素精确）

    [Fact]
    public void Fill_stretches_the_bitmap_over_the_content_box()
    {
        using var src = Columns();
        using var bmp = RenderFit(src, "image-rendering: pixelated");
        At(bmp, 10, 10, Red);
        At(bmp, 19, 49, Red);
        At(bmp, 20, 30, Green);
        At(bmp, 35, 30, Blue);
        At(bmp, 49, 49, Yellow);
        At(bmp, 50, 30, White);
        At(bmp, 9, 30, White);
    }

    [Fact]
    public void Contain_letterboxes_and_keeps_the_ratio()
    {
        using var src = Columns();
        using var bmp = RenderFit(src, "object-fit: contain; image-rendering: pixelated");
        // 40×20，垂直居中：y 20..40
        At(bmp, 15, 15, Black);
        At(bmp, 15, 20, Red);
        At(bmp, 45, 39, Yellow);
        At(bmp, 15, 45, Black);
    }

    [Fact]
    public void Cover_crops_through_the_source_rect_without_a_clip()
    {
        using var src = Columns();
        var h = new PipelineHarness(new Div
        {
            Style = "padding: 10px",
            Children = [new Image { Id = "img", Source = ImageSource.FromBitmap(src), Style = "display: block; width: 40px; height: 40px; object-fit: cover; image-rendering: pixelated" }],
        }, width: 80, height: 70, defaultSheet: false);
        using var bmp = h.Render();
        // 放大到 80×40，水平居中：第 0、3 列在内容盒外
        At(bmp, 10, 30, Green);
        At(bmp, 29, 30, Green);
        At(bmp, 30, 30, Blue);
        At(bmp, 49, 10, Blue);
        At(bmp, 9, 30, White);
        At(bmp, 50, 30, White);

        // 录制的是可见部分：源矩形是中间两列，目标就是内容盒，没有裁剪命令
        var images = new List<(Rect Src, Rect Dst, ImageSampling Sampling)>();
        var ops = new List<DrawOp>();
        FrameWalker.Walk(h.Pipeline.Frame, (DrawOp op, Point o, ref DisplayListReader r) =>
        {
            ops.Add(op);
            if (op != DrawOp.DrawImage) return;
            var c = r.Read<DrawImageCmd>();
            Assert.Same(src, r.GetImage(c));
            images.Add((c.Src, FrameWalker.Offset(c.Dst, o), c.Sampling));
        });
        var img = Assert.Single(images);
        Assert.Equal(new Rect(1, 0, 2, 2), img.Src);
        Assert.Equal(new Rect(10, 10, 40, 40), img.Dst);
        Assert.Equal(ImageSampling.Nearest, img.Sampling);
        Assert.DoesNotContain(DrawOp.ClipRect, ops);
        Assert.DoesNotContain(DrawOp.ClipRoundRect, ops);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("scale-down")]
    public void None_and_scale_down_draw_small_images_at_natural_size(string fit)
    {
        using var src = Columns();
        using var bmp = RenderFit(src, "object-fit: " + fit + "; image-rendering: pixelated");
        // 4×2 居中：x 28..32，y 29..31
        At(bmp, 28, 29, Red);
        At(bmp, 29, 30, Green);
        At(bmp, 30, 29, Blue);
        At(bmp, 31, 30, Yellow);
        At(bmp, 27, 29, Black);
        At(bmp, 32, 29, Black);
        At(bmp, 28, 31, Black);
    }

    [Fact]
    public void Object_position_moves_the_cover_crop()
    {
        using var src = Columns();
        using var bmp = RenderFit(src, "object-fit: cover; object-position: left top; image-rendering: pixelated");
        At(bmp, 10, 30, Red);
        At(bmp, 29, 30, Red);
        At(bmp, 30, 30, Green);
        At(bmp, 49, 30, Green);
        At(bmp, 50, 30, White);
    }

    [Fact]
    public void None_with_a_large_image_shows_the_centre_at_natural_size()
    {
        // 100×100：每个像素的红 = x、绿 = y
        using var src = Make(100, 100, (x, y) => ((byte)x, (byte)y, (byte)0));
        using var bmp = RenderFit(src, "object-fit: none; image-rendering: pixelated");
        // 居中：内容盒 (10, 10) 对应图片 (30, 30)
        var c = bmp.ToRgbaUnpremultiplied();
        int i = (10 * 80 + 10) * 4;
        Assert.Equal(30, c[i]);
        Assert.Equal(30, c[i + 1]);
        i = (49 * 80 + 49) * 4;
        Assert.Equal(69, c[i]);
        Assert.Equal(69, c[i + 1]);
        At(bmp, 50, 30, White);
    }

    // ------------------------------------------------------------ 采样

    [Fact]
    public void Pixelated_samples_nearest_and_auto_samples_bilinear()
    {
        using var src = Make(2, 1, (x, _) => x == 0 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255));
        using var nearest = RenderFit(src, "image-rendering: pixelated");
        using var crisp = RenderFit(src, "image-rendering: crisp-edges");
        using var linear = RenderFit(src, "");
        using var smooth = RenderFit(src, "image-rendering: smooth");
        // 最近邻：硬边
        At(nearest, 29, 30, Red);
        At(nearest, 30, 30, Blue);
        Assert.True(crisp.ToRgbaUnpremultiplied().AsSpan().SequenceEqual(nearest.ToRgbaUnpremultiplied()));
        // 双线性：中线两侧是过渡色，两端仍是纯色（边缘按钳制，不渗入外面）
        var mid = linear.GetPixel(29, 30);
        Assert.True(mid.R > 0.3f && mid.B > 0.3f, mid.ToString());
        At(linear, 10, 30, Red);
        At(linear, 49, 30, Blue);
        Assert.True(smooth.ToRgbaUnpremultiplied().AsSpan().SequenceEqual(linear.ToRgbaUnpremultiplied()));
    }

    [Fact]
    public void Image_rendering_maps_to_sampling()
    {
        Assert.Equal(ImageSampling.Nearest, ImagePainter.SamplingFor(AwAUI.Css.Values.CssImageRendering.Pixelated));
        Assert.Equal(ImageSampling.Nearest, ImagePainter.SamplingFor(AwAUI.Css.Values.CssImageRendering.CrispEdges));
        Assert.Equal(ImageSampling.Linear, ImagePainter.SamplingFor(AwAUI.Css.Values.CssImageRendering.Auto));
        Assert.Equal(ImageSampling.Linear, ImagePainter.SamplingFor(AwAUI.Css.Values.CssImageRendering.Smooth));
        Assert.Equal(ImageSampling.Linear, ImagePainter.SamplingFor(AwAUI.Css.Values.CssImageRendering.HighQuality));
    }

    [Fact]
    public void Fractional_scale_draws_the_same_layout()
    {
        using var src = Columns();
        using var bmp = RenderFit(src, "object-fit: contain; image-rendering: pixelated", scale: 1.5f);
        // 逻辑 (15, 30) → 设备 (22, 45)
        At(bmp, 22, 45, Red);
        At(bmp, 70, 45, Yellow);
        At(bmp, 22, 25, Black);
    }

    // ------------------------------------------------------------ 圆角与不透明度

    [Fact]
    public void Border_radius_clips_the_image_to_the_rounded_content_box()
    {
        using var src = Make(8, 8, (_, _) => ((byte)255, (byte)0, (byte)0));
        using var bmp = RenderFit(src, "border-radius: 50%; background-color: transparent");
        At(bmp, 30, 30, Red);
        At(bmp, 11, 11, White);  // 角上在圆外
        At(bmp, 48, 48, White);
        At(bmp, 30, 11, Red);    // 上边中点在圆内
    }

    [Fact]
    public void Padding_and_border_shrink_the_content_radius()
    {
        // 外缘半径 10，内边距 10：内容盒的半径为 0，不需要圆角裁剪
        using var src = Make(4, 4, (_, _) => ((byte)0, (byte)0, (byte)255));
        var h = new PipelineHarness(new Div
        {
            Style = "padding: 10px",
            Children = [new Image { Id = "img", Source = ImageSource.FromBitmap(src), Style = "display: block; width: 20px; height: 20px; padding: 10px; border-radius: 10px" }],
        }, width: 80, height: 70, defaultSheet: false);
        var ops = FrameWalker.Ops(h.Pipeline.Frame);
        Assert.Contains(DrawOp.DrawImage, ops);
        Assert.DoesNotContain(DrawOp.ClipRoundRect, ops);
        using var bmp = h.Render();
        At(bmp, 20, 20, Blue);
        At(bmp, 39, 39, Blue);
    }

    [Fact]
    public void Element_opacity_applies_to_the_image()
    {
        using var src = Make(4, 4, (_, _) => ((byte)0, (byte)0, (byte)0));
        using var bmp = RenderFit(src, "opacity: 0.5; background-color: transparent");
        var c = bmp.GetPixel(30, 30);
        Assert.InRange(c.R, 0.45f, 0.55f);
    }

    // ------------------------------------------------------------ SVG 中的位图

    [Fact]
    public void Svg_image_elements_with_data_uris_decode_through_imaging_by_default()
    {
        using var red = Make(2, 2, (_, _) => ((byte)255, (byte)0, (byte)0));
        string png = Convert.ToBase64String(PngCodec.Encode(red));
        string svg = "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'><image width='20' height='20' href='data:image/png;base64," + png + "'/>"
            + "<image x='10' width='10' height='10' href='data:image/png;base64,AAAA'/></svg>";
        var h = new PipelineHarness(new Div
        {
            Style = "padding: 10px",
            Children = [new Image { Id = "img", Source = ImageSource.FromBytes(System.Text.Encoding.UTF8.GetBytes(svg)), Style = "display: block" }],
        }, width: 60, height: 60, defaultSheet: false);
        Assert.Equal(ImageLoadState.Ready, ((ImageElement)h.El("img")).LoadState);
        using var bmp = h.Render();
        At(bmp, 12, 12, Red);
        At(bmp, 28, 28, Red);
        At(bmp, 31, 20, White);
        // 解析器本身：不认识或损坏的数据返回 null，而不是抛出
        Assert.Null(ImagingSvgImageResolver.Instance.Decode(new byte[] { 0, 0, 0 }, "image/png"));
        Assert.Null(ImagingSvgImageResolver.Instance.Decode(PngCodec.Encode(red).AsSpan(0, 30), "image/png"));
        Assert.Same(ImagingSvgImageResolver.Instance, ImagingSvgImageResolver.DefaultOptions.ImageResolver);
    }

    // ------------------------------------------------------------ 动画

    [Fact]
    public void Animated_frame_advance_bumps_the_bitmap_version_and_changes_pixels()
    {
        var clock = new ManualUIClock();
        var h = new PipelineHarness(new Div { Children = [new Image { Id = "img", Source = ImageSource.FromBytes(AnimatedGif()), Style = "display: block" }] },
            defaultSheet: false, deferMount: true);
        h.Root.Clock = clock;
        h.MountNow();
        var img = (ImageElement)h.El("img");
        var frameBitmap = img.Content!.Bitmap!;
        long v0 = frameBitmap.Version;
        using var first = h.Render();
        clock.Advance(TimeSpan.FromMilliseconds(200));
        h.Frame();
        Assert.Equal(1, img.CurrentFrame);
        Assert.Same(frameBitmap, img.Content!.Bitmap);
        Assert.True(frameBitmap.Version > v0);
        using var second = h.Render();
        Assert.False(first.ToRgbaUnpremultiplied().AsSpan().SequenceEqual(second.ToRgbaUnpremultiplied()));
        // 与单独解码的第 1 帧相同
        var anim = ImageDecoder.DecodeAnimation(AnimatedGif());
        using var expected = new Bitmap(anim.Size);
        anim.RenderFrame(1, expected);
        var box = h.Box("img");
        for (int y = 0; y < anim.Height; y += 3)
            for (int x = 0; x < anim.Width; x += 3)
                ColorAssert.Near(expected.GetPixel(x, y), second.GetPixel((int)box.X + x, (int)box.Y + y), 0.005f);
    }

    // ------------------------------------------------------------ 宿主：局部光栅与丢帧

    sealed partial class Gallery : Component
    {
        public Bitmap Photo = null!, Pixel = null!;
        public byte[] Gif = null!;
        public readonly State<int> Step = new(0);

        protected override Node Build()
        {
            int s = Step.Value;
            string[] fits = ["fill", "contain", "cover", "none", "scale-down"];
            var cells = new NodeList();
            for (int i = 0; i < fits.Length; i++)
                cells.Add(new Image { Key = i, Id = "fit-" + i, Source = ImageSource.FromBitmap(Photo), Class = "cell", Style = "object-fit: " + fits[(i + s) % fits.Length] });
            cells.Add(new Image { Key = 10, Id = "px", Source = ImageSource.FromBitmap(Pixel), Class = "cell", Style = "image-rendering: pixelated" });
            cells.Add(new Image { Key = 11, Id = "smooth", Source = ImageSource.FromBitmap(Pixel), Class = "cell" });
            cells.Add(new Image { Key = 12, Id = "avatar", Source = ImageSource.FromBitmap(Photo), Class = "cell avatar", Style = "object-position: " + (s * 10) + "% 50%" });
            cells.Add(new Image { Key = 13, Id = "gif", Source = ImageSource.FromBytes(Gif, "gif"), Class = "cell" });
            // 一个在图片上移动的半透明方块：损伤矩形切过图片中间
            cells.Add(new Div { Key = 14, Class = "mover", Style = "left: " + (7 + s * 23) + ".5px; top: " + (11 + s * 17) + ".25px" });
            return new Div { Class = "grid", Children = cells };
        }
    }

    const string GalleryCss = """
        :root { background-color: #f1f5f9; }
        .grid { display: flex; flex-wrap: wrap; gap: 7px; padding: 9px; position: relative; }
        .cell { width: 61px; height: 43px; background-color: #1e293b; border: 1px solid #94a3b8; object-fit: cover; }
        .avatar { width: 45px; height: 45px; border-radius: 50%; border: 2px solid #4f46e5; }
        .mover { position: absolute; width: 37px; height: 29px; background-color: rgba(220, 38, 38, 0.5); border-radius: 6px; }
        """;

    static Bitmap Photo() => Make(48, 32, (x, y) =>
    {
        int dx = x - 24, dy = y - 16;
        if (dx * dx + dy * dy < 64) return (20, 20, 60);
        return ((byte)(x * 5), (byte)(y * 7), (byte)(255 - x * 4));
    });

    static Bitmap Checker() => Make(6, 4, (x, y) => ((x + y) & 1) == 0 ? ((byte)250, (byte)200, (byte)40) : ((byte)30, (byte)90, (byte)200));

    static (UIHost Host, HeadlessPlatform Platform, Gallery Gallery, ManualUIClock Clock) CreateGallery(double scale, bool incremental, RenderBackend? backend = null)
    {
        var platform = new HeadlessPlatform { DefaultScale = scale };
        var window = platform.CreateWindow(new WindowOptions { Size = new Size(300, 170) });
        var host = new UIHost(window, backend ?? new SoftwareRenderBackend(), new TestTextEngine(), platform.Dispatcher);
        var clock = new ManualUIClock();
        host.Root.Clock = clock;
        host.Root.Images.AsyncDecoding = false;
        host.Pipeline.IncrementalPaint = incremental;
        host.PartialRaster = incremental;
        host.AddStyleSheet(GalleryCss);
        var g = new Gallery { Photo = Photo(), Pixel = Checker(), Gif = AnimatedGif() };
        window.Show();
        host.Mount(g);
        platform.RunFrame();
        return (host, platform, g, clock);
    }

    static byte[] Pixels(UIHost host) => ((HeadlessWindow)host.Window).Framebuffer!.ToRgbaUnpremultiplied();

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Partial_raster_with_images_equals_a_full_raster(double scale)
    {
        var inc = CreateGallery(scale, incremental: true);
        var full = CreateGallery(scale, incremental: false);
        Assert.True(Pixels(inc.Host).AsSpan().SequenceEqual(Pixels(full.Host)));
        for (int step = 1; step <= 5; step++)
        {
            // 动画换帧（只损伤 GIF）与状态变化（换 object-fit、移动方块）交替
            if (step % 2 == 1)
            {
                inc.Clock.Advance(TimeSpan.FromMilliseconds(200));
                full.Clock.Advance(TimeSpan.FromMilliseconds(200));
            }
            else
            {
                inc.Gallery.Step.Value = step;
                full.Gallery.Step.Value = step;
            }
            inc.Platform.RunFrame();
            full.Platform.RunFrame();
            Assert.Null(full.Host.LastDamage);
            if (step % 2 == 1)
            {
                // 只有 GIF 变了：局部光栅的损伤远小于整个窗口
                var d = inc.Host.LastDamage!.Value;
                Assert.True(d.Width * d.Height < 300 * 170 * scale * scale / 4, d.ToString());
            }
            var a = Pixels(inc.Host);
            var b = Pixels(full.Host);
            Assert.True(a.AsSpan().SequenceEqual(b), "step " + step + ": partial raster differs from full raster at " + FirstDiff(a, b, inc.Host));
        }
    }

    static string FirstDiff(byte[] a, byte[] b, UIHost host)
    {
        int w = ((HeadlessWindow)host.Window).Framebuffer!.Width;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return $"({i / 4 % w}, {i / 4 / w}) channel {i % 4}: {a[i]} vs {b[i]}";
        return "-";
    }

    /// <summary>委托给软件目标、可以模拟丢帧（交换链没有给出可绘制纹理：什么都不画，LastFramePresented 为 false）的后端。</summary>
    sealed class DroppingBackend : RenderBackend
    {
        readonly SoftwareRenderBackend _inner = new();
        public DroppingTarget? Target;
        public override string Name => "dropping";
        public override BackendCapabilities Capabilities => _inner.Capabilities;
        public override RenderTarget CreateRenderTarget(NativeSurface surface) => Target = new DroppingTarget(_inner.CreateRenderTarget(surface));
        public override RenderTarget CreateOffscreenTarget(PixelSize size, double scale) => _inner.CreateOffscreenTarget(size, scale);
        public override void Dispose() => _inner.Dispose();
    }

    sealed class DroppingTarget(RenderTarget inner) : RenderTarget
    {
        bool _presented = true;
        public int DropNext;
        public readonly List<PixelRect?> Damages = new();
        public override PixelSize PixelSize => inner.PixelSize;
        public override double Scale => inner.Scale;
        public override bool LastFramePresented => _presented;
        public override void Resize(PixelSize size, double scale) => inner.Resize(size, scale);
        public override void ReadPixels(PixelRect rect, Span<byte> destination, PixelFormat format) => inner.ReadPixels(rect, destination, format);

        public override void Render(DisplayList content, in RenderOptions options)
        {
            Damages.Add(options.Damage);
            if (DropNext > 0)
            {
                DropNext--;
                _presented = false;
                return;
            }
            inner.Render(content, options);
            _presented = true;
        }

        public override void Dispose() => inner.Dispose();
    }

    [Fact]
    public void Dropped_frame_is_rendered_again_in_full_on_the_next_tick()
    {
        var backend = new DroppingBackend();
        var g = CreateGallery(1, incremental: true, backend);
        var reference = CreateGallery(1, incremental: false);
        var target = backend.Target!;
        int renders = target.Damages.Count;

        // 一帧被丢弃：宿主记下丢帧并请求下一帧
        target.DropNext = 1;
        g.Gallery.Step.Value = 2;
        reference.Gallery.Step.Value = 2;
        g.Platform.RunFrame();
        reference.Platform.RunFrame();
        Assert.Equal(renders + 1, target.Damages.Count);
        Assert.Equal(1, g.Host.DroppedFrames);
        Assert.False(g.Host.LastFrame.Rendered);
        Assert.False(Pixels(g.Host).AsSpan().SequenceEqual(Pixels(reference.Host)));

        // 没有任何新变化，下一个节拍仍整帧重画（损伤为 null），窗口显示最新内容
        g.Platform.RunFrame();
        Assert.Equal(renders + 2, target.Damages.Count);
        Assert.Null(target.Damages[target.Damages.Count - 1]);
        Assert.True(g.Host.LastFrame.Rendered);
        Assert.Equal(1, g.Host.DroppedFrames);
        Assert.True(Pixels(g.Host).AsSpan().SequenceEqual(Pixels(reference.Host)));

        // 之后回到局部光栅，且不再请求多余的帧
        g.Platform.RunFrame();
        Assert.Equal(renders + 2, target.Damages.Count);
        g.Clock.Advance(TimeSpan.FromMilliseconds(200));
        g.Platform.RunFrame();
        Assert.Equal(renders + 3, target.Damages.Count);
        Assert.NotNull(target.Damages[target.Damages.Count - 1]);
    }

    [Fact]
    public void Consecutive_dropped_frames_keep_retrying_once_per_tick()
    {
        var backend = new DroppingBackend();
        var g = CreateGallery(1, incremental: true, backend);
        var target = backend.Target!;
        int renders = target.Damages.Count;
        target.DropNext = 3;
        g.Gallery.Step.Value = 1;
        for (int i = 1; i <= 4; i++)
        {
            g.Platform.RunFrame();
            Assert.Equal(renders + i, target.Damages.Count);   // 每个节拍恰好一次，不在帧内循环重试
        }
        Assert.Equal(3, g.Host.DroppedFrames);
        Assert.True(g.Host.LastFrame.Rendered);
        g.Platform.RunFrame();
        Assert.Equal(renders + 4, target.Damages.Count);
    }

    // ------------------------------------------------------------ 概览图（人工查看）

    /// <summary>
    /// 设置环境变量 AWAUI_IMAGE_SHEET=路径 时写出一张概览 PNG：第一行五种 object-fit 与 cover + left top，
    /// 第二行最近邻 / 双线性放大、大比例缩小（mip）、圆角头像、动画 GIF 与随 color 着色的 SVG 图标。不设置时只检查能画出。
    /// </summary>
    [Fact]
    public void Overview_sheet()
    {
        using var photo = Photo();
        using var pixel = Checker();
        using var stripes = Make(512, 512, (x, _) => (x & 1) == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255));
        var icon = SvgDocument.Parse("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path fill='currentColor' d='M12 2l3 7h7l-5.5 4.5 2 7.5-6.5-4.5-6.5 4.5 2-7.5L2 9h7z'/></svg>");
        string[] fits = ["fill", "contain", "cover", "none", "scale-down"];
        var row1 = new NodeList();
        foreach (var f in fits)
            row1.Add(new Image { Source = ImageSource.FromBitmap(photo), Class = "cell", Style = "object-fit: " + f });
        row1.Add(new Image { Source = ImageSource.FromBitmap(photo), Class = "cell", Style = "object-fit: cover; object-position: left top" });
        var row2 = new NodeList
        {
            new Image { Source = ImageSource.FromBitmap(pixel), Class = "cell", Style = "image-rendering: pixelated" },
            new Image { Source = ImageSource.FromBitmap(pixel), Class = "cell" },
            new Image { Source = ImageSource.FromBitmap(stripes), Class = "cell" },
            new Image { Source = ImageSource.FromBitmap(photo), Class = "cell avatar", Style = "object-fit: cover" },
            new Image { Source = ImageSource.FromBytes(AnimatedGif()), Class = "cell", Style = "object-fit: contain" },
            new Svg(icon) { Class = "cell icon" },
        };
        const string css = """
            root { background-color: #f8fafc; padding: 12px; }
            .row { display: flex; gap: 12px; margin-bottom: 12px; }
            .cell { display: block; width: 96px; height: 72px; background-color: #cbd5e1; border: 1px solid #64748b; }
            .avatar { width: 72px; height: 72px; border-radius: 50%; border: 3px solid #4f46e5; }
            .icon { width: 72px; color: #db2777; padding: 0; background-color: #fff; }
            """;
        var h = new PipelineHarness(new Div { Children = [new Div { Class = "row", Children = row1 }, new Div { Class = "row", Children = row2 }] },
            css, width: 680, height: 200, scale: 2, defaultSheet: false);
        using var bmp = h.Render();
        // 缩小 512 → 96 的 1 px 条纹：mip 预滤波后是均匀的灰
        var grey = bmp.GetPixel(281 * 2, 135 * 2);
        Assert.InRange(grey.R, 0.35f, 0.65f);
        var path = Environment.GetEnvironmentVariable("AWAUI_IMAGE_SHEET");
        if (!string.IsNullOrEmpty(path)) File.WriteAllBytes(path, PngCodec.Encode(bmp));
    }
}
