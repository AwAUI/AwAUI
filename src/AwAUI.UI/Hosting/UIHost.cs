using System;
using System.Collections.Generic;
using System.Diagnostics;
using AwAUI.Css;
using AwAUI.Css.Values;
using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.Text;
using AwAUI.Threading;

namespace AwAUI.UI;

/// <summary>宿主一帧的统计（毫秒，墙钟时间）。</summary>
public readonly struct HostFrameStatistics
{
    public HostFrameStatistics(long frame, double buildMs, FrameTimings pipeline, double rasterMs, double totalMs, bool rendered)
    {
        Frame = frame;
        BuildMs = buildMs;
        Pipeline = pipeline;
        RasterMs = rasterMs;
        TotalMs = totalMs;
        Rendered = rendered;
    }

    /// <summary><see cref="UIRoot.FrameNumber"/>。</summary>
    public long Frame { get; }

    /// <summary>Build 阶段（组件重建、协调、提交）。</summary>
    public double BuildMs { get; }

    /// <summary>样式 / 布局 / 绘制各阶段。</summary>
    public FrameTimings Pipeline { get; }

    /// <summary>后端把 DisplayList 光栅化并呈现到窗口的时间；没有新内容时为 0。</summary>
    public double RasterMs { get; }

    public double TotalMs { get; }

    /// <summary>本帧是否向渲染目标提交了新内容并已呈现（渲染目标丢帧时为 false，见 <see cref="UIHost.DroppedFrames"/>）。</summary>
    public bool Rendered { get; }

    public override string ToString() =>
        FormattableString.Invariant($"frame {Frame}: build {BuildMs:F2} ms, {Pipeline}, raster {RasterMs:F2} ms, total {TotalMs:F2} ms");
}

/// <summary>
/// 把一个平台窗口接到 UI 根上的宿主（design/01 组合根的运行时部分）：
/// <list type="bullet">
/// <item>窗口事件（指针、按键、文本输入、尺寸与缩放变化、关闭）→ <see cref="UIRoot"/> 的输入派发与 <see cref="RenderPipeline"/> 的视口。</item>
/// <item>帧调度：只在有东西变脏时（State 变化、悬停 / 焦点等伪状态、尺寸变化）向平台帧时钟请求一帧；
/// 收到 <see cref="IWindowEvents.OnFrame"/> 后执行 Build → Style → Layout → Paint，有新 DisplayList 时交给渲染后端。</item>
/// <item>光标：悬停元素的 CSS <c>cursor</c> 转发给窗口。布局变化后按最近的指针位置重新命中，更新悬停。</item>
/// </list>
/// 渲染后端与文本引擎由应用传入（UI 不引用 AwAUI.Render，design/01 规则 1）。所有方法只能在 UI 线程上调用。
/// </summary>
public sealed class UIHost : IWindowEvents, IWindowStateEvents, IWindowLifecycleEvents, IWindowMemoryPressureEvents, IDisposable
{
    readonly WindowImpl _window;
    readonly RenderTarget _target;
    readonly IReducedMotionPreference? _reducedMotion;
    readonly IAppearancePreference? _appearance;
    long _renderedVersion = -1;
    bool _needsPresent = true;
    bool _pointerInside;
    bool _pointerPressed;
    Point _lastPointer;
    CursorShape _cursor = CursorShape.Arrow;
    bool _disposed;
    readonly IDispatcher? _dispatcher;
    UIDevTools? _devTools;
    int _renderedOverlayVersion = -1;
    bool _overlayShown;
    IDisposable? _accessibilityConnection;

    /// <param name="window">平台窗口；宿主成为它的 <see cref="WindowImpl.Events"/>。</param>
    /// <param name="backend">渲染后端（软件 / GPU），由应用的组合根选择。</param>
    /// <param name="textEngine">文本引擎（字体、整形、段落）。</param>
    /// <param name="dispatcher">UI 线程调度器（通常是 <see cref="PlatformBackend.Dispatcher"/>），用于跨线程的 State 写入。</param>
    public UIHost(WindowImpl window, RenderBackend backend, TextEngine textEngine, IDispatcher? dispatcher = null)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        if (backend is null) throw new ArgumentNullException(nameof(backend));
        _dispatcher = dispatcher;
        _backend = backend;
        Root = new UIRoot(dispatcher);
        Root.Host = this;
        Pipeline = new RenderPipeline(textEngine);
        Root.Pipeline = Pipeline;
        // 帧时钟：State 写入或脏标记出现时请求一帧（宿主调度，不在处理器里同步执行帧）
        Root.FrameRequested += _ => RequestFrame();
        _target = backend.CreateRenderTarget(window.Surface);
        Pipeline.SetViewport(window.ClientSize, (float)window.RenderScale);
        Pipeline.SetSafeArea(window.SafeAreaInsets);
        // 滚动条样式随平台（design/21「滚动条」）
        Pipeline.ScrollbarStyle = window.PreferredScrollbarStyle;
        window.Events = this;
        // 输入法桥与剪贴板：焦点所在的输入框作为文本输入客户端交给窗口（design/04「IME」）
        Root.Input.TextInputMethod = window.TextInput;
        Root.Input.Clipboard = window.Clipboard;
        // 无障碍桥（design/10）：平台把语义树暴露给辅助技术；没有桥时不创建语义树
        _accessibilityConnection = window.Accessibility is { } accessibility
            ? Root.Accessibility.ConnectBridge(accessibility)
            : null;
        _reducedMotion = window.GetFeature<IReducedMotionPreference>();
        if (_reducedMotion is { } preference)
        {
            preference.PrefersReducedMotionChanged += OnReducedMotionChanged;
            Pipeline.SetPrefersReducedMotion(preference.PrefersReducedMotion);
        }
        // 系统外观（浅色 / 深色）驱动 prefers-color-scheme（design/08「媒体查询」）
        _appearance = window.GetFeature<IAppearancePreference>();
        if (_appearance is { } appearance)
        {
            appearance.PrefersDarkChanged += OnAppearanceChanged;
            Pipeline.SetPrefersDark(appearance.PrefersDark);
        }
    }

    /// <summary>UI 根（组件、元素树、输入与焦点）。</summary>
    public UIRoot Root { get; }

    /// <summary>样式 / 布局 / 绘制管线（加样式表、命中测试、最近一帧的 DisplayList）。</summary>
    public RenderPipeline Pipeline { get; }

    public WindowImpl Window => _window;

    /// <summary>
    /// 托管这个宿主的顶层窗口（<see cref="AwAUI.UI.Window"/>）；宿主由 <see cref="AwApp.CreateHost"/> 直接创建时为 null。
    /// </summary>
    public Window? TopLevel { get; internal set; }

    /// <summary>平台报告窗口状态变化。</summary>
    public event Action<WindowState>? WindowStateChanged;

    /// <summary>平台报告窗口位置变化。</summary>
    public event Action<Point>? WindowPositionChanged;

    /// <summary>平台报告窗口激活状态变化。</summary>
    public event Action<bool>? WindowActivated;

    /// <summary>平台报告应用生命周期变化。</summary>
    public event Action<AppLifecycle>? LifecycleChanged;

    /// <summary>安全区变化（逻辑像素）。</summary>
    public event Action<Thickness>? SafeAreaChanged;

    /// <summary>平台关闭请求（标题栏关闭按钮、Alt+F4）：返回 false 取消。由 <see cref="Window"/> 设置。</summary>
    internal Func<bool>? CloseRequested { get; set; }

    /// <summary>是否已批准关闭（<see cref="Window.Close()"/> 已询问过 <see cref="Closing"/>，平台不必再问）。</summary>
    internal bool CloseApproved { get; set; }

    /// <summary>
    /// 是否只光栅化相对上一帧变化的区域（<see cref="RenderPipeline.LastDamage"/> → <see cref="RenderOptions.Damage"/>）。
    /// 默认对软件后端开启（其目标在帧间保留像素），GPU 后端关闭。默认值每帧按后端当前的
    /// <see cref="RenderBackend.Capabilities"/> 判断，GPU 后端运行中回退到软件后随之开启；显式赋值后固定为该值。
    /// </summary>
    public bool PartialRaster
    {
        // GPU 目标（交换链）不保证保留上一帧的内容，只对软件后端做局部光栅化
        get => _partialRaster ?? (_backend.Capabilities & BackendCapabilities.Gpu) == 0;
        set => _partialRaster = value;
    }

    readonly RenderBackend _backend;
    bool? _partialRaster;

    /// <summary>每帧光栅化前清屏的颜色（根元素背景会覆盖它）。</summary>
    public Color ClearColor { get; set; } = Color.White;

    /// <summary>最近一帧的统计。</summary>
    public HostFrameStatistics LastFrame { get; private set; }

    /// <summary>渲染后端的名字（DevTools 的 App.getInfo 报告它）。</summary>
    public string BackendName => _backend.Name;

    /// <summary>最近一次交给渲染后端的 DisplayList（应用帧；启用 DevTools 且有高亮时含叠加层）。</summary>
    public DisplayList PresentedFrame { get; private set; } = DisplayList.Empty;

    // ------------------------------------------------------------ DevTools

    /// <summary>本窗口的 DevTools 接入；没有启用时为 null。</summary>
    public UIDevTools? DevTools => _devTools;

    /// <summary>
    /// 启用 DevTools（design/06）：把窗口登记为调试目标（元素树、CSS、高亮与拾取），F12 切换拾取模式，
    /// 可选地在本机 WebSocket 上监听（<see cref="UIDevToolsOptions.ListenWebSocket"/>）。重复调用返回已有的实例。
    /// 诊断被特性开关 <c>AwAUI.Diagnostics.Enabled</c> 关闭时抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public UIDevTools EnableDevTools(UIDevToolsOptions? options = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UIHost));
        if (_devTools is not null) return _devTools;
        if (!AwAUI.Diagnostics.DiagnosticsFeature.IsEnabled)
            throw new InvalidOperationException("DevTools are disabled by the '" + AwAUI.Diagnostics.DiagnosticsFeature.SwitchName + "' feature switch.");
        _devTools = new UIDevTools(this, options ?? new UIDevToolsOptions(), _dispatcher);
        RequestFrame();
        return _devTools;
    }

    /// <summary>叠加层内容变化：下一帧重新合成并呈现。</summary>
    internal void OnOverlayChanged() => RequestFrame();

    /// <summary>每帧结束（光栅化之后）触发。</summary>
    public event Action<UIHost>? FrameCompleted;

    /// <summary>窗口关闭后触发。</summary>
    public event Action<UIHost>? Closed;

    /// <summary>加入应用样式表。</summary>
    public void AddStyleSheet(StyleSheet sheet) => Pipeline.AddStyleSheet(sheet);

    /// <summary>解析并加入应用样式表。</summary>
    public void AddStyleSheet(string css, string? sourceName = null) => Pipeline.AddStyleSheet(css, sourceName);

    /// <summary>挂载根节点（通常是根组件）并请求第一帧。</summary>
    public void Mount(Node root)
    {
        // UIRoot.Mount 立即执行一帧（Build → Style → Layout → Paint）；光栅化留给下一次帧时钟回调
        var sw = Stopwatch.StartNew();
        Root.Mount(root);
        double total = sw.Elapsed.TotalMilliseconds;
        var t = Pipeline.LastTimings;
        LastFrame = new HostFrameStatistics(Root.FrameNumber, Math.Max(0, total - t.StyleMs - t.LayoutMs - t.PaintMs), t, 0, total, false);
        MountStatistics = LastFrame;
        RequestFrame();
    }

    /// <summary>挂载那一帧的统计（不含光栅化）。</summary>
    public HostFrameStatistics MountStatistics { get; private set; }

    /// <summary>请求在下一个垂直同步时执行一帧。</summary>
    public void RequestFrame()
    {
        if (!_disposed) _window.RequestFrame();
    }

    /// <summary>
    /// 立即执行一帧：Build → Style → Layout → Paint，若有新内容则光栅化到窗口。平台帧时钟经 <see cref="IWindowEvents.OnFrame"/> 调用它；
    /// 测试与无窗口渲染也可以直接调用。
    /// </summary>
    public void RunFrame()
    {
        if (_disposed) return;
        var sw = Stopwatch.StartNew();
        Root.RunFrame();
        double frameMs = sw.Elapsed.TotalMilliseconds;
        var timings = Pipeline.LastTimings;
        double buildMs = Math.Max(0, frameMs - timings.StyleMs - timings.LayoutMs - timings.PaintMs);
        double rasterMs = 0;
        bool rendered = false;
        var dt = _devTools;
        bool overlay = dt is not null && dt.HasOverlay;
        int overlayVersion = dt?.OverlayVersion ?? -1;
        if (Pipeline.FrameVersion != _renderedVersion || _needsPresent || overlay != _overlayShown || (overlay && overlayVersion != _renderedOverlayVersion))
        {
            var r = Stopwatch.StartNew();
            // DevTools 的叠加层画在应用帧之上（独立于应用的 DisplayList，不参与命中测试），跟随最新布局
            var frame = overlay ? dt!.Compose(Pipeline.Frame) : Pipeline.Frame;
            var damage = ComputeDamage(overlay);
            LastDamage = damage;
            bool submitted = damage is not { IsEmpty: true };
            // 纯滚动帧：把上一帧的像素按位移搬移，只重画新露出的条带（软件后端；GPU 交换链不保留像素，damage 为 null 时忽略）
            var shift = damage is null ? null : Pipeline.LastScrollShift;
            var shiftRects = shift is null ? null : ExpandDamageRects(Pipeline.LastScrollDamage, _target.PixelSize);
            if (submitted) _target.Render(frame, new RenderOptions(ClearColor, damage, shift, shiftRects));
            PresentedFrame = frame;
            rasterMs = r.Elapsed.TotalMilliseconds;
            _renderedVersion = Pipeline.FrameVersion;
            _renderedOverlayVersion = overlayVersion;
            _overlayShown = overlay;
            _needsPresent = false;
            rendered = true;
            if (submitted && !_target.LastFramePresented)
            {
                // 丢帧（RenderTarget.LastFramePresented：交换链没有给出可绘制纹理，窗口隐藏 / 最小化 / 超时）：表面仍显示更早的一帧。
                // 下一个帧时钟节拍整帧重画（_needsPresent 使损伤为整个目标）；不在这里立即重试，取可绘制纹理可能阻塞约 1 秒
                _needsPresent = true;
                rendered = false;
                DroppedFrames++;
                RequestFrame();
            }
        }
        try
        {
            if ((timings.LayoutRan || Pipeline.LastFrameScrolled) && _pointerInside && !_pointerPressed) RehoverAfterLayout();
        }
        finally
        {
            UpdateCursor();
            dt?.AfterFrame();
            LastFrame = new HostFrameStatistics(Root.FrameNumber, buildMs, timings, rasterMs, sw.Elapsed.TotalMilliseconds, rendered);
        }
        // 滚动动画、惯性与覆盖式滚动条淡出需要后续的帧
        if (Pipeline.NeedsAnimationFrame) RequestFrame();
        // 布局之后：输入框的插入点矩形可能变了，通知输入法（候选窗跟随）
        Root.Input.AfterFrame();
        FrameCompleted?.Invoke(this);
    }

    /// <summary>
    /// 渲染目标报告没有呈现的帧数（<see cref="RenderTarget.LastFramePresented"/> 为 false）。每次丢帧后宿主在下一个帧时钟节拍整帧重画，
    /// 所以窗口重新可见后会显示最新内容（不依赖平台在可见性变化时补发尺寸通知）。
    /// </summary>
    public long DroppedFrames { get; private set; }

    /// <summary>最近一次光栅化的设备像素区域；null 表示整个目标（全量光栅化）。</summary>
    public PixelRect? LastDamage { get; private set; }

    /// <summary>
    /// 本帧需要光栅化的设备像素区域：只有应用帧恰好前进一个版本、没有叠加层变化且后端保留像素时才局部光栅化，其余情况整帧。
    /// 逻辑像素的损伤矩形向外取整并多扩一个设备像素（抗锯齿边缘）。
    /// </summary>
    PixelRect? ComputeDamage(bool overlay)
    {
        if (!PartialRaster || overlay || _overlayShown || _needsPresent || _renderedVersion < 0
            || Pipeline.FrameVersion != _renderedVersion + 1 || Pipeline.LastDamage is not { } d)
            return null;
        if (d.Width <= 0 || d.Height <= 0) return new PixelRect(0, 0, 0, 0);
        double scale = Pipeline.Scale;
        int x0 = (int)Math.Floor(d.Left * scale) - 1, y0 = (int)Math.Floor(d.Top * scale) - 1;
        int x1 = (int)Math.Ceiling(d.Right * scale) + 1, y1 = (int)Math.Ceiling(d.Bottom * scale) + 1;
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        return new PixelRect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>纯滚动帧的多个受损矩形：各向外扩一个设备像素（抗锯齿边缘）并裁剪到目标。</summary>
    static PixelRect[] ExpandDamageRects(IReadOnlyList<PixelRect>? rects, PixelSize size)
    {
        var full = new PixelRect(0, 0, size.Width, size.Height);
        var result = new PixelRect[rects?.Count ?? 0];
        for (int i = 0; i < result.Length; i++)
        {
            var r = rects![i];
            result[i] = PixelRect.FromLTRB(Math.Max(0, r.X - 1), Math.Max(0, r.Y - 1), r.Right + 1, r.Bottom + 1).Intersect(full);
        }
        return result;
    }

    /// <summary>布局变化后指针下的元素可能变了：按最近的指针位置重新命中并更新悬停（触发 enter/leave 与 :hover 重算）。</summary>
    void RehoverAfterLayout()
    {
        var hit = Pipeline.HitTest(_lastPointer);
        if (ReferenceEquals(hit, Root.Input.HoveredElement)) return;
        var move = new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 1, _lastPointer, PointerButtons.None, PointerButtons.None, KeyModifiers.None, TimeSpan.Zero);
        // 与常规派发一样包装：enter / leave 处理器中未被错误边界捕获的异常在这里抛出，而不是拖到下一次派发
        Root.BeginDispatch();
        try
        {
            Root.Input.UpdateHover(hit, move);
        }
        finally
        {
            Root.EndDispatch();
        }
    }

    void UpdateCursor()
    {
        var shape = _pointerInside ? CursorFor(Root.Input.HoveredElement) : CursorShape.Arrow;
        if (shape == _cursor) return;
        _cursor = shape;
        _window.SetCursor(shape);
    }

    /// <summary>CSS <c>cursor</c> → 平台光标形状。<c>auto</c> 在输入框上为文本光标，其余为箭头。</summary>
    internal static CursorShape CursorFor(Element? e)
    {
        if (e is null) return CursorShape.Arrow;
        var s = e.ComputedStyle;
        if (s is null) return CursorShape.Arrow;
        return s.Cursor switch
        {
            CssCursor.Auto => e is InputElement ? CursorShape.IBeam : CursorShape.Arrow,
            CssCursor.Pointer => CursorShape.Hand,
            CssCursor.Text or CssCursor.VerticalText => CursorShape.IBeam,
            CssCursor.Crosshair or CssCursor.Cell => CursorShape.Crosshair,
            CssCursor.EwResize or CssCursor.ColResize or CssCursor.EResize or CssCursor.WResize => CursorShape.ResizeHorizontal,
            CssCursor.NsResize or CssCursor.RowResize or CssCursor.NResize or CssCursor.SResize => CursorShape.ResizeVertical,
            CssCursor.Wait or CssCursor.Progress => CursorShape.Wait,
            CssCursor.NotAllowed or CssCursor.NoDrop => CursorShape.NotAllowed,
            CssCursor.None => CursorShape.None,
            _ => CursorShape.Arrow,
        };
    }

    // ------------------------------------------------------------ IWindowEvents

    void IWindowEvents.OnFrame(TimeSpan timestamp) => RunFrame();

    void IWindowEvents.OnResized(Size clientSize, double scale)
    {
        Pipeline.SetViewport(clientSize, (float)scale);
        _target.Resize(PixelSize.FromSize(clientSize, scale), scale);
        _needsPresent = true;
        RequestFrame();
    }

    void IWindowEvents.OnPointer(in RawPointerEvent e)
    {
        _lastPointer = e.Position;
        _pointerInside = e.Type != RawPointerEventType.Leave;
        _pointerPressed = e.Buttons != PointerButtons.None;
        // DevTools 拾取模式：输入在路由进应用之前被截获（design/06）
        if (_devTools is { } dt && dt.HandlePointer(e))
        {
            RequestFrame();
            return;
        }
        Root.Input.DispatchPointer(e);
        UpdateCursor();
    }

    void IWindowEvents.OnKey(in RawKeyEvent e)
    {
        if (_devTools is { } dt && dt.HandleKey(e)) return;
        Root.Input.DispatchKey(e);
    }

    void IWindowEvents.OnTextInput(string text) => Root.Input.DispatchTextInput(text);

    void IWindowEvents.OnFocusChanged(bool focused)
    {
        // 窗口失去键盘焦点：输入法会话随窗口失活而结束，放弃进行中的组合（恢复焦点时输入法会重新开始，无需额外动作）
        if (!focused) Root.Input.TextInputMethod?.ResetComposition();
    }

    // ------------------------------------------------------------ 可选平台回调

    void IWindowStateEvents.OnWindowStateChanged(WindowState state)
    {
        WindowStateChanged?.Invoke(state);
        RequestFrame();
    }

    void IWindowStateEvents.OnPositionChanged(Point position) => WindowPositionChanged?.Invoke(position);

    void IWindowLifecycleEvents.OnSurfaceLost()
    {
        // 平台表面丢失：渲染目标与设备资源在恢复前不可用（移动端切后台、GPU 设备丢失）。
        _backend.InvalidateDeviceResources();
    }

    void IWindowLifecycleEvents.OnSurfaceRestored()
    {
        _backend.InvalidateDeviceResources();
        _needsPresent = true;
        RequestFrame();
    }

    void IWindowLifecycleEvents.OnLifecycle(AppLifecycle state)
    {
        if (state == AppLifecycle.Background || state == AppLifecycle.Inactive)
            Root.Input.TextInputMethod?.ResetComposition();
        LifecycleChanged?.Invoke(state);
        RequestFrame();
    }

    void IWindowLifecycleEvents.OnActivated(bool activated)
    {
        WindowActivated?.Invoke(activated);
        RequestFrame();
    }

    void IWindowLifecycleEvents.OnSafeAreaChanged(Thickness insets)
    {
        Pipeline.SetSafeArea(insets);
        SafeAreaChanged?.Invoke(insets);
        RequestFrame();
    }

    void IWindowMemoryPressureEvents.OnMemoryPressure()
    {
        // 系统内存压力（移动端）：丢弃可重建的设备缓存（图集、字形位图、离屏图层），
        // 下一次呈现时按需重建；保留窗口内容与布局。
        _backend.InvalidateDeviceResources();
        _needsPresent = true;
    }

    void OnReducedMotionChanged(bool value)
    {
        if (!_disposed && Pipeline.SetPrefersReducedMotion(value)) RequestFrame();
    }

    void OnAppearanceChanged(bool value)
    {
        if (!_disposed && Pipeline.SetPrefersDark(value)) RequestFrame();
    }

    bool IWindowEvents.OnCloseRequested()
    {
        if (CloseApproved) return true;
        bool allow = CloseRequested?.Invoke() ?? true;
        if (allow) CloseApproved = true;
        return allow;
    }

    void IWindowEvents.OnClosed()
    {
        try { Closed?.Invoke(this); }
        finally { Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(Root.Host, this)) Root.Host = null;
        if (_reducedMotion is { } preference) preference.PrefersReducedMotionChanged -= OnReducedMotionChanged;
        if (_appearance is { } appearance) appearance.PrefersDarkChanged -= OnAppearanceChanged;
        if (ReferenceEquals(_window.Events, this)) _window.Events = null;
        _accessibilityConnection?.Dispose();
        _accessibilityConnection = null;
        Root.Input.TextInputMethod = null;
        _devTools?.Dispose();
        _devTools = null;
        _target.Dispose();
    }
}

/// <summary>
/// 应用入口的组合根（design/01 规则 5）：平台、渲染后端、文本引擎与样式表都由应用选择并传入，框架核心不做猜测。
/// <code>
/// AwApp.Run(new MacPlatform(), new WindowOptions { Title = "Counter" }, new SoftwareRenderBackend(),
///           textEngine, new Counter(), StyleSheet.Parse(css));
/// </code>
/// </summary>
public static class AwApp
{
    /// <summary>创建窗口与宿主、加入样式表、挂载根节点并显示窗口；不进入消息循环（无窗口渲染与测试使用）。</summary>
    public static UIHost CreateHost(PlatformBackend platform, WindowOptions options, RenderBackend backend, TextEngine textEngine,
        Node root, params StyleSheet[] styleSheets)
    {
        if (platform is null) throw new ArgumentNullException(nameof(platform));
        if (root is null) throw new ArgumentNullException(nameof(root));
        var window = platform.CreateWindow(options ?? new WindowOptions());
        var host = new UIHost(window, backend, textEngine, platform.Dispatcher);
        if (styleSheets is not null)
            foreach (var sheet in styleSheets)
                if (sheet is not null) host.AddStyleSheet(sheet);
        window.Show();
        host.Mount(root);
        return host;
    }

    /// <summary>创建宿主并运行平台消息循环，直到窗口关闭。</summary>
    public static void Run(PlatformBackend platform, WindowOptions options, RenderBackend backend, TextEngine textEngine,
        Node root, params StyleSheet[] styleSheets)
    {
        var host = CreateHost(platform, options, backend, textEngine, root, styleSheets);
        host.Closed += _ => platform.Exit();
        platform.Run();
    }
}
