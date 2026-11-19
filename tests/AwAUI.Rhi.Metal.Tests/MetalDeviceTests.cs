using AwAUI.Graphics;
using AwAUI.Render.Rhi;
using AwAUI.Rhi.Metal;

namespace AwAUI.Rhi.Metal.Tests;

/// <summary>RHI 层面的 Metal 冒烟测试：清屏、上传、拷贝、回读与固定管线。</summary>
public class MetalDeviceTests
{
    static void Near(int expected, int actual) => Assert.InRange(actual, expected - 1, expected + 1);

    static GpuDevice Device => MetalTestSupport.Backend.Device;

    static byte[] Read(GpuTexture texture, PixelRect rect)
    {
        GpuReadback rb;
        using (var cl = Device.BeginCommands())
        {
            rb = cl.ReadTexture(texture, rect);
            cl.Submit().Wait();
        }
        using (rb)
        {
            var data = new byte[rb.RowBytes * rb.Size.Height];
            rb.CopyTo(data, rb.RowBytes);
            return data;
        }
    }

    [MetalFact]
    public void Device_reports_name_and_api()
    {
        lock (MetalTestSupport.Lock)
        {
            Assert.Equal("Metal", Device.Api);
            Assert.False(string.IsNullOrEmpty(Device.Name));
            Assert.True(Device.Capabilities.MaxTextureSize >= 8192);
        }
    }

    [MetalFact]
    public void Clear_pass_writes_premultiplied_color()
    {
        lock (MetalTestSupport.Lock)
        {
            using var t = Device.CreateTexture(new GpuTextureDesc(8, 4, GpuTextureFormat.Rgba8Unorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using (var cl = Device.BeginCommands())
            {
                cl.BeginRenderPass(new GpuRenderPassDesc(t, GpuLoadAction.Clear, new Color(1, 0.5f, 0, 0.5f))).End();
                cl.Submit().Wait();
            }
            var px = Read(t, new PixelRect(0, 0, 8, 4));
            Near(128, px[0]);
            Near(64, px[1]);
            Assert.Equal(0, px[2]);
            Near(128, px[3]);
        }
    }

    [MetalFact]
    public void Upload_and_copy_round_trip()
    {
        lock (MetalTestSupport.Lock)
        {
            using var a = Device.CreateTexture(new GpuTextureDesc(16, 16, GpuTextureFormat.R8Unorm, GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget));
            using var b = Device.CreateTexture(new GpuTextureDesc(16, 16, GpuTextureFormat.R8Unorm, GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget));
            var data = new byte[5 * 3];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 10 + 1);
            using (var cl = Device.BeginCommands())
            {
                cl.UploadTexture(a, new PixelRect(2, 3, 4, 3), data, 5);   // 行距 5，宽 4
                cl.CopyTexture(a, new PixelRect(2, 3, 4, 3), b, 10, 11);
                cl.Submit().Wait();
            }
            var px = Read(b, new PixelRect(10, 11, 4, 3));
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 4; x++)
                    Assert.Equal(data[y * 5 + x], px[y * 4 + x]);
        }
    }

    [MetalFact]
    public void Quad_pipeline_draws_rect_coverage()
    {
        lock (MetalTestSupport.Lock)
        {
            using var t = Device.CreateTexture(new GpuTextureDesc(8, 8, GpuTextureFormat.Rgba8Unorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using var dummy = Device.CreateTexture(new GpuTextureDesc(1, 1, GpuTextureFormat.Rgba8Unorm, GpuTextureUsage.Sampled));
            var inst = new GpuQuadInstance
            {
                Bounds = new(1, 1, 7, 7),
                Shape = new(1.5f, 1, 6, 6.25f),
                Color = new(0, 0, 1, 1),
            };
            var uniforms = new GpuQuadUniforms { Target = new(0, 0, 2f / 8, 2f / 8) };
            using (var cl = Device.BeginCommands())
            {
                var range = cl.Upload(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new[] { inst }.AsSpan()));
                var pass = cl.BeginRenderPass(new GpuRenderPassDesc(t, GpuLoadAction.Clear, new Color(0, 0, 0, 0)));
                pass.SetPipeline(GpuPipelineKind.Quad, GpuBlendState.PremultipliedOver);
                pass.SetBuffer(GpuQuadUniforms.InstanceSlot, range);
                pass.SetUniforms(GpuQuadUniforms.Slot, System.Runtime.InteropServices.MemoryMarshal.AsBytes(new[] { uniforms }.AsSpan()));
                for (int s = 0; s < 4; s++) pass.SetTexture(s, dummy);
                pass.DrawInstanced(GpuQuadUniforms.VertexCount, 1, 0);
                pass.End();
                cl.Submit().Wait();
            }
            var px = Read(t, new PixelRect(0, 0, 8, 8));
            int A(int x, int y) => px[(y * 8 + x) * 4 + 3];
            Assert.Equal(0, A(0, 0));
            Near(128, A(1, 2));   // 左边缘半像素
            Assert.Equal(255, A(3, 3));
            Near(64, A(3, 6));    // 下边缘 1/4 像素
            Near(32, A(1, 6));    // 角：1/2 × 1/4
            Assert.Equal(0, A(6, 3));
        }
    }

    /// <summary>
    /// 回归：超过 4 MB 的上传（大实例数组、比图集页还大的掩码）每次都新建上传块，而复用池只在小请求时取块，
    /// 每帧有一次大上传时池子每帧多一块、永不释放（30 帧 × 6 MB = 180 MB）。
    /// </summary>
    [MetalFact]
    public void Large_uploads_do_not_grow_the_upload_pool_without_bound()
    {
        using var device = MetalDevice.Create();
        var big = new byte[6 << 20];
        var small = new byte[1024];
        for (int frame = 0; frame < 30; frame++)
        {
            using var cl = device.BeginCommands();
            cl.Upload(small);
            cl.Upload(big);
            cl.Submit();
        }
        device.WaitIdle();
        // 最多 MaxFramesInFlight 帧同时在用，再加上池中的空闲上限
        long bound = MetalDevice.MaxFramesInFlight * (10L << 20) + MetalDevice.MaxFreeUploadBytes + (8L << 20);
        Assert.True(device.UploadChunkBytes <= bound, $"{device.UploadChunkBytes >> 20} MB of upload chunks alive ({device.UploadChunkCount} chunks)");
        Assert.True(device.UploadChunkBytes <= MetalDevice.MaxFreeUploadBytes + (10L << 20));
    }

    /// <summary>RHI 与各图形 API 着色器之间的数据契约：C# 结构体的大小与字段偏移必须与 MSL 的 QuadInstance / ClipShape / QuadUniforms 一致。</summary>
    [Fact]
    public void Quad_data_layout_matches_the_shader_contract()
    {
        Assert.Equal(GpuQuadInstance.SizeInBytes, System.Runtime.CompilerServices.Unsafe.SizeOf<GpuQuadInstance>());
        Assert.Equal(GpuClipShape.SizeInBytes, System.Runtime.CompilerServices.Unsafe.SizeOf<GpuClipShape>());
        Assert.Equal(32, System.Runtime.CompilerServices.Unsafe.SizeOf<GpuQuadUniforms>());
        string[] fields = { "Bounds", "Shape", "RadiiX", "RadiiY", "Color", "Paint0", "Paint1", "Texture", "Clip", "Extra", "Kinds" };
        for (int i = 0; i < fields.Length; i++)
            Assert.Equal(i * 16, (int)System.Runtime.InteropServices.Marshal.OffsetOf<GpuQuadInstance>(fields[i]));
    }
}
