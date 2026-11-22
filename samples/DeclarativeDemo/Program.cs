using System;
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
using DeclarativeDemo;

// Usage:
//   DeclarativeDemo                                     open a native window (macOS / Windows / X11)
//   DeclarativeDemo --headless out.png [--page id]      render one page to PNG without a display
//   DeclarativeDemo --headless out.png --dark            dark theme
//   DeclarativeDemo --headless out.png --width 1440 --height 960 --scale 2
// Page ids: dashboard, projects, project-dialog, schedule, settings.
// The sample pins the culture so headless previews are deterministic across machines.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

string? output = Option("--headless");
string? page = Option("--page");
bool dark = Has("--dark");
int width = Dimension("--width", 1280);
int height = Dimension("--height", 860);
double scale = Scale();
bool software = Has("--software");

var fonts = new FontCollection { SystemFonts = SystemFonts.Default };
fonts.RegisterFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "NotoSans-Regular.ttf"));
foreach (string family in new[] { "sans-serif", "system-ui", "ui-sans-serif" }) fonts.SetGenericFamily(family, "Noto Sans");

ShadcnTheme.Register();

PlatformBackend platform = output is not null ? new HeadlessPlatform { DefaultScale = scale }
    : OperatingSystem.IsMacOS() ? new MacPlatform()
    : OperatingSystem.IsWindows() ? new AwAUI.Win32.Win32Platform()
    : OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } ? new AwAUI.X11.X11Platform()
    : new HeadlessPlatform { DefaultScale = scale };

try
{
    using var backend = MetalRenderer.Create(preferGpu: platform is MacPlatform && !software);
    backend.Degraded += message => Console.Error.WriteLine("[degraded] " + message);

    var window = platform.CreateWindow(new WindowOptions { Title = "AwAUI · Declarative demo", Size = new Size(width, height) });
    using var host = new UIHost(window, backend, new DefaultTextEngine(fonts), platform.Dispatcher);

    int errorCount = 0;
    host.Root.Diagnostic += diagnostic =>
    {
        Console.Error.WriteLine(diagnostic);
        if (diagnostic.Severity == DiagnosticSeverity.Error) errorCount++;
    };

    var clock = platform is HeadlessPlatform ? new ManualUIClock() : null;
    if (clock is not null) host.Root.Clock = clock;

    window.Show();
    host.Mount(new App { Dark = dark, Page = page });
    host.Closed += _ => platform.Exit();
    Console.WriteLine(FormattableString.Invariant($"{platform.Name} / {backend.Name} @ {host.Window.RenderScale:g}x"));

    if (platform is HeadlessPlatform headless && window is HeadlessWindow headlessWindow)
    {
        // A handful of frames lets mount effects, theme attributes and entrance animations settle.
        for (int i = 0; i < 24; i++)
        {
            clock!.Advance(headless.FrameInterval);
            headless.RunFrame();
        }

        var framebuffer = headlessWindow.Framebuffer
            ?? throw new InvalidOperationException("No frame was rendered.");
        string path = output ?? "declarative.png";
        PngCodec.Save(framebuffer, path);
        Console.WriteLine($"Wrote {path} ({framebuffer.Width}x{framebuffer.Height} px)");
        if (errorCount > 0)
        {
            Console.Error.WriteLine($"{errorCount} error diagnostic(s) were reported.");
            Environment.ExitCode = 1;
        }
        return;
    }

    platform.Run();
}
finally
{
    (platform as IDisposable)?.Dispose();
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
    : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int size) && size is >= 1 and <= 4096
        ? size
        : throw new ArgumentException($"{flag} requires an integer between 1 and 4096.");

double Scale() => Option("--scale") is not { } text ? 1
    : double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) && value is >= 0.5 and <= 3
        ? value
        : throw new ArgumentException("--scale requires a number between 0.5 and 3.");
