using AwAUI.Css;
using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text.Testing;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 无头窗口对第十二批窗口 API 的实现：选项应用与快照、位置 / 状态 / 激活回调、
/// 重绘记录、安全区 → CSS <c>env()</c> 重算，以及表面丢失 / 恢复使设备资源失效。
/// </summary>
public class HeadlessWindowTests
{
    sealed class RecordingEvents : IWindowEvents, IWindowStateEvents, IWindowLifecycleEvents
    {
        public readonly List<WindowState> States = new();
        public readonly List<Point> Positions = new();
        public readonly List<bool> Activated = new();
        public readonly List<AppLifecycle> Lifecycles = new();
        public readonly List<Thickness> SafeAreas = new();
        public int Frames;
        public int SurfaceLost;
        public int SurfaceRestored;

        public void OnFrame(TimeSpan timestamp) => Frames++;
        public void OnResized(Size clientSize, double scale) { }
        public void OnPointer(in RawPointerEvent e) { }
        public void OnKey(in RawKeyEvent e) { }
        public void OnTextInput(string text) { }
        public void OnFocusChanged(bool focused) { }
        public bool OnCloseRequested() => true;
        public void OnClosed() { }

        public void OnWindowStateChanged(WindowState state) => States.Add(state);
        public void OnPositionChanged(Point position) => Positions.Add(position);

        public void OnSurfaceLost() => SurfaceLost++;
        public void OnSurfaceRestored() => SurfaceRestored++;
        public void OnLifecycle(AppLifecycle state) => Lifecycles.Add(state);
        public void OnActivated(bool activated) => Activated.Add(activated);
        public void OnSafeAreaChanged(Thickness insets) => SafeAreas.Add(insets);
    }

    sealed class RecordingBackend : RenderBackend
    {
        readonly SoftwareRenderBackend _inner = new();

        public int Invalidations { get; private set; }

        public override string Name => "recording";
        public override BackendCapabilities Capabilities => _inner.Capabilities;
        public override RenderTarget CreateRenderTarget(NativeSurface surface) => _inner.CreateRenderTarget(surface);
        public override RenderTarget CreateOffscreenTarget(PixelSize size, double scale) => _inner.CreateOffscreenTarget(size, scale);
        public override void InvalidateDeviceResources() => Invalidations++;
        public override void Dispose() => _inner.Dispose();
    }

    [Fact]
    public void Window_options_are_applied_and_snapshotted()
    {
        var platform = new HeadlessPlatform();
        using var icon = new Bitmap(new PixelSize(2, 2));
        var options = new WindowOptions
        {
            Title = "Options",
            Size = new Size(100, 80),
            Position = new Point(12, 34),
            StartupLocation = WindowStartupLocation.Manual,
            State = WindowState.Maximized,
            Topmost = true,
            MinWidth = 50,
            MinHeight = 40,
            MaxWidth = 300,
            MaxHeight = 200,
            Icon = icon,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.Width,
            Decorations = WindowDecorations.None,
        };

        var window = (HeadlessWindow)platform.CreateWindow(options);

        Assert.Equal("Options", window.Options.Title);
        Assert.Equal(new Size(100, 80), window.Options.Size);
        Assert.Equal(new Point(12, 34), window.Position);
        Assert.Equal(WindowStartupLocation.Manual, window.StartupLocation);
        Assert.Equal(WindowState.Maximized, window.WindowState);
        Assert.True(window.IsMaximized);
        Assert.True(window.Topmost);
        Assert.Equal(50d, window.MinWidth!.Value);
        Assert.Equal(40d, window.MinHeight!.Value);
        Assert.Equal(300d, window.MaxWidth!.Value);
        Assert.Equal(200d, window.MaxHeight!.Value);
        Assert.Same(icon, window.Icon);
        Assert.False(window.ShowInTaskbar);
        Assert.Equal(SizeToContent.Width, window.Options.SizeToContent);
        Assert.Equal(WindowDecorations.None, window.Decorations);

        // 选项是创建时的快照：之后修改调用方的对象不影响窗口。
        options.Topmost = false;
        options.Size = new Size(999, 999);
        options.MinWidth = null;
        Assert.True(window.Options.Topmost);
        Assert.Equal(new Size(100, 80), window.Options.Size);
        Assert.Equal(50d, window.MinWidth!.Value);
    }

    [Fact]
    public void Startup_location_uses_position_or_centers_in_primary_working_area()
    {
        var platform = new HeadlessPlatform();

        var manual = (HeadlessWindow)platform.CreateWindow(new WindowOptions
        {
            Position = new Point(40, 50),
            StartupLocation = WindowStartupLocation.Manual,
        });
        Assert.Equal(new Point(40, 50), manual.Position);

        var unpositioned = (HeadlessWindow)platform.CreateWindow(new WindowOptions
        {
            Size = new Size(200, 100),
            StartupLocation = WindowStartupLocation.Manual,
        });
        Assert.Equal(Point.Zero, unpositioned.Position);

        // 默认显示器 1920×1080，窗口 800×600 -> (560, 240)。
        var centered = (HeadlessWindow)platform.CreateWindow(new WindowOptions
        {
            Size = new Size(800, 600),
            StartupLocation = WindowStartupLocation.CenterScreen,
        });
        Assert.Equal(new Point(560, 240), centered.Position);

        // CenterOwner 没有所有者，与 CenterScreen 相同；工作区移动后重新居中。
        platform.HeadlessScreens.SetScreens(
            new Screen("Primary", new Rect(100, 50, 1600, 900), new Rect(120, 70, 1600, 900), 1, isPrimary: true));
        var ownerCentered = (HeadlessWindow)platform.CreateWindow(new WindowOptions
        {
            Size = new Size(800, 600),
            StartupLocation = WindowStartupLocation.CenterOwner,
        });
        Assert.Equal(new Point(520, 220), ownerCentered.Position);
    }

    [Fact]
    public void Size_constraints_are_enforced_on_creation_and_resize()
    {
        var platform = new HeadlessPlatform();
        var window = (HeadlessWindow)platform.CreateWindow(new WindowOptions
        {
            Size = new Size(100, 100),
            MinWidth = 200,
            MinHeight = 120,
            MaxWidth = 400,
            MaxHeight = 150,
        });

        Assert.Equal(new Size(200, 120), window.ClientSize);

        window.Resize(new Size(1000, 1000), 2);
        Assert.Equal(new Size(400, 150), window.ClientSize);
        Assert.Equal(2d, window.RenderScale);

        window.Resize(new Size(10, 10), 1);
        Assert.Equal(new Size(200, 120), window.ClientSize);
    }

    [Fact]
    public void State_and_position_callbacks_update_window_and_notify_events()
    {
        var platform = new HeadlessPlatform();
        var window = (HeadlessWindow)platform.CreateWindow(new WindowOptions());
        var events = new RecordingEvents();
        window.Events = events;

        window.SendStateChanged(WindowState.Minimized);
        Assert.Equal(WindowState.Minimized, window.WindowState);
        Assert.True(window.IsMinimized);
        Assert.Equal(WindowState.Minimized, Assert.Single(events.States));

        window.SendPositionChanged(new Point(7, 8));
        Assert.Equal(new Point(7, 8), window.Position);
        Assert.Equal(new Point(7, 8), Assert.Single(events.Positions));

        window.SendActivated(false);
        Assert.False(window.IsActive);
        Assert.False(Assert.Single(events.Activated));

        window.Activate();
        Assert.True(window.IsActive);
        Assert.Single(events.Activated); // Activate 是程序请求，不回调平台事件

        window.SendActivated(true);
        Assert.True(window.IsActive);
        Assert.Equal(new[] { false, true }, events.Activated);

        window.SendLifecycle(AppLifecycle.Background);
        Assert.Equal(AppLifecycle.Background, Assert.Single(events.Lifecycles));

        window.SetWindowState(WindowState.Normal);
        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.False(window.IsMinimized);
    }

    [Fact]
    public void Invalidate_records_region_and_requests_a_frame()
    {
        var platform = new HeadlessPlatform();
        var window = (HeadlessWindow)platform.CreateWindow(new WindowOptions { Size = new Size(80, 60) });

        window.Invalidate(new Rect(1, 2, 30, 40));
        Assert.Equal(1, window.InvalidateCount);
        Assert.Equal(new Rect(1, 2, 30, 40), window.LastInvalidatedRect);
        Assert.False(window.LastInvalidationWasWholeWindow);

        window.Invalidate(default);
        Assert.Equal(2, window.InvalidateCount);
        Assert.True(window.LastInvalidationWasWholeWindow);

        // 平台的 Invalidate 请求下一帧；headless 的帧时钟应把它送出去。
        var events = new RecordingEvents();
        window.Events = events;
        window.Invalidate(default);
        platform.RunFrame();
        Assert.Equal(1, events.Frames);
    }

    [Fact]
    public void Safe_area_change_recomputes_css_environment_and_layout()
    {
        var platform = new HeadlessPlatform();
        using var backend = new RecordingBackend();
        var host = AwApp.CreateHost(platform, new WindowOptions { Size = new Size(200, 200) }, backend, new TestTextEngine(),
            new Div { Id = "probe", Children = { "x" } },
            StyleSheet.Parse("#probe { padding-top: env(safe-area-inset-top); padding-bottom: env(safe-area-inset-bottom); }"));
        var window = (HeadlessWindow)host.Window;
        platform.RunFrame();

        var probe = host.Root.RootElement.ById("probe");
        Assert.Equal(0f, probe.ComputedStyle!.PaddingTop.Resolve(0));

        Thickness reported = default;
        host.SafeAreaChanged += insets => reported = insets;

        window.SendSafeArea(new Thickness(left: 0, top: 24, right: 0, bottom: 12));

        Assert.Equal(new Thickness(0, 24, 0, 12), reported);
        Assert.Equal(new Thickness(0, 24, 0, 12), window.SafeAreaInsets);
        Assert.Equal(24d, host.Pipeline.StyleEngine.Environment.SafeAreaInsetTop);
        Assert.Equal(12d, host.Pipeline.StyleEngine.Environment.SafeAreaInsetBottom);

        platform.RunFrame();
        Assert.Equal(24f, probe.ComputedStyle!.PaddingTop.Resolve(0));
        Assert.Equal(12f, probe.ComputedStyle!.PaddingBottom.Resolve(0));
    }

    [Fact]
    public void Surface_lost_and_restored_invalidate_device_resources()
    {
        var platform = new HeadlessPlatform();
        using var backend = new RecordingBackend();
        var host = AwApp.CreateHost(platform, new WindowOptions { Size = new Size(60, 40) }, backend, new TestTextEngine(),
            new Div { Children = { "x" } });
        var window = (HeadlessWindow)host.Window;
        platform.RunFrame();

        int before = backend.Invalidations;

        window.SendSurfaceLost();
        Assert.Equal(before + 1, backend.Invalidations);

        window.SendSurfaceRestored();
        Assert.Equal(before + 2, backend.Invalidations);

        // 恢复后请求一帧并重新呈现，不抛异常。
        platform.RunFrame();
        Assert.True(host.LastFrame.TotalMs >= 0);
    }
}
