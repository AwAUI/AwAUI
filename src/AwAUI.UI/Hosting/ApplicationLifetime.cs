using System;

namespace AwAUI.UI;

/// <summary>
/// 应用生命周期（design/01 规则 5）：持有窗口集合与退出策略，运行平台消息循环。
/// 组合根创建具体生命周期并交给 <see cref="Application.Lifetime"/>；桌面应用使用
/// <see cref="DesktopApplicationLifetime"/>，移动端 / Web 以后可提供单视图实现。
/// <para>
/// 生命周期是可替换的扩展点，按 design/11 用抽象类而不是接口。应用代码通常只在
/// <see cref="Application.OnFrameworkInitializationCompleted"/> 里设置主窗口，不直接调用 <see cref="Start"/>。
/// </para>
/// </summary>
public abstract class ApplicationLifetime
{
    /// <summary>命令行参数。</summary>
    public string[] Args { get; internal set; } = Array.Empty<string>();

    /// <summary>主循环启动之前触发。</summary>
    public event Action<StartupEventArgs>? Startup;

    /// <summary>主循环结束、进程退出之前触发；可修改 <see cref="ExitEventArgs.ExitCode"/>。</summary>
    public event Action<ExitEventArgs>? Exit;

    /// <summary>请求退出（强制关闭窗口，忽略关闭取消）。</summary>
    public abstract void Shutdown(int exitCode = 0);

    /// <summary>由组合根调用：显示主窗口并运行主循环，返回退出码。</summary>
    internal abstract int Start();

    internal abstract IWindowHostProvider? WindowHostProvider { get; }

    /// <summary>窗口内容挂载完成、显示之前（组合根挂 DevTools 与帧事件）。</summary>
    internal virtual void OnHostReady(UIHost host)
    {
    }

    /// <summary>窗口显示时登记；由具体生命周期维护窗口集合。</summary>
    internal virtual void Register(Window window)
    {
    }

    /// <summary>窗口关闭时移除，并按退出策略决定是否结束生命周期。</summary>
    internal virtual void Unregister(Window window)
    {
    }

    /// <summary>供具体生命周期派生类触发启动事件。</summary>
    protected void RaiseStartup(StartupEventArgs args) => Startup?.Invoke(args);

    /// <summary>供具体生命周期派生类触发退出事件。</summary>
    protected void RaiseExit(ExitEventArgs args) => Exit?.Invoke(args);
}

/// <summary>启动事件参数。</summary>
public sealed class StartupEventArgs
{
    public StartupEventArgs(string[] args) => Args = args ?? Array.Empty<string>();

    public string[] Args { get; }
}

/// <summary>退出事件参数。</summary>
public sealed class ExitEventArgs
{
    public ExitEventArgs(int exitCode) => ExitCode = exitCode;

    /// <summary>返回给操作系统的退出码（事件处理器可以修改）。</summary>
    public int ExitCode { get; set; }
}

/// <summary>应用何时退出。</summary>
public enum ShutdownMode
{
    /// <summary>最后一个窗口关闭时退出（默认）。</summary>
    OnLastWindowClose,

    /// <summary>主窗口关闭时退出，其余窗口随之关闭。</summary>
    OnMainWindowClose,

    /// <summary>只在显式调用 <see cref="ApplicationLifetime.Shutdown"/> 时退出。</summary>
    OnExplicitShutdown,
}

/// <summary>生命周期 → 托管窗口的宿主工厂。由 <see cref="Window"/> 使用，不对外公开。</summary>
internal interface IWindowHostProvider
{
    UIHost CreateHost(Window window);
}
