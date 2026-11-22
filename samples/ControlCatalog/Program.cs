using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using AwAUI;
using AwAUI.Headless;
using AwAUI.Imaging;
using AwAUI.MacOS;
using AwAUI.Platform;
using AwAUI.Rhi.Metal;
using AwAUI.Text;
using AwAUI.Themes.Fluent;
using AwAUI.UI;
using ControlCatalog;

string? output = Option("--headless");
bool compact = Array.IndexOf(args, "--compact") >= 0;
bool listEnd = Array.IndexOf(args, "--list-end") >= 0;
string? overlayPreview = Option("--overlay");
if (overlayPreview is not null && (output is null || overlayPreview is not ("menu" or "popup" or "tooltip")))
    throw new ArgumentException("Use --overlay menu|popup|tooltip with --headless.");
var variant = Option("--theme") switch
{
    "dark" => FluentThemeVariant.Dark,
    "high-contrast" => FluentThemeVariant.HighContrast,
    "light" => FluentThemeVariant.Light,
    null or "system" => FluentThemeVariant.System,
    var value => throw new ArgumentException($"Unknown theme: {value}. Use system, light, dark, or high-contrast."),
};

var fonts = new FontCollection { SystemFonts = SystemFonts.Default };
fonts.RegisterFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSans-Regular.ttf"));
fonts.SetGenericFamily("sans-serif", "Noto Sans");
fonts.SetGenericFamily("system-ui", "Noto Sans");

PlatformBackend platform = output is not null ? new HeadlessPlatform()
    : OperatingSystem.IsMacOS() ? new MacPlatform()
    : OperatingSystem.IsWindows() ? new AwAUI.Win32.Win32Platform()
    : OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ? new AwAUI.X11.X11Platform()
    : new HeadlessPlatform();
try
{
    using var renderer = MetalRenderer.Create(preferGpu: platform is MacPlatform);
    renderer.Degraded += Console.Error.WriteLine;
    var window = platform.CreateWindow(new WindowOptions { Title = "AwAUI Control Catalog", Size = new Size(Dimension("--width", 980), Dimension("--height", overlayPreview is null ? 720 : 2520)) });
    using var host = new UIHost(window, renderer, new DefaultTextEngine(fonts), platform.Dispatcher);
    var previewClock = overlayPreview is null ? null : new ManualUIClock();
    if (previewClock is not null) host.Root.Clock = previewClock;
    host.Root.Diagnostic += diagnostic => Console.Error.WriteLine(diagnostic);
    host.UseFluentTheme();
    FluentTheme.SetVariant(host.Root, variant);
    FluentTheme.SetCompact(host.Root, compact);
    host.Closed += _ => platform.Exit();
    window.Show();
    var catalog = new Catalog { InitialTheme = variant, InitialCompact = compact };
    host.Mount(catalog);

    if (platform is HeadlessPlatform headless && window is HeadlessWindow preview)
    {
        headless.RunFrame();
        if (listEnd)
        {
            catalog.ActivityScroll.JumpTo(catalog.ActivityScroll.MaxOffset);
            headless.RunFrame();
            var rows = catalog.ActivityScroll.Element!.ElementChildren[0].ElementChildren;
            if (rows.Count == 0 || rows[rows.Count - 1].GetAttribute("aria-posinset") != "10000")
                throw new InvalidOperationException("The final activity row is missing after the scroll jump.");
            Console.WriteLine($"Realized {rows.Count} of 10,000 activity rows after one frame.");
        }
        if (overlayPreview is { } overlay && previewClock is not null)
        {
            var trigger = overlay == "menu"
                ? FindByClass(FindById(host.Root.RootElement, "catalog-menu-root") ?? host.Root.RootElement, "aw-menu-flyout-trigger")
                : FindById(host.Root.RootElement, $"catalog-{overlay}-trigger");
            if (trigger is null) throw new InvalidOperationException($"The {overlay} preview target is missing.");
            var point = trigger.GetBoundingClientRect().Center;
            if (overlay == "tooltip") Send(RawPointerEventType.Move, PointerButtons.None, PointerButtons.None);
            else
            {
                Send(RawPointerEventType.Down, PointerButtons.Left, PointerButtons.Left);
                Send(RawPointerEventType.Up, PointerButtons.Left, PointerButtons.None);
            }
            host.RunFrame();
            previewClock.Advance(TimeSpan.FromMilliseconds(700));
            host.RunFrame();

            void Send(RawPointerEventType type, PointerButtons changed, PointerButtons buttons) =>
                preview.SendPointer(new RawPointerEvent(type, PointerType.Mouse, 1, point,
                    changed, buttons, KeyModifiers.None, previewClock.Now));
        }
        string path = output ?? "control-catalog.png";
        PngCodec.Save(preview.Framebuffer ?? throw new InvalidOperationException("The catalog rendered no frame."), path);
        Console.WriteLine($"Wrote {path}");
    }
    else platform.Run();
}
finally
{
    (platform as IDisposable)?.Dispose();
}

static Element? FindById(Element root, string id)
{
    var pending = new Stack<Element>();
    pending.Push(root);
    while (pending.Count > 0)
    {
        var element = pending.Pop();
        if (element.Id == id) return element;
        foreach (var child in element.ElementChildren) pending.Push(child);
    }
    return null;
}

static Element? FindByClass(Element root, string className)
{
    var pending = new Stack<Element>();
    pending.Push(root);
    while (pending.Count > 0)
    {
        var element = pending.Pop();
        if (element.HasClass(className)) return element;
        foreach (var child in element.ElementChildren) pending.Push(child);
    }
    return null;
}

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    if (i < 0) return null;
    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        throw new ArgumentException($"{name} requires a value.");
    return args[i + 1];
}

int Dimension(string name, int fallback) => Option(name) is not { } value ? fallback
    : int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int size) && size is >= 240 and <= 4096
        ? size : throw new ArgumentException($"{name} must be an integer from 240 to 4096.");
