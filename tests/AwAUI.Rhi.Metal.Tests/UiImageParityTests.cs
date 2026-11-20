using AwAUI.Graphics;
using AwAUI.Render;
using AwAUI.Render.Gpu;
using AwAUI.Svg;
using AwAUI.Text.Testing;
using AwAUI.UI;
using Xunit.Abstractions;

namespace AwAUI.Rhi.Metal.Tests;

/// <summary>
/// UI 的图片元素经两个后端画出的结果一致（第七批只在 scratchpad 的一次性程序里做过）：object-fit 的各种取值与 object-position、
/// 最近邻与双线性采样（含大比例缩小的 mip）、圆角裁剪、透明度（图层）、作为图片的 SVG、内联 SVG 图标（currentColor），
/// 以及加载失败的占位（图标 + 替代文本）。UI 的帧（DisplayList）由软件后端与 Metal 各渲染一次，容差与
/// <see cref="GpuParityTests"/> 的默认容差相同。没有 Metal 设备时跳过。旋转与缩放后的图片采样由
/// GpuParityTests 的 image-transforms 场景（DisplayList 层面）覆盖；UI 层的 CSS 变换、滤镜与背景滤镜见 <see cref="UiEffectsParityTests"/>。
/// </summary>
public class UiImageParityTests
{
    readonly ITestOutputHelper _output;

    public UiImageParityTests(ITestOutputHelper output) => _output = output;

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

    const int Width = 680, Height = 300;

    const string Css = """
        root { background-color: #f8fafc; padding: 12px; font-size: 13px; color: #1f2937; }
        .row { display: flex; gap: 12px; margin-bottom: 12px; }
        .cell { display: block; width: 96px; height: 72px; background-color: #cbd5e1; border: 1px solid #64748b; }
        .avatar { width: 72px; height: 72px; border-radius: 50%; border: 3px solid #4f46e5; }
        .icon { width: 72px; color: #db2777; background-color: #fff; }
        .fade { opacity: 0.6; }
        .broken { width: 150px; }
        """;

    /// <summary>三行：object-fit 网格；采样（最近邻 / 双线性 / 缩小）、圆角、SVG；透明度、失败占位。</summary>
    static Node Scene()
    {
        var photo = Make(48, 32, (x, y) =>
        {
            int dx = x - 24, dy = y - 16;
            return dx * dx + dy * dy < 64 ? ((byte)20, (byte)20, (byte)60) : ((byte)(x * 5), (byte)(y * 7), (byte)(255 - x * 4));
        });
        var pixel = Make(6, 4, (x, y) => ((x + y) & 1) == 0 ? ((byte)250, (byte)200, (byte)40) : ((byte)30, (byte)90, (byte)200));
        var stripes = Make(512, 512, (x, _) => (x & 1) == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255));
        var star = SvgDocument.Parse("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'><path fill='currentColor' d='M12 2l3 7h7l-5.5 4.5 2 7.5-6.5-4.5-6.5 4.5 2-7.5L2 9h7z'/></svg>");
        var badge = SvgDocument.Parse("<svg xmlns='http://www.w3.org/2000/svg' width='40' height='30'><rect width='40' height='30' rx='6' fill='#0ea5e9'/><circle cx='20' cy='15' r='9' fill='#fde047'/></svg>");

        var fits = new NodeList();
        foreach (var f in new[] { "fill", "contain", "cover", "none", "scale-down" })
            fits.Add(new Image { Source = ImageSource.FromBitmap(photo), Class = "cell", Style = "object-fit: " + f });
        fits.Add(new Image { Source = ImageSource.FromBitmap(photo), Class = "cell", Style = "object-fit: cover; object-position: left top" });
        var sampling = new NodeList
        {
            new Image { Source = ImageSource.FromBitmap(pixel), Class = "cell", Style = "image-rendering: pixelated" },
            new Image { Source = ImageSource.FromBitmap(pixel), Class = "cell" },
            new Image { Source = ImageSource.FromBitmap(stripes), Class = "cell" },
            new Image { Source = ImageSource.FromBitmap(photo), Class = "cell avatar", Style = "object-fit: cover" },
            new Image { Source = ImageSource.FromSvg(badge), Class = "cell", Style = "object-fit: contain" },
            new UI.Svg(star) { Class = "cell icon" },
        };
        var effects = new NodeList
        {
            new Image { Source = ImageSource.FromBitmap(photo), Class = "cell fade", Style = "object-fit: cover" },
            new Image { Source = ImageSource.FromBitmap(pixel), Class = "cell fade", Style = "image-rendering: pixelated" },
            // 解码失败：图标 + 替代文本（按元素的字体与颜色排版，裁剪到内容盒）
            new Image { Source = ImageSource.FromBytes(new byte[] { 1, 2, 3, 4 }), Alt = "Broken picture with a long caption", Class = "cell broken" },
            new Image { Source = ImageSource.FromBytes(new byte[] { 5, 6, 7, 8 }), Class = "cell", Style = "width: auto; height: auto" },
        };
        return new Div
        {
            Children =
            [
                new Div { Class = "row", Children = fits },
                new Div { Class = "row", Children = sampling },
                new Div { Class = "row", Children = effects },
            ],
        };
    }

    static DisplayList BuildFrame(float scale)
    {
        var pipeline = new RenderPipeline(new TestTextEngine(), includeDefaultStyleSheet: false);
        pipeline.AddStyleSheet(Css, "ui-images.css");
        pipeline.SetViewport(new Size(Width, Height), scale);
        var root = new UIRoot { Pipeline = pipeline, Clock = new ManualUIClock() };
        root.Mount(Scene());
        for (int i = 0; i < 5 && root.NeedsFrame; i++) root.RunFrame();
        return pipeline.Frame;
    }

    /// <summary>
    /// 用独立的 GPU 后端渲染：场景的字形与图片纹理不进入共享后端的图集与缓存（其他测试按共享图集的状态断言绘制调用数）。
    /// </summary>
    static Bitmap RenderGpuIsolated(DisplayList list, float scale)
    {
        lock (MetalTestSupport.Lock)
        {
            using var backend = new GpuRenderBackend(MetalDevice.Create());
            var size = new PixelSize((int)Math.Ceiling(Width * scale), (int)Math.Ceiling(Height * scale));
            using var target = backend.CreateOffscreenTarget(size, scale);
            target.Render(list, new RenderOptions(Color.White));
            var bmp = new Bitmap(size);
            unsafe
            {
                var span = new Span<byte>((void*)bmp.Address, bmp.RowBytes * size.Height);
                target.ReadPixels(new PixelRect(0, 0, size.Width, size.Height), span, PixelFormat.Rgba8888);
            }
            return bmp;
        }
    }

    [MetalTheory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void Ui_image_scenes_match_between_software_and_gpu(float scale)
    {
        var list = BuildFrame(scale);
        Assert.False(list.IsEmpty);
        using var expected = MetalTestSupport.RenderSoftware(list, Width, Height, scale);
        using var actual = RenderGpuIsolated(list, scale);
        string file = scale == 1 ? "ui-images" : "ui-images@2x";
        MetalTestSupport.Save(actual, file + ".gpu");
        var diff = ImageDiff.Compare(expected, actual);
        _output.WriteLine($"{file}: {diff}");
        var tol = GpuParityTests.Default;
        bool ok = diff.MaxDiff <= tol.MaxDiff && diff.Over8Percent <= tol.MaxOver8Percent
                  && diff.Over24Percent <= tol.MaxOver24Percent && diff.Over2Percent <= tol.MaxOver2Percent;
        if (!ok || Environment.GetEnvironmentVariable("AWAUI_SAVE_DIFF") == "1")
        {
            MetalTestSupport.Save(expected, file + ".software");
            diff.SaveDiff(file);
        }
        Assert.True(ok, $"{file}: GPU output differs from software beyond tolerance ({diff}; tolerance {tol}). See {MetalTestSupport.OutputDir}.");
    }

    [Fact]
    public void Scene_contains_bitmaps_svg_and_the_failure_placeholder()
    {
        // 场景本身（不需要 Metal）：确认对照覆盖了预期的绘制命令，而不是一张空白页
        var list = BuildFrame(1);
        int images = 0, nested = 0, glyphs = 0;
        Count(list, 0);
        void Count(DisplayList l, int depth)
        {
            var r = l.GetReader();
            while (r.MoveNext())
            {
                switch (r.Op)
                {
                    case DrawOp.DrawImage: images++; break;
                    case DrawOp.DrawGlyphRun: glyphs++; break;
                    case DrawOp.DrawDisplayList:
                        nested++;
                        if (depth < 64) Count(r.GetResource<DisplayList>(r.Read<Graphics.Commands.DrawDisplayListCmd>().DisplayListRef), depth + 1);
                        break;
                }
            }
        }
        Assert.True(images >= 10, $"DrawImage commands: {images}");
        Assert.True(nested >= 2, $"nested display lists (SVG): {nested}");
        Assert.True(glyphs >= 1, $"glyph runs (alt text): {glyphs}");
    }
}
