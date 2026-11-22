using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Threading;
using AwAUI.Diagnostics;
using AwAUI.Diagnostics.Inspection;
using AwAUI.Diagnostics.Protocol;
using AwAUI.Diagnostics.Transport;
using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.Threading;

namespace AwAUI.UI;

/// <summary><see cref="UIHost.EnableDevTools"/> 的选项。</summary>
public sealed class UIDevToolsOptions
{
    /// <summary>
    /// 在本机回环地址上监听 WebSocket（design/06 ③），供远程 / 独立的 DevTools 前端与脚本连接。默认关闭。
    /// 需要宿主有 UI 线程调度器（<see cref="UIHost"/> 构造时传入的 <see cref="IDispatcher"/>）。
    /// </summary>
    public bool ListenWebSocket { get; set; }

    /// <summary>WebSocket 端口；0 表示由系统分配（见 <see cref="UIDevTools.WebSocketEndpoint"/>）。</summary>
    public int Port { get; set; }

    /// <summary>WebSocket 请求路径。</summary>
    public string Path { get; set; } = "/devtools";

    /// <summary>
    /// 监听地址，默认 <see cref="IPAddress.Loopback"/>。绑定到非回环地址时必须设置 <see cref="PairingToken"/>（design/06「安全」）。
    /// </summary>
    public IPAddress Address { get; set; } = IPAddress.Loopback;

    /// <summary>配对令牌（远程连接时 <c>App.hello</c> 必须携带）；为 null 时不要求配对（只适合回环地址）。</summary>
    public string? PairingToken { get; set; }

    /// <summary>切换拾取模式的按键（浏览器的 F12 / 检查元素）；为 null 时不拦截按键。拾取模式下 Esc 取消。</summary>
    public PhysicalKey? ToggleInspectKey { get; set; } = PhysicalKey.F12;

    /// <summary>
    /// 运行时 Tailwind 补全的候选回调：帧末把该帧新出现（相对上一帧的整棵树）的类名一次性交给回调，可直接接到
    /// <c>TailwindRuntime.AddCandidates</c>。没有变化时不调用。为 null 时不报告新增。
    /// </summary>
    public Action<IReadOnlyList<string>>? ClassCandidates { get; set; }

    /// <summary>
    /// 不再出现在元素树上的类名，帧末一次性交给回调，可直接接到 <c>TailwindRuntime.RemoveCandidates</c>
    /// （撤回对应的运行时工具规则）。没有变化时不调用。为 null 时只在内部维护集合、不报告移除。
    /// </summary>
    public Action<IReadOnlyList<string>>? ClassCandidatesRemoved { get; set; }

    /// <summary>复用的 Agent（多个窗口共享一个 Agent，各自是一个目标）；为 null 时新建，随 <see cref="UIDevTools"/> 释放。</summary>
    public DevToolsAgent? Agent { get; set; }

    /// <summary>目标 id；为 null 时自动分配（<c>w1</c>、<c>w2</c> ...）。</summary>
    public string? TargetId { get; set; }

    /// <summary>App.getInfo 报告的应用名。</summary>
    public string? AppName { get; set; }
}

/// <summary>
/// 一个窗口的 DevTools 接入（design/06、20）：把窗口登记为 <see cref="DevToolsAgent"/> 的目标，挂上元素树、样式与叠加层三个检查接缝，
/// 可选地在本机 WebSocket 上监听。由 <see cref="UIHost.EnableDevTools"/> 创建。
/// <list type="bullet">
/// <item><b>Tree / Layout</b>：元素树（id 为 <see cref="Element.UniqueId"/>）、属性编辑、选择器查询、命中测试与盒模型。</item>
/// <item><b>Css</b>：命中的规则（来自 CSS 引擎的匹配记录）、计算值，实时编辑规则 / 内联样式 / 新规则，强制伪状态；下一帧生效。</item>
/// <item><b>Overlay</b>：在应用帧之上绘制高亮（外边距 / 边框 / 内边距 / 内容四色与尺寸提示），拾取模式截获指针。</item>
/// </list>
/// 宿主每帧在 UI 线程上调用 <see cref="DevToolsAgent.FlushFrame"/> 发出按帧合并的事件。除特别说明外，成员只能在 UI 线程上调用。
/// </summary>
public sealed class UIDevTools : IDisposable
{
    static int s_nextTarget;

    readonly UIHost _host;
    readonly bool _ownsAgent;
    readonly ElementInspectableTree _tree;
    readonly ElementStyleInspector _styles;
    readonly ElementOverlayHost _overlay;
    readonly ClassCandidateObserver? _classCandidates;
    readonly PhysicalKey? _toggleKey;
    WebSocketServerTransport? _webSocket;
    bool _localPick;
    long? _swallowPointer;
    bool _disposed;

    internal UIDevTools(UIHost host, UIDevToolsOptions options, IDispatcher? dispatcher)
    {
        _host = host;
        _toggleKey = options.ToggleInspectKey;
        if (options.Agent is { } agent) Agent = agent;
        else
        {
            var agentOptions = new DevToolsAgentOptions { PairingToken = options.PairingToken };
            if (options.AppName is { } name) agentOptions.AppInfo.Name = name;
            agentOptions.AppInfo.RenderBackend = host.BackendName;
            Agent = new DevToolsAgent(agentOptions);
            _ownsAgent = true;
        }
        var window = host.Window;
        string id = options.TargetId ?? "w" + Interlocked.Increment(ref s_nextTarget).ToString(CultureInfo.InvariantCulture);
        Target = new DevToolsTarget(id, "AwAUI window", window.ClientSize, (float)window.RenderScale) { Dispatcher = dispatcher, Tag = host };
        _tree = new ElementInspectableTree(host.Root, host.Pipeline);
        _styles = new ElementStyleInspector(host.Root, host.Pipeline, _tree);
        _overlay = new ElementOverlayHost(_tree, host.Pipeline, host.Root.Input, host.OnOverlayChanged);
        if (options.ClassCandidates is not null || options.ClassCandidatesRemoved is not null)
            _classCandidates = new ClassCandidateObserver(host.Root, options.ClassCandidates, options.ClassCandidatesRemoved);
        if (_classCandidates is not null) host.Root.AddObserver(_classCandidates);
        Target.Tree = _tree;
        Target.Styles = _styles;
        Target.Overlay = _overlay;
        Agent.AddTarget(Target);

        if (options.ListenWebSocket)
        {
            if (dispatcher is null)
                throw new InvalidOperationException("Listening on a WebSocket requires the host to have a UI-thread dispatcher (pass one to UIHost).");
            _webSocket = new WebSocketServerTransport(new WebSocketServerOptions { Address = options.Address, Port = options.Port, Path = options.Path });
            Agent.Attach(_webSocket);
            _webSocket.Start();
        }
    }

    /// <summary>协议 Agent（可被多个窗口共享）。</summary>
    public DevToolsAgent Agent { get; }

    /// <summary>本窗口对应的调试目标。</summary>
    public DevToolsTarget Target { get; }

    /// <summary>WebSocket 端点（<c>ws://127.0.0.1:端口/devtools</c>）；没有监听时为 null。</summary>
    public Uri? WebSocketEndpoint =>
        _webSocket?.LocalEndPoint is { } ep
            ? new Uri("ws://" + (ep.Address.Equals(IPAddress.Any) ? "127.0.0.1" : ep.Address.ToString()) + ":" + ep.Port.ToString(CultureInfo.InvariantCulture) + (_webSocket.Options.Path ?? "/"))
            : null;

    /// <summary>在一条传输上建立 DevTools 会话（进程内前端、测试、自定义传输）。可在任意线程调用。</summary>
    public DevToolsSession Connect(DevToolsTransport transport) => Agent.Attach(transport);

    /// <summary>当前的检查模式。</summary>
    public InspectMode InspectMode => _overlay.Mode;

    /// <summary>当前高亮的元素（拾取中为悬停的元素）。</summary>
    public Element? HighlightedElement => _overlay.Highlighted;

    /// <summary>当前的持续显示选项（<c>Overlay.setShowOptions</c>）：标尺、命中目标、触摸点与布局网格。</summary>
    public OverlayShowOptions ShowOptions => _overlay.ShowOptions;

    /// <summary>拾取模式下用户选中了元素（本地 F12 拾取与前端发起的拾取都会触发）。</summary>
    public event Action<Element>? ElementPicked;

    /// <summary>高亮一个元素（null 隐藏）；与 <c>Overlay.highlightNode</c> 相同的外观。</summary>
    public void Highlight(Element? element, HighlightConfig? config = null) => _overlay.Highlight(element, config);

    /// <summary>进入拾取模式（相当于在浏览器里按「检查元素」）：选中一个元素后自动退出并保持高亮它。</summary>
    public void StartInspecting()
    {
        _localPick = true;
        _overlay.SetInspectMode(InspectMode.Pick);
    }

    /// <summary>退出拾取模式；之前由协议设置的拾取会收到 <c>Overlay.inspectModeCanceled</c>。</summary>
    public void StopInspecting()
    {
        if (_overlay.Mode == InspectMode.None) return;
        _localPick = false;
        _overlay.SetInspectMode(InspectMode.None);
        _overlay.NotifyInspectModeCanceled();
    }

    /// <summary>
    /// 元素的文字摘要（标签、盒尺寸、内联样式与命中的规则，按级联优先级从高到低，被覆盖的声明标 <c>~~</c>）。
    /// 内嵌面板完成之前，示例在 F12 拾取后把它打印到控制台。
    /// </summary>
    public string DescribeElement(Element element)
    {
        if (element is null) throw new ArgumentNullException(nameof(element));
        var sb = new System.Text.StringBuilder();
        sb.Append(element).Append("  ").Append(element.BoundingRect.Width.ToString("0.##", CultureInfo.InvariantCulture))
          .Append(" × ").Append(element.BoundingRect.Height.ToString("0.##", CultureInfo.InvariantCulture));
        if (element.Owner is { } owner) sb.Append("  (").Append(owner.GetType().Name).Append(')');
        sb.AppendLine();
        var matched = _styles.GetMatchedRules(element.UniqueId);
        if (matched is null) return sb.ToString();
        if (matched.Inline.Count > 0)
        {
            sb.AppendLine("  element.style {");
            AppendDeclarations(sb, matched.Inline);
            sb.AppendLine("  }");
        }
        foreach (var m in matched.Matched)
        {
            sb.Append("  ").Append(m.Rule.SelectorText).Append(" {   /* ").Append(m.Rule.Source?.ToString() ?? m.Rule.SheetId).AppendLine(" */");
            AppendDeclarations(sb, m.Rule.Declarations);
            sb.AppendLine("  }");
        }
        return sb.ToString();
    }

    static void AppendDeclarations(System.Text.StringBuilder sb, System.Collections.Generic.IReadOnlyList<StyleDeclarationInfo> decls)
    {
        foreach (var d in decls)
        {
            string mark = d.Disabled ? "//" : d.Status == StyleDeclarationStatus.Active ? "  " : "~~";
            sb.Append("    ").Append(mark).Append(d.Name).Append(": ").Append(d.Value).Append(d.Important ? " !important" : "").AppendLine(";");
        }
    }

    // ------------------------------------------------------------ 宿主钩子

    internal bool HasOverlay => _overlay.HasContent;

    internal int OverlayVersion => _overlay.Version;

    /// <summary>在应用帧之上录制叠加层。</summary>
    internal DisplayList Compose(DisplayList frame)
    {
        var dc = new DrawingContext(256);
        dc.DrawDisplayList(frame);
        _overlay.Paint(dc);
        return dc.Finish();
    }

    /// <summary>每帧末尾（UI 线程）：同步目标尺寸、推送布局帧事件并发出按帧合并的事件。</summary>
    internal void AfterFrame()
    {
        var w = _host.Window;
        Target.Size = w.ClientSize;
        Target.Scale = (float)w.RenderScale;
        var timings = _host.Pipeline.LastTimings;
        // 只在真的完成了一次布局计算时推送；缓存的帧没有 layoutPass。
        if (timings.LayoutRan)
            Agent.Layout.NotifyLayoutPass(Target, _host.Root.FrameNumber, timings.LayoutMs, layoutRan: true, _host.Pipeline.LastLayoutNodeCount);
        _classCandidates?.Flush();
        Agent.FlushFrame();
    }

    /// <summary>拾取模式下截获指针（返回 true 表示不再路由进应用）。</summary>
    internal bool HandlePointer(in RawPointerEvent e)
    {
        if (_swallowPointer is { } swallowed && e.PointerId == swallowed)
        {
            // 拾取用的那次按下已被截获：同一指针直到抬起 / 取消的事件也不进入应用（否则应用会收到没有 pointerdown 的 pointerup）
            if (e.Type is RawPointerEventType.Up or RawPointerEventType.Cancel)
            {
                _swallowPointer = null;
                return true;
            }
            if (e.Type == RawPointerEventType.Leave) _swallowPointer = null;
            else if (_overlay.Mode != InspectMode.Pick) return true;
        }
        if (_overlay.Mode != InspectMode.Pick) return false;
        switch (e.Type)
        {
            case RawPointerEventType.Leave:
                _overlay.SetPickHover(null);
                break;
            case RawPointerEventType.Down:
            {
                _swallowPointer = e.PointerId;
                var hit = _host.Pipeline.HitTest(e.Position, ignorePointerEvents: true);
                if (hit is null) break;
                _overlay.SetPickHover(hit);
                Picked(hit);
                break;
            }
            case RawPointerEventType.Move:
                _overlay.SetPickHover(_host.Pipeline.HitTest(e.Position, ignorePointerEvents: true));
                break;
        }
        return true;
    }

    void Picked(Element e)
    {
        _overlay.NotifyNodePicked(e.UniqueId);
        if (_localPick)
        {
            // 本地发起的拾取（F12）与浏览器一样：选中后退出拾取并保持高亮
            _localPick = false;
            _overlay.Highlight(e);
            _overlay.SetInspectMode(InspectMode.None);
        }
        ElementPicked?.Invoke(e);
    }

    /// <summary>F12 切换拾取模式、拾取中 Esc 取消；返回 true 表示按键被 DevTools 消耗。</summary>
    internal bool HandleKey(in RawKeyEvent e)
    {
        if (_toggleKey is { } toggle && e.Key == toggle)
        {
            if (e.Type == RawKeyEventType.Down && !e.IsRepeat)
            {
                if (_overlay.Mode == InspectMode.Pick) StopInspecting();
                else
                {
                    StartInspecting();
                }
            }
            return true;
        }
        if (_overlay.Mode == InspectMode.Pick && e.Key == PhysicalKey.Escape)
        {
            if (e.Type == RawKeyEventType.Down) StopInspecting();
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Agent.RemoveTarget(Target.Id);
        Target.Tree = null;
        Target.Styles = null;
        Target.Overlay = null;
        _overlay.Dispose();
        _styles.Dispose();
        _tree.Dispose();
        if (_classCandidates is not null) _host.Root.RemoveObserver(_classCandidates);
        if (_webSocket is { } ws)
        {
            _webSocket = null;
            try { ws.StopAsync().Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception) { /* 关闭时忽略传输错误 */ }
        }
        if (_ownsAgent) Agent.Dispose();
    }
}
