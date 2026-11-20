using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using AwAUI;
using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Imaging;
using AwAUI.MacOS;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Rhi.Metal;
using AwAUI.Shadcn;
using AwAUI.Text;
using AwAUI.UI;
using ShadcnGallery;

string? output = Option("--headless");
string? motionFrames = Option("--motion-frames");
string motionComponent = Option("--motion-component") ?? "menu";
bool reduceMotion = Has("--reduced-motion");
int width = Dimension("--width", 1024);
int height = Dimension("--height", motionFrames is null ? 650 : 1420);
bool benchmark = Has("--bench-render");
bool dialogPreview = Has("--dialog") || benchmark;
bool interactiveDialog = Has("--open-dialog") && !dialogPreview && output is null;
bool timings = Has("--timings");
double previewScale = Scale();
var fonts = new FontCollection();
fonts.RegisterFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSans-Regular.ttf"));
foreach (string family in new[] { "sans-serif", "system-ui", "ui-sans-serif" }) fonts.SetGenericFamily(family, "Noto Sans");

PlatformBackend platform = output is not null || dialogPreview || motionFrames is not null ? new HeadlessPlatform { DefaultScale = previewScale }
    : OperatingSystem.IsMacOS() ? new MacPlatform()
    : OperatingSystem.IsWindows() ? new AwAUI.Win32.Win32Platform()
    : OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } ? new AwAUI.X11.X11Platform()
    : new HeadlessPlatform { DefaultScale = previewScale };
try
{
    // The gallery uses the same native presentation path as the other windowed samples.
    // Headless PNGs always use software for reproducible capture.
    using var backend = MetalRenderer.Create(preferGpu: platform is MacPlatform && !Has("--software"));
    backend.Degraded += message => Console.Error.WriteLine(message);
    ShadcnTheme.Register();
    var sampleWindow = platform.CreateWindow(new WindowOptions { Title = "AwAUI · Shadcn gallery", Size = new Size(width, height) });
    using var host = new UIHost(sampleWindow, backend, new DefaultTextEngine(fonts), platform.Dispatcher);
    host.Root.Diagnostic += d => Console.Error.WriteLine(d);
    if (reduceMotion) host.Pipeline.StyleEngine.Environment.Media.PrefersReducedMotion = true;
    var clock = platform is HeadlessPlatform ? new ManualUIClock() : null;
    if (clock is not null) host.Root.Clock = clock;
    sampleWindow.Show();
    host.Mount(new App { Dark = Has("--dark"), ShowCatalog = !benchmark && (!dialogPreview || Has("--catalog")) });
    host.Closed += _ => platform.Exit();
    Console.WriteLine(FormattableString.Invariant($"{platform.Name} / {backend.Name} @ {host.Window.RenderScale:g}x"));
    if (timings) Console.WriteLine(host.MountStatistics);
    bool dialogQueued = false;
    if (timings || interactiveDialog)
    {
        host.FrameCompleted += h =>
        {
            if (interactiveDialog && !dialogQueued)
            {
                dialogQueued = true;
                var trigger = FindById(h.Root.RootElement, "edit-workspace")
                    ?? throw new InvalidOperationException("Dialog trigger is missing.");
                h.Root.Input.Click(trigger);
            }
            if (timings && h.LastFrame.Rendered)
                Console.WriteLine(FormattableString.Invariant($"{backend.Name} {h.LastFrame} dropped={h.DroppedFrames}"));
        };
    }
    if (platform is HeadlessPlatform headless && host.Window is HeadlessWindow window)
    {
        headless.RunFrame();
        if (motionFrames is not null)
        {
            SaveMotionFrames(host, window, clock!, motionComponent, motionFrames);
            return;
        }
        if (dialogPreview)
        {
            var trigger = FindById(host.Root.RootElement, "edit-workspace") ?? throw new InvalidOperationException("Dialog trigger is missing.");
            Point point = trigger.GetBoundingClientRect().Center;
            window.SendPointer(new RawPointerEvent(RawPointerEventType.Down, PointerType.Mouse, 1, point,
                PointerButtons.Left, PointerButtons.Left, KeyModifiers.None, clock!.Now));
            window.SendPointer(new RawPointerEvent(RawPointerEventType.Up, PointerType.Mouse, 1, point,
                PointerButtons.Left, PointerButtons.None, KeyModifiers.None, clock!.Now));
        }
        for (int i = 0; i < 24; i++)
        {
            clock!.Advance(headless.FrameInterval);
            headless.RunFrame();
        }
        if (dialogPreview && !host.Root.Overlay.IsAttached)
            throw new InvalidOperationException("The dialog did not open.");
        if (benchmark) BenchmarkRender(host.Pipeline.Frame, host.Pipeline.Viewport, host.Window.RenderScale);
        if (!benchmark || output is not null)
        {
            string path = output ?? "shadcn-gallery.png";
            PngCodec.Save(window.Framebuffer ?? throw new InvalidOperationException("No gallery frame was rendered."), path);
            Console.WriteLine($"Wrote {path}");
        }
    }
    else platform.Run();
}
finally
{
    (platform as IDisposable)?.Dispose();
}

static void SaveMotionFrames(UIHost host, HeadlessWindow window, ManualUIClock clock, string component, string directory)
{
    if (component is not ("dialog" or "select" or "menu" or "submenu" or "popover" or "sheet" or "accordion"))
        throw new ArgumentException("Motion component must be dialog, select, menu, submenu, popover, sheet, or accordion.");
    string triggerName = component == "submenu" ? "menu" : component;
    var trigger = Find(host.Root.RootElement, element => element.GetAttribute("data-motion-demo") == triggerName)
        ?? throw new InvalidOperationException($"The motion trigger for {component} is missing.");

    Directory.CreateDirectory(directory);
    clock.Advance(TimeSpan.FromMilliseconds(600));
    host.RunFrame();
    host.Root.Input.Click(trigger, trigger.GetBoundingClientRect().Center);
    if (component == "submenu")
    {
        host.RunFrame();
        clock.Advance(TimeSpan.FromMilliseconds(225));
        host.RunFrame();
        var submenu = Find(host.Root.RootElement, element => element.GetAttribute("data-slot") == "dropdown-menu-sub-trigger")
            ?? throw new InvalidOperationException("The submenu trigger is missing.");
        host.Root.Input.Click(submenu, submenu.GetBoundingClientRect().Center);
    }
    Capture("open", [0, 50, 125, 275, 525]);
    if (component == "sheet") CaptureSheetHover();
    if (component == "accordion") host.Root.Input.Click(trigger, trigger.GetBoundingClientRect().Center);
    else window.SendKey(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.Escape, KeyModifiers.None, false, 0, clock.Now));
    Capture("close", [0, 50, 125, 225, 325]);

    void Capture(string state, int[] times)
    {
        int previous = 0;
        foreach (int time in times)
        {
            clock.Advance(TimeSpan.FromMilliseconds(time - previous));
            host.RunFrame();
            Save(state, time);
            previous = time;
        }
    }

    void CaptureSheetHover()
    {
        Element? panel = null;
        foreach (var layer in host.Root.Overlay.Layers)
        foreach (var child in layer.Children)
            panel ??= Find(child, element => element.GetAttribute("data-slot") == "sheet-content");
        if (panel is null) throw new InvalidOperationException("The Sheet panel is missing after opening.");

        var settled = panel.GetBoundingClientRect();
        window.SendPointer(new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 1, settled.Center,
            PointerButtons.None, PointerButtons.None, KeyModifiers.None, clock.Now));
        int previous = 0;
        foreach (int time in new[] { 0, 16, 75 })
        {
            clock.Advance(TimeSpan.FromMilliseconds(time - previous));
            host.RunFrame();
            if (panel.GetBoundingClientRect() != settled || host.Root.Input.HoveredElement is not { } hovered || !panel.IsInclusiveAncestorOf(hovered))
                throw new InvalidOperationException("The Sheet shifted or lost the pointer after its entrance animation settled.");
            Save("hover", time);
            previous = time;
        }
        Console.WriteLine($"Sheet bounds stayed at {settled} after pointer entry.");
    }

    void Save(string state, int time)
    {
        string path = Path.Combine(directory, $"{component}-{state}-{time:D3}ms.png");
        PngCodec.Save(window.Framebuffer ?? throw new InvalidOperationException("No gallery frame was rendered."), path);
        Console.WriteLine($"Wrote {path}");
    }
}

static void BenchmarkRender(DisplayList frame, Size viewport, double scale)
{
    const int warmup = 5;
    const int iterations = 20;
    var size = PixelSize.FromSize(viewport, scale);
    var options = new RenderOptions(Color.White);
    using (var software = new SoftwareRenderBackend())
    using (var target = software.CreateOffscreenTarget(size, scale))
    {
        var samples = new List<double>(iterations);
        for (int i = 0; i < warmup + iterations; i++)
        {
            var watch = Stopwatch.StartNew();
            target.Render(frame, options);
            if (i >= warmup) samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"Software raster median/p95 {Percentile(samples, 0.5):F2}/{Percentile(samples, 0.95):F2} ms");
    }

    using var metal = MetalRenderer.TryCreate();
    if (metal is null)
    {
        Console.WriteLine("Metal unavailable.");
        return;
    }
    using var gpuTarget = metal.CreateOffscreenTarget(size, scale);
    var submit = new List<double>(iterations);
    var gpu = new List<double>(iterations);
    for (int i = 0; i < warmup + iterations; i++)
    {
        var watch = Stopwatch.StartNew();
        gpuTarget.Render(frame, options);
        double elapsed = watch.Elapsed.TotalMilliseconds;
        if (metal.IsSoftwareFallback || metal.LastFrameStats is not { } stats)
        {
            Console.WriteLine($"Metal benchmark ended before completion. {metal.FallbackReason}");
            return;
        }
        metal.WaitForGpu();
        if (i < warmup) continue;
        submit.Add(elapsed);
        if (stats.GpuTime is { } gpuTime) gpu.Add(gpuTime.TotalMilliseconds);
    }
    Console.WriteLine($"Metal submit median/p95 {Percentile(submit, 0.5):F2}/{Percentile(submit, 0.95):F2} ms");
    Console.WriteLine(gpu.Count == 0
        ? "Metal GPU execution timing unavailable."
        : $"Metal GPU execution median/p95 {Percentile(gpu, 0.5):F2}/{Percentile(gpu, 0.95):F2} ms");
}

static double Percentile(List<double> samples, double percentile)
{
    samples.Sort();
    int index = (int)Math.Ceiling(percentile * samples.Count) - 1;
    return samples[Math.Clamp(index, 0, samples.Count - 1)];
}

bool Has(string flag) => Array.IndexOf(args, flag) >= 0;
string? Option(string flag)
{
    int index = Array.IndexOf(args, flag);
    if (index < 0) return null;
    if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"{flag} requires a value.");
    return args[index + 1];
}

int Dimension(string flag, int fallback) => Option(flag) is not { } text ? fallback
    : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int size) && size is >= 1 and <= 4096 ? size
    : throw new ArgumentException($"{flag} requires an integer between 1 and 4096.");

double Scale() => Option("--scale") is not { } text ? 1
    : double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double scale) && scale is >= 0.5 and <= 3
    ? scale : throw new ArgumentException("--scale requires a number between 0.5 and 3.");

static Element? FindById(Element root, string id) => Find(root, element => element.Id == id);

static Element? Find(Element root, Func<Element, bool> matches)
{
    var stack = new Stack<Element>();
    stack.Push(root);
    while (stack.Count > 0)
    {
        var element = stack.Pop();
        if (matches(element)) return element;
        foreach (var child in element.Children) stack.Push(child);
    }
    return null;
}
