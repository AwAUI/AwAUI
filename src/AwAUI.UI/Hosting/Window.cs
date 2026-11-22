using System;
using System.Collections.Generic;
using AwAUI.Css;
using AwAUI.Platform;

namespace AwAUI.UI;

/// <summary>
/// 顶层窗口（design/01 规则 5）：把一棵 <see cref="Node"/> 树挂到一个平台窗口上，并加入
/// <see cref="Application.Current"/> 的生命周期。
/// <para>
/// 窗口由应用创建（通常在 <see cref="Application.OnFrameworkInitializationCompleted"/> 里），
/// <see cref="Show"/> 时登记到 <see cref="DesktopApplicationLifetime.Windows"/>；关闭按钮与
/// <see cref="Close"/> 汇聚到 <see cref="Closing"/>，可取消。窗口只做身份与生命周期，
/// 输入、布局、绘制与 DevTools 都在 <see cref="UIHost"/> 里。
/// </para>
/// <para>
/// 自绘边框：把 <see cref="WindowOptions.Decorations"/> 设为 <see cref="WindowDecorations.None"/>，
/// 内容铺满整个窗口；标题栏的按下事件调用 <see cref="BeginDragMove"/>，按钮调用
/// <see cref="Minimize"/>、<see cref="ToggleMaximize"/> 与 <see cref="Close"/>。组件内用
/// <c>HostWindow</c> 拿到所在窗口。平台不支持这些动作时方法返回 false。
/// </para>
/// </summary>
public sealed class Window
{
    Node? _content;
    UIHost? _host;
    bool _shown;
    bool _closed;

    public Window() : this(new WindowOptions())
    {
    }

    public Window(WindowOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>平台窗口选项（标题、初始大小、是否可缩放、装饰方式）。</summary>
    public WindowOptions Options { get; }

    /// <summary>窗口内容；<see cref="Show"/> 之后不能再改（换根请另开窗口）。</summary>
    public Node? Content
    {
        get => _content;
        set
        {
            if (_shown) throw new InvalidOperationException("Content cannot be changed after the window is shown.");
            _content = value;
        }
    }

    /// <summary>窗口级样式表：显示时在应用样式之后、组件样式之前加入级联。</summary>
    public IList<StyleSheet> Styles { get; } = new List<StyleSheet>();

    /// <summary>标题（显示后可改，立即转发给平台窗口）。</summary>
    public string Title
    {
        get => Options.Title;
        set
        {
            Options.Title = value ?? "";
            _host?.Window.SetTitle(Options.Title);
        }
    }

    /// <summary>窗口装饰方式。只能在 <see cref="Show"/> 之前修改。</summary>
    public WindowDecorations Decorations
    {
        get => Options.Decorations;
        set
        {
            if (_shown) throw new InvalidOperationException("Decorations cannot be changed after the window is shown.");
            Options.Decorations = value;
        }
    }

    /// <summary>客户区尺寸（逻辑像素）；未显示时为初始尺寸。</summary>
    public Size ClientSize => _host?.Window.ClientSize ?? Options.Size;

    /// <summary>窗口左上角在屏幕逻辑坐标中的位置；未显示时为 <see cref="WindowOptions.Position"/>。</summary>
    public Point Position
    {
        get => _host?.Window.Position ?? Options.Position ?? default;
        set
        {
            Options.Position = value;
            _host?.Window.SetPosition(value);
        }
    }

    /// <summary>窗口状态；设置后转发给平台，平台可能异步回报实际状态。</summary>
    public WindowState WindowState
    {
        get => _host?.Window.WindowState ?? Options.State;
        set
        {
            Options.State = value;
            _host?.Window.SetWindowState(value);
        }
    }

    /// <summary>是否置顶。</summary>
    public bool Topmost
    {
        get => _host?.Window.Topmost ?? Options.Topmost;
        set
        {
            Options.Topmost = value;
            if (_host is not null) _host.Window.Topmost = value;
        }
    }

    /// <summary>安全区（刘海、圆角、软键盘占位），逻辑像素。</summary>
    public Thickness SafeAreaInsets => _host?.Window.SafeAreaInsets ?? Thickness.Zero;

    /// <summary>窗口是否处于激活状态。</summary>
    public bool IsActive => _host?.Window.IsActive ?? false;

    /// <summary>请求平台激活窗口。</summary>
    public void Activate() => _host?.Window.Activate();

    /// <summary>窗口所在显示器集合；平台不支持时为 null。</summary>
    public Screens? Screens => _host?.Window.Screens;

    /// <summary>文件与目录选择器；平台不支持时为 null。</summary>
    public StorageProvider? StorageProvider => _host?.Window.StorageProvider;

    /// <summary>打开 URI / 文件；平台不支持时为 null。</summary>
    public Launcher? Launcher => _host?.Window.Launcher;

    /// <summary>窗口状态变化（包括用户通过系统标题栏改变最大化/最小化）。</summary>
    public event Action<Window>? StateChanged;

    /// <summary>窗口位置变化。</summary>
    public event Action<Window>? PositionChanged;

    /// <summary>窗口激活状态变化。</summary>
    public event Action<Window>? Activated;

    /// <summary>承载窗口内容的宿主；未显示或已关闭时为 null。</summary>
    public UIHost? Host => _host;

    /// <summary>是否已显示（关闭后回到 false）。</summary>
    public bool IsShown => _shown;

    /// <summary>是否已关闭（关闭后不能再显示）。</summary>
    public bool IsClosed => _closed;

    /// <summary>窗口显示之后触发。</summary>
    public event Action<Window>? Opened;

    /// <summary>窗口关闭、宿主销毁之后触发。</summary>
    public event Action<Window>? Closed;

    /// <summary>
    /// 窗口即将关闭（<see cref="Close"/> 或平台关闭按钮）：任一处理器返回 false 取消。
    /// 强制退出（<see cref="DesktopApplicationLifetime.Shutdown"/>）只通知，不能取消。
    /// </summary>
    public event Func<bool>? Closing;

    /// <summary>创建平台窗口与宿主、加入样式、挂载内容并显示；生命周期随后接管主循环。</summary>
    /// <exception cref="InvalidOperationException">没有正在运行的应用、窗口已关闭或已显示。</exception>
    public void Show()
    {
        if (_closed) throw new InvalidOperationException("A closed window cannot be shown again.");
        if (_shown) return;

        var application = Application.Current
            ?? throw new InvalidOperationException("Window.Show requires a running Application; start the app through AwAppBuilder.Run<TApp>().");
        var lifetime = application.Lifetime
            ?? throw new InvalidOperationException("The running Application has no lifetime.");
        var provider = lifetime.WindowHostProvider
            ?? throw new InvalidOperationException("The current application does not have a desktop application lifetime.");

        var host = provider.CreateHost(this);
        _host = host;
        host.CloseRequested = RequestCloseFromPlatform;
        host.Closed += _ => OnHostClosed();
        host.WindowStateChanged += state =>
        {
            Options.State = state;
            StateChanged?.Invoke(this);
        };
        host.WindowPositionChanged += position =>
        {
            Options.Position = position;
            PositionChanged?.Invoke(this);
        };
        host.WindowActivated += _ => Activated?.Invoke(this);

        foreach (var sheet in application.Styles)
            if (sheet is not null) host.AddStyleSheet(sheet);
        foreach (var sheet in Styles)
            if (sheet is not null) host.AddStyleSheet(sheet);

        if (_content is not null) host.Mount(_content);
        lifetime.OnHostReady(host);

        lifetime.Register(this);
        _shown = true;
        host.Window.Show();
        Opened?.Invoke(this);
    }

    /// <summary>关闭窗口；<see cref="Closing"/> 可以取消。</summary>
    public void Close() => CloseCore(programmatic: true, ignoreCancel: false);

    /// <summary>开始拖动窗口（自绘标题栏的指针按下处理）。平台不支持时返回 false。</summary>
    public bool BeginDragMove()
    {
        var chrome = Chrome();
        if (chrome is null) return false;
        chrome.BeginDragMove();
        return true;
    }

    /// <summary>最小化。平台不支持时返回 false。</summary>
    public bool Minimize()
    {
        var chrome = Chrome();
        if (chrome is null) return false;
        chrome.Minimize();
        return true;
    }

    /// <summary>在最大化与还原之间切换。平台不支持时返回 false。</summary>
    public bool ToggleMaximize()
    {
        var chrome = Chrome();
        if (chrome is null) return false;
        chrome.ToggleMaximize();
        return true;
    }

    /// <summary>是否已最大化。</summary>
    public bool IsMaximized => Chrome()?.IsMaximized ?? false;

    IWindowChrome? Chrome() => _host?.Window.GetFeature<IWindowChrome>();

    /// <summary>平台关闭请求：询问 <see cref="Closing"/>，同意后由平台继续并回调 <see cref="OnHostClosed"/>。</summary>
    bool RequestCloseFromPlatform()
    {
        if (_closed) return true;
        return InvokeClosing();
    }

    internal bool CloseCore(bool programmatic, bool ignoreCancel)
    {
        if (_closed) return true;
        if (!ignoreCancel && !InvokeClosing()) return false;

        var host = _host;
        if (host is null)
        {
            // 还没显示过：只标记关闭，Windows 中也没有登记
            OnHostClosed();
            return true;
        }

        host.CloseApproved = true;
        host.Window.Close();
        // 平台同步回调 OnClosed → OnHostClosed；不同步的后端在这里补一次，至少释放渲染目标与输入桥
        if (!_closed)
        {
            host.Dispose();
            OnHostClosed();
        }
        return true;
    }

    bool InvokeClosing()
    {
        var handlers = Closing;
        if (handlers is null) return true;
        foreach (Func<bool> handler in handlers.GetInvocationList())
            if (!handler()) return false;
        return true;
    }

    void OnHostClosed()
    {
        if (_closed) return;
        _closed = true;
        _shown = false;

        var host = _host;
        _host = null;
        if (host is not null)
        {
            host.TopLevel = null;
            host.CloseRequested = null;
        }

        Application.Current?.Lifetime?.Unregister(this);
        Closed?.Invoke(this);
    }
}
