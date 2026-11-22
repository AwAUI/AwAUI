using System;
using System.Collections.Generic;
using System.IO;
using AwAUI.Css;
using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.Render;
using AwAUI.Text;
using AwAUI.UI;

namespace AwAUI.Desktop;

/// <summary>
/// 桌面应用的托管组合根（design/01 规则 5）：应用只描述界面与配置，平台后端、渲染后端与文本引擎在这里装配。
/// <para>
/// 应用不引用任何平台程序集：<see cref="UsePlatformDetect"/> 使用构建期为当前目标平台选定的后端
/// （Windows → AwAUI.Win32，macOS → AwAUI.MacOS，Linux → AwAUI.X11），其他平台的代码不会进入依赖图。
/// </para>
/// <example>
/// <code>
/// using AwAUI.Desktop;
///
/// AwAppBuilder.Create()
///     .UsePlatformDetect()
///     .UseSoftwareRenderer()
///     .UseDefaultTextEngine()
///     .UseStyleSheetFile("Styles/app.css")
///     .Run&lt;App&gt;();
/// </code>
/// </example>
/// <para>构建器是单次使用的：<see cref="Run{TApp}()"/> 返回后释放它自己创建的渲染后端；外部通过
/// <see cref="UseRenderer"/> 传入的后端仍由调用方负责释放。</para>
/// </summary>
public sealed class AwAppBuilder : IDisposable
{
    readonly List<StyleSheet> _styleSheets = new();
    WindowOptions _options = new();
    ShutdownMode _shutdownMode = ShutdownMode.OnLastWindowClose;
    PlatformBackend? _platform;
    RenderBackend? _renderer;
    TextEngine? _textEngine;
    // DevTools 用延迟委托保存：未调用 UseDevTools 时，对 EnableDevTools 的引用在裁剪阶段不可达，
    // 整个 Diagnostics / 协议栈不会进入 NativeAOT 产物。
    Action<UIHost>? _devToolsConfigure;
    Action<UIHost>? _configureHost;
    bool _ownsRenderer;
    bool _disposed;

    AwAppBuilder()
    {
    }

    /// <summary>创建构建器。所有装配项都有默认值：平台按目标 OS、软件渲染、系统字体文本引擎。</summary>
    public static AwAppBuilder Create() => new();

    /// <summary>按构建期选定的目标平台创建窗口后端。Windows / macOS / Linux 之外的目标会抛
    /// <see cref="PlatformNotSupportedException"/>，此时请改用 <see cref="UsePlatform"/>。</summary>
    public AwAppBuilder UsePlatformDetect()
    {
        ThrowIfDisposed();
        _platform = DesktopPlatform.Create();
        return this;
    }

    /// <summary>显式指定平台后端（自定义平台、测试与无窗口宿主使用）。</summary>
    public AwAppBuilder UsePlatform(PlatformBackend platform)
    {
        ThrowIfDisposed();
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        return this;
    }

    /// <summary>使用软件渲染后端（默认）。</summary>
    public AwAppBuilder UseSoftwareRenderer()
    {
        ThrowIfDisposed();
        ReplaceOwnedRenderer(new SoftwareRenderBackend());
        return this;
    }

    /// <summary>使用调用方提供的渲染后端（GPU 后端等）；构建器不负责释放它。</summary>
    public AwAppBuilder UseRenderer(RenderBackend renderer)
    {
        ThrowIfDisposed();
        if (renderer is null) throw new ArgumentNullException(nameof(renderer));
        if (_ownsRenderer) _renderer?.Dispose();
        _renderer = renderer;
        _ownsRenderer = false;
        return this;
    }

    /// <summary>使用系统字体与默认整形器创建文本引擎（默认）。</summary>
    public AwAppBuilder UseDefaultTextEngine()
    {
        ThrowIfDisposed();
        _textEngine = new DefaultTextEngine(SystemFonts.CreateCollection());
        return this;
    }

    /// <summary>使用调用方提供的文本引擎。</summary>
    public AwAppBuilder UseTextEngine(TextEngine textEngine)
    {
        ThrowIfDisposed();
        _textEngine = textEngine ?? throw new ArgumentNullException(nameof(textEngine));
        return this;
    }

    /// <summary>设置兼容 <see cref="Run(Node)"/> 的窗口选项（标题、初始大小、是否可缩放、装饰方式）。</summary>
    public AwAppBuilder UseWindowOptions(WindowOptions options)
    {
        ThrowIfDisposed();
        _options = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>在兼容 <see cref="Run(Node)"/> 的默认窗口选项上做增量配置。</summary>
    public AwAppBuilder UseWindow(Action<WindowOptions> configure)
    {
        ThrowIfDisposed();
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        configure(_options);
        return this;
    }

    /// <summary>加入一张已解析的样式表，按加入顺序进入级联。</summary>
    public AwAppBuilder UseStyleSheet(StyleSheet sheet)
    {
        ThrowIfDisposed();
        _styleSheets.Add(sheet ?? throw new ArgumentNullException(nameof(sheet)));
        return this;
    }

    /// <summary>解析并加入一段 CSS 文本。</summary>
    public AwAppBuilder UseStyleSheet(string css, string sourceName = "app.css")
    {
        ThrowIfDisposed();
        if (css is null) throw new ArgumentNullException(nameof(css));
        _styleSheets.Add(StyleSheet.Parse(css, sourceName));
        return this;
    }

    /// <summary>从文件读取并加入样式表（源名取文件名）。</summary>
    public AwAppBuilder UseStyleSheetFile(string path)
    {
        ThrowIfDisposed();
        if (path is null) throw new ArgumentNullException(nameof(path));
        _styleSheets.Add(StyleSheet.Parse(File.ReadAllText(path), Path.GetFileName(path)));
        return this;
    }

    /// <summary>为每个窗口启用 DevTools（F12 拾取元素；可按需在 WebSocket 上监听）。</summary>
    public AwAppBuilder UseDevTools(Action<UIDevToolsOptions>? configure = null)
    {
        ThrowIfDisposed();
        var options = new UIDevToolsOptions();
        configure?.Invoke(options);
        _devToolsConfigure = host => host.EnableDevTools(options);
        return this;
    }

    /// <summary>宿主创建完成后的额外配置（例如挂接帧事件），在挂载根组件前后由调用方决定。</summary>
    public AwAppBuilder ConfigureHost(Action<UIHost> configure)
    {
        ThrowIfDisposed();
        _configureHost = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    /// <summary>设置桌面生命周期的退出策略（默认最后一个窗口关闭时退出）。</summary>
    public AwAppBuilder UseShutdownMode(ShutdownMode mode)
    {
        ThrowIfDisposed();
        _shutdownMode = mode;
        return this;
    }

    /// <summary>创建窗口与宿主、加入样式表并挂载根组件；不进入消息循环（测试与自定义宿主使用）。</summary>
    public UIHost CreateHost(Node root)
    {
        ThrowIfDisposed();
        if (root is null) throw new ArgumentNullException(nameof(root));

        var host = AwApp.CreateHost(EnsurePlatform(), _options, EnsureRenderer(), EnsureTextEngine(), root, _styleSheets.ToArray());
        _devToolsConfigure?.Invoke(host);
        _configureHost?.Invoke(host);
        return host;
    }

    /// <summary>
    /// 启动应用：创建 <typeparamref name="TApp"/>、注入生命周期与应用样式表，依次调用
    /// <see cref="Application.Initialize"/>、<see cref="Application.OnFrameworkInitializationCompleted"/>，
    /// 显示主窗口并运行消息循环，最后调用 <see cref="Application.OnExit"/>；返回退出码。
    /// </summary>
    /// <remarks>
    /// 应用类必须是 <see cref="Application"/> 的可构造子类；窗口 / 主窗口由应用在
    /// <see cref="Application.OnFrameworkInitializationCompleted"/> 里创建，构建器不猜窗口结构。
    /// </remarks>
    public int Run<TApp>(params string[] args) where TApp : Application, new()
    {
        ThrowIfDisposed();
        return RunApplication(new TApp(), args);
    }

    /// <summary>启动一个已经构造好的应用实例（测试与工厂使用）。</summary>
    public int RunApplication(Application application, params string[] args)
    {
        ThrowIfDisposed();
        if (application is null) throw new ArgumentNullException(nameof(application));

        var lifetime = new DesktopApplicationLifetime(EnsurePlatform(), EnsureRenderer(), EnsureTextEngine())
        {
            Args = args ?? Array.Empty<string>(),
            ShutdownMode = _shutdownMode,
        };
        lifetime.HostConfigured = ConfigureHostInstance;

        try
        {
            Application.SetCurrent(application);
            application.Lifetime = lifetime;
            foreach (var sheet in _styleSheets)
                if (sheet is not null) application.Styles.Add(sheet);

            application.Initialize();
            application.OnFrameworkInitializationCompleted();
            var exitCode = lifetime.Start();
            application.OnExit(exitCode);
            return exitCode;
        }
        finally
        {
            Application.ClearCurrent(application);
            Dispose();
        }
    }

    /// <summary>
    /// 单窗口兼容入口：把 <paramref name="root"/> 作为主窗口内容，按 <see cref="UseWindowOptions"/> 的设置显示。
    /// 新代码请用 <see cref="Run{TApp}()"/> 并从 <see cref="Application"/> 创建窗口。
    /// </summary>
    public int Run(Node root)
    {
        ThrowIfDisposed();
        if (root is null) throw new ArgumentNullException(nameof(root));
        return RunApplication(new SingleWindowApplication(root, _options), Array.Empty<string>());
    }

    void ConfigureHostInstance(UIHost host)
    {
        _devToolsConfigure?.Invoke(host);
        _configureHost?.Invoke(host);
    }

    PlatformBackend EnsurePlatform() => _platform ??= DesktopPlatform.Create();

    RenderBackend EnsureRenderer()
    {
        if (_renderer is null) ReplaceOwnedRenderer(new SoftwareRenderBackend());
        return _renderer!;
    }

    TextEngine EnsureTextEngine() => _textEngine ??= new DefaultTextEngine(SystemFonts.CreateCollection());

    /// <summary>兼容 <see cref="Run(Node)"/>：初始化完成时把根节点放进主窗口。</summary>
    sealed class SingleWindowApplication : Application
    {
        readonly Node _root;
        readonly WindowOptions _options;

        public SingleWindowApplication(Node root, WindowOptions options)
        {
            _root = root;
            _options = options;
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (Lifetime is not DesktopApplicationLifetime lifetime)
                throw new InvalidOperationException("The single-window entry point requires a desktop application lifetime.");
            var options = new WindowOptions
            {
                Title = _options.Title,
                Size = _options.Size,
                Resizable = _options.Resizable,
                Decorations = _options.Decorations,
            };
            lifetime.MainWindow = new Window(options) { Content = _root };
        }
    }

    void ReplaceOwnedRenderer(RenderBackend renderer)
    {
        if (_ownsRenderer) _renderer?.Dispose();
        _renderer = renderer;
        _ownsRenderer = true;
    }

    void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AwAppBuilder));
    }

    /// <summary>释放构建器创建的资源（仅限它自己创建的渲染后端）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsRenderer) _renderer?.Dispose();
        _renderer = null;
    }
}
