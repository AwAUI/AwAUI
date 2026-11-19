using System;
using AwAUI.Css;
using AwAUI.Diagnostics.Client;
using AwAUI.Diagnostics.Inspection;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Diagnostics.Transport;
using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.Text;
using AwAUI.UI;

namespace AwAUI.DevTools;

/// <summary><see cref="DevToolsPanel"/> 的选项。</summary>
public sealed class DevToolsPanelOptions
{
    /// <summary>面板窗口的标题。</summary>
    public string Title { get; set; } = "AwAUI DevTools";

    /// <summary>面板窗口的大小（逻辑像素）。</summary>
    public Size Size { get; set; } = new(980, 640);

    /// <summary>
    /// 在应用窗口里按 F12（<see cref="UIDevToolsOptions.ToggleInspectKey"/>，进入拾取模式）时自动打开面板。默认开启；
    /// 只对 <see cref="DevToolsPanel.Attach"/> 有意义。
    /// </summary>
    public bool OpenOnInspect { get; set; } = true;

    /// <summary>要检查的目标（窗口）id；为 null 时：进程内为被附加窗口的目标，远程为 <c>App.hello</c> 列出的第一个目标。</summary>
    public string? TargetId { get; set; }

    /// <summary>远程应用要求的配对令牌（<c>App.hello</c> 携带）；进程内不需要。</summary>
    public string? PairingToken { get; set; }
}

/// <summary>
/// 内嵌 DevTools 面板（design/06 ④「独立窗口」形态）：一个用 AwAUI.UI 写成的检查器，在同进程的第二个原生窗口里打开，
/// 显示元素树（虚拟化）、匹配的规则与计算值、盒模型，可实时编辑声明，并与 F12 拾取、悬停高亮联动。
/// <para>
/// 面板<b>只通过协议</b>与被检查的应用通信（<see cref="DevToolsClient"/>）：<see cref="Attach"/> 用进程内传输连到应用窗口的
/// DevTools Agent，<see cref="Connect"/> 接受任意传输（例如连到远程应用的 <see cref="WebSocketClientTransport"/>），两者是同一套界面。
/// 面板窗口自己从不登记为调试目标，所以它不会出现在自己的元素树里，F12 也不会拾取到它。
/// </para>
/// <code>
/// var host = AwApp.CreateHost(platform, options, backend, textEngine, new App(), sheets);
/// var panel = DevToolsPanel.Attach(host, platform, backend, textEngine);   // F12 打开；或 panel.Open()
/// </code>
/// 所有成员只能在 UI 线程（<see cref="PlatformBackend.Dispatcher"/> 的线程）上调用。
/// </summary>
public sealed class DevToolsPanel : IDisposable
{
    static StyleSheet? s_sheet;

    readonly PlatformBackend _platform;
    readonly RenderBackend _backend;
    readonly TextEngine _textEngine;
    readonly DevToolsPanelOptions _options;
    readonly UIHost? _app;
    readonly string? _targetId;
    readonly bool _ownsTransport;
    PanelSession? _session;
    bool _disposed;

    DevToolsPanel(PlatformBackend platform, RenderBackend backend, TextEngine textEngine, DevToolsPanelOptions options,
        DevToolsClient client, UIHost? app, string? targetId, bool ownsTransport)
    {
        _platform = platform;
        _backend = backend;
        _textEngine = textEngine;
        _options = options;
        Client = client;
        _app = app;
        _targetId = targetId;
        _ownsTransport = ownsTransport;
    }

    /// <summary>
    /// 把面板附加到应用窗口（同进程）：必要时为该窗口启用 DevTools（<see cref="UIHost.EnableDevTools"/>），经进程内传输连上它的 Agent。
    /// 面板窗口此时还没有打开：应用窗口里按 F12（<see cref="DevToolsPanelOptions.OpenOnInspect"/>）或调用 <see cref="Open"/> 打开。
    /// 应用窗口关闭时面板随之关闭。
    /// </summary>
    /// <param name="app">被检查的应用窗口的宿主。</param>
    /// <param name="platform">用来创建面板窗口的平台（与应用同一个平台与 UI 线程）。</param>
    /// <param name="backend">面板窗口的渲染后端（可与应用共用）。</param>
    /// <param name="textEngine">面板的文本引擎（可与应用共用）。</param>
    public static DevToolsPanel Attach(UIHost app, PlatformBackend platform, RenderBackend backend, TextEngine textEngine, DevToolsPanelOptions? options = null)
    {
        if (app is null) throw new ArgumentNullException(nameof(app));
        Check(platform, backend, textEngine);
        options ??= new DevToolsPanelOptions();
        var devTools = app.DevTools ?? app.EnableDevTools();
        var (agentSide, clientSide) = InProcTransport.CreatePair();
        var agentSession = devTools.Connect(agentSide);
        var client = new DevToolsClient(clientSide, new DevToolsClientOptions { Dispatcher = platform.Dispatcher });
        var panel = new DevToolsPanel(platform, backend, textEngine, options, client, app, options.TargetId ?? devTools.Target.Id, ownsTransport: true)
        {
            _agentSide = agentSide,
            _agentSession = agentSession,
            _agent = devTools.Agent,
        };
        app.FrameCompleted += panel.OnAppFrame;
        app.Closed += panel.OnAppClosed;
        return panel;
    }

    /// <summary>
    /// 在任意传输上创建面板（远程形态）：例如 <c>await WebSocketClientTransport.ConnectAsync(new Uri("ws://127.0.0.1:9230/devtools"))</c>。
    /// 调用 <see cref="Open"/> 打开窗口。面板释放时一并释放传输。
    /// </summary>
    public static DevToolsPanel Connect(DevToolsTransport transport, PlatformBackend platform, RenderBackend backend, TextEngine textEngine, DevToolsPanelOptions? options = null)
    {
        if (transport is null) throw new ArgumentNullException(nameof(transport));
        Check(platform, backend, textEngine);
        options ??= new DevToolsPanelOptions();
        var client = new DevToolsClient(transport, new DevToolsClientOptions { Dispatcher = platform.Dispatcher });
        return new DevToolsPanel(platform, backend, textEngine, options, client, null, options.TargetId, ownsTransport: true);
    }

    static void Check(PlatformBackend platform, RenderBackend backend, TextEngine textEngine)
    {
        if (platform is null) throw new ArgumentNullException(nameof(platform));
        if (backend is null) throw new ArgumentNullException(nameof(backend));
        if (textEngine is null) throw new ArgumentNullException(nameof(textEngine));
    }

    InProcTransport? _agentSide;
    DevToolsSession? _agentSession;
    DevToolsAgent? _agent;

    /// <summary>面板与应用之间的协议客户端。</summary>
    public DevToolsClient Client { get; }

    /// <summary>面板窗口是否打开。</summary>
    public bool IsOpen => _session is not null;

    /// <summary>面板窗口的宿主（打开时）；测试经它驱动面板的输入与读取面板自己的元素。</summary>
    public UIHost? Host => _session?.Host;

    /// <summary>面板窗口（打开时）。</summary>
    public WindowImpl? Window => _session?.Host.Window;

    /// <summary>当前选中的节点 id（协议的节点 id，即应用元素的 <see cref="Element.UniqueId"/>）；没有选中为 0。</summary>
    public long SelectedNodeId => _session?.Model.Selected?.Id ?? 0;

    /// <summary>面板窗口打开。</summary>
    public event Action<DevToolsPanel>? Opened;

    /// <summary>面板窗口关闭。</summary>
    public event Action<DevToolsPanel>? Closed;

    internal InspectorModel? Model => _session?.Model;

    /// <summary>打开面板窗口（已打开时什么也不做）：握手、启用 Tree / Css / Overlay 域并加载元素树。</summary>
    public void Open()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DevToolsPanel));
        if (_session is not null) return;
        var window = _platform.CreateWindow(new WindowOptions { Title = _options.Title, Size = _options.Size });
        var host = new UIHost(window, _backend, _textEngine, _platform.Dispatcher);
        host.AddStyleSheet(s_sheet ??= StyleSheet.Parse(PanelStyles.Css, "AwAUI.DevTools panel"));
        var model = new InspectorModel(Client, _platform.Dispatcher, _targetId, _options.PairingToken);
        var session = new PanelSession(this, host, model);
        _session = session;
        host.Closed += _ => OnPanelWindowClosed(session);
        host.FrameCompleted += session.AfterFrame;
        model.Start();
        window.Show();
        host.Mount(new PanelView(model, session.Refs, session.HandleKey));
        // 面板快捷键挂在根元素上（按键派发到焦点元素并冒泡）：一开始就让面板根持有焦点
        session.Refs.Root.Current?.Focus();
        Opened?.Invoke(this);
    }

    /// <summary>关闭面板窗口（连接保留，可再次 <see cref="Open"/>）。</summary>
    public void Close()
    {
        if (_session is not { } s) return;
        s.Host.Window.Close();
        // 平台在 Close 中同步回调 OnClosed 时，OnPanelWindowClosed 已经处理；否则在这里收尾
        if (_session == s) OnPanelWindowClosed(s);
    }

    void OnPanelWindowClosed(PanelSession s)
    {
        if (_session != s) return;
        _session = null;
        s.Model.Dispose();
        s.Host.Dispose();
        Closed?.Invoke(this);
    }

    /// <summary>
    /// 应用每帧之后：应用<b>进入</b>拾取模式（F12）时打开面板（同浏览器：F12 打开 DevTools 并开始检查元素）。
    /// 只看「未拾取 → 拾取」的边沿：拾取中关掉面板不会让它立刻重新打开。
    /// </summary>
    void OnAppFrame(UIHost app)
    {
        var mode = app.DevTools?.InspectMode ?? InspectMode.None;
        bool entered = mode == InspectMode.Pick && _lastAppMode != InspectMode.Pick;
        _lastAppMode = mode;
        if (!entered || _disposed || _session is not null || !_options.OpenOnInspect) return;
        _platform.Dispatcher.Post(() =>
        {
            if (!_disposed && _session is null) Open();
        });
    }

    InspectMode _lastAppMode;

    void OnAppClosed(UIHost app) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        if (_app is not null)
        {
            _app.FrameCompleted -= OnAppFrame;
            _app.Closed -= OnAppClosed;
        }
        Client.Dispose();
        if (_ownsTransport)
        {
            Client.Transport.Dispose();
            _agentSide?.Dispose();
        }
        // 进程内会话从应用的 Agent 上摘下（否则 Agent 一直持有它）
        if (_agent is not null && _agentSession is not null) _agent.Detach(_agentSession);
    }
}

/// <summary>一次打开的面板窗口：宿主、模型与需要在布局后量取的元素。</summary>
internal sealed class PanelSession
{
    public PanelSession(DevToolsPanel panel, UIHost host, InspectorModel model)
    {
        Panel = panel;
        Host = host;
        Model = model;
    }

    public readonly DevToolsPanel Panel;
    public readonly UIHost Host;
    public readonly InspectorModel Model;
    public readonly PanelRefs Refs = new();

    /// <summary>每帧之后量取滚动区域（视口高度决定树视图要描述多少行）。</summary>
    public void AfterFrame(UIHost host)
    {
        if (Refs.TreeViewport.Current is { } tree) Model.SetTreeViewport(tree.BoundingRect.Height);
        if (Refs.SideViewport.Current is { } side)
        {
            float content = side.FirstChild?.BoundingRect.Height ?? 0;
            Model.SetSideMetrics(side.BoundingRect.Height, content);
        }
    }

    /// <summary>
    /// 面板窗口的快捷键（面板根元素的 keydown，冒泡而来）：方向键导航元素树，F12 / Ctrl+Shift+C 切换拾取，Esc 退出拾取。
    /// 编辑框有焦点时不拦截。处理了的键阻止默认动作（包括滚动系统的键盘滚动）。
    /// </summary>
    public void HandleKey(KeyEvent ke)
    {
        if (ke.Target is InputElement || Host.Root.FocusManager.FocusedElement is { Tag: "input" }) return;
        if (Handle(ke)) ke.PreventDefault();
    }

    bool Handle(KeyEvent e)
    {
        switch (e.PhysicalKey)
        {
            case PhysicalKey.F12:
                if (!e.IsRepeat) Model.TogglePick();
                return true;
            case PhysicalKey.KeyC when (e.Modifiers & KeyModifiers.Shift) != 0 && Shortcuts.HasPrimaryModifier(e.Modifiers):
                if (!e.IsRepeat) Model.TogglePick();
                return true;
            case PhysicalKey.Escape when Model.Picking:
                Model.TogglePick();
                return true;
            case PhysicalKey.ArrowUp:
                Model.MoveSelection(-1);
                return true;
            case PhysicalKey.ArrowDown:
                Model.MoveSelection(1);
                return true;
            case PhysicalKey.ArrowLeft:
                Model.CollapseOrAscend();
                return true;
            case PhysicalKey.ArrowRight:
                Model.ExpandOrDescend();
                return true;
            case PhysicalKey.PageUp:
                Model.ScrollTreeBy(-Model.TreeViewportHeight);
                return true;
            case PhysicalKey.PageDown:
                Model.ScrollTreeBy(Model.TreeViewportHeight);
                return true;
            default:
                return false;
        }
    }
}
