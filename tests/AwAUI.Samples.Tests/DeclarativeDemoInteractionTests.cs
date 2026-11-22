using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwAUI.Headless;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Shadcn;
using AwAUI.Text;
using AwAUI.UI;

namespace AwAUI.Samples.Tests;

/// <summary>
/// Behavioural tests for samples/DeclarativeDemo. Each case mounts the real application in the headless
/// platform, clicks and types through the same input path a window uses, and then asserts on the rendered
/// element tree: filtered/paged table rows, dialog validation and creation, toasts raised into the Toaster,
/// and root theme/switch state. A <see cref="ManualUIClock"/> keeps animations and toast timers deterministic.
/// </summary>
public class DeclarativeDemoInteractionTests
{
    static DeclarativeDemoInteractionTests()
    {
        // The sample depends on the bundled shadcn New York theme and its Tailwind utilities.
        ShadcnTheme.Register();
    }

    // ------------------------------------------------------------ Projects: search

    [Fact]
    public void Projects_search_filters_rows_and_reports_the_empty_state()
    {
        using var app = new DeclarativeHarness("projects");
        Assert.Equal(8, app.RowCount());
        Assert.Contains("Page 1 of 3", app.TableStatusText);

        app.Click(app.ByAttribute("aria-label", "Search projects"));
        app.Type("Atlas");
        app.Settle(4);

        Assert.Equal(1, app.RowCount());
        Assert.True(app.HasText("Atlas design system"));
        Assert.False(app.HasText("Northwind refresh"));
        Assert.Contains("Page 1 of 1", app.TableStatusText);

        // The controlled input keeps focus, so more text continues the same query.
        app.Type("zzz");
        app.Settle(4);

        Assert.Equal(0, app.RowCount());
        Assert.True(app.HasText("No results."));
        Assert.Contains("No results", app.TableStatusText);
        app.AssertNoErrors();
    }

    // ------------------------------------------------------------ Projects: paging

    [Fact]
    public void Projects_pager_and_page_size_change_the_visible_rows()
    {
        using var app = new DeclarativeHarness("projects");
        Assert.Contains("Page 1 of 3", app.TableStatusText);
        Assert.True(app.HasText("Atlas design system"));

        app.Click(app.ByAttribute("aria-label", "Go to next page"));
        app.Settle(4);

        Assert.Contains("Page 2 of 3", app.TableStatusText);
        Assert.False(app.HasText("Atlas design system"));
        Assert.True(app.HasText("Partner API v2"));

        // Raising the page size to 24 collapses the 18 rows onto a single page again.
        app.Click(app.ByAttribute("aria-label", "Show 24 rows"));
        app.Settle(4);

        Assert.Contains("Page 1 of 1", app.TableStatusText);
        Assert.Equal(18, app.RowCount());
        Assert.True(app.HasText("Atlas design system"));
        app.AssertNoErrors();
    }

    // ------------------------------------------------------------ Projects: row actions

    [Fact]
    public void Projects_delete_row_action_removes_the_row_and_raises_a_toast()
    {
        using var app = new DeclarativeHarness("projects");
        Assert.True(app.HasText("Atlas design system"));
        Assert.Equal(8, app.RowCount());

        app.Click(app.ByAttribute("aria-label", "Actions for Atlas design system"));
        app.Settle(24);

        var delete = app.Find(e => e.Role == Role.MenuItem && e.TextContent == "Delete");
        app.Click(delete);
        app.Settle(24);

        Assert.False(app.HasText("Atlas design system"));
        Assert.Empty(app.All(e => e.GetAttribute("data-row-key") == "p-01"));
        // The page stays full: p-09 moves up onto the first page.
        Assert.Equal(8, app.RowCount());
        Assert.True(app.HasText("Partner API v2"));
        Assert.Contains("Project deleted", app.ToastTitles);
        app.AssertNoErrors();
    }

    // ------------------------------------------------------------ Project dialog: validation

    [Fact]
    public void ProjectDialog_empty_submit_shows_inline_errors_and_a_warning_toast()
    {
        using var app = new DeclarativeHarness("projects");
        Assert.Null(app.TryDialogContent());

        app.Click(app.Button("New project"));
        app.Settle(24);
        Assert.NotNull(app.TryDialogContent());
        Assert.True(app.HasText("New project"));

        app.Click(app.Button("Create project"));
        app.Settle(24);

        // FormMessage renders the first failing validator; the dialog stays open.
        Assert.True(app.HasText("Give the project a name."));
        Assert.Contains("Check the form", app.ToastTitles);
        Assert.Contains("One field needs attention.", app.ToastDescriptions);
        Assert.NotNull(app.TryDialogContent());
        app.AssertNoErrors();
    }

    // ------------------------------------------------------------ Project dialog: create

    [Fact]
    public void ProjectDialog_valid_submit_adds_the_project_closes_the_dialog_and_toasts()
    {
        using var app = new DeclarativeHarness("projects");
        app.Click(app.Button("New project"));
        app.Settle(24);

        var name = app.Find(e => e.GetAttribute("placeholder") == "e.g. Atlas design system");
        app.Click(name);
        app.Type("Website Redesign");
        app.Settle(4);

        // The FormControl writes the typed value back through the controller.
        Assert.Equal("Website Redesign",
            app.Find(e => e.GetAttribute("placeholder") == "e.g. Atlas design system").GetAttribute("value"));

        app.Click(app.Button("Create project"));
        app.Settle(24);

        Assert.Null(app.TryDialogContent());
        Assert.True(app.HasText("Website Redesign"));
        Assert.Contains("Project created", app.ToastTitles);
        Assert.Contains(app.TableRowNames(), row => row == "Website Redesign");
        app.AssertNoErrors();
    }

    // ------------------------------------------------------------ Schedule: toasts

    [Fact]
    public void Schedule_share_raises_a_toast_that_the_clock_auto_dismisses()
    {
        using var app = new DeclarativeHarness("schedule");
        Assert.Empty(app.ToastTitles);

        app.Click(app.Button("Share with team"));
        app.Settle(4);

        var toast = app.Find(e => e.GetAttribute("data-slot") == "sonner-toast");
        Assert.Equal("success", toast.GetAttribute("data-variant"));
        Assert.Contains("Range shared", app.ToastTitles);

        // The default toast duration is 4s; advancing the manual clock runs the dismissal timer.
        app.Advance(4500);
        app.Settle(4);

        Assert.Empty(app.ToastTitles);
        Assert.Null(app.TryFind(e => e.GetAttribute("data-slot") == "sonner-toast"));
        app.AssertNoErrors();
    }

    // ------------------------------------------------------------ Settings: theme and switches

    [Fact]
    public void Settings_theme_toggle_updates_the_root_attribute_and_a_notification_switch_flips()
    {
        using var app = new DeclarativeHarness("settings");
        Assert.Null(app.Host.Root.RootElement.GetAttribute("data-aw-theme"));

        app.Click(app.ByAttribute("aria-label", "Toggle theme"));
        app.Settle(4);
        Assert.Equal("dark", app.Host.Root.RootElement.GetAttribute("data-aw-theme"));

        app.Click(app.ByAttribute("aria-label", "Toggle theme"));
        app.Settle(4);
        Assert.Null(app.Host.Root.RootElement.GetAttribute("data-aw-theme"));

        app.Click(app.Find(e => e.Role == Role.Tab && e.TextContent == "Notifications"));
        app.Settle(24);

        var digest = app.ById("weekly-digest");
        Assert.Equal("true", digest.GetAttribute("aria-checked"));
        Assert.Equal("checked", digest.GetAttribute("data-state"));

        app.Click(digest);
        app.Settle(4);

        var updated = app.ById("weekly-digest");
        Assert.Equal("false", updated.GetAttribute("aria-checked"));
        Assert.Equal("unchecked", updated.GetAttribute("data-state"));
        app.AssertNoErrors();
    }
}

/// <summary>
/// Headless mount plus pointer/keyboard helpers for one DeclarativeDemo instance. Frames are driven through
/// <see cref="HeadlessPlatform.RunFrame"/> and time through a <see cref="ManualUIClock"/> so entrance
/// animations and toast timers advance deterministically.
/// </summary>
sealed class DeclarativeHarness : IDisposable
{
    readonly List<UIDiagnostic> _diagnostics = new();
    TimeSpan _time;

    public DeclarativeHarness(string page, bool dark = false, float width = 1280, float height = 1000)
    {
        Platform = new HeadlessPlatform { DefaultScale = 1 };
        Window = (HeadlessWindow)Platform.CreateWindow(new WindowOptions { Size = new Size(width, height) });
        Host = new UIHost(Window, new SoftwareRenderBackend(), CreateTextEngine(), Platform.Dispatcher);
        Clock = new ManualUIClock();
        Host.Root.Clock = Clock;
        Host.Root.Diagnostic += _diagnostics.Add;

        Window.Show();
        Host.Mount(new DeclarativeDemo.App { Page = page, Dark = dark });
        Settle(24);
    }

    public HeadlessPlatform Platform { get; }
    public HeadlessWindow Window { get; }
    public UIHost Host { get; }
    public ManualUIClock Clock { get; }

    public Element Root => Host.Root.RootElement;

    // ------------------------------------------------------------ element queries

    public List<Element> All(Func<Element, bool> predicate)
    {
        var result = new List<Element>();
        var stack = new Stack<Element>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var element = stack.Pop();
            if (predicate(element)) result.Add(element);
            for (int i = element.Children.Count - 1; i >= 0; i--) stack.Push(element.Children[i]);
        }
        return result;
    }

    public Element Find(Func<Element, bool> predicate) => All(predicate).Single();

    public Element? TryFind(Func<Element, bool> predicate) => All(predicate).FirstOrDefault();

    public Element ById(string id) => Find(e => e.Id == id);

    public Element ByAttribute(string name, string value) => Find(e => e.GetAttribute(name) == value);

    /// <summary>A button whose visible text is exactly <paramref name="text"/> (icons contribute no text).</summary>
    public Element Button(string text) => Find(e => e.Tag == "button" && e.TextContent == text);

    public bool HasText(string text) => All(e => e.TextContent == text).Count > 0;

    public Element? TryDialogContent() => TryFind(e => e.GetAttribute("data-slot") == "dialog-content");

    public int RowCount() => All(e => e.Tag == "tr" && e.GetAttribute("data-row-key") is not null).Count;

    public List<string> TableRowNames() => All(e => e.HasClass("project-name")).Select(e => e.TextContent).ToList();

    public string TableStatusText => Find(e => e.GetAttribute("data-slot") == "data-table-status").TextContent;

    public List<string> ToastTitles =>
        All(e => e.GetAttribute("data-title") is not null).Select(e => e.TextContent).ToList();

    public List<string> ToastDescriptions =>
        All(e => e.GetAttribute("data-description") is not null).Select(e => e.TextContent).ToList();

    // ------------------------------------------------------------ frames, clock and input

    public void Settle(int frames = 1)
    {
        for (int i = 0; i < frames; i++)
        {
            Clock.Advance(Platform.FrameInterval);
            Platform.RunFrame();
        }
    }

    /// <summary>Advance the deterministic clock so timers (toast auto-dismiss) fire, then render the result.</summary>
    public void Advance(int milliseconds)
    {
        Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
        Settle(2);
    }

    void Send(RawPointerEventType type, Point position, PointerButtons changed, PointerButtons buttons)
    {
        _time += TimeSpan.FromMilliseconds(16);
        Window.SendPointer(new RawPointerEvent(type, PointerType.Mouse, 1, position, changed, buttons,
            KeyModifiers.None, _time));
    }

    public void ClickAt(Point position)
    {
        Send(RawPointerEventType.Move, position, PointerButtons.None, PointerButtons.None);
        Send(RawPointerEventType.Down, position, PointerButtons.Left, PointerButtons.Left);
        Send(RawPointerEventType.Up, position, PointerButtons.Left, PointerButtons.None);
        Settle(2);
    }

    public void Click(Element element) => ClickAt(element.GetBoundingClientRect().Center);

    /// <summary>Send text to the focused input through the same path a platform uses.</summary>
    public void Type(string text)
    {
        Window.SendText(text);
        Settle(2);
    }

    public void AssertNoErrors() =>
        Assert.DoesNotContain(_diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    public void Dispose() => Host.Dispose();

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
}
