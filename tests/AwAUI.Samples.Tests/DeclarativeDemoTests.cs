using System;
using System.Collections.Generic;
using System.IO;
using AwAUI.Headless;
using AwAUI.Imaging;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Shadcn;
using AwAUI.Text;
using AwAUI.UI;

namespace AwAUI.Samples.Tests;

/// <summary>
/// End-to-end smoke tests for samples/DeclarativeDemo: every page id mounts in the headless platform,
/// renders a non-empty frame, and reports no Markup or runtime diagnostics.
/// </summary>
public class DeclarativeDemoTests
{
    static DeclarativeDemoTests()
    {
        // The sample depends on the bundled shadcn New York theme and its Tailwind utilities.
        ShadcnTheme.Register();
    }

    static TextEngine CreateTextEngine()
    {
        var fonts = new FontCollection();
        string root = FindRepoRoot();
        fonts.RegisterFile(Path.Combine(root, "tests", "TestFiles", "Fonts", "NotoSans-Regular.ttf"));
        foreach (string generic in new[] { "sans-serif", "system-ui", "ui-sans-serif" })
            fonts.SetGenericFamily(generic, "Noto Sans");
        return new DefaultTextEngine(fonts);
    }

    static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "samples", "DeclarativeDemo")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("The repository root was not found above " + AppContext.BaseDirectory);
    }

    [Theory]
    [InlineData("dashboard")]
    [InlineData("projects")]
    [InlineData("project-dialog")]
    [InlineData("schedule")]
    [InlineData("settings")]
    public void Every_page_renders_without_diagnostics(string page)
    {
        var platform = new HeadlessPlatform { DefaultScale = 1 };
        var window = platform.CreateWindow(new WindowOptions { Size = new Size(1280, 900) });
        using var host = new UIHost(window, new SoftwareRenderBackend(), CreateTextEngine(), platform.Dispatcher);

        var diagnostics = new List<UIDiagnostic>();
        host.Root.Diagnostic += diagnostics.Add;
        var clock = new ManualUIClock();
        host.Root.Clock = clock;

        window.Show();
        host.Mount(new DeclarativeDemo.App { Page = page });

        // Mount effects, the theme attribute and the dialog/toast entrance animations settle over these frames.
        for (int i = 0; i < 24; i++)
        {
            clock.Advance(platform.FrameInterval);
            platform.RunFrame();
        }

        var framebuffer = ((HeadlessWindow)window).Framebuffer;
        Assert.NotNull(framebuffer);
        Assert.True(framebuffer!.Width > 0 && framebuffer.Height > 0, $"{page}: the framebuffer is empty.");
        Assert.True(PngCodec.Encode(framebuffer).Length > 4096, $"{page}: the rendered frame looks blank.");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
    }
}
