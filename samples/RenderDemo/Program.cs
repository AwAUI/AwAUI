using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using AwAUI;
using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Imaging;
using AwAUI.MacOS;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Render.Gpu;
using AwAUI.Rhi.Metal;
using AwAUI.Samples;
using RenderDemo;

// 用法：
//   RenderDemo                         打开窗口（macOS / Windows / Linux X11）；macOS 上默认用 GPU（Metal），没有 Metal 时用软件后端
//   RenderDemo --gpu | --software      选择渲染后端（--gpu 在不支持 Metal 时退回软件并提示）
//   RenderDemo --headless out.png      无窗口渲染一帧到 PNG（总是软件后端）
//   RenderDemo --frames 30 --dump x.png  窗口中运行 30 帧后把帧缓冲写入 PNG 并退出（用于自动验证）
//   RenderDemo --exit-after-ms 3000    窗口运行 3 秒后关闭，打印每帧 CPU 耗时的统计
//   RenderDemo --self-check [N]        第 N 帧（默认 30）起冻结动画，5 帧后自检：读回呈现到 CAMetalLayer 的可绘制纹理（GPU）
//                                      以及窗口服务器合成后的窗口内容（两种后端），分别与软件后端渲染同一 DisplayList 的结果比较
//   RenderDemo --simulate-device-loss N  （GPU 窗口）第 N 帧后模拟 GPU 设备丢失，验证回退到软件呈现
//   RenderDemo --bench [帧数] [--gpu]   离屏基准：软件后端（加 --gpu 另测 GPU 后端）渲染示例场景（640×440 逻辑像素 × 2），输出动画帧与静态帧的统计
//                                      测量请用优化构建：dotnet run -c <配置> -p:Optimize=true -- --bench 300
if (Array.IndexOf(args, "--bench") >= 0)
{
    Bench.Run(int.TryParse(Arg("--bench"), out var bf) ? bf : 300, Array.IndexOf(args, "--gpu") >= 0);
    return;
}
string? headlessOut = Arg("--headless");
string? dumpPath = Arg("--dump");
int maxFrames = int.TryParse(Arg("--frames"), out var n) ? n : 0;
int exitAfterMs = int.TryParse(Arg("--exit-after-ms"), out var ems) ? ems : 0;
int lossAfter = int.TryParse(Arg("--simulate-device-loss"), out var la) ? la : 0;
bool selfCheck = Array.IndexOf(args, "--self-check") >= 0;
int selfCheckFrame = int.TryParse(Arg("--self-check"), out var scf) && scf > 0 ? scf : 30;
bool software = Array.IndexOf(args, "--software") >= 0;

PlatformBackend platform = headlessOut != null ? new HeadlessPlatform { DefaultScale = 2 }
    : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? new MacPlatform()
    : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? new AwAUI.Win32.Win32Platform()
    : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } ? new AwAUI.X11.X11Platform()
    : new HeadlessPlatform { DefaultScale = 2 };

var window = platform.CreateWindow(new WindowOptions { Title = "AwAUI RenderDemo", Size = new Size(640, 440) });
// 组合根：GPU 可用时用 GPU（设备丢失时自动回退软件），否则软件；无窗口模式总是软件
RenderBackend backend = platform is HeadlessPlatform ? new SoftwareRenderBackend() : MetalRenderer.Create(preferGpu: !software);
if (Array.IndexOf(args, "--gpu") >= 0 && backend is not MetalRenderBackend) Console.Error.WriteLine("Metal is not available; using the software backend.");
backend.Degraded += msg => Console.Error.WriteLine("[degraded] " + msg);
if (selfCheck && backend is MetalRenderBackend capture) capture.CapturePresentedFrames = true;
var target = backend.CreateRenderTarget(window.Surface);
Console.WriteLine($"Backend: {backend.Name}" + (window is MacWindow mw ? $", presentation: {mw.PresentationMode}" : ""));
var app = new DemoApp(window, target, backend, maxFrames, dumpPath ?? headlessOut, platform) { SelfCheck = selfCheck, SelfCheckFrame = selfCheckFrame, SimulateDeviceLossAfter = lossAfter };
window.Events = app;
window.Show();
window.RequestFrame();

if (platform is HeadlessPlatform headless)
{
    headless.RunFrame();
    Console.WriteLine($"Wrote {headlessOut ?? "nothing"} ({app.LastFrameMs:F2} ms/frame)");
}
else
{
    if (exitAfterMs > 0)
    {
        new Thread(() =>
        {
            Thread.Sleep(exitAfterMs);
            platform.Dispatcher.Post(window.Close);
        }) { IsBackground = true }.Start();
    }
    var wall = Stopwatch.StartNew();
    platform.Run();
    app.PrintSummary(wall.Elapsed);
}
backend.Dispose();

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

/// <summary>离屏基准（只测光栅化；DisplayList 预先录好）。软件后端，加 --gpu 时另测 GPU 后端的 CPU 侧与 GPU 执行时间。</summary>
static class Bench
{
    const int Width = 640, Height = 440;
    const double Scale = 2;
    const int Warmup = 30;

    public static void Run(int frames, bool gpu)
    {
        var size = new PixelSize((int)(Width * Scale), (int)(Height * Scale));
        var attr = (DebuggableAttribute?)Attribute.GetCustomAttribute(typeof(SoftwareRenderBackend).Assembly, typeof(DebuggableAttribute));
        Console.WriteLine($"Scene: RenderDemo DemoScene, {size.Width}x{size.Height} device px, {frames} frames; optimized: {attr is null || !attr.IsJITOptimizerDisabled}");
        using var backend = new SoftwareRenderBackend();
        using var target = backend.CreateOffscreenTarget(size, Scale);
        MetalRenderBackend? metal = gpu ? MetalRenderer.TryCreate() : null;
        if (gpu && metal is null) Console.WriteLine("  (Metal is not available: GPU rows skipped)");
        using var gpuTarget = metal?.CreateOffscreenTarget(size, Scale);
        foreach (bool animated in new[] { true, false })
        {
            var scene = new DemoScene { Clicks = 5, Pointer = new Point(420, 300) };
            var lists = new DisplayList[frames + Warmup];
            for (int i = 0; i < lists.Length; i++)
            {
                if (animated) scene.Time = i / 60.0;
                lists[i] = scene.Build(new Size(Width, Height));
            }
            var times = new double[frames];
            for (int i = 0; i < lists.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                target.Render(lists[i], new RenderOptions(Color.White));
                if (i >= Warmup) times[i - Warmup] = sw.Elapsed.TotalMilliseconds;
            }
            string kind = animated ? "animated" : "static";
            Print("software " + kind, times);
            if (metal is null || gpuTarget is null) continue;
            // GPU：CPU 侧 = Render 调用（录制 + 编码 + 提交）；逐帧等待 GPU 完成，避免排队等待计入下一帧
            var cpu = new double[frames];
            var gpuTimes = new List<double>();
            for (int i = 0; i < lists.Length; i++)
            {
                var sw = Stopwatch.StartNew();
                gpuTarget.Render(lists[i], new RenderOptions(Color.White));
                double t = sw.Elapsed.TotalMilliseconds;
                metal.WaitForGpu();
                if (i < Warmup) continue;
                cpu[i - Warmup] = t;
                if (metal.LastFrameStats?.GpuTime is { } g) gpuTimes.Add(g.TotalMilliseconds);
            }
            Print("gpu cpu " + kind, cpu);
            if (gpuTimes.Count > 0) Print("gpu exec " + kind, gpuTimes.ToArray());
        }
        metal?.Dispose();
    }

    static void Print(string name, double[] times)
    {
        Array.Sort(times);
        double mean = 0;
        foreach (var t in times) mean += t;
        mean /= times.Length;
        Console.WriteLine($"  {name,-16} min {times[0],7:F3} ms   median {times[times.Length / 2],7:F3} ms   mean {mean,7:F3} ms   p95 {times[(int)(times.Length * 0.95)],7:F3} ms");
    }
}

sealed class DemoApp : IWindowEvents
{
    readonly WindowImpl _window;
    readonly RenderTarget _target;
    readonly RenderBackend _backend;
    readonly PlatformBackend _platform;
    readonly List<double> _cpuMs = new();
    readonly List<double> _gpuCpuMs = new();
    readonly List<double> _waitMs = new();
    DisplayList? _lastList;
    bool _checked;
    readonly int _maxFrames;
    readonly string? _dumpPath;
    readonly DemoScene _scene = new();
    readonly Stopwatch _fpsClock = Stopwatch.StartNew();
    int _frames, _fpsFrames;
    double _renderMs;

    public DemoApp(WindowImpl window, RenderTarget target, RenderBackend backend, int maxFrames, string? dumpPath, PlatformBackend platform)
    {
        _window = window; _target = target; _backend = backend; _maxFrames = maxFrames; _dumpPath = dumpPath; _platform = platform;
    }

    public double LastFrameMs { get; private set; }

    /// <summary>第 <see cref="SelfCheckFrame"/> 帧起冻结动画，5 帧后（屏幕上已是这一帧）读回实际呈现的帧并与软件渲染比较。</summary>
    public bool SelfCheck { get; init; }

    public int SelfCheckFrame { get; init; } = 30;

    double? _frozenTime;

    /// <summary>大于 0 时，第 N 帧之后模拟 GPU 设备丢失。</summary>
    public int SimulateDeviceLossAfter { get; init; }

    /// <summary>打印每帧 CPU 耗时（录制 + Render 调用）的统计；GPU 后端另列录制 + 编码（不含等待可绘制对象）。</summary>
    public void PrintSummary(TimeSpan wall)
    {
        Console.WriteLine($"Window closed after {wall.TotalSeconds:F1} s: {_frames} frames ({_frames / Math.Max(1e-9, wall.TotalSeconds):F1} fps), backend {_backend.Name}" +
                          (_window is MacWindow mw ? $", presentation {mw.PresentationMode}, frame clock {mw.FrameClock}" : ""));
        Stats("CPU per frame (build + Render call)", _cpuMs);
        if (_gpuCpuMs.Count > 0) Stats("GPU path CPU (build + record + encode)", _gpuCpuMs);
        if (_waitMs.Count > 0) Stats("  waiting in nextDrawable (in Render)", _waitMs);
    }

    static void Stats(string name, List<double> v)
    {
        if (v.Count == 0) return;
        // 跳过前 10 帧（JIT 与首次光栅化）
        var a = v.Count > 20 ? v.GetRange(10, v.Count - 10).ToArray() : v.ToArray();
        Array.Sort(a);
        double mean = 0;
        foreach (var x in a) mean += x;
        mean /= a.Length;
        Console.WriteLine($"  {name,-38} median {a[a.Length / 2],7:F3} ms   mean {mean,7:F3} ms   p95 {a[(int)(a.Length * 0.95)],7:F3} ms   max {a[a.Length - 1],7:F3} ms   (n={a.Length})");
    }

    void RunSelfCheck(string when)
    {
        if (!SelfCheck || _lastList is null) return;
        if (_backend is MetalRenderBackend metal)
        {
            var result = FrameCheck.CompareWithSoftware(metal, _lastList, _target.Scale, Color.Transparent);
            Console.WriteLine(result is { } r ? $"[self-check {when}] presented drawable vs software: {r}" : $"[self-check {when}] no presented frame captured ({_backend.Name})");
        }
        if (_window is MacWindow mac)
        {
            var result = FrameCheck.CompareWindow(mac, _lastList, _target.Scale, Color.Transparent);
            Console.WriteLine(result is { } r ? $"[self-check {when}] window capture ({mac.PresentationMode}) vs software: {r}" : $"[self-check {when}] window capture unavailable");
        }
    }

    public void OnFrame(TimeSpan timestamp)
    {
        if (SelfCheck && _frozenTime is null && _frames + 1 >= SelfCheckFrame) _frozenTime = timestamp.TotalSeconds;
        _scene.Time = _frozenTime ?? timestamp.TotalSeconds;
        var sw = Stopwatch.StartNew();
        var list = _scene.Build(_window.ClientSize);
        double buildMs = sw.Elapsed.TotalMilliseconds;
        _target.Render(list, new RenderOptions(Color.Transparent));
        LastFrameMs = sw.Elapsed.TotalMilliseconds;
        _lastList = list;
        _cpuMs.Add(LastFrameMs);
        if (_backend is MetalRenderBackend { IsSoftwareFallback: false, LastFrameStats: { } gs } gm)
        {
            _gpuCpuMs.Add(buildMs + (gs.RecordTime + gs.EncodeTime).TotalMilliseconds);
            _waitMs.Add(gm.LastDrawableWait.TotalMilliseconds);
        }
        if (SelfCheck && !_checked && _frames + 1 >= SelfCheckFrame + 5)
        {
            _checked = true;
            RunSelfCheck("frame " + (_frames + 1));
        }
        if (SimulateDeviceLossAfter > 0 && _frames + 1 == SimulateDeviceLossAfter && _backend is MetalRenderBackend lose)
        {
            Console.WriteLine($"Simulating GPU device loss after frame {_frames + 1}");
            lose.SimulateDeviceLoss();
        }
        _renderMs += LastFrameMs;
        _frames++;
        _fpsFrames++;

        if (_fpsClock.Elapsed.TotalSeconds >= 1)
        {
            var px = _target.PixelSize;
            _window.SetTitle($"AwAUI RenderDemo — {_fpsFrames / _fpsClock.Elapsed.TotalSeconds:F0} fps, " +
                             $"{_renderMs / _fpsFrames:F1} ms/frame (record {buildMs:F2} ms), {px.Width}x{px.Height} px, {list.CommandCount} cmds");
            _fpsClock.Restart();
            _fpsFrames = 0;
            _renderMs = 0;
        }

        if (_maxFrames > 0 && _frames >= _maxFrames || _platform is HeadlessPlatform)
        {
            if (_dumpPath != null) Dump(_dumpPath);
            if (_platform is not HeadlessPlatform)
            {
                Console.WriteLine($"Rendered {_frames} frames, last {LastFrameMs:F2} ms, {_target.PixelSize} px @ {_target.Scale}x");
                _window.Close();
            }
            return;
        }
        if (_scene.IsAnimating) _window.RequestFrame();
    }

    void Dump(string path)
    {
        var size = _target.PixelSize;
        var bytes = new byte[size.Width * size.Height * 4];
        _target.ReadPixels(new PixelRect(0, 0, size.Width, size.Height), bytes, PixelFormat.Rgba8888);
        using var bmp = new Bitmap(size);
        for (int y = 0; y < size.Height; y++) bytes.AsSpan(y * size.Width * 4, size.Width * 4).CopyTo(bmp.GetRow(y));
        PngCodec.Save(bmp, path);
        Console.WriteLine($"Framebuffer written to {path}");
    }

    public void OnResized(Size clientSize, double scale)
    {
        // GPU 目标（交换链）需要显式改变可绘制尺寸；软件的帧缓冲目标按表面尺寸自适应
        _target.Resize(PixelSize.FromSize(clientSize, scale), scale);
        _window.RequestFrame();
    }

    public void OnPointer(in RawPointerEvent e)
    {
        switch (e.Type)
        {
            case RawPointerEventType.Leave: _scene.Pointer = null; break;
            case RawPointerEventType.Down: _scene.Pressed = true; _scene.Pointer = e.Position; break;
            case RawPointerEventType.Up:
                if (_scene.Hovered == "primary") _scene.Clicks++;
                _scene.Pressed = false;
                break;
            default: _scene.Pointer = e.Position; break;
        }
        _scene.UpdateHover();
        _window.SetCursor(_scene.Hovered != null ? CursorShape.Hand : CursorShape.Arrow);
        _window.RequestFrame();
    }

    public void OnKey(in RawKeyEvent e)
    {
        if (e.Type == RawKeyEventType.Down && e.Key == PhysicalKey.Escape) _window.Close();
    }

    public void OnTextInput(string text) { }
    public void OnFocusChanged(bool focused) { }
    public bool OnCloseRequested() => true;
    public void OnClosed() { }
}
