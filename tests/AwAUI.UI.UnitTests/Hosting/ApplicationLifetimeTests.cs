using AwAUI.Graphics;
using AwAUI.Headless;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text.Testing;

namespace AwAUI.UI.UnitTests;

/// <summary>
/// 应用与窗口托管（design/01 规则 5）：Application 的生命周期、窗口登记与退出策略、关闭取消、
/// 以及自绘标题栏经 IWindowChrome 转发到平台窗口。
/// </summary>
public partial class ApplicationLifetimeTests
{
    sealed partial class Blank : Component
    {
        protected override Node Build() => new Div();
    }

    static DesktopApplicationLifetime NewLifetime(out HeadlessPlatform platform)
    {
        platform = new HeadlessPlatform();
        return new DesktopApplicationLifetime(platform, new SoftwareRenderBackend(), new TestTextEngine());
    }

    static Window NewWindow(string title = "window") =>
        new(new WindowOptions { Title = title, Size = new AwAUI.Size(160, 100) }) { Content = new Blank() };

    [Fact]
    public void Closing_main_window_runs_start_and_leaves_no_windows()
    {
        var lifetime = NewLifetime(out _);
        var app = new Application { Lifetime = lifetime };
        Application.SetCurrent(app);
        try
        {
            var window = NewWindow();
            lifetime.MainWindow = window;
            Window? opened = null;
            window.Opened += w =>
            {
                opened = w;
                w.Close();
            };

            int code = lifetime.Start();

            Assert.Equal(0, code);
            Assert.Same(window, opened);
            Assert.True(window.IsClosed);
            Assert.Empty(lifetime.Windows);
        }
        finally
        {
            Application.ClearCurrent(app);
        }
    }

    [Fact]
    public void Main_window_close_shuts_down_other_windows()
    {
        var lifetime = NewLifetime(out _);
        lifetime.ShutdownMode = ShutdownMode.OnMainWindowClose;
        var app = new Application { Lifetime = lifetime };
        Application.SetCurrent(app);
        try
        {
            var main = NewWindow("main");
            var secondary = NewWindow("secondary");
            lifetime.MainWindow = main;
            bool secondaryOpened = false;
            secondary.Opened += _ => secondaryOpened = true;
            main.Opened += _ =>
            {
                secondary.Show();
                Assert.Equal(2, lifetime.Windows.Count);

                secondary.Close();
                Assert.True(secondary.IsClosed);
                Assert.Single(lifetime.Windows); // 非主窗口关闭不退出

                main.Close();
            };

            int code = lifetime.Start();

            Assert.Equal(0, code);
            Assert.True(secondaryOpened);
            Assert.Empty(lifetime.Windows);
        }
        finally
        {
            Application.ClearCurrent(app);
        }
    }

    [Fact]
    public void Closing_can_be_cancelled()
    {
        var lifetime = NewLifetime(out _);
        var app = new Application { Lifetime = lifetime };
        Application.SetCurrent(app);
        try
        {
            bool allow = false;
            var window = NewWindow();
            window.Closing += () => allow;
            lifetime.MainWindow = window;
            window.Opened += w =>
            {
                w.Close();
                Assert.True(w.IsShown);

                allow = true;
                w.Close();
                Assert.True(w.IsClosed);
            };

            lifetime.Start();

            Assert.True(window.IsClosed);
            Assert.Empty(lifetime.Windows);
        }
        finally
        {
            Application.ClearCurrent(app);
        }
    }

    [Fact]
    public void Platform_close_request_uses_the_same_closing_hook()
    {
        var lifetime = NewLifetime(out _);
        var app = new Application { Lifetime = lifetime };
        Application.SetCurrent(app);
        try
        {
            bool allow = false;
            var window = NewWindow();
            window.Closing += () => allow;
            lifetime.MainWindow = window;
            window.Opened += w =>
            {
                var impl = Assert.IsType<HeadlessWindow>(w.Host!.Window);
                impl.Close(); // 平台关闭按钮的路径
                Assert.True(w.IsShown);
                Assert.False(impl.IsClosed);

                allow = true;
                impl.Close();
            };

            lifetime.Start();

            Assert.True(window.IsClosed);
        }
        finally
        {
            Application.ClearCurrent(app);
        }
    }

    [Fact]
    public void Borderless_window_exposes_chrome_actions()
    {
        var lifetime = NewLifetime(out _);
        var app = new Application { Lifetime = lifetime };
        Application.SetCurrent(app);
        try
        {
            var window = new Window(new WindowOptions
            {
                Title = "borderless",
                Size = new AwAUI.Size(160, 100),
                Decorations = WindowDecorations.None,
            }) { Content = new Blank() };
            lifetime.MainWindow = window;
            window.Opened += w =>
            {
                var impl = Assert.IsType<HeadlessWindow>(w.Host!.Window);
                Assert.Equal(WindowDecorations.None, impl.Decorations);

                Assert.True(w.BeginDragMove());
                Assert.Equal(1, impl.DragMoveCount);

                Assert.True(w.Minimize());
                Assert.True(impl.IsMinimized);

                Assert.True(w.ToggleMaximize());
                Assert.True(w.IsMaximized);

                w.Close();
            };

            lifetime.Start();
        }
        finally
        {
            Application.ClearCurrent(app);
        }
    }

    [Fact]
    public void Shutdown_request_can_cancel_exit()
    {
        var lifetime = NewLifetime(out _);
        var app = new Application { Lifetime = lifetime };
        Application.SetCurrent(app);
        try
        {
            bool cancel = true;
            lifetime.ShutdownRequested += e => e.Cancel = cancel;
            var window = NewWindow();
            lifetime.MainWindow = window;
            window.Opened += w =>
            {
                Assert.False(lifetime.TryShutdown());
                Assert.True(w.IsShown);

                cancel = false;
                Assert.True(lifetime.TryShutdown());
            };

            lifetime.Start();

            Assert.True(window.IsClosed);
        }
        finally
        {
            Application.ClearCurrent(app);
        }
    }

    [Fact]
    public void Only_one_application_can_be_current()
    {
        var lifetime = NewLifetime(out _);
        var app = new Application { Lifetime = lifetime };
        Application.SetCurrent(app);
        try
        {
            Assert.Throws<InvalidOperationException>(() => Application.SetCurrent(new Application()));
        }
        finally
        {
            Application.ClearCurrent(app);
        }

        Assert.Null(Application.Current);
    }

    [Fact]
    public void Window_without_application_is_rejected()
    {
        var window = NewWindow();
        Assert.Throws<InvalidOperationException>(() => window.Show());
        Assert.False(window.IsShown);
    }
}
