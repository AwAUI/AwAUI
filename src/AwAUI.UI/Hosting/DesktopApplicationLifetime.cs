using System;
using System.Collections.Generic;
using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.Text;

namespace AwAUI.UI;

/// <summary>
/// 桌面应用生命周期（design/01 规则 5）：跟踪打开的窗口、显示主窗口、运行平台消息循环，
/// 并按 <see cref="ShutdownMode"/> 退出。窗口由应用创建，<see cref="Window.Show"/> 时登记到这里。
/// <para>
/// 关闭流程：窗口关闭 → 从 <see cref="Windows"/> 移除 → 按 <see cref="ShutdownMode"/> 决定是否退出；
/// 退出时先触发可取消的 <see cref="ShutdownRequested"/>，再关闭其余窗口（每个窗口的关闭仍可取消），
/// 最后调用 <see cref="PlatformBackend.Exit"/> 结束主循环。
/// </para>
/// </summary>
public sealed class DesktopApplicationLifetime : ApplicationLifetime, IWindowHostProvider
{
    readonly PlatformBackend _platform;
    readonly RenderBackend _renderer;
    readonly TextEngine _textEngine;
    readonly List<Window> _windows = new();
    int _exitCode;
    bool _started;
    bool _shuttingDown;
    bool _exitRequested;

    public DesktopApplicationLifetime(PlatformBackend platform, RenderBackend renderer, TextEngine textEngine)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _textEngine = textEngine ?? throw new ArgumentNullException(nameof(textEngine));
    }

    /// <summary>何时退出。默认 <see cref="ShutdownMode.OnLastWindowClose"/>。</summary>
    public ShutdownMode ShutdownMode { get; set; } = ShutdownMode.OnLastWindowClose;

    /// <summary>主窗口；由 <see cref="Application.OnFrameworkInitializationCompleted"/> 设置。</summary>
    public Window? MainWindow { get; set; }

    /// <summary>当前打开的窗口（按显示顺序）。</summary>
    public IReadOnlyList<Window> Windows => _windows;

    /// <summary>
    /// 退出请求（<see cref="TryShutdown"/> 与关闭最后一个窗口 / 主窗口时触发）；设置
    /// <see cref="ShutdownRequestedEventArgs.Cancel"/> 可以取消。强制 <see cref="Shutdown"/> 不触发它。
    /// 操作系统的注销 / 关机请求以后也经这里上报。
    /// </summary>
    public event Action<ShutdownRequestedEventArgs>? ShutdownRequested;

    /// <summary>尝试退出：先询问 <see cref="ShutdownRequested"/>，再关闭所有窗口；有窗口拒绝关闭时返回 false。</summary>
    public bool TryShutdown(int exitCode = 0) => DoShutdown(exitCode, force: false);

    /// <summary>强制退出：忽略 <see cref="ShutdownRequested"/> 与窗口的关闭取消。</summary>
    public override void Shutdown(int exitCode = 0) => DoShutdown(exitCode, force: true);

    /// <summary>每个窗口的宿主挂载完内容之后调用（组合根用它挂 DevTools 与帧事件）。</summary>
    internal Action<UIHost>? HostConfigured { get; set; }

    internal override IWindowHostProvider? WindowHostProvider => this;

    // 显式实现保持 i 宿主工厂不进入公共 API；Window.Show 经 ApplicationLifetime.WindowHostProvider 调用。
    UIHost IWindowHostProvider.CreateHost(Window window)
    {
        var impl = _platform.CreateWindow(window.Options);
        return new UIHost(impl, _renderer, _textEngine, _platform.Dispatcher) { TopLevel = window };
    }

    internal override void OnHostReady(UIHost host) => HostConfigured?.Invoke(host);

    internal override void Register(Window window)
    {
        if (!_windows.Contains(window)) _windows.Add(window);
    }

    internal override void Unregister(Window window)
    {
        if (!_windows.Remove(window) || _shuttingDown || _exitRequested) return;
        if (ShutdownMode == ShutdownMode.OnLastWindowClose && _windows.Count == 0)
            TryShutdown();
        else if (ShutdownMode == ShutdownMode.OnMainWindowClose && ReferenceEquals(window, MainWindow))
            TryShutdown();
    }

    internal override int Start()
    {
        if (_started) throw new InvalidOperationException("The application lifetime has already started.");
        _started = true;

        RaiseStartup(new StartupEventArgs(Args));

        if (!_exitRequested)
        {
            if (MainWindow is { } main)
                main.Show();
            else if (_windows.Count == 0 && ShutdownMode != ShutdownMode.OnExplicitShutdown)
                _exitRequested = true; // 没有主窗口也没有已显示的窗口：没有可运行的东西
        }

        if (!_exitRequested) _platform.Run();

        var exit = new ExitEventArgs(_exitCode);
        RaiseExit(exit);
        _exitCode = exit.ExitCode;
        return _exitCode;
    }

    bool DoShutdown(int exitCode, bool force)
    {
        if (_exitRequested) return true;
        if (_shuttingDown) return false;

        _shuttingDown = true;
        try
        {
            if (!force)
            {
                var args = new ShutdownRequestedEventArgs();
                ShutdownRequested?.Invoke(args);
                if (args.Cancel) return false;
            }

            foreach (var window in _windows.ToArray())
            {
                if (!window.CloseCore(programmatic: true, ignoreCancel: force) && !force)
                    return false;
            }

            if (!force && _windows.Count > 0) return false; // 某个窗口取消了关闭

            _exitCode = exitCode;
            _exitRequested = true;
            _platform.Exit();
            return true;
        }
        finally
        {
            _shuttingDown = false;
        }
    }
}

/// <summary>退出请求参数。</summary>
public sealed class ShutdownRequestedEventArgs
{
    /// <summary>设为 true 取消退出（例如有未保存的文档）。</summary>
    public bool Cancel { get; set; }
}
