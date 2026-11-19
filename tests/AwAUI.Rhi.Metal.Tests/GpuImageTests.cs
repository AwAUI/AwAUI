using System.Numerics;
using AwAUI.Graphics;
using AwAUI.Render.Gpu;
using AwAUI.Render.Gpu.Atlas;
using AwAUI.RenderTests;

namespace AwAUI.Rhi.Metal.Tests;

/// <summary>
/// GPU 后端的 DrawImage：纹理缓存按（位图，内容版本）做键（动画图片复用同一张位图）、超过纹理尺寸上限的位图、
/// 已释放的位图、每帧上传上限与实例上限（扇出）。与软件后端的像素对照在 <see cref="GpuParityTests"/>（image: 场景）。
/// </summary>
public class GpuImageTests
{
    static unsafe Bitmap Read(RenderTarget target)
    {
        var size = target.PixelSize;
        var bmp = new Bitmap(size);
        target.ReadPixels(new PixelRect(0, 0, size.Width, size.Height), new Span<byte>((void*)bmp.Address, bmp.RowBytes * size.Height), PixelFormat.Rgba8888);
        return bmp;
    }

    static byte[] Pixel(Bitmap b, int x, int y) => b.GetRow(y).Slice(x * 4, 4).ToArray();

    [MetalFact]
    public void Texture_cache_is_keyed_by_bitmap_version()
    {
        using var img = new Bitmap(new PixelSize(32, 32));
        img.Clear(Color.Parse("#ff0000"));
        var dc = new DrawingContext();
        dc.DrawImage(img, new Rect(0, 0, 32, 32));     // 第 0 级
        dc.DrawImage(img, new Rect(40, 0, 8, 8));      // 缩小 4 倍：mip
        var list = dc.Finish();
        lock (MetalTestSupport.Lock)
        {
            var backend = MetalTestSupport.Backend;
            using var target = backend.CreateOffscreenTarget(new PixelSize(64, 32), 1);
            target.Render(list, new RenderOptions(Color.Transparent));
            long created = backend.Statistics.ImageTexturesCreated;
            target.Render(list, new RenderOptions(Color.Transparent));
            Assert.Equal(created, backend.Statistics.ImageTexturesCreated);   // 同一版本：命中缓存
            using (var a = Read(target))
            {
                Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(a, 16, 16));
                Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(a, 44, 4));
            }

            // 动画：同一张位图写入新内容并递增版本
            img.Clear(Color.Parse("#0000ff"));
            target.Render(list, new RenderOptions(Color.Transparent));
            Assert.Equal(created + 2, backend.Statistics.ImageTexturesCreated);
            using (var b = Read(target))
            {
                Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(b, 16, 16));
                Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(b, 44, 4));
            }
            // 旧版本的纹理在下一帧开始时释放
            int before = backend.Statistics.ImageTextures;
            for (int i = 0; i < 5; i++)
            {
                img.Invalidate();
                target.Render(list, new RenderOptions(Color.Transparent));
            }
            Assert.True(backend.Statistics.ImageTextures <= before, $"{backend.Statistics.ImageTextures} textures cached");
        }
    }

    [MetalFact]
    public void Bitmap_wider_than_the_texture_limit_draws_without_crashing()
    {
        int max = MetalTestSupport.Backend.Device.Capabilities.MaxTextureSize;
        using var img = TestImages.Stripes(max + 300, 8);
        var degraded = new List<string>();
        var dc = new DrawingContext();
        // 1:1 的源矩形：只上传裁剪区（第 0 级），结果与软件相同
        dc.DrawImage(img, new Rect(max, 0, 64, 8), new Rect(0, 0, 64, 8));
        // 整张缩小：mip 级别
        dc.DrawImage(img, new Rect(0, 10, 64, 8));
        // 整张几乎不缩小地画（第 0 级放不下 → 降级为更高的级别并上报）
        dc.Transform(Matrix3x2.CreateTranslation(-max + 50, 20));
        dc.DrawImage(img, new Rect(0, 0, max + 300, 8));
        var list = dc.Finish();
        lock (MetalTestSupport.Lock)
        {
            var backend = MetalTestSupport.Backend;
            backend.Degraded += degraded.Add;
            try
            {
                using var target = backend.CreateOffscreenTarget(new PixelSize(64, 28), 1);
                target.Render(list, new RenderOptions(Color.White));
                using var gpu = Read(target);
                using var sw = MetalTestSupport.RenderSoftware(list, 64, 28);
                for (int y = 0; y < 18; y++)
                    for (int x = 0; x < 64; x++)
                        for (int c = 0; c < 4; c++)
                            Assert.InRange(Math.Abs(Pixel(gpu, x, y)[c] - Pixel(sw, x, y)[c]), 0, 2);
            }
            finally
            {
                backend.Degraded -= degraded.Add;
            }
        }
        Assert.Contains(degraded, d => d.Contains("texture limit"));
    }

    [MetalFact]
    public void Disposed_bitmap_is_skipped_and_reported()
    {
        var img = TestImages.Checker(4, 4);
        var dc = new DrawingContext();
        dc.DrawImage(img, new Rect(0, 0, 8, 8));
        var list = dc.Finish();
        img.Dispose();
        var degraded = new List<string>();
        lock (MetalTestSupport.Lock)
        {
            var backend = MetalTestSupport.Backend;
            backend.Degraded += degraded.Add;
            try
            {
                using var target = backend.CreateOffscreenTarget(new PixelSize(8, 8), 1);
                target.Render(list, new RenderOptions(Color.White));
            }
            finally
            {
                backend.Degraded -= degraded.Add;
            }
        }
        Assert.Contains(degraded, d => d.Contains("disposed"));
    }

    [MetalFact]
    public void Many_distinct_large_images_hit_the_per_frame_upload_limit()
    {
        // 每张 4096×2048 = 32 MB，第 9 张起超出每帧 256 MB 的上传上限：跳过并上报，而不是无界地分配显存
        var images = Enumerable.Range(0, 9).Select(_ => new Bitmap(new PixelSize(4096, 2048))).ToArray();
        try
        {
            var dc = new DrawingContext();
            foreach (var img in images) dc.DrawImage(img, new Rect(0, 0, 4096, 2048), new Rect(0, 0, 4096, 2048));
            var list = dc.Finish();
            var degraded = new List<string>();
            lock (MetalTestSupport.Lock)
            {
                var backend = MetalTestSupport.Backend;
                backend.Degraded += degraded.Add;
                try
                {
                    using var target = backend.CreateOffscreenTarget(new PixelSize(16, 16), 1);
                    target.Render(list, new RenderOptions(Color.White));
                    Assert.True(backend.Statistics.ImageTextureBytes <= ImageTextures.MaxUploadBytesPerFrame + ImageTextures.BudgetBytes);
                    // 下一帧：缓存收缩回预算之内
                    target.Render(DisplayList.Empty, new RenderOptions(Color.White));
                    target.Render(DisplayList.Empty, new RenderOptions(Color.White));
                    Assert.True(backend.Statistics.ImageTextureBytes <= ImageTextures.BudgetBytes);
                }
                finally
                {
                    backend.Degraded -= degraded.Add;
                }
            }
            Assert.Contains(degraded, d => d.Contains("image data uploaded in one frame"));
        }
        finally
        {
            foreach (var img in images) img.Dispose();
        }
    }

    [MetalFact]
    public void Image_fan_out_is_bounded_by_the_frame_limits()
    {
        using var img = TestImages.Checker(16, 16);
        var leaf = new DrawingContext();
        leaf.DrawImage(img, new Rect(0, 0, 128, 128), ImageSampling.Linear, 0.5f);
        var list = leaf.Finish();
        for (int i = 0; i < 22; i++)
        {
            var dc = new DrawingContext();
            dc.DrawDisplayList(list);
            dc.DrawDisplayList(list);
            list = dc.Finish();
        }
        var degraded = new List<string>();
        lock (MetalTestSupport.Lock)
        {
            var backend = MetalTestSupport.Backend;
            backend.Degraded += degraded.Add;
            try
            {
                using var target = (GpuRenderTarget)backend.CreateOffscreenTarget(new PixelSize(128, 128), 1);
                target.Render(list, new RenderOptions(Color.White));
                target.WaitForGpu();
                Assert.True(target.LastFrameStats!.Instances <= Render.Gpu.Frame.FrameRecorder.MaxInstancesPerFrame);
            }
            finally
            {
                backend.Degraded -= degraded.Add;
            }
        }
        Assert.Contains(degraded, d => d.Contains("work limit") || d.Contains("draw commands"));
    }
}
