using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AwAUI;
using AwAUI.Graphics;
using AwAUI.Imaging;
using AwAUI.Render.Gpu;
using AwAUI.Rhi.Metal;
using RenderDemo;

namespace GpuDemo;

/// <summary>
/// 最小的 CAMetalLayer 窗口：NSWindow 的内容视图以 CAMetalLayer 为宿主层，主循环自己取事件（没有原生回调），
/// 每次循环渲染一帧（nextDrawable 按显示器节拍阻塞）。作为不依赖平台层的最小参照保留；
/// 正式的窗口呈现在 AwAUI.MacOS（MacWindow 的 CAMetalLayer 模式，表面实现 AwAUI.Graphics.IMetalLayerSurface），
/// 用法见 samples/RenderDemo 与 samples/Counter（默认 GPU，--software 切换）。
/// </summary>
static unsafe class MetalWindow
{
    const string Lib = "/usr/lib/libobjc.A.dylib";

    [StructLayout(LayoutKind.Sequential)] struct CGPoint { public double X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct CGSize { public double W, H; }
    [StructLayout(LayoutKind.Sequential)] struct CGRect { public CGPoint Origin; public CGSize Size; }

    [DllImport(Lib)] static extern IntPtr objc_getClass(string name);
    [DllImport(Lib)] static extern IntPtr sel_registerName(string name);
    [DllImport(Lib)] static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Lib)] static extern void objc_autoreleasePoolPop(IntPtr pool);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr self, IntPtr sel, long a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern void SendBool(IntPtr self, IntPtr sel, byte a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern byte SendByte(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern double SendDouble(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern void SendDoubleArg(IntPtr self, IntPtr sel, double a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern IntPtr SendRect(IntPtr self, IntPtr sel, CGRect r, ulong style, ulong backing, byte defer);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern IntPtr SendEvent(IntPtr self, IntPtr sel, ulong mask, IntPtr date, IntPtr mode, byte dequeue);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern CGRect SendRectRet(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern CGPoint SendPointRet(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern IntPtr SendChars(IntPtr self, IntPtr sel, byte* s);
    [DllImport("/usr/lib/libSystem.dylib")] static extern IntPtr dlopen(string path, int mode);

    static IntPtr Cls(string n) => objc_getClass(n);
    static IntPtr Sel(string n) => sel_registerName(n);

    static IntPtr NSString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s + "\0");
        fixed (byte* p = bytes) return SendChars(Cls("NSString"), Sel("stringWithUTF8String:"), p);
    }

    public static int Run(int maxFrames, string? dumpPath, bool vsync)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            Console.Error.WriteLine("The window demo uses arm64 struct-return conventions; run --bench on Intel Macs.");
            return 1;
        }
        dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", 9);
        dlopen("/System/Library/Frameworks/QuartzCore.framework/QuartzCore", 9);
        var pool = objc_autoreleasePoolPush();
        var app = Send(Cls("NSApplication"), Sel("sharedApplication"));
        Send(app, Sel("setActivationPolicy:"), 0L);
        var rect = new CGRect { Size = new CGSize { W = 640, H = 440 } };
        var window = SendRect(Send(Cls("NSWindow"), Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"), rect, 1 | 2 | 4 | 8, 2, 0);
        SendBool(window, Sel("setReleasedWhenClosed:"), 0);
        Send(window, Sel("setTitle:"), NSString("AwAUI GpuDemo (Metal)"));
        var view = Send(window, Sel("contentView"));
        // 宿主层视图：先设置层再 wantsLayer
        var layer = Send(Cls("CAMetalLayer"), Sel("layer"));
        Send(view, Sel("setLayer:"), layer);
        SendBool(view, Sel("setWantsLayer:"), 1);
        double scale = SendDouble(window, Sel("backingScaleFactor"));
        SendDoubleArg(layer, Sel("setContentsScale:"), scale);
        Send(window, Sel("center"));
        Send(window, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
        SendBool(app, Sel("activateIgnoringOtherApps:"), 1);
        Send(app, Sel("finishLaunching"));

        using var backend = new GpuRenderBackend(MetalDevice.Create());
        backend.Degraded += m => Console.Error.WriteLine("[degraded] " + m);
        var device = (MetalDevice)backend.Device;
        var bounds = SendRectRet(view, Sel("bounds"));
        var swapchain = device.CreateSwapchain(layer, PixelSize.FromSize(new Size((float)bounds.Size.W, (float)bounds.Size.H), scale));
        swapchain.DisplaySync = vsync;
        using var target = backend.CreateRenderTarget(swapchain, scale);
        objc_autoreleasePoolPop(pool);

        var scene = new DemoScene();
        var clock = Stopwatch.StartNew();
        var fps = Stopwatch.StartNew();
        int frames = 0, fpsFrames = 0;
        double cpuMs = 0;
        var mode = NSString("kCFRunLoopDefaultMode");
        var distantPast = Send(Cls("NSDate"), Sel("distantPast"));
        Send(mode, Sel("retain"));
        Send(distantPast, Sel("retain"));

        while (true)
        {
            pool = objc_autoreleasePoolPush();
            IntPtr ev;
            while ((ev = SendEvent(app, Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"), ulong.MaxValue, distantPast, mode, 1)) != IntPtr.Zero)
                Send(app, Sel("sendEvent:"), ev);
            bool visible = SendByte(window, Sel("isVisible")) != 0;
            if (visible)
            {
                bounds = SendRectRet(view, Sel("bounds"));
                scale = SendDouble(window, Sel("backingScaleFactor"));
                var logical = new Size((float)bounds.Size.W, (float)bounds.Size.H);
                var px = PixelSize.FromSize(logical, scale);
                if (px != target.PixelSize || scale != target.Scale)
                {
                    SendDoubleArg(layer, Sel("setContentsScale:"), scale);
                    target.Resize(px, scale);
                }
                // 指针（窗口坐标 y 向上）
                var mouse = SendPointRet(window, Sel("mouseLocationOutsideOfEventStream"));
                scene.Pointer = new Point((float)mouse.X, (float)(bounds.Size.H - mouse.Y));
                scene.UpdateHover();
                scene.Time = clock.Elapsed.TotalSeconds;
                var list = scene.Build(logical);
                // Render 里的 nextDrawable 会按显示器节拍阻塞：CPU 时间取录制 + 编码
                target.Render(list, new RenderOptions(Color.White));
                if (((GpuRenderTarget)target).LastFrameStats is { } st) cpuMs += (st.RecordTime + st.EncodeTime).TotalMilliseconds;
                frames++;
                fpsFrames++;
            }
            objc_autoreleasePoolPop(pool);
            if (!visible) break;

            if (fps.Elapsed.TotalSeconds >= 1)
            {
                var s = ((GpuRenderTarget)target).LastFrameStats;
                ((GpuRenderTarget)target).WaitForGpu();
                var title = $"AwAUI GpuDemo (Metal) — {fpsFrames / fps.Elapsed.TotalSeconds:F0} fps, CPU {cpuMs / fpsFrames:F2} ms/frame, " +
                            $"GPU {(s?.GpuTime is { } g ? g.TotalMilliseconds.ToString("F2") : "?")} ms, {s?.DrawCalls} draws";
                pool = objc_autoreleasePoolPush();
                Send(window, Sel("setTitle:"), NSString(title));
                objc_autoreleasePoolPop(pool);
                Console.WriteLine(title);
                fps.Restart();
                fpsFrames = 0;
                cpuMs = 0;
            }
            if (maxFrames > 0 && frames >= maxFrames) break;
        }

        if (dumpPath is not null && target.PixelSize is { IsEmpty: false } size)
        {
            var pixels = new byte[size.Width * size.Height * 4];
            target.ReadPixels(new PixelRect(0, 0, size.Width, size.Height), pixels, PixelFormat.Rgba8888);
            using var bmp = new Bitmap(size);
            pixels.AsSpan().CopyTo(new Span<byte>((void*)bmp.Address, pixels.Length));
            PngCodec.Save(bmp, dumpPath);
            Console.WriteLine("Wrote " + dumpPath);
        }
        Send(window, Sel("close"));
        return 0;
    }
}
