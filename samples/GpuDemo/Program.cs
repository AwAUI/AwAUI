using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AwAUI;
using AwAUI.Graphics;
using AwAUI.Imaging;
using AwAUI.Render;
using AwAUI.Render.Gpu;
using AwAUI.Rhi.Metal;
using GpuDemo;
using RenderDemo;

// 用法：
//   GpuDemo --bench [帧数]                         离屏基准：软件与 GPU 后端渲染 RenderDemo 场景（1280×880 设备像素）
//   GpuDemo [--frames N] [--dump x.png] [--no-vsync]  CAMetalLayer 窗口，标题栏显示帧率；N 帧后退出并可截图
// 测量请用优化构建：dotnet run -c <配置> -p:Optimize=true -- --bench
if (!MetalDevice.IsSupported)
{
    Console.Error.WriteLine("Metal is not available on this machine (macOS with a Metal GPU is required).");
    return 1;
}

if (args.Contains("--bench"))
{
    int frames = int.TryParse(Arg("--bench"), out var f) ? f : 300;
    Bench.Run(frames);
    return 0;
}

return MetalWindow.Run(
    maxFrames: int.TryParse(Arg("--frames"), out var n) ? n : 0,
    dumpPath: Arg("--dump"),
    vsync: !args.Contains("--no-vsync"));

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
}

namespace GpuDemo
{
    /// <summary>离屏基准：RenderDemo 场景，640×440 逻辑像素 × 2 = 1280×880 设备像素。</summary>
    static class Bench
    {
        const int Width = 640, Height = 440;
        const double Scale = 2;

        public static void Run(int frames)
        {
            var size = new PixelSize((int)(Width * Scale), (int)(Height * Scale));
            Console.WriteLine($"Scene: RenderDemo DemoScene, {size.Width}x{size.Height} device px, {frames} frames per run");
            Console.WriteLine($"Optimized build: {IsOptimized()}");

            using var software = new SoftwareRenderBackend();
            using var swTarget = software.CreateOffscreenTarget(size, Scale);
            using var gpu = new GpuRenderBackend(MetalDevice.Create());
            gpu.Degraded += m => Console.Error.WriteLine("[degraded] " + m);
            using var gpuTarget = (GpuRenderTarget)gpu.CreateOffscreenTarget(size, Scale);
            Console.WriteLine($"GPU: {gpu.Device.Name} ({gpu.Device.Api})");

            foreach (bool animated in new[] { true, false })
            {
                Console.WriteLine();
                Console.WriteLine(animated ? "== Animated (chart, spinner and pointer glow change every frame) ==" : "== Static (same content every frame) ==");
                var scene = new DemoScene { Clicks = 5, Pointer = new Point(420, 300) };
                var lists = new List<DisplayList>();
                var build = new List<double>();
                for (int i = 0; i < frames + 30; i++)
                {
                    if (animated) scene.Time = i / 60.0;
                    var sw = Stopwatch.StartNew();
                    lists.Add(scene.Build(new Size(Width, Height)));
                    build.Add(sw.Elapsed.TotalMilliseconds);
                }
                Report("DisplayList build (shared)", build.Skip(30));

                // 软件后端
                var swTimes = new List<double>();
                for (int i = 0; i < lists.Count; i++)
                {
                    var sw = Stopwatch.StartNew();
                    swTarget.Render(lists[i], new RenderOptions(Color.White));
                    if (i >= 30) swTimes.Add(sw.Elapsed.TotalMilliseconds);
                }
                Report("Software render", swTimes);

                // GPU：逐帧等待 GPU 完成，分别得到 CPU 录制 / 编码与 GPU 执行时间
                var cpu = new List<double>();
                var record = new List<double>();
                var encode = new List<double>();
                var gpuTime = new List<double>();
                var latency = new List<double>();
                GpuFrameStats? last = null;
                for (int i = 0; i < lists.Count; i++)
                {
                    var sw = Stopwatch.StartNew();
                    gpuTarget.Render(lists[i], new RenderOptions(Color.White));
                    double submit = sw.Elapsed.TotalMilliseconds;
                    gpuTarget.WaitForGpu();
                    double done = sw.Elapsed.TotalMilliseconds;
                    if (i < 30) continue;
                    last = gpuTarget.LastFrameStats!;
                    cpu.Add(submit);
                    record.Add(last.RecordTime.TotalMilliseconds);
                    encode.Add(last.EncodeTime.TotalMilliseconds);
                    if (last.GpuTime is { } g) gpuTime.Add(g.TotalMilliseconds);
                    latency.Add(done);
                }
                Report("GPU: CPU side (record + encode + submit)", cpu);
                Report("  of which record (DisplayList → instances, masks)", record);
                Report("  of which encode (uploads + RHI commands)", encode);
                Report("GPU: GPU execution (command buffer start → end)", gpuTime);
                Report("GPU: submit → completed (serialized)", latency);

                // 流水线吞吐：不逐帧等待（设备最多 3 帧在途）
                var total = Stopwatch.StartNew();
                for (int i = 0; i < lists.Count; i++) gpuTarget.Render(lists[i], new RenderOptions(Color.White));
                gpuTarget.WaitForGpu();
                Console.WriteLine($"  {"GPU: pipelined throughput",-52} {total.Elapsed.TotalMilliseconds / lists.Count,7:F3} ms/frame");
                Console.WriteLine($"  last frame: {last}");
            }

            // 输出一帧供人工检查
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "gpudemo-bench.png");
            var pixels = new byte[size.Width * size.Height * 4];
            gpuTarget.ReadPixels(new PixelRect(0, 0, size.Width, size.Height), pixels, PixelFormat.Rgba8888);
            using (var bmp = new Bitmap(size))
            {
                unsafe { pixels.AsSpan().CopyTo(new Span<byte>((void*)bmp.Address, pixels.Length)); }
                PngCodec.Save(bmp, path);
            }
            Console.WriteLine();
            Console.WriteLine("Wrote " + path);
            var stats = gpu.Statistics;
            Console.WriteLine($"Backend: {stats.AtlasPages} atlas pages, {stats.AtlasEvictions} evictions, {stats.CachedGlyphs} glyphs, " +
                              $"{stats.CachedPathMasks} cached path masks, {stats.GradientLutPages} LUT pages, {stats.PooledTextures} pooled textures");
        }

        static void Report(string name, IEnumerable<double> samples)
        {
            var s = samples.OrderBy(x => x).ToArray();
            if (s.Length == 0)
            {
                Console.WriteLine($"  {name,-52} n/a");
                return;
            }
            Console.WriteLine($"  {name,-52} median {s[s.Length / 2],7:F3} ms   mean {s.Average(),7:F3} ms   p95 {s[(int)(s.Length * 0.95)],7:F3} ms");
        }

        static bool IsOptimized()
        {
            var attr = (DebuggableAttribute?)Attribute.GetCustomAttribute(typeof(GpuRenderBackend).Assembly, typeof(DebuggableAttribute));
            return attr is null || !attr.IsJITOptimizerDisabled;
        }
    }
}
